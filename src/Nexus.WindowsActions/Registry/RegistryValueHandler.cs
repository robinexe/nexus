using Nexus.Core;

namespace Nexus.WindowsActions.Registry;

/// <summary>
/// Handler "RegistryValue". Alvo: hive, path, name, type, view (opcional).
/// Opcional: policyHive/policyPath/policyName — se esse valor de política existir, a ação fica Bloqueada.
/// Backup guarda se o VALOR e a CHAVE existiam; restaurar "ausente" remove só o valor
/// (e a chave apenas se o NEXUS a criou e ela continua vazia).
/// </summary>
public sealed class RegistryValueHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "RegistryValue";
    private const string KeyExistedDetail = "keyExisted";
    private readonly IRegistryBackend _reg;

    public RegistryValueHandler(IRegistryBackend backend) => _reg = backend;

    public string HandlerKind => Kind;

    public static RegistryTarget TargetOf(ActionInstance a) => new(
        Enum.Parse<RegHive>(a.Target["hive"], ignoreCase: true),
        a.Target["path"],
        a.Target["name"],
        a.Target.TryGetValue("view", out var v) ? Enum.Parse<RegView>(v, ignoreCase: true) : RegView.Default);

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        foreach (var k in new[] { "hive", "path", "name", "type" })
            if (!action.Target.ContainsKey(k)) return Applicability.Blocked($"Alvo incompleto: falta '{k}'.");
        if (!Enum.TryParse<RegHive>(action.Target["hive"], true, out _)) return Applicability.Blocked("Hive não permitida.");
        if (action.Target.TryGetValue("view", out var view) && !Enum.TryParse<RegView>(view, true, out _)) return Applicability.Blocked("Visão de registro inválida.");
        if (action.Target.TryGetValue("policyHive", out var ph) && !Enum.TryParse<RegHive>(ph, true, out _)) return Applicability.Blocked("Hive de política inválida.");
        if (action.Target["type"] is not ("DWord" or "QWord" or "String" or "ExpandString" or "Binary"))
            return Applicability.Blocked("Tipo de valor não permitido para escrita.");
        if (action.Desired.Exists && action.Target["type"] is "DWord" or "QWord" &&
            (!ulong.TryParse(action.Desired.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ||
             n.ToString(System.Globalization.CultureInfo.InvariantCulture) != action.Desired.Value || (action.Target["type"] == "DWord" && n > uint.MaxValue)))
            return Applicability.Blocked("Valor numérico não canônico ou fora da faixa.");

        var problem = SafetyPolicy.CheckRegistry(action.Target["hive"], action.Target["path"], action.Target["name"]);
        if (problem is not null) return Applicability.Blocked(problem);

        if (action.Desired.Exists && !string.Equals(action.Desired.Type, action.Target["type"], StringComparison.OrdinalIgnoreCase))
            return Applicability.Blocked("Tipo do valor desejado difere do tipo declarado no catálogo.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var t = TargetOf(action);
        var r = _reg.Read(t);
        var detail = new Dictionary<string, string> { [KeyExistedDetail] = r.KeyExists ? "true" : "false" };

        if (PolicyShadowExists(action, out var policyDescription))
        {
            detail["policy"] = policyDescription;
            return r.ValueExists
                ? new StateSnapshot(true, r.Kind, r.Value, ValueOrigin.PolicyEnforced, detail)
                : new StateSnapshot(false, null, null, ValueOrigin.PolicyEnforced, detail);
        }

        return r.ValueExists ? StateSnapshot.Of(r.Kind!, r.Value!, ValueOrigin.Explicit, detail) : StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action)
    {
        var t = TargetOf(action);
        if (action.Desired.Exists) _reg.Write(t, action.Desired.Type!, action.Desired.Value!);
        else _reg.DeleteValue(t);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        var t = TargetOf(action);
        if (original.Exists)
        {
            _reg.Write(t, original.Type!, original.Value!);
            return;
        }

        _reg.DeleteValue(t);
        if (original.Detail is not null && original.Detail.TryGetValue(KeyExistedDetail, out var existed) && existed == "false")
            _reg.DeleteKeyIfEmpty(t.Hive, t.Path, t.View);
    }

    private static readonly HashSet<string> RestorableTypes = new(StringComparer.OrdinalIgnoreCase)
        { "DWord", "QWord", "String", "ExpandString", "Binary", "MultiString" };

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        foreach (var k in new[] { "hive", "path", "name", "type" })
            if (!action.Target.ContainsKey(k)) return $"Alvo incompleto: falta '{k}'.";
        if (!Enum.TryParse<RegHive>(action.Target["hive"], true, out _)) return "Hive não permitida.";
        if (action.Target.TryGetValue("view", out var view) && !Enum.TryParse<RegView>(view, true, out _)) return "Visão de registro inválida.";
        if (SafetyPolicy.CheckRegistry(action.Target["hive"], action.Target["path"], action.Target["name"]) is { } problem) return problem;
        if (original.Exists && (original.Type is null || !RestorableTypes.Contains(original.Type) || original.Value is null))
            return $"Tipo original '{original.Type}' não restaurável.";
        return null;
    }

    private bool PolicyShadowExists(ActionInstance a, out string description)
    {
        description = string.Empty;
        if (!a.Target.TryGetValue("policyPath", out var path) || !a.Target.TryGetValue("policyName", out var name)) return false;
        var hive = a.Target.TryGetValue("policyHive", out var h) ? Enum.Parse<RegHive>(h, true) : RegHive.HKLM;
        var r = _reg.Read(new RegistryTarget(hive, path, name, RegView.Default));
        if (!r.ValueExists) return false;
        description = $@"{hive}\{path}\{name}={r.Value}";
        return true;
    }
}
