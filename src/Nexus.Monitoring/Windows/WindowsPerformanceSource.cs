using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Monitoring.Windows;

/// <summary>
/// Contadores do Windows por PDH (a mesma fonte do Gerenciador de Tarefas e do Monitor de Desempenho): uma consulta aberta com
/// todos os contadores, uma coleta por tique. Nomes em inglês (PdhAddEnglishCounter) para funcionar em Windows em português.
/// Memória por GlobalMemoryStatusEx + GetPerformanceInfo. Somente leitura.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsPerformanceSource : IPerformanceSource
{
    private const uint PdhFmtDouble = 0x00000200, PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint CStatusValidData = 0, CStatusNewData = 1;

    private readonly IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.Ordinal);
    private readonly List<string> _unavailable = [];
    private IntPtr _buffer;
    private uint _bufferSize;
    private bool _disposed;

    private const string Utility = "util", Performance = "perf", Frequency = "freq", DiskIdle = "didle", DiskRead = "dread", DiskWrite = "dwrite",
        DiskQueue = "dqueue", NetSent = "nsent", NetRecv = "nrecv", NetBw = "nbw", GpuEngine = "geng", GpuMem = "gmem", Thermal = "therm";

    public WindowsPerformanceSource()
    {
        var status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0)
        {
            _unavailable.Add($"Não foi possível abrir os contadores de desempenho do Windows (PDH 0x{status:X8}).");
            return;
        }

        if (!Add(Utility, @"\Processor Information(*)\% Processor Utility"))
            Add(Utility, @"\Processor Information(*)\% Processor Time", "CPU");
        Add(Performance, @"\Processor Information(*)\% Processor Performance", "Frequência efetiva da CPU");
        Add(Frequency, @"\Processor Information(*)\Processor Frequency", "Frequência base da CPU");
        Add(DiskIdle, @"\PhysicalDisk(*)\% Idle Time", "Atividade de disco");
        Add(DiskRead, @"\PhysicalDisk(*)\Disk Read Bytes/sec", "Leitura de disco");
        Add(DiskWrite, @"\PhysicalDisk(*)\Disk Write Bytes/sec", "Escrita de disco");
        Add(DiskQueue, @"\PhysicalDisk(*)\Current Disk Queue Length", "Fila de disco");
        Add(NetSent, @"\Network Interface(*)\Bytes Sent/sec", "Envio de rede");
        Add(NetRecv, @"\Network Interface(*)\Bytes Received/sec", "Recebimento de rede");
        Add(NetBw, @"\Network Interface(*)\Current Bandwidth", "Velocidade do link");
        Add(GpuEngine, @"\GPU Engine(*)\Utilization Percentage", "Uso da GPU (exige Windows 10 1709+ e driver WDDM 2.x)");
        Add(GpuMem, @"\GPU Adapter Memory(*)\Dedicated Usage", "Memória de vídeo em uso");
        Add(Thermal, @"\Thermal Zone Information(*)\Temperature", "Zonas térmicas ACPI");
        _ = PdhCollectQueryData(_query); // primeira coleta: base para os contadores de taxa
    }

    public IReadOnlyList<string> Unavailable => _unavailable;

    private bool Add(string key, string path, string? label = null)
    {
        var status = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter);
        if (status == 0)
        {
            _counters[key] = counter;
            return true;
        }
        if (label is not null) _unavailable.Add($"{label}: o Windows não oferece o contador {path} neste PC (PDH 0x{status:X8}).");
        return false;
    }

    public RawSample Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_query == IntPtr.Zero) return new RawSample { Memory = ReadMemory() };
        _ = PdhCollectQueryData(_query);
        return new RawSample
        {
            ProcessorUtility = Array(Utility),
            ProcessorPerformance = Array(Performance),
            ProcessorFrequency = Array(Frequency),
            Memory = ReadMemory(),
            DiskIdle = Array(DiskIdle),
            DiskReadBytes = Array(DiskRead),
            DiskWriteBytes = Array(DiskWrite),
            DiskQueue = Array(DiskQueue),
            NetSentBytes = Array(NetSent),
            NetReceivedBytes = Array(NetRecv),
            NetBandwidth = Array(NetBw),
            GpuEngine = Array(GpuEngine),
            GpuDedicated = Array(GpuMem),
            ThermalZones = Array(Thermal),
        };
    }

    private CounterSet Array(string key)
    {
        if (!_counters.TryGetValue(key, out var counter)) return CounterSet.Empty;
        const uint format = PdhFmtDouble | PdhFmtNoCap100;
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, format, ref size, out var count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return CounterSet.Empty;
        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = Math.Max(size, _bufferSize * 2);
            _buffer = Marshal.AllocHGlobal((nint)_bufferSize);
        }
        size = _bufferSize;
        status = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, _buffer);
        if (status != 0) return CounterSet.Empty;

        var values = new Dictionary<string, double>((int)count, StringComparer.OrdinalIgnoreCase);
        var items = (PdhFmtCounterValueItem*)_buffer;
        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            if (item.Value.CStatus is not (CStatusValidData or CStatusNewData) || item.Name == IntPtr.Zero) continue;
            var name = Marshal.PtrToStringUni(item.Name);
            if (name is not null) values[name] = item.Value.DoubleValue;
        }
        return new CounterSet(values);
    }

    private static MemoryReading? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        if (GlobalMemoryStatusEx(ref status) == 0) return null;
        var perf = new PerformanceInformation { Cb = (uint)sizeof(PerformanceInformation) };
        ulong cache = 0, commit = 0, limit = 0;
        if (K32GetPerformanceInfo(ref perf, perf.Cb) != 0)
        {
            var page = (ulong)perf.PageSize;
            cache = (ulong)perf.SystemCache * page;
            commit = (ulong)perf.CommitTotal * page;
            limit = (ulong)perf.CommitLimit * page;
        }
        return new MemoryReading(status.TotalPhys, status.AvailPhys, cache, commit, limit);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero) _ = PdhCloseQuery(_query);
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    // ------------------------------------------------------------------ nativo

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue; // união de 8 bytes alinhada em 8 (igual em x86 e x64)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int K32GetPerformanceInfo(ref PerformanceInformation info, uint size);
}
