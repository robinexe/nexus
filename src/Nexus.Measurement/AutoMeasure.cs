using System.Globalization;
using System.Text;

namespace Nexus.Measurement;

/// <summary>Lê processos e a janela em primeiro plano (Windows: <see cref="Windows.WindowsProcessProbe"/>; testes: falso).</summary>
public interface IProcessProbe
{
    ProcessSnapshot Read();
    bool IsAlive(int pid);
}

/// <summary>Uma captura de quadros em andamento (PresentMon). Encerrar não mexe no jogo.</summary>
public interface ILiveCapture : IDisposable
{
    /// <summary>Mensagens do PresentMon (para explicar um erro).</summary>
    string Log { get; }
    /// <summary>A captura terminou sozinha (PresentMon fechou ou falhou).</summary>
    bool Ended { get; }
    void Finish();
}

/// <summary>Inicia capturas. <paramref name="onFrame"/> é chamado em outra thread, um quadro por vez.</summary>
public interface ILiveCaptureStarter
{
    bool Available { get; }
    ILiveCapture Start(string processName, Action<LiveFrame> onFrame);
}

/// <summary>
/// Os quadros de UMA partida: estatística ao vivo + todos os frametimes (4 bytes cada; 2 h a 300 FPS ≈ 9 MB) para salvar
/// a sessão completa no fim. Seguro para quadros chegando de outra thread.
/// </summary>
public sealed class LiveSession
{
    private readonly object _gate = new();
    private readonly LiveFrameStats _stats = new();
    private readonly List<float> _frametimes = [];
    private readonly List<float> _cpu = [];
    private readonly List<float> _gpu = [];
    private readonly Dictionary<string, int> _modes = new(StringComparer.Ordinal);
    // Colunas extras (Por que travou?): Half = 2 bytes por valor (precisão de ~0,1% basta para ms).
    private readonly List<Half> _cpuWait = [];
    private readonly List<Half> _gpuWait = [];
    private readonly List<Half> _display = [];
    private readonly List<Half> _animation = [];
    private readonly List<bool> _generated = [];
    private readonly List<(DateTimeOffset At, IReadOnlyList<ProcessCpuShare> Others, double? GameRead)> _activity = [];
    private readonly Func<DateTimeOffset> _clock;
    private bool _hasFrameType;
    private DateTimeOffset? _firstFrameAt;
    private int _pid;

    public LiveSession(DetectedGame game, DateTimeOffset startedAt, bool automatic, Func<DateTimeOffset>? clock = null)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        StartedAt = startedAt;
        Automatic = automatic;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>PID do jogo (do primeiro quadro aceito; 0 antes disso). O amostrador de atividade lê as leituras deste processo.</summary>
    public int ProcessId { get { lock (_gate) return _pid != 0 ? _pid : Game.Pid; } }

    public DetectedGame Game { get; }
    public DateTimeOffset StartedAt { get; }
    public bool Automatic { get; }
    public long Frames { get { lock (_gate) return _stats.Frames; } }
    public double Seconds { get { lock (_gate) return _stats.ElapsedMs / 1000; } }

    /// <summary>Aceita um quadro se for do jogo desta sessão (o PresentMon filtra pelo nome, mas o nome pode repetir em outro processo).</summary>
    public void Add(LiveFrame f)
    {
        if (!f.Application.Equals(Game.ExeName, StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            if (_pid == 0) _pid = f.ProcessId;
            _firstFrameAt ??= _clock();
            _stats.Add(f.FrametimeMs);
            _cpuWait.Add(H(f.CpuWaitMs));
            _gpuWait.Add(H(f.GpuWaitMs));
            _display.Add(H(f.DisplayLatencyMs));
            _animation.Add(H(f.AnimationErrorMs));
            _generated.Add(f.IsGenerated);
            if (f.FrameType is not null) _hasFrameType = true;
            _frametimes.Add((float)f.FrametimeMs);
            _cpu.Add(f.CpuBusyMs is { } c ? (float)c : float.NaN);
            _gpu.Add(f.GpuBusyMs is { } g ? (float)g : float.NaN);
            if (f.PresentMode is { } m) _modes[m] = _modes.TryGetValue(m, out var n) ? n + 1 : 1;
        }
    }

    private static Half H(double? v) => v is { } d && double.IsFinite(d) ? (Half)d : Half.NaN;
    private static double? D(Half h) => Half.IsNaN(h) ? null : (double)h;
    private static double? D(float f) => float.IsNaN(f) ? null : f;

    /// <summary>Amostra de atividade (CPU de outros processos, leitura do jogo) feita durante a captura. Antes do primeiro quadro: ignorada.</summary>
    public void AddActivity(DateTimeOffset at, IReadOnlyList<ProcessCpuShare> others, double? gameReadBytesPerSec)
    {
        ArgumentNullException.ThrowIfNull(others);
        lock (_gate)
        {
            if (_firstFrameAt is null || _activity.Count >= 20_000) return; // 20 mil amostras = 11 h a cada 2 s
            _activity.Add((at, others, gameReadBytesPerSec));
        }
    }

    /// <summary>Amostras de atividade com o tempo em segundos desde o primeiro quadro (o mesmo relógio dos quadros).</summary>
    public IReadOnlyList<ActivitySample> Activity()
    {
        lock (_gate)
        {
            if (_firstFrameAt is not { } t0) return [];
            return _activity.Select(a => new ActivitySample((a.At - t0).TotalSeconds, a.Others, a.GameRead)).ToList();
        }
    }

    /// <summary>Os quadros da partida para "Por que travou?".</summary>
    public IReadOnlyList<StutterFrame> StutterFrames()
    {
        lock (_gate)
        {
            var list = new List<StutterFrame>(_frametimes.Count);
            for (var i = 0; i < _frametimes.Count; i++)
                list.Add(new StutterFrame(_frametimes[i], D(_cpu[i]), D(_cpuWait[i]), D(_gpu[i]), D(_gpuWait[i]), D(_display[i]), D(_animation[i]), _generated[i]));
            return list;
        }
    }

    public LiveReading Read() { lock (_gate) return _stats.Read(); }
    public IReadOnlyList<TracePoint> Trace() { lock (_gate) return _stats.Trace(); }

    private static string Fmt(Half h) => Half.IsNaN(h) ? "NA" : ((double)h).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Grava os quadros num CSV no formato do PresentMon 2.x (lido por <see cref="SessionAnalyzer"/>), tempo em ms acumulado.</summary>
    public void WriteCsv(string path)
    {
        lock (_gate)
        {
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            w.WriteLine("Application,ProcessID,CPUStartTime,FrameTime,CPUBusy,GPUBusy,PresentMode,CPUWait,GPUWait,DisplayLatency,AnimationError,FrameType");
            var mode = _modes.Count > 0 ? _modes.MaxBy(k => k.Value).Key.Replace(",", " ", StringComparison.Ordinal) : "";
            var app = Game.ExeName.Replace(",", " ", StringComparison.Ordinal);
            double t = 0;
            for (var i = 0; i < _frametimes.Count; i++)
            {
                t += _frametimes[i];
                w.Write(app); w.Write(',');
                w.Write(_pid.ToString(CultureInfo.InvariantCulture)); w.Write(',');
                w.Write(t.ToString("0.###", CultureInfo.InvariantCulture)); w.Write(',');
                w.Write(_frametimes[i].ToString("0.####", CultureInfo.InvariantCulture)); w.Write(',');
                w.Write(float.IsNaN(_cpu[i]) ? "NA" : _cpu[i].ToString("0.###", CultureInfo.InvariantCulture)); w.Write(',');
                w.Write(float.IsNaN(_gpu[i]) ? "NA" : _gpu[i].ToString("0.###", CultureInfo.InvariantCulture)); w.Write(',');
                w.Write(mode); w.Write(',');
                w.Write(Fmt(_cpuWait[i])); w.Write(',');
                w.Write(Fmt(_gpuWait[i])); w.Write(',');
                w.Write(Fmt(_display[i])); w.Write(',');
                w.Write(Fmt(_animation[i])); w.Write(',');
                w.WriteLine(!_hasFrameType ? "NA" : _generated[i] ? "Generated" : "Application");
            }
        }
    }
}

/// <summary>O que aconteceu numa leitura do motor.</summary>
public sealed record AutoMeasureEvent(GameEventKind Kind, DetectedGame? Game, LiveSession? Finished, bool CaptureStarted, string? Note);

/// <summary>
/// Medição automática: a cada leitura (o chamador usa um temporizador de 2 s), detecta o jogo, começa a captura quando ele
/// abre (se "medir automaticamente" estiver ligado e houver PresentMon) e entrega a partida quando ele fecha. Uma captura
/// também pode ser iniciada à mão (<see cref="StartManual"/>). Não decide nada sobre salvar: quem chama decide (duração mínima).
/// </summary>
/// <param name="activity">Opcional: inicia a amostragem de atividade (CPU de outros processos, leitura do jogo) para a partida;
/// o objeto devolvido é descartado quando a captura para. Falha ao iniciar não impede a medição.</param>
public sealed class AutoMeasureEngine(IProcessProbe probe, ILiveCaptureStarter starter, GameSessionTracker? tracker = null,
    Func<LiveSession, IDisposable?>? activity = null)
{
    private IDisposable? _activity;
    private readonly IProcessProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly ILiveCaptureStarter _starter = starter ?? throw new ArgumentNullException(nameof(starter));
    private readonly GameSessionTracker _tracker = tracker ?? new GameSessionTracker();
    private ILiveCapture? _capture;

    public DetectedGame? CurrentGame => _tracker.Current;
    public LiveSession? Session { get; private set; }
    public ILiveCapture? Capture => _capture;
    public ProcessSnapshot? LastSnapshot { get; private set; }

    public AutoMeasureEvent Poll(DetectionRules rules, bool autoMeasure, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var snap = _probe.Read();
        LastSnapshot = snap;
        var seen = GameMatcher.Find(snap, rules);
        var alive = _tracker.Current is { } cur && _probe.IsAlive(cur.Pid);
        var ev = _tracker.Update(seen, alive, now);
        switch (ev.Kind)
        {
            case GameEventKind.Started:
                var started = autoMeasure && _starter.Available && StartCapture(ev.Game!, now, automatic: true);
                return new(GameEventKind.Started, ev.Game, null, started,
                    autoMeasure && !_starter.Available ? "Medição automática ligada, mas o PresentMon não foi encontrado." : null);
            case GameEventKind.Stopped:
                return new(GameEventKind.Stopped, ev.Game, StopCapture(), false, null);
            default:
                return new(GameEventKind.None, ev.Game, null, false, null);
        }
    }

    /// <summary>Começa a medir agora o jogo detectado (ou o que o usuário indicou).</summary>
    public bool StartManual(DetectedGame game, DateTimeOffset now) => Session is null && StartCapture(game, now, automatic: false);

    /// <summary>Para a captura e devolve a partida (ou null se não havia).</summary>
    public LiveSession? StopCapture()
    {
        var s = Session;
        try
        {
            _activity?.Dispose();
            _capture?.Finish();
        }
        finally
        {
            _activity = null;
            _capture?.Dispose();
            _capture = null;
            Session = null;
        }
        return s;
    }

    private bool StartCapture(DetectedGame game, DateTimeOffset now, bool automatic)
    {
        if (Session is not null) return false;
        var session = new LiveSession(game, now, automatic);
        _capture = _starter.Start(game.ExeName, session.Add);
        Session = session;
        try { _activity = activity?.Invoke(session); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _activity = null; } // sem amostras: a análise diz "sem dados de atividade"
        return true;
    }
}
