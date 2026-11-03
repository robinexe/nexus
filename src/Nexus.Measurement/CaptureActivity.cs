namespace Nexus.Measurement;

/// <summary>Uso de CPU de um processo desde a leitura anterior (0–100% da máquina inteira).</summary>
public readonly record struct ProcessCpuReading(int Pid, string Name, double CpuPercent);

/// <summary>
/// De onde vêm as amostras de atividade durante a captura. No Windows: a lista de processos do Monitor (TotalProcessorTime, o
/// mesmo cálculo do Gerenciador de Tarefas) e GetProcessIoCounters do jogo (acesso limitado, sem tocar no jogo). Testes: falso.
/// </summary>
public interface IActivitySource
{
    /// <summary>CPU de cada processo desde a chamada anterior (a primeira chamada pode vir toda com 0%).</summary>
    IReadOnlyList<ProcessCpuReading> ProcessCpu(DateTimeOffset now);

    /// <summary>Total de bytes lidos pelo processo desde que abriu (ReadTransferCount), ou null sem acesso.</summary>
    ulong? ReadTransferBytes(int pid);
}

/// <summary>
/// Amostra, enquanto a partida é medida, quem mais usou CPU além do jogo e quanto o jogo leu (para "Por que travou?"). Custo baixo:
/// uma leitura da lista de processos a cada <see cref="Interval"/> (padrão 2 s), numa thread do pool, nunca na do jogo.
/// </summary>
public sealed class CaptureActivitySampler : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);
    /// <summary>Só os N processos que mais usaram CPU (e com pelo menos <see cref="MinPercent"/>) entram na amostra.</summary>
    public const int Top = 3;
    public const double MinPercent = 3;

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "Idle", "System Idle Process", "_Total" };

    private readonly IActivitySource _source;
    private readonly LiveSession _session;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private Timer? _timer;
    private ulong? _lastRead;
    private DateTimeOffset? _lastAt;
    private bool _disposed;

    public CaptureActivitySampler(IActivitySource source, LiveSession session, TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Interval = interval ?? DefaultInterval;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public TimeSpan Interval { get; }
    public int Samples { get; private set; }

    /// <summary>Começa a amostrar (primeira leitura agora, para ter a base do cálculo de CPU).</summary>
    public CaptureActivitySampler Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return this;
            _timer = new Timer(_ => SafeSample(), null, TimeSpan.Zero, Interval);
        }
        return this;
    }

    private void SafeSample()
    {
        try { Sample(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { } // uma amostra perdida não derruba a medição
    }

    /// <summary>Uma amostra: CPU dos outros processos (os <see cref="Top"/> maiores) e taxa de leitura do jogo desde a amostra anterior.</summary>
    public void Sample()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock();
            var gamePid = _session.ProcessId;
            var gameExe = Path.GetFileNameWithoutExtension(_session.Game.ExeName);
            var others = _source.ProcessCpu(now)
                .Where(p => p.Pid != 0 && p.Pid != gamePid && !Ignored.Contains(p.Name) && !p.Name.Equals(gameExe, StringComparison.OrdinalIgnoreCase))
                .Where(p => p.CpuPercent >= MinPercent)
                .OrderByDescending(p => p.CpuPercent).Take(Top)
                .Select(p => new ProcessCpuShare(p.Name, Math.Round(p.CpuPercent, 1)))
                .ToList();
            double? readRate = null;
            var read = gamePid > 0 ? _source.ReadTransferBytes(gamePid) : null;
            if (read is { } r && _lastRead is { } lr && _lastAt is { } la && r >= lr && (now - la).TotalSeconds > 0.2)
                readRate = (r - lr) / (now - la).TotalSeconds;
            _lastRead = read;
            _lastAt = now;
            if (Samples++ == 0) return; // a primeira leitura só serve de base (CPU e leitura dependem da anterior)
            _session.AddActivity(now, others, readRate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
