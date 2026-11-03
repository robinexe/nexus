namespace Nexus.Measurement;

/// <summary>Resultado do teste do mouse ("mexa o mouse em círculos por 5 s").</summary>
public enum MousePollingVerdict { NotEnoughData, Stable, Irregular }

/// <param name="Intervals">Intervalos usados (com o mouse em movimento).</param>
/// <param name="RateHz">Taxa medida: 1000 / mediana dos intervalos.</param>
/// <param name="NominalHz">Taxa padrão mais próxima (125, 250, 500, 1000, 2000, 4000, 8000) quando a medida está a até 25% dela; senão 0.</param>
/// <param name="JitterMs">Desvio padrão dos intervalos (irregularidade), em ms.</param>
/// <param name="P99Ms">99% dos intervalos ficaram abaixo disto (os "soluços").</param>
public sealed record MousePollingResult(int Intervals, double RateHz, double NominalHz, double MedianMs, double JitterMs, double P99Ms, MousePollingVerdict Verdict)
{
    public static MousePollingResult Empty { get; } = new(0, double.NaN, 0, double.NaN, double.NaN, double.NaN, MousePollingVerdict.NotEnoughData);
    /// <summary>Irregularidade relativa (desvio / mediana).</summary>
    public double RelativeJitter => MedianMs > 0 ? JitterMs / MedianMs : double.NaN;
}

/// <summary>
/// Teste de taxa de atualização (polling) e irregularidade do mouse a partir dos instantes de chegada de cada relatório Raw Input
/// (WM_INPUT, https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input). Só diagnóstico: não muda nada no mouse nem no
/// Windows. Limite honesto: o instante é o de quando a janela recebeu a mensagem, então inclui o atraso do Windows em entregar;
/// acima de ~2000 Hz o Windows pode juntar relatórios e a taxa medida sai menor que a real.
/// </summary>
public static class MousePollingAnalyzer
{
    public static readonly double[] StandardRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    /// <summary>Intervalo maior que isto = o mouse parou (não entra na conta).</summary>
    public const double PauseMs = 40;
    public const int MinIntervals = 200;

    /// <param name="timestamps">Instantes de chegada (Stopwatch.GetTimestamp), em ordem.</param>
    /// <param name="frequency">Stopwatch.Frequency.</param>
    public static MousePollingResult Analyze(IReadOnlyList<long> timestamps, long frequency)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        if (frequency <= 0 || timestamps.Count < 2) return MousePollingResult.Empty;
        var intervals = new List<double>(timestamps.Count);
        for (var i = 1; i < timestamps.Count; i++)
        {
            var ms = (timestamps[i] - timestamps[i - 1]) * 1000.0 / frequency;
            if (ms > 0 && ms <= PauseMs) intervals.Add(ms);
        }
        if (intervals.Count < MinIntervals) return MousePollingResult.Empty with { Intervals = intervals.Count };
        intervals.Sort();
        var median = Percentile(intervals, 0.5);
        var p99 = Percentile(intervals, 0.99);
        var mean = intervals.Average();
        var jitter = Math.Sqrt(intervals.Sum(x => (x - mean) * (x - mean)) / intervals.Count);
        var rate = 1000.0 / median;
        var nominal = StandardRates.MinBy(r => Math.Abs(Math.Log(rate / r)));
        if (Math.Abs(rate - nominal) > nominal * 0.25) nominal = 0;
        var stable = p99 <= median * 2.5 && jitter <= median * 0.5;
        return new MousePollingResult(intervals.Count, rate, nominal, median, jitter, p99, stable ? MousePollingVerdict.Stable : MousePollingVerdict.Irregular);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var pos = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(sorted.Count - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
