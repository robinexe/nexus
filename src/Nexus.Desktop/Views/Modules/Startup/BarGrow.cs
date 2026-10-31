using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop.Views.Modules.Startup;

/// <summary>
/// Barra que cresce de baixo para cima quando aparece (gráfico dos últimos boots). <c>startup:BarGrow.OnLoad="True"</c> no
/// elemento; <c>startup:BarGrow.Delay</c> (ms) escalona as barras. Com as animações do Windows desligadas, a barra já aparece cheia.
/// Só apresentação: não lê nem escreve nada.
/// </summary>
public static class BarGrow
{
    public static readonly DependencyProperty OnLoadProperty = DependencyProperty.RegisterAttached("OnLoad", typeof(bool), typeof(BarGrow),
        new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached("Delay", typeof(double), typeof(BarGrow),
        new PropertyMetadata(0d));

    public static bool GetOnLoad(DependencyObject element) => (bool)element.GetValue(OnLoadProperty);
    public static void SetOnLoad(DependencyObject element, bool value) => element.SetValue(OnLoadProperty, value);
    public static double GetDelay(DependencyObject element) => (double)element.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject element, double value) => element.SetValue(DelayProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= Grow;
        if (e.NewValue is true) element.Loaded += Grow;
    }

    private static void Grow(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 1);
        element.RenderTransform = scale;
        if (!ShellMotion.Enabled) return;
        scale.ScaleY = 0; // parada em zero durante o atraso; a animação termina e fica em 1
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, ShellMotion.Count)
        {
            BeginTime = TimeSpan.FromMilliseconds(Math.Clamp(GetDelay(element), 0, 1000)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }
}
