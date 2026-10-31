using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Lab.Memory;

public enum DecodeConfidence { None, Low, Medium, High }

/// <summary>
/// O que o número de peça revela. Campo null = o número não diz (nunca um palpite). <see cref="Profile"/>:
/// "JEDEC" (a velocidade anunciada é a padrão, sem perfil), "XMP" ou "EXPO" (a velocidade anunciada exige ligar o perfil no BIOS).
/// </summary>
public sealed record PartNumberInfo
{
    public required string PartNumber { get; init; }
    public string? Vendor { get; init; }
    public string? Family { get; init; }
    public string? Generation { get; init; }
    public int? SpeedMts { get; init; }
    public int? CasLatency { get; init; }
    /// <summary>A latência veio da tabela JEDEC para a velocidade (o número de peça não a traz explícita).</summary>
    public bool CasLatencyInferred { get; init; }
    public ulong? ModuleBytes { get; init; }
    public int? KitModules { get; init; }
    public ulong? KitTotalBytes { get; init; }
    public MemoryFormFactor FormFactor { get; init; }
    public string? Organization { get; init; }
    public string? Profile { get; init; }
    public DecodeConfidence Confidence { get; init; }
    public string Rule { get; init; } = "";
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool Decoded => Confidence != DecodeConfidence.None;

    /// <summary>Resumo de uma linha, por exemplo "Kingston Fury Beast · DDR4-3200 CL16 · 8 GB · XMP".</summary>
    public string Summary
    {
        get
        {
            if (!Decoded) return "Número de peça não decodificado (desconhecido).";
            var parts = new List<string>();
            var name = string.Join(' ', new[] { Vendor, Family }.Where(s => s is not null));
            if (name.Length > 0) parts.Add(name);
            var speed = (Generation, SpeedMts) switch
            {
                ({ } g, { } s) => $"{g}-{s}",
                ({ } g, null) => g,
                (null, { } s) => $"{s} MT/s",
                _ => null,
            };
            if (speed is not null) parts.Add(CasLatency is { } cl ? $"{speed} CL{cl}{(CasLatencyInferred ? " (padrão JEDEC)" : "")}" : speed);
            if (KitModules is > 1 && ModuleBytes is { } mb) parts.Add($"kit {KitModules}×{Gb(mb)}");
            else if (ModuleBytes is { } m) parts.Add(Gb(m));
            if (FormFactor == MemoryFormFactor.SoDimm) parts.Add("SODIMM");
            if (Organization is not null) parts.Add(Organization);
            if (Profile is not null) parts.Add(Profile);
            return string.Join(" · ", parts);
        }
    }

    internal static string Gb(ulong bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024UL * 1024 * 1024)} GB");
}

/// <summary>
/// "Engenharia reversa" do número de peça: regras por fabricante, cada uma baseada no padrão público de nomenclatura do
/// fabricante (guias de decodificação de Kingston, Corsair, G.Skill, Crucial/Micron, Samsung, SK hynix, ADATA, TeamGroup).
/// O que não casa com uma regra conhecida volta como "desconhecido".
/// </summary>
public static partial class PartNumberDecoder
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    public static PartNumberInfo Decode(string? partNumber)
    {
        var raw = SmbiosMemory.Clean(partNumber);
        if (raw is null) return new PartNumberInfo { PartNumber = "", Rule = "vazio" };
        var pn = raw.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);

        foreach (var rule in Rules)
        {
            try
            {
                if (rule(pn) is { } info) return info with { PartNumber = raw };
            }
            catch (RegexMatchTimeoutException) { /* regra seguinte */ }
        }
        if (pn.StartsWith("9905", StringComparison.Ordinal) || pn.StartsWith("9965", StringComparison.Ordinal))
            return new PartNumberInfo { PartNumber = raw, Vendor = "Kingston", Rule = "kingston-interno", Notes = ["Número interno da Kingston (fábrica): não revela velocidade nem latência."] };
        return new PartNumberInfo { PartNumber = raw, Rule = "nenhuma" };
    }

    private static readonly Func<string, PartNumberInfo?>[] Rules =
    [
        KingstonFury, KingstonHyperX, KingstonHyperXOld, KingstonValueRam, KingstonSystemSpecific, KingstonOem,
        Corsair, GSkillDdr4, GSkillDdr5, CrucialDdr4, CrucialDdr5, Ballistix, Samsung, HynixDdr4, HynixOther,
        MicronDdr4, MicronDdr5, AdataXpgDdr5, AdataXpg, Adata, TeamGroup, Ramaxel,
    ];

    // ---------------------------------------------------------------- utilidades

    private static int I(Match m, string g) => int.Parse(m.Groups[g].Value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static int? OptI(Match m, string g) => m.Groups[g].Success && m.Groups[g].Value.Length > 0 ? I(m, g) : null;

    /// <summary>Código de 2 dígitos de velocidade (Kingston, Crucial…): 26 = 2666, 29 = 2933, 21 = 2133, demais ×100.</summary>
    internal static int Speed2(int code) => code switch
    {
        10 => 1066, 13 => 1333, 18 => 1866, 21 => 2133, 26 => 2666, 29 => 2933, 33 => 3333, 37 => 3733, 46 => 4600,
        _ => code * 100,
    };

    private static int Speed3(string code) => code.Length switch
    {
        3 when code == "266" => 2666,
        3 when code == "213" => 2133,
        3 when code == "293" => 2933,
        3 => int.Parse(code, CultureInfo.InvariantCulture) * 10,
        _ => Speed2(int.Parse(code, CultureInfo.InvariantCulture)),
    };

    /// <summary>Latência padrão JEDEC mais comum para a velocidade (para módulos "valor" sem CL no número).</summary>
    internal static int? JedecCl(string? gen, int speed) => (gen, speed) switch
    {
        ("DDR3", 1333) => 9, ("DDR3", 1600) => 11,
        ("DDR4", 2133) => 15, ("DDR4", 2400) => 17, ("DDR4", 2666) => 19, ("DDR4", 2933) => 21, ("DDR4", 3200) => 22,
        ("DDR5", 4800) => 40, ("DDR5", 5200) => 42, ("DDR5", 5600) => 46, ("DDR5", 6400) => 52,
        _ => null,
    };

    private static string Gen(char digit) => $"DDR{digit}";

    private static (ulong? Module, int? Kit, ulong? Total) Kit(int totalGb, int? modules)
    {
        var n = modules is > 0 ? modules.Value : 1;
        return totalGb % n == 0 ? ((ulong)(totalGb / n) * GiB, n, (ulong)totalGb * GiB) : (null, n, (ulong)totalGb * GiB);
    }

    private static string? Rank(char rk, char width)
    {
        var w = width switch { '4' => "x4", '8' => "x8", '6' => "x16", _ => null };
        var r = rk switch { 'S' => "1R", 'D' => "2R", 'Q' => "4R", _ => null };
        return r is null || w is null ? null : r + w;
    }

    // ---------------------------------------------------------------- Kingston

    [GeneratedRegex(@"\AKF(?<gen>[345])(?<spd>\d{2})C(?<cl>\d{2})(?<fam>[A-Z])(?<rest>[A-Z]*?)(?:K(?<kit>\d))?[/-](?<size>\d+)", RegexOptions.None, 200)]
    private static partial Regex KfRx();

    private static PartNumberInfo? KingstonFury(string pn)
    {
        var m = KfRx().Match(pn);
        if (!m.Success) return null;
        var fam = m.Groups["fam"].Value[0];
        var rest = m.Groups["rest"].Value;
        var (module, kit, total) = Kit(I(m, "size"), OptI(m, "kit"));
        var family = fam switch { 'B' => "Fury Beast", 'R' => "Fury Renegade", 'I' => "Fury Impact", _ => "Fury" };
        var notes = new List<string>();
        if (rest.Contains('A', StringComparison.Ordinal)) { family += " RGB"; }
        var profile = rest.Contains('E', StringComparison.Ordinal) ? "EXPO" : "XMP";
        if (fam == 'I') notes.Add("Fury Impact (notebook): ajusta a velocidade sozinho (Plug and Play) até onde o notebook permite.");
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Kingston", Family = family, Generation = Gen(m.Groups["gen"].Value[0]),
            SpeedMts = Speed2(I(m, "spd")), CasLatency = I(m, "cl"), ModuleBytes = module, KitModules = kit, KitTotalBytes = total,
            FormFactor = fam == 'I' ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm, Profile = profile,
            Confidence = DecodeConfidence.High, Rule = "kingston-fury", Notes = notes,
        };
    }

    [GeneratedRegex(@"\AHX(?<gen>[345])(?<spd>\d{2})(?<ff>[CS])(?<cl>\d{2})(?<fam>[A-Z]+?)(?<v>\d)?(?:K(?<kit>\d))?/(?<size>\d+)", RegexOptions.None, 200)]
    private static partial Regex HxRx();

    private static PartNumberInfo? KingstonHyperX(string pn)
    {
        var m = HxRx().Match(pn);
        if (!m.Success) return null;
        var (module, kit, total) = Kit(I(m, "size"), OptI(m, "kit"));
        var family = m.Groups["fam"].Value[0] switch { 'F' => "HyperX Fury", 'P' => "HyperX Predator", 'I' => "HyperX Impact", _ => "HyperX" };
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Kingston", Family = family, Generation = Gen(m.Groups["gen"].Value[0]),
            SpeedMts = Speed2(I(m, "spd")), CasLatency = I(m, "cl"), ModuleBytes = module, KitModules = kit, KitTotalBytes = total,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm, Profile = "XMP",
            Confidence = DecodeConfidence.High, Rule = "kingston-hyperx",
        };
    }

    [GeneratedRegex(@"\AKHX(?<spd>\d{4})C(?<cl>\d{1,2})(?<ff>[DS])(?<gen>[345])(?:K(?<kit>\d))?/(?<size>\d+)", RegexOptions.None, 200)]
    private static partial Regex KhxRx();

    private static PartNumberInfo? KingstonHyperXOld(string pn)
    {
        var m = KhxRx().Match(pn);
        if (!m.Success) return null;
        var (module, kit, total) = Kit(I(m, "size"), OptI(m, "kit"));
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Kingston", Family = "HyperX", Generation = Gen(m.Groups["gen"].Value[0]),
            SpeedMts = I(m, "spd"), CasLatency = I(m, "cl"), ModuleBytes = module, KitModules = kit, KitTotalBytes = total,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm, Profile = "XMP",
            Confidence = DecodeConfidence.High, Rule = "kingston-khx",
        };
    }

    [GeneratedRegex(@"\AKVR(?<spd>\d{2})(?<ff>[NSEUR])(?<cl>\d{2})(?<b>B)?(?:(?<rk>[SDQ])(?<w>[468]))?L?(?:K(?<kit>\d))?[/-](?<size>\d+)", RegexOptions.None, 200)]
    private static partial Regex KvrRx();

    private static PartNumberInfo? KingstonValueRam(string pn)
    {
        var m = KvrRx().Match(pn);
        if (!m.Success) return null;
        var code = I(m, "spd");
        var gen = m.Groups["b"].Success || code >= 44 ? "DDR5" : code is 10 or 13 or 16 or 18 ? "DDR3" : "DDR4";
        var (module, kit, total) = Kit(I(m, "size"), OptI(m, "kit"));
        var ff = m.Groups["ff"].Value[0];
        var notes = new List<string>();
        if (ff == 'E') notes.Add("Módulo com ECC (servidor/estação de trabalho).");
        if (ff == 'R') notes.Add("Módulo registrado (RDIMM): não funciona em placa-mãe comum.");
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Kingston", Family = "ValueRAM", Generation = gen, SpeedMts = Speed2(code), CasLatency = I(m, "cl"),
            ModuleBytes = module, KitModules = kit, KitTotalBytes = total,
            FormFactor = ff == 'S' ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Organization = m.Groups["rk"].Success ? Rank(m.Groups["rk"].Value[0], m.Groups["w"].Value[0]) : null,
            Profile = "JEDEC", Confidence = DecodeConfidence.High, Rule = "kingston-valueram", Notes = notes,
        };
    }

    [GeneratedRegex(@"\AKCP(?<gen>[345])(?<spd>\d{2})(?<ff>[NSE])(?<rk>[SD])(?<w>[468])(?:K(?<kit>\d))?/(?<size>\d+)", RegexOptions.None, 200)]
    private static partial Regex KcpRx();

    private static PartNumberInfo? KingstonSystemSpecific(string pn)
    {
        var m = KcpRx().Match(pn);
        if (!m.Success) return null;
        var gen = Gen(m.Groups["gen"].Value[0]);
        var speed = Speed2(I(m, "spd"));
        var (module, kit, total) = Kit(I(m, "size"), OptI(m, "kit"));
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Kingston", Family = "memória específica de sistema", Generation = gen, SpeedMts = speed,
            CasLatency = JedecCl(gen, speed), CasLatencyInferred = JedecCl(gen, speed) is not null,
            ModuleBytes = module, KitModules = kit, KitTotalBytes = total,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Organization = Rank(m.Groups["rk"].Value[0], m.Groups["w"].Value[0]), Profile = "JEDEC",
            Confidence = DecodeConfidence.High, Rule = "kingston-kcp",
        };
    }

    [GeneratedRegex(@"\A(?<oem>ACR|HP|DL|LV|AS|MSI)(?<spd>\d{2})D(?<gen>[345])(?<ff>[SU])(?<cl>\d)(?<rk>[SD])(?<w>\d)[A-Z]{1,3}-(?<size>\d+)\z", RegexOptions.None, 200)]
    private static partial Regex KoemRx();

    private static PartNumberInfo? KingstonOem(string pn)
    {
        var m = KoemRx().Match(pn);
        if (!m.Success) return null;
        var gen = Gen(m.Groups["gen"].Value[0]);
        var speed = Speed2(I(m, "spd"));
        var digit = I(m, "cl");
        int? cl = JedecCl(gen, speed) is { } j && j % 10 == digit ? j : null;
        var oem = m.Groups["oem"].Value switch { "ACR" => "Acer", "HP" => "HP", "DL" => "Dell", "LV" => "Lenovo", "AS" => "ASUS", _ => "MSI" };
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Kingston", Family = $"módulo OEM para {oem}", Generation = gen, SpeedMts = speed, CasLatency = cl,
            ModuleBytes = (ulong)I(m, "size") * GiB, KitModules = 1, KitTotalBytes = (ulong)I(m, "size") * GiB,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Organization = Rank(m.Groups["rk"].Value[0], m.Groups["w"].Value[0]), Profile = "JEDEC",
            Confidence = DecodeConfidence.Medium, Rule = "kingston-oem",
            Notes = ["Padrão de peça OEM da Kingston (vem de fábrica no notebook). Velocidade e tamanho seguem o padrão público; a latência é a JEDEC quando o dígito confere."],
        };
    }

    // ---------------------------------------------------------------- Corsair

    [GeneratedRegex(@"\ACM(?<series>[A-Z]{1,4}?)(?<size>\d+)GX(?<gen>[345])M(?<mods>\d)(?<rev>[A-Z])(?<spd>\d{4})(?<prof>[CZ])(?<cl>\d{2})", RegexOptions.None, 200)]
    private static partial Regex CorsairRx();

    private static PartNumberInfo? Corsair(string pn)
    {
        var m = CorsairRx().Match(pn);
        if (!m.Success) return null;
        var series = m.Groups["series"].Value;
        var gen = Gen(m.Groups["gen"].Value[0]);
        var family = series switch
        {
            "K" => gen == "DDR4" ? "Vengeance LPX" : "Vengeance",
            "W" => "Vengeance RGB Pro",
            "H" => "Vengeance RGB",
            "T" => "Dominator Platinum RGB",
            "D" => "Dominator Platinum",
            "U" => "Vengeance LED",
            "SX" => "Vengeance SODIMM",
            "SO" => "ValueSelect SODIMM",
            "V" => "ValueSelect",
            _ => null,
        };
        var jedec = series is "V" or "SO";
        var (module, kit, total) = Kit(I(m, "size"), I(m, "mods"));
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Corsair", Family = family, Generation = gen, SpeedMts = I(m, "spd"), CasLatency = I(m, "cl"),
            ModuleBytes = module, KitModules = kit, KitTotalBytes = total,
            FormFactor = series.StartsWith('S') ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Profile = jedec ? "JEDEC" : m.Groups["prof"].Value == "Z" ? "EXPO" : "XMP",
            Confidence = family is null ? DecodeConfidence.Medium : DecodeConfidence.High, Rule = "corsair",
            Notes = family is null ? ["Linha (série) Corsair não reconhecida; velocidade, latência e tamanho vêm do padrão comum do número."] : [],
        };
    }

    // ---------------------------------------------------------------- G.Skill

    [GeneratedRegex(@"\AF(?<gen>[34])-(?<spd>\d{4})(?<x>[CU])(?<cl>\d{1,2})(?<cfg>[SDQO])-(?<total>\d+)G(?<series>[A-Z0-9]*)", RegexOptions.None, 200)]
    private static partial Regex GSkill4Rx();

    private static int CfgCount(char c) => c switch { 'D' => 2, 'Q' => 4, 'O' => 8, _ => 1 };

    private static PartNumberInfo? GSkillDdr4(string pn)
    {
        var m = GSkill4Rx().Match(pn);
        if (!m.Success) return null;
        var series = m.Groups["series"].Value;
        var family = series switch
        {
            _ when series.StartsWith("TZN", StringComparison.Ordinal) => "Trident Z Neo",
            _ when series.StartsWith("TZR", StringComparison.Ordinal) => "Trident Z RGB",
            _ when series.StartsWith("TZ", StringComparison.Ordinal) => "Trident Z",
            _ when series.StartsWith('V') => "Ripjaws V",
            _ when series.StartsWith("RS", StringComparison.Ordinal) => "Ripjaws (SODIMM)",
            _ when series.StartsWith("IS", StringComparison.Ordinal) => "Aegis",
            _ when series.StartsWith("FX", StringComparison.Ordinal) => "Flare X",
            _ when series.StartsWith("SX", StringComparison.Ordinal) => "Sniper X",
            _ => null,
        };
        var (module, kit, total) = Kit(I(m, "total"), CfgCount(m.Groups["cfg"].Value[0]));
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "G.Skill", Family = family, Generation = Gen(m.Groups["gen"].Value[0]), SpeedMts = I(m, "spd"),
            CasLatency = I(m, "cl"), ModuleBytes = module, KitModules = kit, KitTotalBytes = total,
            FormFactor = series.StartsWith("RS", StringComparison.Ordinal) ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Profile = "XMP", Confidence = DecodeConfidence.High, Rule = "gskill-ddr4",
        };
    }

    [GeneratedRegex(@"\AF5-(?<spd>\d{4})[A-Z](?<cl>\d{2})(?<rcd>\d{2})[A-Z](?<size>\d+)GX(?<n>\d)-(?<series>[A-Z0-9]+)", RegexOptions.None, 200)]
    private static partial Regex GSkill5Rx();

    private static PartNumberInfo? GSkillDdr5(string pn)
    {
        var m = GSkill5Rx().Match(pn);
        if (!m.Success) return null;
        var series = m.Groups["series"].Value;
        var family = series switch
        {
            _ when series.StartsWith("TZ5N", StringComparison.Ordinal) => "Trident Z5 Neo",
            _ when series.StartsWith("TZ5R", StringComparison.Ordinal) => "Trident Z5 RGB",
            _ when series.StartsWith("TZ5", StringComparison.Ordinal) => "Trident Z5",
            _ when series.StartsWith("FX5", StringComparison.Ordinal) => "Flare X5",
            _ when series.StartsWith("RS5", StringComparison.Ordinal) => "Ripjaws S5",
            _ when series.StartsWith("RM5", StringComparison.Ordinal) => "Ripjaws M5",
            "RS" => "Ripjaws (SODIMM)",
            _ => null,
        };
        var expo = series.StartsWith("TZ5N", StringComparison.Ordinal) || series.StartsWith("FX5", StringComparison.Ordinal);
        var n = I(m, "n");
        var size = (ulong)I(m, "size") * GiB;
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "G.Skill", Family = family, Generation = "DDR5", SpeedMts = I(m, "spd"), CasLatency = I(m, "cl"),
            ModuleBytes = size, KitModules = n, KitTotalBytes = size * (ulong)n,
            FormFactor = series == "RS" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Profile = expo ? "EXPO" : "XMP", Confidence = DecodeConfidence.High, Rule = "gskill-ddr5",
            Notes = [string.Create(CultureInfo.InvariantCulture, $"tRCD {I(m, "rcd")} (segundo número de latência do código).")],
        };
    }

    // ---------------------------------------------------------------- Crucial / Micron

    [GeneratedRegex(@"\AC(?<pro>[TP])(?:(?<kit>\d)K)?(?<size>\d+)G4(?<ff>[SDRLW])F(?<org>[A-Z]{1,2}\d?)?(?<spd>\d{2,3})(?![0-9])", RegexOptions.None, 200)]
    private static partial Regex Crucial4Rx();

    private static PartNumberInfo? CrucialDdr4(string pn)
    {
        var m = Crucial4Rx().Match(pn.Split('.')[0]);
        if (!m.Success) return null;
        var speed = Speed3(m.Groups["spd"].Value);
        var org = m.Groups["org"].Value switch { "S8" => "1Rx8", "D8" => "2Rx8", "S6" => "1Rx16", "D4" => "2Rx4", _ => null };
        var ff = m.Groups["ff"].Value[0];
        var kitN = OptI(m, "kit") ?? 1;
        var size = (ulong)I(m, "size") * GiB;
        var cl = JedecCl("DDR4", speed);
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Crucial", Family = m.Groups["pro"].Value == "P" ? "Crucial Pro" : "Crucial Basics",
            Generation = "DDR4", SpeedMts = speed, CasLatency = cl, CasLatencyInferred = cl is not null,
            ModuleBytes = size, KitModules = kitN, KitTotalBytes = size * (ulong)kitN,
            FormFactor = ff == 'S' ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm, Organization = org, Profile = "JEDEC",
            Confidence = DecodeConfidence.High, Rule = "crucial-ddr4",
            Notes = ff is 'R' or 'L' ? ["Módulo registrado (servidor)."] : ff == 'W' ? ["Módulo com ECC."] : [],
        };
    }

    [GeneratedRegex(@"\AC(?<pro>[TP])(?:(?<kit>\d)K)?(?<size>\d+)G(?<spd>\d{2})C(?<cl>\d{2})(?<ff>[US])5", RegexOptions.None, 200)]
    private static partial Regex Crucial5Rx();

    private static PartNumberInfo? CrucialDdr5(string pn)
    {
        var m = Crucial5Rx().Match(pn.Split('.')[0]);
        if (!m.Success) return null;
        var kitN = OptI(m, "kit") ?? 1;
        var size = (ulong)I(m, "size") * GiB;
        var pro = m.Groups["pro"].Value == "P";
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Crucial", Family = pro ? "Crucial Pro" : "Crucial", Generation = "DDR5",
            SpeedMts = Speed2(I(m, "spd")), CasLatency = I(m, "cl"), ModuleBytes = size, KitModules = kitN, KitTotalBytes = size * (ulong)kitN,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Profile = pro ? "XMP/EXPO" : "JEDEC", Confidence = DecodeConfidence.High, Rule = "crucial-ddr5",
        };
    }

    [GeneratedRegex(@"\ABL(?<sub>[MST])?(?:(?<kit>\d)K)?(?<size>\d+)G(?<spd>\d{2})C(?<cl>\d{2})(?<ff>[US])4", RegexOptions.None, 200)]
    private static partial Regex BallistixRx();

    private static PartNumberInfo? Ballistix(string pn)
    {
        var m = BallistixRx().Match(pn);
        if (!m.Success) return null;
        var kitN = OptI(m, "kit") ?? 1;
        var size = (ulong)I(m, "size") * GiB;
        var family = m.Groups["sub"].Value switch { "M" => "Ballistix Max", "S" => "Ballistix Sport", "T" => "Ballistix Tactical", _ => "Ballistix" };
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Crucial", Family = family, Generation = "DDR4", SpeedMts = Speed2(I(m, "spd")), CasLatency = I(m, "cl"),
            ModuleBytes = size, KitModules = kitN, KitTotalBytes = size * (ulong)kitN,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Profile = "XMP", Confidence = DecodeConfidence.High, Rule = "crucial-ballistix",
        };
    }

    [GeneratedRegex(@"\AMTA(?<chips>\d{1,2})A[A-Z]F(?<depth>512|1G|2G|4G|8G)(?<width>64|72)(?<ff>[A-Z]{1,3})-(?<spd>\dG\d)", RegexOptions.None, 200)]
    private static partial Regex Micron4Rx();

    private static PartNumberInfo? MicronDdr4(string pn)
    {
        var m = Micron4Rx().Match(pn);
        if (!m.Success) return null;
        (int Speed, int Cl)? s = m.Groups["spd"].Value switch
        {
            "2G1" => (2133, 15), "2G3" or "2G4" => (2400, 17), "2G6" => (2666, 19), "2G9" => (2933, 21), "3G2" => (3200, 22), _ => null,
        };
        var depth = m.Groups["depth"].Value;
        var size = depth == "512" ? 4 * GiB : (ulong)(depth[0] - '0') * 8 * GiB;
        var chips = I(m, "chips");
        var ecc = m.Groups["width"].Value == "72";
        var org = ecc ? null : chips switch { 4 => "1Rx16", 8 => "1Rx8", 16 => "2Rx8", _ => null };
        var ff = m.Groups["ff"].Value switch
        {
            "HZ" or "HZG" => MemoryFormFactor.SoDimm,
            "AZ" or "AZG" => MemoryFormFactor.Dimm,
            "PZ" or "PDZ" => MemoryFormFactor.Dimm,
            _ => MemoryFormFactor.Unknown,
        };
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Micron", Generation = "DDR4", SpeedMts = s?.Speed, CasLatency = s?.Cl, CasLatencyInferred = s is not null,
            ModuleBytes = size, KitModules = 1, KitTotalBytes = size, FormFactor = ff, Organization = org, Profile = "JEDEC",
            Confidence = s is null ? DecodeConfidence.Medium : DecodeConfidence.High, Rule = "micron-ddr4",
            Notes = ecc ? ["Módulo com ECC (72 bits)."] : [],
        };
    }

    [GeneratedRegex(@"\AMTC(?<chips>\d{1,2})[A-Z]\d{4,5}S1(?<ff>[SU])C(?<spd>\d{2})", RegexOptions.None, 200)]
    private static partial Regex Micron5Rx();

    private static PartNumberInfo? MicronDdr5(string pn)
    {
        var m = Micron5Rx().Match(pn);
        if (!m.Success) return null;
        var speed = Speed2(I(m, "spd"));
        var cl = JedecCl("DDR5", speed);
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Micron", Generation = "DDR5", SpeedMts = speed, CasLatency = cl, CasLatencyInferred = cl is not null,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm, Profile = "JEDEC",
            Confidence = DecodeConfidence.Medium, Rule = "micron-ddr5",
            Notes = ["Tamanho não decodificado do número DDR5 da Micron (vem do SMBIOS)."],
        };
    }

    // ---------------------------------------------------------------- Samsung

    [GeneratedRegex(@"\AM(?<type>378|471|393|386|474|391|323|425|321)(?<gen>[ABR])(?<depth>5[12]|[1-8][A-Z])(?<org>[0-9A-Z]{2})[A-Z0-9]{3}-(?<suffix>[A-Z0-9]{3})", RegexOptions.None, 200)]
    private static partial Regex SamsungRx();

    private static PartNumberInfo? Samsung(string pn)
    {
        var m = SamsungRx().Match(pn);
        if (!m.Success) return null;
        var gen = m.Groups["gen"].Value switch { "A" => "DDR4", "B" => "DDR3", _ => "DDR5" };
        var suffix = m.Groups["suffix"].Value;
        (int Speed, int Cl)? s = gen switch
        {
            "DDR4" => suffix[1] switch { 'P' => (2133, 15), 'R' => (2400, 17), 'T' => (2666, 19), 'V' => (2933, 21), 'W' => (3200, 22), _ => null },
            "DDR5" => suffix[1] switch { 'Q' => (4800, 40), 'W' => (5600, 46), _ => null },
            _ => suffix[1..] switch { "K0" => (1600, 11), "H9" => (1333, 9), _ => null },
        };
        var depth = m.Groups["depth"].Value;
        var size = depth[0] == '5' ? 4 * GiB : (ulong)(depth[0] - '0') * 8 * GiB;
        var type = m.Groups["type"].Value;
        var ff = type is "471" or "474" or "425" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm;
        var org = (depth, m.Groups["org"].Value) switch
        {
            ("1K", "43") => "1Rx8", ("2K", "43") => "2Rx8", ("52", "44") => "1Rx16", ("1G", "44") => "1Rx16", ("2G", "43") => "1Rx8", ("4G", "43") => "2Rx8",
            _ => null,
        };
        var notes = new List<string>();
        if (type is "393" or "321") notes.Add("Módulo registrado (RDIMM): servidor.");
        if (type is "474" or "391") notes.Add("Módulo com ECC.");
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Samsung", Generation = gen, SpeedMts = s?.Speed, CasLatency = s?.Cl, CasLatencyInferred = s is not null,
            ModuleBytes = size, KitModules = 1, KitTotalBytes = size, FormFactor = ff, Organization = org, Profile = "JEDEC",
            Confidence = s is null ? DecodeConfidence.Medium : DecodeConfidence.High, Rule = "samsung", Notes = notes,
        };
    }

    // ---------------------------------------------------------------- SK hynix

    [GeneratedRegex(@"\AHMA(?<die>[48A])(?<depth>1G|2G|4G|51|52)(?<ff>[SURE])(?<w>[67])[A-Z]{2}R(?<org>[468])N-(?<spd>[A-Z]{2})", RegexOptions.None, 200)]
    private static partial Regex Hynix4Rx();

    private static PartNumberInfo? HynixDdr4(string pn)
    {
        var m = Hynix4Rx().Match(pn);
        if (!m.Success) return null;
        (int Speed, int Cl)? s = m.Groups["spd"].Value switch
        {
            "TF" => (2133, 15), "UH" => (2400, 17), "VK" => (2666, 19), "WM" => (2933, 21), "XN" => (3200, 22), _ => null,
        };
        var depth = m.Groups["depth"].Value;
        var sizeGb = depth[0] == '5' ? 4 : (depth[0] - '0') * 8;
        var dieGbit = m.Groups["die"].Value switch { "4" => 4, "8" => 8, _ => 16 };
        var width = m.Groups["org"].Value switch { "4" => 4, "8" => 8, _ => 16 };
        var ranks = sizeGb * 8 / (dieGbit * (64 / width));
        var ff = m.Groups["ff"].Value[0];
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "SK hynix", Generation = "DDR4", SpeedMts = s?.Speed, CasLatency = s?.Cl, CasLatencyInferred = s is not null,
            ModuleBytes = (ulong)sizeGb * GiB, KitModules = 1, KitTotalBytes = (ulong)sizeGb * GiB,
            FormFactor = ff == 'S' ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Organization = ranks is >= 1 and <= 4 ? string.Create(CultureInfo.InvariantCulture, $"{ranks}Rx{width}") : null,
            Profile = "JEDEC", Confidence = s is null ? DecodeConfidence.Medium : DecodeConfidence.High, Rule = "hynix-ddr4",
            Notes = ff is 'R' ? ["Módulo registrado (servidor)."] : ff is 'E' ? ["Módulo com ECC."] : [],
        };
    }

    private static PartNumberInfo? HynixOther(string pn)
    {
        var gen = pn.StartsWith("HMC", StringComparison.Ordinal) ? "DDR5" : pn.StartsWith("HMT", StringComparison.Ordinal) ? "DDR3" : null;
        if (gen is null) return null;
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "SK hynix", Generation = gen, Confidence = DecodeConfidence.Low, Rule = "hynix-geração",
            Notes = ["Só a geração foi reconhecida; velocidade e tamanho vêm do SMBIOS."],
        };
    }

    // ---------------------------------------------------------------- ADATA / XPG

    private static readonly int[] CommonSizes = [4, 8, 16, 24, 32, 48, 64];

    /// <summary>ADATA cola um dígito de organização antes do tamanho ("38G" = org. 3, 8 GB; "716G" = org. 7, 16 GB).</summary>
    private static int? AdataSize(string digits)
    {
        var whole = int.Parse(digits, CultureInfo.InvariantCulture);
        if (CommonSizes.Contains(whole)) return whole;
        if (digits.Length >= 2 && int.Parse(digits[1..], CultureInfo.InvariantCulture) is var rest && CommonSizes.Contains(rest)) return rest;
        return null;
    }

    [GeneratedRegex(@"\AAX5U(?<spd>\d{4})C(?<cl>\d{2})(?<size>\d{1,2})G-(?<kit>[SDQ])", RegexOptions.None, 200)]
    private static partial Regex Xpg5Rx();

    private static PartNumberInfo? AdataXpgDdr5(string pn)
    {
        var m = Xpg5Rx().Match(pn);
        if (!m.Success) return null;
        var n = m.Groups["kit"].Value switch { "D" => 2, "Q" => 4, _ => 1 };
        var size = (ulong)I(m, "size") * GiB;
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "ADATA", Family = "XPG", Generation = "DDR5", SpeedMts = I(m, "spd"), CasLatency = I(m, "cl"),
            ModuleBytes = size, KitModules = n, KitTotalBytes = size * (ulong)n, FormFactor = MemoryFormFactor.Dimm, Profile = "XMP/EXPO",
            Confidence = DecodeConfidence.Medium, Rule = "adata-xpg-ddr5",
        };
    }

    [GeneratedRegex(@"\AA(?<xpg>[DX])(?<gen>[345])(?<ff>[SU])(?<spd>\d{4})(?<sz>\d{1,3})G(?<cl>\d{2})?-(?<kit>[SDQB])?", RegexOptions.None, 200)]
    private static partial Regex AdataRx();

    private static PartNumberInfo? AdataXpg(string pn) => AdataCore(pn, xpg: true);
    private static PartNumberInfo? Adata(string pn) => AdataCore(pn, xpg: false);

    private static PartNumberInfo? AdataCore(string pn, bool xpg)
    {
        var m = AdataRx().Match(pn);
        if (!m.Success || (m.Groups["xpg"].Value == "X") != xpg) return null;
        var sizeGb = AdataSize(m.Groups["sz"].Value);
        var n = m.Groups["kit"].Value switch { "D" => 2, "Q" => 4, _ => 1 };
        var gen = Gen(m.Groups["gen"].Value[0]);
        var speed = I(m, "spd");
        var cl = OptI(m, "cl");
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "ADATA", Family = xpg ? "XPG" : "Premier", Generation = gen, SpeedMts = speed,
            CasLatency = cl ?? JedecCl(gen, speed), CasLatencyInferred = cl is null && JedecCl(gen, speed) is not null,
            ModuleBytes = sizeGb is { } g ? (ulong)g * GiB : null, KitModules = n, KitTotalBytes = sizeGb is { } t ? (ulong)t * GiB * (ulong)n : null,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Profile = xpg ? "XMP" : "JEDEC", Confidence = DecodeConfidence.Medium, Rule = xpg ? "adata-xpg" : "adata",
        };
    }

    // ---------------------------------------------------------------- TeamGroup

    [GeneratedRegex(@"\A(?<pre>TF\d|FF\d|TED|TPD|TLZ[A-Z]?|TDZ[A-Z]?|TT[A-Z]?)(?<gen>D[345])(?<size>\d{1,3})G(?<spd>\d{4})H?C(?<cl>\d{2})(?<tail>[A-Z0-9-]*)\z", RegexOptions.None, 200)]
    private static partial Regex TeamRx();

    private static PartNumberInfo? TeamGroup(string pn)
    {
        var m = TeamRx().Match(pn);
        if (!m.Success) return null;
        var pre = m.Groups["pre"].Value;
        var tail = m.Groups["tail"].Value;
        var kit = tail.Contains("DC", StringComparison.Ordinal) ? 2 : tail.Contains("QC", StringComparison.Ordinal) ? 4 : 1;
        var total = I(m, "size");
        var (module, kitN, kitTotal) = Kit(total, kit);
        var elite = pre is "TED" or "TPD";
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "TeamGroup", Family = elite ? "Elite" : "T-Force", Generation = "DDR" + m.Groups["gen"].Value[1],
            SpeedMts = I(m, "spd"), CasLatency = I(m, "cl"), ModuleBytes = module, KitModules = kitN, KitTotalBytes = kitTotal,
            FormFactor = tail.StartsWith("-S", StringComparison.Ordinal) ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm,
            Profile = elite ? "JEDEC" : "XMP", Confidence = DecodeConfidence.Medium, Rule = "teamgroup",
        };
    }

    // ---------------------------------------------------------------- Ramaxel (comum em notebooks Acer/Lenovo)

    [GeneratedRegex(@"\ARM(?<ff>[SU])(?<gen>[AB])\d{4}[A-Z0-9]+-(?<spd>\d{4})\z", RegexOptions.None, 200)]
    private static partial Regex RamaxelRx();

    private static PartNumberInfo? Ramaxel(string pn)
    {
        var m = RamaxelRx().Match(pn);
        if (!m.Success) return null;
        var gen = m.Groups["gen"].Value == "A" ? "DDR4" : "DDR5";
        var speed = I(m, "spd");
        var cl = JedecCl(gen, speed);
        return new PartNumberInfo
        {
            PartNumber = pn, Vendor = "Ramaxel", Generation = gen, SpeedMts = speed, CasLatency = cl, CasLatencyInferred = cl is not null,
            FormFactor = m.Groups["ff"].Value == "S" ? MemoryFormFactor.SoDimm : MemoryFormFactor.Dimm, Profile = "JEDEC",
            Confidence = DecodeConfidence.Medium, Rule = "ramaxel",
            Notes = ["Módulo OEM (vem de fábrica): a velocidade está no fim do número; o tamanho vem do SMBIOS."],
        };
    }
}
