using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Nexus.Desktop.Views.Shell;

/// <summary>
/// Comandos da casca que qualquer página pode usar sem depender do MainViewModel. A janela principal trata (CommandBinding).
/// <code>&lt;Button Command="{x:Static shell:ShellCommands.Navigate}" CommandParameter="CleaningPage" Content="Abrir a Limpeza"/&gt;</code>
/// O parâmetro é o nome do tipo do ViewModel da página (o mesmo de ShellMap).
/// </summary>
public static class ShellCommands
{
    public static readonly RoutedUICommand Navigate = new("Abrir página", nameof(Navigate), typeof(ShellCommands));
}

/// <summary>
/// Página (ViewModel ou nome do tipo) → ícone de linha (Themes/Icons.xaml), pelo mapa de <see cref="ShellMap"/>.
/// ConverterParameter="illustration" devolve a ilustração (Assets/Illustrations).
/// </summary>
public sealed class PageIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? value?.GetType().Name;
        var key = parameter is "illustration" ? ShellMap.IllustrationFor(name) : ShellMap.IconFor(name);
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
