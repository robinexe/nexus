using Nexus.Core;
using Nexus.Lab.Gpu.Windows;
using Nexus.WindowsActions;

namespace Nexus.Lab.Gpu;

/// <summary>
/// Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.
/// A preferência de GPU por app reaproveita o handler do núcleo (DirectXUserGpuPreference), registrado pelo broker.
/// </summary>
public static class GpuLabHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        IDisplayModeBackend backend = OperatingSystem.IsWindows() ? new WindowsDisplayModeBackend() : new NoDisplayModeBackend();
        return [new DisplayModeHandler(backend, environment.OtherAccount)];
    }
}
