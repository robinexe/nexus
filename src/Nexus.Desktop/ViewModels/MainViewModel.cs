using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;

namespace Nexus.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly NexusServices _services = new();
    private PageViewModel _selectedPage;
    private string _status = "Iniciando…";
    private bool _busy;
    private SystemSnapshot? _snapshot;
    private IReadOnlyList<DiagnosticFinding> _findings = [];

    public MainViewModel()
    {
        Pages = [Overview, Hardware, Diagnostics, Catalog, Plan, History, Capabilities];
        _selectedPage = Overview;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !Busy);
        ExportCommand = new RelayCommand(Export, () => !Busy && _snapshot is not null);
        Plan.SimulateCommand = new RelayCommand(() => _ = SimulateAsync(), () => !Busy && !Plan.Busy && _snapshot is not null);
        History.OnSelected = LoadHistoryEntries;
    }

    public OverviewPage Overview { get; } = new();
    public HardwarePage Hardware { get; } = new();
    public DiagnosticsPage Diagnostics { get; } = new();
    public CatalogPage Catalog { get; } = new();
    public PlanPage Plan { get; } = new();
    public HistoryPage History { get; } = new();
    public CapabilitiesPage Capabilities { get; } = new();
    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel SelectedPage { get => _selectedPage; set => Set(ref _selectedPage, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }

    public string ModeBanner { get; } =
        "MODO SOMENTE LEITURA — esta versão não altera nada neste PC. Aplicação de otimizações bloqueada até a aprovação dos testes em VM Windows.";

    public async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            Status = "Lendo hardware e configuração do Windows…";
            var snapshot = await Task.Run(Discovery.Capture);
            _snapshot = snapshot;
            _findings = DiagnosticRules.Evaluate(snapshot.Facts);
            Hardware.Sections = snapshot.Sections.Select(s => new SectionViewModel(s)).ToList();
            Diagnostics.Findings = _findings;
            Overview.Cards = OverviewPage.Build(snapshot, _findings);

            Status = "Lendo o estado real das ações do catálogo…";
            await LoadCatalogAsync(snapshot.Facts);

            Status = "Lendo o histórico…";
            LoadHistory();

            var unavailable = snapshot.Sections.SelectMany(s => s.Facts).Count(f => f.Status == FactStatus.Unavailable);
            Overview.Footer = $"Leitura concluída em {snapshot.Elapsed.TotalSeconds:0.0} s · {unavailable} valor(es) indisponível(is), cada um com o motivo.";
            Status = $"Pronto · leitura de {snapshot.TakenAt:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Status = $"Erro na leitura: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task LoadCatalogAsync(SystemFacts facts)
    {
        CatalogDocument doc;
        try
        {
            doc = CatalogLoader.Load(NexusServices.CatalogPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            Catalog.Version = $"catálogo indisponível: {ex.Message}";
            Catalog.Rows = [];
            return;
        }

        var parameters = NexusServices.ParametersFrom(facts);
        var rows = doc.Actions.Select(e =>
        {
            var instance = e.TryInstantiate(parameters, out var reason);
            var implemented = _services.Handlers.Contains(e.Definition.HandlerKind);
            if (!implemented)
            {
                instance = null;
                reason = $"handler '{e.Definition.HandlerKind}' pendente";
            }
            return new CatalogRow(e, instance, reason, CapabilityCatalog.ForHandler(implemented, OperatingSystem.IsWindows()));
        }).ToList();

        Catalog.Version = $"{doc.CatalogVersion} ({doc.Schema}) · {doc.Actions.Count} ações · {doc.Rejected.Count} rejeitadas";
        Catalog.Rows = rows;
        Catalog.Rejected = doc.Rejected;
        Plan.Candidates.Clear();
        foreach (var r in rows.Where(r => r.CanSimulate)) Plan.Candidates.Add(r);

        // Leitura do estado atual (Detect) fora da thread da interface.
        var reads = await Task.Run(() => rows.Where(r => r.Instance is not null).Select(r =>
        {
            try
            {
                var s = _services.Handlers.Get(r.Instance!.Definition.HandlerKind).Detect(r.Instance);
                var origin = s.Origin is ValueOrigin.PolicyEnforced or ValueOrigin.ThirdPartyManaged ? $"  (gerenciado: {s.Origin})" : "";
                return (Row: r, Text: s.Describe() + origin, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Row: r, Text: "erro de leitura", Error: $"{ex.GetType().Name}: {ex.Message}");
            }
        }).ToList());
        foreach (var (row, text, error) in reads)
        {
            row.Current = text;
            row.ReadError = error;
        }
    }

    private async Task SimulateAsync()
    {
        if (_snapshot is null) return;
        Plan.Busy = true;
        try
        {
            var chosen = Plan.Candidates.Where(c => c.Include && c.Instance is not null).Select(c => c.Instance!).ToList();
            if (chosen.Count == 0)
            {
                Plan.Rows = [];
                Plan.Summary = "Nenhuma ação selecionada.";
                return;
            }
            var context = NexusServices.ContextFrom(_snapshot.Facts);
            var plan = await Task.Run(() => new DesiredStateResolver(_services.Handlers).Resolve(chosen, context));
            Plan.Rows = plan.Items.Select(i => new PlanRow(
                $"{i.Action.Definition.Title} ({i.Action.Definition.Key})",
                i.Status switch
                {
                    PlanStatus.Pending => "Seria aplicada",
                    PlanStatus.AlreadyConfigured => "Já configurada",
                    PlanStatus.NotApplicable => "Não aplicável",
                    _ => "Bloqueada",
                },
                i.Status is PlanStatus.Pending or PlanStatus.AlreadyConfigured ? i.ObservedAtPlan.Describe() : "—",
                i.Action.Desired.Describe(),
                i.Status == PlanStatus.Pending ? (i.Batch == 0 ? "1 (normal)" : "2 (após reinício)") : "—",
                i.Reason ?? "",
                i.Status)).ToList();
            var pending = plan.Items.Count(i => i.Status == PlanStatus.Pending);
            Plan.Summary = $"{pending} seriam aplicadas · {plan.Items.Count(i => i.Status == PlanStatus.AlreadyConfigured)} já configuradas · " +
                           $"{plan.Items.Count(i => i.Status == PlanStatus.NotApplicable)} não aplicáveis · {plan.Items.Count(i => i.Status == PlanStatus.Blocked)} bloqueadas" +
                           (plan.RequiresReboot ? " · exigiria reinício" : "") +
                           (_snapshot.Facts.WindowsBuild is null ? " · ATENÇÃO: build do Windows indisponível, compatibilidade não verificada" : "") +
                           $" · simulado às {DateTime.Now:HH:mm:ss}. Nada foi aplicado.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            Plan.Summary = $"Simulação recusada: {ex.Message}";
        }
        catch (Exception ex)
        {
            Plan.Summary = $"Simulação falhou ao ler o estado: {ex.GetType().Name}: {ex.Message}. Nada foi aplicado.";
        }
        finally
        {
            Plan.Busy = false;
        }
    }

    private void LoadHistory()
    {
        var path = History.JournalPath;
        try
        {
            var probe = NexusServices.ProbeExecutors(path);
            History.Coordination = probe.State switch
            {
                CoordinationState.Active => $"Executor ativo: {probe.Holder?.Describe() ?? "dono não identificado"}.",
                CoordinationState.Idle when probe.AbandonedByCrash => $"Nenhum executor ativo. Trava abandonada por: {probe.Holder!.Describe()}.",
                CoordinationState.Idle => "Nenhum executor ativo.",
                _ => $"Não foi possível verificar executores ativos: {probe.Detail}",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            History.Coordination = $"Coordenação não verificada: {ex.Message}";
        }

        if (!File.Exists(path))
        {
            History.Status = $"Nenhum diário em {path}: nenhuma execução do NEXUS foi registrada neste PC.";
            History.Runs = [];
            History.Entries = [];
            return;
        }
        try
        {
            using var journal = SqliteJournal.OpenReadOnly(path);
            History.Runs = journal.Runs().OrderByDescending(r => r.StartedAt).Select(r => new HistoryRun(r.RunId, $"{r.StartedAt.ToLocalTime():dd/MM/yyyy HH:mm}",
                r.EndedAt is null ? "não encerrada" : $"encerrada {r.EndedAt.Value.ToLocalTime():dd/MM HH:mm}", r.Profile, r.CatalogVersion)).ToList();
            History.Status = $"Diário {path} (somente leitura) · {History.Runs.Count} execução(ões).";
        }
        catch (Exception ex)
        {
            History.Status = $"Diário encontrado mas não pôde ser lido: {ex.GetType().Name}: {ex.Message}";
            History.Runs = [];
        }
    }

    private void LoadHistoryEntries(HistoryRun? run)
    {
        if (run is null) { History.Entries = []; return; }
        try
        {
            using var journal = SqliteJournal.OpenReadOnly(History.JournalPath);
            History.Entries = journal.Entries(run.RunId).Select(e => new HistoryEntry(e.Id, e.State.ToString(), e.Action.Definition.Key,
                e.Action.TargetDescription, e.Original?.Describe() ?? "—", e.Action.Desired.Describe(), e.LastObserved?.Describe() ?? "—", e.Note)).ToList();
        }
        catch (Exception ex)
        {
            History.Status = $"Falha ao ler entradas: {ex.Message}";
        }
    }

    private void Export()
    {
        if (_snapshot is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar relatório de leitura (JSON)",
            FileName = $"nexus-leitura-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;
        var report = new
        {
            geradoEm = DateTimeOffset.Now,
            modo = "somente leitura",
            snapshot = _snapshot,
            diagnostico = _findings,
            catalogo = Catalog.Rows.Select(r => new { r.Id, r.Title, r.Handler, estadoAtual = r.Current, erro = r.ReadError, desejado = r.Desired, leitura = r.ReadLabelText, r.NotInstantiableReason }),
            simulacao = Plan.Rows,
            capacidades = Capabilities.Rows,
        };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, ExportJson), new System.Text.UTF8Encoding(true));
        Status = $"Relatório exportado para {dialog.FileName}";
    }
}
