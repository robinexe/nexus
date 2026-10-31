using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Nexus.Desktop.Views.Shell;

/// <summary>
/// Barra de título escura (DWM), para a moldura do Windows combinar com o tema. Só aparência; falha em silêncio em versões do
/// Windows que não têm os atributos.
/// </summary>
public static class DarkTitleBar
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmBorderColor = 34;

    /// <summary>Aplica à janela já inicializada (chamar em SourceInitialized).</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var on = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        // COLORREF 0x00BBGGRR: #111726 (C.SurfaceLow) na legenda e #1E2638 (C.LineSoft) na borda (Windows 11).
        var caption = 0x00261711;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
        var border = 0x0038261E;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
