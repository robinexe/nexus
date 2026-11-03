using System.Diagnostics;
using System.Globalization;
using System.IO;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.Desktop.Services.Shell;
using Nexus.WindowsActions.Registry;

namespace Nexus.Desktop.ViewModels;

/// <summary>Editor de uma tarefa agendada (verificação ou limpeza segura) na página Ajustes.</summary>
public sealed class ScheduleEditor : ObservableObject
{
    private readonly SettingsPage _page;
    private int _frequency = 1;
    private int _day;
    private int _monthDay = 1;
    private string _hour = "19";
    private string _minute = "00";
    private bool _skipOnBattery;
    private bool _skipWhenGaming;
    private string _status = Loc.T("Settings.LendoAgendador");
    private bool _isOn;

    internal ScheduleEditor(SettingsPage page, NexusTaskKind kind, bool defaultConditions)
    {
        _page = page;
        Kind = kind;
        _skipOnBattery = _skipWhenGaming = defaultConditions;
        SaveCommand = new RelayCommand(() => _ = _page.SaveScheduleAsync(this), () => !_page.Busy && _page.Host is not null);
        TurnOffCommand = new RelayCommand(() => _ = _page.TurnOffScheduleAsync(this), () => !_page.Busy && _page.Host is not null && IsOn);
    }

    public NexusTaskKind Kind { get; }
    public string Title => Kind.Title;
    public string Explanation => Kind.Explanation;

    public static IReadOnlyList<string> Frequencies { get; } = [Loc.T("Settings.TodoDia"), Loc.T("Settings.TodaSemana"), Loc.T("Settings.TodoMes")];
    public static IReadOnlyList<string> Days { get; } = [Loc.T("Settings.Domingo"), Loc.T("Settings.Segunda"), Loc.T("Settings.Terca"), Loc.T("Settings.Quarta"), Loc.T("Settings.Quinta"), Loc.T("Settings.Sexta"), Loc.T("Settings.Sabado")];
    public static IReadOnlyList<int> MonthDays { get; } = Enumerable.Range(1, 28).ToList();
    public static IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.InvariantCulture)).ToList();
    public static IReadOnlyList<string> Minutes { get; } = ["00", "15", "30", "45"];

    public int Frequency
    {
        get => _frequency;
        set { if (Set(ref _frequency, value)) { OnPropertyChanged(nameof(IsWeekly)); OnPropertyChanged(nameof(IsMonthly)); } }
    }
    public bool IsWeekly => _frequency == 1;
    public bool IsMonthly => _frequency == 2;
    public int Day { get => _day; set => Set(ref _day, value); }
    public int MonthDay { get => _monthDay; set => Set(ref _monthDay, value); }
    public string Hour { get => _hour; set => Set(ref _hour, value); }
    public string Minute { get => _minute; set => Set(ref _minute, value); }
    public bool SkipOnBattery { get => _skipOnBattery; set => Set(ref _skipOnBattery, value); }
    public bool SkipWhenGaming { get => _skipWhenGaming; set => Set(ref _skipWhenGaming, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsOn
    {
        get => _isOn;
        private set
        {
            if (!Set(ref _isOn, value)) return;
            OnPropertyChanged(nameof(SaveText));
            OnPropertyChanged(nameof(Tone));
            OnPropertyChanged(nameof(StateText));
            _page.RaiseSchedules();
        }
    }
    public string SaveText => IsOn ? Loc.T("Settings.SalvarHorario") : Loc.T("Settings.Ligar");

    // ---- apresentação v2
    public Views.Controls.Tone Tone => IsOn ? Views.Controls.Tone.Good : Views.Controls.Tone.Neutral;
    public string StateText => IsOn ? Loc.T("Settings.Ligada") : Loc.T("Settings.Desligada");
    public string IconKey => Kind == NexusTaskKind.Clean ? "Icon.Clean" : "Icon.Search";
    public bool IsClean => Kind == NexusTaskKind.Clean;

    public RelayCommand SaveCommand { get; }
    public RelayCommand TurnOffCommand { get; }

    /// <summary>Horário escolhido (sempre válido: as listas só oferecem valores permitidos).</summary>
    public TaskSchedule Schedule
    {
        get
        {
            var h = int.Parse(Hour, CultureInfo.InvariantCulture);
            var m = int.Parse(Minute, CultureInfo.InvariantCulture);
            return Frequency switch
            {
                0 => TaskSchedule.Daily(h, m),
                2 => TaskSchedule.Monthly(Math.Clamp(MonthDay, 1, 28), h, m),
                _ => TaskSchedule.Weekly((DayOfWeek)Math.Clamp(Day, 0, 6), h, m),
            };
        }
    }

    public TaskConditions Conditions => new(SkipOnBattery, SkipWhenGaming);

    internal void Show(NexusTaskStatus status)
    {
        Status = status.Describe();
        IsOn = status.IsOn;
        if (status.Schedule is { } s)
        {
            Frequency = (int)s.Frequency;
            Day = (int)s.Day;
            MonthDay = s.DayOfMonth;
            Hour = s.Hour.ToString("00", CultureInfo.InvariantCulture);
            Minute = Minutes.Contains(s.Minute.ToString("00", CultureInfo.InvariantCulture)) ? s.Minute.ToString("00", CultureInfo.InvariantCulture) : "00";
        }
        if (status.Conditions is { } c)
        {
            SkipOnBattery = c.SkipOnBattery;
            SkipWhenGaming = c.SkipWhenGaming;
        }
    }
}

/// <summary>Licença de uma dependência redistribuída (Sobre).</summary>
public sealed record DependencyLicense(string Name, string License, string Note);

/// <summary>
/// Ajustes: agendamentos (verificação somente leitura e limpeza segura opt-in), notificações, bandeja, iniciar com o Windows,
/// pastas de dados e relatórios, e sobre. Preferências da interface vão para settings.json; o que muda o sistema (tarefas e
/// iniciar com o Windows) vai pelo aplicador do usuário (Host.ApplyAsync), com diário e desfazer.
/// </summary>
public sealed class SettingsPage : ModulePage
{
    public override string Title => Loc.T("Settings.Ajustes");
    public override string Group => Loc.T("Nav.Registro");
    public override string Subtitle => Loc.T("Settings.AgendamentosAtualizacoesNotificacoes");

    private CatalogDocument? _catalog;
    private string _legacyText = "";
    private bool _legacyOn;
    private string _startText = Loc.T("Settings.Lendo");
    private bool _startOn;
    private string _cleaningRules = "";
    private string _cleaningExcluded = "";
    private string _catalogText = "";
    private string _settingsProblem = "";
    private string _lastCleaning = "";

    public SettingsPage()
    {
        Scan = new ScheduleEditor(this, NexusTaskKind.Scan, defaultConditions: false);
        Clean = new ScheduleEditor(this, NexusTaskKind.Clean, defaultConditions: true);
        LegacyOffCommand = new RelayCommand(() => _ = LegacyOffAsync(), () => !Busy && Host is not null && _legacyOn);
        ToggleStartCommand = new RelayCommand(() => _ = ToggleStartAsync(), () => !Busy && Host is not null);
        OpenDataCommand = new RelayCommand(() => OpenFolder(DataDirectory));
        OpenReportsCommand = new RelayCommand(() => OpenFolder(ScanReport.DefaultDirectory));
        OpenLastCleaningCommand = new RelayCommand(OpenLastCleaning, () => LastCleaningReport() is not null);
        RefreshCommand = new RelayCommand(() => _ = RunAsync(RefreshAsync), () => !Busy);
        OpenTermsCommand = new RelayCommand(() => OpenLegal("TERMOS-DE-USO"));
        OpenPrivacyCommand = new RelayCommand(() => OpenLegal("PRIVACIDADE"));
        RestartForLanguageCommand = new RelayCommand(RestartForLanguage, () => LanguageRestartNeeded);
        var windows = LanguageOption.FromCulture(System.Globalization.CultureInfo.CurrentUICulture);
        LanguageChoices =
        [
            new(null, "", windows),
            .. LanguageOption.Choices.Select(c => new LanguageChoice(c.Code, c.NativeName, c.Code)),
        ];
    }

    // ----- idioma (settings.json; vale a partir do próximo início: os textos são carregados uma vez, ao abrir)

    /// <summary>Opção da lista de idiomas: código gravado (null = igual ao Windows), nome mostrado e idioma que resulta.</summary>
    public sealed record LanguageChoice(string? Code, string Name, string Effective)
    {
        /// <summary>"Igual ao Windows" no idioma atual, com o idioma que o Windows dá entre parênteses.</summary>
        public string Display => Code is null
            ? Loc.F("Settings.IgualWindowsN", LanguageOption.Choices.First(c => c.Code == Effective).NativeName)
            : Name;
    }

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    public LanguageChoice SelectedLanguage
    {
        get => LanguageChoices.FirstOrDefault(c => c.Code == LanguageOption.Normalize(UiSettings.Current.Language)) ?? LanguageChoices[0];
        set
        {
            if (value is null) return;
            SettingsProblem = UiSettings.Update(s => s with { Language = value.Code }) ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(LanguageRestartNeeded));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>O idioma escolhido é diferente do que está na tela: mostra "Reiniciar agora".</summary>
    public bool LanguageRestartNeeded => SelectedLanguage.Effective != Loc.Language;

    public RelayCommand RestartForLanguageCommand { get; }

    private void RestartForLanguage()
    {
        if (AppUpdates.IsAppBusy()) { SettingsProblem = Loc.T("Shell.TermineOperacaoAndamentoAplicar"); return; }
        if (!App.Relaunch()) SettingsProblem = Loc.T("Settings.NaoDeuReabrir");
    }

    public ScheduleEditor Scan { get; }
    public ScheduleEditor Clean { get; }
    public RelayCommand LegacyOffCommand { get; }
    public RelayCommand ToggleStartCommand { get; }
    public RelayCommand OpenDataCommand { get; }
    public RelayCommand OpenReportsCommand { get; }
    public RelayCommand OpenLastCleaningCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenTermsCommand { get; }
    public RelayCommand OpenPrivacyCommand { get; }

    public string LegacyText { get => _legacyText; private set => Set(ref _legacyText, value); }
    public bool LegacyOn { get => _legacyOn; private set => Set(ref _legacyOn, value); }
    public string StartText { get => _startText; private set => Set(ref _startText, value); }
    public bool StartOn
    {
        get => _startOn;
        private set
        {
            if (!Set(ref _startOn, value)) return;
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(StartTone));
            OnPropertyChanged(nameof(StartChip));
        }
    }

    // ---- apresentação v2 (somente leitura)

    /// <summary>Agendamentos ligados (verificação e limpeza segura).</summary>
    public double ActiveSchedules => (Scan.IsOn ? 1 : 0) + (Clean.IsOn ? 1 : 0);
    public Views.Controls.Tone StartTone => StartOn ? Views.Controls.Tone.Good : Views.Controls.Tone.Neutral;
    public string StartChip => StartOn ? Loc.T("Settings.AbreWindows") : Loc.T("Settings.NaoAbreSozinho");
    public Views.Controls.Tone NotificationsTone => Notifications ? Views.Controls.Tone.Good : Views.Controls.Tone.Neutral;
    public string NotificationsChip => Notifications ? Loc.T("Settings.AvisosLigados") : Loc.T("Settings.SemAvisos");

    internal void RaiseSchedules() => OnPropertyChanged(nameof(ActiveSchedules));
    public string StartButtonText => StartOn ? Loc.T("Settings.Desligar") : Loc.T("Settings.Ligar");
    public string CleaningRules { get => _cleaningRules; private set => Set(ref _cleaningRules, value); }
    public string CleaningExcluded { get => _cleaningExcluded; private set => Set(ref _cleaningExcluded, value); }
    public string LastCleaning { get => _lastCleaning; private set => Set(ref _lastCleaning, value); }
    public string SettingsProblem { get => _settingsProblem; private set => Set(ref _settingsProblem, value); }

    // ----- preferências da interface (settings.json)

    public bool Notifications { get => UiSettings.Current.Notifications; set => Change(s => s with { Notifications = value }); }
    public bool NotifySkipped { get => UiSettings.Current.NotifySkipped; set => Change(s => s with { NotifySkipped = value }); }
    public bool TrayIcon { get => UiSettings.Current.TrayIcon; set => Change(s => s with { TrayIcon = value }); }
    public bool MinimizeToTray { get => UiSettings.Current.MinimizeToTray; set => Change(s => s with { MinimizeToTray = value }); }
    public bool CloseToTray { get => UiSettings.Current.CloseToTray; set => Change(s => s with { CloseToTray = value }); }
    /// <summary>Aviso de notebook na bateria quando um jogo abre (checagens do PC).</summary>
    public bool GameBatteryNotice { get => UiSettings.Current.GameBatteryNotice; set => Change(s => s with { GameBatteryNotice = value }); }

    // ----- Perfil do PC (recomendação automática por hardware; a preferência é do usuário, não é hardware)

    /// <summary>"Prefiro menos atraso a imagem bonita" (competitivo): opções de latência passam a "recomendadas para o seu PC".</summary>
    public bool PreferLowLatency
    {
        get => UiSettings.Current.PreferLowLatency;
        set { Change(s => s with { PreferLowLatency = value }); OnPropertyChanged(nameof(ProfileHeadline)); }
    }

    /// <summary>O perfil detectado em palavras simples ("Notebook com placa de entrada (GTX 1650, 4 GB), 16 GB de RAM, SSD").</summary>
    public string ProfileHeadline => Host?.HardwareProfile is { } h ? Simple.HardwareProfileText.Headline(h) : Loc.T("Profile.Reading");
    /// <summary>O que não deu para ler, ou vazio.</summary>
    public string ProfileUnknowns => Host?.HardwareProfile is { } h ? Simple.HardwareProfileText.Unknowns(h) : "";
    public bool HasProfileUnknowns => ProfileUnknowns.Length > 0;

    public override Task OnFactsReadyAsync(Nexus.HardwareDiscovery.SystemFacts facts)
    {
        OnPropertyChanged(nameof(ProfileHeadline));
        OnPropertyChanged(nameof(ProfileUnknowns));
        OnPropertyChanged(nameof(HasProfileUnknowns));
        return Task.CompletedTask;
    }

    private void Change(Func<NexusSettings, NexusSettings> change)
    {
        SettingsProblem = UiSettings.Update(change) ?? "";
        OnPropertyChanged(nameof(Notifications));
        OnPropertyChanged(nameof(NotifySkipped));
        OnPropertyChanged(nameof(TrayIcon));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(GameBatteryNotice));
        OnPropertyChanged(nameof(PreferLowLatency));
        OnPropertyChanged(nameof(NotificationsTone));
        OnPropertyChanged(nameof(NotificationsChip));
    }

    // ----- pastas e sobre

    public static string DataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.User))!;
    public static string ReportsDirectory => ScanReport.DefaultDirectory;
    public static string SettingsFile => UiSettings.Path;
    public static string Version => Loc.F("Settings.NexusNAplicadorN", AppUpdates.Instance.CurrentVersion, BrokerService.Version, Environment.Version);

    /// <summary>Atualização do programa (versão, procurar agora, automático, progresso).</summary>
    public AppUpdates Updates { get; } = AppUpdates.Instance;
    public string CatalogText { get => _catalogText; private set => Set(ref _catalogText, value); }

    public IReadOnlyList<DependencyLicense> Licenses { get; } =
    [
        new("Microsoft.Data.Sqlite", "MIT", Loc.T("Settings.DiarioSqliteAplicadorLeitura")),
        new(Loc.T("Settings.SqlitepclrawBundleSqlite3"), "Apache-2.0", Loc.T("Settings.PonteSqliteNativoSqlite")),
        new("System.ServiceProcess.ServiceController", "MIT", Loc.T("Settings.AplicadorInstaladoComoServico")),
        new(Loc.T("Settings.Net10Wpf"), "MIT", Loc.T("Settings.RuntimeInterfaceSemOutros")),
    ];

    public static string LicenseNote => Loc.T("Settings.NexusSiAindaNao");

    // ------------------------------------------------------------------ leitura

    public override Task OnShownAsync()
    {
        OnPropertyChanged(nameof(ProfileHeadline));
        OnPropertyChanged(nameof(ProfileUnknowns));
        OnPropertyChanged(nameof(HasProfileUnknowns));
        return RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        SettingsProblem = UiSettings.Problem ?? "";
        var read = await Task.Run(() =>
        {
            var catalog = CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory).Localized(Loc.Language);
            var scan = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Scan, AutomationScheduledScanHandler.DefaultExecutable);
            var clean = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, AutomationScheduledScanHandler.DefaultExecutable);
            var legacy = AutomationHandlers.ReadScheduledScanState(AutomationScheduledScanHandler.DefaultExecutable);
            var start = AutomationHandlers.ReadStartWithWindows(new WindowsRegistryBackend(), AutomationStartWithWindowsHandler.DefaultDesktopExecutable);
            var (safe, excluded) = SafeCleaningSelector.Select(catalog, CleaningRuleSet.BuiltIn, RiskModel.ReadDeclared(NexusServices.CatalogDirectory));
            return (catalog, scan, clean, legacy, start, safe, excluded);
        });
        _catalog = read.catalog;
        CatalogText = Loc.F("Settings.CatalogoNNAcoes", read.catalog.CatalogVersion, read.catalog.Actions.Count, read.catalog.Rejected.Count);
        Scan.Show(read.scan);
        Clean.Show(read.clean);
        LegacyOn = read.legacy.State != ScanSchedule.Off && read.legacy.State != AutomationScheduledScanHandler.Foreign;
        LegacyText = LegacyOn ? Loc.F("Settings.VerificacaoSemanalAntigaTambem", ScanSchedule.TryParse(read.legacy.State)?.Describe() ?? read.legacy.State) : "";
        StartOn = read.start.State is AutomationStartWithWindowsHandler.On or AutomationStartWithWindowsHandler.Stale;
        StartText = read.start.Error is not null ? Loc.F("Settings.NaoFoiPossivelLer", read.start.Error) : read.start.State switch
        {
            AutomationStartWithWindowsHandler.On => Loc.T("Settings.LigadoNexusAbreMinimizado"),
            AutomationStartWithWindowsHandler.Stale => Loc.T("Settings.LigadoMasApontandoOutra"),
            AutomationStartWithWindowsHandler.Foreign => Loc.T("Settings.ExisteEntradaNexusInicializacao"),
            _ => Loc.T("Settings.Desligado"),
        };
        CleaningRules = string.Join(" · ", read.safe.Select(s => s.Title));
        CleaningExcluded = Loc.F("Settings.NuncaEntramNRegras", read.excluded.Count);
        var last = LastCleaningReport();
        LastCleaning = last is null ? Loc.T("Settings.NenhumaLimpezaRegistradaRelatorio") : Loc.F("Settings.UltimoRelatorioLimpezaN", File.GetLastWriteTime(last));
    }

    private string ActionKey(string id) =>
        _catalog?.Actions.Where(a => a.Definition.Id == id).MaxBy(a => a.Definition.Version)?.Definition.Key ?? id + "@1";

    // ------------------------------------------------------------------ escrita (só pelo Host)

    internal Task SaveScheduleAsync(ScheduleEditor editor) => RunAsync(async () =>
    {
        if (Host is null) return;
        try
        {
            var schedule = editor.Schedule;
            var conditions = editor.Conditions;
            var text = new System.Text.StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"{editor.Title}: {schedule.Describe()} ({conditions.Describe()}).");
            text.AppendLine();
            text.AppendLine(editor.Explanation);
            if (editor.Kind == NexusTaskKind.Clean)
            {
                text.AppendLine();
                text.AppendLine(Loc.T("Settings.QuandoChegarHoraNexus"));
                text.AppendLine(CleaningRules);
                text.AppendLine();
                text.AppendLine(Loc.T("Settings.NuncaPastasAdministradorHistorico"));
                text.AppendLine(Loc.T("Settings.CadaLimpezaFicaDiario"));
            }
            text.AppendLine();
            text.Append(Loc.T("Settings.TarefaFicaAgendadorWindows"));
            var r = await Host.ApplyAsync(editor.Title, text.ToString(),
                [new ActionRequest(ActionKey(editor.Kind.ActionId), AutomationScheduledTaskHandler.On, Arguments(schedule, conditions))], BrokerLevel.User);
            if (r is not null) Message = r.Message;
        }
        finally { await RefreshAsync(); }
    });

    internal Task TurnOffScheduleAsync(ScheduleEditor editor) => RunAsync(async () =>
    {
        if (Host is null) return;
        try
        {
            var r = await Host.ApplyAsync(Loc.F("Settings.DesligarN", editor.Title), Loc.F("Settings.RemoverTarefaNAgendador", editor.Kind.TaskName),
                [new ActionRequest(ActionKey(editor.Kind.ActionId), AutomationScheduledTaskHandler.Off, Arguments(editor.Schedule, editor.Conditions))], BrokerLevel.User);
            if (r is not null) Message = r.Message;
        }
        finally { await RefreshAsync(); }
    });

    private static Dictionary<string, string> Arguments(TaskSchedule schedule, TaskConditions conditions) =>
        new() { ["schedule"] = schedule.ToString(), ["conditions"] = conditions.ToString() };

    private Task LegacyOffAsync() => RunAsync(async () =>
    {
        if (Host is null) return;
        try
        {
            var r = await Host.ApplyAsync(Loc.T("Settings.DesligarVerificacaoSemanalAntiga"), Loc.F("Settings.RemoverTarefaNVersao", ScheduledScanTask.TaskName),
                [new ActionRequest(ActionKey(AutomationHandlers.ScheduledScanActionId), ScanSchedule.Off)], BrokerLevel.User);
            if (r is not null) Message = r.Message;
        }
        finally { await RefreshAsync(); }
    });

    private Task ToggleStartAsync() => RunAsync(async () =>
    {
        if (Host is null) return;
        try
        {
            var on = !StartOn;
            var text = on
                ? Loc.T("Settings.AbrirNexusMinimizadoBandeja") +
                  Loc.T("Settings.CriaValorNexusHkcu") +
                  Loc.T("Settings.ApontandoNexusDesktopExe") +
                  Loc.T("Settings.DesligarAquiGerenciadorTarefas")
                : Loc.T("Settings.PararAbrirNexusQuando");
            var r = await Host.ApplyAsync(on ? Loc.T("Settings.IniciarWindows") : Loc.T("Settings.NaoIniciarWindows"), text,
                [new ActionRequest(ActionKey(AutomationHandlers.StartWithWindowsActionId), on ? AutomationStartWithWindowsHandler.On : AutomationStartWithWindowsHandler.Off)],
                BrokerLevel.User);
            if (r is not null) Message = r.Message;
        }
        finally { await RefreshAsync(); }
    });

    // ------------------------------------------------------------------ pastas (abrir no Explorador)

    private static string? LastCleaningReport()
    {
        try
        {
            var dir = ScanReport.DefaultDirectory;
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir).EnumerateFiles("clean-*.html").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void OpenLastCleaning()
    {
        if (LastCleaningReport() is { } file && !Nexus.Automation.Notifications.BalloonNotifier.OpenReport(file))
            Message = Loc.T("Settings.NaoFoiPossivelAbrir");
    }

    /// <summary>
    /// Termos de uso / Política de privacidade: legal\&lt;nome&gt;.html (ou .txt) da pasta do NEXUS, gerados de docs/legal pela
    /// publicação (tools/packaging/legal_docs.py). Só arquivos fixos desta pasta; abre no programa padrão do Windows.
    /// </summary>
    private void OpenLegal(string stem)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "legal");
        var file = new[] { stem + ".html", stem + ".txt" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        if (file is null) { Message = Loc.F("Settings.ArquivoNaoEncontradoN", dir); return; }
        try { using var _ = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = Loc.F("Asst.NaoFoiPossivelAbrir2", ex.Message); }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) { Message = Loc.F("Settings.PastaAindaNaoExiste", folder); return; }
            // Explorador do Windows numa pasta do próprio NEXUS (o usuário vê e controla).
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false, ArgumentList = { folder } });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = Loc.F("Asst.NaoFoiPossivelAbrir2", ex.Message); }
    }
}
