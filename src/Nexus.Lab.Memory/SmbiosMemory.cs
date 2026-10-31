using System.Buffers.Binary;
using System.Globalization;
using Nexus.HardwareDiscovery;

namespace Nexus.Lab.Memory;

/// <summary>Forma física do módulo (SMBIOS tipo 17, campo Form Factor), simplificada para o que interessa ao usuário.</summary>
public enum MemoryFormFactor { Unknown, Dimm, SoDimm, Soldered, Camm, Other }

/// <summary>Banco de memória da placa (SMBIOS tipo 16, Physical Memory Array).</summary>
/// <param name="MaxCapacityBytes">Capacidade máxima suportada pela placa (null = não informada).</param>
/// <param name="DeviceCount">Número de posições (slots ou chips soldados) que o fabricante declarou.</param>
public sealed record MemoryArray(ushort Handle, string Location, string Use, string Ecc, ulong? MaxCapacityBytes, int? DeviceCount);

/// <summary>
/// Uma posição de memória (SMBIOS tipo 17, Memory Device): um slot ocupado, um slot vazio ou memória soldada.
/// Todos os campos opcionais são null quando o firmware não informa (nunca um valor presumido).
/// </summary>
public sealed record MemoryDevice
{
    public ushort Handle { get; init; }
    public ushort? ArrayHandle { get; init; }
    public string? Locator { get; init; }
    public string? BankLocator { get; init; }
    /// <summary>Tamanho em bytes; 0 = posição vazia; null = desconhecido.</summary>
    public ulong? SizeBytes { get; init; }
    public bool Populated => SizeBytes is not 0;
    public string? Type { get; init; }
    public byte? TypeCode { get; init; }
    public MemoryFormFactor FormFactor { get; init; }
    public string? FormFactorRaw { get; init; }
    public bool Registered { get; init; }
    public bool Unbuffered { get; init; }
    public uint? SpeedMts { get; init; }
    public uint? ConfiguredSpeedMts { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public string? PartNumber { get; init; }
    /// <summary>Número de ranks (1–15), null se não informado.</summary>
    public int? Rank { get; init; }
    public int? TotalWidthBits { get; init; }
    public int? DataWidthBits { get; init; }
    public int? MinVoltageMv { get; init; }
    public int? MaxVoltageMv { get; init; }
    public int? ConfiguredVoltageMv { get; init; }
    /// <summary>Código JEDEC do fabricante do módulo (SMBIOS 3.2+): byte baixo = nº de códigos de continuação (com paridade), alto = código.</summary>
    public ushort? ModuleManufacturerId { get; init; }
    /// <summary>Indícios de memória soldada na placa (forma "chip"/"linha de chips" ou nome do slot "onboard").</summary>
    public bool LooksSoldered { get; init; }
}

/// <summary>Inventário de memória lido do SMBIOS.</summary>
public sealed record MemoryInventory(IReadOnlyList<MemoryArray> Arrays, IReadOnlyList<MemoryDevice> Devices, string SmbiosVersion)
{
    public IReadOnlyList<MemoryDevice> Populated => Devices.Where(d => d.Populated).ToList();
    public IReadOnlyList<MemoryDevice> Empty => Devices.Where(d => !d.Populated).ToList();
    public ulong InstalledBytes => (ulong)Populated.Sum(d => (decimal)(d.SizeBytes ?? 0));

    /// <summary>Capacidade máxima da placa (soma das matrizes de memória do sistema), null se nenhuma informou.</summary>
    public ulong? MaxCapacityBytes
    {
        get
        {
            var sys = Arrays.Where(a => a.Use == "memória do sistema" && a.MaxCapacityBytes is not null).ToList();
            return sys.Count == 0 ? null : (ulong)sys.Sum(a => (decimal)a.MaxCapacityBytes!.Value);
        }
    }
}

/// <summary>
/// Decodificação própria dos tipos 16 e 17 do SMBIOS (DMTF DSP0134 3.x). Pura, testável em Linux com tabelas montadas.
/// A separação em estruturas usa <see cref="SmbiosParser.Parse"/> (Nexus.HardwareDiscovery), só por leitura.
/// </summary>
public static class SmbiosMemory
{
    public static MemoryInventory Parse(ReadOnlySpan<byte> rawSmbios) => FromTable(SmbiosParser.Parse(rawSmbios));

    public static MemoryInventory FromTable(SmbiosParser.Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var arrays = table.Structures.Where(s => s.Type == 16).Select(ParseArray).ToList();
        // Só memória do sistema: matrizes de vídeo/flash/cache (uso diferente de 0x03) ficam de fora dos slots.
        var byHandle = arrays.GroupBy(a => a.Handle).ToDictionary(g => g.Key, g => g.First());
        var devices = table.Structures.Where(s => s.Type == 17).Select(ParseDevice)
            .Where(d => d.ArrayHandle is not { } h || !byHandle.TryGetValue(h, out var a) || a.Use == "memória do sistema")
            .ToList();
        return new MemoryInventory(arrays, devices, string.Create(CultureInfo.InvariantCulture, $"{table.Major}.{table.Minor}"));
    }

    /// <summary>Notebook/portátil pelo tipo de gabinete (SMBIOS tipo 3). Null = não informado ou ambíguo.</summary>
    public static bool? IsPortable(SmbiosParser.Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var t = table.Structures.FirstOrDefault(s => s.Type == 3)?.Byte(0x05);
        return (t & 0x7F) switch
        {
            0x08 or 0x09 or 0x0A or 0x0B or 0x0E or 0x1E or 0x1F or 0x20 => true,
            0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x0D or 0x0F or 0x10 or 0x23 or 0x24 => false,
            _ => null,
        };
    }

    private static ulong? QWord(SmbiosStructure s, int offset) =>
        offset + 8 <= s.Formatted.Length ? BinaryPrimitives.ReadUInt64LittleEndian(s.Formatted.AsSpan(offset)) : null;

    internal static MemoryArray ParseArray(SmbiosStructure s)
    {
        ulong? max = null;
        if (s.DWord(0x07) is { } kb)
        {
            if (kb == 0x80000000) max = QWord(s, 0x0F) is { } ext and not 0 ? ext : null;
            else if (kb != 0) max = (ulong)kb * 1024;
        }
        var count = s.Word(0x0D);
        return new MemoryArray(s.Handle, ArrayLocation(s.Byte(0x04)), ArrayUse(s.Byte(0x05)), Ecc(s.Byte(0x06)), max, count is null or 0 ? null : count);
    }

    internal static MemoryDevice ParseDevice(SmbiosStructure s)
    {
        var sizeWord = s.Word(0x0C);
        ulong? size = sizeWord switch
        {
            null => null,
            0 => 0,
            0xFFFF => null,
            0x7FFF => s.DWord(0x1C) is { } ext ? (ulong)(ext & 0x7FFFFFFF) * 1024 * 1024 : null,
            _ when (sizeWord & 0x8000) != 0 => (ulong)(sizeWord & 0x7FFF) * 1024,
            _ => (ulong)sizeWord.Value * 1024 * 1024,
        };
        uint? speed = s.Word(0x15) is { } sp and not 0 ? (sp == 0xFFFF ? NonZero(s.DWord(0x54)) : sp) : null;
        uint? configured = s.Word(0x20) is { } cs and not 0 ? (cs == 0xFFFF ? NonZero(s.DWord(0x58)) : cs) : null;
        var detail = s.Word(0x13) ?? 0;
        var ff = s.Byte(0x0E);
        var locator = s.Str(0x10);
        var bank = s.Str(0x11);
        var attributes = s.Byte(0x1B);
        var form = FormFactor(ff);
        var soldered = form == MemoryFormFactor.Soldered || SolderedName(locator) || SolderedName(bank);
        if (soldered && form is MemoryFormFactor.Unknown or MemoryFormFactor.Other) form = MemoryFormFactor.Soldered;
        return new MemoryDevice
        {
            Handle = s.Handle,
            ArrayHandle = s.Word(0x04) is { } ah and not 0xFFFE and not 0xFFFF ? ah : null,
            Locator = locator,
            BankLocator = bank,
            SizeBytes = size,
            TypeCode = s.Byte(0x12),
            Type = MemoryType(s.Byte(0x12)),
            FormFactor = form,
            FormFactorRaw = FormFactorName(ff),
            Registered = (detail & (1 << 13)) != 0,
            Unbuffered = (detail & (1 << 14)) != 0,
            SpeedMts = speed,
            ConfiguredSpeedMts = configured,
            Manufacturer = Clean(s.Str(0x17)),
            SerialNumber = Clean(s.Str(0x18)),
            PartNumber = Clean(s.Str(0x1A)),
            Rank = attributes is { } at && (at & 0x0F) != 0 ? at & 0x0F : null,
            TotalWidthBits = Width(s.Word(0x08)),
            DataWidthBits = Width(s.Word(0x0A)),
            MinVoltageMv = NonZeroInt(s.Word(0x22)),
            MaxVoltageMv = NonZeroInt(s.Word(0x24)),
            ConfiguredVoltageMv = NonZeroInt(s.Word(0x26)),
            ModuleManufacturerId = s.Word(0x2C) is { } id and not 0 ? id : null,
            LooksSoldered = soldered,
        };
    }

    private static uint? NonZero(uint? v) => v is null or 0 ? null : v;
    private static int? NonZeroInt(ushort? v) => v is null or 0 ? null : v;
    private static int? Width(ushort? v) => v is null or 0 or 0xFFFF ? null : v;

    private static readonly string[] Placeholders =
        ["unknown", "not specified", "none", "to be filled by o.e.m.", "default string", "n/a", "na", "undefined", "0000", "00000000", "serial number", "part number", "module part number", "array1", "manufacturer"];

    /// <summary>Normaliza strings do firmware: espaços nas pontas, textos de preenchimento ("Unknown", "To be filled…") viram null.</summary>
    public static string? Clean(string? s)
    {
        if (s is null) return null;
        var t = s.Trim().Trim('\0').Trim();
        if (t.Length == 0) return null;
        if (Placeholders.Contains(t, StringComparer.OrdinalIgnoreCase)) return null;
        if (t.All(c => c == '0' || c == ' ') || t.All(c => c == 'F' || c == 'f')) return null;
        return t;
    }

    private static bool SolderedName(string? s) =>
        s is not null && (s.Contains("onboard", StringComparison.OrdinalIgnoreCase) || s.Contains("on board", StringComparison.OrdinalIgnoreCase) ||
                          s.Contains("soldered", StringComparison.OrdinalIgnoreCase) || s.Contains("memory down", StringComparison.OrdinalIgnoreCase));

    public static string? MemoryType(byte? code) => code switch
    {
        null or 0 or 0x02 => null,
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4", // o SMBIOS não distingue LPDDR4X de LPDDR4
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5", // idem para LPDDR5X
        0x24 => "HBM3",
        _ => string.Create(CultureInfo.InvariantCulture, $"código 0x{code:X2}"),
    };

    public static MemoryFormFactor FormFactor(byte? code) => code switch
    {
        0x09 or 0x0F => MemoryFormFactor.Dimm,        // DIMM, FB-DIMM
        0x0D or 0x0E => MemoryFormFactor.SoDimm,      // SODIMM, SRIMM
        0x05 or 0x0B or 0x10 => MemoryFormFactor.Soldered, // chip, linha de chips, die
        0x11 => MemoryFormFactor.Camm,
        null or 0x02 => MemoryFormFactor.Unknown,
        _ => MemoryFormFactor.Other,
    };

    private static string? FormFactorName(byte? code) => code switch
    {
        0x01 => "outro", 0x02 => "desconhecido", 0x03 => "SIMM", 0x04 => "SIP", 0x05 => "chip", 0x06 => "DIP", 0x07 => "ZIP",
        0x08 => "cartão proprietário", 0x09 => "DIMM", 0x0A => "TSOP", 0x0B => "linha de chips", 0x0C => "RIMM", 0x0D => "SODIMM",
        0x0E => "SRIMM", 0x0F => "FB-DIMM", 0x10 => "die", 0x11 => "CAMM",
        null => null,
        _ => string.Create(CultureInfo.InvariantCulture, $"código 0x{code:X2}"),
    };

    private static string ArrayLocation(byte? code) => code switch
    {
        0x03 => "placa-mãe",
        0x04 => "placa ISA",
        0x05 => "placa EISA",
        0x06 => "placa PCI",
        0x07 => "placa MCA",
        0x08 => "placa PCMCIA",
        0x09 => "placa proprietária",
        0x0A => "NuBus",
        _ => "desconhecido",
    };

    private static string ArrayUse(byte? code) => code switch
    {
        0x03 => "memória do sistema",
        0x04 => "memória de vídeo",
        0x05 => "memória flash",
        0x06 => "RAM não volátil",
        0x07 => "memória cache",
        _ => "outro",
    };

    private static string Ecc(byte? code) => code switch
    {
        0x03 => "sem ECC",
        0x04 => "paridade",
        0x05 => "ECC de bit único",
        0x06 => "ECC de múltiplos bits",
        0x07 => "CRC",
        _ => "desconhecido",
    };
}
