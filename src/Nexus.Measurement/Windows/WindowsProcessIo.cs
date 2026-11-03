using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Bytes lidos por um processo (GetProcessIoCounters.ReadTransferCount: disco e outras E/S), com
/// PROCESS_QUERY_LIMITED_INFORMATION — o mesmo acesso do Gerenciador de Tarefas. Não lê memória nem toca no jogo.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsProcessIo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static ulong? ReadTransferBytes(int pid)
    {
        if (pid <= 4) return null;
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try { return GetProcessIoCounters(h, out var c) ? c.ReadTransferCount : null; }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
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
