using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Lab.Gpu;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class GpuLabHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return [];
    }
}
