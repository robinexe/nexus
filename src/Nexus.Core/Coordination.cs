namespace Nexus.Core;

/// <summary>Quem segura (ou segurou) a trava de execução. Só diagnóstico: a prova de vida é a trava do SO.</summary>
public sealed record LeaseHolder(
    Guid OwnerToken,
    string Purpose,
    Guid? RunId,
    int ProcessId,
    DateTimeOffset? ProcessStartedAt,
    string Machine,
    string User,
    DateTimeOffset AcquiredAt)
{
    public string Describe() =>
        $"{Purpose} (pid {ProcessId}{(ProcessStartedAt is { } s ? $", processo iniciado {s:u}" : "")}, {User}@{Machine}, desde {AcquiredAt:u}" +
        $"{(RunId is { } r ? $", execução {r}" : "")})";
}

public enum LeaseAcquireStatus
{
    Acquired,
    /// <summary>Outro processo (ou outra instância neste processo) segura a trava agora.</summary>
    HeldByOther,
    /// <summary>Sem permissão para abrir a trava. Não dá para saber se há execução ativa: tratar como ocupada.</summary>
    AccessDenied,
    /// <summary>Falha inesperada. Tratar como ocupada.</summary>
    Error,
}

public sealed record LeaseAcquireResult(LeaseAcquireStatus Status, IRunLease? Lease, LeaseHolder? Holder, string? Detail)
{
    public bool Acquired => Status == LeaseAcquireStatus.Acquired && Lease is not null;
}

public enum CoordinationState
{
    /// <summary>Ninguém executa agora. Execuções não encerradas no diário estão abandonadas.</summary>
    Idle,
    /// <summary>Há um executor vivo. Recuperação deve recusar.</summary>
    Active,
    /// <summary>Não foi possível determinar (permissão, erro). Recuperação deve recusar.</summary>
    Unknown,
}

/// <param name="Holder">Com <see cref="CoordinationState.Active"/>: quem está executando (se o registro estiver legível).
/// Com <see cref="CoordinationState.Idle"/>: dono anterior que não liberou a trava de forma limpa (trava abandonada).</param>
public sealed record CoordinationProbe(CoordinationState State, LeaseHolder? Holder, string? Detail)
{
    public bool AbandonedByCrash => State == CoordinationState.Idle && Holder is not null;
}

/// <summary>
/// Trava de execução em posse deste processo. Enquanto existir, nenhum outro executor (interface, CLI, broker)
/// consegue começar uma execução, recuperar ou reverter. O SO a libera se o processo morrer.
/// </summary>
public interface IRunLease : IDisposable
{
    LeaseHolder Holder { get; }
    bool IsHeld { get; }
    /// <summary>Execução que esta trava protege (null até <see cref="BindRun"/>).</summary>
    Guid? ActiveRunId { get; }
    void BindRun(Guid runId);
    void UnbindRun(Guid runId);
    /// <summary>Lança se a trava foi liberada. Chamado antes de cada escrita no sistema.</summary>
    void EnsureHeld();
}

/// <summary>Coordenação compartilhada entre todos os executores que usam o mesmo diário.</summary>
public interface IRunCoordinator
{
    /// <summary>Tenta obter a trava, esperando até <paramref name="wait"/> (uma sondagem concorrente a segura por instantes).</summary>
    LeaseAcquireResult TryAcquire(string purpose, TimeSpan wait = default);

    /// <summary>Somente leitura: diz se há executor vivo, sem assumir a trava por mais que um instante.</summary>
    CoordinationProbe Probe();
}
