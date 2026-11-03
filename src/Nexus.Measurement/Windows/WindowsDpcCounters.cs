using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Contadores documentados "\Processor Information(*)" — % DPC Time, % Interrupt Time, DPCs Queued/sec, Interrupts/sec — por
/// núcleo (instâncias "0,0", "0,1"…; os totais "_Total" ficam de fora). PDH com nomes em inglês (PdhAddEnglishCounter), então
/// funciona em qualquer idioma do Windows. Nível de usuário: não precisa de administrador e não muda nada no sistema.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsDpcCounters : IDpcCounterSource
{
    private const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint CStatusValidData = 0, CStatusNewData = 1;

    private readonly IntPtr _query;
    private readonly IntPtr[] _counters = new IntPtr[4];
    private readonly List<string> _unavailable = [];
    private IntPtr _buffer;
    private uint _bufferSize;
    private bool _disposed;

    private static readonly string[] Paths =
    [
        @"\Processor Information(*)\% DPC Time",
        @"\Processor Information(*)\% Interrupt Time",
        @"\Processor Information(*)\DPCs Queued/sec",
        @"\Processor Information(*)\Interrupts/sec",
    ];

    public WindowsDpcCounters()
    {
        var status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0)
        {
            _unavailable.Add($"Contadores de desempenho do Windows indisponíveis (PDH 0x{status:X8}).");
            return;
        }
        for (var i = 0; i < Paths.Length; i++)
        {
            status = PdhAddEnglishCounterW(_query, Paths[i], IntPtr.Zero, out _counters[i]);
            if (status != 0) _unavailable.Add($"{Paths[i]} (PDH 0x{status:X8})");
        }
    }

    public IReadOnlyList<string> Unavailable => _unavailable;

    public IReadOnlyList<CoreDpcReading>? Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_query == IntPtr.Zero || _counters[0] == IntPtr.Zero || _counters[1] == IntPtr.Zero) return null;
        if (PdhCollectQueryData(_query) != 0) return null;
        var dpc = Array(_counters[0]);
        var isr = Array(_counters[1]);
        var dpcs = Array(_counters[2]);
        var ints = Array(_counters[3]);
        if (dpc.Count == 0) return null;
        var list = new List<CoreDpcReading>(dpc.Count);
        foreach (var (core, d) in dpc)
        {
            if (core.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new CoreDpcReading(core, Math.Max(0, d), Math.Max(0, isr.GetValueOrDefault(core)),
                Math.Max(0, dpcs.GetValueOrDefault(core)), Math.Max(0, ints.GetValueOrDefault(core))));
        }
        return list.OrderBy(c => c.Core, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Dictionary<string, double> Array(IntPtr counter)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter == IntPtr.Zero) return values;
        const uint format = PdhFmtDouble | PdhFmtNoCap100;
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return values;
        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(size, _bufferSize * 2);
            _buffer = Marshal.AllocHGlobal((nint)_bufferSize);
        }
        size = _bufferSize;
        status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, _buffer);
        if (status != 0) return values;
        var items = (PdhFmtCounterValueItem*)_buffer;
        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            if (item.Value.CStatus is not (CStatusValidData or CStatusNewData) || item.Name == IntPtr.Zero) continue;
            var name = Marshal.PtrToStringUni(item.Name);
            if (name is not null && double.IsFinite(item.Value.DoubleValue)) values[name] = item.Value.DoubleValue;
        }
        return values;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(IntPtr query);
}
