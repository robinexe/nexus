using Nexus.Core;
using Nexus.DiskTools.SecureDelete;
using Nexus.WindowsActions;

namespace Nexus.DiskTools;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
/// <remarks>A análise de disco é somente leitura e roda na própria interface (não passa pelo aplicador). Só a exclusão segura escreve.</remarks>
public static class DiskToolsHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return [DiskSecureDeleteHandler.ForEnvironment(environment)];
    }
}
