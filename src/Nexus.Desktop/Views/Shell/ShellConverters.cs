using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.Views.Shell;

/// <summary>
/// Tom do selo de status, nos tokens do tema: ciano = lido/informativo, âmbar = atenção/pendente, verde = ok/validado,
/// vermelho = bloqueado, cinza = inferido/indisponível. Com ConverterParameter="text", o cinza escuro vira o cinza legível.
/// </summary>
public sealed class StatusToneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            FactStatus.Detected => "Brush.Signal",
            FactStatus.Inferred => "Brush.TextMuted",
            FactStatus.Unavailable => "Brush.TextDim",
            DiagnosticSeverity.Ok => "Brush.Good",
            DiagnosticSeverity.Info => "Brush.Signal",
            DiagnosticSeverity.Attention => "Brush.Trace",
            PlanStatus.Pending => "Brush.Trace",
            PlanStatus.AlreadyConfigured => "Brush.Good",
            PlanStatus.NotApplicable => "Brush.TextDim",
            PlanStatus.Blocked => "Brush.Bad",
            CapabilityLabel.ImplementedValidated => "Brush.Good",
            CapabilityLabel.ImplementedNotValidated => "Brush.Trace",
            CapabilityLabel.NotSupportedHere => "Brush.TextDim",
            CapabilityLabel.Pending => "Brush.TextMuted",
            _ => "Brush.TextMuted",
        };
        if (key == "Brush.TextDim" && parameter is "text") key = "Brush.TextMuted";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>O que cada severidade de diagnóstico quer dizer para o jogador. Nenhuma promete ganho.</summary>
public sealed class SeverityHintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiagnosticSeverity.Attention => "Vale conferir. Não é garantia de FPS a mais: meça uma partida antes e depois de mexer.",
        DiagnosticSeverity.Info => "Só para você saber. Não pede nenhuma ação.",
        DiagnosticSeverity.Ok => "Está como deveria. Nada a fazer aqui.",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visível quando o número é zero (ConverterParameter="invert": visível quando é maior que zero).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var zero = value is not int n || n == 0;
        if (parameter is "invert") zero = !zero;
        return zero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visível quando o booleano é falso (o inverso do BooleanToVisibilityConverter).</summary>
public sealed class FalseToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
