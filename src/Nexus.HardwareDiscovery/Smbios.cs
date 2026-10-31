using System.Buffers.Binary;
using System.Text;

namespace Nexus.HardwareDiscovery;

public sealed record SmbiosStructure(byte Type, byte Length, ushort Handle, byte[] Formatted, IReadOnlyList<string> Strings)
{
    public byte? Byte(int offset) => offset < Formatted.Length ? Formatted[offset] : null;
    public ushort? Word(int offset) => offset + 2 <= Formatted.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Formatted.AsSpan(offset)) : null;
    public uint? DWord(int offset) => offset + 4 <= Formatted.Length ? BinaryPrimitives.ReadUInt32LittleEndian(Formatted.AsSpan(offset)) : null;

    /// <summary>String referenciada pelo byte em <paramref name="offset"/> (índice 1-based; 0 = sem string).</summary>
    public string? Str(int offset)
    {
        var idx = Byte(offset);
        if (idx is null or 0 || idx > Strings.Count) return null;
        var s = Strings[idx.Value - 1].Trim();
        return s.Length == 0 ? null : s;
    }
}

/// <summary>
/// Decodificador da tabela SMBIOS bruta devolvida por GetSystemFirmwareTable('RSMB'). Puro: testável sem Windows.
/// Formato: cabeçalho RawSMBIOSData de 8 bytes (método, versão maior/menor, revisão DMI, tamanho) seguido das estruturas.
/// </summary>
public static class SmbiosParser
{
    public sealed record Table(byte Major, byte Minor, IReadOnlyList<SmbiosStructure> Structures);

    public static Table Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) throw new InvalidDataException("Tabela SMBIOS menor que o cabeçalho.");
        var major = raw[1];
        var minor = raw[2];
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]), (uint)(raw.Length - 8));
        var data = raw.Slice(8, length);
        var list = new List<SmbiosStructure>();
        var pos = 0;
        while (pos + 4 <= data.Length)
        {
            var type = data[pos];
            var len = data[pos + 1];
            if (len < 4 || pos + len > data.Length) break; // estrutura corrompida: para sem inventar
            var handle = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            var formatted = data.Slice(pos, len).ToArray();

            // Conjunto de strings: termina em dois zeros seguidos.
            var strings = new List<string>();
            var s = pos + len;
            if (s + 1 < data.Length && data[s] == 0 && data[s + 1] == 0)
            {
                s += 2;
            }
            else
            {
                while (s < data.Length)
                {
                    var end = data[s..].IndexOf((byte)0);
                    if (end < 0) { s = data.Length; break; }
                    if (end == 0) { s++; break; }
                    strings.Add(Encoding.ASCII.GetString(data.Slice(s, end)));
                    s += end + 1;
                }
            }

            list.Add(new SmbiosStructure(type, len, handle, formatted, strings));
            if (type == 127) break; // fim da tabela
            pos = s;
        }
        return new Table(major, minor, list);
    }

    /// <summary>Tipo 17 (Memory Device). Módulos com tamanho 0 (slot vazio) são omitidos.</summary>
    public static IReadOnlyList<MemoryModule> MemoryModules(Table t)
    {
        var result = new List<MemoryModule>();
        foreach (var m in t.Structures.Where(s => s.Type == 17))
        {
            var size = m.Word(0x0C);
            if (size is null or 0) continue;
            ulong? bytes = size switch
            {
                0xFFFF => null, // desconhecido
                0x7FFF => m.DWord(0x1C) is { } ext ? (ulong)(ext & 0x7FFFFFFF) * 1024 * 1024 : null,
                _ when (size & 0x8000) != 0 => (ulong)(size & 0x7FFF) * 1024,
                _ => (ulong)size.Value * 1024 * 1024,
            };
            uint? speed = m.Word(0x15) is { } sp and not 0 ? (sp == 0xFFFF ? m.DWord(0x54) : sp) : null;
            uint? configured = m.Word(0x20) is { } cs and not 0 ? (cs == 0xFFFF ? m.DWord(0x58) : cs) : null;
            result.Add(new MemoryModule(m.Str(0x10), bytes, MemoryType(m.Byte(0x12)), speed, configured, m.Str(0x17), m.Str(0x1A)));
        }
        return result;
    }

    public static string? MemoryType(byte? code) => code switch
    {
        null => null,
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => $"código 0x{code:X2}",
    };

    /// <summary>(fabricante, produto) do tipo 1 (System Information).</summary>
    public static (string? Manufacturer, string? Product) SystemInfo(Table t)
    {
        var s = t.Structures.FirstOrDefault(x => x.Type == 1);
        return (s?.Str(0x04), s?.Str(0x05));
    }

    /// <summary>(fabricante, produto) do tipo 2 (Baseboard).</summary>
    public static (string? Manufacturer, string? Product) Baseboard(Table t)
    {
        var s = t.Structures.FirstOrDefault(x => x.Type == 2);
        return (s?.Str(0x04), s?.Str(0x05));
    }

    /// <summary>(fabricante, versão, data) do tipo 0 (BIOS).</summary>
    public static (string? Vendor, string? Version, string? Date) Bios(Table t)
    {
        var s = t.Structures.FirstOrDefault(x => x.Type == 0);
        return (s?.Str(0x04), s?.Str(0x05), s?.Str(0x08));
    }

    private static readonly string[] VmMarkers = ["VMware", "VirtualBox", "QEMU", "KVM", "Virtual Machine", "HVM domU", "Parallels", "Bochs"];

    /// <summary>Inferência: true se fabricante/produto do sistema contêm marcadores conhecidos de hipervisor.</summary>
    public static bool? LooksVirtual(string? manufacturer, string? product)
    {
        if (manufacturer is null && product is null) return null;
        var text = $"{manufacturer} {product}";
        if (string.Equals(manufacturer?.Trim(), "Xen", StringComparison.OrdinalIgnoreCase)) return true;
        return VmMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
