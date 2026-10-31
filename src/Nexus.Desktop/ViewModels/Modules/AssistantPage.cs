using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Nexus.Assistant;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;
using Nexus.Security;

namespace Nexus.Desktop.ViewModels;

/// <summary>Uma posição da barra de etapas.</summary>
public sealed class StepChip(StepInfo info, string state, RelayCommand go)
{
    public int Number => info.Number;
    public string Name => info.ShortName;
    public string Glyph => info.Glyph;
    /// <summary>current, done, skipped ou pending.</summary>
    public string State { get; } = state;
    public RelayCommand GoCommand { get; } = go;
}

/// <summary>Uma mudança proposta, com a marcação do usuário.</summary>
public sealed class ChangeRow(ProposedChange change) : ObservableObject
{
    private bool _isSelected = change.SelectedByDefault;

    public ProposedChange Change { get; } = change;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string Title => Change.Title;
    public string Detail => Change.Detail;
    public string BeforeAfter => Change.BeforeAfter;
    public bool HasBeforeAfter => Change.BeforeAfter.Length > 0;
    public string Meta => $"risco {Change.RiskText} · {Change.LevelText}" + (Change.Undoable ? " · dá para desfazer" : " · efeito imediato") +
                          (Change.OptIn ? " · opcional" : "");
    /// <summary>good (risco baixo), warn (médio/opcional), bad (alto/experimental).</summary>
    public string Tone => Change.Experimental || Change.Risk == ActionRisk.High ? "bad" : Change.Risk == ActionRisk.Medium || Change.OptIn ? "warn" : "good";
    public void Reset() => IsSelected = Change.SelectedByDefault;
}

/// <summary>Ação de usuário com link (abre só https, depois de confirmar).</summary>
public sealed class UserActionRow(UserAction action, RelayCommand? open)
{
    public string Title => action.Title;
    public string Why => action.Why;
    public RelayCommand? OpenCommand { get; } = open;
    public bool HasLink => OpenCommand is not null;
}

/// <summary>Botão extra de uma etapa (medir, procurar atualizações, abrir a página do módulo).</summary>
public sealed record ExtraAction(string Label, RelayCommand Command, bool Primary = false);

/// <summary>
/// Assistente completo: percorre com o usuário todas as funções do NEXUS, etapa por etapa (o que verifica, por que importa,
/// o que encontrou, o que recomenda), aplica o que ele aceitar AGRUPADO por etapa e nível (sua conta primeiro, administrador
/// depois, um aviso do Windows) e termina com o relatório. Lê pelas bibliotecas dos módulos (Nexus.Assistant), planeja pelo
/// catálogo + Host.PlanAsync e escreve só por Host.ApplyAsync/RevertAsync. Parar e retomar: estado em
/// %LocalAppData%\NEXUS\assistente\estado.json.
/// </summary>
public sealed class AssistantPage : ModulePage
{
    public override string Title => "Assistente completo";
    public override string Group => "Automático";
    public override string Subtitle => "Passo a passo por todas as funções do NEXUS: verifica, explica, ajusta com a sua confirmação e entrega o relatório.";

    private readonly AssistantStateStore _store = new(AssistantStateStore.DefaultPath);
    private AssistantState _state = new();
    private AssistantFlow _flow;
    private AssistantCatalog? _catalog;
    private StepScan? _scan;
    private PcSummary? _pc;
    private StepPlanners.PcMeasurements? _measured;
    private string _cleaningBatch = "";
    private CancellationTokenSource? _cancel;
    private bool _inFlow, _choosing, _scanning, _hasSaved;
    private string _startText = "";
    private string _scanStatus = "";
    private string _stepResult = "";
    private string _recommendation = "";
    private IReadOnlyList<StepChip> _stepBar = [];
    private IReadOnlyList<StepFact> _facts = [];
    private IReadOnlyList<ReviewItem> _review = [];
    private IReadOnlyList<UserActionRow> _userActions = [];
    private IReadOnlyList<string> _notes = [];
    private IReadOnlyList<ExtraAction> _extras = [];
    private AssistantReport? _report;

    public AssistantPage()
    {
        _flow = new AssistantFlow(_state);
        StartCommand = new RelayCommand(() => _ = RunAsync(() => BeginAsync(fresh: true)), () => !Busy && Host?.Facts is not null);
        ResumeCommand = new RelayCommand(() => _ = RunAsync(() => BeginAsync(fresh: false)), () => !Busy && _hasSaved && Host?.Facts is not null);
        ApplyRecommendedCommand = new RelayCommand(() => _ = RunAsync(() => ApplyAsync(recommended: true)), () => CanAct && Changes.Any(c => c.Change.SelectedByDefault));
        ApplySelectedCommand = new RelayCommand(() => _ = RunAsync(() => ApplyAsync(recommended: false)), () => CanAct && Changes.Any(c => c.IsSelected));
        ChooseCommand = new RelayCommand(() => Choosing = !Choosing, () => CanAct && Changes.Count > 0);
        ContinueCommand = new RelayCommand(() => _ = RunAsync(ContinueAsync), () => CanAct);
        SkipCommand = new RelayCommand(() => _ = RunAsync(SkipAsync), () => CanAct && !_flow.IsLast);
        BackCommand = new RelayCommand(() => _ = RunAsync(BackAsync), () => CanAct && _flow.CanGoBack);
        StopCommand = new RelayCommand(Stop, () => _inFlow && !Busy);
        UndoAllCommand = new RelayCommand(() => _ = RunAsync(UndoAllAsync), () => !Busy && (_state.HasUndoableRuns || _state.AppPreferences.Count > 0));
        OpenReportCommand = new RelayCommand(OpenReport, () => _state.ReportPath is { } p && File.Exists(p));
        ExportReportCommand = new RelayCommand(ExportReport, () => _state.ReportPath is { } p && File.Exists(p));
        FinishCommand = new RelayCommand(Finish, () => !Busy && _flow.IsLast && _report is not null);
        CancelScanCommand = new RelayCommand(() => _cancel?.Cancel(), () => _scanning);
        RefreshStart();
    }

    // ------------------------------------------------------------------ propriedades da tela

    public bool InFlow { get => _inFlow; private set { if (Set(ref _inFlow, value)) OnPropertyChanged(nameof(ShowStart)); } }
    public bool ShowStart => !_inFlow;
    public string StartText { get => _startText; private set => Set(ref _startText, value); }
    public bool HasSaved { get => _hasSaved; private set => Set(ref _hasSaved, value); }

    public IReadOnlyList<StepChip> StepBar { get => _stepBar; private set => Set(ref _stepBar, value); }
    public string StepCounter => $"Etapa {_flow.CurrentInfo.Number} de {AssistantSteps.Count}";
    public string StepTitle => _flow.CurrentInfo.Title;
    public string StepChecks => _flow.CurrentInfo.Checks;
    public string StepWhy => _flow.CurrentInfo.Why;
    public string StepGlyph => _flow.CurrentInfo.Glyph;
    public double Progress => (double)_flow.Completed / AssistantSteps.Count;
    public string ProgressText => $"{_flow.Completed} de {AssistantSteps.Count} etapas";

    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) OnPropertyChanged(nameof(ShowStep)); } }
    public bool ShowStep => _inFlow && !_scanning;
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public IReadOnlyList<StepFact> Facts { get => _facts; private set => Set(ref _facts, value); }
    public string Recommendation { get => _recommendation; private set => Set(ref _recommendation, value); }
    public ObservableCollection<ChangeRow> Changes { get; } = [];
    public bool HasChanges => Changes.Count > 0;
    public bool NoChanges => Changes.Count == 0;
    public string ChangesSummary
    {
        get
        {
            var rec = Changes.Count(c => c.Change.SelectedByDefault);
            var other = Changes.Count - rec;
            return (rec == 1 ? "1 mudança recomendada" : $"{rec} mudanças recomendadas") + (other > 0 ? $" · {other} para você decidir (desmarcadas)" : "") +
                   (Changes.Any(c => c.Change.Level == BrokerLevel.Admin) ? " · algumas pedem administrador (um aviso do Windows)" : "");
        }
    }
    public bool Choosing { get => _choosing; private set => Set(ref _choosing, value); }
    public IReadOnlyList<ReviewItem> Review { get => _review; private set { if (Set(ref _review, value)) OnPropertyChanged(nameof(HasReview)); } }
    public bool HasReview => _review.Count > 0;
    public IReadOnlyList<UserActionRow> UserActions { get => _userActions; private set { if (Set(ref _userActions, value)) OnPropertyChanged(nameof(HasUserActions)); } }
    public bool HasUserActions => _userActions.Count > 0;
    public IReadOnlyList<string> Notes { get => _notes; private set => Set(ref _notes, value); }
    public IReadOnlyList<ExtraAction> Extras { get => _extras; private set => Set(ref _extras, value); }
    public string StepResult { get => _stepResult; private set => Set(ref _stepResult, value); }
    public bool IsReportStep => _inFlow && _flow.IsLast;
    public bool IsNormalStep => _inFlow && !_flow.IsLast;
    public bool CanUndoAll => _state.HasUndoableRuns || _state.AppPreferences.Count > 0;

    // Relatório
    public string ReportApplied => (_report?.AppliedCount ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string ReportSkipped => (_report?.Skipped.Count ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string ReportUserActions => (_report?.UserActions.Count ?? 0).ToString(System.Globalization.CultureInfo.CurrentCulture);
    public IReadOnlyList<MetricRecord> ReportMetrics => _report?.Metrics ?? [];
    public string ReportNextStep => _report?.NextStep ?? "";
    public string ReportPath => _state.ReportPath ?? "";

    private bool CanAct => _inFlow && !Busy && !_scanning;

    public RelayCommand StartCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand ApplyRecommendedCommand { get; }
    public RelayCommand ApplySelectedCommand { get; }
    public RelayCommand ChooseCommand { get; }
    public RelayCommand ContinueCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand UndoAllCommand { get; }
    public RelayCommand OpenReportCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public RelayCommand FinishCommand { get; }
    public RelayCommand CancelScanCommand { get; }

    // ------------------------------------------------------------------ início, parar e retomar

    public override Task OnShownAsync()
    {
        if (!_inFlow) RefreshStart();
        return Task.CompletedTask;
    }

    public override Task OnFactsReadyAsync(SystemFacts facts)
    {
        if (!_inFlow) RefreshStart();
        return Task.CompletedTask;
    }

    private void RefreshStart()
    {
        HasSaved = false;
        try
        {
            if (_store.Exists)
            {
                var (saved, status) = _store.Load();
                if (status == LoadStatus.Resumed && !saved.Finished)
                {
                    HasSaved = true;
                    StartText = $"Você parou na etapa {(int)saved.Current} de {AssistantSteps.Count} ({AssistantSteps.Info(saved.Current).Title}) em {saved.UpdatedAt.ToLocalTime():dd/MM HH:mm}. " +
                                $"{saved.Changes.Count(c => c.Outcome == ChangeOutcome.Applied)} mudança(s) já aplicada(s).";
                    return;
                }
                if (status == LoadStatus.Corrupt) { StartText = "O progresso salvo estava ilegível e foi guardado à parte. Comece de novo."; return; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        StartText = Host?.Facts is null ? "Esperando a leitura do PC terminar…" : "Leva de 10 a 20 minutos. Você confirma cada grupo de mudanças e pode parar a qualquer momento.";
    }

    private async Task BeginAsync(bool fresh)
    {
        if (Host?.Facts is null) return;
        var catalogDir = NexusServices.CatalogDirectory;
        _catalog ??= await Task.Run(() => AssistantCatalog.Load(catalogDir));
        if (fresh)
        {
            if (_hasSaved && !Host.Confirm("Começar de novo", "Começar do zero? O que já foi aplicado continua aplicado (e pode ser desfeito na página Aplicar/Histórico), mas o progresso salvo do assistente é substituído."))
                return;
            _state = new AssistantState();
        }
        else
        {
            var (saved, status) = _store.Load();
            _state = status == LoadStatus.Resumed ? saved : new AssistantState();
        }
        _flow = new AssistantFlow(_state);
        _report = null;
        Save();
        InFlow = true;
        await EnterStepAsync();
    }

    private void Stop()
    {
        _cancel?.Cancel();
        Save();
        InFlow = false;
        RefreshStart();
        Message = "Progresso salvo. Use \"Retomar\" para continuar de onde parou.";
    }

    private void Save()
    {
        try { _store.Save(_state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = $"Não consegui salvar o progresso: {ex.Message}"; }
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ etapas

    private void RefreshHeader()
    {
        StepBar = AssistantSteps.All.Select(s => new StepChip(s,
            s.Id == _state.Current ? "current" : _state.StatusOf(s.Id) switch { StepStatus.Done => "done", StepStatus.Skipped => "skipped", _ => "pending" },
            new RelayCommand(() => _ = RunAsync(() => GoToAsync(s.Id)), () => CanAct))).ToList();
        foreach (var name in new[] { nameof(StepCounter), nameof(StepTitle), nameof(StepChecks), nameof(StepWhy), nameof(StepGlyph), nameof(Progress),
                     nameof(ProgressText), nameof(IsReportStep), nameof(IsNormalStep), nameof(CanUndoAll), nameof(ShowStep) })
            OnPropertyChanged(name);
    }

    private async Task GoToAsync(AssistantStepId step)
    {
        if (!_flow.GoTo(step)) { Message = "Termine (ou pule) as etapas anteriores primeiro."; return; }
        Save();
        await EnterStepAsync();
    }

    private async Task EnterStepAsync()
    {
        RefreshHeader();
        StepResult = "";
        Choosing = false;
        Message = "";
        if (_flow.IsLast) await BuildReportAsync();
        else await ScanAsync(_state.Current);
        RefreshHeader();
    }

    private async Task ScanAsync(AssistantStepId step, Func<CancellationToken, Task<StepScan>>? custom = null)
    {
        var host = Host;
        var facts = host?.Facts;
        if (host is null || facts is null || _catalog is null) return;
        _cancel?.Cancel();
        using var cts = new CancellationTokenSource();
        _cancel = cts;
        Scanning = true;
        ScanStatus = $"Verificando: {AssistantSteps.Info(step).Checks}";
        try
        {
            var scan = custom is not null ? await custom(cts.Token) : await ReadAsync(step, facts, cts.Token);
            if (cts.IsCancellationRequested) return;
            var notes = scan.Notes.ToList();
            var changes = scan.Changes.Where(ChangeSelection.IsAllowed).ToList();
            if (changes.Count > 0)
            {
                ScanStatus = "Conferindo com o aplicador o que de fato mudaria (nada é alterado)…";
                var (keep, dropped, problem) = await StepPlanners.PlanAsync(changes, q => host.PlanAsync(q, BrokerLevel.User));
                changes = [.. keep];
                if (dropped.Count > 0) notes.Insert(0, $"{dropped.Count} item(ns) já estão como recomendado ou não se aplicam a este PC.");
                if (problem is not null) notes.Insert(0, $"Aplicador indisponível para conferir agora ({problem}); ele confere de novo antes de escrever.");
            }
            _scan = scan with { Changes = changes, Notes = notes };
            foreach (var (key, m) in scan.Metrics)
                if (!_state.Metrics.TryGetValue(key, out var old) || old.Before is null) _state.SetMetric(key, m.Label, before: m.Value);
            foreach (var u in scan.UserActions) _state.AddUserAction(u);
            ShowScan(_scan, step);
            Save();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            ShowScan(StepScan.Unavailable(AssistantSteps.Info(step).Title, ex.Message), step);
        }
        catch (OperationCanceledException) { ScanStatus = "Verificação cancelada."; }
        finally
        {
            if (ReferenceEquals(_cancel, cts)) _cancel = null;
            Scanning = false;
        }
    }

    private Task<StepScan> ReadAsync(AssistantStepId step, SystemFacts facts, CancellationToken ct)
    {
        var catalog = _catalog!;
        var dir = NexusServices.CatalogDirectory;
        var build = facts.WindowsBuild ?? 0;
        return step switch
        {
            AssistantStepId.Welcome => Task.Run(() => AssistantReaders.Welcome(catalog, facts.SystemDrive, PendingRecovery()), ct),
            AssistantStepId.MyPc => Task.Run(() =>
            {
                var r = AssistantReaders.MyPc(catalog, facts, _measured);
                _pc = r.Summary;
                return r.Scan;
            }, ct),
            AssistantStepId.Cleaning => Task.Run(async () =>
            {
                var (scan, batch) = await AssistantReaders.CleaningAsync(catalog, ct);
                _cleaningBatch = batch;
                return scan;
            }, ct),
            AssistantStepId.Programs => Task.Run(() => AssistantReaders.ProgramsAsync(checkUpdates: false, ct), ct),
            AssistantStepId.Startup => Task.Run(() => AssistantReaders.Startup(catalog, StartupToggle, dir), ct),
            AssistantStepId.Privacy => Task.Run(() => AssistantReaders.Privacy(catalog, dir, build), ct),
            AssistantStepId.Security => Task.Run(AssistantReaders.Security, ct),
            AssistantStepId.Drivers => Task.Run(() => AssistantReaders.Drivers(catalog, searchWindowsUpdate: false, ct), ct),
            AssistantStepId.Memory => Task.Run(() => AssistantReaders.Memory(catalog, facts), ct),
            AssistantStepId.Performance => Task.Run(() => AssistantReaders.Performance(catalog, facts, NexusServices.ParametersFrom(facts)), ct),
            AssistantStepId.Measurement => Task.FromResult(MeasurementScan()),
            AssistantStepId.Schedules => Task.Run(() => AssistantReaders.Schedules(catalog), ct),
            _ => Task.FromResult(StepScan.Empty("")),
        };
    }

    /// <summary>Mesma regra da página Inicialização (StartupPage.ToggleRequest): desligar = valor "disabled".</summary>
    private static ActionRequest? StartupToggle(Nexus.Boot.Inventory.StartupItem item) =>
        StartupPage.ToggleRequest(item) is { } t ? new ActionRequest(t.Key, "disabled", t.Args) : null;

    private StepScan MeasurementScan()
    {
        var m = Host?.FindPage(typeof(MeasurePage)) as MeasurePage;
        return StepPlanners.Measurement(m?.HasPresentMon == true, m?.AutoMeasure == true, m?.OverlayOnGameStart == true);
    }

    private void ShowScan(StepScan scan, AssistantStepId step)
    {
        Facts = scan.Facts;
        Recommendation = scan.Recommendation;
        Changes.Clear();
        foreach (var c in scan.Changes) Changes.Add(new ChangeRow(c));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(NoChanges));
        OnPropertyChanged(nameof(ChangesSummary));
        Review = scan.Review;
        UserActions = scan.UserActions.Select(u => new UserActionRow(u, u.Link is { } link && link.StartsWith("https://", StringComparison.Ordinal)
            ? new RelayCommand(() => OpenLink(link)) : null)).ToList();
        Notes = scan.Notes;
        Extras = ExtrasFor(step);
    }

    // ------------------------------------------------------------------ botões extras de cada etapa

    private List<ExtraAction> ExtrasFor(AssistantStepId step)
    {
        var list = new List<ExtraAction>();
        RelayCommand Open(Type page) => new(() => { if (Host?.FindPage(page) is { } p) Host.Navigate(p); });
        switch (step)
        {
            case AssistantStepId.Welcome when Facts.Any(f => f.Label == "Alterações pela metade" && f.Tone == "bad"):
                list.Add(new ExtraAction("Abrir Aplicar › Recuperar", Open(typeof(BrokerPage)), true));
                break;
            case AssistantStepId.MyPc:
                list.Add(new ExtraAction(_measured is null ? "Medir CPU e RAM (≈15 s)" : "Medir de novo", new RelayCommand(() => _ = RunAsync(MeasureAsync), () => CanAct)));
                list.Add(new ExtraAction("Abrir Laboratório de memória", Open(typeof(MemoryLabPage))));
                list.Add(new ExtraAction("Abrir Laboratório de vídeo", Open(typeof(GpuLabPage))));
                break;
            case AssistantStepId.Cleaning:
                list.Add(new ExtraAction("Abrir Limpeza (todas as regras)", Open(typeof(CleaningPage))));
                break;
            case AssistantStepId.Programs:
                list.Add(new ExtraAction("Procurar atualizações", new RelayCommand(() => _ = RunAsync(() => ScanAsync(step, ct => Task.Run(() => AssistantReaders.ProgramsAsync(true, ct), ct))), () => CanAct)));
                list.Add(new ExtraAction("Revisar na página Programas", new RelayCommand(OpenProgramsWithSelection, () => CanAct), true));
                break;
            case AssistantStepId.Startup:
                list.Add(new ExtraAction("Abrir Inicialização", Open(typeof(StartupPage))));
                list.Add(new ExtraAction("Abrir Serviços", Open(typeof(ServicesPage))));
                break;
            case AssistantStepId.Privacy:
                list.Add(new ExtraAction("Abrir Privacidade (item a item)", Open(typeof(PrivacyPage))));
                break;
            case AssistantStepId.Security:
                list.Add(new ExtraAction("Verificação rápida do Defender", new RelayCommand(() => _ = RunAsync(DefenderQuickScanAsync), () => CanAct)));
                list.Add(new ExtraAction("Revisar na página Segurança", Open(typeof(SecurityPage))));
                break;
            case AssistantStepId.Drivers:
                list.Add(new ExtraAction("Procurar no Windows Update", new RelayCommand(() => _ = RunAsync(() => ScanAsync(step, ct => Task.Run(() => AssistantReaders.Drivers(_catalog!, true, ct), ct))), () => CanAct), true));
                list.Add(new ExtraAction("Abrir Atualizar drivers", Open(typeof(DriverUpdatesPage))));
                break;
            case AssistantStepId.Memory:
                list.Add(new ExtraAction("Abrir Memória: diagnóstico", Open(typeof(MemoryDoctorPage))));
                break;
            case AssistantStepId.Performance:
                list.Add(new ExtraAction("Abrir Otimização automática", Open(typeof(AutomationPage))));
                break;
            case AssistantStepId.Measurement:
                list.Add(new ExtraAction("Ligar medição automática e mostrador", new RelayCommand(EnableMeasurement, () => CanAct), true));
                list.Add(new ExtraAction("Abrir Medir", Open(typeof(MeasurePage))));
                break;
            case AssistantStepId.Schedules:
                list.Add(new ExtraAction("Abrir Ajustes", Open(typeof(SettingsPage))));
                break;
        }
        return list;
    }

    private async Task MeasureAsync()
    {
        ScanStatus = "Medindo o processador e a memória (≈15 s). Feche jogos e programas pesados para um número justo…";
        Scanning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _measured = await Task.Run(() => AssistantReaders.Measure(cts.Token));
        }
        finally { Scanning = false; }
        await ScanAsync(AssistantStepId.MyPc);
    }

    private void OpenProgramsWithSelection()
    {
        if (Host?.FindPage(typeof(ProgramsPage)) is not ProgramsPage page) return;
        var ids = Review.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        _ = MarkProgramsAsync(page, ids);
    }

    /// <summary>Abre a página Programas com os candidatos já marcados (só marca; quem desinstala é o usuário, lá, com confirmação).</summary>
    private async Task MarkProgramsAsync(ProgramsPage page, HashSet<string> ids)
    {
        await page.OnShownAsync();
        var marked = 0;
        foreach (var row in page.Programs.Where(r => ids.Contains(r.Program.Id)))
        {
            row.IsChecked = true;
            if (row.IsChecked) marked++;
        }
        page.IsProgramsTab = true;
        Host?.Navigate(page);
        Host?.Notify(marked > 0
            ? $"{marked} programa(s) marcados pelo assistente. Confira e, se quiser, use \"Desinstalar selecionados\". Depois volte ao Assistente."
            : "Abra a lista e marque o que não usa mais. Depois volte ao Assistente.");
    }

    private async Task DefenderQuickScanAsync()
    {
        if (Host is null || !Host.Confirm("Verificação rápida do Defender",
                "O NEXUS vai pedir ao Microsoft Defender uma verificação rápida (alguns minutos), pela ferramenta oficial MpCmdRun.\n\n" +
                "Nada é alterado. Se algo for encontrado, a remoção é feita pelo próprio Defender (Segurança do Windows).\n\nContinuar?"))
            return;
        Scanning = true;
        ScanStatus = "Verificação rápida do Defender em andamento…";
        try
        {
            using var cts = new CancellationTokenSource();
            _cancel = cts;
            var result = await Task.Run(() => DefenderScanner.ScanAsync(ScanKind.Quick, null, (line, elapsed) => { }, cts.Token));
            _state.SetMetric("defender", "Verificação rápida do Defender", after: result.Summary);
            StepResult = $"Defender: {result.Summary}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { StepResult = $"Defender: não foi possível concluir ({ex.Message})."; }
        finally { Scanning = false; _cancel = null; }
        await ScanAsync(AssistantStepId.Security);
    }

    /// <summary>Preferências do próprio NEXUS (não mexem no Windows); guardadas para o "Desfazer tudo".</summary>
    private void EnableMeasurement()
    {
        if (Host?.FindPage(typeof(MeasurePage)) is not MeasurePage m) return;
        if (!_state.AppPreferences.ContainsKey("measure.auto")) _state.AppPreferences["measure.auto"] = m.AutoMeasure ? "1" : "0";
        if (!_state.AppPreferences.ContainsKey("measure.overlay-on-start")) _state.AppPreferences["measure.overlay-on-start"] = m.OverlayOnGameStart ? "1" : "0";
        var changed = !m.AutoMeasure || !m.OverlayOnGameStart;
        m.AutoMeasure = true;
        m.OverlayOnGameStart = true;
        if (changed)
            _state.Changes.Add(new ChangeRecord(AssistantStepId.Measurement, "Medição automática e mostrador ao abrir jogo (preferência do NEXUS)", "desligados", "ligados",
                BrokerLevel.User, "baixo", true, ChangeOutcome.Applied, "Não altera o Windows.", null));
        StepResult = "Medição automática ligada: jogue uma partida normal e o NEXUS mede sozinho. O mostrador aparece quando um jogo abrir.";
        Save();
        _ = ScanAsync(AssistantStepId.Measurement);
    }

    // ------------------------------------------------------------------ aplicar, pular, voltar

    private async Task ApplyAsync(bool recommended)
    {
        if (Host is null || _scan is null) return;
        if (recommended) foreach (var c in Changes) c.Reset();
        var chosen = Changes.Where(c => c.IsSelected).Select(c => c.Change).ToList();
        var unselected = Changes.Where(c => !c.IsSelected).Select(c => c.Change).ToList();
        if (chosen.Count == 0) { Message = "Nada marcado."; return; }
        var step = _state.Current;
        var result = await AssistantRunner.ApplyAsync(_state, step, chosen, new HostWriter(Host));
        AssistantRunner.RecordUnselected(_state, unselected);
        Save();
        if (result.Applied + result.Already + result.Refused == 0 && result.Canceled > 0)
        {
            StepResult = "Cancelado. Nada foi alterado nesta etapa; dá para escolher de novo ou pular.";
            return;
        }
        StepResult = result.Summary;
        _flow.Advance(result.Summary, _scan.Facts);
        Save();
        await EnterStepAsync();
        StepResult = $"Etapa anterior: {result.Summary}";
    }

    private async Task ContinueAsync()
    {
        if (_flow.IsLast) return;
        if (Changes.Count > 0) AssistantRunner.RecordUnselected(_state, Changes.Select(c => c.Change));
        _flow.Advance(_scan?.Recommendation is { Length: > 0 } r && Changes.Count == 0 ? r : Changes.Count > 0 ? "Verificada; nada aplicado." : "Verificada.", _scan?.Facts);
        Save();
        await EnterStepAsync();
    }

    private async Task SkipAsync()
    {
        _flow.Skip("Pulada por você.");
        Save();
        await EnterStepAsync();
    }

    private async Task BackAsync()
    {
        _flow.Back();
        Save();
        await EnterStepAsync();
    }

    // ------------------------------------------------------------------ relatório

    private async Task BuildReportAsync()
    {
        var host = Host;
        var facts = host?.Facts;
        if (facts is null || _catalog is null) return;
        Scanning = true;
        ScanStatus = "Lendo de novo os números para o \"depois\" e montando o relatório…";
        try
        {
            var catalog = _catalog;
            var dir = NexusServices.CatalogDirectory;
            var build = facts.WindowsBuild ?? 0;
            var after = await Task.Run(async () =>
            {
                var scans = new List<StepScan>
                {
                    AssistantReaders.Welcome(catalog, facts.SystemDrive, 0),
                    (await AssistantReaders.CleaningAsync(catalog, CancellationToken.None)).Scan,
                    AssistantReaders.Startup(catalog, StartupToggle, dir),
                    AssistantReaders.Privacy(catalog, dir, build),
                    AssistantReaders.Security(),
                };
                return scans.SelectMany(s => s.Metrics).ToList();
            });
            foreach (var (key, m) in after)
                if (_state.Metrics.ContainsKey(key)) _state.SetMetric(key, m.Label, after: m.Value);
            if (_state.Metrics.TryGetValue("boot", out var boot)) _state.Metrics["boot"] = boot with { After = null, Note = "O \"depois\" aparece no próximo boot (página Inicialização)." };
            _pc ??= AssistantReaders.Summary(facts, null);
            var report = AssistantReport.From(_state, _pc, DateTimeOffset.Now, ReportRedactor.ForCurrentUser());
            var path = await Task.Run(() => report.Save(AssistantReport.DefaultDirectory));
            _state.ReportPath = path;
            _report = report;
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Message = $"Não consegui gravar o relatório: {ex.Message}"; }
        finally { Scanning = false; }
        Facts = [];
        Changes.Clear();
        Review = [];
        Notes = [];
        Extras = [];
        UserActions = (_report?.UserActions ?? []).Select(u => new UserActionRow(u, u.Link is { } link && link.StartsWith("https://", StringComparison.Ordinal)
            ? new RelayCommand(() => OpenLink(link)) : null)).ToList();
        Recommendation = _report?.NextStep ?? "";
        foreach (var n in new[] { nameof(ReportApplied), nameof(ReportSkipped), nameof(ReportUserActions), nameof(ReportMetrics), nameof(ReportNextStep), nameof(ReportPath),
                     nameof(HasChanges), nameof(NoChanges) })
            OnPropertyChanged(n);
    }

    private void Finish()
    {
        _flow.Advance("Relatório gerado.");
        Save();
        InFlow = false;
        RefreshStart();
        Message = "Assistente concluído. O relatório fica em " + (_state.ReportPath ?? "");
    }

    private void OpenReport()
    {
        if (_state.ReportPath is not { } p || !File.Exists(p)) return;
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = $"Não consegui abrir o relatório: {ex.Message}"; }
    }

    private void ExportReport()
    {
        if (_state.ReportPath is not { } p || !File.Exists(p)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Exportar relatório do assistente", FileName = Path.GetFileName(p), Filter = "Página HTML (*.html)|*.html" };
        if (dialog.ShowDialog() != true) return;
        try { File.Copy(p, dialog.FileName, overwrite: true); Message = $"Relatório exportado para {dialog.FileName}"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = $"Não consegui exportar: {ex.Message}"; }
    }

    private void OpenLink(string url)
    {
        if (Host is null || !url.StartsWith("https://", StringComparison.Ordinal)) return;
        if (!Host.Confirm("Abrir no navegador", $"Abrir esta página no navegador?\n\n{url}\n\nO NEXUS não baixa nem instala nada.")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = $"Não consegui abrir o navegador: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ desfazer tudo

    private async Task UndoAllAsync()
    {
        if (Host is null) return;
        var runs = _state.Runs.Count(r => !r.Reverted);
        if (!Host.Confirm("Desfazer tudo que o assistente fez",
                $"Desfazer as {runs} execução(ões) do assistente, da última para a primeira? O Windows pode pedir confirmação para cada uma " +
                "(e permissão de administrador nas mudanças de sistema). A limpeza volta da quarentena para o lugar original."))
            return;
        var lines = (await AssistantRunner.UndoAllAsync(_state, new HostWriter(Host))).ToList();
        if (_state.AppPreferences.Count > 0 && Host.FindPage(typeof(MeasurePage)) is MeasurePage m)
        {
            if (_state.AppPreferences.TryGetValue("measure.auto", out var auto)) m.AutoMeasure = auto == "1";
            if (_state.AppPreferences.TryGetValue("measure.overlay-on-start", out var ov)) m.OverlayOnGameStart = ov == "1";
            _state.AppPreferences.Clear();
            lines.Add("Medição automática e mostrador voltaram ao que eram.");
        }
        StepResult = string.Join(Environment.NewLine, lines);
        Save();
        RefreshHeader();
    }

    // ------------------------------------------------------------------ leitura do diário (somente leitura)

    /// <summary>Alterações que ficaram pela metade nos dois diários (mesma leitura da janela principal, sem travar nem escrever).</summary>
    private static int PendingRecovery()
    {
        static int Count(string path)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                using var j = SqliteJournal.OpenReadOnly(path);
                return RecoveryService.FindInterrupted(j).Count(f => f.Entry.State is JournalState.BackedUp or JournalState.Applying or JournalState.Applied or JournalState.Restoring);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { return 0; }
        }
        return Count(LocalBroker.JournalPath(BrokerLevel.User)) + Count(LocalBroker.JournalPath(BrokerLevel.Admin));
    }

    /// <summary>A única via de escrita: confirmação + aplicador da janela (IModuleHost).</summary>
    private sealed class HostWriter(IModuleHost host) : IAssistantWriter
    {
        public async Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level) =>
            await host.ApplyAsync(title, confirmation, requests, level) is { } r ? WriteOutcome.From(r) : null;

        public async Task<WriteOutcome?> RevertAsync(Guid runId, BrokerLevel level) =>
            await host.RevertAsync(runId, level) is { } r ? WriteOutcome.From(r) : null;
    }
}
