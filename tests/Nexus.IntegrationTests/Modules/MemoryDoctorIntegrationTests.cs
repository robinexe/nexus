using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Lab.Memory;
using Nexus.Lab.Memory.Problems;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>Memória: diagnóstico no Windows REAL: leitura completa e as ações pelo BrokerService (aparar/encerrar um processo do
/// próprio teste, esvaziar a lista de espera, política do Edge aplicada e desfeita, reinício de serviço voltando a rodar).</summary>
[Trait("Category", "VmIntegration")]
public sealed class MemoryDoctorIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly WindowsRegistryBackend _reg = new();
    private string JournalPath => Path.Combine(_dir, "journal.db");

    public MemoryDoctorIntegrationTests()
    {
        Vm.Require();
        _dir = Directory.CreateTempSubdirectory("nexus-vm-memdoctor-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private BrokerService Broker(SqliteJournal journal, bool elevated)
    {
        var handlers = new HandlerRegistry();
        foreach (var h in MemoryLabHandlers.Create(new HandlerEnvironment(_reg, _dir, OtherAccount: false, Elevated: elevated))) handlers.Add(h);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, elevated);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx, () => new Dictionary<string, string>());
    }

    private static BrokerResponse Run(BrokerService broker, params ActionRequest[] actions) =>
        broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, actions));

    [Fact]
    public void Snapshot_reads_processes_services_and_kernel_pools()
    {
        var snap = WindowsDoctorReader.Read();
        Assert.True(snap.System.Total > 0);
        Assert.True(snap.System.CommitLimit >= snap.System.CommitTotal);
        Assert.True(snap.System.NonPagedPool > 0);
        Assert.Contains(snap.Processes, p => p.Pid == Environment.ProcessId && p.StartTimeUtcTicks is not null);
        Assert.NotEmpty(snap.ServicesByPid);
        if (Vm.Elevated) Assert.True(snap.PoolTags is { Count: > 20 }, string.Join("; ", snap.Problems.Select(p => $"{p.Key}: {p.Value}")));
        var groups = AppGrouping.Group(snap.Processes, snap.ServicesByPid);
        Assert.Contains(groups, g => g.Kind == AppKind.ServiceHost && g.Services.Count > 0);
        var h = new MemoryHistory();
        h.Add(snap);
        _ = MemoryDoctorEngine.Evaluate(h); // não lança com dados reais
    }

    [Fact]
    public void Trim_and_end_a_process_started_by_the_test()
    {
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            var id = new WindowsProcessControl().Find(child.Id)!;
            var args = new Dictionary<string, string>
            {
                ["pid"] = child.Id.ToString(CultureInfo.InvariantCulture), ["name"] = id.Name,
                ["started"] = id.StartFileTimeUtc.ToString(CultureInfo.InvariantCulture),
            };
            using var journal = new SqliteJournal(JournalPath);
            using var broker = Broker(journal, Vm.Elevated);
            var trim = Run(broker, new ActionRequest(DoctorActions.ProcessTrim, null, args));
            Assert.True(trim.Ok, trim.Error?.Detail);
            Assert.All(trim.Results!, r => Assert.Equal("Verified", r.Outcome));
            var wrong = new Dictionary<string, string>(args) { ["started"] = "1" };
            var refused = Run(broker, new ActionRequest(DoctorActions.ProcessEnd, null, wrong));
            Assert.DoesNotContain(refused.Results ?? [], r => r.Outcome == "Verified");
            Assert.False(child.HasExited);
            var end = Run(broker, new ActionRequest(DoctorActions.ProcessEnd, null, args));
            Assert.True(end.Ok, end.Error?.Detail);
            Assert.True(child.WaitForExit(10_000));
        }
        finally { if (!child.HasExited) child.Kill(); }
    }

    [Fact]
    public void Standby_purge_and_edge_policy_through_the_broker()
    {
        if (!Vm.Elevated) return;
        using var journal = new SqliteJournal(JournalPath);
        using var broker = Broker(journal, true);
        var purge = Run(broker, new ActionRequest(DoctorActions.StandbyPurge));
        Assert.True(purge.Ok, purge.Error?.Detail);
        Assert.All(purge.Results!, r => Assert.Equal("Verified", r.Outcome));

        static object? Read() { using var k = Registry.LocalMachine.OpenSubKey(BrowserPolicyHandler.EdgeKey); return k?.GetValue("StartupBoostEnabled"); }
        var before = Read();
        var apply = Run(broker, new ActionRequest(DoctorActions.EdgeStartupBoostOff));
        Assert.True(apply.Ok, apply.Error?.Detail);
        Assert.Equal(0, Read());
        var revert = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: apply.RunId));
        Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
        Assert.Equal(before, Read());
    }

    [Fact]
    public void Service_restart_ends_running()
    {
        if (!Vm.Elevated) return;
        var control = new WindowsServiceControl();
        var info = control.Query("Spooler");
        if (info is not { State: "Running", RunningDependents: 0, CanStop: true }) return;
        using var journal = new SqliteJournal(JournalPath);
        using var broker = Broker(journal, true);
        var r = Run(broker, new ActionRequest(DoctorActions.ServiceRestart, null, new Dictionary<string, string> { ["service"] = "Spooler" }));
        Assert.True(r.Ok, r.Error?.Detail);
        Assert.All(r.Results!, x => Assert.Equal("Verified", x.Outcome));
        Assert.Equal("Running", control.Query("Spooler")!.State);
        var refused = Run(broker, new ActionRequest(DoctorActions.ServiceRestart, null, new Dictionary<string, string> { ["service"] = "RpcSs" }));
        Assert.DoesNotContain(refused.Results ?? [], x => x.Outcome == "Verified");
    }
}
