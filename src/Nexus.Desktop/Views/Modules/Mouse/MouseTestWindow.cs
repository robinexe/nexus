using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.Desktop.Localization;
using Nexus.Measurement;
using Nexus.Measurement.Windows;

namespace Nexus.Desktop.Views.Modules.Mouse;

/// <summary>
/// Teste do mouse (só diagnóstico): "mexa o mouse em círculos por 5 s". Recebe cada relatório do mouse por Raw Input (WM_INPUT)
/// só enquanto esta janela está em primeiro plano, anota o instante de chegada e mostra a taxa real (Hz) e a irregularidade.
/// Não muda nada no mouse nem no Windows. Janela feita em código (sem XAML) para ficar autocontida.
/// </summary>
public sealed class MouseTestWindow : Window
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private readonly List<long> _stamps = new(64 * 1024);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly TextBlock _countdown = new();
    private readonly TextBlock _result = new();
    private readonly TextBlock _detail = new();
    private readonly Button _start = new();
    private HwndSource? _source;
    private bool _recording;
    private bool _registered;
    private long _startedAt;

    public MouseTestWindow()
    {
        Title = Loc.T("Mouse.Title");
        Width = 560;
        Height = 460;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("Brush.Bg", Brushes.Black);
        Foreground = Res("Brush.Text", Brushes.White);

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock { Text = Loc.T("Mouse.Title"), FontSize = 24, FontFamily = Font("Font.Display"), Foreground = Foreground });
        panel.Children.Add(new TextBlock { Text = Loc.T("Mouse.Lead"), FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16),
            Foreground = Res("Brush.TextMuted", Brushes.Gray) });
        _countdown.FontSize = 40;
        _countdown.FontFamily = Font("Font.Numbers");
        _countdown.Foreground = Res("Brush.Trace", Brushes.Orange);
        _countdown.HorizontalAlignment = HorizontalAlignment.Center;
        _countdown.Text = Duration.TotalSeconds.ToString("0.0", Loc.Culture) + " s";
        panel.Children.Add(_countdown);
        _start.Content = Loc.T("Mouse.Start");
        _start.HorizontalAlignment = HorizontalAlignment.Center;
        _start.Margin = new Thickness(0, 12, 0, 16);
        _start.Padding = new Thickness(22, 10, 22, 10);
        if (TryFindResource("Button.Primary") is Style primary) _start.Style = primary;
        _start.Click += (_, _) => Begin();
        panel.Children.Add(_start);
        _result.FontSize = 18;
        _result.TextWrapping = TextWrapping.Wrap;
        _result.Foreground = Foreground;
        panel.Children.Add(_result);
        _detail.FontSize = 13;
        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.Margin = new Thickness(0, 10, 0, 0);
        _detail.Foreground = Res("Brush.TextDim", Brushes.Gray);
        _detail.Text = Loc.T("Mouse.Honest");
        panel.Children.Add(_detail);
        Content = panel;

        _timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) => Attach();
        Closed += (_, _) => Detach();
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    private FontFamily? Font(string key) => TryFindResource(key) as FontFamily;

    private void Attach()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        _registered = RawMouseInput.Register(hwnd);
        if (!_registered)
        {
            _start.IsEnabled = false;
            _result.Text = Loc.T("Mouse.Unavailable");
        }
    }

    private void Detach()
    {
        _timer.Stop();
        _source?.RemoveHook(WndProc);
        if (_registered && OperatingSystem.IsWindows()) RawMouseInput.Unregister();
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // handled continua false: o WPF repassa ao DefWindowProc, que libera o buffer do relatório.
        if (msg == RawMouseInput.WmInput && _recording && OperatingSystem.IsWindows() && RawMouseInput.IsMouseMove(lParam))
            _stamps.Add(Stopwatch.GetTimestamp());
        return IntPtr.Zero;
    }

    private void Begin()
    {
        _stamps.Clear();
        _result.Text = "";
        _start.IsEnabled = false;
        _recording = true;
        _startedAt = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    private void Tick()
    {
        var elapsed = Stopwatch.GetElapsedTime(_startedAt);
        var left = Duration - elapsed;
        _countdown.Text = Math.Max(0, left.TotalSeconds).ToString("0.0", Loc.Culture) + " s";
        if (left > TimeSpan.Zero) return;
        _recording = false;
        _timer.Stop();
        _start.IsEnabled = true;
        _start.Content = Loc.T("Mouse.Again");
        Show(MousePollingAnalyzer.Analyze(_stamps, Stopwatch.Frequency));
    }

    private void Show(MousePollingResult r)
    {
        _result.Text = Describe(r);
        _detail.Text = Loc.T("Mouse.Honest");
    }

    /// <summary>Frases do resultado (a lógica é de <see cref="MousePollingAnalyzer"/>).</summary>
    internal static string Describe(MousePollingResult r)
    {
        if (r.Verdict == MousePollingVerdict.NotEnoughData) return Loc.T("Mouse.NotEnough");
        var rate = r.RateHz.ToString("0", Loc.Culture);
        var head = r.NominalHz > 0 ? Loc.F("Mouse.RateNominal", rate, r.NominalHz.ToString("0", Loc.Culture)) : Loc.F("Mouse.Rate", rate);
        var jitter = Loc.F("Mouse.Jitter", r.JitterMs.ToString("0.00", Loc.Culture), r.P99Ms.ToString("0.00", Loc.Culture));
        var verdict = r.Verdict == MousePollingVerdict.Stable ? Loc.T("Mouse.Stable") : Loc.T("Mouse.Irregular");
        return head + "\n" + jitter + "\n" + verdict;
    }
}
