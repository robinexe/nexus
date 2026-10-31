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
    public override string Subtitle => "Leitura do sistema: cada valor mostra se foi detectado, inferido ou está indisponível, e a fonte.";
    public IReadOnlyList<SectionViewModel> Sections { get => _sections; set => Set(ref _sections, value); }
}

// ------------------------------------------------------------------ Diagnóstico

public sealed class DiagnosticsPage : PageViewModel
{
    private IReadOnlyList<DiagnosticFinding> _findings = [];
    public override string Title => "Diagnóstico";
    public override string Subtitle => "Somente leitura. Cada achado cita a evidência; nenhum presume ganho de desempenho.";
    public IReadOnlyList<DiagnosticFinding> Findings
    {
        get => _findings;
        set { Set(ref _findings, value); OnPropertyChanged(nameof(Summary)); }
    }
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
    public string ApplyLabel { get; } = "bloqueado: somente leitura até validação em VM";
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
    public override string Title => "Catálogo";
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
    public override string Title => "Simulação do plano";
    public override string Subtitle => "Dry-run: calcula o que mudaria, na ordem e em lotes, relendo o estado real. Nenhuma alteração é aplicada.";
    public ObservableCollection<CatalogRow> Candidates { get; } = [];
    public IReadOnlyList<PlanRow> Rows { get => _rows; set => Set(ref _rows, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand? SimulateCommand { get; set; }
    public string BlockedNotice { get; } =
        "Aplicação real desativada neste PC: a execução de otimizações continua bloqueada até os testes de integração e recuperação em VM Windows serem aprovados.";
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
    public override string Title => "Capacidades";
    public override string Subtitle => "O que existe nesta versão e o que foi validado. Rótulos: implementado e validado · implementado, não validado · não suportado neste ambiente · pendente.";
    public IReadOnlyList<CapabilityRow> Rows { get; } = CapabilityCatalog.Rows(OperatingSystem.IsWindows());
}

public sealed record OverviewCard(string Title, string Value, FactStatus Status, string Detail);

public sealed class OverviewPage : PageViewModel
{
    private IReadOnlyList<OverviewCard> _cards = [];
    private string _footer = "";
    public override string Title => "Visão geral";
    public override string Subtitle => "Resumo do que foi lido deste PC. Modo somente leitura: nada é alterado.";
    public IReadOnlyList<OverviewCard> Cards { get => _cards; set => Set(ref _cards, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }

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
