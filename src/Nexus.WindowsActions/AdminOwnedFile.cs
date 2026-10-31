using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nexus.WindowsActions;

/// <summary>
/// Confere se um arquivo de controle (ex.: liberação do portão de execução) só pode ter sido criado e só pode ser alterado
/// por administradores: dono Administradores/SYSTEM/TrustedInstaller e nenhuma permissão de escrita para outras contas.
/// Sob a ACL padrão de %ProgramData%, um usuário comum consegue criar arquivos; esta checagem impede que isso libere algo.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AdminOwnedFile
{
    private const FileSystemRights WriteLike = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <returns>null se confiável; caso contrário, o motivo.</returns>
    public static string? Check(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsPrivileged(owner))
            return $"O arquivo {path} não pertence a Administradores/SYSTEM (dono: {security.GetOwner(typeof(SecurityIdentifier))}).";
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || IsPrivileged(sid)) continue;
            if ((rule.FileSystemRights & WriteLike) != 0)
                return $"O arquivo {path} pode ser alterado por uma conta sem privilégio ({sid}).";
        }
        return null;
    }

    private static bool IsPrivileged(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller
}
