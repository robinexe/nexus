using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates.Vendor;

/// <summary>Um driver de vídeo mais novo achado no serviço oficial do fabricante (NVIDIA ou AMD).</summary>
/// <param name="Version">Versão do fabricante: NVIDIA "621.01", AMD "26.5.2".</param>
/// <param name="DownloadUrl">Instalador completo (https, host oficial conferido por <see cref="VendorDriverPolicy.IsAllowedDownload"/>).</param>
/// <param name="ApproxBytes">Tamanho anunciado pelo fabricante ("1.04 GB", "623 MB"), aproximado. O tamanho exato vem do servidor.</param>
/// <param name="Label">Nome do pacote como o fabricante mostra (ex.: "Driver GeForce Game Ready", "Adrenalin 26.5.2 (WHQL Recommended)").</param>
public sealed record VendorDriverOffer(
    GpuVendor Vendor,
    string Version,
    Uri DownloadUrl,
    long ApproxBytes,
    DateOnly? ReleaseDate,
    string? DetailsUrl,
    string Label,
    bool Notebook);

/// <summary>
/// Regras fixas de confiança para baixar e instalar o driver do fabricante: hosts permitidos (para cada redirecionamento também),
/// limite de tamanho, assinante exigido, formato da versão, nome e pasta do arquivo. Tudo puro (testável em qualquer SO).
/// </summary>
public static partial class VendorDriverPolicy
{
    /// <summary>Limite do instalador (o da NVIDIA tem ~1,0 GB; o da AMD ~0,6–1,0 GB).</summary>
    public const long MaxInstallerBytes = 1536L * 1024 * 1024;
    public const int MaxRedirects = 8;
    /// <summary>Folga em disco além do instalador: o instalador extrai ~2x o tamanho na pasta temporária do Windows.</summary>
    public const long ExtraFreeSpaceBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>Hosts de onde o INSTALADOR pode vir.</summary>
    public static IReadOnlySet<string> NvidiaDownloadHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "us.download.nvidia.com", "international.download.nvidia.com", "download.nvidia.com",
    };

    public static IReadOnlySet<string> AmdDownloadHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "drivers.amd.com",
    };

    /// <summary>Hosts das CONSULTAS (lista de produtos e versão mais nova).</summary>
    public static IReadOnlySet<string> LookupHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gfwsl.geforce.com", "www.nvidia.com", "www.amd.com",
    };

    /// <summary>
    /// Nome simples (CN) do certificado do assinante exigido. Conferido em 01/11/2026 nos instaladores reais: NVIDIA
    /// "CN=NVIDIA Corporation, O=NVIDIA Corporation" (DigiCert); AMD "CN=Advanced Micro Devices, O=Advanced Micro Devices" (Sectigo).
    /// "Advanced Micro Devices, Inc." era o nome de certificados anteriores da AMD.
    /// </summary>
    public static IReadOnlyList<string> AllowedSigners(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => ["NVIDIA Corporation"],
        GpuVendor.Amd => ["Advanced Micro Devices", "Advanced Micro Devices, Inc."],
        _ => [],
    };

    public static bool IsAllowedSigner(GpuVendor vendor, string? signer) =>
        signer is not null && AllowedSigners(vendor).Contains(signer.Trim(), StringComparer.Ordinal);

    /// <summary>Só https, porta padrão, sem usuário/senha, host oficial do fabricante. Vale para o endereço inicial e cada redirecionamento.</summary>
    public static bool IsAllowedDownload(GpuVendor vendor, Uri? url) =>
        url is { IsAbsoluteUri: true } && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) &&
        (vendor switch { GpuVendor.Nvidia => NvidiaDownloadHosts, GpuVendor.Amd => AmdDownloadHosts, _ => new HashSet<string>() }).Contains(url.IdnHost) &&
        url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedLookup(Uri? url) =>
        url is { IsAbsoluteUri: true } && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) && LookupHosts.Contains(url.IdnHost);

    // ------------------------------------------------------------------ parâmetros do pedido ao aplicador

    /// <summary>Os mesmos padrões do catálogo (<c>driverupdates.gpu.install</c>): o broker valida e o handler confere de novo, por fabricante.</summary>
    public const string VendorRegex = "nvidia|amd";
    public const string VersionRegex = @"[0-9]{2,3}\.[0-9]{1,2}(?:\.[0-9]{1,2})?";
    public const string Sha256Regex = "[0-9a-f]{64}";

    [GeneratedRegex(@"\A[0-9]{3}\.[0-9]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaVersion();

    [GeneratedRegex(@"\A[0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AmdVersion();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    public static bool IsValidVersion(GpuVendor vendor, string? version) => version is not null && vendor switch
    {
        GpuVendor.Nvidia => NvidiaVersion().IsMatch(version),
        GpuVendor.Amd => AmdVersion().IsMatch(version),
        _ => false,
    };

    public static bool IsValidSha256(string? hex) => hex is not null && Sha256Hex().IsMatch(hex);

    public static string VendorToken(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "nvidia",
        GpuVendor.Amd => "amd",
        _ => throw new ArgumentException("Só NVIDIA e AMD têm instalação pelo NEXUS.", nameof(vendor)),
    };

    public static GpuVendor? VendorFromToken(string? token) => token switch
    {
        "nvidia" => GpuVendor.Nvidia,
        "amd" => GpuVendor.Amd,
        _ => null,
    };

    // ------------------------------------------------------------------ arquivos

    /// <summary>Nome fixo do instalador (nunca vem da URL nem do usuário).</summary>
    public static string FileName(GpuVendor vendor, string version)
    {
        if (!IsValidVersion(vendor, version)) throw new ArgumentException("Versão em formato inesperado.", nameof(version));
        return vendor == GpuVendor.Nvidia ? $"nvidia-{version}.exe" : $"amd-adrenalin-{version}.exe";
    }

    /// <summary>
    /// Pasta dos downloads do usuário: <c>&lt;perfil&gt;\AppData\Local\NEXUS\drivers</c>. A interface e o aplicador usam a MESMA
    /// fórmula a partir da pasta do perfil (o aplicador como serviço acha o perfil pelo SID em ProfileList).
    /// </summary>
    public static string UserDownloadRoot(string userProfile) => Path.Combine(userProfile, "AppData", "Local", "NEXUS", "drivers");

    /// <summary><c>&lt;raiz&gt;\&lt;fabricante&gt;\&lt;versão&gt;\&lt;arquivo&gt;</c>.</summary>
    public static string InstallerPath(string root, GpuVendor vendor, string version) =>
        Path.Combine(root, VendorToken(vendor), version, FileName(vendor, version));

    /// <summary>Cópia do aplicador elevado (pasta só de administradores): <c>DataDirectory\drivers\&lt;fabricante&gt;\&lt;versão&gt;\</c>.</summary>
    public static string AdminCopyPath(string dataDirectory, GpuVendor vendor, string version) =>
        InstallerPath(Path.Combine(dataDirectory, "drivers"), vendor, version);

    /// <summary>"1.04 GB", "912.74 MB", "623 MB" (unidades decimais, como os sites mostram) → bytes. Null se não reconhecer.</summary>
    public static long? ParseAnnouncedSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SizeText().Match(Uri.UnescapeDataString(text).Trim());
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return null;
        var mult = m.Groups[2].Value.ToUpperInvariant() switch { "GB" => 1e9, "MB" => 1e6, "KB" => 1e3, _ => 0 };
        return mult == 0 ? null : (long)(n * mult);
    }

    [GeneratedRegex(@"\A([0-9]+(?:\.[0-9]+)?)\s*(GB|MB|KB)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SizeText();

    /// <summary>O tamanho exato (do servidor) está perto do anunciado: até 5% de diferença (o anúncio é arredondado).</summary>
    public static bool SizeMatchesAnnounced(long exact, long announced) =>
        exact > 0 && announced > 0 && Math.Abs(exact - announced) <= Math.Max(announced / 20, 2_000_000);
}

/// <summary>Versões dos drivers de vídeo: conversão Windows ↔ fabricante e comparação.</summary>
public static class VendorVersions
{
    /// <summary>
    /// NVIDIA "512.74" → 51274. A NVIDIA numera a versão do Windows com os 5 últimos dígitos dos dois campos finais
    /// (30.0.15.1274 → 512.74), então os dois lados viram o mesmo número.
    /// </summary>
    public static int? NvidiaKey(string? vendorVersion)
    {
        if (vendorVersion is null) return null;
        var p = vendorVersion.Trim().Split('.');
        if (p.Length != 2 || p[0].Length is < 1 or > 3 || p[1].Length != 2 || !p.All(x => x.All(char.IsAsciiDigit))) return null;
        return int.Parse(p[0], CultureInfo.InvariantCulture) * 100 + int.Parse(p[1], CultureInfo.InvariantCulture);
    }

    /// <summary>Windows "30.0.15.1274" → 51274 (os 5 últimos dígitos de "15" + "1274"). Null se não for a.b.c.d numérico.</summary>
    public static int? NvidiaKeyFromWindows(string? windowsVersion)
    {
        var decoded = DriverInfo.Decode(GpuVendor.Nvidia, windowsVersion).VendorVersion;
        return NvidiaKey(decoded);
    }

    /// <summary>NVIDIA a partir da versão do Windows: "30.0.15.1274" → "512.74".</summary>
    public static string? NvidiaFromWindows(string? windowsVersion) => DriverInfo.Decode(GpuVendor.Nvidia, windowsVersion).VendorVersion;

    /// <summary>-1/0/1 entre duas versões do fabricante (NVIDIA "xxx.yy"; AMD "aa.b.c"). Null se não der para comparar.</summary>
    public static int? Compare(GpuVendor vendor, string? a, string? b)
    {
        if (vendor == GpuVendor.Nvidia)
            return NvidiaKey(a) is { } x && NvidiaKey(b) is { } y ? Math.Sign(x.CompareTo(y)) : null;
        if (vendor == GpuVendor.Amd && a is not null && b is not null && Version.TryParse(a.Trim(), out var va) && Version.TryParse(b.Trim(), out var vb))
            return Math.Sign(new Version(va.Major, va.Minor, Math.Max(0, va.Build)).CompareTo(new Version(vb.Major, vb.Minor, Math.Max(0, vb.Build))));
        return null;
    }
}
