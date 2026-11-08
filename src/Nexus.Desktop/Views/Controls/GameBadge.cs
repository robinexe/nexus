using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Selo de um jogo na tela Jogos: monograma de 1–3 letras sobre um gradiente discreto da cor de destaque do cartão
/// (<c>GameCardDefinition.Monogram</c>/<c>AccentHex</c>). Sem cor (jogo sem cartão próprio), usa o âmbar do tema. Nenhum
/// logotipo ou arte de marca é desenhado: só letras e cor (regra 5 do brief).
/// <code>&lt;c:GameBadge Monogram="FN" AccentHex="#6E8BD9"/&gt;</code>
/// </summary>
public class GameBadge : Control
{
    public static readonly DependencyProperty MonogramProperty = DependencyProperty.Register(nameof(Monogram), typeof(string), typeof(GameBadge), new PropertyMetadata("?"));
    public static readonly DependencyProperty AccentHexProperty = DependencyProperty.Register(nameof(AccentHex), typeof(string), typeof(GameBadge),
        new PropertyMetadata(null, (d, _) => ((GameBadge)d).UpdateBrushes()));

    private static readonly DependencyPropertyKey AccentBrushKey = DependencyProperty.RegisterReadOnly(nameof(AccentBrush), typeof(Brush), typeof(GameBadge), new PropertyMetadata(Brushes.Gray));
    private static readonly DependencyPropertyKey AccentSoftBrushKey = DependencyProperty.RegisterReadOnly(nameof(AccentSoftBrush), typeof(Brush), typeof(GameBadge), new PropertyMetadata(Brushes.Transparent));
    private static readonly DependencyPropertyKey AccentLineBrushKey = DependencyProperty.RegisterReadOnly(nameof(AccentLineBrush), typeof(Brush), typeof(GameBadge), new PropertyMetadata(Brushes.Transparent));
    public static readonly DependencyProperty AccentBrushProperty = AccentBrushKey.DependencyProperty;
    public static readonly DependencyProperty AccentSoftBrushProperty = AccentSoftBrushKey.DependencyProperty;
    public static readonly DependencyProperty AccentLineBrushProperty = AccentLineBrushKey.DependencyProperty;

    public GameBadge()
    {
        Focusable = false;
        Loaded += (_, _) => UpdateBrushes();
        UpdateBrushes();
    }

    public string Monogram { get => (string)GetValue(MonogramProperty); set => SetValue(MonogramProperty, value); }
    /// <summary>Cor de destaque (#RRGGBB) ou null/inválida = âmbar do tema.</summary>
    public string? AccentHex { get => (string?)GetValue(AccentHexProperty); set => SetValue(AccentHexProperty, value); }
    /// <summary>Cor cheia (letras).</summary>
    public Brush AccentBrush => (Brush)GetValue(AccentBrushProperty);
    /// <summary>Gradiente discreto de fundo (≈ 12 % → 22 % da cor, do canto superior esquerdo ao inferior direito).</summary>
    public Brush AccentSoftBrush => (Brush)GetValue(AccentSoftBrushProperty);
    /// <summary>Contorno de 1 px (≈ 35 % da cor).</summary>
    public Brush AccentLineBrush => (Brush)GetValue(AccentLineBrushProperty);

    /// <summary>"#RRGGBB" → cor; qualquer outra coisa → null (<see cref="DesignMath.ParseHexColor"/>, lógica pura testada).</summary>
    public static Color? ParseHex(string? hex) => DesignMath.ParseHexColor(hex) is { } c ? Color.FromRgb(c.R, c.G, c.B) : null;

    private void UpdateBrushes()
    {
        var color = ParseHex(AccentHex) ?? (Theme.Find("Brush.Trace") as SolidColorBrush)?.Color ?? Color.FromRgb(0xFF, 0xB5, 0x47);
        var soft = new LinearGradientBrush(Color.FromArgb(0x1F, color.R, color.G, color.B), Color.FromArgb(0x38, color.R, color.G, color.B), new Point(0, 0), new Point(1, 1));
        var line = new SolidColorBrush(Color.FromArgb(0x59, color.R, color.G, color.B));
        var full = new SolidColorBrush(color);
        soft.Freeze(); line.Freeze(); full.Freeze();
        SetValue(AccentBrushKey, full);
        SetValue(AccentSoftBrushKey, soft);
        SetValue(AccentLineBrushKey, line);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.Image, nameof(GameBadge), () => Monogram);
}
