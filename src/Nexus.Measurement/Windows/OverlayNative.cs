using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Chamadas do Windows usadas pelo mostrador de FPS e pelo atalho global. Só mexem em janelas do PRÓPRIO NEXUS:
/// estilo estendido (não recebe cliques/foco), "sempre no topo" e RegisterHotKey (não é hook de teclado).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class OverlayNative
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExTopmost = 0x8, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    public const int WmHotkey = 0x0312;

    /// <summary>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE.</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(style | WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate));
    }

    /// <summary>Reafirma "sempre no topo" sem mover, redimensionar nem ativar.</summary>
    public static void KeepOnTop(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    public static bool RegisterHotKey(IntPtr hwnd, int id, Hotkey hotkey) =>
        RegisterHotKeyNative(hwnd, id, (uint)hotkey.Modifiers | 0x4000 /* MOD_NOREPEAT */, (uint)hotkey.VirtualKey);

    public static void UnregisterHotKey(IntPtr hwnd, int id) => UnregisterHotKeyNative(hwnd, id);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKeyNative(IntPtr hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKeyNative(IntPtr hwnd, int id);
}
