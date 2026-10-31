using Nexus.Core;

namespace Nexus.Broker;

/// <summary>
/// No broker, restaurações vêm do DIÁRIO (desserializado). Este decorador garante que cada entrada a restaurar corresponde
/// exatamente a uma ação do catálogo do próprio broker: mesma chave id@versão, mesmo handler e o mesmo alvo que o catálogo
/// produz. Um diário adulterado com um alvo arbitrário é recusado antes de qualquer escrita, e a validação do handler
/// (<see cref="IRestoreValidator"/>, política de segurança) roda em seguida.
/// </summary>
public sealed class CatalogBoundHandler : IActionHandler, IRestoreValidator
{
    private readonly IActionHandler _inner;
    private readonly CatalogDocument _catalog;
    private readonly Func<IReadOnlyDictionary<string, string>> _parameters;
    private readonly bool _requireUserProfile;

    /// <param name="requireUserProfile">Broker como serviço: entradas de HKCU sem SID de usuário no alvo são recusadas
    /// (restaurá-las escreveria no perfil do SYSTEM).</param>
    public CatalogBoundHandler(IActionHandler inner, CatalogDocument catalog, Func<IReadOnlyDictionary<string, string>> parameters, bool requireUserProfile = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _requireUserProfile = requireUserProfile;
    }

    public static HandlerRegistry Wrap(HandlerRegistry inner, IEnumerable<string> kinds, CatalogDocument catalog, Func<IReadOnlyDictionary<string, string>> parameters,
        bool requireUserProfile = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var reg = new HandlerRegistry();
        foreach (var k in kinds.Distinct(StringComparer.Ordinal))
            if (inner.Contains(k)) reg.Add(new CatalogBoundHandler(inner.Get(k), catalog, parameters, requireUserProfile));
        return reg;
    }

    public string HandlerKind => _inner.HandlerKind;
    public Applicability Assess(ActionInstance action, SystemContext context) => _inner.Assess(action, context);
    public StateSnapshot Detect(ActionInstance action) => _inner.Detect(action);
    public void Apply(ActionInstance action) => _inner.Apply(action);
    public void Restore(ActionInstance action, StateSnapshot original) => _inner.Restore(action, original);

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        var entry = _catalog.Actions.FirstOrDefault(e => string.Equals(e.Definition.Key, action.Definition.Key, StringComparison.Ordinal));
        if (entry is null)
            return $"A entrada do diário ({action.Definition.Key}) não existe no catálogo do broker ({_catalog.CatalogVersion}).";
        if (!string.Equals(entry.Definition.HandlerKind, action.Definition.HandlerKind, StringComparison.Ordinal))
            return $"Handler do diário ({action.Definition.HandlerKind}) difere do catálogo ({entry.Definition.HandlerKind}).";

        // Alvo esperado: o que o catálogo instancia. O esquema de energia (PowerSettingValue) é capturado no planejamento
        // e pode diferir do ativo agora; ele só precisa ser um GUID válido.
        var parameters = WithScheme(action);
        if (entry.UserParameters.Count > 0)
        {
            // Argumentos do usuário: recuperados do alvo registrado e revalidados pela regra do catálogo.
            var args = entry.UserArgumentsFromTarget(action.Target, out var argError);
            if (args is null) return $"Argumento do usuário inválido no diário: {argError}";
            foreach (var (argName, argValue) in args) parameters[argName] = argValue;
        }
        var probe = (entry.DesiredValue is null && entry.Definition.AllowedValues is { Count: > 0 } allowed ? entry with { DesiredValue = allowed[0] } : entry)
            .TryInstantiate(parameters, out var reason);
        if (probe is null)
            return $"Não foi possível reconstruir o alvo pelo catálogo: {reason}";
        var expected = probe.Target;
        // O perfil do usuário (userSid) é o único campo que o catálogo não define: é acrescentado pelo broker como serviço.
        var recorded = action.Target;
        if (recorded.TryGetValue(UserProfileScope.TargetKey, out var sid))
        {
            if (!UserProfileScope.IsUserScoped(action) || !UserProfileScope.IsValidSid(sid))
                return "Perfil de usuário inválido no diário; restauração recusada.";
            recorded = recorded.Where(kv => kv.Key != UserProfileScope.TargetKey).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
        else if (_requireUserProfile && UserProfileScope.IsUserScoped(action))
        {
            return "Entrada de HKCU sem perfil de usuário registrado: como serviço, restaurá-la escreveria no perfil do SYSTEM. Restaure pelo nexus-recover na sessão do usuário.";
        }
        if (expected.Count != recorded.Count || expected.Any(kv => !recorded.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal)))
            return "O alvo registrado no diário difere do alvo definido pelo catálogo; restauração recusada.";

        return _inner is IRestoreValidator v ? v.CheckRestore(action, original) : null;
    }

    private Dictionary<string, string> WithScheme(ActionInstance action)
    {
        var p = new Dictionary<string, string>(_parameters(), StringComparer.Ordinal);
        if (action.Definition.HandlerKind == "PowerSettingValue" && action.Target.TryGetValue("scheme", out var s) && Guid.TryParse(s, out _))
            p["activeScheme"] = s;
        return p;
    }
}
