using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Limpeza real (pasta %TEMP% da conta da VM) pelo broker completo: aplicar, conferir, arquivo em uso pulado, desfazer.
/// Só numa VM liberada: o portão é avaliado antes de qualquer arquivo ser criado.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class CleaningIntegrationTests : IDisposable
{
    private readonly string _dir;

    public CleaningIntegrationTests()
    {
        Vm.Require();
        _dir = Directory.CreateTempSubdirectory("nexus-vm-limpeza-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void Temp_cleaning_moves_old_files_skips_open_ones_and_reverts()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var old = Path.Combine(temp, $"nexus-teste-{Guid.NewGuid():N}.tmp");
        var busy = Path.Combine(temp, $"nexus-aberto-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(old, "velho");
        File.WriteAllText(busy, "aberto");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(busy, DateTime.UtcNow.AddDays(-3));

        var registry = new WindowsRegistryBackend();
        var handlers = new HandlerRegistry();
        foreach (var h in CleaningHandlers.Create(new HandlerEnvironment(registry, _dir, false, Vm.Elevated))) handlers.Add(h);
        var journalPath = Path.Combine(_dir, "journal.db");
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        using var journal = new SqliteJournal(journalPath);
        using var broker = new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(journalPath), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>());

        var batch = CleaningBatchId.New(DateTime.UtcNow);
        BrokerResponse exec;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read)) // aberto por "outro programa"
        {
            exec = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute,
                [new ActionRequest("cleaning.temp-user@1", null, new Dictionary<string, string> { ["batch"] = batch })]));
        }
        Assert.True(exec.Ok, exec.Error?.Detail);
        Assert.Equal("Verified", Assert.Single(exec.Results!).Outcome);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(busy));
        var manifest = QuarantineStore.ReadManifest(QuarantineStore.RuleDirectory(_dir, batch, "temp-user"))!;
        Assert.Contains(manifest.Entries, e => e.Path.Equals(old, StringComparison.OrdinalIgnoreCase) && e.State == QuarantineEntryState.Moved);
        Assert.Contains(manifest.Entries, e => e.Path.Equals(busy, StringComparison.OrdinalIgnoreCase) && e.State == QuarantineEntryState.SkippedInUse);

        var revert = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: exec.RunId));
        Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
        Assert.Equal("velho", File.ReadAllText(old));
        File.Delete(old);
        File.Delete(busy);
    }
}
