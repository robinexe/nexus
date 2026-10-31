using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Nexus.Lab.Cpu;

namespace Nexus.Desktop.Views.Modules.CpuLab;

/// <summary>
/// Base dos gráficos do Laboratório do processador: desenho direto em OnRender (sem um elemento por ponto), cores do tema
/// (Brush.*) lidas dos recursos, números em Bahnschrift (Font.Numbers). Sem animação.
/// </summary>
public abstract class CpuChartBase : FrameworkElement
{
    protected static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    protected CpuChartBase()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    protected Brush Res(string key, byte r, byte g, byte b) =>
        TryFindResource(key) as Brush ?? Frozen(r, g, b);

    protected Brush Trace => Res("Brush.Trace", 0xFF, 0xB5, 0x47);
    protected Brush Signal => Res("Brush.Signal", 0x4F, 0xD1, 0xE8);
    protected Brush Good => Res("Brush.Good", 0x5F, 0xD0, 0x8A);
    protected Brush Bad => Res("Brush.Bad", 0xF0, 0x5D, 0x5E);
    protected Brush Grid => Res("Brush.Graticule", 0x2A, 0x35, 0x50);
    protected Brush Label => Res("Brush.TextDim", 0x5C, 0x67, 0x85);
    protected Brush Text => Res("Brush.Text", 0xE6, 0xEA, 0xF2);

    protected FontFamily Numbers => TryFindResource("Font.Numbers") as FontFamily ?? new FontFamily("Bahnschrift");

    protected static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    protected static Pen PenOf(Brush brush, double thickness, bool dashed = false)
    {
        var pen = new Pen(brush, thickness);
        if (dashed) pen.DashStyle = new DashStyle([3, 3], 0);
        pen.Freeze();
        return pen;
    }

    protected FormattedText Fmt(string text, Brush brush, double size = 11) =>
        new(text, Pt, FlowDirection.LeftToRight, new Typeface(Numbers, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((UIElement)d).InvalidateVisual();

    protected void Empty(DrawingContext dc, string message)
    {
        var t = Fmt(message, Label, 12);
        dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, (ActualHeight - t.Height) / 2));
    }
}

/// <summary>Curva de latência (ns) × tamanho do conjunto, ambos em escala log, com os degraus medidos e os tamanhos do CPUID.</summary>
public sealed class LatencyCurveChart : CpuChartBase
{
    private const double L = 44, R = 12, T = 12, B = 24;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<LatencyPoint>),
        typeof(LatencyCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty StepsProperty = DependencyProperty.Register(nameof(Steps), typeof(IReadOnlyList<CacheStep>),
        typeof(LatencyCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty DeclaredProperty = DependencyProperty.Register(nameof(Declared), typeof(IReadOnlyList<CacheMarker>),
        typeof(LatencyCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty PreviousProperty = DependencyProperty.Register(nameof(Previous), typeof(IReadOnlyList<LatencyPoint>),
        typeof(LatencyCurveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public IReadOnlyList<LatencyPoint>? Points { get => (IReadOnlyList<LatencyPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<CacheStep>? Steps { get => (IReadOnlyList<CacheStep>?)GetValue(StepsProperty); set => SetValue(StepsProperty, value); }
    /// <summary>Tamanhos declarados pelo CPUID/Windows (linhas tracejadas em ciano).</summary>
    public IReadOnlyList<CacheMarker>? Declared { get => (IReadOnlyList<CacheMarker>?)GetValue(DeclaredProperty); set => SetValue(DeclaredProperty, value); }
    /// <summary>Curva da medição anterior (cinza, por baixo).</summary>
    public IReadOnlyList<LatencyPoint>? Previous { get => (IReadOnlyList<LatencyPoint>?)GetValue(PreviousProperty); set => SetValue(PreviousProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var pts = Points;
        if (pts is not { Count: > 1 }) { Empty(dc, "Meça para ver a curva de latência."); return; }
        double w = ActualWidth - L - R, h = ActualHeight - T - B;
        if (w < 40 || h < 40) return;

        double x0 = Math.Log2(pts[0].Bytes), x1 = Math.Log2(pts[^1].Bytes);
        var all = pts.Concat(Previous ?? []).Where(p => p.Ns > 0).ToList();
        double y0 = Math.Log10(Math.Max(0.3, all.Min(p => p.Ns) * 0.8)), y1 = Math.Log10(all.Max(p => p.Ns) * 1.25);
        double X(double bytes) => L + (Math.Log2(bytes) - x0) / Math.Max(0.001, x1 - x0) * w;
        double Y(double ns) => T + h - (Math.Log10(Math.Max(ns, 0.01)) - y0) / Math.Max(0.001, y1 - y0) * h;

        var grid = PenOf(Grid, 1);
        foreach (var ns in new[] { 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 })
        {
            if (Math.Log10(ns) < y0 || Math.Log10(ns) > y1) continue;
            dc.DrawLine(grid, new Point(L, Y(ns)), new Point(L + w, Y(ns)));
            var t = Fmt(ns.ToString("0.#", Pt) + " ns", Label, 10);
            dc.DrawText(t, new Point(L - t.Width - 6, Y(ns) - t.Height / 2));
        }
        for (var e = (int)Math.Ceiling(x0); e <= (int)x1; e += 2)
        {
            var bytes = Math.Pow(2, e);
            var t = Fmt(CacheStepDetector.Bytes((long)bytes), Label, 10);
            dc.DrawText(t, new Point(X(bytes) - t.Width / 2, T + h + 6));
        }

        foreach (var m in Declared ?? [])
        {
            if (m.Bytes < pts[0].Bytes || m.Bytes > pts[^1].Bytes) continue;
            var x = X(m.Bytes);
            dc.DrawLine(PenOf(Signal, 1, dashed: true), new Point(x, T), new Point(x, T + h));
            var t = Fmt(m.Label, Signal, 10);
            dc.DrawText(t, new Point(Math.Min(x + 3, L + w - t.Width), T));
        }

        if (Previous is { Count: > 1 } prev) DrawLine(dc, prev, X, Y, PenOf(Label, 1.2));
        DrawLine(dc, pts, X, Y, PenOf(Trace, 2));

        foreach (var s in Steps ?? [])
        {
            var y = Y(s.Ns);
            var t = Fmt(s.CapacityBytes is { } cap
                ? $"{s.Level} · {s.Ns.ToString(s.Ns < 10 ? "0.0" : "0", Pt)} ns · até ~{CacheStepDetector.Bytes(cap)}"
                : $"{s.Level} · {s.Ns.ToString("0", Pt)} ns", Text, 11);
            var x = s.CapacityBytes is { } c ? X(c) : L + w;
            dc.DrawEllipse(Trace, null, new Point(Math.Min(x, L + w - 3), y), 3.5, 3.5);
            var tx = Math.Clamp(x - t.Width - 6, L + 2, L + w - t.Width);
            dc.DrawText(t, new Point(tx, Math.Max(T, y - t.Height - 4)));
        }
    }

    private static void DrawLine(DrawingContext dc, IReadOnlyList<LatencyPoint> pts, Func<double, double> x, Func<double, double> y, Pen pen)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x(pts[0].Bytes), y(pts[0].Ns)), false, false);
            for (var i = 1; i < pts.Count; i++) ctx.LineTo(new Point(x(pts[i].Bytes), y(pts[i].Ns)), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

public sealed record CacheMarker(string Label, long Bytes);

/// <summary>Mapa de calor da latência núcleo-a-núcleo: verde = perto, âmbar/vermelho = longe. O número de cada célula em ns.</summary>
public sealed class CoreMatrixHeatmap : CpuChartBase
{
    public static readonly DependencyProperty MatrixProperty = DependencyProperty.Register(nameof(Matrix), typeof(CoreToCoreMatrix),
        typeof(CoreMatrixHeatmap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));

    public CoreToCoreMatrix? Matrix { get => (CoreToCoreMatrix?)GetValue(MatrixProperty); set => SetValue(MatrixProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var n = Matrix?.Cores.Count ?? 0;
        var side = n == 0 ? 120 : Math.Min(double.IsInfinity(availableSize.Width) ? 520 : availableSize.Width, 30 + n * 40.0);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        var m = Matrix;
        if (m is null || m.Cores.Count < 2) { Empty(dc, "Precisa de 2+ núcleos medidos."); return; }
        var n = m.Cores.Count;
        const double gutter = 28;
        var cell = Math.Max(8, (Math.Min(ActualWidth, ActualHeight) - gutter) / n);
        var values = new List<double>();
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) if (m.At(i, j) is { } v) values.Add(v);
        if (values.Count == 0) { Empty(dc, "Sem pares medidos."); return; }
        double lo = values.Min(), hi = Math.Max(values.Max(), lo * 1.01);
        var good = (Good as SolidColorBrush)?.Color ?? Color.FromRgb(0x5F, 0xD0, 0x8A);
        var warm = (Trace as SolidColorBrush)?.Color ?? Color.FromRgb(0xFF, 0xB5, 0x47);
        var hot = (Bad as SolidColorBrush)?.Color ?? Color.FromRgb(0xF0, 0x5D, 0x5E);
        var showNumbers = cell >= 30;

        for (var i = 0; i < n; i++)
        {
            var lbl = Fmt((m.Cores[i] + 1).ToString(Pt), Label, 10);
            dc.DrawText(lbl, new Point(gutter + i * cell + (cell - lbl.Width) / 2, 6));
            dc.DrawText(lbl, new Point(gutter - lbl.Width - 6, gutter + i * cell + (cell - lbl.Height) / 2));
            for (var j = 0; j < n; j++)
            {
                var rect = new Rect(gutter + j * cell + 1, gutter + i * cell + 1, cell - 2, cell - 2);
                if (m.At(i, j) is not { } v)
                {
                    dc.DrawRectangle(i == j ? Grid : null, null, rect);
                    continue;
                }
                var f = (v - lo) / (hi - lo);
                var c = f < 0.5 ? Lerp(good, warm, f * 2) : Lerp(warm, hot, (f - 0.5) * 2);
                var brush = new SolidColorBrush(Color.FromArgb(0xD0, c.R, c.G, c.B));
                brush.Freeze();
                dc.DrawRectangle(brush, null, rect);
                if (showNumbers)
                {
                    var t = Fmt(v.ToString("0", Pt), Frozen(0x0E, 0x13, 0x20), 11);
                    dc.DrawText(t, new Point(rect.X + (rect.Width - t.Width) / 2, rect.Y + (rect.Height - t.Height) / 2));
                }
            }
        }
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

/// <summary>Frequência média de todos os núcleos ao longo do teste sustentado, com a medição anterior por baixo.</summary>
public sealed class FrequencyTimeChart : CpuChartBase
{
    private const double L = 40, R = 10, T = 10, B = 22;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<FrequencySample>),
        typeof(FrequencyTimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty PreviousProperty = DependencyProperty.Register(nameof(Previous), typeof(IReadOnlyList<FrequencySample>),
        typeof(FrequencyTimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));
    public static readonly DependencyProperty BaseGhzProperty = DependencyProperty.Register(nameof(BaseGhz), typeof(double),
        typeof(FrequencyTimeChart), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public IReadOnlyList<FrequencySample>? Series { get => (IReadOnlyList<FrequencySample>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<FrequencySample>? Previous { get => (IReadOnlyList<FrequencySample>?)GetValue(PreviousProperty); set => SetValue(PreviousProperty, value); }
    /// <summary>Frequência base anunciada (linha de referência tracejada). 0 = não desenhar.</summary>
    public double BaseGhz { get => (double)GetValue(BaseGhzProperty); set => SetValue(BaseGhzProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        var s = Series;
        if (s is not { Count: > 1 }) { Empty(dc, "Rode o teste sustentado para ver a frequência no tempo."); return; }
        double w = ActualWidth - L - R, h = ActualHeight - T - B;
        if (w < 40 || h < 30) return;
        var all = s.Concat(Previous ?? []).ToList();
        var maxT = all.Max(p => p.Seconds);
        var lo = Math.Floor(Math.Min(all.Min(p => p.Ghz), BaseGhz > 0 ? BaseGhz : double.MaxValue) * 2 - 1) / 2;
        var hi = Math.Ceiling(all.Max(p => p.Ghz) * 2 + 0.5) / 2;
        lo = Math.Max(0, lo);
        double X(double t) => L + t / Math.Max(0.001, maxT) * w;
        double Y(double g) => T + h - (g - lo) / Math.Max(0.001, hi - lo) * h;

        var grid = PenOf(Grid, 1);
        for (var g = lo; g <= hi + 1e-9; g += 0.5)
        {
            dc.DrawLine(grid, new Point(L, Y(g)), new Point(L + w, Y(g)));
            var t = Fmt(g.ToString("0.0", Pt), Label, 10);
            dc.DrawText(t, new Point(L - t.Width - 6, Y(g) - t.Height / 2));
        }
        var step = maxT > 40 ? 10 : maxT > 12 ? 5 : 1;
        for (var sec = 0; sec <= maxT; sec += step)
        {
            var t = Fmt(sec + " s", Label, 10);
            dc.DrawText(t, new Point(X(sec) - t.Width / 2, T + h + 5));
        }
        if (BaseGhz > 0 && BaseGhz >= lo && BaseGhz <= hi)
        {
            dc.DrawLine(PenOf(Signal, 1, dashed: true), new Point(L, Y(BaseGhz)), new Point(L + w, Y(BaseGhz)));
            dc.DrawText(Fmt("base", Signal, 10), new Point(L + 4, Y(BaseGhz) - 14));
        }
        if (Previous is { Count: > 1 } prev) Line(dc, prev, X, Y, PenOf(Label, 1.2));
        Line(dc, s, X, Y, PenOf(Trace, 2));
    }

    private static void Line(DrawingContext dc, IReadOnlyList<FrequencySample> s, Func<double, double> x, Func<double, double> y, Pen pen)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x(s[0].Seconds), y(s[0].Ghz)), false, false);
            for (var i = 1; i < s.Count; i++) ctx.LineTo(new Point(x(s[i].Seconds), y(s[i].Ghz)), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
