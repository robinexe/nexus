namespace Nexus.Core;

public sealed record RecoveryFinding(JournalEntry Entry, string Reason);

public sealed record RecoveryReport(
    IReadOnlyList<RecoveryFinding> Interrupted,
    IReadOnlyList<RevertResult> Results,
    IReadOnlyList<RecoveryFinding> SkippedActive)
{
    public RecoveryReport(IReadOnlyList<RecoveryFinding> interrupted, IReadOnlyList<RevertResult> results)
        : this(interrupted, results, []) { }

    public bool FullyRestored => SkippedActive.Count == 0 &&
        Results.All(r => r.Outcome is RevertOutcome.Reverted or RevertOutcome.AlreadyOriginal or RevertOutcome.NothingToRevert);

    public bool AnyInferred => Results.Any(r => r.Inferred);
}

/// <summary>
/// Recuperação após crash/reinício. Funciona sem a interface: usa somente o diário e os handlers.
/// Interrompidas = entradas em BackedUp/Applying/Applied/Restoring (o processo morreu no meio),
/// conflitos pendentes, compensações não confirmadas e ajustes de sessão Verified em execuções que nunca foram encerradas.
/// <para>
/// Em produção, o executor recebido segura a trava de execução: portanto nenhum outro executor está vivo e toda
/// execução não encerrada é abandonada, exceto a vinculada à própria trava, que nunca é tocada.
/// </para>
/// </summary>
public sealed class RecoveryService
{
    private readonly IJournal _journal;
    private readonly ActionExecutor _executor;

    public RecoveryService(IJournal journal, ActionExecutor executor)
    {
        _journal = journal;
        _executor = executor;
    }

    /// <summary>Somente leitura. Pode ser usado sem trava (ex.: <c>nexus-recover scan</c>).</summary>
    public static IReadOnlyList<RecoveryFinding> FindInterrupted(IJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var findings = new List<RecoveryFinding>();
        foreach (var run in journal.Runs())
        {
            foreach (var e in journal.Entries(run.RunId))
            {
                if (e.State is JournalState.BackedUp or JournalState.Applying or JournalState.Applied)
                    findings.Add(new(e, $"Execução interrompida em {e.State}."));
                else if (e.State == JournalState.Restoring)
                    findings.Add(new(e, "Restauração interrompida ou não confirmada."));
                else if (e.State == JournalState.Verified && run.EndedAt is null &&
                         e.Action.Definition.Persistence == PersistenceKind.Session)
                    findings.Add(new(e, "Ajuste de sessão sem encerramento de sessão registrado."));
                else if (e.State == JournalState.RevertConflict)
                    findings.Add(new(e, "Conflito pendente: o alvo foi alterado externamente após a aplicação (use --force para restaurar o original)."));
                else if (e.State == JournalState.Failed && e.Original is not null)
                    findings.Add(new(e, "Falha com compensação não confirmada."));
            }
        }
        return findings;
    }

    public IReadOnlyList<RecoveryFinding> FindInterrupted() => FindInterrupted(_journal);

    public RecoveryReport Recover(bool forceConflicts = false)
    {
        var lease = _executor.Lease;
        lease?.EnsureHeld();
        var activeRun = lease?.ActiveRunId;

        var interrupted = FindInterrupted();
        var skipped = interrupted.Where(f => activeRun is not null && f.Entry.RunId == activeRun).ToList();

        // Ordem: execução mais recente primeiro e, dentro dela, sequência inversa (desfaz na ordem contrária à aplicação).
        // UpdatedAt não serve: marcar um conflito muda UpdatedAt sem mudar a ordem real das escritas (caso A6).
        var runs = _journal.Runs();
        var started = runs.ToDictionary(r => r.RunId, r => r.StartedAt);
        var all = runs.SelectMany(r => _journal.Entries(r.RunId)).ToList();
        var ordered = interrupted
            .Where(f => activeRun is null || f.Entry.RunId != activeRun)
            .OrderByDescending(f => started.TryGetValue(f.Entry.RunId, out var s) ? s : DateTimeOffset.MinValue)
            .ThenByDescending(f => f.Entry.RunId)
            .ThenByDescending(f => f.Entry.Sequence)
            .ToList();

        var results = new List<RevertResult>();
        foreach (var f in ordered)
        {
            var key = f.Entry.Action.Definition.Key;
            try
            {
                // Superada: uma entrada MAIS NOVA do próprio NEXUS no mesmo alvo, ainda não revertida. Restaurar este backup
                // desfaria aquela mudança (e deixaria o diário dela mentindo). Não escreve; exige decisão.
                var newer = Superseding(f.Entry, all, started);
                if (newer is not null)
                {
                    results.Add(new RevertResult(f.Entry.Id, key, RevertOutcome.Conflict,
                        $"Superada: a entrada #{newer.Id} ({newer.Action.Definition.Key}, execução {newer.RunId}) alterou o mesmo alvo depois e não foi revertida. " +
                        "Restaurar este backup desfaria essa mudança; nada foi escrito."));
                    continue;
                }
                results.Add(_executor.Revert(f.Entry.Id, forceConflicts));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _executor.FaultInjector is null)
            {
                // Erro do diário/sistema num item: registra e segue, para o relatório não se perder (nunca vira "completa").
                results.Add(new RevertResult(f.Entry.Id, key, RevertOutcome.Failed, $"Erro inesperado: {ex.GetType().Name}: {ex.Message}"));
            }
        }
        return new RecoveryReport(interrupted, results, skipped);
    }

    private JournalEntry? Superseding(JournalEntry e, IReadOnlyList<JournalEntry> all, IReadOnlyDictionary<Guid, DateTimeOffset> started)
    {
        var target = TargetKey(e);
        var mine = started.GetValueOrDefault(e.RunId);
        foreach (var other in all)
        {
            if (other.Id == e.Id || other.Original is null || TargetKey(other) != target) continue;
            var theirs = started.GetValueOrDefault(other.RunId);
            var isNewer = theirs > mine || (other.RunId == e.RunId && other.Sequence > e.Sequence);
            if (!isNewer) continue;
            if (_journal.Get(other.Id).State != JournalState.Reverted) return other; // estado atual, não o do início
        }
        return null;
    }

    private static string TargetKey(JournalEntry e) => e.Action.Definition.HandlerKind + "|" + e.Action.TargetDescription;
}
