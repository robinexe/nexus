using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Lab.Memory;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>Laboratório da memória no Windows REAL: leitura do SMBIOS/estado e a ação do arquivo de paginação pelo BrokerService.</summary>
[Trait("Category", "VmIntegration")]
public sealed class MemoryLabIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly WindowsRegistryBackend _reg = new();
    private string JournalPath => Path.Combine(_dir, "journal.db");

    public MemoryLabIntegrationTests()
    {
        Vm.Require(); // nada é criado antes do portão
        _dir = Directory.CreateTempSubdirectory("nexus-vm-memory-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private static string? PagingFiles()
    {
        using var k = Registry.LocalMachine.OpenSubKey(PageFileHandler.MemoryManagementKey);
        return k?.GetValue("PagingFiles") switch { string[] a => string.Join('\u0000', a), string s => s, _ => null };
    }

    [Fact]
    public void Smbios_and_windows_state_are_read_without_admin_requirements()
    {
        var analysis = MemoryLabAnalyzer.Analyze(WindowsMemoryReader.ReadSmbios());
        Assert.NotEmpty(analysis.Inventory.Devices);
        var state = WindowsMemoryReader.ReadState(null, null);
        Assert.NotNull(state.TotalPhysicalBytes);
        Assert.NotNull(state.CommitLimitBytes);
        Assert.NotEqual(PageFileMode.Unknown, state.PageFile.Mode);
    }

    [Fact]
    public void Pagefile_automatic_is_applied_verified_and_reverted_exactly()
    {
        if (!Vm.Elevated) return; // ação de administrador
        var before = PagingFiles();
        var handlers = new HandlerRegistry();
        foreach (var h in MemoryLabHandlers.Create(new HandlerEnvironment(_reg, _dir, OtherAccount: false, Elevated: true))) handlers.Add(h);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, true);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        using var journal = new SqliteJournal(JournalPath);
        using var broker = new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx, () => new Dictionary<string, string>());

        var exec = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest("memorylab.pagefile.automatic@1")]));
        Assert.True(exec.Ok, exec.Error?.Detail);
        Assert.All(exec.Results!, r => Assert.True(r.Outcome is "Verified" or "AlreadyConfigured", $"{r.ActionKey}: {r.Outcome} {r.Detail}"));
        Assert.Equal(PageFileHandler.AutomaticValue, PagingFiles());

        var revert = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: exec.RunId));
        Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
        Assert.Equal(before, PagingFiles());
    }
}
