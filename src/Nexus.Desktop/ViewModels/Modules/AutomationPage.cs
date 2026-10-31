using System.IO;
using System.Globalization;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.ViewModels;

/// <summary>Linha de recomendação na página.</summary>
public sealed class RecommendationRow
{
    public RecommendationRow(Recommendation r, RelayCommand? details)
    {
        Title = r.Title;
        Reason = r.Reason;
        Gain = r.Gain is null ? "" : "Ganho esperado: " + r.Gain;
        Kind = ScanReport.KindText(r.Kind.ToString());
        Tone = r.Kind switch
        {
            RecommendationKind.Recommend => "action",
            RecommendationKind.Avoid => "bad",
            RecommendationKind.Info => "muted",
            _ => "signal",
        };
        var meta = new List<string> { $"prioridade {r.PriorityText}" };
        if (r.Risk is { } risk) meta.Add($"risco {RiskModel.Text(risk)}");
        if (r.Privileges == PrivilegeLevel.Admin) meta.Add("pede administrador");
        if (r.OneClick) meta.Add("entra no Otimizar agora");
        Meta = string.Join(" · ", meta);
        DetailsCommand = details;
    }

    public string Title { get; }
    public string Reason { get; }
    public string Gain { get; }
    public string Kind { get; }
    /// <summary>action (âmbar), signal (ciano), bad (vermelho), muted.</summary>
    public string Tone { get; }
    public string Meta { get; }
    public RelayCommand? DetailsCommand { get; }
    public bool HasDetails => DetailsCommand is not null;
}

/// <summary>Item do plano do "Otimizar agora".</summary>
public sealed record OneClickRow(string Title, string Detail, string Level);

/// <summary>
/// Automático: lê o PC, classifica (notebook na bateria, desktop gamer, PC modesto, VM…), recomenda com o motivo, monta o
/// "Otimizar agora" (só risco baixo, confirmado pelo aplicador) e liga a verificação semanal SOMENTE LEITURA.
/// Escrita só por Host.PlanAsync/ApplyAsync/RevertAsync.
/// </summary>
public sealed class AutomationPage : ModulePage
{
    public override string Title => "Otimização automática";
    public override string Group => "Automático";
    public override string Subtitle => "O NEXUS lê o seu PC, propõe o que vale a pena e aplica com segurança.";

    public static IReadOnlyList<string> ScheduleChoices { get; } = ["SUN 19:00", "SAT 12:00", "WED 20:00"];

    private CatalogDocument? _catalog;
    private RecommendationSet? _set;
    private IReadOnlyList<(Recommendation Recommendation, ActionRequest Request)> _candidates = [];
    private OneClickPlan _plan = OneClickPlan.Empty;
    private readonly List<(Guid Run, BrokerLevel Level)> _runs = [];
    private bool _shown, _planVerified, _refreshing;
    private string _headline = "Lendo seu PC…";
    private IReadOnlyList<string> _chips = [];
    private IReadOnlyList<string> _notes = [];
    private IReadOnlyList<RecommendationRow> _recommendations = [];
    private IReadOnlyList<RecommendationRow> _avoid = [];
    private IReadOnlyList<RecommendationRow> _advice = [];
    private IReadOnlyList<OneClickRow> _planRows = [];
    private IReadOnlyList<string> _planSkipped = [];
    private string _planSummary = "O plano aparece depois da leitura do PC.";
    private string _planGains = "";
    private string _planState = "";
    private string _result = "";
    private string _scheduleText = "Lendo o Agendador…";
    private string _scheduleState = ScanSchedule.Off;
    private string _selectedSchedule = ScheduleChoices[0];

    public AutomationPage()
    {
        OptimizeCommand = new RelayCommand(() => _ = OptimizeAsync(), () => !Busy && Host is not null && _set is not null);
        UndoAllCommand = new RelayCommand(() => _ = UndoAllAsync(), () => !Busy && _runs.Count > 0);
        RefreshPlanCommand = new RelayCommand(() => _ = RunAsync(VerifyPlanCoreAsync), () => !Busy && _set is not null);
        ToggleScheduleCommand = new RelayCommand(() => _ = ToggleScheduleAsync(), () => !Busy && Host is not null);
    }

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public IReadOnlyList<string> ProfileChips { get => _chips; private set => Set(ref _chips, value); }
    public IReadOnlyList<string> ProfileNotes { get => _notes; private set => Set(ref _notes, value); }
    public IReadOnlyList<RecommendationRow> Recommendations { get => _recommendations; private set => Set(ref _recommendations, value); }
    public IReadOnlyList<RecommendationRow> AvoidList { get => _avoid; private set => Set(ref _avoid, value); }
    public IReadOnlyList<RecommendationRow> Advice { get => _advice; private set => Set(ref _advice, value); }
    public IReadOnlyList<OneClickRow> PlanRows { get => _planRows; private set => Set(ref _planRows, value); }
    public IReadOnlyList<string> PlanSkipped { get => _planSkipped; private set => Set(ref _planSkipped, value); }
    public string PlanSummary { get => _planSummary; private set => Set(ref _planSummary, value); }
    public string PlanGains { get => _planGains; private set => Set(ref _planGains, value); }
    /// <summary>"prévia" (antes de perguntar ao aplicador) ou "confirmado pelo aplicador".</summary>
    public string PlanState { get => _planState; private set => Set(ref _planState, value); }
    public int PlanCount => _plan.Count;
    public string PlanCountText => _planVerified ? _plan.Count.ToString(CultureInfo.InvariantCulture) : _candidates.Count.ToString(CultureInfo.InvariantCulture);
    public string Result { get => _result; private set => Set(ref _result, value); }
    public bool CanUndo => _runs.Count > 0;
    public bool HasAvoid => _avoid.Count > 0;
    public bool HasAdvice => _advice.Count > 0;

    public string ScheduleText { get => _scheduleText; private set => Set(ref _scheduleText, value); }
    public bool ScheduleOn => ScanSchedule.TryParse(_scheduleState) is not null || _scheduleState == AutomationScheduledScanHandler.Stale;
    public string SelectedSchedule { get => _selectedSchedule; set => Set(ref _selectedSchedule, value); }

    public RelayCommand OptimizeCommand { get; }
    public RelayCommand UndoAllCommand { get; }
    public RelayCommand RefreshPlanCommand { get; }
    public RelayCommand ToggleScheduleCommand { get; }

    // ------------------------------------------------------------------ leitura

    public override async Task OnFactsReadyAsync(SystemFacts facts)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var built = await Task.Run(() => Build(facts));
            _catalog = built.Catalog;
            _set = built.Set;
            _candidates = OneClickPlanner.Candidates(built.Set, built.Catalog);
            _planVerified = false;
            ShowProfile(built.Set.Profile, built.Traits);
            ShowRecommendations(built.Set);
            ShowPreview();
            await ReadScheduleAsync();
            if (_shown && Host is not null) await RunAsync(VerifyPlanCoreAsync);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
        {
            Message = $"Não foi possível montar as recomendações: {ex.Message}";
        }
        finally { _refreshing = false; }
    }

    public override async Task OnShownAsync()
    {
        _shown = true;
        if (_set is not null && !_planVerified && Host is not null) await RunAsync(VerifyPlanCoreAsync);
    }

    private sealed record Built(CatalogDocument Catalog, RecommendationSet Set, PcTraits Traits);

    private static Built Build(SystemFacts facts)
    {
        var traits = PcTraitsProbe.Read(facts.SystemDrive);
        var profile = PcProfiler.Classify(facts, traits, DiagnosticRules.Evaluate(facts));
        var catalog = CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory);
        var set = RecommendationEngine.Default().Evaluate(profile, catalog, RiskModel.ReadDeclared(NexusServices.CatalogDirectory), NexusServices.ParametersFrom(facts));
        return new Built(catalog, set, traits);
    }

    private void ShowProfile(PcProfile p, PcTraits t)
    {
        Headline = "Seu PC: " + p.Headline;
        var chips = new List<string>();
        if (p.Has(PcProfiler.OnBattery)) chips.Add(t.BatteryPercent is { } b ? $"Na bateria · {b}%" : "Na bateria");
        else if (p.Has(PcProfiler.Laptop) && p.Has(PcProfiler.OnAc)) chips.Add("Na tomada");
        if (p.Has(PcProfiler.Gamer)) chips.Add("Placa de vídeo dedicada");
        if (p.Has(PcProfiler.GpuIntegratedOnly)) chips.Add("Só vídeo integrado");
        if (p.Has(PcProfiler.LowRam)) chips.Add("Pouca memória");
        if (p.Has(PcProfiler.Hdd)) chips.Add("Windows num HDD");
        if (p.Has(PcProfiler.LowDisk)) chips.Add("Pouco espaço livre");
        if (p.Has(PcProfiler.Win11)) chips.Add("Windows 11");
        else if (p.Has(PcProfiler.Win10)) chips.Add("Windows 10");
        if (t.SystemDriveBus is { } bus) chips.Add($"Disco {bus}");
        ProfileChips = chips;
        ProfileNotes = p.Notes;
    }

    private void ShowRecommendations(RecommendationSet set)
    {
        RecommendationRow Row(Recommendation r) => new(r, DetailsFor(r));
        Recommendations = set.Actionable.Select(Row).ToList();
        AvoidList = set.Avoid.Select(Row).ToList();
        Advice = set.Info.Select(Row).ToList();
        OnPropertyChanged(nameof(HasAvoid));
        OnPropertyChanged(nameof(HasAdvice));
    }

    /// <summary>"Ver detalhes": a página do módulo dono da ação (pelo prefixo do id).</summary>
    private RelayCommand? DetailsFor(Recommendation r)
    {
        var type = r.ModulePrefix switch
        {
            "cleaning." => typeof(CleaningPage),
            "boot." or "startup." => typeof(StartupPage),
            "svc." => typeof(ServicesPage),
            "privacy." => typeof(PrivacyPage),
            "disk." => typeof(DiskPage),
            "programs." or "apps." => typeof(ProgramsPage),
            "security." => typeof(SecurityPage),
            "system." => typeof(SystemToolsPage),
            "monitor." => typeof(MonitorPage),
            null => null,
            _ => typeof(CatalogPage),
        };
        if (type is null) return null;
        return new RelayCommand(() =>
        {
            // IModuleHost não expõe a lista de páginas; a janela principal expõe (ver relatório: proposta de IModuleHost.FindPage).
            if (Host is MainViewModel main && main.Pages.FirstOrDefault(p => p.GetType() == type) is { } page) Host.Navigate(page);
        });
    }

    private void ShowPreview()
    {
        PlanRows = _candidates.Select(c => new OneClickRow(c.Recommendation.Title, c.Recommendation.Reason,
            c.Recommendation.Privileges == PrivilegeLevel.Admin ? "administrador" : "sua conta")).ToList();
        PlanSkipped = [];
        PlanSummary = _candidates.Count == 0
            ? "Nada de risco baixo a aplicar em um clique para este PC."
            : $"{_candidates.Count} {(_candidates.Count == 1 ? "ação candidata" : "ações candidatas")} de risco baixo. Conferindo com o aplicador…";
        PlanGains = string.Join(Environment.NewLine, _candidates.Select(c => c.Recommendation.Gain).Where(g => g is not null).Distinct());
        PlanState = "prévia";
        OnPropertyChanged(nameof(PlanCountText));
    }

    /// <summary>Pergunta ao aplicador do usuário (sem aviso do Windows) o que de fato mudaria. Nada é escrito.</summary>
    private async Task VerifyPlanCoreAsync()
    {
        if (Host is null || _set is null) return;
        if (_candidates.Count == 0) { _plan = OneClickPlan.Empty; _planVerified = true; ShowPlan(); return; }
        var host = Host;
        var (results, problem) = await OneClickPlanner.SimulateAsync(_candidates.Select(c => c.Request).ToList(), q => host.PlanAsync(q, BrokerLevel.User));
        if (problem is not null) { PlanState = "prévia (aplicador indisponível)"; Message = problem; return; }
        _plan = OneClickPlanner.Build(_candidates, results);
        _planVerified = true;
        ShowPlan();
    }

    private void ShowPlan()
    {
        PlanRows = _plan.User.Concat(_plan.Admin).Select(i => new OneClickRow(i.Recommendation.Title, i.Recommendation.Reason,
            i.Level == BrokerLevel.Admin ? "administrador" : "sua conta")).ToList();
        PlanSkipped = _plan.Skipped.Select(s => $"{s.Recommendation.Title}: {s.Reason}").ToList();
        PlanSummary = _plan.Summary;
        PlanGains = string.Join(Environment.NewLine, _plan.Gains);
        PlanState = "confirmado pelo aplicador";
        OnPropertyChanged(nameof(PlanCount));
        OnPropertyChanged(nameof(PlanCountText));
    }

    // ------------------------------------------------------------------ escrita (só pelo Host)

    private Task OptimizeAsync() => RunAsync(async () =>
    {
        if (Host is null) return;
        await VerifyPlanCoreAsync();
        if (!_planVerified) return; // aplicador indisponível: mensagem já mostrada
        if (_plan.IsEmpty) { Message = _plan.Summary; return; }
        _runs.Clear();
        var lines = new List<string>();
        foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
        {
            var requests = _plan.Requests(level);
            if (requests.Count == 0) continue;
            var title = level == BrokerLevel.User ? "Otimizar agora" : "Otimizar agora: mudanças de sistema";
            var r = await Host.ApplyAsync(title, _plan.Confirmation(level), requests, level);
            if (r is null) { lines.Add(level == BrokerLevel.User ? "Cancelado. Nada foi alterado." : "Mudanças de sistema canceladas; as da sua conta continuam aplicadas."); break; }
            lines.Add(r.Message);
            if (r.Response is { Ok: true, RunId: { } run }) _runs.Add((run, level));
            else break;
        }
        Result = string.Join(Environment.NewLine, lines);
        OnPropertyChanged(nameof(CanUndo));
        await VerifyPlanCoreAsync(); // o que foi aplicado passa a "já está assim"
    });

    private Task UndoAllAsync() => RunAsync(async () =>
    {
        if (Host is null) return;
        var lines = new List<string>();
        // Ordem inversa: o que foi aplicado por último é desfeito primeiro.
        foreach (var (run, level) in Enumerable.Reverse(_runs).ToList())
        {
            var r = await Host.RevertAsync(run, level);
            if (r is null) { lines.Add("Desfazer cancelado."); break; }
            lines.Add(r.Message);
            if (r.Response?.FullyRestored == true) _runs.Remove((run, level));
        }
        Result = string.Join(Environment.NewLine, lines);
        OnPropertyChanged(nameof(CanUndo));
        await VerifyPlanCoreAsync();
    });

    private async Task ReadScheduleAsync()
    {
        var (state, error) = await Task.Run(() => AutomationHandlers.ReadScheduledScanState(AutomationScheduledScanHandler.DefaultExecutable));
        _scheduleState = state;
        ScheduleText = error is not null
            ? $"Não foi possível ler o Agendador: {error}"
            : state switch
            {
                ScanSchedule.Off => "Desligada. Ligue para o NEXUS verificar o PC uma vez por semana, sem mudar nada.",
                AutomationScheduledScanHandler.Stale => "Ligada, mas apontando para outra pasta do NEXUS. Desligue e ligue de novo para atualizar.",
                AutomationScheduledScanHandler.Foreign => $"Existe uma tarefa \"{ScheduledScanTask.TaskName}\" que não é do NEXUS; ela não será tocada.",
                _ => $"Ligada: {ScanSchedule.TryParse(state)?.Describe()}. Relatórios em {ScanReport.DefaultDirectory}.",
            };
        if (ScanSchedule.TryParse(state) is { } s && ScheduleChoices.Contains(s.ToString())) SelectedSchedule = s.ToString();
        OnPropertyChanged(nameof(ScheduleOn));
    }

    private string ScheduledScanKey() =>
        _catalog?.Actions.Where(a => a.Definition.Id == AutomationHandlers.ScheduledScanActionId).MaxBy(a => a.Definition.Version)?.Definition.Key
        ?? AutomationHandlers.ScheduledScanActionId + "@1";

    private Task ToggleScheduleAsync() => RunAsync(async () =>
    {
        if (Host is null) return;
        try
        {
            var turnOn = !ScheduleOn;
            var value = turnOn ? SelectedSchedule : ScanSchedule.Off;
            var confirmation = turnOn
                ? $"Criar uma tarefa no Agendador do Windows (na sua conta, sem administrador) que roda \"nexus scan --report\" toda {ScanSchedule.TryParse(value)?.Describe()}?\n\n" +
                  "A verificação só LÊ o PC e grava um relatório. O NEXUS nunca agenda mudanças: aplicar é sempre com você presente.\n" +
                  "Dá para desligar aqui a qualquer momento."
                : "Remover a verificação semanal do Agendador do Windows?";
            var r = await Host.ApplyAsync(turnOn ? "Verificação semanal" : "Desligar verificação semanal", confirmation,
                [new ActionRequest(ScheduledScanKey(), value)], BrokerLevel.User);
            if (r is not null) Message = r.Message;
        }
        finally
        {
            await ReadScheduleAsync(); // ressincroniza o interruptor com o estado real
        }
    });
}
