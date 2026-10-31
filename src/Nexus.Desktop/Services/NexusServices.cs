using System.IO;
using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;
using Nexus.WindowsActions.Startup;

namespace Nexus.Desktop.Services;

/// <summary>
/// Tudo o que a interface usa do motor, em modo SOMENTE LEITURA:
/// handlers reais embrulhados em <see cref="SimulationOnlyHandler"/>, diário aberto com <see cref="SqliteJournal.OpenReadOnly"/>
/// e coordenação apenas sondada (<see cref="IRunCoordinator.Probe"/>), nunca adquirida.
/// </summary>
public sealed class NexusServices
{
    public static string DefaultJournalPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", "journal.db");

    public static string CatalogDirectory => Path.Combine(AppContext.BaseDirectory, "catalog");

    public HandlerRegistry Handlers { get; } = BuildReadOnlyHandlers();

    private static HandlerRegistry BuildReadOnlyHandlers()
    {
        var registry = new WindowsRegistryBackend();
        return SimulationOnlyHandler.Wrap(
        [
            new RegistryValueHandler(registry),
            new DirectXUserGpuPreferenceHandler(registry),
            new StartupApprovedHandler(registry),
            new PowerActiveSchemeHandler(new WindowsPowerBackend()),
            new PowerSettingValueHandler(new WindowsPowerBackend()),
            new ServiceStartTypeHandler(new WindowsServiceBackend()),
            new SourceCfgConvarHandler(() => null),
            .. ModuleHandlersForPreview(registry),
        ]);
    }

    /// <summary>Handlers dos módulos, só para a página Catálogo ler o estado e simular (embrulhados acima: nunca escrevem).</summary>
    private static List<IActionHandler> ModuleHandlersForPreview(WindowsRegistryBackend registry)
    {
        var env = new HandlerEnvironment(registry,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS"), OtherAccount: false, Elevated: false);
        var all = new List<IActionHandler>();
        Func<HandlerEnvironment, IEnumerable<IActionHandler>>[] modules =
        [
            Nexus.Cleaning.CleaningHandlers.Create, Nexus.DiskTools.DiskToolsHandlers.Create, Nexus.Programs.ProgramsHandlers.Create,
            Nexus.Boot.BootHandlers.Create, Nexus.Privacy.PrivacyHandlers.Create, Nexus.Security.SecurityHandlers.Create,
            Nexus.Security.Scanner.ScannerHandlers.Create,
            Nexus.Monitoring.MonitoringHandlers.Create, Nexus.SystemTools.SystemToolsHandlers.Create, Nexus.DriverUpdates.DriverUpdatesHandlers.Create, Nexus.Automation.AutomationHandlers.Create,
            Nexus.Lab.Cpu.CpuLabHandlers.Create, Nexus.Lab.Gpu.GpuLabHandlers.Create, Nexus.Lab.Memory.MemoryLabHandlers.Create,
        ];
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var create in modules)
        {
            try
            {
                foreach (var h in create(env))
                    if (kinds.Add(h.HandlerKind)) all.Add(h);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { } // módulo indisponível neste PC: a página mostra "handler pendente"
        }
        return all;
    }

    public static SystemContext ContextFrom(SystemFacts f) =>
        new(f.WindowsBuild ?? 0, f.CpuModel ?? "desconhecido", f.GpuVendorPrimary, f.IsElevated ?? false); // desconhecido = não elevado (ações de administrador ficam bloqueadas)

    public static Dictionary<string, string> ParametersFrom(SystemFacts f)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        if (f.ActivePowerScheme is { } s) p["activeScheme"] = s.ToString("D");
        return p;
    }

    public static CoordinationProbe ProbeExecutors(string journalPath) => FileRunCoordinator.ForJournal(journalPath).Probe();
}
