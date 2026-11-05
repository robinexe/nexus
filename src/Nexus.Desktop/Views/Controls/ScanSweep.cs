using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>Um ponto da Varredura: nome, terminado, em andamento.</summary>
public interface ISweepNode
{
    string Title { get; }
    bool IsDone { get; }
    bool IsCurrent { get; }
}

/// <summary>
/// A Varredura — animação exclusiva do NEXUS Optimize (docs/14-redesign-brief.md): um anel com um ponto por etapa e um ponteiro
/// âmbar que gira como o de um radar enquanto o trabalho acontece. Cada ponto só acende quando a etapa termina de verdade
/// (<see cref="ISweepNode.IsDone"/>, vindo do Assistente); o da etapa atual fica em ciano. O ponteiro nunca "termina" antes do PC.
/// Custo: um desenho simples por quadro só enquanto <see cref="IsActive"/> e a janela estão à vista. Com as animações do Windows
/// desligadas, não há ponteiro: os pontos acendem em sequência, com os mesmos textos.
/// </summary>
public class ScanSweep : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(ScanSweep),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(ScanSweep),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ScanSweep)d).UpdateSpin()));

    private static readonly DependencyProperty AngleProperty = DependencyProperty.Register("Angle", typeof(double), typeof(ScanSweep),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double LabelRoom = 120;

    public ScanSweep()
    {
        Focusable = false;
        Loaded += (_, _) => UpdateSpin();
        Unloaded += (_, _) => BeginAnimation(AngleProperty, null);
        IsVisibleChanged += (_, _) => UpdateSpin();
    }

    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (ScanSweep)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= s.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += s.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void UpdateSpin()
    {
        var spin = IsActive && IsVisible && IsLoaded && ShellMotion.Enabled;
        if (!spin) { BeginAnimation(AngleProperty, null); return; }
        if (HasAnimatedProperties) return;
        BeginAnimation(AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(3.2))) { RepeatBehavior = RepeatBehavior.Forever });
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? Math.Min(availableSize.Width, 560) : 520,
        double.IsFinite(availableSize.Height) ? Math.Min(availableSize.Height, 420) : 400);

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var nodes = Items?.OfType<ISweepNode>().ToList() ?? [];
        var w = ActualWidth;
        var h = ActualHeight;
        var r = Math.Min((w - 2 * LabelRoom) / 2, (h - 60) / 2);
        if (r < 40) return;
        var c = new Point(w / 2, h / 2);

        // Fundo: disco fundo, dois anéis finos.
        var disc = new RadialGradientBrush(Color.FromRgb(0x11, 0x17, 0x26), Color.FromRgb(0x0A, 0x0E, 0x18));
        disc.Freeze();
        dc.DrawEllipse(disc, null, c, r + 34, r + 34);
        var ring = new Pen(Theme.Find("Brush.Line"), 1);
        ring.Freeze();
        var soft = new Pen(Theme.Find("Brush.LineSoft"), 1);
        soft.Freeze();
        dc.DrawEllipse(null, ring, c, r, r);
        dc.DrawEllipse(null, soft, c, r * 0.55, r * 0.55);

        // Ponteiro com rastro (só girando).
        if (IsActive && ShellMotion.Enabled)
        {
            var angle = (double)GetValue(AngleProperty);
            var trace = ((SolidColorBrush)Theme.Find("Brush.Trace")).Color;
            for (var i = 14; i >= 1; i--)
            {
                var a = (angle - i * 3.5) * Math.PI / 180;
                var p = new Pen(new SolidColorBrush(Color.FromArgb((byte)(48 * (1 - i / 15d)), trace.R, trace.G, trace.B)), 3);
                p.Freeze();
                dc.DrawLine(p, c, new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a)));
            }
            var ha = angle * Math.PI / 180;
            var hand = new Pen(new LinearGradientBrush(Color.FromArgb(0, trace.R, trace.G, trace.B), trace,
                new Point(0.5 - 0.5 * Math.Sin(ha), 0.5 + 0.5 * Math.Cos(ha)), new Point(0.5 + 0.5 * Math.Sin(ha), 0.5 - 0.5 * Math.Cos(ha))), 2);
            hand.Freeze();
            dc.DrawLine(hand, c, new Point(c.X + r * Math.Sin(ha), c.Y - r * Math.Cos(ha)));
        }

        // Pontos e nomes.
        if (nodes.Count == 0) return;
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var a = (i / (double)nodes.Count) * 2 * Math.PI;
            var p = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
            if (n.IsDone)
            {
                dc.DrawEllipse(Theme.Find("Brush.Good"), null, p, 9, 9);
                var tick = new Pen(Theme.Find("Brush.Ground"), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                tick.Freeze();
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(p.X - 4, p.Y + 0.5), false, false);
                    ctx.LineTo(new Point(p.X - 1, p.Y + 3.5), true, true);
                    ctx.LineTo(new Point(p.X + 4.5, p.Y - 3), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, tick, g);
            }
            else if (n.IsCurrent)
            {
                dc.DrawEllipse(Theme.Find("Brush.SignalSoft"), null, p, 15, 15);
                var cur = new Pen(Theme.Find("Brush.Signal"), 2);
                cur.Freeze();
                dc.DrawEllipse(Theme.Find("Brush.Ground"), cur, p, 9, 9);
            }
            else
            {
                var pend = new Pen(Theme.Find("Brush.Line"), 1.5);
                pend.Freeze();
                dc.DrawEllipse(Theme.Find("Brush.Ground"), pend, p, 8, 8);
            }

            var color = n.IsDone ? Theme.Find("Brush.Text") : n.IsCurrent ? Theme.Find("Brush.Signal") : Theme.Find("Brush.TextDim");
            var text = new FormattedText(n.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 13.5, color, dpi)
            {
                MaxTextWidth = LabelRoom + 30,
                MaxLineCount = 2,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var sin = Math.Sin(a);
            var cos = Math.Cos(a);
            Point at;
            if (Math.Abs(sin) < 0.3) // em cima ou embaixo: centralizado
            {
                text.TextAlignment = TextAlignment.Center;
                at = cos > 0 ? new Point(p.X - text.MaxTextWidth / 2, p.Y - 22 - text.Height) : new Point(p.X - text.MaxTextWidth / 2, p.Y + 20);
            }
            else if (sin > 0) at = new Point(p.X + 20, p.Y - text.Height / 2);                       // direita
            else { text.TextAlignment = TextAlignment.Right; at = new Point(p.X - 20 - text.MaxTextWidth, p.Y - text.Height / 2); } // esquerda
            dc.DrawText(text, at);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.ProgressBar, nameof(ScanSweep), () =>
        {
            var nodes = Items?.OfType<ISweepNode>().ToList() ?? [];
            var done = nodes.Count(n => n.IsDone);
            var current = nodes.FirstOrDefault(n => n.IsCurrent)?.Title;
            return current is null ? $"{done}/{nodes.Count}" : $"{done}/{nodes.Count} · {current}";
        });
}
