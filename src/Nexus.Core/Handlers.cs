namespace Nexus.Core;

/// <summary>
/// Handler tipado de um tipo de alvo (registro, serviço, arquivo de jogo...).
/// Contrato: Detect lê o estado real; Apply escreve <see cref="ActionInstance.Desired"/>;
/// Restore escreve um snapshot anterior, inclusive "ausente". Nenhum método decide
/// sucesso por código de retorno: o executor sempre relê com Detect.
/// </summary>
public interface IActionHandler
{
    string HandlerKind { get; }

    /// <summary>Validação de entrada e pré-condições específicas do alvo (sem efeitos colaterais).</summary>
    Applicability Assess(ActionInstance action, SystemContext context);

    StateSnapshot Detect(ActionInstance action);

    void Apply(ActionInstance action);

    void Restore(ActionInstance action, StateSnapshot original);
}

public sealed class HandlerRegistry
{
    private readonly Dictionary<string, IActionHandler> _handlers = new(StringComparer.Ordinal);

    public HandlerRegistry Add(IActionHandler handler)
    {
        if (!_handlers.TryAdd(handler.HandlerKind, handler))
            throw new InvalidOperationException($"Handler duplicado: {handler.HandlerKind}");
        return this;
    }

    public IActionHandler Get(string kind) =>
        _handlers.TryGetValue(kind, out var h)
            ? h
            : throw new KeyNotFoundException($"HandlerKind desconhecido: '{kind}'. Catálogo não pode introduzir handlers.");

    public bool Contains(string kind) => _handlers.ContainsKey(kind);
}

/// <summary>Validação comum a todos os handlers: compatibilidade de build e lista de valores permitidos.</summary>
public static class CommonAssessment
{
    public static Applicability Check(ActionInstance action, SystemContext context)
    {
        var d = action.Definition;
        if (d.Classification is Classification.Rejected or Classification.Obsolete or Classification.Incompatible)
            return Applicability.Blocked($"Classificação {d.Classification}: ação não executável.");
        if (d.MinBuild is { } min && context.WindowsBuild < min)
            return Applicability.NotApplicable($"Requer build >= {min}; detectado {context.WindowsBuild}.");
        if (d.MaxBuild is { } max && context.WindowsBuild > max)
            return Applicability.NotApplicable($"Validado só até build {max}; detectado {context.WindowsBuild}.");
        if (d.Privileges == PrivilegeLevel.Admin && !context.IsElevated)
            return Applicability.Blocked("Requer o broker elevado.");
        if (d.AllowedValues is { Count: > 0 } allowed && action.Desired.Exists &&
            !allowed.Contains(action.Desired.Value ?? string.Empty, StringComparer.Ordinal))
            return Applicability.Blocked($"Valor '{action.Desired.Value}' fora da lista permitida pelo catálogo.");
        return Applicability.Ok;
    }
}
