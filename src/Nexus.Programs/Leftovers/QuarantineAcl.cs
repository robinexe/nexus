using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nexus.Programs.Leftovers;

/// <summary>
/// Quarentena de sobras da MÁQUINA (o que volta para Program Files/ProgramData): pasta e manifesto com dono Administradores e
/// ACL protegida (SYSTEM e Administradores: controle total; Usuários: só leitura). Assim um usuário comum não consegue trocar
/// o conteúdo que o Restore devolveria com privilégio de administrador, e <c>AdminOwnedFile.Check</c> confere isso antes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class QuarantineAcl
{
    public static void Harden(string path)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        if (Directory.Exists(path))
        {
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var ds = new DirectorySecurity();
            ds.SetOwner(admins);
            ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(ds);
        }
        else if (File.Exists(path))
        {
            var fs = new FileSecurity();
            fs.SetOwner(admins);
            fs.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fs.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            fs.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            fs.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(fs);
        }
    }
}
