using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Automation;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class AutomationHandlers
{
    /// <summary>Verificação semanal antiga (v1). Continua funcionando para quem a ligou; a interface nova usa as tarefas genéricas.</summary>
    public const string ScheduledScanActionId = "automation.scheduled-scan.weekly";
    public const string StartWithWindowsActionId = "automation.start-with-windows";

    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ITaskSchedulerBackend backend = OperatingSystem.IsWindows()
            ? new SchtasksBackend(Path.Combine(environment.DataDirectory, "automation"))
            : new UnsupportedTaskScheduler();
        return
        [
            new AutomationScheduledScanHandler(backend, AutomationScheduledScanHandler.DefaultExecutable, environment.OtherAccount),
            new AutomationScheduledTaskHandler(backend, AutomationScheduledScanHandler.DefaultExecutable, environment.OtherAccount),
            new AutomationStartWithWindowsHandler(environment.Registry, AutomationStartWithWindowsHandler.DefaultDesktopExecutable, environment.OtherAccount),
        ];
    }

    /// <summary>Leitura (somente) de uma tarefa agendada do NEXUS no Agendador. <paramref name="executable"/> = nexus.exe esperado.</summary>
    public static NexusTaskStatus ReadTaskStatus(NexusTaskKind kind, Func<string?> executable) =>
        ReadTaskStatus(kind, executable, OperatingSystem.IsWindows() ? new SchtasksBackend(Path.GetTempPath()) : null);

    /// <summary>Mesma leitura, com o backend dado (testes).</summary>
    public static NexusTaskStatus ReadTaskStatus(NexusTaskKind kind, Func<string?> executable, ITaskSchedulerBackend? backend)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(executable);
        if (backend is null) return new(kind, AutomationScheduledTaskHandler.Off, null, null, false, "O Agendador do Windows não existe neste sistema.");
        try
        {
            var xml = backend.QueryXml(kind.TaskName);
            if (xml is null) return new(kind, AutomationScheduledTaskHandler.Off, null, null, false, null);
            NexusTaskInfo info;
            try { info = NexusTaskXml.Inspect(xml, kind); }
            catch (System.Xml.XmlException) { return new(kind, AutomationScheduledTaskHandler.Foreign, null, null, false, null); }
            if (!info.Ours) return new(kind, AutomationScheduledTaskHandler.Foreign, null, null, false, null);
            var exe = executable();
            var here = exe is not null && string.Equals(info.Command, exe, StringComparison.OrdinalIgnoreCase);
            return new(kind, here ? AutomationScheduledTaskHandler.On : AutomationScheduledTaskHandler.Different, info.Schedule, info.Conditions, here, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or IOException or PlatformNotSupportedException)
        {
            return new(kind, AutomationScheduledTaskHandler.Off, null, null, false, ex.Message);
        }
    }

    /// <summary>Leitura (somente) de "iniciar com o Windows": ON, OFF, STALE ou FOREIGN.</summary>
    public static (string State, string? Error) ReadStartWithWindows(IRegistryBackend registry, Func<string?> desktopExecutable)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            var handler = new AutomationStartWithWindowsHandler(registry, desktopExecutable, otherAccount: false);
            var probe = new ActionInstance(
                new ActionDefinition(StartWithWindowsActionId, 1, "", Classification.SupportedDocumented, AutomationStartWithWindowsHandler.Kind, "", [], "",
                    PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
                new Dictionary<string, string> { ["hive"] = "HKCU", ["path"] = AutomationStartWithWindowsHandler.RunKey, ["name"] = AutomationStartWithWindowsHandler.ValueName },
                StateSnapshot.Of(AutomationStartWithWindowsHandler.StateType, AutomationStartWithWindowsHandler.Off));
            return (handler.Detect(probe).Value ?? AutomationStartWithWindowsHandler.Off, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            return (AutomationStartWithWindowsHandler.Off, ex.Message);
        }
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
