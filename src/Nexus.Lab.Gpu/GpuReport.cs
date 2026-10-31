namespace Nexus.Lab.Gpu;

/// <summary>Leitura NVML de uma placa NVIDIA (null em cada campo = o driver não informou para esta placa).</summary>
public sealed record NvidiaReading
{
    public string? Name { get; init; }
    /// <summary>pciDeviceId do NVML: (DeviceId &lt;&lt; 16) | VendorId.</summary>
    public uint? PciDeviceId { get; init; }
    public uint? PciSubSystemId { get; init; }
    public string? BusId { get; init; }
    public double? GraphicsClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
    public double? MaxGraphicsClockMhz { get; init; }
    public double? MaxMemoryClockMhz { get; init; }
    public double? PowerWatts { get; init; }
    public double? PowerLimitWatts { get; init; }
    public double? DefaultPowerLimitWatts { get; init; }
    public double? MinPowerLimitWatts { get; init; }
    public double? MaxPowerLimitWatts { get; init; }
    public double? EnforcedPowerLimitWatts { get; init; }
    public double? TemperatureC { get; init; }
    public double? SlowdownTemperatureC { get; init; }
    public double? ShutdownTemperatureC { get; init; }
    public double? MaxOperatingTemperatureC { get; init; }
    public double? FanPercent { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? MemoryUtilizationPercent { get; init; }
    public int? PcieGen { get; init; }
    public int? PcieMaxGen { get; init; }
    public int? PcieWidth { get; init; }
    public int? PcieMaxWidth { get; init; }
    public ulong? ReasonsMask { get; init; }
    public ulong? VramTotalBytes { get; init; }
    public ulong? VramUsedBytes { get; init; }
    public ulong? Bar1TotalBytes { get; init; }
    public int? PerformanceState { get; init; }

    public uint? VendorId => PciDeviceId is { } p ? p & 0xFFFF : null;
    public uint? DeviceId => PciDeviceId is { } p ? p >> 16 : null;

    public GpuTelemetrySample ToSample(double seconds) => new(seconds, UtilizationPercent, GraphicsClockMhz, MemoryClockMhz, PowerWatts,
        EnforcedPowerLimitWatts ?? PowerLimitWatts, TemperatureC, ReasonsMask, PcieGen, PcieWidth, VramUsedBytes, VramTotalBytes, FanPercent);
}

/// <summary>Uma GPU como o DXGI e o gerenciador de dispositivos a veem, mais o que o driver NVIDIA informa (se houver).</summary>
public sealed record GpuAdapter
{
    public required string Name { get; init; }
    public uint VendorId { get; init; }
    public uint DeviceId { get; init; }
    public uint SubSysId { get; init; }
    public uint Revision { get; init; }
    public long Luid { get; init; }
    public ulong DedicatedVideoBytes { get; init; }
    public ulong DedicatedSystemBytes { get; init; }
    public ulong SharedSystemBytes { get; init; }
    public bool IsSoftware { get; init; }
    /// <summary>Primeira da lista "alto desempenho" do DXGI (IDXGIFactory6::EnumAdapterByGpuPreference).</summary>
    public bool IsHighPerformance { get; init; }
    /// <summary>Primeira da lista "economia de energia".</summary>
    public bool IsMinimumPower { get; init; }
    public ulong? LocalBudgetBytes { get; init; }
    public ulong? LocalUsageBytes { get; init; }
    public string? InstanceId { get; init; }
    public string? DriverVersion { get; init; }
    public DateOnly? DriverDate { get; init; }
    public string? DriverProvider { get; init; }
    public PcieLinkState Pcie { get; init; } = new(null, null, null, null);
    public bool? HagsSupported { get; init; }
    public bool? HagsEnabled { get; init; }
    public NvidiaReading? Nvidia { get; init; }

    public GpuVendor Vendor => GpuIdentity.VendorOf(VendorId);
    public GpuArchitecture Architecture => GpuIdentity.Architecture(VendorId, DeviceId);
    public bool IsIntegrated => GpuIdentity.IsIntegrated(VendorId, DeviceId, DedicatedVideoBytes);
    public DriverVersionInfo DriverInfoDecoded => DriverInfo.Decode(Vendor, DriverVersion);
}

/// <summary>Monitor ativo: quem o desenha, modo atual, melhor modo listado e cores avançadas (HDR).</summary>
public sealed record MonitorReport
{
    public required string Device { get; init; }
    public string FriendlyName { get; init; } = "";
    public string AdapterName { get; init; } = "";
    public long AdapterLuid { get; init; }
    public bool Internal { get; init; }
    public bool Primary { get; init; }
    public string Connection { get; init; } = "";
    public required DisplayMode Current { get; init; }
    public required DisplayModeAdvice Advice { get; init; }
    public int ListedModes { get; init; }
    public bool? HdrSupported { get; init; }
    public bool? HdrEnabled { get; init; }
    public int? BitsPerColor { get; init; }
}

/// <summary>Ajustes gráficos do Windows lidos do registro / D3DKMT.</summary>
public sealed record GraphicsSettings
{
    /// <summary>HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode: 2 = ligado, 1 = desligado, null = padrão do driver.</summary>
    public int? HwSchMode { get; init; }
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5 desliga o MPO (planos de sobreposição).</summary>
    public int? OverlayTestMode { get; init; }
    /// <summary>HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled (0 = desligado; ausente = ligado por padrão).</summary>
    public int? AutoGameModeEnabled { get; init; }
    public bool? OnBattery { get; init; }
    public bool? HasBattery { get; init; }
}

/// <summary>Notebook híbrido (iGPU + dGPU) e como a tela interna está ligada.</summary>
public sealed record HybridInfo(bool IsHybrid, string? InternalPanelGpu, string Description);

/// <summary>Uma preferência de GPU por app (HKCU\Software\Microsoft\DirectX\UserGpuPreferences).</summary>
public sealed record AppGpuPreference(string ExePath, int? GpuPreference, string RawValue);

public sealed record GpuLabReport
{
    public DateTimeOffset TakenAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<GpuAdapter> Adapters { get; init; } = [];
    public IReadOnlyList<MonitorReport> Monitors { get; init; } = [];
    public GraphicsSettings Settings { get; init; } = new();
    public IReadOnlyList<AppGpuPreference> AppPreferences { get; init; } = [];
    public IReadOnlyList<GameEntry> Games { get; init; } = [];
    public string NvmlStatus { get; init; } = "";
    public IReadOnlyList<string> Notes { get; init; } = [];

    public IEnumerable<GpuAdapter> Hardware => Adapters.Where(a => !a.IsSoftware && a.Vendor != GpuVendor.Microsoft);

    /// <summary>A GPU principal para jogos: a de alto desempenho do DXGI; senão a dedicada com mais VRAM.</summary>
    public GpuAdapter? Primary =>
        Hardware.FirstOrDefault(a => a.IsHighPerformance) ??
        Hardware.OrderBy(a => a.IsIntegrated).ThenByDescending(a => a.DedicatedVideoBytes).FirstOrDefault();

    public bool IsLaptop => Settings.HasBattery == true || Monitors.Any(m => m.Internal);

    public HybridInfo Hybrid => GpuDiagnostics.Hybrid(this);
}
