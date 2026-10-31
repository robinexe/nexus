using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nexus.Desktop.Views.Controls;

/// <summary>
/// Cartão de um número: ícone (ou ilustração), número grande, rótulo curto, detalhe de 1 linha e tendência opcional.
/// Com <c>Command</c>, vira um botão (foco, Enter, hover); sem, é só leitura e não recebe foco.
/// <code>&lt;c:StatTile Illustration="{StaticResource Illo.Cpu}" Number="6" Suffix=" núcleos" Label="Processador"
///     Detail="Ryzen 5 5600H" Command="{x:Static shell:ShellCommands.Navigate}" CommandParameter="CpuLabPage"/&gt;</code>
/// </summary>
public class StatTile : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(StatTile), new PropertyMetadata(null));
    public static readonly DependencyProperty IllustrationProperty = DependencyProperty.Register(nameof(Illustration), typeof(ImageSource), typeof(StatTile), new PropertyMetadata(null));
    /// <summary>Número animado (NaN = usar <see cref="Value"/> como texto).</summary>
    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(double), typeof(StatTile), new PropertyMetadata(double.NaN, OnShapeChanged));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(NumberKind), typeof(StatTile), new PropertyMetadata(NumberKind.Number));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(StatTile), new PropertyMetadata(-1));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    /// <summary>Valor em texto, quando não é um número ("NVMe", "—").</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    /// <summary>Tendência curta ("+3%", "1 de 2 slots"); a cor vem de <see cref="TrendTone"/>.</summary>
    public static readonly DependencyProperty TrendProperty = DependencyProperty.Register(nameof(Trend), typeof(string), typeof(StatTile), new PropertyMetadata(""));
    public static readonly DependencyProperty TrendToneProperty = DependencyProperty.Register(nameof(TrendTone), typeof(Tone), typeof(StatTile), new PropertyMetadata(Tone.Neutral));
    /// <summary>Cor do número (Neutral = cor do texto).</summary>
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(Tone), typeof(StatTile), new PropertyMetadata(Tone.Neutral));

    private static readonly DependencyPropertyKey ShowsNumberKey = DependencyProperty.RegisterReadOnly(nameof(ShowsNumber), typeof(bool), typeof(StatTile), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowsNumberProperty = ShowsNumberKey.DependencyProperty;

    static StatTile()
    {
        CommandProperty.OverrideMetadata(typeof(StatTile), new FrameworkPropertyMetadata(null, (d, _) => ((StatTile)d).UpdateInteractive()));
    }

    public StatTile()
    {
        UpdateInteractive();
    }

    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public ImageSource? Illustration { get => (ImageSource?)GetValue(IllustrationProperty); set => SetValue(IllustrationProperty, value); }
    public double Number { get => (double)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public NumberKind Kind { get => (NumberKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public string Trend { get => (string)GetValue(TrendProperty); set => SetValue(TrendProperty, value); }
    public Tone TrendTone { get => (Tone)GetValue(TrendToneProperty); set => SetValue(TrendToneProperty, value); }
    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public bool ShowsNumber => (bool)GetValue(ShowsNumberProperty);

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.SetValue(ShowsNumberKey, double.IsFinite(((StatTile)d).Number));

    private void UpdateInteractive()
    {
        var interactive = Command is not null;
        Focusable = interactive;
        IsTabStop = interactive;
        Cursor = interactive ? Cursors.Hand : Cursors.Arrow;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, Command is null ? AutomationControlType.Text : AutomationControlType.Button, nameof(StatTile),
            () => $"{Label}: {(ShowsNumber ? DesignMath.Format(Number, Kind, Decimals) + Suffix : Value)}. {Detail}".TrimEnd(' ', '.'));
}

/// <summary>
/// Selo de estado com ícone: Good = ok (verde, ✓), Warn = atenção (âmbar, !), Bad = problema (vermelho, ×), Info (ciano).
/// <code>&lt;c:StatusChip Tone="Good" Text="Tudo em ordem"/&gt;</code>
/// </summary>
public class StatusChip : Control
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(Tone), typeof(StatusChip),
        new PropertyMetadata(Tone.Info, (d, _) => ((StatusChip)d).UpdateTone()));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusChip), new PropertyMetadata(""));
    /// <summary>Ícone próprio (padrão: o do tom).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(StatusChip),
        new PropertyMetadata(null, (d, _) => ((StatusChip)d).UpdateTone()));

    private static readonly DependencyPropertyKey ToneBrushKey = DependencyProperty.RegisterReadOnly(nameof(ToneBrush), typeof(Brush), typeof(StatusChip), new PropertyMetadata(Brushes.Gray));
    private static readonly DependencyPropertyKey ToneSoftBrushKey = DependencyProperty.RegisterReadOnly(nameof(ToneSoftBrush), typeof(Brush), typeof(StatusChip), new PropertyMetadata(Brushes.Transparent));
    private static readonly DependencyPropertyKey ToneIconKey = DependencyProperty.RegisterReadOnly(nameof(ToneIcon), typeof(Geometry), typeof(StatusChip), new PropertyMetadata(null));
    public static readonly DependencyProperty ToneBrushProperty = ToneBrushKey.DependencyProperty;
    public static readonly DependencyProperty ToneSoftBrushProperty = ToneSoftBrushKey.DependencyProperty;
    public static readonly DependencyProperty ToneIconProperty = ToneIconKey.DependencyProperty;

    public StatusChip()
    {
        Focusable = false;
        Loaded += (_, _) => UpdateTone();
        UpdateTone();
    }

    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush ToneBrush => (Brush)GetValue(ToneBrushProperty);
    public Brush ToneSoftBrush => (Brush)GetValue(ToneSoftBrushProperty);
    public Geometry? ToneIcon => (Geometry?)GetValue(ToneIconProperty);

    private void UpdateTone()
    {
        SetValue(ToneBrushKey, Theme.ToneBrush(Tone));
        SetValue(ToneSoftBrushKey, Theme.ToneSoftBrush(Tone));
        SetValue(ToneIconKey, Icon ?? Theme.Icon(DesignMath.IconKey(Tone)));
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new DescribedPeer(this, AutomationControlType.Text, nameof(StatusChip), () => Text);
}

/// <summary>
/// Estado vazio: ilustração, uma frase e um botão que diz o que fazer.
/// <code>&lt;c:EmptyState Illustration="{StaticResource Illo.Cleaning}" Text="Nenhuma limpeza ainda."
///     ActionText="Medir agora" Command="{Binding RescanCommand}"/&gt;</code>
/// </summary>
public class EmptyState : Control
{
    public static readonly DependencyProperty IllustrationProperty = DependencyProperty.Register(nameof(Illustration), typeof(ImageSource), typeof(EmptyState), new PropertyMetadata(null));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(EmptyState), new PropertyMetadata(null));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

    public EmptyState()
    {
        Focusable = false;
    }

    public ImageSource? Illustration { get => (ImageSource?)GetValue(IllustrationProperty); set => SetValue(IllustrationProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
}
