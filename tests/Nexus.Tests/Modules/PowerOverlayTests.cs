using Nexus.Core;
using Nexus.WindowsActions.Power;

namespace Nexus.Tests.Modules;

/// <summary>"Modo de energia" do Windows (overlay sobre o Equilibrado): só overlays conhecidos, só com o Equilibrado ativo, desfazer exato.</summary>
[Trait("Category", "Unit")]
public sealed class PowerOverlayTests
{
    private sealed class FakeOverlay : IPowerOverlayBackend
    {
        public Guid? Overlay { get; set; } = Guid.Empty;
        public Guid Scheme { get; set; } = PowerOverlaySchemeHandler.Balanced;
        public Guid? GetEffectiveOverlay() => Overlay;
        public void SetActiveOverlay(Guid overlay) => Overlay = overlay;
        public Guid GetActiveScheme() => Scheme;
    }

    private static ActionInstance Action()
    {
        var e = Wiring.Entry("fps.power.mode.best-performance@1") ?? throw new InvalidOperationException("ação ausente");
        return e.TryInstantiate(new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);
    }

    [Fact]
    public void Best_performance_applies_on_balanced_and_restores_the_previous_mode()
    {
        var power = new FakeOverlay();
        var h = new PowerOverlaySchemeHandler(power);
        var a = Action();
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var original = h.Detect(a);
        h.Apply(a);
        Assert.Equal(PowerOverlaySchemeHandler.BestPerformance, power.Overlay);
        Assert.Null(h.CheckRestore(a, original));
        h.Restore(a, original);
        Assert.Equal(Guid.Empty, power.Overlay);
    }

    [Fact]
    public void Other_plan_or_old_windows_is_not_applicable_and_unknown_modes_are_refused()
    {
        var power = new FakeOverlay { Scheme = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c") }; // Alto desempenho
        var h = new PowerOverlaySchemeHandler(power);
        var a = Action();
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(a, T.Ctx).Kind);
        power.Scheme = PowerOverlaySchemeHandler.Balanced;
        power.Overlay = null;
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(a, T.Ctx).Kind);
        power.Overlay = Guid.Empty;
        var bad = a with { Desired = StateSnapshot.Of("Guid", Guid.NewGuid().ToString("D")) };
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(bad, T.Ctx).Kind);
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of("Guid", "nao-e-guid")));
    }
}
