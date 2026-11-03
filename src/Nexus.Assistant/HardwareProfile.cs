using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Automation;
using Nexus.HardwareDiscovery;

namespace Nexus.Assistant;

/// <summary>Classe da placa de vídeo para as recomendações (tabela em docs/modules/recommendation.md).</summary>
public enum GpuClass
{
    /// <summary>Nenhuma placa lida (ou só adaptador de software).</summary>
    Unknown,
    /// <summary>Só vídeo integrado ao processador (Intel UHD/Iris/Arc integrada, AMD Radeon Graphics/Vega).</summary>
    Integrated,
    /// <summary>Placa dedicada de entrada: xx50 e abaixo, MX, RX x400/x500, Arc A3xx, ou até 4 GB de VRAM.</summary>
    Entry,
    /// <summary>Placa intermediária: xx60, RX x600/x700, Arc A5xx/A7xx/B5xx, 6–8 GB.</summary>
    Mid,
    /// <summary>Placa forte: RTX xx70 e acima, RX x800/x900 (RX 9070), 12 GB ou mais.</summary>
    Strong,
}

public enum GpuMaker { Unknown, Nvidia, Amd, Intel }

/// <summary>
/// Perfil do PC usado pelas recomendações automáticas por hardware. Montado só a partir da descoberta (somente leitura) e das
/// leituras extras (<see cref="PcTraits"/>); o que não foi lido fica null e NUNCA é presumido. "Competitivo" não é hardware:
/// é a preferência do usuário em Ajustes ("Prefiro menos atraso a imagem bonita").
/// </summary>
/// <param name="GpuName">Nome curto da placa principal ("GTX 1650"), ou null.</param>
/// <param name="VramGb">VRAM dedicada em GB (arredondada), ou null.</param>
/// <param name="GpuClassSource">Como a classe foi decidida (nome da família, VRAM ou integrada), para o texto e a doc.</param>
/// <param name="MemoryGb">RAM total em GB (arredondada), ou null.</param>
public sealed record HardwareProfile(
    GpuClass GpuClass,
    GpuMaker GpuMaker,
    string? GpuName,
    int? VramGb,
    string GpuClassSource,
    int? MemoryGb,
    bool? IsLaptop,
    bool? OnBattery,
    bool? Ssd,
    bool Competitive)
{
    /// <summary>Perfil sem leitura nenhuma: toda regra responde "neutro".</summary>
    public static readonly HardwareProfile Empty = new(GpuClass.Unknown, GpuMaker.Unknown, null, null, "sem leitura", null, null, null, null, false);

    /// <summary>8 GB ou menos (limite de <see cref="HardwareProfiler.LowRamGb"/>).</summary>
    public bool LowRam => MemoryGb is { } gb && gb <= HardwareProfiler.LowRamGb;

    /// <summary>Vídeo integrado ou placa de entrada.</summary>
    public bool WeakGpu => GpuClass is GpuClass.Integrated or GpuClass.Entry;

    /// <summary>Placa intermediária ou melhor.</summary>
    public bool StrongGpu => GpuClass is GpuClass.Mid or GpuClass.Strong;

    public bool Hdd => Ssd == false;
}

/// <summary>Classificação PURA do PC (testável em qualquer SO). A tabela da classe da placa está documentada em docs/modules/recommendation.md.</summary>
public static partial class HardwareProfiler
{
    /// <summary>"RAM baixa": 8 GB ou menos (requisito recomendado dos jogos atuais é 16 GB; ver a doc).</summary>
    public const int LowRamGb = 8;
    private const ulong Gb = 1024UL * 1024 * 1024;

    public static HardwareProfile Classify(SystemFacts facts, PcTraits? traits = null, bool competitive = false)
    {
        ArgumentNullException.ThrowIfNull(facts);
        traits ??= PcTraits.Unknown;

        // Placa principal: a dedicada com mais VRAM; sem dedicada, a integrada.
        var gpus = facts.Gpus.Where(g => !PcProfiler.IsSoftwareAdapter(g.Name ?? "")).ToList();
        var discrete = gpus.Where(PcProfiler.IsDiscrete).OrderByDescending(g => g.MemoryBytes ?? 0).FirstOrDefault();
        var main = discrete ?? gpus.FirstOrDefault();
        var maker = MakerOf(main);
        int? vram = main?.MemoryBytes is { } mb and > 0 ? (int)Math.Round((double)mb / Gb) : null;
        var (cls, source) = main is null ? (GpuClass.Unknown, "placa de vídeo não lida")
            : discrete is null ? (GpuClass.Integrated, "sem placa dedicada: vídeo integrado")
            : ClassOf(main.Name ?? "", maker, vram);

        int? memGb = facts.TotalMemoryBytes is { } total and > 0 ? (int)Math.Round((double)total / Gb) : null;

        var portable = PcTraits.IsPortableChassis(traits.ChassisType);
        bool? laptop = portable ?? (traits.HasSystemBattery == true ? true : null);
        bool? onBattery = traits.HasSystemBattery == true && traits.OnAcPower is { } ac ? !ac : null;

        bool? ssd = traits.SystemDriveIsSsd ?? facts.SystemDiskMedia switch { DiskMedia.Ssd => true, DiskMedia.Hdd => false, _ => null };

        return new HardwareProfile(cls, maker, main is null ? null : PcProfiler.ShortGpuName(main.Name ?? ""), vram, source, memGb, laptop, onBattery, ssd, competitive);
    }

    public static GpuMaker MakerOf(GpuInfo? gpu)
    {
        if (gpu is null) return GpuMaker.Unknown;
        var v = gpu.Vendor ?? "";
        var n = gpu.Name ?? "";
        if (v.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return GpuMaker.Nvidia;
        if (v.Equals("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || n.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return GpuMaker.Amd;
        if (v.Equals("Intel", StringComparison.OrdinalIgnoreCase) || n.Contains("Intel", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc", StringComparison.Ordinal)) return GpuMaker.Intel;
        return GpuMaker.Unknown;
    }

    [GeneratedRegex(@"\b(GTX|RTX)\s*(\d{2})(\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaFamily();

    [GeneratedRegex(@"\b(MX\s*\d{3}|GT\s*\d{3,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaEntry();

    [GeneratedRegex(@"\bRX\s*(\d)(\d{2,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmdFamily();

    [GeneratedRegex(@"\bArc(?:\(TM\))?\s+([AB])(\d)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelArc();

    /// <summary>
    /// Classe de uma placa DEDICADA pelo nome da família (segmentação dos próprios fabricantes: xx50 entrada, xx60 intermediária,
    /// xx70+ topo) e, sem família conhecida, pela VRAM. Tabela e fontes: docs/modules/recommendation.md.
    /// </summary>
    public static (GpuClass Class, string Source) ClassOf(string name, GpuMaker maker, int? vramGb)
    {
        ArgumentNullException.ThrowIfNull(name);
        switch (maker)
        {
            case GpuMaker.Nvidia:
            {
                if (NvidiaEntry().IsMatch(name)) return (GpuClass.Entry, "família NVIDIA MX/GT (entrada)");
                var m = NvidiaFamily().Match(name);
                if (m.Success)
                {
                    var rtx = m.Groups[1].Value.Equals("RTX", StringComparison.OrdinalIgnoreCase);
                    var tier = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture); // 50, 60, 70, 80, 90
                    if (tier <= 50) return (GpuClass.Entry, $"família NVIDIA {m.Value.ToUpperInvariant()} (xx50 ou abaixo)");
                    if (tier == 60 || !rtx) return (GpuClass.Mid, $"família NVIDIA {m.Value.ToUpperInvariant()} ({(rtx ? "xx60" : "GTX")})");
                    return (GpuClass.Strong, $"família NVIDIA {m.Value.ToUpperInvariant()} (RTX xx70 ou acima)");
                }
                break;
            }
            case GpuMaker.Amd:
            {
                var m = AmdFamily().Match(name);
                if (m.Success)
                {
                    var gen = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var rest = m.Groups[2].Value; // "650", "070", "80"
                    var label = $"família AMD {m.Value.ToUpperInvariant()}";
                    if (rest.Length == 2) // RX 550/560/570/580/590
                    {
                        var t = int.Parse(rest, CultureInfo.InvariantCulture);
                        return t <= 60 ? (GpuClass.Entry, label + " (RX 5x0 de entrada)") : (GpuClass.Mid, label + " (RX 570/580/590)");
                    }
                    var hundreds = int.Parse(rest, CultureInfo.InvariantCulture);
                    if (gen >= 9) // RX 9060/9070: "9070" → 0 7 0, a dezena é o segmento (como xx60/xx70 da NVIDIA)
                    {
                        var tier = hundreds % 100;
                        if (tier <= 50) return (GpuClass.Entry, label + " (x050 ou abaixo)");
                        if (tier == 60) return (GpuClass.Mid, label + " (x060)");
                        return (GpuClass.Strong, label + " (x070 ou acima)");
                    }
                    if (hundreds < 600) return (GpuClass.Entry, label + " (x400/x500)");
                    if (hundreds < 800) return (GpuClass.Mid, label + " (x600/x700)");
                    return (GpuClass.Strong, label + " (x800/x900)");
                }
                break;
            }
            case GpuMaker.Intel:
            {
                var m = IntelArc().Match(name);
                if (m.Success)
                {
                    var series = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    var label = $"família Intel {m.Value}";
                    return series <= 3 ? (GpuClass.Entry, label + " (A3xx)") : (GpuClass.Mid, label + " (A5xx/A7xx/B5xx)");
                }
                break;
            }
        }
        // Sem família conhecida: só a VRAM dedicada (critério fraco, documentado como tal).
        return vramGb switch
        {
            null => (GpuClass.Mid, "família desconhecida e VRAM não lida: tratada como intermediária"),
            <= 4 => (GpuClass.Entry, $"família desconhecida: {vramGb} GB de VRAM"),
            < 12 => (GpuClass.Mid, $"família desconhecida: {vramGb} GB de VRAM"),
            _ => (GpuClass.Strong, $"família desconhecida: {vramGb} GB de VRAM"),
        };
    }
}
