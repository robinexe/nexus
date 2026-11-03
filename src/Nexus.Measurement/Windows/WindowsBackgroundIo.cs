using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Contadores de E/S (GetProcessIoCounters) só dos programas de <see cref="BackgroundActivityRules.Processes"/>, abertos com acesso
/// LIMITADO (PROCESS_QUERY_LIMITED_INFORMATION: não lê memória nem mexe no processo). Só leitura.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsBackgroundIo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static IReadOnlyList<ProcessIo> Sample()
    {
        var list = new List<ProcessIo>();
        foreach (var exe in BackgroundActivityRules.Processes.Keys)
        {
            Process[] found;
            try { found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)); }
            catch (InvalidOperationException) { continue; }
            foreach (var p in found)
            {
                using (p)
                {
                    var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        if (GetProcessIoCounters(h, out var io))
                            list.Add(new ProcessIo(p.Id, exe, io.ReadTransferCount + io.WriteTransferCount + io.OtherTransferCount));
                    }
                    finally { CloseHandle(h); }
                }
            }
        }
        return list;
    }

    /// <summary>Duas leituras com <paramref name="interval"/> entre elas (chame fora da thread da interface).</summary>
    public static IReadOnlyList<BackgroundActivity> Measure(TimeSpan interval)
    {
        var before = Sample();
        if (before.Count == 0) return [];
        var watch = Stopwatch.StartNew();
        Thread.Sleep(interval);
        var after = Sample();
        return BackgroundActivityRules.Busy(before, after, watch.Elapsed.TotalSeconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
