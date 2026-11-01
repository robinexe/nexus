using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Shell;

/// <summary>
/// Fração (0..1) → ScaleTransform para barras. Ligar <c>ScaleTransform.ScaleX="{Binding}"</c> dentro de RenderTransform dá
/// "Cannot find governing FrameworkElement" (o Freezable nasce antes do contexto de dados; visto no autoteste da VM).
/// Ligar o próprio RenderTransform pelo conversor evita isso. Parâmetro "Y" = escala vertical.
/// </summary>
public sealed class FractionScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var f = value switch { double d => d, float s => s, int i => i, _ => 0d };
        if (double.IsNaN(f) || double.IsInfinity(f)) f = 0;
        f = Math.Clamp(f, 0, 1);
        var t = string.Equals(parameter as string, "Y", StringComparison.OrdinalIgnoreCase) ? new ScaleTransform(1, f) : new ScaleTransform(f, 1);
        t.Freeze();
        return t;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
