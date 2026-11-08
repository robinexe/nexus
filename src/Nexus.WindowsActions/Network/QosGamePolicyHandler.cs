using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.WindowsActions.Network;

/// <summary>
/// Handler "QosGamePolicy": política de QoS local para UM executável de jogo (a mesma que a Política de Grupo "QoS baseado em
/// política" grava), marcando os pacotes dele com DSCP 46. Chave fixa <c>HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\NEXUS
/// &lt;exe sem .exe&gt;</c>, sempre com os mesmos 11 valores (todas as portas/IPs, protocolo qualquer, sem limite de banda).
/// <list type="bullet">
/// <item>Alvo: <c>exe</c> = só o nome do arquivo (sem pasta). Desejado: Type "Dscp", valor "46" (único aceito).</item>
/// <item>Uma política com o mesmo nome que NÃO aponta para este exe é de outra pessoa/ferramenta: recusa.</item>
/// <item>Desfazer: sem política antes = apaga os 11 valores e a chave (se ficar vazia); com política antes = devolve o DSCP.</item>
/// </list>
/// </summary>
public sealed partial class QosGamePolicyHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "QosGamePolicy";
    public const string StateType = "Dscp";
    public const string Root = @"SOFTWARE\Policies\Microsoft\Windows\QoS";

    private readonly IRegistryBackend _registry;
    public QosGamePolicyHandler(IRegistryBackend registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    public string HandlerKind => Kind;

    [GeneratedRegex(@"\A(?i)[A-Za-z0-9 ._()+\-]{1,64}\.exe\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExeName();

    /// <summary>Os valores da política, além do DSCP e do nome do aplicativo (todos REG_SZ, como a Política de Grupo grava).</summary>
    public static IReadOnlyList<(string Name, string Value)> FixedValues { get; } =
    [
        ("Version", "1.0"), ("Protocol", "*"), ("Local Port", "*"), ("Local IP", "*"), ("Local IP Prefix Length", "*"),
        ("Remote Port", "*"), ("Remote IP", "*"), ("Remote IP Prefix Length", "*"), ("Throttle Rate", "-1"),
    ];

    private static string? Exe(ActionInstance a) =>
        a.Target.Count == 1 && a.Target.TryGetValue("exe", out var e) && ExeName().IsMatch(e) ? e : null;

    public static string KeyPath(string exe) => Root + @"\NEXUS " + Path.GetFileNameWithoutExtension(exe);

    private static RegistryTarget T(string exe, string name) => new(RegHive.HKLM, KeyPath(exe), name, RegView.Registry64);

    private string? Read(string exe, string name)
    {
        var r = _registry.Read(T(exe, name));
        return r.ValueExists ? r.Value : null;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (Exe(action) is not { } exe) return Applicability.Blocked("Executável inválido (só o nome do arquivo, como cs2.exe).");
        if (action.Desired.Type != StateType || action.Desired.Value != "46") return Applicability.Blocked("Só o DSCP 46 é aceito.");
        if (Read(exe, "Application Name") is { } app && !string.Equals(app, exe, StringComparison.OrdinalIgnoreCase))
            return Applicability.Blocked($"Já existe uma política de QoS com este nome para outro programa ({app}); nada foi mudado.");
        return Applicability.Ok;
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (Exe(action) is null) return "Executável inválido no diário.";
        if (original.Exists && (original.Type != StateType || !int.TryParse(original.Value, out var v) || v is < 0 or > 63))
            return "DSCP original inválido no diário.";
        return null;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var exe = Exe(action) ?? throw new InvalidOperationException("Executável inválido.");
        var app = Read(exe, "Application Name");
        var dscp = Read(exe, "DSCP Value");
        if (app is null || dscp is null || !string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)) return StateSnapshot.Absent();
        return StateSnapshot.Of(StateType, dscp, ValueOrigin.Explicit);
    }

    public void Apply(ActionInstance action)
    {
        var exe = Exe(action) ?? throw new InvalidOperationException("Executável inválido.");
        if (action.Desired.Value != "46") throw new InvalidOperationException("Só o DSCP 46 é aceito.");
        foreach (var (name, value) in FixedValues) _registry.Write(T(exe, name), "String", value);
        _registry.Write(T(exe, "Application Name"), "String", exe);
        _registry.Write(T(exe, "DSCP Value"), "String", "46"); // por último: a política só fica completa no fim
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var exe = Exe(action) ?? throw new InvalidOperationException("Executável inválido.");
        if (original.Exists)
        {
            _registry.Write(T(exe, "DSCP Value"), "String", original.Value!);
            return;
        }
        _registry.DeleteValue(T(exe, "DSCP Value")); // primeiro: sem DSCP a política deixa de valer
        _registry.DeleteValue(T(exe, "Application Name"));
        foreach (var (name, _) in FixedValues) _registry.DeleteValue(T(exe, name));
        _registry.DeleteKeyIfEmpty(RegHive.HKLM, KeyPath(exe), RegView.Registry64);
    }
}
