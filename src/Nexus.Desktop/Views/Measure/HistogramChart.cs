using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Nexus.Measurement;

namespace Nexus.Desktop.Views.Measure;

/// <summary>
/// Histograma de frametime pequeno: uma barra por faixa de 1 ms, só na janela ocupada (<see cref="ChartScale.HistogramWindow"/>),
/// mais uma barra à parte para "acima de 50 ms" quando houver. Torre estreita à esquerda = jogo liso; cauda longa = engasgos.
/// </summary>
public sealed class HistogramChart : FrameworkElement
{
    private const double BottomGutter = 16, OverflowGap = 6;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly DependencyProperty BinsProperty = DependencyProperty.Register(nameof(Bins), typeof(IReadOnlyList<HistogramBin>),
        typeof(HistogramChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(HistogramChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Cor da barra "acima de 50 ms" (engasgos fortes).</summary>
    public static readonly DependencyProperty OverflowFillProperty = DependencyProperty.Register(nameof(OverflowFill), typeof(Brush), typeof(HistogramChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(HistogramChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<HistogramBin>? Bins { get => (IReadOnlyList<HistogramBin>?)GetValue(BinsProperty); set => SetValue(BinsProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? OverflowFill { get => (Brush?)GetValue(OverflowFillProperty); set => SetValue(OverflowFillProperty, value); }
    public Brush? LabelBrush { get => (Brush?)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 80 : Math.Min(availableSize.Height, 80));

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var bins = Bins;
        var (first, last) = ChartScale.HistogramWindow(bins);
        if (bins is null || last < first) return;
        var overflow = bins[^1] is { Count: > 0 } o && double.IsInfinity(o.ToMs) ? o : null;

        var slots = last - first + 1;
        if (!(ActualWidth >= slots + 2 * OverflowGap) || !(ActualHeight - BottomGutter >= 10)) return; // Rect negativo lançaria exceção
        var plot = new Rect(0, 0, ActualWidth, ActualHeight - BottomGutter);
        var barsWidth = overflow is null ? plot.Width : plot.Width - OverflowGap;
        var slot = barsWidth / (slots + (overflow is null ? 0 : 1));

        var max = 1;
        for (var i = first; i <= last; i++) max = Math.Max(max, bins[i].Count);
        if (overflow is not null) max = Math.Max(max, overflow.Count);

        var fill = Fill ?? Brushes.Orange;
        var labelBrush = LabelBrush ?? Brushes.Gray;
        var gap = slot >= 4 ? 1 : 0;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        for (var i = first; i <= last; i++)
        {
            var count = bins[i].Count;
            if (count <= 0) continue;
            var h = Math.Max(1.5, count / (double)max * plot.Height);
            drawingContext.DrawRectangle(fill, null, new Rect((i - first) * slot, plot.Bottom - h, Math.Max(1, slot - gap), h));
        }
        if (overflow is not null)
        {
            var h = Math.Max(1.5, overflow.Count / (double)max * plot.Height);
            drawingContext.DrawRectangle(OverflowFill ?? fill, null, new Rect(slots * slot + OverflowGap, plot.Bottom - h, Math.Max(1, slot - gap), h));
        }

        // Rótulos: algumas marcas em ms embaixo, e "50+" sob a barra à parte.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("Font.Numbers") as FontFamily ?? new FontFamily("Bahnschrift SemiCondensed"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var every = Math.Max(1, (int)Math.Ceiling(slots / 5.0));
        var lastRight = double.NegativeInfinity;
        for (var i = first; i <= last + 1; i += every)
        {
            var ms = i <= last ? bins[i].FromMs : bins[last].ToMs;
            var text = new FormattedText(ms.ToString("0", Pt) + (i == first ? " ms" : ""), Pt, FlowDirection.LeftToRight, typeface, 10.5, labelBrush, dpi);
            var x = Math.Clamp((i - first) * slot - (i == first ? 0 : text.Width / 2), 0, Math.Max(0, slots * slot - text.Width));
            if (x < lastRight + 4) continue;
            drawingContext.DrawText(text, new Point(x, plot.Bottom + 2));
            lastRight = x + text.Width;
        }
        if (overflow is not null)
        {
            var text = new FormattedText(overflow.FromMs.ToString("0", Pt) + "+", Pt, FlowDirection.LeftToRight, typeface, 10.5, labelBrush, dpi);
            drawingContext.DrawText(text, new Point(Math.Max(0, ActualWidth - text.Width), plot.Bottom + 2));
        }
    }
}
