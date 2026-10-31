using System.Collections.ObjectModel;
using System.Globalization;
using Nexus.Core;
using Nexus.Desktop.Services;
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
    public string ApplyLabel { get; } = "só pelo serviço do broker (escrita exige VM de teste liberada)";
    public bool CanSimulate => Instance is not null;

    public string Current { get => _current; set => Set(ref _current, value); }
    public string? ReadError { get => _readError; set => Set(ref _readError, value); }
    public bool Include { get => _include; set => Set(ref _include, value && CanSimulate); }
}

public sealed class CatalogPage : PageViewModel
{
    private IReadOnlyList<CatalogRow> _rows = [];
    private IReadOnlyList<RejectedCatalogItem> _rejected = [];
    private CatalogRow? _selected;
    private string _version = "";
    public override string Title => "O que dá pra ajustar";
    public override string Group => "Otimizar";
    public override string Subtitle => "Ações do catalog/actions.v1.json com o estado real lido agora. Nada é aplicado.";
    public IReadOnlyList<CatalogRow> Rows { get => _rows; set => Set(ref _rows, value); }
    public IReadOnlyList<RejectedCatalogItem> Rejected { get => _rejected; set => Set(ref _rejected, value); }
    public CatalogRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Version { get => _version; set => Set(ref _version, value); }
}

// ------------------------------------------------------------------ Simulação

public sealed record PlanRow(string Action, string Status, string Observed, string Desired, string Batch, string Reason, PlanStatus Kind);

public sealed class PlanPage : PageViewModel
{
    private IReadOnlyList<PlanRow> _rows = [];
    private string _summary = "Nenhuma simulação executada.";
    private bool _busy;
    public override string Title => "Testar sem aplicar";
    public override string Group => "Otimizar";
    public override string Subtitle => "Dry-run: calcula o que mudaria, na ordem e em lotes, relendo o estado real. Nenhuma alteração é aplicada.";
    public ObservableCollection<CatalogRow> Candidates { get; } = [];
    public IReadOnlyList<PlanRow> Rows { get => _rows; set => Set(ref _rows, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand? SimulateCommand { get; set; }
    public string BlockedNotice { get; } =
        "Esta página só simula, localmente. Para aplicar, use a página Broker: o serviço NexusBroker é a única via de escrita e recusa qualquer alteração fora de uma VM de teste liberada.";
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
    public override string Subtitle => "Execuções registradas no diário (aberto somente leitura) e estado da coordenação entre executores.";
    public string JournalPath { get; set; } = NexusServices.DefaultJournalPath;
    public IReadOnlyList<HistoryRun> Runs { get => _runs; set => Set(ref _runs, value); }
    public IReadOnlyList<HistoryEntry> Entries { get => _entries; set => Set(ref _entries, value); }
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
    public override string Subtitle => "O que existe nesta versão e o que foi validado. Rótulos: implementado e validado · implementado, não validado · não suportado neste ambiente · pendente.";
    public IReadOnlyList<CapabilityRow> Rows { get; } = CapabilityCatalog.Rows(OperatingSystem.IsWindows());
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
        "Aplicar e reverter pelo serviço NexusBroker. A escrita real só acontece se o serviço estiver numa VM de teste liberada; fora dela, ele recusa sem alterar nada.";

    /// <summary>As mesmas ações marcadas na página Simulação.</summary>
    public ObservableCollection<CatalogRow> Candidates { get; set; } = [];

    public bool Connected { get => _connected; set => Set(ref _connected, value); }
    public bool WriteAllowed { get => _writeAllowed; set => Set(ref _writeAllowed, value); }
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
