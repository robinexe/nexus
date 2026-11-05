using System.Windows;
using System.Windows.Controls;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Dois blocos lado a lado (na proporção <see cref="Ratio"/> para o primeiro) quando cabem; abaixo de <see cref="StackBelow"/> px
/// de largura, um embaixo do outro. Para a janela no mínimo (980 px) não espremer o Início e o Otimizar.
/// <code>&lt;c:TwoUpPanel Ratio="0.42" Gap="24" StackBelow="860"&gt;…dois filhos…&lt;/c:TwoUpPanel&gt;</code>
/// </summary>
public class TwoUpPanel : Panel
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(nameof(Ratio), typeof(double), typeof(TwoUpPanel),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TwoUpPanel),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StackBelowProperty = DependencyProperty.Register(nameof(StackBelow), typeof(double), typeof(TwoUpPanel),
        new FrameworkPropertyMetadata(860d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double StackBelow { get => (double)GetValue(StackBelowProperty); set => SetValue(StackBelowProperty, value); }

    private bool Stacked(double width) => !double.IsFinite(width) || width < StackBelow;

    private (double First, double Second) Widths(double width)
    {
        var usable = Math.Max(0, width - Gap);
        var first = Math.Round(usable * Math.Clamp(Ratio, 0.1, 0.9));
        return (first, usable - first);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var kids = InternalChildren.Cast<UIElement>().Take(2).ToList();
        if (kids.Count == 0) return default;
        var width = availableSize.Width;
        if (kids.Count == 1 || Stacked(width))
        {
            double h = 0, w = 0;
            foreach (var k in kids) { k.Measure(new Size(width, double.PositiveInfinity)); h += k.DesiredSize.Height; w = Math.Max(w, k.DesiredSize.Width); }
            return new Size(double.IsFinite(width) ? width : w, h + (kids.Count - 1) * Gap);
        }
        var (a, b) = Widths(width);
        kids[0].Measure(new Size(a, double.PositiveInfinity));
        kids[1].Measure(new Size(b, double.PositiveInfinity));
        return new Size(width, Math.Max(kids[0].DesiredSize.Height, kids[1].DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kids = InternalChildren.Cast<UIElement>().Take(2).ToList();
        if (kids.Count == 0) return finalSize;
        if (kids.Count == 1 || Stacked(finalSize.Width))
        {
            double y = 0;
            foreach (var k in kids) { k.Arrange(new Rect(0, y, finalSize.Width, k.DesiredSize.Height)); y += k.DesiredSize.Height + Gap; }
            return finalSize;
        }
        var (a, b) = Widths(finalSize.Width);
        kids[0].Arrange(new Rect(0, 0, a, finalSize.Height));
        kids[1].Arrange(new Rect(a + Gap, 0, b, finalSize.Height));
        return finalSize;
    }
}
