using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Número que conta até o valor (≈0,7 s, desacelerando no fim) quando aparece e quando muda: mostra que algo mudou.
/// É um TextBlock: aceita os estilos Text.* (Text.Metric para o número grande).
/// <code>&lt;c:AnimatedNumber Value="{Binding RecoverableBytes}" Kind="Bytes" Style="{StaticResource Text.Metric}"/&gt;</code>
/// Sem valor (NaN), mostra <see cref="Placeholder"/>. Com as animações do Windows desligadas, troca direto, sem contar.
/// </summary>
public class AnimatedNumber : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(double.NaN, (d, e) => ((AnimatedNumber)d).Animate((double)e.OldValue, (double)e.NewValue)));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(NumberKind), typeof(AnimatedNumber),
        new PropertyMetadata(NumberKind.Number, (d, _) => ((AnimatedNumber)d).Refresh()));

    /// <summary>Casas decimais para <see cref="NumberKind.Number"/>; -1 = automático (inteiro quando o valor é inteiro, senão 1).</summary>
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(AnimatedNumber),
        new PropertyMetadata(-1, (d, _) => ((AnimatedNumber)d).Refresh()));

    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(nameof(Prefix), typeof(string), typeof(AnimatedNumber),
        new PropertyMetadata("", (d, _) => ((AnimatedNumber)d).Refresh()));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(AnimatedNumber),
        new PropertyMetadata("", (d, _) => ((AnimatedNumber)d).Refresh()));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(AnimatedNumber),
        new PropertyMetadata("—", (d, _) => ((AnimatedNumber)d).Refresh()));

    private static readonly DependencyProperty CurrentProperty = DependencyProperty.Register("Current", typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(double.NaN, (d, _) => ((AnimatedNumber)d).Refresh()));

    public AnimatedNumber()
    {
        TextWrapping = TextWrapping.NoWrap;
        Loaded += (_, _) =>
        {
            // Primeira aparição: conta de zero até o valor uma vez (uma vez por navegação, não em loop).
            if (double.IsFinite(Value) && ShellMotion.Enabled) Animate(Value, Value, fromZero: true);
            else Refresh();
        };
        Refresh();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public NumberKind Kind { get => (NumberKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public string Prefix { get => (string)GetValue(PrefixProperty); set => SetValue(PrefixProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    private double Current => (double)GetValue(CurrentProperty);

    private void Animate(double from, double to, bool fromZero = false)
    {
        // O que está na tela agora (no meio de uma contagem, continua dali).
        var visible = double.IsFinite(Current) ? Current : double.IsFinite(from) ? from : 0;
        BeginAnimation(CurrentProperty, null);
        SetValue(CurrentProperty, to);
        var start = fromZero ? 0 : visible;
        if (!double.IsFinite(to) || !double.IsFinite(start) || !ShellMotion.Enabled || !IsLoaded || start.Equals(to))
        {
            Refresh();
            return;
        }
        BeginAnimation(CurrentProperty, new DoubleAnimation(start, to, ShellMotion.Count)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    private void Refresh()
    {
        var target = Value;
        var shown = double.IsFinite(Current) ? Current : target;
        if (!double.IsFinite(shown))
        {
            Text = Placeholder;
            return;
        }
        var decimals = Decimals >= 0 ? Decimals : Kind == NumberKind.Number && double.IsFinite(target) && Math.Abs(target - Math.Round(target)) < 1e-9 ? 0 : -1;
        Text = Prefix + DesignMath.Format(shown, Kind, decimals) + Suffix;
    }
}
