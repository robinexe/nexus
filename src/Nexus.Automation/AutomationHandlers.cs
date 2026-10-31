using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Automation;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class AutomationHandlers
{
    public const string ScheduledScanActionId = "automation.scheduled-scan.weekly";

    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ITaskSchedulerBackend backend = OperatingSystem.IsWindows()
            ? new SchtasksBackend(Path.Combine(environment.DataDirectory, "automation"))
            : new UnsupportedTaskScheduler();
        return [new AutomationScheduledScanHandler(backend, AutomationScheduledScanHandler.DefaultExecutable, environment.OtherAccount)];
    }

    /// <summary>
    /// Leitura (somente) do estado da verificação agendada, para a interface e a linha de comando: "OFF", "SUN 19:00", "STALE" ou
    /// "FOREIGN". <paramref name="executable"/> = o nexus.exe que a tarefa deveria rodar.
    /// </summary>
    public static (string State, string? Error) ReadScheduledScanState(Func<string?> executable)
    {
        if (!OperatingSystem.IsWindows()) return (ScanSchedule.Off, "O Agendador do Windows não existe neste sistema.");
        try
        {
            var handler = new AutomationScheduledScanHandler(new SchtasksBackend(Path.GetTempPath()), executable, otherAccount: false);
            var probe = new ActionInstance(
                new ActionDefinition(ScheduledScanActionId, 1, "", Classification.SupportedDocumented, AutomationScheduledScanHandler.Kind, "", [], "",
                    PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
                new Dictionary<string, string> { ["taskName"] = ScheduledScanTask.TaskName },
                StateSnapshot.Of(AutomationScheduledScanHandler.StateType, ScanSchedule.Off));
            return (handler.Detect(probe).Value ?? ScanSchedule.Off, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or IOException)
        {
            return (ScanSchedule.Off, ex.Message);
        }
    }

    private sealed class UnsupportedTaskScheduler : ITaskSchedulerBackend
    {
        public string? QueryXml(string taskName) => throw new PlatformNotSupportedException("O Agendador do Windows só existe no Windows.");
        public void CreateOrReplace(string taskName, string xml) => throw new PlatformNotSupportedException("O Agendador do Windows só existe no Windows.");
        public void Delete(string taskName) => throw new PlatformNotSupportedException("O Agendador do Windows só existe no Windows.");
    }
}
