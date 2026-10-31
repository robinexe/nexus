using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Processos e janela em primeiro plano, só leitura. Para saber a pasta do executável usa OpenProcess com
/// PROCESS_QUERY_LIMITED_INFORMATION (o mesmo acesso do Gerenciador de Tarefas): não lê memória, não lista módulos, não
/// injeta nada. Isso mantém o NEXUS fora do caminho de anticheats (Vanguard, Easy Anti-Cheat, BattlEye).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsProcessProbe : IProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private readonly Dictionary<int, (string Name, string? Path)> _paths = [];

    public ProcessSnapshot Read()
    {
        var list = new List<RunningProcess>();
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                int pid;
                string name;
                try { pid = p.Id; name = p.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (pid <= 4) continue;
                seen.Add(pid);
                if (!_paths.TryGetValue(pid, out var cached) || !cached.Name.Equals(name, StringComparison.Ordinal))
                {
                    cached = (name, ImagePath(pid));
                    _paths[pid] = cached;
                }
                list.Add(new RunningProcess(pid, name + ".exe", cached.Path));
            }
        }
        foreach (var gone in _paths.Keys.Where(k => !seen.Contains(k)).ToList()) _paths.Remove(gone);
        return new ProcessSnapshot(list, Foreground());
    }

    public bool IsAlive(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out var code) && code == StillActive; }
        finally { CloseHandle(h); }
    }

    private static unsafe string? ImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Janela em primeiro plano: PID, tamanho da área cliente e se cobre o monitor inteiro.</summary>
    public static ForegroundWindow? Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || !GetClientRect(hwnd, out var client) || !GetWindowRect(hwnd, out var win)) return null;
        var covers = false;
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
            covers = win.Left <= info.Monitor.Left && win.Top <= info.Monitor.Top && win.Right >= info.Monitor.Right && win.Bottom >= info.Monitor.Bottom;
        return new ForegroundWindow((int)pid, client.Right - client.Left, client.Bottom - client.Top, covers);
    }

    /// <summary>Notebook na bateria (null = sem bateria ou não informado).</summary>
    public static bool? OnBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        return s.AcLineStatus switch { 0 => true, 1 => false, _ => null };
    }

    /// <summary>
    /// A conta pode rastrear sem pedir permissão: processo elevado (administrador) ou membro do grupo
    /// "Usuários de log de desempenho" (S-1-5-32-559). Sem isso, o PresentMon precisa pedir permissão ao Windows.
    /// </summary>
    public static bool CanTraceWithoutElevation()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator)) return true;
            var perfLogUsers = new SecurityIdentifier("S-1-5-32-559");
            return id.Groups?.Any(g => g.Equals(perfLogUsers)) == true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr h, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(IntPtr h, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
}

/// <summary>Raízes das bibliotecas neste PC (pasta da Steam no registro do usuário, %ProgramData%, unidades fixas).</summary>
[SupportedOSPlatform("windows")]
public static class WindowsGameRoots
{
    public static GameLibrary.Roots Read()
    {
        string? steam = null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var drives = new List<string>();
        try
        {
            drives.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new GameLibrary.Roots(steam, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), drives);
    }
}
