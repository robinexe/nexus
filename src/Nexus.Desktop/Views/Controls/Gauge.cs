using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Mostrador de instrumento ("Instrumento de precisão", docs/14-redesign-brief.md): arco de 270° com a boca para baixo, trilho
/// escuro, marcas finas de escala e o valor em ciano (ou na cor de <see cref="Accent"/>). A agulha "assenta" com uma leve
/// ultrapassagem (BackEase) quando o valor muda; com as animações do Windows desligadas, vai direto ao valor. Sem valor (NaN):
/// só o trilho. O número e as frases ficam por cima, no XAML de quem usa.
/// <code>&lt;c:Gauge Value="{Binding Live.CpuPercent}" Width="176" Height="176"/&gt;</code>
/// </summary>
public class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Gauge),
        new PropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Gauge),
        new PropertyMetadata(100d, OnValueChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Cor do arco (padrão: ciano, Brush.Signal — medição).</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(Gauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Nome lido pelo Narrador ("Processador: 23%").</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(Gauge),
        new PropertyMetadata(""));

    private static readonly DependencyProperty SweepProperty = DependencyProperty.Register("Sweep", typeof(double), typeof(Gauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public Gauge()
    {
        Focusable = false;
        SnapsToDevicePixels = false;
        Loaded += (_, _) => Animate(fromZero: true);
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Gauge)d).Animate(fromZero: false);

    private void Animate(bool fromZero)
    {
        var target = DesignMath.Fraction(Value, Maximum);
        if (!double.IsFinite(target)) target = 0;
        var from = fromZero ? 0 : (double)GetValue(SweepProperty);
        BeginAnimation(SweepProperty, null);
        SetValue(SweepProperty, target);
        if (!ShellMotion.Enabled || !IsLoaded || Math.Abs(from - target) < 1e-4) return;
        // Leituras a 1 Hz: a primeira entrada assenta como agulha (600 ms, BackEase); as seguintes deslizam curto (sem tremer).
        var duration = fromZero ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromMilliseconds(450);
        IEasingFunction ease = fromZero ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } : new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(SweepProperty, new DoubleAnimation(from, target, new Duration(duration)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        var t = Math.Min(Thickness, size / 5);
        if (!(size > t * 3)) return;
        var r = (size - t) / 2;
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;

        // Trilho (270°)
        var track = new Pen(Theme.Find("Brush.Track"), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        track.Freeze();
        drawingContext.DrawGeometry(null, track, Arc(1, r, cx, cy));

        // Marcas de escala: 28 traços finos num raio interno (10 em 10 %, a cada 3 um pouco mais forte)
        var tickPen = new Pen(Theme.Find("Brush.LineSoft"), 1);
        tickPen.Freeze();
        var majorPen = new Pen(Theme.Find("Brush.Line"), 1.2);
        majorPen.Freeze();
        var inner = r - t / 2 - 8;
        for (var i = 0; i <= 27; i++)
        {
            var f = i / 27d;
            var major = i % 9 == 0;
            var (x1, y1, _) = DesignMath.GaugePoint(f, inner, cx, cy);
            var (x2, y2, _) = DesignMath.GaugePoint(f, inner - (major ? 7 : 4), cx, cy);
            drawingContext.DrawLine(major ? majorPen : tickPen, new Point(x1, y1), new Point(x2, y2));
        }

        // Valor
        var sweep = (double)GetValue(SweepProperty);
        if (!double.IsFinite(Value) || sweep <= 0.002) return;
        var pen = new Pen(Accent ?? Theme.Find("Brush.Signal"), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, Arc(Math.Min(1, sweep), r, cx, cy));
    }

    private static StreamGeometry Arc(double fraction, double r, double cx, double cy)
    {
        var (sx, sy, _) = DesignMath.GaugePoint(0, r, cx, cy);
        var (ex, ey, large) = DesignMath.GaugePoint(fraction, r, cx, cy);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(sx, sy), false, false);
            c.ArcTo(new Point(ex, ey), new Size(r, r), 0, large, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.ProgressBar, nameof(Gauge),
            () => double.IsFinite(Value) ? $"{Label}: {DesignMath.Format(Value, NumberKind.Percent, 0)}" : Label);
}
