using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Anel grande da tela de progresso (estilo painel de telemetria): marcas em volta que acendem com o andamento, arco âmbar
/// com brilho pulsando, um ponto na ponta e um arco ciano fino girando por dentro enquanto <see cref="IsActive"/>.
/// O número no centro fica por conta de quem usa (um AnimatedNumber por cima). Com as animações do Windows desligadas, nada
/// gira nem pulsa: o arco vai direto ao valor.
/// <code>&lt;c:ProcessRing Value="{Binding Percent}" IsActive="{Binding IsRunning}" Tone="{Binding RingTone}" Width="230" Height="230"/&gt;</code>
/// </summary>
public class ProcessRing : FrameworkElement
{
    public const int TickCount = 72;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProcessRing),
        new PropertyMetadata(0d, (d, _) => ((ProcessRing)d).AnimateSweep()));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ProcessRing),
        new PropertyMetadata(false, (d, _) => ((ProcessRing)d).UpdateLoops()));

    /// <summary>Cor do arco: Warn (âmbar, trabalhando), Good (verde, concluído), Bad (vermelho, falhou), Info (ciano, lendo).</summary>
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(Tone), typeof(ProcessRing),
        new FrameworkPropertyMetadata(Tone.Warn, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(ProcessRing),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SweepProperty = DependencyProperty.Register("Sweep", typeof(double), typeof(ProcessRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SpinProperty = DependencyProperty.Register("Spin", typeof(double), typeof(ProcessRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty GlowProperty = DependencyProperty.Register("Glow", typeof(double), typeof(ProcessRing),
        new FrameworkPropertyMetadata(0.7d, FrameworkPropertyMetadataOptions.AffectsRender));

    public ProcessRing()
    {
        Focusable = false;
        Loaded += (_, _) => { AnimateSweep(); UpdateLoops(); };
        Unloaded += (_, _) => StopLoops();
        IsVisibleChanged += (_, _) => UpdateLoops();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private void AnimateSweep()
    {
        var target = DesignMath.Fraction(Value, 100);
        var from = (double)GetValue(SweepProperty);
        BeginAnimation(SweepProperty, null);
        SetValue(SweepProperty, target);
        if (!ShellMotion.Enabled || !IsLoaded || Math.Abs(from - target) < 1e-4) return;
        BeginAnimation(SweepProperty, new DoubleAnimation(from, target, new Duration(TimeSpan.FromMilliseconds(target < from ? 1 : 450)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    private bool _looping;

    private void UpdateLoops()
    {
        var want = IsActive && IsLoaded && IsVisible && ShellMotion.Enabled;
        if (want == _looping) return;
        if (!want) { StopLoops(); return; }
        _looping = true;
        BeginAnimation(SpinProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(2.6))) { RepeatBehavior = RepeatBehavior.Forever });
        BeginAnimation(GlowProperty, new DoubleAnimation(0.45, 1, new Duration(TimeSpan.FromSeconds(1.1)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void StopLoops()
    {
        _looping = false;
        BeginAnimation(SpinProperty, null);
        BeginAnimation(GlowProperty, null);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 40) return;
        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var center = new Point(cx, cy);
        var outer = size / 2 - 2;
        var t = Math.Min(Thickness, size / 10);
        var sweep = (double)GetValue(SweepProperty);
        var accent = (Theme.ToneBrush(Tone) as SolidColorBrush)?.Color ?? Colors.Orange;
        var line = (Theme.Find("Brush.Line") as SolidColorBrush)?.Color ?? Colors.SlateGray;
        var signal = (Theme.Find("Brush.Signal") as SolidColorBrush)?.Color ?? Colors.Cyan;

        // Marcas em volta (acendem com o andamento; a cada 6, uma maior).
        var lit = Freeze(new Pen(new SolidColorBrush(accent), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var dim = Freeze(new Pen(new SolidColorBrush(line), 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        for (var i = 0; i < TickCount; i++)
        {
            var f = (double)i / TickCount;
            var major = i % 6 == 0;
            var a = f * 2 * Math.PI - Math.PI / 2;
            var r1 = outer;
            var r2 = outer - (major ? 9 : 5);
            drawingContext.DrawLine(f < sweep ? lit : dim, new Point(cx + r1 * Math.Cos(a), cy + r1 * Math.Sin(a)), new Point(cx + r2 * Math.Cos(a), cy + r2 * Math.Sin(a)));
        }

        // Trilho + arco com brilho.
        var r = outer - 16 - t / 2;
        var track = Freeze(new Pen(Theme.Find("Brush.SurfaceHi"), t));
        drawingContext.DrawEllipse(null, track, center, r, r);
        if (sweep > 0.0005)
        {
            var glow = (double)GetValue(GlowProperty);
            var halo = Freeze(new Pen(new SolidColorBrush(Color.FromArgb((byte)(48 * glow), accent.R, accent.G, accent.B)), t + 10)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            var pen = Freeze(new Pen(new SolidColorBrush(accent), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            DrawArc(drawingContext, halo, center, r, 0, sweep);
            DrawArc(drawingContext, pen, center, r, 0, sweep);
            // Ponto brilhante na ponta.
            var end = sweep * 2 * Math.PI - Math.PI / 2;
            var tip = new Point(cx + r * Math.Cos(end), cy + r * Math.Sin(end));
            drawingContext.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(70 * glow), accent.R, accent.G, accent.B)), null, tip, t * 0.95, t * 0.95);
            drawingContext.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xD6)), null, tip, t * 0.28, t * 0.28);
        }

        // Anel interno tracejado e o arco ciano que gira (só com animação ligada e trabalhando).
        var inner = r - t / 2 - 10;
        if (inner > 10)
        {
            var dash = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, signal.R, signal.G, signal.B)), 1) { DashStyle = new DashStyle([2, 4], 0) });
            drawingContext.DrawEllipse(null, dash, center, inner, inner);
            if (IsActive && ShellMotion.Enabled)
            {
                var spin = (double)GetValue(SpinProperty) / 360;
                var scan = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, signal.R, signal.G, signal.B)), 2.5)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                DrawArc(drawingContext, scan, center, inner, spin, spin + 0.14);
                DrawArc(drawingContext, Freeze(new Pen(new SolidColorBrush(Color.FromArgb(90, signal.R, signal.G, signal.B)), 2.5)), center, inner, spin + 0.5, spin + 0.56);
            }
        }
    }

    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>Arco de <paramref name="from"/> a <paramref name="to"/> (frações da volta, 0 = 12 h, sentido horário).</summary>
    private static void DrawArc(DrawingContext dc, Pen pen, Point c, double r, double from, double to)
    {
        var span = to - from;
        if (span <= 0.0005) return;
        if (span >= 0.9995)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        var a0 = from * 2 * Math.PI - Math.PI / 2;
        var a1 = to * 2 * Math.PI - Math.PI / 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, span > 0.5, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.ProgressBar, nameof(ProcessRing), () => $"Andamento: {DesignMath.Format(Value, NumberKind.Percent, 0)}");
}

/// <summary>
/// Textura de monitor por cima de um painel: linhas horizontais bem fracas (fixas) e, enquanto <see cref="IsActive"/>, uma faixa
/// ciano suave que desce devagar, como uma varredura. Não recebe clique. Sem animações no Windows, só a textura fixa.
/// </summary>
public class ScanlineOverlay : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ScanlineOverlay),
        new PropertyMetadata(false, (d, _) => ((ScanlineOverlay)d).Update()));

    private static readonly DependencyProperty OffsetProperty = DependencyProperty.Register("Offset", typeof(double), typeof(ScanlineOverlay),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _running;

    public ScanlineOverlay()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => Update();
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private void Update()
    {
        var want = IsActive && IsLoaded && IsVisible && ShellMotion.Enabled;
        if (want == _running) return;
        if (!want) { Stop(); return; }
        _running = true;
        BeginAnimation(OffsetProperty, new DoubleAnimation(-0.15, 1.15, new Duration(TimeSpan.FromSeconds(4.2))) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void Stop()
    {
        _running = false;
        BeginAnimation(OffsetProperty, null);
        SetValue(OffsetProperty, -1d);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 1 || h < 1) return;
        var lineBrush = new SolidColorBrush(Color.FromArgb(9, 0xE8, 0xEC, 0xF5));
        lineBrush.Freeze();
        for (var y = 1.5; y < h; y += 4) drawingContext.DrawRectangle(lineBrush, null, new Rect(0, y, w, 1));
        var offset = (double)GetValue(OffsetProperty);
        if (offset < -0.5 || !_running) return;
        var signal = (Theme.Find("Brush.Signal") as SolidColorBrush)?.Color ?? Colors.Cyan;
        var band = new LinearGradientBrush(
            [
                new GradientStop(Color.FromArgb(0, signal.R, signal.G, signal.B), 0),
                new GradientStop(Color.FromArgb(22, signal.R, signal.G, signal.B), 0.85),
                new GradientStop(Color.FromArgb(40, signal.R, signal.G, signal.B), 1),
            ], 90);
        band.Freeze();
        const double bandHeight = 70;
        drawingContext.DrawRectangle(band, null, new Rect(0, offset * h - bandHeight, w, bandHeight));
    }
}

/// <summary>
/// Pulso de "ao vivo": <c>c:Pulse.IsOn="{Binding IsRunning}"</c> faz a opacidade ir e voltar (1 ↔ 0,35) enquanto verdadeiro.
/// Com as animações do Windows desligadas, fica parado e visível.
/// </summary>
public static class Pulse
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached("IsOn", typeof(bool), typeof(Pulse),
        new PropertyMetadata(false, OnChanged));

    public static bool GetIsOn(DependencyObject d) => (bool)d.GetValue(IsOnProperty);
    public static void SetIsOn(DependencyObject d, bool value) => d.SetValue(IsOnProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (el is FrameworkElement fe)
        {
            fe.Loaded -= OnLoaded;
            fe.Loaded += OnLoaded;
        }
        Apply(el);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement el) Apply(el);
    }

    private static void Apply(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        if (!GetIsOn(el) || !ShellMotion.Enabled) return;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromSeconds(0.8)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
