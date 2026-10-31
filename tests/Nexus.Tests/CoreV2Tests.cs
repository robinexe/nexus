using System.Text;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.Programs.Inventory;
using Nexus.Programs.Leftovers;
using Nexus.Security;
using Nexus.SystemTools.Appx;
using Nexus.Tests.Modules;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

/// <summary>Estado em memória por alvo "k". Assess bloqueia k=blocked; Apply lança em k=fail.</summary>
internal sealed class FakeStateHandler : IActionHandler
{
    public Dictionary<string, string?> State { get; } = new(StringComparer.Ordinal);
    public int Detects { get; private set; }
    public int Applies { get; private set; }
    public string HandlerKind => "Fake";
    private static string K(ActionInstance a) => a.Target["k"];
    public Applicability Assess(ActionInstance action, SystemContext context) =>
        K(action) == "blocked" ? Applicability.Blocked("bloqueado no teste") : Applicability.Ok;
    public StateSnapshot Detect(ActionInstance action)
    {
        Detects++;
        return State.TryGetValue(K(action), out var v) && v is not null ? StateSnapshot.Of("T", v) : StateSnapshot.Absent();
    }
    public void Apply(ActionInstance action)
    {
        Applies++;
        if (K(action) == "fail") throw new InvalidOperationException("falha simulada");
        State[K(action)] = action.Desired.Value;
    }
    public void Restore(ActionInstance action, StateSnapshot original) => State[K(action)] = original.Exists ? original.Value : null;
}

/// <summary>Ação sem estado: Detect e Restore nunca podem ser chamados.</summary>
internal sealed class FakeEphemeralHandler(string kind = "FakeEphemeral") : IActionHandler
{
    public int Runs { get; private set; }
    public bool Fail { get; set; }
    public string HandlerKind => kind;
    public Applicability Assess(ActionInstance action, SystemContext context) => Applicability.Ok;
    public StateSnapshot Detect(ActionInstance action) => throw new InvalidOperationException("Detect não deve ser chamado numa ação efêmera.");
    public void Apply(ActionInstance action)
    {
        if (Fail) throw new InvalidOperationException("o Windows recusou");
        Runs++;
    }
    public void Restore(ActionInstance action, StateSnapshot original) => throw new InvalidOperationException("Restore não deve ser chamado numa ação efêmera.");
}

/// <summary>RegistryValue que falha no Apply de um nome (falha de uma instância do lote).</summary>
internal sealed class FailingRegistryHandler(RegistryValueHandler inner, string failName) : IActionHandler
{
    public string HandlerKind => inner.HandlerKind;
    public Applicability Assess(ActionInstance action, SystemContext context) => inner.Assess(action, context);
    public StateSnapshot Detect(ActionInstance action) => inner.Detect(action);
    public void Apply(ActionInstance action)
    {
        if (action.Target["name"] == failName) throw new InvalidOperationException("falha simulada em " + failName);
        inner.Apply(action);
    }
    public void Restore(ActionInstance action, StateSnapshot original) => inner.Restore(action, original);
}

// ====================================================================== lote no núcleo

[Trait("Category", "Unit")]
public sealed class CoreBatchTests
{
    private sealed class SimulatedCrash(string at) : Exception($"crash simulado em {at}");

    private static ActionInstance A(string id, string k, string[]? dependsOn = null, string[]? conflicts = null,
        PersistenceKind persistence = PersistenceKind.Persistent) =>
        new(T.Def(id, "Fake", dependsOn: dependsOn, conflicts: conflicts, persistence: persistence),
            new Dictionary<string, string> { ["k"] = k }, StateSnapshot.Of("T", "1"));

    private static (FakeStateHandler H, HandlerRegistry Handlers, InMemoryJournal Journal, ActionExecutor Exec, DesiredStateResolver Resolver) Engine()
    {
        var h = new FakeStateHandler();
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        return (h, handlers, journal, new ActionExecutor(handlers, journal), new DesiredStateResolver(handlers));
    }

    [Fact]
    public void Same_action_with_different_targets_is_two_instances_and_exact_duplicate_is_refused()
    {
        var (_, _, _, _, resolver) = Engine();
        var plan = resolver.Resolve([A("x", "1"), A("x", "2")], T.Ctx);
        Assert.Equal(2, plan.Pending.Count());
        Assert.NotEqual(A("x", "1").InstanceKey, A("x", "2").InstanceKey);
        var ex = Assert.Throws<ArgumentException>(() => resolver.Resolve([A("x", "1"), A("x", "1")], T.Ctx));
        Assert.Contains("mesmo alvo", ex.Message, StringComparison.Ordinal);
        // Mesmo alvo com outro valor desejado também é duplicata (valores contraditórios).
        var other = A("x", "1") with { Desired = StateSnapshot.Of("T", "2") };
        Assert.Throws<ArgumentException>(() => resolver.Resolve([A("x", "1"), other], T.Ctx));
    }

    [Fact]
    public void Dependency_waits_for_every_instance_and_is_blocked_if_any_instance_is_blocked()
    {
        var (_, _, _, _, resolver) = Engine();
        // b vem antes no pedido, mas depende de a (duas instâncias): ordem a, a, b.
        var ordered = resolver.Resolve([A("b", "z", dependsOn: ["a"]), A("a", "1"), A("a", "2")], T.Ctx);
        Assert.Equal(["a", "a", "b"], ordered.Items.Select(i => i.Action.Definition.Id));
        Assert.All(ordered.Items, i => Assert.Equal(PlanStatus.Pending, i.Status));

        var blocked = resolver.Resolve([A("a", "1"), A("a", "blocked"), A("b", "z", dependsOn: ["a"])], T.Ctx);
        var b = blocked.Items.Single(i => i.Action.Definition.Id == "b");
        Assert.Equal(PlanStatus.Blocked, b.Status);
        Assert.Contains("Dependência não satisfeita: a", b.Reason, StringComparison.Ordinal);
        Assert.Equal(PlanStatus.Pending, blocked.Items.Single(i => i.Action.Target["k"] == "1").Status);
    }

    [Fact]
    public void Conflict_with_any_active_instance_blocks()
    {
        var (_, _, _, _, resolver) = Engine();
        var plan = resolver.Resolve([A("a", "1"), A("a", "2"), A("c", "9", conflicts: ["a"])], T.Ctx);
        Assert.Equal(PlanStatus.Blocked, plan.Items.Single(i => i.Action.Definition.Id == "c").Status);
        Assert.Equal(2, plan.Pending.Count());
    }

    [Fact]
    public void One_instance_fails_the_other_is_kept_dependents_blocked_and_RevertRun_undoes_per_instance()
    {
        var (h, _, journal, exec, resolver) = Engine();
        h.State["ok"] = "0";
        var run = exec.BeginRun("lote", "1");
        var results = exec.Execute(run, resolver.Resolve([A("a", "ok"), A("a", "fail"), A("b", "z", dependsOn: ["a"])], T.Ctx), T.Ctx);

        Assert.Equal(ExecutionOutcome.Verified, results.Single(r => r.Target == "k=ok").Outcome);
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, results.Single(r => r.Target == "k=fail").Outcome);
        var b = results.Single(r => r.ActionKey == "b@1");
        Assert.Equal(ExecutionOutcome.Blocked, b.Outcome); // dependência não concluída nesta execução
        Assert.Equal("1", h.State["ok"]);
        Assert.Equal(2, journal.Entries(run).Count);       // uma entrada por instância

        var undo = exec.RevertRun(run);
        Assert.Equal(2, undo.Count);
        Assert.Equal(RevertOutcome.Reverted, undo.Single(u => u.Target == "k=ok").Outcome);
        Assert.True(undo.All(u => u.Settled));
        Assert.Equal("0", h.State["ok"]);
    }

    [Fact]
    public void RevertRun_undoes_both_instances_in_reverse_order()
    {
        var (h, _, journal, exec, resolver) = Engine();
        var run = exec.BeginRun("lote", "1");
        Assert.All(exec.Execute(run, resolver.Resolve([A("a", "1"), A("a", "2")], T.Ctx), T.Ctx), r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));
        var undo = exec.RevertRun(run);
        Assert.Equal(["k=2", "k=1"], undo.Select(u => u.Target));
        Assert.All(undo, u => Assert.Equal(RevertOutcome.Reverted, u.Outcome));
        Assert.Null(h.State["1"]);
        Assert.Null(h.State["2"]);
        Assert.All(journal.Entries(run), e => Assert.Equal(JournalState.Reverted, e.State));
    }

    [Fact]
    public void Crash_in_the_middle_of_a_batch_is_recovered_per_instance()
    {
        var (h, handlers, journal, exec, resolver) = Engine();
        var run = journal.BeginRun("lote", "1");
        exec.FaultInjector = (where, a) => { if (where == "after-apply" && a.Target["k"] == "2") throw new SimulatedCrash("k=2"); };
        Assert.Throws<SimulatedCrash>(() => exec.Execute(run, resolver.Resolve([A("a", "1"), A("a", "2"), A("a", "3")], T.Ctx), T.Ctx));
        Assert.Equal("1", h.State["1"]);
        Assert.Equal("1", h.State["2"]);
        Assert.False(h.State.ContainsKey("3"));

        var after = new ActionExecutor(handlers, journal);
        var recovery = new RecoveryService(journal, after);
        var pending = Assert.Single(recovery.FindInterrupted());
        Assert.Equal("k=2", pending.Entry.Action.TargetLabel);
        var report = recovery.Recover();
        Assert.True(report.FullyRestored);
        Assert.Equal("k=2", Assert.Single(report.Results).Target);
        Assert.Null(h.State["2"]);
        Assert.Equal("1", h.State["1"]); // a instância concluída fica até o usuário desfazer a execução
        Assert.All(after.RevertRun(run), u => Assert.True(u.Settled));
        Assert.Null(h.State["1"]);
    }
}

// ====================================================================== lote no broker

[Trait("Category", "Unit")]
public sealed class BrokerBatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-batch-").FullName;
    private readonly InMemoryRegistryBackend _reg = new();
    private readonly InMemoryJournal _journal = new();
    private const string P = @"Software\NexusBatchTest";

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class OpenGate : IExecutionGate
    {
        public GateDecision Evaluate() => new(true, "teste: liberado");
    }

    private static string CatalogJson(string extra = "") => "{ \"catalogVersion\": \"b-1\", \"schema\": \"nexus.catalog/1\", \"actions\": [ " +
        "{ \"id\": \"t.param\", \"version\": 1, \"title\": \"Lote\", \"classification\": \"Experimental\", \"handlerKind\": \"RegistryValue\", " +
        "\"target\": { \"hive\": \"HKCU\", \"path\": \"" + P.Replace("\\", "\\\\", StringComparison.Ordinal) + "\", \"name\": \"{entry}\", \"type\": \"DWord\" }, \"desiredValue\": 1, " +
        "\"userParameters\": { \"entry\": \"Entrada[0-9]{1,3}\" }, \"supersedes\": [\"t.old.01@1\", \"t.old.02@1\"], " +
        "\"privileges\": \"User\", \"persistence\": \"Persistent\", \"reboot\": \"None\" }" + extra + " ] }";

    private BrokerService Broker(IActionHandler? handler = null, string extra = "", Func<string?>? beforeSystemChange = null)
    {
        var handlers = new HandlerRegistry().Add(handler ?? new RegistryValueHandler(_reg));
        if (extra.Length > 0) handlers.Add(new FakeEphemeralHandler());
        return new BrokerService(CatalogLoader.Parse(CatalogJson(extra)), handlers, _journal, new FileRunCoordinator(_dir), new OpenGate(),
            () => T.Ctx, () => new Dictionary<string, string>()) { BeforeSystemChange = beforeSystemChange };
    }

    private static ActionRequest Req(string entry, string id = "t.param@1") => new(id, null, new Dictionary<string, string> { ["entry"] = entry });

    private string? Value(string name) => _reg.Read(T.RT(P, name)).Value;

    [Fact]
    public void Exact_duplicate_in_a_request_is_refused_before_anything_is_written()
    {
        using var svc = Broker();
        var r = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [Req("Entrada1"), Req("Entrada2"), Req("Entrada1")]));
        Assert.False(r.Ok);
        Assert.Equal("invalid-request", r.Error!.Code);
        Assert.Contains("mesmo alvo", r.Error.Detail, StringComparison.Ordinal);
        Assert.Null(Value("Entrada1"));
        Assert.Empty(_journal.Runs());
    }

    [Fact]
    public void Two_instances_are_applied_mapped_to_the_request_and_reverted_together()
    {
        using var svc = Broker();
        var r = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [Req("Entrada1"), Req("Entrada2")]));
        Assert.True(r.Ok, r.Error?.Detail);
        Assert.Equal(2, r.Results!.Count);
        Assert.All(r.Results, x => Assert.Equal("Verified", x.Outcome));
        Assert.Equal([0, 1], r.Results.Select(x => x.Index!.Value).Order());
        Assert.Contains("name=Entrada2", r.Results.Single(x => x.Index == 1).Target, StringComparison.Ordinal);
        Assert.Equal("1", Value("Entrada1"));
        Assert.Equal("1", Value("Entrada2"));

        var plan = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Plan, [Req("Entrada3"), Req("Entrada1")]));
        Assert.Equal("Pending", plan.Results!.Single(x => x.Index == 0).Outcome);
        Assert.Equal("AlreadyConfigured", plan.Results!.Single(x => x.Index == 1).Outcome);

        var undo = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: r.RunId));
        Assert.True(undo.FullyRestored);
        Assert.Equal(2, undo.Results!.Count);
        Assert.Null(Value("Entrada1"));
        Assert.Null(Value("Entrada2"));
    }

    [Fact]
    public void One_instance_fails_and_the_other_is_kept_with_its_own_undo()
    {
        using var svc = Broker(new FailingRegistryHandler(new RegistryValueHandler(_reg), "Entrada2"));
        var r = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [Req("Entrada1"), Req("Entrada2")]));
        Assert.True(r.Ok, r.Error?.Detail);
        Assert.Equal("Verified", r.Results!.Single(x => x.Index == 0).Outcome);
        Assert.Equal("FailedAndCompensated", r.Results!.Single(x => x.Index == 1).Outcome);
        Assert.Equal("1", Value("Entrada1"));
        Assert.Null(Value("Entrada2"));
        var undo = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: r.RunId));
        Assert.True(undo.FullyRestored);
        Assert.Null(Value("Entrada1"));
    }

    [Fact]
    public void Full_batch_of_64_with_long_details_fits_in_a_frame()
    {
        var items = Enumerable.Range(0, BrokerService.MaxActionsPerRequest)
            .Select(i => new ActionResultDto("t.param@1", "Failed", new string('é', 1024), new string('x', 1024), new string('y', 1024), Index: i, Target: new string('z', 260)))
            .ToList();
        var capped = BrokerService.Cap(items);
        Assert.Equal(BrokerService.MaxActionsPerRequest, capped.Count);
        var frame = BrokerWire.Encode(new BrokerResponse(Guid.NewGuid(), true, Results: capped, RunId: Guid.NewGuid(), Message: new string('m', 300)));
        Assert.True(frame.Length <= BrokerWire.MaxFrameBytes + 4);
        Assert.Equal(Enumerable.Range(0, 64), capped.Select(c => c.Index!.Value));
    }

    // ---------------------------------------------------------------- chaves antigas (supersedes)

    [Fact]
    public void Old_journal_key_listed_in_supersedes_is_reverted_and_cannot_be_requested_again()
    {
        // Execução "antiga", gravada com a chave t.old.01@1 e o mesmo alvo que t.param produz.
        var entry = CatalogLoader.Parse(CatalogJson()).Actions.Single();
        var inst = entry.TryInstantiate(new Dictionary<string, string> { ["entry"] = "Entrada5" }, out _)!;
        var legacy = inst with { Definition = inst.Definition with { Id = "t.old.01" } };
        var raw = new HandlerRegistry().Add(new RegistryValueHandler(_reg));
        var old = new ActionExecutor(raw, _journal);
        var run = old.BeginRun("antigo", "a-0");
        Assert.Equal(ExecutionOutcome.Verified, old.Execute(run, new DesiredStateResolver(raw).Resolve([legacy], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal("1", Value("Entrada5"));

        using var svc = Broker();
        var undo = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: run));
        Assert.True(undo.FullyRestored, string.Join("; ", undo.Results?.Select(x => x.Detail) ?? []));
        Assert.Null(Value("Entrada5"));

        var again = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [Req("Entrada5", "t.old.01@1")]));
        Assert.False(again.Ok);
        Assert.Equal("unknown-action", again.Error!.Code);
    }

    [Fact]
    public void Old_key_not_in_supersedes_or_with_another_target_is_refused_on_restore()
    {
        var catalog = CatalogLoader.Parse(CatalogJson());
        var entry = catalog.Actions.Single();
        var inst = entry.TryInstantiate(new Dictionary<string, string> { ["entry"] = "Entrada5" }, out _)!;
        var bound = new CatalogBoundHandler(new RegistryValueHandler(_reg), catalog, () => new Dictionary<string, string>());
        Assert.Null(bound.CheckRestore(inst with { Definition = inst.Definition with { Id = "t.old.02" } }, StateSnapshot.Absent()));
        Assert.NotNull(bound.CheckRestore(inst with { Definition = inst.Definition with { Id = "t.old.03" } }, StateSnapshot.Absent()));
        var otherTarget = inst with
        {
            Definition = inst.Definition with { Id = "t.old.01" },
            Target = new Dictionary<string, string>(inst.Target) { ["path"] = @"Software\Outro" },
        };
        Assert.NotNull(bound.CheckRestore(otherTarget, StateSnapshot.Absent()));
    }

    [Fact]
    public void Catalog_refuses_supersedes_of_a_live_key_shared_old_keys_and_bad_format()
    {
        static string Two(string a, string b) => "{ \"catalogVersion\": \"s\", \"schema\": \"nexus.catalog/1\", \"actions\": [ " +
            "{ \"id\": \"t.a\", \"handlerKind\": \"RegistryValue\", \"supersedes\": " + a + " }, " +
            "{ \"id\": \"t.b\", \"handlerKind\": \"RegistryValue\", \"supersedes\": " + b + " } ] }";
        Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(Two("[\"t.b@1\"]", "[]")));           // ainda é ação viva
        Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(Two("[\"t.x@1\"]", "[\"t.x@1\"]"))); // duas donas
        Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(Two("[\"t.x\"]", "[]")));            // sem versão
        Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(Two("[\"t.a@1\"]", "[]")));          // a si mesma
        Assert.Equal(["t.x@1"], CatalogLoader.Parse(Two("[\"t.x@1\"]", "[]")).Actions[0].Supersedes);
    }

    // ---------------------------------------------------------------- CheckRestore confere a definição do diário

    [Fact]
    public void Restore_is_refused_when_journal_definition_differs_from_catalog_in_safety_properties()
    {
        var catalog = CatalogLoader.Parse(CatalogJson());
        var inst = catalog.Actions.Single().TryInstantiate(new Dictionary<string, string> { ["entry"] = "Entrada9" }, out _)!;
        var bound = new CatalogBoundHandler(new RegistryValueHandler(_reg), catalog, () => new Dictionary<string, string>());
        Assert.Null(bound.CheckRestore(inst, StateSnapshot.Absent()));
        Assert.Contains("irreversível", bound.CheckRestore(inst with { Definition = inst.Definition with { Irreversible = true } }, StateSnapshot.Absent()), StringComparison.Ordinal);
        Assert.Contains("Persistência", bound.CheckRestore(inst with { Definition = inst.Definition with { Persistence = PersistenceKind.Session } }, StateSnapshot.Absent()), StringComparison.Ordinal);
        Assert.Contains("Privilégio", bound.CheckRestore(inst with { Definition = inst.Definition with { Privileges = PrivilegeLevel.Admin } }, StateSnapshot.Absent()), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- efêmeras no broker

    private const string EphemeralAdmin = ", { \"id\": \"t.eph\", \"version\": 1, \"title\": \"Sem estado\", \"classification\": \"SupportedDocumented\", " +
        "\"handlerKind\": \"FakeEphemeral\", \"target\": { \"cache\": \"x\" }, \"desiredValue\": \"executado\", \"desiredType\": \"Ephemeral\", " +
        "\"privileges\": \"Admin\", \"persistence\": \"Ephemeral\", \"reboot\": \"None\" }" +
        ", { \"id\": \"t.adm\", \"version\": 1, \"title\": \"Admin persistente\", \"classification\": \"SupportedDocumented\", \"handlerKind\": \"RegistryValue\", " +
        "\"target\": { \"hive\": \"HKLM\", \"path\": \"SOFTWARE\\\\NexusBatchTest\", \"name\": \"A\", \"type\": \"DWord\" }, \"desiredValue\": 1, " +
        "\"privileges\": \"Admin\", \"persistence\": \"Persistent\", \"reboot\": \"None\" }";

    [Fact]
    public void Ephemeral_admin_action_does_not_create_a_restore_point_nor_hold_a_session_and_undo_says_nothing_to_undo()
    {
        var points = 0;
        using var svc = Broker(extra: EphemeralAdmin, beforeSystemChange: () => { points++; return "ponto criado"; });
        var r = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest("t.eph@1")]));
        Assert.True(r.Ok, r.Error?.Detail);
        Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
        Assert.Equal(0, points);
        Assert.False(r.SessionActive);
        Assert.False(svc.HasActiveSession);

        var undo = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: r.RunId));
        Assert.True(undo.FullyRestored);
        var u = Assert.Single(undo.Results!);
        Assert.Equal("NothingToRevert", u.Outcome);
        Assert.Equal(ActionExecutor.EphemeralNothingToUndo, u.Detail);

        // Persistente de administrador no mesmo pedido: o ponto é criado (uma vez).
        var both = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest("t.eph@1"), new ActionRequest("t.adm@1")]));
        Assert.True(both.Ok, both.Error?.Detail);
        Assert.Equal(1, points);
    }
}

// ====================================================================== ações efêmeras no motor

[Trait("Category", "Unit")]
public sealed class EphemeralTests
{
    private sealed class SimulatedCrash(string at) : Exception($"crash simulado em {at}");

    private static ActionInstance Eph(string id = "e") =>
        new(T.Def(id, "FakeEphemeral", PrivilegeLevel.Admin, persistence: PersistenceKind.Ephemeral),
            new Dictionary<string, string> { ["cache"] = "dns" }, StateSnapshot.Of("Ephemeral", "executado"));

    [Fact]
    public void Ephemeral_runs_without_detect_or_backup_and_undo_is_explicit()
    {
        var h = new FakeEphemeralHandler();
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = exec.BeginRun("e", "1");
        var plan = new DesiredStateResolver(handlers).Resolve([Eph()], T.Ctx); // Detect lançaria se fosse chamado
        Assert.Equal(PlanStatus.Pending, Assert.Single(plan.Items).Status);
        var r = Assert.Single(exec.Execute(run, plan, T.Ctx));
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.Equal(1, h.Runs);
        var e = Assert.Single(journal.Entries(run));
        Assert.Null(e.Original);
        Assert.Equal(JournalState.Verified, e.State);

        var undo = exec.Revert(e.Id); // Restore lançaria se fosse chamado
        Assert.Equal(RevertOutcome.NothingToRevert, undo.Outcome);
        Assert.Equal(ActionExecutor.EphemeralNothingToUndo, undo.Detail);
        Assert.Equal(JournalState.Accepted, journal.Get(e.Id).State);
        Assert.Equal(RevertOutcome.NothingToRevert, exec.Revert(e.Id).Outcome); // de novo: mesma resposta, sem erro
        Assert.Empty(RecoveryService.FindInterrupted(journal));
    }

    [Theory]
    [InlineData("after-backup", JournalState.Reverted)]
    [InlineData("after-apply", JournalState.Accepted)]
    public void Interrupted_ephemeral_is_closed_by_recovery_without_calling_the_handler(string point, JournalState closedAs)
    {
        var h = new FakeEphemeralHandler();
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal) { FaultInjector = (where, _) => { if (where == point) throw new SimulatedCrash(where); } };
        var run = journal.BeginRun("e", "1");
        Assert.Throws<SimulatedCrash>(() => exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Eph()], T.Ctx), T.Ctx));

        var recovery = new RecoveryService(journal, new ActionExecutor(handlers, journal));
        Assert.Single(recovery.FindInterrupted());
        var report = recovery.Recover();
        Assert.True(report.FullyRestored);
        Assert.Equal(RevertOutcome.NothingToRevert, Assert.Single(report.Results).Outcome);
        Assert.Equal(closedAs, Assert.Single(journal.Entries(run)).State);
        Assert.Empty(recovery.FindInterrupted());
    }

    [Fact]
    public void Ephemeral_failure_is_failed_without_pending_compensation()
    {
        var h = new FakeEphemeralHandler { Fail = true };
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = exec.BeginRun("e", "1");
        var r = Assert.Single(exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Eph()], T.Ctx), T.Ctx));
        Assert.Equal(ExecutionOutcome.Failed, r.Outcome);
        Assert.Contains("recusou", r.Detail, StringComparison.Ordinal);
        Assert.Empty(RecoveryService.FindInterrupted(journal));
    }

    [Fact]
    public void Catalog_refuses_irreversible_ephemeral()
    {
        const string json = "{ \"catalogVersion\": \"e\", \"schema\": \"nexus.catalog/1\", \"actions\": [ { \"id\": \"t.e\", \"handlerKind\": \"X\", " +
                            "\"persistence\": \"Ephemeral\", \"irreversible\": true } ] }";
        Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(json));
        Assert.Equal(PersistenceKind.Ephemeral, CatalogLoader.Parse(json.Replace("true", "false", StringComparison.Ordinal)).Actions[0].Definition.Persistence);
    }
}

// ====================================================================== escopo do perfil

[Trait("Category", "Unit")]
public sealed class UserScopeTests
{
    private static ActionInstance With(string key, string value) =>
        new(T.Def("t", "Qualquer"), new Dictionary<string, string> { [key] = value }, StateSnapshot.Of("T", "1"));

    [Theory]
    [InlineData("scope", "User", true)]
    [InlineData("scope", "user", true)]
    [InlineData("scope", "HKCU", true)]   // diários antigos continuam iguais
    [InlineData("hive", "HKCU", true)]
    [InlineData("scope", "HKLM", false)]
    [InlineData("scope", "Machine", false)]
    [InlineData("hive", "User", false)]   // "User" só vale como escopo, não como hive de registro
    public void User_scope_is_recognized(string key, string value, bool expected) =>
        Assert.Equal(expected, UserProfileScope.IsUserScoped(With(key, value)));
}

// ====================================================================== programas: diário das vagas antigas

[Trait("Category", "Unit")]
public sealed class ProgramsLegacySlotsTests
{
    private sealed class SimulatedCrash(string at) : Exception($"crash simulado em {at}");

    private sealed class Roots(TempTree t) : ILeftoverRootsProvider
    {
        public IReadOnlyList<string> Machine() => t.Roots.Machine;
        public IReadOnlyList<string>? User(string? userSid) => t.Roots.User;
    }

    private static CatalogDocument Load() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private static ActionInstance LegacySlot(CatalogDocument catalog, string slotId, string path)
    {
        var entry = catalog.Actions.Single(a => a.Definition.Id == "programs.leftover.user");
        var inst = entry.TryInstantiate(new Dictionary<string, string> { ["kind"] = "registry", ["path"] = path }, out var reason);
        Assert.True(inst is not null, reason);
        return inst! with { Definition = inst!.Definition with { Id = slotId, Title = "Remover sobra de programa (seu perfil) — vaga" } };
    }

    [Fact]
    public void Run_made_with_the_old_slots_is_reverted_and_recovered_through_the_new_action()
    {
        using var t = new TempTree();
        var catalog = Load();
        var reg = new InMemoryRegistryTreeBackend();
        reg.SetValue(RegHive.HKCU, @"Software\Zedsoft\Zed Paint", "a", "DWord", "7");
        reg.SetValue(RegHive.HKCU, @"Software\Zedsoft\Zed Draw", "b", "String", "x");
        var inner = new ProgramLeftoverHandler(reg, new Roots(t), _ => Array.Empty<InstalledProgram>(), t.Data, elevated: true, otherAccount: false);
        var bound = CatalogBoundHandler.Wrap(new HandlerRegistry().Add(inner), [ProgramLeftoverHandler.Kind], catalog, () => new Dictionary<string, string>());
        var journal = new InMemoryJournal();

        // Execução antiga: vaga 03 concluída, vaga 04 interrompida depois do Apply (queda).
        var old = new ActionExecutor(bound, journal)
        {
            FaultInjector = (where, a) => { if (where == "after-apply" && a.Definition.Id == "programs.leftover.user.04") throw new SimulatedCrash("vaga 04"); },
        };
        var run = journal.BeginRun("antigo", "1.0.0");
        var plan = new DesiredStateResolver(bound).Resolve(
            [LegacySlot(catalog, "programs.leftover.user.03", @"Software\Zedsoft\Zed Paint"), LegacySlot(catalog, "programs.leftover.user.04", @"Software\Zedsoft\Zed Draw")], T.Ctx);
        Assert.Throws<SimulatedCrash>(() => old.Execute(run, plan, T.Ctx));
        Assert.False(reg.KeyExists(RegHive.HKCU, @"Software\Zedsoft\Zed Paint", null));
        Assert.False(reg.KeyExists(RegHive.HKCU, @"Software\Zedsoft\Zed Draw", null));
        Assert.Equal("programs.leftover.user.03@1", journal.Entries(run)[0].Action.Definition.Key);

        // Hoje: catálogo novo (sem as vagas), mesma validação pelo catálogo.
        var now = new ActionExecutor(bound, journal);
        var report = new RecoveryService(journal, now).Recover();
        Assert.True(report.FullyRestored, string.Join("; ", report.Results.Select(r => r.Detail)));
        Assert.True(reg.KeyExists(RegHive.HKCU, @"Software\Zedsoft\Zed Draw", null));
        Assert.All(now.RevertRun(run), r => Assert.True(r.Settled, r.Detail));
        Assert.Equal(("DWord", "7"), reg.GetValue(RegHive.HKCU, @"Software\Zedsoft\Zed Paint", "a"));
    }

    [Fact]
    public void Old_slot_number_outside_supersedes_is_refused()
    {
        using var t = new TempTree();
        var catalog = Load();
        var inner = new ProgramLeftoverHandler(new InMemoryRegistryTreeBackend(), new Roots(t), _ => Array.Empty<InstalledProgram>(), t.Data, true, false);
        var bound = new CatalogBoundHandler(inner, catalog, () => new Dictionary<string, string>());
        Assert.Null(bound.CheckRestore(LegacySlot(catalog, "programs.leftover.user.16", @"Software\Zedsoft\Zed Paint"), StateSnapshot.Absent()));
        Assert.NotNull(bound.CheckRestore(LegacySlot(catalog, "programs.leftover.user.17", @"Software\Zedsoft\Zed Paint"), StateSnapshot.Absent()));
        // Vaga de usuário com o alvo do escopo da máquina: o alvo reconstruído difere.
        var forged = LegacySlot(catalog, "programs.leftover.user.01", @"Software\Zedsoft\Zed Paint");
        forged = forged with { Target = new Dictionary<string, string>(forged.Target) { ["scope"] = "HKLM" } };
        Assert.NotNull(bound.CheckRestore(forged, StateSnapshot.Absent()));
    }
}

// ====================================================================== SafeFileMover

/// <summary>
/// Em Linux, o move é por caminho com conferência dupla da cadeia (simula a troca por symlink). No Windows (VM do CI), o mesmo
/// teste roda contra a implementação por handle: a troca no ponto de interposição é recusada pelo próprio Windows (a pasta-mãe
/// está aberta sem FILE_SHARE_DELETE) ou detectada pela conferência final. Só valida no Windows: o rename relativo ao handle
/// (FileRenameInfoEx) e a conferência por GetFinalPathNameByHandle — ver também tests/Nexus.IntegrationTests (SafeFileMover).
/// </summary>
[Trait("Category", "Unit")]
public sealed class SafeFileMoverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nexus-mover-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static bool TrySymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // Windows sem privilégio de symlink
    }

    [Fact]
    public void Moves_a_folder_and_a_file_on_the_same_volume()
    {
        var src = Dir("app", "Acme");
        File.WriteAllText(Path.Combine(src, "a.txt"), "1");
        var q = Dir("q");
        SafeFileMover.Move(src, Path.Combine(q, "payload"));
        Assert.False(Directory.Exists(src));
        Assert.Equal("1", File.ReadAllText(Path.Combine(q, "payload", "a.txt")));

        var file = Path.Combine(Dir("app"), "b.ini");
        File.WriteAllText(file, "2");
        SafeFileMover.Move(file, Path.Combine(q, "b.ini"));
        Assert.Equal("2", File.ReadAllText(Path.Combine(q, "b.ini")));
    }

    [Fact]
    public void Refuses_existing_destination_same_path_and_destination_inside_source()
    {
        var src = Dir("app", "Acme");
        var q = Dir("q", "payload");
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(src, q));
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(src, src));
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(src, Path.Combine(src, "x")));
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move("relativo", Path.Combine(_root, "x")));
        Assert.True(Directory.Exists(src));
    }

    [Fact]
    public void Refuses_a_link_as_source_or_in_the_parent_chain()
    {
        var victim = Dir("victim");
        File.WriteAllText(Path.Combine(victim, "important.txt"), "não mover");
        var app = Dir("app");
        var q = Dir("q");
        if (!TrySymlink(Path.Combine(app, "Acme"), victim)) return;
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(Path.Combine(app, "Acme"), Path.Combine(q, "p1")));
        // Pasta-mãe da origem é link: AppData\Local\Junction\important.txt.
        Assert.Throws<SafeMoveRefusedException>(() => SafeFileMover.Move(Path.Combine(app, "Acme", "important.txt"), Path.Combine(q, "p2")));
        Assert.Equal("não mover", File.ReadAllText(Path.Combine(victim, "important.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(q));
    }

    [Fact]
    public void Swap_of_the_parent_for_a_link_between_check_and_move_is_refused_or_blocked()
    {
        var victim = Dir("victim");
        File.WriteAllText(Path.Combine(victim, "Acme"), "arquivo da vítima");
        var parent = Dir("Local");
        var src = Path.Combine(parent, "Acme");
        File.WriteAllText(src, "sobra");
        var q = Dir("q");
        var swapped = false;
        var blockedByOs = false;

        void Swap(SafeMoveStage _)
        {
            try
            {
                Directory.Move(parent, Path.Combine(_root, "Local.real"));
                swapped = TrySymlink(parent, victim);
                if (!swapped) Directory.Move(Path.Combine(_root, "Local.real"), parent);
            }
            catch (IOException) { blockedByOs = true; }            // Windows: pasta-mãe aberta sem FILE_SHARE_DELETE
            catch (UnauthorizedAccessException) { blockedByOs = true; }
        }

        try
        {
            SafeFileMover.Move(src, Path.Combine(q, "payload"), Swap);
            // Só pode chegar aqui se a troca não aconteceu: o move pegou a sobra verdadeira.
            Assert.False(swapped);
            Assert.True(blockedByOs || !SafeFileMover.IsHandleRelative);
            Assert.Equal("sobra", File.ReadAllText(Path.Combine(q, "payload")));
        }
        catch (SafeMoveRefusedException)
        {
            Assert.True(swapped);
            Assert.False(File.Exists(Path.Combine(q, "payload")));
        }
        Assert.Equal("arquivo da vítima", File.ReadAllText(Path.Combine(victim, "Acme"))); // nunca tocado
    }

    [Fact]
    public void Leftover_quarantine_uses_the_safe_mover_and_refuses_the_swap()
    {
        using var t = new TempTree();
        var victim = TempTree.Dir(t.Root, "victim");
        File.WriteAllText(Path.Combine(victim, "Zed Paint"), "da vítima");
        var parent = Path.Combine(t.Local, "Zedsoft");
        var folder = TempTree.Dir(parent, "Zed Paint");
        File.WriteAllText(Path.Combine(folder, "cache.bin"), "cache");
        var swapped = false;
        var q = new LeftoverQuarantine(t.Data)
        {
            Interpose = _ =>
            {
                try
                {
                    Directory.Move(parent, parent + ".real");
                    swapped = TrySymlink(parent, victim);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            },
        };
        var target = new LeftoverTarget(false, false, folder, null);
        try
        {
            q.QuarantinePath(target);
            Assert.False(swapped); // o sistema impediu a troca: a sobra verdadeira foi para a quarentena
            Assert.True(q.PayloadExists(target));
        }
        catch (SafeMoveRefusedException)
        {
            Assert.True(swapped);
            Assert.False(q.PayloadExists(target));
        }
        Assert.Equal("da vítima", File.ReadAllText(Path.Combine(victim, "Zed Paint")));
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\ana\AppData", @"C:\Users\ana\AppData")]
    [InlineData(@"\\?\UNC\srv\share\x", @"\\srv\share\x")]
    [InlineData(@"C:\x", @"C:\x")]
    public void Final_path_prefix_is_stripped(string final, string expected) =>
        Assert.Equal(expected, SafeFileMover.StripPrefix(final));
}

// ====================================================================== itens baixos da revisão

[Trait("Category", "Unit")]
public sealed class ReviewLowItemsTests
{
    [Fact]
    public void Lock_file_rule_refuses_any_access_for_unprivileged_accounts()
    {
        const string users = AdminOnlyDirectory.UsersSid;
        Assert.Null(AdminOnlyDirectory.EvaluateLock(AdminOnlyDirectory.AdministratorsSid,
            [new AccessEntry(AdminOnlyDirectory.LocalSystemSid, 0x1F01FF, true), new AccessEntry(AdminOnlyDirectory.AdministratorsSid, 0x1F01FF, true)]));
        Assert.NotNull(AdminOnlyDirectory.EvaluateLock(AdminOnlyDirectory.AdministratorsSid, [new AccessEntry(users, 0x120089, true)])); // só leitura já basta para segurar
        Assert.NotNull(AdminOnlyDirectory.EvaluateLock("S-1-5-21-1-2-3-1001", []));                                                    // dono comum
        Assert.Null(AdminOnlyDirectory.EvaluateLock(AdminOnlyDirectory.LocalSystemSid, [new AccessEntry(users, 0x120089, false)]));     // negação não abre
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.BingNews_4.55.62231.0_x64__8wekyb3d8bbwe", true)]
    [InlineData(@"D:\WindowsApps\Microsoft.ZuneMusic_11.2.0.0_x64__8wekyb3d8bbwe", true)]
    [InlineData(@"C:\Windows\SystemApps\Microsoft.Windows.Search_cw5n1h2txyewy", true)]
    [InlineData(@"C:\Users\x\WindowsApps\evil", false)]                         // antes: bastava conter "\WindowsApps\"
    [InlineData(@"C:\Users\x\Downloads\SystemApps\evil", false)]
    [InlineData(@"C:\Program Files\WindowsApps\..\..\evil", false)]
    [InlineData(@"C:\Program Files\WindowsApps\pkg\sub", false)]
    [InlineData(@"C:\Program Files\WindowsApps\a'b", false)]
    [InlineData(@"C:\Program Files\WindowsApps\", false)]
    [InlineData(@"\\srv\WindowsApps\pkg", false)]
    public void Windows_apps_path_is_strict(string path, bool ok) => Assert.Equal(ok, AppxPackageHandler.IsWindowsAppsPath(path));

    [Fact]
    public void Hosts_restore_without_the_written_record_is_refused_instead_of_overwriting()
    {
        using var tmp = new TempDir();
        var hosts = new FakeHosts { Content = Encoding.UTF8.GetBytes("127.0.0.1 localhost\r\n0.0.0.0 update.microsoft.com\r\n") };
        var handler = new HostsFileHandler(hosts, tmp.Path);
        var action = new ActionInstance(T.Def("security.hosts.disable-hijack", HostsFileHandler.Kind, PrivilegeLevel.Admin),
            new Dictionary<string, string> { ["file"] = "hosts" }, StateSnapshot.Of(HostsFileHandler.StateType, HostsFileHandler.Clean));
        var original = handler.Detect(action);
        handler.Apply(action);
        foreach (var f in Directory.EnumerateFiles(tmp.Path, "*.written", SearchOption.AllDirectories)) File.Delete(f);
        var written = hosts.Content!.ToArray();

        Assert.Contains(".written", handler.CheckRestore(action, original), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => handler.Restore(action, original));
        Assert.Equal(written, hosts.Content);
    }
}
