namespace Nexus.Core;

public sealed record RecoveryFinding(JournalEntry Entry, string Reason);

public enum AcceptOutcome
{
    /// <summary>Entrada encerrada como <see cref="JournalState.Accepted"/>; nada foi escrito no sistema.</summary>
    Accepted,
    /// <summary>A entrada não existe no diário.</summary>
    NotFound,
    /// <summary>A entrada não está pendente (já encerrada ou nunca aplicada): nada a aceitar.</summary>
    NotPending,
    /// <summary>A entrada pertence à execução vinculada à própria trava (execução ativa): nunca é tocada.</summary>
    ActiveRun,
}

public sealed record AcceptResult(long EntryId, AcceptOutcome Outcome, string Detail);

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

    /// <summary>
    /// Decisão explícita do usuário para uma entrada pendente (superada, em conflito ou interrompida): mantém o estado
    /// atual do alvo e encerra a entrada como <see cref="JournalState.Accepted"/>, com registro no diário.
    /// Não chama nenhum handler: não lê nem escreve no sistema. Exige a trava, como a recuperação.
    /// </summary>
    public AcceptResult Accept(long entryId)
    {
        var lease = _executor.Lease;
        lease?.EnsureHeld();

        var pending = FindInterrupted().FirstOrDefault(f => f.Entry.Id == entryId);
        if (pending is null)
        {
            var exists = _journal.Runs().SelectMany(r => _journal.Entries(r.RunId)).FirstOrDefault(e => e.Id == entryId);
            return exists is null
                ? new(entryId, AcceptOutcome.NotFound, "Entrada não encontrada no diário.")
                : new(entryId, AcceptOutcome.NotPending, $"Entrada em {exists.State}: não está pendente, nada a aceitar.");
        }
        if (lease?.ActiveRunId is { } active && pending.Entry.RunId == active)
            return new(entryId, AcceptOutcome.ActiveRun, "A entrada pertence à execução ativa desta trava; não foi tocada.");

        var previous = pending.Entry.State;
        _journal.Transition(entryId, JournalState.Accepted,
            note: $"Aceito pelo usuário (estava em {previous}: {pending.Reason}). Estado atual mantido; backup original descartado. Nada foi escrito no sistema.");
        return new(entryId, AcceptOutcome.Accepted, $"Encerrada (estava em {previous}). Nada foi escrito no sistema.");
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
