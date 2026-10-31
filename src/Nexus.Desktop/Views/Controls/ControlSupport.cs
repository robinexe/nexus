using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Controls;

/// <summary>Pincéis do tema por chave (Tokens.xaml), com um cinza de reserva quando a chave não existe.</summary>
internal static class Theme
{
    public static Brush Find(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Brush ToneBrush(Tone tone) => Find(DesignMath.BrushKey(tone));

    public static Brush ToneSoftBrush(Tone tone) => Find(DesignMath.SoftBrushKey(tone));

    public static Geometry? Icon(string key) => Application.Current?.TryFindResource(key) as Geometry;
}

/// <summary>
/// Par de automação genérico: leitores de tela ouvem um nome descritivo ("Itens em ordem: 87%") em vez do nome da classe.
/// O nome dado no XAML (AutomationProperties.Name) sempre vence.
/// </summary>
internal sealed class DescribedPeer(FrameworkElement owner, AutomationControlType type, string className, Func<string> describe)
    : FrameworkElementAutomationPeer(owner)
{
    protected override string GetNameCore()
    {
        var given = base.GetNameCore();
        return string.IsNullOrEmpty(given) ? describe() : given;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => type;

    protected override string GetClassNameCore() => className;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}
