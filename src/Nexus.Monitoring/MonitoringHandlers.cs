using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Monitoring;

/// <summary>
/// Handlers do módulo Monitor. O módulo é SOMENTE LEITURA (contadores, processos, saúde de disco): não há ação de catálogo
/// nem handler de escrita, por isso a lista é vazia de propósito.
/// </summary>
public static class MonitoringHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return [];
    }
}
