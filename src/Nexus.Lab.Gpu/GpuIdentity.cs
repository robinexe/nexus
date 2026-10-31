using System.Globalization;

namespace Nexus.Lab.Gpu;

/// <summary>Fabricante pelo PCI Vendor ID (registrado no PCI-SIG).</summary>
public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Other }

/// <summary>
/// Arquitetura deduzida do PCI Device ID por faixas conhecidas (tabela pública dos drivers / pci.ids). É uma DEDUÇÃO:
/// a página mostra "deduzido do Device ID" e, fora das faixas, diz "não identificada" em vez de chutar.
/// </summary>
/// <param name="Name">Nome comercial da arquitetura (ex.: "Ampere").</param>
/// <param name="Family">Linha de produtos típica (ex.: "GeForce RTX 30").</param>
/// <param name="Year">Ano de lançamento da arquitetura.</param>
/// <param name="Integrated">Faixa usada por gráficos integrados (iGPU dentro do processador).</param>
/// <param name="ResizableBarCapable">A geração suporta Resizable BAR (com BIOS/CPU compatíveis).</param>
public sealed record GpuArchitecture(string Name, string Family, int Year, bool Integrated = false, bool ResizableBarCapable = false)
{
    public static readonly GpuArchitecture Unknown = new("não identificada", "", 0);
}

public static class GpuIdentity
{
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorAmd = 0x1002;
    public const uint VendorIntel = 0x8086;
    public const uint VendorMicrosoft = 0x1414; // Microsoft Basic Render Driver / WARP

    public static GpuVendor VendorOf(uint vendorId) => vendorId switch
    {
        VendorNvidia => GpuVendor.Nvidia,
        VendorAmd => GpuVendor.Amd,
        VendorIntel => GpuVendor.Intel,
        VendorMicrosoft => GpuVendor.Microsoft,
        0 => GpuVendor.Unknown,
        _ => GpuVendor.Other,
    };

    public static string VendorName(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Microsoft => "Microsoft",
        GpuVendor.Other => "Outro",
        _ => "Desconhecido",
    };

    private sealed record Range(uint From, uint To, GpuArchitecture Arch);

    private static readonly GpuArchitecture NvKepler = new("Kepler ou anterior", "GeForce 600/700", 2012);
    private static readonly GpuArchitecture NvMaxwell = new("Maxwell", "GeForce 900 / GTX 750", 2014);
    private static readonly GpuArchitecture NvPascal = new("Pascal", "GeForce GTX 10", 2016);
    private static readonly GpuArchitecture NvVolta = new("Volta", "Titan V / Tesla V100", 2017);
    private static readonly GpuArchitecture NvTuring = new("Turing", "GeForce RTX 20 / GTX 16", 2018);
    private static readonly GpuArchitecture NvAmpere = new("Ampere", "GeForce RTX 30", 2020, ResizableBarCapable: true);
    private static readonly GpuArchitecture NvHopper = new("Hopper", "H100 (data center)", 2022);
    private static readonly GpuArchitecture NvAda = new("Ada Lovelace", "GeForce RTX 40", 2022, ResizableBarCapable: true);
    private static readonly GpuArchitecture NvBlackwell = new("Blackwell", "GeForce RTX 50", 2025, ResizableBarCapable: true);

    private static readonly Range[] Nvidia =
    [
        new(0x1340, 0x14FF, NvMaxwell), new(0x1600, 0x17FF, NvMaxwell),
        new(0x15F0, 0x15FF, NvPascal), new(0x1B00, 0x1D7F, NvPascal),
        new(0x1D80, 0x1DFF, NvVolta),
        new(0x1E00, 0x1FFF, NvTuring), new(0x2180, 0x21FF, NvTuring),
        new(0x2000, 0x217F, NvAmpere), new(0x2200, 0x22FF, NvAmpere), new(0x2400, 0x25FF, NvAmpere),
        new(0x2300, 0x23FF, NvHopper),
        new(0x2600, 0x28FF, NvAda),
        new(0x2900, 0x2FFF, NvBlackwell),
    ];

    private static readonly GpuArchitecture AmdGcn = new("GCN (1ª–3ª geração)", "Radeon HD 7000 / R7 / R9", 2012);
    private static readonly GpuArchitecture AmdPolaris = new("GCN 4 (Polaris)", "Radeon RX 400/500", 2016);
    private static readonly GpuArchitecture AmdVega = new("GCN 5 (Vega)", "Radeon RX Vega / Radeon VII", 2017);
    private static readonly GpuArchitecture AmdVegaIgpu = new("GCN 5 (Vega integrada)", "Radeon Graphics (Ryzen 2000–5000 móvel)", 2018, Integrated: true);
    private static readonly GpuArchitecture AmdRdna1 = new("RDNA 1", "Radeon RX 5000", 2019);
    private static readonly GpuArchitecture AmdRdna2 = new("RDNA 2", "Radeon RX 6000", 2020, ResizableBarCapable: true);
    private static readonly GpuArchitecture AmdRdna2Igpu = new("RDNA 2 (integrada)", "Radeon 610M/660M/680M", 2022, Integrated: true);
    private static readonly GpuArchitecture AmdRdna3 = new("RDNA 3", "Radeon RX 7000", 2022, ResizableBarCapable: true);
    private static readonly GpuArchitecture AmdRdna3Igpu = new("RDNA 3/3.5 (integrada)", "Radeon 740M/760M/780M/890M", 2023, Integrated: true);
    private static readonly GpuArchitecture AmdRdna4 = new("RDNA 4", "Radeon RX 9000", 2025, ResizableBarCapable: true);

    private static readonly Range[] Amd =
    [
        new(0x6600, 0x66FF, AmdGcn), new(0x6700, 0x67BF, AmdGcn), new(0x6800, 0x685F, AmdGcn), new(0x6900, 0x697F, AmdGcn),
        new(0x67C0, 0x67FF, AmdPolaris), new(0x6980, 0x699F, AmdPolaris),
        new(0x66A0, 0x66AF, AmdVega), new(0x6860, 0x687F, AmdVega), new(0x69A0, 0x69AF, AmdVega),
        new(0x15D8, 0x15D8, AmdVegaIgpu), new(0x15DD, 0x15DD, AmdVegaIgpu), new(0x1636, 0x1636, AmdVegaIgpu),
        new(0x1638, 0x1638, AmdVegaIgpu), new(0x164C, 0x164C, AmdVegaIgpu), new(0x15E7, 0x15E7, AmdVegaIgpu),
        new(0x7310, 0x736F, AmdRdna1),
        new(0x73A0, 0x743F, AmdRdna2),
        new(0x1681, 0x1681, AmdRdna2Igpu), new(0x163F, 0x163F, AmdRdna2Igpu), new(0x164E, 0x164E, AmdRdna2Igpu), new(0x1506, 0x1506, AmdRdna2Igpu),
        new(0x7440, 0x74FF, AmdRdna3),
        new(0x15BF, 0x15BF, AmdRdna3Igpu), new(0x15C8, 0x15C8, AmdRdna3Igpu), new(0x1900, 0x1901, AmdRdna3Igpu), new(0x150E, 0x150E, AmdRdna3Igpu),
        new(0x7550, 0x75FF, AmdRdna4),
    ];

    private static readonly GpuArchitecture IntelGen9 = new("Gen 9–11 (UHD/Iris)", "Intel HD/UHD/Iris Plus", 2015, Integrated: true);
    private static readonly GpuArchitecture IntelXeLp = new("Xe-LP", "Intel Iris Xe / UHD (11ª–14ª ger.)", 2020, Integrated: true);
    private static readonly GpuArchitecture IntelXeLpDg1 = new("Xe-LP (DG1)", "Intel Iris Xe MAX", 2020);
    private static readonly GpuArchitecture IntelXeLpg = new("Xe-LPG", "Intel Arc Graphics (Core Ultra)", 2023, Integrated: true);
    private static readonly GpuArchitecture IntelXe2Lpg = new("Xe2-LPG", "Intel Arc 130V/140V (Lunar Lake)", 2024, Integrated: true);
    private static readonly GpuArchitecture IntelAlchemist = new("Xe-HPG (Alchemist)", "Intel Arc A", 2022, ResizableBarCapable: true);
    private static readonly GpuArchitecture IntelBattlemage = new("Xe2-HPG (Battlemage)", "Intel Arc B", 2024, ResizableBarCapable: true);

    private static readonly Range[] Intel =
    [
        new(0x1900, 0x19FF, IntelGen9), new(0x5900, 0x59FF, IntelGen9), new(0x3E90, 0x3EFF, IntelGen9), new(0x9B00, 0x9BFF, IntelGen9),
        new(0x8A50, 0x8A5F, IntelGen9), new(0x4E50, 0x4E7F, IntelGen9),
        new(0x9A40, 0x9AFF, IntelXeLp), new(0x4C80, 0x4C9F, IntelXeLp), new(0x4680, 0x46FF, IntelXeLp), new(0xA780, 0xA7FF, IntelXeLp),
        new(0x4600, 0x467F, IntelXeLp), new(0xA700, 0xA77F, IntelXeLp),
        new(0x4905, 0x4908, IntelXeLpDg1),
        new(0x7D40, 0x7DFF, IntelXeLpg),
        new(0x6420, 0x64BF, IntelXe2Lpg),
        new(0x5690, 0x56CF, IntelAlchemist),
        new(0xE200, 0xE2FF, IntelBattlemage),
    ];

    /// <summary>Arquitetura pelo par Vendor/Device ID; <see cref="GpuArchitecture.Unknown"/> fora das faixas conhecidas.</summary>
    public static GpuArchitecture Architecture(uint vendorId, uint deviceId)
    {
        var table = VendorOf(vendorId) switch
        {
            GpuVendor.Nvidia => Nvidia,
            GpuVendor.Amd => Amd,
            GpuVendor.Intel => Intel,
            _ => null,
        };
        if (table is null) return GpuArchitecture.Unknown;
        // Faixas específicas (uma só id) vencem faixas largas: procura a menor faixa que contém o id.
        var hit = table.Where(r => deviceId >= r.From && deviceId <= r.To).OrderBy(r => r.To - r.From).FirstOrDefault();
        if (hit is not null) return hit.Arch;
        if (VendorOf(vendorId) == GpuVendor.Nvidia && deviceId is > 0 and < 0x1340) return NvKepler;
        return GpuArchitecture.Unknown;
    }

    /// <summary>
    /// "PCI\VEN_10DE&amp;DEV_25A2&amp;SUBSYS_14E51025&amp;REV_A1" → (0x10DE, 0x25A2, 0x14E51025). Null se não for um id PCI.
    /// </summary>
    public static (uint Vendor, uint Device, uint? SubSys)? ParseHardwareId(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        var s = hardwareId.ToUpperInvariant();
        if (!s.StartsWith(@"PCI\", StringComparison.Ordinal)) return null;
        uint? ven = Field(s, "VEN_", 4), dev = Field(s, "DEV_", 4), sub = Field(s, "SUBSYS_", 8);
        return ven is { } v && dev is { } d ? (v, d, sub) : null;
    }

    private static uint? Field(string s, string tag, int len)
    {
        var i = s.IndexOf(tag, StringComparison.Ordinal);
        if (i < 0 || i + tag.Length + len > s.Length) return null;
        return uint.TryParse(s.AsSpan(i + tag.Length, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// iGPU ou dedicada: a tabela decide quando a faixa é de integrada; senão, o padrão do DXGI (memória dedicada pequena
    /// e memória compartilhada grande) é o que o Gerenciador de Tarefas também usa para "GPU integrada".
    /// </summary>
    public static bool IsIntegrated(uint vendorId, uint deviceId, ulong dedicatedBytes)
    {
        var arch = Architecture(vendorId, deviceId);
        if (arch.Integrated) return true;
        if (VendorOf(vendorId) == GpuVendor.Nvidia) return false;
        if (VendorOf(vendorId) == GpuVendor.Intel && arch == GpuArchitecture.Unknown) return dedicatedBytes < 1UL << 30;
        return arch == GpuArchitecture.Unknown && dedicatedBytes <= 512UL << 20;
    }

    /// <summary>Chave do adaptador nos contadores PDH "GPU Engine" (luid_0xHHHHHHHH_0xLLLLLLLL_phys_0), para casar com Nexus.Monitoring.</summary>
    public static string PdhAdapterKey(long luid) =>
        $"luid_0x{(uint)(luid >> 32):x8}_0x{(uint)(luid & 0xFFFFFFFF):x8}_phys_0";
}
