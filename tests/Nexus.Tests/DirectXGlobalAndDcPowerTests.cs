using Nexus.Core;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

/// <summary>Padrões globais do DirectX (DirectXUserGlobalSettings) e valores DC (bateria) do PowerSettingValue.</summary>
[Trait("Category", "Unit")]
public class DirectXGlobalAndDcPowerTests
{
    private static readonly RegistryTarget Global =
        new(RegHive.HKCU, DirectXUserGpuPreferenceHandler.KeyPath, DirectXUserGpuPreferenceHandler.GlobalSettingsName, RegView.Default);

    private static ActionInstance DxGlobal(string token, string? value) =>
        new(T.Def("dx." + token, DirectXUserGpuPreferenceHandler.Kind),
            new Dictionary<string, string> { ["name"] = DirectXUserGpuPreferenceHandler.GlobalSettingsName, ["token"] = token },
            value is null ? StateSnapshot.Absent() : StateSnapshot.Of("Token", value));

    private static (HandlerRegistry Handlers, InMemoryJournal Journal, ActionExecutor Exec) Engine(IActionHandler h)
    {
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        return (handlers, journal, new ActionExecutor(handlers, journal));
    }

    private static ExecutionOutcome Run(HandlerRegistry handlers, ActionExecutor exec, InMemoryJournal journal, ActionInstance a, out Guid run)
    {
        var r = journal.BeginRun("p", "1");
        run = r;
        return exec.Execute(r, new DesiredStateResolver(handlers).Resolve([a], T.Ctx), T.Ctx).Single().Outcome;
    }

    [Theory]
    [InlineData("SwapEffectUpgradeEnable=0;VRROptimizeEnable=0;AutoHDREnable=1;", "SwapEffectUpgradeEnable", "1",
        "SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;AutoHDREnable=1;")]
    [InlineData("SwapEffectUpgradeEnable=1;AutoHDREnable=1;", "VRROptimizeEnable", "1",
        "SwapEffectUpgradeEnable=1;AutoHDREnable=1;VRROptimizeEnable=1;")]
    [InlineData("SwapEffectUpgradeEnable=1;Future=7;AutoHDREnable=1;", "AutoHDREnable", "0",
        "SwapEffectUpgradeEnable=1;Future=7;AutoHDREnable=0;")]
    public void Global_token_merge_preserves_other_tokens_and_restores_exactly(string before, string token, string value, string after)
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(Global, "String", before);
        var (handlers, journal, exec) = Engine(new DirectXUserGpuPreferenceHandler(reg));

        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, DxGlobal(token, value), out var run));
        Assert.Equal(after, reg.Read(Global).Value);

        exec.RevertRun(run);
        Assert.Equal(before, reg.Read(Global).Value);
    }

    [Fact]
    public void Global_value_absent_is_created_then_removed_on_restore()
    {
        var reg = new InMemoryRegistryBackend(); // Windows Server / perfil novo: nem o valor existe
        var (handlers, journal, exec) = Engine(new DirectXUserGpuPreferenceHandler(reg));

        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, DxGlobal("SwapEffectUpgradeEnable", "1"), out var run));
        Assert.Equal("SwapEffectUpgradeEnable=1;", reg.Read(Global).Value);

        exec.RevertRun(run);
        Assert.False(reg.Read(Global).ValueExists);
    }

    [Fact]
    public void Global_value_absent_keeps_tokens_written_by_others_after_restore()
    {
        var reg = new InMemoryRegistryBackend();
        var (handlers, journal, exec) = Engine(new DirectXUserGpuPreferenceHandler(reg));
        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, DxGlobal("VRROptimizeEnable", "1"), out var run));
        reg.Write(Global, "String", reg.Read(Global).Value + "AutoHDREnable=1;"); // usuário mexeu em Configurações depois

        exec.RevertRun(run);
        Assert.Equal("AutoHDREnable=1;", reg.Read(Global).Value);
    }

    [Fact]
    public void Global_token_absent_in_existing_value_is_removed_but_value_kept()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(Global, "String", "SwapEffectUpgradeEnable=1;");
        var (handlers, journal, exec) = Engine(new DirectXUserGpuPreferenceHandler(reg));
        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, DxGlobal("AutoHDREnable", "0"), out var run));
        Assert.Equal("SwapEffectUpgradeEnable=1;AutoHDREnable=0;", reg.Read(Global).Value);

        exec.RevertRun(run);
        Assert.Equal("SwapEffectUpgradeEnable=1;", reg.Read(Global).Value);
    }

    [Fact]
    public void Global_value_that_existed_empty_is_not_deleted_on_restore()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(Global, "String", "");
        var (handlers, journal, exec) = Engine(new DirectXUserGpuPreferenceHandler(reg));
        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, DxGlobal("VRROptimizeEnable", "1"), out var run));

        exec.RevertRun(run);
        Assert.True(reg.Read(Global).ValueExists);
        Assert.Equal("", reg.Read(Global).Value);
    }

    [Theory]
    [InlineData("GpuPreference", "2")]      // só por executável
    [InlineData("Unknown", "1")]
    [InlineData("SwapEffectUpgradeEnable", "2")] // global aceita só 0/1
    [InlineData("VRROptimizeEnable", "1;X=1")]
    [InlineData("AutoHDREnable", "")]
    public void Global_rejects_tokens_and_values_outside_the_allow_list(string token, string value)
    {
        var h = new DirectXUserGpuPreferenceHandler(new InMemoryRegistryBackend());
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(DxGlobal(token, value), T.Ctx).Kind);
    }

    [Fact]
    public void Vrr_token_is_only_global_not_per_app()
    {
        var h = new DirectXUserGpuPreferenceHandler(new InMemoryRegistryBackend());
        var perApp = new ActionInstance(T.Def("dx", DirectXUserGpuPreferenceHandler.Kind),
            new Dictionary<string, string> { ["name"] = @"C:\Games\game.exe", ["token"] = "VRROptimizeEnable" }, StateSnapshot.Of("Token", "1"));
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(perApp, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(DxGlobal("VRROptimizeEnable", "1"), T.Ctx).Kind);
    }

    [Fact]
    public void Global_restore_validator_rejects_forged_original()
    {
        var h = new DirectXUserGpuPreferenceHandler(new InMemoryRegistryBackend());
        Assert.NotNull(h.CheckRestore(DxGlobal("GpuPreference", "1"), StateSnapshot.Of("Token", "1")));
        Assert.NotNull(h.CheckRestore(DxGlobal("AutoHDREnable", "0"), StateSnapshot.Of("Token", "1;GpuPreference=2")));
        Assert.Null(h.CheckRestore(DxGlobal("AutoHDREnable", "0"), StateSnapshot.Absent()));
    }

    // ---------- PowerSettingValue AC/DC ----------

    private sealed class FakePower : IPowerBackend
    {
        public Guid Active = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        public readonly Dictionary<(Guid, Guid, Guid), uint> Ac = [];
        public readonly Dictionary<(Guid, Guid, Guid), uint> Dc = [];
        public readonly HashSet<Guid> MissingSettings = [];
        public Guid GetActiveScheme() => Active;
        public void SetActiveScheme(Guid scheme) => Active = scheme;
        public IReadOnlyList<Guid> EnumerateSchemes() => [Active];
        public uint ReadAcValue(Guid s, Guid g, Guid k) => Get(Ac, s, g, k);
        public void WriteAcValue(Guid s, Guid g, Guid k, uint v) => Ac[(s, g, k)] = v;
        public uint ReadDcValue(Guid s, Guid g, Guid k) => Get(Dc, s, g, k);
        public void WriteDcValue(Guid s, Guid g, Guid k, uint v) => Dc[(s, g, k)] = v;
        private uint Get(Dictionary<(Guid, Guid, Guid), uint> d, Guid s, Guid g, Guid k) =>
            MissingSettings.Contains(k) ? throw new InvalidOperationException("PowerRead falhou com código Win32 2.")
            : d.TryGetValue((s, g, k), out var v) ? v : 0;
    }

    private static readonly Guid WifiSub = Guid.Parse("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    private static readonly Guid WifiPsm = Guid.Parse("12bbebe6-58d6-4636-95bb-3217ef867c1a");

    private static ActionInstance PowerAction(FakePower p, string? acdc, string value)
    {
        var t = new Dictionary<string, string> { ["scheme"] = p.Active.ToString("D"), ["subgroup"] = WifiSub.ToString("D"), ["setting"] = WifiPsm.ToString("D") };
        if (acdc is not null) t["acdc"] = acdc;
        return new ActionInstance(T.Def("pw", PowerSettingValueHandler.Kind, PrivilegeLevel.Admin), t, StateSnapshot.Of("UInt32", value));
    }

    [Fact]
    public void Dc_target_reads_writes_and_restores_only_the_dc_index()
    {
        var p = new FakePower();
        p.Ac[(p.Active, WifiSub, WifiPsm)] = 0;
        p.Dc[(p.Active, WifiSub, WifiPsm)] = 2;
        var (handlers, journal, exec) = Engine(new PowerSettingValueHandler(p));

        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, PowerAction(p, "DC", "0"), out var run));
        Assert.Equal(0u, p.Dc[(p.Active, WifiSub, WifiPsm)]);
        p.Ac[(p.Active, WifiSub, WifiPsm)] = 3; // AC mudado por outro: não pode ser tocado pelo desfazer do DC

        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Equal(2u, p.Dc[(p.Active, WifiSub, WifiPsm)]);
        Assert.Equal(3u, p.Ac[(p.Active, WifiSub, WifiPsm)]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AC")]
    public void Ac_is_the_default_and_never_touches_dc(string? acdc)
    {
        var p = new FakePower();
        p.Dc[(p.Active, WifiSub, WifiPsm)] = 2;
        var (handlers, journal, exec) = Engine(new PowerSettingValueHandler(p));
        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, PowerAction(p, acdc, "1"), out _));
        Assert.Equal(1u, p.Ac[(p.Active, WifiSub, WifiPsm)]);
        Assert.Equal(2u, p.Dc[(p.Active, WifiSub, WifiPsm)]);
    }

    [Theory]
    [InlineData("dc")]
    [InlineData("BOTH")]
    [InlineData("")]
    public void Invalid_acdc_is_blocked_and_rejected_on_restore(string acdc)
    {
        var p = new FakePower();
        var h = new PowerSettingValueHandler(p);
        var a = PowerAction(p, acdc, "0");
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(a, T.Ctx).Kind);
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of("UInt32", "2")));
    }

    [Fact]
    public void Missing_power_setting_is_not_applicable()
    {
        var p = new FakePower();
        p.MissingSettings.Add(WifiPsm); // ex.: Windows Server sem o subgrupo do Wi-Fi
        var h = new PowerSettingValueHandler(p);
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(PowerAction(p, "DC", "0"), T.Ctx).Kind);
    }

    // ---------- catálogo ----------

    private static CatalogDocument Catalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    [Theory]
    [InlineData("fps.dx.windowed-optimizations.global@1", "SwapEffectUpgradeEnable", "1")]
    [InlineData("fps.dx.vrr.global@1", "VRROptimizeEnable", "1")]
    [InlineData("fps.dx.autohdr.off@1", "AutoHDREnable", "0")]
    public void DirectX_global_entries_instantiate_and_apply_through_the_handler(string key, string token, string value)
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == key);
        var a = entry.TryInstantiate(new Dictionary<string, string>(), out var reason);
        Assert.True(a is not null, reason);
        Assert.Equal(DirectXUserGpuPreferenceHandler.GlobalSettingsName, a.Target["name"]);
        Assert.Equal(token, a.Target["token"]);
        Assert.Equal(("Token", value), (a.Desired.Type, a.Desired.Value));
        Assert.True(UserProfileScope.IsUserScoped(a));

        var reg = new InMemoryRegistryBackend();
        reg.Seed(Global, "String", "SwapEffectUpgradeEnable=0;VRROptimizeEnable=0;AutoHDREnable=1;");
        var (handlers, journal, exec) = Engine(new DirectXUserGpuPreferenceHandler(reg));
        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, a, out var run));
        exec.RevertRun(run);
        Assert.Equal("SwapEffectUpgradeEnable=0;VRROptimizeEnable=0;AutoHDREnable=1;", reg.Read(Global).Value);
    }

    [Fact]
    public void Wifi_dc_entry_targets_the_dc_index_of_the_active_scheme()
    {
        var p = new FakePower();
        p.Dc[(p.Active, WifiSub, WifiPsm)] = 2;
        var entry = Catalog().Actions.Single(a => a.Definition.Key == "net.wifi.power-saving.dc-max-perf@1");
        var a = entry.TryInstantiate(new Dictionary<string, string> { ["activeScheme"] = p.Active.ToString("D") }, out var reason);
        Assert.True(a is not null, reason);
        Assert.Equal("DC", a.Target["acdc"]);

        var (handlers, journal, exec) = Engine(new PowerSettingValueHandler(p));
        Assert.Equal(ExecutionOutcome.Verified, Run(handlers, exec, journal, a, out var run));
        Assert.Equal(0u, p.Dc[(p.Active, WifiSub, WifiPsm)]);
        Assert.False(p.Ac.ContainsKey((p.Active, WifiSub, WifiPsm)));
        exec.RevertRun(run);
        Assert.Equal(2u, p.Dc[(p.Active, WifiSub, WifiPsm)]);
    }
}
