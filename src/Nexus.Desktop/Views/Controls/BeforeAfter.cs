using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Antes → depois: duas barras (ciano = antes/referência, âmbar = depois) com os valores e a diferença em % num selo verde
/// (melhorou) ou vermelho (piorou). As barras enchem ao aparecer e ao mudar. Modelo em Themes/Components.xaml.
/// <code>&lt;c:BeforeAfter Before="{Binding FreeBytes}" After="{Binding FreeAfterBytes}" Kind="Bytes"
///     BeforeLabel="Livre agora" AfterLabel="Depois de esvaziar"/&gt;</code>
/// </summary>
public class BeforeAfter : Control
{
    public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(nameof(Before), typeof(double), typeof(BeforeAfter),
        new PropertyMetadata(double.NaN, OnChanged));

    public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(nameof(After), typeof(double), typeof(BeforeAfter),
        new PropertyMetadata(double.NaN, OnChanged));

    public static readonly DependencyProperty BeforeLabelProperty = DependencyProperty.Register(nameof(BeforeLabel), typeof(string), typeof(BeforeAfter),
        new PropertyMetadata(Loc.T("Shell.Antes")));

    public static readonly DependencyProperty AfterLabelProperty = DependencyProperty.Register(nameof(AfterLabel), typeof(string), typeof(BeforeAfter),
        new PropertyMetadata(Loc.T("Shell.Depois")));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(NumberKind), typeof(BeforeAfter),
        new PropertyMetadata(NumberKind.Number, OnChanged));

    /// <summary>Verdadeiro (padrão) quando um número maior é melhor (espaço livre, FPS); falso para tempo, uso, tamanho.</summary>
    public static readonly DependencyProperty HigherIsBetterProperty = DependencyProperty.Register(nameof(HigherIsBetter), typeof(bool), typeof(BeforeAfter),
        new PropertyMetadata(true, OnChanged));

    private static readonly DependencyPropertyKey BeforeTextKey = DependencyProperty.RegisterReadOnly(nameof(BeforeText), typeof(string), typeof(BeforeAfter), new PropertyMetadata("—"));
    private static readonly DependencyPropertyKey AfterTextKey = DependencyProperty.RegisterReadOnly(nameof(AfterText), typeof(string), typeof(BeforeAfter), new PropertyMetadata("—"));
    private static readonly DependencyPropertyKey DeltaTextKey = DependencyProperty.RegisterReadOnly(nameof(DeltaText), typeof(string), typeof(BeforeAfter), new PropertyMetadata("—"));
    private static readonly DependencyPropertyKey DeltaBrushKey = DependencyProperty.RegisterReadOnly(nameof(DeltaBrush), typeof(Brush), typeof(BeforeAfter), new PropertyMetadata(Brushes.Gray));
    private static readonly DependencyPropertyKey DeltaSoftBrushKey = DependencyProperty.RegisterReadOnly(nameof(DeltaSoftBrush), typeof(Brush), typeof(BeforeAfter), new PropertyMetadata(Brushes.Transparent));
    private static readonly DependencyPropertyKey DeltaIconKey = DependencyProperty.RegisterReadOnly(nameof(DeltaIcon), typeof(Geometry), typeof(BeforeAfter), new PropertyMetadata(null));

    public static readonly DependencyProperty BeforeTextProperty = BeforeTextKey.DependencyProperty;
    public static readonly DependencyProperty AfterTextProperty = AfterTextKey.DependencyProperty;
    public static readonly DependencyProperty DeltaTextProperty = DeltaTextKey.DependencyProperty;
    public static readonly DependencyProperty DeltaBrushProperty = DeltaBrushKey.DependencyProperty;
    public static readonly DependencyProperty DeltaSoftBrushProperty = DeltaSoftBrushKey.DependencyProperty;
    public static readonly DependencyProperty DeltaIconProperty = DeltaIconKey.DependencyProperty;

    /// <summary>Escala horizontal (0..1) das barras; o modelo liga ScaleTransform.ScaleX a estas (animadas).</summary>
    public static readonly DependencyProperty BeforeScaleProperty = DependencyProperty.Register(nameof(BeforeScale), typeof(double), typeof(BeforeAfter), new PropertyMetadata(0d));
    public static readonly DependencyProperty AfterScaleProperty = DependencyProperty.Register(nameof(AfterScale), typeof(double), typeof(BeforeAfter), new PropertyMetadata(0d));

    public BeforeAfter()
    {
        Focusable = false;
        Loaded += (_, _) => { Recompute(); AnimateBars(fromZero: true); };
    }

    public double Before { get => (double)GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public double After { get => (double)GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    public string BeforeLabel { get => (string)GetValue(BeforeLabelProperty); set => SetValue(BeforeLabelProperty, value); }
    public string AfterLabel { get => (string)GetValue(AfterLabelProperty); set => SetValue(AfterLabelProperty, value); }
    public NumberKind Kind { get => (NumberKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool HigherIsBetter { get => (bool)GetValue(HigherIsBetterProperty); set => SetValue(HigherIsBetterProperty, value); }
    public string BeforeText => (string)GetValue(BeforeTextProperty);
    public string AfterText => (string)GetValue(AfterTextProperty);
    public string DeltaText => (string)GetValue(DeltaTextProperty);
    public Brush DeltaBrush => (Brush)GetValue(DeltaBrushProperty);
    public Brush DeltaSoftBrush => (Brush)GetValue(DeltaSoftBrushProperty);
    public Geometry? DeltaIcon => (Geometry?)GetValue(DeltaIconProperty);
    public double BeforeScale { get => (double)GetValue(BeforeScaleProperty); set => SetValue(BeforeScaleProperty, value); }
    public double AfterScale { get => (double)GetValue(AfterScaleProperty); set => SetValue(AfterScaleProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (BeforeAfter)d;
        c.Recompute();
        c.AnimateBars(fromZero: false);
    }

    private void Recompute()
    {
        SetValue(BeforeTextKey, DesignMath.Format(Before, Kind));
        SetValue(AfterTextKey, DesignMath.Format(After, Kind));
        var (text, tone) = DesignMath.Delta(Before, After, Kind, HigherIsBetter);
        SetValue(DeltaTextKey, text);
        SetValue(DeltaBrushKey, Theme.ToneBrush(tone));
        SetValue(DeltaSoftBrushKey, Theme.ToneSoftBrush(tone));
        var up = double.IsFinite(Before) && double.IsFinite(After) && After > Before;
        var down = double.IsFinite(Before) && double.IsFinite(After) && After < Before;
        SetValue(DeltaIconKey, Theme.Icon(up ? "Icon.TrendUp" : down ? "Icon.TrendDown" : "Icon.ArrowRight"));
    }

    private void AnimateBars(bool fromZero)
    {
        var (b, a) = DesignMath.BarScales(Before, After);
        Animate(BeforeScaleProperty, b, fromZero);
        Animate(AfterScaleProperty, a, fromZero);
    }

    private void Animate(DependencyProperty property, double to, bool fromZero)
    {
        var from = fromZero ? 0 : (double)GetValue(property);
        BeginAnimation(property, null);
        SetValue(property, to);
        if (!ShellMotion.Enabled || !IsLoaded || Math.Abs(from - to) < 1e-6) return;
        BeginAnimation(property, new DoubleAnimation(from, to, ShellMotion.Count)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.Text, nameof(BeforeAfter),
            () => Loc.F("Shell.NNNN", BeforeLabel, BeforeText, AfterLabel, AfterText, DeltaText));
}
