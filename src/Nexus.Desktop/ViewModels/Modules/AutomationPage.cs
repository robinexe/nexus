using System.IO;
using System.Globalization;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.Desktop.Views.Controls;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.ViewModels;

/// <summary>Linha de recomendação na página.</summary>
public sealed class RecommendationRow
{
    public RecommendationRow(Recommendation r, RelayCommand? details)
    {
        Title = r.Title;
        Reason = r.Reason;
        Gain = r.Gain is null ? "" : Loc.T("Auto.GanhoEsperado") + r.Gain;
        Kind = ScanReport.KindText(r.Kind.ToString());
        Tone = r.Kind switch
        {
            RecommendationKind.Recommend => "action",
            RecommendationKind.Avoid => "bad",
            RecommendationKind.Info => "muted",
            _ => "signal",
        };
        var meta = new List<string> { Loc.F("Auto.PrioridadeN", r.PriorityText) };
        if (r.Risk is { } risk) meta.Add(Loc.F("Asst.RiscoN", RiskModel.Text(risk)));
        if (r.Privileges == PrivilegeLevel.Admin) meta.Add(Loc.T("Auto.PedeAdministrador"));
        if (r.OneClick) meta.Add(Loc.T("Auto.EntraOtimizarAgora"));
        Meta = string.Join(" · ", meta);
        DetailsCommand = details;
        IconKey = r.ModulePrefix is { } prefix ? PageText.ActionIcon(prefix) : r.Kind == RecommendationKind.Info ? "Icon.Hardware" : "Icon.Sparkle";
        HasGainOrMeta = Gain.Length > 0 || Meta.Length > 0;
    }

    // ---- apresentação v2
    /// <summary>Âmbar = recomendado, ciano = revisar/medir, vermelho = evitar, neutro = informação.</summary>
    public Views.Controls.Tone ToneValue => Tone switch
    {
        "action" => Views.Controls.Tone.Warn,
        "signal" => Views.Controls.Tone.Info,
        "bad" => Views.Controls.Tone.Bad,
        _ => Views.Controls.Tone.Neutral,
    };
    public string IconKey { get; }
    public bool HasGainOrMeta { get; }

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
public sealed record OneClickRow(string Title, string Detail, string Level)
{
    public bool IsAdmin => Level == "administrador";
    /// <summary>Âmbar quando pede administrador (aviso do Windows); ciano na sua conta.</summary>
    public Views.Controls.Tone LevelTone => IsAdmin ? Views.Controls.Tone.Warn : Views.Controls.Tone.Info;
    public string IconKey => IsAdmin ? "Icon.Lock" : "Icon.User";
}

/// <summary>
/// Automático: lê o PC, classifica (notebook na bateria, desktop gamer, PC modesto, VM…), recomenda com o motivo, monta o
/// "Otimizar agora" (só risco baixo, confirmado pelo aplicador, com a limpeza segura e o espaço antes/depois) e resume os
/// agendamentos (configurados em Ajustes). Escrita só por Host.PlanAsync/ApplyAsync/RevertAsync.
/// </summary>
public sealed class AutomationPage : ModulePage
{
    public override string Title => Loc.T("Auto.OtimizacaoAutomatica");
    public override string Group => Loc.T("Nav.Automatico");
    public override string Subtitle => Loc.T("Auto.NexusLeSeuPc");

    private CatalogDocument? _catalog;
    private RecommendationSet? _set;
    private IReadOnlyList<(Recommendation Recommendation, ActionRequest Request)> _candidates = [];
    private OneClickPlan _plan = OneClickPlan.Empty;
    private readonly List<(Guid Run, BrokerLevel Level)> _runs = [];
    private bool _shown, _planVerified, _refreshing;
    private string _headline = Loc.T("Auto.LendoSeuPc");
    private IReadOnlyList<string> _chips = [];
    private IReadOnlyList<string> _notes = [];
    private IReadOnlyList<RecommendationRow> _recommendations = [];
    private IReadOnlyList<RecommendationRow> _avoid = [];
    private IReadOnlyList<RecommendationRow> _advice = [];
    private IReadOnlyList<OneClickRow> _planRows = [];
    private IReadOnlyList<string> _planSkipped = [];
    private string _planSummary = Loc.T("Auto.PlanoApareceDepoisLeitura");
    private string _planGains = "";
    private string _planState = "";
    private string _result = "";
    private string _scheduleText = Loc.T("Settings.LendoAgendador");
    private CleaningPlan _cleaning = CleaningPlan.None;
    private string _cleaningText = "";

    public AutomationPage()
    {
        OptimizeCommand = new RelayCommand(() => _ = OptimizeAsync(), () => !Busy && Host is not null && _set is not null);
        UndoAllCommand = new RelayCommand(() => _ = UndoAllAsync(), () => !Busy && _runs.Count > 0);
        RefreshPlanCommand = new RelayCommand(() => _ = RunAsync(VerifyPlanCoreAsync), () => !Busy && _set is not null);
        OpenSettingsCommand = new RelayCommand(() =>
        {
            if (Host?.FindPage(typeof(SettingsPage)) is { } page) Host.Navigate(page);
        });
    }

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public IReadOnlyList<string> ProfileChips { get => _chips; private set => Set(ref _chips, value); }
    public IReadOnlyList<string> ProfileNotes { get => _notes; private set => Set(ref _notes, value); }
    public IReadOnlyList<RecommendationRow> Recommendations
    {
        get => _recommendations;
        private set { if (Set(ref _recommendations, value)) Raise(nameof(RecommendCount), nameof(HasRecommendations), nameof(NoRecommendations), nameof(IsLoading)); }
    }
    public IReadOnlyList<RecommendationRow> AvoidList { get => _avoid; private set { if (Set(ref _avoid, value)) Raise(nameof(AvoidCount)); } }
    public IReadOnlyList<RecommendationRow> Advice { get => _advice; private set { if (Set(ref _advice, value)) Raise(nameof(AdviceCount)); } }
    public IReadOnlyList<OneClickRow> PlanRows { get => _planRows; private set { if (Set(ref _planRows, value)) Raise(nameof(HasPlanRows)); } }
    public IReadOnlyList<string> PlanSkipped { get => _planSkipped; private set { if (Set(ref _planSkipped, value)) Raise(nameof(HasSkipped), nameof(SkippedTitle)); } }
    public string PlanSummary { get => _planSummary; private set => Set(ref _planSummary, value); }
    public string PlanGains { get => _planGains; private set { if (Set(ref _planGains, value)) Raise(nameof(HasGains)); } }
    /// <summary>"prévia" (antes de perguntar ao aplicador) ou "confirmado pelo aplicador".</summary>
    public string PlanState { get => _planState; private set => Set(ref _planState, value); }
    public int PlanCount => _plan.Count;
    /// <summary>Ações de risco baixo que o Automático recomenda para este PC (a tela Início conta as que ainda mudariam algo).</summary>
    public IReadOnlyList<string> CandidateActionKeys => _candidates.Select(c => c.Request.ActionId).ToList();
    public string PlanCountText => PlanNumberRaw.ToString(CultureInfo.InvariantCulture);
    /// <summary>"Limpeza segura: 9 regras, 1,2 GB recuperáveis (vão para a quarentena)".</summary>
    public string CleaningText { get => _cleaningText; private set { if (Set(ref _cleaningText, value)) { Raise(nameof(HasCleaning), nameof(PlanNumber)); OnPropertyChanged(nameof(PlanCountText)); } } }
    public string Result { get => _result; private set => Set(ref _result, value); }
    public bool CanUndo => _runs.Count > 0;
    public bool HasAvoid => _avoid.Count > 0;
    public bool HasAdvice => _advice.Count > 0;

    public string ScheduleText { get => _scheduleText; private set { if (Set(ref _scheduleText, value)) OnPropertyChanged(nameof(ScheduleLines)); } }

    // ---- apresentação v2 (somente leitura)

    public bool IsLoading => _set is null;
    /// <summary>Ações no plano (o mesmo número de <see cref="PlanCountText"/>); NaN antes da leitura.</summary>
    public double PlanNumber => _set is null ? double.NaN : PlanNumberRaw;
    // Antes de o aplicador conferir, a limpeza segura conta como 1 item (o número de regras só se sabe depois).
    private int PlanNumberRaw => _planVerified ? _plan.Count + _cleaning.Requests.Count : _candidates.Count + (_cleaningText.Length > 0 ? 1 : 0);
    public bool HasPlanRows => _planRows.Count > 0;
    public bool HasSkipped => _planSkipped.Count > 0;
    public bool HasGains => _planGains.Length > 0;
    public bool HasCleaning => _cleaningText.Length > 0;
    public string UndoText => CanUndo ? Loc.T("Auto.DesfazerTudo") : "";
    public double RecommendCount => _recommendations.Count;
    public double AvoidCount => _avoid.Count;
    public double AdviceCount => _advice.Count;
    public bool HasRecommendations => _recommendations.Count > 0;
    public bool NoRecommendations => _set is not null && _recommendations.Count == 0;
    public bool IsVerified => _planVerified;
    public string SkippedTitle => _planSkipped.Count == 1 ? Loc.T("Auto.N1FicouForaPlano") : Loc.F("Auto.NFicaramForaPlano", _planSkipped.Count);
    public IReadOnlyList<string> ScheduleLines => _scheduleText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private void Raise(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }

    public RelayCommand OptimizeCommand { get; }
    public RelayCommand UndoAllCommand { get; }
    public RelayCommand RefreshPlanCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }

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
            OnPropertyChanged(nameof(CandidateActionKeys));
            _planVerified = false;
            ShowProfile(built.Set.Profile, built.Traits);
            ShowRecommendations(built.Set);
            ShowPreview();
            await ReadScheduleAsync();
            if (_shown && Host is not null) await RunAsync(VerifyPlanCoreAsync);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
        {
            Message = Loc.F("Auto.NaoFoiPossivelMontar", ex.Message);
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
        var catalog = CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory).Localized(Loc.Language);
        var set = RecommendationEngine.Default().Evaluate(profile, catalog, RiskModel.ReadDeclared(NexusServices.CatalogDirectory), NexusServices.ParametersFrom(facts));
        return new Built(catalog, set, traits);
    }

    private void ShowProfile(PcProfile p, PcTraits t)
    {
        Headline = Loc.T("Auto.SeuPc") + p.Headline;
        var chips = new List<string>();
        if (p.Has(PcProfiler.OnBattery)) chips.Add(t.BatteryPercent is { } b ? Loc.F("Auto.BateriaN", b) : Loc.T("Auto.Bateria"));
        else if (p.Has(PcProfiler.Laptop) && p.Has(PcProfiler.OnAc)) chips.Add(Loc.T("Auto.Tomada"));
        if (p.Has(PcProfiler.Gamer)) chips.Add(Loc.T("Auto.PlacaVideoDedicada"));
        if (p.Has(PcProfiler.GpuIntegratedOnly)) chips.Add(Loc.T("Auto.SoVideoIntegrado"));
        if (p.Has(PcProfiler.LowRam)) chips.Add(Loc.T("Auto.PoucaMemoria"));
        if (p.Has(PcProfiler.Hdd)) chips.Add(Loc.T("Auto.WindowsNumHdd"));
        if (p.Has(PcProfiler.LowDisk)) chips.Add(Loc.T("Auto.PoucoEspacoLivre"));
        if (p.Has(PcProfiler.Win11)) chips.Add(Loc.T("Auto.Windows11"));
        else if (p.Has(PcProfiler.Win10)) chips.Add(Loc.T("Auto.Windows10"));
        if (t.SystemDriveBus is { } bus) chips.Add(Loc.F("Auto.DiscoN", bus));
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
            "scanner." => typeof(ScannerPage),
            "system." => typeof(SystemToolsPage),
            "monitor." => typeof(MonitorPage),
            null => null,
            _ => typeof(CatalogPage),
        };
        if (type is null) return null;
        return new RelayCommand(() =>
        {
            if (Host?.FindPage(type) is { } page) Host.Navigate(page);
        });
    }

    private void ShowPreview()
    {
        PlanRows = _candidates.Select(c => new OneClickRow(c.Recommendation.Title, c.Recommendation.Reason,
            c.Recommendation.Privileges == PrivilegeLevel.Admin ? Loc.T("Pg.Administrador") : Loc.T("Auto.SuaConta"))).ToList();
        PlanSkipped = [];
        PlanSummary = _candidates.Count == 0
            ? Loc.T("Auto.NadaRiscoBaixoAplicar")
            : Loc.F("Auto.NNRiscoBaixo", _candidates.Count, (_candidates.Count == 1 ? Loc.T("Auto.AcaoCandidata") : Loc.T("Auto.AcoesCandidatas")));
        PlanGains = string.Join(Environment.NewLine, _candidates.Select(c => c.Recommendation.Gain).Where(g => g is not null).Distinct());
        PlanState = Loc.T("Auto.Previa");
        OnPropertyChanged(nameof(PlanCountText));
        Raise(nameof(PlanNumber), nameof(IsVerified));
    }

    /// <summary>Pergunta ao aplicador do usuário (sem aviso do Windows) o que de fato mudaria. Nada é escrito.</summary>
    private async Task VerifyPlanCoreAsync()
    {
        if (Host is null || _set is null) return;
        var host = Host;
        _cleaning = await PrepareCleaningAsync(host);
        if (_candidates.Count == 0) { _plan = OneClickPlan.Empty; _planVerified = true; ShowPlan(); return; }
        var (results, problem) = await OneClickPlanner.SimulateAsync(_candidates.Select(c => c.Request).ToList(), q => host.PlanAsync(q, BrokerLevel.User));
        if (problem is not null) { PlanState = Loc.T("Auto.PreviaAplicadorIndisponivel"); Message = problem; return; }
        _plan = OneClickPlanner.Build(_candidates, results);
        _planVerified = true;
        ShowPlan();
    }

    // ------------------------------------------------------------------ limpeza segura (seleção, medição, simulação)

    /// <summary>Limpeza segura conferida no aplicador: pedidos pendentes (um lote), regras, medição antes e o texto da confirmação.</summary>
    private sealed record CleaningPlan(IReadOnlyList<ActionRequest> Requests, IReadOnlyList<CleaningRule> Rules, string Batch, long Before, CleaningFolders? Folders, IReadOnlyList<string> Skipped)
    {
        public static readonly CleaningPlan None = new([], [], "", 0, null, []);
    }

    private static string UserDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.User))!;
    private static string AdminDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.Admin))!;

    private async Task<CleaningPlan> PrepareCleaningAsync(IModuleHost host)
    {
        if (_catalog is null) return CleaningPlan.None;
        var catalog = _catalog;
        var prep = await Task.Run(async () =>
        {
            var (safe, _) = SafeCleaningSelector.Select(catalog, CleaningRuleSet.BuiltIn, RiskModel.ReadDeclared(NexusServices.CatalogDirectory));
            var folders = SafeCleaningSelector.CurrentUserFolders(UserDataDirectory, AdminDataDirectory);
            var before = safe.Count == 0 ? 0 : await SafeCleaningSelector.MeasureAsync(safe.Select(x => x.Rule), folders, SystemProcessProbe.Instance);
            return (safe, folders, before);
        });
        if (prep.safe.Count == 0) { CleaningText = ""; return CleaningPlan.None; }
        var batch = CleaningBatchId.New(DateTime.UtcNow);
        var requests = SafeCleaningSelector.Requests(prep.safe, batch);
        var (results, problem) = await OneClickPlanner.SimulateAsync(requests, q => host.PlanAsync(q, BrokerLevel.User));
        if (problem is not null) { CleaningText = Loc.F("Auto.LimpezaSeguraNRecuperaveis", SafeCleaningSelector.Bytes(prep.before)); return CleaningPlan.None; }
        var pending = new List<ActionRequest>();
        var rules = new List<CleaningRule>();
        var skipped = new List<string>();
        foreach (var item in prep.safe)
        {
            var r = results.FirstOrDefault(x => x.ActionKey == item.ActionKey);
            if (r is { Outcome: "Pending" }) { pending.Add(requests.First(q => q.ActionId == item.ActionKey)); rules.Add(item.Rule); }
            else if (r is { Outcome: "Blocked" }) skipped.Add($"{item.Title}: {r.Detail}");
        }
        CleaningText = pending.Count == 0
            ? Loc.T("Auto.LimpezaSeguraNadaLimpar")
            : Loc.F("Auto.LimpezaSeguraNN", pending.Count, (pending.Count == 1 ? Loc.T("Auto.Regra") : Loc.T("Auto.Regras")), SafeCleaningSelector.Bytes(prep.before))
              + (skipped.Count > 0 ? Loc.F("Auto.NPuladaSPrograma", skipped.Count) : "");
        return new CleaningPlan(pending, rules, batch, prep.before, prep.folders, skipped);
    }

    private string CleaningConfirmation()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Loc.F("Auto.LimpezaSeguraNN2", _cleaning.Requests.Count, (_cleaning.Requests.Count == 1 ? Loc.T("Auto.Regra") : Loc.T("Auto.Regras")), SafeCleaningSelector.Bytes(_cleaning.Before)));
        foreach (var rule in _cleaning.Rules) sb.AppendLine(CultureInfo.InvariantCulture, $"• {rule.Title}");
        sb.Append(Loc.T("Auto.SoCachesTemporariosSua"));
        return sb.ToString();
    }

    private void ShowPlan()
    {
        PlanRows = _plan.User.Concat(_plan.Admin).Select(i => new OneClickRow(i.Recommendation.Title, i.Recommendation.Reason,
            i.Level == BrokerLevel.Admin ? Loc.T("Pg.Administrador") : Loc.T("Auto.SuaConta"))).ToList();
        PlanSkipped = _plan.Skipped.Select(s => $"{s.Recommendation.Title}: {s.Reason}").ToList();
        PlanSummary = _plan.Summary;
        PlanGains = string.Join(Environment.NewLine, _plan.Gains);
        PlanState = Loc.T("Auto.ConfirmadoPeloAplicador");
        if (_plan.IsEmpty && _cleaning.Requests.Count > 0) PlanSummary = Loc.T("Auto.AjustesCliqueJaEstao");
        OnPropertyChanged(nameof(PlanCount));
        OnPropertyChanged(nameof(PlanCountText));
        Raise(nameof(PlanNumber), nameof(IsVerified));
    }

    // ------------------------------------------------------------------ escrita (só pelo Host)

    private Task OptimizeAsync() => RunAsync(async () =>
    {
        if (Host is null) return;
        await VerifyPlanCoreAsync();
        if (!_planVerified) return; // aplicador indisponível: mensagem já mostrada
        if (_plan.IsEmpty && _cleaning.Requests.Count == 0) { Message = _plan.Summary; return; }
        _runs.Clear();
        var lines = new List<string>();
        var cleaning = _cleaning;
        var cleaned = false;
        foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
        {
            var requests = _plan.Requests(level);
            var confirmation = _plan.Confirmation(level);
            if (level == BrokerLevel.User && cleaning.Requests.Count > 0)
            {
                requests = [.. requests, .. cleaning.Requests];
                confirmation = (_plan.User.Count > 0 ? confirmation + Environment.NewLine + Environment.NewLine : "") + CleaningConfirmation();
            }
            if (requests.Count == 0) continue;
            var title = level == BrokerLevel.User ? Loc.T("Auto.OtimizarAgora") : Loc.T("Auto.OtimizarAgoraMudancasSistema");
            var r = await Host.ApplyAsync(title, confirmation, requests, level);
            if (level == BrokerLevel.User && r is { Response.Ok: true } && cleaning.Requests.Count > 0) cleaned = true;
            if (r is null) { lines.Add(level == BrokerLevel.User ? Loc.T("Shell.CanceladoNadaFoiAlterado") : Loc.T("Auto.MudancasSistemaCanceladasSua")); break; }
            lines.Add(r.Message);
            if (r.Response is { Ok: true, RunId: { } run }) _runs.Add((run, level));
            else break;
        }
        if (cleaned && cleaning.Folders is { } folders)
        {
            var after = await Task.Run(() => SafeCleaningSelector.MeasureAsync(cleaning.Rules, folders, SystemProcessProbe.Instance));
            var moved = await Task.Run(() => MovedBytes(cleaning.Batch));
            lines.Add(Loc.F("Auto.EspacoRecuperavelNasRegras", SafeCleaningSelector.Bytes(cleaning.Before), SafeCleaningSelector.Bytes(after)) +
                      Loc.F("Auto.NForamQuarentenaMesmo", SafeCleaningSelector.Bytes(moved)));
        }
        Result = string.Join(Environment.NewLine, lines);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoText));
        await VerifyPlanCoreAsync(); // o que foi aplicado passa a "já está assim"
    });

    private static long MovedBytes(string batch)
    {
        long total = 0;
        foreach (var dir in new[] { UserDataDirectory, AdminDataDirectory })
        {
            try { total += QuarantineStore.ReadBatches(dir).Where(b => b.BatchId == batch).Sum(b => b.Rules.Sum(r => r.MovedBytes)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    /// <summary>
    /// "Verificar agora" (bandeja): grava o relatório da leitura atual (somente leitura) em %LocalAppData%\NEXUS\reports.
    /// Devolve o HTML (ou null) e um resumo de uma linha.
    /// </summary>
    public async Task<(string? Report, string Summary)> SaveReportAsync()
    {
        var facts = Host?.Facts;
        if (facts is null || _set is null || _catalog is null) return (null, Loc.T("Auto.LeituraPcAindaNao"));
        var set = _set;
        var version = _catalog.CatalogVersion;
        return await Task.Run(() =>
        {
            var findings = DiagnosticRules.Evaluate(facts);
            var scan = ScanReport.From(facts, findings, set, version, DateTimeOffset.Now) with { Cleaning = CleaningOverview.Read(UserDataDirectory, AdminDataDirectory) };
            var files = scan.Save(ScanReport.DefaultDirectory, html: true);
            var attention = findings.Count(f => f.Severity == DiagnosticSeverity.Attention);
            return ((string?)files.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)),
                Loc.F("Auto.NPontoSPedem", attention, set.Actionable.Count()));
        });
    }

    private Task UndoAllAsync() => RunAsync(async () =>
    {
        if (Host is null) return;
        var lines = new List<string>();
        // Ordem inversa: o que foi aplicado por último é desfeito primeiro.
        foreach (var (run, level) in Enumerable.Reverse(_runs).ToList())
        {
            var r = await Host.RevertAsync(run, level);
            if (r is null) { lines.Add(Loc.T("Auto.DesfazerCancelado")); break; }
            lines.Add(r.Message);
            if (r.Response?.FullyRestored == true) _runs.Remove((run, level));
        }
        Result = string.Join(Environment.NewLine, lines);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoText));
        await VerifyPlanCoreAsync();
    });

    /// <summary>Resumo dos agendamentos (somente leitura). Ligar, mudar e desligar ficam em Ajustes.</summary>
    private async Task ReadScheduleAsync()
    {
        var (scan, clean, legacy) = await Task.Run(() => (
            AutomationHandlers.ReadTaskStatus(NexusTaskKind.Scan, AutomationScheduledScanHandler.DefaultExecutable),
            AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, AutomationScheduledScanHandler.DefaultExecutable),
            AutomationHandlers.ReadScheduledScanState(AutomationScheduledScanHandler.DefaultExecutable).State));
        var lines = new List<string> { Loc.F("Auto.VerificacaoN", scan.Describe()), Loc.F("Auto.LimpezaSeguraN", clean.Describe()) };
        if (legacy != ScanSchedule.Off) lines.Add(Loc.T("Auto.VerificacaoSemanalAntigaTambem"));
        ScheduleText = string.Join(Environment.NewLine, lines);
    }
}
