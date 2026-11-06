using Nexus.Core;
using Nexus.Lab.Gpu.Windows;
using Nexus.WindowsActions;

namespace Nexus.Lab.Gpu;

/// <summary>
/// Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.
/// NvidiaDrsSetting grava a lista fechada de ajustes do driver NVIDIA (catalog/nvidia.v1.json; NVAPI carregada só no primeiro uso).
/// GpuVendorSetting: AMD (ADLX) e Intel (IGCL), num processo separado (catalog/gpuvendors.v1.json).
/// A preferência de GPU por app reaproveita o handler do núcleo (DirectXUserGpuPreference), registrado pelo broker.
/// </summary>
public static class GpuLabHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        IDisplayModeBackend backend = OperatingSystem.IsWindows() ? new WindowsDisplayModeBackend() : new NoDisplayModeBackend();
        INvidiaDrs drs = OperatingSystem.IsWindows() ? new WindowsNvidiaDrs() : new NoNvidiaDrs();
        return [new DisplayModeHandler(backend, environment.OtherAccount), new NvidiaDrsHandler(drs),
                new Vendors.GpuVendorSettingHandler(Vendors.GpuVendorSettingHandler.RunOutOfProcess)];
    }
}
