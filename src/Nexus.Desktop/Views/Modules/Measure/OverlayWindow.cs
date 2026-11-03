using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Nexus.Measurement;
using Nexus.Measurement.Windows;

namespace Nexus.Desktop.Views.Modules.Measure;

/// <summary>
/// Mostrador de FPS: janela PRÓPRIA do NEXUS, transparente, sempre no topo, que não recebe cliques nem foco
/// (WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE). Não entra no processo do
/// jogo, não usa hooks nem injeção: por isso é seguro com anticheats. Limitação honesta: em tela cheia EXCLUSIVA o jogo
/// é dono da tela e nenhuma janela aparece por cima; em janela sem bordas (padrão do Windows 11 com as otimizações de
/// janela) funciona.
/// </summary>
public sealed class OverlaySurface : IOverlaySurface
{
    private OverlayWindow? _window;

    public void Show(OverlayCorner corner)
    {
        var app = Application.Current;
        if (app is null) return;
        if (_window is null)
        {
            _window = new OverlayWindow();
            // A janela principal fechando de vez fecha o mostrador (senão ele manteria o NEXUS aberto).
            if (app.MainWindow is { } main) main.Closed += (_, _) => Hide();
        }
        _window.Corner = corner;
        if (!_window.IsVisible) _window.Show();
        _window.Place();
    }

    public void Hide()
    {
        // Fecha (não só esconde): uma janela escondida impediria o NEXUS de encerrar quando a principal fecha.
        _window?.Close();
        _window = null;
    }

    public void Render(OverlayContent content) => _window?.Render(content);
}

internal sealed class OverlayWindow : Window
{
    private readonly TextBlock _fps = Line(22, Color.FromRgb(0xFF, 0xB0, 0x3B), true);
    private readonly TextBlock _low = Line(13, Color.FromRgb(0xE6, 0xEA, 0xF2), false);
    private readonly TextBlock _ft = Line(13, Color.FromRgb(0x6F, 0xD8, 0xF0), false);
    private readonly TextBlock _sys = Line(12, Color.FromRgb(0x9A, 0xA3, 0xB5), false);
    private readonly TextBlock _status = Line(11, Color.FromRgb(0x9A, 0xA3, 0xB5), false);

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = Loc.T("Meas.NexusMostradorFps");
        var stack = new StackPanel();
        stack.Children.Add(_fps);
        stack.Children.Add(_low);
        stack.Children.Add(_ft);
        stack.Children.Add(_sys);
        stack.Children.Add(_status);
        _status.MaxWidth = 320;
        _status.TextWrapping = TextWrapping.Wrap;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x0E, 0x13, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xB0, 0x3B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 12, 8),
            Child = stack,
        };
        SourceInitialized += (_, _) => MakeClickThrough();
        SizeChanged += (_, _) => Place();
    }

    public OverlayCorner Corner { get; set; }

    private static TextBlock Line(double size, Color color, bool bold) => new()
    {
        FontFamily = new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"),
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(color),
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black },
    };

    /// <summary>
    /// Sem quadros: a primeira linha diz o que está acontecendo de verdade (procurando o jogo, aguardando a permissão do Windows,
    /// sem permissão, jogo sem desenhar) e os números somem; com quadros: FPS grande e, embaixo, "medindo X" (o processo medido).
    /// </summary>
    public void Render(OverlayContent c)
    {
        var status = c.Status ?? (c.Waiting ? Loc.T("Meas.Live.Procurando") : null);
        var lines = status?.Split('\n', 2) ?? [];
        _fps.Text = c.Waiting ? lines.ElementAtOrDefault(0) ?? "" : c.Fps;
        _fps.FontSize = c.Waiting ? 15 : 22;
        _low.Text = c.Low1;
        _ft.Text = c.Frametime;
        _low.Visibility = _ft.Visibility = c.Waiting ? Visibility.Collapsed : Visibility.Visible;
        _sys.Text = c.System ?? "";
        _sys.Visibility = c.System is null ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = c.Waiting ? lines.ElementAtOrDefault(1) ?? "" : status ?? "";
        _status.Visibility = _status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        KeepOnTop();
    }

    /// <summary>Canto da tela principal (a área inteira do monitor: jogos sem bordas cobrem a barra de tarefas).</summary>
    public void Place()
    {
        var (x, y) = OverlayController.Position(Corner, 0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight,
            ActualWidth > 0 ? ActualWidth : 160, ActualHeight > 0 ? ActualHeight : 90);
        Left = x;
        Top = y;
    }

    private void MakeClickThrough() => OverlayNative.MakeClickThrough(new WindowInteropHelper(this).Handle);

    /// <summary>Reafirma "sempre no topo" sem roubar o foco (um jogo sem bordas pode ter subido por cima).</summary>
    private void KeepOnTop() => OverlayNative.KeepOnTop(new WindowInteropHelper(this).Handle);
}

/// <summary>
/// Atalho global (RegisterHotKey) numa janela só de mensagens. Não é hook de teclado: o Windows só avisa quando ESSA
/// combinação é apertada. Se outro programa já usa a combinação, o registro falha e a página avisa.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x4E58;
    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>Registra (trocando o anterior). Devolve false se a combinação estiver em uso por outro programa.</summary>
    public bool Register(Hotkey hotkey)
    {
        Unregister();
        _source ??= CreateSource();
        _registered = OverlayNative.RegisterHotKey(_source.Handle, Id, hotkey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null) OverlayNative.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    private HwndSource CreateSource()
    {
        var p = new HwndSourceParameters("NEXUS.Hotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        var s = new HwndSource(p);
        s.AddHook(Hook);
        return s;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == OverlayNative.WmHotkey && wParam.ToInt32() == Id) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(Hook);
        _source?.Dispose();
        _source = null;
    }
}
