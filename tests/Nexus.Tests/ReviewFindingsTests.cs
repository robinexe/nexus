using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;

namespace Nexus.Tests;

/// <summary>Achados da revisão independente (docs/07-recovery-cases.md, seção "Revisão independente").</summary>
[Trait("Category", "Unit")]
public sealed class ReviewFindingsTests
{
    private const string P = @"Software\NexusTest";

    /// <summary>Diário adulterado: a entrada aponta para um alvo que a política de segurança proíbe.</summary>
    private static (InMemoryRegistryBackend Reg, InMemoryJournal Journal, long Id, HandlerRegistry Handlers) TamperedRegistryEntry(string path, RegHive hive)
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var journal = new InMemoryJournal();
        var run = journal.BeginRun("p", "1");
        var forged = new ActionInstance(T.Def("x", RegistryValueHandler.Kind),
            new Dictionary<string, string> { ["hive"] = hive.ToString(), ["path"] = path, ["name"] = "RunAsPPL", ["type"] = "DWord" },
            StateSnapshot.Of("DWord", "1"));
        var id = journal.AddPlanned(run, 0, forged);
        journal.Transition(id, JournalState.BackedUp, original: StateSnapshot.Of("DWord", "0"));
        journal.Transition(id, JournalState.Applying);
        reg.Seed(new RegistryTarget(hive, path, "RunAsPPL", RegView.Default), "DWord", "1"); // "aplicado"
        return (reg, journal, id, handlers);
    }

    [Fact]
    public void Restore_refuses_a_journal_entry_whose_target_violates_the_safety_policy()
    {
        var (reg, journal, id, handlers) = TamperedRegistryEntry(@"SYSTEM\CurrentControlSet\Control\Lsa", RegHive.HKLM);

        var r = new ActionExecutor(handlers, journal).Revert(id, force: true);

        Assert.Equal(RevertOutcome.Failed, r.Outcome);
        Assert.Contains("recusada", r.Detail, StringComparison.Ordinal);
        Assert.Equal("1", reg.Read(new RegistryTarget(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL", RegView.Default)).Value);
        Assert.Equal(JournalState.Applying, journal.Get(id).State); // continua visível para a recuperação
    }

    [Fact]
    public void Restore_of_valid_target_still_works()
    {
        var (reg, journal, id, handlers) = TamperedRegistryEntry(P, RegHive.HKCU);
        Assert.Equal(RevertOutcome.Reverted, new ActionExecutor(handlers, journal).Revert(id).Outcome);
        Assert.Equal("0", reg.Read(new RegistryTarget(RegHive.HKCU, P, "RunAsPPL", RegView.Default)).Value);
    }

    [Theory]
    [InlineData("WinDefend", "Manual")]
    [InlineData("SysMain", "Boot")]
    public void Service_restore_refuses_protected_service_or_boot_start_type(string service, string original)
    {
        var h = new ServiceStartTypeHandler(new NullServiceBackend());
        var a = new ActionInstance(T.Def("s", ServiceStartTypeHandler.Kind), new Dictionary<string, string> { ["service"] = service }, StateSnapshot.Of("StartType", "Manual"));
        Assert.NotNull(((IRestoreValidator)h).CheckRestore(a, StateSnapshot.Of("StartType", original)));
    }

    [Fact]
    public void Cfg_restore_refuses_path_outside_root()
    {
        var root = Directory.CreateTempSubdirectory("nexus-rv-").FullName;
        try
        {
            var h = new SourceCfgConvarHandler(() => null);
            var a = new ActionInstance(T.Def("c", SourceCfgConvarHandler.Kind),
                new Dictionary<string, string> { ["root"] = root, ["file"] = Path.Combine("..", "evil.cfg"), ["convar"] = "fps_max" }, StateSnapshot.Of("Convar", "1"));
            Assert.NotNull(((IRestoreValidator)h).CheckRestore(a, StateSnapshot.Of("Convar", "0")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Older_entry_superseded_by_a_newer_nexus_run_is_not_restored()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var journal = new InMemoryJournal();
        var t = T.RT(P, "V");
        reg.Seed(t, "DWord", "0");

        // Execução A: backup 0, Apply, depois a compensação não conseguiu ler: fica Failed com original 0.
        var runA = journal.BeginRun("A", "1");
        var a = T.RegAction("a", P, "V", 1);
        var idA = journal.AddPlanned(runA, 0, a);
        journal.Transition(idA, JournalState.BackedUp, original: reg.Read(t).ValueExists ? StateSnapshot.Of("DWord", "0") : StateSnapshot.Absent());
        journal.Transition(idA, JournalState.Applying);
        journal.Transition(idA, JournalState.Failed, note: "releitura falhou");
        reg.Write(t, "DWord", "5"); // depois disso o valor mudou por fora

        // Execução B (mais nova, do próprio NEXUS): 5 → 1, verificada e encerrada.
        var exec = new ActionExecutor(handlers, journal);
        var runB = journal.BeginRun("B", "1");
        Thread.Sleep(5); // StartedAt de B posterior ao de A
        Assert.Equal(ExecutionOutcome.Verified,
            exec.Execute(runB, new DesiredStateResolver(handlers).Resolve([T.RegAction("b", P, "V", 1)], T.Ctx), T.Ctx).Single().Outcome);
        journal.EndRun(runB);

        var report = new RecoveryService(journal, new ActionExecutor(handlers, journal)).Recover();

        var r = Assert.Single(report.Results);
        Assert.Equal(RevertOutcome.Conflict, r.Outcome);
        Assert.Contains("Superada", r.Detail, StringComparison.Ordinal);
        Assert.False(report.FullyRestored);
        Assert.Equal("1", reg.Read(t).Value); // a mudança de B foi preservada
    }

    // ---------- R3: nexus-recover accept ----------

    private sealed class CountingRegistry(IRegistryBackend inner) : IRegistryBackend
    {
        public int Calls { get; private set; }
        public RegistryRead Read(RegistryTarget target) { Calls++; return inner.Read(target); }
        public void Write(RegistryTarget target, string kind, string value) { Calls++; inner.Write(target, kind, value); }
        public void DeleteValue(RegistryTarget target) { Calls++; inner.DeleteValue(target); }
        public bool DeleteKeyIfEmpty(RegHive hive, string path, RegView view) { Calls++; return inner.DeleteKeyIfEmpty(hive, path, view); }
    }

    /// <summary>Mesmo cenário do teste acima: A (Failed, original 0) superada por B (5 → 1, verificada).</summary>
    private static (InMemoryRegistryBackend Reg, InMemoryJournal Journal, HandlerRegistry Handlers, long IdA, Guid RunB, RegistryTarget T) SupersededScenario()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var journal = new InMemoryJournal();
        var t = T.RT(P, "V");
        reg.Seed(t, "DWord", "0");
        var runA = journal.BeginRun("A", "1");
        var idA = journal.AddPlanned(runA, 0, T.RegAction("a", P, "V", 1));
        journal.Transition(idA, JournalState.BackedUp, original: StateSnapshot.Of("DWord", "0"));
        journal.Transition(idA, JournalState.Applying);
        journal.Transition(idA, JournalState.Failed, note: "releitura falhou");
        reg.Write(t, "DWord", "5");
        var runB = journal.BeginRun("B", "1");
        Thread.Sleep(5);
        var exec = new ActionExecutor(handlers, journal);
        Assert.Equal(ExecutionOutcome.Verified,
            exec.Execute(runB, new DesiredStateResolver(handlers).Resolve([T.RegAction("b", P, "V", 1)], T.Ctx), T.Ctx).Single().Outcome);
        journal.EndRun(runB);
        return (reg, journal, handlers, idA, runB, t);
    }

    [Fact]
    public void Accept_closes_a_superseded_entry_without_touching_the_system_and_recovery_becomes_complete()
    {
        var (reg, journal, _, idA, _, t) = SupersededScenario();
        var counting = new CountingRegistry(reg);
        var recovery = new RecoveryService(journal, new ActionExecutor(new HandlerRegistry().Add(new RegistryValueHandler(counting)), journal));
        Assert.False(recovery.Recover().FullyRestored); // antes: pendente para sempre

        var calls = counting.Calls;
        var r = recovery.Accept(idA);

        Assert.Equal(AcceptOutcome.Accepted, r.Outcome);
        Assert.Equal(calls, counting.Calls); // não leu nem escreveu no sistema
        var e = journal.Get(idA);
        Assert.Equal(JournalState.Accepted, e.State);
        Assert.Contains("Aceito pelo usuário", e.Note, StringComparison.Ordinal);
        Assert.Equal(StateSnapshot.Of("DWord", "0"), e.Original); // o backup fica registrado, só não é mais usado
        Assert.Equal("1", reg.Read(t).Value);

        Assert.DoesNotContain(RecoveryService.FindInterrupted(journal), f => f.Entry.Id == idA);
        var after = recovery.Recover();
        Assert.Empty(after.Results);
        Assert.True(after.FullyRestored);
    }

    [Fact]
    public void Accepted_entry_is_never_restored_later_even_with_force()
    {
        var (reg, journal, handlers, idA, _, t) = SupersededScenario();
        var exec = new ActionExecutor(handlers, journal);
        new RecoveryService(journal, exec).Accept(idA);

        var r = exec.Revert(idA, force: true);
        Assert.Equal(RevertOutcome.NothingToRevert, r.Outcome);
        Assert.Equal(JournalState.Accepted, journal.Get(idA).State);
        Assert.Equal("1", reg.Read(t).Value);
        Assert.Throws<InvalidOperationException>(() => journal.Transition(idA, JournalState.Restoring)); // terminal
    }

    [Fact]
    public void Accept_refuses_entries_that_are_not_pending_or_do_not_exist()
    {
        var (_, journal, handlers, idA, runB, _) = SupersededScenario();
        var recovery = new RecoveryService(journal, new ActionExecutor(handlers, journal));
        var idB = journal.Entries(runB).Single().Id; // verificada numa execução encerrada: não é pendência

        Assert.Equal(AcceptOutcome.NotPending, recovery.Accept(idB).Outcome);
        Assert.Equal(JournalState.Verified, journal.Get(idB).State);
        Assert.Equal(AcceptOutcome.NotFound, recovery.Accept(9999).Outcome);
        Assert.Equal(AcceptOutcome.Accepted, recovery.Accept(idA).Outcome);
        Assert.Equal(AcceptOutcome.NotPending, recovery.Accept(idA).Outcome); // segunda vez: já encerrada
    }

    [Fact]
    public void Accepted_newer_entry_still_counts_as_a_live_change_for_older_entries()
    {
        // B é mais nova e foi aceita (a mudança dela continua no sistema). A, mais antiga, continua superada por B.
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var journal = new InMemoryJournal();
        var t = T.RT(P, "V");
        reg.Seed(t, "DWord", "0");
        var runA = journal.BeginRun("A", "1");
        var idA = journal.AddPlanned(runA, 0, T.RegAction("a", P, "V", 1));
        journal.Transition(idA, JournalState.BackedUp, original: StateSnapshot.Of("DWord", "0"));
        journal.Transition(idA, JournalState.Applying);
        journal.Transition(idA, JournalState.Failed, note: "releitura falhou");
        Thread.Sleep(5);
        var runB = journal.BeginRun("B", "1");
        var idB = journal.AddPlanned(runB, 0, T.RegAction("b", P, "V", 7));
        journal.Transition(idB, JournalState.BackedUp, original: StateSnapshot.Of("DWord", "0"));
        journal.Transition(idB, JournalState.Applying);
        reg.Write(t, "DWord", "7");
        var recovery = new RecoveryService(journal, new ActionExecutor(handlers, journal));
        Assert.Equal(AcceptOutcome.Accepted, recovery.Accept(idB).Outcome);

        var report = recovery.Recover();

        var r = Assert.Single(report.Results);
        Assert.Equal(idA, r.EntryId);
        Assert.Equal(RevertOutcome.Conflict, r.Outcome);
        Assert.Contains("Superada", r.Detail, StringComparison.Ordinal);
        Assert.Equal("7", reg.Read(t).Value);
    }

    [Fact]
    public void Accepted_state_round_trips_through_the_sqlite_journal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-accept-{Guid.NewGuid():N}.db");
        try
        {
            long id;
            using (var j = new SqliteJournal(path))
            {
                var run = j.BeginRun("p", "1");
                id = j.AddPlanned(run, 0, T.RegAction("a", P, "V", 1));
                j.Transition(id, JournalState.BackedUp, original: StateSnapshot.Of("DWord", "0"));
                j.Transition(id, JournalState.Applying);
                Assert.Equal(AcceptOutcome.Accepted,
                    new RecoveryService(j, new ActionExecutor(new HandlerRegistry(), j)).Accept(id).Outcome);
            }
            using var ro = SqliteJournal.OpenReadOnly(path);
            Assert.Equal(JournalState.Accepted, ro.Get(id).State);
            Assert.Empty(RecoveryService.FindInterrupted(ro));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void Recovery_records_unexpected_errors_per_item_and_keeps_the_report()
    {
        var (reg, handlers, mem, exec, resolver) = T.Engine();
        var run = mem.BeginRun("p", "1");
        exec.FaultInjector = (w, _) => { if (w == "after-apply") throw new InvalidOperationException("crash"); };
        Assert.Throws<InvalidOperationException>(() => exec.Execute(run, resolver.Resolve([T.RegAction("a", P, "A", 1)], T.Ctx), T.Ctx));
        Assert.Throws<InvalidOperationException>(() => exec.Execute(run, resolver.Resolve([T.RegAction("b", P, "B", 1)], T.Ctx), T.Ctx));

        // O diário falha (ex.: disco cheio) ao gravar Restoring no primeiro item recuperado.
        var failing = new JournalThatFailsOnce(mem, JournalState.Restoring);
        var report = new RecoveryService(failing, new ActionExecutor(handlers, failing)).Recover();

        Assert.Equal(2, report.Results.Count);                                          // o relatório não se perdeu
        Assert.Contains(report.Results, r => r.Outcome == RevertOutcome.Failed && r.Detail.Contains("Erro inesperado", StringComparison.Ordinal));
        Assert.Contains(report.Results, r => r.Outcome == RevertOutcome.Reverted);      // e o outro item seguiu
        Assert.False(report.FullyRestored);
    }

    private sealed class JournalThatFailsOnce(IJournal inner, JournalState failOn) : IJournal
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

    [Fact]
    public void Owner_record_that_does_not_match_a_live_process_is_not_presented_as_the_holder()
    {
        var dir = Directory.CreateTempSubdirectory("nexus-owner-").FullName;
        try
        {
            var c = new FileRunCoordinator(dir);
            using var lease = c.TryAcquire("real").Lease!;
            // Registro adulterado/obsoleto: PID inexistente.
            var fake = new LeaseHolder(Guid.NewGuid(), "fantasma", Guid.NewGuid(), int.MaxValue - 7, DateTimeOffset.UtcNow, Environment.MachineName, "x", DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(dir, FileRunCoordinator.OwnerFileName), System.Text.Json.JsonSerializer.Serialize(fake));

            var probe = new FileRunCoordinator(dir).Probe();
            Assert.Equal(CoordinationState.Active, probe.State);   // a trava do SO continua mandando
            Assert.Null(probe.Holder);
            Assert.Contains("desatualizado", probe.Detail, StringComparison.Ordinal);
            Assert.True(FileRunCoordinator.IsLive(lease.Holder));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class NullServiceBackend : IServiceBackend
    {
        public string? GetStartType(string service) => "Manual";
        public void SetStartType(string service, string startType) => throw new InvalidOperationException("não deveria escrever");
    }
}
