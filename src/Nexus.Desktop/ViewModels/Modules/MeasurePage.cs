using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Nexus.Broker;
using Nexus.Desktop.Views.Modules.Measure;
using Nexus.HardwareDiscovery;
using Nexus.Measurement;
using Nexus.Measurement.Windows;
using Nexus.Monitoring;
using Nexus.Monitoring.Windows;
using Nexus.Persistence;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Medir: detecta o jogo aberto, mede sozinho (PresentMon em tempo real), compara a partida com a anterior do mesmo jogo
/// (antes x depois automático, com veredito honesto), mostra os números ao vivo e controla o mostrador de FPS. Só LÊ:
/// processos (acesso limitado, sem tocar no jogo), quadros (ETW pelo PresentMon) e o diário do NEXUS (somente leitura).
/// As preferências e as sessões ficam em arquivos do usuário em %LocalAppData%\NEXUS.
/// </summary>
public sealed class MeasurePage : ModulePage, IDisposable
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly SessionStore _store = new(SessionStore.DefaultFolder);
    private readonly string _prefsPath = MeasurePreferences.DefaultPath;
    private readonly object _engineGate = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly OverlayController _overlay = new(new OverlaySurface());
    private readonly Dictionary<string, int> _resolutions = new(StringComparer.Ordinal);
    private MeasurePreferences _prefs;
    private WindowsProcessProbe? _probe;
    private AutoMeasureEngine? _engine;
    private GlobalHotkey? _hotkey;
    private MonitorSession? _monitor;
    private SystemLoad _load;
    private bool _polling, _loadTicking, _started, _onBattery;
    private DateTimeOffset _libraryReadAt = DateTimeOffset.MinValue, _systemReadAt = DateTimeOffset.MinValue;
    private IReadOnlyList<KnownGame> _library = [];
    private IReadOnlyList<AppliedChange> _changes = [];
    private IReadOnlyList<MeasuredSession> _all = [];
    private MeasuredSession? _liveReference;
    private DetectedGame? _detected;
    private string _status = "";
    private string _heroTitle = "Nenhum jogo aberto";
    private string _heroDetail = "";
    private string _captureNote = "";
    private string _liveFps = "—", _liveLow1 = "—", _liveFrametime = "—", _liveAverage = "—", _liveDelta = "", _liveClock = "";
    private IReadOnlyList<TracePoint> _liveTrace = [], _referenceTrace = [];
    private string _referenceText = "";
    private AutoComparison? _comparison;
    private GameRow? _selectedGame;
    private SessionRow? _selected;
    private string _newGameExe = "";
    private string _manualExe = "";
    private string _hotkeyText;
    private string _hotkeyStatus = "";
    private string _libraryText = "Lendo as bibliotecas de jogos…";
    private string _inviteText = "";
    private SessionComparisonResult? _manual;

    public MeasurePage()
    {
        _prefs = MeasurePreferences.Load(_prefsPath);
        if (_prefs.PresentMonPath is null && LegacyPresentMonPath() is { } legacy) _prefs = _prefs with { PresentMonPath = legacy };
        _prefs = _prefs with { PresentMonPath = PresentMonCli.Locate(_prefs.PresentMonPath, AppContext.BaseDirectory) };
        _hotkeyText = _prefs.OverlayHotkey;
        _overlay.SetCorner(_prefs.OverlayCorner);
        _overlay.ShowSystem = _prefs.OverlayShowSystem;
        _overlay.Changed += () => OnPropertyChanged(nameof(OverlayEnabled));
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _liveTimer.Tick += (_, _) => UpdateLive();

        StartCaptureCommand = new RelayCommand(() => _ = StartManualAsync(), () => !IsCapturing && HasPresentMon && (_detected is not null || PresentMonCli.IsValidExeName(ManualExe.Trim())));
        StopCaptureCommand = new RelayCommand(() => _ = StopManualAsync(), () => IsCapturing);
        AddCurrentGameCommand = new RelayCommand(() => SetPrefs(_prefs.WithUserGame(_detected!.ExeName), $"{_detected!.ExeName} entrou na sua lista de jogos."),
            () => _detected is { Reason: not DetectionReason.UserList });
        ExcludeCurrentCommand = new RelayCommand(() => SetPrefs(_prefs.WithExcluded(_detected!.ExeName), $"{_detected!.ExeName} não será mais tratado como jogo."),
            () => _detected is not null);
        AddGameCommand = new RelayCommand(AddGame, () => PresentMonCli.IsValidExeName(NewGameExe.Trim()));
        RemoveGameCommand = new TextCommand(exe => SetPrefs(_prefs with { UserGames = _prefs.UserGames.Where(g => g != exe).ToList() }, $"{exe} saiu da sua lista."));
        RemoveExcludedCommand = new TextCommand(exe => SetPrefs(_prefs with { ExcludedGames = _prefs.ExcludedGames.Where(g => g != exe).ToList() }, $"{exe} voltou a poder ser detectado."));
        ApplyHotkeyCommand = new RelayCommand(ApplyHotkey);
        BrowseCommand = new RelayCommand(Browse);
        RescanPresentMonCommand = new RelayCommand(RescanPresentMon);
        RescanLibraryCommand = new RelayCommand(() => _ = ReadLibraryAsync(force: true));
        PreviewOverlayCommand = new RelayCommand(() => { _overlay.SetPreview(!_overlay.Preview); if (_overlay.Preview && !_overlay.Enabled) _overlay.SetEnabled(true); OnPropertyChanged(nameof(PreviewText)); });
        DismissInviteCommand = new RelayCommand(() => { SetPrefs(_prefs with { LastInviteDismissedAt = DateTimeOffset.Now }, null); UpdateInvite(); });
        EnableAutoCommand = new RelayCommand(() => AutoMeasure = true, () => !AutoMeasure);
        ImportCommand = new RelayCommand(Import);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null && !Busy);
        ToggleExcludeCommand = new RelayCommand(ToggleExclude, () => Selected is not null);
        TagBeforeCommand = new RelayCommand(() => Retag(SessionTag.Before), () => Selected is not null);
        TagAfterCommand = new RelayCommand(() => Retag(SessionTag.After), () => Selected is not null);
        ClearTagCommand = new RelayCommand(() => Retag(SessionTag.None), () => Selected is not null && Selected.Tag != SessionTag.None);
        CompareSelectedCommand = new RelayCommand(() => Compare(Selected?.Session), () => Selected is not null);
        ExportReportCommand = new RelayCommand(ExportReport, () => Comparison is not null);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _all.Count > 0);
        CleanupCommand = new RelayCommand(Cleanup, () => KeepDays > 0 && _all.Count > 0);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        Reload();
    }

    public override string Title => "Medir";
    public override string Group => "Medir";
    public override string Subtitle => "O NEXUS percebe quando um jogo abre, mede cada partida e compara com a anterior. Medir não altera nada no PC.";

    // ------------------------------------------------------------------ estado geral

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string HeroTitle { get => _heroTitle; private set => Set(ref _heroTitle, value); }
    public string HeroDetail { get => _heroDetail; private set => Set(ref _heroDetail, value); }
    public bool IsPlaying => _detected is not null;
    public bool IsCapturing => _engine?.Session is not null;
    public string CaptureNote { get => _captureNote; private set => Set(ref _captureNote, value); }
    public string ManualExe { get => _manualExe; set => Set(ref _manualExe, value); }

    // ------------------------------------------------------------------ ao vivo

    public string LiveFps { get => _liveFps; private set => Set(ref _liveFps, value); }
    public string LiveLow1 { get => _liveLow1; private set => Set(ref _liveLow1, value); }
    public string LiveFrametime { get => _liveFrametime; private set => Set(ref _liveFrametime, value); }
    public string LiveAverage { get => _liveAverage; private set => Set(ref _liveAverage, value); }
    public string LiveDelta { get => _liveDelta; private set => Set(ref _liveDelta, value); }
    public string LiveClock { get => _liveClock; private set => Set(ref _liveClock, value); }
    public IReadOnlyList<TracePoint> LiveTrace { get => _liveTrace; private set => Set(ref _liveTrace, value); }
    public IReadOnlyList<TracePoint> ReferenceTrace { get => _referenceTrace; private set => Set(ref _referenceTrace, value); }
    public string ReferenceText { get => _referenceText; private set => Set(ref _referenceText, value); }

    // ------------------------------------------------------------------ convite depois de aplicar

    public string InviteText { get => _inviteText; private set { if (Set(ref _inviteText, value)) OnPropertyChanged(nameof(ShowInvite)); } }
    public bool ShowInvite => InviteText.Length > 0;

    // ------------------------------------------------------------------ antes x depois

    public AutoComparison? Comparison
    {
        get => _comparison;
        private set
        {
            if (!Set(ref _comparison, value)) return;
            foreach (var p in new[] { nameof(HasComparison), nameof(ComparisonTitle), nameof(ComparisonVerdict), nameof(ComparisonTone), nameof(ComparisonRows),
                         nameof(ComparisonWarnings), nameof(ComparisonChanges), nameof(ComparisonMethod), nameof(ComparisonWhy), nameof(ComparisonBeforeTrace),
                         nameof(ComparisonAfterTrace), nameof(ComparisonModeText) })
                OnPropertyChanged(p);
        }
    }
    public bool HasComparison => Comparison is not null;
    public string ComparisonTitle => Comparison is { } c
        ? $"{c.Current.DisplayName}: partida de {c.Reference.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt)} → {c.Current.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt)}"
        : "";
    public string ComparisonVerdict => Comparison?.Verdict ?? "";
    public string ComparisonTone => Comparison?.Tone ?? "Neutral";
    public string ComparisonModeText => Comparison?.Mode == "partidas" ? "Comparando partidas inteiras" : "Comparando trechos equivalentes de duas partidas";
    public IReadOnlyList<ComparisonRow> ComparisonRows => Comparison?.Result.Metrics.Select(m => new ComparisonRow(m)).ToList() ?? [];
    public IReadOnlyList<string> ComparisonWarnings => Comparison?.Warnings ?? [];
    public IReadOnlyList<string> ComparisonChanges => Comparison?.ChangesBetween.Select(c => $"{c.Title} · {c.At.ToLocalTime().ToString("dd/MM HH:mm", Pt)}{(c.Active ? "" : " · desfeita depois")}").ToList() ?? [];
    public string ComparisonMethod => Comparison?.Result.Method ?? "";
    public string ComparisonWhy => Comparison?.Why ?? "";
    public IReadOnlyList<TracePoint> ComparisonBeforeTrace => Comparison?.Reference.Trace ?? [];
    public IReadOnlyList<TracePoint> ComparisonAfterTrace => Comparison?.Current.Trace ?? [];
    /// <summary>Sem comparação: explica o que falta (primeira partida, nenhuma partida...).</summary>
    public string ComparisonEmptyText => _all.Count == 0
        ? "Ligue a medição automática e jogue uma partida. Na próxima partida do mesmo jogo, o antes x depois aparece aqui sozinho."
        : "Jogue mais uma partida deste jogo (2 minutos ou mais) e o NEXUS compara com a anterior.";

    // ------------------------------------------------------------------ jogos e sessões

    public ObservableCollection<GameRow> Games { get; } = [];
    public GameRow? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!Set(ref _selectedGame, value)) return;
            FillSessions();
            Compare(_all.Where(s => value is null || s.Game == value.Key).MaxBy(s => s.CapturedAt));
        }
    }
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public SessionRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool HasSessions => _all.Count > 0;
    public bool HasGames => Games.Count > 0;

    public string ManualHeadline => _manual?.Headline ?? "";
    public string ManualMethod => _manual?.Method ?? "";
    public IReadOnlyList<ComparisonRow> ManualRows => _manual?.Metrics.Select(m => new ComparisonRow(m)).ToList() ?? [];
    public bool HasManual => _manual is { Metrics.Count: > 0 };

    // ------------------------------------------------------------------ preferências

    public bool AutoMeasure
    {
        get => _prefs.AutoMeasure;
        set { if (value != _prefs.AutoMeasure) SetPrefs(_prefs with { AutoMeasure = value }, value ? "Medição automática ligada: a próxima partida será medida sozinha." : "Medição automática desligada."); }
    }

    /// <summary>O botão do mostrador (o atalho global faz o mesmo).</summary>
    public bool OverlayEnabled
    {
        get => _overlay.Enabled;
        set { if (value != _overlay.Enabled) { _overlay.SetEnabled(value); EnsureSystemLoad(); } }
    }
    public string PreviewText => _overlay.Preview ? "Esconder a prévia" : "Ver a prévia agora";
    public bool OverlayOnGameStart { get => _prefs.OverlayOnGameStart; set { if (value != _prefs.OverlayOnGameStart) SetPrefs(_prefs with { OverlayOnGameStart = value }, null); } }
    public bool OverlayShowSystem
    {
        get => _prefs.OverlayShowSystem;
        set { if (value == _prefs.OverlayShowSystem) return; SetPrefs(_prefs with { OverlayShowSystem = value }, null); _overlay.ShowSystem = value; EnsureSystemLoad(); }
    }
    public int OverlayCornerValue
    {
        get => (int)_prefs.OverlayCorner;
        set
        {
            var c = (OverlayCorner)Math.Clamp(value, 0, 3);
            if (c == _prefs.OverlayCorner) return;
            SetPrefs(_prefs with { OverlayCorner = c }, null);
            _overlay.SetCorner(c);
        }
    }
    public bool HotkeyEnabled { get => _prefs.HotkeyEnabled; set { if (value != _prefs.HotkeyEnabled) { SetPrefs(_prefs with { HotkeyEnabled = value }, null); RegisterHotkey(); } } }
    public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }
    public string HotkeyStatus { get => _hotkeyStatus; private set => Set(ref _hotkeyStatus, value); }
    public bool DetectFullscreen { get => _prefs.DetectFullscreen; set { if (value != _prefs.DetectFullscreen) SetPrefs(_prefs with { DetectFullscreen = value }, null); } }
    public bool RequestElevation { get => _prefs.RequestElevation; set { if (value != _prefs.RequestElevation) SetPrefs(_prefs with { RequestElevation = value }, null); } }
    public int MinSessionSeconds { get => _prefs.MinSessionSeconds; set { if (value != _prefs.MinSessionSeconds) SetPrefs(_prefs with { MinSessionSeconds = value }, null); } }
    public int KeepDays { get => _prefs.KeepDays; set { if (value != _prefs.KeepDays) SetPrefs(_prefs with { KeepDays = value }, null); } }
    public IReadOnlyList<string> UserGames => _prefs.UserGames;
    public IReadOnlyList<string> ExcludedGames => _prefs.ExcludedGames;
    public string NewGameExe { get => _newGameExe; set => Set(ref _newGameExe, value); }
    public string LibraryText { get => _libraryText; private set => Set(ref _libraryText, value); }

    public string? PresentMonPath => _prefs.PresentMonPath;
    public bool HasPresentMon => PresentMonPath is not null && File.Exists(PresentMonPath);
    public string PresentMonText => HasPresentMon ? $"{Path.GetFileName(PresentMonPath!)} ({PresentMonCli.GenerationOf(PresentMonPath) switch { PresentMonGeneration.V1 => "versão 1.x", _ => "versão 2.x" }})" : "PresentMon não encontrado";
    public string PresentMonHelp { get; } =
        "O NEXUS usa o PresentMon, ferramenta gratuita e oficial da Intel, para ler o tempo de cada quadro (rastreamento de eventos do Windows, " +
        "sem entrar no jogo). O NEXUS não baixa nada sozinho: baixe o arquivo PresentMon-2.x-x64.exe em github.com/GameTechDev/PresentMon " +
        "(seção Releases) e salve em %LocalAppData%\\NEXUS\\PresentMon ou indique onde salvou. Se você já usa o CapFrameX ou o app PresentMon da " +
        "Intel, o NEXUS procura lá também.";
    public bool CanTraceSilently { get; } = SafeCanTrace();
    public string RightsText => CanTraceSilently
        ? "Sua conta pode medir sem aviso do Windows."
        : "Cada medição vai pedir permissão ao Windows (o PresentMon precisa disso). Para medir sem aviso, um administrador pode adicionar " +
          "sua conta ao grupo \"Usuários de log de desempenho\" (PowerShell como administrador: Add-LocalGroupMember -SID S-1-5-32-559 -Member $env:USERNAME) " +
          "e você sai e entra de novo no Windows. O NEXUS não faz isso sozinho.";

    public string OverlayHelp { get; } =
        "O mostrador é uma janela do próprio NEXUS por cima do jogo: não entra no jogo, não usa ganchos nem injeção, por isso é seguro com " +
        "anticheats (Vanguard, Easy Anti-Cheat, BattlEye). Ele aparece em janela sem bordas, que é o padrão no Windows 11 com as otimizações " +
        "para jogos em janela. Em tela cheia exclusiva o jogo é dono da tela e nenhuma janela aparece por cima: troque para sem bordas.";

    // ------------------------------------------------------------------ comandos

    public RelayCommand StartCaptureCommand { get; }
    public RelayCommand StopCaptureCommand { get; }
    public RelayCommand AddCurrentGameCommand { get; }
    public RelayCommand ExcludeCurrentCommand { get; }
    public RelayCommand AddGameCommand { get; }
    public TextCommand RemoveGameCommand { get; }
    public TextCommand RemoveExcludedCommand { get; }
    public RelayCommand ApplyHotkeyCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand RescanPresentMonCommand { get; }
    public RelayCommand RescanLibraryCommand { get; }
    public RelayCommand PreviewOverlayCommand { get; }
    public RelayCommand DismissInviteCommand { get; }
    public RelayCommand EnableAutoCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ToggleExcludeCommand { get; }
    public RelayCommand TagBeforeCommand { get; }
    public RelayCommand TagAfterCommand { get; }
    public RelayCommand ClearTagCommand { get; }
    public RelayCommand CompareSelectedCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand CleanupCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    // ------------------------------------------------------------------ ciclo de vida

    public override async Task OnFactsReadyAsync(SystemFacts facts)
    {
        EnsureStarted();
        await RefreshChangesAsync();
        UpdateInvite();
    }

    public override async Task OnShownAsync()
    {
        EnsureStarted();
        if (DateTimeOffset.Now - _libraryReadAt > TimeSpan.FromMinutes(10)) await ReadLibraryAsync(force: false);
        await RefreshChangesAsync();
        UpdateInvite();
    }

    /// <summary>Começa a acompanhar processos (a cada 2 s, leitura barata) e registra o atalho, uma vez.</summary>
    private void EnsureStarted()
    {
        if (_started || !OperatingSystem.IsWindows()) return;
        _started = true;
        try
        {
            _probe = new WindowsProcessProbe();
            _engine = new AutoMeasureEngine(_probe, new PresentMonLiveStarter(() => _prefs.PresentMonPath, () => _prefs.RequestElevation && !CanTraceSilently));
            _pollTimer.Start();
            RegisterHotkey();
            CleanOldTempFiles();
            _ = ReadLibraryAsync(force: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = $"Não foi possível iniciar a detecção de jogos: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _liveTimer.Stop();
        lock (_engineGate) _engine?.StopCapture();
        _hotkey?.Dispose();
        _monitor?.Dispose();
        _overlay.SetEnabled(false);
    }

    // ------------------------------------------------------------------ detecção e captura

    private DetectionRules Rules() => new(_library, _prefs.UserGames, _prefs.ExcludedGames, _prefs.DetectFullscreen);

    private async Task PollAsync()
    {
        if (_polling || _engine is null) return;
        _polling = true;
        try
        {
            var rules = Rules();
            var auto = _prefs.AutoMeasure;
            var (ev, snap, game) = await Task.Run(() =>
            {
                lock (_engineGate)
                {
                    var e = _engine.Poll(rules, auto, DateTimeOffset.Now);
                    return (e, _engine.LastSnapshot, _engine.CurrentGame);
                }
            });
            _detected = game;
            TrackConditions(snap);
            switch (ev.Kind)
            {
                case GameEventKind.Started:
                    _overlay.OnGame(true, _prefs.OverlayOnGameStart);
                    EnsureSystemLoad();
                    if (ev.CaptureStarted) OnCaptureStarted(ev.Game!);
                    else if (ev.Note is not null) CaptureNote = ev.Note;
                    break;
                case GameEventKind.Stopped:
                    _overlay.OnGame(false, false);
                    if (ev.Finished is { } finished) await SaveAsync(finished);
                    break;
            }
            if (_engine.Capture is { Ended: true } c && _engine.Session is { Frames: 0 } && c.Log.Length > 0)
                CaptureNote = "O PresentMon parou sem enviar quadros. Veja a saída: " + Shorten(c.Log);
            UpdateHero();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = $"Falha ao acompanhar o jogo: {ex.Message}";
        }
        finally { _polling = false; }
    }

    private void TrackConditions(ProcessSnapshot? snap)
    {
        if (_engine?.Session is not { } s || snap?.Foreground is not { } fg || fg.Pid != s.Game.Pid || fg.Width < 320) return;
        var key = $"{fg.Width}x{fg.Height}";
        _resolutions[key] = _resolutions.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    private void OnCaptureStarted(DetectedGame game)
    {
        _resolutions.Clear();
        _onBattery = WindowsProcessProbe.OnBattery() == true;
        _liveReference = AutoCompare.LiveReference(game.Key, DateTimeOffset.Now, _all, _changes);
        ReferenceText = _liveReference is { } r
            ? $"Linha ciano: partida de {r.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt)} ({r.Summary.AverageFps.ToString("0", Pt)} FPS médio, 1% low {r.Summary.OnePercentLowAverageFps.ToString("0", Pt)})."
            : "Primeira partida medida deste jogo: ela vira a referência das próximas.";
        CaptureNote = RequestElevation && !CanTraceSilently ? "Se o Windows pedir permissão para o PresentMon, aceite para medir." : "";
        _liveTimer.Start();
        Status = $"Medindo {game.Name}…";
        Notify();
    }

    private async Task StartManualAsync()
    {
        if (_engine is null) return;
        var game = _detected ?? new DetectedGame(GameMatcher.Pretty(ManualExe.Trim()), ManualExe.Trim(), 0, DetectionReason.UserList, "à mão");
        var ok = await Task.Run(() => { lock (_engineGate) return _engine.StartManual(game, DateTimeOffset.Now); });
        if (ok) OnCaptureStarted(game);
    }

    private async Task StopManualAsync()
    {
        if (_engine is null) return;
        var finished = await Task.Run(() => { lock (_engineGate) return _engine.StopCapture(); });
        if (finished is not null) await SaveAsync(finished);
        Notify();
    }

    private void UpdateLive()
    {
        var session = _engine?.Session;
        if (session is null)
        {
            _liveTimer.Stop();
            Notify();
            return;
        }
        var r = session.Read();
        LiveFps = Num(r.CurrentFps, "0");
        LiveLow1 = Num(r.Low1Fps, "0");
        LiveFrametime = Num(r.FrametimeMs, "0.0");
        LiveAverage = Num(r.AverageFps, "0");
        LiveClock = TimeSpan.FromSeconds(r.ElapsedSeconds).ToString(r.ElapsedSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", Pt);
        LiveDelta = LiveComparison.Delta(r, _liveReference)?.Text ?? (_liveReference is null ? "" : "A diferença aparece depois de 30 s de jogo.");
        var trace = session.Trace();
        var end = r.ElapsedSeconds;
        var from = Math.Max(0, end - 60);
        LiveTrace = trace.Where(p => p.Seconds >= from).Select(p => new TracePoint(p.Seconds - from, p.FrametimeMs)).ToList();
        ReferenceTrace = LiveComparison.ReferenceWindow(_liveReference, from, end).Select(p => new TracePoint(p.Seconds - from, p.FrametimeMs)).ToList();
        _overlay.Update(r, _load);
        if (DateTimeOffset.Now - _systemReadAt >= TimeSpan.FromSeconds(1)) _ = ReadSystemLoadAsync();
    }

    /// <summary>Partida terminou: guarda (se durou o mínimo), marca com jogo + condições + mudanças ativas e compara.</summary>
    private async Task SaveAsync(LiveSession finished)
    {
        _liveTimer.Stop();
        ClearLive();
        if (finished.Seconds < _prefs.MinSessionSeconds || finished.Frames < 100)
        {
            Status = $"Partida de {finished.Game.Name} curta demais ({finished.Seconds:0} s): não foi guardada (mínimo {_prefs.MinSessionSeconds} s).";
            Notify();
            return;
        }
        var resolution = _resolutions.Count > 0 ? _resolutions.MaxBy(k => k.Value).Key : null;
        var battery = _onBattery || WindowsProcessProbe.OnBattery() == true;
        var driver = GpuDriver(Host?.Facts);
        var active = _changes.Where(c => c.Active).ToList();
        try
        {
            var saved = await Task.Run(() =>
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"nexus-partida-{Guid.NewGuid():N}.csv");
                try
                {
                    finished.WriteCsv(tmp);
                    var s = _store.Import(tmp, $"{finished.Game.Name} {finished.StartedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt)}", finished.Game.ExeName,
                        finished.Automatic ? "automático" : "ao vivo", finished.StartedAt);
                    s = s with
                    {
                        GameName = finished.Game.Name,
                        Automatic = finished.Automatic,
                        Applied = active,
                        Conditions = (s.Conditions ?? new SessionConditions()) with
                        {
                            Resolution = resolution, GpuDriver = driver, OnBattery = battery,
                            WindowsBuild = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture),
                        },
                    };
                    _store.Save(s);
                    return s;
                }
                finally { try { File.Delete(tmp); } catch (IOException) { } }
            });
            Reload(saved.Game);
            Compare(saved);
            Status = Comparison is { } c && c.Current.Id == saved.Id
                ? $"Partida de {saved.DisplayName} guardada. Antes x depois: {c.Verdict}"
                : $"Partida de {saved.DisplayName} guardada: {saved.Summary.AverageFps:0} FPS médio, 1% low {saved.Summary.OnePercentLowAverageFps:0}.";
            Host?.Notify(Status);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"A partida terminou, mas não deu para guardar: {ex.Message}";
        }
        Notify();
    }

    private void ClearLive()
    {
        LiveFps = LiveLow1 = LiveFrametime = LiveAverage = "—";
        LiveDelta = LiveClock = "";
        LiveTrace = [];
        ReferenceTrace = [];
    }

    private void UpdateHero()
    {
        if (_detected is { } g)
        {
            HeroTitle = g.Name;
            var measuring = _engine?.Session is { } s ? $" · medindo há {LiveClockOr(s)}" : AutoMeasure ? "" : " · medição automática desligada";
            HeroDetail = $"Aberto agora: {g.ExeName}, {g.ReasonText}{measuring}.";
        }
        else if (_engine?.Session is { } s)
        {
            HeroTitle = s.Game.Name;
            HeroDetail = $"Medindo {s.Game.ExeName} (iniciado à mão).";
        }
        else
        {
            var last = _all.MaxBy(x => x.CapturedAt);
            HeroTitle = "Nenhum jogo aberto";
            HeroDetail = last is null
                ? (AutoMeasure ? "Abra um jogo: o NEXUS começa a medir sozinho." : "Ligue \"Medir automaticamente\" e abra um jogo.")
                : $"Último jogo medido: {last.DisplayName}, em {last.CapturedAt.ToLocalTime().ToString("dd/MM 'às' HH:mm", Pt)}.";
        }
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsCapturing));
    }

    private static string LiveClockOr(LiveSession s) => TimeSpan.FromSeconds(s.Seconds).ToString(s.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private void Notify()
    {
        UpdateHero();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                                    }

    // ------------------------------------------------------------------ mostrador

    private void RegisterHotkey()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!_prefs.HotkeyEnabled) { _hotkey?.Unregister(); HotkeyStatus = "Atalho desligado."; return; }
        try
        {
            if (_hotkey is null)
            {
                _hotkey = new GlobalHotkey();
                _hotkey.Pressed += () => { _overlay.Toggle(); EnsureSystemLoad(); Host?.Notify(_overlay.Enabled ? "Mostrador de FPS ligado." : "Mostrador de FPS desligado."); };
            }
            var key = Hotkey.Parse(_prefs.OverlayHotkey);
            HotkeyStatus = _hotkey.Register(key)
                ? $"{key} liga e desliga o mostrador, mesmo com o NEXUS minimizado."
                : $"{key} já é usado por outro programa. Escolha outra combinação.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { HotkeyStatus = $"Não foi possível registrar o atalho: {ex.Message}"; }
    }

    private void ApplyHotkey()
    {
        if (!Hotkey.TryParse(HotkeyText, out var key))
        {
            HotkeyStatus = "Use Ctrl, Alt ou Win com uma letra, número ou F1–F24. Ex.: Ctrl+Shift+F.";
            return;
        }
        HotkeyText = key.ToString();
        SetPrefs(_prefs with { OverlayHotkey = key.ToString(), HotkeyEnabled = true }, null);
        OnPropertyChanged(nameof(HotkeyEnabled));
        RegisterHotkey();
    }

    /// <summary>CPU/GPU/temperatura para o mostrador: só abre os contadores quando o mostrador mostra esses dados.</summary>
    private void EnsureSystemLoad()
    {
        if (!OperatingSystem.IsWindows() || !_overlay.ShowSystem || !_overlay.Visible || _monitor is not null) return;
        _ = Task.Run(() =>
        {
            try { _monitor ??= new MonitorSession(new WindowsPerformanceSource(), null, new WindowsNvidiaSource()) { NvidiaEveryTicks = 5 }; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _monitor = null; }
        });
        if (!_liveTimer.IsEnabled) _liveTimer.Start();
    }

    private async Task ReadSystemLoadAsync()
    {
        if (_loadTicking || _monitor is not { } m || !_overlay.Visible || !_overlay.ShowSystem) return;
        _loadTicking = true;
        _systemReadAt = DateTimeOffset.Now;
        try
        {
            var tick = await Task.Run(m.Tick);
            _load = new SystemLoad(tick.Snapshot.Cpu.TotalPercent, tick.Snapshot.Gpu?.OverallPercent,
                tick.Nvidia.Select(n => n.TemperatureC).FirstOrDefault(t => t is not null) ?? _load.GpuTemperatureC);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _load = default; }
        finally { _loadTicking = false; }
    }

    // ------------------------------------------------------------------ dados: sessões, diário, bibliotecas

    private void Reload(string? selectGame = null)
    {
        var (list, bad) = _store.List();
        _all = list;
        var keepGame = selectGame ?? SelectedGame?.Key;
        Games.Clear();
        foreach (var g in SessionLibrary.Group(list)) Games.Add(new GameRow(g));
        _selectedGame = Games.FirstOrDefault(g => g.Key == keepGame) ?? Games.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedGame));
        FillSessions();
        if (Comparison is null || selectGame is not null) Compare(_all.Where(s => _selectedGame is null || s.Game == _selectedGame.Key).MaxBy(s => s.CapturedAt));
        RecomputeManual();
        foreach (var p in new[] { nameof(HasSessions), nameof(HasGames), nameof(ComparisonEmptyText) }) OnPropertyChanged(p);
                        if (bad > 0) Status = $"{bad} arquivo(s) de sessão ilegível(is) em {_store.Folder} foram ignorados.";
        UpdateHero();
    }

    private void FillSessions()
    {
        var keep = Selected?.Session.Id;
        Sessions.Clear();
        foreach (var s in _all.Where(s => SelectedGame is null || s.Game == SelectedGame.Key)) Sessions.Add(new SessionRow(s));
        Selected = Sessions.FirstOrDefault(r => r.Session.Id == keep) ?? Sessions.FirstOrDefault();
    }

    private void Compare(MeasuredSession? current)
    {
        try { Comparison = current is null ? null : AutoCompare.Compare(current, _all, _changes); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Comparison = null; Status = $"Não deu para comparar: {ex.Message}"; }
        OnPropertyChanged(nameof(ComparisonEmptyText));
            }

    private void RecomputeManual()
    {
        var before = _all.Where(s => s.Tag == SessionTag.Before).ToList();
        var after = _all.Where(s => s.Tag == SessionTag.After).ToList();
        _manual = before.Count > 0 && after.Count > 0 ? SessionComparison.Compare(before, after) : null;
        foreach (var p in new[] { nameof(ManualHeadline), nameof(ManualMethod), nameof(ManualRows), nameof(HasManual) }) OnPropertyChanged(p);
    }

    private async Task RefreshChangesAsync()
    {
        try
        {
            _changes = await Task.Run(ReadChanges);
            if (Comparison is { } c) Compare(_all.FirstOrDefault(s => s.Id == c.Current.Id));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = $"Não deu para ler o diário: {ex.Message}"; }
    }

    /// <summary>Mudanças do NEXUS dos dois diários (usuário e administrador), abertos SOMENTE para leitura.</summary>
    private static IReadOnlyList<AppliedChange> ReadChanges()
    {
        var rows = new List<JournalRow>();
        foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
        {
            var path = LocalBroker.JournalPath(level);
            if (!File.Exists(path)) continue;
            try
            {
                using var journal = SqliteJournal.OpenReadOnly(path);
                var since = DateTimeOffset.Now.AddDays(-180);
                foreach (var run in journal.Runs().Where(r => r.StartedAt > since).OrderByDescending(r => r.StartedAt).Take(300))
                foreach (var e in journal.Entries(run.RunId))
                    rows.Add(new JournalRow(run.RunId, e.Action.Definition.Id, e.Action.Definition.Title, e.Action.TargetDescription, e.State.ToString(), e.UpdatedAt));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* diário de outra conta ou bloqueado: segue sem ele */ }
        }
        return ChangeHistory.From(rows);
    }

    /// <summary>
    /// Convite depois de aplicar (integração com o Automático e as outras páginas): se houve mudança do NEXUS depois da última
    /// partida medida, sugere jogar uma partida para medir o antes e depois.
    /// </summary>
    private void UpdateInvite()
    {
        var latestChange = _changes.Count > 0 ? _changes[0] : null;
        var lastSession = _all.MaxBy(s => s.CapturedAt);
        if (latestChange is null || lastSession is null || latestChange.At <= lastSession.CapturedAt ||
            (_prefs.LastInviteDismissedAt is { } d && d >= latestChange.At))
        {
            InviteText = "";
            return;
        }
        var n = _changes.Count(c => c.At > lastSession.CapturedAt);
        InviteText = $"Você aplicou {(n == 1 ? $"\"{latestChange.Title}\"" : $"{n} mudanças")} depois da última partida de {lastSession.DisplayName}. " +
                     "Jogue uma partida (2 minutos ou mais) para medirmos o antes e depois." + (AutoMeasure ? "" : " Ligue a medição automática para não precisar lembrar.");
    }

    private async Task ReadLibraryAsync(bool force)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!force && DateTimeOffset.Now - _libraryReadAt < TimeSpan.FromMinutes(10)) return;
        _libraryReadAt = DateTimeOffset.Now;
        LibraryText = "Lendo as bibliotecas de jogos…";
        try
        {
            var notes = new List<string>();
            var games = await Task.Run(() => GameLibrary.Scan(WindowsGameRoots.Read(), notes));
            _library = games;
            var byStore = games.GroupBy(g => g.Store).Select(g => $"{g.Key} {g.Count()}");
            LibraryText = games.Count == 0
                ? "Nenhuma biblioteca encontrada (Steam, Epic, Riot, Battle.net, Xbox). Jogos conhecidos e a sua lista continuam valendo."
                : $"{games.Count} jogo(s) nas bibliotecas: {string.Join(", ", byStore)}." + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LibraryText = $"Não deu para ler as bibliotecas: {ex.Message}"; }
    }

    private static string? GpuDriver(SystemFacts? facts) =>
        facts?.Gpus.Where(g => g.Vendor is not null and not "Microsoft").OrderByDescending(g => g.MemoryBytes ?? 0).Select(g => g.DriverVersion).FirstOrDefault(v => v is not null);

    // ------------------------------------------------------------------ preferências

    private void SetPrefs(MeasurePreferences prefs, string? message)
    {
        _prefs = prefs.Normalize();
        if (!_prefs.Save(_prefsPath)) Status = "Não deu para salvar as preferências em " + _prefsPath;
        else if (message is not null) Status = message;
        foreach (var p in new[] { nameof(AutoMeasure), nameof(OverlayOnGameStart), nameof(OverlayShowSystem), nameof(OverlayCornerValue), nameof(HotkeyEnabled),
                     nameof(DetectFullscreen), nameof(RequestElevation), nameof(MinSessionSeconds), nameof(KeepDays), nameof(UserGames), nameof(ExcludedGames),
                     nameof(PresentMonPath), nameof(HasPresentMon), nameof(PresentMonText) })
            OnPropertyChanged(p);
                        Notify();
    }

    private void AddGame()
    {
        var exe = NewGameExe.Trim();
        if (!PresentMonCli.IsValidExeName(exe)) return;
        SetPrefs(_prefs.WithUserGame(exe), $"{exe} entrou na sua lista de jogos.");
        NewGameExe = "";
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Onde está o PresentMon?", Filter = "PresentMon (PresentMon*.exe)|PresentMon*.exe|Executável (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;
        SetPrefs(_prefs with { PresentMonPath = dialog.FileName }, "PresentMon indicado.");
    }

    private void RescanPresentMon()
    {
        var found = PresentMonCli.Locate(null, AppContext.BaseDirectory);
        SetPrefs(_prefs with { PresentMonPath = found ?? _prefs.PresentMonPath }, found is null ? "PresentMon não encontrado nas pastas conhecidas." : $"PresentMon encontrado: {found}");
    }

    /// <summary>Caminho do PresentMon da versão anterior da página (desktop-settings.json), para não pedir de novo.</summary>
    private static string? LegacyPresentMonPath()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "desktop-settings.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(nameof(PresentMonPath), out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static bool SafeCanTrace() => OperatingSystem.IsWindows() && WindowsProcessProbe.CanTraceWithoutElevation();

    // ------------------------------------------------------------------ sessões: importar, apagar, excluir, marcar, exportar

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Importar captura (CSV do PresentMon ou CapFrameX)", Filter = "CSV (*.csv)|*.csv", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var ok = 0;
        var errors = new List<string>();
        string? lastGame = null;
        foreach (var f in dialog.FileNames)
        {
            try { lastGame = _store.Import(f).Game; ok++; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        Reload(lastGame);
        Status = $"{ok} sessão(ões) importada(s)." + (errors.Count > 0 ? " Não importadas: " + string.Join(" · ", errors) : "");
    }

    private void Delete()
    {
        if (Selected is not { } row) return;
        if (Host?.Confirm("Apagar sessão", $"Apagar a sessão \"{row.Name}\"? O arquivo dela na pasta de sessões do NEXUS será removido.") != true) return;
        _store.Delete(row.Session);
        Reload();
        Status = $"Sessão \"{row.Name}\" apagada.";
    }

    private void ToggleExclude()
    {
        if (Selected is not { } row) return;
        var updated = row.Session with { Excluded = !row.Session.Excluded };
        _store.Save(updated);
        Reload();
        Status = updated.Excluded ? "Esta partida não entra mais nas comparações." : "Esta partida voltou a entrar nas comparações.";
    }

    private void Retag(SessionTag tag)
    {
        if (Selected is not { } row) return;
        row.Session = _store.Retag(row.Session, tag);
        _all = _all.Select(s => s.Id == row.Session.Id ? row.Session : s).ToList();
        RecomputeManual();
            }

    private void ExportReport()
    {
        if (Comparison is not { } c) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar relatório do antes x depois", Filter = "Texto (*.txt)|*.txt",
            FileName = $"nexus-antes-depois-{c.Current.Game.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)}-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SessionLibrary.Report(c), new System.Text.UTF8Encoding(true));
        Status = $"Relatório salvo em {dialog.FileName}.";
    }

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Exportar sessões (CSV)", Filter = "CSV (*.csv)|*.csv", FileName = $"nexus-sessoes-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SessionLibrary.SummaryCsv(_all), new System.Text.UTF8Encoding(true));
        Status = $"{_all.Count} sessão(ões) exportada(s) para {dialog.FileName}.";
    }

    private void Cleanup()
    {
        if (KeepDays <= 0) return;
        var limit = DateTimeOffset.Now.AddDays(-KeepDays);
        var count = _all.Count(s => s.CapturedAt < limit && s.Tag == SessionTag.None);
        if (count == 0) { Status = $"Nenhuma sessão com mais de {KeepDays} dias."; return; }
        if (Host?.Confirm("Limpar sessões antigas", $"Apagar até {count} sessão(ões) com mais de {KeepDays} dias? A última partida de cada jogo e as marcadas como antes/depois ficam.") != true) return;
        var removed = _store.Cleanup(limit);
        Reload();
        Status = $"{removed} sessão(ões) antiga(s) apagada(s) da pasta de sessões do NEXUS.";
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { _store.Folder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { Status = $"Não deu para abrir a pasta: {ex.Message}"; }
    }

    /// <summary>Arquivos temporários de capturas anteriores (de um NEXUS fechado no meio da partida).</summary>
    private static void CleanOldTempFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "nexus-ao-vivo-*.csv").Concat(Directory.EnumerateFiles(Path.GetTempPath(), "nexus-partida-*.csv")))
            {
                try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Num(double v, string fmt) => double.IsFinite(v) ? v.ToString(fmt, Pt) : "—";
    private static string Shorten(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
