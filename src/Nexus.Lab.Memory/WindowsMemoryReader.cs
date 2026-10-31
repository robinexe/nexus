using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Nexus.Lab.Memory;

/// <summary>
/// Leituras no Windows (nenhuma escreve): SMBIOS bruto, contadores de memória, compressão, arquivo de paginação, SysMain.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class WindowsMemoryReader
{
    public const string MemoryManagementKey = PageFileHandler.MemoryManagementKey;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint provider, uint id, byte* buffer, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    /// <summary>Tabela SMBIOS bruta (GetSystemFirmwareTable 'RSMB'); funciona sem administrador.</summary>
    public static byte[] ReadSmbios()
    {
        const uint rsmb = 0x52534D42;
        var size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetSystemFirmwareTable('RSMB') não informou o tamanho.");
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var got = GetSystemFirmwareTable(rsmb, 0, p, size);
            if (got == 0 || got > size) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetSystemFirmwareTable('RSMB') falhou.");
        }
        return buffer;
    }

    /// <summary>Memória física livre agora (para dimensionar as medições).</summary>
    public static ulong? AvailablePhysical()
    {
        var s = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        return GlobalMemoryStatusEx(ref s) ? s.AvailPhys : null;
    }

    /// <summary>Lê o estado da memória. Leva ~1 s (amostra de taxas) e alguns segundos se precisar do PowerShell.</summary>
    public static MemoryState ReadState(string? sysMainState, string? sysMainStartType, CancellationToken cancel = default)
    {
        var problems = new Dictionary<string, string>();
        var state = new MemoryState { SysMainState = sysMainState, SysMainStartType = sysMainStartType, Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64) };

        var ms = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        if (GlobalMemoryStatusEx(ref ms))
            state = state with { TotalPhysicalBytes = ms.TotalPhys, AvailablePhysicalBytes = ms.AvailPhys };
        else problems["memória"] = "GlobalMemoryStatusEx falhou.";

        var pi = new PerformanceInformation { Cb = (uint)sizeof(PerformanceInformation) };
        if (GetPerformanceInfo(ref pi, pi.Cb))
        {
            var page = (ulong)pi.PageSize;
            state = state with
            {
                CommitTotalBytes = (ulong)pi.CommitTotal * page,
                CommitLimitBytes = (ulong)pi.CommitLimit * page,
                CommitPeakBytes = (ulong)pi.CommitPeak * page,
                SystemCacheBytes = (ulong)pi.SystemCache * page,
            };
        }
        else problems["commit"] = "GetPerformanceInfo falhou.";

        try
        {
            var c = Pdh.Sample(
                [@"\Memory\Standby Cache Core Bytes", @"\Memory\Standby Cache Normal Priority Bytes", @"\Memory\Standby Cache Reserve Bytes",
                 @"\Memory\Modified Page List Bytes", @"\Memory\Free & Zero Page List Bytes", @"\Memory\Page Faults/sec", @"\Memory\Pages Input/sec"],
                TimeSpan.FromSeconds(1), cancel);
            ulong? U(int i) => c[i] is { } v && v >= 0 ? (ulong)v : null;
            ulong? standby = U(0) is null && U(1) is null && U(2) is null ? null : (U(0) ?? 0) + (U(1) ?? 0) + (U(2) ?? 0);
            state = state with { StandbyBytes = standby, ModifiedBytes = U(3), FreeZeroBytes = U(4), PageFaultsPerSec = c[5], PagesInputPerSec = c[6] };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { problems["contadores"] = ex.Message; }

        // Arquivo de paginação
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKey);
            var raw = key?.GetValue("PagingFiles", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var lines = raw switch { string[] arr => arr, string s => [s], null => key is null ? null : Array.Empty<string>(), _ => null };
            var cfg = PageFileConfig.Parse(lines);
            state = state with { PageFile = cfg, PageFileCurrentBytes = PageFileSizes(cfg) };
            using var pf = key?.OpenSubKey("PrefetchParameters");
            if (pf?.GetValue("EnablePrefetcher") is int ep) state = state with { EnablePrefetcher = ep };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { problems["paginação"] = ex.Message; }

        // Compressão de memória
        cancel.ThrowIfCancellationRequested();
        var (compression, combining, source) = ReadCompression(cancel);
        state = state with { CompressionEnabled = compression, PageCombining = combining, CompressionSource = source };

        return state with { Problems = problems };
    }

    private static ulong? PageFileSizes(PageFileConfig cfg)
    {
        if (cfg.Mode is PageFileMode.None or PageFileMode.Unknown) return cfg.Mode == PageFileMode.None ? 0 : null;
        ulong total = 0;
        var found = false;
        IEnumerable<string> paths = cfg.Mode == PageFileMode.AutomaticAllDrives
            ? DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => Path.Combine(d.RootDirectory.FullName, "pagefile.sys"))
            : cfg.Entries.Select(e => e.Path);
        foreach (var p in paths)
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) continue;
                total += (ulong)fi.Length;
                found = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return found ? total : null;
    }

    /// <summary>
    /// Get-MMAgent (PowerShell oculto, sem perfil). Sem ele (pode exigir administrador em algumas versões), cai para a presença do
    /// processo "Memory Compression", que só existe com a compressão ligada.
    /// </summary>
    private static (bool? Compression, bool? Combining, string Source) ReadCompression(CancellationToken cancel)
    {
        try
        {
            var json = RunPowerShell("[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-MMAgent | Select-Object MemoryCompression,PageCombining | ConvertTo-Json -Compress", TimeSpan.FromSeconds(20), cancel);
            if (json is not null)
            {
                var start = json.IndexOf('{', StringComparison.Ordinal);
                if (start >= 0)
                {
                    using var doc = JsonDocument.Parse(json[start..]);
                    var root = doc.RootElement;
                    bool? B(string n) => root.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
                    if (B("MemoryCompression") is { } mc) return (mc, B("PageCombining"), "Get-MMAgent");
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or JsonException or IOException) { }

        try
        {
            var procs = Process.GetProcessesByName("Memory Compression");
            var found = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return (found, null, found ? "processo Memory Compression presente" : "processo Memory Compression ausente");
        }
        catch (InvalidOperationException) { return (null, null, "não foi possível ler"); }
    }

    internal static string? RunPowerShell(string command, TimeSpan timeout, CancellationToken cancel)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe)) return null;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        var stdout = p.StandardOutput.ReadToEndAsync(cancel);
        _ = p.StandardError.ReadToEndAsync(cancel);
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        return p.ExitCode == 0 ? stdout.GetAwaiter().GetResult() : null;
    }

    /// <summary>Consulta PDH mínima: duas coletas separadas por <paramref name="interval"/> (taxas precisam de duas amostras).</summary>
    internal static partial class Pdh
    {
        private const uint FmtDouble = 0x00000200, FmtNoCap100 = 0x00008000;

        [StructLayout(LayoutKind.Sequential)]
        private struct FmtCounterValue { public uint Status; public uint Pad; public double Value; }

        [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhOpenQuery(string? source, nint userData, out nint query);

        [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

        [LibraryImport("pdh.dll")]
        private static partial uint PdhCollectQueryData(nint query);

        [LibraryImport("pdh.dll")]
        private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, nint type, out FmtCounterValue value);

        [LibraryImport("pdh.dll")]
        private static partial uint PdhCloseQuery(nint query);

        [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
        private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

        /// <summary>Contador com curinga (ex.: <c>\GPU Adapter Memory(*)\Shared Usage</c>): instância → valor, numa única coleta.</summary>
        public static Dictionary<string, double> SampleInstances(string path)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (PdhOpenQuery(null, 0, out var q) != 0) return result;
            try
            {
                if (PdhAddEnglishCounter(q, path, 0, out var c) != 0) return result;
                if (PdhCollectQueryData(q) != 0) return result;
                uint size = 0;
                const uint moreData = 0x800007D2;
                if (PdhGetFormattedCounterArray(c, FmtDouble | FmtNoCap100, ref size, out _, 0) != moreData || size == 0) return result;
                var buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArray(c, FmtDouble | FmtNoCap100, ref size, out var count, buf) != 0) return result;
                    // PDH_FMT_COUNTERVALUE_ITEM_W: LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue (Status, pad, double).
                    var itemSize = IntPtr.Size + sizeof(FmtCounterValue);
                    for (var i = 0; i < count; i++)
                    {
                        var item = buf + i * itemSize;
                        var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                        var value = Marshal.PtrToStructure<FmtCounterValue>(item + IntPtr.Size);
                        if (name is not null && value.Status is 0 or 1) result[name] = result.GetValueOrDefault(name) + value.Value;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
                return result;
            }
            finally { _ = PdhCloseQuery(q); }
        }

        public static double?[] Sample(string[] paths, TimeSpan interval, CancellationToken cancel)
        {
            if (PdhOpenQuery(null, 0, out var q) != 0) throw new InvalidOperationException("PdhOpenQuery falhou.");
            try
            {
                var counters = new nint[paths.Length];
                for (var i = 0; i < paths.Length; i++)
                    if (PdhAddEnglishCounter(q, paths[i], 0, out var c) == 0) counters[i] = c;
                _ = PdhCollectQueryData(q);
                cancel.WaitHandle.WaitOne(interval);
                cancel.ThrowIfCancellationRequested();
                _ = PdhCollectQueryData(q);
                var result = new double?[paths.Length];
                for (var i = 0; i < paths.Length; i++)
                {
                    if (counters[i] == 0) continue;
                    if (PdhGetFormattedCounterValue(counters[i], FmtDouble | FmtNoCap100, 0, out var v) == 0 && v.Status is 0 or 1)
                        result[i] = v.Value;
                }
                return result;
            }
            finally { _ = PdhCloseQuery(q); }
        }
    }
}
