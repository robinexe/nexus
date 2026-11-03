using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Measurement;

/// <summary>Um quadro lido ao vivo. <see cref="FrametimeMs"/> é o intervalo entre apresentações (o "frametime" de sempre).</summary>
public readonly record struct LiveFrame(string Application, int ProcessId, double FrametimeMs, double? CpuBusyMs, double? GpuBusyMs, string? PresentMode)
{
    /// <summary>CPUWait (2.1+) / MsCPUWait (2.0 e <c>--v2_metrics</c>): o jogo esperando antes de começar o próximo quadro.</summary>
    public double? CpuWaitMs { get; init; }
    /// <summary>GPUWait / MsGPUWait: a GPU parada durante este quadro.</summary>
    public double? GpuWaitMs { get; init; }
    /// <summary>DisplayLatency / MsDisplayLatency: do começo do quadro até aparecer na tela ("NA" quando não foi mostrado).</summary>
    public double? DisplayLatencyMs { get; init; }
    /// <summary>AnimationError / MsAnimationError: diferença entre o ritmo da CPU e o da tela (movimento irregular).</summary>
    public double? AnimationErrorMs { get; init; }
    /// <summary>FrameType (só com <c>--track_frame_type</c>): "Application" ou um tipo gerado (geração de quadros do driver/SDK).</summary>
    public string? FrameType { get; init; }

    /// <summary>Quadro gerado pela geração de quadros (não foi o jogo que desenhou): qualquer FrameType diferente de vazio/NA/NotSet/Unspecified/Application.</summary>
    public bool IsGenerated => IsGeneratedType(FrameType);

    public static bool IsGeneratedType(string? frameType) =>
        !string.IsNullOrWhiteSpace(frameType) && !(frameType.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
            frameType.Equals("NotSet", StringComparison.OrdinalIgnoreCase) || frameType.Equals("Unspecified", StringComparison.OrdinalIgnoreCase) ||
            frameType.Equals("Application", StringComparison.OrdinalIgnoreCase));

    public StutterFrame ToStutterFrame() => new(FrametimeMs, CpuBusyMs, CpuWaitMs, GpuBusyMs, GpuWaitMs, DisplayLatencyMs, AnimationErrorMs, IsGenerated);
}

/// <summary>Geração da linha de comando do PresentMon: a 1.x usa opções com um traço; a 2.x, com dois.</summary>
public enum PresentMonGeneration { V1, V2 }

/// <summary>
/// Lê as linhas do PresentMon em tempo real (saída padrão ou arquivo que cresce), por NOME de coluna. Cabeçalhos aceitos:
/// <list type="bullet">
/// <item>1.x (e 2.x com <c>--v1_metrics</c>): <c>MsBetweenPresents</c>, <c>MsCPUBusy</c>/<c>MsGPUBusy</c> quando existirem;</item>
/// <item>2.0: <c>MsBetweenAppStart</c>, <c>MsCPUBusy</c>, <c>MsGPUBusy</c>;</item>
/// <item>2.1 em diante: <c>FrameTime</c>, <c>CPUBusy</c>, <c>GPUBusy</c>.</item>
/// </list>
/// Linhas que não são de dados (avisos do PresentMon, cabeçalho repetido, "NA") são ignoradas e contadas. O tempo NÃO vem
/// da coluna de tempo (a unidade dela muda com <c>--qpc_time</c>/<c>--time_units</c>): o relógio ao vivo é a soma dos frametimes.
/// </summary>
public sealed class PresentMonLineParser
{
    private int _app = -1, _pid = -1, _frameTime = -1, _cpu = -1, _gpu = -1, _mode = -1, _columns;
    private int _cpuWait = -1, _gpuWait = -1, _display = -1, _animation = -1, _frameType = -1;

    public bool HasHeader => _frameTime >= 0;
    public int Skipped { get; private set; }
    /// <summary>Nome da coluna de frametime reconhecida (para o relatório: "FrameTime", "MsBetweenAppStart" ou "MsBetweenPresents").</summary>
    public string? FrametimeColumn { get; private set; }

    /// <summary>Aceita uma linha. Devolve true e o quadro quando é uma linha de dados válida.</summary>
    public bool TryParse(string? line, out LiveFrame frame)
    {
        frame = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (!line.Contains(',', StringComparison.Ordinal)) { Skipped++; return false; }
        var f = SplitCsv(line);
        if (LooksLikeHeader(f)) { ReadHeader(f); return false; }
        if (!HasHeader || f.Count != _columns) { Skipped++; return false; }
        var ft = Num(f[_frameTime]);
        if (ft is not { } ms || ms <= 0 || ms > 60_000) { Skipped++; return false; }
        frame = new LiveFrame(
            f[_app],
            _pid >= 0 && int.TryParse(f[_pid], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0,
            ms,
            _cpu >= 0 ? Num(f[_cpu]) : null,
            _gpu >= 0 ? Num(f[_gpu]) : null,
            _mode >= 0 && f[_mode].Length > 0 ? f[_mode] : null)
        {
            CpuWaitMs = _cpuWait >= 0 ? Num(f[_cpuWait]) : null,
            GpuWaitMs = _gpuWait >= 0 ? Num(f[_gpuWait]) : null,
            DisplayLatencyMs = _display >= 0 ? Num(f[_display]) : null,
            AnimationErrorMs = _animation >= 0 ? Num(f[_animation]) : null,
            FrameType = _frameType >= 0 && f[_frameType].Length > 0 ? f[_frameType] : null,
        };
        return true;
    }

    private static bool LooksLikeHeader(List<string> f) =>
        f.Any(c => c.Equals("Application", StringComparison.OrdinalIgnoreCase)) &&
        f.Any(c => c.Equals("FrameTime", StringComparison.OrdinalIgnoreCase) || c.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("MsBetweenAppStart", StringComparison.OrdinalIgnoreCase));

    private void ReadHeader(List<string> cols)
    {
        int Col(params string[] names)
        {
            foreach (var n in names)
                for (var i = 0; i < cols.Count; i++)
                    if (cols[i].Equals(n, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        _columns = cols.Count;
        _app = Col("Application");
        _pid = Col("ProcessID");
        _frameTime = Col("FrameTime", "MsBetweenAppStart", "MsBetweenPresents");
        FrametimeColumn = _frameTime >= 0 ? cols[_frameTime] : null;
        _cpu = Col("CPUBusy", "MsCPUBusy");
        _gpu = Col("GPUBusy", "MsGPUBusy");
        _mode = Col("PresentMode");
        // PresentMon 2.6: nomes sem "Ms" no conjunto padrão; com "Ms" em --v2_metrics (conferido nas strings do executável 2.6.0).
        _cpuWait = Col("CPUWait", "MsCPUWait");
        _gpuWait = Col("GPUWait", "MsGPUWait");
        _display = Col("DisplayLatency", "MsDisplayLatency");
        _animation = Col("AnimationError", "MsAnimationError");
        _frameType = Col("FrameType");
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    internal static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') q = false;
                else cur.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { result.Add(cur.ToString().Trim()); cur.Clear(); }
            else if (c != '\r') cur.Append(c);
        }
        result.Add(cur.ToString().Trim());
        return result;
    }
}

/// <summary>Captura ao vivo: qual PresentMon, qual processo, e se o próprio PresentMon pede permissão ao Windows.</summary>
/// <param name="OutputCsv">Com elevação, o PresentMon elevado grava neste arquivo e o NEXUS o lê enquanto cresce (a saída
/// padrão de um processo elevado não chega ao NEXUS). Sem elevação, null = lê a saída padrão (<c>--output_stdout</c>).</param>
/// <param name="TrackFrameType">Pede a coluna FrameType (<c>--track_frame_type</c>, 2.x): só para um PresentMon de versão conhecida
/// (hash conferido), porque uma 2.x antiga que não conheça a opção recusaria a linha de comando inteira.</param>
public sealed record LiveCaptureRequest(string PresentMonExe, string ProcessName, bool RequestElevation, string? OutputCsv, int MaxMinutes = 240, bool TrackFrameType = false)
{
    /// <summary>Outros executáveis aceitos além de <see cref="ProcessName"/> (o PresentMon aceita <c>--process_name</c> repetido):
    /// o processo que desenha quando o detectado é o lançador (<see cref="GameAliases"/>).</summary>
    public IReadOnlyList<string> ExtraProcessNames { get; init; } = [];
    /// <summary>Sem filtro de processo (todos que apresentam quadros, menos <see cref="Exclude"/>): o NEXUS escolhe o processo pelos
    /// quadros (<see cref="LiveTargetPicker"/>). Sem <c>--terminate_on_proc_exit</c> (não há alvo): quem para é o NEXUS. Só 2.x.</summary>
    public bool AllProcesses { get; init; }
    /// <summary>Executáveis ignorados (<c>--exclude</c>), usados na captura sem filtro.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
    /// <summary>Nome da sessão ETW (<c>--session_name</c>).</summary>
    public string SessionName { get; init; } = PresentMonCli.LiveSessionName;
}

public static partial class PresentMonCli
{
    public const string LiveSessionName = "NEXUS_Live";

    [GeneratedRegex(@"(?i)presentmon[^\\/]*?[-_ ]v?(?<major>\d+)\.(?<minor>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionInName();

    [GeneratedRegex(@"\A[A-Za-z0-9 ._()\[\]+\-]{1,120}\.exe\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExeNameRegex();

    [GeneratedRegex(@"\A[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionNameRegex();

    /// <summary>Nome de executável aceito (sem pasta, sem aspas, sem curingas). Mesmo critério para a lista de jogos do usuário.</summary>
    public static bool IsValidExeName(string? name) => name is not null && ExeNameRegex().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>Geração pelo nome do arquivo (PresentMon-2.3.0-x64.exe, PresentMon64-1.9.2.exe). Sem versão no nome: 2.x (a atual).</summary>
    public static PresentMonGeneration GenerationOf(string? exePath)
    {
        var name = GameLibrary.WinFileName(exePath);
        var m = VersionInName().Match(name);
        if (m.Success && int.TryParse(m.Groups["major"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
            return major <= 1 ? PresentMonGeneration.V1 : PresentMonGeneration.V2;
        if (Regex.IsMatch(name, @"(?i)presentmon(64|32)-1\.", RegexOptions.CultureInvariant)) return PresentMonGeneration.V1;
        return PresentMonGeneration.V2;
    }

    /// <summary>
    /// Argumentos da captura ao vivo. Só leitura (rastreamento ETW de apresentação de quadros). O PresentMon termina sozinho
    /// quando o jogo fecha (<c>terminate_on_proc_exit</c>) e, por segurança, depois de <see cref="LiveCaptureRequest.MaxMinutes"/>.
    /// </summary>
    public static IReadOnlyList<string> LiveArguments(LiveCaptureRequest r, PresentMonGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(r);
        var all = r.AllProcesses && generation == PresentMonGeneration.V2;
        if (!all && !IsValidExeName(r.ProcessName)) throw new ArgumentException("Nome de executável inválido (ex.: cs2.exe).", nameof(r));
        if (r.ExtraProcessNames.Concat(r.Exclude).Any(n => !IsValidExeName(n))) throw new ArgumentException("Nome de executável inválido na lista.", nameof(r));
        if (!SessionNameRegex().IsMatch(r.SessionName)) throw new ArgumentException("Nome de sessão inválido.", nameof(r));
        if (r.MaxMinutes is < 1 or > 24 * 60) throw new ArgumentOutOfRangeException(nameof(r), "Limite entre 1 minuto e 24 horas.");
        var d = generation == PresentMonGeneration.V1 ? "-" : "--";
        var args = new List<string>();
        if (!all)
            foreach (var name in new[] { r.ProcessName }.Concat(r.ExtraProcessNames).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                args.Add(d + "process_name");
                args.Add(name);
            }
        if (r.OutputCsv is { } file) { args.Add(d + "output_file"); args.Add(file); }
        else args.Add(d + "output_stdout");
        if (!all) args.Add(d + "terminate_on_proc_exit");
        args.Add(d + "timed");
        args.Add((r.MaxMinutes * 60).ToString(CultureInfo.InvariantCulture));
        args.Add(d + "terminate_after_timed");
        args.Add(d + "stop_existing_session");
        args.Add(d + "session_name");
        args.Add(r.SessionName);
        if (all)
            foreach (var name in r.Exclude.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                args.Add(d + "exclude");
                args.Add(name);
            }
        if (generation == PresentMonGeneration.V1)
        {
            args.Add("-no_top");
            if (!r.RequestElevation) args.Add("-dont_restart_as_admin"); // a 1.x se reabre como administrador por padrão
        }
        else
        {
            args.Add("--no_console_stats");
            if (r.TrackFrameType) args.Add("--track_frame_type");
            if (r.RequestElevation) args.Add("--restart_as_admin");
        }
        return args;
    }

    /// <summary>
    /// Pastas onde um PresentMon já instalado costuma estar (nada é baixado): a configurada, ao lado do NEXUS, em
    /// %LocalAppData%\NEXUS\PresentMon, nas instalações oficiais da Intel (PresentMon app / CapFrameX, que inclui o
    /// PresentMon) e em Downloads. Devolve o executável de console mais novo encontrado.
    /// </summary>
    public static IReadOnlyList<string> CandidateFolders(string appBaseDirectory, Func<Environment.SpecialFolder, string>? folder = null)
    {
        folder ??= Environment.GetFolderPath;
        var pf = folder(Environment.SpecialFolder.ProgramFiles);
        var pf86 = folder(Environment.SpecialFolder.ProgramFilesX86);
        var local = folder(Environment.SpecialFolder.LocalApplicationData);
        var profile = folder(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(appBaseDirectory, "PresentMon"),
            Path.Combine(local, "NEXUS", "PresentMon"),
            Path.Combine(pf, "Intel", "PresentMon", "PresentMonCli"),
            Path.Combine(pf, "Intel", "PresentMon"),
            Path.Combine(pf, "CapFrameX"),
            Path.Combine(pf86, "CapFrameX"),
            Path.Combine(profile, "Downloads"),
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Entre os nomes de arquivo de uma pasta, o PresentMon de console mais novo (ignora a interface gráfica "PresentMon.exe" do app).</summary>
    public static string? PickExecutable(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files
            .Where(f => GameLibrary.WinFileName(f).StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => !GameLibrary.WinFileName(f).Equals("PresentMon.exe", StringComparison.OrdinalIgnoreCase) ||
                        f.Replace('/', '\\').EndsWith("PresentMonCli\\PresentMon.exe", StringComparison.OrdinalIgnoreCase))
            .Where(f => !GameLibrary.WinFileName(f).Contains("Service", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => VersionKey(GameLibrary.WinFileName(f)))
            .FirstOrDefault();
    }

    private static string VersionKey(string name)
    {
        var m = Regex.Match(name, @"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);
        if (!m.Success) return "000.000.000";
        string P(int g) => (m.Groups[g].Success ? m.Groups[g].Value : "0").PadLeft(3, '0');
        return $"{P(1)}.{P(2)}.{P(3)}";
    }

    /// <summary>
    /// Procura o PresentMon (só o caminho, sem conferir): primeiro a cópia que acompanha o NEXUS
    /// (<c>tools\PresentMon\PresentMon.exe</c>), depois o caminho configurado e as pastas conhecidas. Nada é baixado.
    /// Antes de executar, use <see cref="PresentMonLocator.Locate"/> (confere hash/assinatura).
    /// </summary>
    public static string? Locate(string? configured, string appBaseDirectory)
    {
        var bundled = PresentMonTrust.BundledPath(appBaseDirectory);
        if (File.Exists(bundled)) return bundled;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var dir in CandidateFolders(appBaseDirectory))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var exe = PickExecutable(Directory.EnumerateFiles(dir, "PresentMon*.exe", SearchOption.TopDirectoryOnly));
                if (exe is not null) return exe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }
}
