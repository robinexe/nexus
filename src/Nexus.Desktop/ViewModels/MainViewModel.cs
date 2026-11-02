using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Assistant;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;

namespace Nexus.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IModuleHost
{
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    // Handlers de leitura de todos os módulos: montados na primeira leitura do catálogo, já fora da thread da interface
    // (antes eram criados no construtor, na abertura, antes da janela aparecer).
    private readonly Lazy<NexusServices> _servicesLazy = new(() => new NexusServices(), LazyThreadSafetyMode.ExecutionAndPublication);
    private NexusServices ReadOnlyServices => _servicesLazy.Value;
    private static readonly string CurrentUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    private BrokerLevel _lastLevel = BrokerLevel.User;

    /// <summary>Aplicar neste PC: broker sob demanda do nível pedido (usuário sem aviso; administrador com o aviso do Windows).</summary>
    private BrokerSession Session(BrokerLevel level, IProgress<ApplyStage>? stages = null) =>
        BrokerSession.ForThisPc(level, AppContext.BaseDirectory, CurrentUserSid, m =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => { Broker.LastMessage = m; Broker.LastSummary = m; });
            stages?.Report(level == BrokerLevel.Admin ? ApplyStage.WaitingWindows : ApplyStage.StartingApplier);
        }, stages is null ? null : () => stages.Report(ApplyStage.Running));

    private static BrokerLevel LevelFor(IEnumerable<(CatalogEntry Entry, ActionInstance Instance)> chosen) =>
        chosen.Any(c => c.Entry.Definition.Privileges == PrivilegeLevel.Admin) ? BrokerLevel.Admin : BrokerLevel.User;
    private PageViewModel _selectedPage;
    private string _status = "Iniciando…";
    private bool _busy;
    private SystemSnapshot? _snapshot;
    private IReadOnlyList<DiagnosticFinding> _findings = [];

    public MainViewModel()
    {
        Pages = [Overview, Hardware, Diagnostics, CpuLab, GpuLab, MemoryLab, MemoryDoctor, Assistant, Automation, Catalog, Plan, Broker, Startup, Services, Cleaning, Disk, Programs,
            Privacy, Security, Scanner, SystemTools, DriverUpdates, Measure, Monitor, History, Settings, Capabilities];
        Broker.Candidates = Plan.Candidates;
        Broker.StatusCommand = new RelayCommand(() => _ = BrokerStatusAsync(), () => !Broker.Busy);
        Broker.PlanCommand = new RelayCommand(() => _ = BrokerPlanAsync(), () => !Broker.Busy && Broker.Connected && Chosen().Count > 0);
        Broker.ApplyCommand = new RelayCommand(() => _ = BrokerApplyAsync(), () => !Broker.Busy && !(Broker.Connected && !Broker.WriteAllowed) && Chosen().Count > 0); // consulta sozinho ao clicar
        Broker.RevertCommand = new RelayCommand(() => _ = BrokerRevertAsync(), () => !Broker.Busy && Broker.WriteAllowed && Broker.LastRun is not null);
        Broker.EndSessionCommand = new RelayCommand(() => _ = BrokerEndSessionAsync(), () => !Broker.Busy && Broker.SessionOpen && Broker.LastRun is not null);
        Broker.RecoverCommand = new RelayCommand(() => _ = BrokerRecoverAsync(), () => !Broker.Busy && Broker.Connected && Broker.WriteAllowed);
        _selectedPage = Overview;
        ApplyView = new ApplyOverlay(() => SelectedPage = Broker);
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !Busy);
        RelaunchAsAdminCommand = new RelayCommand(RelaunchAsAdmin);
        ExportCommand = new RelayCommand(Export, () => !Busy && _snapshot is not null);
        Plan.SimulateCommand = new RelayCommand(() => _ = SimulateAsync(), () => !Busy && !Plan.Busy && _snapshot is not null);
        History.OnSelected = LoadHistoryEntries;
        History.Host = this;
        ShowMeasureCommand = new RelayCommand(() => SelectedPage = Measure);
        ShowCatalogCommand = new RelayCommand(() => SelectedPage = Catalog);
        ShowDiagnosticsCommand = new RelayCommand(() => SelectedPage = Diagnostics);
        ShowHardwareCommand = new RelayCommand(() => SelectedPage = Hardware);
        foreach (var module in Pages.OfType<ModulePage>()) module.Attach(this);
    }

    // ------------------------------------------------------------------ IModuleHost (páginas dos módulos)

    public SystemFacts? Facts => _snapshot?.Facts;
    string IModuleHost.UserSid => CurrentUserSid;

    // ------------------------------------------------------------------ modo autoteste (--selftest-ui)

    /// <summary>
    /// Autoteste da interface: nenhum pedido chega ao aplicador (aplicar, desfazer e simular são recusados sem abrir o
    /// aplicador), Confirm responde "não" sem janela. Ligado por App antes de criar a janela; nunca desligado na mesma execução.
    /// </summary>
    internal static bool SelfTestMode { get; set; }

    /// <summary>Quantos pedidos de escrita/simulação o modo autoteste recusou.</summary>
    internal int SelfTestRefusals { get; private set; }

    /// <summary>A tarefa do último OnShownAsync disparado pela navegação (o autoteste espera por ela e vê a exceção).</summary>
    internal Task? LastShownTask { get; private set; }

    private bool RefuseInSelfTest()
    {
        if (!SelfTestMode) return false;
        SelfTestRefusals++;
        Notify("Autoteste: pedido ao aplicador recusado. Nada foi alterado.");
        return true;
    }

    public Task<BrokerCallResult> PlanAsync(IReadOnlyList<ActionRequest> requests, BrokerLevel level) =>
        RefuseInSelfTest() ? Task.FromResult(new BrokerCallResult(false, null, "Autoteste: o aplicador não é iniciado."))
            : CallBrokerAsync(level, b => b.Plan(requests));

    public Task<BrokerCallResult?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level) =>
        ApplyAsync(title, confirmation, requests, level, null);

    public async Task<BrokerCallResult?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level, IProgress<ApplyStage>? stages)
    {
        if (requests.Count == 0 || RefuseInSelfTest()) return null;
        // Quem passa marcos (o Assistente) tem a própria tela de progresso: a da janela não aparece por cima.
        var own = stages is null ? ApplyView.Begin(title, requests.Select(r => (r.ActionId, ActionName(r.ActionId))).ToList(), level, name: ActionName) : null;
        return await ExecuteWithProgressAsync(() => Confirm(title, confirmation), level, b => b.Execute(requests, "modulo"), stages, own, revert: false);
    }

    /// <summary>Tela de progresso da janela (qualquer aplicar/desfazer vindo de uma página que não tenha a sua própria).</summary>
    public ApplyOverlay ApplyView { get; }

    private Dictionary<string, string> _actionTitles = new(StringComparer.Ordinal);

    /// <summary>Nome para pessoas de uma ação do catálogo (id@versão → título); a própria chave se o catálogo não carregou.</summary>
    private string ActionName(string key) => _actionTitles.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t) ? t : key;

    /// <summary>
    /// Confirma, chama o aplicador e relê o PC, avisando os marcos reais a quem acompanha (a página, com <paramref name="stages"/>,
    /// e/ou a tela de progresso da janela, com <paramref name="own"/>). Null = o usuário cancelou.
    /// </summary>
    private async Task<BrokerCallResult?> ExecuteWithProgressAsync(Func<bool> confirm, BrokerLevel level, Func<BrokerSession, BrokerCallResult> call,
        IProgress<ApplyStage>? stages, HostApplyProgress? own, bool revert)
    {
        own?.Stage(ApplyStage.WaitingConfirmation);
        if (!confirm())
        {
            if (own is not null) { own.Canceled(); ApplyView.Close(); }
            Notify("Cancelado. Nada foi alterado.");
            return null;
        }
        try
        {
            // Marcos vindos da thread do aplicador voltam para a interface na ordem em que aconteceram.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            IProgress<ApplyStage>? ui = stages is null && own is null ? null
                : new UiStages(new StageRelay(s => { stages?.Report(s); own?.Stage(s); }), dispatcher);
            ui?.Report(ApplyStage.Confirmed);
            var r = await CallBrokerAsync(level, call, ui);
            if (!revert && r.Response is { RunId: { } run } resp && r.AllRunIds.Count > 0) { Broker.LastRun = run; _lastRuns = r.AllRunIds; Broker.SessionOpen = resp.SessionActive == true; _lastLevel = level; }
            own?.Finished(r);
            Notify(revert ? ApplySummary.DescribeRevert(r) : ApplySummary.Describe(r));
            ui?.Report(ApplyStage.Refreshing);
            await LoadAfterWriteAsync();
            if (own is not null && ReferenceEquals(ApplyView.Progress, own)) ApplyView.Finish();
            return r;
        }
        catch
        {
            if (own is not null) ApplyView.Close();
            throw;
        }
    }

    /// <summary>
    /// Autoteste da interface: mostra a tela de progresso da janela com dados de DEMONSTRAÇÃO (rotulados assim) para fotografar.
    /// Nada é enviado ao aplicador.
    /// </summary>
    internal void ShowApplyDemo()
    {
        (string, string)[] actions =
        [
            ("demonstracao-1", "Desligar a telemetria opcional"), ("demonstracao-2", "Desligar anúncios personalizados"), ("demonstracao-3", "Plano de energia: alto desempenho"),
            ("demonstracao-4", "Modo de Jogo ligado"), ("demonstracao-5", "Agendamento de GPU acelerado por hardware"), ("demonstracao-6", "Desligar a Xbox Game Bar em segundo plano"),
        ];
        var p = ApplyView.Begin("Aplicar os ajustes marcados (demonstração do autoteste)", actions, BrokerLevel.Admin);
        p.Stage(ApplyStage.WaitingConfirmation);
        ApplyView.AdvanceClock(TimeSpan.FromSeconds(3));
        p.Stage(ApplyStage.Confirmed);
        p.Stage(ApplyStage.WaitingWindows);
        ApplyView.AdvanceClock(TimeSpan.FromSeconds(4));
        p.Stage(ApplyStage.Running);
        ApplyView.AdvanceClock(TimeSpan.FromSeconds(ProcessTimeModel.AdminRestorePoint + 2.5));
        ApplyView.Tick();
    }

    internal void HideApplyDemo() => ApplyView.Close();

    private sealed class StageRelay(Action<ApplyStage> action) : IProgress<ApplyStage>
    {
        public void Report(ApplyStage value) => action(value);
    }

    /// <summary>Repassa o marco na thread da interface (direto quando já está nela; senão, na fila, preservando a ordem).</summary>
    private sealed class UiStages(IProgress<ApplyStage> target, System.Windows.Threading.Dispatcher? dispatcher) : IProgress<ApplyStage>
    {
        public void Report(ApplyStage value)
        {
            if (dispatcher is null || dispatcher.CheckAccess()) target.Report(value);
            else dispatcher.BeginInvoke(() => target.Report(value));
        }
    }

    public Task<BrokerCallResult?> RevertAsync(Guid runId, BrokerLevel level) =>
        RevertCoreAsync(runId, level, () => Confirm("Desfazer", "Desfazer esta alteração? O NEXUS volta ao estado registrado no diário antes da mudança."));

    public Task<BrokerCallResult?> RevertWithoutConfirmationAsync(Guid runId, BrokerLevel level) => RevertCoreAsync(runId, level, () => true);

    private async Task<BrokerCallResult?> RevertCoreAsync(Guid runId, BrokerLevel level, Func<bool> confirm)
    {
        if (RefuseInSelfTest()) return null;
        var own = ApplyView.Begin("Desfazer alteração", [], level, revert: true, name: ActionName);
        var r = await ExecuteWithProgressAsync(confirm, level, b => b.RevertRun(runId), null, own, revert: true);
        if (r?.Response?.FullyRestored == true && Broker.LastRun == runId) Broker.SessionOpen = false;
        return r;
    }

    public void Notify(string message) => Status = message;

    public bool Confirm(string title, string message) => !SelfTestMode &&
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    public void Navigate(PageViewModel page) => SelectedPage = page;

    public PageViewModel? FindPage(Type pageType) => Pages.FirstOrDefault(p => p.GetType() == pageType);

    public OverviewPage Overview { get; } = Make<OverviewPage>();
    public HardwarePage Hardware { get; } = Make<HardwarePage>();
    public DiagnosticsPage Diagnostics { get; } = Make<DiagnosticsPage>();
    public CatalogPage Catalog { get; } = Make<CatalogPage>();
    public PlanPage Plan { get; } = Make<PlanPage>();
    public BrokerPage Broker { get; } = Make<BrokerPage>();
    public MeasurePage Measure { get; } = Make<MeasurePage>();
    public HistoryPage History { get; } = Make<HistoryPage>();
    public CapabilitiesPage Capabilities { get; } = Make<CapabilitiesPage>();
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Cria uma página anotando no desktop-diag.log as que demoram (o construtor roda na thread da interface, na abertura).</summary>
    private static T Make<T>() where T : new()
    {
        using var _ = Nexus.Desktop.Services.Shell.UiWatchdog.Phase($"criando a página {typeof(T).Name}", logAboveMs: 30);
        return new T();
    }

    // Módulos
    public AssistantPage Assistant { get; } = Make<AssistantPage>();
    public AutomationPage Automation { get; } = Make<AutomationPage>();
    public StartupPage Startup { get; } = Make<StartupPage>();
    public ServicesPage Services { get; } = Make<ServicesPage>();
    public CleaningPage Cleaning { get; } = Make<CleaningPage>();
    public DiskPage Disk { get; } = Make<DiskPage>();
    public ProgramsPage Programs { get; } = Make<ProgramsPage>();
    public PrivacyPage Privacy { get; } = Make<PrivacyPage>();
    public SecurityPage Security { get; } = Make<SecurityPage>();
    public ScannerPage Scanner { get; } = Make<ScannerPage>();
    public SystemToolsPage SystemTools { get; } = Make<SystemToolsPage>();
    public DriverUpdatesPage DriverUpdates { get; } = Make<DriverUpdatesPage>();
    public MonitorPage Monitor { get; } = Make<MonitorPage>();
    public CpuLabPage CpuLab { get; } = Make<CpuLabPage>();
    public GpuLabPage GpuLab { get; } = Make<GpuLabPage>();
    public MemoryLabPage MemoryLab { get; } = Make<MemoryLabPage>();
    public MemoryDoctorPage MemoryDoctor { get; } = Make<MemoryDoctorPage>();
    public SettingsPage Settings { get; } = Make<SettingsPage>();
    /// <summary>Atualização do programa (faixa "pronto — Reiniciar e atualizar" na janela; ver AppUpdates).</summary>
    public AppUpdates Updates { get; } = AppUpdates.Instance;

    public PageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!Set(ref _selectedPage, value)) return;
            Nexus.Desktop.Services.Shell.UiWatchdog.Step($"abrindo página {value?.Title}");
            LastShownTask = value is ModulePage m ? m.OnShownAsync() : null;
        }
    }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RelaunchAsAdminCommand { get; }

    /// <summary>Não elevado e fora do autoteste: oferece reabrir como administrador (só para ler mais; escrever continua pelo aplicador).</summary>
    public bool CanRelaunchAsAdmin { get; } = !IsProcessElevated() && !Environment.GetCommandLineArgs().Any(a => a.StartsWith("--selftest", StringComparison.Ordinal));

    private static bool IsProcessElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (System.Security.SecurityException) { return false; }
    }

    private void RelaunchAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--admin-relaunch") { UseShellExecute = true, Verb = "runas" });
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { Status = "Você recusou o aviso do Windows. O NEXUS continua aberto sem administrador."; }
    }
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
            Nexus.Desktop.Services.Shell.UiWatchdog.Step($"leitura do PC pronta em {snapshot.Elapsed.TotalSeconds:0.0} s (fora da interface)");
            _snapshot = snapshot;
            _findings = DiagnosticRules.Evaluate(snapshot.Facts);
            Hardware.Sections = snapshot.Sections.Select(s => new SectionViewModel(s)).ToList();
            Diagnostics.Findings = _findings;
            Overview.Cards = OverviewPage.Build(snapshot, _findings);

            Status = "Lendo o estado real das ações do catálogo…";
            await LoadCatalogAsync(snapshot.Facts);

            Status = "Lendo o histórico…";
            await LoadHistoryAsync();

            var unavailable = snapshot.Sections.SelectMany(s => s.Facts).Count(f => f.Status == FactStatus.Unavailable);
            Overview.Footer = $"Leitura concluída em {snapshot.Elapsed.TotalSeconds:0.0} s · {unavailable} valor(es) indisponível(is), cada um com o motivo.";
            Status = $"Pronto · leitura de {snapshot.TakenAt:HH:mm:ss}";
            Nexus.Desktop.Services.Shell.UiWatchdog.Step("leitura do PC concluída");
            await CheckPendingRecoveryAsync();
            foreach (var module in Pages.OfType<ModulePage>())
            {
                Nexus.Desktop.Services.Shell.UiWatchdog.Step($"leitura do PC → {module.Title}");
                try
                {
                    // Só a parte síncrona (até o primeiro await) prende a interface: anotada quando passa de 50 ms.
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var task = module.OnFactsReadyAsync(snapshot.Facts);
                    var blocked = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (blocked >= 50) Nexus.Desktop.Services.Shell.UiWatchdog.Note($"{module.Title}: {blocked} ms na thread da interface ao receber a leitura do PC");
                    await task;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Status = $"{module.Title}: {ex.Message}"; }
            }
            await LoadOwnerChecksAsync(snapshot);
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

    /// <summary>
    /// "Só você pode fazer" (Resizable BAR, XMP/EXPO, MUX, link PCIe): lidos depois do resto (NVML + consulta D3D12 + SMBIOS),
    /// fora da thread da interface. Entram no Diagnóstico e no Hardware; falha de leitura só deixa de mostrar o ponto.
    /// </summary>
    private async Task LoadOwnerChecksAsync(SystemSnapshot snapshot)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Nexus.Desktop.Services.Shell.UiWatchdog.Step("leitura do PC → só você pode fazer");
            var owner = await Task.Run(() => Nexus.Assistant.AssistantReaders.OwnerFindings(snapshot.Facts));
            if (owner.Count == 0 || !ReferenceEquals(_snapshot, snapshot)) return;
            // owner.xmp diz o mesmo que mem.speed, com a explicação e a fonte: fica só ele.
            var xmp = owner.Any(f => f.Id == OwnerChecks.XmpId);
            _findings = [.. _findings.Where(f => f.Owner is null && !(xmp && f.Id == "mem.speed")), .. owner];
            Diagnostics.Findings = _findings;
            Overview.Cards = OverviewPage.Build(snapshot, _findings);
            if (HardwarePage.OwnerSection(owner) is { } section)
                Hardware.Sections = [.. Hardware.Sections.Where(x => x.Title != HardwarePage.OwnerSectionTitle), new SectionViewModel(section)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = $"Só você pode fazer: {ex.Message}"; }
    }

    private async Task LoadCatalogAsync(SystemFacts facts)
    {
        // Ler e validar o catálogo (vários JSON), montar os handlers de leitura e instanciar cada ação: tudo fora da thread da
        // interface (antes era síncrono aqui e contava para a parada "interface iniciada" da abertura).
        var parameters = NexusServices.ParametersFrom(facts);
        var windows = OperatingSystem.IsWindows();
        CatalogDocument doc;
        List<CatalogRow> rows;
        Dictionary<string, string> titles;
        try
        {
            (doc, rows, titles) = await Task.Run(() =>
            {
                var d = CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory);
                var t = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in d.Actions) t[e.Definition.Key] = e.Definition.Title;
                var handlers = ReadOnlyServices.Handlers;
                var r = d.Actions.Select(e =>
                {
                    var instance = e.TryInstantiate(parameters, out var reason);
                    var implemented = handlers.Contains(e.Definition.HandlerKind);
                    if (!implemented)
                    {
                        instance = null;
                        reason = $"handler '{e.Definition.HandlerKind}' pendente";
                    }
                    return new CatalogRow(e, instance, reason, CapabilityCatalog.ForHandler(implemented, windows));
                }).ToList();
                return (d, r, t);
            });
            _actionTitles = titles;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            Catalog.Version = $"catálogo indisponível: {ex.Message}";
            Catalog.Rows = [];
            return;
        }

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
                var s = ReadOnlyServices.Handlers.Get(r.Instance!.Definition.HandlerKind).Detect(r.Instance);
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
            // Simulação: a interface não roda elevada, mas aplicar eleva sozinho (aviso do Windows). Então simula como administrador
            // e marca o que vai pedir permissão, em vez de dizer "bloqueada" para tudo que é de sistema.
            var context = NexusServices.ContextFrom(_snapshot.Facts) with { IsElevated = true };
            var plan = await Task.Run(() => new DesiredStateResolver(ReadOnlyServices.Handlers).Resolve(chosen, context));
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
                (i.Action.Definition.Privileges == PrivilegeLevel.Admin && i.Status == PlanStatus.Pending ? "Pede a permissão do Windows ao aplicar. " : "") + (i.Reason ?? ""),
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

    private async Task<BrokerCallResult> CallBrokerAsync(BrokerLevel level, Func<BrokerSession, BrokerCallResult> call, IProgress<ApplyStage>? stages = null)
    {
        Broker.Busy = true;
        try
        {
            var session = Session(level, stages);
            var result = await Task.Run(() => call(session));
            Broker.LastMessage = $"{DateTime.Now:HH:mm:ss} {result.Message}";
            Broker.LastSummary = $"{DateTime.Now:HH:mm} · {ApplySummary.Human(result)}";
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
        if (!Broker.Connected)
        {
            // "O aplicador inicia sozinho quando você aplica": consulta primeiro (inicia o aplicador), sem exigir outro clique.
            await BrokerStatusAsync();
            if (!Broker.Connected || !Broker.WriteAllowed)
            {
                Broker.LastSummary = $"{DateTime.Now:HH:mm} · Não deu para aplicar: {(Broker.Connected ? Broker.Gate : Broker.ServiceStatus)}";
                return;
            }
        }
        var level = LevelFor(chosen);
        var requests = BrokerSession.RequestsFor(chosen);
        var own = ApplyView.Begin("Aplicar os ajustes marcados", chosen.Select(c => (c.Entry.Definition.Key, c.Entry.Definition.Title)).ToList(), level, name: ActionName);
        var r = await ExecuteWithProgressAsync(() => System.Windows.MessageBox.Show(BrokerSession.ConfirmationText(chosen), "Confirmar aplicação",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes,
            level, b => b.Execute(requests), null, own, revert: false);
        if (r is null) Broker.LastMessage = Broker.LastSummary = "Aplicação cancelada. Nada foi enviado.";
    }

    private IReadOnlyList<Guid> _lastRuns = [];

    private async Task BrokerRevertAsync()
    {
        if (Broker.LastRun is not { } run) return;
        var answer = System.Windows.MessageBox.Show("Desfazer a última aplicação? O NEXUS volta os valores originais guardados no diário.",
            "Confirmar reversão", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var own = ApplyView.Begin("Reverter a última execução", [], _lastLevel, revert: true, name: ActionName);
        // Aplicação grande = vários lotes: desfaz todos, do último para o primeiro.
        var runs = _lastRuns.Contains(run) ? _lastRuns.Reverse().ToList() : [run];
        var r = await ExecuteWithProgressAsync(() => true, _lastLevel, b =>
        {
            BrokerCallResult res = null!;
            foreach (var id in runs) { res = b.RevertRun(id); if (!res.Ok) break; }
            return res;
        }, null, own, revert: true);
        if (r?.Response?.FullyRestored == true) _lastRuns = [];
        if (r?.Response?.FullyRestored == true) Broker.SessionOpen = false;
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
    private async Task CheckPendingRecoveryAsync()
    {
        var (user, machine) = await Task.Run(PendingRecovery); // dois diários SQLite: fora da thread da interface
        if (user + machine == 0) return;
        Broker.LastMessage = $"{user + machine} alteração(ões) ficaram pela metade da última vez. Use \"Recuperar interrompidas\" para desfazer.";
        Status = $"Atenção: {user + machine} alteração(ões) pela metade. Abra Aplicar → Recuperar interrompidas.";
    }

    /// <summary>Depois de uma escrita pelo broker, relê o estado (catálogo e histórico) para a tela mostrar o que mudou.</summary>
    private async Task LoadAfterWriteAsync()
    {
        if (_snapshot is not null) await LoadCatalogAsync(_snapshot.Facts);
        await LoadHistoryAsync();
        await BrokerStatusAsync();
    }

    /// <summary>Diário (SQLite) e sondagem da trava lidos fora da thread da interface; o resultado entra na página igual a antes.</summary>
    private async Task LoadHistoryAsync()
    {
        var path = History.JournalPath;
        var (coordination, status, runs, noJournal) = await Task.Run(() => ReadHistory(path));
        History.Coordination = coordination;
        History.Status = status;
        History.Runs = runs;
        if (noJournal) History.Entries = [];
    }

    private static (string Coordination, string Status, IReadOnlyList<HistoryRun> Runs, bool NoJournal) ReadHistory(string path)
    {
        string coordination;
        try
        {
            var probe = NexusServices.ProbeExecutors(path);
            coordination = probe.State switch
            {
                CoordinationState.Active => $"Executor ativo: {probe.Holder?.Describe() ?? "dono não identificado"}.",
                CoordinationState.Idle when probe.AbandonedByCrash => $"Nenhum executor ativo. Trava abandonada por: {probe.Holder!.Describe()}.",
                CoordinationState.Idle => "Nenhum executor ativo.",
                _ => $"Não foi possível verificar executores ativos: {probe.Detail}",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            coordination = $"Coordenação não verificada: {ex.Message}";
        }

        if (!File.Exists(path))
            return (coordination, $"Nenhum diário em {path}: nenhuma execução do NEXUS foi registrada neste PC.", [], true);
        try
        {
            using var journal = SqliteJournal.OpenReadOnly(path);
            var runs = journal.Runs().OrderByDescending(r => r.StartedAt).Select(r => new HistoryRun(r.RunId, $"{r.StartedAt.ToLocalTime():dd/MM/yyyy HH:mm}",
                r.EndedAt is null ? "não encerrada" : $"encerrada {r.EndedAt.Value.ToLocalTime():dd/MM HH:mm}", r.Profile, r.CatalogVersion)).ToList();
            return (coordination, $"Diário {path} (somente leitura) · {runs.Count} execução(ões).", runs, false);
        }
        catch (Exception ex)
        {
            return (coordination, $"Diário encontrado mas não pôde ser lido: {ex.GetType().Name}: {ex.Message}", [], false);
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
