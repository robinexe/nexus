using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Nexus.Persistence;
using Nexus.WindowsActions;

namespace Nexus.IntegrationTests;

/// <summary>
/// Núcleo v2 no Windows real (VM descartável): <see cref="SafeFileMover"/> por handle (rename relativo ao handle com
/// FileRenameInfoEx, caminho final por GetFinalPathNameByHandle, pasta-mãe presa sem FILE_SHARE_DELETE) e a trava
/// <c>nexus.lock</c> criada pelo aplicador com ACL só de SYSTEM/Administradores. O lote pelo broker real é coberto por
/// <c>ProgramsIntegrationTests</c> (duas instâncias de programs.leftover.user num pedido, com RevertRun).
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class CoreV2IntegrationTests : IDisposable
{
    private readonly string _dir;

    public CoreV2IntegrationTests()
    {
        Vm.Require(); // nada é criado antes do portão
        _dir = Directory.CreateTempSubdirectory("nexus-vm-core2-").FullName;
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
                Directory.Delete(d); // junções do teste: apaga o link, não o alvo
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void Safe_mover_renames_by_handle_folders_and_files()
    {
        Assert.True(SafeFileMover.IsHandleRelative);
        var src = Dir("Local", "Acme");
        File.WriteAllText(Path.Combine(src, "a.txt"), "1");
        var q = Dir("q");
        SafeFileMover.Move(src, Path.Combine(q, "payload"));
        Assert.False(Directory.Exists(src));
        Assert.Equal("1", File.ReadAllText(Path.Combine(q, "payload", "a.txt")));
        var file = Path.Combine(Dir("Local"), "b.ini");
        File.WriteAllText(file, "2");
        SafeFileMover.Move(file, Path.Combine(q, "b.ini"));
        Assert.Equal("2", File.ReadAllText(Path.Combine(q, "b.ini")));
        // Destino ocupado: nunca sobrescreve.
        File.WriteAllText(file, "3");
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(file, Path.Combine(q, "b.ini")));
        Assert.Equal("3", File.ReadAllText(file));
    }

    [Fact]
    public void Safe_mover_refuses_junction_in_the_chain_and_as_the_item()
    {
        var victim = Dir("Windows-falso");
        File.WriteAllText(Path.Combine(victim, "kernel.dll"), "não mover");
        var local = Dir("Local");
        Junction(Path.Combine(local, "Acme"), victim);
        var q = Dir("q");
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(Path.Combine(local, "Acme"), Path.Combine(q, "p1")));
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(Path.Combine(local, "Acme", "kernel.dll"), Path.Combine(q, "p2")));
        Assert.Equal("não mover", File.ReadAllText(Path.Combine(victim, "kernel.dll")));
    }

    [Fact]
    public void Parent_cannot_be_swapped_while_the_mover_holds_it()
    {
        var local = Dir("Local");
        var src = Path.Combine(local, "Acme");
        File.WriteAllText(src, "sobra");
        var q = Dir("q");
        Exception? swap = null;
        SafeFileMover.Move(src, Path.Combine(q, "payload"), _ =>
        {
            try { Directory.Move(local, local + ".real"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { swap = ex; }
        });
        Assert.NotNull(swap); // o Windows recusou renomear a pasta-mãe: o handle está aberto sem FILE_SHARE_DELETE
        Assert.Equal("sobra", File.ReadAllText(Path.Combine(q, "payload")));
    }

    [Fact]
    public void File_in_use_is_refused_and_stays()
    {
        var src = Path.Combine(Dir("Local"), "aberto.log");
        File.WriteAllText(src, "x");
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(src, Path.Combine(Dir("q"), "aberto.log")));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Lock_file_is_created_only_for_system_and_administrators()
    {
        if (!Vm.Elevated) return; // dono Administradores exige elevação; a VM do CI roda elevada
        var path = Path.Combine(_dir, FileRunCoordinator.LockFileName);
        Assert.Null(AdminOnlyDirectory.EnsureLockFile(path));
        var acl = new FileInfo(path).GetAccessControl();
        Assert.Equal(AdminOnlyDirectory.AdministratorsSid, acl.GetOwner(typeof(SecurityIdentifier))?.Value);
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet();
        Assert.DoesNotContain(AdminOnlyDirectory.UsersSid, sids);
        Assert.True(acl.AreAccessRulesProtected);
        // Idempotente, e a trava continua funcionando para o próprio aplicador.
        Assert.Null(AdminOnlyDirectory.EnsureLockFile(path));
        var lease = new FileRunCoordinator(_dir).TryAcquire("teste");
        Assert.True(lease.Acquired, lease.Detail);
        lease.Lease!.Dispose();
    }
}
