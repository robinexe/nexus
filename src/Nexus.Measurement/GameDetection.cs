namespace Nexus.Measurement;

/// <summary>Um processo em execução. <see cref="Path"/> vem de QueryFullProcessImageName com acesso LIMITADO (não lê memória do jogo); null = sem acesso.</summary>
public sealed record RunningProcess(int Pid, string Name, string? Path);

/// <summary>A janela em primeiro plano: de qual processo, tamanho da área do jogo e se cobre o monitor inteiro.</summary>
/// <param name="CoversMonitor">A janela ocupa o monitor inteiro (tela cheia ou janela sem bordas).</param>
public sealed record ForegroundWindow(int Pid, int Width, int Height, bool CoversMonitor);

/// <summary>Uma leitura do sistema para o detector (processos + janela em primeiro plano).</summary>
public sealed record ProcessSnapshot(IReadOnlyList<RunningProcess> Processes, ForegroundWindow? Foreground);

/// <summary>Por que o NEXUS acha que é um jogo (mostrado na página: nada de mágica).</summary>
public enum DetectionReason { UserList, Library, BuiltIn, Fullscreen }

/// <summary>Jogo detectado agora.</summary>
public sealed record DetectedGame(string Name, string ExeName, int Pid, DetectionReason Reason, string Source)
{
    /// <summary>Chave estável do jogo para agrupar sessões: o nome do executável em minúsculas.</summary>
    public string Key => GameKey.Of(ExeName);

    public string ReasonText => Reason switch
    {
        DetectionReason.UserList => "está na sua lista de jogos",
        DetectionReason.Library => $"instalado pela {Source}",
        DetectionReason.BuiltIn => "jogo conhecido pelo NEXUS",
        _ => "em tela cheia",
    };
}

public static class GameKey
{
    public static string Of(string? exeName) => (exeName ?? "").Trim().ToLowerInvariant();
}

/// <summary>Quais regras valem (vindas das preferências).</summary>
public sealed record DetectionRules(
    IReadOnlyList<KnownGame> Library,
    IReadOnlyList<string> UserGames,
    IReadOnlyList<string> Excluded,
    bool DetectFullscreen);

/// <summary>
/// Decide se algum processo em execução é um jogo. Ordem: lista do usuário → pasta de biblioteca de loja → lista embutida →
/// (opcional) janela em primeiro plano cobrindo o monitor. Exclusões do usuário e <see cref="GameLibrary.NeverGames"/>
/// vencem sempre. Com vários candidatos, vence o que está em primeiro plano. Não abre o processo do jogo para ler memória
/// nem módulos (seguro com anticheat: Vanguard, EAC, BattlEye).
/// </summary>
public static class GameMatcher
{
    public static DetectedGame? Find(ProcessSnapshot snapshot, DetectionRules rules)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        var excluded = new HashSet<string>(rules.Excluded, StringComparer.OrdinalIgnoreCase);
        var user = new HashSet<string>(rules.UserGames, StringComparer.OrdinalIgnoreCase);
        var builtIn = GameLibrary.BuiltIn.Where(g => g.ExeName is not null).ToDictionary(g => g.ExeName!, StringComparer.OrdinalIgnoreCase);
        var libraryByExe = rules.Library.Where(g => g.ExeName is not null).GroupBy(g => g.ExeName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var folders = rules.Library.Where(g => g.InstallDir is not null).ToList();

        var candidates = new List<DetectedGame>();
        foreach (var p in snapshot.Processes)
        {
            var exe = ExeName(p);
            if (exe.Length == 0 || excluded.Contains(exe) || GameLibrary.NeverGames.Contains(exe)) continue;
            if (user.Contains(exe)) { candidates.Add(new(Pretty(exe), exe, p.Pid, DetectionReason.UserList, "sua lista")); continue; }
            if (p.Path is { } path && !GameLibrary.IsHelperExe(exe))
            {
                var lib = folders.Where(g => GameLibrary.IsUnder(path, g.InstallDir!)).MaxBy(g => g.InstallDir!.Length);
                if (lib is not null) { candidates.Add(new(lib.Name, exe, p.Pid, DetectionReason.Library, lib.Store)); continue; }
            }
            if (libraryByExe.TryGetValue(exe, out var byExe)) { candidates.Add(new(byExe.Name, exe, p.Pid, DetectionReason.Library, byExe.Store)); continue; }
            if (builtIn.TryGetValue(exe, out var known)) candidates.Add(new(known.Name, exe, p.Pid, DetectionReason.BuiltIn, known.Store));
        }

        var fg = snapshot.Foreground;
        if (candidates.Count > 0)
            return candidates.FirstOrDefault(c => fg is not null && c.Pid == fg.Pid)
                   ?? candidates.OrderBy(c => GameAliases.IsBootstrapper(c.ExeName)) // ..._EAC_EOS.exe/lançador só abre o jogo: quem desenha vence
                       .ThenBy(c => c.Reason).ThenBy(c => c.Pid).First();

        // Tela cheia: só com a regra ligada, só a janela em primeiro plano, só se cobrir o monitor e não for algo sabido como não-jogo.
        if (rules.DetectFullscreen && fg is { CoversMonitor: true } && fg.Width >= 640 && fg.Height >= 480)
        {
            var proc = snapshot.Processes.FirstOrDefault(p => p.Pid == fg.Pid);
            if (proc is not null)
            {
                var exe = ExeName(proc);
                var systemPath = proc.Path is { } sp && (sp.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) || sp.Contains(@"\WindowsApps\Microsoft.", StringComparison.OrdinalIgnoreCase));
                if (exe.Length > 0 && !excluded.Contains(exe) && !GameLibrary.IsHelperExe(exe) && !systemPath)
                    return new DetectedGame(Pretty(exe), exe, proc.Pid, DetectionReason.Fullscreen, "tela cheia");
            }
        }
        return null;
    }

    private static string ExeName(RunningProcess p)
    {
        var n = (p.Path is { Length: > 0 } path ? System.IO.Path.GetFileName(path.Replace('/', '\\').Split('\\')[^1]) : p.Name)?.Trim() ?? "";
        if (n.Length > 0 && !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
        return n;
    }

    /// <summary>Nome legível a partir do executável: "VALORANT-Win64-Shipping.exe" → "VALORANT".</summary>
    public static string Pretty(string exe)
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(exe);
        foreach (var suffix in new[] { "-Win64-Shipping", "-WinGDK-Shipping", "_dx12", "-Shipping", "_x64", "64" })
            if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && n.Length > suffix.Length) { n = n[..^suffix.Length]; break; }
        return n.Length == 0 ? exe : n;
    }
}

/// <summary>O que mudou no jogo acompanhado desde a última leitura.</summary>
public enum GameEventKind { None, Started, Stopped }

public sealed record GameEvent(GameEventKind Kind, DetectedGame? Game, TimeSpan PlayedFor);

/// <summary>
/// Máquina de estados "jogo aberto/fechado" com filtro: um jogo só conta como aberto depois de visto por
/// <see cref="StartAfter"/> (telas de abertura e lançadores que abrem e fecham não disparam captura) e só conta como
/// fechado quando o processo sumiu por <see cref="StopAfter"/> (um piscar da lista de processos não encerra a sessão).
/// Um jogo por vez.
/// </summary>
public sealed class GameSessionTracker(TimeSpan? startAfter = null, TimeSpan? stopAfter = null)
{
    private DetectedGame? _pending;
    private DateTimeOffset _pendingSince;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _missingSince;

    public TimeSpan StartAfter { get; } = startAfter ?? TimeSpan.FromSeconds(8);
    public TimeSpan StopAfter { get; } = stopAfter ?? TimeSpan.FromSeconds(4);
    public DetectedGame? Current { get; private set; }
    public DateTimeOffset StartedAt => _startedAt;

    /// <param name="seen">O que o <see cref="GameMatcher"/> achou nesta leitura (null = nenhum jogo).</param>
    /// <param name="currentAlive">O processo do jogo atual ainda existe (pelo PID), mesmo que o matcher prefira outro.</param>
    public GameEvent Update(DetectedGame? seen, bool currentAlive, DateTimeOffset now)
    {
        if (Current is { } cur)
        {
            if (currentAlive || (seen is not null && seen.Pid == cur.Pid)) { _missingSince = null; return new(GameEventKind.None, cur, now - _startedAt); }
            _missingSince ??= now;
            if (now - _missingSince.Value < StopAfter) return new(GameEventKind.None, cur, now - _startedAt);
            Current = null;
            _missingSince = null;
            _pending = null;
            return new(GameEventKind.Stopped, cur, now - _startedAt);
        }

        if (seen is null) { _pending = null; return new(GameEventKind.None, null, TimeSpan.Zero); }
        if (_pending is null || _pending.Pid != seen.Pid) { _pending = seen; _pendingSince = now; }
        if (now - _pendingSince < StartAfter) return new(GameEventKind.None, null, TimeSpan.Zero);
        Current = _pending;
        _startedAt = now;
        _pending = null;
        return new(GameEventKind.Started, Current, TimeSpan.Zero);
    }
}
