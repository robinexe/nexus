using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

/// <summary>Falhas simuladas exigidas pela seção 14 da extensão.</summary>
[Trait("Category", "Unit")]
public class RecoveryTests
{
    private const string P = @"Software\NexusTest";

    private sealed class SimulatedCrash(string at) : Exception($"crash simulado em {at}");

    private static (InMemoryRegistryBackend reg, InMemoryJournal journal, ActionExecutor exec, Guid run) CrashAt(string point, uint? preexisting = null)
    {
        var (reg, handlers, journal, exec, resolver) = T.Engine();
        if (preexisting is { } v) reg.Seed(T.RT(P, "V"), "DWord", v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var run = journal.BeginRun("p", "1");
        var plan = resolver.Resolve([T.RegAction("a", P, "V", 1)], T.Ctx);
        exec.FaultInjector = (where, _) => { if (where == point) throw new SimulatedCrash(where); };
        Assert.Throws<SimulatedCrash>(() => exec.Execute(run, plan, T.Ctx));
        // "Reinício": novo executor sem injetor, mesmo diário e mesmo sistema.
        return (reg, journal, new ActionExecutor(handlers, journal), run);
    }

    [Fact]
    public void Crash_between_backup_and_apply_is_detected_and_resolved_without_writing()
    {
        var (reg, journal, exec, run) = CrashAt("after-backup", preexisting: 0);
        var recovery = new RecoveryService(journal, exec);

        var finding = Assert.Single(recovery.FindInterrupted());
        Assert.Equal(JournalState.BackedUp, finding.Entry.State);

        var report = recovery.Recover();
        Assert.Equal(RevertOutcome.AlreadyOriginal, Assert.Single(report.Results).Outcome);
        Assert.True(report.FullyRestored);
        Assert.Equal("0", reg.Read(T.RT(P, "V")).Value);
    }

    [Fact]
    public void Interrupted_before_apply_never_overwrites_a_value_nexus_did_not_write()
    {
        var (reg, journal, exec, _) = CrashAt("after-backup", preexisting: 0);
        reg.Write(T.RT(P, "V"), "DWord", "1"); // outro software definiu o mesmo valor desejado

        var report = new RecoveryService(journal, exec).Recover(forceConflicts: true);

        Assert.Equal(RevertOutcome.NothingToRevert, Assert.Single(report.Results).Outcome);
        Assert.Equal("1", reg.Read(T.RT(P, "V")).Value);
    }

    [Fact]
    public void Conflicts_remain_visible_to_recovery_until_resolved()
    {
        var (reg, journal, exec, _) = CrashAt("after-apply");
        reg.Write(T.RT(P, "V"), "DWord", "42");
        var recovery = new RecoveryService(journal, exec);
        Assert.False(recovery.Recover().FullyRestored);

        Assert.Single(recovery.FindInterrupted()); // continua listado como conflito pendente
        var forced = recovery.Recover(forceConflicts: true);
        Assert.True(forced.FullyRestored);
        Assert.False(reg.Read(T.RT(P, "V")).ValueExists);
    }

    [Fact]
    public void Crash_after_apply_before_verification_restores_original()
    {
        var (reg, journal, exec, _) = CrashAt("after-apply", preexisting: 0);
        Assert.Equal("1", reg.Read(T.RT(P, "V")).Value);

        var report = new RecoveryService(journal, exec).Recover();

        Assert.Equal(RevertOutcome.Reverted, Assert.Single(report.Results).Outcome);
        Assert.Equal("0", reg.Read(T.RT(P, "V")).Value);
    }

    [Fact]
    public void External_change_before_rollback_yields_partial_recovery_not_false_success()
    {
        var (reg, journal, exec, _) = CrashAt("after-apply");
        reg.Write(T.RT(P, "V"), "DWord", "42");

        var report = new RecoveryService(journal, exec).Recover();

        Assert.Equal(RevertOutcome.Conflict, Assert.Single(report.Results).Outcome);
        Assert.False(report.FullyRestored);
        Assert.Equal("42", reg.Read(T.RT(P, "V")).Value);
    }

    [Fact]
    public void Failure_in_middle_of_sequence_keeps_completed_items_revertible()
    {
        var (reg, handlers, journal, _, resolver) = T.Engine();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("p", "1");
        var plan = resolver.Resolve([T.RegAction("a", P, "A", 1), T.RegAction("b", P, "B", 1), T.RegAction("c", P, "C", 1)], T.Ctx);
        exec.FaultInjector = (where, a) => { if (where == "after-apply" && a.Definition.Id == "b") throw new SimulatedCrash("b"); };
        Assert.Throws<SimulatedCrash>(() => exec.Execute(run, plan, T.Ctx));

        var states = journal.Entries(run).Select(e => (e.Action.Definition.Id, e.State)).ToList();
        Assert.Equal(new[] { ("a", JournalState.Verified), ("b", JournalState.Applying) }, states.ToArray());

        var results = new ActionExecutor(handlers, journal).RevertRun(run);
        Assert.All(results, r => Assert.Equal(RevertOutcome.Reverted, r.Outcome));
        Assert.False(reg.Read(T.RT(P, "A")).ValueExists);
        Assert.False(reg.Read(T.RT(P, "B")).ValueExists);
        Assert.False(reg.Read(T.RT(P, "C")).ValueExists);
    }

    [Fact]
    public void Session_actions_of_unfinished_runs_are_recovered_after_reboot()
    {
        var (reg, handlers, journal, exec, resolver) = T.Engine();
        var run = journal.BeginRun("partida", "1");
        exec.Execute(run, resolver.Resolve([T.RegAction("s", P, "S", 1, persistence: PersistenceKind.Session)], T.Ctx), T.Ctx);
        // Nenhum EndRun: o PC reiniciou durante a partida.

        var report = new RecoveryService(journal, new ActionExecutor(handlers, journal)).Recover();

        Assert.Equal(RevertOutcome.Reverted, Assert.Single(report.Results).Outcome);
        Assert.False(reg.Read(T.RT(P, "S")).ValueExists);
    }

    [Fact]
    public void Sqlite_journal_survives_reopen_and_enforces_transitions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-{Guid.NewGuid():N}.db");
        try
        {
            long id;
            Guid run;
            var a = T.RegAction("a", P, "V", 1);
            using (var j = new SqliteJournal(path))
            {
                run = j.BeginRun("p", "1.0.0");
                id = j.AddPlanned(run, 0, a);
                j.Transition(id, JournalState.BackedUp, original: StateSnapshot.Absent(new Dictionary<string, string> { ["keyExisted"] = "false" }));
                j.Transition(id, JournalState.Applying);
                Assert.Throws<InvalidOperationException>(() => j.Transition(id, JournalState.Planned));
                Assert.Throws<InvalidOperationException>(() => j.Transition(id, JournalState.Applied, original: StateSnapshot.Of("DWord", "9")));
            }
            using (var j = new SqliteJournal(path))
            {
                var e = j.Get(id);
                Assert.Equal(JournalState.Applying, e.State);
                Assert.False(e.Original!.Exists);
                Assert.Equal("false", e.Original.Detail!["keyExisted"]);
                Assert.Equal(a.Definition.Key, e.Action.Definition.Key);
                Assert.Equal("1", e.Action.Desired.Value);
                Assert.Null(Assert.Single(j.Runs()).EndedAt);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void Run_lock_prevents_concurrent_execution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-{Guid.NewGuid():N}.lock");
        using var first = RunLock.TryAcquire(path);
        Assert.NotNull(first);
        Assert.Null(RunLock.TryAcquire(path));
    }
}
