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

    /// <summary>
    /// Regra pura do arquivo de trava (<c>nexus.lock</c>): dono privilegiado e NENHUMA permissão (nem leitura) para contas sem
    /// privilégio. A trava é obtida abrindo o arquivo com <c>FileShare.None</c>; quem consegue abri-lo, mesmo só para leitura,
    /// consegue segurá-la para sempre e impedir toda recuperação (achado B7).
    /// </summary>
    /// <returns>null se confiável; senão o motivo.</returns>
    public static string? EvaluateLock(string? ownerSid, IEnumerable<AccessEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (ownerSid is null || !IsPrivileged(ownerSid)) return $"o dono ({ownerSid ?? "desconhecido"}) não é Administradores/SYSTEM";
        foreach (var e in entries)
            if (e.Allow && e.Rights != 0 && !IsPrivileged(e.Sid) && e.Sid != CreatorOwnerSid)
                return $"a conta {e.Sid} consegue abrir a trava (e segurá-la)";
        return null;
    }

    /// <summary>
    /// Garante <c>nexus.lock</c> para o aplicador elevado sob demanda (sem o instalador do serviço, que já cria o arquivo assim):
    /// se não existe, cria com ACL protegida só para SYSTEM e Administradores; se existe com dono privilegiado e ACL herdada
    /// (ex.: Usuários com leitura, vinda da pasta), corrige; se o dono não é privilegiado (alguém criou antes), recusa.
    /// Nunca segue link. Retorna null se ficou confiável; senão o motivo (o aplicador então não escreve nada).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? EnsureLockFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        try
        {
            if (!file.Exists && file.LinkTarget is null && !Directory.Exists(file.FullName))
            {
                try
                {
                    // Criado já com a ACL final (CreateNew: se alguém criou no meio tempo, cai na conferência abaixo).
                    using var _ = file.Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, /* CreateNew exige direito de escrita */ FileShare.ReadWrite, 4096, FileOptions.None, LockSecurity());
                }
                catch (IOException) when (File.Exists(file.FullName)) { }
                file.Refresh();
            }
            if (!file.Exists) return $"{file.FullName} não é um arquivo.";
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
                return $"{file.FullName} é um link; o aplicador elevado não usa essa trava.";
            var problem = CheckLock(file.GetAccessControl());
            if (problem is null) return null;
            var owner = file.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner is null || !IsPrivileged(owner))
                return $"{file.FullName}: {problem}. Apague o arquivo (como administrador) ou reinstale o serviço do NEXUS.";
            file.SetAccessControl(LockSecurity());
            return CheckLock(file.GetAccessControl()) is { } still ? $"{file.FullName}: {still}." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return $"Não foi possível proteger {file.FullName}: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity LockSecurity()
    {
        var fs = new FileSecurity();
        fs.SetOwner(new SecurityIdentifier(AdministratorsSid));
        fs.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fs.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(LocalSystemSid), FileSystemRights.FullControl, AccessControlType.Allow));
        fs.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdministratorsSid), FileSystemRights.FullControl, AccessControlType.Allow));
        return fs;
    }

    [SupportedOSPlatform("windows")]
    private static string? CheckLock(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        var entries = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => new AccessEntry(r.IdentityReference.Value, (int)r.FileSystemRights, r.AccessControlType == AccessControlType.Allow));
        return EvaluateLock(owner, entries);
    }

    /// <summary>
    /// Pasta com segredos (ex.: backups de Wi-Fi com a senha): SYSTEM e Administradores com controle total, mais ninguém (nem
    /// leitura para Usuários). Os arquivos dentro herdam. Lança exceção se não conseguir: o handler então não grava o segredo.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RestrictToAdministrators(string directory)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var ds = new DirectorySecurity();
        ds.SetOwner(new SecurityIdentifier(AdministratorsSid));
        ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(LocalSystemSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdministratorsSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(ds);
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
