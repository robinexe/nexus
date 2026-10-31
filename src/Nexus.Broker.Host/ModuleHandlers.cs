using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Broker.Host;

/// <summary>Reúne os handlers de todos os módulos. Um módulo novo entra aqui com uma linha.</summary>
internal static class ModuleHandlers
{
    public static IEnumerable<IActionHandler> All(HandlerEnvironment environment) =>
    [
        .. Nexus.Cleaning.CleaningHandlers.Create(environment),
        .. Nexus.DiskTools.DiskToolsHandlers.Create(environment),
        .. Nexus.Programs.ProgramsHandlers.Create(environment),
        .. Nexus.Boot.BootHandlers.Create(environment),
        .. Nexus.Privacy.PrivacyHandlers.Create(environment),
        .. Nexus.Security.SecurityHandlers.Create(environment),
        .. Nexus.Monitoring.MonitoringHandlers.Create(environment),
        .. Nexus.SystemTools.SystemToolsHandlers.Create(environment),
        .. Nexus.Automation.AutomationHandlers.Create(environment),
        .. Nexus.Lab.Cpu.CpuLabHandlers.Create(environment),
        .. Nexus.Lab.Gpu.GpuLabHandlers.Create(environment),
        .. Nexus.Lab.Memory.MemoryLabHandlers.Create(environment),
    ];
}
