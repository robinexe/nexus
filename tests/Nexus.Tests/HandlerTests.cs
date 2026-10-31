using Nexus.Core;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;
using Nexus.WindowsActions.Startup;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public class HandlerTests
{
    [Fact]
    public void TokenString_preserves_unknown_tokens_and_order()
    {
        var t = TokenString.Parse("GpuPreference=2;Weird;SwapEffectUpgradeEnable=0;");
        t.Set("SwapEffectUpgradeEnable", "1");
        Assert.Equal("GpuPreference=2;Weird;SwapEffectUpgradeEnable=1;", t.ToString());
        t.Remove("SwapEffectUpgradeEnable");
        Assert.Equal("GpuPreference=2;Weird;", t.ToString());
    }

    [Fact]
    public void DirectX_preference_restore_removes_only_the_token_it_added()
    {
        var reg = new InMemoryRegistryBackend();
        const string exe = @"C:\Riot Games\VALORANT\live\VALORANT.exe";
        var target = new RegistryTarget(RegHive.HKCU, DirectXUserGpuPreferenceHandler.KeyPath, exe, RegView.Default);
        reg.Seed(target, "String", "GpuPreference=2;");
        var handlers = new HandlerRegistry().Add(new DirectXUserGpuPreferenceHandler(reg));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var a = new ActionInstance(T.Def("dx", DirectXUserGpuPreferenceHandler.Kind),
            new Dictionary<string, string> { ["name"] = exe, ["token"] = "SwapEffectUpgradeEnable" },
            StateSnapshot.Of("Token", "1"));
        var run = journal.BeginRun("p", "1");

        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, new DesiredStateResolver(handlers).Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal("GpuPreference=2;SwapEffectUpgradeEnable=1;", reg.Read(target).Value);

        exec.RevertRun(run);
        Assert.Equal("GpuPreference=2;", reg.Read(target).Value);
    }

    [Fact]
    public void Startup_disable_keeps_run_entry_and_restores_absent_approval()
    {
        var reg = new InMemoryRegistryBackend();
        var run32 = new RegistryTarget(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Run", "RgbTool", RegView.Registry64);
        reg.Seed(run32, "String", @"C:\Tools\rgb.exe --tray");
        var handlers = new HandlerRegistry().Add(new StartupApprovedHandler(reg));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var a = new ActionInstance(T.Def("st", StartupApprovedHandler.Kind),
            new Dictionary<string, string> { ["scope"] = "HKCU", ["kind"] = "Run", ["entryName"] = "RgbTool" },
            StateSnapshot.Of("StartupState", "disabled"));
        var run = journal.BeginRun("p", "1");

        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, new DesiredStateResolver(handlers).Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        var approved = new RegistryTarget(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "RgbTool", RegView.Default);
        Assert.StartsWith("03", reg.Read(approved).Value);
        Assert.True(reg.Read(run32).ValueExists);

        exec.RevertRun(run);
        Assert.False(reg.Read(approved).ValueExists);
    }

    [Fact]
    public void Startup_missing_run_entry_is_not_applicable()
    {
        var handlers = new HandlerRegistry().Add(new StartupApprovedHandler(new InMemoryRegistryBackend()));
        var a = new ActionInstance(T.Def("st", StartupApprovedHandler.Kind),
            new Dictionary<string, string> { ["scope"] = "HKCU", ["kind"] = "Run", ["entryName"] = "Ghost" },
            StateSnapshot.Of("StartupState", "disabled"));
        Assert.Equal(PlanStatus.NotApplicable, new DesiredStateResolver(handlers).Resolve([a], T.Ctx).Items.Single().Status);
    }

    private sealed class FakePower : IPowerBackend
    {
        public Guid Active;
        public readonly List<Guid> Schemes = [];
        public readonly Dictionary<(Guid, Guid, Guid), uint> Values = [];
        public Guid GetActiveScheme() => Active;
        public void SetActiveScheme(Guid scheme) => Active = scheme;
        public IReadOnlyList<Guid> EnumerateSchemes() => Schemes;
        public uint ReadAcValue(Guid s, Guid g, Guid k) => Values.TryGetValue((s, g, k), out var v) ? v : 0;
        public void WriteAcValue(Guid s, Guid g, Guid k, uint v) => Values[(s, g, k)] = v;
    }

    [Fact]
    public void Power_setting_restores_into_the_scheme_captured_at_backup()
    {
        var balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var high = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var sub = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
        var cpmin = Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583");
        var power = new FakePower { Active = balanced };
        power.Schemes.AddRange([balanced, high]);
        power.Values[(balanced, sub, cpmin)] = 10;
        var handlers = new HandlerRegistry().Add(new PowerSettingValueHandler(power)).Add(new PowerActiveSchemeHandler(power));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("p", "1");
        var a = new ActionInstance(T.Def("cp", PowerSettingValueHandler.Kind, PrivilegeLevel.Admin),
            new Dictionary<string, string> { ["scheme"] = balanced.ToString(), ["subgroup"] = sub.ToString(), ["setting"] = cpmin.ToString() },
            StateSnapshot.Of("UInt32", "100"));

        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, new DesiredStateResolver(handlers).Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal(100u, power.Values[(balanced, sub, cpmin)]);

        power.Active = high; // usuário trocou de plano depois
        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Equal(10u, power.Values[(balanced, sub, cpmin)]);
        Assert.Equal(0u, power.ReadAcValue(high, sub, cpmin)); // o outro esquema não foi tocado
    }

    [Fact]
    public void Missing_power_scheme_is_not_created()
    {
        var power = new FakePower { Active = Guid.NewGuid() };
        var handlers = new HandlerRegistry().Add(new PowerActiveSchemeHandler(power));
        var a = new ActionInstance(T.Def("hp", PowerActiveSchemeHandler.Kind, persistence: PersistenceKind.Session),
            new Dictionary<string, string>(), StateSnapshot.Of("Guid", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"));
        Assert.Equal(PlanStatus.NotApplicable, new DesiredStateResolver(handlers).Resolve([a], T.Ctx).Items.Single().Status);
    }

    private sealed class FakeServices : IServiceBackend
    {
        public readonly Dictionary<string, string> Start = new(StringComparer.OrdinalIgnoreCase);
        public string? GetStartType(string service) => Start.TryGetValue(service, out var v) ? v : null;
        public void SetStartType(string service, string startType) => Start[service] = startType;
    }

    [Theory]
    [InlineData("vgc", PlanStatus.Blocked)]
    [InlineData("WinDefend", PlanStatus.Blocked)]
    [InlineData("NoSuchService", PlanStatus.NotApplicable)]
    [InlineData("SysMain", PlanStatus.Pending)]
    public void Service_handler_protects_security_and_anticheat(string service, PlanStatus expected)
    {
        var svc = new FakeServices();
        svc.Start["vgc"] = "Manual";
        svc.Start["WinDefend"] = "Automatic";
        svc.Start["SysMain"] = "Automatic";
        var handlers = new HandlerRegistry().Add(new ServiceStartTypeHandler(svc));
        var a = new ActionInstance(T.Def("svc", ServiceStartTypeHandler.Kind, PrivilegeLevel.Admin),
            new Dictionary<string, string> { ["service"] = service }, StateSnapshot.Of("StartType", "Manual"));
        Assert.Equal(expected, new DesiredStateResolver(handlers).Resolve([a], T.Ctx).Items.Single().Status);
    }
}
