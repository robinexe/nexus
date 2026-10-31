using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;

namespace Nexus.Desktop.Views.Measure;

/// <summary>
/// Número que conta de 0 até o valor quando aparece (~500 ms), num TextBlock. Uso:
/// <c>m:CountUp.Value="{Binding AverageFps}" m:CountUp.Format="0"</c>. Não ligue Text ao mesmo tempo. Sem animação quando
/// o Windows pede menos movimento; valor ausente ou não finito mostra "—".
/// </summary>
public static class CountUp
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Duration Length = new(TimeSpan.FromMilliseconds(520));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached("Value", typeof(double), typeof(CountUp),
        new PropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached("Format", typeof(string), typeof(CountUp),
        new PropertyMetadata("0", OnLookChanged));

    private static readonly DependencyProperty CurrentProperty = DependencyProperty.RegisterAttached("Current", typeof(double), typeof(CountUp),
        new PropertyMetadata(0.0, OnLookChanged));

    public static double GetValue(DependencyObject d) => (double)(d ?? throw new ArgumentNullException(nameof(d))).GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, double value) => (d ?? throw new ArgumentNullException(nameof(d))).SetValue(ValueProperty, value);
    public static string GetFormat(DependencyObject d) => (string)(d ?? throw new ArgumentNullException(nameof(d))).GetValue(FormatProperty);
    public static void SetFormat(DependencyObject d, string value) => (d ?? throw new ArgumentNullException(nameof(d))).SetValue(FormatProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;
        var target = (double)e.NewValue;
        if (double.IsFinite(target) && SystemParameters.ClientAreaAnimation)
        {
            var animation = new DoubleAnimation(0, target, Length) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Freeze();
            text.BeginAnimation(CurrentProperty, animation);
        }
        else
        {
            text.BeginAnimation(CurrentProperty, null);
            text.SetValue(CurrentProperty, double.IsFinite(target) ? target : 0.0);
        }
        Refresh(text);
    }

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock text) Refresh(text);
    }

    private static void Refresh(TextBlock text)
    {
        if (!double.IsFinite(GetValue(text))) { text.Text = "—"; return; }
        var format = GetFormat(text);
        text.Text = ((double)text.GetValue(CurrentProperty)).ToString(string.IsNullOrEmpty(format) ? "0" : format, Pt);
    }
}

/// <summary>bool para Visibility; <see cref="Invert"/> troca o sentido. Aceita bool? (null conta como falso).</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visível quando o valor existe (não nulo, string não vazia); <see cref="Invert"/> troca o sentido.</summary>
public sealed class PresenceVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null && value is not string { Length: 0 } && value is not 0;
        return present != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Liga um botão de opção a um valor inteiro: marcado quando o valor é igual ao parâmetro (texto, ex. "60"); ao marcar,
/// devolve o parâmetro. Usado nos atalhos de duração (30/60/120/300 s).
/// </summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int v && TryParse(parameter, out var p) && v == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && TryParse(parameter, out var p) ? p : Binding.DoNothing;

    private static bool TryParse(object? parameter, out int result) =>
        int.TryParse(parameter as string ?? parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}

/// <summary>Relógio da captura: (Progress 0..1, Seconds) vira "23 s de 60 s".</summary>
public sealed class CaptureClockConverter : IMultiValueConverter
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [double progress, int seconds]) return "";
        var elapsed = Math.Clamp(progress, 0, 1) * seconds;
        return string.Create(Pt, $"{elapsed:0} s de {seconds} s");
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}

/// <summary>Inverte um bool (ex.: campos ligados a IsEnabled ficam travados enquanto Busy).</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Conversores de visibilidade compostos usados nas páginas de medição.</summary>
public static class MeasureVisibility
{
    /// <summary>(bool temItens, objeto selecionado): visível quando há itens mas nada está selecionado.</summary>
    public static IMultiValueConverter HasItemsButNoSelection { get; } = new ItemsWithoutSelection();

    private sealed class ItemsWithoutSelection : IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [true, null] ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
    }
}
