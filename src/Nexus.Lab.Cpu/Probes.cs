using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Lab.Cpu;

/// <summary>
/// Frequência efetiva por cronometragem: uma cadeia de operações inteiras DEPENDENTES (cada uma espera o resultado da anterior).
/// Soma e XOR entre registradores levam exatamente 1 ciclo de latência em todos os x86 desde ~2006 (e nos ARM modernos), então
/// "operações por segundo" = "ciclos por segundo" = frequência real do núcleo naquele instante, incluindo o turbo. É o mesmo
/// princípio de ferramentas de microbenchmark (ex.: Chips and Cheese, "clock speed via dependent adds"). Não lê registrador nenhum:
/// não precisa de driver.
/// </summary>
public static class FrequencyProbe
{
    /// <summary>Operações dependentes por iteração do laço (o laço em si roda em paralelo, fora da cadeia).</summary>
    public const int OpsPerIteration = 32;

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static long Chain(long iterations, long b, long c)
    {
        long a = iterations;
        for (long i = 0; i < iterations; i++)
        {
            a += b; a ^= c; a += b; a ^= c; a += b; a ^= c; a += b; a ^= c;
            a += b; a ^= c; a += b; a ^= c; a += b; a ^= c; a += b; a ^= c;
            a += b; a ^= c; a += b; a ^= c; a += b; a ^= c; a += b; a ^= c;
            a += b; a ^= c; a += b; a ^= c; a += b; a ^= c; a += b; a ^= c;
        }
        return a;
    }

    private static long _sink;

    /// <summary>Roda a cadeia por ~<paramref name="sampleMs"/> e devolve GHz. Calibra o número de iterações sozinho.</summary>
    public static double Sample(double sampleMs, ref long iterations)
    {
        if (iterations <= 0) iterations = 100_000;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var t0 = Stopwatch.GetTimestamp();
            _sink += Chain(iterations, 3, 0x5A5A);
            var seconds = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            if (seconds >= sampleMs / 1000.0 * 0.6 || attempt == 7)
            {
                var ghz = iterations * (double)OpsPerIteration / seconds / 1e9;
                iterations = Math.Max(1000, (long)(iterations * (sampleMs / 1000.0) / Math.Max(seconds, 1e-6)));
                return ghz;
            }
            iterations = Math.Max(iterations * 2, (long)(iterations * (sampleMs / 1000.0) / Math.Max(seconds, 1e-6)));
        }
        return 0;
    }

    /// <summary>
    /// Mede um núcleo: aquece (o núcleo sobe do repouso para o turbo em dezenas de ms) e devolve a melhor de várias amostras.
    /// A melhor, não a média: interrupções e trocas de contexto só podem deixar uma amostra mais lenta, nunca mais rápida.
    /// </summary>
    public static double MeasureCurrentThread(TimeSpan warmup, int samples, double sampleMs, CancellationToken ct)
    {
        long iterations = 0;
        var end = Stopwatch.GetTimestamp() + (long)(warmup.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            ct.ThrowIfCancellationRequested();
            Sample(Math.Min(20, sampleMs), ref iterations);
        }
        var best = 0.0;
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            best = Math.Max(best, Sample(sampleMs, ref iterations));
        }
        return best;
    }
}

public sealed record CoreFrequency(int Core, int Logical, double Ghz, bool Pinned, bool PerformanceCore);

public sealed record LatencyPoint(long Bytes, double Ns, double Cycles);

/// <summary>
/// Latência de cache e memória por perseguição de ponteiros: cada posição guarda o endereço da próxima, numa ordem aleatória
/// (um ciclo único, algoritmo de Sattolo) que salta de linha de cache em linha de cache. Como cada leitura depende da anterior e
/// a ordem é aleatória, o pré-buscador não adivinha nada: o tempo médio por salto é a latência de quem guarda aquele tamanho de
/// dados (L1, L2, L3 ou a RAM). Acima de alguns MB o tempo inclui também faltas de TLB (tradução de páginas de 4 KB), como em
/// qualquer programa real que espalha dados pela memória.
/// </summary>
public static unsafe class LatencyProbe
{
    public const int LineBytes = 64;

    /// <summary>Tamanhos de 4 KB até <paramref name="maxBytes"/>, em passos de √2 (dois por oitava) até 64 MB e de 2× depois.</summary>
    public static IReadOnlyList<long> Sizes(long maxBytes, long minBytes = 4096)
    {
        var list = new List<long>();
        for (var exp = 12.0; ; exp += 0.5)
        {
            var bytes = (long)Math.Round(Math.Pow(2, exp) / LineBytes) * LineBytes;
            if (bytes < minBytes) continue;
            if (bytes > maxBytes) break;
            if (bytes > 64L << 20 && exp % 1 != 0) continue;
            list.Add(bytes);
        }
        return list;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static nint Chase(nint start, long steps)
    {
        var p = start;
        for (long i = 0; i < steps; i += 8)
        {
            p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
            p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
        }
        return p;
    }

    private static nint _sink;

    /// <summary>Latência média (ns) de um salto num conjunto de <paramref name="bytes"/>. Lança OutOfMemoryException se não couber.</summary>
    public static double Measure(long bytes, Random random, CancellationToken ct, long minSteps = 2_000_000, double targetMs = 120)
    {
        ArgumentNullException.ThrowIfNull(random);
        var lines = (int)Math.Max(2, bytes / LineBytes);
        var mem = (byte*)NativeMemory.AlignedAlloc((nuint)((long)lines * LineBytes), 4096);
        try
        {
            var order = new int[lines];
            for (var i = 0; i < lines; i++) order[i] = i;
            for (var i = lines - 1; i > 0; i--) // Sattolo: uma permutação que é um ciclo só, passando por todas as linhas
            {
                var j = random.Next(i);
                (order[i], order[j]) = (order[j], order[i]);
            }
            for (var i = 0; i < lines; i++)
            {
                var from = mem + (long)order[i] * LineBytes;
                var to = mem + (long)order[(i + 1) % lines] * LineBytes;
                *(nint*)from = (nint)to;
            }
            ct.ThrowIfCancellationRequested();
            var start = (nint)(mem + (long)order[0] * LineBytes);
            _sink += Chase(start, Math.Max(8, ((long)lines + 7) / 8 * 8)); // aquece: traz o conjunto para a cache (ou o mais perto possível)

            // Calibra: quantos saltos cabem no tempo alvo, com um mínimo para a precisão do cronômetro.
            long steps = 1 << 16;
            var t0 = Stopwatch.GetTimestamp();
            _sink += Chase(start, steps);
            var probeNs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1e6 / steps;
            steps = (long)Math.Clamp(targetMs * 1e6 / Math.Max(probeNs, 0.2), 1 << 18, Math.Max(minSteps, 1 << 18) * 4L);
            steps = (steps + 7) / 8 * 8;
            ct.ThrowIfCancellationRequested();

            var best = double.MaxValue;
            for (var rep = 0; rep < 2; rep++)
            {
                var t = Stopwatch.GetTimestamp();
                _sink += Chase(start, steps);
                best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalMilliseconds * 1e6 / steps);
            }
            return best;
        }
        finally { NativeMemory.AlignedFree(mem); }
    }
}

/// <summary>
/// Latência núcleo-a-núcleo: duas threads presas em núcleos diferentes passam a "bola" por uma única linha de cache com
/// Interlocked.CompareExchange (a mesma operação atômica que travas e filas usam). Cada passe obriga a linha a viajar entre as
/// caches dos dois núcleos; metade do tempo de ida e volta é o custo de comunicação entre eles. Núcleos que dividem a mesma L3
/// (um CCX na AMD) respondem rápido; entre CCX/CCD, ou entre núcleos P e E, o caminho é mais longo.
/// </summary>
public static class CoreToCoreProbe
{
    private sealed class Ball
    {
        // Um vetor de 48 longs com a "bola" no meio: as linhas vizinhas ficam vazias (128 bytes de cada lado cobrem o
        // pré-buscador de pares de linhas), então só a linha da bola viaja entre os núcleos.
        private readonly long[] _line = new long[48];
        public ref long Value => ref _line[24];
        public volatile bool Abort;
    }

    /// <summary>Latência de ida (ns) entre dois processadores lógicos. Null se a outra thread não respondeu a tempo.</summary>
    public static async Task<double?> MeasurePairAsync(IThreadPinner pinner, LogicalCpu a, LogicalCpu b, int rounds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pinner);
        var best = double.MaxValue;
        for (var sample = 0; sample < 3; sample++)
        {
            ct.ThrowIfCancellationRequested();
            var ball = new Ball();
            using var ready = new CountdownEvent(2);
            using var go = new ManualResetEventSlim(false);
            var ping = MeasureThreads.Run(pinner, a, _ => Play(ball, 0, rounds, ready, go, timeStart: true));
            var pong = MeasureThreads.Run(pinner, b, _ => Play(ball, 1, rounds, ready, go, timeStart: false));
            var both = Task.WhenAll(ping, pong);
            double? seconds;
            try
            {
                var started = await Task.Run(() => ready.Wait(TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
                if (!started || ct.IsCancellationRequested) ball.Abort = true;
                go.Set();
                var timeout = Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
                while (!both.IsCompleted)
                {
                    if (await Task.WhenAny(both, timeout, Task.Delay(100, CancellationToken.None)).ConfigureAwait(false) == timeout || ct.IsCancellationRequested)
                        ball.Abort = true;
                }
            }
            finally
            {
                ball.Abort |= !both.IsCompleted;
                go.Set();
                await both.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            seconds = ping.Result;
            if (seconds is null) return null;
            best = Math.Min(best, seconds.Value * 1e9 / rounds / 2);
        }
        return best;
    }

    private static double? Play(Ball ball, int parity, int rounds, CountdownEvent ready, ManualResetEventSlim go, bool timeStart)
    {
        ready.Signal();
        while (!go.Wait(50)) if (ball.Abort) return null;
        if (ball.Abort) return null;
        // Aquecimento curto (não cronometrado) para os dois núcleos saírem do repouso.
        var warm = Math.Min(2000, rounds / 4);
        if (!Exchange(ball, parity, 0, warm)) return null;
        var t0 = Stopwatch.GetTimestamp();
        if (!Exchange(ball, parity, warm, warm + rounds)) return null;
        return timeStart ? Stopwatch.GetElapsedTime(t0).TotalSeconds : 0;
    }

    private static bool Exchange(Ball ball, int parity, int from, int to)
    {
        for (var r = from; r < to; r++)
        {
            var expect = 2L * r + parity;
            var spins = 0;
            while (Interlocked.CompareExchange(ref ball.Value, expect + 1, expect) != expect)
            {
                if ((++spins & 0x3FF) == 0)
                {
                    if (ball.Abort) return false;
                    if (spins > 1 << 16) Thread.Yield(); // sem fixação ou com poucos processadores: cede a vez
                }
            }
        }
        return true;
    }
}

/// <summary>Carga em todos os processadores lógicos: cada thread roda a cadeia dependente em fatias e anota GHz por fatia.</summary>
public static class AllCoreLoad
{
    public sealed record Slice(int Logical, double AtSeconds, double Ghz);

    public static async Task<IReadOnlyList<Slice>> RunAsync(IThreadPinner pinner, IReadOnlyList<LogicalCpu> cpus, TimeSpan duration, double sliceMs,
        IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pinner);
        ArgumentNullException.ThrowIfNull(cpus);
        var start = Stopwatch.GetTimestamp();
        var stopAt = start + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var tasks = cpus.Select(cpu => MeasureThreads.Run(pinner, cpu, _ =>
        {
            var slices = new List<Slice>();
            long iterations = 0;
            while (Stopwatch.GetTimestamp() < stopAt && !ct.IsCancellationRequested)
            {
                var ghz = FrequencyProbe.Sample(sliceMs, ref iterations);
                slices.Add(new Slice(cpu.Index, Stopwatch.GetElapsedTime(start).TotalSeconds, ghz));
            }
            return slices;
        }, ThreadPriority.AboveNormal)).ToList();

        var all = Task.WhenAll(tasks);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
            progress?.Report(Math.Min(1, Stopwatch.GetElapsedTime(start).TotalSeconds / Math.Max(0.001, duration.TotalSeconds)));
        }
        var result = (await all.ConfigureAwait(false)).SelectMany(s => s).ToList();
        ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Média de GHz entre as threads, em janelas de <paramref name="window"/> segundos.</summary>
    public static IReadOnlyList<FrequencySample> Series(IReadOnlyList<Slice> slices, double window = 1.0)
    {
        ArgumentNullException.ThrowIfNull(slices);
        return slices.GroupBy(s => (int)(s.AtSeconds / window))
                     .OrderBy(g => g.Key)
                     .Select(g => new FrequencySample((g.Key + 1) * window, g.Average(s => s.Ghz)))
                     .ToList();
    }

    /// <summary>Operações dependentes por segundo somadas em todas as threads (bilhões/s), na janela informada.</summary>
    public static double Throughput(IReadOnlyList<Slice> slices, double fromSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(slices);
        return slices.Where(s => s.AtSeconds >= fromSeconds).GroupBy(s => s.Logical).Sum(g => g.Average(s => s.Ghz));
    }
}

public sealed record FrequencySample(double Seconds, double Ghz);
