using Nexus.Core;
using Nexus.SystemTools.Appx;
using Nexus.SystemTools.Crash;
using Nexus.SystemTools.Drivers;
using Nexus.SystemTools.Network;
using Nexus.SystemTools.Restore;
using Nexus.SystemTools.Wifi;
using Nexus.WindowsActions;

namespace Nexus.SystemTools;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class SystemToolsHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OperatingSystem.IsWindows())
        {
            var runner = new WindowsProcessRunner();
            var store = new PnpUtilDriverStore(runner);
            return Build(environment, new WindowsNetworkBackend(runner), new WindowsWifiBackend(),
                new DpapiProtector(machineScope: environment.OtherAccount), store, new WindowsRestorePointBackend(runner), new PowerShellAppxBackend(runner),
                environment.OtherAccount || environment.Elevated ? AdminOnlyDirectory.RestrictToAdministrators : null);
        }
        var none = new Unsupported();
        return Build(environment, none, none, none, none, none, none);
    }

    /// <summary>Monta os handlers com backends injetados (testes usam backends falsos).</summary>
    /// <param name="restrictSecrets">Protege pastas com segredos (backups de Wi-Fi) para só SYSTEM/Administradores (aplicador elevado).</param>
    public static IEnumerable<IActionHandler> Build(HandlerEnvironment environment, INetworkBackend network, IWifiBackend wifi, IBackupProtector protector,
        IDriverStore drivers, IRestorePointBackend restore, IAppxBackend appx, Action<string>? restrictSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var data = environment.DataDirectory;
        return
        [
            new DnsFlushHandler(network),
            new ArpFlushHandler(network),
            new WifiProfileHandler(wifi, protector, data, environment.OtherAccount, restrictSecrets),
            new DriverPackageHandler(drivers, data),
            new DriverBackupAllHandler(drivers, data),
            new RestorePointHandler(restore),
            new AppxPackageHandler(appx, data, environment.OtherAccount),
            new LocalDumpsHandler(environment.Registry),
        ];
    }

    /// <summary>Fora do Windows: os handlers existem (o catálogo é validado), mas qualquer leitura/escrita recusa.</summary>
    private sealed class Unsupported : INetworkBackend, IWifiBackend, IBackupProtector, IDriverStore, IRestorePointBackend, IAppxBackend
    {
        private static PlatformNotSupportedException No() => new("Ferramentas do sistema só funcionam no Windows.");
        public void FlushDns() => throw No();
        public void FlushArp() => throw No();
        public IReadOnlyList<WifiProfileRef> Find(string profileName) => throw new WifiUnavailableException("Wi-Fi só no Windows.");
        public IReadOnlyList<Guid> Interfaces() => [];
        public string ExportXml(Guid iface, string profileName) => throw No();
        public void Delete(Guid iface, string profileName) => throw No();
        public void Import(Guid iface, string xml, bool userOnly) => throw No();
        public string Scope => "none";
        public byte[] Protect(byte[] data) => throw No();
        public byte[] Unprotect(byte[] data) => throw No();
        public IReadOnlyList<DriverPackage> List() => throw No();
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? InUse() => null;
        public void Export(string publishedName, string directory) => throw No();
        public void Delete(string publishedName) => throw No();
        public void Add(string infPath) => throw No();
        public void ExportAll(string directory) => throw No();
        public int Create(string description) => throw No();
        RestorePointList IRestorePointBackend.List() => new(null, "Só no Windows.");
        public AppxPackageInfo? Find(string name, string? userSid) => throw No();
        public void Remove(string packageFullName, string? userSid) => throw No();
        public void Register(string manifestPath) => throw No();
        public bool ManifestExists(string manifestPath) => false;
    }
}
