using Nexus.Core;
using Nexus.Lab.Gpu.Vendors;
using Nexus.WindowsActions.Power;

namespace Nexus.Tests.Modules;

/// <summary>
/// AMD (ADLX) e Intel (IGCL) num processo separado: o handler só fala com o processo pela linha de saída; "não suportado" vira
/// "não se aplica", erro nunca vira sucesso; desfazer devolve o valor lido. Plano Desempenho máximo do NEXUS: cria, ativa, desfaz.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GpuVendorTests
{
    private static ActionInstance Action(string key)
    {
        var e = Wiring.Entry(key) ?? throw new InvalidOperationException(key + " ausente");
        return e.TryInstantiate(new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);
    }

    private sealed class FakeTool
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal) { ["amd/antilag"] = "off" };
        public List<string[]> Calls { get; } = [];
        public string? ForcedLine { get; set; }

        public (int, string) Run(string[] args)
        {
            Calls.Add(args);
            if (ForcedLine is not null) return (1, ForcedLine);
            if (!Values.TryGetValue(args[1], out var v)) return (3, "unsupported:sem placa");
            if (args[0] == "set") Values[args[1]] = args[2];
            return (0, "ok:" + v);
        }
    }

    [Fact]
    public void Amd_antilag_applies_and_restores_through_the_separate_process()
    {
        var tool = new FakeTool();
        var h = new GpuVendorSettingHandler(tool.Run);
        var a = Action("gpu.amd.antilag.on@1");
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var original = h.Detect(a);
        Assert.Equal("off", original.Value);
        h.Apply(a);
        Assert.Equal("on", tool.Values["amd/antilag"]);
        Assert.Null(h.CheckRestore(a, original));
        h.Restore(a, original);
        Assert.Equal("off", tool.Values["amd/antilag"]);
        Assert.All(tool.Calls, c => Assert.Contains(c[0], new[] { "get", "set" }));
    }

    [Fact]
    public void Missing_vendor_is_not_applicable_and_errors_never_count_as_success()
    {
        var tool = new FakeTool();
        var h = new GpuVendorSettingHandler(tool.Run);
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(Action("gpu.intel.endurance.off@1"), T.Ctx).Kind);
        tool.ForcedLine = "error:AccessViolation";
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action("gpu.amd.antilag.on@1"), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(Action("gpu.amd.antilag.on@1")));
        tool.ForcedLine = "lixo sem prefixo";
        Assert.Throws<InvalidOperationException>(() => h.Apply(Action("gpu.amd.antilag.on@1")));
    }

    [Fact]
    public void Only_listed_settings_and_values_are_accepted_and_the_catalog_never_caps_fps()
    {
        if (OperatingSystem.IsWindows()) Assert.Equal("error:ajuste ou valor fora da lista", GpuVendorTool.Run(["set", "amd/boost", "on"]));
        Assert.False(GpuVendorTool.IsValid("amd/frtc", "60"));
        var mine = Wiring.Catalog.Actions.Where(e => e.Definition.HandlerKind == GpuVendorSettingHandler.Kind).ToList();
        Assert.Equal(7, mine.Count);
        foreach (var e in mine) Assert.True(GpuVendorTool.IsValid(e.TargetTemplate["setting"], e.DesiredValue), e.Definition.Id);
        // Nenhum liga limite: FRTC e limite da Intel só "off"; Chill (reduz FPS) só "off"; Endurance só "0".
        Assert.Equal("off", mine.Single(e => e.TargetTemplate["setting"] == "amd/frtc").DesiredValue);
        Assert.Equal("off", mine.Single(e => e.TargetTemplate["setting"] == "intel/framelimit").DesiredValue);
        Assert.Equal("off", mine.Single(e => e.TargetTemplate["setting"] == "amd/chill").DesiredValue);
        Assert.Equal("0", mine.Single(e => e.TargetTemplate["setting"] == "intel/endurance").DesiredValue);
    }

    private sealed class FakeSchemes : IPowerSchemeFactory
    {
        public List<Guid> Schemes { get; } = [new("381b4222-f694-41f0-9685-ff5bb260df2e")];
        public Guid Active { get; set; } = new("381b4222-f694-41f0-9685-ff5bb260df2e");
        public IReadOnlyList<Guid> EnumerateSchemes() => Schemes;
        public Guid GetActiveScheme() => Active;
        public void SetActiveScheme(Guid scheme) => Active = scheme;
        public void DuplicateScheme(Guid source, Guid destination) => Schemes.Add(destination);
        public void DeleteScheme(Guid scheme) => Schemes.Remove(scheme);
    }

    [Fact]
    public void Ultimate_plan_is_created_activated_and_undo_returns_and_deletes_it()
    {
        var power = new FakeSchemes();
        var h = new PowerUltimateSchemeHandler(power);
        var a = Action("fps.power.plan.ultimate@1");
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var original = h.Detect(a);
        h.Apply(a);
        Assert.Equal(PowerUltimateSchemeHandler.NexusUltimate, power.Active);
        Assert.Contains(PowerUltimateSchemeHandler.NexusUltimate, power.Schemes);
        h.Restore(a, original);
        Assert.Equal(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), power.Active);
        Assert.DoesNotContain(PowerUltimateSchemeHandler.NexusUltimate, power.Schemes);
    }
}
