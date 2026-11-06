using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Nexus.Desktop.Localization;
using Nexus.Desktop.ViewModels;
using Nexus.Desktop.Views.Controls;

namespace Nexus.Desktop.Views.Shell;

/// <summary>
/// Avisos do NEXUS no lugar da caixa de mensagem do Windows (docs/10-design-system.md, "Aviso"): cartão escuro por cima da
/// janela, com véu, título, texto em blocos (parágrafos, lista e a pergunta) e os botões SEMPRE à vista: o texto rola dentro
/// do cartão, que nunca passa da altura da janela. Mesma assinatura síncrona de antes (devolve sim/não), então nenhuma tela
/// muda: o despachante continua rodando enquanto o aviso está aberto (DispatcherFrame), como num ShowDialog.
/// <para>Teclado: Esc = Cancelar; Tab circula só dentro do aviso; o foco começa no botão principal (em "Cancelar" quando a ação
/// não tem volta). Narrador: o cartão leva o título como nome e o texto como ajuda. Animações do Windows desligadas = sem
/// movimento (ShellMotion).</para>
/// </summary>
public static class NexusDialog
{
    /// <summary>Nome da camada no MainWindow.xaml onde os avisos aparecem.</summary>
    public const string LayerName = "DialogLayer";

    /// <summary>Pergunta sim/não. Falso quando a pessoa cancela, fecha ou aperta Esc.</summary>
    public static bool Confirm(string title, string message) =>
        Show(title, message, DialogText.ToneFor(title, message), confirm: true);

    /// <summary>Aviso só com "Entendi" (erro inesperado, informação).</summary>
    public static void Alert(string title, string message, DialogTone tone = DialogTone.Error) =>
        Show(title, message, tone, confirm: false);

    private static bool Show(string title, string message, DialogTone tone, bool confirm)
    {
        var app = Application.Current;
        if (app is null) return false;
        if (!app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => Show(title, message, tone, confirm));

        var view = new DialogCard(title, message, tone, confirm);
        var layer = app.MainWindow is { IsLoaded: true, IsVisible: true } main ? main.FindName(LayerName) as Grid : null;
        return layer is null ? ShowAsWindow(view, app.MainWindow) : ShowInLayer(view, layer);
    }

    private static bool ShowInLayer(DialogCard card, Grid layer)
    {
        var previousFocus = Keyboard.FocusedElement;
        var root = new Grid { Tag = card };
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(root, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(root, KeyboardNavigationMode.Cycle);
        var scrim = new Rectangle { Fill = Brush("Brush.Scrim") };
        scrim.MouseLeftButtonDown += (_, e) => e.Handled = true; // clicar fora não fecha nem chega na página
        root.Children.Add(scrim);
        card.Margin = new Thickness(32);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(card);

        var frame = new DispatcherFrame();
        var result = false;
        card.Answered += answer =>
        {
            result = answer;
            layer.Children.Remove(root);
            if (layer.Children.Count == 0) layer.Visibility = Visibility.Collapsed;
            frame.Continue = false;
        };
        layer.Children.Add(root);
        layer.Visibility = Visibility.Visible;
        Enter(scrim, card);
        card.FocusStart();
        Dispatcher.PushFrame(frame);
        if (previousFocus is IInputElement { } back && (back as UIElement)?.IsVisible != false) back.Focus();
        return result;
    }

    /// <summary>Sem a janela principal pronta (erro na abertura): o mesmo cartão numa janela própria, sem a moldura do Windows.</summary>
    private static bool ShowAsWindow(DialogCard card, Window? owner)
    {
        card.Margin = new Thickness(24); // espaço para a sombra
        var window = new Window
        {
            Title = card.TitleText,
            Content = card,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = owner is null,
            Topmost = owner is null,
            MaxHeight = SystemParameters.WorkArea.Height * 0.9,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner is { IsVisible: true }) window.Owner = owner;
        var result = false;
        card.Answered += answer => { result = answer; window.Close(); };
        window.Loaded += (_, _) => card.FocusStart();
        card.MouseLeftButtonDown += (_, _) => TryDrag(window); // botões marcam o clique como tratado: arrastar só pelo cartão
        window.ShowDialog();
        return result;
    }

    private static void TryDrag(Window window)
    {
        try { window.DragMove(); }
        catch (InvalidOperationException) { } // botão já solto
    }

    private static void Enter(UIElement scrim, FrameworkElement card)
    {
        if (!ShellMotion.Enabled) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scrim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, ShellMotion.Fast));
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, ShellMotion.Normal) { EasingFunction = ease });
        var scale = new ScaleTransform(0.97, 0.97);
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, ShellMotion.Normal) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, ShellMotion.Normal) { EasingFunction = ease });
    }

    internal static Brush Brush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    internal static T Res<T>(string key, T fallback) => Application.Current?.TryFindResource(key) is T v ? v : fallback;
}

/// <summary>O cartão do aviso: cabeçalho (ícone + título + fechar), corpo que rola, rodapé com os botões.</summary>
internal sealed class DialogCard : Border
{
    private readonly Button _primary;
    private readonly Button? _cancel;
    private readonly DialogTone _tone;
    private bool _answered;

    public event Action<bool>? Answered;

    public string TitleText { get; }

    public DialogCard(string title, string message, DialogTone tone, bool confirm)
    {
        TitleText = title;
        _tone = tone;
        var blocks = DialogText.Parse(message);
        var compact = DialogText.IsCompact(blocks);

        MaxWidth = 620;
        MinWidth = 380;
        Background = NexusDialog.Brush("Brush.Surface");
        BorderBrush = NexusDialog.Brush("Brush.Line");
        BorderThickness = new Thickness(1);
        CornerRadius = NexusDialog.Res("Radius.Panel", new CornerRadius(14));
        Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black };
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetHelpText(this, message);
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Child = grid;

        // ---- cabeçalho
        var (iconKey, accent, soft) = tone switch
        {
            DialogTone.Danger => ("Icon.Warning", "Brush.Bad", "Brush.BadSoft"),
            DialogTone.Error => ("Icon.Error", "Brush.Bad", "Brush.BadSoft"),
            DialogTone.Info => ("Icon.Info", "Brush.Signal", "Brush.SignalSoft"),
            _ => ("Icon.Warning", "Brush.Trace", "Brush.TraceSoft"),
        };
        var header = new DockPanel { Margin = new Thickness(24, 22, 14, 6), LastChildFill = true };
        var close = new Button
        {
            Style = NexusDialog.Res<Style?>("Button.Ghost", null),
            Content = new Icon { Data = NexusDialog.Res<Geometry?>("Icon.Close", null), Size = 16, Foreground = NexusDialog.Brush("Brush.TextMuted") },
            Padding = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, -4, 0, 0),
            ToolTip = Loc.T("Dialog.Close"),
        };
        AutomationProperties.SetName(close, Loc.T("Dialog.Close"));
        close.Click += (_, _) => Answer(false);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var badge = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        badge.Children.Add(new Ellipse { Fill = NexusDialog.Brush(soft) });
        badge.Children.Add(new Icon
        {
            Data = NexusDialog.Res<Geometry?>(iconKey, null), Size = 20, Foreground = NexusDialog.Brush(accent),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(badge, Dock.Left);
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = title,
            Style = NexusDialog.Res<Style?>("Text.H2", null),
            FontSize = 19,
            Foreground = NexusDialog.Brush("Brush.Text"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(header);

        // ---- corpo (rola; o cartão nunca passa da janela)
        var body = new StackPanel { Margin = new Thickness(78, 4, 28, 18) };
        StackPanel? list = null;
        Border? listBox = null;
        foreach (var b in blocks)
        {
            if (b.Kind == DialogBlockKind.Bullet)
            {
                if (list is null)
                {
                    list = new StackPanel();
                    listBox = new Border
                    {
                        Background = NexusDialog.Brush("Brush.SurfaceLow"),
                        CornerRadius = NexusDialog.Res("Radius.Card", new CornerRadius(10)),
                        Padding = compact ? new Thickness(14, 8, 14, 8) : new Thickness(16, 12, 16, 12),
                        Margin = new Thickness(0, 2, 0, 14),
                        Child = list,
                    };
                    body.Children.Add(listBox);
                }
                list.Children.Add(BulletRow(b, compact));
                continue;
            }
            list = null;
            body.Children.Add(b.Kind == DialogBlockKind.Question
                ? Text(b.Text, 15, "Brush.Text", FontWeights.SemiBold, new Thickness(0, 4, 0, 0))
                : Text(b.Text, 15, "Brush.TextMuted", FontWeights.Normal, new Thickness(0, 0, 0, 12)));
        }
        if (body.Children.Count > 0 && body.Children[^1] is FrameworkElement lastEl)
            lastEl.Margin = new Thickness(lastEl.Margin.Left, lastEl.Margin.Top, lastEl.Margin.Right, 0);
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        // ---- rodapé: botões sempre visíveis
        var footer = new Border
        {
            Background = NexusDialog.Brush("Brush.SurfaceLow"),
            BorderBrush = NexusDialog.Brush("Brush.LineSoft"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, CornerRadius.BottomRight, CornerRadius.BottomLeft),
            Padding = new Thickness(20, 14, 20, 14),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirm)
        {
            _cancel = new Button { Content = Loc.T("Dialog.Cancel"), Style = NexusDialog.Res<Style?>("Button.Secondary", null), MinWidth = 110, IsCancel = true };
            _cancel.Click += (_, _) => Answer(false);
            buttons.Children.Add(_cancel);
        }
        _primary = new Button
        {
            Content = confirm ? Loc.T("Dialog.Continue") : Loc.T("Dialog.Ok"),
            Style = NexusDialog.Res<Style?>(tone == DialogTone.Danger ? "Button.Danger" : "Button.Primary", null),
            MinWidth = 130,
            Margin = new Thickness(0),
            IsCancel = !confirm,
        };
        _primary.Click += (_, _) => Answer(confirm);
        buttons.Children.Add(_primary);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Answer(false); e.Handled = true; }
        };
    }

    /// <summary>Foco inicial: o botão principal; "Cancelar" quando a ação não tem volta.</summary>
    public void FocusStart()
    {
        var target = _tone == DialogTone.Danger && _cancel is not null ? _cancel : _primary;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { target.Focus(); Keyboard.Focus(target); });
    }

    private void Answer(bool value)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(value);
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight weight, Thickness margin) => new()
    {
        Text = text,
        FontFamily = NexusDialog.Res<FontFamily?>("Font.Body", null) ?? new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = NexusDialog.Brush(brush),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = size * 1.45,
        Margin = margin,
    };

    private static Grid BulletRow(DialogBlock b, bool compact)
    {
        var row = new Grid { Margin = new Thickness(0, compact ? 3 : 5, 0, compact ? 3 : 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var size = compact ? 13.5 : 14.5;
        row.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, Fill = NexusDialog.Brush("Brush.Trace"),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, size * 0.55, 0, 0),
        });
        var texts = new StackPanel();
        texts.Children.Add(Text(b.Text, size, "Brush.Text", FontWeights.Normal, new Thickness(0)));
        if (b.Detail is { Length: > 0 } detail)
            texts.Children.Add(Text(detail, compact ? 12 : 12.5, "Brush.TextMuted", FontWeights.Normal, new Thickness(0, 1, 0, 0)));
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        return row;
    }
}
