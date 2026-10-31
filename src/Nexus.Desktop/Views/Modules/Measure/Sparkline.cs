using System.Windows;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Modules.Measure;

/// <summary>Tendência minúscula (uma linha por sessão, da mais antiga para a mais nova), com o último ponto destacado.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 90 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 24 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var v = Values?.Where(double.IsFinite).ToList();
        if (v is null || v.Count < 2 || !(ActualWidth > 4) || !(ActualHeight > 4)) return;
        var min = v.Min();
        var max = v.Max();
        var span = max - min;
        if (span < 1e-9) span = Math.Max(1, Math.Abs(max) * 0.1);
        var pad = 3.0;
        Point P(int i) => new(pad + i * (ActualWidth - 2 * pad) / (v.Count - 1), pad + (1 - (v[i] - min) / span) * (ActualHeight - 2 * pad));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (var i = 1; i < v.Count; i++) c.LineTo(P(i), true, true);
        }
        g.Freeze();
        var brush = Stroke ?? Brushes.Orange;
        var pen = new Pen(brush, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, g);
        drawingContext.DrawEllipse(brush, null, P(v.Count - 1), 2.5, 2.5);
    }
}
