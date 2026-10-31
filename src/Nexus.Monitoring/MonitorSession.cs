namespace Nexus.Monitoring;

/// <summary>Fonte dos contadores (PDH no Windows; falsa nos testes). Contadores ausentes vêm vazios, nunca exceção.</summary>
public interface IPerformanceSource : IDisposable
{
    RawSample Read();

    /// <summary>Contadores que o Windows não ofereceu neste PC (ex.: GPU sem driver WDDM 2.x), com o motivo, em português.</summary>
    IReadOnlyList<string> Unavailable { get; }
}

/// <summary>Lista de processos (API de processos do .NET no Windows; falsa nos testes).</summary>
public interface IProcessSource
{
    int LogicalProcessors { get; }
    IReadOnlyList<ProcessReading> Read();
}

/// <summary>nvidia-smi (se o driver NVIDIA estiver instalado). <see cref="Available"/> falso = sem a ferramenta.</summary>
public interface INvidiaSource
{
    bool Available { get; }
    IReadOnlyList<NvidiaGpuReading> Read();
}

public sealed record DiskHealthEntry(PhysicalDiskReading Disk, DiskHealthVerdict Verdict);

public sealed record DiskHealthReport(IReadOnlyList<DiskHealthEntry> Disks, bool Elevated, string? Error)
{
    public static DiskHealthReport From(IReadOnlyList<PhysicalDiskReading> disks, bool elevated, string? error = null) =>
        new([.. disks.Select(d => new DiskHealthEntry(d, DiskHealthClassifier.Classify(d)))], elevated, error);
}

public interface IDiskHealthSource
{
    Task<DiskHealthReport> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Resultado de um tique (1 s): leitura agregada, veredito de gargalo e a última leitura do nvidia-smi.</summary>
public sealed record MonitorTick(MonitorSnapshot Snapshot, BottleneckVerdict Verdict, IReadOnlyList<NvidiaGpuReading> Nvidia);

/// <summary>
/// Junta as fontes num tique por segundo, com custo baixo: contadores PDH a cada tique (uma coleta só para todos), processos
/// a cada <see cref="ProcessEveryTicks"/> tiques e nvidia-smi a cada <see cref="NvidiaEveryTicks"/> em segundo plano (é um
/// processo externo). Chamadas a <see cref="Tick"/> devem ser sequenciais (a página garante).
/// </summary>
public sealed class MonitorSession : IDisposable
{
    public const int TopProcesses = 10;
    public const int BottleneckWindow = 10;

    private readonly IPerformanceSource _performance;
    private readonly IProcessSource? _processes;
    private readonly INvidiaSource? _nvidia;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ProcessCpuTracker? _tracker;
    private IReadOnlyList<ProcessUsage> _lastProcesses = [];
    private Dictionary<int, string> _names = [];
    private volatile IReadOnlyList<NvidiaGpuReading> _lastNvidia = [];
    private Task? _nvidiaTask;
    private long _ticks;

    public MonitorSession(IPerformanceSource performance, IProcessSource? processes = null, INvidiaSource? nvidia = null, Func<DateTimeOffset>? clock = null)
    {
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
        _processes = processes;
        _nvidia = nvidia;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (processes is not null) _tracker = new ProcessCpuTracker(processes.LogicalProcessors);
    }

    public int ProcessEveryTicks { get; init; } = 2;
    public int NvidiaEveryTicks { get; init; } = 5;
    public MonitorHistory History { get; } = new();
    public IReadOnlyList<string> Unavailable => _performance.Unavailable;
    public bool NvidiaAvailable => _nvidia?.Available == true;

    public MonitorTick Tick()
    {
        var raw = _performance.Read();
        var now = _clock();
        if (_processes is not null && _tracker is not null && _ticks % Math.Max(1, ProcessEveryTicks) == 0)
        {
            try
            {
                var list = _processes.Read();
                _lastProcesses = _tracker.Update(list, now);
                var names = new Dictionary<int, string>(list.Count);
                foreach (var p in list) names[p.Pid] = p.Name;
                _names = names;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // lista de processos indisponível neste tique: mantém a anterior
            }
        }

        if (_nvidia?.Available == true && _ticks % Math.Max(1, NvidiaEveryTicks) == 0 && _nvidiaTask is null or { IsCompleted: true })
        {
            _nvidiaTask = Task.Run(() =>
            {
                try { _lastNvidia = _nvidia.Read(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { _lastNvidia = []; }
            });
        }
        _ticks++;

        var snapshot = MonitorAggregator.Aggregate(raw with { At = now }, _lastProcesses, _names);
        snapshot = snapshot with { Processes = Top(snapshot.Processes) };
        History.Add(snapshot);
        var verdict = BottleneckDetector.Evaluate(BottleneckInput.From([.. History.Snapshots.Last(BottleneckWindow)]));
        return new MonitorTick(snapshot, verdict, _lastNvidia);
    }

    /// <summary>Guarda só o que a página mostra (os mais pesados em CPU, memória e GPU), para o histórico não crescer.</summary>
    internal static IReadOnlyList<ProcessUsage> Top(IReadOnlyList<ProcessUsage> all)
    {
        if (all.Count <= TopProcesses * 2) return all;
        var byCpu = all.OrderByDescending(p => p.CpuPercent).Take(TopProcesses);
        var byMem = all.OrderByDescending(p => p.WorkingSetBytes).Take(TopProcesses);
        var byGpu = all.Where(p => p.GpuPercent > 0).OrderByDescending(p => p.GpuPercent).Take(5);
        return [.. byCpu.Concat(byMem).Concat(byGpu).DistinctBy(p => p.Pid)];
    }

    public void Dispose() => _performance.Dispose();
}
