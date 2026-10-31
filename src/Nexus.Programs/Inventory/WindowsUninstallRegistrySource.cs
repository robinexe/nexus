using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexus.Programs.Inventory;

/// <summary>
/// Lê (só leitura) as chaves Uninstall: HKLM 64 bits, HKLM 32 bits (WOW6432Node) e o HKCU do usuário. Com
/// <paramref name="userSid"/>, o "HKCU" é <c>HKU\&lt;SID&gt;</c> (aplicador rodando como serviço); com
/// <paramref name="includeCurrentUser"/> = false, o HKCU fica de fora (outra conta sem SID: seria o perfil errado).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUninstallRegistrySource(string? userSid = null, bool includeCurrentUser = true) : IUninstallRegistrySource
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<UninstallEntryRaw> ReadAll()
    {
        var list = new List<UninstallEntryRaw>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, null, InstallScope.Machine64, list);
        if (Environment.Is64BitOperatingSystem)
            Read(RegistryHive.LocalMachine, RegistryView.Registry32, null, InstallScope.Machine32, list);
        if (userSid is not null) Read(RegistryHive.Users, RegistryView.Default, userSid, InstallScope.User, list);
        else if (includeCurrentUser) Read(RegistryHive.CurrentUser, RegistryView.Default, null, InstallScope.User, list);
        return list;
    }

    private static void Read(RegistryHive hive, RegistryView view, string? sidPrefix, InstallScope scope, List<UninstallEntryRaw> into)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(sidPrefix is null ? UninstallPath : sidPrefix + "\\" + UninstallPath, writable: false);
            if (uninstall is null) return;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var k = uninstall.OpenSubKey(name, writable: false);
                    if (k is null) continue;
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var vn in k.GetValueNames())
                    {
                        if (vn.Length == 0) continue;
                        if (ToText(k.GetValue(vn)) is { } text) values[vn] = text;
                    }
                    into.Add(new UninstallEntryRaw(scope, name, values));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static string? ToText(object? v) => v switch
    {
        null => null,
        string s => s,
        int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
        long l => unchecked((ulong)l).ToString(CultureInfo.InvariantCulture),
        string[] a => string.Join(" ", a),
        _ => null,
    };
}
