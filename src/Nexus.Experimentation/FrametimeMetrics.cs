namespace Nexus.Experimentation;

/// <summary>Critério de stutter declarado no relatório (seção 16).</summary>
public sealed record StutterCriterion(double RatioToRollingMedian = 2.0, double AbsoluteMinimumMs = 8.0, int WindowFrames = 30)
{
    public string Describe() =>
        $"frame com frametime > {RatioToRollingMedian:0.##} × mediana dos {WindowFrames} frames anteriores e > {AbsoluteMinimumMs:0.##} ms";
}

/// <summary>
/// Métricas de UMA execução. Fórmulas (exibidas no relatório):
/// <list type="bullet">
/// <item>FPS médio = 1000 × N / Σ frametime  (média temporal, não média dos FPS instantâneos)</item>
/// <item>P99 frametime = quantil 0,99 por posto mais próximo: ordenar crescente, posição ⌈0,99·N⌉</item>
/// <item>"FPS P1" = 1000 / P99 frametime</item>
/// <item>"1% low (média)" = 1000 / média dos ⌈0,01·N⌉ maiores frametimes  (equivale ao "1% low average" do CapFrameX)</item>
/// </list>
/// </summary>
public sealed record FrametimeSummary(
    int Frames,
    double DurationMs,
    double AverageFps,
    double MedianFrametimeMs,
    double P99FrametimeMs,
    double P999FrametimeMs,
    double FpsP1,
    double OnePercentLowAverageFps,
    double FrametimeStdDevMs,
    int StutterCount,
    double StuttersPerMinute,
    string StutterCriterion);

public static class FrametimeMetrics
{
    public static FrametimeSummary Summarize(IReadOnlyList<double> frametimesMs, StutterCriterion? criterion = null)
    {
        if (frametimesMs.Count < 100)
            throw new ArgumentException("Menos de 100 frames: amostra insuficiente para percentis de cauda.", nameof(frametimesMs));
        if (frametimesMs.Any(f => !(f > 0) || !double.IsFinite(f)))
            throw new ArgumentException("Frametimes devem ser finitos e positivos.", nameof(frametimesMs));

        criterion ??= new StutterCriterion();
        var n = frametimesMs.Count;
        var total = frametimesMs.Sum();
        var sorted = frametimesMs.Order().ToArray();
        var mean = total / n;
        var variance = frametimesMs.Sum(f => (f - mean) * (f - mean)) / (n - 1);

        var worstCount = (int)Math.Ceiling(0.01 * n);
        var worstMean = sorted.Skip(n - worstCount).Average();
        var p99 = NearestRank(sorted, 0.99);
        var stutters = CountStutters(frametimesMs, criterion);

        return new FrametimeSummary(
            Frames: n,
            DurationMs: total,
            AverageFps: 1000.0 * n / total,
            MedianFrametimeMs: NearestRank(sorted, 0.5),
            P99FrametimeMs: p99,
            P999FrametimeMs: NearestRank(sorted, 0.999),
            FpsP1: 1000.0 / p99,
            OnePercentLowAverageFps: 1000.0 / worstMean,
            FrametimeStdDevMs: Math.Sqrt(variance),
            StutterCount: stutters.Count,
            StuttersPerMinute: stutters.Count / (total / 60000.0),
            StutterCriterion: criterion.Describe());
    }

    public static double NearestRank(double[] sortedAscending, double p)
    {
        var rank = (int)Math.Ceiling(p * sortedAscending.Length);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Length) - 1];
    }

    /// <summary>Índices dos frames classificados como stutter. Os primeiros WindowFrames frames não são avaliados.</summary>
    public static IReadOnlyList<int> CountStutters(IReadOnlyList<double> ft, StutterCriterion c)
    {
        var result = new List<int>();
        var window = new double[c.WindowFrames];
        for (var i = c.WindowFrames; i < ft.Count; i++)
        {
            for (var k = 0; k < c.WindowFrames; k++) window[k] = ft[i - c.WindowFrames + k];
            Array.Sort(window);
            var median = window[c.WindowFrames / 2];
            if (ft[i] > c.RatioToRollingMedian * median && ft[i] > c.AbsoluteMinimumMs) result.Add(i);
        }
        return result;
    }
}
