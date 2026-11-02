using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Nexus.Measurement;

namespace Nexus.Desktop.Views.Measure;

/// <summary>
/// O traço de frametime: uma curva fina tipo fósforo de osciloscópio sobre uma grade discreta, com as referências de 60, 144
/// e 240 FPS. Aceita uma segunda curva (a referência, "antes") desenhada por baixo, no mesmo eixo. A curva se desenha da
/// esquerda para a direita quando os dados mudam (se o Windows permite animações). Até ~1500 pontos por curva: cada curva é
/// uma única StreamGeometry congelada num DrawingVisual, não um elemento por ponto. A escala vem de <see cref="ChartScale"/>.
/// </summary>
public sealed class FrametimeChart : FrameworkElement
{
    private const double LeftGutter = 46, RightGutter = 8, TopGutter = 10, BottomGutter = 22;
    private static CultureInfo Pt => Loc.Culture; // idioma da interface (docs/12-idiomas.md)
    private static readonly Duration RevealDuration = new(TimeSpan.FromMilliseconds(600));

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<TracePoint>),
        typeof(FrametimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty SecondaryPointsProperty = DependencyProperty.Register(nameof(SecondaryPoints), typeof(IReadOnlyList<TracePoint>),
        typeof(FrametimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(FrametimeChart),
        new FrameworkPropertyMetadata(Frozen(0xFF, 0xB5, 0x47), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(nameof(SecondaryStroke), typeof(Brush), typeof(FrametimeChart),
        new FrameworkPropertyMetadata(Frozen(0x4F, 0xD1, 0xE8), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(FrametimeChart),
        new FrameworkPropertyMetadata(Frozen(0x2A, 0x35, 0x50), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(FrametimeChart),
        new FrameworkPropertyMetadata(Frozen(0x5C, 0x67, 0x85), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gráfico ao vivo (atualiza várias vezes por segundo): sem a animação de revelar a curva.</summary>
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(nameof(IsLive), typeof(bool), typeof(FrametimeChart),
        new FrameworkPropertyMetadata(false));

    private readonly DrawingVisual _secondary = new();
    private readonly DrawingVisual _primary = new();
    private readonly ScaleTransform _reveal = new(1, 1);
    private ChartScale _scale = ChartScale.For(null);
    private Color? _glowColor;
    private bool _revealPending;

    public FrametimeChart()
    {
        AddVisualChild(_secondary);
        AddVisualChild(_primary);
        SnapsToDevicePixels = true;
        Loaded += (_, _) =>
        {
            if (!_revealPending) return;
            _revealPending = false;
            StartReveal();
        };
    }

    public IReadOnlyList<TracePoint>? Points
    {
        get => (IReadOnlyList<TracePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Curva de referência ("antes"), desenhada por baixo da principal e sem brilho.</summary>
    public IReadOnlyList<TracePoint>? SecondaryPoints
    {
        get => (IReadOnlyList<TracePoint>?)GetValue(SecondaryPointsProperty);
        set => SetValue(SecondaryPointsProperty, value);
    }

    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? SecondaryStroke { get => (Brush?)GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush? LabelBrush { get => (Brush?)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _secondary,
        1 => _primary,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (FrametimeChart)d;
        chart._scale = ChartScale.For(chart.Points, chart.SecondaryPoints);
        if (e.NewValue is IReadOnlyList<TracePoint> { Count: > 1 } && !chart.IsLive) chart.StartReveal();
    }

    /// <summary>Revela a curva da esquerda para a direita (~600 ms). Sem animação quando o Windows pede menos movimento.</summary>
    private void StartReveal()
    {
        if (!IsLoaded && SystemParameters.ClientAreaAnimation)
        {
            // Ainda não está na tela (página abrindo): esconde a curva e revela quando carregar.
            _reveal.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _reveal.ScaleX = 0;
            _revealPending = true;
            return;
        }
        if (!SystemParameters.ClientAreaAnimation)
        {
            _reveal.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _reveal.ScaleX = 1;
            return;
        }
        var animation = new DoubleAnimation(0, 1, RevealDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        animation.Freeze();
        _reveal.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 200 : Math.Min(availableSize.Height, 200));

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var plotWidth = ActualWidth - LeftGutter - RightGutter;
        var plotHeight = ActualHeight - TopGutter - BottomGutter;
        if (!(plotWidth >= 20) || !(plotHeight >= 20)) // também cobre NaN; Rect com tamanho negativo lançaria exceção
        {
            using (_primary.RenderOpen()) { }
            using (_secondary.RenderOpen()) { }
            return;
        }
        var plot = new Rect(LeftGutter, TopGutter, plotWidth, plotHeight);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("Font.Numbers") as FontFamily ?? new FontFamily("Bahnschrift SemiCondensed"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var gridBrush = GridBrush ?? Brushes.DimGray;
        var labelBrush = LabelBrush ?? Brushes.Gray;

        // Fundo transparente que pega o mouse (para dicas de ferramenta no elemento inteiro).
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var gridPen = new Pen(gridBrush, 1);
        gridPen.Freeze();
        var gridDim = new Pen(gridBrush, 1) { DashStyle = new DashStyle([1, 3], 0) };
        gridDim.Freeze();

        // Grade horizontal com rótulos em ms.
        foreach (var v in _scale.YGrid())
        {
            var y = Snap(plot.Top + _scale.Y(v, plot.Height));
            drawingContext.DrawLine(gridDim, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(v.ToString("0.#", Pt) + " ms", typeface, 11, labelBrush, dpi);
            drawingContext.DrawText(label, new Point(plot.Left - 8 - label.Width, y - label.Height / 2));
        }

        // Linha de base e marcas de tempo.
        var baseY = Snap(plot.Bottom);
        drawingContext.DrawLine(gridPen, new Point(plot.Left, baseY), new Point(plot.Right, baseY));
        foreach (var t in _scale.XTicks())
        {
            var x = Snap(plot.Left + _scale.X(t, plot.Width));
            drawingContext.DrawLine(gridPen, new Point(x, baseY), new Point(x, baseY + 4));
            var label = Text(t.ToString("0.#", Pt) + " s", typeface, 11, labelBrush, dpi);
            var lx = Math.Max(plot.Left - 4, Math.Min(x - label.Width / 2, ActualWidth - label.Width)); // sem Math.Clamp: min > max lançaria
            drawingContext.DrawText(label, new Point(lx, baseY + 5));
        }

        // Referências de FPS (tracejado, rótulo à direita, acima da linha).
        var refPen = new Pen(labelBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
        refPen.Freeze();
        foreach (var r in _scale.VisibleReferences())
        {
            var y = Snap(plot.Top + _scale.Y(r.Ms, plot.Height));
            drawingContext.PushOpacity(0.55);
            drawingContext.DrawLine(refPen, new Point(plot.Left, y), new Point(plot.Right, y));
            drawingContext.Pop();
            var label = Text(r.Label, typeface, 10.5, labelBrush, dpi);
            drawingContext.DrawText(label, new Point(plot.Right - label.Width - 2, y - label.Height - 1));
        }

        _reveal.CenterX = plot.Left;
        var clip = new Rect(plot.Left, plot.Top - 8, plot.Width, plot.Height + 10);
        DrawTrace(_secondary, SecondaryPoints, SecondaryStroke ?? Brushes.Cyan, plot, clip, 1.1, 0.85);
        DrawTrace(_primary, Points, Stroke ?? Brushes.Orange, plot, clip, 1.4, 1);
        UpdateGlow();
    }

    private void DrawTrace(DrawingVisual visual, IReadOnlyList<TracePoint>? points, Brush brush, Rect plot, Rect clip, double thickness, double opacity)
    {
        using var dc = visual.RenderOpen();
        visual.Clip = new RectangleGeometry(clip) { Transform = _reveal };
        if (points is null || points.Count == 0) return;

        var line = new StreamGeometry();
        var spikes = new StreamGeometry();
        var lastSpikeX = double.NegativeInfinity;
        var any = false;
        using (var g = line.Open())
        using (var s = spikes.Open())
        {
            var started = false;
            foreach (var p in points)
            {
                if (!double.IsFinite(p.Seconds) || !double.IsFinite(p.FrametimeMs)) continue;
                var pt = new Point(plot.Left + _scale.X(p.Seconds, plot.Width), plot.Top + _scale.Y(p.FrametimeMs, plot.Height));
                if (!started) { g.BeginFigure(pt, false, false); started = true; }
                else g.LineTo(pt, true, true);
                if (_scale.IsClipped(p.FrametimeMs) && pt.X - lastSpikeX >= 4)
                {
                    // Pico acima do topo do eixo: pequeno triângulo apontando para cima, colado no topo.
                    s.BeginFigure(new Point(pt.X, plot.Top - 6), true, true);
                    s.LineTo(new Point(pt.X + 3.5, plot.Top - 0.5), true, false);
                    s.LineTo(new Point(pt.X - 3.5, plot.Top - 0.5), true, false);
                    lastSpikeX = pt.X;
                    any = true;
                }
            }
            if (!started) return;
        }
        line.Freeze();
        spikes.Freeze();

        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pen.CanFreeze) pen.Freeze();
        if (opacity < 1) dc.PushOpacity(opacity);
        dc.DrawGeometry(null, pen, line);
        if (any) dc.DrawGeometry(brush, null, spikes);
        if (opacity < 1) dc.Pop();
    }

    /// <summary>Brilho de fósforo só na curva principal: sombra sem deslocamento, da mesma cor, bem suave.</summary>
    private void UpdateGlow()
    {
        var color = (Stroke as SolidColorBrush)?.Color;
        if (color == _glowColor) return;
        _glowColor = color;
        _primary.Effect = color is { } c ? new DropShadowEffect { Color = c, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.6, RenderingBias = RenderingBias.Performance } : null;
    }

    private static double Snap(double v) => Math.Round(v) + 0.5;

    private static FormattedText Text(string s, Typeface typeface, double size, Brush brush, double dpi) =>
        new(s, Pt, FlowDirection.LeftToRight, typeface, size, brush, dpi);
}
