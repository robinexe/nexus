using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Broker;
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
    private static readonly string UserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    private BrokerLevel _lastLevel = BrokerLevel.User;

    /// <summary>Aplicar neste PC: broker sob demanda do nível pedido (usuário sem aviso; administrador com o aviso do Windows).</summary>
    private BrokerSession Session(BrokerLevel level) =>
        BrokerSession.ForThisPc(level, AppContext.BaseDirectory, UserSid, m => System.Windows.Application.Current?.Dispatcher.Invoke(() => Broker.LastMessage = m));

    private static BrokerLevel LevelFor(IEnumerable<(CatalogEntry Entry, ActionInstance Instance)> chosen) =>
        chosen.Any(c => c.Entry.Definition.Privileges == PrivilegeLevel.Admin) ? BrokerLevel.Admin : BrokerLevel.User;
    private PageViewModel _selectedPage;
    private string _status = "Iniciando…";
    private bool _busy;
    private SystemSnapshot? _snapshot;
    private IReadOnlyList<DiagnosticFinding> _findings = [];

    public MainViewModel()
    {
        Compare = new ComparePage(Measure);
        Pages = [Overview, Hardware, Diagnostics, Catalog, Plan, Broker, Measure, Compare, History, Capabilities];
        Broker.Candidates = Plan.Candidates;
        Broker.StatusCommand = new RelayCommand(() => _ = BrokerStatusAsync(), () => !Broker.Busy);
        Broker.PlanCommand = new RelayCommand(() => _ = BrokerPlanAsync(), () => !Broker.Busy && Broker.Connected && Chosen().Count > 0);
        Broker.ApplyCommand = new RelayCommand(() => _ = BrokerApplyAsync(), () => !Broker.Busy && Broker.Connected && Broker.WriteAllowed && Chosen().Count > 0);
        Broker.RevertCommand = new RelayCommand(() => _ = BrokerRevertAsync(), () => !Broker.Busy && Broker.WriteAllowed && Broker.LastRun is not null);
        Broker.EndSessionCommand = new RelayCommand(() => _ = BrokerEndSessionAsync(), () => !Broker.Busy && Broker.SessionOpen && Broker.LastRun is not null);
        Broker.RecoverCommand = new RelayCommand(() => _ = BrokerRecoverAsync(), () => !Broker.Busy && Broker.Connected && Broker.WriteAllowed);
        _selectedPage = Overview;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !Busy);
        ExportCommand = new RelayCommand(Export, () => !Busy && _snapshot is not null);
        Plan.SimulateCommand = new RelayCommand(() => _ = SimulateAsync(), () => !Busy && !Plan.Busy && _snapshot is not null);
        History.OnSelected = LoadHistoryEntries;
        ShowMeasureCommand = new RelayCommand(() => SelectedPage = Measure);
        ShowCatalogCommand = new RelayCommand(() => SelectedPage = Catalog);
        ShowDiagnosticsCommand = new RelayCommand(() => SelectedPage = Diagnostics);
        ShowHardwareCommand = new RelayCommand(() => SelectedPage = Hardware);
    }

    public OverviewPage Overview { get; } = new();
    public HardwarePage Hardware { get; } = new();
    public DiagnosticsPage Diagnostics { get; } = new();
    public CatalogPage Catalog { get; } = new();
    public PlanPage Plan { get; } = new();
    public BrokerPage Broker { get; } = new();
    public MeasurePage Measure { get; } = new();
    public ComparePage Compare { get; }
    public HistoryPage History { get; } = new();
    public CapabilitiesPage Capabilities { get; } = new();
    public IReadOnlyList<PageViewModel> Pages { get; }

    public PageViewModel SelectedPage { get => _selectedPage; set => Set(ref _selectedPage, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }

    // Navegação (usada pelos convites da página "Meu PC").
    public RelayCommand ShowMeasureCommand { get; }
    public RelayCommand ShowCatalogCommand { get; }
    public RelayCommand ShowDiagnosticsCommand { get; }
    public RelayCommand ShowHardwareCommand { get; }

    public string ModeBanner { get; } =
        "Nada muda sem você confirmar. Cada alteração fica registrada e pode ser desfeita; mudanças de sistema pedem permissão ao Windows e criam um ponto de restauração.";

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
            CheckPendingRecovery();
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
            doc = CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory);
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

    // ------------------------------------------------------------------ broker (única via de escrita)

    private List<(CatalogEntry Entry, ActionInstance Instance)> Chosen() =>
        Plan.Candidates.Where(c => c.Include && c.Instance is not null).Select(c => (c.Entry, c.Instance!)).ToList();

    private Task<BrokerCallResult> CallBrokerAsync(Func<BrokerSession, BrokerCallResult> call) => CallBrokerAsync(BrokerLevel.User, call);

    private async Task<BrokerCallResult> CallBrokerAsync(BrokerLevel level, Func<BrokerSession, BrokerCallResult> call)
    {
        Broker.Busy = true;
        try
        {
            var session = Session(level);
            var result = await Task.Run(() => call(session));
            Broker.LastMessage = $"{DateTime.Now:HH:mm:ss} {result.Message}";
            if (result.Response?.Results is { } items)
                Broker.Results = items.Select(i => new BrokerResultRow(i.ActionKey, i.Outcome + (i.Inferred ? " [inferido]" : ""), i.Detail ?? "", i.Observed ?? "—", i.Desired ?? "—")).ToList();
            if (!result.Connected) { Broker.Connected = false; Broker.WriteAllowed = false; }
            return result;
        }
        finally
        {
            Broker.Busy = false;
        }
    }

    private async Task BrokerStatusAsync()
    {
        var r = await CallBrokerAsync(b => b.Status());
        if (r.Response?.Status is { } s)
        {
            Broker.Connected = true;
            Broker.WriteAllowed = s.ExecutionAllowed;
            Broker.ServiceStatus = $"Aplicador pronto · versão {s.BrokerVersion} · catálogo {s.CatalogVersion}";
            Broker.Gate = s.ExecutionGateReason;
            Broker.Coordination = $"Coordenação: {s.Coordination}";
            if (s.ActiveSessionRunId is { } run) { Broker.LastRun = run; Broker.SessionOpen = true; }
        }
        else
        {
            Broker.ServiceStatus = r.Message;
            Broker.Gate = "";
            Broker.Coordination = "";
        }
    }

    private async Task BrokerPlanAsync() => await CallBrokerAsync(b => b.Plan(BrokerSession.RequestsFor(Chosen())));

    private async Task BrokerApplyAsync()
    {
        var chosen = Chosen();
        if (chosen.Count == 0) return;
        var answer = System.Windows.MessageBox.Show(BrokerSession.ConfirmationText(chosen), "Confirmar aplicação pelo broker",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) { Broker.LastMessage = "Aplicação cancelada. Nada foi enviado."; return; }
        var level = LevelFor(chosen);
        var r = await CallBrokerAsync(level, b => b.Execute(BrokerSession.RequestsFor(chosen)));
        if (r.Response is { Ok: true, RunId: { } run } resp) { Broker.LastRun = run; Broker.SessionOpen = resp.SessionActive == true; _lastLevel = level; }
        await LoadAfterWriteAsync();
    }

    private async Task BrokerRevertAsync()
    {
        if (Broker.LastRun is not { } run) return;
        var answer = System.Windows.MessageBox.Show($"Reverter a execução {run}? O broker restaura os valores originais registrados no diário.",
            "Confirmar reversão", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var r = await CallBrokerAsync(_lastLevel, b => b.RevertRun(run));
        if (r.Response?.FullyRestored == true) Broker.SessionOpen = false;
        await LoadAfterWriteAsync();
    }

    private async Task BrokerEndSessionAsync()
    {
        if (Broker.LastRun is not { } run) return;
        var r = await CallBrokerAsync(_lastLevel, b => b.EndSession(run));
        if (r.Response?.FullyRestored == true) Broker.SessionOpen = false;
        await LoadAfterWriteAsync();
    }

    private async Task BrokerRecoverAsync()
    {
        var answer = System.Windows.MessageBox.Show("Desfazer as alterações que ficaram pela metade (registradas no diário)?", "Recuperar",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var (user, machine) = PendingRecovery();
        if (user > 0 || machine == 0) await CallBrokerAsync(BrokerLevel.User, b => b.Recover());
        if (machine > 0) await CallBrokerAsync(BrokerLevel.Admin, b => b.Recover());
        await LoadAfterWriteAsync();
    }

    /// <summary>Operações interrompidas nos dois diários (usuário e máquina), lidas sem travar nem escrever.</summary>
    private static (int User, int Machine) PendingRecovery()
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
        return (Count(LocalBroker.JournalPath(BrokerLevel.User)), Count(LocalBroker.JournalPath(BrokerLevel.Admin)));
    }

    /// <summary>Ao abrir: avisa se alguma alteração ficou pela metade (ex.: PC desligou no meio).</summary>
    private void CheckPendingRecovery()
    {
        var (user, machine) = PendingRecovery();
        if (user + machine == 0) return;
        Broker.LastMessage = $"{user + machine} alteração(ões) ficaram pela metade da última vez. Use \"Recuperar interrompidas\" para desfazer.";
        Status = $"Atenção: {user + machine} alteração(ões) pela metade. Abra Aplicar → Recuperar interrompidas.";
    }

    /// <summary>Depois de uma escrita pelo broker, relê o estado (catálogo e histórico) para a tela mostrar o que mudou.</summary>
    private async Task LoadAfterWriteAsync()
    {
        if (_snapshot is not null) await LoadCatalogAsync(_snapshot.Facts);
        LoadHistory();
        await BrokerStatusAsync();
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
