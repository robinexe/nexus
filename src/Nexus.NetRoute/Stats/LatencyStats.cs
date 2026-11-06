using System.Globalization;

namespace Nexus.NetRoute.Stats;

/// <summary>
/// Resumo de uma série de sondas (RTT em ms; null = sonda perdida). O p99 importa mais que a média: é o "travou" da partida.
/// Jitter como no RFC 3550 (média do |diferença| entre sondas respondidas consecutivas).
/// </summary>
public sealed record LatencyStats(int Sent, int Received, double? MinMs, double? P50Ms, double? P95Ms, double? P99Ms, double? MeanMs, double? JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    public bool HasData => Received > 0;

    public static LatencyStats Empty { get; } = new(0, 0, null, null, null, null, null, null);

    public static LatencyStats From(IReadOnlyList<double?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var ok = samples.Where(s => s is { } v && double.IsFinite(v) && v >= 0).Select(s => s!.Value).ToList();
        if (ok.Count == 0) return new LatencyStats(samples.Count, 0, null, null, null, null, null, null);
        var sorted = ok.Order().ToList();
        double? jitter = null;
        if (ok.Count >= 2)
        {
            var sum = 0.0;
            for (var i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
            jitter = sum / (ok.Count - 1);
        }
        return new LatencyStats(samples.Count, ok.Count, sorted[0], Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99), ok.Average(), jitter);
    }

    /// <summary>Percentil pelo posto mais próximo (sem interpolar): o valor existe de fato na série.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double percent)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0) throw new ArgumentException("Série vazia.", nameof(sorted));
        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>Soma de dois trechos independentes (cliente→relay + relay→alvo): RTTs somam, perdas se compõem.</summary>
    public static LatencyStats Chain(LatencyStats first, LatencyStats second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (!first.HasData || !second.HasData) return new LatencyStats(Math.Max(first.Sent, second.Sent), 0, null, null, null, null, null, null);
        var keep = (1 - first.LossPercent / 100.0) * (1 - second.LossPercent / 100.0);
        var sent = Math.Max(first.Sent, second.Sent);
        static double? Add(double? a, double? b) => a is { } x && b is { } y ? x + y : null;
        double? jitter = first.JitterMs is { } j1 && second.JitterMs is { } j2 ? Math.Sqrt(j1 * j1 + j2 * j2) : first.JitterMs ?? second.JitterMs;
        return new LatencyStats(sent, (int)Math.Round(sent * keep), Add(first.MinMs, second.MinMs), Add(first.P50Ms, second.P50Ms),
            Add(first.P95Ms, second.P95Ms), Add(first.P99Ms, second.P99Ms), Add(first.MeanMs, second.MeanMs), jitter);
    }

    public string Describe()
    {
        if (!HasData) return Sent == 0 ? "sem medição" : $"sem resposta ({Sent} sondas)";
        var c = CultureInfo.InvariantCulture;
        return string.Create(c, $"mediana {P50Ms:0.#} ms · p95 {P95Ms:0.#} ms · p99 {P99Ms:0.#} ms · jitter {JitterMs ?? 0:0.#} ms · perda {LossPercent:0.#}%");
    }
}
