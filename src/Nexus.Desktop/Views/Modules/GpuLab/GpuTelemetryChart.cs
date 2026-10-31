using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;
using Nexus.Lab.Gpu;

namespace Nexus.Desktop.Views.Modules.GpuLab;

/// <summary>
/// Gráfico da gravação da GPU: faixas de limitação ao fundo (âmbar = potência, vermelho = temperatura/hardware, ciano =
/// configuração) e linhas de clock (âmbar), uso (ciano), temperatura (vermelho, 0–100 °C) e potência (verde, em relação ao
/// limite). Um único elemento desenhado em OnRender; somente leitura. Entrada: revelado da esquerda para a direita quando
/// aparece (não a cada amostra da gravação); com as animações do Windows desligadas, aparece pronto.
/// </summary>
public sealed class GpuTelemetryChart : FrameworkElement
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(IReadOnlyList<GpuTelemetrySample>), typeof(GpuTelemetryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(nameof(Bands), typeof(IReadOnlyList<LimitBand>), typeof(GpuTelemetryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxClockProperty = DependencyProperty.Register(nameof(MaxClock), typeof(double), typeof(GpuTelemetryChart),
        new FrameworkPropertyMetadata(2000.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(double), typeof(GpuTelemetryChart),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<GpuTelemetrySample>? Samples { get => (IReadOnlyList<GpuTelemetrySample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public IReadOnlyList<LimitBand>? Bands { get => (IReadOnlyList<LimitBand>?)GetValue(BandsProperty); set => SetValue(BandsProperty, value); }
    public double MaxClock { get => (double)GetValue(MaxClockProperty); set => SetValue(MaxClockProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }

    private static readonly Brush Label = Frozen(0x8A, 0x96, 0xB3, 1);
    private static readonly Brush PowerBand = Frozen(0xFF, 0xB5, 0x47, 0.16);
    private static readonly Brush ThermalBand = Frozen(0xFF, 0x6B, 0x7A, 0.18);
    private static readonly Brush OtherBand = Frozen(0x4F, 0xD1, 0xE8, 0.12);
    private static readonly Pen ClockPen = FrozenPen(0xFF, 0xB5, 0x47, 2);
    private static readonly Pen UtilPen = FrozenPen(0x4F, 0xD1, 0xE8, 1.4);
    private static readonly Pen TempPen = FrozenPen(0xFF, 0x6B, 0x7A, 1.4);
    private static readonly Pen PowerPen = FrozenPen(0x5E, 0xE6, 0xA0, 1.4);
    private static readonly Pen GridPen = FrozenPen(0x1E, 0x26, 0x38, 1);

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double), typeof(GpuTelemetryChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fração já revelada (0..1).</summary>
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    public GpuTelemetryChart()
    {
        SnapsToDevicePixels = true;
        Focusable = false;
        Loaded += (_, _) => PlayReveal();
    }

    /// <summary>Revela o gráfico uma vez (0,9 s, desacelerando).</summary>
    public void PlayReveal()
    {
        BeginAnimation(RevealProperty, null);
        if (!ShellMotion.Enabled) { Reveal = 1; return; }
        BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(Frozen(r, g, b, 1), thickness) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var dc = drawingContext;
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40) return;
        const double left = 0, bottom = 18;
        var plotH = h - bottom;
        var duration = Math.Max(1, Duration);
        double X(double s) => left + (w - left) * Math.Clamp(s / duration, 0, 1);
        double Y(double fraction) => plotH * (1 - Math.Clamp(fraction, 0, 1));

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var reveal = Math.Clamp(Reveal, 0, 1);
        if (reveal < 1) dc.PushClip(new RectangleGeometry(new Rect(0, 0, w * reveal, h)));
        Plot(dc, w, plotH, duration, X, Y);
        if (reveal < 1) dc.Pop();
    }

    private void Plot(DrawingContext dc, double w, double plotH, double duration, Func<double, double> x, Func<double, double> y)
    {
        const double left = 0;
        foreach (var band in Bands ?? [])
        {
            var brush = band.Category switch
            {
                LimitCategory.Power => PowerBand,
                LimitCategory.Thermal or LimitCategory.Hardware => ThermalBand,
                _ => OtherBand,
            };
            dc.DrawRectangle(brush, null, new Rect(x(band.FromSeconds), 0, Math.Max(1, x(band.ToSeconds) - x(band.FromSeconds)), plotH));
        }
        for (var i = 0; i <= 4; i++) dc.DrawLine(GridPen, new Point(left, y(i / 4.0)), new Point(w, y(i / 4.0)));
        for (var s = 0; s <= duration; s += 10)
        {
            var text = new FormattedText($"{s} s", Pt, FlowDirection.LeftToRight, new Typeface("Bahnschrift"), 10.5, Label, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Min(x(s), w - text.Width), plotH + 3));
        }

        var samples = Samples;
        if (samples is null || samples.Count < 2) return;
        var maxClock = MaxClock > 0 ? MaxClock : Math.Max(1, samples.Max(x => x.GraphicsClockMhz ?? 0));
        Line(dc, samples, s => s.UtilizationPercent / 100.0, UtilPen, x, y);
        Line(dc, samples, s => s.TemperatureC / 100.0, TempPen, x, y);
        Line(dc, samples, s => s.PowerWatts is { } p && s.PowerLimitWatts is > 0 ? p / s.PowerLimitWatts : null, PowerPen, x, y);
        Line(dc, samples, s => s.GraphicsClockMhz / maxClock, ClockPen, x, y);
    }

    private static void Line(DrawingContext dc, IReadOnlyList<GpuTelemetrySample> samples, Func<GpuTelemetrySample, double?> value, Pen pen,
        Func<double, double> x, Func<double, double> y)
    {
        var geometry = new StreamGeometry();
        var any = false;
        using (var ctx = geometry.Open())
        {
            var open = false;
            foreach (var s in samples)
            {
                if (value(s) is not { } v || double.IsNaN(v)) { open = false; continue; }
                var p = new Point(x(s.Seconds), y(v));
                if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                else ctx.LineTo(p, true, true);
                any = true;
            }
        }
        if (!any) return;
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
