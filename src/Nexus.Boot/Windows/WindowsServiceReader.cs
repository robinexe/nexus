using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.Boot.Inventory;

namespace Nexus.Boot.Windows;

/// <summary>
/// Lista completa de serviços Win32 (somente leitura): configuração do registro (HKLM\SYSTEM\CurrentControlSet\Services, legível
/// por qualquer usuário) e estado atual pelo Gerenciador de Controle de Serviços (EnumServicesStatusExW).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsServiceReader : IServiceReader
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public IReadOnlyList<ServiceInfo> All()
    {
        var states = States();
        var list = new List<ServiceInfo>();
        using var root = Registry.LocalMachine.OpenSubKey(ServicesKey, writable: false);
        if (root is null) return list;
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name, writable: false);
                if (key is null || key.GetValue("Type") is not int type || !ServicePolicy.IsWin32Service(type)) continue;
                var start = key.GetValue("Start") is int s ? s : -1;
                var delayed = key.GetValue("DelayedAutostart") is int d && d != 0;
                var startType = start switch
                {
                    0 => "Boot",
                    1 => "System",
                    2 => delayed ? "AutomaticDelayed" : "Automatic",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Unknown",
                };
                string? serviceDll = null;
                using (var p = key.OpenSubKey("Parameters", writable: false))
                    serviceDll = p?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var deps = key.GetValue("DependOnService") as string[] ?? [];
                states.TryGetValue(name, out var state);
                list.Add(new ServiceInfo(
                    name,
                    state.DisplayName ?? Indirect(key.GetValue("DisplayName") as string) ?? name,
                    Indirect(key.GetValue("Description") as string),
                    startType,
                    state.State ?? "Unknown",
                    key.GetValue("ObjectName") as string,
                    deps.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                    key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    serviceDll,
                    type));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return list;
    }

    /// <summary>"@%SystemRoot%\system32\x.dll,-101" → texto localizado (SHLoadIndirectString); texto comum passa direto.</summary>
    private static unsafe string? Indirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@') return value;
        var buffer = stackalloc char[1024];
        return SHLoadIndirectString(value, buffer, 1024, IntPtr.Zero) == 0 ? new string(buffer) : null;
    }

    private static readonly string[] StateNames = ["", "Stopped", "StartPending", "StopPending", "Running", "ContinuePending", "PausePending", "Paused"];

    private static Dictionary<string, (string? State, string? DisplayName)> States()
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var scm = OpenSCManagerW(null, null, 0x0004); // SC_MANAGER_ENUMERATE_SERVICE
        if (scm == IntPtr.Zero) return result;
        try
        {
            uint resume = 0;
            var size = 64 * 1024;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                while (true)
                {
                    var ok = EnumServicesStatusExW(scm, 0, 0x30, 3, buffer, (uint)size, out var needed, out var count, ref resume, null);
                    var err = ok ? 0 : Marshal.GetLastPInvokeError();
                    if (!ok && err != 234) break; // ERROR_MORE_DATA
                    var stride = (2 * IntPtr.Size + 36 + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
                    for (var i = 0; i < count; i++)
                    {
                        var entry = buffer + i * stride;
                        var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry));
                        var display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, IntPtr.Size));
                        var state = Marshal.ReadInt32(entry, 2 * IntPtr.Size + 4);
                        if (name is not null) result[name] = (state >= 0 && state < StateNames.Length ? StateNames[state] : state.ToString(CultureInfo.InvariantCulture), display);
                    }
                    if (ok) break;
                    if (needed > size)
                    {
                        Marshal.FreeHGlobal(buffer);
                        size = (int)needed;
                        buffer = Marshal.AllocHGlobal(size);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseServiceHandle(scm); }
        return result;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr services,
        uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* outBuf, uint outBufSize, IntPtr reserved);
}
