using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;

namespace Nexus.Boot.Handlers;

/// <summary>
/// "BootServiceStartType": tipo de início de um serviço escolhido pelo usuário (ou de um preset). Reaproveita o
/// <see cref="ServiceStartTypeHandler"/> (mesma leitura/escrita/restauração, inclusive Automático atrasado) e acrescenta as
/// barreiras do módulo: lista proibida (<see cref="ServicePolicy"/>), nome com formato estrito, só serviços Win32 (nunca
/// drivers) e nunca serviços por usuário.
/// </summary>
public sealed partial class BootServiceStartTypeHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "BootServiceStartType";
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private readonly ServiceStartTypeHandler _inner;
    private readonly IRegistryBackend _registry;

    public BootServiceStartTypeHandler(IServiceBackend services, IRegistryBackend registry)
    {
        _inner = new ServiceStartTypeHandler(services);
        _registry = registry;
    }

    public string HandlerKind => Kind;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]([A-Za-z0-9_.\- ]{0,78}[A-Za-z0-9_.\-])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    private string? Problem(ActionInstance action, string? desired)
    {
        if (!action.Target.TryGetValue("service", out var name) || !IsValidName(name)) return "Nome de serviço inválido.";
        if (ServicePolicy.Check(name, desired) is { } policy) return policy;
        var type = _registry.Read(new RegistryTarget(RegHive.HKLM, $@"{ServicesKey}\{name}", "Type", RegView.Default));
        if (type.ValueExists)
        {
            if (!int.TryParse(type.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && !TryUInt(type.Value, out t))
                return "Tipo do serviço ilegível.";
            if (ServicePolicy.IsPerUserService(t)) return "Serviço por usuário: o Windows recria a cada logon; não é alterado.";
            if (!ServicePolicy.IsWin32Service(t)) return "É um driver, não um serviço: drivers não são alterados.";
        }
        return null;
    }

    private static bool TryUInt(string? s, out int value)
    {
        value = 0;
        if (!uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var u)) return false;
        value = unchecked((int)u);
        return true;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (Problem(action, action.Desired.Value) is { } p) return Applicability.Blocked(p);
        return _inner.Assess(action, context);
    }

    public StateSnapshot Detect(ActionInstance action) => _inner.Detect(action);

    public void Apply(ActionInstance action)
    {
        // Revalida no momento da escrita (o executor chama Assess no plano; aqui é a segunda barreira).
        if (Problem(action, action.Desired.Value) is { } p) throw new InvalidOperationException(p);
        _inner.Apply(action);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original) =>
        // Restaurar devolve o estado que o próprio usuário tinha (até Desativado), então a regra "nunca Desativado" não se aplica.
        Problem(action, null) ?? _inner.CheckRestore(action, original);

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        if (CheckRestore(action, original) is { } p) throw new InvalidOperationException(p);
        _inner.Restore(action, original);
    }
}
