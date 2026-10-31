using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Lab.Memory;

/// <summary>
/// Fabricantes de memória pelo código JEDEC JEP106 (banco = nº de códigos de continuação 0x7F + 1; código com bit de paridade).
/// Muitos firmwares gravam no SMBIOS o código em hexadecimal ("80CE", "0198", "859B") em vez do nome. Lista curta e conferida:
/// só os fabricantes de módulos/chips comuns em PCs; código fora da lista = "desconhecido" (nunca um palpite).
/// </summary>
public static partial class JedecVendors
{
    // (nº de continuações, código com paridade) → nome. Fonte: JEDEC JEP106 (Standard Manufacturer's Identification Code).
    private static readonly Dictionary<(int Continuations, byte Code), string> Table = new()
    {
        [(0, 0x2C)] = "Micron",
        [(0, 0xAD)] = "SK hynix",
        [(0, 0xCE)] = "Samsung",
        [(0, 0x98)] = "Toshiba",
        [(1, 0x98)] = "Kingston",
        [(1, 0x94)] = "Smart Modular",
        [(1, 0x4F)] = "Transcend",
        [(1, 0x7A)] = "Apacer",
        [(1, 0xBA)] = "PNY",
        [(2, 0x9E)] = "Corsair",
        [(2, 0xFE)] = "Elpida",
        [(3, 0x0B)] = "Nanya",
        [(4, 0xCB)] = "ADATA",
        [(4, 0xCD)] = "G.Skill",
        [(4, 0xEF)] = "TeamGroup",
        [(4, 0x43)] = "Ramaxel",
        [(5, 0x02)] = "Patriot",
        [(5, 0x9B)] = "Crucial",
    };

    private static readonly string[] KnownNames =
        ["Micron", "SK hynix", "Hynix", "Samsung", "Kingston", "Corsair", "G.Skill", "G Skill", "Crucial", "ADATA", "A-DATA", "TeamGroup", "Team Group",
         "Patriot", "Ramaxel", "Nanya", "Transcend", "Apacer", "PNY", "Elpida", "Smart Modular", "Kingmax", "Lexar", "Netac", "Mushkin", "Silicon Power", "CXMT", "Hikvision", "Asgard", "XPG"];

    /// <summary>Nome a partir do código do campo Module Manufacturer ID (SMBIOS 3.2+). Null se não conhecido.</summary>
    public static string? FromModuleId(ushort id)
    {
        var continuations = id & 0x7F; // byte baixo: nº de continuações (bit 7 = paridade)
        var code = (byte)(id >> 8);
        return Table.TryGetValue((continuations, code), out var n) ? n : null;
    }

    [GeneratedRegex(@"\A(?:0x)?([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})(?:[0-9A-Fa-f]{4}){0,3}\z")]
    private static partial Regex HexId();

    /// <summary>
    /// Resolve o nome do fabricante a partir do texto do SMBIOS (nome ou código hexadecimal) e/ou do ID JEDEC do módulo.
    /// Retorna (nome, como foi resolvido). Nome null = desconhecido.
    /// </summary>
    public static (string? Name, string How) Resolve(string? manufacturer, ushort? moduleId)
    {
        var text = SmbiosMemory.Clean(manufacturer);
        if (text is not null)
        {
            var m = HexId().Match(text.Replace(" ", "", StringComparison.Ordinal));
            if (m.Success)
            {
                var cont = byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0x7F;
                var code = byte.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (Table.TryGetValue((cont, code), out var byHex)) return (byHex, $"código JEDEC {text}");
            }
            else
            {
                var known = KnownNames.FirstOrDefault(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (known is not null) return (Normalize(known), "nome gravado no SMBIOS");
            }
        }
        if (moduleId is { } id && FromModuleId(id) is { } fromId)
            return (fromId, string.Create(CultureInfo.InvariantCulture, $"ID JEDEC do módulo 0x{id:X4}"));
        return text is null ? (null, "não informado") : (text, "texto do SMBIOS (não reconhecido)");
    }

    private static string Normalize(string n) => n switch
    {
        "Hynix" => "SK hynix",
        "G Skill" => "G.Skill",
        "A-DATA" or "XPG" => "ADATA",
        "Team Group" => "TeamGroup",
        _ => n,
    };
}
