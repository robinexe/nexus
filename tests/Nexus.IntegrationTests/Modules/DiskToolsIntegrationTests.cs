using System.Diagnostics;
using Nexus.Broker;
using Nexus.Core;
using Nexus.DiskTools.SecureDelete;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Exclusão segura com o handler REAL no Windows (só em VM liberada): verificação pelo handle (caminho final, links físicos),
/// junção no caminho, e o contrato irreversível pelo broker com diário SQLite. Só escreve numa pasta própria em %LocalAppData%
/// (caminho longo: %TEMP% do runner usa nome curto 8.3, que a política recusa de propósito).
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class DiskToolsIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _journalDir;

    public DiskToolsIntegrationTests()
    {
        Vm.Require(); // antes de qualquer arquivo
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nexus-vm-disk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _journalDir = Directory.CreateTempSubdirectory("nexus-vm-disk-j-").FullName;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DiskSecureDeleteHandler Handler() =>
        // Elevated=false de propósito: a VM do CI roda elevada, e o handler recusa elevação em produção.
        new(new HandlerEnvironment(new WindowsRegistryBackend(), _journalDir, OtherAccount: false, Elevated: false), new SecureDeleteFileSystem(),
            new SecureDeletePolicy(Environment.GetFolderPath(Environment.SpecialFolder.Windows), [_journalDir]).Check);

    private string NewFile(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "conteúdo sensível de teste");
        return p;
    }

    private static void Cmd(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(15000);
        Assert.Equal(0, p.ExitCode);
    }

    private static BrokerRequest Exec(string path, bool? confirm) =>
        new(1, Guid.NewGuid(), BrokerOperation.Execute,
            [new ActionRequest("disk.secure-delete@1", null, new Dictionary<string, string> { ["filePath"] = path }, confirm)]);

    [Fact]
    public void Broker_erases_only_with_confirmation_and_refuses_to_undo()
    {
        var path = NewFile("segredo.txt");
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var journalPath = Path.Combine(_journalDir, "journal.db");
        using var journal = new SqliteJournal(journalPath);
        var ctx = new SystemContext(Vm.Facts.WindowsBuild ?? 0, "?", null, false);
        using var broker = new BrokerService(catalog, new HandlerRegistry().Add(Handler()), journal, FileRunCoordinator.ForJournal(journalPath), Vm.Gate,
            () => ctx, () => new Dictionary<string, string>());

        var refused = broker.Handle(Exec(path, null));
        Assert.Equal("irreversible-unconfirmed", refused.Error?.Code);
        Assert.True(File.Exists(path));

        var done = broker.Handle(Exec(path, true));
        Assert.True(done.Ok, done.Error?.Detail);
        Assert.Equal("Verified", Assert.Single(done.Results!).Outcome);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));

        var undo = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: done.RunId));
        Assert.Equal("Irreversible", Assert.Single(undo.Results!).Outcome);
        Assert.False(undo.FullyRestored);
    }

    [Fact]
    public void Hard_links_and_junctions_in_the_path_are_refused_by_the_real_checks()
    {
        var h = Handler();
        var def = new ActionDefinition("disk.secure-delete", 1, "t", Classification.Conditional, DiskSecureDeleteHandler.Kind, "m", [], "r",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None) { Irreversible = true };
        ActionInstance A(string p) => new(def, new Dictionary<string, string> { ["path"] = p }, StateSnapshot.Of("FileState", "absent"));

        var original = NewFile("original.txt");
        var link = Path.Combine(_dir, "linkfisico.txt");
        Cmd($"mklink /H \"{link}\" \"{original}\"");
        var r = h.Assess(A(original), new SystemContext(0, "?", null, false));
        Assert.Equal(ApplicabilityKind.Blocked, r.Kind);
        Assert.Contains("links físicos", r.Reason, StringComparison.Ordinal);

        var realDir = Path.Combine(_dir, "real");
        Directory.CreateDirectory(realDir);
        var inside = Path.Combine(realDir, "alvo.txt");
        File.WriteAllText(inside, "x");
        var junction = Path.Combine(_dir, "juncao");
        Cmd($"mklink /J \"{junction}\" \"{realDir}\"");
        var viaJunction = h.Assess(A(Path.Combine(junction, "alvo.txt")), new SystemContext(0, "?", null, false));
        Assert.Equal(ApplicabilityKind.Blocked, viaJunction.Kind);
        Assert.True(File.Exists(inside));

        // Arquivo comum: aceito pelas verificações reais (caminho final igual, um link).
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(A(inside), new SystemContext(0, "?", null, false)).Kind);
    }
}
