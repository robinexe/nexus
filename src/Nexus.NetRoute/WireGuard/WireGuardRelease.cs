using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.NetRoute.WireGuard;

/// <summary>Instalador MSI oficial do WireGuard para Windows, com o SHA-256 publicado pela WireGuard LLC.</summary>
public sealed record WireGuardMsi(string FileName, string Architecture, Version Version, string Sha256)
{
    public Uri Url => new(WireGuardRelease.BaseUrl + FileName);
}

/// <summary>
/// A WireGuard LLC publica em <c>https://download.wireguard.com/windows-client/latest.sig</c> a lista assinada dos instaladores
/// atuais ("&lt;sha256&gt;  wireguard-&lt;arq&gt;-&lt;versão&gt;.msi"; é o que o próprio instalador oficial lê). O NEXUS escolhe o MSI da
/// arquitetura do PC, baixa do mesmo servidor, confere o SHA-256 da lista E a assinatura Authenticode "WireGuard LLC" antes de
/// executar. Qualquer divergência = não instala.
/// </summary>
public static partial class WireGuardRelease
{
    public const string BaseUrl = "https://download.wireguard.com/windows-client/";
    public const string ListFile = "latest.sig";
    public const string Publisher = "WireGuard LLC";

    [GeneratedRegex(@"^(?<sha>[0-9a-f]{64})\s+(?<file>wireguard-(?<arch>amd64|arm64|x86)-(?<ver>[0-9]+(?:\.[0-9]+){1,3})\.msi)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    public static IReadOnlyList<WireGuardMsi> ParseList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var list = new List<WireGuardMsi>();
        foreach (Match m in Line().Matches(text.Replace("\r", "", StringComparison.Ordinal)))
            if (Version.TryParse(m.Groups["ver"].Value, out var v))
                list.Add(new WireGuardMsi(m.Groups["file"].Value, m.Groups["arch"].Value, v, m.Groups["sha"].Value));
        return list;
    }

    /// <summary>MSI mais novo para a arquitetura ("amd64", "arm64" ou "x86"); null se a lista não tiver.</summary>
    public static WireGuardMsi? Pick(IReadOnlyList<WireGuardMsi> list, string architecture)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.Where(m => m.Architecture == architecture).MaxBy(m => m.Version);
    }

    public static string ArchitectureOf(System.Runtime.InteropServices.Architecture arch) => arch switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "amd64",
    };

    public static string Describe(WireGuardMsi msi)
    {
        ArgumentNullException.ThrowIfNull(msi);
        return string.Create(CultureInfo.InvariantCulture, $"WireGuard {msi.Version} ({msi.Architecture})");
    }
}
