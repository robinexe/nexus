using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.Desktop.Views.Controls;
using Nexus.Desktop.Views.Optimize;
using Nexus.Desktop.Views.Shell;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.ViewModels;

// ------------------------------------------------------------------ Hardware

public sealed class TableViewModel
{
    public TableViewModel(DiscoveryTable t)
    {
        Title = t.Title;
        Columns = t.Columns;
        Rows = t.Rows.Select(r => new RowViewModel(r)).ToList();
        Status = t.Status;
        Source = t.Source;
        Note = t.Note;
    }

    public string Title { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<RowViewModel> Rows { get; }
    public FactStatus Status { get; }
    public string Source { get; }
    public string? Note { get; }
    public string Header => $"{Title} ({Rows.Count})";
}

public sealed class RowViewModel(IReadOnlyList<string?> cells)
{
    public IReadOnlyList<string?> Cells { get; } = cells;
}

public sealed class SectionViewModel(DiscoverySection s)
{
    public string Title { get; } = s.Title;
    public IReadOnlyList<Fact> Facts { get; } = s.Facts;
    public IReadOnlyList<TableViewModel> Tables { get; } = s.Tables.Select(t => new TableViewModel(t)).ToList();

    // ---- apresentação v2 (somente leitura)

    /// <summary>Ícone da seção (Themes/Icons.xaml), escolhido pelo título.</summary>
    public string IconKey { get; } = PageText.SectionIcon(s.Title);
    public int DetectedCount => Facts.Count(f => f.Status == FactStatus.Detected);
    public int UnavailableCount => Facts.Count(f => f.Status == FactStatus.Unavailable);
    /// <summary>"8 valores · 1 sem leitura".</summary>
    public string CountText => PageText.Plural(Facts.Count, Loc.T("Pg.Valor"), Loc.T("Pg.Valores")) + (UnavailableCount > 0 ? Loc.F("Pg.NSemLeitura", UnavailableCount) : "");
    public bool HasTables => Tables.Count > 0;
    /// <summary>Fontes distintas da seção, para o "De onde vêm estes valores?".</summary>
    public string Sources => string.Join(" · ", Facts.Select(f => f.Source).Concat(Tables.Select(t => t.Source))
        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal));
}

public sealed class HardwarePage : PageViewModel
{
    private IReadOnlyList<SectionViewModel> _sections = [];
    public override string Title => "Hardware";
    public override string Group => Loc.T("Nav.MeuPc");
    public override string Subtitle => Loc.T("Pg.LeituraSistemaCadaValor");
    public IReadOnlyList<SectionViewModel> Sections
    {
        get => _sections;
        set
        {
            if (!Set(ref _sections, value)) return;
            foreach (var name in (string[])[nameof(DetectedCount), nameof(InferredCount), nameof(UnavailableCount), nameof(HasData), nameof(IsLoading),
                         nameof(TotalFacts), nameof(DetectedPercent), nameof(DetectedNumber), nameof(InferredNumber), nameof(UnavailableNumber)])
                OnPropertyChanged(name);
        }
    }

    // Derivados para a legenda do topo da página.
    public int DetectedCount => CountFacts(FactStatus.Detected);
    public int InferredCount => CountFacts(FactStatus.Inferred);
    public int UnavailableCount => CountFacts(FactStatus.Unavailable);
    public bool HasData => Sections.Count > 0;
    public bool IsLoading => Sections.Count == 0;

    /// <summary>Total de valores lidos (NaN antes da leitura).</summary>
    public double TotalFacts => HasData ? Sections.Sum(s => s.Facts.Count) : double.NaN;
    /// <summary>Parcela lida direto da fonte (0–100; NaN antes da leitura).</summary>
    public double DetectedPercent => HasData && TotalFacts > 0 ? 100.0 * DetectedCount / TotalFacts : double.NaN;
    public double DetectedNumber => HasData ? DetectedCount : double.NaN;
    public double InferredNumber => HasData ? InferredCount : double.NaN;
    public double UnavailableNumber => HasData ? UnavailableCount : double.NaN;

    private int CountFacts(FactStatus status) => Sections.SelectMany(s => s.Facts).Count(f => f.Status == status);

    public const string OwnerSectionTitle = "O que só você pode fazer";

    /// <summary>
    /// Pontos "só você pode fazer" (BIOS, hardware, software do fabricante) como uma seção da página: título, situação, explicação
    /// com o ganho medido e a fonte. Só informa; null quando não há nada a mostrar.
    /// </summary>
    public static DiscoverySection? OwnerSection(IEnumerable<DiagnosticFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var facts = findings.Where(f => f.Owner is not null).Select(f => new Fact(f.Title,
            f.Severity switch { DiagnosticSeverity.Attention => Loc.T("Pg.ValeConferir"), DiagnosticSeverity.Info => Loc.T("Pg.Saber"), _ => Loc.T("Pg.Ordem2") },
            f.Basis, $"{f.Owner!.SourceTitle} ({f.Owner.SourceUrl})", Loc.F("Pg.NNGanhoMedido", f.Evidence, f.Owner.Explanation, f.Owner.Gain))).ToList();
        return facts.Count == 0 ? null : new DiscoverySection(OwnerSectionTitle, facts);
    }
}

// ------------------------------------------------------------------ Diagnóstico

public sealed class DiagnosticsPage : PageViewModel
{
    private IReadOnlyList<DiagnosticFinding> _findings = [];
    public override string Title => Loc.T("Pg.Diagnostico");
    public override string Group => Loc.T("Nav.MeuPc");
    public override string Subtitle => Loc.T("Pg.SomenteLeituraCadaAchado");
    public IReadOnlyList<DiagnosticFinding> Findings
    {
        get => _findings;
        set
        {
            if (!Set(ref _findings, value)) return;
            foreach (var name in Derived) OnPropertyChanged(name);
        }
    }

    private static readonly string[] Derived =
    [
        nameof(Summary), nameof(Ordered), nameof(Highlights), nameof(AttentionCount), nameof(InfoCount), nameof(OkCount),
        nameof(UnavailableCount), nameof(Total), nameof(AttentionLabel), nameof(HasFindings), nameof(NoAttention),
        nameof(IsLoading), nameof(Cards), nameof(HealthScore), nameof(AttentionNumber), nameof(InfoNumber), nameof(OkNumber), nameof(UnavailableNumber),
        nameof(HealthCaption),
    ];

    // ---- apresentação v2 (somente leitura)

    public bool IsLoading => Findings.Count == 0;
    /// <summary>Achados como cartões (atenção primeiro), com ícone, tom e a página que resolve.</summary>
    public IReadOnlyList<FindingCard> Cards => Ordered.Select(f => new FindingCard(f)).ToList();
    /// <summary>"Itens em ordem" (0–100), a mesma conta do Meu PC; NaN antes da leitura.</summary>
    public double HealthScore => DesignMath.HealthScore(Findings.Count, AttentionCount) ?? double.NaN;
    public double AttentionNumber => HasFindings ? AttentionCount : double.NaN;
    public double InfoNumber => HasFindings ? InfoCount : double.NaN;
    public double OkNumber => HasFindings ? OkCount : double.NaN;
    public double UnavailableNumber => HasFindings ? UnavailableCount : double.NaN;
    public string HealthCaption => !HasFindings ? "" : AttentionCount == 0 ? Loc.T("Pg.NadaPedeAtencaoAgora") : $"{AttentionCount} {AttentionLabel}";

    // Derivados para a apresentação: atenção primeiro, depois informativo, depois ok.
    public IReadOnlyList<DiagnosticFinding> Ordered => Findings.OrderBy(f => Rank(f.Severity)).ToList();
    public IReadOnlyList<DiagnosticFinding> Highlights => Ordered.Where(f => f.Severity == DiagnosticSeverity.Attention).Take(3).ToList();
    public int AttentionCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Attention);
    public int InfoCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Info);
    public int OkCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Ok);
    public int UnavailableCount => Findings.Count(f => f.Basis == FactStatus.Unavailable);
    public int Total => Findings.Count;
    public string AttentionLabel => AttentionCount == 1 ? Loc.T("Pg.PontoConferir") : Loc.T("Pg.PontosConferir2");
    public bool HasFindings => Findings.Count > 0;
    public bool NoAttention => HasFindings && AttentionCount == 0;

    private static int Rank(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Attention => 0,
        DiagnosticSeverity.Info => 1,
        _ => 2,
    };
    public string Summary => Loc.F("Pg.NPontoSAtencao", Findings.Count(f => f.Severity == DiagnosticSeverity.Attention)) +
                             Loc.F("Pg.NNaoAvaliavelIs", Findings.Count(f => f.Basis == FactStatus.Unavailable), Findings.Count);
}

/// <summary>Achado do diagnóstico pronto para o cartão: ícone do módulo, tom da severidade e a página que resolve.</summary>
public sealed class FindingCard(DiagnosticFinding finding)
{
    public DiagnosticFinding Finding { get; } = finding;
    public string Title => Finding.Title;
    public string Evidence => Finding.Evidence;
    public DiagnosticSeverity Severity => Finding.Severity;
    public FactStatus Basis => Finding.Basis;
    public string? RelatedActionId => Finding.RelatedActionId;
    public bool HasRelatedAction => !string.IsNullOrEmpty(Finding.RelatedActionId);
    public string IconKey => ShellMap.IconForFinding(Finding.Id);
    public Tone Tone => Finding.Severity switch
    {
        DiagnosticSeverity.Attention => Tone.Warn,
        DiagnosticSeverity.Info => Tone.Info,
        _ => Tone.Good,
    };
    public string SeverityText => Finding.Severity switch
    {
        DiagnosticSeverity.Attention => Loc.T("Pg.ValeConferir"),
        DiagnosticSeverity.Info => Loc.T("Pg.Saber"),
        _ => Loc.T("Pg.Ordem2"),
    };
    /// <summary>Página que resolve ou aprofunda (nome do tipo, para ShellCommands.Navigate).</summary>
    public string Target => ShellMap.PageForFinding(Finding.Id);
    public string ActionText => ShellMap.ActionFor(Target);
    public bool HasTarget => Target != "DiagnosticsPage";

    // ---- "só você pode fazer": explicação, ganho medido e fonte (só informa; nada é aplicado)

    public bool IsOwnerAction => Finding.Owner is not null;
    public string OwnerExplanation => Finding.Owner?.Explanation ?? "";
    public string OwnerGain => Finding.Owner?.Gain ?? "";
    public string OwnerSource => Finding.Owner?.SourceTitle ?? "";
    public string OwnerSourceUrl => Finding.Owner?.SourceUrl ?? "";
    /// <summary>Abre a fonte no navegador: só as páginas fixas de <see cref="OwnerChecks.SourceUrls"/>.</summary>
    public RelayCommand OpenSourceCommand => _open ??= new RelayCommand(OpenSource, () => OwnerChecks.SourceUrls.Contains(OwnerSourceUrl));
    private RelayCommand? _open;

    private void OpenSource()
    {
        if (!OwnerChecks.SourceUrls.Contains(OwnerSourceUrl)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OwnerSourceUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

// ------------------------------------------------------------------ Catálogo

public sealed class CatalogRow : ObservableObject
{
    private string _current = Loc.T("Pg.Lendo");
    private string? _readError;
    private bool _include;

    public CatalogRow(CatalogEntry entry, ActionInstance? instance, string? notInstantiableReason, CapabilityLabel readLabel)
    {
        Entry = entry;
        Instance = instance;
        NotInstantiableReason = notInstantiableReason;
        ReadLabel = readLabel;
        _include = instance is not null;
        if (instance is null) _current = Loc.T("Pg.NaoLido") + notInstantiableReason;
    }

    public CatalogEntry Entry { get; }
    public ActionInstance? Instance { get; }
    public string? NotInstantiableReason { get; }
    public CapabilityLabel ReadLabel { get; }
    public string ReadLabelText => CapabilityText.Of(ReadLabel);

    public string Id => Entry.Definition.Id;
    public string Title => Entry.Definition.Title;
    public string Classification => Entry.Definition.Classification.ToString();
    public string Handler => Entry.Definition.HandlerKind;
    public string Privileges => Entry.PrivilegesRaw;
    public string Desired => Instance?.Desired.Describe() ?? Entry.DesiredValue ?? "—";
    public string Persistence => Entry.Definition.Persistence == PersistenceKind.Session ? Loc.T("Pg.Sessao") : Loc.T("Pg.Persistente");
    public string Reboot => Entry.Definition.Reboot.ToString();
    public string Mechanism => Entry.Definition.Mechanism;
    public string Risks => Entry.Definition.Risks;
    public string Evidence => string.Join("\n", Entry.Definition.Evidence.Select(e => $"[{e.Level}] {e.Source}"));
    public string? Condition => Entry.Condition;
    public string? Verification => Entry.Verification;
    public string? Revert => Entry.Revert;
    public string Target => string.Join("  ·  ", Entry.TargetTemplate.Select(kv => $"{kv.Key}={kv.Value}"));
    public string ApplyLabel { get; } = Loc.T("Pg.AplicavelNestePcPela");
    public bool CanSimulate => Instance is not null;

    public string Current
    {
        get => _current;
        set { if (Set(ref _current, value)) RaiseReadState(); }
    }
    public string? ReadError
    {
        get => _readError;
        set { if (Set(ref _readError, value)) RaiseReadState(); }
    }
    public bool Include { get => _include; set => Set(ref _include, value && CanSimulate); }

    // ---- textos de apoio para a vitrine (somente leitura, derivados do catálogo e da leitura)

    public string ClassificationText => CatalogText.Classification(Entry.Definition.Classification);
    public string PersistenceText => CatalogText.Persistence(Entry.Definition.Persistence);
    public bool IsSession => Entry.Definition.Persistence == PersistenceKind.Session;
    public string RebootText => CatalogText.Reboot(Entry.Definition.Reboot);
    public bool NeedsRestart => Entry.Definition.Reboot != RebootRequirement.None;
    public string PrivilegeText => CatalogText.Privileges(Entry.PrivilegesRaw);
    public bool NeedsAdmin => Entry.PrivilegesRaw.Contains("Admin", StringComparison.Ordinal);
    public bool IsExperimental => Entry.Definition.Classification == Nexus.Core.Classification.Experimental;

    public IReadOnlyList<EvidenceItem> EvidenceItems => Entry.Definition.Evidence
        .Select(e => { var (text, strength) = CatalogText.Evidence(e.Level); return new EvidenceItem(text, e.Source, strength); })
        .OrderByDescending(e => e.Strength)
        .ToList();

    /// <summary>Alvo técnico, uma linha por chave (para o bloco recolhido "Detalhes técnicos").</summary>
    public IReadOnlyList<string> TargetLines => Instance is not null
        ? Instance.Target.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} = {kv.Value}").ToList()
        : Entry.TargetTemplate.Select(kv => $"{kv.Key} = {kv.Value}").ToList();

    /// <summary>"reading", "unread", "error", "same" (já está como o desejado) ou "change" (mudaria).</summary>
    public string ReadState =>
        Instance is null ? "unread"
        : ReadError is not null ? "error"
        : _current == Loc.T("Pg.Lendo") ? "reading"
        : _current == Desired || _current.StartsWith(Desired + "  (", StringComparison.Ordinal) ? "same"
        : "change";

    /// <summary>Valor atual para exibição: um traço quando a ação nem chega a ser lida (o motivo aparece no detalhe).</summary>
    public string CurrentShown => Instance is null ? "—" : _current;

    public string ReadStateText => ReadState switch
    {
        "unread" => Loc.T("Pg.AindaNaoPraLer"),
        "error" => Loc.T("Pg.NaoDeuPraLer"),
        "reading" => Loc.T("Pg.LendoValorAtual"),
        "same" => Loc.T("Pg.JaEstaAssimSeu"),
        _ => Loc.T("Pg.MudariaEsteAjuste"),
    };

    /// <summary>Tom do selo da leitura: verde já está assim, âmbar mudaria, vermelho falhou, ciano lendo, neutro sem leitura.</summary>
    public Tone ReadTone => ReadState switch
    {
        "same" => Tone.Good,
        "change" => Tone.Warn,
        "error" => Tone.Bad,
        "reading" => Tone.Info,
        _ => Tone.Neutral,
    };

    /// <summary>Texto curto do selo ("Já está assim", "Mudaria", "Não deu pra ler").</summary>
    public string ReadChipText => ReadState switch
    {
        "same" => Loc.T("Pg.JaEstaAssim"),
        "change" => Loc.T("Pg.Mudaria"),
        "error" => Loc.T("Pg.NaoDeuPraLer2"),
        "reading" => Loc.T("Settings.Lendo"),
        _ => Loc.T("Pg.SemLeituraAqui"),
    };

    /// <summary>Ícone do assunto do ajuste (pelo prefixo do id).</summary>
    public string IconKey => PageText.ActionIcon(Id);

    private void RaiseReadState()
    {
        OnPropertyChanged(nameof(CurrentShown));
        OnPropertyChanged(nameof(ReadState));
        OnPropertyChanged(nameof(ReadStateText));
        OnPropertyChanged(nameof(ReadTone));
        OnPropertyChanged(nameof(ReadChipText));
    }
}

public sealed class CatalogPage : PageViewModel
{
    private IReadOnlyList<CatalogRow> _rows = [];
    private IReadOnlyList<RejectedCatalogItem> _rejected = [];
    private CatalogRow? _selected;
    private string _version = "";
    public override string Title => Loc.T("Pg.PraAjustar");
    public override string Group => Loc.T("Nav.Otimizar");
    public override string Subtitle => Loc.T("Pg.CadaAjusteMostraValor");
    private bool _isMythsTab;

    public CatalogPage()
    {
        ShowMythsCommand = new RelayCommand(() => IsMythsTab = true);
        ShowActionsCommand = new RelayCommand(() => IsActionsTab = true);
    }

    public IReadOnlyList<CatalogRow> Rows
    {
        get => _rows;
        set
        {
            foreach (var r in _rows) r.PropertyChanged -= OnRowChanged;
            if (!Set(ref _rows, value)) return;
            foreach (var r in _rows) r.PropertyChanged += OnRowChanged;
            OnPropertyChanged(nameof(RowsTitle));
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(RowsNumber));
            RaiseCounts();
        }
    }
    public IReadOnlyList<RejectedCatalogItem> Rejected
    {
        get => _rejected;
        set
        {
            if (!Set(ref _rejected, value)) return;
            OnPropertyChanged(nameof(Myths));
            OnPropertyChanged(nameof(MythsTitle));
            OnPropertyChanged(nameof(MythsNumber));
        }
    }

    // ---- apresentação v2 (somente leitura, exceto as abas)

    public bool IsActionsTab { get => !_isMythsTab; set { if (value) IsMythsTab = false; } }
    public bool IsMythsTab
    {
        get => _isMythsTab;
        set { if (Set(ref _isMythsTab, value)) OnPropertyChanged(nameof(IsActionsTab)); }
    }
    public RelayCommand ShowMythsCommand { get; }
    public RelayCommand ShowActionsCommand { get; }

    public bool HasRows => Rows.Count > 0;
    public bool IsLoading => Rows.Count == 0;
    public double RowsNumber => HasRows ? Rows.Count : double.NaN;
    public double MythsNumber => Rejected.Count;
    /// <summary>Quantos mudariam se aplicados (lidos e diferentes do desejado).</summary>
    public double ChangeCount => HasRows ? Rows.Count(r => r.ReadState == "change") : double.NaN;
    /// <summary>Quantos já estão como o desejado neste PC.</summary>
    public double SameCount => HasRows ? Rows.Count(r => r.ReadState == "same") : double.NaN;
    /// <summary>Quantos estão marcados para o teste (os mesmos de Testar sem aplicar e Aplicar).</summary>
    public double IncludedCount => HasRows ? Rows.Count(r => r.Include) : double.NaN;

    /// <summary>Passos do caminho seguro (nenhum é o atual: aqui só se escolhe).</summary>
    public IReadOnlyList<FlowStep> Flow { get; } = OptimizeFlow.For(null);

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CatalogRow.ReadState) or nameof(CatalogRow.Include)) RaiseCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(SameCount));
        OnPropertyChanged(nameof(IncludedCount));
    }
    public CatalogRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Version { get => _version; set => Set(ref _version, value); }

    public string RowsTitle => Rows.Count == 1 ? Loc.T("Pg.N1Ajuste") : Loc.F("Pg.NAjustes", Rows.Count);
    public string MythsTitle => Rejected.Count == 1 ? Loc.T("Pg.N1MitoNaoAplicamos") : Loc.F("Pg.NMitosNaoAplicamos", Rejected.Count);

    /// <summary>Itens recusados do catálogo como "mitos", com título e veredito legíveis; os não recomendados vêm primeiro.</summary>
    public IReadOnlyList<MythItem> Myths => Rejected
        .Select(r => (Item: r, Verdict: CatalogText.MythVerdict(r.Classification)))
        .OrderBy(x => x.Verdict.Order)
        .Select(x => new MythItem(x.Item.Id, CatalogText.MythTitle(x.Item.Id), x.Verdict.Verdict, x.Verdict.Tone, x.Item.Reason, x.Item.Source))
        .ToList();
}

// ------------------------------------------------------------------ Simulação

public sealed record PlanRow(string Action, string Status, string Observed, string Desired, string Batch, string Reason, PlanStatus Kind)
{
    /// <summary>Título da ação sem a chave técnica entre parênteses.</summary>
    [JsonIgnore]
    public string Name => Action.LastIndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? Action[..i] : Action;

    /// <summary>Chave técnica (id@versão), quando presente em <see cref="Action"/>.</summary>
    [JsonIgnore]
    public string Key => Action.LastIndexOf(" (", StringComparison.Ordinal) is var i and > 0 && Action.EndsWith(')')
        ? Action[(i + 2)..^1] : "";

    /// <summary>Iria no segundo lote, depois de um reinício.</summary>
    [JsonIgnore]
    public bool AfterReboot => Batch.StartsWith('2');

    /// <summary>Tem um "agora/depois" que vale mostrar (seria aplicada ou já está assim).</summary>
    [JsonIgnore]
    public bool ShowsChange => Kind is PlanStatus.Pending or PlanStatus.AlreadyConfigured;
}

public sealed class PlanPage : PageViewModel
{
    private IReadOnlyList<PlanRow> _rows = [];
    private string _summary = Loc.T("Pg.NenhumaSimulacaoExecutada");
    private bool _busy;
    public override string Title => Loc.T("Pg.TestarSemAplicar");
    public override string Group => Loc.T("Nav.Otimizar");
    public override string Subtitle => Loc.T("Pg.SimuleAntesMexerGente");
    public PlanPage()
    {
        Candidates.CollectionChanged += (_, e) =>
        {
            foreach (var r in e.OldItems?.OfType<CatalogRow>() ?? []) r.PropertyChanged -= OnCandidateChanged;
            foreach (var r in e.NewItems?.OfType<CatalogRow>() ?? []) r.PropertyChanged += OnCandidateChanged;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                foreach (var r in Candidates) { r.PropertyChanged -= OnCandidateChanged; r.PropertyChanged += OnCandidateChanged; }
            RaiseSelection();
        };
    }

    public ObservableCollection<CatalogRow> Candidates { get; } = [];
    public IReadOnlyList<PlanRow> Rows
    {
        get => _rows;
        set
        {
            if (!Set(ref _rows, value)) return;
            foreach (var name in (string[])[nameof(Groups), nameof(HasResult), nameof(NoResult), nameof(HeroNumber), nameof(HeroValueText),
                         nameof(PendingCount), nameof(AlreadyCount), nameof(NotApplicableCount), nameof(BlockedCount)])
                OnPropertyChanged(name);
        }
    }

    public bool HasResult => Rows.Count > 0;

    // ---- apresentação v2 (somente leitura)

    public bool NoResult => Rows.Count == 0;
    public bool HasCandidates => Candidates.Count > 0;
    /// <summary>Destaque: quantos mudariam (depois de simular); antes disso, o texto "Ainda não testado".</summary>
    public double HeroNumber => HasResult ? Count(PlanStatus.Pending) : double.NaN;
    public string HeroValueText => HasResult ? "" : Loc.T("Pg.AindaNaoTestado");
    public double PendingCount => HasResult ? Count(PlanStatus.Pending) : double.NaN;
    public double AlreadyCount => HasResult ? Count(PlanStatus.AlreadyConfigured) : double.NaN;
    public double NotApplicableCount => HasResult ? Count(PlanStatus.NotApplicable) : double.NaN;
    public double BlockedCount => HasResult ? Count(PlanStatus.Blocked) : double.NaN;
    /// <summary>Marcados para testar agora (os mesmos da página Aplicar).</summary>
    public double SelectedCount => Candidates.Count(c => c.Include);
    public string SelectedCaption => Loc.F("Pg.NPraLerAqui", Candidates.Count);
    public IReadOnlyList<FlowStep> Flow { get; } = OptimizeFlow.For("PlanPage");

    private int Count(PlanStatus s) => Rows.Count(r => r.Kind == s);

    private void OnCandidateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogRow.Include)) RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCaption));
        OnPropertyChanged(nameof(HasCandidates));
    }

    /// <summary>Resultado da simulação agrupado na ordem de leitura: o que mudaria primeiro, o que barrou por último.</summary>
    public IReadOnlyList<PlanGroup> Groups =>
    [
        new(PlanStatus.Pending, Loc.T("Shell.SeriaAplicada"), Loc.T("Pg.HojeEstaDiferenteDesejado"), Rows.Where(r => r.Kind == PlanStatus.Pending).ToList()),
        new(PlanStatus.AlreadyConfigured, Loc.T("Pg.JaEstaAssim"), Loc.T("Pg.SeuPcJaTem"), Rows.Where(r => r.Kind == PlanStatus.AlreadyConfigured).ToList()),
        new(PlanStatus.NotApplicable, Loc.T("Pg.NaoAplica"), Loc.T("Pg.NaoFazSentidoNeste"), Rows.Where(r => r.Kind == PlanStatus.NotApplicable).ToList()),
        new(PlanStatus.Blocked, Loc.T("Shell.Bloqueada"), Loc.T("Pg.AlgoImpedeMudancaSeguranca"), Rows.Where(r => r.Kind == PlanStatus.Blocked).ToList()),
    ];
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand? SimulateCommand { get; set; }
    public string BlockedNotice { get; } =
        Loc.T("Pg.AquiNadaAplicadoSimulacao");
}

// ------------------------------------------------------------------ Histórico

public sealed record HistoryRun(Guid RunId, string Started, string Status, string Profile, string Catalog)
{
    /// <summary>Execução ainda sem encerramento registrado (algo dela pode estar valendo).</summary>
    [JsonIgnore]
    public bool IsOpen => Status == Loc.T("Shell.NaoEncerrada");
}

public sealed record HistoryEntry(long Id, string State, string Action, string Target, string Original, string Desired, string Observed, string? Note)
{
    [JsonIgnore]
    public string StateText => OutcomeText.Of(OutcomeText.Split(State).Name);
    [JsonIgnore]
    public Tone Tone => PageText.OutcomeTone(State);
    [JsonIgnore]
    public string IconKey => PageText.ActionIcon(Action);
    [JsonIgnore]
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

/// <summary>Item da linha do tempo do Histórico: a execução, o selo e o "Desfazer" dela.</summary>
public sealed class HistoryRunCard(HistoryRun run, RelayCommand undo)
{
    public HistoryRun Run { get; } = run;
    public string Started => Run.Started;
    public string Profile => Run.Profile;
    public string Catalog => Run.Catalog;
    public bool IsOpen => Run.IsOpen;
    public Tone Tone => Run.IsOpen ? Tone.Warn : Tone.Neutral;
    public string StatusText => Run.IsOpen ? Loc.T("Pg.AindaValendo") : Run.Status.Length > 0 ? char.ToUpperInvariant(Run.Status[0]) + Run.Status[1..] : "";
    public RelayCommand UndoCommand { get; } = undo;
}

public sealed class HistoryPage : PageViewModel
{
    private IReadOnlyList<HistoryRun> _runs = [];
    private IReadOnlyList<HistoryRunCard> _cards = [];
    private IReadOnlyList<HistoryEntry> _entries = [];
    private HistoryRun? _selected;
    private string _status = "";
    private string _coordination = "";
    private bool _undoing;
    public override string Title => Loc.T("Pg.Historico");
    public override string Group => Loc.T("Nav.Registro");
    public override string Subtitle => Loc.T("Pg.TudoServicoRegistrouDiario");
    public string JournalPath { get; set; } = NexusServices.DefaultJournalPath;
    public IReadOnlyList<HistoryRun> Runs
    {
        get => _runs;
        set
        {
            if (!Set(ref _runs, value)) return;
            Cards = value.Select(r => new HistoryRunCard(r, new RelayCommand(() => _ = UndoAsync(r), () => !_undoing))).ToList();
            foreach (var name in (string[])[nameof(RunsTitle), nameof(HasRuns), nameof(NoRuns), nameof(RunsNumber), nameof(OpenNumber)])
                OnPropertyChanged(name);
        }
    }
    public IReadOnlyList<HistoryEntry> Entries
    {
        get => _entries;
        set
        {
            if (!Set(ref _entries, value)) return;
            foreach (var name in (string[])[nameof(EntriesTitle), nameof(AppliedEntries), nameof(RevertedEntries), nameof(ProblemEntries)])
                OnPropertyChanged(name);
        }
    }
    public string RunsTitle => Runs.Count == 1 ? Loc.T("Pg.N1Execucao") : Loc.F("Pg.NExecucoes", Runs.Count);
    public string EntriesTitle => Entries.Count == 1 ? Loc.T("Pg.N1AjusteNestaExecucao") : Loc.F("Pg.NAjustesNestaExecucao", Entries.Count);
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Coordination
    {
        get => _coordination;
        set { if (Set(ref _coordination, value)) { OnPropertyChanged(nameof(CoordinationTone)); OnPropertyChanged(nameof(CoordinationChip)); } }
    }
    public Action<HistoryRun?>? OnSelected { get; set; }
    public HistoryRun? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnSelected?.Invoke(value);
            OnPropertyChanged(nameof(SelectedCard));
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    // ---- apresentação v2

    /// <summary>Linha do tempo com o "Desfazer" de cada execução.</summary>
    public IReadOnlyList<HistoryRunCard> Cards { get => _cards; private set => Set(ref _cards, value); }
    /// <summary>Cartão escolhido na linha do tempo (espelha <see cref="Selected"/>).</summary>
    public HistoryRunCard? SelectedCard
    {
        get => _cards.FirstOrDefault(c => c.Run == _selected);
        set => Selected = value?.Run;
    }
    public bool HasSelection => _selected is not null;
    public bool HasRuns => Runs.Count > 0;
    public bool NoRuns => Runs.Count == 0;
    public double RunsNumber => Runs.Count;
    /// <summary>Execuções sem encerramento registrado.</summary>
    public double OpenNumber => Runs.Count(r => r.IsOpen);
    public double AppliedEntries => Entries.Count(e => PageText.OutcomeTone(e.State) is Tone.Warn);
    public double RevertedEntries => Entries.Count(e => PageText.OutcomeTone(e.State) is Tone.Good);
    public double ProblemEntries => Entries.Count(e => PageText.OutcomeTone(e.State) is Tone.Bad);
    public Tone CoordinationTone => Coordination.StartsWith("Nenhum executor ativo", StringComparison.Ordinal) ? Tone.Good
        : Coordination.StartsWith("Executor ativo", StringComparison.Ordinal) ? Tone.Warn : Tone.Neutral;
    public string CoordinationChip => CoordinationTone switch
    {
        Tone.Good => Loc.T("Pg.NadaAplicandoAgora"),
        Tone.Warn => Loc.T("Pg.AplicandoAgora"),
        _ => Loc.T("Pg.NaoDeuPraConferir"),
    };
    public IReadOnlyList<FlowStep> Flow { get; } = OptimizeFlow.For("HistoryPage");

    /// <summary>Quem desfaz: a janela (IModuleHost), que pede confirmação e usa o aplicador. Nunca escreve daqui.</summary>
    public IModuleHost? Host { get; set; }

    /// <summary>
    /// Desfaz uma execução do diário mostrado (o do aplicador de sistema, em ProgramData): pede confirmação e, como é
    /// mudança de sistema, o Windows pede permissão. A janela relê o histórico depois.
    /// </summary>
    private async Task UndoAsync(HistoryRun run)
    {
        var host = Host ?? System.Windows.Application.Current?.MainWindow?.DataContext as IModuleHost;
        if (host is null || _undoing) return;
        _undoing = true;
        try
        {
            var level = string.Equals(JournalPath, Nexus.Broker.LocalBroker.JournalPath(Nexus.Broker.BrokerLevel.User), StringComparison.OrdinalIgnoreCase)
                ? Nexus.Broker.BrokerLevel.User : Nexus.Broker.BrokerLevel.Admin;
            var r = await host.RevertAsync(run.RunId, level);
            if (r is not null) Status = r.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("Pg.NaoFoiPossivelDesfazer", ex.Message); }
        finally
        {
            _undoing = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
}

// ------------------------------------------------------------------ Capacidades e visão geral

public sealed class CapabilitiesPage : PageViewModel
{
    public override string Title => Loc.T("Settings.JaFunciona");
    public override string Group => Loc.T("Nav.Registro");
    public override string Subtitle => Loc.T("Pg.SemMaquiagemJaExiste");
    public IReadOnlyList<CapabilityRow> Rows { get; } = CapabilityCatalog.Rows(OperatingSystem.IsWindows());

    /// <summary>Capacidades agrupadas por área, na ordem em que as áreas aparecem.</summary>
    public IReadOnlyList<CapabilityGroup> Groups => Rows.GroupBy(r => r.Area, StringComparer.Ordinal)
        .Select(g => new CapabilityGroup(g.Key, g.ToList())).ToList();

    public int ValidatedCount => Rows.Count(r => r.Label == CapabilityLabel.ImplementedValidated);
    public int NotValidatedCount => Rows.Count(r => r.Label == CapabilityLabel.ImplementedNotValidated);
    public int NotSupportedCount => Rows.Count(r => r.Label == CapabilityLabel.NotSupportedHere);
    public int PendingCount => Rows.Count(r => r.Label == CapabilityLabel.Pending);

    // ---- apresentação v2 (somente leitura)

    /// <summary>Parcela validada com teste real (0–100).</summary>
    public double ValidatedPercent => Rows.Count == 0 ? double.NaN : 100.0 * ValidatedCount / Rows.Count;
    public string ValidatedSuffix => $" de {Rows.Count}";
}

/// <summary>Papel do cartão na página "Meu PC".</summary>
public enum OverviewCardKind
{
    /// <summary>Valor lido (Windows, plano de energia, sistema…), aparece em "Tudo o que foi lido".</summary>
    Fact,
    /// <summary>Peça com ilustração e 1 número (processador, placa de vídeo, memória, disco).</summary>
    Hardware,
    /// <summary>"Itens em ordem" do diagnóstico (anel do destaque).</summary>
    Health,
    /// <summary>"O que o NEXUS recomenda agora": título curto, motivo e ação (abre uma página).</summary>
    Recommendation,
}

/// <summary>
/// Cartão da página "Meu PC". Os campos extras (init) alimentam a apresentação v2: número, sufixo, página de destino
/// (nome do tipo, para ShellCommands.Navigate), ilustração/ícone (chaves de recurso) e tendência.
/// </summary>
public sealed record OverviewCard(string Title, string Value, FactStatus Status, string Detail)
{
    public OverviewCardKind Kind { get; init; } = OverviewCardKind.Fact;
    /// <summary>Número principal (NaN = sem número; mostra <see cref="Value"/>).</summary>
    public double Number { get; init; } = double.NaN;
    public string Suffix { get; init; } = "";
    /// <summary>Página que resolve/aprofunda (nome do tipo do ViewModel, ex.: "MemoryLabPage").</summary>
    public string Target { get; init; } = "";
    public string ActionText { get; init; } = "";
    public string IconKey { get; init; } = "";
    public string IllustrationKey { get; init; } = "";
    public string Trend { get; init; } = "";
    /// <summary>Linha de detalhe do cartão de peça (modelo, tipo e velocidade, unidade e tamanho).</summary>
    public string Note { get; init; } = "";
    /// <summary>Tendência é um aviso (âmbar) e não uma informação (ciano)?</summary>
    public bool TrendWarns { get; init; }
    public Tone TrendTone => Trend.Length == 0 ? Tone.Neutral : TrendWarns ? Tone.Warn : Tone.Good;
    /// <summary>Recomendação vinda de um ponto de atenção (âmbar) ou sugestão geral (ciano).</summary>
    public Tone Tone => TrendWarns ? Tone.Warn : Tone.Info;
}

public sealed class OverviewPage : PageViewModel
{
    private IReadOnlyList<OverviewCard> _cards = [];
    private string _footer = "";
    public override string Title => Loc.T("Nav.MeuPc");
    public override string Group => Loc.T("Nav.MeuPc");
    public override string Subtitle => Loc.T("Pg.ResumoFoiLidoDeste");
    private static readonly string[] HardwareOrder = [Loc.T("Pg.Processador"), "GPU", Loc.T("Pg.Memoria"), Loc.T("Pg.Disco")];

    public IReadOnlyList<OverviewCard> Cards
    {
        get => _cards;
        set
        {
            if (!Set(ref _cards, value)) return;
            foreach (var name in (string[])[nameof(HeroCards), nameof(OtherCards), nameof(Reading), nameof(HasData), nameof(IsLoading),
                         nameof(Cpu), nameof(Gpu), nameof(Ram), nameof(Disk), nameof(Recommendations), nameof(HealthScore),
                         nameof(HealthCaption), nameof(HeroTitle), nameof(HeroCaption)])
                OnPropertyChanged(name);
        }
    }

    public string Footer { get => _footer; set => Set(ref _footer, value); }

    // Derivados para a apresentação.
    public IReadOnlyList<OverviewCard> HeroCards =>
        HardwareOrder.Select(t => Cards.FirstOrDefault(c => c.Title == t)).OfType<OverviewCard>().ToList();
    public IReadOnlyList<OverviewCard> OtherCards => Cards.Where(c => c.Kind == OverviewCardKind.Fact && c.Title != Loc.T("Pg.Leitura")).ToList();
    public OverviewCard? Reading => Cards.FirstOrDefault(c => c.Title == Loc.T("Pg.Leitura"));
    public bool HasData => Cards.Count > 0;
    public bool IsLoading => Cards.Count == 0;

    public OverviewCard? Cpu => Hardware(Loc.T("Pg.Processador"));
    public OverviewCard? Gpu => Hardware("GPU");
    public OverviewCard? Ram => Hardware(Loc.T("Pg.Memoria"));
    public OverviewCard? Disk => Hardware(Loc.T("Pg.Disco"));
    private OverviewCard? Hardware(string title) => Cards.FirstOrDefault(c => c.Kind == OverviewCardKind.Hardware && c.Title == title);

    public IReadOnlyList<OverviewCard> Recommendations => Cards.Where(c => c.Kind == OverviewCardKind.Recommendation).ToList();

    /// <summary>"Itens em ordem" (0–100) do diagnóstico; NaN antes da leitura.</summary>
    public double HealthScore => Cards.FirstOrDefault(c => c.Kind == OverviewCardKind.Health)?.Number ?? double.NaN;
    public string HealthCaption => Cards.FirstOrDefault(c => c.Kind == OverviewCardKind.Health)?.Value ?? "";

    /// <summary>Nome do PC (fabricante e modelo), ou "Seu PC".</summary>
    public string HeroTitle => Cards.FirstOrDefault(c => c.Title == Loc.T("Nav.Sistema")) is { Status: not FactStatus.Unavailable } s ? s.Value : Loc.T("Pg.SeuPc");
    public string HeroCaption => Cards.FirstOrDefault(c => c.Title == "Windows") is { Status: not FactStatus.Unavailable } w
        ? (w.Value.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ? w.Value : "Windows " + w.Value) + Loc.T("Pg.LerNaoMudaNada")
        : Loc.T("Pg.TudoNexusLeuSeu");

    public static IReadOnlyList<OverviewCard> Build(SystemSnapshot s, IReadOnlyList<DiagnosticFinding> findings)
    {
        var f = s.Facts;
        OverviewCard Card(string title, string? value, string detail, FactStatus ok = FactStatus.Detected) =>
            new(title, value ?? Loc.T("Pg.Indisponivel"), value is null ? FactStatus.Unavailable : ok, detail);

        // Notebook híbrido: mostrar a placa dedicada (a que roda os jogos), não a integrada que desenha a tela.
        static int GpuRank(GpuInfo g)
        {
            var n = g.Name ?? "";
            if (string.Equals(g.Vendor, "Microsoft", StringComparison.OrdinalIgnoreCase) || n.Contains("Basic", StringComparison.OrdinalIgnoreCase)) return 0;
            if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Quadro", StringComparison.OrdinalIgnoreCase) || n.Contains("RTX", StringComparison.OrdinalIgnoreCase)) return 3;
            if (n.Contains(" RX ", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1; // integrada (Radeon(TM) Graphics, UHD, Iris...)
        }
        var gpu = f.Gpus.OrderByDescending(GpuRank).ThenByDescending(g => g.MemoryBytes ?? 0).FirstOrDefault();
        var sized = f.MemoryModules.Where(m => m.SizeBytes is not null).ToList();
        var installed = sized.Aggregate(0UL, (a, m) => a + m.SizeBytes!.Value);
        var attention = findings.Count(x => x.Severity == DiagnosticSeverity.Attention);
        var cards = new List<OverviewCard>
        {
            Card("Windows", f.WindowsEdition is null && f.WindowsBuild is null ? null : Loc.F("Pg.NBuildN", f.WindowsEdition, f.WindowsBuild), Loc.T("Pg.RegistroCurrentversion")),
            Card(Loc.T("Pg.Processador"), f.CpuModel, f.PhysicalCores is null ? Loc.T("Pg.TopologiaIndisponivel") : Loc.F("Pg.NNucleosNThreads", f.PhysicalCores, f.LogicalProcessors)) with
            {
                Kind = OverviewCardKind.Hardware, Number = f.PhysicalCores ?? double.NaN, Suffix = Loc.T("Pg.Nucleos"), Target = "CpuLabPage",
                IllustrationKey = "Illo.Cpu", Trend = f.LogicalProcessors is { } t ? Loc.F("Pg.NThreads", t) : "", Note = f.CpuModel ?? "",
            },
            Card("GPU", gpu?.Name, gpu is null ? "SetupAPI" : Loc.F("Pg.DriverNN", gpu.DriverVersion ?? "?", gpu.DriverDate ?? "?")) with
            {
                Kind = OverviewCardKind.Hardware, Number = gpu?.MemoryBytes is { } vram and > 0 ? vram : double.NaN, Target = "GpuLabPage",
                IllustrationKey = "Illo.Gpu", Trend = f.Gpus.Count > 1 ? Loc.F("Pg.NPlacas", f.Gpus.Count) : "",
                Note = gpu?.MemoryBytes is > 0 ? gpu.Name : gpu is null ? "" : Loc.F("Pg.DriverN", gpu.DriverVersion ?? "?"),
            },
            Card(Loc.T("Pg.Memoria"), f.TotalMemoryBytes is null ? null : Units.Bytes(f.TotalMemoryBytes), MemoryDetail(f)) with
            {
                Kind = OverviewCardKind.Hardware, Number = f.TotalMemoryBytes is { } total ? total : double.NaN, Target = "MemoryLabPage",
                IllustrationKey = "Illo.Ram", Trend = MemoryTrend(f.MemoryModules.Count, sized.Count), TrendWarns = sized.Count == 1,
                Note = MemoryDetail(f),
            },
            DiskCard(f),
            Card(Loc.T("Pg.PlanoEnergia"), f.ActivePowerSchemeName ?? f.ActivePowerScheme?.ToString("D"), "PowerGetActiveScheme"),
            Card(Loc.T("Nav.Sistema"), f.SystemModel is null ? null : $"{f.SystemManufacturer} {f.SystemModel}",
                f.IsVirtualMachine switch { true => Loc.T("Pg.MaquinaVirtualInferido"), false => Loc.T("Pg.HardwareFisicoInferido"), null => "SMBIOS" }),
            new(Loc.T("Pg.Diagnostico"), Loc.F("Pg.NPontoSAtencao2", attention), FactStatus.Detected, Loc.F("Pg.NAchadosVejaPagina", findings.Count)),
            new(Loc.T("Pg.Leitura"), s.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s", FactStatus.Detected,
                Loc.F("Pg.CapturadoN", s.TakenAt)),
        };
        if (installed > 0 && cards.FindIndex(c => c.Title == Loc.T("Pg.Memoria")) is var mi and >= 0 && cards[mi].Status == FactStatus.Unavailable)
            cards[mi] = cards[mi] with { Number = installed };

        var score = DesignMath.HealthScore(findings.Count, attention);
        cards.Add(new OverviewCard(Loc.T("Pg.ItensOrdem"), attention == 0 ? Loc.T("Pg.NadaPedeAtencaoAgora") : attention == 1 ? Loc.T("Pg.N1PontoConferir") : Loc.F("Pg.NPontosConferir", attention),
            FactStatus.Detected, Loc.F("Pg.NVerificacoes", findings.Count))
        { Kind = OverviewCardKind.Health, Number = score ?? double.NaN, Target = "DiagnosticsPage" });

        cards.AddRange(Recommend(f, findings));
        return cards;
    }

    private static string MemoryDetail(SystemFacts f)
    {
        var m = f.MemoryModules.FirstOrDefault(x => x.SizeBytes is not null);
        if (m is null) return f.MemoryModules.Count == 0 ? Loc.T("Pg.ModulosIndisponiveis") : Loc.F("Pg.NModuloS", f.MemoryModules.Count);
        var speed = m.ConfiguredSpeedMts ?? m.MaxSpeedMts;
        return string.Join(" · ", new[] { m.Type, speed is null ? null : $"{speed} MT/s" }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>"1 de 2 slots" quando a leitura mostra slots vazios; senão "canal único" (1 pente) / "2 pentes".</summary>
    internal static string MemoryTrend(int slotsListed, int modules) => modules switch
    {
        0 => "",
        _ when slotsListed > modules => Loc.F("Pg.NNSlots", modules, slotsListed),
        1 => Loc.T("Pg.CanalUnico"),
        _ => Loc.F("Pg.NPentes", modules),
    };

    private static OverviewCard DiskCard(SystemFacts f)
    {
        var letter = string.IsNullOrEmpty(f.SystemDrive) ? 'C' : char.ToUpperInvariant(f.SystemDrive[0]);
        var d = f.Drives.FirstOrDefault(x => x.Name.Length > 0 && char.ToUpperInvariant(x.Name[0]) == letter);
        if (d is not { TotalBytes: { } total and > 0, FreeBytes: { } free })
            return new OverviewCard(Loc.T("Pg.Disco"), Loc.T("Pg.Indisponivel"), FactStatus.Unavailable, Loc.F("Pg.UnidadeN", letter))
            { Kind = OverviewCardKind.Hardware, Target = "DiskPage", IllustrationKey = "Illo.Disk", Note = Loc.F("Pg.UnidadeN", letter) };
        var used = 100.0 * (total - free) / total;
        var media = f.SystemDiskMedia == DiskMedia.Unknown ? "" : $" ({StorageClassifier.Describe(f.SystemDiskMedia, f.SystemDiskBus)})";
        return new OverviewCard(Loc.T("Pg.Disco"), Units.Bytes(free) + " livres", FactStatus.Detected, Loc.F("Pg.UnidadeNNN", letter, media, Units.Bytes(total)))
        {
            Kind = OverviewCardKind.Hardware, Number = free, Target = "DiskPage", IllustrationKey = "Illo.Disk",
            Note = Loc.F("Pg.LivresUnidadeNN", letter, media, Units.Bytes(total)),
            Trend = Loc.F("Pg.NUsado", used.ToString("0", CultureInfo.GetCultureInfo("pt-BR"))), TrendWarns = used >= 85,
        };
    }

    /// <summary>
    /// Até três recomendações: primeiro os pontos de atenção do diagnóstico (cada um abre a página que resolve), depois
    /// sugestões gerais seguras (liberar espaço, medir uma partida, ver ajustes). Uma por página de destino.
    /// </summary>
    internal static IReadOnlyList<OverviewCard> Recommend(SystemFacts f, IReadOnlyList<DiagnosticFinding> findings)
    {
        var list = new List<OverviewCard>();
        void Add(OverviewCard c)
        {
            if (list.Count < 3 && list.All(x => x.Target != c.Target)) list.Add(c);
        }
        foreach (var x in findings.Where(x => x.Severity == DiagnosticSeverity.Attention))
        {
            var target = ShellMap.PageForFinding(x.Id);
            Add(new OverviewCard(x.Title, Loc.T("Pg.ValeConferir"), x.Basis, Loc.T("Pg.NaoGarantiaFpsMeca"))
            {
                Kind = OverviewCardKind.Recommendation, Target = target, ActionText = ShellMap.ActionFor(target),
                IconKey = ShellMap.IconFor(target), TrendWarns = true,
            });
        }
        OverviewCard Tip(string title, string detail, string target) => new(title, "", FactStatus.Detected, detail)
        {
            Kind = OverviewCardKind.Recommendation, Target = target, ActionText = ShellMap.ActionFor(target), IconKey = ShellMap.IconFor(target),
        };
        Add(Tip(Loc.T("Pg.LibereEspacoSeguranca"), Loc.T("Pg.TudoVaiQuarentenaPode"), "CleaningPage"));
        Add(Tip(Loc.T("Pg.MecaPartida"), Loc.T("Pg.FpsEngasgosQuadroQuadro"), "MeasurePage"));
        Add(Tip(Loc.T("Pg.VejaPraAjustar"), Loc.T("Pg.CadaAjusteMostraRisco"), "CatalogPage"));
        return list;
    }
}

// ------------------------------------------------------------------ Broker (serviço)

public sealed record BrokerResultRow(string Action, string Outcome, string Detail, string Observed, string Desired)
{
    /// <summary>Resultado em português de jogador ("Aplicada e conferida", "Revertida"…).</summary>
    [JsonIgnore]
    public string OutcomeText
    {
        get
        {
            var (name, inferred) = Views.Optimize.OutcomeText.Split(Outcome);
            return Views.Optimize.OutcomeText.Of(name) + (inferred ? " (inferido)" : "");
        }
    }
    [JsonIgnore]
    public Tone Tone => PageText.OutcomeTone(Outcome);
    [JsonIgnore]
    public string IconKey => PageText.ActionIcon(Action);
    [JsonIgnore]
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>
/// Única via de escrita da interface: pedidos ao serviço NexusBroker. A interface não tem executor, trava nem diário gravável;
/// o serviço decide (catálogo, portão de VM, trava, diário). Aplicar só fica disponível quando o próprio serviço diz que a
/// escrita está liberada, e sempre depois de uma confirmação.
/// </summary>
public sealed class BrokerPage : PageViewModel
{
    private bool _connected;
    private bool _writeAllowed;
    private bool _busy;
    private string _serviceStatus = Loc.T("Pg.NaoConsultadoCliqueConsultar");
    private string _gate = "";
    private string _coordination = "";
    private string _lastMessage = "";
    private Guid? _lastRun;
    private bool _sessionOpen;
    private IReadOnlyList<BrokerResultRow> _results = [];

    public override string Title => Loc.T("Shell.Aplicar");
    public override string Group => Loc.T("Nav.Otimizar");
    public override string Subtitle =>
        Loc.T("Pg.ApliqueAjustesMarcadosNeste");

    private ObservableCollection<CatalogRow> _candidates = [];

    /// <summary>As mesmas ações marcadas na página Simulação.</summary>
    public ObservableCollection<CatalogRow> Candidates
    {
        get => _candidates;
        set
        {
            _candidates.CollectionChanged -= OnCandidatesChanged;
            foreach (var r in _candidates) r.PropertyChanged -= OnCandidateChanged;
            _candidates = value;
            _candidates.CollectionChanged += OnCandidatesChanged;
            foreach (var r in _candidates) r.PropertyChanged += OnCandidateChanged;
            OnPropertyChanged();
            RaiseSelection();
        }
    }

    // ---- apresentação v2 (somente leitura)

    /// <summary>Marcados para aplicar agora (os mesmos de Testar sem aplicar).</summary>
    public double SelectedCount => _candidates.Count(c => c.Include);
    public string SelectedCaption => Loc.F("Pg.NDisponiveis", _candidates.Count);
    public bool HasCandidates => _candidates.Count > 0;
    public bool HasLastRun => LastRun is not null;
    public bool HasResults => Results.Count > 0;
    public bool NoResults => Results.Count == 0;
    public double GoodCount => Results.Count(r => r.Tone == Tone.Good);
    public double ChangedCount => Results.Count(r => r.Tone == Tone.Warn);
    public double ProblemCount => Results.Count(r => r.Tone == Tone.Bad);
    /// <summary>Tom do estado da escrita: verde liberada, ciano recusada (estado, não erro), neutro sem resposta.</summary>
    public Tone GateTone => GateState switch { "open" => Tone.Good, "blocked" => Tone.Info, _ => Tone.Neutral };
    public string GateChip => GateState switch { "open" => Loc.T("Pg.ProntoAplicar"), "blocked" => Loc.T("Pg.AplicadorRecusouEscrever"), _ => Loc.T("Pg.AplicadorAindaNaoConsultado") };
    /// <summary>Legenda de 1 linha do destaque.</summary>
    public string GateCaption => GateState switch
    {
        "open" => Loc.T("Pg.VoceConfirmaAntesTudo"),
        "blocked" => Loc.T("Pg.AplicadorRespondeuMasNao"),
        _ => Loc.T("Pg.AplicadorIniciaSozinhoQuando"),
    };
    public IReadOnlyList<FlowStep> Flow { get; } = OptimizeFlow.For("BrokerPage");

    private void OnCandidatesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var r in e.OldItems?.OfType<CatalogRow>() ?? []) r.PropertyChanged -= OnCandidateChanged;
        foreach (var r in e.NewItems?.OfType<CatalogRow>() ?? []) r.PropertyChanged += OnCandidateChanged;
        RaiseSelection();
    }

    private void OnCandidateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogRow.Include)) RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCaption));
        OnPropertyChanged(nameof(HasCandidates));
    }

    public bool Connected
    {
        get => _connected;
        set { if (Set(ref _connected, value)) RaiseGate(); }
    }
    public bool WriteAllowed
    {
        get => _writeAllowed;
        set { if (Set(ref _writeAllowed, value)) RaiseGate(); }
    }

    /// <summary>"unknown" (serviço não consultado ou fora do ar), "blocked" ou "open".</summary>
    public string GateState => !Connected ? "unknown" : WriteAllowed ? "open" : "blocked";

    public string GateTitle => GateState switch
    {
        "open" => Loc.T("Pg.ProntoAplicarNestePc"),
        "blocked" => Loc.T("Pg.AplicacaoRecusadaPeloAplicador"),
        _ => Loc.T("Pg.AplicadorAindaNaoIniciado"),
    };

    public string GateExplain => GateState switch
    {
        "open" => Loc.T("Pg.AplicarPedeSuaConfirmacao"),
        "blocked" => Loc.T("Pg.AplicadorRespondeuMasRecusou"),
        _ => Loc.T("Pg.CliqueConsultarIniciarAplicador"),
    };

    private void RaiseGate()
    {
        OnPropertyChanged(nameof(GateState));
        OnPropertyChanged(nameof(GateTitle));
        OnPropertyChanged(nameof(GateExplain));
        OnPropertyChanged(nameof(GateTone));
        OnPropertyChanged(nameof(GateChip));
        OnPropertyChanged(nameof(GateCaption));
    }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string ServiceStatus { get => _serviceStatus; set => Set(ref _serviceStatus, value); }
    public string Gate { get => _gate; set => Set(ref _gate, value); }
    public string Coordination { get => _coordination; set => Set(ref _coordination, value); }
    /// <summary>Texto técnico da última resposta (fica em "Detalhes técnicos").</summary>
    public string LastMessage { get => _lastMessage; set { if (Set(ref _lastMessage, value)) OnPropertyChanged(nameof(HasLastMessage)); } }
    /// <summary>A mesma resposta em uma frase para pessoas ("Aplicador pronto · escrita liberada neste PC").</summary>
    public string LastSummary { get => _lastSummary; set => Set(ref _lastSummary, value); }
    public bool HasLastMessage => !string.IsNullOrWhiteSpace(_lastMessage);
    private string _lastSummary = "";
    public Guid? LastRun
    {
        get => _lastRun;
        set { if (Set(ref _lastRun, value)) { OnPropertyChanged(nameof(LastRunText)); OnPropertyChanged(nameof(HasLastRun)); } }
    }
    public bool SessionOpen { get => _sessionOpen; set => Set(ref _sessionOpen, value); }
    public string LastRunText => LastRun is null ? Loc.T("Pg.NenhumaExecucaoNestaSessao") : LastRun.Value.ToString();
    public IReadOnlyList<BrokerResultRow> Results
    {
        get => _results;
        set
        {
            if (!Set(ref _results, value)) return;
            foreach (var name in (string[])[nameof(HasResults), nameof(NoResults), nameof(GoodCount), nameof(ChangedCount), nameof(ProblemCount)])
                OnPropertyChanged(name);
        }
    }

    public RelayCommand? StatusCommand { get; set; }
    public RelayCommand? PlanCommand { get; set; }
    public RelayCommand? ApplyCommand { get; set; }
    public RelayCommand? RevertCommand { get; set; }
    public RelayCommand? EndSessionCommand { get; set; }
    public RelayCommand? RecoverCommand { get; set; }
}

// ------------------------------------------------------------------ apresentação v2 (lógica pura, sem WPF)

/// <summary>Um passo do caminho seguro Simular → Confirmar → Registrar → Desfazer, com a página que o mostra.</summary>
public sealed record FlowStep(int Number, string Title, string Text, string IconKey, string Target, bool IsCurrent)
{
    public Tone Tone => IsCurrent ? Tone.Warn : Tone.Info;
    public bool IsLast => Number == OptimizeFlow.Count;
}

/// <summary>O caminho que toda mudança de ajuste percorre, mostrado nas páginas Testar sem aplicar, Aplicar e Histórico.</summary>
public static class OptimizeFlow
{
    public const int Count = 4;

    /// <summary>Os 4 passos; <paramref name="currentPage"/> (nome do tipo) marca o passo "você está aqui".</summary>
    public static IReadOnlyList<FlowStep> For(string? currentPage) =>
    [
        new(1, Loc.T("Pg.Simular"), Loc.T("Pg.RelePcMostraMudaria"), "Icon.Plan", "PlanPage", currentPage == "PlanPage"),
        new(2, Loc.T("Pg.Confirmar"), Loc.T("Pg.VoceAprovaMudancaSistema"), "Icon.Apply", "BrokerPage", currentPage == "BrokerPage"),
        new(3, Loc.T("Pg.Registrar"), Loc.T("Pg.OriginalFicaGuardadoDiario"), "Icon.History", "HistoryPage", currentPage == "HistoryPage"),
        new(4, Loc.T("Shell.Desfazer"), Loc.T("Pg.VoltaValorOriginalQuando"), "Icon.Undo", "HistoryPage", false),
    ];
}

/// <summary>Textos e ícones de apresentação das páginas de Pages.cs (testáveis sem WPF).</summary>
public static class PageText
{
    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    /// <summary>Ícone de uma seção do Hardware pelo título.</summary>
    public static string SectionIcon(string title)
    {
        var t = title;
        if (t.Contains("processador", StringComparison.OrdinalIgnoreCase)) return "Icon.Cpu";
        if (t.Contains("jogo", StringComparison.OrdinalIgnoreCase)) return "Icon.Games";
        if (t.Contains("gpu", StringComparison.OrdinalIgnoreCase) || t.Contains("vídeo", StringComparison.OrdinalIgnoreCase)) return "Icon.Gpu";
        if (t.Contains("memória", StringComparison.OrdinalIgnoreCase)) return "Icon.Ram";
        if (t.Contains("disco", StringComparison.OrdinalIgnoreCase)) return "Icon.Disk";
        if (t.Contains("rede", StringComparison.OrdinalIgnoreCase)) return "Icon.Network";
        if (t.Contains("energia", StringComparison.OrdinalIgnoreCase)) return "Icon.Bolt";
        if (t.Contains("windows", StringComparison.OrdinalIgnoreCase)) return "Icon.Windows";
        if (t.Contains("serviço", StringComparison.OrdinalIgnoreCase)) return "Icon.Services";
        if (t.Contains("inicialização", StringComparison.OrdinalIgnoreCase)) return "Icon.Startup";
        if (t.Contains("placa", StringComparison.OrdinalIgnoreCase) || t.Contains("firmware", StringComparison.OrdinalIgnoreCase)) return "Icon.Hardware";
        return "Icon.Pc";
    }

    /// <summary>Ícone do assunto de uma ação do catálogo pelo prefixo do id (aceita "id@versão" e "Título (id@versão)").</summary>
    public static string ActionIcon(string? idOrKey)
    {
        var s = idOrKey ?? "";
        var open = s.LastIndexOf('(');
        if (open >= 0 && s.EndsWith(')')) s = s[(open + 1)..^1];
        var dot = s.IndexOf('.', StringComparison.Ordinal);
        var prefix = dot > 0 ? s[..dot] : s;
        return prefix switch
        {
            "power" or "cpulab" or "proc" => "Icon.Cpu",
            "gpu" or "gpulab" or "nvidia" => "Icon.Gpu",
            "memorylab" => "Icon.Ram",
            "disk" => "Icon.Disk",
            "cleaning" => "Icon.Clean",
            "boot" or "startup" => "Icon.Startup",
            "svc" => "Icon.Services",
            "privacy" => "Icon.Privacy",
            "security" => "Icon.Security",
            "scanner" => "Icon.Scan",
            "programs" => "Icon.Programs",
            "driverupdates" => "Icon.Drivers",
            "automation" or "sched" => "Icon.Auto",
            "system" => "Icon.Tools",
            "win" => "Icon.Windows",
            _ => "Icon.Settings",
        };
    }

    /// <summary>Ícone de uma área da página "O que já funciona".</summary>
    public static string AreaIcon(string area) => area switch
    {
        "Ações" => "Icon.Settings",
        "Base" => "Icon.Code",
        "Descoberta" => "Icon.Search",
        "Execução" => "Icon.Apply",
        "Interface" => "Icon.Pc",
        "Jogos" => "Icon.Games",
        "Medição" => "Icon.Measure",
        "Recuperação" => "Icon.Undo",
        _ => "Icon.Capabilities",
    };

    /// <summary>Tom do design system para um resultado do aplicador ou estado do diário (good/bad/change/neutral).</summary>
    public static Tone OutcomeTone(string? raw) => OutcomeText.Tone(OutcomeText.Split(raw ?? "").Name) switch
    {
        "good" => Tone.Good,
        "bad" => Tone.Bad,
        "change" => Tone.Warn,
        _ => Tone.Neutral,
    };
}
