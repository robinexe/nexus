using System.Runtime.Versioning;
using Nexus.Automation;
using Nexus.Boot;
using Nexus.Boot.Inventory;
using Nexus.Boot.Windows;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.DriverUpdates;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Cpu;
using Nexus.Lab.Gpu;
using Nexus.Lab.Gpu.Windows;
using Nexus.Lab.Memory;
using Nexus.Lab.Memory.Problems;
using Nexus.Privacy;
using Nexus.Programs;
using Nexus.Programs.Inventory;
using Nexus.Programs.Updates;
using Nexus.Security;
using Nexus.WindowsActions.Registry;
using BootStartupItem = Nexus.Boot.Inventory.StartupItem;

namespace Nexus.Assistant;

/// <summary>
/// Leituras do PC para cada etapa, SEMPRE pelas bibliotecas dos módulos e só leitura (nada aqui escreve no sistema).
/// Cada leitura que falha vira "indisponível" com o motivo; o assistente segue. Chame fora da thread da interface.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AssistantReaders
{
    private static bool Expected(Exception ex) => ex is not (OutOfMemoryException or OperationCanceledException);

    public static string UserDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.User))!;
    public static string AdminDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.Admin))!;

    // ------------------------------------------------------------------ 1

    public static StepScan Welcome(AssistantCatalog catalog, string? systemDrive, int pendingRecovery)
    {
        try
        {
            var traits = PcTraitsProbe.Read(systemDrive);
            long? free = null, total = null;
            try
            {
                var root = Path.GetPathRoot(string.IsNullOrEmpty(systemDrive) ? Environment.SystemDirectory : systemDrive.TrimEnd('\\') + "\\");
                if (root is not null)
                {
                    var d = new DriveInfo(root);
                    free = d.AvailableFreeSpace;
                    total = d.TotalSize;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            return StepPlanners.Welcome(catalog, traits, free, total, pendingRecovery);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Checagens iniciais", ex.Message); }
    }

    // ------------------------------------------------------------------ 2

    public sealed record PcReading(StepScan Scan, PcSummary Summary, PcProfile? Profile);

    public static PcReading MyPc(AssistantCatalog catalog, SystemFacts facts, StepPlanners.PcMeasurements? measured)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var summary = Summary(facts, null);
        try
        {
            var traits = PcTraitsProbe.Read(facts.SystemDrive);
            var profile = PcProfiler.Classify(facts, traits, DiagnosticRules.Evaluate(facts));
            summary = Summary(facts, profile, traits);
            MemoryAnalysis? memory = null;
            IReadOnlyList<MemoryFinding> memFindings = [];
            try
            {
                memory = MemoryLabAnalyzer.Analyze(WindowsMemoryReader.ReadSmbios(), facts.Gpus);
                var sysMain = facts.Services.FirstOrDefault(s => string.Equals(s.Name, "SysMain", StringComparison.OrdinalIgnoreCase));
                var state = WindowsMemoryReader.ReadState(sysMain?.State, sysMain?.StartType);
                memFindings = MemoryDiagnosis.Evaluate(new MemoryLabFacts(memory.Inventory, memory.Layout, memory.Parts, memory.Machine, state));
            }
            catch (Exception ex) when (Expected(ex)) { }
            IReadOnlyList<GpuFinding> gpuFindings = [];
            try { gpuFindings = GpuDiagnostics.Evaluate(GpuLabScanner.Scan(null, "NVML não usado pelo assistente."), DateOnly.FromDateTime(DateTime.Today)); }
            catch (Exception ex) when (Expected(ex)) { }
            return new PcReading(StepPlanners.MyPc(catalog, facts, profile, memory, memFindings, gpuFindings, measured), summary, profile);
        }
        catch (Exception ex) when (Expected(ex)) { return new PcReading(StepScan.Unavailable("Laboratórios", ex.Message), summary, null); }
    }

    /// <summary>Perfil do relatório lido na hora (quando a etapa 2 foi pulada ou o assistente foi retomado).</summary>
    public static PcSummary SummaryFor(SystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        try
        {
            var traits = PcTraitsProbe.Read(facts.SystemDrive);
            return Summary(facts, PcProfiler.Classify(facts, traits, DiagnosticRules.Evaluate(facts)), traits);
        }
        catch (Exception ex) when (Expected(ex)) { return Summary(facts, null); }
    }

    /// <summary>Perfil do relatório: hardware e Windows, sem nome do computador, usuário ou números de série.</summary>
    public static PcSummary Summary(SystemFacts facts, PcProfile? profile, PcTraits? traits = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var gb = facts.TotalMemoryBytes is { } b ? $"{Math.Round(b / (1024.0 * 1024 * 1024)):0} GB" : null;
        var modules = facts.MemoryModules.Count;
        return new PcSummary(
            profile?.Headline ?? (facts.IsVirtualMachine == true ? "Máquina virtual" : "PC"),
            facts.CpuModel?.Trim() + (facts.PhysicalCores is { } c && facts.LogicalProcessors is { } t ? $" ({c} núcleos, {t} threads)" : ""),
            string.Join(" + ", facts.Gpus.Where(g => !PcProfiler.IsSoftwareAdapter(g.Name)).Select(g => g.Name.Trim()).Distinct()) is { Length: > 0 } gpus ? gpus : null,
            gb is null ? null : gb + (modules > 0 ? $" em {modules} módulo(s)" : ""),
            facts.WindowsBuild is { } build ? $"{(build >= 22000 ? "Windows 11" : "Windows 10")} (build {build})" : facts.WindowsEdition,
            traits?.SystemDriveBus is { } bus ? $"{bus}{(traits.SystemDriveIsSsd == true ? " (SSD)" : traits.SystemDriveIsSsd == false ? " (HDD)" : "")}" : null);
    }

    /// <summary>Medições curtas (opcionais): frequência de 1 núcleo (~2 s) e banda de leitura da RAM (~10 s, blocos que cabem na memória livre).</summary>
    public static StepPlanners.PcMeasurements Measure(CancellationToken cancel)
    {
        double? ghz = null, read = null, copy = null;
        try { ghz = FrequencyProbe.MeasureCurrentThread(TimeSpan.FromMilliseconds(400), 6, 250, cancel); }
        catch (Exception ex) when (Expected(ex)) { }
        try
        {
            var avail = WindowsMemoryReader.AvailablePhysical() ?? 2UL * 1024 * 1024 * 1024;
            var options = BenchmarkOptions.ForAvailableMemory(avail, Environment.ProcessorCount) with { Passes = 3 };
            var bw = MemoryBenchmark.MeasureBandwidth(options, null, cancel);
            read = bw.ReadGbps;
            copy = bw.CopyGbps;
        }
        catch (Exception ex) when (Expected(ex)) { }
        return new StepPlanners.PcMeasurements(ghz, read, copy);
    }

    // ------------------------------------------------------------------ 3

    public static async Task<(StepScan Scan, string Batch)> CleaningAsync(AssistantCatalog catalog, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var batch = CleaningBatchId.New(DateTime.UtcNow);
        try
        {
            var (safe, _) = SafeCleaningSelector.Select(catalog.Document, CleaningRuleSet.BuiltIn, catalog.DeclaredRisk);
            var folders = SafeCleaningSelector.CurrentUserFolders(UserDataDirectory, AdminDataDirectory);
            var scanned = await CleaningScanner.ScanAsync(safe.Select(s => s.Rule), folders, SystemProcessProbe.Instance, DateTime.UtcNow, cancel: cancel).ConfigureAwait(false);
            long inQuarantine = 0;
            try { inQuarantine = CleaningOverview.Read(UserDataDirectory, AdminDataDirectory).BytesInQuarantine; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return (StepPlanners.Cleaning(catalog, safe, scanned, batch, inQuarantine), batch);
        }
        catch (Exception ex) when (Expected(ex)) { return (StepScan.Unavailable("Limpeza segura", ex.Message), batch); }
    }

    // ------------------------------------------------------------------ 4

    public static async Task<StepScan> ProgramsAsync(bool checkUpdates, CancellationToken cancel)
    {
        try
        {
            var programs = ProgramInventory.Build(new WindowsUninstallRegistrySource().ReadAll());
            var candidates = StepPlanners.ProgramCandidates(programs, DateOnly.FromDateTime(DateTime.Today));
            int? updates = null;
            var managers = new List<string>();
            if (checkUpdates)
            {
                var runner = new HiddenProcessRunner();
                var statuses = await Task.WhenAll(PackageManagers.All.Select(k => PackageManagers.DetectAsync(k, runner, cancel))).ConfigureAwait(false);
                var installed = statuses.Where(s => s.Installed).ToList();
                managers.AddRange(installed.Select(s => PackageManagerInfo.DisplayName(s.Kind)));
                var lists = await Task.WhenAll(installed.Select(s => PackageManagers.ListUpdatesAsync(s, runner, cancel))).ConfigureAwait(false);
                updates = lists.Sum(l => l.Updates.Count);
            }
            return StepPlanners.Programs(programs, candidates, updates, managers);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Programas", ex.Message); }
    }

    // ------------------------------------------------------------------ 5

    public static StepScan Startup(AssistantCatalog catalog, Func<BootStartupItem, ActionRequest?> toggle, string catalogDirectory)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        try
        {
            var boot = WindowsBootEventReader.Read();
            var services = new WindowsServiceReader();
            var inventory = new StartupInventory(new WindowsBootRegistryReader(), new WindowsFileInspector(), Environment.GetEnvironmentVariable,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                .Read(WindowsStartupFolders.Current(), new WindowsTaskScheduler(), services, boot);
            IReadOnlyList<ServicePreset> presets;
            try { presets = ServicePresets.Load(catalogDirectory); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { presets = []; }
            var startTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in services.All()) startTypes[s.Name] = s.StartType;
            return StepPlanners.Startup(catalog, inventory.Items, toggle, presets, startTypes, boot.Last?.TotalMs);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Inicialização", ex.Message); }
    }

    // ------------------------------------------------------------------ 6

    public static StepScan Privacy(AssistantCatalog catalog, string catalogDirectory, int windowsBuild)
    {
        try
        {
            var c = PrivacyCatalog.LoadFromDirectory(catalogDirectory);
            var states = new PrivacyScanner(new WindowsRegistryBackend()).Scan(c, windowsBuild);
            return StepPlanners.Privacy(catalog, states);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Privacidade", ex.Message); }
    }

    // ------------------------------------------------------------------ 7

    public static StepScan Security()
    {
        try { return StepPlanners.Security(WindowsSecurityReader.Read()); }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Segurança", ex.Message); }
    }

    // ------------------------------------------------------------------ 8

    public static StepScan Drivers(AssistantCatalog catalog, bool searchWindowsUpdate, CancellationToken cancel)
    {
        try
        {
            var r = DriverUpdatesReader.Scan(searchWindowsUpdate ? DriverUpdatesReader.DefaultTimeout : TimeSpan.Zero, cancel, searchUpdates: searchWindowsUpdate);
            return StepPlanners.Drivers(catalog, r, searchWindowsUpdate);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Drivers", ex.Message); }
    }

    // ------------------------------------------------------------------ 9

    public static StepScan Memory(AssistantCatalog catalog, SystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        try
        {
            var snap = WindowsDoctorReader.Read(includePoolTags: false);
            var history = new MemoryHistory(4);
            history.Add(snap);
            var context = new DoctorContext();
            try
            {
                var sysMain = facts.Services.FirstOrDefault(s => string.Equals(s.Name, "SysMain", StringComparison.OrdinalIgnoreCase));
                var state = WindowsMemoryReader.ReadState(sysMain?.State, sysMain?.StartType);
                MemoryAnalysis? a = null;
                try { a = MemoryLabAnalyzer.Analyze(WindowsMemoryReader.ReadSmbios(), facts.Gpus); }
                catch (Exception ex) when (Expected(ex)) { }
                context = context with
                {
                    CompressionEnabled = state.CompressionEnabled,
                    PageFileMode = state.PageFile.Mode,
                    InstalledRamBytes = a is null ? 0 : (long)a.Inventory.InstalledBytes,
                    SingleChannel = a is null ? null : a.Layout.Mode == ChannelMode.SingleChannel,
                    FreeSlot = a?.Inventory.Empty.Any(d => !d.LooksSoldered),
                };
            }
            catch (Exception ex) when (Expected(ex)) { }
            var suspects = MemoryDoctorEngine.Evaluate(history, context);
            return StepPlanners.Memory(catalog, snap, history.LatestGroups, suspects, context);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Memória", ex.Message); }
    }

    // ------------------------------------------------------------------ 10

    public static StepScan Performance(AssistantCatalog catalog, SystemFacts facts, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(facts);
        try
        {
            var traits = PcTraitsProbe.Read(facts.SystemDrive);
            var profile = PcProfiler.Classify(facts, traits, DiagnosticRules.Evaluate(facts));
            var set = RecommendationEngine.Default().Evaluate(profile, catalog.Document, catalog.DeclaredRisk, parameters);
            return StepPlanners.Performance(catalog, set, parameters);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Desempenho", ex.Message); }
    }

    // ------------------------------------------------------------------ 12

    public static StepScan Schedules(AssistantCatalog catalog)
    {
        try
        {
            var scan = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Scan, AutomationScheduledScanHandler.DefaultExecutable);
            var clean = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, AutomationScheduledScanHandler.DefaultExecutable);
            return StepPlanners.Schedules(catalog, scan, clean);
        }
        catch (Exception ex) when (Expected(ex)) { return StepScan.Unavailable("Agendamentos", ex.Message); }
    }
}
