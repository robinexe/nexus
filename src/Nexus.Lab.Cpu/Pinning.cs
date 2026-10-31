using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Lab.Cpu;

/// <summary>Fixa a thread atual num processador lógico. Sem fixação (Linux nos testes), as medições continuam, só sem garantia de núcleo.</summary>
public interface IThreadPinner
{
    /// <summary>True se a thread atual ficou presa ao processador pedido.</summary>
    bool Pin(LogicalCpu cpu);
}

public sealed class NoPinning : IThreadPinner
{
    public static readonly NoPinning Instance = new();
    public bool Pin(LogicalCpu cpu) => false;
}

/// <summary>SetThreadGroupAffinity: funciona também em PCs com mais de 64 processadores lógicos (vários grupos).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsThreadPinner : IThreadPinner
{
    public bool Pin(LogicalCpu cpu)
    {
        var affinity = new GroupAffinity { Mask = (nuint)(1UL << cpu.Number), Group = cpu.Group };
        return SetThreadGroupAffinity(GetCurrentThread(), in affinity, out _);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public nuint Mask;
        public ushort Group;
        public ushort Reserved0, Reserved1, Reserved2;
    }

    [LibraryImport("kernel32.dll")] private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadGroupAffinity(IntPtr thread, in GroupAffinity affinity, out GroupAffinity previous);
}

public static class ThreadPinners
{
    public static IThreadPinner ForThisOs() => OperatingSystem.IsWindows() ? new WindowsThreadPinner() : NoPinning.Instance;
}

/// <summary>
/// Roda trabalho de medição em threads dedicadas (não do pool), com prioridade alta só enquanto mede e presas ao núcleo pedido.
/// A thread morre no fim: nada de prioridade ou afinidade "vaza" para o resto do programa.
/// </summary>
public static class MeasureThreads
{
    public static Task<T> Run<T>(IThreadPinner pinner, LogicalCpu? cpu, Func<bool, T> work, ThreadPriority priority = ThreadPriority.Highest)
    {
        ArgumentNullException.ThrowIfNull(pinner);
        ArgumentNullException.ThrowIfNull(work);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var pinned = cpu is { } c && pinner.Pin(c);
                tcs.TrySetResult(work(pinned));
            }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, 1 << 20)
        {
            IsBackground = true,
            Name = cpu is { } p ? $"NEXUS medição CPU {p.Index}" : "NEXUS medição",
        };
        try { thread.Priority = priority; }
        catch (ThreadStateException) { /* prioridade é melhor esforço */ }
        thread.Start();
        return tcs.Task;
    }
}
