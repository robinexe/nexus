using Nexus.Core;
using Nexus.NetRoute.Handlers;
using Nexus.WindowsActions;

namespace Nexus.NetRoute;

/// <summary>
/// Handlers do módulo Rota de jogo (catalog/netroute.v1.json), registrados pelo aplicador em ModuleHandlers. Os backends reais
/// (cliente WireGuard oficial, serviço do túnel, ACL dos arquivos) só existem no Windows; fora dele a lista é vazia.
/// </summary>
public static class NetRouteHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsWindows()) return [];
        var directory = FileNetRouteStore.ForDataDirectory(environment.DataDirectory);
        var store = new FileNetRouteStore(directory, Windows.NetRouteAcl.ProtectSecretFile, Windows.NetRouteAcl.ProtectDirectory);
        return NetRouteHandlersFactory.Create(store, new Windows.WindowsNetRouteSystem(directory));
    }
}
