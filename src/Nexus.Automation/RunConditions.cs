using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Automation;

/// <summary>Fonte de energia (testes usam um falso).</summary>
public interface IPowerSource
{
    /// <summary>true = na bateria; false = na tomada; null = não deu para saber (PC sem bateria conta como tomada).</summary>
    bool? OnBattery();
}

/// <summary>Atividade de jogo (testes usam um falso).</summary>
public interface IGameActivity
{
    /// <summary>Motivo para não rodar ("Jogo aberto: cs2", "App em tela cheia") ou null se nada indica jogo.</summary>
    string? Detect();
}

/// <summary>Decide se uma tarefa agendada pode rodar agora. Só pula; nunca muda nada.</summary>
public static class RunConditionChecker
{
    /// <summary>Null = pode rodar; senão, o motivo de pular (em português, para o relatório e a notificação).</summary>
    public static string? WhyNot(TaskConditions conditions, IPowerSource power, IGameActivity games)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(games);
        if (conditions.SkipOnBattery)
        {
            bool? battery;
            try { battery = power.OnBattery(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { battery = null; }
            if (battery == true) return "O PC está na bateria (condição: não rodar na bateria).";
        }
        if (conditions.SkipWhenGaming)
        {
            string? game;
            try { game = games.Detect(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { game = null; }
            if (game is not null) return $"{game} (condição: não rodar com jogo aberto).";
        }
        return null;
    }
}

/// <summary>Bateria pelo <c>GetSystemPowerStatus</c> (ACLineStatus 0 = bateria). Fora do Windows: desconhecido.</summary>
public sealed partial class WindowsPowerSource : IPowerSource
{
    public static readonly WindowsPowerSource Instance = new();

    public bool? OnBattery()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return Read();
    }

    [SupportedOSPlatform("windows")]
    private static bool? Read()
    {
        if (GetSystemPowerStatus(out var s) == 0) return null;
        if ((s.BatteryFlag & 128) != 0) return false; // sem bateria
        return s.ACLineStatus switch { 0 => true, 1 => false, _ => null };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>
/// Jogo aberto: (1) <c>SHQueryUserNotificationState</c> diz que há app em tela cheia (Direct3D exclusivo, "ocupado" = tela cheia
/// sem borda, ou modo apresentação) — é o mesmo sinal que o Windows usa para segurar notificações; (2) um processo de uma lista
/// curta de jogos e anticheats conhecidos. Somente leitura.
/// </summary>
public sealed partial class WindowsGameActivity : IGameActivity
{
    public static readonly WindowsGameActivity Instance = new();

    /// <summary>Processos de jogos populares (sem .exe). Lista curta e conservadora; a detecção de tela cheia cobre o resto.</summary>
    public static readonly IReadOnlyList<string> KnownGames =
    [
        "cs2", "csgo", "VALORANT-Win64-Shipping", "FortniteClient-Win64-Shipping", "r5apex", "League of Legends", "dota2",
        "GTA5", "GTA5_Enhanced", "RDR2", "eldenring", "Overwatch", "cod", "TslGame", "EscapeFromTarkov", "Cyberpunk2077",
        "bg3", "bg3_dx11", "Starfield", "Minecraft.Windows", "RainbowSix", "destiny2", "Warframe.x64", "RocketLeague",
        "MonsterHunterWilds", "HELLDIVERS2", "FC25", "eFootball", "Marvel-Win64-Shipping", "deadlock",
    ];

    private readonly Func<IEnumerable<string>> _processes;
    private readonly Func<int?> _notificationState;

    public WindowsGameActivity() : this(RunningProcesses, ReadNotificationState) { }

    /// <param name="processes">Nomes dos processos em execução (sem .exe).</param>
    /// <param name="notificationState">QUERY_USER_NOTIFICATION_STATE (null = não lido).</param>
    public WindowsGameActivity(Func<IEnumerable<string>> processes, Func<int?> notificationState)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _notificationState = notificationState ?? throw new ArgumentNullException(nameof(notificationState));
    }

    public string? Detect()
    {
        var state = _notificationState();
        switch (state)
        {
            case 3: return "Um jogo em tela cheia está aberto (Direct3D)";
            case 2: return "Um app está em tela cheia";
            case 4: return "O Windows está em modo de apresentação";
        }
        var running = new HashSet<string>(_processes(), StringComparer.OrdinalIgnoreCase);
        var game = KnownGames.FirstOrDefault(running.Contains);
        return game is null ? null : $"Jogo aberto: {game}";
    }

    private static IEnumerable<string> RunningProcesses()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return []; }
        var names = new List<string>(all.Length);
        foreach (var p in all)
        {
            try { names.Add(p.ProcessName); }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return names;
    }

    private static int? ReadNotificationState()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return SHQueryUserNotificationState(out var s) == 0 ? s : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
