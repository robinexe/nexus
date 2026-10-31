using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>Formato do esqueleto de carregamento.</summary>
public enum SkeletonShape
{
    /// <summary>Linhas de texto de larguras variadas.</summary>
    Lines,
    /// <summary>Cartões lado a lado (<see cref="SkeletonLoader.Columns"/>).</summary>
    Tiles,
    /// <summary>Número grande + duas linhas (lugar do número do HeroPanel).</summary>
    Hero,
}

/// <summary>
/// Blocos cinza pulsando enquanto algo carrega (troca o "Lendo…"). O pulso só roda enquanto o controle está visível e as
/// animações do Windows estão ligadas; senão os blocos ficam parados. Mostre com Visibility ligada ao "carregando".
/// <code>&lt;c:SkeletonLoader Shape="Tiles" Columns="4" Height="120"
///     Visibility="{Binding HasData, Converter={StaticResource FalseToVisible}}"/&gt;</code>
/// </summary>
public class SkeletonLoader : FrameworkElement
{
    private static readonly double[] Widths = [1, 0.86, 0.62, 0.92, 0.7, 0.8];

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(nameof(Shape), typeof(SkeletonShape), typeof(SkeletonLoader),
        new FrameworkPropertyMetadata(SkeletonShape.Lines, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(nameof(Lines), typeof(int), typeof(SkeletonLoader),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(SkeletonLoader),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender));

    public SkeletonLoader()
    {
        IsVisibleChanged += (_, _) => UpdatePulse();
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => BeginAnimation(OpacityProperty, null);
    }

    public SkeletonShape Shape { get => (SkeletonShape)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public int Lines { get => (int)GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    private void UpdatePulse()
    {
        BeginAnimation(OpacityProperty, null);
        if (!IsVisible || !ShellMotion.Enabled) { Opacity = 1; return; }
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        var h = Shape switch
        {
            SkeletonShape.Lines => Math.Max(1, Lines) * 22 - 10,
            SkeletonShape.Hero => 92,
            _ => 120,
        };
        return new Size(w, double.IsInfinity(availableSize.Height) ? h : Math.Min(h, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var fill = Theme.Find("Brush.SurfaceHi");
        var w = ActualWidth;
        var h = ActualHeight;
        if (!(w > 0) || !(h > 0)) return;
        switch (Shape)
        {
            case SkeletonShape.Hero:
                drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, 0, Math.Min(w, 220), 48), 6, 6);
                drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, 60, w * 0.8, 12), 4, 4);
                drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, 80, w * 0.5, 12), 4, 4);
                break;
            case SkeletonShape.Tiles:
                var n = Math.Max(1, Columns);
                const double gap = 14;
                var tw = (w - gap * (n - 1)) / n;
                var line = Theme.Find("Brush.Line");
                for (var i = 0; i < n; i++)
                {
                    var x = i * (tw + gap);
                    drawingContext.DrawRoundedRectangle(Theme.Find("Brush.Surface"), new Pen(line, 1), new Rect(x + 0.5, 0.5, tw - 1, h - 1), 8, 8);
                    drawingContext.DrawRoundedRectangle(fill, null, new Rect(x + 16, 16, 28, 28), 14, 14);
                    drawingContext.DrawRoundedRectangle(fill, null, new Rect(x + 16, Math.Min(h - 40, 56), tw * 0.5, 18), 4, 4);
                    drawingContext.DrawRoundedRectangle(fill, null, new Rect(x + 16, Math.Min(h - 16, 82), tw * 0.7, 10), 4, 4);
                }
                break;
            default:
                for (var i = 0; i < Math.Max(1, Lines); i++)
                {
                    var y = i * 22;
                    if (y + 12 > h) break;
                    drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, y, w * Widths[i % Widths.Length], 12), 4, 4);
                }
                break;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.ProgressBar, nameof(SkeletonLoader), () => "Carregando");
}
