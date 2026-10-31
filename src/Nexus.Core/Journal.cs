namespace Nexus.Core;

/// <summary>Estados persistidos de cada ação (seção 15).</summary>
public enum JournalState
{
    Planned,
    BackedUp,
    Applying,
    Applied,
    Verified,
    Reverted,
    Failed,
    /// <summary>O alvo mudou depois da aplicação; nada foi sobrescrito.</summary>
    RevertConflict,
    /// <summary>
    /// Intenção de restaurar gravada ANTES de escrever no sistema. <see cref="JournalEntry.LastObserved"/> guarda o valor
    /// que o NEXUS estava autorizado a sobrescrever (ou que ele próprio produziu numa restauração não confirmada).
    /// </summary>
    Restoring,
    /// <summary>
    /// Encerrada por decisão explícita do usuário (<c>nexus-recover accept</c>): o estado atual do alvo é mantido, o
    /// backup original deixa de ser usado e nada é escrito no sistema. Terminal, como <see cref="Reverted"/>, mas não
    /// significa que o original foi restaurado (por isso continua contando como mudança viva para entradas mais antigas).
    /// </summary>
    Accepted,
}

public static class JournalTransitions
{
    private static readonly Dictionary<JournalState, JournalState[]> Allowed = new()
    {
        [JournalState.Planned] = [JournalState.BackedUp, JournalState.Failed],
        [JournalState.BackedUp] = [JournalState.Applying, JournalState.Failed, JournalState.Reverted, JournalState.RevertConflict, JournalState.Accepted],
        [JournalState.Applying] = [JournalState.Applied, JournalState.Failed, JournalState.Reverted, JournalState.RevertConflict, JournalState.Restoring, JournalState.Accepted],
        [JournalState.Applied] = [JournalState.Verified, JournalState.Failed, JournalState.Reverted, JournalState.RevertConflict, JournalState.Restoring, JournalState.Accepted],
        [JournalState.Verified] = [JournalState.Reverted, JournalState.RevertConflict, JournalState.Restoring, JournalState.Accepted],
        [JournalState.RevertConflict] = [JournalState.Reverted, JournalState.Restoring, JournalState.Accepted],
        [JournalState.Failed] = [JournalState.Reverted, JournalState.RevertConflict, JournalState.Restoring, JournalState.Accepted],
        // Restoring → Restoring registra uma nova tentativa (e o valor que a anterior deixou).
        [JournalState.Restoring] = [JournalState.Reverted, JournalState.RevertConflict, JournalState.Restoring, JournalState.Accepted],
        [JournalState.Reverted] = [],
        [JournalState.Accepted] = [],
    };

    public static bool IsAllowed(JournalState from, JournalState to) => Allowed[from].Contains(to);

    public static void Ensure(JournalState from, JournalState to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidOperationException($"Transição de diário inválida: {from} → {to}");
    }
}

public sealed record JournalEntry(
    long Id,
    Guid RunId,
    int Sequence,
    ActionInstance Action,
    JournalState State,
    StateSnapshot? Original,
    StateSnapshot? LastObserved,
    string? Note,
    DateTimeOffset UpdatedAt);

public sealed record RunInfo(Guid RunId, string Profile, string CatalogVersion, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

/// <summary>
/// Diário persistente. Implementações devem gravar de forma durável ANTES de retornar,
/// porque a recuperação depende de o estado "Applying" existir em disco antes da escrita no sistema.
/// </summary>
public interface IJournal
{
    Guid BeginRun(string profile, string catalogVersion);
    void EndRun(Guid runId);
    long AddPlanned(Guid runId, int sequence, ActionInstance action);
    void Transition(long entryId, JournalState targetState, StateSnapshot? original = null, StateSnapshot? observed = null, string? note = null);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Só afeta implementações em VB.NET; o NEXUS é C# e o nome é usado em toda a base e nos testes.")]
    JournalEntry Get(long entryId);
    IReadOnlyList<JournalEntry> Entries(Guid runId);
    IReadOnlyList<RunInfo> Runs();
}

/// <summary>Diário em memória para testes e simulação. Aplica as mesmas regras de transição.</summary>
public sealed class InMemoryJournal : IJournal
{
    private readonly List<RunInfo> _runs = [];
    private readonly Dictionary<long, JournalEntry> _entries = [];
    private long _next = 1;
    private readonly TimeProvider _clock;

    public InMemoryJournal(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Guid BeginRun(string profile, string catalogVersion)
    {
        var id = Guid.NewGuid();
        _runs.Add(new RunInfo(id, profile, catalogVersion, _clock.GetUtcNow(), null));
        return id;
    }

    public void EndRun(Guid runId)
    {
        var i = _runs.FindIndex(r => r.RunId == runId);
        _runs[i] = _runs[i] with { EndedAt = _clock.GetUtcNow() };
    }

    public long AddPlanned(Guid runId, int sequence, ActionInstance action)
    {
        // Mesma restrição do SqliteJournal: UNIQUE(run_id, seq).
        if (_entries.Values.Any(e => e.RunId == runId && e.Sequence == sequence))
            throw new InvalidOperationException($"Sequência {sequence} já usada na execução {runId}.");
        var id = _next++;
        _entries[id] = new JournalEntry(id, runId, sequence, action, JournalState.Planned, null, null, null, _clock.GetUtcNow());
        return id;
    }

    public void Transition(long entryId, JournalState targetState, StateSnapshot? original = null, StateSnapshot? observed = null, string? note = null)
    {
        var e = _entries[entryId];
        JournalTransitions.Ensure(e.State, targetState);
        if (e.Original is not null && original is not null)
            throw new InvalidOperationException("Backup original é imutável depois de gravado.");
        _entries[entryId] = e with
        {
            State = targetState,
            Original = original ?? e.Original,
            LastObserved = observed ?? e.LastObserved,
            Note = note ?? e.Note,
            UpdatedAt = _clock.GetUtcNow(),
        };
    }

    public JournalEntry Get(long entryId) => _entries[entryId];

    public IReadOnlyList<JournalEntry> Entries(Guid runId) =>
        _entries.Values.Where(e => e.RunId == runId).OrderBy(e => e.Sequence).ToList();

    public IReadOnlyList<RunInfo> Runs() => _runs.ToList();
}
