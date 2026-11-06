using System.Diagnostics;
using System.Runtime.Versioning;
using Nexus.Core;
using Nexus.GameProfiles.Fortnite;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.GameProfiles;

/// <summary>
/// Handlers de configuração de jogos registrados no aplicador como módulo (o do CS2 continua embutido no Program.cs do
/// aplicador). Hoje: Fortnite (GameUserSettings.ini) e os jogos de Configs.GameConfigs (Minecraft, R6, Overwatch 2, Apex). Cada handler precisa de ação correspondente no catálogo.
/// </summary>
public static class GameProfilesHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Func<string?, string?> local = sid =>
        {
            if (!OperatingSystem.IsWindows()) return null;
            // Com SID (serviço): o perfil daquele usuário. Sem SID: o da conta deste processo, também pelo ProfileList (como a
            // Limpeza faz), nunca por variáveis de ambiente. Com outra conta e sem SID, o handler já recusou antes.
            var who = sid ?? (environment.OtherAccount ? null : WindowsProfiles.CurrentUserSid());
            return who is null ? null : WindowsProfiles.LocalAppDataForSid(environment.Registry, who);
        };
        // Pasta do perfil = pai de AppData\Local (mesma origem segura: ProfileList).
        Func<string?, string?> profile = sid => local(sid) is { } l ? Path.GetDirectoryName(Path.GetDirectoryName(l)) : null;
        return
        [
            new FortniteIniHandler(local, FortniteProcesses.AnyRunning, environment.OtherAccount),
            new Configs.GameConfigHandler(profile, FortniteProcesses.AnyOf, environment.OtherAccount),
        ];
    }
}

/// <summary>Processos do Fortnite abertos (qualquer sessão: o aplicador pode ser um serviço). Só leitura.</summary>
public static class FortniteProcesses
{
    public static bool AnyRunning() => AnyOf(FortniteSettings.BlockingProcesses);

    /// <summary>Algum destes processos está aberto (qualquer sessão)? Na dúvida (sem permissão de listar), responde que sim.</summary>
    public static bool AnyOf(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { return true; } // na dúvida, recusa
            var any = found.Length > 0;
            foreach (var p in found) p.Dispose();
            if (any) return true;
        }
        return false;
    }
}

/// <summary>
/// AppData\Local de um perfil pelo SID, lido de <c>HKLM\...\ProfileList</c> (que o usuário não altera), como a Limpeza faz
/// (<c>WindowsCleaningFolders.ForSid</c>): nunca de variáveis de ambiente nem de chaves que o próprio usuário escreve.
/// </summary>
public static class WindowsProfiles
{
    public static string? LocalAppDataForSid(IRegistryBackend registry, string sid)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!UserProfileScope.IsValidSid(sid)) return null;
        string? profile;
        try
        {
            var r = registry.Read(new RegistryTarget(RegHive.HKLM, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", "ProfileImagePath", RegView.Registry64));
            if (!r.ValueExists || r.Value is null) return null;
            var drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            profile = r.Value.Replace("%SystemDrive%", drive, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { return null; }
        if (profile.Contains('%', StringComparison.Ordinal) || !IsLocalAbsolute(profile)) return null;
        return Path.Combine(profile.TrimEnd('\\', '/'), "AppData", "Local");
    }

    /// <summary>"C:\..." (sem compartilhamento de rede "\\servidor").</summary>
    internal static bool IsLocalAbsolute(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    [SupportedOSPlatform("windows")]
    public static string? CurrentUserSid()
    {
        try
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            var sid = me.User?.Value;
            return UserProfileScope.IsValidSid(sid) ? sid : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
}
