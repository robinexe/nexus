namespace Nexus.Core;

/// <summary>
/// Envolve um handler real para uso exclusivo em simulação (dry-run): Assess e Detect são repassados; Apply e Restore
/// lançam <see cref="ExecutionBlockedException"/>. A interface desktop só registra handlers assim, de modo que nenhum
/// caminho de código dela consegue escrever no sistema, mesmo por engano.
/// </summary>
public sealed class SimulationOnlyHandler : IActionHandler
{
    private readonly IActionHandler _inner;

    public SimulationOnlyHandler(IActionHandler inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string HandlerKind => _inner.HandlerKind;
    public Applicability Assess(ActionInstance action, SystemContext context) => _inner.Assess(action, context);
    public StateSnapshot Detect(ActionInstance action) => _inner.Detect(action);

    public void Apply(ActionInstance action) =>
        throw new ExecutionBlockedException($"Aplicação de '{action?.Definition.Key}' bloqueada: modo somente leitura.");

    public void Restore(ActionInstance action, StateSnapshot original) =>
        throw new ExecutionBlockedException($"Restauração de '{action?.Definition.Key}' bloqueada: modo somente leitura.");

    public static HandlerRegistry Wrap(IEnumerable<IActionHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        var reg = new HandlerRegistry();
        foreach (var h in handlers) reg.Add(new SimulationOnlyHandler(h));
        return reg;
    }
}

public sealed class ExecutionBlockedException : InvalidOperationException
{
    public ExecutionBlockedException() { }
    public ExecutionBlockedException(string message) : base(message) { }
    public ExecutionBlockedException(string message, Exception inner) : base(message, inner) { }
}
