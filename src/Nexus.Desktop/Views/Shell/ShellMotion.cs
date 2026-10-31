using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Nexus.Desktop.Views.Shell;

/// <summary>
/// Movimento da casca. Só responde a ações do usuário (hover, clique, troca de página) e há um único momento orquestrado: a
/// varredura âmbar que atravessa o cabeçalho ao abrir. Tudo respeita <see cref="SystemParameters.ClientAreaAnimation"/>: com as
/// animações do Windows desligadas, as durações viram zero e nada se move.
/// </summary>
public static class ShellMotion
{
    private static readonly Duration Zero = new(TimeSpan.Zero);

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Transições curtas de cor/opacidade em hover e clique (usado pelos modelos de controle via x:Static).</summary>
    public static Duration Fast => Enabled ? new Duration(TimeSpan.FromMilliseconds(120)) : Zero;

    /// <summary>Troca de página e entradas.</summary>
    public static Duration Normal => Enabled ? new Duration(TimeSpan.FromMilliseconds(220)) : Zero;

    /// <summary>Início/fim do segmento do indicador de ocupado (ProgressBar indeterminado); com animações desligadas fica parado.</summary>
    public static double SweepFrom => Enabled ? -1d : 0d;

    public static double SweepTo => Enabled ? 1d : 0d;

    /// <summary>Entrada da página: aparece com fade e sobe 10 px em ~220 ms.</summary>
    public static void EnterPage(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.BeginAnimation(UIElement.OpacityProperty, null);
        if (!Enabled)
        {
            host.Opacity = 1;
            host.RenderTransform = Transform.Identity;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform(0, 10);
        host.RenderTransform = shift;
        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Normal) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Normal) { EasingFunction = ease });
    }

    /// <summary>A varredura: um traço âmbar atravessa o cabeçalho uma vez e se apaga.</summary>
    public static void SweepOnce(FrameworkElement beam, double width)
    {
        ArgumentNullException.ThrowIfNull(beam);
        if (!Enabled || width <= 0 || double.IsNaN(width)) return;

        var start = -(double.IsNaN(beam.Width) ? 240 : beam.Width);
        Canvas.SetLeft(beam, start);
        beam.Opacity = 1;
        var travel = new DoubleAnimation(start, width, new Duration(TimeSpan.FromMilliseconds(1150)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            BeginTime = TimeSpan.FromMilliseconds(250),
        };
        travel.Completed += (_, _) =>
            beam.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(260))));
        beam.BeginAnimation(Canvas.LeftProperty, travel);
    }
}
