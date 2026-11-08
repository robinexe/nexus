using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexus.Lab.Gpu.Windows;

/// <summary>
/// Estados gráficos do Windows: HAGS efetivo (D3DKMTQueryAdapterInfo com KMTQAITYPE_WDDM_2_7_CAPS, a mesma consulta que a
/// página Configurações > Elementos gráficos faz), valores de registro (HwSchMode, OverlayTestMode, Modo de Jogo),
/// energia (bateria/carregador) e preferências de GPU por app. Tudo leitura.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class GraphicsState
{
    private const int KmtqaiTypeWddm27Caps = 70;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromLuid
    {
        public WindowsDisplay.Luid AdapterLuid;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public void* PrivateData;
        public uint PrivateDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint Adapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTOpenAdapterFromLuid(OpenAdapterFromLuid* p);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(QueryAdapterInfo* p);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(CloseAdapter* p);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(SystemPowerStatus* status);

    /// <summary>(suportado, ligado) pelo driver; null quando o Windows/driver não responde a esta consulta (pré-WDDM 2.7).</summary>
    public static (bool? Supported, bool? Enabled) Hags(long luid)
    {
        var open = new OpenAdapterFromLuid { AdapterLuid = WindowsDisplay.Luid.From(luid) };
        if (D3DKMTOpenAdapterFromLuid(&open) != 0) return (null, null);
        try
        {
            uint caps = 0;
            var q = new QueryAdapterInfo { Adapter = open.Adapter, Type = KmtqaiTypeWddm27Caps, PrivateData = &caps, PrivateDataSize = 4 };
            if (D3DKMTQueryAdapterInfo(&q) != 0) return (null, null);
            return ((caps & 0x1) != 0, (caps & 0x2) != 0); // HwSchSupported, HwSchEnabled
        }
        finally
        {
            var c = new CloseAdapter { Adapter = open.Adapter };
            _ = D3DKMTCloseAdapter(&c);
        }
    }

    public static (bool? OnBattery, bool? HasBattery) Power()
    {
        SystemPowerStatus s;
        if (!GetSystemPowerStatus(&s)) return (null, null);
        bool? hasBattery = s.BatteryFlag == 255 ? null : (s.BatteryFlag & 128) == 0;
        bool? onBattery = s.AcLineStatus switch { 0 => true, 1 => false, _ => null };
        return (hasBattery == true ? onBattery : false, hasBattery);
    }

    private static int? Dword(RegistryKey root, string path, string name)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            return k?.GetValue(name) is int v ? v : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static GraphicsSettings Settings()
    {
        var (onBattery, hasBattery) = Power();
        return new GraphicsSettings
        {
            HwSchMode = Dword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode"),
            OverlayTestMode = Dword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode"),
            AutoGameModeEnabled = Dword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled"),
            OnBattery = onBattery,
            HasBattery = hasBattery,
        };
    }

    /// <summary>HKCU\Software\Microsoft\DirectX\UserGpuPreferences (o que Configurações > Elementos gráficos grava).</summary>
    public static IReadOnlyList<AppGpuPreference> AppPreferences()
    {
        var list = new List<AppGpuPreference>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (k is null) return list;
            foreach (var name in k.GetValueNames())
            {
                if (name.Equals("DirectXUserGlobalSettings", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue(name) is string raw) list.Add(new AppGpuPreference(name, GameLibraries.ParseGpuPreference(raw), raw));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    /// <summary>
    /// "Programas instalados" da máquina (HKLM\...\Uninstall, 64 bits e WOW6432Node): DisplayName e InstallLocation de cada
    /// entrada. Só leitura; serve para achar jogos de lojas sem manifesto (Battle.net, EA app) pela lista fechada de
    /// <see cref="GameLibraries.FromInstalledPrograms"/>.
    /// </summary>
    public static IReadOnlyList<(string DisplayName, string? InstallLocation)> InstalledPrograms()
    {
        var list = new List<(string, string?)>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames().Take(1000))
                {
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        if (k?.GetValue("DisplayName") is string display && display.Length > 0)
                            list.Add((display, k.GetValue("InstallLocation") as string));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list;
    }

    public static string? SteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return (k?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
