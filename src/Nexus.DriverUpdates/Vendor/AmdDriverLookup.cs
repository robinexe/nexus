using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates.Vendor;

/// <summary>Ramo de drivers da AMD a que a placa pertence (cada ramo tem um pacote Adrenalin próprio no site da AMD).</summary>
public enum AmdDriverBranch
{
    /// <summary>Não identificada pelo Device ID: só o "Auto-Detect and Install" oficial da AMD resolve.</summary>
    Unknown,
    /// <summary>RDNA 1/2 (RX 5000/6000) e integradas RDNA 2 (Ryzen 6000/7035): pacote "…-win11-a.exe".</summary>
    Rdna12,
    /// <summary>RDNA 3/3.5/4 (RX 7000/9000) e integradas RDNA 3 (Ryzen 7040/8040/AI): pacote "…-win11-b.exe".</summary>
    Rdna34,
    /// <summary>Polaris/Vega (RX 400/500, Vega) e integradas Vega (Ryzen 2000–5000 e 7030): pacote "…-vega-polaris.exe" (ramo de manutenção).</summary>
    VegaPolaris,
    /// <summary>GCN 1–3 (HD 7000, R7/R9): a AMD não publica mais drivers novos.</summary>
    Legacy,
}

/// <summary>
/// AMD não tem API pública em JSON. A versão mais nova e o instalador completo vêm da página de drivers OFICIAL de um produto
/// do mesmo ramo (HTML do amd.com: seção "Windows 11 - 64-Bit Edition" → artigo "AMD Software: Adrenalin Edition" com
/// "Revision Number", "File Size", "Release Date" e o link em drivers.amd.com). Conferido em 01/11/2026: RX 7600, RX 9070 XT e
/// Ryzen 7 7840HS → "…-win11-b.exe"; RX 6600, RX 5700 XT e Ryzen 7 6800H → "…-win11-a.exe"; Ryzen 5 5600H, Ryzen 7 5800H e
/// Ryzen 5 4600H → "…-win11-may-vega-polaris.exe". O download em drivers.amd.com exige o cabeçalho Referer do amd.com (sem ele o
/// servidor redireciona para uma página de erro, que o NEXUS recusa por não ser host de download).
/// </summary>
public static partial class AmdDriverLookup
{
    /// <summary>Referer exigido por drivers.amd.com.</summary>
    public static readonly Uri Referer = new("https://www.amd.com/");

    /// <summary>Página geral de drivers (tem o link do "Auto-Detect and Install", usado quando o ramo não é identificado).</summary>
    public static readonly Uri DriversPage = new("https://www.amd.com/en/support/download/drivers.html");

    /// <summary>Página de produto de referência de cada ramo (o pacote é o mesmo para todo o ramo).</summary>
    public static Uri? BranchPage(AmdDriverBranch branch) => branch switch
    {
        AmdDriverBranch.Rdna12 => new Uri("https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-6000-series/amd-radeon-rx-6600.html"),
        AmdDriverBranch.Rdna34 => new Uri("https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7600.html"),
        AmdDriverBranch.VegaPolaris => new Uri("https://www.amd.com/en/support/downloads/drivers.html/processors/ryzen/ryzen-5000-series/amd-ryzen-5-5600h.html"),
        _ => null,
    };

    /// <summary>Ramo pelo PCI Device ID (faixas do Laboratório de GPU, <see cref="GpuIdentity.Architecture"/>). Puro.</summary>
    public static AmdDriverBranch Classify(uint deviceId)
    {
        var arch = GpuIdentity.Architecture(GpuIdentity.VendorAmd, deviceId).Name;
        if (arch.StartsWith("RDNA 1", StringComparison.Ordinal) || arch.StartsWith("RDNA 2", StringComparison.Ordinal)) return AmdDriverBranch.Rdna12;
        if (arch.StartsWith("RDNA 3", StringComparison.Ordinal) || arch.StartsWith("RDNA 4", StringComparison.Ordinal)) return AmdDriverBranch.Rdna34;
        if (arch.Contains("Polaris", StringComparison.Ordinal) || arch.Contains("Vega", StringComparison.Ordinal)) return AmdDriverBranch.VegaPolaris;
        if (arch.StartsWith("GCN (", StringComparison.Ordinal)) return AmdDriverBranch.Legacy;
        return AmdDriverBranch.Unknown;
    }

    /// <summary>Nome do ramo para a tela.</summary>
    public static string BranchName(AmdDriverBranch branch) => branch switch
    {
        AmdDriverBranch.Rdna12 => "Radeon RX 5000/6000 e Ryzen com gráficos RDNA 2",
        AmdDriverBranch.Rdna34 => "Radeon RX 7000/9000 e Ryzen com gráficos RDNA 3",
        AmdDriverBranch.VegaPolaris => "Radeon RX 400/500, Vega e Ryzen com gráficos Vega (ramo de manutenção)",
        AmdDriverBranch.Legacy => "Radeon HD 7000 / R7 / R9 (sem drivers novos)",
        _ => "não identificado",
    };

    [GeneratedRegex(@"\Ahttps://drivers\.amd\.com/drivers/whql-amd-software-adrenalin-edition-([0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2})-[a-z0-9\-]{1,40}\.exe\z", RegexOptions.CultureInvariant)]
    private static partial Regex FullInstallerUrl();

    [GeneratedRegex(@"\Ahttps://drivers\.amd\.com/drivers/installer/[0-9.]{1,10}/(?:beta|whql)/amd-software-adrenalin-edition-([0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2})-minimalsetup-[0-9]{6}_web\.exe\z", RegexOptions.CultureInvariant)]
    private static partial Regex AutoDetectUrl();

    [GeneratedRegex(@"<article\b[^>]*\bdriver-download-details\b[^>]*>(.*?)</article>", RegexOptions.CultureInvariant | RegexOptions.Singleline, 2000)]
    private static partial Regex Article();

    [GeneratedRegex(@"<h4[^>]*>(.*?)</h4>", RegexOptions.CultureInvariant | RegexOptions.Singleline, 1000)]
    private static partial Regex Title();

    [GeneratedRegex(@"<strong>\s*([^<]+?)\s*</strong>\s*<p[^>]*>(.*?)</p>", RegexOptions.CultureInvariant | RegexOptions.Singleline, 1000)]
    private static partial Regex Field();

    [GeneratedRegex(@"href=""(https://drivers\.amd\.com/[^""<>\s]{1,300})""", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Href();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\AAdrenalin\s+([0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex Revision();

    private const string Windows11Header = "Windows 11 - 64-Bit Edition";
    private const string Windows10Header = "Windows 10 - 64-Bit Edition";

    /// <summary>
    /// Instalador completo mais novo da seção do Windows indicado (11 ou 10). Exige: título "AMD Software: Adrenalin Edition",
    /// link https em drivers.amd.com no formato "whql-amd-software-adrenalin-edition-&lt;versão&gt;-….exe", a MESMA versão no
    /// "Revision Number" e tamanho anunciado. Retorna (oferta, null) ou (null, motivo).
    /// </summary>
    public static (VendorDriverOffer? Offer, string? Problem) ParseProductPage(string html, bool windows11)
    {
        ArgumentNullException.ThrowIfNull(html);
        var section = Section(html, windows11 ? Windows11Header : Windows10Header);
        if (section is null) return (null, "A página da AMD mudou de formato (seção do Windows não encontrada).");
        VendorDriverOffer? best = null;
        try
        {
            foreach (Match a in Article().Matches(section))
            {
                var body = a.Groups[1].Value;
                if (Text(Title().Match(body).Groups[1].Value) != "AMD Software: Adrenalin Edition") continue;
                var fields = Field().Matches(body).GroupBy(m => Text(m.Groups[1].Value), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => Text(g.First().Groups[2].Value), StringComparer.Ordinal);
                var link = Href().Matches(body).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).FirstOrDefault(u => FullInstallerUrl().IsMatch(u));
                if (link is null || !fields.TryGetValue("Revision Number", out var rev)) continue;
                var version = FullInstallerUrl().Match(link).Groups[1].Value;
                var revMatch = Revision().Match(rev);
                if (!revMatch.Success || revMatch.Groups[1].Value != version) continue; // página incoerente: ignora
                if (!fields.TryGetValue("File Size", out var sizeText) || VendorDriverPolicy.ParseAnnouncedSize(sizeText) is not { } size ||
                    size <= 0 || size > VendorDriverPolicy.MaxInstallerBytes) continue;
                DateOnly? date = fields.TryGetValue("Release Date", out var dt) && DateOnly.TryParseExact(dt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                var offer = new VendorDriverOffer(GpuVendor.Amd, version, new Uri(link), size, date, null, rev, false);
                if (best is null || VendorVersions.Compare(GpuVendor.Amd, offer.Version, best.Version) > 0) best = offer;
            }
        }
        catch (RegexMatchTimeoutException) { return (null, "A página da AMD é grande demais para ler."); }
        return best is null ? (null, "A página da AMD não trouxe o instalador completo do Adrenalin.") : (best, null);
    }

    /// <summary>Link do "Auto-Detect and Install" (instalador mínimo oficial) em qualquer página da AMD. Null se não achar.</summary>
    public static Uri? ParseAutoDetectUrl(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        try
        {
            var link = Href().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).FirstOrDefault(u => AutoDetectUrl().IsMatch(u));
            return link is null ? null : new Uri(link);
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>Trecho do HTML do cabeçalho da seção até o próximo cabeçalho de seção (accordion) — a primeira ocorrência.</summary>
    private static string? Section(string html, string header)
    {
        var i = html.IndexOf(header, StringComparison.Ordinal);
        if (i < 0) return null;
        var next = html.IndexOf("accordion-header", i + header.Length, StringComparison.Ordinal);
        return html[i..(next < 0 ? html.Length : next)];
    }

    private static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), @"\s+", " ").Trim();
}
