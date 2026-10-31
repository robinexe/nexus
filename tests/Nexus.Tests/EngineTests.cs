using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public class EngineTests
{
    private static readonly string[] RebootBatchOrder = ["free", "r", "d"];

    private const string GameBar = @"Software\Microsoft\GameBar";

    [Fact]
    public void Apply_verifies_by_rereading_and_restore_absent_removes_only_value_and_created_key()
    {
        var (reg, _, journal, exec, resolver) = T.Engine();
        var a = T.RegAction("gm", GameBar, "AutoGameModeEnabled", 1);
        var run = journal.BeginRun("p", "1");

        var results = exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx);

        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results).Outcome);
        Assert.Equal("1", reg.Read(T.RT(GameBar, "AutoGameModeEnabled")).Value);
        var entry = journal.Entries(run).Single();
        Assert.False(entry.Original!.Exists);

        var rv = exec.Revert(entry.Id);
        Assert.Equal(RevertOutcome.Reverted, rv.Outcome);
        Assert.False(reg.Read(T.RT(GameBar, "AutoGameModeEnabled")).KeyExists); // chave criada pelo NEXUS foi removida
    }

    [Fact]
    public void Restore_absent_keeps_key_with_third_party_values()
    {
        var (reg, _, journal, exec, resolver) = T.Engine();
        reg.Seed(T.RT(GameBar, "ThirdParty"), "DWord", "7");
        var a = T.RegAction("gm", GameBar, "AutoGameModeEnabled", 1);
        var run = journal.BeginRun("p", "1");
        exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx);

        exec.RevertRun(run);

        Assert.False(reg.Read(T.RT(GameBar, "AutoGameModeEnabled")).ValueExists);
        Assert.Equal("7", reg.Read(T.RT(GameBar, "ThirdParty")).Value);
    }

    [Fact]
    public void Restore_existing_value_writes_original_not_presumed_default()
    {
        var (reg, _, journal, exec, resolver) = T.Engine();
        reg.Seed(T.RT(GameBar, "AutoGameModeEnabled"), "DWord", "0");
        var run = journal.BeginRun("p", "1");
        exec.Execute(run, resolver.Resolve([T.RegAction("gm", GameBar, "AutoGameModeEnabled", 1)], T.Ctx), T.Ctx);
        exec.RevertRun(run);
        Assert.Equal("0", reg.Read(T.RT(GameBar, "AutoGameModeEnabled")).Value);
    }

    [Fact]
    public void Success_return_without_effect_is_not_called_applied_and_is_compensated()
    {
        var journal = new InMemoryJournal();
        var handlers = new HandlerRegistry().Add(new LyingHandler());
        var exec = new ActionExecutor(handlers, journal);
        var a = new ActionInstance(T.Def("lie", "Lying"), new Dictionary<string, string>(), StateSnapshot.Of("DWord", "1"));
        var run = journal.BeginRun("p", "1");

        var r = exec.Execute(run, new DesiredStateResolver(handlers).Resolve([a], T.Ctx), T.Ctx).Single();

        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Equal(JournalState.Reverted, journal.Entries(run).Single().State);
    }

    [Fact]
    public void Revert_does_not_overwrite_later_user_change_without_confirmation()
    {
        var (reg, _, journal, exec, resolver) = T.Engine();
        var run = journal.BeginRun("p", "1");
        exec.Execute(run, resolver.Resolve([T.RegAction("gm", GameBar, "AutoGameModeEnabled", 1)], T.Ctx), T.Ctx);
        reg.Write(T.RT(GameBar, "AutoGameModeEnabled"), "DWord", "5"); // usuário mudou depois

        var conflict = exec.RevertRun(run).Single();
        Assert.Equal(RevertOutcome.Conflict, conflict.Outcome);
        Assert.Equal("5", reg.Read(T.RT(GameBar, "AutoGameModeEnabled")).Value);

        var forced = exec.RevertRun(run, force: true).Single();
        Assert.Equal(RevertOutcome.Reverted, forced.Outcome);
        Assert.False(reg.Read(T.RT(GameBar, "AutoGameModeEnabled")).ValueExists);
    }

    [Fact]
    public void External_change_between_plan_and_execution_skips_action()
    {
        var (reg, _, journal, exec, resolver) = T.Engine();
        var plan = resolver.Resolve([T.RegAction("gm", GameBar, "AutoGameModeEnabled", 1)], T.Ctx);
        reg.Write(T.RT(GameBar, "AutoGameModeEnabled"), "DWord", "3");

        var r = exec.Execute(journal.BeginRun("p", "1"), plan, T.Ctx).Single();

        Assert.Equal(ExecutionOutcome.SkippedExternalChange, r.Outcome);
        Assert.Equal("3", reg.Read(T.RT(GameBar, "AutoGameModeEnabled")).Value);
    }

    [Fact]
    public void Resolver_reports_already_configured_policy_blocked_conflicts_dependencies_and_reboot_batch()
    {
        var (reg, _, _, _, resolver) = T.Engine();
        reg.Seed(T.RT(GameBar, "Done"), "DWord", "1");
        reg.Seed(T.RT(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", RegHive.HKLM), "DWord", "0");

        var actions = new[]
        {
            T.RegAction("reboot", @"Software\X", "R", 1, reboot: RebootRequirement.Required),
            T.RegAction("done", GameBar, "Done", 1),
            T.RegAction("policy", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0,
                extraTarget: new() { ["policyHive"] = "HKLM", ["policyPath"] = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", ["policyName"] = "AllowGameDVR" }),
            T.RegAction("child", @"Software\X", "C", 1, dependsOn: ["policy"]),
            T.RegAction("a", @"Software\X", "A", 1),
            T.RegAction("b", @"Software\X", "B", 1, conflicts: ["a"]),
        };

        var plan = resolver.Resolve(actions, T.Ctx);
        PlanStatus S(string id) => plan.Items.Single(i => i.Action.Definition.Id == id).Status;

        Assert.Equal(PlanStatus.AlreadyConfigured, S("done"));
        Assert.Equal(PlanStatus.Blocked, S("policy"));
        Assert.Equal(PlanStatus.Blocked, S("child"));
        Assert.Equal(PlanStatus.Pending, S("a"));
        Assert.Equal(PlanStatus.Blocked, S("b"));
        Assert.Equal("reboot", plan.Pending.Last().Action.Definition.Id);
        Assert.True(plan.RequiresReboot);
    }

    [Fact]
    public void Item_depending_on_conflict_blocked_item_is_also_blocked()
    {
        var (_, _, _, _, resolver) = T.Engine();
        var plan = resolver.Resolve([
            T.RegAction("a", @"Software\X", "A", 1),
            T.RegAction("b", @"Software\X", "B", 1, conflicts: ["a"]),
            T.RegAction("c", @"Software\X", "C", 1, dependsOn: ["b"]),
        ], T.Ctx);
        Assert.Equal(PlanStatus.Blocked, plan.Items.Single(i => i.Action.Definition.Id == "c").Status);
    }

    [Fact]
    public void Dependents_of_failed_items_are_not_executed()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg)).Add(new LyingHandler());
        var journal = new InMemoryJournal();
        var lie = new ActionInstance(T.Def("lie", "Lying"), new Dictionary<string, string>(), StateSnapshot.Of("DWord", "1"));
        var child = T.RegAction("child", @"Software\X", "C", 1, dependsOn: ["lie"]);
        var results = new ActionExecutor(handlers, journal).Execute(journal.BeginRun("p", "1"),
            new DesiredStateResolver(handlers).Resolve([lie, child], T.Ctx), T.Ctx);

        Assert.Equal(ExecutionOutcome.Blocked, results.Single(r => r.ActionKey == "child@1").Outcome);
        Assert.False(reg.Read(T.RT(@"Software\X", "C")).ValueExists);
    }

    [Fact]
    public void Dependent_of_reboot_item_moves_to_reboot_batch()
    {
        var (_, _, _, _, resolver) = T.Engine();
        var plan = resolver.Resolve([
            T.RegAction("r", @"Software\X", "R", 1, reboot: RebootRequirement.Required),
            T.RegAction("d", @"Software\X", "D", 1, dependsOn: ["r"]),
            T.RegAction("free", @"Software\X", "F", 1),
        ], T.Ctx);
        Assert.Equal(RebootBatchOrder, plan.Pending.Select(i => i.Action.Definition.Id).ToArray());
    }

    [Fact]
    public void Resolver_rejects_dependency_cycles()
    {
        var (_, _, _, _, resolver) = T.Engine();
        var actions = new[]
        {
            T.RegAction("x", @"Software\X", "X", 1, dependsOn: ["y"]),
            T.RegAction("y", @"Software\X", "Y", 1, dependsOn: ["x"]),
        };
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(actions, T.Ctx));
    }

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", RegHive.HKLM)]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\vgk", RegHive.HKLM)]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", RegHive.HKLM)]
    public void Safety_policy_blocks_security_anticheat_and_policy_targets(string path, RegHive hive)
    {
        var (_, _, _, _, resolver) = T.Engine();
        var plan = resolver.Resolve([T.RegAction("evil", path, "Start", 4, hive)], T.Ctx);
        Assert.Equal(PlanStatus.Blocked, plan.Items.Single().Status);
    }

    [Fact]
    public void Allowed_values_list_is_enforced()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        var def = T.Def("prio", RegistryValueHandler.Kind, PrivilegeLevel.Admin, allowed: ["2", "38"]);
        var a = new ActionInstance(def,
            new Dictionary<string, string> { ["hive"] = "HKLM", ["path"] = @"SYSTEM\CurrentControlSet\Control\PriorityControl", ["name"] = "Win32PrioritySeparation", ["type"] = "DWord" },
            StateSnapshot.Of("DWord", "26"));
        Assert.Equal(PlanStatus.Blocked, new DesiredStateResolver(handlers).Resolve([a], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.Blocked, new DesiredStateResolver(handlers).Resolve([a with { Desired = StateSnapshot.Of("DWord", "2") }],
            T.Ctx with { IsElevated = false }).Items.Single().Status);
    }

    [Fact]
    public void Rejected_classification_is_never_executable()
    {
        var (_, _, _, _, resolver) = T.Engine();
        var a = T.RegAction("myth", @"Software\X", "M", 1);
        a = a with { Definition = a.Definition with { Classification = Classification.Rejected } };
        Assert.Equal(PlanStatus.Blocked, resolver.Resolve([a], T.Ctx).Items.Single().Status);
    }
}
