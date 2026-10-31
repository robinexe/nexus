using System.IO;
using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;
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
        ]);
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
