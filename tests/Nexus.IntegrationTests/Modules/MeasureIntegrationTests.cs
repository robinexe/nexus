using Nexus.Broker;
using Nexus.Core;
using Nexus.Measurement;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Medir com handler REAL na VM descartável: a ação efêmera <c>measure.driver-latency.trace@1</c> abre a sessão ETW do kernel por
/// 10 s, grava o resumo no perfil de quem pediu e a sessão não existe mais ao fim. Só com elevação (sessão do kernel); sem ela a
/// ação é recusada (Blocked) sem iniciar nada. Em Hyper-V os tempos não valem como medida; aqui só se confere o mecanismo.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class MeasureIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private string JournalPath => Path.Combine(_dir, "journal.db");

    public MeasureIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        Vm.Require(); // PRIMEIRO passo
        _dir = Directory.CreateTempSubdirectory("nexus-vm-measure-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private (BrokerService Broker, SqliteJournal Journal, HandlerEnvironment Env) Broker()
    {
        var journal = new SqliteJournal(JournalPath);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var env = new HandlerEnvironment(new WindowsRegistryBackend(), _dir, OtherAccount: false, Elevated: Vm.Elevated);
        var handlers = new HandlerRegistry();
        foreach (var h in MeasureHandlers.Create(env)) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return (new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>()), journal, env);
    }

    private static BrokerRequest Exec(string actionId) =>
        new(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest(actionId)]);

    private static BrokerRequest Revert(Guid? run) => new(1, Guid.NewGuid(), BrokerOperation.RevertRun, null, run);

    private static string Details(BrokerResponse r) => string.Join("; ", r.Results?.Select(x => $"{x.Outcome}: {x.Detail}") ?? []) + (r.Error?.Detail ?? "");

    [Fact]
    public void Driver_latency_trace_runs_as_admin_writes_the_summary_and_leaves_no_session()
    {
        var (broker, journal, env) = Broker();
        using (broker) using (journal)
        {
            var path = DriverTraceFile.PathFor(MeasureHandlers.LocalAppDataForSid(env, null) ?? throw new InvalidOperationException("perfil não resolvido"));
            if (File.Exists(path)) File.Delete(path);
            var before = DateTimeOffset.Now;

            var exec = broker.Handle(Exec("measure.driver-latency.trace@1"));
            Assert.True(exec.Ok, Details(exec));
            var item = Assert.Single(exec.Results!);
            if (!Vm.Elevated)
            {
                Assert.Equal("Blocked", item.Outcome); // exige administrador: nada iniciado, nada gravado
                Assert.False(File.Exists(path));
                _out.WriteLine("[dpc] sem elevação: recusado como esperado: " + item.Detail);
                return;
            }
            Assert.Equal("Verified", item.Outcome);
            Assert.True(DateTimeOffset.Now - before >= TimeSpan.FromSeconds(9), "a captura dura ~10 s");

            var result = DriverTraceFile.Load(path);
            Assert.NotNull(result);
            Assert.True(result!.TakenAt >= before.AddSeconds(-1));
            Assert.Equal(10, result.DurationSeconds);
            Assert.True(result.EventCount > 0, "um Windows ligado tem DPCs (temporizadores) em 10 s");
            Assert.Contains(result.Modules, m => m.IsKernelCore); // ntoskrnl sempre aparece
            Assert.All(result.Modules, m => Assert.False(string.IsNullOrWhiteSpace(m.Module)));
            _out.WriteLine($"[dpc] {result.EventCount} eventos, {result.Unresolved} sem módulo, {result.Modules.Count} módulos; culpado: {result.Culprit?.Module ?? "nenhum"}");
            foreach (var m in result.Top5) _out.WriteLine($"  {m.Module}: pior {m.WorstUs} µs, {m.DpcCount + m.IsrCount} chamadas, {m.TotalMs} ms");

            // A sessão foi parada: um segundo rastreamento começa do zero (não dá ERROR_ALREADY_EXISTS nem fica preso).
            var again = broker.Handle(Exec("measure.driver-latency.trace@1"));
            Assert.Equal("Verified", Assert.Single(again.Results!).Outcome);

            var revert = broker.Handle(Revert(exec.RunId));
            Assert.True(revert.Ok && revert.FullyRestored == true, Details(revert));
            Assert.Equal("NothingToRevert", Assert.Single(revert.Results!).Outcome);
            Assert.NotEqual(true, exec.SessionActive);
        }
    }
}
