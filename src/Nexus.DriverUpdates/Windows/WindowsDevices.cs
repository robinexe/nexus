using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nexus.Lab.Gpu;
using Nexus.SystemTools;
using Nexus.SystemTools.Drivers;

namespace Nexus.DriverUpdates.Windows;

/// <summary>
/// Dispositivos presentes (SetupAPI, DIGCF_PRESENT | DIGCF_ALLCLASSES) e o driver de cada um, lido da chave do driver
/// (HKLM\SYSTEM\CurrentControlSet\Control\Class\{classe}\NNNN): DriverVersion, DriverDate, InfPath, ProviderName,
/// MatchingDeviceId. Só leitura; não precisa de administrador.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class SetupApiDeviceInventory : IDeviceInventory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW")]
    private static partial IntPtr SetupDiGetClassDevs(Guid* classGuid, char* enumerator, IntPtr hwnd, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr set, uint index, SpDevInfoData* data);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(IntPtr set, SpDevInfoData* data, char* buffer, uint size, uint* required);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryProperty(IntPtr set, SpDevInfoData* data, uint property, uint* regType, byte* buffer, uint size, uint* required);

    private const uint DigcfPresent = 0x2, DigcfAllClasses = 0x4;
    private const uint SpdrpDeviceDesc = 0x0, SpdrpHardwareId = 0x1, SpdrpCompatibleIds = 0x2, SpdrpClass = 0x7, SpdrpClassGuid = 0x8,
        SpdrpDriver = 0x9, SpdrpMfg = 0xB, SpdrpFriendlyName = 0xC;

    [GeneratedRegex(@"\A\{[0-9A-Fa-f\-]{36}\}\\[0-9]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DriverKey();

    public IReadOnlyList<DeviceDriver> List()
    {
        var list = new List<DeviceDriver>();
        var set = SetupDiGetClassDevs(null, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var idBuf = stackalloc char[1024];
        var buf = new byte[16 * 1024];
        try
        {
            for (uint i = 0; i < 5000; i++)
            {
                var data = new SpDevInfoData { CbSize = (uint)sizeof(SpDevInfoData) };
                if (!SetupDiEnumDeviceInfo(set, i, &data)) break;
                uint req;
                if (!SetupDiGetDeviceInstanceId(set, &data, idBuf, 1024, &req)) continue;
                var instance = new string(idBuf);
                var pData = &data;
                var driver = First(set, pData, buf, SpdrpDriver);
                string? version = null, inf = null, provider = null, matching = null;
                DateOnly? date = null;
                if (driver is not null && DriverKey().IsMatch(driver))
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
                    if (key is not null)
                    {
                        version = key.GetValue("DriverVersion") as string;
                        date = DriverInfo.ParseDate(key.GetValue("DriverDate") as string);
                        inf = key.GetValue("InfPath") as string;
                        provider = key.GetValue("ProviderName") as string;
                        matching = key.GetValue("MatchingDeviceId") as string;
                    }
                }
                list.Add(new DeviceDriver(instance, First(set, pData, buf, SpdrpFriendlyName) ?? First(set, pData, buf, SpdrpDeviceDesc) ?? instance,
                    Strings(set, pData, SpdrpHardwareId, buf), Strings(set, pData, SpdrpCompatibleIds, buf),
                    First(set, pData, buf, SpdrpClass), First(set, pData, buf, SpdrpClassGuid)?.ToLowerInvariant(), First(set, pData, buf, SpdrpMfg), version, date, provider, inf, matching));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    private static string? First(IntPtr set, SpDevInfoData* data, byte[] buffer, uint property)
    {
        var l = Strings(set, data, property, buffer);
        return l.Count > 0 ? l[0] : null;
    }

    /// <summary>REG_SZ ou REG_MULTI_SZ da propriedade, como lista.</summary>
    private static List<string> Strings(IntPtr set, SpDevInfoData* data, uint property, byte[] buffer)
    {
        uint type, req;
        fixed (byte* b = buffer)
        {
            if (!SetupDiGetDeviceRegistryProperty(set, data, property, &type, b, (uint)buffer.Length, &req)) return [];
            var len = (int)Math.Min(req, (uint)buffer.Length);
            var text = Encoding.Unicode.GetString(buffer, 0, len - len % 2);
            return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }
    }
}

/// <summary>pnputil (repositório de drivers) + newdev.dll (instalação forçada, como o "Atualizar driver" do Gerenciador de Dispositivos).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDriverPackageTool : IDriverPackageTool
{
    private readonly PnpUtilDriverStore _store = new(new WindowsProcessRunner());

    [LibraryImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string fullInfPath, uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    private const uint InstallFlagForce = 0x1, InstallFlagNonInteractive = 0x4;

    public string? OriginalInfName(string publishedName) =>
        _store.List().FirstOrDefault(p => string.Equals(p.PublishedName, publishedName, StringComparison.OrdinalIgnoreCase))?.OriginalName;

    public void Export(string publishedName, string directory) => _store.Export(publishedName, directory);

    public void Add(string infPath) => _store.Add(infPath);

    public bool ForceInstall(string hardwareId, string infPath)
    {
        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, hardwareId, Path.GetFullPath(infPath), InstallFlagForce | InstallFlagNonInteractive, out var reboot))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"O Windows não reinstalou o driver anterior ({Path.GetFileName(infPath)}).");
        return reboot;
    }

    public string InboxInfDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
}

[SupportedOSPlatform("windows")]
public sealed class WindowsBootClock : IBootClock
{
    public DateTime LastBootUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>Reinício pendente: Windows Update (SystemInfo) ou as chaves que o próprio Windows usa para isso.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsRebootState
{
    public static bool? Pending(IWindowsUpdateAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var wu = agent.RebootRequired();
        if (wu == true) return true;
        var lm = Registry.LocalMachine;
        var keys = Exists(lm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") ||
                   Exists(lm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        return keys || wu == false ? keys : null;
    }

    private static bool Exists(RegistryKey root, string path)
    {
        try { using var k = root.OpenSubKey(path); return k is not null; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
}
