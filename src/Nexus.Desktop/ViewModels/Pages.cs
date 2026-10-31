using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.Desktop.Views.Optimize;
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
}

public sealed class HardwarePage : PageViewModel
{
    private IReadOnlyList<SectionViewModel> _sections = [];
    public override string Title => "Hardware";
    public override string Group => "Meu PC";
    public override string Subtitle => "Leitura do sistema: cada valor mostra se foi detectado, inferido ou está indisponível, e a fonte.";
    public IReadOnlyList<SectionViewModel> Sections
    {
        get => _sections;
        set
        {
            if (!Set(ref _sections, value)) return;
            OnPropertyChanged(nameof(DetectedCount));
            OnPropertyChanged(nameof(InferredCount));
            OnPropertyChanged(nameof(UnavailableCount));
            OnPropertyChanged(nameof(HasData));
        }
    }

    // Derivados para a legenda do topo da página.
    public int DetectedCount => CountFacts(FactStatus.Detected);
    public int InferredCount => CountFacts(FactStatus.Inferred);
    public int UnavailableCount => CountFacts(FactStatus.Unavailable);
    public bool HasData => Sections.Count > 0;

    private int CountFacts(FactStatus status) => Sections.SelectMany(s => s.Facts).Count(f => f.Status == status);
}

// ------------------------------------------------------------------ Diagnóstico

public sealed class DiagnosticsPage : PageViewModel
{
    private IReadOnlyList<DiagnosticFinding> _findings = [];
    public override string Title => "Diagnóstico";
    public override string Group => "Meu PC";
    public override string Subtitle => "Somente leitura. Cada achado cita a evidência; nenhum presume ganho de desempenho.";
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
    ];

    // Derivados para a apresentação: atenção primeiro, depois informativo, depois ok.
    public IReadOnlyList<DiagnosticFinding> Ordered => Findings.OrderBy(f => Rank(f.Severity)).ToList();
    public IReadOnlyList<DiagnosticFinding> Highlights => Ordered.Where(f => f.Severity == DiagnosticSeverity.Attention).Take(3).ToList();
    public int AttentionCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Attention);
    public int InfoCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Info);
    public int OkCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Ok);
    public int UnavailableCount => Findings.Count(f => f.Basis == FactStatus.Unavailable);
    public int Total => Findings.Count;
    public string AttentionLabel => AttentionCount == 1 ? "ponto para conferir" : "pontos para conferir";
    public bool HasFindings => Findings.Count > 0;
    public bool NoAttention => HasFindings && AttentionCount == 0;

    private static int Rank(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Attention => 0,
        DiagnosticSeverity.Info => 1,
        _ => 2,
    };
    public string Summary => $"{Findings.Count(f => f.Severity == DiagnosticSeverity.Attention)} ponto(s) de atenção · " +
                             $"{Findings.Count(f => f.Basis == FactStatus.Unavailable)} não avaliável(is) · {Findings.Count} no total";
}

// ------------------------------------------------------------------ Catálogo

public sealed class CatalogRow : ObservableObject
{
    private string _current = "lendo…";
    private string? _readError;
    private bool _include;

    public CatalogRow(CatalogEntry entry, ActionInstance? instance, string? notInstantiableReason, CapabilityLabel readLabel)
    {
        Entry = entry;
        Instance = instance;
        NotInstantiableReason = notInstantiableReason;
        ReadLabel = readLabel;
        _include = instance is not null;
        if (instance is null) _current = "não lido: " + notInstantiableReason;
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
    public string Persistence => Entry.Definition.Persistence == PersistenceKind.Session ? "Sessão" : "Persistente";
    public string Reboot => Entry.Definition.Reboot.ToString();
    public string Mechanism => Entry.Definition.Mechanism;
    public string Risks => Entry.Definition.Risks;
    public string Evidence => string.Join("\n", Entry.Definition.Evidence.Select(e => $"[{e.Level}] {e.Source}"));
    public string? Condition => Entry.Condition;
    public string? Verification => Entry.Verification;
    public string? Revert => Entry.Revert;
    public string Target => string.Join("  ·  ", Entry.TargetTemplate.Select(kv => $"{kv.Key}={kv.Value}"));
    public string ApplyLabel { get; } = "aplicável neste PC pela página Aplicar, com confirmação e desfazer";
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
        : _current == "lendo…" ? "reading"
        : _current == Desired || _current.StartsWith(Desired + "  (", StringComparison.Ordinal) ? "same"
        : "change";

    /// <summary>Valor atual para exibição: um traço quando a ação nem chega a ser lida (o motivo aparece no detalhe).</summary>
    public string CurrentShown => Instance is null ? "—" : _current;

    public string ReadStateText => ReadState switch
    {
        "unread" => "Ainda não dá pra ler neste PC",
        "error" => "Não deu pra ler agora",
        "reading" => "Lendo o valor atual…",
        "same" => "Já está assim no seu PC",
        _ => "Mudaria com este ajuste",
    };

    private void RaiseReadState()
    {
        OnPropertyChanged(nameof(CurrentShown));
        OnPropertyChanged(nameof(ReadState));
        OnPropertyChanged(nameof(ReadStateText));
    }
}

public sealed class CatalogPage : PageViewModel
{
    private IReadOnlyList<CatalogRow> _rows = [];
    private IReadOnlyList<RejectedCatalogItem> _rejected = [];
    private CatalogRow? _selected;
    private string _version = "";
    public override string Title => "O que dá pra ajustar";
    public override string Group => "Otimizar";
    public override string Subtitle => "Cada ajuste mostra o valor lido agora no seu PC e o que ele passaria a ser. Nada é aplicado nesta página.";
    public IReadOnlyList<CatalogRow> Rows
    {
        get => _rows;
        set { if (Set(ref _rows, value)) OnPropertyChanged(nameof(RowsTitle)); }
    }
    public IReadOnlyList<RejectedCatalogItem> Rejected
    {
        get => _rejected;
        set
        {
            if (!Set(ref _rejected, value)) return;
            OnPropertyChanged(nameof(Myths));
            OnPropertyChanged(nameof(MythsTitle));
        }
    }
    public CatalogRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Version { get => _version; set => Set(ref _version, value); }

    public string RowsTitle => Rows.Count == 1 ? "1 ajuste" : $"{Rows.Count} ajustes";
    public string MythsTitle => Rejected.Count == 1 ? "1 mito que não aplicamos" : $"{Rejected.Count} mitos que não aplicamos";

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
    private string _summary = "Nenhuma simulação executada.";
    private bool _busy;
    public override string Title => "Testar sem aplicar";
    public override string Group => "Otimizar";
    public override string Subtitle => "Simule antes de mexer: a gente relê o estado real do PC e mostra o que mudaria, em ordem e por lote. Nada é aplicado.";
    public ObservableCollection<CatalogRow> Candidates { get; } = [];
    public IReadOnlyList<PlanRow> Rows
    {
        get => _rows;
        set
        {
            if (!Set(ref _rows, value)) return;
            OnPropertyChanged(nameof(Groups));
            OnPropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => Rows.Count > 0;

    /// <summary>Resultado da simulação agrupado na ordem de leitura: o que mudaria primeiro, o que barrou por último.</summary>
    public IReadOnlyList<PlanGroup> Groups =>
    [
        new(PlanStatus.Pending, "Seria aplicada", "Hoje está diferente do desejado. É isso que mudaria.", Rows.Where(r => r.Kind == PlanStatus.Pending).ToList()),
        new(PlanStatus.AlreadyConfigured, "Já está assim", "O seu PC já tem o valor desejado. Nada a fazer.", Rows.Where(r => r.Kind == PlanStatus.AlreadyConfigured).ToList()),
        new(PlanStatus.NotApplicable, "Não se aplica", "Não faz sentido neste PC ou nesta versão do Windows.", Rows.Where(r => r.Kind == PlanStatus.NotApplicable).ToList()),
        new(PlanStatus.Blocked, "Bloqueada", "Algo impede a mudança com segurança. O motivo está em cada item.", Rows.Where(r => r.Kind == PlanStatus.Blocked).ToList()),
    ];
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand? SimulateCommand { get; set; }
    public string BlockedNotice { get; } =
        "Aqui nada é aplicado: a simulação só lê. Para aplicar de verdade, use a página Aplicar: ela pede confirmação, registra tudo e permite desfazer.";
}

// ------------------------------------------------------------------ Histórico

public sealed record HistoryRun(Guid RunId, string Started, string Status, string Profile, string Catalog);
public sealed record HistoryEntry(long Id, string State, string Action, string Target, string Original, string Desired, string Observed, string? Note);

public sealed class HistoryPage : PageViewModel
{
    private IReadOnlyList<HistoryRun> _runs = [];
    private IReadOnlyList<HistoryEntry> _entries = [];
    private HistoryRun? _selected;
    private string _status = "";
    private string _coordination = "";
    public override string Title => "Histórico";
    public override string Group => "Registro";
    public override string Subtitle => "Tudo o que o serviço registrou no diário, aberto só para leitura, e se há algum executor em uso agora.";
    public string JournalPath { get; set; } = NexusServices.DefaultJournalPath;
    public IReadOnlyList<HistoryRun> Runs
    {
        get => _runs;
        set { if (Set(ref _runs, value)) OnPropertyChanged(nameof(RunsTitle)); }
    }
    public IReadOnlyList<HistoryEntry> Entries
    {
        get => _entries;
        set { if (Set(ref _entries, value)) OnPropertyChanged(nameof(EntriesTitle)); }
    }
    public string RunsTitle => Runs.Count == 1 ? "1 execução" : $"{Runs.Count} execuções";
    public string EntriesTitle => Entries.Count == 1 ? "1 ajuste nesta execução" : $"{Entries.Count} ajustes nesta execução";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Coordination { get => _coordination; set => Set(ref _coordination, value); }
    public Action<HistoryRun?>? OnSelected { get; set; }
    public HistoryRun? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) OnSelected?.Invoke(value); }
    }
}

// ------------------------------------------------------------------ Capacidades e visão geral

public sealed class CapabilitiesPage : PageViewModel
{
    public override string Title => "O que já funciona";
    public override string Group => "Registro";
    public override string Subtitle => "Sem maquiagem: o que já existe nesta versão e o que foi conferido de verdade, item por item.";
    public IReadOnlyList<CapabilityRow> Rows { get; } = CapabilityCatalog.Rows(OperatingSystem.IsWindows());

    /// <summary>Capacidades agrupadas por área, na ordem em que as áreas aparecem.</summary>
    public IReadOnlyList<CapabilityGroup> Groups => Rows.GroupBy(r => r.Area, StringComparer.Ordinal)
        .Select(g => new CapabilityGroup(g.Key, g.ToList())).ToList();

    public int ValidatedCount => Rows.Count(r => r.Label == CapabilityLabel.ImplementedValidated);
    public int NotValidatedCount => Rows.Count(r => r.Label == CapabilityLabel.ImplementedNotValidated);
    public int NotSupportedCount => Rows.Count(r => r.Label == CapabilityLabel.NotSupportedHere);
    public int PendingCount => Rows.Count(r => r.Label == CapabilityLabel.Pending);
}

public sealed record OverviewCard(string Title, string Value, FactStatus Status, string Detail);

public sealed class OverviewPage : PageViewModel
{
    private IReadOnlyList<OverviewCard> _cards = [];
    private string _footer = "";
    public override string Title => "Meu PC";
    public override string Group => "Meu PC";
    public override string Subtitle => "Resumo do que foi lido deste PC. Modo somente leitura: nada é alterado.";
    private static readonly string[] HeroOrder = ["Processador", "GPU", "Memória", "Windows"];

    public IReadOnlyList<OverviewCard> Cards
    {
        get => _cards;
        set
        {
            if (!Set(ref _cards, value)) return;
            OnPropertyChanged(nameof(HeroCards));
            OnPropertyChanged(nameof(OtherCards));
            OnPropertyChanged(nameof(Reading));
            OnPropertyChanged(nameof(HasData));
        }
    }

    public string Footer { get => _footer; set => Set(ref _footer, value); }

    // Derivados para a apresentação: o destaque (CPU, GPU, memória, Windows), o resto do que foi lido e a leitura em si.
    public IReadOnlyList<OverviewCard> HeroCards =>
        HeroOrder.Select(t => Cards.FirstOrDefault(c => c.Title == t)).OfType<OverviewCard>().ToList();
    public IReadOnlyList<OverviewCard> OtherCards =>
        Cards.Where(c => !HeroOrder.Contains(c.Title) && c.Title is not ("Diagnóstico" or "Leitura")).ToList();
    public OverviewCard? Reading => Cards.FirstOrDefault(c => c.Title == "Leitura");
    public bool HasData => Cards.Count > 0;

    public static IReadOnlyList<OverviewCard> Build(SystemSnapshot s, IReadOnlyList<DiagnosticFinding> findings)
    {
        var f = s.Facts;
        OverviewCard Card(string title, string? value, string detail, FactStatus ok = FactStatus.Detected) =>
            new(title, value ?? "indisponível", value is null ? FactStatus.Unavailable : ok, detail);

        var gpu = f.Gpus.FirstOrDefault(g => g.Vendor is not "Microsoft") ?? (f.Gpus.Count > 0 ? f.Gpus[0] : null);
        var installed = f.MemoryModules.Where(m => m.SizeBytes is not null).Aggregate(0UL, (a, m) => a + m.SizeBytes!.Value);
        return
        [
            Card("Windows", f.WindowsEdition is null && f.WindowsBuild is null ? null : $"{f.WindowsEdition} · build {f.WindowsBuild}", "Registro CurrentVersion"),
            Card("Processador", f.CpuModel, f.PhysicalCores is null ? "Topologia indisponível" : $"{f.PhysicalCores} núcleos / {f.LogicalProcessors} threads"),
            Card("GPU", gpu?.Name, gpu is null ? "SetupAPI" : $"Driver {gpu.DriverVersion ?? "?"} ({gpu.DriverDate ?? "?"})"),
            Card("Memória", f.TotalMemoryBytes is null ? null : Units.Bytes(f.TotalMemoryBytes),
                f.MemoryModules.Count == 0 ? "Módulos indisponíveis" : $"{f.MemoryModules.Count} módulo(s), {Units.Bytes(installed)} instalados"),
            Card("Plano de energia", f.ActivePowerSchemeName ?? f.ActivePowerScheme?.ToString("D"), "PowerGetActiveScheme"),
            Card("Sistema", f.SystemModel is null ? null : $"{f.SystemManufacturer} {f.SystemModel}",
                f.IsVirtualMachine switch { true => "Máquina virtual (inferido)", false => "Hardware físico (inferido)", null => "SMBIOS" }),
            new("Diagnóstico", $"{findings.Count(x => x.Severity == DiagnosticSeverity.Attention)} ponto(s) de atenção", FactStatus.Detected,
                $"{findings.Count} achados; veja a página Diagnóstico"),
            new("Leitura", s.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s", FactStatus.Detected,
                $"Capturado em {s.TakenAt:dd/MM/yyyy HH:mm:ss}"),
        ];
    }
}

// ------------------------------------------------------------------ Broker (serviço)

public sealed record BrokerResultRow(string Action, string Outcome, string Detail, string Observed, string Desired);

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
    private string _serviceStatus = "Não consultado. Clique em \"Consultar serviço\".";
    private string _gate = "";
    private string _coordination = "";
    private string _lastMessage = "";
    private Guid? _lastRun;
    private bool _sessionOpen;
    private IReadOnlyList<BrokerResultRow> _results = [];

    public override string Title => "Aplicar";
    public override string Group => "Otimizar";
    public override string Subtitle =>
        "Aplique os ajustes marcados neste PC. Cada mudança é registrada antes e pode ser desfeita; as de sistema pedem permissão ao Windows e criam um ponto de restauração.";

    /// <summary>As mesmas ações marcadas na página Simulação.</summary>
    public ObservableCollection<CatalogRow> Candidates { get; set; } = [];

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
        "open" => "Pronto para aplicar neste PC",
        "blocked" => "Aplicação recusada pelo aplicador",
        _ => "Aplicador ainda não iniciado",
    };

    public string GateExplain => GateState switch
    {
        "open" => "Aplicar pede a sua confirmação. Ajustes do seu usuário são aplicados direto; ajustes de sistema pedem permissão no aviso do Windows. Tudo fica no diário para desfazer.",
        "blocked" => "O aplicador respondeu, mas recusou escrever. O motivo aparece abaixo; nada foi alterado.",
        _ => "Clique em Consultar para iniciar o aplicador do NEXUS e conferir se está pronto. Ele também inicia sozinho quando você aplicar.",
    };

    private void RaiseGate()
    {
        OnPropertyChanged(nameof(GateState));
        OnPropertyChanged(nameof(GateTitle));
        OnPropertyChanged(nameof(GateExplain));
    }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string ServiceStatus { get => _serviceStatus; set => Set(ref _serviceStatus, value); }
    public string Gate { get => _gate; set => Set(ref _gate, value); }
    public string Coordination { get => _coordination; set => Set(ref _coordination, value); }
    public string LastMessage { get => _lastMessage; set => Set(ref _lastMessage, value); }
    public Guid? LastRun { get => _lastRun; set { if (Set(ref _lastRun, value)) OnPropertyChanged(nameof(LastRunText)); } }
    public bool SessionOpen { get => _sessionOpen; set => Set(ref _sessionOpen, value); }
    public string LastRunText => LastRun is null ? "nenhuma execução nesta sessão da interface" : LastRun.Value.ToString();
    public IReadOnlyList<BrokerResultRow> Results { get => _results; set => Set(ref _results, value); }

    public RelayCommand? StatusCommand { get; set; }
    public RelayCommand? PlanCommand { get; set; }
    public RelayCommand? ApplyCommand { get; set; }
    public RelayCommand? RevertCommand { get; set; }
    public RelayCommand? EndSessionCommand { get; set; }
    public RelayCommand? RecoverCommand { get; set; }
}
