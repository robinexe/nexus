using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nexus.WindowsActions;

/// <summary>Uma entrada de permissão lida da ACL (máscara de acesso do Win32, forma neutra para a regra ser testável fora do Windows).</summary>
public sealed record AccessEntry(string Sid, int Rights, bool Allow);

/// <summary>
/// Pasta de dados do aplicador ELEVADO (diário, quarentenas, cópias de segurança): só administradores podem alterá-la.
/// <para>Sob a ACL padrão de <c>%ProgramData%</c>, um usuário comum consegue criar subpastas e arquivos (e é dono do que cria).
/// Quando o aplicador elevado sob demanda (sem o instalador do serviço) criava <c>%ProgramData%\NEXUS</c> com
/// <c>Directory.CreateDirectory</c>, ou quando um usuário comum criava a pasta antes, esse usuário podia adulterar o diário e
/// os manifestos que o aplicador usa para RESTAURAR com privilégio de administrador (ex.: devolver um <c>Debugger</c> de IFEO
/// escolhido por ele) ou plantar junções nas pastas da quarentena. Isto vale também para malware sem elevação na sessão de um
/// administrador: ele esperaria o usuário clicar em "Desfazer".</para>
/// </summary>
public static class AdminOnlyDirectory
{
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string LocalSystemSid = "S-1-5-18";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const string CreatorOwnerSid = "S-1-3-0";
    public const string UsersSid = "S-1-5-32-545";

    /// <summary>
    /// Direitos (máscara Win32) que permitem criar, trocar, apagar ou mudar a ACL de algo dentro da pasta: FILE_WRITE_DATA/ADD_FILE,
    /// FILE_APPEND_DATA/ADD_SUBDIRECTORY, FILE_WRITE_EA, FILE_DELETE_CHILD, FILE_WRITE_ATTRIBUTES, DELETE, WRITE_DAC, WRITE_OWNER,
    /// GENERIC_ALL e GENERIC_WRITE.
    /// </summary>
    public const int WriteLike = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

    public const int MaxEntriesToRepair = 200_000;

    public static bool IsPrivileged(string sid) => sid is AdministratorsSid or LocalSystemSid or TrustedInstallerSid;

    /// <summary>
    /// Regra pura: dono privilegiado e nenhuma permissão de escrita para contas sem privilégio. "CREATOR OWNER" é ignorado:
    /// ele só vale para o que for criado dentro, e ninguém sem privilégio consegue criar algo quando esta regra é satisfeita.
    /// </summary>
    /// <returns>null se confiável; senão o motivo.</returns>
    public static string? Evaluate(string? ownerSid, IEnumerable<AccessEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (ownerSid is null || !IsPrivileged(ownerSid)) return $"o dono ({ownerSid ?? "desconhecido"}) não é Administradores/SYSTEM";
        foreach (var e in entries)
        {
            if (!e.Allow || IsPrivileged(e.Sid) || e.Sid == CreatorOwnerSid) continue;
            if ((e.Rights & WriteLike) != 0) return $"a conta {e.Sid} pode criar ou alterar arquivos ali";
        }
        return null;
    }

    /// <summary>
    /// Garante a pasta: cria com ACL protegida (SYSTEM e Administradores: total; Usuários: leitura) se não existir; se existir com
    /// dono privilegiado mas ACL herdada (versões anteriores), corrige a ACL e confere que nada dentro pertence a outra conta.
    /// Nunca segue link/junção. Retorna null se a pasta ficou confiável; senão o motivo (o aplicador então não escreve nada).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? Ensure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        try
        {
            if (!dir.Exists && !File.Exists(dir.FullName) && dir.LinkTarget is null)
            {
                if (dir.Parent is { Exists: false } parent) parent.Create();
                dir.Create(Protected()); // criada já com a ACL final: não há janela com a ACL herdada
                dir.Refresh();
            }
            if (!dir.Exists) return $"{dir.FullName} não é uma pasta.";
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || dir.LinkTarget is not null)
                return $"{dir.FullName} é um link ou junção; o aplicador elevado não usa essa pasta.";

            var problem = Check(dir.GetAccessControl());
            if (problem is null) return null;
            var owner = dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner is null || !IsPrivileged(owner))
                return $"{dir.FullName}: {problem}. Apague a pasta (como administrador) ou reinstale o serviço do NEXUS.";

            // Dono privilegiado, ACL herdada de %ProgramData%: corrige e confere o que já existe dentro.
            dir.SetAccessControl(Protected());
            if (Check(dir.GetAccessControl()) is { } still) return $"{dir.FullName}: {still}.";
            var seen = 0;
            foreach (var item in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if (++seen > MaxEntriesToRepair) return $"{dir.FullName} tem itens demais para conferir; esvazie a quarentena antiga ou reinstale o serviço.";
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return $"{item.FullName} é um link ou junção dentro da pasta de dados.";
                FileSystemSecurity security = item is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)item).GetAccessControl();
                if (Check(security) is { } bad) return $"{item.FullName}: {bad}.";
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return $"Não foi possível proteger {dir.FullName}: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? Check(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        var entries = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => new AccessEntry(r.IdentityReference.Value, (int)r.FileSystemRights, r.AccessControlType == AccessControlType.Allow));
        return Evaluate(owner, entries);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity Protected()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var ds = new DirectorySecurity();
        ds.SetOwner(new SecurityIdentifier(AdministratorsSid));
        ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(LocalSystemSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdministratorsSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(UsersSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return ds;
    }
}
