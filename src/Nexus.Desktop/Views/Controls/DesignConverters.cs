using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Chave de recurso (texto) → o recurso. Para ícones/ilustrações escolhidos pelo ViewModel por nome:
/// <c>Data="{Binding IconKey, Converter={StaticResource ResourceByKey}}"</c>. Chave desconhecida = nada (null).
/// </summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && key.Length > 0 ? Application.Current?.TryFindResource(key) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Inverte um booleano (verdadeiro ↔ falso).</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Tom (<see cref="Tone"/>) → pincel do tema. ConverterParameter="soft" devolve o fundo suave; "text" devolve a cor do texto
/// para o tom neutro (números que só ganham cor quando têm tom).
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value is Tone t ? t : Tone.Neutral;
        if (parameter is "soft") return Theme.ToneSoftBrush(tone);
        if (parameter is "text" && tone == Tone.Neutral) return Theme.Find("Brush.Text");
        return Theme.ToneBrush(tone);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
