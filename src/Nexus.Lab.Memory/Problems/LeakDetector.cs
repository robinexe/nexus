using System.Globalization;

namespace Nexus.Lab.Memory.Problems;

/// <summary>Uma leitura de uma métrica (bytes, contagem de handles, objetos GDI...) num instante.</summary>
public readonly record struct TrendSample(DateTimeOffset At, double Value);

/// <summary>
/// Limiares do detector de vazamento para uma métrica. Todos precisam ser atendidos ao mesmo tempo:
/// amostras e duração mínimas (uma tendência de 3 minutos não diz nada), crescimento absoluto E relativo (100 MB num
/// programa de 8 GB é ruído), e ajuste linear bom (R²) para separar "subindo sem parar" de picos e serras.
/// </summary>
public sealed record LeakThresholds(string Metric, int MinSamples, TimeSpan MinSpan, double MinGrowth, double MinRelativeGrowth, double MinR2)
{
    private const double Mb = 1024 * 1024;

    /// <summary>Memória comprometida (privada) de um processo: ≥ 150 MB e ≥ 20% em ≥ 15 min.</summary>
    public static readonly LeakThresholds PrivateBytes = new("memória privada", 6, TimeSpan.FromMinutes(15), 150 * Mb, 0.20, 0.70);

    /// <summary>Handles do kernel: ≥ 1.500 e ≥ 30%.</summary>
    public static readonly LeakThresholds Handles = new("handles", 6, TimeSpan.FromMinutes(15), 1500, 0.30, 0.70);

    /// <summary>Objetos GDI: ≥ 500 e ≥ 30% (o limite padrão por processo é 10.000).</summary>
    public static readonly LeakThresholds Gdi = new("objetos GDI", 6, TimeSpan.FromMinutes(15), 500, 0.30, 0.70);

    /// <summary>Objetos USER: ≥ 500 e ≥ 30% (limite padrão por processo: 10.000).</summary>
    public static readonly LeakThresholds User = new("objetos USER", 6, TimeSpan.FromMinutes(15), 500, 0.30, 0.70);

    /// <summary>Threads: ≥ 150 e ≥ 50%.</summary>
    public static readonly LeakThresholds Threads = new("threads", 6, TimeSpan.FromMinutes(15), 150, 0.50, 0.70);

    /// <summary>Pool não paginado do kernel (sempre em RAM): ≥ 150 MB e ≥ 30%.</summary>
    public static readonly LeakThresholds NonPagedPool = new("pool não paginado", 6, TimeSpan.FromMinutes(15), 150 * Mb, 0.30, 0.70);

    /// <summary>Pool paginado do kernel: ≥ 300 MB e ≥ 30%.</summary>
    public static readonly LeakThresholds PagedPool = new("pool paginado", 6, TimeSpan.FromMinutes(15), 300 * Mb, 0.30, 0.70);

    /// <summary>Uma tag do pool (bytes): ≥ 100 MB e ≥ 50%.</summary>
    public static readonly LeakThresholds PoolTag = new("tag do pool", 6, TimeSpan.FromMinutes(15), 100 * Mb, 0.50, 0.70);
}

public enum TrendKind
{
    /// <summary>Poucas amostras ou pouco tempo: ainda não dá para dizer.</summary>
    Insufficient,
    Stable,
    /// <summary>Sobe, mas pouco, ou de forma irregular (serra, picos): não é tratado como vazamento.</summary>
    Noisy,
    /// <summary>Sobe de forma contínua acima dos limiares: suspeita de vazamento.</summary>
    Leak,
}

/// <summary>Resultado do detector, com os números para a evidência mostrada ao usuário.</summary>
public sealed record TrendVerdict(
    TrendKind Kind,
    int Samples,
    TimeSpan Span,
    double SlopePerHour,
    double RobustSlopePerHour,
    double R2,
    double Growth,
    double Start,
    double Last,
    string Reason)
{
    public bool IsLeak => Kind == TrendKind.Leak;

    /// <summary>Crescimento pela reta ajustada (inclinação × duração): o número mostrado ao usuário ("cresceu 1,2 GB em 40 min").</summary>
    public double FittedGrowth => SlopePerHour * Span.TotalHours;
}

/// <summary>
/// Detector de vazamento por tendência. Regras (todas):
/// <list type="number">
/// <item>pelo menos <see cref="LeakThresholds.MinSamples"/> leituras cobrindo <see cref="LeakThresholds.MinSpan"/>;</item>
/// <item>inclinação positiva pela regressão linear (mínimos quadrados) E pela mediana das inclinações (Theil-Sen, que ignora
/// pontos fora da curva);</item>
/// <item>ajuste linear bom: R² ≥ limiar (uma serra de coleta de lixo ou um pico isolado têm R² baixo);</item>
/// <item>crescimento robusto = mediana do último terço − mediana do primeiro terço ≥ limiar absoluto e relativo (um único pico
/// não mexe nas medianas);</item>
/// <item>a última leitura continua alta (≥ início + metade do crescimento): uma subida que já voltou não é vazamento.</item>
/// </list>
/// </summary>
public static class LeakDetector
{
    public static TrendVerdict Evaluate(IReadOnlyList<TrendSample> samples, LeakThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(thresholds);
        var s = samples.Where(p => double.IsFinite(p.Value)).OrderBy(p => p.At).ToList();
        if (s.Count == 0) return new TrendVerdict(TrendKind.Insufficient, 0, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, "Sem leituras.");
        var span = s[^1].At - s[0].At;
        var third = Math.Max(1, s.Count / 3);
        var start = Median(s.Take(third).Select(p => p.Value));
        var end = Median(s.Skip(s.Count - third).Select(p => p.Value));
        var growth = end - start;
        var last = s[^1].Value;
        if (s.Count < thresholds.MinSamples || span < thresholds.MinSpan)
            return new TrendVerdict(TrendKind.Insufficient, s.Count, span, 0, 0, 0, growth, start, last,
                $"Ainda observando ({s.Count} leituras em {Minutes(span)}; precisa de {thresholds.MinSamples} em {Minutes(thresholds.MinSpan)}).");

        var t0 = s[0].At;
        var xs = s.Select(p => (p.At - t0).TotalHours).ToArray();
        var ys = s.Select(p => p.Value).ToArray();
        var (slope, r2) = Ols(xs, ys);
        var robust = TheilSen(xs, ys);
        var needed = Math.Max(thresholds.MinGrowth, Math.Abs(start) * thresholds.MinRelativeGrowth);

        if (slope <= 0 || robust <= 0 || growth <= 0)
            return new TrendVerdict(TrendKind.Stable, s.Count, span, slope, robust, r2, growth, start, last, "Sem tendência de alta.");
        if (growth < needed)
            return new TrendVerdict(TrendKind.Stable, s.Count, span, slope, robust, r2, growth, start, last, "Subiu pouco: dentro do normal.");
        if (r2 < thresholds.MinR2)
            return new TrendVerdict(TrendKind.Noisy, s.Count, span, slope, robust, r2, growth, start, last,
                "Sobe e desce (picos ou serra): uso variável, não um vazamento contínuo.");
        if (last < start + growth / 2)
            return new TrendVerdict(TrendKind.Noisy, s.Count, span, slope, robust, r2, growth, start, last, "Subiu, mas já voltou a cair.");
        return new TrendVerdict(TrendKind.Leak, s.Count, span, slope, robust, r2, growth, start, last,
            string.Create(Pt, $"Subiu de forma contínua por {Minutes(span)} (ajuste linear R² = {r2:0.00})."));
    }

    internal static (double Slope, double R2) Ols(double[] x, double[] y)
    {
        var n = x.Length;
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        if (sxx <= 0) return (0, 0);
        var slope = sxy / sxx;
        var r2 = syy <= 0 ? 0 : sxy * sxy / (sxx * syy);
        return (slope, r2);
    }

    /// <summary>Mediana das inclinações entre todos os pares (até ~400 pontos; acima disso, uma amostra regular dos pares).</summary>
    internal static double TheilSen(double[] x, double[] y)
    {
        var n = x.Length;
        var step = n > 400 ? n / 400 + 1 : 1;
        var slopes = new List<double>();
        for (var i = 0; i < n; i += step)
            for (var j = i + 1; j < n; j += step)
                if (x[j] > x[i]) slopes.Add((y[j] - y[i]) / (x[j] - x[i]));
        return slopes.Count == 0 ? 0 : Median(slopes);
    }

    internal static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(d => d).ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static string Minutes(TimeSpan t) =>
        t.TotalMinutes >= 90 ? string.Create(Pt, $"{t.TotalHours:0.#} h") : string.Create(Pt, $"{Math.Round(t.TotalMinutes):0} min");
}
