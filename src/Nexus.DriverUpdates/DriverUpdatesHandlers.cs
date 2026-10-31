using System.Diagnostics;
using System.Runtime.Versioning;
using Nexus.Core;
using Nexus.DriverUpdates.Windows;
using Nexus.Lab.Gpu;
using Nexus.WindowsActions;

namespace Nexus.DriverUpdates;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
public static class DriverUpdatesHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (OperatingSystem.IsWindows())
            return Build(environment, new WuaAgent(), new SetupApiDeviceInventory(), new WindowsDriverPackageTool(), new WindowsBootClock());
        var none = new Unsupported();
        return Build(environment, none, none, none, none);
    }

    /// <summary>Monta os handlers com backends injetados (testes usam backends falsos).</summary>
    public static IEnumerable<IActionHandler> Build(HandlerEnvironment environment, IWindowsUpdateAgent wua, IDeviceInventory devices, IDriverPackageTool tool, IBootClock clock)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return [new DriverUpdateInstallHandler(wua, devices, tool, clock, environment.DataDirectory)];
    }

    /// <summary>Fora do Windows: o handler existe (o catálogo é validado), mas qualquer leitura/escrita recusa.</summary>
    private sealed class Unsupported : IWindowsUpdateAgent, IDeviceInventory, IDriverPackageTool, IBootClock
    {
        private static PlatformNotSupportedException No() => new("Atualização de drivers só funciona no Windows.");
        public WuSearchOutcome SearchDrivers(TimeSpan timeout, CancellationToken cancel) => WuSearchOutcome.Fail(WuErrorKind.Unsupported, "Windows Update só no Windows.");
        public WuInstallResult DownloadAndInstall(string updateId, int revision, TimeSpan timeout) => throw No();
        public bool? RebootRequired() => null;
        public IReadOnlyList<DeviceDriver> List() => throw No();
        public string? OriginalInfName(string publishedName) => throw No();
        public void Export(string publishedName, string directory) => throw No();
        public void Add(string infPath) => throw No();
        public bool ForceInstall(string hardwareId, string infPath) => throw No();
        public string InboxInfDirectory => "";
        public DateTime LastBootUtc => DateTime.UtcNow;
    }
}

/// <summary>
/// Exceção controlada (brief, regra 2): abrir o que o usuário VÊ e controla. Só destinos fixos: páginas oficiais dos fabricantes,
/// links de detalhes da Microsoft (https e domínio conferido), as Configurações do Windows Update e os apps oficiais da
/// placa de vídeo em caminhos fixos. A página pede confirmação (Host.Confirm) antes. Nada é baixado.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverVendorTools
{
    public static void OpenOfficialDriverPage(GpuVendor vendor) =>
        Start(DriverInfo.OfficialDownloadPage(vendor) ?? throw new ArgumentException("Fabricante sem página oficial conhecida.", nameof(vendor)));

    public static void OpenDetails(string url)
    {
        if (!WindowsUpdateQueries.IsAllowedDetailsUrl(url)) throw new ArgumentException("Link fora dos domínios da Microsoft: não aberto.", nameof(url));
        Start(url);
    }

    /// <summary>Configurações > Windows Update > Atualizações opcionais (Windows 11); no 10, a página do Windows Update.</summary>
    public static void OpenWindowsUpdateSettings(bool optional) => Start(optional ? "ms-settings:windowsupdate-optionalupdates" : "ms-settings:windowsupdate");

    public static void OpenVendorApp(VendorApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        var known = GpuDrivers.KnownApps.Any(k => roots.Any(r => r.Length > 0 && string.Equals(Path.Combine(r, k.RelativePath), app.Path, StringComparison.OrdinalIgnoreCase)));
        if (!known || !File.Exists(app.Path)) throw new FileNotFoundException("App oficial não encontrado no caminho esperado.", app.Path);
        Start(app.Path);
    }

    private static void Start(string target)
    {
        using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
