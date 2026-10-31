using System.Globalization;
using Nexus.HardwareDiscovery;

namespace Nexus.Lab.Memory;

/// <summary>Resultado da leitura do SMBIOS já interpretado: módulos, canais, números de peça e dados da máquina.</summary>
public sealed record MemoryAnalysis(MemoryInventory Inventory, ChannelLayout Layout, IReadOnlyList<PartNumberInfo> Parts, MachineInfo Machine)
{
    /// <summary>Velocidade configurada comum (a menor entre os módulos; null se o firmware não informa).</summary>
    public uint? ConfiguredMts => Inventory.Populated.Select(d => d.ConfiguredSpeedMts ?? d.SpeedMts).Where(s => s is not null).DefaultIfEmpty(null).Min();

    /// <summary>Largura de um canal do tipo instalado (DDR4/DDR5 DIMM = 64 bits; LPDDR = largura informada do canal).</summary>
    public int WidthPerChannelBits => Layout.ChannelCount > 0 ? Math.Max(16, Layout.BusWidthBits / Layout.ChannelCount) : 64;

    public double TheoreticalGbps => ConfiguredMts is { } mts ? BandwidthTheory.TheoreticalGbps(mts, Layout.BusWidthBits) : 0;

    /// <summary>Linha do cabeçalho, por exemplo "8 GB DDR4-3200 · 1 módulo · SINGLE CHANNEL".</summary>
    public string Headline
    {
        get
        {
            var p = Inventory.Populated;
            if (p.Count == 0) return "Memória não identificada pelo SMBIOS";
            var gb = Inventory.InstalledBytes / (1024.0 * 1024 * 1024);
            var type = p.Select(d => d.Type).FirstOrDefault(t => t is not null) ?? "RAM";
            var speed = ConfiguredMts is { } s ? string.Create(CultureInfo.InvariantCulture, $"-{s}") : "";
            var mods = p.Count == 1 ? "1 módulo" : string.Create(CultureInfo.InvariantCulture, $"{p.Count} módulos");
            var mode = Layout.Mode switch
            {
                ChannelMode.SingleChannel => "SINGLE CHANNEL",
                ChannelMode.DualChannel => "DUAL CHANNEL",
                ChannelMode.QuadChannel => "QUAD CHANNEL",
                ChannelMode.MultiChannel => string.Create(CultureInfo.InvariantCulture, $"{Layout.ChannelCount} CANAIS"),
                _ => "canais não identificados",
            };
            return string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{gb:0.#} GB {type}{speed} · {mods} · {mode}");
        }
    }
}

public static class MemoryLabAnalyzer
{
    public static MemoryAnalysis Analyze(ReadOnlySpan<byte> rawSmbios, IReadOnlyList<GpuInfo>? gpus = null) =>
        Analyze(SmbiosParser.Parse(rawSmbios), gpus);

    public static MemoryAnalysis Analyze(SmbiosParser.Table table, IReadOnlyList<GpuInfo>? gpus = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var inventory = SmbiosMemory.FromTable(table);
        var layout = ChannelInference.Infer(inventory.Devices);
        var parts = inventory.Populated.Select(d =>
        {
            var info = PartNumberDecoder.Decode(d.PartNumber);
            if (info.Vendor is null && JedecVendors.Resolve(d.Manufacturer, d.ModuleManufacturerId).Name is { } v) info = info with { Vendor = v };
            return info;
        }).ToList();
        var (sysMaker, model) = SmbiosParser.SystemInfo(table);
        var (boardMaker, _) = SmbiosParser.Baseboard(table);
        var g = gpus ?? [];
        var integrated = g.Any(IsIntegrated);
        var discrete = g.Any(x => !IsIntegrated(x) && x.Vendor is not null and not "Microsoft");
        var machine = new MachineInfo(SmbiosMemory.Clean(sysMaker), SmbiosMemory.Clean(model), SmbiosMemory.Clean(boardMaker), SmbiosMemory.IsPortable(table), integrated, discrete);
        return new MemoryAnalysis(inventory, layout, parts, machine);
    }

    /// <summary>GPU integrada pelo nome (Radeon "Graphics" sem modelo RX/Pro; Intel UHD/Iris/HD Graphics).</summary>
    public static bool IsIntegrated(GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        var n = gpu.Name;
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
            (n.Contains("UHD", StringComparison.OrdinalIgnoreCase) || n.Contains("Iris", StringComparison.OrdinalIgnoreCase) || n.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)))
            return true;
        return n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && n.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
               && !n.Contains("RX", StringComparison.Ordinal) && !n.Contains("Pro", StringComparison.OrdinalIgnoreCase);
    }
}
