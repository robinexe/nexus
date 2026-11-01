using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates.Vendor;

/// <summary>Um produto GeForce da lista oficial da NVIDIA (série = psid, produto = pfid).</summary>
public sealed record NvidiaProduct(int Psid, string Series, bool Notebook, int Pfid, string Name);

/// <summary>Série/produto escolhidos para a consulta e de onde veio a escolha ("nome" ou "Device ID").</summary>
public sealed record NvidiaProductRef(int Psid, int Pfid, bool Notebook, string Source, string? MatchedName = null);

/// <summary>
/// Consulta oficial da NVIDIA (a mesma da página "Drivers NVIDIA" / nvidia.com/drivers), em duas partes:
/// <list type="number">
/// <item>Lista de séries e produtos: <c>https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2</c> (séries; ParentID 1 =
/// GeForce) e <c>TypeID=3</c> (produtos; ParentID = série). TypeID=4 lista os sistemas (Windows 10 64-bit = 57, Windows 11 = 135).
/// Séries de notebook têm "(Notebooks)" no nome (ex.: "GeForce GTX 16 Series (Notebooks)" = 115; GTX 1650 notebook = 898).</item>
/// <item>Versão mais nova: <c>https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&amp;psid=…&amp;pfid=…&amp;osID=…&amp;languageCode=1046&amp;isWHQL=1&amp;dch=1&amp;sort1=0&amp;numberOfResults=1</c>
/// (JSON; campos em URL-encoding; DownloadURL em us.download.nvidia.com; DownloadURLFileSize "1.04 GB").</item>
/// </list>
/// Tudo aqui é puro (parsing e escolha); a rede fica em <see cref="VendorDriverClient"/>.
/// </summary>
public static partial class NvidiaDriverLookup
{
    public static readonly Uri SeriesUrl = new("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2");
    public static readonly Uri ProductsUrl = new("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3");
    public const int OsWindows10x64 = 57;
    public const int OsWindows11 = 135;
    /// <summary>Português (Brasil) na consulta da NVIDIA (nomes e datas em pt-BR).</summary>
    public const int LanguagePtBr = 1046;
    private const string GeForceProductType = "1";

    /// <summary>Windows 11 = build 22000 ou maior.</summary>
    public static int OsId(int windowsBuild) => windowsBuild >= 22000 ? OsWindows11 : OsWindows10x64;

    public static Uri LookupUrl(NvidiaProductRef product, int osId)
    {
        ArgumentNullException.ThrowIfNull(product);
        return new Uri(string.Create(CultureInfo.InvariantCulture,
            $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={product.Psid}&pfid={product.Pfid}&osID={osId}&languageCode={LanguagePtBr}&isWHQL=1&dch=1&sort1=0&numberOfResults=1"));
    }

    // ------------------------------------------------------------------ lista de produtos

    /// <summary>Produtos GeForce (séries com ParentID 1 no TypeID=2) a partir dos dois XML oficiais.</summary>
    public static IReadOnlyList<NvidiaProduct> ParseProducts(string seriesXml, string productsXml)
    {
        var series = Values(seriesXml).Where(v => v.Parent == GeForceProductType)
            .ToDictionary(v => v.Value, v => v.Name, StringComparer.Ordinal);
        var list = new List<NvidiaProduct>();
        foreach (var p in Values(productsXml))
        {
            if (!series.TryGetValue(p.Parent ?? "", out var seriesName)) continue;
            if (!int.TryParse(p.Parent, NumberStyles.None, CultureInfo.InvariantCulture, out var psid) ||
                !int.TryParse(p.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pfid)) continue;
            list.Add(new NvidiaProduct(psid, seriesName, seriesName.Contains("Notebook", StringComparison.OrdinalIgnoreCase), pfid, p.Name));
        }
        return list;
    }

    private static IEnumerable<(string? Parent, string Name, string Value)> Values(string xml)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8_000_000 });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { yield break; }
        foreach (var lv in doc.Descendants("LookupValue"))
        {
            var name = lv.Element("Name")?.Value.Trim();
            var value = lv.Element("Value")?.Value.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) continue;
            yield return (lv.Attribute("ParentID")?.Value, name, value);
        }
    }

    /// <summary>"NVIDIA GeForce RTX 3050 Laptop GPU" e "GeForce RTX 3050 Laptop GPU" → "GEFORCE RTX 3050".</summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var s = name.ToUpperInvariant().Replace("(TM)", " ", StringComparison.Ordinal).Replace("(R)", " ", StringComparison.Ordinal);
        s = NoiseWords().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    [GeneratedRegex(@"\bNVIDIA\b|\bLAPTOP GPU\b|\bWITH MAX-Q DESIGN\b|\bMAX-Q\b|\bMOBILE\b", RegexOptions.CultureInvariant)]
    private static partial Regex NoiseWords();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>O nome que o Windows dá indica notebook ("Laptop GPU", "Max-Q", "GTX 1050 Ti with Max-Q", GeForce "xxxM").</summary>
    public static bool NameLooksNotebook(string name) =>
        name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) || name.Contains("Max-Q", StringComparison.OrdinalIgnoreCase) || MobileSuffix().IsMatch(name);

    [GeneratedRegex(@"\b[0-9]{3,4}MX?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MobileSuffix();

    /// <summary>
    /// Escolhe série e produto. Primeiro pelo nome (normalizado) na lista oficial, preferindo a série de notebook quando o PC é
    /// notebook (ou o nome diz "Laptop GPU"); sem casar, cai para <see cref="FromDeviceId"/> (faixas de Device ID → série).
    /// </summary>
    public static NvidiaProductRef? Resolve(IReadOnlyList<NvidiaProduct>? products, string gpuName, uint deviceId, bool laptop)
    {
        ArgumentNullException.ThrowIfNull(gpuName);
        var notebook = laptop || NameLooksNotebook(gpuName);
        if (products is { Count: > 0 })
        {
            var wanted = NormalizeName(gpuName);
            var same = products.Where(p => NormalizeName(p.Name) == wanted).ToList();
            var pick = same.FirstOrDefault(p => p.Notebook == notebook) ?? (same.Count > 0 && !notebook ? null : same.FirstOrDefault());
            if (pick is not null) return new NvidiaProductRef(pick.Psid, pick.Pfid, pick.Notebook, "nome", pick.Name);
        }
        return FromDeviceId(deviceId, notebook);
    }

    /// <summary>
    /// Plano B (puro): faixa do PCI Device ID → arquitetura → uma série/produto da mesma linha. O driver da NVIDIA é o mesmo
    /// para todos os produtos de uma série (e de toda a linha atual, notebook ou desktop); a série importa para as linhas que
    /// pararam num ramo antigo (Kepler: 47x; Maxwell/Pascal: 58x). Valores conferidos na lista oficial em 01/11/2026.
    /// </summary>
    public static NvidiaProductRef? FromDeviceId(uint deviceId, bool notebook)
    {
        (int Psid, int Pfid, int NbPsid, int NbPfid)? s = deviceId switch
        {
            >= 0x2900 and <= 0x2FFF => (131, 1066, 133, 1073),           // Blackwell: RTX 50
            >= 0x2600 and <= 0x28FF => (127, 995, 129, 1004),            // Ada: RTX 40
            >= 0x2400 and <= 0x25FF or >= 0x2200 and <= 0x22FF => (120, 930, 123, 938), // Ampere: RTX 30
            >= 0x2180 and <= 0x21FF or >= 0x1E00 and <= 0x1FFF => (112, 897, 115, 898), // Turing: GTX 16 / RTX 20
            >= 0x1B00 and <= 0x1D7F => (101, 815, 102, 819),            // Pascal: GTX 10
            >= 0x1340 and <= 0x17FF => (98, 755, 99, 757),              // Maxwell: 900 / 900M
            > 0 and < 0x1340 => (95, 691, 92, 700),                     // Kepler: 700 / 700M
            _ => null,
        };
        if (s is not { } v) return null;
        return notebook ? new NvidiaProductRef(v.NbPsid, v.NbPfid, true, "Device ID") : new NvidiaProductRef(v.Psid, v.Pfid, false, "Device ID");
    }

    // ------------------------------------------------------------------ resposta da consulta

    /// <summary>
    /// Lê a resposta do DriverManualLookup. Exige Success = 1, WHQL, não beta, versão "xxx.yy", URL https num host oficial e
    /// tamanho anunciado. Retorna (oferta, null) ou (null, motivo em português).
    /// </summary>
    public static (VendorDriverOffer? Offer, string? Problem) ParseLookup(string json, bool notebook)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (Str(root, "Success") != "1" || !root.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
                return (null, "A NVIDIA não tem driver para esta placa neste Windows (consulta sem resultado).");
            var info = ids[0].TryGetProperty("downloadInfo", out var d) ? d : default;
            if (info.ValueKind != JsonValueKind.Object || Str(info, "Success") != "1")
                return (null, "A NVIDIA não tem driver para esta placa neste Windows (consulta sem resultado).");
            var version = Str(info, "Version");
            if (!VendorDriverPolicy.IsValidVersion(GpuVendor.Nvidia, version)) return (null, "Resposta da NVIDIA com versão em formato inesperado.");
            if (Str(info, "IsBeta") == "1" || Str(info, "IsWHQL") != "1") return (null, "A versão mais nova da NVIDIA não é WHQL estável: o NEXUS não instala.");
            if (!Uri.TryCreate(Str(info, "DownloadURL"), UriKind.Absolute, out var url) || !VendorDriverPolicy.IsAllowedDownload(GpuVendor.Nvidia, url))
                return (null, "Endereço de download fora dos servidores da NVIDIA: recusado.");
            var size = VendorDriverPolicy.ParseAnnouncedSize(Str(info, "DownloadURLFileSize"));
            if (size is not { } bytes || bytes <= 0 || bytes > VendorDriverPolicy.MaxInstallerBytes) return (null, "Tamanho do instalador anunciado fora do limite.");
            var name = Decode(Str(info, "NameLocalized")) is { Length: > 0 } n ? n : Decode(Str(info, "Name")) ?? "Driver NVIDIA";
            var details = Str(info, "DetailsURL") is { } du && Uri.TryCreate(du, UriKind.Absolute, out var dUri) && dUri.Scheme == Uri.UriSchemeHttps &&
                          dUri.IdnHost.EndsWith("nvidia.com", StringComparison.OrdinalIgnoreCase) ? du : null;
            return (new VendorDriverOffer(GpuVendor.Nvidia, version!, url, bytes, ParseDate(Decode(Str(info, "ReleaseDateTime"))), details, name, notebook), null);
        }
        catch (JsonException) { return (null, "Resposta da NVIDIA ilegível."); }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Decode(string? s)
    {
        if (s is null) return null;
        try { return Uri.UnescapeDataString(s).Trim(); }
        catch (UriFormatException) { return s; }
    }

    /// <summary>"2026 outubro 21" (pt-BR) ou "2026 October 21" (inglês).</summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("pt-BR"), CultureInfo.InvariantCulture })
            if (DateOnly.TryParseExact(text.Trim(), ["yyyy MMMM d", "yyyy MMMM dd", "yyyy-MM-dd"], culture, DateTimeStyles.None, out var d)) return d;
        return null;
    }
}
