namespace Nexus.Measurement;

/// <summary>Um processo que está apresentando quadros agora (visto pelo PresentMon).</summary>
/// <param name="Name">Nome do executável como o PresentMon informa ("&lt;unknown&gt;" quando ele não pôde abrir o processo).</param>
/// <param name="Fps">Quadros por segundo na janela recente.</param>
public sealed record PresenterInfo(int Pid, string Name, double Fps, bool Foreground = false)
{
    /// <summary>O PresentMon não sabe o nome (processo de outra conta/elevado sem SeDebugPrivilege).</summary>
    public bool UnknownName => Name.Length == 0 || Name.StartsWith('<');
}

/// <summary>Contagem de quadros por processo numa janela deslizante (para escolher quem está desenhando). Seguro entre threads.</summary>
public sealed class PresenterWindow(TimeSpan? window = null)
{
    private const int MaxPerProcess = 4000;
    private readonly object _gate = new();
    private readonly Dictionary<int, (string Name, Queue<DateTimeOffset> At)> _byPid = [];

    public TimeSpan Window { get; } = window ?? TimeSpan.FromSeconds(3);

    public void Add(int pid, string name, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_byPid.TryGetValue(pid, out var e)) { e = (name, new Queue<DateTimeOffset>()); _byPid[pid] = e; }
            else if (e.Name != name && !string.IsNullOrEmpty(name) && !name.StartsWith('<')) _byPid[pid] = e = (name, e.At);
            e.At.Enqueue(at);
            while (e.At.Count > MaxPerProcess) e.At.Dequeue();
        }
    }

    /// <summary>Quem apresentou na janela recente, do que mais desenha para o que menos.</summary>
    public IReadOnlyList<PresenterInfo> Snapshot(DateTimeOffset now, int? foregroundPid = null)
    {
        lock (_gate)
        {
            var list = new List<PresenterInfo>();
            foreach (var (pid, e) in _byPid.ToList())
            {
                while (e.At.Count > 0 && now - e.At.Peek() > Window) e.At.Dequeue();
                if (e.At.Count == 0) { _byPid.Remove(pid); continue; }
                list.Add(new PresenterInfo(pid, e.Name, e.At.Count / Window.TotalSeconds, pid == foregroundPid));
            }
            return list.OrderByDescending(p => p.Fps).ThenBy(p => p.Pid).ToList();
        }
    }

    public void Clear() { lock (_gate) _byPid.Clear(); }
}

/// <summary>
/// Escolhe o processo a medir entre os que estão desenhando (captura sem filtro de processo). Ordem:
/// <list type="number">
/// <item>o processo do jogo detectado (pelo PID);</item>
/// <item>um nome esperado para o jogo (<see cref="GameAliases.CaptureNames"/>, na ordem);</item>
/// <item>o dono da janela em primeiro plano, se estiver desenhando;</item>
/// <item>o que mais desenha (≥ <c>minFps</c> × 2), sem compositor, Explorer, navegadores, lançadores, o próprio NEXUS e o que
/// o usuário excluiu.</item>
/// </list>
/// Nada é escolhido abaixo de <c>minFps</c> (um processo que apresenta 1 quadro por segundo não é um jogo rodando).
/// </summary>
public static class LiveTargetPicker
{
    /// <summary>Nunca medidos na escolha automática (além de <see cref="GameLibrary.NeverGames"/>).</summary>
    public static readonly IReadOnlySet<string> NeverTarget = new HashSet<string>(GameLibrary.NeverGames.Concat(
    [
        "dwm.exe", "explorer.exe", "csrss.exe", "nexus.exe", "nexus.desktop.exe", "msedgewebview2.exe", "widgets.exe", "phoneexperiencehost.exe",
        "systemsettings.exe", "taskmgr.exe", "snippingtool.exe", "screenclippinghost.exe", "gamebarpresencewriter.exe", "epicwebhelper.exe",
        "overwolf.exe", "medal.exe", "nvidia app.exe", "amdrsserv.exe",
    ]), StringComparer.OrdinalIgnoreCase);

    public static PresenterInfo? Pick(IReadOnlyList<PresenterInfo> presenters, IReadOnlyList<string> targetNames, int? gamePid,
        int? foregroundPid, IReadOnlyCollection<string>? excluded = null, double minFps = 5, int? ownPid = null)
    {
        ArgumentNullException.ThrowIfNull(presenters);
        ArgumentNullException.ThrowIfNull(targetNames);
        var ex = new HashSet<string>(excluded ?? [], StringComparer.OrdinalIgnoreCase);
        var live = presenters.Where(p => p.Fps >= minFps && p.Pid != ownPid && !ex.Contains(p.Name)).ToList();
        if (gamePid is > 0 && live.FirstOrDefault(p => p.Pid == gamePid) is { } byPid) return byPid;
        foreach (var name in targetNames)
            if (live.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).MaxBy(p => p.Fps) is { } byName) return byName;
        var allowed = live.Where(p => !NeverTarget.Contains(p.Name) && !GameLibrary.IsHelperExe(p.Name) || p.UnknownName).ToList();
        if (foregroundPid is > 0 && allowed.FirstOrDefault(p => p.Pid == foregroundPid) is { } fg) return fg;
        return allowed.Where(p => p.Fps >= minFps * 2).MaxBy(p => p.Fps);
    }
}

/// <summary>Resultado de <see cref="PresenterProbe"/>.</summary>
public sealed record PresenterProbeResult(IReadOnlyList<PresenterInfo> Presenters, PresentMonFailure Failure, string Log);

/// <summary>
/// "Quem está desenhando agora?": PresentMon sem filtro de processo por alguns segundos (sessão própria, <c>NEXUS_Probe</c>, que não
/// atrapalha uma captura em andamento) e a lista de processos que apresentaram quadros, do que mais desenha para o que menos. Serve
/// para o usuário escolher o jogo à mão quando a detecção não acerta. Só leitura (rastreamento ETW).
/// </summary>
public static class PresenterProbe
{
    public const string SessionName = "NEXUS_Probe";

    public static IReadOnlyList<string> Arguments(int seconds, string? outputCsv, bool elevate)
    {
        if (seconds is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(seconds), "Entre 1 e 30 segundos.");
        var args = new List<string>();
        if (outputCsv is { } f) { args.Add("--output_file"); args.Add(f); }
        else args.Add("--output_stdout");
        args.AddRange(["--timed", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--terminate_after_timed",
            "--stop_existing_session", "--session_name", SessionName, "--no_console_stats"]);
        foreach (var n in PresentMonLiveCapture.AllModeExclude) { args.Add("--exclude"); args.Add(n); }
        if (elevate) args.Add("--restart_as_admin");
        return args;
    }

    public static async Task<PresenterProbeResult> RunAsync(string exe, bool elevate, IPresentMonRunner? runner = null, int seconds = 3,
        int? ownPid = null, TimeSpan? permissionWait = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(exe);
        runner ??= new PresentMonProcessRunner();
        var window = new PresenterWindow(TimeSpan.FromSeconds(seconds + 2));
        var parser = new PresentMonLineParser();
        var log = new System.Text.StringBuilder();
        var gate = new object();
        var file = elevate ? Path.Combine(Path.GetTempPath(), $"nexus-quem-desenha-{Guid.NewGuid():N}.csv") : null;
        IPresentMonRun run;
        try
        {
            run = runner.Start(exe, Arguments(seconds, file, elevate), file,
                l => { lock (gate) { if (parser.TryParse(l, out var f)) window.Add(f.ProcessId, f.Application, DateTimeOffset.Now); } },
                e => { lock (gate) if (log.Length < 8000) log.AppendLine(e); });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new([], PresentMonFailure.NotStarted, ex.Message);
        }
        using (run)
        {
            var limit = DateTimeOffset.Now + TimeSpan.FromSeconds(seconds + 5) + (elevate ? permissionWait ?? TimeSpan.FromSeconds(60) : TimeSpan.Zero);
            try
            {
                while (!run.HasExited && DateTimeOffset.Now < limit) await Task.Delay(200, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            run.Terminate();
            string text;
            lock (gate) text = log.ToString();
            var list = window.Snapshot(DateTimeOffset.Now).Where(p => p.Pid != (ownPid ?? Environment.ProcessId))
                .Select(p => p with { Fps = p.Fps * (seconds + 2) / seconds }) // a janela tem 2 s de folga
                .ToList();
            var failure = PresentMonDiagnosis.Classify(run.HasExited ? run.ExitCode : null, text, list.Count > 0);
            return new(list, failure == PresentMonFailure.TargetExited ? PresentMonFailure.None : failure, text);
        }
    }
}
