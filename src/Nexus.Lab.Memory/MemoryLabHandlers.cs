using Nexus.Core;
using Nexus.Lab.Memory.Problems;
using Nexus.WindowsActions;

namespace Nexus.Lab.Memory;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class MemoryLabHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return OperatingSystem.IsWindows()
            ? Create(environment, new WindowsStandbyList(), new WindowsProcessControl(), new WindowsServiceControl(), new WindowsMemoryAgent())
            : Create(environment, new UnsupportedBackend(), new UnsupportedBackend(), new UnsupportedBackend(), new UnsupportedBackend());
    }

    /// <summary>Com backends escolhidos (testes usam falsos).</summary>
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment, IStandbyListBackend standby, IProcessControlBackend processes,
        IServiceControlBackend services, IMemoryAgentBackend agent)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return
        [
            new PageFileHandler(environment.Registry),
            new StandbyPurgeHandler(standby),
            new ProcessTrimHandler(processes),
            new ProcessCloseHandler(processes),
            new ServiceRestartHandler(services),
            new CompressionHandler(agent),
            new BrowserPolicyHandler(environment.Registry),
        ];
    }

    /// <summary>Fora do Windows (testes do catálogo em Linux): registra os handlers, mas qualquer escrita falha com mensagem clara.</summary>
    private sealed class UnsupportedBackend : IStandbyListBackend, IProcessControlBackend, IServiceControlBackend, IMemoryAgentBackend
    {
        private static InvalidOperationException No() => new("Disponível só no Windows.");
        public void PurgeStandby() => throw No();
        public ProcessIdentity? Find(int pid) => null;
        public void Trim(int pid) => throw No();
        public bool CloseGracefully(int pid, TimeSpan wait) => throw No();
        public void Terminate(int pid) => throw No();
        public ServiceRunInfo? Query(string service) => null;
        public void StopService(string service, TimeSpan timeout) => throw No();
        public void StartService(string service, TimeSpan timeout) => throw No();
        public bool? GetCompression() => null;
        public void SetCompression(bool enabled) => throw No();
    }
}
