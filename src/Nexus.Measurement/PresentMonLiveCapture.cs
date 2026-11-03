using System.Diagnostics;
using System.Text;

namespace Nexus.Measurement;

/// <summary>Um PresentMon em execução (real: <see cref="PresentMonProcessRunner"/>; testes: falso).</summary>
public interface IPresentMonRun : IDisposable
{
    /// <summary>Terminou e tudo o que ele gravou já foi entregue.</summary>
    bool HasExited { get; }
    int? ExitCode { get; }
    /// <summary>Já saiu alguma linha (ou o arquivo do PresentMon elevado já existe): passou do aviso do Windows.</summary>
    bool OutputStarted { get; }
    /// <summary>Para de ler e encerra o processo quando ele é nosso (o elevado termina sozinho quando o jogo fecha ou outra sessão o substitui).</summary>
    void Terminate();
}

/// <summary>Inicia o PresentMon com os argumentos dados e entrega cada linha da saída (padrão ou do arquivo) e de erro.</summary>
public interface IPresentMonRunner
{
    IPresentMonRun Start(string exe, IReadOnlyList<string> arguments, string? outputCsv, Action<string> onLine, Action<string> onError);
}

/// <summary>
/// Inicia capturas robustas (<see cref="PresentMonLiveCapture"/>). Sem elevação: lê a saída padrão (<c>--output_stdout</c>). Com
/// elevação (<c>--restart_as_admin</c>, o Windows pergunta e o usuário aceita): o PresentMon elevado grava num arquivo temporário
/// e o NEXUS o lê enquanto cresce.
/// </summary>
/// <param name="verify">Conferência do executável (<see cref="PresentMonTrust.Verify"/>) feita a cada início, imediatamente antes
/// de executar: um PresentMon recusado não roda (a captura volta encerrada, com o motivo em <see cref="ILiveCapture"/>.Log).</param>
/// <param name="runner">Quem executa o PresentMon (null = processo real). Com um executor falso, o arquivo não precisa existir.</param>
/// <param name="diag">Recebe as linhas de diagnóstico (linha de comando, erros, código de saída, mudanças de estado) para o
/// desktop-diag.log do suporte.</param>
public sealed class PresentMonLiveStarter(Func<string?> presentMonPath, Func<bool> requestElevation,
    IPresentMonRunner? runner = null, Func<string, PresentMonVerdict>? verify = null, Action<string>? diag = null,
    Func<DateTimeOffset>? clock = null) : ILiveCaptureStarter
{
    public bool Available => presentMonPath() is { } p && (runner is not null || (File.Exists(p) && (verify is null || verify(p).Trusted)));

    /// <summary>Motivo da última recusa (PresentMon que não passou na conferência), ou null.</summary>
    public string? LastRefusal { get; private set; }

    public ILiveCapture Start(string processName, Action<LiveFrame> onFrame) =>
        Start(new LiveTarget(processName, 0, GameAliases.CaptureNames(processName)), onFrame);

    public ILiveCapture Start(LiveTarget target, Action<LiveFrame> onFrame)
    {
        ArgumentNullException.ThrowIfNull(target);
        var exe = presentMonPath() ?? throw new InvalidOperationException("PresentMon não encontrado.");
        var verdict = runner is null ? verify?.Invoke(exe) : null;
        // FrameType só para uma versão conhecida (hash conferido): uma 2.x antiga recusaria a opção. Se mesmo assim for recusada,
        // a captura tenta de novo sem ela (PresentMonLiveCapture).
        var known = verdict is { Trusted: true, Sha256: { } h } && PresentMonTrust.KnownSha256.Contains(h);
        var options = new LiveCaptureOptions(exe, PresentMonCli.GenerationOf(exe), requestElevation(), known);
        var capture = new PresentMonLiveCapture(options, target, onFrame, runner ?? new PresentMonProcessRunner(), clock, diag);
        LastRefusal = verdict is { Trusted: false } refused ? refused.Reason : null;
        if (LastRefusal is { } reason) capture.Refuse(reason);
        else capture.Begin();
        return capture;
    }
}

/// <summary>Como rodar o PresentMon nesta captura.</summary>
/// <param name="Elevate">Pedir elevação (<c>--restart_as_admin</c>; saída por arquivo temporário).</param>
/// <param name="TrackFrameType">Pedir <c>--track_frame_type</c> (desligado sozinho se o PresentMon recusar).</param>
public sealed record LiveCaptureOptions(string PresentMonExe, PresentMonGeneration Generation, bool Elevate, bool TrackFrameType, int MaxMinutes = 240)
{
    /// <summary>Sem quadros do processo filtrado por este tempo: recomeça sem filtro de processo e escolhe pelo que está desenhando.</summary>
    public TimeSpan FallbackAfter { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>Sem quadros por este tempo: a interface explica o que fazer.</summary>
    public TimeSpan TimeoutAfter { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Processo travado sem apresentar por este tempo: "não está desenhando".</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>PID do próprio NEXUS (o mostrador também apresenta quadros: nunca é escolhido).</summary>
    public int OwnPid { get; init; } = Environment.ProcessId;
    /// <summary>Pasta do arquivo temporário do PresentMon elevado.</summary>
    public string TempFolder { get; init; } = Path.GetTempPath();
}

/// <summary>
/// Captura ao vivo que não fica "aguardando quadros" calada. Etapas:
/// <list type="number">
/// <item>PresentMon filtrado pelos nomes do jogo (o detectado + os processos que desenham por ele, <see cref="GameAliases"/>).
/// Trava no primeiro processo desses nomes que apresentar quadros.</item>
/// <item>Sem quadros em <see cref="LiveCaptureOptions.FallbackAfter"/> (ou o processo filtrado fechou sem desenhar: era o
/// lançador): recomeça SEM filtro de processo e trava no processo do jogo, num nome esperado, no dono da janela em primeiro
/// plano que esteja desenhando, ou no que mais desenha (<see cref="LiveTargetPicker"/>). Com o PresentMon elevado (que o NEXUS não
/// consegue encerrar), o recomeço é filtrado pelo processo em primeiro plano, para o PresentMon ainda terminar com o jogo.</item>
/// <item>PresentMon recusou <c>--track_frame_type</c> (ou a linha de comando): tenta de novo sem a opção. Sessão ETW com o mesmo nome
/// presa: tenta com outro nome.</item>
/// <item>Sem permissão ("access denied", aviso do Windows recusado): estado <see cref="LiveCaptureState.NoPermission"/>, para a
/// interface oferecer "Liberar medição" ou "Medir como administrador".</item>
/// </list>
/// Tudo o que o PresentMon diz (erros, código de saída) vai para <see cref="Log"/> e para o diagnóstico.
/// </summary>
public sealed class PresentMonLiveCapture : ILiveCapture
{
    /// <summary>Executáveis ignorados na captura sem filtro: compositor, Explorer, o próprio NEXUS (mostrador), navegadores e lançadores.</summary>
    public static readonly IReadOnlyList<string> AllModeExclude =
    [
        "dwm.exe", "explorer.exe", "Nexus.Desktop.exe", "msedgewebview2.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe",
        "brave.exe", "Discord.exe", "steamwebhelper.exe", "EpicWebHelper.exe", "EpicGamesLauncher.exe", "Spotify.exe",
    ];

    private const int MaxLogChars = 16_000;
    private readonly object _gate = new();
    private readonly LiveCaptureOptions _options;
    private readonly LiveTarget _target;
    private readonly Action<LiveFrame> _onFrame;
    private readonly IPresentMonRunner _runner;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _diag;
    private readonly StringBuilder _log = new();
    private readonly PresenterWindow _window = new();
    private readonly DateTimeOffset _startedAt;
    private IPresentMonRun? _run;
    private StringBuilder _runLog = new();
    private PresentMonLineParser _parser = new();
    private int _runId;
    private DateTimeOffset _runStartedAt;
    private bool _allMode, _trackFrameType, _fallbackDone, _frameTypeRetried, _sessionRetried, _framesThisRun;
    private IReadOnlyList<string> _extraNames = [];
    private string _sessionName = PresentMonCli.LiveSessionName;
    private int _lockPid;
    private string? _lockName;
    private DateTimeOffset? _lastAccepted;
    private long _accepted;
    private int? _foregroundPid;
    private IReadOnlyList<RunningProcess> _processes = [];
    private LiveCaptureState _state = LiveCaptureState.Searching;
    private PresentMonFailure _failure;
    private string? _detail;
    private volatile bool _ended;

    internal PresentMonLiveCapture(LiveCaptureOptions options, LiveTarget target, Action<LiveFrame> onFrame, IPresentMonRunner runner,
        Func<DateTimeOffset>? clock = null, Action<string>? diag = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _onFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? (() => DateTimeOffset.Now);
        _diag = diag;
        _trackFrameType = options.TrackFrameType;
        _startedAt = _clock();
        // Jogo escolhido pelo usuário entre os que desenham: já sabemos o PID, nada de procurar outro.
        if (target.Locked && target.Pid > 0) { _lockPid = target.Pid; _lockName = target.ExeName; }
    }

    public string Log { get { lock (_log) return _log.ToString(); } }
    public bool Ended => _ended;
    public int SkippedLines { get { lock (_gate) return _parser.Skipped; } }
    /// <summary>A captura está sem filtro de processo (o NEXUS escolhe pelo que está desenhando).</summary>
    public bool AllProcessesMode { get { lock (_gate) return _allMode; } }
    /// <summary>A captura atual pede <c>--track_frame_type</c>.</summary>
    public bool TracksFrameType { get { lock (_gate) return _trackFrameType; } }

    public LiveCaptureStatus Status
    {
        get
        {
            lock (_gate)
            {
                var now = _clock();
                var waitingSince = _lastAccepted ?? _startedAt;
                var timedOut = _state is LiveCaptureState.Searching or LiveCaptureState.WaitingPermission or LiveCaptureState.NotDrawing &&
                               now - waitingSince >= _options.TimeoutAfter;
                return new LiveCaptureStatus(_state, _lockName, _lockPid, timedOut, _failure, _allMode || _fallbackDone, _detail);
            }
        }
    }

    public IReadOnlyList<PresenterInfo> Presenters => _window.Snapshot(_clock(), _foregroundPid);

    /// <summary>Não executa: a captura já nasce encerrada, com o motivo no log.</summary>
    internal void Refuse(string reason)
    {
        AppendLog(reason);
        lock (_gate) { _state = LiveCaptureState.Failed; _failure = PresentMonFailure.NotStarted; _detail = reason; }
        _ended = true;
    }

    internal void Begin()
    {
        // Processo escolhido pelo usuário sem nome utilizável ("<unknown>"): sem filtro de nome, travado no PID.
        lock (_gate) _allMode = _target.Locked && !PresentMonCli.IsValidExeName(_target.ExeName) && _options.Generation == PresentMonGeneration.V2;
        StartRun();
    }

    // ------------------------------------------------------------------ execução

    private void StartRun()
    {
        IPresentMonRun? old;
        int id;
        LiveCaptureRequest request;
        lock (_gate)
        {
            old = _run;
            _run = null;
            id = ++_runId;
            _parser = new PresentMonLineParser();
            _runLog = new StringBuilder();
            _framesThisRun = false;
            _runStartedAt = _clock();
            _window.Clear();
            var file = _options.Elevate ? Path.Combine(_options.TempFolder, $"nexus-ao-vivo-{Guid.NewGuid():N}.csv") : null;
            var primary = PresentMonCli.IsValidExeName(_target.ExeName) ? _target.ExeName : _target.Names.FirstOrDefault(PresentMonCli.IsValidExeName) ?? "game.exe";
            request = new LiveCaptureRequest(_options.PresentMonExe, primary, _options.Elevate, file, _options.MaxMinutes, _trackFrameType)
            {
                ExtraProcessNames = _target.Names.Concat(_extraNames).Where(PresentMonCli.IsValidExeName)
                    .Where(n => !n.Equals(primary, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AllProcesses = _allMode,
                Exclude = _allMode ? AllModeExclude.Where(n => !_target.Names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList() : [],
                SessionName = _sessionName,
            };
            if (!_ended && _state is not LiveCaptureState.Measuring) _state = _options.Elevate ? LiveCaptureState.WaitingPermission : LiveCaptureState.Searching;
        }
        StopRun(old);
        if (_ended) return;
        IReadOnlyList<string> args;
        try { args = PresentMonCli.LiveArguments(request, _options.Generation); }
        catch (ArgumentException ex) { Fail(PresentMonFailure.BadArguments, ex.Message); return; }
        Diag($"PresentMon iniciando ({(request.AllProcesses ? "sem filtro de processo" : "filtrado")}{(request.RequestElevation ? ", pedindo elevação" : "")}): {string.Join(' ', args)}");
        try
        {
            var run = _runner.Start(_options.PresentMonExe, args, request.OutputCsv, l => Line(id, l), e => Error(id, e));
            lock (_gate)
            {
                if (id == _runId && !_ended) { _run = run; return; }
            }
            StopRun(run); // terminou/foi parada enquanto iniciava
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppendLog($"Não foi possível iniciar o PresentMon: {ex.Message}");
            Fail(PresentMonFailure.NotStarted, ex.Message);
        }
    }

    private static void StopRun(IPresentMonRun? run)
    {
        if (run is null) return;
        try { run.Terminate(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
        run.Dispose();
    }

    private void Fail(PresentMonFailure failure, string? detail)
    {
        lock (_gate)
        {
            _failure = failure;
            _detail = detail;
            _state = PresentMonDiagnosis.IsPermission(failure) ? LiveCaptureState.NoPermission : LiveCaptureState.Failed;
        }
        _ended = true;
        Diag($"captura encerrada: {failure}{(detail is null ? "" : " — " + detail)}");
    }

    // ------------------------------------------------------------------ linhas

    private void Line(int runId, string line)
    {
        LiveFrame frame;
        lock (_gate)
        {
            if (runId != _runId || _ended) return;
            if (!_parser.TryParse(line, out frame))
            {
                if (!_parser.HasHeader && line.Length > 0) { AppendLog(line); _runLog.AppendLine(line); }
                return;
            }
            var now = _clock();
            _framesThisRun = true;
            _window.Add(frame.ProcessId, frame.Application, now);
            if (_lockPid == 0 && QuickLock(frame)) Lock(frame.ProcessId, frame.Application, "primeiro quadro de um nome esperado");
            if (_lockPid == 0 || frame.ProcessId != _lockPid) return;
            _accepted++;
            _lastAccepted = now;
            _state = LiveCaptureState.Measuring;
        }
        try { _onFrame(frame); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppendLog($"Quadro ignorado: {ex.Message}"); }
    }

    /// <summary>Trava já no primeiro quadro quando não há dúvida: o PID do jogo, ou um nome esperado (filtrado: todos são).</summary>
    private bool QuickLock(LiveFrame f) =>
        f.ProcessId != _options.OwnPid &&
        ((_target.Pid > 0 && f.ProcessId == _target.Pid) ||
         _target.Names.Contains(f.Application, StringComparer.OrdinalIgnoreCase) ||
         _extraNames.Contains(f.Application, StringComparer.OrdinalIgnoreCase));

    private void Error(int runId, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        AppendLog(line);
        lock (_gate)
        {
            if (runId != _runId) return;
            if (_runLog.Length < MaxLogChars) _runLog.AppendLine(line);
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) _detail = line.Trim();
        }
        Diag("PresentMon: " + line.Trim());
    }

    private void Lock(int pid, string name, string why)
    {
        _lockPid = pid;
        _lockName = NameOf(pid, name);
        Diag($"medindo {_lockName} (PID {pid}): {why}");
    }

    private string NameOf(int pid, string name)
    {
        if (!string.IsNullOrEmpty(name) && !name.StartsWith('<')) return name;
        var p = _processes.FirstOrDefault(x => x.Pid == pid);
        if (p is null) return name;
        var n = p.Path is { Length: > 0 } path ? GameLibrary.WinFileName(path) : p.Name;
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe";
    }

    // ------------------------------------------------------------------ prazos e decisões (a cada leitura do motor)

    public void Tick(DateTimeOffset now, ProcessSnapshot? snapshot)
    {
        if (_ended) return;
        Action? act = null;
        lock (_gate)
        {
            if (snapshot is not null)
            {
                _processes = snapshot.Processes;
                _foregroundPid = snapshot.Foreground?.Pid;
            }
            var run = _run;
            if (run is null) return;
            if (run.HasExited) act = OnExited(run);
            else act = WhileRunning(run, now, snapshot);
        }
        act?.Invoke();
    }

    /// <summary>O PresentMon terminou: decide se tenta de novo (sem FrameType, outra sessão, sem filtro) ou encerra explicando.</summary>
    private Action? OnExited(IPresentMonRun run)
    {
        var log = _runLog.ToString();
        var failure = PresentMonDiagnosis.Classify(run.ExitCode, log, _framesThisRun);
        Diag($"PresentMon terminou: código {run.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}, {failure}, {_accepted} quadros aceitos");
        if (_trackFrameType && !_frameTypeRetried && !_framesThisRun &&
            (failure == PresentMonFailure.BadArguments || PresentMonDiagnosis.RejectedOption(log, "track_frame_type")))
        {
            _frameTypeRetried = true;
            _trackFrameType = false;
            Diag("o PresentMon recusou a linha de comando com --track_frame_type: tentando de novo sem a opção");
            return StartRun;
        }
        if (failure == PresentMonFailure.SessionConflict && !_sessionRetried)
        {
            _sessionRetried = true;
            _sessionName = PresentMonCli.LiveSessionName + "_" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StartRun;
        }
        if (PresentMonDiagnosis.IsPermission(failure) || failure is PresentMonFailure.BadArguments or PresentMonFailure.Other or PresentMonFailure.NotStarted)
        {
            var detail = _detail ?? LastLine(log);
            return () => Fail(failure, detail);
        }
        // Código 0: com quadros = o jogo fechou (--terminate_on_proc_exit); sem quadros = o processo filtrado fechou sem desenhar
        // (era o lançador/inicializador): procura quem desenha.
        if (_accepted == 0 && !_fallbackDone && !_target.Locked) return Fallback("o processo filtrado fechou sem apresentar quadros");
        return () =>
        {
            lock (_gate)
            {
                _state = _accepted > 0 ? LiveCaptureState.Finished : LiveCaptureState.Failed;
                if (_accepted == 0) _failure = PresentMonFailure.TargetExited;
            }
            _ended = true;
        };
    }

    private Action? WhileRunning(IPresentMonRun run, DateTimeOffset now, ProcessSnapshot? snapshot)
    {
        if (_options.Elevate && !run.OutputStarted && _accepted == 0)
        {
            _state = LiveCaptureState.WaitingPermission;
            return null;
        }
        if (_lockPid == 0)
        {
            if (Pick(now, "escolhido entre os que estão desenhando")) return null;
            if (!_allMode && !_fallbackDone && !_target.Locked && now - _runStartedAt >= _options.FallbackAfter)
                return Fallback($"{_options.FallbackAfter.TotalSeconds:0} s sem quadros dos processos {string.Join(", ", _target.Names)}");
            _state = LiveCaptureState.Searching;
            return null;
        }
        var last = _lastAccepted ?? _runStartedAt;
        if (now - last < _options.StaleAfter) return null;
        // Travado num processo que parou de apresentar: fechou (procura outro) ou está minimizado/em segundo plano.
        var alive = snapshot is null || snapshot.Processes.Any(p => p.Pid == _lockPid);
        if (!_target.Locked && (!alive || PickOther(now)))
        {
            if (!alive)
            {
                Diag($"{_lockName} (PID {_lockPid}) fechou: procurando quem desenha");
                _lockPid = 0;
                _lockName = null;
                _state = LiveCaptureState.Searching;
            }
            return null;
        }
        _state = LiveCaptureState.NotDrawing;
        return null;
    }

    /// <summary>Escolhe entre os que estão desenhando (só quando há candidatos fora do filtro ou sem filtro).</summary>
    private bool Pick(DateTimeOffset now, string why)
    {
        var chosen = LiveTargetPicker.Pick(_window.Snapshot(now, _foregroundPid), _target.Names.Concat(_extraNames).ToList(), _target.Pid,
            _foregroundPid, _target.Excluded, ownPid: _options.OwnPid);
        if (chosen is null) return false;
        Lock(chosen.Pid, chosen.Name, why);
        return true;
    }

    /// <summary>O processo travado parou, mas outro esperado está desenhando (o jogo reabriu o processo, tela de abertura → jogo).</summary>
    private bool PickOther(DateTimeOffset now)
    {
        var chosen = LiveTargetPicker.Pick(_window.Snapshot(now, _foregroundPid), _target.Names.Concat(_extraNames).ToList(), null,
            _foregroundPid, _target.Excluded, ownPid: _options.OwnPid);
        if (chosen is null || chosen.Pid == _lockPid) return false;
        Lock(chosen.Pid, chosen.Name, "o processo anterior parou de desenhar");
        return true;
    }

    /// <summary>
    /// Recomeça procurando quem desenha. Sem elevação (PresentMon nosso, que dá para encerrar): sem filtro de processo. Com o
    /// PresentMon elevado: filtrado pelo processo em primeiro plano e pelos que desenham pelo jogo que estiverem abertos (o elevado
    /// precisa terminar sozinho com o jogo; o Windows pede permissão de novo). Também sem <c>--track_frame_type</c>, por segurança.
    /// </summary>
    private Action? Fallback(string why)
    {
        _fallbackDone = true;
        _trackFrameType = false;
        if (!_options.Elevate && _options.Generation == PresentMonGeneration.V2)
        {
            _allMode = true;
            Diag($"recomeçando sem filtro de processo: {why}");
            return StartRun;
        }
        var extra = new List<string>();
        if (_foregroundPid is { } fg && fg != _options.OwnPid && _processes.Any(p => p.Pid == fg))
        {
            var name = NameOf(fg, "");
            if (PresentMonCli.IsValidExeName(name) && !LiveTargetPicker.NeverTarget.Contains(name) && !GameLibrary.IsHelperExe(name) &&
                !(_target.Excluded?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false))
                extra.Add(name);
        }
        if (GameAliases.RunningRenderProcess(_target.ExeName, _processes) is { } render) extra.Add(NameOf(render.Pid, ""));
        extra = extra.Where(PresentMonCli.IsValidExeName).Where(n => !_target.Names.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (extra.Count == 0)
        {
            Diag($"sem quadros ({why}) e nenhum outro processo para tentar com o PresentMon elevado");
            return null;
        }
        _extraNames = extra;
        Diag($"recomeçando filtrado também por {string.Join(", ", extra)}: {why}");
        return StartRun;
    }

    private static string? LastLine(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
        ?? log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private void AppendLog(string line)
    {
        lock (_log)
        {
            if (_log.Length > MaxLogChars) return;
            _log.AppendLine(line);
        }
    }

    private void Diag(string text)
    {
        try { _diag?.Invoke(text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    /// <summary>Junta o pedaço lido ao que sobrou e devolve só as linhas terminadas em \n; o resto fica em <paramref name="pending"/>.</summary>
    internal static List<string> TakeLines(StringBuilder pending, ReadOnlySpan<char> chunk)
    {
        var lines = new List<string>();
        foreach (var c in chunk)
        {
            if (c == '\n') { lines.Add(pending.ToString().TrimEnd('\r')); pending.Clear(); }
            else pending.Append(c);
        }
        return lines;
    }

    public void Finish()
    {
        IPresentMonRun? run;
        lock (_gate)
        {
            run = _run;
            _run = null;
            _runId++;
            if (_state is not (LiveCaptureState.NoPermission or LiveCaptureState.Failed)) _state = LiveCaptureState.Finished;
        }
        _ended = true;
        StopRun(run);
    }

    public void Dispose() => Finish();
}

/// <summary>
/// Executa o PresentMon de verdade. Sem arquivo: lê a saída padrão. Com arquivo (PresentMon elevado): lê o arquivo enquanto cresce,
/// só linhas completas, compartilhado para escrita. O processo pai do PresentMon elevado espera o filho terminar, então "terminou"
/// vale para os dois; o texto de erro do pai (ex.: "failed to elevate privilege: cancelled.") chega pela saída de erro.
/// </summary>
public sealed class PresentMonProcessRunner : IPresentMonRunner
{
    public IPresentMonRun Start(string exe, IReadOnlyList<string> arguments, string? outputCsv, Action<string> onLine, Action<string> onError)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        var run = new Run(outputCsv, onLine, onError);
        run.Begin(psi);
        return run;
    }

    private sealed class Run(string? file, Action<string> onLine, Action<string> onError) : IPresentMonRun
    {
        private readonly CancellationTokenSource _cancel = new();
        private Process? _process;
        private Task? _tail;
        private volatile bool _exited, _drained, _output;
        private int? _exitCode;

        public bool HasExited => _exited && (file is null || _drained || _tail is null || _tail.IsCompleted);
        public int? ExitCode => _exited ? _exitCode : null;
        public bool OutputStarted => _output || (file is not null && File.Exists(file));

        public void Begin(ProcessStartInfo psi)
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is { } d) { _output = true; onLine(d); } };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) onError(d); };
            p.Exited += (_, _) =>
            {
                try { p.WaitForExit(); _exitCode = p.ExitCode; } // WaitForExit() sem prazo: espera o fim das leituras redirecionadas
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                _exited = true;
            };
            p.Start(); // Win32Exception sobe para quem chamou (captura: "não foi possível iniciar")
            _process = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (file is not null) _tail = Task.Run(() => TailAsync(file, _cancel.Token));
        }

        /// <summary>Lê o arquivo enquanto cresce (compartilhado para escrita: o PresentMon elevado continua gravando).</summary>
        private async Task TailAsync(string path, CancellationToken cancel)
        {
            try
            {
                while (!File.Exists(path))
                {
                    if (_exited) { _drained = true; return; } // terminou sem criar o arquivo (aviso recusado, erro)
                    await Task.Delay(250, cancel).ConfigureAwait(false);
                }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                var pending = new StringBuilder();
                var buffer = new char[16 * 1024];
                while (!cancel.IsCancellationRequested)
                {
                    var exitedBefore = _exited;
                    var n = await reader.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
                    if (n == 0)
                    {
                        if (exitedBefore) { _drained = true; return; }
                        await Task.Delay(100, cancel).ConfigureAwait(false);
                        continue;
                    }
                    _output = true;
                    // Só linhas COMPLETAS: o PresentMon pode estar no meio de gravar a última (um número cortado pareceria válido).
                    foreach (var line in PresentMonLiveCapture.TakeLines(pending, buffer.AsSpan(0, n))) onLine(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onError($"Leitura do arquivo do PresentMon falhou: {ex.Message}"); }
            finally { _drained = true; }
        }

        public void Terminate()
        {
            _cancel.Cancel();
            try { _tail?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            // Sem elevação, o PresentMon é nosso: encerra. Com elevação, ele termina sozinho quando o jogo fecha, no limite de tempo
            // ou quando uma nova captura para a sessão dele (--stop_existing_session).
            try { if (_process is { HasExited: false } p && file is null) p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }

        public void Dispose()
        {
            Terminate();
            _process?.Dispose();
            _cancel.Dispose();
            if (file is not null)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
