using Nexus.Automation;
using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Verificação semanal com o Agendador REAL (schtasks.exe), na conta do teste, sem elevação. Só em VM liberada (portão antes de
/// tudo). Confere que o XML gerado é aceito pelo Windows, que a releitura bate e que a restauração remove a tarefa.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class AutomationIntegrationTests
{
    private static ActionInstance Action(string value) => new(
        new ActionDefinition(AutomationHandlers.ScheduledScanActionId, 1, "t", Classification.SupportedDocumented, AutomationScheduledScanHandler.Kind, "", [], "",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
        new Dictionary<string, string> { ["taskName"] = ScheduledScanTask.TaskName },
        StateSnapshot.Of(AutomationScheduledScanHandler.StateType, value));

    [Fact]
    public void Scheduled_scan_task_is_created_read_back_and_removed()
    {
        Vm.Require(); // nada é criado antes do portão
        var dir = Directory.CreateTempSubdirectory("nexus vm agendada ").FullName; // espaço no caminho de propósito
        var exe = Path.Combine(dir, "nexus.exe");
        File.WriteAllBytes(exe, []);
        var backend = new SchtasksBackend(dir);
        var handler = new AutomationScheduledScanHandler(backend, () => exe, otherAccount: false);
        var ctx = new SystemContext(Vm.Facts.WindowsBuild ?? 0, "vm", null, Vm.Elevated);
        try
        {
            var before = handler.Detect(Action("SUN 19:00"));
            Assert.Equal(ScanSchedule.Off, before.Value);
            Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(Action("SUN 19:00"), ctx).Kind);

            handler.Apply(Action("SUN 19:00"));
            var after = handler.Detect(Action("SUN 19:00"));
            Assert.Equal("SUN 19:00", after.Value);
            Assert.True(after.Detail!.ContainsKey("xml"));

            // Troca de dia e volta ao XML anterior.
            handler.Apply(Action("WED 20:00"));
            Assert.Equal("WED 20:00", handler.Detect(Action("WED 20:00")).Value);
            handler.Restore(Action("WED 20:00"), after);
            Assert.Equal("SUN 19:00", handler.Detect(Action("SUN 19:00")).Value);

            handler.Restore(Action("SUN 19:00"), before);
            Assert.Equal(ScanSchedule.Off, handler.Detect(Action("SUN 19:00")).Value);
        }
        finally
        {
            try { if (backend.QueryXml(ScheduledScanTask.TaskName) is not null) backend.Delete(ScheduledScanTask.TaskName); }
            catch (InvalidOperationException) { }
        }
    }

    private static ActionInstance TaskAction(NexusTaskKind kind, string desired, string schedule, string conditions) => new(
        new ActionDefinition(kind.ActionId, 1, "t", Classification.SupportedDocumented, AutomationScheduledTaskHandler.Kind, "", [], "",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
        new Dictionary<string, string> { ["task"] = kind.Id, ["taskName"] = kind.TaskName, ["schedule"] = schedule, ["conditions"] = conditions },
        StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, desired));

    [Fact]
    public void Generic_tasks_daily_weekly_monthly_are_accepted_by_the_real_scheduler_and_restored()
    {
        Vm.Require(); // nada é criado antes do portão
        var dir = Directory.CreateTempSubdirectory("nexus vm tarefas & cia ").FullName; // espaço e & no caminho de propósito
        var exe = Path.Combine(dir, "nexus.exe");
        File.WriteAllBytes(exe, []);
        var backend = new SchtasksBackend(dir);
        var handler = new AutomationScheduledTaskHandler(backend, () => exe, otherAccount: false);
        try
        {
            foreach (var kind in NexusTaskKind.All)
            {
                var off = handler.Detect(TaskAction(kind, "OFF", "DAILY 03:30", "none"));
                Assert.Equal("OFF", off.Value);
                foreach (var (schedule, conditions) in new[] { ("DAILY 03:30", "none"), ("WEEKLY WED 20:15", "battery-game"), ("MONTHLY 28 07:45", "battery") })
                {
                    var a = TaskAction(kind, "ON", schedule, conditions);
                    handler.Apply(a);
                    var now = handler.Detect(a);
                    Assert.Equal("ON", now.Value); // o Windows aceitou e a releitura bate (horário, condições, caminho)
                    Assert.Equal(schedule, now.Detail!["schedule"]);
                }
                var last = TaskAction(kind, "ON", "MONTHLY 28 07:45", "battery");
                var lastState = handler.Detect(last);
                handler.Apply(TaskAction(kind, "OFF", "MONTHLY 28 07:45", "battery"));
                Assert.Equal("OFF", handler.Detect(last).Value);
                handler.Restore(last, lastState); // recria o XML exato
                Assert.Equal("ON", handler.Detect(last).Value);
                handler.Restore(last, off);
                Assert.Equal("OFF", handler.Detect(last).Value);
            }
        }
        finally
        {
            foreach (var kind in NexusTaskKind.All)
            {
                try { if (backend.QueryXml(kind.TaskName) is not null) backend.Delete(kind.TaskName); }
                catch (InvalidOperationException) { }
            }
        }
    }

    [Fact]
    public void Start_with_windows_writes_the_run_value_of_this_account_and_restores()
    {
        Vm.Require();
        var registry = new WindowsRegistryBackend();
        var target = new RegistryTarget(RegHive.HKCU, AutomationStartWithWindowsHandler.RunKey, AutomationStartWithWindowsHandler.ValueName, RegView.Default);
        const string exe = @"C:\Program Files\NEXUS teste\Nexus.Desktop.exe";
        var handler = new AutomationStartWithWindowsHandler(registry, () => exe, otherAccount: false);
        static ActionInstance A(string v) => new(
            new ActionDefinition(AutomationHandlers.StartWithWindowsActionId, 1, "t", Classification.SupportedDocumented, AutomationStartWithWindowsHandler.Kind, "", [], "",
                PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
            new Dictionary<string, string> { ["hive"] = "HKCU", ["path"] = AutomationStartWithWindowsHandler.RunKey, ["name"] = "NEXUS" },
            StateSnapshot.Of(AutomationStartWithWindowsHandler.StateType, v));
        var before = handler.Detect(A("ON"));
        Assert.NotEqual("FOREIGN", before.Value); // VM descartável: não existe outra entrada NEXUS
        try
        {
            handler.Apply(A("ON"));
            Assert.Equal("ON", handler.Detect(A("ON")).Value);
            Assert.Equal("\"" + exe + "\" --minimized", registry.Read(target).Value);
            handler.Restore(A("ON"), before);
            Assert.Equal(before.Value, handler.Detect(A("ON")).Value);
        }
        finally
        {
            if (before.Value == "OFF" && registry.Read(target).ValueExists) registry.DeleteValue(target);
        }
    }
}
