using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Nexus.Broker;
using Nexus.Desktop.Views.Controls;
using Nexus.Programs;
using Nexus.Programs.Inventory;
using Nexus.Programs.Leftovers;
using Nexus.Programs.Uninstall;
using Nexus.Programs.Updates;
using Nexus.SystemTools.Restore;

namespace Nexus.Desktop.ViewModels;

/// <summary>Linha da lista de programas (marcável para a desinstalação em massa quando as regras do lote deixam).</summary>
public sealed class ProgramRow(InstalledProgram program, BulkEligibility? eligibility = null, Action? checkedChanged = null) : ObservableObject
{
    private bool _isChecked;
    public InstalledProgram Program { get; } = program;
    public BulkEligibility Eligibility { get; } = eligibility ?? BulkEligibility.Allowed;
    public string Name => Program.Name;
    public string Publisher => Program.Publisher ?? "—";
    public string Version => Program.Version ?? "—";
    public string Size => ProgramInventory.FormatSize(Program.SizeBytes);
    public string Installed => Program.InstallDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—";
    public string Scope => Program.ScopeText;
    public string Location => Program.InstallLocation ?? Loc.T("Prog.NaoInformadoPeloInstalador");
    public string Kind => Program.IsMsi ? Loc.T("Prog.WindowsInstallerMsi") : Loc.T("Prog.InstaladorProprioFornecedor");
    public string Uninstaller => Program.UninstallString ?? "—";
    public string Quiet => Program.IsMsi
        ? Loc.T("Prog.WindowsInstallerDesinstalacaoMassa")
        : Program.QuietUninstallString is null
            ? Loc.T("Prog.NaoDeclaradoPeloFornecedor")
            : Loc.T("Prog.DeclaradoPeloFornecedorUsado");
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public bool CanCheck => Eligibility.Selectable;
    public bool IsCritical => Eligibility.Critical;
    public bool IsBlocked => !Eligibility.Selectable;
    public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value && CanCheck)) checkedChanged?.Invoke(); } }

    /// <summary>Selo do lote: vermelho = fora, âmbar = atenção (biblioteca/driver), nenhum quando pode entrar.</summary>
    public Tone FlagTone => IsBlocked ? Tone.Bad : IsCritical ? Tone.Warn : Tone.Neutral;
    public bool HasFlag => Flag.Length > 0;
    public string ScopeAndKind => $"{Scope} · {Kind}";

    /// <summary>Marca curta da coluna "Lote".</summary>
    public string Flag => IsBlocked ? Loc.T("Prog.Fora") : IsCritical ? Loc.T("Conv.Atencao") : "";

    public string BulkNote => Eligibility.Kind switch
    {
        BulkEligibilityKind.Blocked => Loc.F("Prog.ForaDesinstalacaoMassaN", Eligibility.Category, Eligibility.Reason),
        BulkEligibilityKind.NeedsExtraConfirmation => Loc.F("Prog.NNMarqueSo", Eligibility.Category, Eligibility.Reason),
        _ => Loc.T("Prog.PodeEntrarDesinstalacaoMassa"),
    };
}

/// <summary>Linha da fila de desinstalação (atualizada a cada mudança do executor).</summary>
public sealed class BulkItemRow(BulkQueueItem item) : ObservableObject
{
    private string _state = Loc.T("Prog.Fila"), _detail = "";
    private bool _active, _good, _bad, _skipped;
    private double _progress;
    public string Name => item.Program.Name;
    public string Mode => item.ModeText + (item.Note is { } n ? $" · {n}" : "");
    public string Size => ProgramInventory.FormatSize(item.Program.SizeBytes);
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public bool Active { get => _active; private set => Set(ref _active, value); }
    public bool Good { get => _good; private set => Set(ref _good, value); }
    public bool Bad { get => _bad; private set => Set(ref _bad, value); }
    public bool Skipped { get => _skipped; private set => Set(ref _skipped, value); }
    /// <summary>Barra do item: 0 = na fila, 100 = terminou (a barra do item ativo fica em movimento).</summary>
    public double ProgressValue { get => _progress; private set => Set(ref _progress, value); }
    /// <summary>Tom do item: âmbar = agora, verde = removido, vermelho = falhou, neutro = na fila ou pulado.</summary>
    public Tone Tone => Bad ? Tone.Bad : Good ? Tone.Good : Active ? Tone.Warn : Tone.Neutral;
    public string IconKey => Bad ? "Icon.Error" : Good ? "Icon.Check" : Active ? "Icon.Refresh" : Skipped ? "Icon.ChevronRight" : "Icon.History";

    public void Update(BulkItemStatus s, bool current)
    {
        ArgumentNullException.ThrowIfNull(s);
        State = s.StateText;
        Detail = s.Detail;
        Active = current && !s.Finished;
        Good = s.State == BulkItemState.Removed;
        Bad = s.State == BulkItemState.Failed;
        Skipped = s.State == BulkItemState.Skipped;
        ProgressValue = s.Finished ? 100 : 0;
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(IconKey));
    }
}

public sealed class StoreAppRow(StoreApp app)
{
    public string Name => app.DisplayName;
    public string Publisher => app.Publisher is { } p && p.StartsWith("CN=", StringComparison.Ordinal) ? p[3..].Split(',')[0] : app.Publisher ?? "—";
    public string Version => app.Version ?? "—";
    public string Package => app.PackageFullName;
}

/// <summary>Linha de sobra (marcável).</summary>
public sealed class LeftoverRow(LeftoverCandidate candidate, string programName = "") : ObservableObject
{
    private bool _isChecked = candidate.Recommended;
    public LeftoverCandidate Candidate { get; } = candidate;
    public string Program => programName;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public string Confidence => $"{Candidate.Confidence} · {Candidate.Score}";
    public string Kind => Candidate.KindText;
    public string Path => Candidate.DisplayPath;
    public string Size => Candidate.Kind == LeftoverKind.Folder ? Loc.F("Prog.NNArq", ProgramInventory.FormatSize(Candidate.SizeBytes), Candidate.FileCount ?? 0) : "—";
    public string Reason => Candidate.Reason;
    public bool High => Candidate.Score >= 75;
    public bool Low => Candidate.Score < 50;
    /// <summary>Selo de confiança: verde = alta, ciano = média, âmbar = baixa (confira antes de marcar).</summary>
    public Tone Tone => High ? Tone.Good : Low ? Tone.Warn : Tone.Info;
    public string ConfidenceLabel => High ? Loc.F("Prog.AltaN", Candidate.Score) : Low ? Loc.F("Prog.BaixaN", Candidate.Score) : Loc.F("Prog.MediaN", Candidate.Score);
    public string IconKey => Candidate.Kind == LeftoverKind.Folder ? "Icon.Catalog" : "Icon.Code";
    public double SizeBytes => Candidate.Kind == LeftoverKind.Folder ? Candidate.SizeBytes ?? 0 : 0;
    public string SubLine => string.IsNullOrEmpty(programName) ? Kind : $"{programName} · {Kind}";
}

public sealed class ManagerRow(PackageManagerStatus status) : ObservableObject
{
    private string _detail = "";
    public PackageManagerStatus Status { get; } = status;
    public string Name => Status.DisplayName;
    public string State => Status.StatusText;
    public bool Installed => Status.Installed;
    public string Explanation => Status.Explanation;
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    /// <summary>As atualizações deste gerenciador (o cartão mostra só as dele).</summary>
    public ObservableCollection<UpdateRow> Updates { get; } = [];
    public bool HasUpdates => Updates.Count > 0;
    public Tone Tone => !Installed ? Tone.Neutral : Updates.Count > 0 ? Tone.Warn : Tone.Good;
    public string CountText => !Installed ? Loc.T("Prog.NaoInstalado") : Updates.Count == 0 ? Loc.T("Prog.Dia") : Updates.Count == 1 ? Loc.T("Prog.N1Nova") : Loc.F("Prog.NNovas", Updates.Count);
    public string IconKey => Status.Kind switch
    {
        PackageManagerKind.Winget => "Icon.Windows",
        PackageManagerKind.Npm => "Icon.Code",
        _ => "Icon.Programs",
    };

    internal void AddUpdates(IEnumerable<UpdateRow> rows)
    {
        foreach (var r in rows) Updates.Add(r);
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(CountText));
    }
}

public sealed class UpdateRow(PackageUpdate update) : ObservableObject
{
    private bool _isChecked = update.CanUpgrade;
    public PackageUpdate Update { get; } = update;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && Update.CanUpgrade); }
    public bool CanUpgrade => Update.CanUpgrade;
    public string Name => Update.Name;
    public string Id => Update.Id;
    public string Current => Update.Current;
    public string Available => Update.Available;
    public string Manager => PackageManagerInfo.DisplayName(Update.Manager) + (Update.Source is { } s && Update.Manager == PackageManagerKind.Winget ? $" · {s}" : "");
    public string Note => Update.IdTruncated ? Loc.T("Prog.IdCortadoPeloWinget")
        : Update.Pinned ? Loc.T("Prog.FixadoPinnedGerenciadorNao")
        : Update.Manager == PackageManagerKind.Winget && string.IsNullOrWhiteSpace(Update.Source) ? Loc.T("Prog.SemOrigemConhecida")
        : Update.RequiresExplicit ? Loc.T("Prog.ExigePedidoExplicitoNexus")
        : "";
    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// Programas e atualizações: inventário somente leitura (chaves Uninstall + Store opcional), desinstalador OFICIAL visível
/// (exceção controlada, depois de confirmar), varredura de sobras e remoção delas pelo aplicador (handler ProgramLeftover,
/// reversível), e atualizações por winget/Chocolatey/Scoop/npm numa janela visível do próprio gerenciador.
/// </summary>
public sealed class ProgramsPage : ModulePage
{
    /// <summary>Máximo de sobras por escopo num pedido (limite de ações por pedido do aplicador).</summary>
    public const int MaxPerRequest = BrokerService.MaxActionsPerRequest;
    public const string UserAction = "programs.leftover.user@1", MachineAction = "programs.leftover.machine@1";
    /// <summary>Ação do módulo Ferramentas do sistema (catalog/system.v1.json) usada antes do lote.</summary>
    public const string RestorePointAction = "system.restore.create-point@1";

    private static CultureInfo PtBr => Loc.Culture; // idioma da interface (docs/12-idiomas.md)
    private readonly IProcessRunner _runner = new HiddenProcessRunner();
    private List<InstalledProgram> _all = [];
    private Dictionary<string, ProgramRow> _rows = new(StringComparer.Ordinal);
    private int _tab;
    private string _search = "";
    private int _sortIndex;
    private ProgramRow? _selected;
    private bool _loaded;
    private bool _includeStore;
    private string _storeHeadline = "";
    private List<InstalledProgram> _leftoverTargets = [];
    private bool _useQuietMode = true;
    private bool _createRestorePoint = true;
    private string _selectionText = Loc.T("Prog.NenhumSelecionado");
    private BulkUninstaller? _queue;
    private bool _bulkRunning, _showBulkPanel, _bulkReboot;
    private double _bulkPercent;
    private string _bulkHeadline = "", _bulkCounts = "", _bulkCurrent = "", _bulkSummary = "", _bulkFreed = "";
    private List<InstalledProgram> _batchRemoved = [];
    private string _leftoversTitle = Loc.T("Prog.NenhumProgramaEscolhidoAinda");
    private string _leftoversHeadline = "—";
    private string _updatesHeadline = Loc.T("Prog.AindaNaoVerificado2");
    private string _updatesDetail = Loc.T("Prog.CliqueProcurarAtualizacoesNexus");
    private string _programsHeadline = "—";
    private string _programsDetail = "";
    private readonly List<(Guid Run, BrokerLevel Level)> _lastRuns = [];
    private bool _loadingPrograms = true, _checkingUpdates, _updatesChecked, _leftoversScanned;
    private double _selectedBytes, _selectedCount, _bulkPending, _bulkRemoved, _bulkFailed, _bulkSkipped;

    public ProgramsPage()
    {
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadProgramsAsync), () => !Busy);
        UninstallCommand = new RelayCommand(() => _ = RunAsync(UninstallAsync), () => !Busy && Selected is not null);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        LeftoversForSelectedCommand = new RelayCommand(() => { if (Selected is { } s) { _leftoverTargets = [s.Program]; IsLeftoversTab = true; _ = RunAsync(ScanLeftoversAsync); } },
            () => !Busy && Selected is not null);
        ScanLeftoversCommand = new RelayCommand(() => _ = RunAsync(ScanLeftoversAsync), () => !Busy && _leftoverTargets.Count > 0);
        SelectVisibleCommand = new RelayCommand(SelectVisible, () => !Busy && Programs.Any(r => r.Eligibility.Kind == BulkEligibilityKind.Allowed && !r.IsChecked));
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => !Busy && _rows.Values.Any(r => r.IsChecked));
        BulkUninstallCommand = new RelayCommand(() => _ = RunAsync(BulkUninstallAsync), () => !Busy && _rows.Values.Any(r => r.IsChecked));
        StopAfterCurrentCommand = new RelayCommand(() => { _queue?.RequestStopAfterCurrent(); Say(Loc.T("Prog.FilaDepoisDesinstaladorAtual")); },
            () => _queue is not null && !_queue.Snapshot().StopRequested);
        SkipCurrentCommand = new RelayCommand(() => _queue?.SkipCurrent(), () => _queue?.Snapshot().Current is { Finished: false });
        BulkLeftoversCommand = new RelayCommand(() => { _leftoverTargets = [.. _batchRemoved]; IsLeftoversTab = true; _ = RunAsync(ScanLeftoversAsync); },
            () => !Busy && _batchRemoved.Count > 0);
        CloseBulkCommand = new RelayCommand(() => ShowBulkPanel = false, () => !BulkRunning);
        RemoveLeftoversCommand = new RelayCommand(() => _ = RunAsync(RemoveLeftoversAsync), () => !Busy && Leftovers.Any(l => l.IsChecked));
        UndoLeftoversCommand = new RelayCommand(() => _ = RunAsync(UndoLeftoversAsync), () => !Busy && _lastRuns.Count > 0);
        CheckUpdatesCommand = new RelayCommand(() => _ = RunAsync(CheckUpdatesAsync), () => !Busy);
        UpdateSelectedCommand = new RelayCommand(() => StartUpdates(Updates.Where(u => u.IsChecked && u.CanUpgrade).ToList(), Loc.T("Prog.AtualizarSelecionados")),
            () => !Busy && Updates.Any(u => u.IsChecked && u.CanUpgrade));
        UpdateAllCommand = new RelayCommand(() => StartUpdates(Updates.Where(u => u.CanUpgrade).ToList(), Loc.T("Prog.AtualizarTudo")), () => !Busy && Updates.Any(u => u.CanUpgrade));
        ShowProgramsCommand = new RelayCommand(() => IsProgramsTab = true);
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Message)) OnPropertyChanged(nameof(HasMessage)); };
    }

    public override string Title => Loc.T("Prog.NavTitle");
    public override string Group => Loc.T("Nav.Limpar");
    public override string Subtitle => Loc.T("Prog.NavSubtitle");

    // ------------------------------------------------------------------ abas

    public bool IsProgramsTab { get => _tab == 0; set { if (value) SetTab(0); } }
    public bool IsUpdatesTab { get => _tab == 1; set { if (value) SetTab(1); } }
    public bool IsLeftoversTab { get => _tab == 2; set { if (value) SetTab(2); } }

    private void SetTab(int tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        OnPropertyChanged(nameof(IsProgramsTab));
        OnPropertyChanged(nameof(IsUpdatesTab));
        OnPropertyChanged(nameof(IsLeftoversTab));
    }

    public string ProgramsTabLabel => _loaded ? Loc.F("Prog.ProgramasN", _all.Count) : Loc.T("AsstStep.Programas");
    public string UpdatesTabLabel => Updates.Count > 0 ? Loc.F("Prog.AtualizacoesN", Updates.Count) : Loc.T("Settings.Atualizacoes");
    public string LeftoversTabLabel => Leftovers.Count > 0 ? Loc.F("Prog.SobrasN", Leftovers.Count) : Loc.T("Prog.Sobras2");

    public bool HasMessage => Message.Length > 0;

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Primeira leitura do inventário (esqueletos).</summary>
    public bool IsLoadingPrograms { get => _loadingPrograms; private set => Set(ref _loadingPrograms, value); }
    public double ProgramCount => _loaded ? _all.Count : double.NaN;
    public double TotalBytes => _loaded ? _all.Sum(p => p.SizeBytes ?? 0) : double.NaN;
    public double SelectedBytes { get => _selectedBytes; private set => Set(ref _selectedBytes, value); }
    public double SelectedCount { get => _selectedCount; private set => Set(ref _selectedCount, value); }
    public double BulkPending { get => _bulkPending; private set => Set(ref _bulkPending, value); }
    public double BulkRemoved { get => _bulkRemoved; private set => Set(ref _bulkRemoved, value); }
    public double BulkFailed { get => _bulkFailed; private set => Set(ref _bulkFailed, value); }
    public double BulkSkipped { get => _bulkSkipped; private set => Set(ref _bulkSkipped, value); }
    public bool HasPrograms => Programs.Count > 0;
    public bool ShowNoPrograms => _loaded && Programs.Count == 0;
    public RelayCommand ShowProgramsCommand { get; }

    public bool CheckingUpdates { get => _checkingUpdates; private set => Set(ref _checkingUpdates, value); }
    public bool UpdatesChecked { get => _updatesChecked; private set { if (Set(ref _updatesChecked, value)) OnPropertyChanged(nameof(ShowUpdatesNotChecked)); } }
    public bool ShowUpdatesNotChecked => !UpdatesChecked && !CheckingUpdates;
    public double UpdatesCount => UpdatesChecked ? Updates.Count : double.NaN;
    public bool ShowAllUpToDate => UpdatesChecked && Updates.Count == 0;

    public bool LeftoversScanned { get => _leftoversScanned; private set => Set(ref _leftoversScanned, value); }
    public double LeftoverCount => LeftoversScanned ? Leftovers.Count : double.NaN;
    public double LeftoverBytes => LeftoversScanned ? Leftovers.Sum(l => l.SizeBytes) : double.NaN;
    public bool HasLeftovers => Leftovers.Count > 0;

    private void Say(string message) => Message = message;

    // ------------------------------------------------------------------ programas

    public ObservableCollection<ProgramRow> Programs { get; } = [];
    public ObservableCollection<StoreAppRow> StoreApps { get; } = [];
    public IReadOnlyList<string> SortOptions { get; } = [Loc.T("Prog.NomeZ"), Loc.T("Prog.MaiorPrimeiro"), Loc.T("Prog.InstaladosUltimo"), Loc.T("Prog.Editor")];

    public string Search { get => _search; set { if (Set(ref _search, value ?? "")) ApplyFilter(); } }
    public int SortIndex { get => _sortIndex; set { if (Set(ref _sortIndex, value)) ApplyFilter(); } }
    public ProgramRow? Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); } }
    public bool HasSelection => _selected is not null;
    public string ProgramsHeadline { get => _programsHeadline; private set => Set(ref _programsHeadline, value); }
    public string ProgramsDetail { get => _programsDetail; private set => Set(ref _programsDetail, value); }
    public string StoreHeadline { get => _storeHeadline; private set => Set(ref _storeHeadline, value); }

    public bool IncludeStore
    {
        get => _includeStore;
        set
        {
            if (!Set(ref _includeStore, value)) return;
            if (value) _ = RunAsync(LoadStoreAsync);
            else { StoreApps.Clear(); StoreHeadline = ""; }
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand LeftoversForSelectedCommand { get; }

    public override Task OnShownAsync() => _loaded ? Task.CompletedTask : RunAsync(LoadProgramsAsync);

    private static UninstallEnvironment UninstallEnv() =>
        new(System.IO.File.Exists, Environment.ExpandEnvironmentVariables, Environment.SystemDirectory, AppContext.BaseDirectory.TrimEnd('\\', '/'));

    /// <summary>Inventário + regra do lote de cada programa (confere o desinstalador no disco: fora da thread da interface).</summary>
    private static Task<List<(InstalledProgram Program, BulkEligibility Eligibility)>> ReadInventoryAsync() =>
        Task.Run(() =>
        {
            var env = UninstallEnv();
            return ProgramInventory.Build(new WindowsUninstallRegistrySource().ReadAll()).Select(p => (p, BulkUninstallPolicy.Evaluate(p, env))).ToList();
        });

    private async Task LoadProgramsAsync()
    {
        try
        {
            await ReadProgramsAsync();
        }
        finally
        {
            IsLoadingPrograms = false;
        }
    }

    private async Task ReadProgramsAsync()
    {
        var list = await ReadInventoryAsync();
        _all = list.Select(x => x.Program).ToList();
        // As marcações sobrevivem a "ler de novo" (mesma chave do registro).
        var old = _rows;
        _rows = new Dictionary<string, ProgramRow>(StringComparer.Ordinal);
        foreach (var (program, eligibility) in list)
        {
            var row = new ProgramRow(program, eligibility, UpdateSelection);
            if (old.TryGetValue(program.Id, out var prev) && prev.IsChecked) row.IsChecked = true;
            _rows[program.Id] = row;
        }
        _loaded = true;
        var total = _all.Sum(p => p.SizeBytes ?? 0);
        ProgramsHeadline = Loc.F("Prog.NProgramasInstalados", _all.Count);
        ProgramsDetail = string.Format(PtBr,
            Loc.T("Prog.NDeclaradosPelosInstaladores") +
            Loc.T("Prog.MesmasAplicativosInstaladosSem"),
            ProgramInventory.FormatSize(total), _all.Count(p => p.Scope == InstallScope.User), _all.Count(p => p.SizeBytes is null));
        ApplyFilter();
        UpdateSelection();
        OnPropertyChanged(nameof(ProgramsTabLabel));
        OnPropertyChanged(nameof(ProgramCount));
        OnPropertyChanged(nameof(TotalBytes));
    }

    private void ApplyFilter()
    {
        var keep = Selected?.Program.Id;
        var sort = (ProgramSort)Math.Clamp(SortIndex, 0, 3);
        var rows = ProgramInventory.Sort(ProgramInventory.Filter(_all, Search), sort)
            .Select(p => _rows.TryGetValue(p.Id, out var r) ? r : new ProgramRow(p, null, UpdateSelection)).ToList();
        Programs.Clear();
        foreach (var r in rows) Programs.Add(r);
        Selected = Programs.FirstOrDefault(r => r.Program.Id == keep);
        OnPropertyChanged(nameof(HasPrograms));
        OnPropertyChanged(nameof(ShowNoPrograms));
    }

    private async Task LoadStoreAsync()
    {
        StoreHeadline = Loc.T("Prog.LendoAppsStore");
        try
        {
            var apps = await Task.Run(() => Nexus.Programs.Inventory.StoreApps.ReadAsync(_runner, PackageManagers.PowerShellPath));
            StoreApps.Clear();
            foreach (var a in apps) StoreApps.Add(new StoreAppRow(a));
            StoreHeadline = Loc.F("Prog.NAppsMicrosoftStore", apps.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StoreHeadline = Loc.F("Prog.NaoFoiPossivelLer", ex.Message);
        }
    }

    private void OpenSettings()
    {
        try { UninstallLauncher.OpenWindowsAppsSettings(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say(Loc.F("Prog.NaoFoiPossivelAbrir", ex.Message)); }
    }

    private async Task UninstallAsync()
    {
        if (Host is null || Selected is not { } row) return;
        var p = row.Program;
        if (p.NoRemove) { Say(Loc.F("Prog.NRegistrouComoNao", p.Name)); return; }
        var cmd = UninstallLauncher.Prepare(p.UninstallString, out var error);
        if (cmd is null) { Say(error ?? Loc.T("Prog.DesinstaladorInvalido")); return; }

        var text = new StringBuilder()
            .Append(Loc.F("Prog.AbrirDesinstaladorOficialN", p.Name))
            .Append(Loc.T("Prog.QuemRemoveProgramaDesinstalador"))
            .Append(CultureInfo.InvariantCulture, $"{cmd.Display}\n\n")
            .Append(Loc.T("Prog.NexusNaoConsegueDesfazer"))
            .Append(Loc.T("Prog.MudancasSistemaFeitasPelo"))
            .Append(Loc.T("Prog.QuiserGarantiaExtraCrie"))
            .Append(Loc.T("Prog.QuandoDesinstaladorTerminarNexus"))
            .ToString();
        if (!Host.Confirm(Loc.F("Prog.DesinstalarN", p.Name), text)) { Say(Loc.T("Shell.CanceladoNadaFoiAlterado")); return; }

        System.Diagnostics.Process? process;
        try { process = UninstallLauncher.Start(cmd); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException or InvalidOperationException)
        {
            Say(Loc.F("Prog.DesinstaladorNaoAbriuN", ex.Message));
            return;
        }
        _leftoverTargets = [p];
        LeftoversTitle = Loc.F("Prog.SobrasN2", p.Name);
        Say(Loc.F("Prog.DesinstaladorNAbertoSiga", p.Name));
        if (process is not null) _ = WatchUninstallerAsync(process, p);
        await Task.CompletedTask;
    }

    private async Task WatchUninstallerAsync(System.Diagnostics.Process process, InstalledProgram p)
    {
        try
        {
            using (process) await process.WaitForExitAsync();
            await Task.Delay(1500); // o registro às vezes é atualizado logo depois de a janela fechar
            await RunAsync(async () =>
            {
                await LoadProgramsAsync();
                if (StillInstalled(p))
                {
                    Say(Loc.F("Prog.DesinstaladorFechouMasN", p.Name));
                    return;
                }
                IsLeftoversTab = true;
                await ScanLeftoversAsync();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private bool StillInstalled(InstalledProgram p) => ProgramInventory.Contains(_all, p);

    // ------------------------------------------------------------------ desinstalação em massa

    public bool UseQuietMode { get => _useQuietMode; set => Set(ref _useQuietMode, value); }
    public bool CreateRestorePoint { get => _createRestorePoint; set => Set(ref _createRestorePoint, value); }
    public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }
    public bool HasChecked => _rows.Values.Any(r => r.IsChecked);
    public RelayCommand SelectVisibleCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand BulkUninstallCommand { get; }
    public RelayCommand StopAfterCurrentCommand { get; }
    public RelayCommand SkipCurrentCommand { get; }
    public RelayCommand BulkLeftoversCommand { get; }
    public RelayCommand CloseBulkCommand { get; }

    public ObservableCollection<BulkItemRow> BulkItems { get; } = [];
    public bool BulkRunning { get => _bulkRunning; private set { if (Set(ref _bulkRunning, value)) OnPropertyChanged(nameof(HasBulkSummary)); } }
    public bool ShowBulkPanel { get => _showBulkPanel; private set => Set(ref _showBulkPanel, value); }
    public double BulkPercent { get => _bulkPercent; private set => Set(ref _bulkPercent, value); }
    public string BulkHeadline { get => _bulkHeadline; private set => Set(ref _bulkHeadline, value); }
    public string BulkCounts { get => _bulkCounts; private set => Set(ref _bulkCounts, value); }
    public string BulkCurrent { get => _bulkCurrent; private set => Set(ref _bulkCurrent, value); }
    public string BulkSummary { get => _bulkSummary; private set { if (Set(ref _bulkSummary, value)) OnPropertyChanged(nameof(HasBulkSummary)); } }
    public string BulkFreed { get => _bulkFreed; private set => Set(ref _bulkFreed, value); }
    public bool BulkReboot { get => _bulkReboot; private set => Set(ref _bulkReboot, value); }
    public bool HasBulkSummary => !BulkRunning && BulkSummary.Length > 0;
    public bool HasBatchRemoved => _batchRemoved.Count > 0;
    public string BulkLeftoversLabel => Loc.F("Prog.ProcurarSobrasNRemovido", _batchRemoved.Count);

    private void UpdateSelection()
    {
        var chosen = _rows.Values.Where(r => r.IsChecked).ToList();
        var bytes = chosen.Sum(r => r.Program.SizeBytes ?? 0);
        var critical = chosen.Count(r => r.IsCritical);
        SelectedBytes = bytes;
        SelectedCount = chosen.Count;
        SelectionText = chosen.Count == 0 ? Loc.T("Prog.NenhumSelecionado")
            : Loc.F("Prog.NSelecionadoSN", chosen.Count, (bytes > 0 ? ProgramInventory.FormatSize(bytes) : Loc.T("Prog.TamanhoNaoInformado"))) +
              (critical > 0 ? Loc.F("Prog.NPedeMAtencao", critical) : "");
        OnPropertyChanged(nameof(HasChecked));
    }

    /// <summary>Marca os visíveis (busca atual) que podem entrar no lote sem ressalva. Os de "atenção" só um a um.</summary>
    private void SelectVisible()
    {
        foreach (var r in Programs.Where(r => r.Eligibility.Kind == BulkEligibilityKind.Allowed)) r.IsChecked = true;
        var skipped = Programs.Count(r => r.Eligibility.Kind != BulkEligibilityKind.Allowed);
        if (skipped > 0)
            Say(Loc.F("Prog.NVisivelIsNao", skipped));
    }

    private void ClearSelection()
    {
        foreach (var r in _rows.Values) r.IsChecked = false;
    }

    private async Task BulkUninstallAsync()
    {
        if (Host is null) return;
        await LoadProgramsAsync(); // nunca roda um desinstalador lido há muito tempo
        var chosen = _rows.Values.Where(r => r.IsChecked).Select(r => r.Program).ToList();
        if (chosen.Count == 0) { Say(Loc.T("Prog.NenhumProgramaSelecionadoContinua")); return; }
        var preferQuiet = UseQuietMode;
        var plan = await Task.Run(() => BulkQueueBuilder.Build(chosen, UninstallEnv(), preferQuiet));
        if (plan.Items.Count == 0)
        {
            Say(Loc.T("Prog.NenhumSelecionadosPodeSer") + string.Join(" | ", plan.Refused.Take(3).Select(r => $"{r.Program.Name}: {r.Reason}")));
            return;
        }

        // 1) Confirmação extra para bibliotecas e drivers dos quais outros programas dependem.
        if (plan.CriticalItems.Count > 0)
        {
            var warn = new StringBuilder().Append(Loc.F("Prog.NItemNsFila", plan.CriticalItems.Count));
            foreach (var c in plan.CriticalItems)
                warn.Append(CultureInfo.InvariantCulture, $"• {c.Program.Name}\n   {c.Eligibility.Category}: {c.Eligibility.Reason}\n");
            warn.Append(Loc.T("Prog.ListaCriticosNexusConservadora"))
                .Append(Loc.T("Prog.SoContinueVoceSabe"))
                .Append(Loc.T("Prog.ElesVaoUltimoFila"));
            if (!Host.Confirm(Loc.T("Prog.AtencaoBibliotecasDriversFila"), warn.ToString())) { Say(Loc.T("Prog.CanceladoNadaFoiDesinstalado")); return; }
        }

        // 2) Ponto de restauração: só se a Proteção do Sistema estiver ligada (ou não deu para saber).
        var restore = CreateRestorePoint;
        if (restore)
        {
            var (drives, policy) = await Task.Run(() => (WindowsRestorePointBackend.ProtectedDrives(), WindowsRestorePointBackend.DisabledByPolicy()));
            if (policy || drives is { Count: 0 })
            {
                var why = policy ? Loc.T("Prog.RestauracaoSistemaEstaDesligada") : Loc.T("Prog.ProtecaoSistemaEstaDesligada");
                if (!Host.Confirm(Loc.T("Prog.SemPontoRestauracao"),
                        Loc.F("Prog.NNaoCriarPonto", why)))
                { Say(Loc.T("Prog.CanceladoNadaFoiDesinstalado")); return; }
                restore = false;
            }
        }

        // 3) A confirmação do lote, com tudo listado.
        if (!Host.Confirm(Loc.F("Prog.DesinstalarNProgramaS", plan.Items.Count), BulkConfirmation(plan, restore, preferQuiet))) { Say(Loc.T("Prog.CanceladoNadaFoiDesinstalado")); return; }

        if (restore)
        {
            var r = await Host.ApplyAsync(Loc.T("Prog.PontoRestauracaoAntesLote"),
                Loc.F("Prog.CriarPontoRestauracaoAgora", plan.Items.Count) +
                Loc.T("Prog.WindowsVaiPedirPermissao"),
                [new ActionRequest(RestorePointAction)], BrokerLevel.Admin);
            if (r is null) { Say(Loc.T("Prog.CanceladoNadaFoiDesinstalado")); return; }
            var result = r.Response?.Results is { Count: > 0 } rs ? rs[0] : null;
            if (!r.Ok || result?.Outcome != "Verified")
            {
                var detail = result?.Detail ?? r.Message;
                if (!Host.Confirm(Loc.T("Prog.PontoRestauracaoNaoCriado"), Loc.F("Prog.NContinuarDesinstalacaoSem", detail))) { Say(Loc.T("Prog.CanceladoNadaFoiDesinstalado")); return; }
            }
        }

        await RunQueueAsync(plan);
    }

    private static string BulkConfirmation(BulkQueuePlan plan, bool restore, bool preferQuiet)
    {
        const int maxLines = 25;
        var text = new StringBuilder().Append(Loc.F("Prog.DesinstalarNProgramaS2", plan.Items.Count));
        foreach (var i in plan.Items.Take(maxLines))
        {
            var size = ProgramInventory.FormatSize(i.Program.SizeBytes);
            text.Append(CultureInfo.InvariantCulture, $"• {i.Program.Name} — {size} · {i.Program.Publisher ?? Loc.T("Prog.EditorNaoInformado")} · {i.KindText} · {i.ModeText}{(i.Critical ? Loc.T("Prog.Atencao") : "")}\n");
        }
        if (plan.Items.Count > maxLines) text.Append(Loc.F("Prog.MaisN", plan.Items.Count - maxLines));
        text.Append(Loc.F("Prog.TotalDeclaradoNN", ProgramInventory.FormatSize(plan.KnownBytes), (plan.UnknownSize > 0 ? Loc.F("Prog.NSemTamanhoInformado", plan.UnknownSize) : "")));
        if (plan.Refused.Count > 0)
        {
            text.Append(Loc.T("Prog.FicamFora"));
            foreach (var (p, reason) in plan.Refused.Take(8)) text.Append(CultureInfo.InvariantCulture, $"• {p.Name}: {reason}\n");
        }
        text.Append(Loc.T("Prog.CadaDesinstaladorOficialFornecedor"))
            .Append(Loc.T("Prog.VoltarReinstalePrograma"))
            .Append(preferQuiet
                ? Loc.T("Prog.SilenciosoComandoSemPerguntas")
                : Loc.T("Prog.ModoSilenciosoDesligadoTodos"))
            .Append(Loc.T("Prog.JanelaVoceConcluiFila"))
            .Append(Loc.T("Prog.VezWindowsInstallerSo"))
            .Append(restore ? Loc.T("Prog.AntesComecarCriarPonto") : Loc.T("Prog.SemPontoRestauracaoAntes"))
            .Append(Loc.T("Prog.FimNexusLeLista"));
        return text.ToString();
    }

    private async Task RunQueueAsync(BulkQueuePlan plan)
    {
        BulkItems.Clear();
        foreach (var i in plan.Items) BulkItems.Add(new BulkItemRow(i));
        BulkSummary = "";
        BulkFreed = "";
        BulkReboot = false;
        _batchRemoved = [];
        OnPropertyChanged(nameof(HasBatchRemoved));
        ShowBulkPanel = true;
        BulkRunning = true;
        Say(Loc.T("Prog.DesinstalandoFilaAcompanheAbaixo"));
        var progress = new Progress<BulkQueueSnapshot>(ApplySnapshot);
        var queue = new BulkUninstaller(plan.Items, new WindowsUninstallRunner(), WindowsInstalledCheck.StillInstalled, new WindowsRebootPendingProbe(), null, progress);
        _queue = queue;
        Nexus.Programs.Uninstall.BulkSummary summary;
        try
        {
            summary = await Task.Run(() => queue.RunAsync());
        }
        finally
        {
            _queue = null;
            BulkRunning = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        ApplySnapshot(queue.Snapshot());
        await LoadProgramsAsync();
        _batchRemoved = summary.RemovedPrograms.Where(p => !StillInstalled(p)).ToList();
        OnPropertyChanged(nameof(HasBatchRemoved));
        OnPropertyChanged(nameof(BulkLeftoversLabel));
        BulkHeadline = summary.Headline;
        BulkFreed = summary.FreedText;
        BulkReboot = summary.RebootRequired;
        BulkCurrent = "";
        BulkSummary = summary.Text();
        Say(summary.Headline + (summary.RebootRequired ? Loc.T("Prog.ReiniciePcConcluir") : ".") +
            (_batchRemoved.Count > 0 ? Loc.T("Prog.AgoraProcurarSobrasTodos") : ""));
    }

    private void ApplySnapshot(BulkQueueSnapshot s)
    {
        for (var i = 0; i < s.Items.Count && i < BulkItems.Count; i++) BulkItems[i].Update(s.Items[i], s.CurrentIndex == i);
        BulkPercent = s.Total == 0 ? 0 : 100.0 * s.Done / s.Total;
        BulkPending = s.Pending;
        BulkRemoved = s.Removed;
        BulkFailed = s.Failed;
        BulkSkipped = s.Skipped;
        BulkCounts = Loc.F("Prog.FilaNRemovidosN", s.Pending, s.Removed, s.Failed, s.Skipped) +
                     (s.StopRequested && BulkRunning ? Loc.T("Prog.DepoisAtual") : "");
        if (s.Current is { Finished: false } c)
        {
            BulkHeadline = Loc.F("Prog.NNN", s.Done + 1, s.Total, c.Item.Program.Name);
            BulkCurrent = $"{c.StateText}. {c.Detail}";
        }
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ sobras

    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];
    public string LeftoversTitle { get => _leftoversTitle; private set => Set(ref _leftoversTitle, value); }
    public string LeftoversHeadline { get => _leftoversHeadline; private set => Set(ref _leftoversHeadline, value); }
    public bool CanUndo => _lastRuns.Count > 0;
    public RelayCommand ScanLeftoversCommand { get; }
    public RelayCommand RemoveLeftoversCommand { get; }
    public RelayCommand UndoLeftoversCommand { get; }

    /// <summary>Procura as sobras de um ou vários programas (o lote) que já saíram da lista; achados repetidos viram um.</summary>
    private async Task ScanLeftoversAsync()
    {
        if (_leftoverTargets.Count == 0) return;
        var targets = _leftoverTargets.ToList();
        LeftoversTitle = targets.Count == 1 ? Loc.F("Prog.SobrasN2", targets[0].Name) : Loc.F("Prog.SobrasNProgramasRemovidos", targets.Count);
        await LoadProgramsAsync();
        Leftovers.Clear();
        var still = targets.Where(StillInstalled).ToList();
        var gone = targets.Except(still).ToList();
        if (gone.Count == 0)
        {
            LeftoversHeadline = Loc.T("Prog.AindaInstalado");
            NotifyLeftovers();
            Say(targets.Count == 1
                ? Loc.F("Prog.NAindaEstaInstalado", targets[0].Name)
                : Loc.T("Prog.TodosAindaAparecemComo"));
            OnPropertyChanged(nameof(LeftoversTabLabel));
            return;
        }
        var installed = _all;
        var found = await Task.Run(() =>
        {
            var roots = new LeftoverRoots(WindowsLeftoverRoots.MachineRoots(), WindowsLeftoverRoots.CurrentUserRoots());
            var reader = new WindowsRegistryKeyReader();
            var seen = new HashSet<(LeftoverKind, bool, string)>();
            var list = new List<(InstalledProgram Program, LeftoverCandidate Candidate)>();
            foreach (var t in gone)
                foreach (var c in LeftoverScanner.Scan(t, installed, roots, reader))
                    if (seen.Add((c.Kind, c.MachineScope, c.Path.ToUpperInvariant()))) list.Add((t, c));
            return list;
        });
        foreach (var (program, candidate) in found) Leftovers.Add(new LeftoverRow(candidate, program.Name));
        LeftoversScanned = true;
        NotifyLeftovers();
        LeftoversHeadline = found.Count == 0 ? Loc.T("Prog.NadaSobrou") : LeftoverScanner.Summary(found.Select(f => f.Candidate).ToList());
        var stillNote = still.Count == 0 ? "" : Loc.F("Prog.NAindaInstaladoS", still.Count, string.Join(", ", still.Take(3).Select(p => p.Name)));
        Say((found.Count == 0
                ? (gone.Count == 1 ? Loc.F("Prog.NenhumaSobraNEncontrada", gone[0].Name) : Loc.T("Prog.NenhumaSobraEncontradaNas"))
                : Loc.T("Prog.ConfiraListaConfiancaAlta")) + stillNote);
        OnPropertyChanged(nameof(LeftoversTabLabel));
    }

    private void NotifyLeftovers()
    {
        OnPropertyChanged(nameof(LeftoverCount));
        OnPropertyChanged(nameof(LeftoverBytes));
        OnPropertyChanged(nameof(HasLeftovers));
    }

    private async Task RemoveLeftoversAsync()
    {
        if (Host is null) return;
        var chosen = Leftovers.Where(l => l.IsChecked).Select(l => l.Candidate).ToList();
        if (chosen.Count == 0) return;
        var user = chosen.Where(c => !c.MachineScope).ToList();
        var machine = chosen.Where(c => c.MachineScope).ToList();

        var moved = 0;
        var failed = new List<string>();
        foreach (var (group, level, actionId, label) in new[]
                 {
                     (user, BrokerLevel.User, UserAction, Loc.T("Prog.SeuPerfil")),
                     (machine, BrokerLevel.Admin, MachineAction, Loc.T("Prog.TodosUsuariosPedeAdministrador")),
                 })
        {
            if (group.Count == 0) continue;
            // Lote: a mesma ação, uma instância por sobra (alvo diferente). Uma confirmação, uma execução, um desfazer por parte
            // (o aplicador aceita até MaxPerRequest ações por pedido; acima disso, vai em partes).
            var parts = group.Chunk(MaxPerRequest).ToList();
            for (var n = 0; n < parts.Count; n++)
            {
                var part = parts[n];
                var requests = part.Select(c => new ActionRequest(actionId, null,
                    new Dictionary<string, string> { ["kind"] = c.Kind == LeftoverKind.Folder ? "file" : "registry", ["path"] = c.Path })).ToList();
                var partLabel = parts.Count > 1 ? Loc.F("Prog.ParteNN", n + 1, parts.Count) : "";
                var text = new StringBuilder().Append(Loc.F("Prog.MoverNSobraS", part.Length, label, partLabel));
                foreach (var c in part.Take(12)) text.Append(CultureInfo.InvariantCulture, $"• {c.DisplayPath}\n");
                if (part.Length > 12) text.Append(Loc.F("Prog.MaisN", part.Length - 12));
                text.Append(Loc.T("Prog.NadaApagadoPastasVao"))
                    .Append(Loc.T("Prog.VocePodeDesfazerAqui"));
                var r = await Host.ApplyAsync(Loc.T("Prog.RemoverSobras"), text.ToString(), requests, level);
                if (r is null) break;
                if (r.Response is { Ok: true, RunId: { } run })
                {
                    _lastRuns.Add((run, level));
                    var results = r.Response.Results ?? [];
                    moved += results.Count(x => x.Outcome == "Verified");
                    failed.AddRange(results.Where(x => x.Outcome != "Verified").Select(x => $"{x.Outcome}: {x.Detail}"));
                }
                else failed.Add(r.Message);
            }
        }
        OnPropertyChanged(nameof(CanUndo));
        await ScanLeftoversAsync();
        Say(failed.Count == 0
            ? Loc.F("Prog.NSobraSQuarentena", moved)
            : Loc.F("Prog.NQuarentenaNNao", moved, failed.Count, string.Join(" | ", failed.Take(3))));
    }

    private async Task UndoLeftoversAsync()
    {
        if (Host is null || _lastRuns.Count == 0) return;
        foreach (var (run, level) in Enumerable.Reverse(_lastRuns).ToList())
        {
            var r = await Host.RevertAsync(run, level);
            if (r is null) return; // cancelado: mantém para tentar de novo
            if (r.Response?.FullyRestored == true) _lastRuns.Remove((run, level));
            else { Say(Loc.F("Prog.DesfazerNaoTerminouN", r.Message)); break; }
        }
        OnPropertyChanged(nameof(CanUndo));
        if (_leftoverTargets.Count > 0) await ScanLeftoversAsync();
        if (_lastRuns.Count == 0) Say(Loc.T("Prog.DesfeitoSobrasVoltaramOnde"));
    }

    // ------------------------------------------------------------------ atualizações

    public ObservableCollection<ManagerRow> Managers { get; } = [];
    public ObservableCollection<UpdateRow> Updates { get; } = [];
    public string UpdatesHeadline { get => _updatesHeadline; private set => Set(ref _updatesHeadline, value); }
    public string UpdatesDetail { get => _updatesDetail; private set => Set(ref _updatesDetail, value); }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand UpdateSelectedCommand { get; }
    public RelayCommand UpdateAllCommand { get; }

    private async Task CheckUpdatesAsync()
    {
        CheckingUpdates = true;
        OnPropertyChanged(nameof(ShowUpdatesNotChecked));
        try
        {
            await ReadUpdatesAsync();
        }
        finally
        {
            CheckingUpdates = false;
            UpdatesChecked = true;
            OnPropertyChanged(nameof(ShowUpdatesNotChecked));
            OnPropertyChanged(nameof(UpdatesCount));
            OnPropertyChanged(nameof(ShowAllUpToDate));
        }
    }

    private async Task ReadUpdatesAsync()
    {
        UpdatesHeadline = Loc.T("Prog.Verificando");
        UpdatesDetail = Loc.T("Prog.PerguntandoCadaGerenciadorPode");
        Managers.Clear();
        Updates.Clear();
        var statuses = await Task.WhenAll(PackageManagers.All.Select(k => Task.Run(() => PackageManagers.DetectAsync(k, _runner))));
        var rows = statuses.Select(s => new ManagerRow(s)).ToList();
        foreach (var r in rows) Managers.Add(r);
        var lists = await Task.WhenAll(statuses.Select(s => Task.Run(() => PackageManagers.ListUpdatesAsync(s, _runner))));
        foreach (var (row, list) in rows.Zip(lists))
        {
            row.Detail = !row.Installed ? Loc.T("Prog.NaoInstaladoNestePc")
                : list.Problem is { } problem ? problem
                : list.Updates.Count == 0 ? Loc.T("Drv.TudoDia")
                : Loc.F("Prog.NVersaoNova", list.Updates.Count);
            var mine = list.Updates.Select(u => new UpdateRow(u)).ToList();
            foreach (var u in mine) Updates.Add(u);
            row.AddUpdates(mine);
        }
        var n = Updates.Count;
        UpdatesHeadline = n == 0 ? Loc.T("Prog.TudoDia") : string.Create(PtBr, $"{n} {(n == 1 ? Loc.T("Prog.AtualizacaoDisponivel") : Loc.T("Prog.AtualizacoesDisponiveis2"))}");
        UpdatesDetail = n == 0
            ? Loc.T("Prog.NenhumGerenciadorEncontradoRelatou")
            : Loc.F("Prog.NPodemSerPedidas", Updates.Count(u => u.CanUpgrade));
        OnPropertyChanged(nameof(UpdatesTabLabel));
    }

    private void StartUpdates(List<UpdateRow> rows, string title)
    {
        if (Host is null || rows.Count == 0) return;
        var groups = rows.GroupBy(r => r.Update.Manager).ToList();
        var text = new StringBuilder().Append(Loc.F("Prog.AtualizarNPacoteS", rows.Count));
        foreach (var g in groups)
        {
            text.Append(CultureInfo.InvariantCulture, $"{PackageManagerInfo.DisplayName(g.Key)}:\n");
            foreach (var r in g.Take(15)) text.Append(CultureInfo.InvariantCulture, $"• {r.Name}  {r.Current} → {r.Available}\n");
            if (g.Count() > 15) text.Append(Loc.F("Prog.MaisN", g.Count() - 15));
        }
        text.Append(Loc.T("Prog.CadaGerenciadorAbreNuma"))
            .Append(Loc.T("Prog.NexusNaoConsegueDesfazer2"));
        if (groups.Any(g => g.Key == PackageManagerKind.Chocolatey))
            text.Append(Loc.T("Prog.ChocolateyPedeAdministradorWindows"));
        if (!Host.Confirm(title, text.ToString())) { Say(Loc.T("Shell.CanceladoNadaFoiAlterado")); return; }

        var opened = new List<string>();
        foreach (var g in groups)
        {
            var status = Managers.FirstOrDefault(m => m.Status.Kind == g.Key)?.Status;
            if (status is null) continue;
            try
            {
                PackageManagers.StartUpgradeWindow(status, g.Select(r => r.Update).ToList())?.Dispose();
                opened.Add(status.DisplayName);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                Say(Loc.F("Prog.NJanelaNaoAbriu", status.DisplayName, ex.Message));
            }
        }
        if (opened.Count > 0)
            Say(Loc.F("Prog.JanelaAbertaNQuando", string.Join(", ", opened)));
    }
}
