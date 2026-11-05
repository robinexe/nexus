using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.NetRoute.Profiles;

namespace Nexus.NetRoute.Windows;

/// <summary>Jogo em execução: processo, perfil (especial ou genérico) e o id usado nos blocos aprendidos e no túnel.</summary>
public sealed record RunningGame(int ProcessId, string ProcessName, GameNetProfile Profile, string GameId);

/// <summary>
/// Acha o jogo: primeiro os perfis especiais (VALORANT, CS2, Fortnite) em execução; senão, o processo da janela em primeiro
/// plano, se não for navegador/chat/lançador (<see cref="GameNetProfiles.NeverGame"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class GameProcess
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public static RunningGame? Find(string? preferredProfileId = null)
    {
        foreach (var profile in GameNetProfiles.Special.Where(p => preferredProfileId is null || p.Id == preferredProfileId))
            foreach (var exe in profile.Processes)
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
                try
                {
                    if (procs.FirstOrDefault() is { } p) return new RunningGame(p.Id, p.ProcessName, profile, profile.Id);
                }
                finally { foreach (var x in procs) x.Dispose(); }
            }
        if (preferredProfileId is not null && preferredProfileId != GameNetProfiles.GenericId) return null;
        return Foreground();
    }

    public static RunningGame? Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            if (GameNetProfiles.NeverGame.Contains(name)) return null;
            var special = GameNetProfiles.ForProcess(name);
            return special is not null
                ? new RunningGame(p.Id, name, special, special.Id)
                : new RunningGame(p.Id, name, GameNetProfiles.Generic, GameNetProfiles.GenericIdFor(name));
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Todos os PIDs com o mesmo nome do jogo (jogos que abrem mais de um processo, como lançador + cliente).</summary>
    public static HashSet<int> ProcessIds(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try { return procs.Select(p => p.Id).ToHashSet(); }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}
