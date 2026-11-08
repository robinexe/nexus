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
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Medir: detecta o jogo aberto, mede sozinho (PresentMon em tempo real), compara a partida com a anterior do mesmo jogo
/// (antes x depois automático, com veredito honesto), mostra os números ao vivo e controla o mostrador de FPS. Só LÊ:
/// processos (acesso limitado, sem tocar no jogo), quadros (ETW pelo PresentMon) e o diário do NEXUS (somente leitura).
/// As preferências e as sessões ficam em arquivos do usuário em %LocalAppData%\NEXUS.
/// </summary>
public sealed class MeasurePage : ModulePage, IDisposable
{
    private static CultureInfo Pt => Loc.Culture; // idioma da interface (docs/12-idiomas.md)
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
    private string _heroTitle = Loc.T("Meas.NenhumJogoAberto");
    private string _heroDetail = "";
    private string _captureNote = "";
    private string _heroKicker = Loc.T("Meas.NenhumJogoAberto"), _heroGame = Loc.T("Meas.ProntoMedir"), _heroFpsLabel = Loc.T("Meas.AbraJogoVerFps");
    private double _heroFps = double.NaN;
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
    private string _libraryText = Loc.T("Meas.LendoBibliotecasJogos");
    private string _inviteText = "";
    private SessionComparisonResult? _manual;
    private readonly IPresentMonTrustProbe _trust = PresentMonLocator.SystemProbe();
    private PresentMonLocation? _pm;
    private bool? _perfLogMember;
    private Guid? _perfLogRun;
    private bool _elevateOnce, _probing, _probeNoPermission;
    private string _presentersText = "";
    private LiveCaptureState? _lastLoggedState;

    public MeasurePage()
    {
        DriverLatency = new DriverLatencyPanel(host: () => Host);
        _prefs = MeasurePreferences.Load(_prefsPath);
        if (_prefs.PresentMonPath is null && LegacyPresentMonPath() is { } legacy) _prefs = _prefs with { PresentMonPath = legacy };
        // Conferir o PresentMon (SHA-256/assinatura) e ler as sessões salvas vão para fora da thread da interface (InitialLoadAsync):
        // na abertura do NEXUS esta página é criada antes da janela aparecer.
        _hotkeyText = _prefs.OverlayHotkey;
        _overlay.SetCorner(_prefs.OverlayCorner);
        _overlay.ShowSystem = _prefs.OverlayShowSystem;
        _overlay.Changed += () => OnPropertyChanged(nameof(OverlayEnabled));
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _liveTimer.Tick += (_, _) => UpdateLive();

        StartCaptureCommand = new RelayCommand(() => _ = StartManualAsync(), () => !IsCapturing && HasPresentMon && (_detected is not null || PresentMonCli.IsValidExeName(ManualExe.Trim())));
        StopCaptureCommand = new RelayCommand(() => _ = StopManualAsync(), () => IsCapturing);
        AddCurrentGameCommand = new RelayCommand(() => SetPrefs(_prefs.WithUserGame(_detected!.ExeName), Loc.F("Meas.NEntrouSuaLista", _detected!.ExeName)),
            () => _detected is { Reason: not DetectionReason.UserList });
        ExcludeCurrentCommand = new RelayCommand(() => SetPrefs(_prefs.WithExcluded(_detected!.ExeName), Loc.F("Meas.NNaoSeraMais", _detected!.ExeName)),
            () => _detected is not null);
        AddGameCommand = new RelayCommand(AddGame, () => PresentMonCli.IsValidExeName(NewGameExe.Trim()));
        RemoveGameCommand = new TextCommand(exe => SetPrefs(_prefs with { UserGames = _prefs.UserGames.Where(g => g != exe).ToList() }, Loc.F("Meas.NSaiuSuaLista", exe)));
        RemoveExcludedCommand = new TextCommand(exe => SetPrefs(_prefs with { ExcludedGames = _prefs.ExcludedGames.Where(g => g != exe).ToList() }, Loc.F("Meas.NVoltouPoderSer", exe)));
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
        JoinPerfLogCommand = new RelayCommand(() => _ = JoinPerfLogAsync(), () => ShowPerfLogOffer && !Busy);
        UndoPerfLogCommand = new RelayCommand(() => _ = UndoPerfLogAsync(), () => _perfLogRun is not null && !Busy);
        MeasureAsAdminCommand = new RelayCommand(() => _ = MeasureAsAdminAsync(), () => HasPresentMon && !CanTraceSilently && (_detected is not null || _engine?.Session is not null));
        PickProcessCommand = new RelayCommand(() => _ = ProbePresentersAsync(), () => HasPresentMon && !_probing);
        UsePresenterCommand = new TextCommand(pid => _ = UsePresenterAsync(pid));
        _ = InitialLoadAsync();
    }

    /// <summary>
    /// O que o construtor fazia na thread da interface: localizar e conferir o PresentMon (hash de um executável de vários MB e,
    /// se não for o embutido, a assinatura) e ler todas as sessões salvas. Agora em Task.Run; o resultado entra na interface igual.
    /// </summary>
    private async Task InitialLoadAsync()
    {
        var configured = _prefs.PresentMonPath;
        try
        {
            var (located, error, sessions) = await Task.Run(() =>
            {
                PresentMonLocation? pm = null;
                string? err = null;
                try { pm = PresentMonLocator.Locate(configured, AppContext.BaseDirectory, _trust); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { err = ex.Message; }
                return (pm, err, _store.List());
            });
            // Se o usuário escolheu outro PresentMon enquanto isso (Procurar/Procurar de novo), vale a escolha dele.
            if (_pm is null && string.Equals(_prefs.PresentMonPath, configured, StringComparison.OrdinalIgnoreCase))
            {
                _pm = located;
                if (error is not null) Status = Loc.T("Meas.NaoDeuConferirPresentmon") + error;
                _prefs = _prefs with { PresentMonPath = _pm?.Path ?? configured };
                foreach (var p in new[] { nameof(PresentMonPath), nameof(HasPresentMon), nameof(PresentMonText), nameof(PresentMonTrustText), nameof(ShowIntro) })
                    OnPropertyChanged(p);
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
            Reload(sessions);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("Meas.NaoDeuLerSessoes", ex.Message); }
    }

    public override string Title => Loc.T("Nav.Medir");
    public override string Group => Loc.T("Nav.Medir");
    public override string Subtitle => Loc.T("Meas.NexusPercebeQuandoJogo");

    // ------------------------------------------------------------------ estado geral

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string HeroTitle { get => _heroTitle; private set => Set(ref _heroTitle, value); }
    public string HeroDetail { get => _heroDetail; private set => Set(ref _heroDetail, value); }
    public bool IsPlaying => _detected is not null;
    /// <summary>Jogo aberto agora pela detecção do Medir (a sessão de jogo da tela Jogos usa a mesma detecção).</summary>
    public DetectedGame? CurrentGame => _detected;
    /// <summary>Começa a acompanhar processos sem abrir a página (sessão de jogo ligada).</summary>
    public void StartWatching() => EnsureStarted();
    public bool IsCapturing => _engine?.Session is not null;
    public string CaptureNote { get => _captureNote; private set => Set(ref _captureNote, value); }
    public string ManualExe { get => _manualExe; set => Set(ref _manualExe, value); }

    // ------------------------------------------------------------------ destaque (só apresentação)

    /// <summary>"Medindo agora", "Aberto agora", "Último jogo medido" ou "Nenhum jogo aberto".</summary>
    public string HeroKicker { get => _heroKicker; private set => Set(ref _heroKicker, value); }
    /// <summary>Nome do jogo em destaque (aberto, medido ou o último medido).</summary>
    public string HeroGame { get => _heroGame; private set => Set(ref _heroGame, value); }
    /// <summary>O número gigante: FPS do último segundo durante a medição; fora dela, o FPS médio da última partida (NaN = nenhuma).</summary>
    public double HeroFps { get => _heroFps; private set => Set(ref _heroFps, value); }
    public string HeroFpsLabel { get => _heroFpsLabel; private set => Set(ref _heroFpsLabel, value); }
    /// <summary>"Como funciona" só enquanto não há nenhuma partida (e o PresentMon já está pronto).</summary>
    public bool ShowIntro => HasPresentMon && !HasSessions;

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
                         nameof(ComparisonAfterTrace), nameof(ComparisonModeText), nameof(ComparisonToneValue), nameof(ComparisonIconKey) })
                OnPropertyChanged(p);
        }
    }
    public bool HasComparison => Comparison is not null;
    public string ComparisonTitle => Comparison is { } c
        ? Loc.F("Meas.NPartidaNN", c.Current.DisplayName, c.Reference.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt), c.Current.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt))
        : "";
    public string ComparisonVerdict => Comparison?.Verdict ?? "";
    public string ComparisonTone => Comparison?.Tone ?? "Neutral";
    /// <summary>Tom do veredito para o selo e o distintivo (verde melhorou, vermelho piorou, ciano sem diferença comprovada).</summary>
    public Tone ComparisonToneValue => ComparisonTone switch { "Good" => Tone.Good, "Bad" => Tone.Bad, _ => Tone.Info };
    public string ComparisonIconKey => ComparisonTone switch { "Good" => "Icon.TrendUp", "Bad" => "Icon.TrendDown", _ => "Icon.Info" };
    /// <summary>Botão do estado vazio: só aparece enquanto a medição automática está desligada.</summary>
    public string ComparisonEmptyAction => AutoMeasure ? "" : Loc.T("Meas.MedirAutomaticamente");
    public string ComparisonModeText => Comparison?.Mode == "partidas" ? Loc.T("Meas.ComparandoPartidasInteiras") : Loc.T("Meas.ComparandoTrechosEquivalentesDuas");
    public IReadOnlyList<ComparisonRow> ComparisonRows => Comparison?.Result.Metrics.Select(m => new ComparisonRow(m)).ToList() ?? [];
    public IReadOnlyList<string> ComparisonWarnings => Comparison?.Warnings ?? [];
    public IReadOnlyList<string> ComparisonChanges => Comparison?.ChangesBetween.Select(c => $"{c.Title} · {c.At.ToLocalTime().ToString("dd/MM HH:mm", Pt)}{(c.Active ? "" : Loc.T("Meas.DesfeitaDepois"))}").ToList() ?? [];
    public string ComparisonMethod => Comparison?.Result.Method ?? "";
    public string ComparisonWhy => Comparison?.Why ?? "";
    public IReadOnlyList<TracePoint> ComparisonBeforeTrace => Comparison?.Reference.Trace ?? [];
    public IReadOnlyList<TracePoint> ComparisonAfterTrace => Comparison?.Current.Trace ?? [];
    /// <summary>Sem comparação: explica o que falta (primeira partida, nenhuma partida...).</summary>
    public string ComparisonEmptyText => _all.Count == 0
        ? Loc.T("Meas.LigueMedicaoAutomaticaJogue")
        : Loc.T("Meas.JogueMaisPartidaDeste");

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
    /// <summary>Uma partida guardada, pelo id (o Laboratório A/B compara as partidas do teste); null se foi apagada.</summary>
    public MeasuredSession? FindSession(string? id) => id is null ? null : _all.FirstOrDefault(s => s.Id == id);
    public bool HasGames => Games.Count > 0;

    public string ManualHeadline => _manual?.Headline ?? "";
    public string ManualMethod => _manual?.Method ?? "";
    public IReadOnlyList<ComparisonRow> ManualRows => _manual?.Metrics.Select(m => new ComparisonRow(m)).ToList() ?? [];
    public bool HasManual => _manual is { Metrics.Count: > 0 };

    // ------------------------------------------------------------------ preferências

    public bool AutoMeasure
    {
        get => _prefs.AutoMeasure;
        set { if (value != _prefs.AutoMeasure) SetPrefs(_prefs with { AutoMeasure = value }, value ? Loc.T("Meas.MedicaoAutomaticaLigadaProxima") : Loc.T("Meas.MedicaoAutomaticaDesligada")); }
    }

    /// <summary>O botão do mostrador (o atalho global faz o mesmo).</summary>
    public bool OverlayEnabled
    {
        get => _overlay.Enabled;
        set { if (value != _overlay.Enabled) { _overlay.SetEnabled(value); EnsureSystemLoad(); _ = EnsureCaptureForOverlayAsync(); } }
    }
    public string PreviewText => _overlay.Preview ? Loc.T("Meas.EsconderPrevia") : Loc.T("Meas.VerPreviaAgora");
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
    /// <summary>Há um PresentMon aprovado na conferência (hash conhecido ou assinatura da Intel).</summary>
    public bool HasPresentMon => _pm is { Usable: true } pm && File.Exists(pm.Path);
    public string PresentMonText => HasPresentMon
        ? _pm!.Source == PresentMonSource.Bundled
            ? Loc.F("Meas.PresentmonNAcompanhaNexus", PresentMonTrust.BundledVersion)
            : $"{Path.GetFileName(_pm.Path)} ({PresentMonCli.GenerationOf(_pm.Path) switch { PresentMonGeneration.V1 => Loc.T("Meas.Versao1X"), _ => Loc.T("Meas.Versao2X") }}, {_pm.SourceText})"
        : _pm is not null ? Loc.T("Meas.PresentmonRecusado") : Loc.T("Meas.PresentmonNaoEncontrado");
    /// <summary>Resultado da conferência (por que foi aceito ou recusado).</summary>
    public string PresentMonTrustText => _pm?.Verdict.Reason ?? "";
    public string PresentMonHelp { get; } =
        Loc.T("Meas.NexusUsaPresentmonFerramenta") +
        Loc.T("Meas.WindowsSemEntrarJogo") + PresentMonTrust.BundledVersion + Loc.T("Meas.JaVemNexusPasta") +
        Loc.T("Meas.NexusConfereArquivoOficial") +
        Loc.T("Meas.EleNaoExecutadoCopia");
    public bool CanTraceSilently { get; } = SafeCanTrace();
    public string RightsText => CanTraceSilently
        ? Loc.T("Meas.SuaContaPodeMedir")
        : Loc.T("Meas.SemPermissaoEspecialCada") +
          Loc.T("Meas.MedirSemPedirPermissao2") +
          Loc.T("Meas.PedeAvisoWindowsVez") +
          Loc.T("Meas.ContaIniciaremRastreamentosEventos");

    /// <summary>A conta já está no grupo, mas o acesso atual ainda não tem o grupo (falta sair e entrar de novo).</summary>
    public bool PerfLogPendingLogon => !CanTraceSilently && _perfLogMember == true;
    public bool ShowPerfLogOffer => OperatingSystem.IsWindows() && !CanTraceSilently && _perfLogMember != true;
    public bool CanUndoPerfLog => _perfLogRun is not null;
    public string PerfLogText => CanTraceSilently ? ""
        : _perfLogMember == true ? Loc.T("Meas.SuaContaJaEsta")
        : Loc.T("Meas.AvisoWindowsAgoraDepois");

    public string OverlayHelp { get; } =
        Loc.T("Meas.MostradorJanelaProprioNexus") +
        Loc.T("Meas.AnticheatsVanguardEasyAnti") +
        Loc.T("Meas.JogosJanelaTelaCheia");

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
    public RelayCommand JoinPerfLogCommand { get; }
    public RelayCommand UndoPerfLogCommand { get; }
    /// <summary>"Medir como administrador agora": recomeça a captura pedindo elevação ao PresentMon (--restart_as_admin) uma vez.</summary>
    public RelayCommand MeasureAsAdminCommand { get; }
    /// <summary>"Escolher o processo do jogo": PresentMon sem filtro por 3 s e a lista de quem está desenhando.</summary>
    public RelayCommand PickProcessCommand { get; }
    /// <summary>Medir o processo escolhido na lista (parâmetro: PID).</summary>
    public TextCommand UsePresenterCommand { get; }

    /// <summary>Processos que estavam desenhando quadros na última procura.</summary>
    public ObservableCollection<PresenterRow> Presenters { get; } = [];
    public string PresentersText { get => _presentersText; private set => Set(ref _presentersText, value); }

    /// <summary>A captura parou por falta de permissão (ou a procura de processos): mostrar "Liberar medição" e "Medir como administrador".</summary>
    public bool ShowPermissionActions => !CanTraceSilently && (_probeNoPermission || _engine?.Capture?.Status.State == LiveCaptureState.NoPermission);

    /// <summary>Um jogo acabou de abrir (nome do jogo). A janela usa para o aviso de notebook na bateria (checagens do PC).</summary>
    public event EventHandler<string>? GameStarted;

    /// <summary>Uma partida medida acabou de ser guardada (automática ou à mão). O Laboratório A/B oferece cada uma ao teste em andamento.</summary>
    public event EventHandler<MeasuredSession>? SessionSaved;

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
            _engine = new AutoMeasureEngine(_probe, new PresentMonLiveStarter(() => _prefs.PresentMonPath,
                () => (_prefs.RequestElevation || _elevateOnce) && !CanTraceSilently,
                verify: p => PresentMonTrust.Verify(p, _trust), diag: Diag), activity: StartActivity);
            _ = RefreshPerfLogAsync();
            _pollTimer.Start();
            RegisterHotkey();
            _ = Task.Run(CleanOldTempFiles); // varre %TEMP% (pode ter dezenas de milhares de arquivos): fora da interface
            _ = ReadLibraryAsync(force: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = Loc.F("Meas.NaoFoiPossivelIniciar", ex.Message);
        }
    }

    /// <summary>"Por que travou?": durante a partida, quem mais usou CPU além do jogo e quanto o jogo leu (a cada 2 s, só leitura).</summary>
    private static CaptureActivitySampler? StartActivity(LiveSession session) =>
        OperatingSystem.IsWindows() ? new CaptureActivitySampler(new WindowsActivitySource(), session).Start() : null;

    /// <summary>Teste "Atraso de drivers" (contadores sem administrador + investigação como administrador pelo aplicador).</summary>
    public DriverLatencyPanel DriverLatency { get; }

    /// <summary>Uma linha de "Por que travou?" da última partida medida deste jogo (para a tela Jogos), ou vazio.</summary>
    public string LatestStutterVerdict(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return "";
        var key = GameKey.Of(exe);
        var last = _all.Where(x => x.Game == key && x.Stutters is not null).MaxBy(x => x.CapturedAt);
        return last is null ? "" : StutterWords.Verdict(last.Stutters);
    }

    /// <summary>FPS médio da última partida medida deste jogo (para o estado do cartão em Jogos), ou null sem medição.</summary>
    public double? LatestAverageFps(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var key = GameKey.Of(exe);
        var last = _all.Where(x => x.Game == key).MaxBy(x => x.CapturedAt);
        return last is null || last.Summary.AverageFps <= 0 ? null : last.Summary.AverageFps;
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
                    GameStarted?.Invoke(this, (ev.Game ?? game)?.Name ?? "");
                    _overlay.OnGame(true, _prefs.OverlayOnGameStart);
                    EnsureSystemLoad();
                    if (ev.CaptureStarted) OnCaptureStarted(ev.Game!);
                    else if (ev.Note is not null) CaptureNote = ev.Note;
                    if (!ev.CaptureStarted) await EnsureCaptureForOverlayAsync();
                    break;
                case GameEventKind.Stopped:
                    _overlay.OnGame(false, false);
                    if (ev.Finished is { } finished) await SaveAsync(finished);
                    break;
            }
            RefreshCaptureStatus();
            UpdateHero();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = Loc.F("Meas.FalhaAcompanharJogoN", ex.Message);
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
            ? Loc.F("Meas.LinhaCianoPartidaN", r.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt), r.Summary.AverageFps.ToString("0", Pt), r.Summary.OnePercentLowAverageFps.ToString("0", Pt))
            : Loc.T("Meas.PrimeiraPartidaMedidaDeste");
        CaptureNote = RequestElevation && !CanTraceSilently ? Loc.T("Meas.WindowsPedirPermissaoPresentmon") : "";
        _liveTimer.Start();
        Status = Loc.F("Meas.MedindoN", game.Name);
        Notify();
    }

    /// <summary>
    /// O mostrador precisa dos quadros: com ele ligado e um jogo aberto, mede mesmo com a medição automática desligada (a
    /// partida também é guardada, como "ao vivo").
    /// </summary>
    private async Task EnsureCaptureForOverlayAsync()
    {
        if (_engine is null || !_overlay.Enabled || _detected is not { } game || IsCapturing || !HasPresentMon) return;
        var ok = await Task.Run(() => { lock (_engineGate) return _engine.StartManual(game, DateTimeOffset.Now); });
        if (ok) OnCaptureStarted(game);
    }

    private async Task StartManualAsync()
    {
        if (_engine is null) return;
        var game = _detected ?? new DetectedGame(GameMatcher.Pretty(ManualExe.Trim()), ManualExe.Trim(), 0, DetectionReason.UserList, Loc.T("Meas.Mao"));
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
        HeroFps = double.IsFinite(r.CurrentFps) ? r.CurrentFps : double.NaN;
        HeroKicker = Loc.F("Meas.MedindoAgoraN", TimeSpan.FromSeconds(r.ElapsedSeconds).ToString(r.ElapsedSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", Pt));
        LiveLow1 = Num(r.Low1Fps, "0");
        LiveFrametime = Num(r.FrametimeMs, "0.0");
        LiveAverage = Num(r.AverageFps, "0");
        LiveClock = TimeSpan.FromSeconds(r.ElapsedSeconds).ToString(r.ElapsedSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", Pt);
        LiveDelta = LiveComparison.Delta(r, _liveReference)?.Text ?? (_liveReference is null ? "" : Loc.T("Meas.DiferencaApareceDepois30"));
        var trace = session.Trace();
        var end = r.ElapsedSeconds;
        var from = Math.Max(0, end - 60);
        LiveTrace = trace.Where(p => p.Seconds >= from).Select(p => new TracePoint(p.Seconds - from, p.FrametimeMs)).ToList();
        ReferenceTrace = LiveComparison.ReferenceWindow(_liveReference, from, end).Select(p => new TracePoint(p.Seconds - from, p.FrametimeMs)).ToList();
        _overlay.Update(r, _load, DescribeCapture().Overlay);
        if (DateTimeOffset.Now - _systemReadAt >= TimeSpan.FromSeconds(1)) _ = ReadSystemLoadAsync();
    }

    /// <summary>Partida terminou: guarda (se durou o mínimo), marca com jogo + condições + mudanças ativas e compara.</summary>
    private async Task SaveAsync(LiveSession finished)
    {
        _liveTimer.Stop();
        ClearLive();
        if (finished.Seconds < _prefs.MinSessionSeconds || finished.Frames < 100)
        {
            Status = Loc.F("Meas.PartidaNCurtaDemais", finished.Game.Name, finished.Seconds, _prefs.MinSessionSeconds);
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
                        finished.Automatic ? Loc.T("Meas.Automatico") : Loc.T("Shell.Vivo"), finished.StartedAt);
                    StutterReport? stutters = s.Stutters;
                    try { stutters = StutterAnalyzer.Analyze(finished.StutterFrames(), finished.Activity()); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // fica a análise sem atividade
                    s = s with
                    {
                        Stutters = stutters,
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
            SessionSaved?.Invoke(this, saved);
            Status = Comparison is { } c && c.Current.Id == saved.Id
                ? Loc.F("Meas.PartidaNGuardadaAntes", saved.DisplayName, c.Verdict)
                : Loc.F("Meas.PartidaNGuardadaN", saved.DisplayName, saved.Summary.AverageFps, saved.Summary.OnePercentLowAverageFps);
            Host?.Notify(Status);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = Loc.F("Meas.PartidaTerminouMasNao", ex.Message);
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
            var measuring = _engine?.Session is { } s ? Loc.F("Meas.MedindoHaN", LiveClockOr(s)) : AutoMeasure ? "" : Loc.T("Meas.MedicaoAutomaticaDesligada2");
            HeroDetail = Loc.F("Meas.AbertoAgoraNN", g.ExeName, g.ReasonText, measuring);
        }
        else if (_engine?.Session is { } s)
        {
            HeroTitle = s.Game.Name;
            HeroDetail = Loc.F("Meas.MedindoNIniciadoMao", s.Game.ExeName);
        }
        else
        {
            var last = _all.MaxBy(x => x.CapturedAt);
            HeroTitle = Loc.T("Meas.NenhumJogoAberto");
            HeroDetail = last is null
                ? (AutoMeasure ? Loc.T("Meas.AbraJogoNexusComeca") : Loc.T("Meas.LigueMedirAutomaticamenteAbra"))
                : Loc.F("Meas.UltimoJogoMedidoN", last.DisplayName, last.CapturedAt.ToLocalTime().ToString(Loc.T("Meas.DdMmHhMm"), Pt));
        }
        UpdateHeroNumbers();
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsCapturing));
        OnPropertyChanged(nameof(ShowIntro));
        OnPropertyChanged(nameof(ComparisonEmptyAction));
    }

    /// <summary>Destaque: o que está aberto/medindo e o número gigante. Só apresentação; a captura não muda.</summary>
    private void UpdateHeroNumbers()
    {
        var capturing = _engine?.Session;
        var last = _all.MaxBy(x => x.CapturedAt);
        if (capturing is not null)
        {
            HeroGame = _detected?.Name ?? capturing.Game.Name;
            if (!HeroKicker.StartsWith("Medindo", StringComparison.Ordinal)) HeroKicker = Loc.T("Meas.MedindoAgora");
            HeroFpsLabel = Loc.T("Meas.FpsAgora");
            return;
        }
        if (_detected is { } g)
        {
            var previous = _all.Where(x => x.Game == GameKey.Of(g.ExeName)).MaxBy(x => x.CapturedAt);
            HeroKicker = Loc.T("Meas.AbertoAgora");
            HeroGame = g.Name;
            HeroFps = previous?.Summary.AverageFps ?? double.NaN;
            HeroFpsLabel = previous is null ? Loc.T("Meas.PrimeiraPartidaDesteJogo") : Loc.T("Meas.FpsMedioUltimaPartida");
            return;
        }
        HeroKicker = last is null ? Loc.T("Meas.NenhumJogoAberto") : Loc.T("Meas.UltimoJogoMedido");
        HeroGame = last?.DisplayName ?? Loc.T("Meas.ProntoMedir");
        HeroFps = last?.Summary.AverageFps ?? double.NaN;
        HeroFpsLabel = last is null ? Loc.T("Meas.AbraJogoVerFps") : Loc.T("Meas.FpsMedioUltimaPartida2");
    }

    private static string LiveClockOr(LiveSession s) => TimeSpan.FromSeconds(s.Seconds).ToString(s.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private void Notify()
    {
        UpdateHero();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                                    }

    // ------------------------------------------------------------------ estado da captura (verdadeiro, nunca "aguardando" calado)

    /// <summary>Linha para o suporte em %LocalAppData%\NEXUS\desktop-diag.log (linha de comando, erros e código de saída do PresentMon).</summary>
    private static void Diag(string text) => Nexus.Desktop.Services.Shell.UiWatchdog.Note("medir: " + text);

    /// <summary>
    /// O que dizer agora: no mostrador (curto) e na página (o que fazer). Procurando o jogo, aguardando a permissão do Windows, sem
    /// permissão, jogo sem desenhar (minimizado?), medindo X, PresentMon parou — e, depois de 15 s sem quadros, a explicação.
    /// </summary>
    private (string Overlay, string Page) DescribeCapture()
    {
        if (_engine?.Session is not { } session || _engine.Capture is not { } capture)
            return (_detected is not null && !HasPresentMon ? Loc.T("Meas.PresentmonNaoEncontrado") : Loc.T("Meas.Live.Procurando"), "");
        LiveCaptureStatus st;
        try { st = capture.Status; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (Loc.T("Meas.Live.Procurando"), ""); }
        var target = st.Target ?? session.Game.ExeName;
        var hint = st.TimedOut ? "\n" + Loc.T("Meas.Live.DicaTimeout") : "";
        return st.State switch
        {
            LiveCaptureState.Measuring => (Loc.F("Meas.Live.MedindoN", target),
                st.Fallback || !target.Equals(session.Game.ExeName, StringComparison.OrdinalIgnoreCase) ? Loc.F("Meas.Live.MedindoOutroN", target) : ""),
            LiveCaptureState.WaitingPermission => (Loc.T("Meas.Live.AguardandoPermissao") + hint,
                st.TimedOut ? Loc.T("Meas.Live.TimeoutPermissao") : Loc.T("Meas.WindowsPedirPermissaoPresentmon")),
            LiveCaptureState.NotDrawing => (Loc.T("Meas.Live.NaoDesenhando") + hint, st.TimedOut ? Loc.T("Meas.Live.TimeoutNaoDesenhando") : ""),
            LiveCaptureState.NoPermission => (Loc.T("Meas.Live.SemPermissao"),
                (st.Failure == PresentMonFailure.ElevationCancelled ? Loc.T("Meas.Live.AvisoRecusado") + " " : "") + Loc.T("Meas.Live.SemPermissaoTexto")),
            LiveCaptureState.Failed => (Loc.T("Meas.Live.Falhou") + hint,
                Loc.T("Meas.PresentmonParouSemEnviar") + Shorten(st.Detail ?? capture.Log)),
            LiveCaptureState.Finished => ("", ""),
            _ => (Loc.T("Meas.Live.Procurando") + hint, st.TimedOut ? Loc.T("Meas.Live.TimeoutProcurando") : ""),
        };
    }

    /// <summary>A cada leitura: nota da página, mostrador (também sem quadros) e diagnóstico quando o estado muda.</summary>
    private void RefreshCaptureStatus()
    {
        var (overlay, page) = DescribeCapture();
        if (_engine?.Session is not null) CaptureNote = page;
        if (_engine?.Session is null || _engine.Session.Frames == 0) _overlay.Update(LiveReading.Empty, _load, overlay);
        var state = _engine?.Capture?.Status.State;
        if (state != _lastLoggedState && state is not null)
        {
            _lastLoggedState = state;
            Diag($"estado: {state} ({overlay.Replace('\n', ' ')})");
        }
        OnPropertyChanged(nameof(ShowPermissionActions));
    }

    /// <summary>Recomeça a medição pedindo elevação ao PresentMon (o aviso do Windows aparece agora, com o NEXUS na frente).</summary>
    private async Task MeasureAsAdminAsync()
    {
        if (_engine is null) return;
        var game = _engine.Session?.Game ?? _detected;
        if (game is null) return;
        Diag("medir como administrador (--restart_as_admin) pedido pelo usuário");
        _probeNoPermission = false;
        _elevateOnce = true;
        try
        {
            var ok = await Task.Run(() =>
            {
                lock (_engineGate)
                {
                    _engine.StopCapture();
                    return _engine.StartManual(game, DateTimeOffset.Now);
                }
            });
            if (ok) OnCaptureStarted(game);
        }
        finally { _elevateOnce = false; }
        RefreshCaptureStatus();
    }

    /// <summary>"Quem está desenhando agora?" (3 s de PresentMon sem filtro, sessão própria) para o usuário escolher o jogo.</summary>
    private async Task ProbePresentersAsync()
    {
        if (_pm is not { Usable: true } pm || _probing) return;
        _probing = true;
        Presenters.Clear();
        PresentersText = Loc.T("Meas.Live.ProcurandoQuemDesenha");
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            var exe = pm.Path;
            var elevate = !CanTraceSilently;
            var names = _engine?.LastSnapshot?.Processes ?? [];
            var r = await Task.Run(async () =>
            {
                var verdict = PresentMonTrust.Verify(exe, _trust);
                if (!verdict.Trusted) return new PresenterProbeResult([], PresentMonFailure.NotStarted, verdict.Reason);
                return await PresenterProbe.RunAsync(exe, elevate).ConfigureAwait(false);
            });
            Diag($"procura de quem desenha: {r.Presenters.Count} processos, {r.Failure}{(r.Log.Length > 0 ? " — " + Shorten(r.Log) : "")}");
            _probeNoPermission = PresentMonDiagnosis.IsPermission(r.Failure);
            foreach (var p in r.Presenters.Where(p => !LiveTargetPicker.NeverTarget.Contains(p.Name)).Take(12))
            {
                var name = p.UnknownName ? names.FirstOrDefault(x => x.Pid == p.Pid)?.Name ?? p.Name : p.Name;
                Presenters.Add(new PresenterRow(p.Pid, name, p.Fps));
            }
            PresentersText = _probeNoPermission ? Loc.T("Meas.Live.SemPermissaoTexto")
                : r.Failure is PresentMonFailure.None ? (Presenters.Count > 0 ? Loc.T("Meas.Live.EscolhaProcesso") : Loc.T("Meas.Live.NinguemDesenhando"))
                : Loc.T("Meas.PresentmonParouSemEnviar") + Shorten(r.Log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PresentersText = Loc.T("Meas.PresentmonParouSemEnviar") + ex.Message;
        }
        finally
        {
            _probing = false;
            OnPropertyChanged(nameof(ShowPermissionActions));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Mede o processo escolhido na lista (travado no PID: nada de procurar outro).</summary>
    private async Task UsePresenterAsync(string pidText)
    {
        if (_engine is null || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return;
        if (Presenters.FirstOrDefault(p => p.Pid == pid) is not { } row) return;
        var exe = row.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || row.Name.StartsWith('<') ? row.Name : row.Name + ".exe";
        var game = new DetectedGame(GameMatcher.Pretty(exe), exe, pid, DetectionReason.UserList, Loc.T("Meas.Live.ProcessoUsuario"));
        Diag($"usuário escolheu {exe} (PID {pid}) entre os que desenham");
        var (finished, ok) = await Task.Run(() =>
        {
            lock (_engineGate)
            {
                var f = _engine.StopCapture();
                return (f, _engine.StartManual(game, DateTimeOffset.Now, lockPid: true));
            }
        });
        if (finished is { Frames: > 0 }) await SaveAsync(finished);
        Presenters.Clear();
        PresentersText = "";
        if (ok) OnCaptureStarted(game);
        RefreshCaptureStatus();
    }

    // ------------------------------------------------------------------ mostrador

    private void RegisterHotkey()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!_prefs.HotkeyEnabled) { _hotkey?.Unregister(); HotkeyStatus = Loc.T("Meas.AtalhoDesligado"); return; }
        try
        {
            if (_hotkey is null)
            {
                _hotkey = new GlobalHotkey();
                _hotkey.Pressed += () => { _overlay.Toggle(); EnsureSystemLoad(); _ = EnsureCaptureForOverlayAsync(); Host?.Notify(_overlay.Enabled ? Loc.T("Meas.MostradorFpsLigado") : Loc.T("Meas.MostradorFpsDesligado")); };
            }
            var key = Hotkey.Parse(_prefs.OverlayHotkey);
            HotkeyStatus = _hotkey.Register(key)
                ? Loc.F("Meas.NLigaDesligaMostrador", key)
                : Loc.F("Meas.NJaUsadoOutro", key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { HotkeyStatus = Loc.F("Meas.NaoFoiPossivelRegistrar", ex.Message); }
    }

    private void ApplyHotkey()
    {
        if (!Hotkey.TryParse(HotkeyText, out var key))
        {
            HotkeyStatus = Loc.T("Meas.UseCtrlAltOu");
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

    private void Reload(string? selectGame = null) => Reload(_store.List(), selectGame);

    private void Reload((IReadOnlyList<MeasuredSession> Sessions, int Unreadable) stored, string? selectGame = null)
    {
        var (list, bad) = stored;
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
                        if (bad > 0) Status = Loc.F("Meas.NArquivoSSessao", bad, _store.Folder);
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
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Comparison = null; Status = Loc.F("Meas.NaoDeuCompararN", ex.Message); }
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
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("Meas.NaoDeuLerDiario", ex.Message); }
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
        InviteText = Loc.F("Meas.VoceAplicouNDepois", (n == 1 ? $"\"{latestChange.Title}\"" : Loc.F("Meas.NMudancas", n)), lastSession.DisplayName) +
                     Loc.T("Meas.JoguePartida2Minutos") + (AutoMeasure ? "" : Loc.T("Meas.LigueMedicaoAutomaticaNao"));
    }

    private async Task ReadLibraryAsync(bool force)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!force && DateTimeOffset.Now - _libraryReadAt < TimeSpan.FromMinutes(10)) return;
        _libraryReadAt = DateTimeOffset.Now;
        LibraryText = Loc.T("Meas.LendoBibliotecasJogos");
        try
        {
            var notes = new List<string>();
            var games = await Task.Run(() => GameLibrary.Scan(WindowsGameRoots.Read(), notes));
            _library = games;
            var byStore = games.GroupBy(g => g.Store).Select(g => $"{g.Key} {g.Count()}");
            LibraryText = games.Count == 0
                ? Loc.T("Meas.NenhumaBibliotecaEncontradaSteam")
                : Loc.F("Meas.NJogoSNas", games.Count, string.Join(", ", byStore)) + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LibraryText = Loc.F("Meas.NaoDeuLerBibliotecas", ex.Message); }
    }

    private static string? GpuDriver(SystemFacts? facts) =>
        facts?.Gpus.Where(g => g.Vendor is not null and not "Microsoft").OrderByDescending(g => g.MemoryBytes ?? 0).Select(g => g.DriverVersion).FirstOrDefault(v => v is not null);

    // ------------------------------------------------------------------ preferências

    private void SetPrefs(MeasurePreferences prefs, string? message)
    {
        _prefs = prefs.Normalize();
        if (!_prefs.Save(_prefsPath)) Status = Loc.T("Meas.NaoDeuSalvarPreferencias") + _prefsPath;
        else if (message is not null) Status = message;
        foreach (var p in new[] { nameof(AutoMeasure), nameof(OverlayOnGameStart), nameof(OverlayShowSystem), nameof(OverlayCornerValue), nameof(HotkeyEnabled),
                     nameof(DetectFullscreen), nameof(RequestElevation), nameof(MinSessionSeconds), nameof(KeepDays), nameof(UserGames), nameof(ExcludedGames),
                     nameof(PresentMonPath), nameof(HasPresentMon), nameof(PresentMonText), nameof(PresentMonTrustText) })
            OnPropertyChanged(p);
                        Notify();
    }

    private void AddGame()
    {
        var exe = NewGameExe.Trim();
        if (!PresentMonCli.IsValidExeName(exe)) return;
        SetPrefs(_prefs.WithUserGame(exe), Loc.F("Meas.NEntrouSuaLista", exe));
        NewGameExe = "";
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Meas.OndeEstaPresentmon"), Filter = Loc.T("Meas.PresentmonPresentmonExePresentmon") };
        if (dialog.ShowDialog() != true) return;
        RelocatePresentMon(dialog.FileName);
        SetPrefs(_prefs, _pm is { Usable: true, Source: PresentMonSource.Bundled } bundled && !string.Equals(bundled.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase)
            ? Loc.T("Meas.NexusContinuaUsandoPresentmon")
            : PresentMonStatus());
    }

    private void RescanPresentMon()
    {
        RelocatePresentMon(_prefs.PresentMonPath);
        SetPrefs(_prefs, PresentMonStatus());
    }

    /// <summary>Procura (embutido primeiro) e confere o PresentMon. Só leitura: hash e assinatura do arquivo.</summary>
    private void RelocatePresentMon(string? configured)
    {
        try { _pm = PresentMonLocator.Locate(configured, AppContext.BaseDirectory, _trust); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _pm = null; _status = Loc.T("Meas.NaoDeuConferirPresentmon") + ex.Message; }
        _prefs = _prefs with { PresentMonPath = _pm?.Path ?? configured };
    }

    private string PresentMonStatus() => _pm switch
    {
        null => Loc.T("Meas.PresentmonNaoEncontradoNas"),
        { Usable: true } pm => Loc.F("Meas.PresentmonProntoNN", pm.SourceText, pm.Verdict.Reason),
        var pm => pm.Verdict.Reason,
    };

    // ------------------------------------------------------------------ grupo "Usuários do log de desempenho" (ação de catálogo)

    private const string PerfLogActionId = "measure.perflog-group.join@1";

    private async Task RefreshPerfLogAsync()
    {
        if (!OperatingSystem.IsWindows() || CanTraceSilently) return;
        var sid = Host?.UserSid ?? WindowsLocalGroupBackend.CurrentUserSid();
        _perfLogMember = sid is null ? null : await Task.Run(() => WindowsLocalGroupBackend.IsMemberOfPerfLogUsers(sid));
        foreach (var p in new[] { nameof(PerfLogPendingLogon), nameof(ShowPerfLogOffer), nameof(PerfLogText), nameof(CanUndoPerfLog) }) OnPropertyChanged(p);
    }

    private async Task JoinPerfLogAsync()
    {
        if (Host is null) return;
        try
        {
            Busy = true;
            var r = await Host.ApplyAsync(Loc.T("Meas.MedirSemPedirPermissao"),
                Loc.T("Meas.NexusVaiColocarSua") +
                Loc.T("Meas.DepoisDissoPresentmonMede") +
                Loc.T("Meas.MudaProgramasSuaConta") +
                Loc.T("Meas.NaoPrivilegioAdministradorDesfazer"),
                [new ActionRequest(PerfLogActionId)], BrokerLevel.Admin);
            if (r is null) { Status = Loc.T("Shell.CanceladoNadaFoiAlterado"); return; }
            if (r.Response is { Ok: true, RunId: { } run })
            {
                _perfLogRun = run;
                Status = Loc.T("Meas.ProntoSuaContaEsta");
            }
            else Status = r.Message;
        }
        finally
        {
            Busy = false;
            await RefreshPerfLogAsync();
        }
    }

    private async Task UndoPerfLogAsync()
    {
        if (Host is null || _perfLogRun is not { } run) return;
        try
        {
            Busy = true;
            var r = await Host.RevertAsync(run, BrokerLevel.Admin);
            if (r is null) return;
            if (r.Ok) { _perfLogRun = null; Status = Loc.T("Meas.DesfeitoSuaContaSaiu"); }
            else Status = r.Message;
        }
        finally
        {
            Busy = false;
            await RefreshPerfLogAsync();
        }
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
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Meas.ImportarCapturaCsvPresentmon"), Filter = Loc.T("Clean.CsvCsvCsv"), Multiselect = true };
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
        Status = Loc.F("Meas.NSessaoOesImportada", ok) + (errors.Count > 0 ? Loc.T("Meas.NaoImportadas") + string.Join(" · ", errors) : "");
    }

    private void Delete()
    {
        if (Selected is not { } row) return;
        if (Host?.Confirm(Loc.T("Meas.ApagarSessao"), Loc.F("Meas.ApagarSessaoNArquivo", row.Name)) != true) return;
        _store.Delete(row.Session);
        Reload();
        Status = Loc.F("Meas.SessaoNApagada", row.Name);
    }

    private void ToggleExclude()
    {
        if (Selected is not { } row) return;
        var updated = row.Session with { Excluded = !row.Session.Excluded };
        _store.Save(updated);
        Reload();
        Status = updated.Excluded ? Loc.T("Meas.EstaPartidaNaoEntra") : Loc.T("Meas.EstaPartidaVoltouEntrar");
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
            Title = Loc.T("Meas.ExportarRelatorioAntesX"), Filter = Loc.T("Meas.TextoTxtTxt"),
            FileName = $"nexus-antes-depois-{c.Current.Game.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)}-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SessionLibrary.Report(c), new System.Text.UTF8Encoding(true));
        Status = Loc.F("Meas.RelatorioSalvoN", dialog.FileName);
    }

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Meas.ExportarSessoesCsv"), Filter = Loc.T("Clean.CsvCsvCsv"), FileName = $"nexus-sessoes-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SessionLibrary.SummaryCsv(_all), new System.Text.UTF8Encoding(true));
        Status = Loc.F("Meas.NSessaoOesExportada", _all.Count, dialog.FileName);
    }

    private void Cleanup()
    {
        if (KeepDays <= 0) return;
        var limit = DateTimeOffset.Now.AddDays(-KeepDays);
        var count = _all.Count(s => s.CapturedAt < limit && s.Tag == SessionTag.None);
        if (count == 0) { Status = Loc.F("Meas.NenhumaSessaoMaisN", KeepDays); return; }
        if (Host?.Confirm(Loc.T("Meas.LimparSessoesAntigas"), Loc.F("Meas.ApagarAteNSessao", count, KeepDays)) != true) return;
        var removed = _store.Cleanup(limit);
        Reload();
        Status = Loc.F("Meas.NSessaoOesAntiga", removed);
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { _store.Folder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { Status = Loc.F("Meas.NaoDeuAbrirPasta", ex.Message); }
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
