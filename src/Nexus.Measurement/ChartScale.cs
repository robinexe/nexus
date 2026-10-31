namespace Nexus.Measurement;

/// <summary>Linha de referência do gráfico de frametime: o tempo por quadro de uma taxa de FPS conhecida.</summary>
public sealed record FrametimeReference(double Ms, string Label);

/// <summary>
/// Escala dos gráficos de medição, sem dependência de interface (testável em qualquer SO). O eixo Y vai até um pouco acima
/// do P99,5 dos frametimes (nunca abaixo de 8 ms, com teto), para que um único engasgo de 200 ms não achate a curva: os
/// pontos acima do topo são cortados e marcados pelo gráfico.
/// </summary>
public readonly record struct ChartScale(double XMax, double YMax, double YStep, double XStep)
{
    /// <summary>Eixo Y mínimo (ms): mantém as referências de 144 e 240 FPS visíveis mesmo em jogos muito rápidos.</summary>
    public const double MinYMax = 8;
    /// <summary>Teto do eixo Y (ms), salvo quando o jogo inteiro roda mais lento que isso.</summary>
    public const double MaxYMax = 100;
    /// <summary>Duração mostrada quando não há dados (s).</summary>
    public const double EmptyXMax = 60;

    public static IReadOnlyList<FrametimeReference> References { get; } =
    [
        new(1000.0 / 240, "240 FPS"),
        new(1000.0 / 144, "144 FPS"),
        new(1000.0 / 60, "60 FPS"),
    ];

    /// <summary>Escala que acomoda uma ou duas curvas no mesmo eixo.</summary>
    public static ChartScale For(IReadOnlyList<TracePoint>? primary, IReadOnlyList<TracePoint>? secondary = null)
    {
        var values = new List<double>((primary?.Count ?? 0) + (secondary?.Count ?? 0));
        var xMax = 0.0;
        foreach (var series in new[] { primary, secondary })
        {
            if (series is null) continue;
            foreach (var p in series)
            {
                if (double.IsFinite(p.Seconds) && p.Seconds > xMax) xMax = p.Seconds;
                if (double.IsFinite(p.FrametimeMs) && p.FrametimeMs > 0) values.Add(p.FrametimeMs);
            }
        }

        if (xMax <= 0) xMax = values.Count > 0 ? 1 : EmptyXMax;
        var top = 20.0;
        if (values.Count > 0)
        {
            values.Sort();
            var p995 = Percentile(values, 0.995);
            var median = Percentile(values, 0.5);
            var ceiling = Math.Max(MaxYMax, median * 2);
            top = Math.Clamp(Math.Max(p995 * 1.2, median * 1.6), MinYMax, ceiling);
        }

        var yStep = NiceStep(top, 4);
        var yMax = Math.Ceiling(top / yStep - 1e-9) * yStep;
        return new ChartScale(xMax, yMax, yStep, NiceStep(xMax, 6));
    }

    /// <summary>Percentil por interpolação linear numa lista JÁ ORDENADA (q entre 0 e 1).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0) return double.NaN;
        var pos = Math.Clamp(q, 0, 1) * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(sorted.Count - 1, lo + 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>Passo "redondo" (1, 2, 2,5 ou 5 × 10ⁿ) para dividir <paramref name="range"/> em cerca de <paramref name="ticks"/> partes.</summary>
    public static double NiceStep(double range, int ticks)
    {
        if (!double.IsFinite(range) || range <= 0 || ticks <= 0) return 1;
        var raw = range / ticks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var norm = raw / magnitude;
        var nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>Posição horizontal (0..width) de um instante, em segundos.</summary>
    public double X(double seconds, double width) => Math.Clamp(seconds / XMax, 0, 1) * width;

    /// <summary>Posição vertical (0 no topo, height embaixo) de um frametime; acima do topo fica colado no topo.</summary>
    public double Y(double ms, double height) => height - Math.Clamp(ms / YMax, 0, 1) * height;

    public bool IsClipped(double ms) => ms > YMax;

    /// <summary>Valores das linhas de grade horizontais (sem o zero).</summary>
    public IReadOnlyList<double> YGrid()
    {
        var list = new List<double>();
        if (YStep <= 0) return list;
        for (var i = 1; i * YStep <= YMax + 1e-9 && i < 50; i++) list.Add(i * YStep);
        return list;
    }

    /// <summary>Marcas do eixo do tempo, em segundos, a partir do zero.</summary>
    public IReadOnlyList<double> XTicks()
    {
        var list = new List<double>();
        if (XStep <= 0) return list;
        for (var i = 0; i * XStep <= XMax + 1e-9 && i < 50; i++) list.Add(i * XStep);
        return list;
    }

    /// <summary>Referências de FPS que cabem no eixo (abaixo do topo).</summary>
    public IReadOnlyList<FrametimeReference> VisibleReferences()
    {
        var top = YMax;
        return References.Where(r => r.Ms < top).ToList();
    }

    /// <summary>
    /// Janela de faixas FINITAS do histograma que vale desenhar: da primeira à última faixa com frames, com uma de folga de
    /// cada lado e pelo menos <paramref name="minBins"/> faixas. A faixa final "acima de" (ToMs infinito) fica de fora: o
    /// gráfico a desenha à parte. Lista vazia ou sem faixas finitas: (0, -1).
    /// </summary>
    public static (int First, int Last) HistogramWindow(IReadOnlyList<HistogramBin>? bins, int minBins = 10)
    {
        if (bins is null) return (0, -1);
        var finite = 0;
        while (finite < bins.Count && double.IsFinite(bins[finite].ToMs)) finite++;
        if (finite == 0) return (0, -1);
        int first = -1, last = -1;
        for (var i = 0; i < finite; i++)
        {
            if (bins[i].Count <= 0) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return (0, Math.Min(finite, Math.Max(1, minBins)) - 1);
        first = Math.Max(0, first - 1);
        last = Math.Min(finite - 1, last + 1);
        if (last - first + 1 < minBins) last = Math.Min(finite - 1, first + minBins - 1);
        if (last - first + 1 < minBins) first = Math.Max(0, last - minBins + 1);
        return (first, last);
    }
}
