namespace Nexus.DiskTools.Analysis;

/// <summary>Retângulo do treemap (unidades da área pedida; sem dependência de WPF).</summary>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Treemap "squarified" (Bruls, Huizing e van Wijk, 2000): divide um retângulo em retângulos de área proporcional a cada
/// tamanho, preenchendo faixas ao longo do lado menor e mantendo a proporção dos retângulos o mais perto possível de 1
/// (fácil de ler e de clicar). Puro e determinístico.
/// </summary>
public static class Treemap
{
    /// <summary>
    /// Calcula o layout. Devolve um retângulo por tamanho, NA ORDEM DE ENTRADA. Tamanhos zero, negativos ou não finitos
    /// recebem um retângulo vazio na origem da área. Os retângulos cobrem a área inteira (quando há algum tamanho positivo) sem
    /// sobreposição.
    /// </summary>
    public static IReadOnlyList<TreemapRect> Squarify(IReadOnlyList<double> sizes, TreemapRect bounds)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var result = new TreemapRect[sizes.Count];
        for (var i = 0; i < result.Length; i++) result[i] = new TreemapRect(bounds.X, bounds.Y, 0, 0);
        if (!(bounds.Width > 0) || !(bounds.Height > 0)) return result;

        var order = Enumerable.Range(0, sizes.Count)
            .Where(i => double.IsFinite(sizes[i]) && sizes[i] > 0)
            .OrderByDescending(i => sizes[i]).ThenBy(i => i)
            .ToList();
        if (order.Count == 0) return result;

        var total = order.Sum(i => sizes[i]);
        var scale = bounds.Width * bounds.Height / total;
        var areas = order.Select(i => sizes[i] * scale).ToList();

        var free = bounds;
        var start = 0;
        while (start < order.Count)
        {
            var side = Math.Min(free.Width, free.Height);
            // Cresce a faixa enquanto a pior proporção melhora.
            var end = start + 1;
            var rowSum = areas[start];
            var worst = Worst(areas, start, end, rowSum, side);
            while (end < order.Count)
            {
                var nextSum = rowSum + areas[end];
                var nextWorst = Worst(areas, start, end + 1, nextSum, side);
                if (nextWorst > worst) break;
                worst = nextWorst;
                rowSum = nextSum;
                end++;
            }

            var last = end == order.Count;
            free = LayoutRow(areas, order, start, end, rowSum, free, result, last);
            start = end;
        }
        return result;
    }

    /// <summary>Pior proporção (maior/menor lado) de uma faixa com estas áreas ao longo de um lado de comprimento <paramref name="side"/>.</summary>
    private static double Worst(List<double> areas, int start, int end, double sum, double side)
    {
        var max = double.MinValue;
        var min = double.MaxValue;
        for (var i = start; i < end; i++)
        {
            max = Math.Max(max, areas[i]);
            min = Math.Min(min, areas[i]);
        }
        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    private static TreemapRect LayoutRow(List<double> areas, List<int> order, int start, int end, double rowSum, TreemapRect free,
        TreemapRect[] result, bool lastRow)
    {
        var horizontalStrip = free.Width >= free.Height; // faixa vertical à esquerda quando a área é mais larga que alta
        if (horizontalStrip)
        {
            // Faixa ocupa a altura toda; largura = soma/altura. A última faixa fecha exatamente a área (sem sobra por arredondamento).
            var w = lastRow ? free.Width : Math.Min(free.Width, rowSum / free.Height);
            var y = free.Y;
            for (var i = start; i < end; i++)
            {
                var h = i == end - 1 ? free.Bottom - y : areas[i] / w;
                result[order[i]] = new TreemapRect(free.X, y, w, Math.Max(0, h));
                y += h;
            }
            return new TreemapRect(free.X + w, free.Y, Math.Max(0, free.Width - w), free.Height);
        }
        else
        {
            var h = lastRow ? free.Height : Math.Min(free.Height, rowSum / free.Width);
            var x = free.X;
            for (var i = start; i < end; i++)
            {
                var w = i == end - 1 ? free.Right - x : areas[i] / h;
                result[order[i]] = new TreemapRect(x, free.Y, Math.Max(0, w), h);
                x += w;
            }
            return new TreemapRect(free.X, free.Y + h, free.Width, Math.Max(0, free.Height - h));
        }
    }
}
