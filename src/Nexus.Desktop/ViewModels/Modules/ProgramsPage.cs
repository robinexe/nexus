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
    public string Location => Program.InstallLocation ?? "não informado pelo instalador";
    public string Kind => Program.IsMsi ? "Windows Installer (MSI)" : "Instalador próprio do fornecedor";
    public string Uninstaller => Program.UninstallString ?? "—";
    public string Quiet => Program.IsMsi
        ? "Windows Installer: na desinstalação em massa com modo silencioso, só a barra de progresso (/qb- /norestart)"
        : Program.QuietUninstallString is null
            ? "não declarado pelo fornecedor (na desinstalação em massa, abre a janela dele)"
            : "declarado pelo fornecedor: usado só na desinstalação em massa com o modo silencioso ligado";
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
    public string Flag => IsBlocked ? "fora" : IsCritical ? "atenção" : "";

    public string BulkNote => Eligibility.Kind switch
    {
        BulkEligibilityKind.Blocked => $"Fora da desinstalação em massa ({Eligibility.Category}): {Eligibility.Reason}",
        BulkEligibilityKind.NeedsExtraConfirmation => $"{Eligibility.Category}: {Eligibility.Reason} Marque só se tiver certeza; pede uma confirmação a mais.",
        _ => "Pode entrar na desinstalação em massa.",
    };
}

/// <summary>Linha da fila de desinstalação (atualizada a cada mudança do executor).</summary>
public sealed class BulkItemRow(BulkQueueItem item) : ObservableObject
{
    private string _state = "Na fila", _detail = "";
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
    public string Size => Candidate.Kind == LeftoverKind.Folder ? $"{ProgramInventory.FormatSize(Candidate.SizeBytes)} · {Candidate.FileCount ?? 0} arq." : "—";
    public string Reason => Candidate.Reason;
    public bool High => Candidate.Score >= 75;
    public bool Low => Candidate.Score < 50;
    /// <summary>Selo de confiança: verde = alta, ciano = média, âmbar = baixa (confira antes de marcar).</summary>
    public Tone Tone => High ? Tone.Good : Low ? Tone.Warn : Tone.Info;
    public string ConfidenceLabel => High ? $"Alta · {Candidate.Score}" : Low ? $"Baixa · {Candidate.Score}" : $"Média · {Candidate.Score}";
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
    public string CountText => !Installed ? "Não instalado" : Updates.Count == 0 ? "Em dia" : Updates.Count == 1 ? "1 nova" : $"{Updates.Count} novas";
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
    public string Note => Update.IdTruncated ? "ID cortado pelo winget: atualize pelo próprio winget"
        : Update.Pinned ? "Fixado (pinned): o gerenciador não atualiza sem você liberar"
        : Update.Manager == PackageManagerKind.Winget && string.IsNullOrWhiteSpace(Update.Source) ? "Sem origem conhecida"
        : Update.RequiresExplicit ? "Exige pedido explícito (o NEXUS pede por ID)"
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

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
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
    private string _selectionText = "Nenhum selecionado";
    private BulkUninstaller? _queue;
    private bool _bulkRunning, _showBulkPanel, _bulkReboot;
    private double _bulkPercent;
    private string _bulkHeadline = "", _bulkCounts = "", _bulkCurrent = "", _bulkSummary = "", _bulkFreed = "";
    private List<InstalledProgram> _batchRemoved = [];
    private string _leftoversTitle = "Nenhum programa escolhido ainda";
    private string _leftoversHeadline = "—";
    private string _updatesHeadline = "Ainda não verificado";
    private string _updatesDetail = "Clique em \"Procurar atualizações\". O NEXUS só lê: pergunta a cada gerenciador o que tem versão nova.";
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
        StopAfterCurrentCommand = new RelayCommand(() => { _queue?.RequestStopAfterCurrent(); Say("A fila para depois do desinstalador atual. Nenhum desinstalador é fechado pelo NEXUS."); },
            () => _queue is not null && !_queue.Snapshot().StopRequested);
        SkipCurrentCommand = new RelayCommand(() => _queue?.SkipCurrent(), () => _queue?.Snapshot().Current is { Finished: false });
        BulkLeftoversCommand = new RelayCommand(() => { _leftoverTargets = [.. _batchRemoved]; IsLeftoversTab = true; _ = RunAsync(ScanLeftoversAsync); },
            () => !Busy && _batchRemoved.Count > 0);
        CloseBulkCommand = new RelayCommand(() => ShowBulkPanel = false, () => !BulkRunning);
        RemoveLeftoversCommand = new RelayCommand(() => _ = RunAsync(RemoveLeftoversAsync), () => !Busy && Leftovers.Any(l => l.IsChecked));
        UndoLeftoversCommand = new RelayCommand(() => _ = RunAsync(UndoLeftoversAsync), () => !Busy && _lastRuns.Count > 0);
        CheckUpdatesCommand = new RelayCommand(() => _ = RunAsync(CheckUpdatesAsync), () => !Busy);
        UpdateSelectedCommand = new RelayCommand(() => StartUpdates(Updates.Where(u => u.IsChecked && u.CanUpgrade).ToList(), "Atualizar selecionados"),
            () => !Busy && Updates.Any(u => u.IsChecked && u.CanUpgrade));
        UpdateAllCommand = new RelayCommand(() => StartUpdates(Updates.Where(u => u.CanUpgrade).ToList(), "Atualizar tudo"), () => !Busy && Updates.Any(u => u.CanUpgrade));
        ShowProgramsCommand = new RelayCommand(() => IsProgramsTab = true);
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Message)) OnPropertyChanged(nameof(HasMessage)); };
    }

    public override string Title => "Programas e atualizações";
    public override string Group => "Limpar";
    public override string Subtitle => "Desinstale o que não usa, limpe o que sobrou e mantenha o resto atualizado.";

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

    public string ProgramsTabLabel => _loaded ? $"Programas · {_all.Count}" : "Programas";
    public string UpdatesTabLabel => Updates.Count > 0 ? $"Atualizações · {Updates.Count}" : "Atualizações";
    public string LeftoversTabLabel => Leftovers.Count > 0 ? $"Sobras · {Leftovers.Count}" : "Sobras";

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
    public IReadOnlyList<string> SortOptions { get; } = ["Nome (A–Z)", "Maior primeiro", "Instalados por último", "Editor"];

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
        ProgramsHeadline = string.Create(PtBr, $"{_all.Count} programas instalados");
        ProgramsDetail = string.Format(PtBr,
            "{0} declarados pelos instaladores · {1} só deste usuário · {2} sem tamanho informado. Fonte: chaves Uninstall do registro " +
            "(as mesmas de \"Aplicativos instalados\"), sem componentes do sistema e sem atualizações.",
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
        StoreHeadline = "Lendo os apps da Store…";
        try
        {
            var apps = await Task.Run(() => Nexus.Programs.Inventory.StoreApps.ReadAsync(_runner, PackageManagers.PowerShellPath));
            StoreApps.Clear();
            foreach (var a in apps) StoreApps.Add(new StoreAppRow(a));
            StoreHeadline = string.Create(PtBr, $"{apps.Count} apps da Microsoft Store deste usuário (lidos com Get-AppxPackage, sem frameworks nem apps do sistema). Para remover, use Configurações > Aplicativos.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StoreHeadline = $"Não foi possível ler os apps da Store: {ex.Message}";
        }
    }

    private void OpenSettings()
    {
        try { UninstallLauncher.OpenWindowsAppsSettings(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say($"Não foi possível abrir Configurações: {ex.Message}"); }
    }

    private async Task UninstallAsync()
    {
        if (Host is null || Selected is not { } row) return;
        var p = row.Program;
        if (p.NoRemove) { Say($"{p.Name} se registrou como \"não removível\" pelo próprio instalador. Use Configurações > Aplicativos."); return; }
        var cmd = UninstallLauncher.Prepare(p.UninstallString, out var error);
        if (cmd is null) { Say(error ?? "Desinstalador inválido."); return; }

        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Abrir o desinstalador oficial de {p.Name}?\n\n")
            .Append("Quem remove o programa é o desinstalador do próprio fornecedor, numa janela que você vê e controla:\n")
            .Append(CultureInfo.InvariantCulture, $"{cmd.Display}\n\n")
            .Append("O NEXUS não consegue desfazer uma desinstalação: para voltar, será preciso instalar o programa de novo.\n\n")
            .Append("As mudanças de sistema feitas pelo próprio NEXUS criam um ponto de restauração automático. Aqui quem age é o fornecedor; ")
            .Append("se quiser uma garantia extra, crie um ponto antes (menu Iniciar > \"Criar um ponto de restauração\").\n\n")
            .Append("Quando o desinstalador terminar, o NEXUS procura as sobras (pastas e chaves que ficaram para trás).")
            .ToString();
        if (!Host.Confirm($"Desinstalar {p.Name}", text)) { Say("Cancelado. Nada foi alterado."); return; }

        System.Diagnostics.Process? process;
        try { process = UninstallLauncher.Start(cmd); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.FileNotFoundException or InvalidOperationException)
        {
            Say($"O desinstalador não abriu: {ex.Message}");
            return;
        }
        _leftoverTargets = [p];
        LeftoversTitle = $"Sobras de {p.Name}";
        Say($"Desinstalador de {p.Name} aberto. Siga as instruções dele; quando terminar, o NEXUS confere a lista e procura as sobras.");
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
                    Say($"O desinstalador fechou, mas {p.Name} ainda aparece na lista. Alguns desinstaladores continuam em outra janela: termine por lá e clique em \"Procurar sobras\".");
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
    public string BulkLeftoversLabel => $"Procurar as sobras dos {_batchRemoved.Count} removido(s)";

    private void UpdateSelection()
    {
        var chosen = _rows.Values.Where(r => r.IsChecked).ToList();
        var bytes = chosen.Sum(r => r.Program.SizeBytes ?? 0);
        var critical = chosen.Count(r => r.IsCritical);
        SelectedBytes = bytes;
        SelectedCount = chosen.Count;
        SelectionText = chosen.Count == 0 ? "Nenhum selecionado"
            : string.Create(PtBr, $"{chosen.Count} selecionado(s) · {(bytes > 0 ? ProgramInventory.FormatSize(bytes) : "tamanho não informado")}") +
              (critical > 0 ? string.Create(PtBr, $" · {critical} pede(m) atenção") : "");
        OnPropertyChanged(nameof(HasChecked));
    }

    /// <summary>Marca os visíveis (busca atual) que podem entrar no lote sem ressalva. Os de "atenção" só um a um.</summary>
    private void SelectVisible()
    {
        foreach (var r in Programs.Where(r => r.Eligibility.Kind == BulkEligibilityKind.Allowed)) r.IsChecked = true;
        var skipped = Programs.Count(r => r.Eligibility.Kind != BulkEligibilityKind.Allowed);
        if (skipped > 0)
            Say(string.Create(PtBr, $"{skipped} visível(is) não foram marcados: bibliotecas e drivers (marque um a um, se tiver certeza) ou itens fora do lote (o motivo aparece ao passar o mouse)."));
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
        if (chosen.Count == 0) { Say("Nenhum programa selecionado continua instalado."); return; }
        var preferQuiet = UseQuietMode;
        var plan = await Task.Run(() => BulkQueueBuilder.Build(chosen, UninstallEnv(), preferQuiet));
        if (plan.Items.Count == 0)
        {
            Say("Nenhum dos selecionados pode ser desinstalado em lote: " + string.Join(" | ", plan.Refused.Take(3).Select(r => $"{r.Program.Name}: {r.Reason}")));
            return;
        }

        // 1) Confirmação extra para bibliotecas e drivers dos quais outros programas dependem.
        if (plan.CriticalItems.Count > 0)
        {
            var warn = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{plan.CriticalItems.Count} item(ns) da fila são usados por outros programas ou pelo hardware:\n\n");
            foreach (var c in plan.CriticalItems)
                warn.Append(CultureInfo.InvariantCulture, $"• {c.Program.Name}\n   {c.Eligibility.Category}: {c.Eligibility.Reason}\n");
            warn.Append("\nA lista de \"críticos\" do NEXUS é conservadora, mas não sabe quais programas seus dependem de cada um. ")
                .Append("Só continue se você sabe que não precisa deles (por exemplo, o driver de uma placa de vídeo que você já tirou). ")
                .Append("Eles vão por último na fila.");
            if (!Host.Confirm("Atenção: bibliotecas e drivers na fila", warn.ToString())) { Say("Cancelado. Nada foi desinstalado."); return; }
        }

        // 2) Ponto de restauração: só se a Proteção do Sistema estiver ligada (ou não deu para saber).
        var restore = CreateRestorePoint;
        if (restore)
        {
            var (drives, policy) = await Task.Run(() => (WindowsRestorePointBackend.ProtectedDrives(), WindowsRestorePointBackend.DisabledByPolicy()));
            if (policy || drives is { Count: 0 })
            {
                var why = policy ? "A Restauração do Sistema está desligada por política (organização)." : "A Proteção do Sistema está desligada em todas as unidades.";
                if (!Host.Confirm("Sem ponto de restauração",
                        $"{why} Não dá para criar um ponto antes do lote.\n\nVocê pode ligá-la na página Ferramentas do sistema (Proteção do Sistema) e voltar aqui.\n\nContinuar SEM ponto de restauração?"))
                { Say("Cancelado. Nada foi desinstalado."); return; }
                restore = false;
            }
        }

        // 3) A confirmação do lote, com tudo listado.
        if (!Host.Confirm($"Desinstalar {plan.Items.Count} programa(s)", BulkConfirmation(plan, restore, preferQuiet))) { Say("Cancelado. Nada foi desinstalado."); return; }

        if (restore)
        {
            var r = await Host.ApplyAsync("Ponto de restauração antes do lote",
                string.Create(PtBr, $"Criar um ponto de restauração agora, antes de desinstalar {plan.Items.Count} programa(s)?\n\n") +
                "O Windows vai pedir permissão de administrador. Se ele já criou um ponto nas últimas 24 horas, esse ponto vale.",
                [new ActionRequest(RestorePointAction)], BrokerLevel.Admin);
            if (r is null) { Say("Cancelado. Nada foi desinstalado."); return; }
            var result = r.Response?.Results is { Count: > 0 } rs ? rs[0] : null;
            if (!r.Ok || result?.Outcome != "Verified")
            {
                var detail = result?.Detail ?? r.Message;
                if (!Host.Confirm("Ponto de restauração não criado", $"{detail}\n\nContinuar a desinstalação sem um ponto novo?")) { Say("Cancelado. Nada foi desinstalado."); return; }
            }
        }

        await RunQueueAsync(plan);
    }

    private static string BulkConfirmation(BulkQueuePlan plan, bool restore, bool preferQuiet)
    {
        const int maxLines = 25;
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Desinstalar {plan.Items.Count} programa(s), um de cada vez, nesta ordem?\n\n");
        foreach (var i in plan.Items.Take(maxLines))
        {
            var size = ProgramInventory.FormatSize(i.Program.SizeBytes);
            text.Append(CultureInfo.InvariantCulture, $"• {i.Program.Name} — {size} · {i.Program.Publisher ?? "editor não informado"} · {i.KindText} · {i.ModeText}{(i.Critical ? " · ATENÇÃO" : "")}\n");
        }
        if (plan.Items.Count > maxLines) text.Append(CultureInfo.InvariantCulture, $"• … e mais {plan.Items.Count - maxLines}\n");
        text.Append(PtBr, $"\nTotal declarado: {ProgramInventory.FormatSize(plan.KnownBytes)}{(plan.UnknownSize > 0 ? $" ({plan.UnknownSize} sem tamanho informado)" : "")}.\n");
        if (plan.Refused.Count > 0)
        {
            text.Append("\nFicam de fora:\n");
            foreach (var (p, reason) in plan.Refused.Take(8)) text.Append(CultureInfo.InvariantCulture, $"• {p.Name}: {reason}\n");
        }
        text.Append("\nCada desinstalador é o oficial do fornecedor: é ele quem remove, e o NEXUS NÃO consegue desfazer uma desinstalação ")
            .Append("(para voltar, reinstale o programa). ")
            .Append(preferQuiet
                ? "Silencioso = o comando sem perguntas que o próprio fornecedor declarou; MSI = Windows Installer só com a barra de progresso. "
                : "Modo silencioso desligado: todos abrem a janela do fornecedor. ")
            .Append("Os de janela você conclui; a fila espera cada um terminar (inclusive quando ele continua em outra janela) e nunca fecha um desinstalador. ")
            .Append("Um por vez: o Windows Installer só aceita uma instalação de cada vez.\n\n")
            .Append(restore ? "Antes de começar: criar um ponto de restauração (pede administrador).\n\n" : "Sem ponto de restauração antes do lote.\n\n")
            .Append("No fim, o NEXUS lê a lista de novo, mostra o resumo e oferece procurar as sobras de todos os removidos de uma vez.");
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
        Say("Desinstalando em fila. Acompanhe abaixo; nos de janela, conclua os passos do fornecedor.");
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
        Say(summary.Headline + (summary.RebootRequired ? ". Reinicie o PC para concluir." : ".") +
            (_batchRemoved.Count > 0 ? " Agora dá para procurar as sobras de todos de uma vez." : ""));
    }

    private void ApplySnapshot(BulkQueueSnapshot s)
    {
        for (var i = 0; i < s.Items.Count && i < BulkItems.Count; i++) BulkItems[i].Update(s.Items[i], s.CurrentIndex == i);
        BulkPercent = s.Total == 0 ? 0 : 100.0 * s.Done / s.Total;
        BulkPending = s.Pending;
        BulkRemoved = s.Removed;
        BulkFailed = s.Failed;
        BulkSkipped = s.Skipped;
        BulkCounts = string.Create(PtBr, $"Na fila {s.Pending} · Removidos {s.Removed} · Falhas {s.Failed} · Pulados {s.Skipped}") +
                     (s.StopRequested && BulkRunning ? " · para depois do atual" : "");
        if (s.Current is { Finished: false } c)
        {
            BulkHeadline = string.Create(PtBr, $"{s.Done + 1} de {s.Total}: {c.Item.Program.Name}");
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
        LeftoversTitle = targets.Count == 1 ? $"Sobras de {targets[0].Name}" : string.Create(PtBr, $"Sobras de {targets.Count} programas removidos");
        await LoadProgramsAsync();
        Leftovers.Clear();
        var still = targets.Where(StillInstalled).ToList();
        var gone = targets.Except(still).ToList();
        if (gone.Count == 0)
        {
            LeftoversHeadline = "Ainda instalado";
            NotifyLeftovers();
            Say(targets.Count == 1
                ? $"{targets[0].Name} ainda está instalado. Desinstale primeiro: o NEXUS só procura sobras de programas que já saíram da lista."
                : "Todos ainda aparecem como instalados. O NEXUS só procura sobras de programas que já saíram da lista.");
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
        LeftoversHeadline = found.Count == 0 ? "Nada sobrou" : LeftoverScanner.Summary(found.Select(f => f.Candidate).ToList());
        var stillNote = still.Count == 0 ? "" : string.Create(PtBr, $" {still.Count} ainda instalado(s), sem busca: {string.Join(", ", still.Take(3).Select(p => p.Name))}.");
        Say((found.Count == 0
                ? (gone.Count == 1 ? $"Nenhuma sobra de {gone[0].Name} encontrada nas pastas e chaves que o NEXUS confere." : "Nenhuma sobra encontrada nas pastas e chaves que o NEXUS confere.")
                : "Confira a lista. As de confiança alta já vêm marcadas; as baixas, não. Nada sai do lugar sem você confirmar.") + stillNote);
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
                     (user, BrokerLevel.User, UserAction, "do seu perfil"),
                     (machine, BrokerLevel.Admin, MachineAction, "de todos os usuários (pede administrador e cria um ponto de restauração)"),
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
                var partLabel = parts.Count > 1 ? string.Create(PtBr, $" (parte {n + 1} de {parts.Count})") : "";
                var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Mover {part.Length} sobra(s) {label}{partLabel} para a quarentena do NEXUS?\n\n");
                foreach (var c in part.Take(12)) text.Append(CultureInfo.InvariantCulture, $"• {c.DisplayPath}\n");
                if (part.Length > 12) text.Append(CultureInfo.InvariantCulture, $"• … e mais {part.Length - 12}\n");
                text.Append("\nNada é apagado: pastas vão para a quarentena com a lista de arquivos conferida, e chaves do registro são guardadas com todos os valores. ")
                    .Append("Você pode desfazer aqui (botão Desfazer) ou na página Aplicar. Liberar espaço não deixa o PC mais rápido.");
                var r = await Host.ApplyAsync("Remover sobras", text.ToString(), requests, level);
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
            ? string.Create(PtBr, $"{moved} sobra(s) na quarentena. Se algo fizer falta, use Desfazer.")
            : string.Create(PtBr, $"{moved} na quarentena; {failed.Count} não: {string.Join(" | ", failed.Take(3))}"));
    }

    private async Task UndoLeftoversAsync()
    {
        if (Host is null || _lastRuns.Count == 0) return;
        foreach (var (run, level) in Enumerable.Reverse(_lastRuns).ToList())
        {
            var r = await Host.RevertAsync(run, level);
            if (r is null) return; // cancelado: mantém para tentar de novo
            if (r.Response?.FullyRestored == true) _lastRuns.Remove((run, level));
            else { Say($"Desfazer não terminou: {r.Message}"); break; }
        }
        OnPropertyChanged(nameof(CanUndo));
        if (_leftoverTargets.Count > 0) await ScanLeftoversAsync();
        if (_lastRuns.Count == 0) Say("Desfeito: as sobras voltaram para onde estavam.");
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
        UpdatesHeadline = "Verificando…";
        UpdatesDetail = "Perguntando a cada gerenciador (pode levar até alguns minutos na primeira vez).";
        Managers.Clear();
        Updates.Clear();
        var statuses = await Task.WhenAll(PackageManagers.All.Select(k => Task.Run(() => PackageManagers.DetectAsync(k, _runner))));
        var rows = statuses.Select(s => new ManagerRow(s)).ToList();
        foreach (var r in rows) Managers.Add(r);
        var lists = await Task.WhenAll(statuses.Select(s => Task.Run(() => PackageManagers.ListUpdatesAsync(s, _runner))));
        foreach (var (row, list) in rows.Zip(lists))
        {
            row.Detail = !row.Installed ? "Não instalado neste PC."
                : list.Problem is { } problem ? problem
                : list.Updates.Count == 0 ? "Tudo em dia."
                : string.Create(PtBr, $"{list.Updates.Count} com versão nova.");
            var mine = list.Updates.Select(u => new UpdateRow(u)).ToList();
            foreach (var u in mine) Updates.Add(u);
            row.AddUpdates(mine);
        }
        var n = Updates.Count;
        UpdatesHeadline = n == 0 ? "Tudo em dia" : string.Create(PtBr, $"{n} {(n == 1 ? "atualização disponível" : "atualizações disponíveis")}");
        UpdatesDetail = n == 0
            ? "Nenhum gerenciador encontrado relatou versão nova."
            : string.Create(PtBr, $"{Updates.Count(u => u.CanUpgrade)} podem ser pedidas daqui. A atualização abre a janela do próprio gerenciador; o NEXUS não consegue desfazê-la.");
        OnPropertyChanged(nameof(UpdatesTabLabel));
    }

    private void StartUpdates(List<UpdateRow> rows, string title)
    {
        if (Host is null || rows.Count == 0) return;
        var groups = rows.GroupBy(r => r.Update.Manager).ToList();
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Atualizar {rows.Count} pacote(s)?\n\n");
        foreach (var g in groups)
        {
            text.Append(CultureInfo.InvariantCulture, $"{PackageManagerInfo.DisplayName(g.Key)}:\n");
            foreach (var r in g.Take(15)) text.Append(CultureInfo.InvariantCulture, $"• {r.Name}  {r.Current} → {r.Available}\n");
            if (g.Count() > 15) text.Append(CultureInfo.InvariantCulture, $"• … e mais {g.Count() - 15}\n");
        }
        text.Append("\nCada gerenciador abre numa janela visível do PowerShell, e é ele quem baixa e instala. ")
            .Append("O NEXUS não consegue desfazer uma atualização.");
        if (groups.Any(g => g.Key == PackageManagerKind.Chocolatey))
            text.Append(" O Chocolatey pede administrador: o Windows vai mostrar o aviso antes de abrir a janela dele.");
        if (!Host.Confirm(title, text.ToString())) { Say("Cancelado. Nada foi alterado."); return; }

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
                Say($"{status.DisplayName}: a janela não abriu ({ex.Message}).");
            }
        }
        if (opened.Count > 0)
            Say($"Janela aberta: {string.Join(", ", opened)}. Quando terminar, clique em \"Procurar atualizações\" para conferir.");
    }
}
