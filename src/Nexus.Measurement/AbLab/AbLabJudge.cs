using Nexus.Experimentation;

namespace Nexus.Measurement.AbLab;

/// <summary>
/// Compara as partidas do teste com a estatística honesta que o Medir já usa (<see cref="AutoCompare.CompareSegments"/>:
/// trechos de 30 s pareados pela posição, IC de 95% por bootstrap, diferença mínima relevante por métrica) e decide.
/// <list type="bullet">
/// <item>A/B: o veredito de cada métrica é o da comparação A × B.</item>
/// <item>A/B/A: B é comparada com A1 e com A2; só conta melhora (ou piora) que aparece contra AS DUAS. Se A1 × A2 já dá
/// diferença "real" numa métrica, o jogo variou sozinho: essa métrica fica inconclusiva (ruído).</item>
/// <item>Decisão: manter só se pelo menos uma métrica decisiva (FPS médio, 1% low, frametime P99, engasgos) melhorou e nenhuma
/// piorou. Misto, sem diferença, inconclusivo, ruído ou dados insuficientes: não manter (não provou ganho).</item>
/// <item>Variação do frametime (desvio padrão): só indicativa (não há bootstrap por trecho para ela), não decide.</item>
/// </list>
/// </summary>
public static class AbLabJudge
{
    /// <summary>Métricas decisivas, na ordem de exibição.</summary>
    public static IReadOnlyList<string> DecisiveKeys { get; } = ["low1", "fps", "stutter", "p99"];

    public const string VarianceKey = "var";

    /// <summary>Diferença relativa mínima para a variação do frametime ser citada como melhor/pior (indicativo).</summary>
    public const double VarianceThreshold = 0.10;

    public static AbJudgement Judge(MeasuredSession baseline, MeasuredSession treatment, MeasuredSession? baseline2 = null, AbRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(treatment);
        rules ??= AbRules.Default;
        var first = AutoCompare.CompareSegments(baseline, treatment);
        var second = baseline2 is null ? null : AutoCompare.CompareSegments(baseline2, treatment);
        var drift = baseline2 is null ? null : AutoCompare.CompareSegments(baseline, baseline2);
        var bracketed = second is not null;

        var metrics = new List<AbMetric>();
        foreach (var key in DecisiveKeys)
        {
            var r1 = Row(first, key);
            if (r1 is null) continue;
            if (second is null)
            {
                metrics.Add(new AbMetric(key, r1.Before, r1.After, r1.DeltaPercent, first.Paired ? r1.Verdict : Verdict.InsufficientData,
                    r1.CiLowPercent, r1.CiHighPercent, false, true));
                continue;
            }
            var r2 = Row(second, key)!;
            var noisy = drift is { Paired: true } && Row(drift, key)?.Verdict is Verdict.Improved or Verdict.Regressed;
            var verdict = !first.Paired || !second.Paired ? Verdict.InsufficientData : noisy ? Verdict.Inconclusive : Combine(r1.Verdict, r2.Verdict);
            var before = (r1.Before + r2.Before) / 2;
            var after = (r1.After + r2.After) / 2;
            var delta = before != 0 ? 100.0 * (after - before) / before : double.NaN;
            metrics.Add(new AbMetric(key, before, after, delta, verdict, Min(r1.CiLowPercent, r2.CiLowPercent), Max(r1.CiHighPercent, r2.CiHighPercent), noisy, true));
        }
        metrics.Add(Variance(baseline, treatment, baseline2));

        var decisive = metrics.Where(m => m.Decisive).ToList();
        var improved = decisive.Where(m => m.Verdict == Verdict.Improved).ToList();
        var regressed = decisive.Where(m => m.Verdict == Verdict.Regressed).ToList();
        var reason =
            decisive.Count == 0 || decisive.All(m => m.Verdict == Verdict.InsufficientData) ? AbReason.Insufficient :
            improved.Count > 0 && regressed.Count == 0 ? AbReason.Improved :
            regressed.Count > 0 && improved.Count == 0 ? AbReason.Regressed :
            improved.Count > 0 ? AbReason.Mixed :
            decisive.All(m => m.Verdict == Verdict.NoMeaningfulChange) ? AbReason.NoChange :
            decisive.Any(m => m.Noisy) ? AbReason.Noisy :
            AbReason.Inconclusive;

        var highlight = reason switch
        {
            AbReason.Improved or AbReason.Mixed => improved.MaxBy(Goodness),
            AbReason.Regressed => regressed.MinBy(Goodness),
            _ => null,
        };

        var warnings = new List<AbWarning>();
        var sessions = new[] { baseline, treatment, baseline2 }.Where(s => s is not null).Select(s => s!).ToList();
        var longest = sessions.Max(s => s.DurationSeconds);
        var shortest = Math.Max(1, sessions.Min(s => s.DurationSeconds));
        if (longest / shortest >= rules.DurationRatioWarning) warnings.Add(AbWarning.DurationMismatch);
        var modeBefore = baseline.Conditions?.PresentMode;
        var modeAfter = treatment.Conditions?.PresentMode;
        var modeChanged = !string.IsNullOrWhiteSpace(modeBefore) && !string.IsNullOrWhiteSpace(modeAfter) && !modeBefore.Equals(modeAfter, StringComparison.OrdinalIgnoreCase);
        if (modeChanged) warnings.Add(AbWarning.PresentModeChanged);
        if (decisive.Any(m => m.Noisy)) warnings.Add(AbWarning.BaselineDrift);
        warnings.Add(AbWarning.SameScene);

        return new AbJudgement(reason == AbReason.Improved, reason, metrics, highlight?.Key, highlight?.DeltaPercent, warnings,
            Math.Min(first.BeforeCount, second?.BeforeCount ?? int.MaxValue), bracketed)
        {
            PresentModeBefore = modeChanged ? modeBefore : null,
            PresentModeAfter = modeChanged ? modeAfter : null,
        };
    }

    /// <summary>A/B/A: as duas comparações precisam concordar; qualquer desacordo vira inconclusivo.</summary>
    public static Verdict Combine(Verdict a, Verdict b)
    {
        if (a == b) return a;
        if (a == Verdict.InsufficientData || b == Verdict.InsufficientData) return Verdict.InsufficientData;
        return Verdict.Inconclusive;
    }

    /// <summary>"Quanto melhor" em %: positivo = melhor, para qualquer direção da métrica.</summary>
    public static double Goodness(AbMetric m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var lowerIsBetter = m.Key is "p99" or "stutter" or VarianceKey;
        var d = double.IsFinite(m.DeltaPercent) ? m.DeltaPercent : 0;
        return lowerIsBetter ? -d : d;
    }

    private static MetricComparison? Row(SessionComparisonResult r, string key) => r.Metrics.FirstOrDefault(m => m.Metric.Key == key);

    private static double? Min(double? a, double? b) => a is { } x && b is { } y ? Math.Min(x, y) : a ?? b;
    private static double? Max(double? a, double? b) => a is { } x && b is { } y ? Math.Max(x, y) : a ?? b;

    /// <summary>Desvio padrão do frametime (sessão inteira), comparado de forma descritiva.</summary>
    private static AbMetric Variance(MeasuredSession a, MeasuredSession b, MeasuredSession? a2)
    {
        var before = a2 is null ? a.Summary.FrametimeStdDevMs : (a.Summary.FrametimeStdDevMs + a2.Summary.FrametimeStdDevMs) / 2;
        var after = b.Summary.FrametimeStdDevMs;
        var delta = before > 0 ? 100.0 * (after - before) / before : double.NaN;
        var verdict = !double.IsFinite(delta) ? Verdict.InsufficientData
            : Math.Abs(delta) < VarianceThreshold * 100 ? Verdict.NoMeaningfulChange
            : delta < 0 ? Verdict.Improved : Verdict.Regressed;
        return new AbMetric(VarianceKey, before, after, delta, verdict, null, null, false, false);
    }
}
