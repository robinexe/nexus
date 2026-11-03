using System.Diagnostics;
using System.Text;

namespace Nexus.Measurement;

/// <summary>
/// Inicia o PresentMon em tempo real para um processo. Sem elevação: lê a saída padrão (<c>--output_stdout</c>). Com
/// elevação (<c>--restart_as_admin</c>, o Windows pergunta e o usuário aceita): o PresentMon elevado grava num arquivo
/// temporário e o NEXUS lê o arquivo enquanto ele cresce. O PresentMon termina sozinho quando o jogo fecha.
/// </summary>
/// <param name="verify">Conferência do executável (<see cref="PresentMonTrust.Verify"/>) feita a cada início, imediatamente antes
/// de executar: um PresentMon recusado não roda (a captura volta encerrada, com o motivo em <see cref="ILiveCapture"/>.Log).</param>
public sealed class PresentMonLiveStarter(Func<string?> presentMonPath, Func<bool> requestElevation,
    Func<ProcessStartInfo, ProcessStartInfo>? customize = null, Func<string, PresentMonVerdict>? verify = null) : ILiveCaptureStarter
{
    public bool Available => presentMonPath() is { } p && (customize is not null || (File.Exists(p) && (verify is null || verify(p).Trusted)));

    /// <summary>Motivo da última recusa (PresentMon que não passou na conferência), ou null.</summary>
    public string? LastRefusal { get; private set; }

    public ILiveCapture Start(string processName, Action<LiveFrame> onFrame)
    {
        var exe = presentMonPath() ?? throw new InvalidOperationException("PresentMon não encontrado.");
        var elevate = requestElevation();
        var file = elevate ? Path.Combine(Path.GetTempPath(), $"nexus-ao-vivo-{Guid.NewGuid():N}.csv") : null;
        var verdict = customize is null ? verify?.Invoke(exe) : null;
        // FrameType só para uma versão conhecida (hash conferido): uma 2.x antiga recusaria a opção.
        var known = verdict is { Trusted: true, Sha256: { } h } && PresentMonTrust.KnownSha256.Contains(h);
        var req = new LiveCaptureRequest(exe, processName, elevate, file, TrackFrameType: known);
        var capture = new PresentMonLiveCapture(req, PresentMonCli.LiveArguments(req, PresentMonCli.GenerationOf(exe)), onFrame, customize);
        LastRefusal = verdict is { Trusted: false } refused ? refused.Reason : null;
        if (LastRefusal is { } reason) capture.Refuse(reason);
        else capture.Begin();
        return capture;
    }
}

public sealed class PresentMonLiveCapture : ILiveCapture
{
    private const int MaxLogChars = 16_000;
    private readonly LiveCaptureRequest _request;
    private readonly IReadOnlyList<string> _args;
    private readonly Action<LiveFrame> _onFrame;
    private readonly Func<ProcessStartInfo, ProcessStartInfo>? _customize;
    private readonly PresentMonLineParser _parser = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly StringBuilder _log = new();
    private Process? _process;
    private Task? _tail;
    private volatile bool _ended;

    internal PresentMonLiveCapture(LiveCaptureRequest request, IReadOnlyList<string> args, Action<LiveFrame> onFrame, Func<ProcessStartInfo, ProcessStartInfo>? customize)
    {
        _request = request;
        _args = args;
        _onFrame = onFrame;
        _customize = customize;
    }

    public string Log { get { lock (_log) return _log.ToString(); } }
    public bool Ended => _ended;
    public int SkippedLines => _parser.Skipped;

    /// <summary>Não executa: a captura já nasce encerrada, com o motivo no log.</summary>
    internal void Refuse(string reason)
    {
        AppendLog(reason);
        _ended = true;
    }

    internal void Begin()
    {
        var psi = new ProcessStartInfo(_request.PresentMonExe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in _args) psi.ArgumentList.Add(a);
        if (_customize is not null) psi = _customize(psi);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is { } d) Line(d, fromStdout: true); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) AppendLog(d); };
        p.Exited += (_, _) => { if (_request.OutputCsv is null) _ended = true; };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppendLog($"Não foi possível iniciar o PresentMon: {ex.Message}");
            _ended = true;
            p.Dispose();
            return;
        }
        _process = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (_request.OutputCsv is { } file) _tail = Task.Run(() => TailAsync(file, _cancel.Token));
    }

    private void Line(string line, bool fromStdout)
    {
        if (_parser.TryParse(line, out var frame))
        {
            try { _onFrame(frame); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppendLog($"Quadro ignorado: {ex.Message}"); }
        }
        else if (fromStdout && !_parser.HasHeader) AppendLog(line);
    }

    /// <summary>Lê o arquivo enquanto cresce (compartilhado para escrita: o PresentMon elevado continua gravando).</summary>
    private async Task TailAsync(string file, CancellationToken cancel)
    {
        var waited = 0;
        try
        {
            while (!File.Exists(file) && !cancel.IsCancellationRequested)
            {
                await Task.Delay(250, cancel).ConfigureAwait(false);
                if ((waited += 250) > 120_000) { AppendLog("O PresentMon não começou a gravar em 2 minutos (permissão negada?)."); _ended = true; return; }
            }
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var pending = new StringBuilder();
            var buffer = new char[16 * 1024];
            while (!cancel.IsCancellationRequested)
            {
                var n = await reader.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
                if (n == 0)
                {
                    await Task.Delay(100, cancel).ConfigureAwait(false);
                    continue;
                }
                // Só linhas COMPLETAS: o PresentMon pode estar no meio de gravar a última (um número cortado pareceria válido).
                foreach (var line in TakeLines(pending, buffer.AsSpan(0, n))) Line(line, fromStdout: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendLog($"Leitura do arquivo do PresentMon falhou: {ex.Message}"); _ended = true; }
    }

    /// <summary>Junta o pedaço lido ao que sobrou e devolve só as linhas terminadas em \n; o resto fica em <paramref name="pending"/>.</summary>
    internal static List<string> TakeLines(StringBuilder pending, ReadOnlySpan<char> chunk)
    {
        var lines = new List<string>();
        foreach (var c in chunk)
        {
            if (c == '\n') { lines.Add(pending.ToString().TrimEnd('\r')); pending.Clear(); }
            else pending.Append(c);
        }
        return lines;
    }

    private void AppendLog(string line)
    {
        lock (_log)
        {
            if (_log.Length > MaxLogChars) return;
            _log.AppendLine(line);
        }
    }

    public void Finish()
    {
        _cancel.Cancel();
        try { _tail?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        // Sem elevação, o PresentMon é nosso: encerra. Com elevação, ele termina sozinho quando o jogo fecha (ou no limite de tempo).
        try { if (_process is { HasExited: false } p && _request.OutputCsv is null) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        _ended = true;
    }

    public void Dispose()
    {
        Finish();
        _process?.Dispose();
        _cancel.Dispose();
        if (_request.OutputCsv is { } f)
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
