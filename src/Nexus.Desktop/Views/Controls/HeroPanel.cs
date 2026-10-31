using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Abertura de página: ilustração do módulo + título curto + 1 número grande que conta + uma linha de legenda + ação principal
/// (e uma secundária opcional). O conteúdo (Content) entra à direita: um RingMeter, um BeforeAfter, um aviso.
/// Enquanto <see cref="IsLoading"/> é verdadeiro, o número vira um esqueleto pulsando.
/// <code>
/// &lt;c:HeroPanel Illustration="{StaticResource Illo.Cleaning}" Title="Dá para liberar"
///     Number="{Binding RecoverableBytes}" Kind="Bytes" Caption="Medido agora. Tudo volta se precisar."
///     ActionText="Limpar selecionados" Command="{Binding CleanCommand}"
///     SecondaryText="Desfazer" SecondaryCommand="{Binding UndoLastCommand}" IsLoading="{Binding Scanning}"/&gt;
/// </code>
/// </summary>
public class HeroPanel : ContentControl
{
    public static readonly DependencyProperty IllustrationProperty = DependencyProperty.Register(nameof(Illustration), typeof(ImageSource), typeof(HeroPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(HeroPanel), new PropertyMetadata(""));
    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(double), typeof(HeroPanel), new PropertyMetadata(double.NaN, OnShapeChanged));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(NumberKind), typeof(HeroPanel), new PropertyMetadata(NumberKind.Number));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(HeroPanel), new PropertyMetadata(-1));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(HeroPanel), new PropertyMetadata(""));
    /// <summary>Valor em texto quando não há número ("Tudo em ordem"). Vence <see cref="Number"/>.</summary>
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(HeroPanel), new PropertyMetadata("", OnShapeChanged));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(HeroPanel), new PropertyMetadata(""));
    /// <summary>Cor do número (padrão: âmbar, a medida).</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(HeroPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(HeroPanel), new PropertyMetadata(""));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(HeroPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(HeroPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty SecondaryTextProperty = DependencyProperty.Register(nameof(SecondaryText), typeof(string), typeof(HeroPanel), new PropertyMetadata(""));
    public static readonly DependencyProperty SecondaryCommandProperty = DependencyProperty.Register(nameof(SecondaryCommand), typeof(ICommand), typeof(HeroPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty SecondaryCommandParameterProperty = DependencyProperty.Register(nameof(SecondaryCommandParameter), typeof(object), typeof(HeroPanel), new PropertyMetadata(null));
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(HeroPanel), new PropertyMetadata(false, OnShapeChanged));

    private static readonly DependencyPropertyKey ShowsNumberKey = DependencyProperty.RegisterReadOnly(nameof(ShowsNumber), typeof(bool), typeof(HeroPanel), new PropertyMetadata(false));
    private static readonly DependencyPropertyKey ShowsTextKey = DependencyProperty.RegisterReadOnly(nameof(ShowsText), typeof(bool), typeof(HeroPanel), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowsNumberProperty = ShowsNumberKey.DependencyProperty;
    public static readonly DependencyProperty ShowsTextProperty = ShowsTextKey.DependencyProperty;

    public HeroPanel()
    {
        Focusable = false;
    }

    public ImageSource? Illustration { get => (ImageSource?)GetValue(IllustrationProperty); set => SetValue(IllustrationProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public double Number { get => (double)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public NumberKind Kind { get => (NumberKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public string SecondaryText { get => (string)GetValue(SecondaryTextProperty); set => SetValue(SecondaryTextProperty, value); }
    public ICommand? SecondaryCommand { get => (ICommand?)GetValue(SecondaryCommandProperty); set => SetValue(SecondaryCommandProperty, value); }
    public object? SecondaryCommandParameter { get => GetValue(SecondaryCommandParameterProperty); set => SetValue(SecondaryCommandParameterProperty, value); }
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public bool ShowsNumber => (bool)GetValue(ShowsNumberProperty);
    public bool ShowsText => (bool)GetValue(ShowsTextProperty);

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var h = (HeroPanel)d;
        var text = !h.IsLoading && !string.IsNullOrEmpty(h.ValueText);
        h.SetValue(ShowsTextKey, text);
        h.SetValue(ShowsNumberKey, !h.IsLoading && !text && double.IsFinite(h.Number));
    }
}
