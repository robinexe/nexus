using System.Diagnostics;

namespace Nexus.Lab.Cpu;

public sealed record CpuLabProgress(string Stage, double Fraction);

public sealed record CpuLabOptions
{
    public TimeSpan Warmup { get; init; } = TimeSpan.FromMilliseconds(150);
    public int Samples { get; init; } = 5;
    public double SampleMs { get; init; } = 40;
    public long LatencyMaxBytes { get; init; } = 512L << 20;
    public double LatencyTargetMs { get; init; } = 120;
    public int CoreToCoreRounds { get; init; } = 20_000;
    /// <summary>Limite de núcleos na matriz (pares crescem com o quadrado). Acima disso, mede-se uma amostra espalhada.</summary>
    public int CoreToCoreMaxCores { get; init; } = 16;
    public TimeSpan AllCoreDuration { get; init; } = TimeSpan.FromSeconds(4);
    public double AllCoreSliceMs { get; init; } = 50;

    /// <summary>A bateria rápida (~20 s num PC de 6 a 8 núcleos).</summary>
    public static CpuLabOptions Quick { get; } = new();

    /// <summary>Versão reduzida para testes: frações de segundo.</summary>
    public static CpuLabOptions Tiny { get; } = new()
    {
        Warmup = TimeSpan.FromMilliseconds(10), Samples = 2, SampleMs = 5, LatencyMaxBytes = 256 << 10, LatencyTargetMs = 3,
        CoreToCoreRounds = 500, CoreToCoreMaxCores = 3, AllCoreDuration = TimeSpan.FromMilliseconds(400), AllCoreSliceMs = 20,
    };
}

/// <summary>
/// Orquestra as medições. Tudo roda fora da thread da interface, em threads dedicadas com prioridade alta só enquanto medem, e
/// para no cancelamento. Nada aqui muda o sistema: só calcula e cronometra.
/// </summary>
public sealed class CpuLabRunner(IThreadPinner pinner, CpuTopology topology, CpuLabOptions options)
{
    private readonly IThreadPinner _pinner = pinner ?? throw new ArgumentNullException(nameof(pinner));
    private readonly CpuTopology _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly CpuLabOptions _o = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Frequência de cada núcleo físico (na primeira thread dele), um núcleo por vez.</summary>
    public async Task<IReadOnlyList<CoreFrequency>> MeasureCoresAsync(IProgress<CpuLabProgress>? progress, double from, double to, CancellationToken ct)
    {
        var list = new List<CoreFrequency>();
        var cores = _topology.Cores;
        for (var i = 0; i < cores.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var core = cores[i];
            var cpu = core.Threads[0];
            progress?.Report(new($"Frequência do núcleo {core.Index + 1} de {cores.Count}", from + (to - from) * i / cores.Count));
            var (ghz, pinned) = await MeasureThreads.Run(_pinner, cpu, p => (FrequencyProbe.MeasureCurrentThread(_o.Warmup, _o.Samples, _o.SampleMs, ct), p)).ConfigureAwait(false);
            list.Add(new CoreFrequency(core.Index, cpu.Index, ghz, pinned, _topology.IsPerformanceCore(core)));
        }
        return list;
    }

    /// <summary>Curva de latência no núcleo indicado. Para no primeiro tamanho que não couber na memória.</summary>
    public async Task<IReadOnlyList<LatencyPoint>> MeasureLatencyAsync(LogicalCpu? cpu, double ghz, IProgress<CpuLabProgress>? progress, double from, double to, CancellationToken ct)
    {
        var max = Math.Min(_o.LatencyMaxBytes, SafeMaxBytes());
        var sizes = LatencyProbe.Sizes(max);
        return await MeasureThreads.Run(_pinner, cpu, _ =>
        {
            var random = new Random(20261031);
            var points = new List<LatencyPoint>();
            for (var i = 0; i < sizes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new($"Latência com {CacheStepDetector.Bytes(sizes[i])}", from + (to - from) * i / sizes.Count));
                double ns;
                try { ns = LatencyProbe.Measure(sizes[i], random, ct, targetMs: _o.LatencyTargetMs); }
                catch (OutOfMemoryException) { break; }
                points.Add(new LatencyPoint(sizes[i], ns, ns * ghz));
            }
            return (IReadOnlyList<LatencyPoint>)points;
        }).ConfigureAwait(false);
    }

    /// <summary>Até 1/8 da memória disponível para o processo, nunca mais que 512 MB.</summary>
    private static long SafeMaxBytes()
    {
        var avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return avail <= 0 ? 512L << 20 : Math.Clamp(avail / 8, 16L << 20, 512L << 20);
    }

    /// <summary>Matriz de latência entre núcleos físicos (a primeira thread de cada um).</summary>
    public async Task<CoreToCoreMatrix> MeasureCoreToCoreAsync(IProgress<CpuLabProgress>? progress, double from, double to, CancellationToken ct)
    {
        var cores = _topology.Cores;
        IReadOnlyList<CoreInfo> chosen = cores.Count <= _o.CoreToCoreMaxCores
            ? cores
            : Enumerable.Range(0, _o.CoreToCoreMaxCores).Select(i => cores[i * cores.Count / _o.CoreToCoreMaxCores]).ToList();
        var n = chosen.Count;
        var ns = Enumerable.Range(0, n).Select(_ => new double?[n]).ToArray();
        var pairs = n * (n - 1) / 2;
        var done = 0;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new($"Núcleo {chosen[i].Index + 1} ↔ núcleo {chosen[j].Index + 1}", from + (to - from) * done++ / Math.Max(1, pairs)));
                var v = await CoreToCoreProbe.MeasurePairAsync(_pinner, chosen[i].Threads[0], chosen[j].Threads[0], _o.CoreToCoreRounds, ct).ConfigureAwait(false);
                ns[i][j] = v;
                ns[j][i] = v;
            }
        return new CoreToCoreMatrix(chosen.Select(c => c.Index).ToList(), ns);
    }

    private List<LogicalCpu> AllLogical => _topology.Cores.SelectMany(c => c.Threads).OrderBy(t => t.Index).ToList();

    /// <summary>Bateria rápida: frequência por núcleo, latência de cache/RAM, núcleo-a-núcleo e alguns segundos com todos carregados.</summary>
    public async Task<CpuLabRun> RunQuickAsync(CpuLabRun template, IProgress<CpuLabProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        var sw = Stopwatch.StartNew();
        var cores = await MeasureCoresAsync(progress, 0, 0.2, ct).ConfigureAwait(false);
        var best = cores.MaxBy(c => c.Ghz);
        var bestCpu = best is null ? (LogicalCpu?)null : _topology.Cores[best.Core].Threads[0];
        var latency = await MeasureLatencyAsync(bestCpu, best?.Ghz ?? 0, progress, 0.2, 0.65, ct).ConfigureAwait(false);
        var matrix = _topology.Cores.Count >= 2
            ? await MeasureCoreToCoreAsync(progress, 0.65, 0.8, ct).ConfigureAwait(false)
            : new CoreToCoreMatrix([], []);
        progress?.Report(new("Todos os núcleos carregados", 0.8));
        var slices = await AllCoreLoad.RunAsync(_pinner, AllLogical, _o.AllCoreDuration, _o.AllCoreSliceMs,
            new Progress<double>(f => progress?.Report(new("Todos os núcleos carregados", 0.8 + 0.2 * f))), ct).ConfigureAwait(false);
        var series = AllCoreLoad.Series(slices, Math.Max(0.1, _o.AllCoreDuration.TotalSeconds / 8));
        progress?.Report(new("Pronto", 1));
        return template with
        {
            TakenAt = DateTimeOffset.Now,
            Kind = CpuRunKind.Quick,
            Pinned = cores.Any(c => c.Pinned),
            CoreFrequencies = cores,
            Latency = latency,
            CacheSteps = CacheStepDetector.Detect(latency),
            CoreToCoreCores = matrix.Cores,
            CoreToCoreNs = matrix.Ns,
            AllCoreSeries = series,
            AllCoreGhz = slices.Count == 0 ? null : AllCoreLoad.Series(slices, 1e9)[0].Ghz,
            SingleThreadGops = best?.Ghz,
            MultiThreadGops = slices.Count == 0 ? null : AllCoreLoad.Throughput(slices),
            Threads = AllLogical.Count,
            DurationSeconds = sw.Elapsed.TotalSeconds,
        };
    }

    /// <summary>Teste sustentado: mede o melhor núcleo sozinho e depois carrega todos por <paramref name="duration"/>.</summary>
    public async Task<CpuLabRun> RunSustainedAsync(CpuLabRun template, TimeSpan duration, int? preferredCore, IProgress<CpuLabProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(template);
        var sw = Stopwatch.StartNew();
        progress?.Report(new("Um núcleo sozinho (referência)", 0));
        var core = _topology.Cores[Math.Clamp(preferredCore ?? 0, 0, _topology.Cores.Count - 1)];
        var (st, pinned) = await MeasureThreads.Run(_pinner, core.Threads[0], p => (FrequencyProbe.MeasureCurrentThread(_o.Warmup, _o.Samples, _o.SampleMs, ct), p)).ConfigureAwait(false);
        // Pausa curta para o processador voltar a uma temperatura de partida comparável.
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1500, duration.TotalMilliseconds / 20)), ct).ConfigureAwait(false);
        var slices = await AllCoreLoad.RunAsync(_pinner, AllLogical, duration, 100,
            new Progress<double>(f => progress?.Report(new($"Todos os núcleos: {f * duration.TotalSeconds:0} de {duration.TotalSeconds:0} s", 0.05 + 0.95 * f))), ct).ConfigureAwait(false);
        var series = AllCoreLoad.Series(slices, duration.TotalSeconds >= 10 ? 1.0 : Math.Max(0.05, duration.TotalSeconds / 10));
        var tailFrom = duration.TotalSeconds * 2 / 3;
        progress?.Report(new("Pronto", 1));
        return template with
        {
            TakenAt = DateTimeOffset.Now,
            Kind = CpuRunKind.Sustained,
            Pinned = pinned,
            CoreFrequencies = [new CoreFrequency(core.Index, core.Threads[0].Index, st, pinned, _topology.IsPerformanceCore(core))],
            AllCoreSeries = series,
            AllCoreGhz = series.Count == 0 ? null : CacheStepDetector.Median(series.Skip(series.Count * 2 / 3).Select(s => s.Ghz)),
            SingleThreadGops = st,
            MultiThreadGops = slices.Count == 0 ? null : AllCoreLoad.Throughput(slices, tailFrom),
            Threads = AllLogical.Count,
            DurationSeconds = sw.Elapsed.TotalSeconds,
        };
    }
}
