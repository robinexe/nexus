namespace Nexus.Monitoring;

/// <summary>Valores de um contador com curinga: nome da instância → valor formatado (ex.: "0,3" → 42,5).</summary>
public sealed class CounterSet
{
    public static readonly CounterSet Empty = new(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

    public CounterSet(IReadOnlyDictionary<string, double> values) => Values = values;

    public IReadOnlyDictionary<string, double> Values { get; }

    public bool IsEmpty => Values.Count == 0;

    public double? Get(string instance) => Values.TryGetValue(instance, out var v) ? v : null;

    public static CounterSet Of(params (string Instance, double Value)[] values)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (i, v) in values) d[i] = v;
        return new CounterSet(d);
    }
}

/// <summary>Memória lida do Windows (GlobalMemoryStatusEx + GetPerformanceInfo), em bytes.</summary>
public sealed record MemoryReading(ulong TotalBytes, ulong AvailableBytes, ulong CacheBytes, ulong CommitBytes, ulong CommitLimitBytes);

/// <summary>
/// Uma leitura crua de todos os contadores, como a fonte (PDH no Windows, falsa nos testes) entrega. Contadores de taxa
/// (bytes/s, %) só têm valor a partir da segunda coleta; antes disso vêm vazios.
/// </summary>
public sealed record RawSample
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>\Processor Information(*)\% Processor Utility (o mesmo número do Gerenciador de Tarefas; pode passar de 100 com turbo).</summary>
    public CounterSet ProcessorUtility { get; init; } = CounterSet.Empty;

    /// <summary>\Processor Information(*)\% Processor Performance (relação entre a frequência atual e a nominal).</summary>
    public CounterSet ProcessorPerformance { get; init; } = CounterSet.Empty;

    /// <summary>\Processor Information(*)\Processor Frequency (frequência nominal/base em MHz).</summary>
    public CounterSet ProcessorFrequency { get; init; } = CounterSet.Empty;

    public MemoryReading? Memory { get; init; }

    public CounterSet DiskIdle { get; init; } = CounterSet.Empty;
    public CounterSet DiskReadBytes { get; init; } = CounterSet.Empty;
    public CounterSet DiskWriteBytes { get; init; } = CounterSet.Empty;
    public CounterSet DiskQueue { get; init; } = CounterSet.Empty;

    public CounterSet NetSentBytes { get; init; } = CounterSet.Empty;
    public CounterSet NetReceivedBytes { get; init; } = CounterSet.Empty;
    /// <summary>\Network Interface(*)\Current Bandwidth em bits/s.</summary>
    public CounterSet NetBandwidth { get; init; } = CounterSet.Empty;

    /// <summary>\GPU Engine(*)\Utilization Percentage (instância por processo × motor).</summary>
    public CounterSet GpuEngine { get; init; } = CounterSet.Empty;

    /// <summary>\GPU Adapter Memory(*)\Dedicated Usage em bytes.</summary>
    public CounterSet GpuDedicated { get; init; } = CounterSet.Empty;

    /// <summary>\Thermal Zone Information(*)\Temperature em Kelvin (zona térmica ACPI; NÃO é a temperatura do núcleo).</summary>
    public CounterSet ThermalZones { get; init; } = CounterSet.Empty;
}

/// <summary>Um processo lido pela API de processos. <see cref="CpuTime"/> nulo = sem acesso (processo protegido).</summary>
public sealed record ProcessReading(int Pid, string Name, TimeSpan? CpuTime, long WorkingSetBytes, DateTime? StartTime = null);

// ------------------------------------------------------------------ resultado agregado

public sealed record CoreUsage(int Index, string Instance, double Percent, double? Mhz);

public sealed record CpuUsage(double TotalPercent, double? EffectiveMhz, double? BaseMhz, IReadOnlyList<CoreUsage> Cores)
{
    public double MaxCorePercent => Cores.Count == 0 ? TotalPercent : Cores.Max(c => c.Percent);
}

public sealed record MemoryUsage(ulong TotalBytes, ulong UsedBytes, ulong AvailableBytes, ulong CacheBytes, ulong CommitBytes, ulong CommitLimitBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
    public double CommitPercent => CommitLimitBytes == 0 ? 0 : 100.0 * CommitBytes / CommitLimitBytes;
}

public sealed record DiskActivity(string Instance, string Label, double ActivePercent, double ReadBytesPerSec, double WriteBytesPerSec, double Queue);

public sealed record NetworkActivity(string Name, double SentBytesPerSec, double ReceivedBytesPerSec, double? LinkBitsPerSec)
{
    public double TotalBytesPerSec => SentBytesPerSec + ReceivedBytesPerSec;
}

public sealed record GpuEngineUsage(string Type, double Percent)
{
    public string Label => GpuEngineNames.Label(Type);
}

public sealed record GpuProcessUsage(int Pid, string? Name, double Percent);

/// <summary>Uma placa (LUID + phys). <see cref="OverallPercent"/> = o motor mais ocupado (mesma regra do Gerenciador de Tarefas).</summary>
public sealed record GpuAdapterUsage(string Key, double OverallPercent, IReadOnlyList<GpuEngineUsage> Engines, IReadOnlyList<GpuProcessUsage> Processes,
    ulong? DedicatedBytes);

public sealed record ProcessUsage(int Pid, string Name, double CpuPercent, long WorkingSetBytes, double? GpuPercent = null);

public sealed record ThermalZoneReading(string Zone, double Celsius);

public sealed record MonitorSnapshot
{
    public DateTimeOffset At { get; init; }
    public CpuUsage Cpu { get; init; } = new(0, null, null, []);
    public MemoryUsage? Memory { get; init; }
    public IReadOnlyList<DiskActivity> Disks { get; init; } = [];
    public IReadOnlyList<NetworkActivity> Networks { get; init; } = [];
    public IReadOnlyList<GpuAdapterUsage> GpuAdapters { get; init; } = [];
    /// <summary>A placa principal: a com mais memória dedicada em uso (normalmente a dedicada), desempate pelo uso.</summary>
    public GpuAdapterUsage? Gpu { get; init; }
    public IReadOnlyList<ProcessUsage> Processes { get; init; } = [];
    public IReadOnlyList<ThermalZoneReading> ThermalZones { get; init; } = [];

    public double DiskActiveMax => Disks.Count == 0 ? 0 : Disks.Max(d => d.ActivePercent);
    public double DiskQueueMax => Disks.Count == 0 ? 0 : Disks.Max(d => d.Queue);
    public double NetworkTotalBytesPerSec => Networks.Sum(n => n.TotalBytesPerSec);
}

public static class GpuEngineNames
{
    public static string Label(string type) => type switch
    {
        "3D" => "3D",
        "Copy" => "Cópia",
        "VideoDecode" => "Decodificação de vídeo",
        "VideoEncode" => "Codificação de vídeo",
        "VideoProcessing" => "Processamento de vídeo",
        "Compute" or "Compute_0" or "Compute_1" => "Computação",
        "Cuda" => "CUDA",
        _ => type.Replace('_', ' '),
    };
}
