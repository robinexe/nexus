using Nexus.Automation;
using Nexus.Core;

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
}
