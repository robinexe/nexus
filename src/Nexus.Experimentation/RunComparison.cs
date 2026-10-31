namespace Nexus.Experimentation;

public enum Direction { HigherIsBetter, LowerIsBetter }

public enum Verdict
{
    /// <summary>Menos de 3 pares válidos: não há base para conclusão.</summary>
    InsufficientData,
    Improved,
    Regressed,
    /// <summary>IC inteiro dentro da faixa ± melhoria mínima relevante: equivalente na prática.</summary>
    NoMeaningfulChange,
    /// <summary>IC cruza a melhoria mínima relevante: a diferença não se distingue do ruído.</summary>
    Inconclusive,
}

public sealed record ComparisonResult(
    string Metric,
    int Pairs,
    double BaselineMean,
    double CandidateMean,
    double MeanDifference,
    double RelativeDifferencePercent,
    double CiLow,
    double CiHigh,
    double ConfidenceLevel,
    double BaselineRunToRunStdDev,
    double MinimumRelevantEffect,
    double? EffectSizeDz,
    Verdict Verdict,
    string Method);

/// <summary>
/// Compara execuções, não frames: a unidade experimental é a EXECUÇÃO, porque frames da mesma
/// sessão são autocorrelacionados e não são observações independentes.
/// Desenho esperado: pares A/B intercalados em ordem alternada ou aleatória (ABBA...), cada par = (baseline_i, candidato_i).
/// IC: bootstrap percentil da média das diferenças pareadas, semente fixa para reprodutibilidade.
/// </summary>
public static class RunComparison
{
    public static ComparisonResult ComparePaired(
        string metric,
        IReadOnlyList<double> baselineRuns,
        IReadOnlyList<double> candidateRuns,
        Direction direction,
        double minimumRelevantEffect,
        double confidence = 0.95,
        int resamples = 20000,
        int seed = 12345)
    {
        if (baselineRuns.Count != candidateRuns.Count)
            throw new ArgumentException("Comparação pareada exige o mesmo número de execuções em cada braço.");
        ArgumentOutOfRangeException.ThrowIfNegative(minimumRelevantEffect);

        var n = baselineRuns.Count;
        var diffs = candidateRuns.Zip(baselineRuns, (c, b) => c - b).ToArray();
        var bMean = n > 0 ? baselineRuns.Average() : double.NaN;
        var cMean = n > 0 ? candidateRuns.Average() : double.NaN;
        var dMean = n > 0 ? diffs.Average() : double.NaN;
        var bSd = StdDev(baselineRuns);
        var dSd = StdDev(diffs);
        const string method = "Diferença pareada por execução; IC bootstrap percentil (reamostragem dos pares).";

        if (n < 3)
            return new(metric, n, bMean, cMean, dMean, Rel(dMean, bMean), double.NaN, double.NaN, confidence, bSd,
                minimumRelevantEffect, null, Verdict.InsufficientData, method);

        var rng = new Random(seed);
        var means = new double[resamples];
        for (var r = 0; r < resamples; r++)
        {
            double s = 0;
            for (var i = 0; i < n; i++) s += diffs[rng.Next(n)];
            means[r] = s / n;
        }
        Array.Sort(means);
        var alpha = (1 - confidence) / 2;
        var lo = means[(int)Math.Floor(alpha * (resamples - 1))];
        var hi = means[(int)Math.Ceiling((1 - alpha) * (resamples - 1))];

        // Converte para "positivo = melhor" antes de decidir.
        var (gLo, gHi) = direction == Direction.HigherIsBetter ? (lo, hi) : (-hi, -lo);
        var m = minimumRelevantEffect;
        var verdict =
            gLo > m ? Verdict.Improved :
            gHi < -m ? Verdict.Regressed :
            gLo >= -m && gHi <= m ? Verdict.NoMeaningfulChange :
            Verdict.Inconclusive;

        return new(metric, n, bMean, cMean, dMean, Rel(dMean, bMean), lo, hi, confidence, bSd, m,
            dSd > 0 ? dMean / dSd : null, verdict, method);
    }

    private static double Rel(double diff, double baseline) => baseline != 0 ? 100.0 * diff / baseline : double.NaN;

    public static double StdDev(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return double.NaN;
        var mean = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1));
    }
}

/// <summary>Motivos para invalidar uma execução antes de entrar na comparação (seção 7 da extensão).</summary>
public sealed record RunConditions(
    string ProcessName,
    string Resolution,
    string ScenarioId,
    double? GpuTempStartC,
    long LostEtwEvents,
    bool CrashDetected,
    bool BackgroundUpdateActive,
    bool UserInterrupted);

public static class RunValidator
{
    public static IReadOnlyList<string> Invalidations(RunConditions reference, RunConditions run, double maxTempDeltaC = 5, long maxLostEvents = 0)
    {
        var reasons = new List<string>();
        if (!run.ProcessName.Equals(reference.ProcessName, StringComparison.OrdinalIgnoreCase)) reasons.Add($"Processo capturado diferente ({run.ProcessName}).");
        if (run.Resolution != reference.Resolution) reasons.Add($"Resolução mudou ({reference.Resolution} → {run.Resolution}).");
        if (run.ScenarioId != reference.ScenarioId) reasons.Add("Cenário diferente.");
        if (run.CrashDetected) reasons.Add("Crash do jogo durante a execução.");
        if (run.BackgroundUpdateActive) reasons.Add("Atualização em segundo plano ativa.");
        if (run.UserInterrupted) reasons.Add("Intervenção do usuário.");
        if (run.LostEtwEvents > maxLostEvents) reasons.Add($"Perda de eventos ETW ({run.LostEtwEvents}).");
        if (reference.GpuTempStartC is { } a && run.GpuTempStartC is { } b && Math.Abs(a - b) > maxTempDeltaC)
            reasons.Add($"Temperatura inicial fora da faixa comparável ({a:0}°C vs {b:0}°C).");
        return reasons;
    }
}
