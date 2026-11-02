using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.Views;

/// <summary>Cor de fundo do "selo" de status (FactStatus, DiagnosticSeverity, PlanStatus, CapabilityLabel).</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Green = Freeze(0x1E, 0x7B, 0x4F), Amber = Freeze(0x9A, 0x6A, 0x00), Gray = Freeze(0x55, 0x5B, 0x66),
        Red = Freeze(0xA3, 0x35, 0x35), Blue = Freeze(0x2B, 0x5C, 0x9E);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FactStatus.Detected => Green,
        FactStatus.Inferred => Amber,
        FactStatus.Unavailable => Gray,
        DiagnosticSeverity.Ok => Green,
        DiagnosticSeverity.Info => Blue,
        DiagnosticSeverity.Attention => Amber,
        PlanStatus.Pending => Blue,
        PlanStatus.AlreadyConfigured => Green,
        PlanStatus.NotApplicable => Gray,
        PlanStatus.Blocked => Red,
        CapabilityLabel.ImplementedValidated => Green,
        CapabilityLabel.ImplementedNotValidated => Amber,
        CapabilityLabel.NotSupportedHere => Gray,
        CapabilityLabel.Pending => Red,
        _ => Gray,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FactStatus.Detected => Loc.T("Conv.Detectado"),
        FactStatus.Inferred => Loc.T("Conv.Inferido"),
        FactStatus.Unavailable => Loc.T("Pg.Indisponivel"),
        DiagnosticSeverity.Ok => "ok",
        DiagnosticSeverity.Info => Loc.T("Conv.Informativo"),
        DiagnosticSeverity.Attention => Loc.T("Conv.Atencao"),
        CapabilityLabel l => CapabilityText.Of(l),
        null => "",
        var v => v.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Monta as colunas de um GridView a partir de uma lista de títulos; cada coluna liga a Cells[i].</summary>
public static class TableColumns
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(IReadOnlyList<string>), typeof(TableColumns), new PropertyMetadata(null, OnSourceChanged));

    public static IReadOnlyList<string>? GetSource(DependencyObject d) => (IReadOnlyList<string>?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, IReadOnlyList<string>? value) => d.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list || e.NewValue is not IReadOnlyList<string> columns) return;
        var view = new GridView();
        for (var i = 0; i < columns.Count; i++)
            view.Columns.Add(new GridViewColumn
            {
                Header = columns[i],
                DisplayMemberBinding = new Binding($"Cells[{i}]") { TargetNullValue = "—" },
            });
        list.View = view;
    }
}
