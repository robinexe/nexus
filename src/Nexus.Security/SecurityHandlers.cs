using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Security;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class SecurityHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsWindows()) return [];
        var registry = new WindowsSecurityRegistry(requireUserProfileForHkcu: environment.OtherAccount);
        return Create(environment, registry, new SystemFileProbe(expandUserVariables: !environment.OtherAccount), new SystemHostsStore());
    }

    /// <summary>Com backends explícitos (testes usam registro em memória, arquivos falsos e hosts falso).</summary>
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment, ISecurityRegistry registry, IFileProbe files, IHostsStore hosts)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return
        [
            new HostsFileHandler(hosts, environment.DataDirectory),
            new RegistryOrphanHandler(registry, files, environment.DataDirectory, environment.OtherAccount),
            new PersistenceValueHandler(registry, environment.DataDirectory, environment.OtherAccount),
        ];
    }
}
