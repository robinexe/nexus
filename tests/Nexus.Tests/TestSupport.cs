using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

internal static class T
{
    public static readonly SystemContext Ctx = new(WindowsBuild: 26100, CpuModel: "test", GpuVendor: "NVIDIA", IsElevated: true);

    public static ActionDefinition Def(string id, string handler, PrivilegeLevel priv = PrivilegeLevel.User,
        RebootRequirement reboot = RebootRequirement.None, PersistenceKind persistence = PersistenceKind.Persistent,
        string[]? dependsOn = null, string[]? conflicts = null, string[]? allowed = null,
        Classification classification = Classification.SupportedDocumented) =>
        new(id, 1, id, classification, handler, "teste", [new EvidenceRef(EvidenceLevel.Hypothesis, "teste")], "nenhum",
            priv, persistence, reboot, dependsOn, conflicts, allowed);

    public static ActionInstance RegAction(string id, string path, string name, uint value, RegHive hive = RegHive.HKCU,
        string[]? dependsOn = null, string[]? conflicts = null, RebootRequirement reboot = RebootRequirement.None,
        PersistenceKind persistence = PersistenceKind.Persistent, Dictionary<string, string>? extraTarget = null) =>
        new(Def(id, RegistryValueHandler.Kind, dependsOn: dependsOn, conflicts: conflicts, reboot: reboot, persistence: persistence),
            Target(hive, path, name, extraTarget),
            StateSnapshot.Of("DWord", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static Dictionary<string, string> Target(RegHive hive, string path, string name, Dictionary<string, string>? extra)
    {
        var t = new Dictionary<string, string> { ["hive"] = hive.ToString(), ["path"] = path, ["name"] = name, ["type"] = "DWord" };
        if (extra is not null) foreach (var kv in extra) t[kv.Key] = kv.Value;
        return t;
    }

    public static (InMemoryRegistryBackend Reg, HandlerRegistry Handlers, InMemoryJournal Journal, ActionExecutor Exec, DesiredStateResolver Resolver) Engine()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var journal = new InMemoryJournal();
        return (reg, handlers, journal, new ActionExecutor(handlers, journal), new DesiredStateResolver(handlers));
    }

    public static RegistryTarget RT(string path, string name, RegHive hive = RegHive.HKCU) => new(hive, path, name, RegView.Default);
}

/// <summary>Handler que "aceita" a escrita mas grava outro valor — simula API que retorna sucesso sem efeito.</summary>
internal sealed class LyingHandler : IActionHandler
{
    private StateSnapshot _state = StateSnapshot.Of("DWord", "0");
    public string HandlerKind => "Lying";
    public Applicability Assess(ActionInstance action, SystemContext context) => Applicability.Ok;
    public StateSnapshot Detect(ActionInstance action) => _state;
    public void Apply(ActionInstance action) => _state = StateSnapshot.Of("DWord", "999");
    public void Restore(ActionInstance action, StateSnapshot original) => _state = original;
}
