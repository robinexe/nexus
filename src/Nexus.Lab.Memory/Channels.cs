using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Lab.Memory;

public enum ChannelMode { Unknown, SingleChannel, DualChannel, QuadChannel, MultiChannel }

/// <summary>Uma posição com o canal deduzido do nome (locator/bank). <see cref="Channel"/> null = o nome não diz.</summary>
public sealed record SlotChannel(MemoryDevice Device, string? Channel, string? Slot);

/// <summary>Configuração deduzida da população dos slots.</summary>
/// <param name="Mode">Modo pelos slots ocupados.</param>
/// <param name="PopulatedChannels">Canais com memória.</param>
/// <param name="KnownChannels">Canais que existem (ocupados ou com slot vazio).</param>
/// <param name="BusWidthBits">Largura total do barramento em uso (soma da largura de dados de um módulo por canal ocupado).</param>
/// <param name="FromNames">true = canais lidos dos nomes dos slots; false = estimativa sem nome de canal.</param>
public sealed record ChannelLayout(
    ChannelMode Mode,
    IReadOnlyList<string> PopulatedChannels,
    IReadOnlyList<string> KnownChannels,
    IReadOnlyList<SlotChannel> Slots,
    int BusWidthBits,
    bool FromNames,
    bool Asymmetric,
    string Explanation)
{
    public int ChannelCount => PopulatedChannels.Count;
    /// <summary>Canais existentes sem nenhum módulo (onde um módulo novo ativaria dual channel).</summary>
    public IReadOnlyList<string> EmptyChannels => KnownChannels.Except(PopulatedChannels).ToList();
}

/// <summary>
/// Deduz canal de cada slot pelos nomes que o firmware grava (DeviceLocator/BankLocator), cobrindo os formatos usados por
/// Acer/AMD ("DIMM 0" + "P0 CHANNEL A"), Intel/Lenovo ("ChannelA-DIMM0" + "BANK 0"), ASUS/MSI ("DIMM_A1"), Gigabyte ("DDR4_A1"),
/// ASRock ("A1"), Dell ("DIMM A"), LPDDR soldada ("Controller0-ChannelA"). O resultado é confirmado pela medição de banda.
/// </summary>
public static partial class ChannelInference
{
    [GeneratedRegex(@"CHANNEL\s*[-_]?\s*([A-H])(?![A-Z])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"(?:DIMM|DDR\d?|SODIMM|SLOT)[\s_-]*([A-H])\s*[-_]?\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex DimmLetterNumber();

    [GeneratedRegex(@"\ADIMM\s+([A-H])\z", RegexOptions.IgnoreCase)]
    private static partial Regex DellLetter();

    [GeneratedRegex(@"\A([A-H])[\s_-]?(\d)\z", RegexOptions.IgnoreCase)]
    private static partial Regex BareLetterNumber();

    [GeneratedRegex(@"(?:\bP|CONTROLLER|CPU|NODE|SOCKET)\s*(\d)", RegexOptions.IgnoreCase)]
    private static partial Regex ControllerNumber();

    [GeneratedRegex(@"(?:DIMM|SLOT)\s*[_-]?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SlotNumber();

    /// <summary>Canal deduzido de um par (locator, bank). Ex.: ("DIMM 0", "P0 CHANNEL A") → "A"; ("Controller1-ChannelB", …) → "1B".</summary>
    public static string? ChannelOf(string? locator, string? bank)
    {
        string? letter = null;
        foreach (var text in new[] { bank, locator })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = ChannelWord().Match(text);
            if (!m.Success) m = DimmLetterNumber().Match(text);
            if (!m.Success) m = DellLetter().Match(text.Trim());
            if (!m.Success) m = BareLetterNumber().Match(text.Trim());
            if (m.Success) { letter = m.Groups[1].Value.ToUpperInvariant(); break; }
        }
        if (letter is null) return null;
        // Controlador/soquete: só quando há mais de um (P1, Controller1…). "P0"/"Controller0" não muda o nome do canal.
        var ctrlText = $"{bank} {locator}";
        var c = ControllerNumber().Match(ctrlText);
        return c.Success && c.Groups[1].Value != "0" ? c.Groups[1].Value + letter : letter;
    }

    public static ChannelLayout Infer(IReadOnlyList<MemoryDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var slots = devices.Select(d => new SlotChannel(d, ChannelOf(d.Locator, d.BankLocator), SlotName(d))).ToList();
        var populated = slots.Where(s => s.Device.Populated).ToList();
        if (populated.Count == 0)
            return new ChannelLayout(ChannelMode.Unknown, [], [], slots, 0, false, false, "O SMBIOS não listou nenhum módulo de memória.");

        var named = populated.All(s => s.Channel is not null);
        List<string> popChannels;
        List<string> known;
        if (named)
        {
            popChannels = populated.Select(s => s.Channel!).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
            known = slots.Where(s => s.Channel is not null).Select(s => s.Channel!).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        }
        else
        {
            // Sem nome de canal: um módulo = um canal; dois ou mais em PC comum = provavelmente dois canais (quase toda placa de
            // consumo tem dois). Fica marcado como estimativa e a medição de banda é que confirma.
            var n = Math.Min(populated.Count, 2);
            popChannels = Enumerable.Range(0, n).Select(i => ((char)('A' + i)).ToString()).ToList();
            known = popChannels.ToList();
            if (slots.Count > populated.Count && known.Count < 2) known.Add("B");
        }

        var width = 0;
        foreach (var ch in popChannels)
        {
            var inChannel = named ? populated.Where(s => s.Channel == ch).ToList() : [populated[popChannels.IndexOf(ch) % populated.Count]];
            width += inChannel.Select(s => s.Device.DataWidthBits ?? DefaultWidth(s.Device)).DefaultIfEmpty(64).Max();
        }

        // Largura típica de um canal "clássico" (DDR4/DDR5 DIMM) = 64 bits. LPDDR tem canais de 16/32 bits: o modo é dado pela
        // largura total equivalente, que é o que define a banda.
        var equivalent = (int)Math.Round(width / 64.0, MidpointRounding.AwayFromZero);
        var lp = populated.Any(s => s.Device.Type?.StartsWith("LP", StringComparison.Ordinal) == true);
        var mode = (lp ? equivalent : popChannels.Count) switch
        {
            <= 0 => ChannelMode.Unknown,
            1 => ChannelMode.SingleChannel,
            2 => ChannelMode.DualChannel,
            4 => ChannelMode.QuadChannel,
            _ => ChannelMode.MultiChannel,
        };

        var sizes = popChannels.Select(ch => named
            ? populated.Where(s => s.Channel == ch).Sum(s => (decimal)(s.Device.SizeBytes ?? 0))
            : (decimal)(populated[popChannels.IndexOf(ch) % populated.Count].Device.SizeBytes ?? 0)).ToList();
        var asymmetric = sizes.Count > 1 && sizes.Distinct().Count() > 1;

        string explanation;
        if (!named)
            explanation = populated.Count == 1
                ? "Um único módulo instalado: só um canal pode estar em uso (os nomes dos slots não informam o canal)."
                : "Os nomes dos slots não informam o canal; com mais de um módulo, a maioria das placas usa dois canais. A medição de banda confirma.";
        else
        {
            var empty = known.Except(popChannels).ToList();
            explanation = mode switch
            {
                ChannelMode.SingleChannel when empty.Count > 0 => $"Só o canal {popChannels[0]} tem memória; o canal {string.Join(", ", empty)} está vazio.",
                ChannelMode.SingleChannel => $"Só o canal {popChannels[0]} tem memória.",
                ChannelMode.DualChannel => $"Canais {string.Join(" e ", popChannels)} ocupados: dual channel.",
                _ => $"Canais ocupados: {string.Join(", ", popChannels)}.",
            };
            if (lp) explanation += string.Create(CultureInfo.InvariantCulture, $" Memória LPDDR: barramento total de {width} bits.");
        }
        if (asymmetric) explanation += " Os canais têm quantidades diferentes de memória (modo assimétrico/flex): só a parte igual nos dois canais trabalha em dual channel.";

        return new ChannelLayout(mode, popChannels, known, slots, width, named, asymmetric, explanation);
    }

    private static int DefaultWidth(MemoryDevice d) => d.Type switch
    {
        "LPDDR4" or "LPDDR5" => 32,
        _ => 64,
    };

    private static string? SlotName(MemoryDevice d)
    {
        var l = d.Locator;
        if (l is null) return null;
        var m = SlotNumber().Match(l);
        return m.Success ? m.Groups[1].Value : l;
    }
}
