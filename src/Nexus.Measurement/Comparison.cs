using System.Globalization;
using Nexus.Experimentation;

namespace Nexus.Measurement;

/// <summary>Uma métrica comparável, com explicação em linguagem simples.</summary>
public sealed record MetricDefinition(string Key, string Name, string Unit, Direction Direction, string Explanation, double RelativeThreshold, double AbsoluteThreshold,
    Func<FrametimeSummary, double> Read)
{
    /// <summary>Menor diferença que conta como real: o maior entre o limiar relativo (sobre o "antes") e o absoluto.</summary>
    public double MinimumRelevant(double baseline) => Math.Max(Math.Abs(baseline) * RelativeThreshold, AbsoluteThreshold);
}

public sealed record MetricComparison(
    MetricDefinition Metric,
    double Before,
    double After,
    double DeltaPercent,
    bool? Better,
    Verdict Verdict,
    double? CiLowPercent,
    double? CiHighPercent,
    string Sentence);

public sealed record SessionComparisonResult(
    int BeforeCount,
    int AfterCount,
    bool Paired,
    string Headline,
    string Method,
    IReadOnlyList<MetricComparison> Metrics);

/// <summary>
/// Compara sessões "antes" e "depois". A unidade é a SESSÃO (frames da mesma partida não são independentes).
/// Com 3 ou mais sessões de cada lado, em igual número, usa a comparação pareada com IC bootstrap
/// (<see cref="RunComparison"/>), pareando na ordem em que foram gravadas. Com menos, mostra só a diferença e diz
/// claramente que ela não prova nada.
/// </summary>
public static class SessionComparison
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<MetricDefinition> Metrics { get; } =
    [
        new("fps", "FPS médio", "FPS", Direction.HigherIsBetter,
            "Quantos quadros por segundo, em média, durante a sessão inteira.", 0.03, 0.5, s => s.AverageFps),
        new("low1", "1% low", "FPS", Direction.HigherIsBetter,
            "O FPS dos piores momentos: a média do 1% de quadros mais lentos. É o que você sente como travadinha.", 0.05, 0.5, s => s.OnePercentLowAverageFps),
        new("p99", "Frametime P99", "ms", Direction.LowerIsBetter,
            "99% dos quadros saíram mais rápido que isto. Quanto menor, mais estável.", 0.05, 0.1, s => s.P99FrametimeMs),
        new("stutter", "Engasgos por minuto", "/min", Direction.LowerIsBetter,
            "Quadros que demoraram mais que o dobro dos vizinhos (e mais de 8 ms). Cada um é um tranco visível.", 0.0, 1.0, s => s.StuttersPerMinute),
    ];

    public static SessionComparisonResult Compare(IReadOnlyList<MeasuredSession> before, IReadOnlyList<MeasuredSession> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count == 0 || after.Count == 0)
            return new(before.Count, after.Count, false, "Marque pelo menos uma sessão como \"antes\" e uma como \"depois\".", "", []);

        var b = before.OrderBy(s => s.CapturedAt).ToList();
        var a = after.OrderBy(s => s.CapturedAt).ToList();
        var paired = b.Count == a.Count && b.Count >= 3;
        var rows = new List<MetricComparison>();
        foreach (var m in Metrics)
        {
            var bv = b.Select(s => m.Read(s.Summary)).ToList();
            var av = a.Select(s => m.Read(s.Summary)).ToList();
            var bMean = bv.Average();
            var aMean = av.Average();
            var delta = bMean != 0 ? 100.0 * (aMean - bMean) / bMean : double.NaN;
            var threshold = m.MinimumRelevant(bMean);
            if (paired)
            {
                var r = RunComparison.ComparePaired(m.Name, bv, av, m.Direction, threshold);
                var lo = bMean != 0 ? 100.0 * r.CiLow / bMean : (double?)null;
                var hi = bMean != 0 ? 100.0 * r.CiHigh / bMean : (double?)null;
                bool? better = r.Verdict switch { Verdict.Improved => true, Verdict.Regressed => false, _ => null };
                rows.Add(new(m, bMean, aMean, delta, better, r.Verdict, lo, hi, PairedSentence(m, r.Verdict, delta, lo, hi, b.Count)));
            }
            else
            {
                var diff = aMean - bMean;
                var good = m.Direction == Direction.HigherIsBetter ? diff : -diff;
                bool? better = Math.Abs(diff) < threshold ? null : good > 0;
                rows.Add(new(m, bMean, aMean, delta, better, Verdict.InsufficientData, null, null, DescriptiveSentence(m, delta, better)));
            }
        }

        var headline = paired ? PairedHeadline(rows, b.Count) : DescriptiveHeadline(b.Count, a.Count);
        var method = paired
            ? $"{b.Count} pares (antes/depois na ordem em que foram gravados). Diferença pareada por sessão; intervalo de confiança de 95% por bootstrap. " +
              "Uma métrica só conta como melhora ou piora se o intervalo inteiro passar da diferença mínima relevante."
            : "Comparação descritiva: médias de cada lado, sem teste estatístico. Para um veredito, grave pelo menos 3 sessões de cada lado, em igual número, alternando antes e depois.";
        return new(b.Count, a.Count, paired, headline, method, rows);
    }

    private static string Pct(double v) => v.ToString("+0.0;-0.0;0.0", Pt) + "%";

    private static string PairedSentence(MetricDefinition m, Verdict v, double delta, double? lo, double? hi, int pairs)
    {
        var ci = lo is { } l && hi is { } h ? $" (intervalo de 95%: {Pct(l)} a {Pct(h)})" : "";
        return v switch
        {
            Verdict.Improved => $"{m.Name} melhorou {Pct(delta)}{ci}.",
            Verdict.Regressed => $"{m.Name} piorou {Pct(delta)}{ci}.",
            Verdict.NoMeaningfulChange => $"{m.Name}: sem mudança que importe{ci}.",
            Verdict.Inconclusive => $"{m.Name}: {Pct(delta)}, mas dentro da variação normal entre sessões{ci}. Mais pares ajudariam.",
            _ => $"{m.Name}: dados insuficientes ({pairs} pares).",
        };
    }

    private static string DescriptiveSentence(MetricDefinition m, double delta, bool? better) => better switch
    {
        true => $"{m.Name}: {Pct(delta)}, a favor do depois. Ainda não dá para dizer se é real.",
        false => $"{m.Name}: {Pct(delta)}, a favor do antes. Ainda não dá para dizer se é real.",
        _ => $"{m.Name}: {Pct(delta)}, diferença pequena.",
    };

    private static string PairedHeadline(IReadOnlyList<MetricComparison> rows, int pairs)
    {
        var improved = rows.Where(r => r.Verdict == Verdict.Improved).Select(r => r.Metric.Name).ToList();
        var regressed = rows.Where(r => r.Verdict == Verdict.Regressed).Select(r => r.Metric.Name).ToList();
        if (improved.Count > 0 && regressed.Count == 0) return $"Melhora comprovada em {pairs} pares: {string.Join(", ", improved)}.";
        if (regressed.Count > 0 && improved.Count == 0) return $"Piora comprovada em {pairs} pares: {string.Join(", ", regressed)}. Considere desfazer a mudança.";
        if (improved.Count > 0) return $"Resultado misto em {pairs} pares: melhorou {string.Join(", ", improved)}; piorou {string.Join(", ", regressed)}.";
        if (rows.All(r => r.Verdict == Verdict.NoMeaningfulChange)) return $"Nenhuma diferença que importe em {pairs} pares. A mudança não fez efeito mensurável.";
        return $"Inconclusivo em {pairs} pares: as diferenças cabem na variação normal entre sessões.";
    }

    private static string DescriptiveHeadline(int b, int a) =>
        $"{b} sessão(ões) antes e {a} depois: dá para ver a diferença, mas não para saber se ela é real. " +
        "Partidas variam sozinhas; grave 3 de cada lado, alternando, para um veredito.";
}
