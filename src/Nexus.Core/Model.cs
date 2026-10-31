namespace Nexus.Core;

/// <summary>Classificação de cada ajuste no catálogo (seção 11 da extensão).</summary>
public enum Classification
{
    SupportedDocumented,
    Conditional,
    Experimental,
    Obsolete,
    Ineffective,
    Incompatible,
    Rejected,
}

public enum RebootRequirement { None, GameRestart, NextLogon, Recommended, Required }

/// <summary>Como a mudança vive no sistema.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Session"/>: revertida ao encerrar a sessão (o broker segura a trava até EndSession).</item>
/// <item><see cref="Persistent"/>: fica até ser desfeita pelo diário.</item>
/// <item><see cref="Ephemeral"/>: operação SEM estado anterior a guardar (limpar cache DNS/ARP, exportar drivers, criar ponto de
/// restauração). O executor não lê nem compara estado: executa, registra no diário e marca Verified se o handler não lançar.
/// Não dispara o ponto de restauração automático, não prende sessão, desfazer responde "nada a desfazer" explicitamente
/// (<see cref="ActionExecutor.EphemeralNothingToUndo"/>) sem chamar o handler, e a recuperação encerra entradas interrompidas
/// como <see cref="JournalState.Accepted"/>. Não pode ser irreversível nem de sessão (o catálogo recusa).</item>
/// </list>
/// </remarks>
public enum PersistenceKind { Session, Persistent, Ephemeral }

public enum PrivilegeLevel { User, Admin }

/// <summary>De onde vem o valor observado. Distingue ausente, explícito, herdado, política e terceiros.</summary>
public enum ValueOrigin
{
    Absent,
    Explicit,
    Inherited,
    PolicyEnforced,
    ThirdPartyManaged,
    Unknown,
}

/// <summary>
/// Estado observado de um alvo. <see cref="Value"/> é uma representação canônica em texto
/// (DWORD em decimal, binário em hexadecimal minúsculo) para que comparação e serialização
/// sejam determinísticas. <see cref="Exists"/> = false significa "valor ausente", que é
/// diferente de qualquer valor padrão presumido.
/// </summary>
public sealed record StateSnapshot(
    bool Exists,
    string? Type,
    string? Value,
    ValueOrigin Origin,
    IReadOnlyDictionary<string, string>? Detail = null)
{
    public static StateSnapshot Absent(IReadOnlyDictionary<string, string>? detail = null) =>
        new(false, null, null, ValueOrigin.Absent, detail);

    public static StateSnapshot Of(string type, string value, ValueOrigin origin = ValueOrigin.Explicit,
        IReadOnlyDictionary<string, string>? detail = null) =>
        new(true, type, value, origin, detail);

    /// <summary>Igualdade semântica: existência, tipo e valor. Origem e detalhes não entram.</summary>
    public bool SameValueAs(StateSnapshot other) =>
        Exists == other.Exists &&
        (!Exists || (string.Equals(Type, other.Type, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(Value, other.Value, StringComparison.Ordinal)));

    public string Describe() => Exists ? $"{Type}:{Value}" : "<ausente>";
}

public enum EvidenceLevel { VendorDocumented, Mechanistic, Observed, Community, Hypothesis }

public sealed record EvidenceRef(EvidenceLevel Level, string Source);

/// <summary>Definição declarativa vinda do catálogo. Não contém código.</summary>
public sealed record ActionDefinition(
    string Id,
    int Version,
    string Title,
    Classification Classification,
    string HandlerKind,
    string Mechanism,
    IReadOnlyList<EvidenceRef> Evidence,
    string Risks,
    PrivilegeLevel Privileges,
    PersistenceKind Persistence,
    RebootRequirement Reboot,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<string>? ConflictsWith = null,
    IReadOnlyList<string>? AllowedValues = null,
    int? MinBuild = null,
    int? MaxBuild = null)
{
    public string Key => $"{Id}@{Version}";

    /// <summary>
    /// Ação IRREVERSÍVEL (campo <c>"irreversible": true</c> do catálogo; ex.: exclusão segura). Contrato:
    /// <list type="bullet">
    /// <item>o executor só a aplica com confirmação explícita para esta chave (<see cref="ActionExecutor.Execute(Guid, Plan, SystemContext, IReadOnlyCollection{string})"/>);
    /// no broker, o pedido precisa trazer <c>ConfirmIrreversible = true</c>;</item>
    /// <item>Restore nunca é chamado: desfazer devolve <see cref="RevertOutcome.Irreversible"/> com mensagem clara;</item>
    /// <item>a recuperação não tenta restaurar: encerra a entrada como <see cref="JournalState.Accepted"/>, com nota no diário;</item>
    /// <item>não pode ser de sessão (a sessão é revertida ao encerrar).</item>
    /// </list>
    /// Fica no diário junto da ação (JSON), então uma entrada antiga sem o campo continua reversível como antes.
    /// </summary>
    public bool Irreversible { get; init; }
}

/// <summary>
/// Ação concreta: definição + alvo resolvido (placeholders já substituídos) + estado desejado.
/// É o que vai para o diário e o que a CLI de recuperação reidrata.
/// </summary>
public sealed record ActionInstance(
    ActionDefinition Definition,
    IReadOnlyDictionary<string, string> Target,
    StateSnapshot Desired)
{
    public string TargetDescription =>
        string.Join(" | ", Target.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));

    /// <summary>
    /// Identidade da instância num pedido/plano: (id da ação, alvo resolvido). A mesma ação pode aparecer várias vezes com alvos
    /// diferentes (remoção em lote); a mesma ação com o MESMO alvo é duplicata exata e é recusada (o valor desejado não entra:
    /// dois valores para o mesmo alvo são contraditórios).
    /// </summary>
    public string InstanceKey => Definition.Id + "\u0001" + TargetDescription;

    /// <summary>Alvo para exibição, sem o SID do perfil (acrescentado pelo broker como serviço).</summary>
    public string TargetLabel =>
        string.Join(" | ", Target.Where(k => k.Key != UserProfileScope.TargetKey).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}"));
}

public enum ApplicabilityKind { Applicable, NotApplicable, Blocked }

public sealed record Applicability(ApplicabilityKind Kind, string? Reason = null)
{
    public static readonly Applicability Ok = new(ApplicabilityKind.Applicable);
    public static Applicability NotApplicable(string reason) => new(ApplicabilityKind.NotApplicable, reason);
    public static Applicability Blocked(string reason) => new(ApplicabilityKind.Blocked, reason);
}

/// <summary>Contexto do sistema usado pelas pré-condições (preenchido pelo HardwareDiscovery).</summary>
public sealed record SystemContext(int WindowsBuild, string CpuModel, string? GpuVendor, bool IsElevated);
