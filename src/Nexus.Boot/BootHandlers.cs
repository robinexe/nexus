using Nexus.Boot.Handlers;
using Nexus.Boot.Inventory;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;

namespace Nexus.Boot;

/// <summary>Backends dos handlers do módulo (reais no Windows; falsos nos testes).</summary>
public sealed record BootBackends(
    IServiceBackend Services,
    IScheduledTaskBackend Tasks,
    Func<string, bool> FileExists,
    Func<string, string?> ProcessEnvironment,
    Func<RegHive, string?, string?> StartupFolderOf);

/// <summary>
/// Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo
/// (catalog/boot.v1.json e catalog/services.v1.json). Itens de Run/Run32 usam o <c>StartupApproved</c> já existente.
/// </summary>
public static class BootHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Create(environment, OperatingSystem.IsWindows() ? WindowsBackends(environment) : Unavailable());
    }

    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment, BootBackends backends)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(backends);
        return
        [
            new BootServiceStartTypeHandler(backends.Services, environment.Registry),
            new BootScheduledTaskHandler(backends.Tasks),
            new BootStartupFolderHandler(environment.Registry, backends.StartupFolderOf, backends.FileExists),
            new BootOrphanEntryHandler(environment.Registry, backends.FileExists, backends.ProcessEnvironment, environment.OtherAccount),
        ];
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static BootBackends WindowsBackends(HandlerEnvironment environment)
    {
        var folders = Windows.WindowsStartupFolders.Current();
        return new BootBackends(
            new WindowsServiceBackend(),
            new Windows.WindowsTaskScheduler(),
            path => { try { return File.Exists(path); } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } },
            Environment.GetEnvironmentVariable,
            // A pasta do usuário só é conhecida com certeza quando o aplicador é o próprio usuário (sem SID no alvo).
            (scope, sid) => scope == RegHive.HKLM ? folders.Common : (sid is null && !environment.OtherAccount ? folders.User : null));
    }

    private static BootBackends Unavailable() => new(
        new UnavailableServices(),
        new UnavailableTasks(),
        _ => false,
        _ => null,
        (_, _) => null);

    private sealed class UnavailableServices : IServiceBackend
    {
        public string? GetStartType(string service) => throw new PlatformNotSupportedException("Serviços existem só no Windows.");
        public void SetStartType(string service, string startType) => throw new PlatformNotSupportedException("Serviços existem só no Windows.");
    }

    private sealed class UnavailableTasks : IScheduledTaskBackend
    {
        public ScheduledTaskInfo? Find(string taskPath) => throw new PlatformNotSupportedException("Tarefas agendadas existem só no Windows.");
        public void SetEnabled(string taskPath, bool enabled) => throw new PlatformNotSupportedException("Tarefas agendadas existem só no Windows.");
    }
}
