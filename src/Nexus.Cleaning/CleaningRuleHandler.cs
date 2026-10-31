using System.Globalization;
using Nexus.Core;

namespace Nexus.Cleaning;

/// <summary>
/// Handler "CleaningRule". Alvo: <c>rule</c> (id da regra), <c>batch</c> (id do lote, escolhido pela interface e validado pela regex
/// do catálogo), <c>scope</c>=HKCU nas regras de perfil (convenção do núcleo para o broker-serviço anexar <c>userSid</c>).
/// <list type="bullet">
/// <item>Detect: lê o manifesto do lote. Sem manifesto (ou restaurado) = <c>CleaningBatch:not-applied</c> (detalhe: arquivos/bytes
/// elegíveis agora); arquivos do lote na quarentena = <c>applied</c>.</item>
/// <item>Apply: grava o manifesto (lista completa) e move cada elegível para <c>DataDirectory\quarantine\cleaning\&lt;lote&gt;\&lt;regra&gt;</c>,
/// pulando arquivos em uso.</item>
/// <item>Restore: devolve tudo o que está na quarentena (sem sobrescrever) e marca o manifesto como restaurado.</item>
/// </list>
/// O diário guarda o alvo (regra + lote), e o lote aponta de forma determinística para o manifesto: a restauração e a recuperação
/// após uma queda encontram os arquivos só com o que está no diário.
/// </summary>
public sealed class CleaningRuleHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = CleaningRuleSet.HandlerKind;
    public const string StateType = "CleaningBatch";
    public const string NotApplied = "not-applied";
    public const string Applied = "applied";
    public const string QuarantineMissing = "quarantine-missing";

    private readonly CleaningRuleSet _rules;
    private readonly string _dataDirectory;
    private readonly Func<string?, CleaningFolders?> _folders;
    private readonly IProcessProbe _processes;
    private readonly bool _otherAccount;
    private readonly QuarantineMover _mover;
    private readonly TimeProvider _clock;

    /// <param name="folders">Pastas para um perfil: recebe o SID do alvo (null = o próprio processo). Null = indisponível.</param>
    /// <param name="otherAccount">O aplicador roda com outra conta: regras de perfil exigem SID no alvo.</param>
    public CleaningRuleHandler(CleaningRuleSet rules, string dataDirectory, Func<string?, CleaningFolders?> folders, IProcessProbe processes,
        bool otherAccount, TimeProvider? clock = null)
        : this(rules, dataDirectory, folders, processes, otherAccount, RealQuarantineFileOps.Instance, clock) { }

    internal CleaningRuleHandler(CleaningRuleSet rules, string dataDirectory, Func<string?, CleaningFolders?> folders, IProcessProbe processes,
        bool otherAccount, IQuarantineFileOps ops, TimeProvider? clock = null)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _otherAccount = otherAccount;
        _clock = clock ?? TimeProvider.System;
        _mover = new QuarantineMover(ops, _clock);
    }

    public string HandlerKind => Kind;

    private string? TargetProblem(ActionInstance action, out CleaningRule? rule, out string? batch, out string? sid)
    {
        rule = null; batch = null; sid = null;
        var t = action.Target;
        if (!t.TryGetValue("rule", out var id) || _rules.Find(id) is not { } r) return "Regra de limpeza desconhecida.";
        rule = r;
        if (r.Mode != CleaningMode.Quarantine) return "Esta regra só mede; a limpeza é feita pela ferramenta do Windows.";
        if (action.Definition.Id != r.ActionId) return "A ação do catálogo não corresponde à regra.";
        if (!t.TryGetValue("batch", out var b) || !CleaningBatchId.IsValid(b)) return "Lote de limpeza inválido.";
        batch = b;
        var expectedPrivilege = r.Admin ? PrivilegeLevel.Admin : PrivilegeLevel.User;
        if (action.Definition.Privileges != expectedPrivilege) return "Privilégio do catálogo diferente do exigido pela regra.";
        if (t.TryGetValue(UserProfileScope.TargetKey, out var s))
        {
            if (r.Scope != CleaningScope.User || !UserProfileScope.IsValidSid(s)) return "Perfil de usuário inválido no alvo.";
            sid = s;
        }
        else if (r.Scope == CleaningScope.User && _otherAccount)
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
            ExtraForbidden = [Path.Combine(_dataDirectory, "quarantine")],
        };

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, out var rule, out _, out _) is { } problem) return Applicability.Blocked(problem);
        var running = rule!.CloseApps.Where(_processes.IsRunning).ToList();
        if (running.Count > 0)
            return Applicability.Blocked($"Feche {string.Join(", ", running)} antes: os arquivos de um programa aberto não são limpos.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action, out var rule, out var batch, out var sid) is { } problem) throw new InvalidOperationException(problem);
        var ruleDir = QuarantineStore.RuleDirectory(_dataDirectory, batch!, rule!.Id);
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
            if (present > 0) return StateSnapshot.Of(StateType, Applied, ValueOrigin.Explicit, detail);
            if (manifest.Status == QuarantineManifest.Applied)
                return StateSnapshot.Of(StateType, manifest.MovedFiles == 0 ? Applied : QuarantineMissing, ValueOrigin.Explicit, detail);
            // "applying" sem nenhum arquivo movido: nada saiu do lugar.
        }

        var folders = Folders(sid);
        var roots = CleaningPaths.Expand(rule, folders);
        long files = 0, bytes = 0;
        foreach (var c in CleaningEnumerator.Enumerate(rule, roots, _clock.GetUtcNow().UtcDateTime))
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
        if (TargetProblem(action, out var rule, out var batch, out var sid) is { } problem) throw new InvalidOperationException(problem);
        if (!action.Desired.Exists || action.Desired.Value != Applied) throw new InvalidOperationException("Valor desejado inválido para CleaningRule.");
        var running = rule!.CloseApps.Where(_processes.IsRunning).ToList();
        if (running.Count > 0) throw new InvalidOperationException($"{string.Join(", ", running)} abriu durante a limpeza; nada foi movido.");
        _mover.Apply(rule, Folders(sid), _dataDirectory, batch!, sid);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } refused) throw new InvalidOperationException(refused);
        TargetProblem(action, out var rule, out var batch, out var sid);
        _mover.Restore(rule!, Folders(sid), _dataDirectory, batch!);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (TargetProblem(action, out _, out _, out _) is { } problem) return problem;
        if (!original.Exists || original.Type != StateType || original.Value != NotApplied)
            return "Só é possível restaurar para o estado anterior à limpeza (not-applied).";
        return null;
    }
}
