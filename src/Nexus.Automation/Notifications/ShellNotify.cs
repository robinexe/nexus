using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Automation.Notifications;

/// <summary>
/// Ícone da área de notificação (bandeja) por <c>Shell_NotifyIconW</c>, sem WinForms nem WinRT. O balão (NIF_INFO) aparece no
/// Windows 10/11 como notificação do sistema (toast) e vai para a central de notificações; clique = NIN_BALLOONUSERCLICK.
/// Só interface: não muda nada no sistema. Usado pela bandeja da interface e pela notificação da linha de comando agendada.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class NotifyIconHandle : IDisposable
{
    public const int NinSelect = 0x400;
    public const int NinKeySelect = 0x401;
    public const int NinBalloonShow = 0x402;
    public const int NinBalloonHide = 0x403;
    public const int NinBalloonTimeout = 0x404;
    public const int NinBalloonUserClick = 0x405;
    public const int WmContextMenu = 0x007B;
    public const int WmLButtonUp = 0x0202;
    public const int WmLButtonDblClk = 0x0203;
    public const int WmRButtonUp = 0x0205;

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifInfo = 0x1, NiifWarning = 0x2;
    private const uint NotifyIconVersion4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint CbSize;
        public nint HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        public fixed char SzTip[128];
        public uint DwState;
        public uint DwStateMask;
        public fixed char SzInfo[256];
        public uint UVersion;
        public fixed char SzInfoTitle[64];
        public uint DwInfoFlags;
        public Guid GuidItem;
        public nint HBalloonIcon;
    }

    private readonly nint _hwnd;
    private readonly uint _id;
    private readonly uint _callback;
    private nint _icon;
    private string _tip;
    private bool _added;

    /// <param name="hwnd">Janela que recebe as mensagens do ícone.</param>
    /// <param name="callbackMessage">Mensagem (WM_APP + n) enviada à janela; com a versão 4, LOWORD(lParam) é o evento.</param>
    public NotifyIconHandle(nint hwnd, uint id, uint callbackMessage, nint icon, string tip)
    {
        _hwnd = hwnd;
        _id = id;
        _callback = callbackMessage;
        _icon = icon;
        _tip = tip ?? "";
        Add();
    }

    public bool Visible => _added;

    /// <summary>(Re)adiciona o ícone; use também quando o Explorador reinicia (mensagem "TaskbarCreated").</summary>
    public bool Add()
    {
        var d = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        Shell_NotifyIconW(NimDelete, ref d); // sem efeito se não existia
        _added = Shell_NotifyIconW(NimAdd, ref d) != 0;
        if (_added)
        {
            d.UVersion = NotifyIconVersion4;
            Shell_NotifyIconW(NimSetVersion, ref d);
        }
        return _added;
    }

    public void SetTip(string tip)
    {
        _tip = tip ?? "";
        var d = Data(NifTip | NifShowTip);
        Shell_NotifyIconW(NimModify, ref d);
    }

    /// <summary>Mostra o balão (notificação do Windows). Retorna false se o Windows recusou.</summary>
    public bool ShowBalloon(string title, string text, bool warning = false)
    {
        var d = Data(NifInfo);
        Copy(title, d.SzInfoTitle, 64);
        Copy(string.IsNullOrEmpty(text) ? " " : text, d.SzInfo, 256);
        d.DwInfoFlags = warning ? NiifWarning : NiifInfo;
        return Shell_NotifyIconW(NimModify, ref d) != 0;
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIconW(NimDelete, ref d);
            _added = false;
        }
        if (_icon != 0 && _ownsIcon) { DestroyIcon(_icon); _icon = 0; }
    }

    private bool _ownsIcon;

    /// <summary>Ícone do executável (o primeiro, pequeno) ou o ícone padrão de aplicativo. O handle passa a ser deste objeto.</summary>
    public static NotifyIconHandle Create(nint hwnd, uint id, uint callbackMessage, string? iconFrom, string tip)
    {
        nint icon = 0;
        var owns = false;
        if (!string.IsNullOrEmpty(iconFrom) && File.Exists(iconFrom))
        {
            nint small = 0;
            if (ExtractIconExW(iconFrom, 0, null, &small, 1) > 0 && small != 0) { icon = small; owns = true; }
        }
        if (icon == 0) icon = LoadIconW(0, (nint)32512); // IDI_APPLICATION (compartilhado, não destruir)
        return new NotifyIconHandle(hwnd, id, callbackMessage, icon, tip) { _ownsIcon = owns };
    }

    /// <summary>Mensagem que o Explorador envia a todas as janelas quando a barra de tarefas é recriada (re-adicionar o ícone).</summary>
    public static uint TaskbarCreatedMessage() => RegisterWindowMessageW("TaskbarCreated");

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string message);

    /// <summary>Evento (LOWORD de lParam) de uma mensagem do ícone na versão 4.</summary>
    public static int EventOf(nint lParam) => (int)(lParam & 0xFFFF);

    private NotifyIconData Data(uint flags)
    {
        var d = new NotifyIconData
        {
            CbSize = (uint)sizeof(NotifyIconData),
            HWnd = _hwnd,
            UId = _id,
            UFlags = flags,
            UCallbackMessage = _callback,
            HIcon = _icon,
        };
        Copy(_tip, d.SzTip, 128);
        return d;
    }

    private static void Copy(string text, char* buffer, int capacity)
    {
        var n = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < n; i++) buffer[i] = text[i];
        buffer[n] = '\0';
    }

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string file, int index, nint* large, nint* small, uint count);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}

/// <summary>Menu de contexto nativo da bandeja (TrackPopupMenuEx), com o comportamento padrão do Windows (fecha ao clicar fora).</summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class TrayMenu
{
    private const uint MfString = 0x0, MfSeparator = 0x800, MfGrayed = 0x1;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2, TpmNoNotify = 0x80, TpmBottomAlign = 0x20;

    /// <summary>Item do menu: Id 0 = separador.</summary>
    public sealed record Item(int Id, string Text, bool IsDefault = false, bool Enabled = true);

    /// <summary>Mostra o menu no cursor e devolve o id escolhido (0 = nada).</summary>
    public static int Show(nint hwnd, IReadOnlyList<Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var menu = CreatePopupMenu();
        if (menu == 0) return 0;
        try
        {
            foreach (var i in items)
            {
                if (i.Id == 0) AppendMenuW(menu, MfSeparator, 0, null);
                else AppendMenuW(menu, MfString | (i.Enabled ? 0 : MfGrayed), (nuint)i.Id, i.Text);
                if (i.IsDefault) SetMenuDefaultItem(menu, (uint)i.Id, 0);
            }
            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd); // sem isto o menu não fecha ao clicar fora (documentado em TrackPopupMenu)
            var id = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmNoNotify | TpmBottomAlign, pt.X, pt.Y, hwnd, 0);
            PostMessageW(hwnd, 0, 0, 0); // WM_NULL
            return id;
        }
        finally { DestroyMenu(menu); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);
}

/// <summary>
/// Notificação de um processo sem janela (a linha de comando agendada): cria uma janela invisível só de mensagens, mostra o
/// balão, espera o clique ou o fim (até <c>wait</c>) e remove o ícone. Clique = abre o relatório HTML (só arquivo .html dentro da
/// pasta de relatórios do NEXUS).
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class BalloonNotifier
{
    private const uint CallbackMessage = 0x8000 + 41; // WM_APP + 41
    private static int _event;

    /// <summary>Mostra e espera. Devolve true se o usuário clicou (e o relatório foi aberto).</summary>
    public static bool Show(string title, string text, string? reportToOpen, string iconFrom, TimeSpan wait, bool warning = false)
    {
        var className = "NEXUS.Notify." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hInstance = GetModuleHandleW(null);
        fixed (char* name = className)
        {
            var wc = new WndClassEx
            {
                CbSize = (uint)sizeof(WndClassEx),
                LpfnWndProc = &WndProc,
                HInstance = hInstance,
                LpszClassName = name,
            };
            if (RegisterClassExW(ref wc) == 0) return false;
            try
            {
                var hwnd = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, hInstance, 0);
                if (hwnd == 0) return false;
                try
                {
                    using var icon = NotifyIconHandle.Create(hwnd, 1, CallbackMessage, iconFrom, "NEXUS");
                    if (!icon.Visible || !icon.ShowBalloon(title, text, warning)) return false;
                    _event = 0;
                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < wait && _event is not (NotifyIconHandle.NinBalloonUserClick or NotifyIconHandle.NinBalloonTimeout or NotifyIconHandle.NinBalloonHide))
                    {
                        if (PeekMessageW(out var msg, 0, 0, 0, 1 /* PM_REMOVE */))
                        {
                            TranslateMessage(ref msg);
                            DispatchMessageW(ref msg);
                        }
                        else Thread.Sleep(50);
                    }
                    if (_event == NotifyIconHandle.NinBalloonUserClick && reportToOpen is not null) return OpenReport(reportToOpen);
                    return false;
                }
                finally { DestroyWindow(hwnd); }
            }
            finally { UnregisterClassW(name, hInstance); }
        }
    }

    /// <summary>Abre um relatório HTML do NEXUS no navegador padrão (só .html dentro da pasta de relatórios).</summary>
    public static bool OpenReport(string path)
    {
        if (!ReportFiles.IsReportFile(path, ScanReport.DefaultDirectory)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { return false; }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            _event = NotifyIconHandle.EventOf(lParam);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public char* LpszMenuName;
        public char* LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint HWnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(ref WndClassEx wc);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClassW(char* name, nint instance);
    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(ref Msg msg);
}

/// <summary>Solta o console quando a tarefa agendada roda (a janela que o Agendador abre some na hora).</summary>
public static partial class ConsoleDetach
{
    public static void Detach()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Só se o console é só nosso (aberto pelo Agendador); num terminal do usuário, fica.
            var ids = new uint[4];
            if (GetConsoleProcessList(ids, (uint)ids.Length) > 1) return;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            FreeConsole();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException) { }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList([Out] uint[] ids, uint count);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
}

/// <summary>Quais arquivos a notificação pode abrir (lógica pura, testável em qualquer sistema).</summary>
public static class ReportFiles
{
    /// <summary>Relatório do NEXUS: arquivo .html existente, direto dentro da pasta de relatórios (sem subpastas, sem links).</summary>
    public static bool IsReportFile(string path, string reportsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(reportsDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(full);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { return false; }
    }
}
