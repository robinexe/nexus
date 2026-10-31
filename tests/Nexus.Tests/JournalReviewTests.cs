using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

/// <summary>
/// Testes adicionados na revisão da Etapa 1 (build log, docs/06-build-log.md).
/// Cobrem divergências entre InMemoryJournal e SqliteJournal que os testes originais não exercitavam.
/// </summary>
[Trait("Category", "Unit")]
public sealed class JournalReviewTests : IDisposable
{
    private const string P = @"Software\NexusTest";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nexus-review-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private static void SecondExecuteInSameRunJournalsEveryAction(IJournal journal)
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var exec = new ActionExecutor(handlers, journal);
        var resolver = new DesiredStateResolver(handlers);
        var run = journal.BeginRun("p", "1");

        // Ex.: lote normal e, depois, o lote pós-reinício na mesma execução.
        var first = exec.Execute(run, resolver.Resolve([T.RegAction("a", P, "A", 1)], T.Ctx), T.Ctx);
        var second = exec.Execute(run, resolver.Resolve([T.RegAction("b", P, "B", 1)], T.Ctx), T.Ctx);

        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(first).Outcome);
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(second).Outcome);
        var entries = journal.Entries(run);
        Assert.Equal(["a", "b"], entries.Select(e => e.Action.Definition.Id));
        Assert.Equal(entries.Count, entries.Select(e => e.Sequence).Distinct().Count());

        // A reversão em ordem inversa precisa enxergar as duas.
        Assert.All(exec.RevertRun(run), r => Assert.Equal(RevertOutcome.Reverted, r.Outcome));
    }

    [Fact]
    public void Second_execute_in_same_run_with_sqlite_journal_does_not_collide_on_sequence()
    {
        using var j = new SqliteJournal(_path);
        SecondExecuteInSameRunJournalsEveryAction(j);
    }

    [Fact]
    public void Second_execute_in_same_run_with_in_memory_journal_does_not_collide_on_sequence() =>
        SecondExecuteInSameRunJournalsEveryAction(new InMemoryJournal());

    [Fact]
    public void In_memory_journal_rejects_duplicate_sequence_like_sqlite()
    {
        var a = T.RegAction("a", P, "A", 1);
        var mem = new InMemoryJournal();
        var r1 = mem.BeginRun("p", "1");
        mem.AddPlanned(r1, 0, a);
        Assert.Throws<InvalidOperationException>(() => mem.AddPlanned(r1, 0, a));
        mem.AddPlanned(mem.BeginRun("p", "1"), 0, a); // outra execução pode reutilizar a sequência

        using var sql = new SqliteJournal(_path);
        var r2 = sql.BeginRun("p", "1");
        sql.AddPlanned(r2, 0, a);
        Assert.NotNull(Record.Exception(() => sql.AddPlanned(r2, 0, a)));
    }

    [Fact]
    public void Read_only_journal_reads_entries_and_refuses_writes_without_touching_schema()
    {
        long id;
        using (var j = new SqliteJournal(_path))
        {
            var run = j.BeginRun("p", "1");
            id = j.AddPlanned(run, 0, T.RegAction("a", P, "A", 1));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var before = File.GetLastWriteTimeUtc(_path);

        using (var ro = SqliteJournal.OpenReadOnly(_path))
        {
            Assert.Equal(JournalState.Planned, ro.Get(id).State);
            Assert.Single(ro.Runs());
            Assert.NotNull(Record.Exception(() => ro.Transition(id, JournalState.BackedUp, original: StateSnapshot.Absent())));
        }

        Assert.Equal(before, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void Read_only_open_refuses_a_file_that_is_not_a_nexus_journal()
    {
        File.WriteAllBytes(_path, []); // banco vazio: schema 0
        Assert.Throws<InvalidOperationException>(() => SqliteJournal.OpenReadOnly(_path).Dispose());
        Assert.Equal(0, new FileInfo(_path).Length); // nada foi criado nem migrado
    }
}
