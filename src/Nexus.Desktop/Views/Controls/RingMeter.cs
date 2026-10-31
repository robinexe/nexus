using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Anel de progresso (nota, %, uso). O arco começa às 12 h e enche no sentido horário; a cor vem da faixa
/// (<see cref="WarnAt"/>/<see cref="BadAt"/>, com <see cref="HigherIsBetter"/>) ou de <see cref="Accent"/> quando não há faixas.
/// No centro: o número (AnimatedNumber) e um rótulo curto. Modelo em Themes/Components.xaml.
/// <code>&lt;c:RingMeter Value="{Binding Score}" WarnAt="80" BadAt="50" Label="em ordem" Width="132" Height="132"/&gt;</code>
/// </summary>
public class RingMeter : Control
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingMeter),
        new PropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RingMeter),
        new PropertyMetadata(100d, OnValueChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingMeter),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarnAtProperty = DependencyProperty.Register(nameof(WarnAt), typeof(double), typeof(RingMeter),
        new PropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty BadAtProperty = DependencyProperty.Register(nameof(BadAt), typeof(double), typeof(RingMeter),
        new PropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty HigherIsBetterProperty = DependencyProperty.Register(nameof(HigherIsBetter), typeof(bool), typeof(RingMeter),
        new PropertyMetadata(true, OnValueChanged));

    /// <summary>Cor do arco quando não há faixas (padrão: âmbar, Brush.Trace).</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(RingMeter),
        new PropertyMetadata(null, OnValueChanged));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(RingMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(NumberKind), typeof(RingMeter),
        new PropertyMetadata(NumberKind.Percent));

    /// <summary>Rótulo curto embaixo do número ("em ordem", "uso").</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RingMeter),
        new PropertyMetadata(""));

    private static readonly DependencyPropertyKey ToneBrushKey = DependencyProperty.RegisterReadOnly(nameof(ToneBrush), typeof(Brush), typeof(RingMeter),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ToneBrushProperty = ToneBrushKey.DependencyProperty;

    private static readonly DependencyPropertyKey ToneKey = DependencyProperty.RegisterReadOnly(nameof(Tone), typeof(Tone), typeof(RingMeter),
        new PropertyMetadata(Tone.Neutral));
    public static readonly DependencyProperty ToneProperty = ToneKey.DependencyProperty;

    /// <summary>Fração desenhada agora (anima até a fração do valor).</summary>
    private static readonly DependencyProperty SweepProperty = DependencyProperty.Register("Sweep", typeof(double), typeof(RingMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public RingMeter()
    {
        Focusable = false;
        Loaded += (_, _) => { UpdateTone(); AnimateSweep(fromZero: true); };
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double WarnAt { get => (double)GetValue(WarnAtProperty); set => SetValue(WarnAtProperty, value); }
    public double BadAt { get => (double)GetValue(BadAtProperty); set => SetValue(BadAtProperty, value); }
    public bool HigherIsBetter { get => (bool)GetValue(HigherIsBetterProperty); set => SetValue(HigherIsBetterProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public NumberKind Kind { get => (NumberKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush ToneBrush => (Brush)GetValue(ToneBrushProperty);
    public Tone Tone => (Tone)GetValue(ToneProperty);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (RingMeter)d;
        ring.UpdateTone();
        ring.AnimateSweep(fromZero: false);
    }

    private void UpdateTone()
    {
        var hasBands = double.IsFinite(WarnAt) && double.IsFinite(BadAt);
        var tone = !double.IsFinite(Value) ? Tone.Neutral : hasBands ? DesignMath.ToneFor(Value, WarnAt, BadAt, HigherIsBetter) : Tone.Warn;
        SetValue(ToneKey, tone);
        SetValue(ToneBrushKey, !hasBands && Accent is not null && double.IsFinite(Value) ? Accent : Theme.ToneBrush(tone));
    }

    private void AnimateSweep(bool fromZero)
    {
        var target = DesignMath.Fraction(Value, Maximum);
        var from = fromZero ? 0 : (double)GetValue(SweepProperty);
        BeginAnimation(SweepProperty, null);
        SetValue(SweepProperty, target);
        if (!ShellMotion.Enabled || !IsLoaded || Math.Abs(from - target) < 1e-6) return;
        BeginAnimation(SweepProperty, new DoubleAnimation(from, target, ShellMotion.Count)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        var t = Math.Min(Thickness, size / 4);
        if (!(size > t * 2)) return;
        var r = (size - t) / 2;
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;

        var track = new Pen(Track ?? Theme.Find("Brush.SurfaceHi"), t);
        track.Freeze();
        drawingContext.DrawEllipse(null, track, new Point(cx, cy), r, r);

        var sweep = (double)GetValue(SweepProperty);
        if (sweep <= 0.0005) return;
        var pen = new Pen(ToneBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (sweep >= 0.9995)
        {
            drawingContext.DrawEllipse(null, pen, new Point(cx, cy), r, r);
            return;
        }
        var (x, y, large) = DesignMath.ArcEnd(sweep, r, cx, cy);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy - r), false, false);
            c.ArcTo(new Point(x, y), new Size(r, r), 0, large, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        drawingContext.DrawGeometry(null, pen, g);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.ProgressBar, nameof(RingMeter),
            () => $"{Label}: {DesignMath.Format(Value, Kind)}".TrimStart(':', ' '));
}
