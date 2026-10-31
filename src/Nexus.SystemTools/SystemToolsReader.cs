using System.Diagnostics;
using System.Runtime.Versioning;
using Nexus.SystemTools.Appx;
using Nexus.SystemTools.Drivers;
using Nexus.SystemTools.Network;
using Nexus.SystemTools.Restore;
using Nexus.SystemTools.Wifi;

namespace Nexus.SystemTools;

public sealed record NetworkReading(int? DnsEntries, int? ArpEntries);

public sealed record WifiReading(IReadOnlyList<WifiProfileInfo> Profiles, IReadOnlyDictionary<string, DateTime> LastConnected, string? Note);

public sealed record DriverReading(IReadOnlyList<DriverPackage> Packages, IReadOnlyDictionary<string, IReadOnlyList<string>>? InUse, string? Error);

public sealed record RestoreReading(RestorePointList Points, IReadOnlyList<char>? ProtectedDrives, bool DisabledByPolicy);

public sealed record AppxReading(IReadOnlyList<AppxPackageInfo> Packages, IReadOnlyList<string>? Provisioned, string? Error);

/// <summary>
/// Leituras SOMENTE LEITURA para a página (inventário, contadores). Nada aqui altera o sistema; as mudanças passam pelo
/// aplicador como ações do catálogo.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemToolsReader
{
    private static readonly WindowsProcessRunner Runner = new();

    public static NetworkReading Network()
    {
        var n = new WindowsNetworkBackend(Runner);
        return new NetworkReading(n.CountDns(), n.CountArp());
    }

    public static WifiReading Wifi()
    {
        WifiListing listing;
        try
        {
            var r = Runner.Run(SystemPaths.Tool("netsh.exe"), ["wlan", "show", "profiles"], TimeSpan.FromSeconds(30));
            listing = WifiParsers.ParseNetshProfiles(r.Combined);
            if (r.ExitCode != 0 && listing.Profiles.Count == 0)
                listing = listing with { Note = listing.Note ?? "Sem Wi-Fi neste PC (ou o serviço WLAN AutoConfig está parado)." };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            listing = new WifiListing([], ex.Message);
        }
        return new WifiReading(listing.Profiles, LastConnected(), listing.Note);
    }

    /// <summary>Última conexão por nome de rede (NetworkList do Windows; o nome do perfil costuma ser o da rede).</summary>
    private static Dictionary<string, DateTime> LastConnected()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
            if (root is null) return map;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("ProfileName") is not string name) continue;
                if (WifiParsers.SystemTime(k.GetValue("DateLastConnected") as byte[]) is { } at && (!map.TryGetValue(name, out var old) || at > old))
                    map[name] = at;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return map;
    }

    public static DriverReading Drivers()
    {
        var store = new PnpUtilDriverStore(Runner);
        try { return new DriverReading(store.List(), store.InUse(), null); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return new DriverReading([], null, ex.Message);
        }
    }

    public static RestoreReading RestorePoints() =>
        new(new WindowsRestorePointBackend(Runner).List(), WindowsRestorePointBackend.ProtectedDrives(), WindowsRestorePointBackend.DisabledByPolicy());

    public static AppxReading Appx()
    {
        var b = new PowerShellAppxBackend(Runner);
        try { return new AppxReading(b.ListUser(), b.ListProvisioned(), null); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            return new AppxReading([], null, ex.Message);
        }
    }

    /// <summary>Pastas de backup dos drivers exportados (as mais novas primeiro) dentro da pasta de dados do aplicador.</summary>
    public static IReadOnlyList<string> DriverBackups(string dataDirectory)
    {
        try
        {
            var root = DriverPackageHandler.BackupRoot(dataDirectory);
            if (!Directory.Exists(root)) return [];
            return new DirectoryInfo(root).EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(d => d.CreationTimeUtc).Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return []; }
    }
}

/// <summary>
/// Exceção controlada (brief, regra 2): abrir ferramentas do próprio Windows que o usuário VÊ e controla. Só destinos fixos;
/// a página pede confirmação (Host.Confirm) antes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VendorTools
{
    /// <summary>Restauração do Sistema (rstrui.exe): o próprio Windows conduz a restauração e pede confirmação.</summary>
    public static void OpenSystemRestore() => Start(SystemPaths.Tool("rstrui.exe"));

    /// <summary>Proteção do Sistema (configurar unidades, ativar/desativar).</summary>
    public static void OpenSystemProtection() => Start(SystemPaths.Tool("SystemPropertiesProtection.exe"));

    /// <summary>Configurações > Rede e Internet (onde fica "Redefinição de rede").</summary>
    public static void OpenNetworkSettings() => Start("ms-settings:network-status");

    public static void OpenStorePage(string familyName)
    {
        if (!AppxCatalog.IsValidFamilyName(familyName)) throw new ArgumentException("Família de pacote inválida.", nameof(familyName));
        Start(AppxCatalog.StoreLink(familyName));
    }

    /// <summary>Abre uma pasta existente no Explorador (sem seguir links/junções).</summary>
    public static void OpenFolder(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new DirectoryNotFoundException(path);
        Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), info.FullName);
    }

    private static void Start(string file, string? argument = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = true };
        if (argument is not null) psi.ArgumentList.Add(argument);
        using var _ = Process.Start(psi);
    }
}
