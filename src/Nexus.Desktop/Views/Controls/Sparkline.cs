using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Tendência genérica (compartilhável): linha dos valores, área suave opcional, linha de referência tracejada opcional
/// (<see cref="Baseline"/>, ciano) e o último ponto destacado. Aceita qualquer lista de números (double, int, long…); se a lista
/// for observável, redesenha quando muda.
/// <code>&lt;c:Sparkline Values="{Binding History}" Height="36" Fill="{StaticResource Brush.TraceSoft}"/&gt;</code>
/// (Os gráficos de Medir/Monitor têm os seus próprios, mais completos; este é o pequeno de cartão.)
/// </summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(nameof(Baseline), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.6d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Baseline { get => (double)GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= s.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged now) now.CollectionChanged += s.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 32 : availableSize.Height);

    private List<double> Numbers()
    {
        var list = new List<double>();
        if (Values is null) return list;
        foreach (var o in Values)
        {
            var v = o switch
            {
                double x => x,
                float x => x,
                int x => x,
                long x => x,
                decimal x => (double)x,
                _ => double.NaN,
            };
            if (double.IsFinite(v)) list.Add(v);
        }
        return list;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var v = Numbers();
        if (v.Count < 2 || !(ActualWidth > 4) || !(ActualHeight > 4)) return;
        var min = v.Min();
        var max = v.Max();
        if (double.IsFinite(Baseline)) { min = Math.Min(min, Baseline); max = Math.Max(max, Baseline); }
        var span = max - min;
        if (span < 1e-9) span = Math.Max(1, Math.Abs(max) * 0.1);
        const double pad = 3;
        var w = ActualWidth - 2 * pad;
        var h = ActualHeight - 2 * pad;
        Point P(int i) => new(pad + i * w / (v.Count - 1), pad + (1 - (v[i] - min) / span) * h);

        if (double.IsFinite(Baseline))
        {
            var y = pad + (1 - (Baseline - min) / span) * h;
            var dash = new Pen(Theme.Find("Brush.Signal"), 1) { DashStyle = new DashStyle([2, 2], 0) };
            dash.Freeze();
            drawingContext.DrawLine(dash, new Point(pad, y), new Point(pad + w, y));
        }

        var stroke = Stroke ?? Theme.Find("Brush.Trace");
        if (Fill is not null)
        {
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(pad, pad + h), true, true);
                for (var i = 0; i < v.Count; i++) c.LineTo(P(i), false, true);
                c.LineTo(new Point(pad + w, pad + h), false, true);
            }
            area.Freeze();
            drawingContext.DrawGeometry(Fill, null, area);
        }

        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (var i = 1; i < v.Count; i++) c.LineTo(P(i), true, true);
        }
        line.Freeze();
        var pen = new Pen(stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, line);
        drawingContext.DrawEllipse(stroke, null, P(v.Count - 1), 2.6, 2.6);
    }
}
