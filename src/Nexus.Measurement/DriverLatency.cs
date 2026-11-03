namespace Nexus.Measurement;

/// <summary>
/// Leitura de um núcleo num intervalo, dos contadores documentados "Processor Information": % DPC Time, % Interrupt Time,
/// DPCs Queued/sec e Interrupts/sec. Mostram quanto do tempo do núcleo foi gasto por drivers (DPC = trabalho adiado do driver;
/// interrupção = ISR), NÃO qual driver.
/// </summary>
public readonly record struct CoreDpcReading(string Core, double DpcPercent, double InterruptPercent, double DpcsPerSec, double InterruptsPerSec)
{
    public double DriverPercent => DpcPercent + InterruptPercent;
}

/// <summary>Uma amostra (padrão: 1 s) com todos os núcleos; <paramref name="Seconds"/> desde o começo do teste.</summary>
public sealed record DpcSample(double Seconds, IReadOnlyList<CoreDpcReading> Cores);

/// <summary>Veredito do teste "Atraso de drivers".</summary>
public enum DriverLatencyLevel
{
    /// <summary>Drivers usaram pouco tempo do processador em todo o teste.</summary>
    Good,
    /// <summary>Picos num núcleo que podem causar engasgos (áudio picotando, travadinhas curtas).</summary>
    MayStutter,
    /// <summary>Drivers tomaram uma parte grande de um núcleo: costuma causar engasgos e estalos de áudio.</summary>
    Bad,
    /// <summary>Sem leitura (contadores indisponíveis ou teste interrompido cedo demais).</summary>
    Unknown,
}

/// <summary>Resumo de um núcleo: pior segundo e média.</summary>
public sealed record CoreDpcSummary(string Core, double PeakPercent, double AveragePercent, double? WorstAverageDpcMicroseconds, double? WorstAverageIsrMicroseconds);

/// <summary>Resultado do teste. É uma APROXIMAÇÃO de nível de usuário (ver <see cref="DriverLatencyAnalyzer"/>).</summary>
public sealed record DriverLatencyResult(
    DriverLatencyLevel Level,
    int Samples,
    double DurationSeconds,
    double PeakPercent,
    string? PeakCore,
    double PeakAtSeconds,
    double AverageDpcPercent,
    double AverageInterruptPercent,
    double? WorstAverageDpcMicroseconds,
    double? WorstAverageIsrMicroseconds,
    int SecondsAboveWarn,
    IReadOnlyList<CoreDpcSummary> Cores)
{
    public static DriverLatencyResult Empty { get; } = new(DriverLatencyLevel.Unknown, 0, 0, 0, null, 0, 0, 0, null, null, 0, []);
}

/// <summary>
/// "Atraso de drivers" (estilo LatencyMon, mas aproximado e sem administrador): lê por 60–180 s os contadores documentados de
/// DPC/interrupção por núcleo e dá um veredito. Limites do NEXUS (heurística declarada, não norma): pior segundo de um núcleo
/// com DPC + interrupção ≥ <see cref="BadPercent"/>% (≥ 200 ms daquele segundo foram de drivers) ou média ≥ <see cref="BadAveragePercent"/>%
/// = ruim; ≥ <see cref="WarnPercent"/>% em algum segundo, ou média ≥ <see cref="WarnAveragePercent"/>% = pode causar engasgos; abaixo = bom.
/// Duração média de cada DPC/ISR ≈ (% do tempo × 1 s) ÷ quantidade por segundo — é média, não o pior caso.
/// O que NÃO dá para ver sem rastreamento do kernel (administrador): QUAL driver (ndis.sys, nvlddmkm.sys…) e a pior chamada
/// isolada (um único atraso de 2 ms num segundo some na média).
/// </summary>
public static class DriverLatencyAnalyzer
{
    public const double WarnPercent = 5;
    public const double BadPercent = 20;
    public const double WarnAveragePercent = 1;
    public const double BadAveragePercent = 3;
    /// <summary>Menos chamadas que isto por segundo num núcleo: a média por chamada fica ruidosa e não entra.</summary>
    public const double MinCallsForAverage = 20;

    public static DriverLatencyResult Analyze(IReadOnlyList<DpcSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var valid = samples.Where(s => s.Cores.Count > 0).ToList();
        if (valid.Count == 0) return DriverLatencyResult.Empty;

        var byCore = valid.SelectMany(s => s.Cores.Select(c => (s.Seconds, c))).GroupBy(x => x.c.Core, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var peak = g.Max(x => x.c.DriverPercent);
                var avg = g.Average(x => x.c.DriverPercent);
                double? dpcUs = g.Where(x => x.c.DpcsPerSec >= MinCallsForAverage).Select(x => x.c.DpcPercent / 100 * 1e6 / x.c.DpcsPerSec).DefaultIfEmpty(double.NaN).Max();
                double? isrUs = g.Where(x => x.c.InterruptsPerSec >= MinCallsForAverage).Select(x => x.c.InterruptPercent / 100 * 1e6 / x.c.InterruptsPerSec).DefaultIfEmpty(double.NaN).Max();
                return new CoreDpcSummary(g.Key, Math.Round(peak, 2), Math.Round(avg, 2), Finite(dpcUs), Finite(isrUs));
            })
            .OrderByDescending(c => c.PeakPercent).ToList();

        var worst = valid.SelectMany(s => s.Cores.Select(c => (s.Seconds, c))).MaxBy(x => x.c.DriverPercent);
        var avgDpc = valid.Average(s => s.Cores.Average(c => c.DpcPercent));
        var avgIsr = valid.Average(s => s.Cores.Average(c => c.InterruptPercent));
        var above = valid.Count(s => s.Cores.Any(c => c.DriverPercent >= WarnPercent));
        var peakPct = worst.c.DriverPercent;
        var avgTotal = avgDpc + avgIsr;
        var level = peakPct >= BadPercent || avgTotal >= BadAveragePercent ? DriverLatencyLevel.Bad
            : peakPct >= WarnPercent || avgTotal >= WarnAveragePercent ? DriverLatencyLevel.MayStutter
            : DriverLatencyLevel.Good;
        var duration = valid.Count > 1 ? valid[^1].Seconds - valid[0].Seconds + (valid[1].Seconds - valid[0].Seconds) : valid[0].Seconds;
        return new DriverLatencyResult(level, valid.Count, Math.Round(duration, 1), Math.Round(peakPct, 2), worst.c.Core, worst.Seconds,
            Math.Round(avgDpc, 3), Math.Round(avgIsr, 3),
            byCore.Select(c => c.WorstAverageDpcMicroseconds).Where(v => v is not null).Max(),
            byCore.Select(c => c.WorstAverageIsrMicroseconds).Where(v => v is not null).Max(),
            above, byCore);
    }

    private static double? Finite(double? v) => v is { } d && double.IsFinite(d) ? Math.Round(d, 1) : null;
}

/// <summary>Fonte dos contadores (PDH no Windows; falsa nos testes). Null = leitura sem valor neste intervalo (a primeira coleta é a base).</summary>
public interface IDpcCounterSource : IDisposable
{
    /// <summary>Contadores que o Windows não ofereceu (texto curto em português), vazio quando está tudo certo.</summary>
    IReadOnlyList<string> Unavailable { get; }
    IReadOnlyList<CoreDpcReading>? Read();
}

/// <summary>
/// Executa o teste: uma leitura por <paramref name="interval"/> (padrão 1 s) durante <paramref name="duration"/> (60–180 s).
/// Só lê contadores; não muda a resolução do timer nem nada do sistema. Cancelar devolve o que foi lido até ali.
/// </summary>
public static class DriverLatencyTest
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(180);

    public static async Task<(DriverLatencyResult Result, IReadOnlyList<DpcSample> Samples, bool Cancelled)> RunAsync(
        IDpcCounterSource source, TimeSpan duration, IProgress<double>? progress = null,
        TimeSpan? interval = null, Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (duration < MinDuration || duration > MaxDuration) throw new ArgumentOutOfRangeException(nameof(duration), "Entre 60 e 180 segundos.");
        var step = interval ?? TimeSpan.FromSeconds(1);
        delay ??= Task.Delay;
        var samples = new List<DpcSample>();
        var total = (int)Math.Round(duration / step);
        var cancelled = false;
        _ = source.Read(); // base dos contadores de taxa
        for (var i = 1; i <= total; i++)
        {
            try { await delay(step, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; break; }
            var cores = source.Read();
            if (cores is { Count: > 0 }) samples.Add(new DpcSample(i * step.TotalSeconds, cores));
            progress?.Report((double)i / total);
        }
        return (DriverLatencyAnalyzer.Analyze(samples), samples, cancelled);
    }
}
