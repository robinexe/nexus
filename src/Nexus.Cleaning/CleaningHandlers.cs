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

        // Regras do usuário: só no aplicador da própria conta e sem elevação. O perfil (e daí %AppData%\NEXUS\rules) vem de
        // ProfileList pelo SID deste processo, como no serviço; as variáveis de ambiente não decidem nada aqui.
        var userRulesAllowed = !environment.OtherAccount && !environment.Elevated;
        Func<string?, CleaningFolders?> userFolders = _ =>
            OperatingSystem.IsWindows() && userRulesAllowed && WindowsCleaningFolders.CurrentUserSid() is { } me
                ? WindowsCleaningFolders.ForSid(environment.Registry, me)
                : null;
        Func<UserRuleLoad?> userRules = () =>
            OperatingSystem.IsWindows() && userRulesAllowed && userFolders(null) is { } f ? UserCleaningRules.Load(UserCleaningRules.FolderFor(f)) : null;

        IRecycleBinBackend? bin = OperatingSystem.IsWindows() ? WindowsRecycleBin.Instance : null;
        return
        [
            new CleaningRuleHandler(CleaningRuleSet.BuiltIn, environment.DataDirectory, folders, SystemProcessProbe.Instance, environment.OtherAccount),
            new CleaningUserRuleHandler(userRules, environment.DataDirectory, userFolders, SystemProcessProbe.Instance, environment.OtherAccount, environment.Elevated),
            new CleaningQuarantinePurgeHandler(environment.DataDirectory, environment.OtherAccount, environment.Elevated),
            new CleaningRecycleBinHandler(bin, environment.OtherAccount),
        ];
    }
}
