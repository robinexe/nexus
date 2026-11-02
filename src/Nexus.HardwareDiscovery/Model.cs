namespace Nexus.HardwareDiscovery;

/// <summary>Como o valor foi obtido. Nunca se inventa um valor: sem fonte, é <see cref="Unavailable"/>.</summary>
public enum FactStatus
{
    /// <summary>Lido diretamente de uma fonte do sistema (API, registro, firmware).</summary>
    Detected,
    /// <summary>Deduzido de dados detectados; a regra está em <see cref="Fact.Note"/>.</summary>
    Inferred,
    /// <summary>Não foi possível obter; o motivo está em <see cref="Fact.Note"/>.</summary>
    Unavailable,
}

public sealed record Fact(string Label, string? Value, FactStatus Status, string Source, string? Note = null)
{
    public static Fact Detected(string label, string value, string source, string? note = null) => new(label, value, FactStatus.Detected, source, note);
    public static Fact Inferred(string label, string value, string source, string rule) => new(label, value, FactStatus.Inferred, source, rule);
    public static Fact Unavailable(string label, string source, string reason) => new(label, null, FactStatus.Unavailable, source, reason);

    /// <summary>Detected se houver valor; Unavailable com o motivo dado caso contrário.</summary>
    public static Fact FromOptional(string label, string? value, string source, string reasonIfMissing) =>
        string.IsNullOrWhiteSpace(value) ? Unavailable(label, source, reasonIfMissing) : Detected(label, value.Trim(), source);
}

public sealed record DiscoveryTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows,
    FactStatus Status, string Source, string? Note = null);

public sealed record DiscoverySection(string Title, IReadOnlyList<Fact> Facts, IReadOnlyList<DiscoveryTable> Tables)
{
    public DiscoverySection(string title, IReadOnlyList<Fact> facts) : this(title, facts, []) { }
}

public sealed record GpuInfo(string Name, string? Vendor, string? DriverVersion, string? DriverDate, string? DriverProvider, ulong? MemoryBytes);

public sealed record MemoryModule(string? Locator, ulong? SizeBytes, string? Type, uint? MaxSpeedMts, uint? ConfiguredSpeedMts,
    string? Manufacturer, string? PartNumber);

/// <param name="Media">Tipo do disco físico onde a unidade está (Unknown = não deu para saber).</param>
public sealed record DriveFacts(string Name, string? Format, ulong? TotalBytes, ulong? FreeBytes, DiskMedia Media = DiskMedia.Unknown);

public sealed record StartupItem(string Name, string Location, string Command, bool? Enabled);

public sealed record ServiceFacts(string Name, string? DisplayName, string? State, string? StartType);

/// <summary>Valores tipados usados pelo diagnóstico e pela simulação do plano. Null = indisponível.</summary>
public sealed class SystemFacts
{
    public int? WindowsBuild { get; init; }
    public string? WindowsEdition { get; init; }
    public string? CpuModel { get; init; }
    public string? CpuVendor { get; init; }
    public int? PhysicalCores { get; init; }
    public int? LogicalProcessors { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public ulong? TotalMemoryBytes { get; init; }
    public IReadOnlyList<MemoryModule> MemoryModules { get; init; } = [];
    public Guid? ActivePowerScheme { get; init; }
    public string? ActivePowerSchemeName { get; init; }
    /// <summary>Null = não determinado (não confundir com "não elevado").</summary>
    public bool? IsElevated { get; init; }
    public bool? IsVirtualMachine { get; init; }
    public string? SystemManufacturer { get; init; }
    public string? SystemModel { get; init; }
    public IReadOnlyList<DriveFacts> Drives { get; init; } = [];
    public string? SystemDrive { get; init; }
    /// <summary>Discos físicos (SSD/HDD pelo próprio disco; ver <see cref="StorageClassifier"/>).</summary>
    public IReadOnlyList<PhysicalDiskFacts> PhysicalDisks { get; init; } = [];
    /// <summary>Tipo do disco onde está o Windows (Unknown = não lido ou volume em discos de tipos diferentes).</summary>
    public DiskMedia SystemDiskMedia { get; init; }
    /// <summary>Barramento do disco do sistema ("NVMe", "SATA"...; null = não lido).</summary>
    public string? SystemDiskBus { get; init; }
    public IReadOnlyList<StartupItem> StartupItems { get; init; } = [];
    public IReadOnlyList<ServiceFacts> Services { get; init; } = [];
    /// <summary>Valores brutos de registro relevantes ao diagnóstico (chave → valor; ausente = não está no dicionário).</summary>
    public IReadOnlyDictionary<string, string> RegistrySignals { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// Sinais cuja leitura foi feita com sucesso, estando o valor presente ou ausente. Um sinal fora deste conjunto NÃO foi
    /// lido: sua ausência em <see cref="RegistrySignals"/> não significa "valor ausente no registro".
    /// </summary>
    public IReadOnlySet<string> ReadSignals { get; init; } = new HashSet<string>();
    /// <summary>Sinais que não puderam ser lidos (chave → motivo).</summary>
    public IReadOnlyDictionary<string, string> UnreadableSignals { get; init; } = new Dictionary<string, string>();

    public string? GpuVendorPrimary => Gpus.Select(g => g.Vendor).FirstOrDefault(v => v is not null and not "Microsoft");
}

public sealed record SystemSnapshot(DateTimeOffset TakenAt, IReadOnlyList<DiscoverySection> Sections, SystemFacts Facts, TimeSpan Elapsed);

public static class Units
{
    public static string Bytes(ulong? b)
    {
        if (b is not { } v) return "?";
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double d = v;
        var i = 0;
        while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
        return d.ToString(i == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + u[i];
    }
}

/// <summary>Chaves de <see cref="SystemFacts.RegistrySignals"/>.</summary>
public static class DiscoverySignals
{
    public const string GameMode = "GameMode.AutoGameModeEnabled";
    public const string AppCapture = "GameDVR.AppCaptureEnabled";
    public const string GameDvrEnabled = "GameConfigStore.GameDVR_Enabled";
    public const string Hags = "GraphicsDrivers.HwSchMode";
    public const string HvciEnabled = "DeviceGuard.HVCI.Enabled";
    public const string VbsEnabled = "DeviceGuard.EnableVirtualizationBasedSecurity";
}
