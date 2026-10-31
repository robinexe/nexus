using System.Globalization;
using Nexus.Core;

namespace Nexus.Cleaning;

/// <summary>
/// Base dos handlers que limpam UMA regra para um lote da quarentena (regras embutidas e regras do usuário). Alvo: <c>rule</c>
/// (id da regra), <c>batch</c> (id do lote, escolhido pela interface e validado pela regex do catálogo), <c>scope</c>=HKCU nas
/// regras de perfil (convenção do núcleo para o broker-serviço anexar <c>userSid</c>).
/// <list type="bullet">
/// <item>Detect: lê o manifesto do lote. Sem manifesto (ou restaurado) = <c>CleaningBatch:not-applied</c> (detalhe: arquivos/bytes
/// elegíveis agora); arquivos do lote na quarentena = <c>applied</c>; quarentena esvaziada = <c>purged</c>.</item>
/// <item>Apply: grava o manifesto (lista completa) e move cada elegível para <c>DataDirectory\quarantine\cleaning\&lt;lote&gt;\&lt;regra&gt;</c>,
/// pulando arquivos em uso.</item>
/// <item>Restore: devolve tudo o que está na quarentena (sem sobrescrever) e marca o manifesto como restaurado. Recusa se o
/// lote foi esvaziado (irreversível).</item>
/// </list>
/// O diário guarda o alvo (regra + lote), e o lote aponta de forma determinística para o manifesto: a restauração e a recuperação
/// após uma queda encontram os arquivos só com o que está no diário.
/// </summary>
public abstract class CleaningBatchHandlerBase : IActionHandler, IRestoreValidator
{
    public const string StateType = "CleaningBatch";
    public const string NotApplied = "not-applied";
    public const string Applied = "applied";
    public const string QuarantineMissing = "quarantine-missing";
    /// <summary>A quarentena deste lote foi esvaziada: os arquivos foram apagados de vez e não podem voltar.</summary>
    public const string Purged = "purged";

    private readonly Func<string?, CleaningFolders?> _folders;
    private readonly IProcessProbe _processes;
    private readonly QuarantineMover _mover;

    private protected CleaningBatchHandlerBase(string dataDirectory, Func<string?, CleaningFolders?> folders, IProcessProbe processes,
        bool otherAccount, IQuarantineFileOps ops, TimeProvider? clock)
    {
        DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        ArgumentNullException.ThrowIfNull(ops);
        OtherAccount = otherAccount;
        Clock = clock ?? TimeProvider.System;
        _mover = new QuarantineMover(ops, Clock);
    }

    protected string DataDirectory { get; }
    protected bool OtherAccount { get; }
    protected TimeProvider Clock { get; }

    public abstract string HandlerKind { get; }

    /// <summary>Acha a regra do alvo e confere que a ação do catálogo corresponde a ela. <paramref name="forRestore"/>: o desfazer
    /// pode usar a cópia da regra guardada no manifesto.</summary>
    private protected abstract string? ResolveRule(ActionInstance action, string? batch, bool forRestore, out CleaningRule? rule);

    private string? TargetProblem(ActionInstance action, bool forRestore, out CleaningRule? rule, out string? batch, out string? sid)
    {
        rule = null; batch = null; sid = null;
        var t = action.Target;
        if (!t.TryGetValue("batch", out var b) || !CleaningBatchId.IsValid(b)) return "Lote de limpeza inválido.";
        batch = b;
        if (ResolveRule(action, b, forRestore, out var r) is { } problem) return problem;
        rule = r!;
        if (r!.Mode != CleaningMode.Quarantine) return "Esta regra só mede; a limpeza é feita pela ferramenta do Windows.";
        var expectedPrivilege = r.Admin ? PrivilegeLevel.Admin : PrivilegeLevel.User;
        if (action.Definition.Privileges != expectedPrivilege) return "Privilégio do catálogo diferente do exigido pela regra.";
        if (t.TryGetValue(UserProfileScope.TargetKey, out var s))
        {
            if (r.Scope != CleaningScope.User || !UserProfileScope.IsValidSid(s)) return "Perfil de usuário inválido no alvo.";
            sid = s;
        }
        else if (r.Scope == CleaningScope.User && OtherAccount)
        {
            return "O aplicador roda com outra conta e o pedido não identificou o seu perfil; nada foi lido.";
        }
        if (r.Scope == CleaningScope.User && !(t.TryGetValue("scope", out var scope) && scope == "HKCU"))
            return "Regra de perfil sem escopo de usuário no catálogo.";
        return null;
    }

    private CleaningFolders Folders(string? sid) =>
        (_folders(sid) ?? throw new InvalidOperationException("Pastas do perfil indisponíveis.")) with
        {
            ExtraForbidden = [Path.Combine(DataDirectory, "quarantine")],
        };

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, false, out var rule, out _, out _) is { } problem) return Applicability.Blocked(problem);
        var running = rule!.CloseApps.Where(_processes.IsRunning).ToList();
        if (running.Count > 0)
            return Applicability.Blocked($"Feche {string.Join(", ", running)} antes: os arquivos de um programa aberto não são limpos.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, true, out var rule, out var batch, out var sid) is { } problem) throw new InvalidOperationException(problem);
        var ruleDir = QuarantineStore.RuleDirectory(DataDirectory, batch!, rule!.QuarantineKey);
        var manifest = QuarantineStore.ReadManifest(ruleDir);
        if (manifest is not null && manifest.Status != QuarantineManifest.Restored)
        {
            var present = QuarantineStore.PresentInQuarantine(ruleDir, manifest);
            var detail = new Dictionary<string, string>
            {
                ["manifest"] = Path.Combine(ruleDir, QuarantineStore.ManifestFile),
                ["inQuarantine"] = present.ToString(CultureInfo.InvariantCulture),
                ["movedBytes"] = manifest.MovedBytes.ToString(CultureInfo.InvariantCulture),
                ["skippedInUse"] = manifest.SkippedInUse.ToString(CultureInfo.InvariantCulture),
                ["status"] = manifest.Status,
            };
            if (QuarantineManifest.IsPurge(manifest.Status)) return StateSnapshot.Of(StateType, Purged, ValueOrigin.Explicit, detail);
            if (present > 0) return StateSnapshot.Of(StateType, Applied, ValueOrigin.Explicit, detail);
            if (manifest.Status == QuarantineManifest.Applied)
                return StateSnapshot.Of(StateType, manifest.MovedFiles == 0 ? Applied : QuarantineMissing, ValueOrigin.Explicit, detail);
            // "applying" sem nenhum arquivo movido: nada saiu do lugar.
        }

        var folders = Folders(sid);
        var roots = CleaningPaths.Expand(rule, folders);
        long files = 0, bytes = 0;
        foreach (var c in CleaningEnumerator.Enumerate(rule, roots, Clock.GetUtcNow().UtcDateTime))
        {
            files++;
            bytes += c.Size;
        }
        return StateSnapshot.Of(StateType, NotApplied, ValueOrigin.Explicit, new Dictionary<string, string>
        {
            ["eligibleFiles"] = files.ToString(CultureInfo.InvariantCulture),
            ["eligibleBytes"] = bytes.ToString(CultureInfo.InvariantCulture),
            ["roots"] = roots.Count.ToString(CultureInfo.InvariantCulture),
        });
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, false, out var rule, out var batch, out var sid) is { } problem) throw new InvalidOperationException(problem);
        if (!action.Desired.Exists || action.Desired.Value != Applied) throw new InvalidOperationException("Valor desejado inválido para a limpeza.");
        var running = rule!.CloseApps.Where(_processes.IsRunning).ToList();
        if (running.Count > 0) throw new InvalidOperationException($"{string.Join(", ", running)} abriu durante a limpeza; nada foi movido.");
        _mover.Apply(rule, Folders(sid), DataDirectory, batch!, sid);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } refused) throw new InvalidOperationException(refused);
        TargetProblem(action, true, out var rule, out var batch, out var sid);
        _mover.Restore(rule!, Folders(sid), DataDirectory, batch!);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (TargetProblem(action, true, out var rule, out var batch, out _) is { } problem) return problem;
        if (!original.Exists || original.Type != StateType || original.Value != NotApplied)
            return "Só é possível restaurar para o estado anterior à limpeza (not-applied).";
        try
        {
            if (QuarantineStore.ReadManifest(QuarantineStore.RuleDirectory(DataDirectory, batch!, rule!.QuarantineKey)) is { } m && QuarantineManifest.IsPurge(m.Status))
                return "A quarentena deste lote foi esvaziada: os arquivos foram apagados de vez e não há o que devolver.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return "Manifesto do lote ilegível: " + ex.Message;
        }
        return null;
    }
}

/// <summary>Handler "CleaningRule": regras embutidas (uma ação <c>cleaning.&lt;regra&gt;</c> por regra no catálogo).</summary>
public sealed class CleaningRuleHandler : CleaningBatchHandlerBase
{
    public const string Kind = CleaningRuleSet.HandlerKind;

    private readonly CleaningRuleSet _rules;

    /// <param name="folders">Pastas para um perfil: recebe o SID do alvo (null = o próprio processo). Null = indisponível.</param>
    /// <param name="otherAccount">O aplicador roda com outra conta: regras de perfil exigem SID no alvo.</param>
    public CleaningRuleHandler(CleaningRuleSet rules, string dataDirectory, Func<string?, CleaningFolders?> folders, IProcessProbe processes,
        bool otherAccount, TimeProvider? clock = null)
        : this(rules, dataDirectory, folders, processes, otherAccount, RealQuarantineFileOps.Instance, clock) { }

    internal CleaningRuleHandler(CleaningRuleSet rules, string dataDirectory, Func<string?, CleaningFolders?> folders, IProcessProbe processes,
        bool otherAccount, IQuarantineFileOps ops, TimeProvider? clock = null)
        : base(dataDirectory, folders, processes, otherAccount, ops, clock)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public override string HandlerKind => Kind;

    private protected override string? ResolveRule(ActionInstance action, string? batch, bool forRestore, out CleaningRule? rule)
    {
        rule = null;
        if (!action.Target.TryGetValue("rule", out var id) || _rules.Find(id) is not { } r) return "Regra de limpeza desconhecida.";
        if (r.IsUserRule) return "Regra do usuário não vale nesta ação.";
        if (action.Definition.Id != r.ActionId) return "A ação do catálogo não corresponde à regra.";
        rule = r;
        return null;
    }
}

/// <summary>
/// Handler "CleaningUserRule": regras escritas pelo usuário (<c>%AppData%\NEXUS\rules\*.json</c>), pela ação genérica
/// <c>cleaning.user-rule</c> com o parâmetro <c>rule</c>. Segurança:
/// <list type="bullet">
/// <item>NUNCA no aplicador elevado nem no serviço/outra conta: uma regra que o próprio usuário escreve não pode virar um jeito de
/// mover arquivos com privilégio (escalada). Lá, tudo é recusado.</item>
/// <item>Só ids de regras carregadas e válidas agora (relidas a cada pedido, com a mesma validação da página).</item>
/// <item>O desfazer usa a cópia da regra guardada no manifesto (revalidada) se o arquivo foi apagado ou mudou.</item>
/// </list>
/// </summary>
public sealed class CleaningUserRuleHandler : CleaningBatchHandlerBase
{
    public const string Kind = "CleaningUserRule";
    public const string ActionId = "cleaning.user-rule";

    private readonly Func<UserRuleLoad?> _load;
    private readonly bool _elevated;

    /// <param name="load">Lê as regras do usuário (null = indisponível). Chamado a cada pedido.</param>
    public CleaningUserRuleHandler(Func<UserRuleLoad?> load, string dataDirectory, Func<string?, CleaningFolders?> folders, IProcessProbe processes,
        bool otherAccount, bool elevated, TimeProvider? clock = null)
        : this(load, dataDirectory, folders, processes, otherAccount, elevated, RealQuarantineFileOps.Instance, clock) { }

    internal CleaningUserRuleHandler(Func<UserRuleLoad?> load, string dataDirectory, Func<string?, CleaningFolders?> folders, IProcessProbe processes,
        bool otherAccount, bool elevated, IQuarantineFileOps ops, TimeProvider? clock = null)
        : base(dataDirectory, folders, processes, otherAccount, ops, clock)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _elevated = elevated;
    }

    public override string HandlerKind => Kind;

    private protected override string? ResolveRule(ActionInstance action, string? batch, bool forRestore, out CleaningRule? rule)
    {
        rule = null;
        if (_elevated || OtherAccount)
            return "Regras do usuário só valem no aplicador da sua própria conta, sem administrador: nunca no aplicador elevado nem no serviço.";
        if (action.Definition.Id != ActionId || action.Definition.Privileges != PrivilegeLevel.User) return "A ação do catálogo não corresponde a uma regra do usuário.";
        if (action.Target.ContainsKey(UserProfileScope.TargetKey)) return "Regras do usuário não aceitam perfil de outra conta.";
        if (!action.Target.TryGetValue("rule", out var id) || id.Length > 48) return "Regra do usuário inválida.";
        var loaded = _load()?.Rules.Find(id);
        if (loaded is { IsUserRule: true })
        {
            rule = loaded;
            return null;
        }
        if (forRestore && batch is not null && FromManifest(id, batch) is { } saved)
        {
            rule = saved;
            return null;
        }
        return $"A regra do usuário '{id}' não está carregada (arquivo removido, desativado ou com erro). Veja Limpeza > Regras.";
    }

    /// <summary>Regra guardada no manifesto do lote, revalidada com a política das regras do usuário.</summary>
    private CleaningRule? FromManifest(string id, string batch)
    {
        try
        {
            var m = QuarantineStore.ReadManifest(QuarantineStore.RuleDirectory(DataDirectory, batch, CleaningRuleSet.UserRuleKeyPrefix + id));
            if (m is null || !m.UserRule || m.Rule != id || string.IsNullOrEmpty(m.RuleSnapshot)) return null;
            var set = CleaningRuleSet.ParseUserRules("{\"rules\":[" + m.RuleSnapshot + "]}", out _);
            return set.Find(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException
                                       or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
