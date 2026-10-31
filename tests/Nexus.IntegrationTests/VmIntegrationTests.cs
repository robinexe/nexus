using System.Diagnostics;
using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;
using Nexus.WindowsActions.Startup;

namespace Nexus.IntegrationTests;

/// <summary>Portão compartilhado: avaliado antes de QUALQUER escrita. Fora de uma VM liberada, o teste falha sem escrever.</summary>
internal static class Vm
{
    private static readonly Lazy<SystemFacts> FactsLazy = new(() => Discovery.Capture().Facts);
    public static SystemFacts Facts => FactsLazy.Value;

    public static IExecutionGate Gate { get; } =
        new VmOnlyExecutionGate(() => Facts.IsVirtualMachine, VmOnlyExecutionGate.DefaultMarkerPath, Environment.MachineName, AdminOwnedFile.Check);

    public static void Require()
    {
        var d = Gate.Evaluate();
        Assert.True(d.Allowed, "BLOQUEADO — nada foi escrito: " + d.Reason);
    }

    public static bool Elevated => Facts.IsElevated == true;
}

[Trait("Category", "VmIntegration")]
public sealed class VmIntegrationTests : IDisposable
{
    private static readonly string[] AppliedOutcomes = ["Verified", "AlreadyConfigured"];
    private static readonly string[] RefusedOutcomes = ["Blocked", "NotApplicable"];
    private readonly string _dir;
    private string JournalPath => Path.Combine(_dir, "journal.db");
    private readonly WindowsRegistryBackend _reg = new();

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public VmIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        Vm.Require(); // PRIMEIRO passo: nada (nem pasta temporária) é criado antes do portão
        _dir = Directory.CreateTempSubdirectory("nexus-vm-").FullName;
    }

    // O diário NÃO é apagado: se um teste falhar com algo aplicado, ele permite recuperar. Na VM descartável isso é inócuo.
    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private HandlerRegistry RealHandlers() => new HandlerRegistry()
        .Add(new RegistryValueHandler(_reg))
        .Add(new DirectXUserGpuPreferenceHandler(_reg))
        .Add(new StartupApprovedHandler(_reg))
        .Add(new PowerActiveSchemeHandler(new WindowsPowerBackend()))
        .Add(new PowerSettingValueHandler(new WindowsPowerBackend()))
        .Add(new ServiceStartTypeHandler(new WindowsServiceBackend()))
        .Add(new SourceCfgConvarHandler(() => null));

    private (BrokerService Broker, SqliteJournal Journal) Broker()
    {
        var journal = new SqliteJournal(JournalPath);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var p = new Dictionary<string, string>();
        if (facts.ActivePowerScheme is { } s) p["activeScheme"] = s.ToString("D");
        var catalog = CatalogLoader.Load(Path.Combine(AppContext.BaseDirectory, "catalog", "actions.v1.json"));
        return (new BrokerService(catalog, RealHandlers(), journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx, () => p), journal);
    }

    private static BrokerRequest Req(BrokerOperation op, string? actionId = null, Guid? run = null) =>
        new(1, Guid.NewGuid(), op, actionId is null ? null : [new ActionRequest(actionId)], run);

    private static string? ReadHkcu(string path, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    // ------------------------------------------------------------------ registro HKCU (catálogo real)

    [Fact]
    public void GameMode_is_applied_verified_and_reverted_to_the_exact_original()
    {
        const string path = @"Software\Microsoft\GameBar";
        var before = ReadHkcu(path, "AutoGameModeEnabled");
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Req(BrokerOperation.Execute, "win.gamemode.enable@1"));
            Assert.True(exec.Ok, exec.Error?.Detail);
            var outcome = Assert.Single(exec.Results!).Outcome;
            Assert.Contains(outcome, AppliedOutcomes);
            Assert.Equal("1", ReadHkcu(path, "AutoGameModeEnabled"));

            var revert = broker.Handle(Req(BrokerOperation.RevertRun, run: exec.RunId));
            Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
            Assert.Equal(before, ReadHkcu(path, "AutoGameModeEnabled")); // inclusive "ausente" volta a ser ausente
        }
    }

    // ------------------------------------------------------------------ crash no meio, recuperação pelo nexus-recover real

    [Fact]
    public void Crash_after_real_registry_write_is_recovered_by_the_real_cli()
    {
        const string path = @"Software\NexusVmIntegration";
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); // chave própria do teste
        var action = new ActionInstance(
            new ActionDefinition("vm.crash", 1, "crash", Classification.SupportedDocumented, RegistryValueHandler.Kind, "teste",
                [new EvidenceRef(EvidenceLevel.Hypothesis, "teste")], "nenhum", PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
            new Dictionary<string, string> { ["hive"] = "HKCU", ["path"] = path, ["name"] = "V", ["type"] = "DWord" },
            StateSnapshot.Of("DWord", "1"));

        using (var journal = new SqliteJournal(JournalPath))
        using (var lease = FileRunCoordinator.ForJournal(JournalPath).TryAcquire("vm-crash").Lease!)
        {
            var handlers = RealHandlers();
            var exec = new ActionExecutor(handlers, journal, lease)
            {
                FaultInjector = (w, _) => { if (w == "after-apply") throw new InvalidOperationException("crash simulado"); },
            };
            var run = exec.BeginRun("vm-crash", "1");
            var plan = new DesiredStateResolver(handlers).Resolve([action], new SystemContext(26100, "?", null, false));
            Assert.Throws<InvalidOperationException>(() => exec.Execute(run, plan, new SystemContext(26100, "?", null, false)));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal("1", ReadHkcu(path, "V")); // a escrita real aconteceu

        var cli = Path.Combine(AppContext.BaseDirectory, "nexus-recover.exe");
        var psi = new ProcessStartInfo(cli, ["recover", "--journal", JournalPath]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000));
        Assert.True(p.ExitCode == 0, output);
        Assert.Null(ReadHkcu(path, "V"));
        using var key = Registry.CurrentUser.OpenSubKey(path);
        Assert.Null(key); // a chave criada pelo NEXUS e deixada vazia foi removida
    }

    // ------------------------------------------------------------------ energia (sessão)

    [Fact]
    public void High_performance_session_is_reverted_by_end_session_or_reported_not_applicable()
    {
        var power = new WindowsPowerBackend();
        var before = power.GetActiveScheme();
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Req(BrokerOperation.Execute, "power.active-scheme.high-performance.session@1"));
            Assert.True(exec.Ok, exec.Error?.Detail);
            var r = Assert.Single(exec.Results!);
            _out.WriteLine($"[energia/sessão] esquema antes={before}; resultado={r.Outcome}");
            if (r.Outcome is "NotApplicable" or "Blocked" or "AlreadyConfigured")
            {
                _out.WriteLine("[energia/sessão] RAMO: não aplicável (nada foi alterado)");
                Assert.Equal(before, power.GetActiveScheme()); // nada mudou
                return;
            }
            Assert.Equal("Verified", r.Outcome);
            Assert.True(exec.SessionActive);
            Assert.Equal(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), power.GetActiveScheme());

            var end = broker.Handle(Req(BrokerOperation.EndSession, run: exec.RunId));
            Assert.True(end.Ok && end.FullyRestored == true, end.Error?.Detail);
            Assert.Equal(before, power.GetActiveScheme());
            _out.WriteLine("[energia/sessão] RAMO: aplicado (Alto desempenho) e revertido por EndSession");
        }
    }

    // ------------------------------------------------------------------ administrador: serviço e energia AC

    [Fact]
    public void Admin_actions_are_blocked_without_elevation_and_round_trip_with_it()
    {
        var svc = new WindowsServiceBackend();
        var before = svc.GetStartType("SysMain");
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Req(BrokerOperation.Execute, "svc.sysmain.manual.lab@1"));
            Assert.True(exec.Ok, exec.Error?.Detail);
            var r = Assert.Single(exec.Results!);
            _out.WriteLine($"[SysMain] elevado={Vm.Elevated}; inicialização antes={before ?? "(serviço ausente)"}; resultado={r.Outcome}");
            if (!Vm.Elevated || before is null)
            {
                _out.WriteLine("[SysMain] RAMO: recusado (sem elevação ou serviço ausente)");
                Assert.Contains(r.Outcome, RefusedOutcomes);
                Assert.Equal(before, svc.GetStartType("SysMain"));
                return;
            }
            Assert.Contains(r.Outcome, AppliedOutcomes);
            Assert.Equal("Manual", svc.GetStartType("SysMain"));
            var revert = broker.Handle(Req(BrokerOperation.RevertRun, run: exec.RunId));
            Assert.True(revert.FullyRestored, string.Join("; ", revert.Results?.Select(x => x.Detail) ?? []));
            Assert.Equal(before, svc.GetStartType("SysMain"));
            _out.WriteLine(r.Outcome == "AlreadyConfigured" ? "[SysMain] RAMO: já estava em Manual (nada escrito)" : "[SysMain] RAMO: Manual aplicado e revertido");
        }
    }

    [Fact]
    public void Core_parking_ac_value_round_trips_in_the_active_scheme_when_elevated()
    {
        var power = new WindowsPowerBackend();
        var scheme = power.GetActiveScheme();
        var sub = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
        var setting = Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583");
        var before = power.ReadAcValue(scheme, sub, setting);
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Req(BrokerOperation.Execute, "power.processor.min-cores-parking@1"));
            Assert.True(exec.Ok, exec.Error?.Detail);
            var r = Assert.Single(exec.Results!);
            _out.WriteLine($"[core parking AC] elevado={Vm.Elevated}; valor antes={before}; resultado={r.Outcome}");
            if (!Vm.Elevated)
            {
                _out.WriteLine("[core parking AC] RAMO: bloqueado sem elevação");
                Assert.Equal("Blocked", r.Outcome);
                Assert.Equal(before, power.ReadAcValue(scheme, sub, setting));
                return;
            }
            Assert.Contains(r.Outcome, AppliedOutcomes);
            var revert = broker.Handle(Req(BrokerOperation.RevertRun, run: exec.RunId));
            Assert.True(revert.FullyRestored, string.Join("; ", revert.Results?.Select(x => x.Detail) ?? []));
            Assert.Equal(before, power.ReadAcValue(scheme, sub, setting));
            _out.WriteLine(r.Outcome == "AlreadyConfigured" ? "[core parking AC] RAMO: já estava no valor desejado (nada escrito)" : "[core parking AC] RAMO: aplicado e revertido");
        }
    }

    // ------------------------------------------------------------------ política de segurança com backends reais

    [Fact]
    public void Protected_targets_are_refused_even_inside_the_vm()
    {
        var h = new RegistryValueHandler(_reg);
        var forbidden = new ActionInstance(
            new ActionDefinition("vm.forbidden", 1, "x", Classification.SupportedDocumented, RegistryValueHandler.Kind, "x",
                [new EvidenceRef(EvidenceLevel.Hypothesis, "x")], "x", PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
            new Dictionary<string, string> { ["hive"] = "HKLM", ["path"] = @"SYSTEM\CurrentControlSet\Control\Lsa", ["name"] = "RunAsPPL", ["type"] = "DWord" },
            StateSnapshot.Of("DWord", "1"));
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(forbidden, new SystemContext(26100, "?", null, true)).Kind);
        Assert.NotNull(((IRestoreValidator)h).CheckRestore(forbidden, StateSnapshot.Of("DWord", "0")));
    }
}
