using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

/// <summary>Casos ambíguos A1–A3 e A5–A7 (docs/07-recovery-cases.md). A4 está em CoordinationTests.</summary>
[Trait("Category", "Unit")]
public sealed class RecoveryCaseTests
{
    private const string P = @"Software\NexusTest";

    private sealed class SimulatedCrash(string at) : Exception($"crash simulado em {at}");

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Diário que falha (como disco cheio) na primeira transição para <paramref name="failOn"/>.</summary>
    private sealed class FailingJournal(IJournal inner, JournalState failOn) : IJournal
    {
        private bool _failed;
        public Guid BeginRun(string profile, string catalogVersion) => inner.BeginRun(profile, catalogVersion);
        public void EndRun(Guid runId) => inner.EndRun(runId);
        public long AddPlanned(Guid runId, int sequence, ActionInstance action) => inner.AddPlanned(runId, sequence, action);
        public void Transition(long entryId, JournalState targetState, StateSnapshot? original = null, StateSnapshot? observed = null, string? note = null)
        {
            if (targetState == failOn && !_failed) { _failed = true; throw new IOException("disco cheio (simulado)"); }
            inner.Transition(entryId, targetState, original, observed, note);
        }
        public JournalEntry Get(long entryId) => inner.Get(entryId);
        public IReadOnlyList<JournalEntry> Entries(Guid runId) => inner.Entries(runId);
        public IReadOnlyList<RunInfo> Runs() => inner.Runs();
    }

    /// <summary>Apply grava outro valor (999); Restore funciona. O "sistema" é o próprio objeto.</summary>
    private sealed class WrongWriteHandler : IActionHandler
    {
        public StateSnapshot State { get; set; } = StateSnapshot.Of("DWord", "0");
        public string HandlerKind => "WrongWrite";
        public Applicability Assess(ActionInstance action, SystemContext context) => Applicability.Ok;
        public StateSnapshot Detect(ActionInstance action) => State;
        public void Apply(ActionInstance action) => State = StateSnapshot.Of("DWord", "999");
        public void Restore(ActionInstance action, StateSnapshot original) => State = original;
    }

    /// <summary>Apply correto; a primeira restauração deixa um valor intermediário (555), as seguintes funcionam.</summary>
    private sealed class FlakyRestoreHandler : IActionHandler
    {
        public StateSnapshot State { get; set; } = StateSnapshot.Of("DWord", "0");
        public int Restores { get; private set; }
        public string HandlerKind => "FlakyRestore";
        public Applicability Assess(ActionInstance action, SystemContext context) => Applicability.Ok;
        public StateSnapshot Detect(ActionInstance action) => State;
        public void Apply(ActionInstance action) => State = action.Desired;
        public void Restore(ActionInstance action, StateSnapshot original) =>
            State = ++Restores == 1 ? StateSnapshot.Of("DWord", "555") : original;
    }

    private static ActionInstance Act(string id, string handler) =>
        new(T.Def(id, handler), new Dictionary<string, string> { ["k"] = id }, StateSnapshot.Of("DWord", "1"));

    // ---------- A1 ----------

    [Fact]
    public void A1_interrupted_during_apply_with_desired_present_is_restored_and_flagged_as_inferred()
    {
        var (reg, handlers, journal, exec, resolver) = T.Engine();
        reg.Seed(T.RT(P, "V"), "DWord", "0");
        var run = journal.BeginRun("p", "1");
        exec.FaultInjector = (w, _) => { if (w == "after-apply") throw new SimulatedCrash(w); };
        Assert.Throws<SimulatedCrash>(() => exec.Execute(run, resolver.Resolve([T.RegAction("a", P, "V", 1)], T.Ctx), T.Ctx));

        var report = new RecoveryService(journal, new ActionExecutor(handlers, journal)).Recover();

        var r = Assert.Single(report.Results);
        Assert.Equal(RevertOutcome.Reverted, r.Outcome);
        Assert.True(r.Inferred);
        Assert.True(report.AnyInferred);
        Assert.Equal("0", reg.Read(T.RT(P, "V")).Value);
        Assert.Contains("Inferido", journal.Entries(run).Single().Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A1_restore_of_verified_entry_is_not_flagged_as_inferred()
    {
        var (reg, _, journal, exec, resolver) = T.Engine();
        var run = journal.BeginRun("p", "1");
        exec.Execute(run, resolver.Resolve([T.RegAction("a", P, "V", 1)], T.Ctx), T.Ctx);
        var r = Assert.Single(exec.RevertRun(run));
        Assert.Equal(RevertOutcome.Reverted, r.Outcome);
        Assert.False(r.Inferred);
        Assert.False(reg.Read(T.RT(P, "V")).ValueExists);
    }

    // ---------- A2 ----------

    [Fact]
    public void A2_value_written_by_nexus_during_failed_apply_is_restored_after_crash_inside_compensation()
    {
        var handler = new WrongWriteHandler();
        var handlers = new HandlerRegistry().Add(handler);
        var mem = new InMemoryJournal();
        // O "crash" acontece ao gravar Restoring, isto é, depois de Failed (com o 999 relido) e antes de restaurar.
        var journal = new FailingJournal(mem, JournalState.Restoring);
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("p", "1");

        var result = Assert.Single(exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Act("w", handler.HandlerKind)], T.Ctx), T.Ctx));
        Assert.Equal(ExecutionOutcome.Interrupted, result.Outcome);
        var entry = Assert.Single(mem.Entries(run));
        Assert.Equal(JournalState.Failed, entry.State);
        Assert.Equal("999", entry.LastObserved!.Value); // o diário sabe que o 999 veio da escrita do NEXUS

        var report = new RecoveryService(mem, new ActionExecutor(handlers, mem)).Recover();

        Assert.Equal(RevertOutcome.Reverted, Assert.Single(report.Results).Outcome);
        Assert.Equal("0", handler.State.Value);
    }

    [Fact]
    public void A2_crash_after_restoring_was_journaled_is_resumed()
    {
        var handler = new WrongWriteHandler();
        var handlers = new HandlerRegistry().Add(handler);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal) { FaultInjector = (w, _) => { if (w == "before-restore") throw new SimulatedCrash(w); } };
        var run = journal.BeginRun("p", "1");
        Assert.Throws<SimulatedCrash>(() => exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Act("w", handler.HandlerKind)], T.Ctx), T.Ctx));
        Assert.Equal(JournalState.Restoring, journal.Entries(run).Single().State);

        var report = new RecoveryService(journal, new ActionExecutor(handlers, journal)).Recover();

        Assert.Equal(RevertOutcome.Reverted, Assert.Single(report.Results).Outcome);
        Assert.Equal("0", handler.State.Value);
    }

    [Fact]
    public void A2_external_change_after_failed_apply_is_still_preserved()
    {
        var handler = new WrongWriteHandler();
        var handlers = new HandlerRegistry().Add(handler);
        var mem = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, new FailingJournal(mem, JournalState.Restoring));
        var run = mem.BeginRun("p", "1");
        exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Act("w", handler.HandlerKind)], T.Ctx), T.Ctx);
        handler.State = StateSnapshot.Of("DWord", "42"); // outro programa mudou depois

        var report = new RecoveryService(mem, new ActionExecutor(handlers, mem)).Recover();

        Assert.Equal(RevertOutcome.Conflict, Assert.Single(report.Results).Outcome);
        Assert.Equal("42", handler.State.Value);
    }

    // ---------- A3 ----------

    [Fact]
    public void A3_unconfirmed_restore_is_journaled_and_retry_finishes_without_force()
    {
        var handler = new FlakyRestoreHandler();
        var handlers = new HandlerRegistry().Add(handler);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("p", "1");
        exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Act("f", handler.HandlerKind)], T.Ctx), T.Ctx);
        var id = journal.Entries(run).Single().Id;

        var first = exec.Revert(id);
        Assert.Equal(RevertOutcome.Failed, first.Outcome);
        Assert.Equal(JournalState.Restoring, journal.Get(id).State);
        Assert.Equal("555", journal.Get(id).LastObserved!.Value);
        Assert.Single(new RecoveryService(journal, exec).FindInterrupted());

        var second = exec.Revert(id); // sem --force: o 555 foi deixado pelo próprio NEXUS
        Assert.Equal(RevertOutcome.Reverted, second.Outcome);
        Assert.Equal("0", handler.State.Value);
    }

    [Fact]
    public void A3_external_change_after_unconfirmed_restore_is_a_conflict()
    {
        var handler = new FlakyRestoreHandler();
        var handlers = new HandlerRegistry().Add(handler);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("p", "1");
        exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Act("f", handler.HandlerKind)], T.Ctx), T.Ctx);
        var id = journal.Entries(run).Single().Id;
        exec.Revert(id);
        handler.State = StateSnapshot.Of("DWord", "777");

        Assert.Equal(RevertOutcome.Conflict, exec.Revert(id).Outcome);
        Assert.Equal("777", handler.State.Value);
        Assert.Equal(RevertOutcome.Reverted, exec.Revert(id, force: true).Outcome);
        Assert.Equal("0", handler.State.Value);
    }

    // ---------- A5 ----------

    [Fact]
    public void A5_journal_failure_after_apply_stops_the_whole_execution_and_is_recoverable()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var mem = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, new FailingJournal(mem, JournalState.Applied));
        var run = mem.BeginRun("p", "1");
        var plan = new DesiredStateResolver(handlers).Resolve([T.RegAction("a", P, "A", 1), T.RegAction("b", P, "B", 1)], T.Ctx);

        var results = exec.Execute(run, plan, T.Ctx);

        Assert.Equal(ExecutionOutcome.Interrupted, results.Single(r => r.ActionKey == "a@1").Outcome);
        Assert.Equal(ExecutionOutcome.Blocked, results.Single(r => r.ActionKey == "b@1").Outcome);
        Assert.False(reg.Read(T.RT(P, "B")).ValueExists);  // nada mais foi aplicado com o diário em falha
        Assert.Equal("1", reg.Read(T.RT(P, "A")).Value);   // o primeiro ficou aplicado...
        Assert.Equal(JournalState.Applying, mem.Entries(run).Single().State); // ...e o diário mostra isso

        var report = new RecoveryService(mem, new ActionExecutor(handlers, mem)).Recover();
        Assert.True(report.FullyRestored);
        Assert.False(reg.Read(T.RT(P, "A")).ValueExists);
    }

    // ---------- A6 ----------

    [Fact]
    public void A6_recovery_undoes_newest_run_first_even_when_older_entry_was_touched_later()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var journal = new InMemoryJournal(clock);
        var exec = new ActionExecutor(handlers, journal);
        var resolver = new DesiredStateResolver(handlers);
        reg.Seed(T.RT(P, "V"), "DWord", "0");

        // Execução 1 (abandonada): sessão 0 → 1.
        var run1 = journal.BeginRun("r1", "1");
        exec.Execute(run1, resolver.Resolve([T.RegAction("s", P, "V", 1, persistence: PersistenceKind.Session)], T.Ctx), T.Ctx);
        // Execução 2 (abandonada, mais nova): 1 → 2, morre antes de verificar.
        clock.Now = clock.Now.AddMinutes(1);
        var run2 = journal.BeginRun("r2", "1");
        exec.FaultInjector = (w, _) => { if (w == "after-apply") throw new SimulatedCrash(w); };
        Assert.Throws<SimulatedCrash>(() => exec.Execute(run2, resolver.Resolve([T.RegAction("t", P, "V", 2)], T.Ctx), T.Ctx));
        // Uma tentativa anterior de recuperação (versão com ordem por UpdatedAt) olhou primeiro a entrada antiga,
        // viu 2 ≠ 1 e a marcou como conflito, e morreu antes de chegar à execução 2. Agora a entrada antiga é a mais "nova".
        clock.Now = clock.Now.AddMinutes(1);
        var oldEntry = journal.Entries(run1).Single();
        journal.Transition(oldEntry.Id, JournalState.RevertConflict, observed: StateSnapshot.Of("DWord", "2"), note: "tentativa anterior");
        Assert.True(journal.Get(oldEntry.Id).UpdatedAt > journal.Entries(run2).Single().UpdatedAt);

        var report = new RecoveryService(journal, new ActionExecutor(handlers, journal)).Recover();

        Assert.True(report.FullyRestored, string.Join("; ", report.Results.Select(r => $"{r.ActionKey}:{r.Outcome}:{r.Detail}")));
        Assert.Equal(["t@1", "s@1"], report.Results.Select(r => r.ActionKey));
        Assert.Equal("0", reg.Read(T.RT(P, "V")).Value);
    }

    // ---------- A7 ----------

    [Fact]
    public void A7_sqlite_journal_tolerates_concurrent_use_from_several_threads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-a7-{Guid.NewGuid():N}.db");
        try
        {
            using var j = new SqliteJournal(path);
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 40; i++)
                    {
                        var run = j.BeginRun($"t{t}", "1");
                        var id = j.AddPlanned(run, 0, T.RegAction($"a{t}-{i}", P, "V", 1));
                        j.Transition(id, JournalState.BackedUp, original: StateSnapshot.Absent());
                        _ = j.Entries(run);
                        _ = j.Runs();
                        _ = j.Get(id);
                    }
                }
                catch (Exception ex) { errors.Add(ex); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Empty(errors);
            Assert.Equal(320, j.Runs().Count);
            Assert.All(j.Runs(), r => Assert.Equal(JournalState.BackedUp, Assert.Single(j.Entries(r.RunId)).State));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    // ---------- esquema ----------

    [Fact]
    public void Schema_v1_journal_is_migrated_and_readable_and_newer_schema_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-schema-{Guid.NewGuid():N}.db");
        try
        {
            using (var j = new SqliteJournal(path)) j.BeginRun("p", "1");
            SetUserVersion(path, 1);
            using (var ro = SqliteJournal.OpenReadOnly(path)) Assert.Single(ro.Runs()); // v1 legível
            using (var j = new SqliteJournal(path)) Assert.Single(j.Runs());             // migra para a versão atual
            Assert.Equal(3, SqliteJournal.SchemaVersion);                                 // 3 = estado Accepted
            Assert.Equal((long)SqliteJournal.SchemaVersion, UserVersion(path));

            SetUserVersion(path, 2);                                                      // v2 também migra
            using (var j = new SqliteJournal(path)) Assert.Single(j.Runs());
            Assert.Equal((long)SqliteJournal.SchemaVersion, UserVersion(path));

            SetUserVersion(path, SqliteJournal.SchemaVersion + 1);
            Assert.Throws<InvalidOperationException>(() => new SqliteJournal(path).Dispose());
            Assert.Throws<InvalidOperationException>(() => SqliteJournal.OpenReadOnly(path).Dispose());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }

        static void SetUserVersion(string p, int v)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={p};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {v};";
            cmd.ExecuteNonQuery();
        }

        static long UserVersion(string p)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={p};Pooling=False;Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return (long)cmd.ExecuteScalar()!;
        }
    }
}
