using System.IO;
using System.Windows.Interop;
using Nexus.Automation;
using Nexus.Automation.Notifications;

namespace Nexus.Desktop.Services.Shell;

/// <summary>
/// Ícone do NEXUS na bandeja do sistema (Shell_NotifyIcon por P/Invoke, sem WinForms). Menu: Abrir, Otimizar agora, Verificar
/// agora, Sair. Também mostra notificações (balão, que o Windows 10/11 exibe como notificação do sistema); clicar abre o relatório.
/// Só interface: não muda nada no sistema (o que muda vai pelas páginas, com confirmação e pelo aplicador).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 17; // WM_APP + 17
    private const int CmdOpen = 1, CmdOptimize = 2, CmdScan = 3, CmdExit = 9;

    private readonly HwndSource _window;
    private readonly NotifyIconHandle _icon;
    private readonly uint _taskbarCreated;
    private string? _pendingReport;

    public event EventHandler? OpenRequested;
    public event EventHandler? OptimizeRequested;
    public event EventHandler? ScanRequested;
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        // Janela invisível de nível superior (não "só mensagens"): assim recebe "TaskbarCreated" quando o Explorador reinicia.
        _window = new HwndSource(new HwndSourceParameters("NEXUS.Tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(Hook);
        _taskbarCreated = NotifyIconHandle.TaskbarCreatedMessage();
        var exe = Environment.ProcessPath;
        _icon = NotifyIconHandle.Create(_window.Handle, 1, CallbackMessage, exe, "NEXUS");
    }

    public bool Visible => _icon.Visible;

    public void SetTip(string tip) => _icon.SetTip(tip.Length > 120 ? tip[..120] : tip);

    /// <summary>Notificação do Windows. <paramref name="report"/> = relatório HTML aberto ao clicar (só da pasta de relatórios).</summary>
    public void Notify(string title, string text, string? report = null, bool warning = false)
    {
        _pendingReport = report;
        _icon.ShowBalloon(title, text, warning);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreated && _taskbarCreated != 0)
        {
            _icon.Add();
            return IntPtr.Zero;
        }
        if (msg != (int)CallbackMessage) return IntPtr.Zero;
        handled = true;
        switch (NotifyIconHandle.EventOf(lParam))
        {
            case NotifyIconHandle.NinSelect or NotifyIconHandle.NinKeySelect or NotifyIconHandle.WmLButtonDblClk:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case NotifyIconHandle.WmContextMenu or NotifyIconHandle.WmRButtonUp:
                ShowMenu();
                break;
            case NotifyIconHandle.NinBalloonUserClick:
                var report = _pendingReport;
                _pendingReport = null;
                if (report is not null && File.Exists(report)) BalloonNotifier.OpenReport(report);
                else OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case NotifyIconHandle.NinBalloonTimeout or NotifyIconHandle.NinBalloonHide:
                _pendingReport = null;
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var choice = TrayMenu.Show(_window.Handle,
        [
            new(CmdOpen, Loc.T("Shell.AbrirNexus"), IsDefault: true),
            new(0, ""),
            new(CmdOptimize, Loc.T("Shell.OtimizarAgora")),
            new(CmdScan, Loc.T("Shell.VerificarAgoraSoLeitura")),
            new(0, ""),
            new(CmdExit, Loc.T("Shell.Sair")),
        ]);
        var handler = choice switch
        {
            CmdOpen => OpenRequested,
            CmdOptimize => OptimizeRequested,
            CmdScan => ScanRequested,
            CmdExit => ExitRequested,
            _ => null,
        };
        handler?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _icon.Dispose();
        _window.RemoveHook(Hook);
        _window.Dispose();
    }
}

/// <summary>Argumentos de linha de comando da interface: só <c>--minimized</c> (iniciar com o Windows, direto na bandeja).</summary>
public static class StartupArguments
{
    public static bool Minimized(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, AutomationStartWithWindowsHandler.MinimizedArgument, StringComparison.OrdinalIgnoreCase));
}
