using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Cleaning;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class CleaningHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Func<string?, CleaningFolders?> folders = sid =>
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (sid is not null) return WindowsCleaningFolders.ForSid(environment.Registry, sid);
            // Sem SID: com outra conta (serviço), só as pastas da máquina; regras de perfil sem SID já são recusadas pelo handler.
            return environment.OtherAccount ? WindowsCleaningFolders.ForMachine(environment.Registry) : WindowsCleaningFolders.ForCurrentProcess(environment.Registry);
        };
        return [new CleaningRuleHandler(CleaningRuleSet.BuiltIn, environment.DataDirectory, folders, SystemProcessProbe.Instance, environment.OtherAccount)];
    }
}
