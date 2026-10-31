using System.Globalization;

namespace Nexus.Lab.Cpu;

/// <summary>Um degrau da curva de latência: até <see cref="CapacityBytes"/> os dados cabem neste nível.</summary>
public sealed record CacheStep(string Level, long? CapacityBytes, long? NextBytes, double Ns, double Cycles);

/// <summary>Comparação de um degrau medido com o que o CPUID/Windows declara.</summary>
public sealed record CacheComparison(string Level, long? DeclaredBytes, long? MeasuredBytes, bool Agrees, string Text);

/// <summary>
/// Acha os patamares da curva de latência (L1, L2, L3, RAM) sem usar o CPUID: ajusta degraus constantes ao logaritmo da latência
/// (programação dinâmica, mínimos quadrados), escolhe o menor número de degraus que explica a curva com erro pequeno, junta
/// patamares vizinhos parecidos e descarta pontos de transição isolados.
/// </summary>
public static class CacheStepDetector
{
    public static IReadOnlyList<CacheStep> Detect(IReadOnlyList<LatencyPoint> curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var pts = curve.Where(p => p.Ns > 0).OrderBy(p => p.Bytes).ToList();
        if (pts.Count < 3) return [];
        var y = pts.Select(p => Math.Log(p.Ns)).ToArray();
        var segments = BestSegmentation(y);

        // Junta segmentos vizinhos com médias próximas (menos de 20% de diferença).
        var merged = new List<(int From, int To)>();
        foreach (var s in segments)
        {
            if (merged.Count > 0 && Math.Exp(Mean(y, s.From, s.To) - Mean(y, merged[^1].From, merged[^1].To)) < 1.2)
                merged[^1] = (merged[^1].From, s.To);
            else merged.Add(s);
        }

        // Patamar = segmento com 2+ pontos (o último pode ter 1, se a varredura terminou ali). Isolados são transição.
        var plateaus = merged.Where((s, i) => s.To - s.From >= 1 || (i == merged.Count - 1 && merged.Count > 1)).ToList();
        var steps = new List<CacheStep>();
        for (var i = 0; i < plateaus.Count; i++)
        {
            var (from, to) = plateaus[i];
            var med = Median(pts.Skip(from).Take(to - from + 1).Select(p => p.Ns));
            var medCycles = Median(pts.Skip(from).Take(to - from + 1).Select(p => p.Cycles));
            var last = i == plateaus.Count - 1;
            long? capacity = null, next = null;
            if (!last)
            {
                // Capacidade: o maior tamanho do patamar que ainda está perto do seu nível típico.
                var cap = from;
                for (var k = from; k <= to; k++) if (pts[k].Ns <= med * 1.15) cap = k;
                capacity = pts[cap].Bytes;
                next = cap + 1 < pts.Count ? pts[cap + 1].Bytes : null;
            }
            steps.Add(new CacheStep("", capacity, next, med, medCycles));
        }

        // Nomes: L1, L2, L3... e "RAM" no último quando ele está claramente fora das caches (mais de 40 ns).
        for (var i = 0; i < steps.Count; i++)
        {
            var isRam = i == steps.Count - 1 && i > 0 && steps[i].Ns >= 40;
            steps[i] = steps[i] with { Level = isRam ? "RAM" : $"L{i + 1}" };
        }
        return steps;
    }

    private static List<(int From, int To)> BestSegmentation(double[] y)
    {
        var n = y.Length;
        var maxK = Math.Min(7, n);
        // cost[i,j]: erro quadrático de um degrau constante em y[i..j].
        var prefix = new double[n + 1];
        var prefix2 = new double[n + 1];
        for (var i = 0; i < n; i++) { prefix[i + 1] = prefix[i] + y[i]; prefix2[i + 1] = prefix2[i] + y[i] * y[i]; }
        double Cost(int i, int j)
        {
            var len = j - i + 1;
            var s = prefix[j + 1] - prefix[i];
            return prefix2[j + 1] - prefix2[i] - s * s / len;
        }

        List<(int, int)>? chosen = null;
        for (var k = 1; k <= maxK; k++)
        {
            var dp = new double[k + 1, n + 1];
            var back = new int[k + 1, n + 1];
            for (var a = 0; a <= k; a++) for (var b = 0; b <= n; b++) dp[a, b] = double.PositiveInfinity;
            dp[0, 0] = 0;
            for (var seg = 1; seg <= k; seg++)
                for (var end = seg; end <= n; end++)
                    for (var startAt = seg - 1; startAt < end; startAt++)
                    {
                        var c = dp[seg - 1, startAt] + Cost(startAt, end - 1);
                        if (c < dp[seg, end]) { dp[seg, end] = c; back[seg, end] = startAt; }
                    }
            var list = new List<(int, int)>();
            var e = n;
            for (var seg = k; seg >= 1; seg--) { var s = back[seg, e]; list.Add((s, e - 1)); e = s; }
            list.Reverse();
            chosen = list;
            // RMS em log < 0,06 ≈ 6% de erro típico por ponto: bom o bastante; mais degraus seriam ruído.
            if (Math.Sqrt(dp[k, n] / n) < 0.06) break;
        }
        return chosen!;
    }

    private static double Mean(double[] y, int from, int to)
    {
        double s = 0;
        for (var i = from; i <= to; i++) s += y[i];
        return s / (to - from + 1);
    }

    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var v = values.Order().ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>
    /// Compara degraus medidos com os tamanhos declarados. Concorda quando o declarado cai entre a última medida dentro do patamar
    /// (dividida por 1,5) e a primeira fora (vezes 1,5): a curva tem dois pontos por oitava, e o degrau real começa um pouco antes
    /// do tamanho cheio porque código, pilha e tabelas de página também ocupam a cache.
    /// </summary>
    public static IReadOnlyList<CacheComparison> Compare(IReadOnlyList<CacheStep> steps, IReadOnlyDictionary<int, long> declaredByLevel)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(declaredByLevel);
        var list = new List<CacheComparison>();
        foreach (var (level, declared) in declaredByLevel.OrderBy(kv => kv.Key))
        {
            var label = $"L{level}";
            // O degrau medido mais próximo (em escala log) do tamanho declarado.
            var match = steps.Where(s => s.CapacityBytes is not null)
                             .OrderBy(s => Math.Abs(Math.Log((double)s.CapacityBytes!.Value / declared)))
                             .FirstOrDefault();
            if (match is null)
            {
                list.Add(new(label, declared, null, false, $"O CPUID diz {label} {Bytes(declared)}; a curva não mostrou um degrau para comparar."));
                continue;
            }
            var lo = match.CapacityBytes!.Value / 1.5;
            var hi = (match.NextBytes ?? match.CapacityBytes.Value * 2) * 1.5;
            var agrees = declared >= lo && declared <= hi;
            var text = agrees
                ? $"O CPUID diz {label} {Bytes(declared)}; a medição mostra o degrau em ~{Bytes(match.CapacityBytes.Value)}–{Bytes(match.NextBytes ?? match.CapacityBytes.Value * 2)}. Confere."
                : $"O CPUID diz {label} {Bytes(declared)}; o degrau medido mais próximo está em ~{Bytes(match.CapacityBytes.Value)}. Não confere (outro programa usando a cache, ou o nível tem comportamento diferente).";
            list.Add(new(label, declared, match.CapacityBytes, agrees, text));
        }
        return list;
    }

    public static string Bytes(long b) => b >= 1L << 30 ? Fmt(b / (double)(1L << 30), "GB") : b >= 1L << 20 ? Fmt(b / (double)(1L << 20), "MB") : Fmt(b / 1024.0, "KB");

    private static string Fmt(double v, string unit) => v.ToString(v >= 10 || Math.Abs(v - Math.Round(v)) < 0.05 ? "0" : "0.#", CultureInfo.GetCultureInfo("pt-BR")) + " " + unit;
}

public sealed record CoreToCoreMatrix(IReadOnlyList<int> Cores, double?[][] Ns)
{
    public double? At(int i, int j) => i == j ? null : Ns[i][j];
}

public sealed record ClusterInference(IReadOnlyList<IReadOnlyList<int>> Clusters, double? NearNs, double? FarNs, string Explanation);

/// <summary>Agrupa núcleos pela matriz de latência: o maior salto entre latências separa "perto" (mesma L3) de "longe".</summary>
public static class ClusterInferrer
{
    public static ClusterInference Infer(CoreToCoreMatrix m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var n = m.Cores.Count;
        var values = new List<double>();
        for (var i = 0; i < n; i++) for (var j = i + 1; j < n; j++) if (m.Ns[i][j] is { } v) values.Add(v);
        if (values.Count == 0) return new([m.Cores.ToList()], null, null, "Sem pares medidos.");
        values.Sort();
        var bestRatio = 1.0;
        var cut = -1;
        for (var i = 0; i + 1 < values.Count; i++)
        {
            var ratio = values[i + 1] / Math.Max(values[i], 0.001);
            if (ratio > bestRatio) { bestRatio = ratio; cut = i; }
        }
        if (bestRatio < 1.4 || cut < 0)
            return new([m.Cores.ToList()], values[0], values[^1],
                $"Todos os núcleos se falam com latência parecida ({values[0]:0} a {values[^1]:0} ns): um grupo só, sem divisão interna que pese.");

        var threshold = Math.Sqrt(values[cut] * values[cut + 1]);
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if ((m.Ns[i][j] ?? double.MaxValue) < threshold) parent[Find(i)] = Find(j);
        var clusters = Enumerable.Range(0, n).GroupBy(Find).Select(g => (IReadOnlyList<int>)g.Select(i => m.Cores[i]).ToList())
                                 .OrderBy(g => g[0]).ToList();
        var near = CacheStepDetector.Median(values.Where(v => v < threshold));
        var far = CacheStepDetector.Median(values.Where(v => v >= threshold));
        var text = clusters.Count > 1
            ? $"{clusters.Count} grupos: dentro do grupo ~{near:0} ns, entre grupos ~{far:0} ns. Na AMD isso é um CCX/CCD por grupo; na Intel híbrida, núcleos E em blocos de 4."
            : $"Um grupo só, com pares mais lentos (~{far:0} ns) espalhados: provavelmente interferência durante a medição.";
        return new(clusters, near, far, text);
    }
}

public sealed record ThrottleResult(double InitialGhz, double SustainedGhz, double DropPercent, double? DropAfterSeconds, bool Throttles, bool Unstable, string Text);

/// <summary>Detecta queda de frequência sob carga contínua (limite de potência ou de temperatura) numa série GHz × tempo.</summary>
public static class ThrottleDetector
{
    public const double ThresholdPercent = 7;

    public static ThrottleResult? Analyze(IReadOnlyList<FrequencySample> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Count < 4) return null;
        var smooth = series.Select((s, i) => series.Skip(Math.Max(0, i - 1)).Take(i == 0 ? 2 : 3).Average(x => x.Ghz)).ToList();
        var head = Math.Max(2, series.Count / 4);
        var initial = smooth.Take(head).Max();
        var tailFrom = series.Count - Math.Max(2, series.Count / 3);
        var tail = series.Skip(tailFrom).Select(s => s.Ghz).ToList();
        var sustained = CacheStepDetector.Median(tail);
        var drop = initial <= 0 ? 0 : Math.Max(0, (initial - sustained) / initial * 100);
        double? after = null;
        if (drop >= ThresholdPercent)
        {
            var mid = initial - (initial - sustained) / 2;
            for (var i = 0; i < smooth.Count; i++) if (smooth[i] < mid) { after = series[i].Seconds; break; }
        }
        var mean = tail.Average();
        var sd = Math.Sqrt(tail.Sum(v => (v - mean) * (v - mean)) / tail.Count);
        var unstable = mean > 0 && sd / mean > 0.08;
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var text = drop >= ThresholdPercent
            ? string.Format(pt, "Começou em {0:0.00} GHz e caiu para {1:0.00} GHz ({2:0}% a menos){3}. É o processador batendo no limite de potência ou de temperatura.",
                initial, sustained, drop, after is { } a ? string.Format(pt, " depois de ~{0:0} s", a) : "")
            : string.Format(pt, "Manteve {0:0.00} GHz do início ao fim (variação de {1:0.#}%): sem queda por temperatura ou potência nesta duração.", sustained, drop);
        if (unstable) text += " A frequência oscilou bastante no fim (mais de 8%): o sistema está alternando entre limites.";
        return new ThrottleResult(initial, sustained, drop, after, drop >= ThresholdPercent, unstable, text);
    }
}
