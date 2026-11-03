using System.Runtime.Versioning;

namespace Nexus.Lab.Gpu.Windows;

/// <summary>
/// Monta o <see cref="GpuLabReport"/> do PC: DXGI (identidade, VRAM, LUID, preferência), gerenciador de dispositivos
/// (driver, PCIe da placa e da porta), D3DKMT (HAGS efetivo), NVML (NVIDIA), monitores (QueryDisplayConfig +
/// EnumDisplaySettingsEx), registro (HAGS, MPO, Modo de Jogo, preferências por app) e manifestos de jogos. Só leitura.
/// Cada parte falha sozinha: o que não pôde ser lido vira nota, nunca exceção.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GpuLabScanner
{
    /// <param name="probeD3D12">Cria um dispositivo D3D12 temporário em cada GPU dedicada para saber se o Resizable BAR está ligado
    /// (acorda a GPU dedicada do notebook por um instante). Desligado por padrão; ligue onde o estado do ReBAR é mostrado.</param>
    public static GpuLabReport Scan(NvmlSession? nvml, string nvmlStatus, bool includeGames = true, bool probeD3D12 = false)
    {
        var notes = new List<string>();
        T Guard<T>(string what, Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                notes.Add($"{what}: {ex.Message}");
                return fallback;
            }
        }

        var (dxgi, high, low) = Guard("DXGI", Dxgi.Enumerate, ((IReadOnlyList<DxgiAdapterInfo>)[], (long?)null, (long?)null));
        var pci = Guard("Gerenciador de dispositivos", PciDevices.DisplayAdapters, []);
        var nvidia = nvml is null ? [] : Guard("NVML", nvml.ReadAll, []);

        var adapters = new List<GpuAdapter>();
        var usedPci = new HashSet<PciDisplayDevice>();
        foreach (var d in dxgi)
        {
            var match = pci.FirstOrDefault(p => !usedPci.Contains(p) && p.Ids is { } ids && ids.Vendor == d.VendorId && ids.Device == d.DeviceId &&
                                                 (ids.SubSys is null || d.SubSysId == 0 || ids.SubSys == d.SubSysId))
                        ?? pci.FirstOrDefault(p => !usedPci.Contains(p) && p.Ids is { } ids && ids.Vendor == d.VendorId && ids.Device == d.DeviceId);
            if (match is not null) usedPci.Add(match);
            var nv = nvidia.FirstOrDefault(n => n.VendorId == d.VendorId && n.DeviceId == d.DeviceId && (n.PciSubSystemId is null || d.SubSysId == 0 || n.PciSubSystemId == d.SubSysId));
            var (hagsSup, hagsOn) = d.Software ? (null, null) : Guard("HAGS", () => GraphicsState.Hags(d.Luid), ((bool?)null, (bool?)null));
            bool? uploadHeap = null;
            if (probeD3D12 && !d.Software && !GpuIdentity.IsIntegrated(d.VendorId, d.DeviceId, d.DedicatedVideo))
                uploadHeap = Guard("D3D12 (Resizable BAR)", () => Dxgi.GpuUploadHeapSupported(d.Luid), (bool?)null);
            var link = match?.Pcie ?? new PcieLinkState(null, null, null, null);
            // NVML conhece o link pelo lado da placa mesmo quando o Windows não expõe as DEVPKEY.
            if (!link.HasData && nv is not null && (nv.PcieGen is not null || nv.PcieWidth is not null))
                link = new PcieLinkState(nv.PcieGen, nv.PcieMaxGen, nv.PcieWidth, nv.PcieMaxWidth);
            adapters.Add(new GpuAdapter
            {
                Name = d.Description,
                VendorId = d.VendorId,
                DeviceId = d.DeviceId,
                SubSysId = d.SubSysId,
                Revision = d.Revision,
                Luid = d.Luid,
                DedicatedVideoBytes = d.DedicatedVideo,
                DedicatedSystemBytes = d.DedicatedSystem,
                SharedSystemBytes = d.SharedSystem,
                IsSoftware = d.Software,
                IsHighPerformance = high == d.Luid,
                IsMinimumPower = low == d.Luid,
                LocalBudgetBytes = d.LocalBudget,
                LocalUsageBytes = d.LocalUsage,
                InstanceId = match?.InstanceId,
                DriverVersion = match?.DriverVersion,
                DriverDate = match?.DriverDate,
                DriverProvider = match?.DriverProvider,
                Pcie = link,
                HagsSupported = hagsSup,
                HagsEnabled = hagsOn,
                GpuUploadHeapSupported = uploadHeap,
                Nvidia = nv,
            });
        }
        // Adaptador presente sem DXGI (driver com problema / desativado): aparece para o usuário saber que existe.
        foreach (var p in pci.Where(p => !usedPci.Contains(p) && p.Ids is not null))
        {
            var ids = p.Ids!.Value;
            adapters.Add(new GpuAdapter
            {
                Name = (p.Description ?? "Adaptador de vídeo") + " (sem DXGI: driver ausente ou desativado)",
                VendorId = ids.Vendor,
                DeviceId = ids.Device,
                SubSysId = ids.SubSys ?? 0,
                InstanceId = p.InstanceId,
                DriverVersion = p.DriverVersion,
                DriverDate = p.DriverDate,
                DriverProvider = p.DriverProvider,
                Pcie = p.Pcie,
            });
        }

        var monitors = Guard("Monitores", () => (IReadOnlyList<MonitorReport>)Monitors(adapters), []);
        var settings = Guard("Ajustes gráficos", GraphicsState.Settings, new GraphicsSettings());
        var prefs = Guard("Preferências por app", GraphicsState.AppPreferences, []);
        IReadOnlyList<GameEntry> games = [];
        if (includeGames)
            games = Guard("Jogos instalados", () =>
            {
                var list = new List<GameEntry>();
                if (GraphicsState.SteamPath() is { } steam && Directory.Exists(steam)) list.AddRange(GameLibraries.SteamGames(steam));
                var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
                list.AddRange(GameLibraries.EpicGames(epic));
                return (IReadOnlyList<GameEntry>)list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }, []);

        return new GpuLabReport
        {
            Adapters = adapters,
            Monitors = monitors,
            Settings = settings,
            AppPreferences = prefs,
            Games = games,
            NvmlStatus = nvmlStatus,
            Notes = notes,
        };
    }

    /// <summary>Só os monitores (taxa atual x máxima, HDR), sem DXGI/NVML: leitura leve para a checagem do monitor da tela Início.</summary>
    public static IReadOnlyList<MonitorReport> MonitorsOnly() => Monitors([]);

    private static List<MonitorReport> Monitors(IReadOnlyList<GpuAdapter> adapters)
    {
        var paths = WindowsDisplay.ActivePaths();
        var outputs = WindowsDisplay.Outputs();
        var list = new List<MonitorReport>();
        foreach (var (device, adapterName, primary) in outputs)
        {
            if (!DisplayModes.IsValidDevice(device)) continue;
            var current = WindowsDisplay.CurrentMode(device);
            if (current is null) continue;
            var modes = WindowsDisplay.ListModes(device);
            var path = paths.FirstOrDefault(p => string.Equals(p.Device, device, StringComparison.OrdinalIgnoreCase));
            var adapter = path is null ? null : adapters.FirstOrDefault(a => a.Luid == path.AdapterLuid);
            list.Add(new MonitorReport
            {
                Device = device,
                FriendlyName = path?.FriendlyName ?? "",
                AdapterName = adapter?.Name ?? adapterName,
                AdapterLuid = path?.AdapterLuid ?? 0,
                Internal = path is not null && WindowsDisplay.IsInternal(path.OutputTechnology),
                Primary = primary,
                Connection = path is null ? "" : WindowsDisplay.ConnectionLabel(path.OutputTechnology),
                Current = current,
                Advice = DisplayModes.Advise(current, modes),
                ListedModes = modes.Count,
                HdrSupported = path?.HdrSupported,
                HdrEnabled = path?.HdrEnabled,
                BitsPerColor = path?.BitsPerColor,
            });
        }
        return list;
    }
}
