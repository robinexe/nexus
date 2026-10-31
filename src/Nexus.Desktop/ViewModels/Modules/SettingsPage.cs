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
    private string _status = "Lendo o Agendador…";
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

    public static IReadOnlyList<string> Frequencies { get; } = ["Todo dia", "Toda semana", "Todo mês"];
    public static IReadOnlyList<string> Days { get; } = ["domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado"];
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
    public bool IsOn { get => _isOn; private set { if (Set(ref _isOn, value)) OnPropertyChanged(nameof(SaveText)); } }
    public string SaveText => IsOn ? "Salvar horário" : "Ligar";

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
    public override string Title => "Ajustes";
    public override string Group => "Registro";
    public override string Subtitle => "Agendamentos, notificações, bandeja, iniciar com o Windows e informações do NEXUS.";

    private CatalogDocument? _catalog;
    private string _legacyText = "";
    private bool _legacyOn;
    private string _startText = "Lendo…";
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
    }

    public ScheduleEditor Scan { get; }
    public ScheduleEditor Clean { get; }
    public RelayCommand LegacyOffCommand { get; }
    public RelayCommand ToggleStartCommand { get; }
    public RelayCommand OpenDataCommand { get; }
    public RelayCommand OpenReportsCommand { get; }
    public RelayCommand OpenLastCleaningCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public string LegacyText { get => _legacyText; private set => Set(ref _legacyText, value); }
    public bool LegacyOn { get => _legacyOn; private set => Set(ref _legacyOn, value); }
    public string StartText { get => _startText; private set => Set(ref _startText, value); }
    public bool StartOn { get => _startOn; private set { if (Set(ref _startOn, value)) OnPropertyChanged(nameof(StartButtonText)); } }
    public string StartButtonText => StartOn ? "Desligar" : "Ligar";
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

    private void Change(Func<NexusSettings, NexusSettings> change)
    {
        SettingsProblem = UiSettings.Update(change) ?? "";
        OnPropertyChanged(nameof(Notifications));
        OnPropertyChanged(nameof(NotifySkipped));
        OnPropertyChanged(nameof(TrayIcon));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(CloseToTray));
    }

    // ----- pastas e sobre

    public static string DataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.User))!;
    public static string ReportsDirectory => ScanReport.DefaultDirectory;
    public static string SettingsFile => UiSettings.Path;
    public static string Version => $"NEXUS {typeof(SettingsPage).Assembly.GetName().Version} · aplicador {BrokerService.Version} · .NET {Environment.Version}";
    public string CatalogText { get => _catalogText; private set => Set(ref _catalogText, value); }

    public IReadOnlyList<DependencyLicense> Licenses { get; } =
    [
        new("Microsoft.Data.Sqlite", "MIT", "Diário (SQLite) do aplicador e leitura do histórico."),
        new("SQLitePCLRaw (bundle e_sqlite3)", "Apache-2.0", "Ponte para o SQLite nativo; o SQLite em si é domínio público."),
        new("System.ServiceProcess.ServiceController", "MIT", "Aplicador instalado como serviço do Windows (opcional)."),
        new(".NET 10 e WPF", "MIT", "Runtime e interface. Sem outros pacotes de interface."),
    ];

    public static string LicenseNote => "O NEXUS em si ainda não tem licença escolhida. Detalhes em docs/05-dependencies.md.";

    // ------------------------------------------------------------------ leitura

    public override Task OnShownAsync() => RunAsync(RefreshAsync);

    private async Task RefreshAsync()
    {
        SettingsProblem = UiSettings.Problem ?? "";
        var read = await Task.Run(() =>
        {
            var catalog = CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory);
            var scan = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Scan, AutomationScheduledScanHandler.DefaultExecutable);
            var clean = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, AutomationScheduledScanHandler.DefaultExecutable);
            var legacy = AutomationHandlers.ReadScheduledScanState(AutomationScheduledScanHandler.DefaultExecutable);
            var start = AutomationHandlers.ReadStartWithWindows(new WindowsRegistryBackend(), AutomationStartWithWindowsHandler.DefaultDesktopExecutable);
            var (safe, excluded) = SafeCleaningSelector.Select(catalog, CleaningRuleSet.BuiltIn, RiskModel.ReadDeclared(NexusServices.CatalogDirectory));
            return (catalog, scan, clean, legacy, start, safe, excluded);
        });
        _catalog = read.catalog;
        CatalogText = $"Catálogo {read.catalog.CatalogVersion} · {read.catalog.Actions.Count} ações · {read.catalog.Rejected.Count} itens rejeitados (mitos e arriscados)";
        Scan.Show(read.scan);
        Clean.Show(read.clean);
        LegacyOn = read.legacy.State != ScanSchedule.Off && read.legacy.State != AutomationScheduledScanHandler.Foreign;
        LegacyText = LegacyOn ? $"A verificação semanal antiga também está ligada ({ScanSchedule.TryParse(read.legacy.State)?.Describe() ?? read.legacy.State}). A nova acima a substitui." : "";
        StartOn = read.start.State is AutomationStartWithWindowsHandler.On or AutomationStartWithWindowsHandler.Stale;
        StartText = read.start.Error is not null ? $"Não foi possível ler: {read.start.Error}" : read.start.State switch
        {
            AutomationStartWithWindowsHandler.On => "Ligado: o NEXUS abre minimizado na bandeja quando você entra no Windows.",
            AutomationStartWithWindowsHandler.Stale => "Ligado, mas apontando para outra pasta do NEXUS. Desligue e ligue de novo para atualizar.",
            AutomationStartWithWindowsHandler.Foreign => "Existe uma entrada \"NEXUS\" na inicialização que não foi criada pelo NEXUS; ela não será tocada.",
            _ => "Desligado.",
        };
        CleaningRules = string.Join(" · ", read.safe.Select(s => s.Title));
        CleaningExcluded = $"Nunca entram: {read.excluded.Count} regras (administrador, histórico, cookies, shaders e outras com efeito colateral). Essas só na página Limpeza, com você escolhendo.";
        var last = LastCleaningReport();
        LastCleaning = last is null ? "Nenhuma limpeza registrada em relatório ainda." : $"Último relatório de limpeza: {File.GetLastWriteTime(last):dd/MM/yyyy HH:mm}.";
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
                text.AppendLine("Quando chegar a hora, o NEXUS vai limpar SEM PERGUNTAR (só estas regras):");
                text.AppendLine(CleaningRules);
                text.AppendLine();
                text.AppendLine("Nunca: pastas de administrador, histórico, cookies, caches de shader ou algo irreversível.");
                text.AppendLine("Cada limpeza fica no diário e volta em Limpeza > Histórico.");
            }
            text.AppendLine();
            text.Append("A tarefa fica no Agendador do Windows, na sua conta, sem administrador. Dá para desligar aqui a qualquer momento.");
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
            var r = await Host.ApplyAsync($"Desligar: {editor.Title}", $"Remover a tarefa {editor.Kind.TaskName} do Agendador do Windows?",
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
            var r = await Host.ApplyAsync("Desligar verificação semanal antiga", $"Remover a tarefa {ScheduledScanTask.TaskName} (versão antiga) do Agendador do Windows?",
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
                ? "Abrir o NEXUS minimizado na bandeja sempre que você entrar no Windows?\n\n" +
                  "Cria o valor \"NEXUS\" em HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run (sua conta, sem administrador), " +
                  "apontando para o Nexus.Desktop.exe desta pasta. O NEXUS só lê o PC enquanto fica aberto.\n" +
                  "Dá para desligar aqui, no Gerenciador de Tarefas ou na página Inicialização."
                : "Parar de abrir o NEXUS quando você entra no Windows?";
            var r = await Host.ApplyAsync(on ? "Iniciar com o Windows" : "Não iniciar com o Windows", text,
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
            Message = "Não foi possível abrir o relatório.";
    }

    private void OpenFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) { Message = $"A pasta ainda não existe: {folder}"; return; }
            // Explorador do Windows numa pasta do próprio NEXUS (o usuário vê e controla).
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false, ArgumentList = { folder } });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = $"Não foi possível abrir: {ex.Message}"; }
    }
}
