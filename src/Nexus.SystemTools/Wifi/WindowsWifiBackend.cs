using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.SystemTools.Wifi;

/// <summary>Native Wifi API (wlanapi.dll): WlanEnumInterfaces, WlanGetProfileList, WlanGetProfile, WlanDeleteProfile, WlanSetProfile.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsWifiBackend : IWifiBackend
{
    private const uint ProfileGroupPolicy = 0x1, ProfileUser = 0x2, GetPlaintextKey = 0x4;
    private const int ErrorServiceNotActive = 1062, ErrorNotFound = 1168;
    private const int InterfaceInfoSize = 16 + 512 + 4; // GUID + WCHAR[256] + WLAN_INTERFACE_STATE
    private const int ProfileInfoSize = 512 + 4;        // WCHAR[256] + DWORD

    public IReadOnlyList<Guid> Interfaces()
    {
        using var h = Open();
        return EnumInterfaces(h.Handle);
    }

    public IReadOnlyList<WifiProfileRef> Find(string profileName)
    {
        using var h = Open();
        var result = new List<WifiProfileRef>();
        foreach (var iface in EnumInterfaces(h.Handle))
            foreach (var (name, flags) in Profiles(h.Handle, iface))
                if (string.Equals(name, profileName, StringComparison.Ordinal))
                    result.Add(new WifiProfileRef(iface, name, (flags & ProfileUser) != 0, (flags & ProfileGroupPolicy) != 0));
        return result;
    }

    public unsafe string ExportXml(Guid iface, string profileName)
    {
        using var h = Open();
        var flags = GetPlaintextKey;
        Check(WlanGetProfile(h.Handle, &iface, profileName, IntPtr.Zero, out var xml, ref flags, out _), nameof(WlanGetProfile));
        try { return Marshal.PtrToStringUni(xml) ?? throw new InvalidOperationException("Perfil vazio."); }
        finally { WlanFreeMemory(xml); }
    }

    public unsafe void Delete(Guid iface, string profileName)
    {
        using var h = Open();
        Check(WlanDeleteProfile(h.Handle, &iface, profileName, IntPtr.Zero), nameof(WlanDeleteProfile));
    }

    public unsafe void Import(Guid iface, string xml, bool userOnly)
    {
        using var h = Open();
        var rc = WlanSetProfile(h.Handle, &iface, userOnly ? ProfileUser : 0, xml, null, false, IntPtr.Zero, out var reason);
        if (rc != 0) throw new InvalidOperationException($"WlanSetProfile falhou (código {rc}, motivo {reason}).");
    }

    private static List<Guid> EnumInterfaces(IntPtr handle)
    {
        Check(WlanEnumInterfaces(handle, IntPtr.Zero, out var list), nameof(WlanEnumInterfaces));
        try
        {
            var n = Marshal.ReadInt32(list);
            var result = new List<Guid>(n);
            for (var i = 0; i < n; i++)
                result.Add(Marshal.PtrToStructure<Guid>(list + 8 + i * InterfaceInfoSize));
            return result;
        }
        finally { WlanFreeMemory(list); }
    }

    private static unsafe List<(string Name, uint Flags)> Profiles(IntPtr handle, Guid iface)
    {
        Check(WlanGetProfileList(handle, &iface, IntPtr.Zero, out var list), nameof(WlanGetProfileList));
        try
        {
            var n = Marshal.ReadInt32(list);
            var result = new List<(string, uint)>(n);
            for (var i = 0; i < n; i++)
            {
                var item = list + 8 + i * ProfileInfoSize;
                result.Add((Marshal.PtrToStringUni(item) ?? "", (uint)Marshal.ReadInt32(item + 512)));
            }
            return result;
        }
        finally { WlanFreeMemory(list); }
    }

    private static WlanHandle Open()
    {
        uint rc;
        IntPtr handle;
        try { rc = WlanOpenHandle(2, IntPtr.Zero, out _, out handle); }
        catch (DllNotFoundException) { throw new WifiUnavailableException("Este Windows não tem o componente de Wi-Fi (wlanapi.dll)."); }
        catch (EntryPointNotFoundException) { throw new WifiUnavailableException("Este Windows não tem o componente de Wi-Fi (wlanapi.dll)."); }
        if (rc == ErrorServiceNotActive) throw new WifiUnavailableException("O serviço de Wi-Fi (WLAN AutoConfig) não está em execução ou não há adaptador Wi-Fi.");
        if (rc != 0) throw new WifiUnavailableException($"Wi-Fi indisponível (WlanOpenHandle: código {rc}).");
        return new WlanHandle(handle);
    }

    private static void Check(uint rc, string api)
    {
        if (rc == ErrorNotFound) throw new InvalidOperationException($"{api}: perfil não encontrado.");
        if (rc != 0) throw new InvalidOperationException($"{api} falhou (código {rc}).");
    }

    private readonly record struct WlanHandle(IntPtr Handle) : IDisposable
    {
        public void Dispose() { if (Handle != IntPtr.Zero) _ = WlanCloseHandle(Handle, IntPtr.Zero); }
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanGetProfileList(IntPtr handle, Guid* iface, IntPtr reserved, out IntPtr list);
    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint WlanGetProfile(IntPtr handle, Guid* iface, string name, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);
    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint WlanDeleteProfile(IntPtr handle, Guid* iface, string name, IntPtr reserved);
    [LibraryImport("wlanapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint WlanSetProfile(IntPtr handle, Guid* iface, uint flags, string xml, string? security,
        [MarshalAs(UnmanagedType.Bool)] bool overwrite, IntPtr reserved, out uint reasonCode);
    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(IntPtr memory);
}
