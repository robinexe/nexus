using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Nexus.Assistant;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.Desktop.Views.Controls;
using Tone = Nexus.Desktop.Views.Controls.Tone;
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

    // ---- apresentação v2
    /// <summary>Ícone de linha da etapa (Themes/Icons.xaml).</summary>
    public string IconKey => AssistantVisuals.IconFor(info.Id);
    /// <summary>Âmbar = atual, verde = feita, neutro = pulada ou ainda não feita.</summary>
    public Tone Tone => State switch { "current" => Tone.Warn, "done" => Tone.Good, _ => Tone.Neutral };
    public bool IsDone => State == "done";
    public bool IsCurrent => State == "current";
    public bool IsSkipped => State == "skipped";
    public bool IsFirst => info.Number == 1;
    public bool IsLast => info.Number == AssistantSteps.Count;
    /// <summary>"Limpeza: feita" para o leitor de tela e a dica.</summary>
    public string Tip => Loc.F("Shell.EtapaNN", info.Number, info.Title) + State switch { "current" => " (agora)", "done" => " (feita)", "skipped" => " (pulada)", _ => "" };
}

/// <summary>Número do que a etapa encontrou, pronto para o StatTile (tom do design system e ícone pelo tom).</summary>
public sealed class FactCard(StepFact fact)
{
    public string Label => fact.Label;
    public string Value => fact.Value;
    public string? Detail => fact.Detail;
    public Tone Tone => fact.Tone switch { "good" => Tone.Good, "warn" => Tone.Warn, "bad" => Tone.Bad, _ => Tone.Info };
    /// <summary>Ícone pelo tom (✓, !, ×, i), para ResourceByKey.</summary>
    public string IconKey => DesignMath.IconKey(Tone);
}

/// <summary>
/// Número do relatório final. Quando o antes e o depois são números da mesma unidade (GB, "x de y", GHz…), vira barras
/// antes → depois (BeforeAfter); senão, um cartão com o texto.
/// </summary>
public sealed record ReportMetricCard(string Label, string Display, string? Note, double Before, double After, NumberKind Kind, bool HigherIsBetter)
{
    public bool HasBars => !double.IsNaN(Before) && !double.IsNaN(After);
    public bool NoBars => !HasBars;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

/// <summary>Ícones, ilustrações e leitura de números do Assistente (lógica pura, testável).</summary>
public static class AssistantVisuals
{
    public static string IconFor(AssistantStepId id) => id switch
    {
        AssistantStepId.Welcome => "Icon.Sparkle",
        AssistantStepId.MyPc => "Icon.Pc",
        AssistantStepId.Cleaning => "Icon.Clean",
        AssistantStepId.Programs => "Icon.Programs",
        AssistantStepId.Startup => "Icon.Startup",
        AssistantStepId.Privacy => "Icon.Privacy",
        AssistantStepId.Security => "Icon.Security",
        AssistantStepId.Drivers => "Icon.Drivers",
        AssistantStepId.Memory => "Icon.Ram",
        AssistantStepId.Performance => "Icon.Bolt",
        AssistantStepId.Measurement => "Icon.Measure",
        AssistantStepId.Schedules => "Icon.Auto",
        _ => "Icon.Export",
    };

    public static string IllustrationFor(AssistantStepId id) => id switch
    {
        AssistantStepId.MyPc => "Illo.Pc",
        AssistantStepId.Cleaning => "Illo.Cleaning",
        AssistantStepId.Programs => "Illo.Programs",
        AssistantStepId.Startup => "Illo.Startup",
        AssistantStepId.Privacy => "Illo.Privacy",
        AssistantStepId.Security => "Illo.Security",
        AssistantStepId.Drivers => "Illo.Drivers",
        AssistantStepId.Memory => "Illo.Ram",
        AssistantStepId.Performance => "Illo.Automation",
        AssistantStepId.Measurement => "Illo.Measure",
        AssistantStepId.Schedules => "Illo.Settings",
        AssistantStepId.Report => "Illo.History",
        _ => "Illo.Assistant",
    };

    // Fica pt-BR de propósito: lê números que as bibliotecas escrevem em pt-BR ("9,6 GB"), não formata texto da tela.
    private static readonly System.Globalization.CultureInfo Pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// Lê "12,4 GB" (bytes), "3 de 10" (3), "2,35 GHz" ou "15" (número). Devolve o valor, o tipo e a unidade (para só comparar
    /// lados com a mesma unidade); NaN quando não é número.
    /// </summary>
    public static (double Value, NumberKind Kind, string Unit) Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return (double.NaN, NumberKind.Number, "");
        var de = t.IndexOf(" de ", StringComparison.Ordinal);
        if (de > 0) t = t[..de];
        var end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] is ',' or '.')) end++;
        if (end == 0 || !double.TryParse(t[..end], System.Globalization.NumberStyles.Number, Pt, out var value)) return (double.NaN, NumberKind.Number, "");
        var unit = t[end..].Trim();
        var factor = unit.ToUpperInvariant() switch
        {
            "B" => 1d, "KB" => 1024d, "MB" => 1024d * 1024, "GB" => 1024d * 1024 * 1024, "TB" => 1024d * 1024 * 1024 * 1024, _ => 0d,
        };
        return factor > 0 ? (value * factor, NumberKind.Bytes, "bytes") : (value, NumberKind.Number, unit);
    }

    /// <summary>Menor é melhor para espaço ainda recuperável e tempo de boot; para o resto, maior é melhor.</summary>
    public static bool HigherIsBetter(string key) => key is not ("cleaning" or "boot");

    public static ReportMetricCard Card(string key, MetricRecord m)
    {
        var (b, kind, bu) = Parse(m.Before);
        var (a, _, au) = Parse(m.After);
        var bars = !double.IsNaN(b) && !double.IsNaN(a) && string.Equals(bu, au, StringComparison.Ordinal);
        return new ReportMetricCard(m.Label, m.Display, m.Note, bars ? b : double.NaN, bars ? a : double.NaN, kind, HigherIsBetter(key));
    }
}

/// <summary>Uma mudança proposta, com a marcação do usuário.</summary>
public sealed class ChangeRow(ProposedChange change, Fit? fit = null) : ObservableObject
{
    /// <summary>Veredito do hardware para esta mudança (neutro sem perfil): "não recomendado" desmarca por padrão.</summary>
    public Fit Fit { get; } = fit ?? Fit.Neutral;
    private bool _isSelected = Simple.QuickTweakSelection.IsRecommended(change.SelectedByDefault, fit ?? Fit.Neutral);

    public ProposedChange Change { get; } = change;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string Title => Change.Title;
    public string Detail => Change.Detail;
    /// <summary>"Recomendado para o seu PC: …" / "Não indicado para notebook: …", ou vazio.</summary>
    public string FitNote => Simple.FitText.Sentence(Fit);
    public bool HasFitNote => FitNote.Length > 0;
    public Views.Controls.Tone FitTone => Fit.IsRecommended ? Views.Controls.Tone.Good : Views.Controls.Tone.Warn;
    public string BeforeAfter => Change.BeforeAfter;
    public bool HasBeforeAfter => Change.BeforeAfter.Length > 0;
    public string Meta => Loc.F("Asst.RiscoNN", Change.RiskText, Change.LevelText) + (Change.Undoable ? Loc.T("Asst.Desfazer") : Loc.T("Asst.EfeitoImediato")) +
                          (Change.OptIn ? Loc.T("Asst.Opcional") : "");
    /// <summary>good (risco baixo), warn (médio/opcional), bad (alto/experimental).</summary>
    public string Tone => Change.Experimental || Change.Risk == ActionRisk.High ? "bad" : Change.Risk == ActionRisk.Medium || Change.OptIn ? "warn" : "good";
    /// <summary>Tom do selo de risco no design system.</summary>
    public Views.Controls.Tone RiskTone => Tone switch { "bad" => Views.Controls.Tone.Bad, "warn" => Views.Controls.Tone.Warn, _ => Views.Controls.Tone.Good };
    public string RiskChip => Loc.F("Asst.RiscoN", Change.RiskText);
    public string LevelChip => Change.LevelText;
    public bool Undoable => Change.Undoable;
    public void Reset() => IsSelected = Simple.QuickTweakSelection.IsRecommended(Change.SelectedByDefault, Fit);
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
    public override string Title => Loc.T("Asst.AssistenteCompleto");
    public override string Group => Loc.T("Nav.Automatico");
    public override string Subtitle => Loc.T("Asst.PassoPassoTodasFuncoes");

    private readonly AssistantStateStore _store = new(AssistantStateStore.DefaultPath);
    private AssistantState _state = new();
    private AssistantFlow _flow;
    private AssistantCatalog? _catalog;
    private StepScan? _scan;
    private PcSummary? _pc;
    private StepPlanners.PcMeasurements? _measured;
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
        StartAutoCommand = new RelayCommand(() => _ = RunAsync(RunAutomaticAsync), () => !Busy && Host?.Facts is not null);
        ProcessView.PropertyChanged += (_, e) => { if (_autoRunning && e.PropertyName == nameof(AssistantProcess.Percent)) OnPropertyChanged(nameof(AutoPercent)); };
        RefreshStart();
    }

    // ------------------------------------------------------------------ propriedades da tela

    public bool InFlow { get => _inFlow; private set { if (Set(ref _inFlow, value)) OnPropertyChanged(nameof(ShowStart)); } }
    public bool ShowStart => !_inFlow;
    public string StartText { get => _startText; private set => Set(ref _startText, value); }
    public bool HasSaved
    {
        get => _hasSaved;
        private set
        {
            if (!Set(ref _hasSaved, value)) return;
            OnPropertyChanged(nameof(StartActionText));
            OnPropertyChanged(nameof(StartActionCommand));
            OnPropertyChanged(nameof(RestartText));
        }
    }

    public IReadOnlyList<StepChip> StepBar { get => _stepBar; private set => Set(ref _stepBar, value); }
    public string StepCounter => Loc.F("Asst.EtapaNN", AssistantStepTexts.Localize(_flow.CurrentInfo).Number, AssistantSteps.Count);
    public string StepTitle => AssistantStepTexts.Localize(_flow.CurrentInfo).Title;
    public string StepChecks => AssistantStepTexts.Localize(_flow.CurrentInfo).Checks;
    public string StepWhy => AssistantStepTexts.Localize(_flow.CurrentInfo).Why;
    public string StepGlyph => AssistantStepTexts.Localize(_flow.CurrentInfo).Glyph;
    public double Progress => (double)_flow.Completed / AssistantSteps.Count;
    public string ProgressText => Loc.F("Asst.NNEtapas", _flow.Completed, AssistantSteps.Count);

    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) OnPropertyChanged(nameof(ShowStep)); } }
    public bool ShowStep => _inFlow && !_scanning;
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public IReadOnlyList<StepFact> Facts { get => _facts; private set { if (Set(ref _facts, value)) OnPropertyChanged(nameof(FactCards)); } }
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
            return (rec == 1 ? Loc.T("Asst.N1MudancaRecomendada") : Loc.F("Asst.NMudancasRecomendadas", rec)) + (other > 0 ? Loc.F("Asst.NVoceDecidirDesmarcadas", other) : "") +
                   (Changes.Any(c => c.Change.Level == BrokerLevel.Admin) ? Loc.T("Asst.AlgumasPedemAdministradorAviso") : "");
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

    /// <summary>Tela de progresso (lendo, aplicando, montando o relatório): fica na frente da página enquanto a etapa trabalha.</summary>
    public AssistantProcess ProcessView { get; } = new();

    /// <summary>A barra das 13 etapas para a tela de progresso.</summary>
    private List<ProcessSegment> Segments() => AssistantStepTexts.All.Select(s => new ProcessSegment(s.Number, s.ShortName,
        s.Id == _state.Current ? "current" : _state.StatusOf(s.Id) switch { StepStatus.Done => "done", StepStatus.Skipped => "skipped", _ => "pending" })).ToList();

    // ---- apresentação v2 (somente leitura)

    /// <summary>Ícone e ilustração da etapa atual (chaves de recurso, para ResourceByKey).</summary>
    public string StepIconKey => AssistantVisuals.IconFor(_state.Current);
    public string StepIllustrationKey => AssistantVisuals.IllustrationFor(_state.Current);
    /// <summary>Etapas concluídas (0–100), para o anel que enche a cada etapa.</summary>
    public double ProgressPercent => 100.0 * _flow.Completed / AssistantSteps.Count;
    public static double StepCount => AssistantSteps.Count;
    public IReadOnlyList<FactCard> FactCards => _facts.Select(f => new FactCard(f)).ToList();
    public bool HasFacts => _facts.Count > 0;
    /// <summary>Botão principal da tela inicial: retomar (quando há progresso salvo) ou começar.</summary>
    public string StartActionText => _hasSaved ? Loc.T("Asst.RetomarOndeParei") : Loc.T("Asst.Comecar");
    public RelayCommand StartActionCommand => _hasSaved ? ResumeCommand : StartCommand;
    public string RestartText => _hasSaved ? Loc.T("Asst.ComecarNovo") : "";

    // Relatório como números (para StatTile) e métricas com antes → depois.
    public double ReportAppliedNumber => _report?.AppliedCount ?? double.NaN;
    public double ReportSkippedNumber => _report?.Skipped.Count ?? double.NaN;
    public double ReportUserActionsNumber => _report?.UserActions.Count ?? double.NaN;
    public IReadOnlyList<ReportMetricCard> ReportCards => _report is null ? []
        : _state.Metrics.Select(kv => AssistantVisuals.Card(kv.Key, kv.Value)).ToList();

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
        RefreshStartCore();
        BuildStepBar();
    }

    private void RefreshStartCore()
    {
        HasSaved = false;
        try
        {
            if (_store.Exists)
            {
                var (saved, status) = _store.Load();
                if (status == LoadStatus.Resumed)
                {
                    // O estado salvo vale também depois de concluído: é ele que guarda o que "Desfazer tudo" desfaz.
                    _state = saved;
                    _flow = new AssistantFlow(_state);
                    OnPropertyChanged(nameof(CanUndoAll));
                }
                if (status == LoadStatus.Resumed && !saved.Finished)
                {
                    HasSaved = true;
                    StartText = Loc.F("Asst.VoceParouEtapaN", (int)saved.Current, AssistantSteps.Count, AssistantStepTexts.Info(saved.Current).Title, saved.UpdatedAt.ToLocalTime()) +
                                Loc.F("Asst.NMudancaSJa", saved.Changes.Count(c => c.Outcome == ChangeOutcome.Applied));
                    return;
                }
                if (status == LoadStatus.Corrupt) { StartText = Loc.T("Asst.ProgressoSalvoEstavaIlegivel"); return; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        StartText = Host?.Facts is null ? Loc.T("Asst.EsperandoLeituraPcTerminar") : Loc.T("Asst.Leva1020Minutos");
    }

    private async Task BeginAsync(bool fresh)
    {
        if (Host?.Facts is null) return;
        var catalogDir = NexusServices.CatalogDirectory;
        _catalog ??= await Task.Run(() => AssistantCatalog.Load(catalogDir));
        if (fresh)
        {
            if ((_hasSaved || _state.HasUndoableRuns) && !Host.Confirm(Loc.T("Asst.ComecarNovo"),
                    Loc.T("Asst.ComecarZeroJaFoi") +
                    Loc.T("Asst.DesfazerTudoAssistenteFez2")))
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
        ProcessView.Close();
        Save();
        InFlow = false;
        RefreshStart();
        Message = Loc.T("Asst.ProgressoSalvoUseRetomar");
    }

    private void Save()
    {
        try { _store.Save(_state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = Loc.F("Asst.NaoConseguiSalvarProgresso", ex.Message); }
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ etapas

    private void RefreshHeader() => BuildStepBar();

    /// <summary>Trilha das 13 etapas (também na tela inicial, mostrando o que já foi feito numa rodada salva).</summary>
    private void BuildStepBar()
    {
        StepBar = AssistantStepTexts.All.Select(s => new StepChip(s,
            s.Id == _state.Current ? "current" : _state.StatusOf(s.Id) switch { StepStatus.Done => "done", StepStatus.Skipped => "skipped", _ => "pending" },
            new RelayCommand(() => _ = RunAsync(() => GoToAsync(s.Id)), () => CanAct))).ToList();
        foreach (var name in new[] { nameof(StepCounter), nameof(StepTitle), nameof(StepChecks), nameof(StepWhy), nameof(StepGlyph), nameof(Progress),
                     nameof(ProgressText), nameof(IsReportStep), nameof(IsNormalStep), nameof(CanUndoAll), nameof(ShowStep),
                     nameof(StepIconKey), nameof(StepIllustrationKey), nameof(ProgressPercent) })
            OnPropertyChanged(name);
    }

    private async Task GoToAsync(AssistantStepId step)
    {
        if (!_flow.GoTo(step)) { Message = Loc.T("Asst.TermineOuPuleEtapas"); return; }
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

    /// <param name="deep">Variante demorada (procurar no Windows Update/winget): a tela de progresso usa o tempo típico dela.</param>
    private async Task ScanAsync(AssistantStepId step, Func<CancellationToken, Task<StepScan>>? custom = null, bool deep = false)
    {
        var host = Host;
        var facts = host?.Facts;
        if (host is null || facts is null || _catalog is null) return;
        _cancel?.Cancel();
        using var cts = new CancellationTokenSource();
        _cancel = cts;
        Scanning = true;
        ScanStatus = Loc.F("Asst.VerificandoN", AssistantStepTexts.Info(step).Checks);
        var tracker = ProcessView.Begin(step, Loc.T("Asst.Verificando"), apply: false, Segments(), canCancel: true);
        var read = tracker.Add("read", deep ? Loc.T("Asst.ProcurandoAtualizacoesPodeLevar") : Loc.T("Asst.LendoPcSoLeitura"), ProcessTimeModel.ScanSeconds(step, deep));
        var plan = tracker.Add("plan", Loc.T("Asst.ConferindoAplicadorMudariaNada"), ProcessTimeModel.Planning);
        tracker.Start(read, ScanStatus);
        using var closeOnCancel = cts.Token.Register(() => { if (ReferenceEquals(ProcessView.Tracker, tracker)) ProcessView.Close(); });
        ProcessSummary? summary = null;
        try
        {
            var scan = custom is not null ? await custom(cts.Token) : await ReadAsync(step, facts, cts.Token);
            if (cts.IsCancellationRequested) return;
            tracker.Finish(read, ProcessTaskStatus.Done, scan.Facts.Count == 1 ? Loc.T("Asst.N1ItemLido") : Loc.F("Asst.NItensLidos", scan.Facts.Count));
            var notes = scan.Notes.ToList();
            var changes = scan.Changes.Where(ChangeSelection.IsAllowed).ToList();
            if (changes.Count > 0)
            {
                ScanStatus = Loc.T("Asst.ConferindoAplicadorFatoMudaria");
                tracker.Start(plan, ScanStatus);
                var asked = changes.Count;
                var (keep, dropped, problem) = await StepPlanners.PlanAsync(changes, q => host.PlanAsync(q, BrokerLevel.User));
                changes = [.. keep];
                if (dropped.Count > 0) notes.Insert(0, Loc.F("Asst.NItemNsJa", dropped.Count));
                if (problem is not null) notes.Insert(0, Loc.F("Asst.AplicadorIndisponivelConferirAgora", problem));
                tracker.Finish(plan, problem is null ? ProcessTaskStatus.Done : ProcessTaskStatus.Skipped,
                    problem is null ? Loc.F("Asst.NNMudariamAlgo", changes.Count, asked) : Loc.T("Asst.AplicadorIndisponivelAgoraEle"));
            }
            else tracker.Finish(plan, ProcessTaskStatus.Skipped, Loc.T("Asst.NadaConferir"));
            _scan = scan with { Changes = changes, Notes = notes };
            foreach (var (key, m) in scan.Metrics)
                if (!_state.Metrics.TryGetValue(key, out var old) || old.Before is null) _state.SetMetric(key, m.Label, before: m.Value);
            foreach (var u in scan.UserActions) _state.AddUserAction(u);
            ShowScan(_scan, step);
            Save();
            summary = ProcessSummary.ForScan(scan.Facts.Count, changes.Count, tracker.Elapsed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            ShowScan(StepScan.Unavailable(AssistantStepTexts.Info(step).Title, ex.Message), step);
            summary = new ProcessSummary(Loc.T("Asst.NaoConseguiLerEsta"), ProcessTaskStatus.Failed);
        }
        catch (OperationCanceledException) { ScanStatus = Loc.T("Asst.VerificacaoCancelada"); }
        finally
        {
            if (ReferenceEquals(_cancel, cts)) _cancel = null;
            Scanning = false;
        }
        if (!ReferenceEquals(ProcessView.Tracker, tracker)) return; // outra leitura já tomou a tela
        if (_auto) return; // modo automático: a próxima etapa toma a tela sem a pausa do resumo
        if (summary is not null && !cts.IsCancellationRequested) await ProcessView.FinishAsync(summary);
        else ProcessView.Close();
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
                var (scan, _) = await AssistantReaders.CleaningAsync(catalog, ct);
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
        var hardware = Host?.HardwareProfile;
        foreach (var c in scan.Changes) Changes.Add(new ChangeRow(c, hardware is null ? null : HardwareRules.ForAction(hardware, c.Request.ActionId)));
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
                list.Add(new ExtraAction(Loc.T("Asst.AbrirAplicarRecuperar"), Open(typeof(BrokerPage)), true));
                break;
            case AssistantStepId.MyPc:
                list.Add(new ExtraAction(_measured is null ? Loc.T("Asst.MedirCpuRam15") : Loc.T("Asst.MedirNovo"), new RelayCommand(() => _ = RunAsync(MeasureAsync), () => CanAct)));
                list.Add(new ExtraAction(Loc.T("Asst.AbrirLaboratorioMemoria"), Open(typeof(MemoryLabPage))));
                list.Add(new ExtraAction(Loc.T("Asst.AbrirLaboratorioVideo"), Open(typeof(GpuLabPage))));
                break;
            case AssistantStepId.Cleaning:
                list.Add(new ExtraAction(Loc.T("Asst.AbrirLimpezaTodasRegras"), Open(typeof(CleaningPage))));
                break;
            case AssistantStepId.Programs:
                list.Add(new ExtraAction(Loc.T("Asst.ProcurarAtualizacoes"), new RelayCommand(() => _ = RunAsync(() => ScanAsync(step, ct => Task.Run(() => AssistantReaders.ProgramsAsync(true, ct), ct), deep: true)), () => CanAct)));
                list.Add(new ExtraAction(Loc.T("Asst.RevisarPaginaProgramas"), new RelayCommand(OpenProgramsWithSelection, () => CanAct), true));
                break;
            case AssistantStepId.Startup:
                list.Add(new ExtraAction(Loc.T("Asst.AbrirInicializacao"), Open(typeof(StartupPage))));
                list.Add(new ExtraAction(Loc.T("Asst.AbrirServicos"), Open(typeof(ServicesPage))));
                break;
            case AssistantStepId.Privacy:
                list.Add(new ExtraAction(Loc.T("Asst.AbrirPrivacidadeItemItem"), Open(typeof(PrivacyPage))));
                break;
            case AssistantStepId.Security:
                list.Add(new ExtraAction(Loc.T("Asst.VerificacaoRapidaDefender"), new RelayCommand(() => _ = RunAsync(DefenderQuickScanAsync), () => CanAct)));
                list.Add(new ExtraAction(Loc.T("Asst.RevisarPaginaSeguranca"), Open(typeof(SecurityPage))));
                break;
            case AssistantStepId.Drivers:
                list.Add(new ExtraAction(Loc.T("Asst.ProcurarWindowsUpdate"), new RelayCommand(() => _ = RunAsync(() => ScanAsync(step, ct => Task.Run(() => AssistantReaders.Drivers(_catalog!, true, ct), ct), deep: true)), () => CanAct), true));
                list.Add(new ExtraAction(Loc.T("Asst.AbrirAtualizarDrivers"), Open(typeof(DriverUpdatesPage))));
                break;
            case AssistantStepId.Memory:
                list.Add(new ExtraAction(Loc.T("Asst.AbrirMemoriaDiagnostico"), Open(typeof(MemoryDoctorPage))));
                break;
            case AssistantStepId.Performance:
                list.Add(new ExtraAction(Loc.T("Asst.AbrirOtimizacaoAutomatica"), Open(typeof(AutomationPage))));
                break;
            case AssistantStepId.Measurement:
                list.Add(new ExtraAction(Loc.T("Asst.LigarMedicaoAutomaticaMostrador"), new RelayCommand(EnableMeasurement, () => CanAct), true));
                list.Add(new ExtraAction(Loc.T("Asst.AbrirMedir"), Open(typeof(MeasurePage))));
                break;
            case AssistantStepId.Schedules:
                list.Add(new ExtraAction(Loc.T("Asst.AbrirAjustes"), Open(typeof(SettingsPage))));
                break;
        }
        return list;
    }

    private async Task MeasureAsync()
    {
        ScanStatus = Loc.T("Asst.MedindoProcessadorMemoria15");
        Scanning = true;
        var tracker = ProcessView.Begin(AssistantStepId.MyPc, Loc.T("Asst.Medindo"), apply: false, Segments());
        var task = tracker.Add("measure", Loc.T("Asst.MedindoProcessadorMemoria"), 15);
        tracker.Start(task, ScanStatus);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _measured = await Task.Run(() => AssistantReaders.Measure(cts.Token));
            tracker.Finish(task, ProcessTaskStatus.Done, "medido");
        }
        catch
        {
            ProcessView.Close();
            throw;
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
            ? Loc.F("Asst.NProgramaSMarcados", marked)
            : Loc.T("Asst.AbraListaMarqueNao"));
    }

    private async Task DefenderQuickScanAsync()
    {
        if (Host is null || !Host.Confirm(Loc.T("Asst.VerificacaoRapidaDefender"),
                Loc.T("Asst.NexusVaiPedirMicrosoft") +
                Loc.T("Asst.NadaAlteradoAlgoFor")))
            return;
        Scanning = true;
        ScanStatus = Loc.T("Asst.VerificacaoRapidaDefenderAndamento");
        // O Defender não informa %: a barra anda pelo tempo típico de uma verificação rápida (estimativa) e o texto é a última
        // linha que o próprio MpCmdRun escreveu.
        var tracker = ProcessView.Begin(AssistantStepId.Security, Loc.T("Asst.VerificandoDefender"), apply: false, Segments(), canCancel: true);
        var task = tracker.Add("defender", Loc.T("Asst.VerificacaoRapidaMicrosoftDefender"), 180);
        tracker.Start(task, ScanStatus);
        var lines = new Progress<string>(line => { if (ReferenceEquals(ProcessView.Tracker, tracker) && !task.IsFinished) tracker.SetText(task, Loc.F("Asst.DefenderN", line)); });
        IProgress<string> report = lines;
        try
        {
            using var cts = new CancellationTokenSource();
            _cancel = cts;
            using var closeOnCancel = cts.Token.Register(() => { if (ReferenceEquals(ProcessView.Tracker, tracker)) ProcessView.Close(); });
            var result = await Task.Run(() => DefenderScanner.ScanAsync(ScanKind.Quick, null, (line, _) => report.Report(line), cts.Token));
            _state.SetMetric("defender", Loc.T("Asst.VerificacaoRapidaDefender"), after: result.Summary);
            StepResult = Loc.F("Asst.DefenderN", result.Summary);
            tracker.Finish(task, ProcessTaskStatus.Done, result.Summary);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { StepResult = Loc.F("Asst.DefenderNaoFoiPossivel", ex.Message); ProcessView.Close(); }
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
            _state.Changes.Add(new ChangeRecord(AssistantStepId.Measurement, Loc.T("Asst.MedicaoAutomaticaMostradorAbrir"), "desligados", "ligados",
                BrokerLevel.User, "baixo", true, ChangeOutcome.Applied, Loc.T("Asst.NaoAlteraWindows"), null));
        StepResult = Loc.T("Asst.MedicaoAutomaticaLigadaJogue");
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
        if (chosen.Count == 0) { Message = Loc.T("Asst.NadaMarcado"); return; }
        var step = _state.Current;
        var tracker = ProcessView.Begin(step, Loc.T("Shell.Aplicando"), apply: true, Segments());
        StepApplyResult result;
        ProcessSummary summary;
        try
        {
            var prep = tracker.Add("prep", Loc.T("Asst.PreparandoMudancasEscolhidas"), 0.5);
            tracker.Start(prep);
            var lookup = chosen.Any(c => c.Request.ActionId == StepPlanners.RestorePointAction)
                ? tracker.Add("restore-lookup", Loc.T("Asst.ProcurandoPontoRestauracaoRecente"), ProcessTimeModel.RestorePointLookup) : null;
            ProcessView.Observer!.Planned(ApplyGrouping.Group(step, chosen)); // o feed mostra os lotes desde o começo
            tracker.Finish(prep, ProcessTaskStatus.Done, chosen.Count == 1 ? Loc.T("Asst.N1Escolhida") : Loc.F("Asst.NEscolhidas", chosen.Count));
            var recent = lookup is null ? null : await RecentRestorePointAsync(tracker, lookup);
            result = await AssistantRunner.ApplyAsync(_state, step, chosen, new HostWriter(Host), ProcessView.Observer, recent);
            summary = ProcessSummary.ForApply(result, ProcessView.Observer.MovedBytes, tracker.Elapsed);
        }
        catch
        {
            ProcessView.Close();
            throw;
        }
        AssistantRunner.RecordUnselected(_state, unselected);
        Save();
        if (result.Applied + result.Already + result.Refused + result.NotNeeded == 0 && result.Canceled > 0)
        {
            ProcessView.Close();
            StepResult = Loc.T("Asst.CanceladoNadaFoiAlterado");
            return;
        }
        await ProcessView.FinishAsync(summary);
        StepResult = result.Summary;
        _flow.Advance(result.Summary, _scan.Facts);
        Save();
        await EnterStepAsync();
        StepResult = Loc.F("Asst.EtapaAnteriorN", summary.Text) + (result.Refused > 0 ? Loc.T("Asst.MotivoCadaRecusaEsta") : "");
    }

    /// <summary>
    /// O ponto de restauração mais novo que dá para ler daqui (só leitura). O Windows cria no máximo um a cada 24 h: se já houver
    /// um recente, o assistente não pede outro (vale como proteção). Listar costuma exigir administrador; sem lista, quem confere
    /// é o aplicador (e a recusa "já existe um recente" também conta como protegido).
    /// </summary>
    private static async Task<DateTime?> RecentRestorePointAsync(ProcessTracker tracker, ProcessTask lookup)
    {
        tracker.Start(lookup, Loc.T("Asst.LendoPontosRestauracaoWindows"));
        try
        {
            var reading = await Task.Run(Nexus.SystemTools.SystemToolsReader.RestorePoints);
            if (reading.Points.Items is not { } items)
            {
                tracker.Finish(lookup, ProcessTaskStatus.Skipped, Loc.T("Asst.NaoDeuLerDaqui"));
                return null;
            }
            var newest = items.Where(p => p.Created is not null).MaxBy(p => p.Created)?.Created;
            tracker.Finish(lookup, ProcessTaskStatus.Done, newest is { } n
                ? (RestorePointRule.IsRecent(n, DateTime.Now) ? Loc.F("Asst.AcheiN", n) : Loc.F("Asst.MaisNovoNMais", n))
                : Loc.T("Asst.NenhumPontoWindows"));
            return newest;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            tracker.Finish(lookup, ProcessTaskStatus.Skipped, Loc.T("Asst.NaoDeuLerDaqui"));
            return null;
        }
    }

    private async Task ContinueAsync()
    {
        if (_flow.IsLast) return;
        if (Changes.Count > 0) AssistantRunner.RecordUnselected(_state, Changes.Select(c => c.Change));
        _flow.Advance(_scan?.Recommendation is { Length: > 0 } r && Changes.Count == 0 ? r : Changes.Count > 0 ? Loc.T("Asst.VerificadaNadaAplicado") : Loc.T("Asst.Verificada"), _scan?.Facts);
        Save();
        await EnterStepAsync();
    }

    private async Task SkipAsync()
    {
        _flow.Skip(Loc.T("Asst.PuladaVoce"));
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
        ScanStatus = Loc.T("Asst.LendoNovoNumerosDepois");
        var tracker = ProcessView.Begin(AssistantStepId.Report, Loc.T("Asst.MontandoRelatorio"), apply: false, Segments());
        var parts = new[]
        {
            tracker.Add("space", Loc.T("Asst.EspacoLivreDisco"), 2),
            tracker.Add("cleaning", Loc.T("Asst.AindaLimpar"), ProcessTimeModel.ScanSeconds(AssistantStepId.Cleaning)),
            tracker.Add("startup", Loc.T("AsstStep.Inicializacao"), ProcessTimeModel.ScanSeconds(AssistantStepId.Startup)),
            tracker.Add("privacy", Loc.T("Settings.Privacidade"), ProcessTimeModel.ScanSeconds(AssistantStepId.Privacy)),
            tracker.Add("security", Loc.T("AsstStep.Seguranca"), ProcessTimeModel.ScanSeconds(AssistantStepId.Security)),
        };
        var save = tracker.Add("save", Loc.T("Asst.GravandoRelatorio"), 1.5);
        // Cada leitura termina de verdade antes da próxima: o feed acompanha (Progress volta para a thread da interface).
        var readProgress = new Progress<int>(i =>
        {
            if (i > 0) tracker.Finish(parts[i - 1], ProcessTaskStatus.Done, Loc.T("Asst.LidoNovo"));
            if (i < parts.Length) tracker.Start(parts[i], Loc.F("Asst.LendoNovoN", parts[i].Title.ToLowerInvariant()));
        });
        IProgress<int> reread = readProgress;
        tracker.Start(parts[0], Loc.T("Asst.LendoNovoNumerosDepois2"));
        try
        {
            var catalog = _catalog;
            var dir = NexusServices.CatalogDirectory;
            var build = facts.WindowsBuild ?? 0;
            var after = await Task.Run(async () =>
            {
                var scans = new List<StepScan> { AssistantReaders.Welcome(catalog, facts.SystemDrive, 0) };
                reread.Report(1);
                scans.Add((await AssistantReaders.CleaningAsync(catalog, CancellationToken.None)).Scan);
                reread.Report(2);
                scans.Add(AssistantReaders.Startup(catalog, StartupToggle, dir));
                reread.Report(3);
                scans.Add(AssistantReaders.Privacy(catalog, dir, build));
                reread.Report(4);
                scans.Add(AssistantReaders.Security());
                reread.Report(5);
                return scans.SelectMany(s => s.Metrics).ToList();
            });
            await Task.Yield(); // deixa os avisos de andamento chegarem antes de seguir
            tracker.Start(save, Loc.T("Asst.GravandoRelatorioHtmlSua"));
            foreach (var (key, m) in after)
                if (_state.Metrics.ContainsKey(key)) _state.SetMetric(key, m.Label, after: m.Value);
            if (_state.Metrics.TryGetValue("boot", out var boot)) _state.Metrics["boot"] = boot with { After = null, Note = Loc.T("Asst.DepoisApareceProximoBoot") };
            _pc ??= await Task.Run(() => AssistantReaders.SummaryFor(facts));
            var report = AssistantReport.From(_state, _pc, DateTimeOffset.Now, ReportRedactor.ForCurrentUser());
            var path = await Task.Run(() => report.Save(AssistantReport.DefaultDirectory));
            _state.ReportPath = path;
            _report = report;
            Save();
            tracker.Finish(save, ProcessTaskStatus.Done, "pronto");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { Message = Loc.F("Asst.NaoConseguiGravarRelatorio2", ex.Message); }
        finally { Scanning = false; }
        if (ReferenceEquals(ProcessView.Tracker, tracker) && !_auto)
            await ProcessView.FinishAsync(_report is null
                ? new ProcessSummary(Loc.T("Asst.NaoConseguiGravarRelatorio"), ProcessTaskStatus.Failed)
                : new ProcessSummary($"{_report.AppliedCount} {(_report.AppliedCount == 1 ? Loc.T("Asst.MudancaAplicada") : Loc.T("Asst.MudancasAplicadas2"))} • {ProcessText.Duration(tracker.Elapsed)}", ProcessTaskStatus.Done));
        Facts = [];
        Changes.Clear();
        Review = [];
        Notes = [];
        Extras = [];
        UserActions = (_report?.UserActions ?? []).Select(u => new UserActionRow(u, u.Link is { } link && link.StartsWith("https://", StringComparison.Ordinal)
            ? new RelayCommand(() => OpenLink(link)) : null)).ToList();
        Recommendation = _report?.NextStep ?? "";
        foreach (var n in new[] { nameof(ReportApplied), nameof(ReportSkipped), nameof(ReportUserActions), nameof(ReportMetrics), nameof(ReportNextStep), nameof(ReportPath),
                     nameof(HasChanges), nameof(NoChanges), nameof(ReportAppliedNumber), nameof(ReportSkippedNumber), nameof(ReportUserActionsNumber), nameof(ReportCards) })
            OnPropertyChanged(n);
    }

    private void Finish()
    {
        _flow.Advance(Loc.T("Asst.RelatorioGerado"));
        Save();
        InFlow = false;
        RefreshStart();
        Message = Loc.T("Asst.AssistenteConcluidoRelatorioFica") + (_state.ReportPath ?? "");
    }

    private void OpenReport()
    {
        if (_state.ReportPath is not { } p || !File.Exists(p)) return;
        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = Loc.F("Asst.NaoConseguiAbrirRelatorio", ex.Message); }
    }

    private void ExportReport()
    {
        if (_state.ReportPath is not { } p || !File.Exists(p)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Asst.ExportarRelatorioAssistente"), FileName = Path.GetFileName(p), Filter = Loc.T("Asst.PaginaHtmlHtmlHtml") };
        if (dialog.ShowDialog() != true) return;
        try { File.Copy(p, dialog.FileName, overwrite: true); Message = Loc.F("Shell.RelatorioExportadoN", dialog.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = Loc.F("Asst.NaoConseguiExportarN", ex.Message); }
    }

    private void OpenLink(string url)
    {
        if (Host is null || !url.StartsWith("https://", StringComparison.Ordinal)) return;
        if (!Host.Confirm(Loc.T("Asst.AbrirNavegador"), Loc.F("Asst.AbrirEstaPaginaNavegador", url))) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = Loc.F("Asst.NaoConseguiAbrirNavegador", ex.Message); }
    }

    // ------------------------------------------------------------------ modo automático (modo simples: "Otimizar meu PC com segurança")

    private bool _auto, _autoRunning, _autoApplying;
    private int _autoIndex;
    private string _autoSummary = "";
    private double _autoBytes;
    private int _autoApplied;

    /// <summary>Roda todas as etapas sozinho, aplicando só o que é de risco baixo e recomendado (<see cref="AutoPilot"/>).</summary>
    public RelayCommand StartAutoCommand { get; }
    public bool AutoRunning { get => _autoRunning; private set { if (Set(ref _autoRunning, value)) RaiseAuto(); } }
    /// <summary>"Pronto! Liberamos 9,3 GB e ajustamos 12 coisas." (vazio até terminar uma rodada automática).</summary>
    public string AutoSummary { get => _autoSummary; private set { if (Set(ref _autoSummary, value)) RaiseAuto(); } }
    public bool AutoDone => !_autoRunning && _autoSummary.Length > 0;
    public bool AutoIdle => !_autoRunning && _autoSummary.Length == 0;
    /// <summary>Andamento da rodada inteira (0–100): cada etapa vale uma parte igual (metade lendo, metade aplicando) e o relatório a última.</summary>
    public double AutoPercent
    {
        get
        {
            var total = AutoPilot.Steps.Count + 1;
            var within = Math.Clamp(ProcessView.Percent / 100, 0, 1);
            var part = _autoIndex >= AutoPilot.Steps.Count ? within : _autoApplying ? 0.5 + within / 2 : within / 2;
            return Math.Clamp(100.0 * (_autoIndex + part) / total, 0, 100);
        }
    }
    /// <summary>Qual dos 4 itens está acontecendo agora (null = nenhum: antes de começar ou no relatório).</summary>
    public AutoPilot.Phase? AutoPhase => _autoRunning && _autoIndex < AutoPilot.Steps.Count ? AutoPilot.PhaseOf(AutoPilot.Steps[_autoIndex]) : null;
    /// <summary>Item terminado: todas as etapas dele já passaram (ou a rodada acabou).</summary>
    public bool IsPhaseDone(AutoPilot.Phase phase) => AutoDone || (_autoRunning && AutoPilot.Steps.Select((s, i) => (s, i)).Where(x => AutoPilot.PhaseOf(x.s) == phase).All(x => x.i < _autoIndex));
    /// <summary>Chamado quando uma rodada automática termina (a janela relê o PC para a nota da tela Início).</summary>
    public event EventHandler? AutoFinished;
    /// <summary>"O que só você pode fazer" juntado pelo assistente (para a tela Problemas do modo simples).</summary>
    public IReadOnlyList<UserAction> PendingUserActions => _state.UserActions;

    private void RaiseAuto()
    {
        foreach (var n in new[] { nameof(AutoDone), nameof(AutoIdle), nameof(AutoPercent), nameof(AutoPhase) }) OnPropertyChanged(n);
        AutoProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Andamento/fase mudou (a tela Otimizar do modo simples atualiza os 4 itens).</summary>
    public event EventHandler? AutoProgressChanged;

    /// <summary>
    /// Uma confirmação no começo (com a lista do que acontece e o aviso único do Windows); depois cada etapa do
    /// <see cref="AutoPilot"/> lê, confere no aplicador e aplica só o que <see cref="AutoPilot.IsAutomatic"/> aceita, sem perguntar
    /// de novo. As outras etapas ficam "puladas" (o passo a passo do modo completo continua com tudo). Termina com o relatório
    /// e um resumo curto. "Desfazer tudo" continua cobrindo esta rodada e as anteriores.
    /// </summary>
    private async Task RunAutomaticAsync()
    {
        var host = Host;
        if (host?.Facts is null) return;
        if (!host.Confirm(Loc.T("Simple.Opt.ConfirmTitle"), Loc.T("Simple.Opt.ConfirmText")))
        {
            Message = Loc.T("Shell.CanceladoNadaFoiAlterado");
            return;
        }
        var catalogDir = NexusServices.CatalogDirectory;
        _catalog ??= await Task.Run(() => AssistantCatalog.Load(catalogDir));
        // Rodada nova, mas o "Desfazer tudo" continua valendo para o que as rodadas anteriores fizeram.
        var previous = _state;
        _state = new AssistantState();
        _state.Runs.AddRange(previous.Runs.Where(r => !r.Reverted));
        foreach (var (k, v) in previous.AppPreferences) _state.AppPreferences[k] = v;
        _flow = new AssistantFlow(_state);
        _report = null;
        _autoBytes = 0;
        _autoApplied = 0;
        _autoIndex = 0;
        _autoApplying = false;
        AutoSummary = "";
        _auto = true;
        AutoRunning = true;
        InFlow = true;
        Save();
        try
        {
            while (!_flow.IsLast)
            {
                var step = _state.Current;
                var index = -1;
                for (var i = 0; i < AutoPilot.Steps.Count; i++) if (AutoPilot.Steps[i] == step) index = i;
                if (index < 0) { _flow.Skip(Loc.T("Simple.Opt.SkippedAuto")); continue; }
                _autoIndex = index;
                _autoApplying = false;
                RaiseAuto();
                _scan = null; // leitura que falhar não pode reaplicar o que a etapa anterior propôs
                await ScanAsync(step);
                var scan = _scan;
                var chosen = scan is null ? [] : AutoPilot.Select(scan.Changes, host.HardwareProfile);
                var summary = scan is null ? Loc.T("Asst.Verificada") : scan.Recommendation;
                if (chosen.Count > 0)
                {
                    _autoApplying = true;
                    RaiseAuto();
                    var tracker = ProcessView.Begin(step, Loc.T("Shell.Aplicando"), apply: true, Segments());
                    var lookup = chosen.Any(c => c.Request.ActionId == StepPlanners.RestorePointAction)
                        ? tracker.Add("restore-lookup", Loc.T("Asst.ProcurandoPontoRestauracaoRecente"), ProcessTimeModel.RestorePointLookup) : null;
                    ProcessView.Observer!.Planned(ApplyGrouping.Group(step, chosen));
                    var recent = lookup is null ? null : await RecentRestorePointAsync(tracker, lookup);
                    var result = await AssistantRunner.ApplyAsync(_state, step, chosen, new ConfirmedWriter(host), ProcessView.Observer, recent);
                    _autoBytes += ProcessView.Observer.MovedBytes;
                    _autoApplied += result.Applied;
                    summary = result.Summary;
                }
                if (scan is not null) AssistantRunner.RecordUnselected(_state, scan.Changes.Where(c => !chosen.Contains(c)));
                _flow.Advance(summary, scan?.Facts);
                Save();
            }
            _autoIndex = AutoPilot.Steps.Count;
            _autoApplying = false;
            RaiseAuto();
            await BuildReportAsync();
            _flow.Advance(Loc.T("Asst.RelatorioGerado"));
            Save();
            AutoSummary = SimpleOptimizeText.Summary(_autoBytes, _autoApplied);
        }
        finally
        {
            _auto = false;
            ProcessView.Close();
            InFlow = false;
            AutoRunning = false;
            RefreshStart();
            OnPropertyChanged(nameof(PendingUserActions));
        }
        AutoFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Escrita do modo automático: o usuário confirmou tudo no começo; cada lote vai ao aplicador sem outra caixa.</summary>
    private sealed class ConfirmedWriter(IModuleHost host) : IAssistantWriter
    {
        public async Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level) =>
            await host.ApplyConfirmedAsync(title, requests, level, null) is { } r ? WriteOutcome.From(r) : null;

        public async Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level, IProgress<ApplyStage>? stages) =>
            await host.ApplyConfirmedAsync(title, requests, level, stages) is { } r ? WriteOutcome.From(r) : null;

        public async Task<WriteOutcome?> RevertAsync(Guid runId, BrokerLevel level) =>
            await host.RevertAsync(runId, level) is { } r ? WriteOutcome.From(r) : null;
    }

    // ------------------------------------------------------------------ desfazer tudo

    private async Task UndoAllAsync()
    {
        if (Host is null) return;
        var runs = _state.Runs.Count(r => !r.Reverted);
        if (!Host.Confirm(Loc.T("Asst.DesfazerTudoAssistenteFez"),
                Loc.F("Asst.DesfazerNExecucaoOes", runs) +
                Loc.T("Asst.PermissaoAdministradorNasMudancas")))
            return;
        var lines = (await AssistantRunner.UndoAllAsync(_state, new HostWriter(Host))).ToList();
        if (_state.AppPreferences.Count > 0 && Host.FindPage(typeof(MeasurePage)) is MeasurePage m)
        {
            if (_state.AppPreferences.TryGetValue("measure.auto", out var auto)) m.AutoMeasure = auto == "1";
            if (_state.AppPreferences.TryGetValue("measure.overlay-on-start", out var ov)) m.OverlayOnGameStart = ov == "1";
            _state.AppPreferences.Clear();
            lines.Add(Loc.T("Asst.MedicaoAutomaticaMostradorVoltaram"));
        }
        StepResult = string.Join(Environment.NewLine, lines);
        Save();
        RefreshHeader();
    }

    // ------------------------------------------------------------------ demonstração para o autoteste da interface

    /// <summary>
    /// Autoteste da interface (--selftest-ui): mostra a tela de progresso com dados de DEMONSTRAÇÃO (rotulados assim) para
    /// fotografar. Nada é lido, planejado nem aplicado; nenhum pedido vai ao aplicador.
    /// </summary>
    internal void ShowProcessDemo()
    {
        var segments = AssistantStepTexts.All.Select(s => new ProcessSegment(s.Number, s.ShortName, s.Number < 3 ? "done" : s.Number == 3 ? "current" : "pending")).ToList();
        var t = ProcessView.Begin(AssistantStepId.Cleaning, Loc.T("Asst.AplicandoDemonstracaoAutoteste"), apply: true, segments);
        var prep = t.Add("prep", Loc.T("Asst.PreparandoMudancasEscolhidas"), 0.5);
        var user = t.Add("batch:User:1", Loc.T("Asst.SuaConta4Mudancas"), 12);
        var admin = t.Add("batch:Admin:2", Loc.T("Asst.MudancasSistema2Mudancas"), 40);
        t.SetItems(user, [new(Loc.T("Asst.CacheChrome"), ProcessTaskStatus.Pending), new(Loc.T("Asst.CacheEdge"), ProcessTaskStatus.Pending),
            new(Loc.T("Asst.TemporariosWindows"), ProcessTaskStatus.Pending), new(Loc.T("Asst.MiniaturasExplorador"), ProcessTaskStatus.Pending)]);
        t.SetItems(admin, [new(Loc.T("Asst.LogsAntigosWindowsUpdate"), ProcessTaskStatus.Pending), new(Loc.T("Asst.CacheOtimizacaoEntrega"), ProcessTaskStatus.Pending)]);
        t.Start(prep);
        ProcessView.AdvanceClock(TimeSpan.FromSeconds(0.6));
        t.Finish(prep, ProcessTaskStatus.Done, Loc.T("Asst.N6Escolhidas"));
        t.Start(user, Loc.T("Asst.MovendoQuarentenaCacheChrome"), estimate: true);
        ProcessView.AdvanceClock(TimeSpan.FromSeconds(9));
        t.SetItems(user, [new(Loc.T("Asst.CacheChrome"), ProcessTaskStatus.Done, Loc.T("Asst.Quarentena")), new(Loc.T("Asst.CacheEdge"), ProcessTaskStatus.Done, Loc.T("Asst.Quarentena")),
            new(Loc.T("Asst.TemporariosWindows"), ProcessTaskStatus.Done, Loc.T("Asst.Quarentena")), new(Loc.T("Asst.MiniaturasExplorador"), ProcessTaskStatus.Skipped, Loc.T("Asst.JaEstavaAssim"))]);
        t.Finish(user, ProcessTaskStatus.Done, Loc.T("Asst.N3Feitas1Nao"));
        t.Start(admin, ProcessTimeModel.BatchText(new ApplyBatch(AssistantStepId.Cleaning, BrokerLevel.Admin, 2, 2, "", "", []), 0), estimate: true);
        ProcessView.AdvanceClock(TimeSpan.FromSeconds(12));
        ProcessView.Tick();
    }

    internal void HideProcessDemo() => ProcessView.Close();

    /// <summary>Autoteste: a tela Otimizar do modo simples no meio de uma rodada (DEMONSTRAÇÃO: nada é lido nem aplicado).</summary>
    internal void ShowAutoDemo()
    {
        ShowProcessDemo();
        _autoIndex = 2; // limpeza
        _autoApplying = true;
        AutoRunning = true;
    }

    /// <summary>Autoteste: o resumo do fim da rodada automática (DEMONSTRAÇÃO, rotulado assim).</summary>
    internal void ShowAutoDoneDemo()
    {
        AutoRunning = false;
        AutoSummary = Loc.T("Simple.SelfTest.DemoPrefix") + SimpleOptimizeText.Summary(9.3 * 1024 * 1024 * 1024, 12);
    }

    internal void HideAutoDemo()
    {
        HideProcessDemo();
        _autoIndex = 0;
        _autoApplying = false;
        AutoRunning = false;
        AutoSummary = "";
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

        public async Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level, IProgress<ApplyStage>? stages) =>
            await host.ApplyAsync(title, confirmation, requests, level, stages) is { } r ? WriteOutcome.From(r) : null;

        public async Task<WriteOutcome?> RevertAsync(Guid runId, BrokerLevel level) =>
            await host.RevertAsync(runId, level) is { } r ? WriteOutcome.From(r) : null;
    }
}
