namespace Nexus.Measurement;

/// <summary>Leitura ao vivo para a tela e o mostrador. NaN = ainda sem quadros suficientes.</summary>
/// <param name="CurrentFps">FPS do último segundo (o número que "pula").</param>
/// <param name="AverageFps">FPS médio desde o começo da sessão (média temporal: quadros ÷ tempo).</param>
/// <param name="Low1Fps">1% low da janela deslizante (últimos <see cref="LiveFrameStats.WindowSeconds"/> s): o que você está sentindo agora.</param>
/// <param name="SessionLow1Fps">1% low da sessão inteira (aproximado ao vivo, em faixas de 0,05 ms; o resumo salvo é exato).</param>
/// <param name="FrametimeMs">Frametime mediano do último segundo.</param>
public readonly record struct LiveReading(double CurrentFps, double AverageFps, double Low1Fps, double SessionLow1Fps, double FrametimeMs,
    long Frames, double ElapsedSeconds)
{
    public static LiveReading Empty { get; } = new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0, 0);
    public bool HasData => Frames > 0 && double.IsFinite(CurrentFps);
}

/// <summary>
/// Estatística incremental dos quadros de uma sessão ao vivo. Custo por quadro O(log n) + cópia de memória pequena:
/// <list type="bullet">
/// <item>FPS atual: quadros do último segundo (fila);</item>
/// <item>1% low deslizante: frametimes dos últimos N segundos numa lista ORDENADA (inserção/remoção por busca binária);
/// 1% low = 1000 ÷ média dos ⌈1%⌉ maiores (mesma fórmula de <see cref="Nexus.Experimentation.FrametimeMetrics"/>);</item>
/// <item>1% low da sessão: histograma de 0,05 ms até 250 ms + lista exata acima disso (memória fixa, horas de jogo).</item>
/// </list>
/// O relógio é a soma dos frametimes (não a coluna de tempo do PresentMon, cuja unidade muda entre versões).
/// </summary>
public sealed class LiveFrameStats
{
    public const double BinMs = 0.05;
    public const double HistogramMaxMs = 250;
    public const int MinFramesForLow = 20;

    private readonly Queue<(double At, double Ft)> _lastSecond = new();
    private double _lastSecondSum;
    private readonly Queue<(double At, double Ft)> _window = new();
    private readonly List<double> _windowSorted = [];
    private readonly int[] _bins = new int[(int)(HistogramMaxMs / BinMs)];
    private readonly List<double> _overflow = [];
    private readonly Queue<TracePoint> _trace = new();
    private double _bucketStart = double.NaN, _bucketWorst;

    public LiveFrameStats(double windowSeconds = 10, double traceSeconds = 60, double traceBucketMs = 50)
    {
        if (windowSeconds is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        WindowSeconds = windowSeconds;
        TraceSeconds = traceSeconds;
        TraceBucketMs = traceBucketMs;
    }

    public double WindowSeconds { get; }
    public double TraceSeconds { get; }
    public double TraceBucketMs { get; }
    public long Frames { get; private set; }
    public double ElapsedMs { get; private set; }

    public void Add(double frametimeMs)
    {
        if (!double.IsFinite(frametimeMs) || frametimeMs <= 0) return;
        Frames++;
        ElapsedMs += frametimeMs;
        var now = ElapsedMs;

        _lastSecond.Enqueue((now, frametimeMs));
        _lastSecondSum += frametimeMs;
        while (_lastSecond.Count > 1 && now - _lastSecond.Peek().At >= 1000) _lastSecondSum -= _lastSecond.Dequeue().Ft;

        _window.Enqueue((now, frametimeMs));
        Insert(_windowSorted, frametimeMs);
        while (_window.Count > 1 && now - _window.Peek().At >= WindowSeconds * 1000) Remove(_windowSorted, _window.Dequeue().Ft);

        if (frametimeMs >= HistogramMaxMs) _overflow.Add(frametimeMs);
        else _bins[(int)(frametimeMs / BinMs)]++;

        // Curva rolante: em cada balde de tempo guarda o PIOR quadro (um engasgo não some do gráfico).
        if (double.IsNaN(_bucketStart)) { _bucketStart = now - frametimeMs; _bucketWorst = 0; }
        _bucketWorst = Math.Max(_bucketWorst, frametimeMs);
        if (now - _bucketStart >= TraceBucketMs)
        {
            _trace.Enqueue(new TracePoint(now / 1000.0, _bucketWorst));
            _bucketStart = now;
            _bucketWorst = 0;
            while (_trace.Count > 0 && now / 1000.0 - _trace.Peek().Seconds > TraceSeconds) _trace.Dequeue();
        }
    }

    public LiveReading Read()
    {
        if (Frames == 0) return LiveReading.Empty;
        var current = _lastSecondSum > 0 ? 1000.0 * _lastSecond.Count / _lastSecondSum : double.NaN;
        var median = Median(_lastSecond);
        var low = _windowSorted.Count >= MinFramesForLow ? Low1(_windowSorted) : double.NaN;
        return new LiveReading(current, 1000.0 * Frames / ElapsedMs, low, Frames >= MinFramesForLow ? SessionLow1() : double.NaN, median, Frames, ElapsedMs / 1000.0);
    }

    /// <summary>Últimos <see cref="TraceSeconds"/> s de curva (tempo absoluto desde o início, em segundos).</summary>
    public IReadOnlyList<TracePoint> Trace() => _trace.ToList();

    /// <summary>1000 ÷ média dos ⌈1%⌉ maiores de uma lista ordenada crescente.</summary>
    public static double Low1(IReadOnlyList<double> sortedAscending)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        var n = sortedAscending.Count;
        if (n == 0) return double.NaN;
        var k = (int)Math.Ceiling(0.01 * n);
        double sum = 0;
        for (var i = n - k; i < n; i++) sum += sortedAscending[i];
        return 1000.0 / (sum / k);
    }

    private double SessionLow1()
    {
        var k = (long)Math.Ceiling(0.01 * Frames);
        double sum = 0;
        long taken = 0;
        if (_overflow.Count > 0)
        {
            foreach (var v in _overflow.OrderDescending())
            {
                if (taken == k) break;
                sum += v;
                taken++;
            }
        }
        for (var b = _bins.Length - 1; b >= 0 && taken < k; b--)
        {
            if (_bins[b] == 0) continue;
            var take = Math.Min(_bins[b], k - taken);
            sum += take * (b + 0.5) * BinMs;
            taken += take;
        }
        return taken == 0 ? double.NaN : 1000.0 / (sum / taken);
    }

    private static double Median(Queue<(double At, double Ft)> q)
    {
        if (q.Count == 0) return double.NaN;
        var a = q.Select(x => x.Ft).Order().ToArray();
        return a[a.Length / 2];
    }

    private static void Insert(List<double> sorted, double v)
    {
        var i = sorted.BinarySearch(v);
        sorted.Insert(i < 0 ? ~i : i, v);
    }

    private static void Remove(List<double> sorted, double v)
    {
        var i = sorted.BinarySearch(v);
        if (i >= 0) sorted.RemoveAt(i);
    }
}

/// <summary>Diferença ao vivo contra a sessão de referência (o "antes") do mesmo jogo.</summary>
public sealed record LiveDelta(double AverageFpsPercent, double Low1Percent, string Text);

public static class LiveComparison
{
    /// <summary>Segundos mínimos de jogo antes de mostrar diferença (abaixo disso o número é ruído de carregamento).</summary>
    public const double MinSeconds = 30;

    public static LiveDelta? Delta(LiveReading live, MeasuredSession? reference)
    {
        if (reference is null || !live.HasData || live.ElapsedSeconds < MinSeconds) return null;
        var avg = Pct(live.AverageFps, reference.Summary.AverageFps);
        var low = Pct(live.SessionLow1Fps, reference.Summary.OnePercentLowAverageFps);
        if (!double.IsFinite(avg)) return null;
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        var text = $"FPS médio {avg.ToString("+0.0;-0.0;0.0", pt)}%" + (double.IsFinite(low) ? $" · 1% low {low.ToString("+0.0;-0.0;0.0", pt)}%" : "") +
                   " contra a partida de referência (prévia: o veredito sai ao fim da partida)";
        return new LiveDelta(avg, low, text);
    }

    /// <summary>A curva da referência no mesmo trecho de tempo da partida atual (para sobrepor no gráfico rolante).</summary>
    public static IReadOnlyList<TracePoint> ReferenceWindow(MeasuredSession? reference, double fromSeconds, double toSeconds)
    {
        if (reference is null || reference.Trace.Count == 0 || !(toSeconds > fromSeconds)) return [];
        var end = reference.Trace[^1].Seconds;
        if (end <= 0) return [];
        // Partida atual mais longa que a referência: repete a referência (ela é só um "nível" de comparação, não um replay).
        var list = new List<TracePoint>();
        foreach (var p in reference.Trace)
        {
            var cycles = Math.Floor(fromSeconds / end);
            for (var c = cycles; c <= cycles + 1; c++)
            {
                var t = p.Seconds + c * end;
                if (t >= fromSeconds && t <= toSeconds) list.Add(new TracePoint(t, p.FrametimeMs));
            }
        }
        list.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
        return list;
    }

    private static double Pct(double now, double before) => double.IsFinite(now) && double.IsFinite(before) && before > 0 ? 100.0 * (now - before) / before : double.NaN;
}
