using System.Globalization;

namespace Nexus.Lab.Memory;

/// <summary>Veredito do número de canais a partir da banda medida.</summary>
public enum ChannelEvidence { NotMeasured, ConsistentWithSingle, ProvesMultiple, Inconclusive }

public sealed record ChannelCheck(ChannelEvidence Evidence, double PerChannelGbps, double MeasuredGbps, double TheoreticalGbps, double EfficiencyPercent, string Text);

/// <summary>
/// Conta da banda teórica e leitura da medição. Teórico = MT/s × largura do barramento em bytes (64 bits = 8 B por canal).
/// Ex.: DDR4-3200 em 1 canal = 3200 × 10⁶ × 8 B = 25,6 GB/s; em 2 canais = 51,2 GB/s. GB aqui é decimal (10⁹), como nos
/// anúncios de memória.
/// </summary>
public static class BandwidthTheory
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static double TheoreticalGbps(uint mts, int busWidthBits) => mts * 1e6 * (busWidthBits / 8.0) / 1e9;

    /// <summary>
    /// Um canal nunca entrega mais que o próprio teórico. Banda de leitura medida acima de ~108% do teórico de UM canal
    /// prova pelo menos dois canais; abaixo disso a medição é compatível com um canal (e, se o SMBIOS diz um módulo só,
    /// confirma). Abaixo de 45% de um canal a medição foi atrapalhada (outro programa, economia de energia) e não serve.
    /// </summary>
    public static ChannelCheck Check(double measuredReadGbps, uint configuredMts, int widthPerChannelBits, int busWidthBits)
    {
        var perChannel = TheoreticalGbps(configuredMts, widthPerChannelBits);
        var total = TheoreticalGbps(configuredMts, busWidthBits);
        if (measuredReadGbps <= 0 || perChannel <= 0)
            return new ChannelCheck(ChannelEvidence.NotMeasured, perChannel, 0, total, 0, "Ainda não medido.");
        var eff = total > 0 ? measuredReadGbps / total * 100 : 0;
        var ratio = measuredReadGbps / perChannel;
        var m = measuredReadGbps.ToString("0.0", Pt);
        var pc = perChannel.ToString("0.0", Pt);
        if (ratio > 1.08)
            return new ChannelCheck(ChannelEvidence.ProvesMultiple, perChannel, measuredReadGbps, total, eff,
                $"Seu PC mede {m} GB/s; um canal desta memória entrega no máximo {pc} GB/s → há pelo menos dois canais trabalhando.");
        if (ratio >= 0.45)
            return new ChannelCheck(ChannelEvidence.ConsistentWithSingle, perChannel, measuredReadGbps, total, eff,
                $"Seu PC mede {m} GB/s; um canal desta memória tem {pc} GB/s teóricos → compatível com um só canal.");
        return new ChannelCheck(ChannelEvidence.Inconclusive, perChannel, measuredReadGbps, total, eff,
            $"A medição deu só {m} GB/s (menos da metade de um canal, {pc} GB/s): algo atrapalhou. Feche programas pesados, ligue o carregador e meça de novo.");
    }

    /// <summary>Junta o que os slots dizem com o que a medição diz numa frase final.</summary>
    public static string Verdict(ChannelLayout layout, ChannelCheck check)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(check);
        return (layout.Mode, check.Evidence) switch
        {
            (_, ChannelEvidence.NotMeasured) => layout.Mode switch
            {
                ChannelMode.SingleChannel => "Single channel pelos slots. Meça a banda para confirmar.",
                ChannelMode.DualChannel => "Dual channel pelos slots. Meça a banda para confirmar.",
                _ => "Configuração de canais não identificada pelos slots. Meça a banda.",
            },
            (ChannelMode.SingleChannel, ChannelEvidence.ConsistentWithSingle) => "Single channel confirmado: os slots mostram um canal só e a banda medida não passa do limite de um canal.",
            (ChannelMode.SingleChannel, ChannelEvidence.ProvesMultiple) => "A banda medida passa do limite de um canal: o firmware nomeia mal os slots e há mais de um canal em uso.",
            (ChannelMode.DualChannel or ChannelMode.QuadChannel or ChannelMode.MultiChannel, ChannelEvidence.ProvesMultiple) => "Multicanal confirmado pela medição.",
            (ChannelMode.DualChannel or ChannelMode.QuadChannel or ChannelMode.MultiChannel, ChannelEvidence.ConsistentWithSingle) =>
                "Os slots indicam dois canais, mas a banda medida não passou do limite de um. Pode ser módulo mal encaixado, modo assimétrico ou economia de energia: meça de novo com o carregador ligado.",
            (_, ChannelEvidence.Inconclusive) => "Medição inconclusiva: repita com o PC parado e o carregador ligado.",
            (_, ChannelEvidence.ProvesMultiple) => "A medição prova pelo menos dois canais.",
            _ => "A medição é compatível com um canal só.",
        };
    }
}
