// CA1416: os métodos só para Windows entram em All() com WindowsOnly = true; o SelfTestRunner nunca os chama fora do
// Windows (vira IGNORADO: "não é Windows"). O analisador não enxerga essa garantia através do delegate.
#pragma warning disable CA1416
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Automation;
using Nexus.Boot;
using Nexus.Boot.Inventory;
using Nexus.Boot.Windows;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.DiskTools.Analysis;
using Nexus.DiskTools.Volumes;
using Nexus.DriverUpdates;
using Nexus.GameProfiles.SourceCfg;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Cpu;
using Nexus.Lab.Gpu.Windows;
using Nexus.Lab.Memory;
using Nexus.Lab.Memory.Problems;
using Nexus.Measurement;
using Nexus.Measurement.Windows;
using Nexus.Monitoring;
using Nexus.Monitoring.Windows;
using Nexus.Privacy;
using Nexus.Programs.Inventory;
using Nexus.Programs.Uninstall;
using Nexus.Programs.Updates;
using Nexus.Security;
using Nexus.Security.Scanner;
using Nexus.SystemTools;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;
using Nexus.WindowsActions.Startup;

namespace Nexus.SelfTest;

/// <summary>
/// As verificações do PC real: as MESMAS leituras que as páginas da interface fazem, chamadas direto nas bibliotecas dos
/// módulos. Regras: só leitura (nada de Apply/Restore, Save, quarentena, aplicador); só contagens e sim/não no relatório
/// (nunca nomes de redes, programas ou caminhos do usuário); leitura degradada com motivo conhecido = AVISO, exceção = FALHA.
/// </summary>
public static class NexusChecks
{
    private const string Snapshot = "snapshot";
    private const string CatalogDoc = "catalog";

    public static IReadOnlyList<SelfTestCheck> All() =>
    [
        new("Descoberta", "Leitura do hardware e do Windows", Discover) { WindowsOnly = false, Timeout = TimeSpan.FromSeconds(120) },
        new("Diagnóstico", "Regras de diagnóstico", Diagnose) { WindowsOnly = false },
        new("Catálogo", "Carregar catalog/*.json", LoadCatalog) { WindowsOnly = false },
        new("Catálogo", "Estado atual de cada ação (Detect, simulação)", DetectCatalog) { Timeout = TimeSpan.FromSeconds(180) },
        new("Limpeza", "Medir o que pode ser liberado (varredura)", CleaningScanAsync) { Timeout = TimeSpan.FromSeconds(240) },
        new("Limpeza", "Lixeira", RecycleBin),
        new("Disco", "Unidades", Volumes),
        new("Disco", "Análise de pasta pequena (pasta do NEXUS)", DiskAnalysisAsync) { WindowsOnly = false },
        new("Programas", "Programas instalados", ProgramsInventory),
        new("Programas", "Apps da Microsoft Store", StoreAppsAsync) { Timeout = TimeSpan.FromSeconds(90) },
        new("Programas", "Gerenciadores de pacote e atualizações (winget com tempo limite)", PackageManagersAsync) { Timeout = TimeSpan.FromSeconds(240) },
        new("Inicialização", "Itens que abrem com o Windows", StartupItems) { Timeout = TimeSpan.FromSeconds(120) },
        new("Inicialização", "Eventos de desempenho do boot", BootEvents),
        new("Serviços", "Serviços e editores", ServicesInventory) { Timeout = TimeSpan.FromSeconds(180) },
        new("Privacidade", "Scanner de privacidade", PrivacyScan),
        new("Segurança", "Defender, proteções e heurísticas", SecurityRead) { Timeout = TimeSpan.FromSeconds(120) },
        new("Segurança", "Inspetor de registro (sobras)", RegistryInspect) { Timeout = TimeSpan.FromSeconds(120) },
        new("Verificar arquivos", "Bases de regras e hashes", ScannerDatabases) { WindowsOnly = false },
        new("Verificar arquivos", "Verificação rápida (limitada)", QuickScan) { Timeout = TimeSpan.FromSeconds(180) },
        new("Monitor", "Três amostras dos contadores (PDH)", MonitorSamplesAsync),
        new("Ferramentas do sistema", "DNS e ARP", NetworkCounts),
        new("Ferramentas do sistema", "Perfis de Wi-Fi", WifiProfiles),
        new("Ferramentas do sistema", "Drivers instalados (pnputil)", DriverStore) { Timeout = TimeSpan.FromSeconds(120) },
        new("Ferramentas do sistema", "Apps do Windows (Appx)", AppxPackages) { Timeout = TimeSpan.FromSeconds(120) },
        new("Ferramentas do sistema", "Pontos de restauração", RestorePoints),
        new("Atualizar drivers", "Dispositivos, placas de vídeo e reinício pendente", DriverInventory) { Timeout = TimeSpan.FromSeconds(120) },
        new("Atualizar drivers", "Busca no Windows Update", WindowsUpdateSearch) { Timeout = TimeSpan.FromSeconds(300) },
        new("Laboratório: processador", "Identidade, topologia e energia", CpuIdentity),
        new("Laboratório: placa de vídeo", "DXGI, PCIe, NVML e monitores", GpuReport) { Timeout = TimeSpan.FromSeconds(90) },
        new("Laboratório: memória", "SMBIOS e estado da memória", MemoryRead),
        new("Laboratório", "Medições curtas de CPU e memória", BenchmarksAsync) { Timeout = TimeSpan.FromSeconds(180) },
        new("Memória: diagnóstico", "Uma amostra (processos, pool, compromisso)", MemoryDoctorSample) { Timeout = TimeSpan.FromSeconds(90) },
        new("Medir", "Bibliotecas de jogos", GameLibraries),
        new("Medir", "PresentMon encontrado?", PresentMon) { WindowsOnly = false },
        new("Medir", "PresentMon executa (captura de 5 s do dwm.exe)", PresentMonRunsAsync) { Timeout = TimeSpan.FromSeconds(60) },
        new("Laboratório A/B", "Arquivo do laboratório (teste em andamento, fila, histórico)", AbLabFile) { WindowsOnly = false },
        new("Automático", "Perfil do PC e recomendações", Recommendations) { WindowsOnly = false },
        new("Automático", "Tarefas agendadas do NEXUS", ScheduledTasks),
        new("Ajustes", "settings.json", Settings) { WindowsOnly = false },
    ];

    private static SystemFacts Facts(CheckContext c) =>
        c.Get<SystemSnapshot>(Snapshot)?.Facts ?? throw new InvalidOperationException("a leitura do PC (Descoberta) não terminou; esta verificação depende dela");

    // ------------------------------------------------------------------ descoberta, diagnóstico, catálogo

    private static void Discover(CheckContext c)
    {
        var s = Discovery.Capture();
        c.Shared[Snapshot] = s;
        var facts = s.Sections.SelectMany(x => x.Facts).ToList();
        c.Count("seções", s.Sections.Count);
        c.Count("valores", facts.Count);
        c.Count("indisponíveis", facts.Count(f => f.Status == FactStatus.Unavailable));
        c.Count("detectados", facts.Count(f => f.Status == FactStatus.Detected));
        if (s.Facts.WindowsBuild is { } b) c.Count("build do Windows", b);
        c.Flag("elevado", s.Facts.IsElevated);
        c.Count("GPUs", s.Facts.Gpus.Count);
        if (OperatingSystem.IsWindows() && s.Facts.WindowsBuild is null) c.Warn("build do Windows indisponível");
    }

    private static void Diagnose(CheckContext c)
    {
        var findings = DiagnosticRules.Evaluate(Facts(c));
        c.Count("achados", findings.Count);
        foreach (var g in findings.GroupBy(f => f.Severity)) c.Count(g.Key.ToString(), g.Count());
    }

    private static void LoadCatalog(CheckContext c)
    {
        var doc = CatalogLoader.LoadDirectory(c.Options.CatalogDirectory);
        c.Shared[CatalogDoc] = doc;
        c.Info("versão", doc.CatalogVersion);
        c.Count("ações", doc.Actions.Count);
        c.Count("rejeitadas de propósito", doc.Rejected.Count);
        c.Count("handlers distintos", doc.Actions.Select(a => a.Definition.HandlerKind).Distinct(StringComparer.Ordinal).Count());
        // "Rejeitadas" são as ações recusadas DE PROPÓSITO no catálogo (mitos, riscos sem ganho), com o motivo; não é erro.
    }

    /// <summary>Os mesmos handlers que a interface usa para ler, embrulhados em <see cref="SimulationOnlyHandler"/> (Apply/Restore lançam).</summary>
    [SupportedOSPlatform("windows")]
    internal static HandlerRegistry ReadOnlyHandlers(out List<string> unavailableModules)
    {
        var registry = new WindowsRegistryBackend();
        var env = new HandlerEnvironment(registry, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS"), OtherAccount: false, Elevated: false);
        var all = new List<IActionHandler>
        {
            new RegistryValueHandler(registry), new DirectXUserGpuPreferenceHandler(registry), new StartupApprovedHandler(registry),
            new PowerActiveSchemeHandler(new WindowsPowerBackend()), new PowerSettingValueHandler(new WindowsPowerBackend()),
            new ServiceStartTypeHandler(new WindowsServiceBackend()), new SourceCfgConvarHandler(() => null),
        };
        var kinds = new HashSet<string>(all.Select(h => h.HandlerKind), StringComparer.Ordinal);
        unavailableModules = [];
        (string Name, Func<HandlerEnvironment, IEnumerable<IActionHandler>> Create)[] modules =
        [
            ("Limpeza", Nexus.Cleaning.CleaningHandlers.Create), ("Disco", Nexus.DiskTools.DiskToolsHandlers.Create), ("Programas", Nexus.Programs.ProgramsHandlers.Create),
            ("Inicialização", Nexus.Boot.BootHandlers.Create), ("Privacidade", Nexus.Privacy.PrivacyHandlers.Create), ("Segurança", Nexus.Security.SecurityHandlers.Create),
            ("Verificar arquivos", Nexus.Security.Scanner.ScannerHandlers.Create), ("Monitor", Nexus.Monitoring.MonitoringHandlers.Create),
            ("Ferramentas do sistema", Nexus.SystemTools.SystemToolsHandlers.Create), ("Atualizar drivers", Nexus.DriverUpdates.DriverUpdatesHandlers.Create),
            ("Automático", Nexus.Automation.AutomationHandlers.Create), ("Lab CPU", Nexus.Lab.Cpu.CpuLabHandlers.Create),
            ("Lab GPU", Nexus.Lab.Gpu.GpuLabHandlers.Create), ("Lab memória", Nexus.Lab.Memory.MemoryLabHandlers.Create),
            ("Medir", Nexus.Measurement.MeasureHandlers.Create), ("Jogos (Fortnite)", Nexus.GameProfiles.GameProfilesHandlers.Create),
            ("Rota de jogo", Nexus.NetRoute.NetRouteHandlers.Create),
        ];
        foreach (var (name, create) in modules)
        {
            try
            {
                foreach (var h in create(env))
                    if (kinds.Add(h.HandlerKind)) all.Add(h);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { unavailableModules.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        return SimulationOnlyHandler.Wrap(all);
    }

    private static void DetectCatalog(CheckContext c)
    {
        if (!OperatingSystem.IsWindows()) CheckContext.Skip("não é Windows");
        var doc = c.Get<CatalogDocument>(CatalogDoc) ?? CatalogLoader.LoadDirectory(c.Options.CatalogDirectory);
        var handlers = ReadOnlyHandlers(out var unavailable);
        foreach (var u in unavailable) c.Warn($"handlers do módulo indisponíveis: {u}");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (c.Get<SystemSnapshot>(Snapshot)?.Facts.ActivePowerScheme is { } scheme) parameters["activeScheme"] = scheme.ToString("D");
        int read = 0, notInstantiable = 0, noHandler = 0, managed = 0;
        var errors = new List<string>();
        foreach (var entry in doc.Actions)
        {
            c.Cancel.ThrowIfCancellationRequested();
            if (!handlers.Contains(entry.Definition.HandlerKind)) { noHandler++; continue; }
            var instance = entry.TryInstantiate(parameters, out _);
            if (instance is null) { notInstantiable++; continue; }
            try
            {
                var state = handlers.Get(entry.Definition.HandlerKind).Detect(instance);
                read++;
                if (state.Origin is ValueOrigin.PolicyEnforced or ValueOrigin.ThirdPartyManaged) managed++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"{entry.Definition.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        c.Count("lidas", read);
        c.Count("erros de leitura", errors.Count);
        c.Count("não instanciáveis neste PC", notInstantiable);
        c.Count("sem handler", noHandler);
        c.Count("gerenciadas por política", managed);
        foreach (var e in errors.Take(15)) c.Warn("Detect falhou: " + e);
        if (noHandler > 0) c.Warn($"{noHandler} ação(ões) sem handler registrado (a interface mostraria \"handler pendente\")");
    }

    // ------------------------------------------------------------------ limpeza e disco

    [SupportedOSPlatform("windows")]
    private static async Task CleaningScanAsync(CheckContext c)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var user = UserCleaningRules.Load(string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "NEXUS", "rules"));
        var rules = CleaningRuleSet.BuiltIn.Rules.Concat(user.Rules.Rules).ToList();
        var userData = Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.User))!;
        var adminData = Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.Admin))!;
        var folders = WindowsCleaningFolders.ForCurrentProcess(new WindowsRegistryBackend()) with
        {
            ExtraForbidden = [Path.Combine(userData, "quarantine"), Path.Combine(adminData, "quarantine")],
        };
        var results = await CleaningScanner.ScanAsync(rules, folders, SystemProcessProbe.Instance, DateTime.UtcNow, RecycleBinInfo.TryQuery, cancel: c.Cancel).ConfigureAwait(false);
        c.Count("regras", results.Count);
        c.Count("regras do usuário", user.Rules.Rules.Count);
        c.Count("regras com algo a liberar", results.Count(r => r.Bytes > 0));
        c.Number("GB recuperáveis", results.Sum(r => r.Bytes) / 1024d / 1024 / 1024);
        c.Count("pastas sem acesso", results.Sum(r => r.Inaccessible));
        c.Count("links não seguidos", results.Sum(r => r.ReparseSkipped));
        c.Count("regras com app aberto", results.Count(r => r.RunningApps.Count > 0));
        var problems = results.Where(r => r.Problem is not null).ToList();
        c.Count("regras com problema", problems.Count);
        foreach (var p in problems.Take(5)) c.Warn($"regra {p.Rule.Id}: {p.Problem}");
    }

    private static void RecycleBin(CheckContext c)
    {
        var r = RecycleBinInfo.TryQuery();
        if (r is null) { c.Warn("a Lixeira não pôde ser lida (SHQueryRecycleBin)"); return; }
        c.Count("arquivos", r.Value.Files);
        c.Number("MB", r.Value.Bytes / 1024d / 1024);
    }

    private static void Volumes(CheckContext c)
    {
        var list = VolumeCatalog.List();
        c.Count("unidades", list.Count);
        c.Count("fixas", list.Count(v => v.DriveType == DriveType.Fixed));
        c.Count("com tipo de mídia conhecido", list.Count(v => v.Media != StorageMediaInfo.Unknown));
        if (list.Count == 0) c.Warn("nenhuma unidade listada");
    }

    private static async Task DiskAnalysisAsync(CheckContext c)
    {
        var root = AppContext.BaseDirectory;
        var result = await DiskScanner.ScanAsync(root, new DiskScanOptions(), null, c.Cancel).ConfigureAwait(false);
        c.Count("arquivos", result.Files);
        c.Count("pastas", result.Directories);
        c.Number("MB", result.Root.Size / 1024d / 1024);
        c.Count("categorias", result.Categories.Count);
        c.Count("sem acesso", result.AccessDenied);
        if (result.Files == 0) c.Warn("a análise não encontrou arquivos na pasta do próprio NEXUS");
    }

    // ------------------------------------------------------------------ programas

    private static UninstallEnvironment UninstallEnv() =>
        new(File.Exists, Environment.ExpandEnvironmentVariables, Environment.SystemDirectory, AppContext.BaseDirectory.TrimEnd('\\', '/'));

    [SupportedOSPlatform("windows")]
    private static void ProgramsInventory(CheckContext c)
    {
        var programs = ProgramInventory.Build(new WindowsUninstallRegistrySource().ReadAll());
        var env = UninstallEnv();
        var eligibility = programs.Select(p => BulkUninstallPolicy.Evaluate(p, env)).ToList();
        c.Count("programas", programs.Count);
        c.Count("podem entrar no lote", eligibility.Count(e => e.Kind == BulkEligibilityKind.Allowed));
        if (programs.Count == 0) c.Warn("nenhum programa lido do registro (Uninstall)");
    }

    private static async Task StoreAppsAsync(CheckContext c)
    {
        var apps = await StoreApps.ReadAsync(new CappedRunner(new Nexus.Programs.HiddenProcessRunner(), TimeSpan.FromSeconds(60)), PackageManagers.PowerShellPath, c.Cancel).ConfigureAwait(false);
        c.Count("apps", apps.Count);
        if (apps.Count == 0) c.Warn("nenhum app da Store lido (Get-AppxPackage)");
    }

    private static async Task PackageManagersAsync(CheckContext c)
    {
        // Cada ferramenta tem no máximo 90 s (winget pode ficar parado esperando a primeira atualização das fontes).
        var runner = new CappedRunner(new Nexus.Programs.HiddenProcessRunner(), TimeSpan.FromSeconds(90));
        var statuses = await Task.WhenAll(PackageManagers.All.Select(k => PackageManagers.DetectAsync(k, runner, c.Cancel))).ConfigureAwait(false);
        foreach (var s in statuses)
        {
            c.Flag(s.Kind.ToString(), s.Installed);
            if (s.Problem is not null) c.Warn($"{s.Kind}: {s.Problem}");
        }
        var installed = statuses.Where(s => s.Installed).ToList();
        var lists = await Task.WhenAll(installed.Select(s => PackageManagers.ListUpdatesAsync(s, runner, c.Cancel))).ConfigureAwait(false);
        foreach (var l in lists)
        {
            c.Count($"{l.Kind}: atualizações", l.Updates.Count);
            if (l.Problem is not null) c.Warn($"{l.Kind}: {l.Problem}");
        }
        if (!statuses.First(s => s.Kind == PackageManagerKind.Winget).Installed) c.Warn("winget não encontrado (Instalador de Aplicativos ausente?)");
    }

    // ------------------------------------------------------------------ inicialização e serviços

    [SupportedOSPlatform("windows")]
    private static void StartupItems(CheckContext c)
    {
        var inventory = new StartupInventory(new WindowsBootRegistryReader(), new WindowsFileInspector(), Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .Read(WindowsStartupFolders.Current(), new WindowsTaskScheduler(), new WindowsServiceReader(), null);
        var items = inventory.Items;
        c.Count("itens", items.Count);
        c.Count("de terceiros", items.Count(i => !i.IsMicrosoft));
        c.Count("ligados (terceiros)", items.Count(i => !i.IsMicrosoft && i.Enabled));
        c.Count("órfãos", items.Count(i => i.IsOrphan));
        c.Count("notas", inventory.Notes.Count);
        foreach (var n in inventory.Notes.Take(3)) c.Warn(n);
    }

    [SupportedOSPlatform("windows")]
    private static void BootEvents(CheckContext c)
    {
        var r = WindowsBootEventReader.Read();
        c.Count("boots registrados", r.Boots.Count);
        c.Count("componentes lentos", r.Slow.Count);
        if (r.Last is { } last) c.Number("último boot (s)", last.TotalMs / 1000d);
        if (r.Unavailable is not null) c.Warn("eventos de boot indisponíveis: " + r.Unavailable);
    }

    [SupportedOSPlatform("windows")]
    private static void ServicesInventory(CheckContext c)
    {
        var services = new WindowsServiceReader().All();
        var files = new WindowsFileInspector();
        var inventory = new StartupInventory(new WindowsBootRegistryReader(), files, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        int microsoft = 0, unknown = 0, missing = 0;
        foreach (var s in services)
        {
            c.Cancel.ThrowIfCancellationRequested();
            var binary = inventory.ServiceBinary(s);
            if (binary is null || !files.Exists(binary)) { missing++; continue; }
            var publisher = files.Publisher(binary);
            if (StartupInventory.IsMicrosoftPublisher(publisher)) microsoft++;
            else if (publisher == FilePublisher.Unknown) unknown++;
        }
        c.Count("serviços", services.Count);
        c.Count("da Microsoft", microsoft);
        c.Count("editor desconhecido", unknown);
        c.Count("binário não localizado", missing);
        var presets = ServicePresets.Load(c.Options.CatalogDirectory);
        c.Count("predefinições", presets.Count);
        if (services.Count == 0) c.Warn("nenhum serviço lido");
    }

    // ------------------------------------------------------------------ privacidade e segurança

    [SupportedOSPlatform("windows")]
    private static void PrivacyScan(CheckContext c)
    {
        var catalog = PrivacyCatalog.LoadFromDirectory(c.Options.CatalogDirectory);
        var build = c.Get<SystemSnapshot>(Snapshot)?.Facts.WindowsBuild ?? Environment.OSVersion.Version.Build;
        var scanner = new PrivacyScanner(new WindowsRegistryBackend());
        var states = scanner.Scan(catalog, build);
        var consent = catalog.Info.Count(i => i.ConsentStore is not null && scanner.ReadConsent(i.ConsentStore) is not null);
        c.Count("itens", states.Count);
        foreach (var g in states.GroupBy(s => s.Status)) c.Count(g.Key.ToString(), g.Count());
        c.Count("permissões de app lidas", consent);
    }

    private static void SecurityRead(CheckContext c)
    {
        var r = WindowsSecurityReader.Read(c.Cancel);
        c.Count("proteções", r.Protection.Count);
        c.Count("achados (heurísticas)", r.Findings.Count);
        c.Count("ameaças no histórico", r.Threats.Count);
        c.Count("notas", r.Notes.Count);
        foreach (var n in r.Notes.Take(5)) c.Warn(n);
        if (r.Protection.Count == 0) c.Warn("nenhuma proteção lida (Defender/Central de Segurança)");
    }

    [SupportedOSPlatform("windows")]
    private static void RegistryInspect(CheckContext c)
    {
        var reg = new WindowsSecurityRegistry();
        var files = new SystemFileProbe();
        var machine = RegistryInspector.Scan(reg, files, RegScope.Machine);
        var user = RegistryInspector.Scan(reg, files, RegScope.User);
        c.Count("sobras (máquina)", machine.Count);
        c.Count("sobras (usuário)", user.Count);
        c.Count("removíveis com cópia", machine.Concat(user).Count(o => o.Removable));
    }

    // ------------------------------------------------------------------ verificar arquivos

    private static void ScannerDatabases(CheckContext c)
    {
        var hashes = HashDatabase.LoadBuiltIn();
        var rules = RuleSet.LoadBuiltIn();
        c.Count("regras de padrão", rules.Rules.Count);
        c.Count("hashes importados", hashes.RealCount);
        c.Count("exceções do usuário", IgnoreList.Load(IgnoreList.DefaultPath).Count);
        if (rules.Rules.Count == 0) c.Warn("nenhuma regra embutida carregada");
    }

    private static void QuickScan(CheckContext c)
    {
        var max = c.Options.ScannerMaxFiles;
        var engine = new ScanEngine(HashDatabase.LoadBuiltIn(), RuleSet.LoadBuiltIn(), new AuthenticodeSignatureChecker(),
            new ScanOptions { ExcludedDirectories = ScanTargets.DefaultExclusions(), ScanSmallFiles = false }, IgnoreList.Load(IgnoreList.DefaultPath));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(c.Cancel);
        var progress = new LimitProgress(max, limit);
        var summary = engine.Scan(ScanTargets.Quick(), progress, null, limit.Token);
        c.Cancel.ThrowIfCancellationRequested();
        c.Count("pastas da verificação rápida", ScanTargets.Quick().Count);
        c.Count("arquivos lidos", summary.Files);
        c.Count("limite", max);
        c.Count("pulados", summary.Skipped);
        c.Count("erros de leitura", summary.Errors);
        c.Count("para revisar", summary.Findings.Count);
        if (summary.Findings.Count > 0) c.Warn($"{summary.Findings.Count} arquivo(s) com sinais para revisar (abra Verificar arquivos para ver quais)");
    }

    /// <summary>Progresso síncrono (não usa contexto de sincronização) que cancela ao atingir o limite de arquivos.</summary>
    private sealed class LimitProgress(int max, CancellationTokenSource cancel) : IProgress<Nexus.Security.Scanner.ScanProgress>
    {
        public void Report(Nexus.Security.Scanner.ScanProgress value)
        {
            if (value.Files >= max && !cancel.IsCancellationRequested) cancel.Cancel();
        }
    }

    // ------------------------------------------------------------------ monitor

    [SupportedOSPlatform("windows")]
    private static async Task MonitorSamplesAsync(CheckContext c)
    {
        using var session = new MonitorSession(new WindowsPerformanceSource(), new WindowsProcessSource(), new WindowsNvidiaSource());
        MonitorTick? last = null;
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) await Task.Delay(1000, c.Cancel).ConfigureAwait(false);
            last = session.Tick();
        }
        c.Count("amostras", 3);
        c.Count("contadores indisponíveis", session.Unavailable.Count);
        c.Number("CPU %", last!.Snapshot.Cpu.TotalPercent);
        c.Flag("memória lida", last.Snapshot.Memory is not null);
        c.Count("discos", last.Snapshot.Disks.Count);
        c.Count("GPUs (PDH)", last.Snapshot.GpuAdapters.Count);
        c.Count("processos no topo", last.Snapshot.Processes.Count);
        c.Flag("NVIDIA (nvidia-smi)", session.NvidiaAvailable);
        foreach (var u in session.Unavailable.Take(5)) c.Warn("contador indisponível: " + u);
    }

    // ------------------------------------------------------------------ ferramentas do sistema

    [SupportedOSPlatform("windows")]
    private static void NetworkCounts(CheckContext c)
    {
        var n = SystemToolsReader.Network();
        if (n.DnsEntries is { } dns) c.Count("entradas DNS", dns);
        else c.Warn("contar o cache DNS precisa de administrador neste Windows (limpar não precisa)");
        if (n.ArpEntries is { } arp) c.Count("entradas ARP", arp);
        else c.Warn("tabela ARP não lida");
    }

    [SupportedOSPlatform("windows")]
    private static void WifiProfiles(CheckContext c)
    {
        var w = SystemToolsReader.Wifi();
        c.Count("perfis", w.Profiles.Count);
        c.Count("com última conexão", w.LastConnected.Count);
        if (w.Note is not null) c.Warn(w.Note);
    }

    [SupportedOSPlatform("windows")]
    private static void DriverStore(CheckContext c)
    {
        var d = SystemToolsReader.Drivers();
        c.Count("pacotes de driver", d.Packages.Count);
        c.Flag("uso por dispositivo lido", d.InUse is not null);
        if (d.Error is not null) c.Warn("pnputil: " + d.Error);
        if (d.InUse is null && d.Error is null) c.Warn("não foi possível ler quais drivers estão em uso");
    }

    [SupportedOSPlatform("windows")]
    private static void AppxPackages(CheckContext c)
    {
        var a = SystemToolsReader.Appx();
        c.Count("apps do usuário", a.Packages.Count);
        if (a.Provisioned is { } p) c.Count("provisionados", p.Count);
        else c.Warn("lista de apps provisionados não lida (pode precisar de administrador)");
        if (a.Error is not null) c.Warn("Appx: " + a.Error);
    }

    [SupportedOSPlatform("windows")]
    private static void RestorePoints(CheckContext c)
    {
        var r = SystemToolsReader.RestorePoints();
        c.Count("pontos", r.Points.Items?.Count ?? 0);
        if (r.Points.Error is not null) c.Warn("pontos de restauração: " + r.Points.Error);
        if (r.ProtectedDrives is { } d) c.Count("unidades protegidas", d.Count);
        else c.Warn("unidades com Proteção do Sistema não lidas");
        c.Flag("desligado por política", r.DisabledByPolicy);
    }

    // ------------------------------------------------------------------ atualizar drivers

    [SupportedOSPlatform("windows")]
    private static void DriverInventory(CheckContext c)
    {
        var r = DriverUpdatesReader.Scan(TimeSpan.Zero, c.Cancel, searchUpdates: false);
        c.Count("placas de vídeo", r.Gpus.Count);
        c.Flag("reinício pendente", r.RebootPending);
        // "Ver se falta algum driver": a mesma leitura do cartão "Dispositivos sem driver" (código de problema de cada dispositivo).
        c.Count("dispositivos", r.DeviceCount);
        c.Flag("código de problema lido", r.ProblemCodesRead);
        c.Count("dispositivos sem driver", r.Problems.Count(p => p.Kind == DeviceProblemKind.MissingDriver));
        c.Count("dispositivos com driver que não carrega", r.Problems.Count(p => p.Kind == DeviceProblemKind.DriverFailed));
        c.Count("dispositivos desativados", r.Problems.Count(p => p.Kind == DeviceProblemKind.Disabled));
        if (r.DeviceCount == 0) c.Warn("nenhum dispositivo lido pela SetupAPI");
        if (r.InventoryNote is not null) c.Warn(r.InventoryNote);
        if (r.GpuNote is not null) c.Warn(r.GpuNote);
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsUpdateSearch(CheckContext c)
    {
        if (!c.Options.WithWindowsUpdate) CheckContext.Skip("use --with-windows-update (a busca pode levar minutos)");
        var r = DriverUpdatesReader.Scan(TimeSpan.FromMinutes(4), c.Cancel);
        c.Count("ofertas de driver", r.Offers.Count);
        c.Count("bloqueadas (instalar pelo Windows Update)", r.Offers.Count(o => o.Blocked is not null));
        if (r.Error != WuErrorKind.None) c.Warn($"Windows Update: {r.Error}: {r.ErrorMessage}");
    }

    // ------------------------------------------------------------------ laboratórios

    [SupportedOSPlatform("windows")]
    private static void CpuIdentity(CheckContext c)
    {
        var id = CpuIdDecoder.Decode(new HardwareCpuIdSource());
        c.Flag("CPUID lido", id.Available);
        if (!id.Available) c.Warn("CPUID indisponível: " + id.UnavailableReason);
        c.Info("fabricante", id.Vendor.ToString());
        c.Info("modelo", id.Brand);
        c.Count("família", id.Family);
        var topo = TopologyReader.Read();
        c.Count("núcleos", topo.CoreCount);
        c.Count("threads", topo.LogicalCount);
        c.Count("domínios de cache", topo.Caches.Count);
        c.Flag("híbrido", topo.Hybrid);
        var power = CpuPowerReader.Create().Read();
        c.Flag("na bateria", power.OnBattery);
        c.Flag("plano de energia lido", power.ActiveScheme is not null);
        if (topo.CoreCount == 0) c.Warn("topologia vazia");
    }

    [SupportedOSPlatform("windows")]
    private static void GpuReport(CheckContext c)
    {
        string status;
        NvmlSession? nvml = null;
        try { nvml = NvmlSession.TryOpen(out status); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status = $"NVML indisponível: {ex.Message}"; }
        using (nvml)
        {
            var r = GpuLabScanner.Scan(nvml, status);
            var hardware = r.Hardware.ToList();
            c.Count("GPUs de hardware", hardware.Count);
            c.Info("principal", r.Primary?.Name);
            c.Count("com PCIe lido", hardware.Count(a => a.Pcie.CurrentGen is not null || a.Pcie.CurrentWidth is not null));
            c.Count("com driver lido", hardware.Count(a => a.DriverVersion is not null));
            c.Flag("NVML aberto", nvml is not null);
            c.Count("monitores", r.Monitors.Count);
            c.Count("jogos encontrados", r.Games.Count);
            c.Count("preferências por app", r.AppPreferences.Count);
            c.Count("notas", r.Notes.Count);
            if (hardware.Count == 0) c.Warn("nenhuma GPU de hardware pelo DXGI");
            if (hardware.Any(a => a.Vendor == Nexus.Lab.Gpu.GpuVendor.Nvidia) && nvml is null) c.Warn("há GPU NVIDIA mas a NVML não abriu: " + status);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void MemoryRead(CheckContext c)
    {
        var gpus = c.Get<SystemSnapshot>(Snapshot)?.Facts.Gpus ?? [];
        var analysis = MemoryLabAnalyzer.Analyze(WindowsMemoryReader.ReadSmbios(), gpus);
        c.Count("módulos", analysis.Inventory.Populated.Count);
        c.Count("canais", analysis.Layout.ChannelCount);
        if (analysis.ConfiguredMts is { } mts) c.Count("MT/s configurado", mts);
        var sysMain = c.Get<SystemSnapshot>(Snapshot)?.Facts.Services.FirstOrDefault(s => string.Equals(s.Name, "SysMain", StringComparison.OrdinalIgnoreCase));
        var state = WindowsMemoryReader.ReadState(sysMain?.State, sysMain?.StartType, c.Cancel);
        c.Flag("total lido", state.TotalPhysicalBytes is not null);
        if (state.TotalPhysicalBytes is { } t) c.Number("GB instalados (Windows)", t / 1024d / 1024 / 1024);
        c.Count("leituras parciais", state.Problems.Count);
        foreach (var p in state.Problems.Take(5)) c.Warn($"{p.Key}: {p.Value}");
        if (analysis.Inventory.Populated.Count == 0) c.Warn("SMBIOS sem módulos de memória");
    }

    [SupportedOSPlatform("windows")]
    private static async Task BenchmarksAsync(CheckContext c)
    {
        if (!c.Options.WithBenchmarks) CheckContext.Skip("use --with-benchmarks (o PC trabalha por alguns segundos)");
        var topology = TopologyReader.Read();
        var options = CpuLabOptions.Quick with { Samples = 2, AllCoreDuration = TimeSpan.FromSeconds(1), CoreToCoreRounds = 2_000, CoreToCoreMaxCores = 4 };
        var runner = new CpuLabRunner(ThreadPinners.ForThisOs(), topology, options);
        var run = await runner.RunQuickAsync(new CpuLabRun(), null, c.Cancel).ConfigureAwait(false);
        c.Number("CPU: duração (s)", run.DurationSeconds);
        var mem = new BenchmarkOptions { BufferBytes = 64L * 1024 * 1024, Passes = 2, LatencyBytes = 128L * 1024 * 1024, LatencyDuration = TimeSpan.FromSeconds(0.3), TryLargePages = false };
        var bw = MemoryBenchmark.MeasureBandwidth(mem, null, c.Cancel);
        var lat = MemoryBenchmark.MeasureLatency(mem, null, null, c.Cancel);
        c.Number("memória: leitura GB/s", bw.ReadGbps);
        c.Number("memória: latência ns", lat.RandomNs);
    }

    [SupportedOSPlatform("windows")]
    private static void MemoryDoctorSample(CheckContext c)
    {
        var snap = WindowsDoctorReader.Read();
        c.Count("processos", snap.Processes.Count);
        c.Count("serviços por PID", snap.ServicesByPid.Count);
        c.Count("leituras parciais", snap.Problems.Count);
        foreach (var p in snap.Problems.Take(5)) c.Warn($"{p.Key}: {p.Value}");
    }

    // ------------------------------------------------------------------ medir

    [SupportedOSPlatform("windows")]
    private static void GameLibraries(CheckContext c)
    {
        var notes = new List<string>();
        var games = GameLibrary.Scan(WindowsGameRoots.Read(), notes);
        c.Count("jogos", games.Count);
        c.Count("lojas com jogos", games.Select(g => g.Store).Distinct().Count());
        foreach (var n in notes.Take(3)) c.Warn(n);
    }

    /// <summary>Laboratório A/B: lê %LocalAppData%\NEXUS\ab-lab.json como a página faz (arquivo ilegível vira vazio + .bad). Só leitura.</summary>
    private static void AbLabFile(CheckContext c)
    {
        var store = new Nexus.Measurement.AbLab.AbLabStore(Nexus.Measurement.AbLab.AbLabStore.DefaultPath);
        c.Flag("existe", File.Exists(store.FilePath));
        var data = store.Load();
        c.Flag("teste em andamento", data.Active is { Step: not Nexus.Measurement.AbLab.AbStep.Done });
        c.Count("na fila", data.Queue.Count);
        c.Count("no histórico", data.History.Count);
        if (File.Exists(store.FilePath + ".bad")) c.Warn("o arquivo anterior estava ilegível e foi guardado como .bad");
    }

    /// <summary>
    /// Embutido primeiro (tools\PresentMon\PresentMon.exe), depois o caminho e as pastas conhecidas; o escolhido precisa passar na
    /// conferência (SHA-256 conhecido ou assinatura Authenticode da Intel). Só leitura: hash e assinatura do arquivo.
    /// </summary>
    private static void PresentMon(CheckContext c)
    {
        var found = PresentMonLocator.Locate(null, AppContext.BaseDirectory, PresentMonLocator.SystemProbe());
        c.Flag("encontrado", found is not null);
        if (found is null)
        {
            c.Warn($"PresentMon não encontrado: a cópia embutida ({PresentMonTrust.BundledPath(AppContext.BaseDirectory)}) falta nesta pasta e nenhum outro foi achado");
            return;
        }
        c.Info("origem", found.Source switch { PresentMonSource.Bundled => "embutido", PresentMonSource.Configured => "configurado", _ => "achado" });
        c.Flag("conferido", found.Usable);
        c.Info("assinante", found.Verdict.Signer);
        c.Info("versão", PresentMonCli.GenerationOf(found.Path).ToString());
        if (!found.Usable) c.Warn(found.Verdict.Reason);
        else if (found.Source != PresentMonSource.Bundled) c.Warn($"a cópia embutida falta; usando {found.Path}");
    }

    /// <summary>
    /// Prova de que o PresentMon executa neste PC (captura de 5 s do dwm.exe, sem pedir elevação). Fica atrás de
    /// --with-benchmarks porque inicia um processo e grava um CSV temporário (apagado em seguida) — o resto do autoteste não
    /// inicia processos nem grava arquivos. Sem permissão de rastreamento (nem administrador nem o grupo "Usuários do log de
    /// desempenho") a verificação é ignorada: o autoteste nunca pede o aviso do Windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task PresentMonRunsAsync(CheckContext c)
    {
        if (!c.Options.WithBenchmarks) CheckContext.Skip("use --with-benchmarks (inicia o PresentMon por 5 s e grava um CSV temporário, apagado em seguida)");
        if (!WindowsProcessProbe.CanTraceWithoutElevation())
            CheckContext.Skip("sem permissão de rastreamento (administrador ou grupo \"Usuários do log de desempenho\"); o autoteste não pede o aviso do Windows");
        var probe = PresentMonLocator.SystemProbe();
        var found = PresentMonLocator.Locate(null, AppContext.BaseDirectory, probe);
        if (found is not { Usable: true }) CheckContext.Skip(found?.Verdict.Reason ?? "PresentMon não encontrado");
        var r = await PresentMonSmokeTest.RunAsync(found!.Path, probe, "dwm.exe", 5, c.Cancel).ConfigureAwait(false);
        c.Flag("executou", r.Ran);
        c.Flag("cabeçalho gravado", r.HeaderWritten);
        c.Count("quadros do dwm.exe", r.Frames);
        if (!r.Ran) throw new InvalidOperationException(r.Message + (r.Log.Length > 0 ? " · " + r.Log.Trim() : ""));
        if (r.Frames == 0) c.Warn(r.Message);
    }

    // ------------------------------------------------------------------ automático e ajustes

    private static void Recommendations(CheckContext c)
    {
        var facts = Facts(c);
        var traits = PcTraitsProbe.Read(facts.SystemDrive);
        var profile = PcProfiler.Classify(facts, traits, DiagnosticRules.Evaluate(facts));
        var catalog = c.Get<CatalogDocument>(CatalogDoc) ?? CatalogLoader.LoadDirectory(c.Options.CatalogDirectory);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (facts.ActivePowerScheme is { } s) parameters["activeScheme"] = s.ToString("D");
        var set = RecommendationEngine.Default().Evaluate(profile, catalog, RiskModel.ReadDeclared(c.Options.CatalogDirectory), parameters);
        c.Info("perfil", profile.Headline);
        foreach (var g in set.Items.GroupBy(i => i.Kind)) c.Count(g.Key.ToString(), g.Count());
        c.Count("candidatas do \"Otimizar agora\"", OneClickPlanner.Candidates(set, catalog).Count);
    }

    [SupportedOSPlatform("windows")]
    private static void ScheduledTasks(CheckContext c)
    {
        foreach (var kind in new[] { NexusTaskKind.Scan, NexusTaskKind.Clean })
        {
            var s = AutomationHandlers.ReadTaskStatus(kind, AutomationScheduledScanHandler.DefaultExecutable);
            c.Info(kind == NexusTaskKind.Scan ? "verificação" : "limpeza segura", s.State);
            if (s.Error is not null) c.Warn($"Agendador ({kind}): {s.Error}");
        }
        var legacy = AutomationHandlers.ReadScheduledScanState(AutomationScheduledScanHandler.DefaultExecutable);
        c.Flag("verificação semanal antiga ligada", legacy.State != ScanSchedule.Off);
    }

    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>
    /// Lê settings.json SEM <see cref="NexusSettings.Load(string)"/>: com arquivo corrompido, o Load guarda uma cópia (escreve).
    /// Aqui a validação é a mesma (tamanho, JSON, formato), só que sem tocar no arquivo.
    /// </summary>
    internal static void Settings(CheckContext c) => CheckSettingsFile(c, NexusSettings.DefaultPath);

    internal static void CheckSettingsFile(CheckContext c, string path)
    {
        if (!File.Exists(path)) { c.Flag("existe", false); c.Info("em uso", "padrões"); return; }
        c.Flag("existe", true);
        var info = new FileInfo(path);
        c.Count("bytes", info.Length);
        if (info.Length > 64 * 1024) { c.Warn("settings.json grande demais: o NEXUS usaria os padrões"); return; }
        NexusSettings? s;
        try { s = JsonSerializer.Deserialize<NexusSettings>(File.ReadAllText(path), SettingsJson); }
        catch (JsonException ex) { c.Warn("settings.json corrompido (o NEXUS guardaria uma cópia e usaria os padrões): " + ex.Message); return; }
        if (s is null) { c.Warn("settings.json vazio: o NEXUS usaria os padrões"); return; }
        if (s.Format is < 1 or > NexusSettings.CurrentFormat) { c.Warn($"settings.json de outro formato ({s.Format}): o NEXUS usaria os padrões"); return; }
        c.Count("formato", s.Format);
        c.Flag("notificações", s.Notifications);
        c.Flag("ícone na bandeja", s.TrayIcon);
        c.Flag("fechar para a bandeja", s.CloseToTray);
    }
}

/// <summary>Limita o tempo de cada ferramenta de leitura (winget, PowerShell) mesmo que o módulo peça mais.</summary>
internal sealed class CappedRunner(Nexus.Programs.IProcessRunner inner, TimeSpan cap) : Nexus.Programs.IProcessRunner
{
    public Task<Nexus.Programs.ProcessOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        inner.RunAsync(fileName, arguments, timeout < cap ? timeout : cap, cancellationToken);
}
