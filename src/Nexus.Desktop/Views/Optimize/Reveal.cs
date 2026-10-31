using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Nexus.Desktop.Views.Optimize;

/// <summary>
/// Revelação curta (opacidade e um leve deslocamento vertical) em resposta a uma ação: o elemento entra quando é
/// carregado (<c>OnLoad</c>) ou quando o valor ligado em <c>Trigger</c> muda para algo diferente de null/false.
/// Com <c>Stagger</c>, itens de uma lista entram em sequência. Nada anima quando o Windows pede menos movimento
/// (<see cref="SystemParameters.ClientAreaAnimation"/> desligado).
/// </summary>
public static class Reveal
{
    private static readonly Duration Fallback = new(TimeSpan.FromMilliseconds(220));
    private const double Offset = 8;
    private const int StaggerStepMs = 35;
    private const int StaggerMaxMs = 350;

    public static readonly DependencyProperty OnLoadProperty = DependencyProperty.RegisterAttached(
        "OnLoad", typeof(bool), typeof(Reveal), new PropertyMetadata(false, OnOnLoadChanged));

    public static bool GetOnLoad(DependencyObject d) => (bool)d.GetValue(OnLoadProperty);
    public static void SetOnLoad(DependencyObject d, bool value) => d.SetValue(OnLoadProperty, value);

    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(Reveal), new PropertyMetadata(false));

    public static bool GetStagger(DependencyObject d) => (bool)d.GetValue(StaggerProperty);
    public static void SetStagger(DependencyObject d, bool value) => d.SetValue(StaggerProperty, value);

    public static readonly DependencyProperty TriggerProperty = DependencyProperty.RegisterAttached(
        "Trigger", typeof(object), typeof(Reveal), new PropertyMetadata(null, OnTriggerChanged));

    public static object? GetTrigger(DependencyObject d) => d.GetValue(TriggerProperty);
    public static void SetTrigger(DependencyObject d, object? value) => d.SetValue(TriggerProperty, value);

    private static void OnOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnLoaded;
        if (e.NewValue is true) fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) Play(fe, GetStagger(fe) ? StaggerDelay(fe) : TimeSpan.Zero);
    }

    private static void OnTriggerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is null or false || !fe.IsLoaded) return;
        Play(fe, TimeSpan.Zero);
    }

    private static void Play(FrameworkElement fe, TimeSpan delay)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var duration = fe.TryFindResource("Motion.Normal") is Duration d && d.HasTimeSpan ? d : Fallback;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (fe.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            fe.RenderTransform = shift;
        }

        var fade = new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease };
        var slide = new DoubleAnimation(Offset, 0, duration) { BeginTime = delay, EasingFunction = ease };
        if (delay > TimeSpan.Zero) fe.Opacity = 0;
        fe.BeginAnimation(UIElement.OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Atraso proporcional à posição do item na lista que o contém (limitado, para não arrastar).</summary>
    private static TimeSpan StaggerDelay(FrameworkElement fe)
    {
        var container = ItemsControl.ContainerFromElement(null, fe);
        if (container is null) return TimeSpan.Zero;
        var owner = ItemsControl.ItemsControlFromItemContainer(container);
        var index = owner?.ItemContainerGenerator.IndexFromContainer(container) ?? -1;
        return index <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(index * StaggerStepMs, StaggerMaxMs));
    }
}
