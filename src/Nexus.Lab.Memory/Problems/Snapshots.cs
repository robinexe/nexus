namespace Nexus.Lab.Memory.Problems;

/// <summary>
/// Um processo numa leitura. <see cref="PrivateBytes"/> = memória comprometida privada (o que o processo "deve" à RAM +
/// paginação; é o número que cresce num vazamento). <see cref="WorkingSet"/> = o que está na RAM agora (inclui partes
/// compartilhadas). GDI/USER = objetos de interface (null quando o Windows não deixa ler, ex.: processo de outra conta).
/// </summary>
public sealed record ProcSample(
    int Pid,
    string Name,
    long WorkingSet,
    long PrivateBytes,
    int Handles,
    int Threads,
    int SessionId,
    long? StartTimeUtcTicks = null,
    int? GdiObjects = null,
    int? UserObjects = null,
    string? Path = null);

/// <summary>Memória do sistema numa leitura (bytes; null = não lido).</summary>
public sealed record SystemMemorySample
{
    public long Total { get; init; }
    public long Available { get; init; }
    public long CommitTotal { get; init; }
    public long CommitLimit { get; init; }
    public long CommitPeak { get; init; }
    public long PagedPool { get; init; }
    public long NonPagedPool { get; init; }
    public long SystemCache { get; init; }
    public long? Standby { get; init; }
    public long? Modified { get; init; }
    public long? FreeZero { get; init; }
    /// <summary>Memória da GPU que mora na RAM (placa integrada, ou a dedicada transbordando), somando os adaptadores.</summary>
    public long? GpuShared { get; init; }
    public long? GpuDedicated { get; init; }
    /// <summary>Em uso, como o Gerenciador de Tarefas: total − disponível − modificada.</summary>
    public long InUse => Math.Max(0, Total - Available - (Modified ?? 0));
    public double CommitRatio => CommitLimit > 0 ? (double)CommitTotal / CommitLimit : 0;
    public double FreeRatio => Total > 0 ? (double)Available / Total : 0;
}

/// <summary>Uso do pool do kernel por tag (4 caracteres), como o PoolMon mostra.</summary>
public sealed record PoolTagUsage(string Tag, long PagedBytes, long NonPagedBytes, long PagedAllocs = 0, long PagedFrees = 0, long NonPagedAllocs = 0, long NonPagedFrees = 0)
{
    public long TotalBytes => PagedBytes + NonPagedBytes;
}

/// <summary>Uma leitura completa (o "quadro" do histórico).</summary>
public sealed record MemorySnapshot
{
    public DateTimeOffset At { get; init; }
    public SystemMemorySample System { get; init; } = new();
    public IReadOnlyList<ProcSample> Processes { get; init; } = [];
    /// <summary>Serviços por PID (svchost e serviços próprios). Vazio = não lido.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>> ServicesByPid { get; init; } = new Dictionary<int, IReadOnlyList<ServiceInfo>>();
    /// <summary>Null = o Windows não deixou ler as tags do pool (normalmente pede administrador).</summary>
    public IReadOnlyList<PoolTagUsage>? PoolTags { get; init; }
    /// <summary>GPU: memória compartilhada (na RAM) por PID, quando o contador existe.</summary>
    public IReadOnlyDictionary<int, long> GpuSharedByPid { get; init; } = new Dictionary<int, long>();
    public IReadOnlyDictionary<string, string> Problems { get; init; } = new Dictionary<string, string>();
}

/// <summary>Serviço do Windows (nome curto + nome de exibição).</summary>
public sealed record ServiceInfo(string Name, string DisplayName);
