using System.Globalization;
using Nexus.NetRoute.Stats;

namespace Nexus.NetRoute.Decision;

public enum TunnelChoice
{
    /// <summary>Faltou medição de um dos lados: fica na rota direta.</summary>
    Inconclusive,
    /// <summary>A rota direta é melhor ou igual: o túnel não liga.</summary>
    StayDirect,
    /// <summary>O relay ganha no ping (mediana) sem piorar picos nem perda.</summary>
    UseRelayLatency,
    /// <summary>O relay corta a perda de pacotes sem piorar o ping de forma relevante.</summary>
    UseRelayLoss,
}

/// <summary>Uma rodada A/B: a mesma janela de tempo, medindo a rota direta e o caminho pelo relay.</summary>
public sealed record AbRound(LatencyStats Direct, LatencyStats Relay);

public sealed record TunnelVerdict(TunnelChoice Choice, double? GainP50Ms, double? GainP95Ms, double LossDirectPercent, double LossRelayPercent,
    int Rounds, int RoundsFavoringRelay, string Text)
{
    public bool UseRelay => Choice is TunnelChoice.UseRelayLatency or TunnelChoice.UseRelayLoss;
}

/// <summary>
/// Decide se o túnel liga, a partir de rodadas alternadas (direto, relay, direto, relay...). Regras conservadoras: o relay
/// precisa ganhar em pelo menos 2/3 das rodadas e por uma margem que a variação normal não explica. Na dúvida, rota direta.
/// </summary>
public static class TunnelDecision
{
    /// <summary>Ganho mínimo na mediana para ligar o túnel por ping (abaixo disso a diferença some na variação da partida).</summary>
    public const double MinGainMs = 5.0;
    /// <summary>Quanto o p95 do relay pode piorar em relação ao direto e ainda valer (picos mais altos estragam o ganho médio).</summary>
    public const double MaxP95WorseningMs = 5.0;
    /// <summary>Perda direta a partir da qual o túnel pode ligar só pela perda.</summary>
    public const double LossTriggerPercent = 2.0;
    /// <summary>No caso da perda, quanto o ping pode piorar e ainda compensar.</summary>
    public const double MaxP50WorseningForLossMs = 3.0;
    /// <summary>Rodadas mínimas para decidir.</summary>
    public const int MinRounds = 3;

    public static TunnelVerdict Decide(IReadOnlyList<AbRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        var valid = rounds.Where(r => r.Direct.HasData && r.Relay.HasData).ToList();
        var lossDirect = rounds.Count == 0 ? 0 : rounds.Average(r => r.Direct.LossPercent);
        var lossRelay = rounds.Count == 0 ? 0 : rounds.Average(r => r.Relay.LossPercent);
        if (valid.Count < MinRounds)
            return new TunnelVerdict(TunnelChoice.Inconclusive, null, null, lossDirect, lossRelay, valid.Count, 0,
                $"Medição incompleta ({valid.Count} de {MinRounds} rodadas com resposta dos dois lados). Fica na rota direta.");

        var gains50 = valid.Select(r => r.Direct.P50Ms!.Value - r.Relay.P50Ms!.Value).Order().ToList();
        var gains95 = valid.Select(r => r.Direct.P95Ms!.Value - r.Relay.P95Ms!.Value).Order().ToList();
        var gain50 = Median(gains50);
        var gain95 = Median(gains95);
        var favoring = gains50.Count(g => g >= MinGainMs / 2);
        var majority = favoring * 3 >= valid.Count * 2;
        var c = CultureInfo.InvariantCulture;

        if (gain50 >= MinGainMs && majority && gain95 >= -MaxP95WorseningMs && lossRelay <= lossDirect + 0.5)
            return new TunnelVerdict(TunnelChoice.UseRelayLatency, gain50, gain95, lossDirect, lossRelay, valid.Count, favoring,
                string.Create(c, $"O relay ganhou em {favoring} de {valid.Count} rodadas: {gain50:0} ms a menos na mediana, sem piorar picos nem perda. O túnel liga."));

        if (lossDirect >= LossTriggerPercent && lossRelay <= lossDirect / 2 && gain50 >= -MaxP50WorseningForLossMs)
            return new TunnelVerdict(TunnelChoice.UseRelayLoss, gain50, gain95, lossDirect, lossRelay, valid.Count, favoring,
                string.Create(c, $"A rota direta perde {lossDirect:0.#}% dos pacotes e pelo relay a perda cai para {lossRelay:0.#}%, com ping praticamente igual. O túnel liga."));

        var why = gain50 < MinGainMs
            ? (gain50 <= 0 ? string.Create(c, $"pelo relay o ping fica {-gain50:0} ms maior") : string.Create(c, $"o ganho de {gain50:0} ms é pequeno demais para valer"))
            : !majority ? $"o relay só ganhou em {favoring} de {valid.Count} rodadas"
            : gain95 < -MaxP95WorseningMs ? string.Create(c, $"os picos pelo relay ficam {-gain95:0} ms piores")
            : "a perda pelo relay é maior";
        return new TunnelVerdict(TunnelChoice.StayDirect, gain50, gain95, lossDirect, lossRelay, valid.Count, favoring, $"A rota direta é melhor: {why}. O túnel não liga.");
    }

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
}

public enum HealthAction { Keep, FallBackToDirect }

/// <summary>
/// Vigia do túnel durante a partida: com histerese, para não ficar ligando e desligando. Volta à rota direta quando o túnel
/// perde pacotes ou fica bem mais lento que no teste, por várias janelas seguidas.
/// </summary>
public sealed class TunnelHealthMonitor(double baselineP50Ms)
{
    public const double LossLimitPercent = 5.0;
    public const double SlowdownLimitMs = 30.0;
    public const int BadWindowsToFallBack = 3;

    private int _badStreak;

    public double BaselineP50Ms { get; } = baselineP50Ms;
    public int BadStreak => _badStreak;

    /// <summary>Avalia uma janela (ex.: 10 s de sondas ao agente do relay pelo túnel).</summary>
    public HealthAction Observe(LatencyStats window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var bad = !window.HasData || window.LossPercent >= LossLimitPercent || window.P50Ms - BaselineP50Ms >= SlowdownLimitMs;
        _badStreak = bad ? _badStreak + 1 : 0;
        return _badStreak >= BadWindowsToFallBack ? HealthAction.FallBackToDirect : HealthAction.Keep;
    }
}
