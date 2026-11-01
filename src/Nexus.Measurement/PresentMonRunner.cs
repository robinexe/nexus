using System.Diagnostics;

namespace Nexus.Measurement;

/// <summary>Pedido de captura: qual PresentMon, qual jogo, por quanto tempo, para onde.</summary>
/// <param name="RequestElevation">Usa <c>--restart_as_admin</c>: o próprio PresentMon pede permissão ao Windows (UAC), e é o
/// usuário quem aceita. Sem isso, a captura só funciona para administradores ou membros do grupo
/// "Usuários de log de desempenho".</param>
public sealed record PresentMonCaptureRequest(string PresentMonExe, string ProcessName, int Seconds, string OutputCsv, bool RequestElevation);

public sealed record PresentMonRunResult(bool Ok, string Message, string? OutputCsv, int? ExitCode, string Log);

/// <summary>
/// Executa o PresentMon (Intel, MIT; a versão 2.6.0 acompanha o NEXUS em tools\PresentMon) só para LER: o rastreamento ETW de
/// apresentação de quadros não altera nada no sistema. O NEXUS não baixa nada sozinho.
/// </summary>
public static class PresentMonRunner
{
    public const int MinSeconds = 10;
    public const int MaxSeconds = 600;
    public const string SessionName = "NEXUS_Capture";

    /// <summary>Argumentos da linha de comando do PresentMon 2.x.</summary>
    public static IReadOnlyList<string> Arguments(PresentMonCaptureRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (string.IsNullOrWhiteSpace(r.ProcessName) || r.ProcessName.IndexOfAny(['"', '\\', '/', ':', '*', '?', '<', '>', '|']) >= 0)
            throw new ArgumentException("Informe só o nome do executável do jogo (ex.: cs2.exe).", nameof(r));
        if (r.Seconds is < MinSeconds or > MaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(r), $"Duração entre {MinSeconds} e {MaxSeconds} segundos.");
        var args = new List<string>
        {
            "--process_name", r.ProcessName.Trim(),
            "--output_file", r.OutputCsv,
            "--timed", r.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--terminate_after_timed",
            "--no_console_stats",
            "--stop_existing_session",
            "--session_name", SessionName,
        };
        if (r.RequestElevation) args.Add("--restart_as_admin");
        return args;
    }

    /// <summary>Onde procurar o PresentMon: a cópia embutida, o caminho configurado e as pastas conhecidas (<see cref="PresentMonCli.Locate"/>).</summary>
    public static string? Locate(string? configured, string appBaseDirectory) => PresentMonCli.Locate(configured, appBaseDirectory);

    /// <param name="customize">Só para testes: troca o executável (ex.: um PresentMon falso).</param>
    /// <param name="verify">Conferência antes de executar (<see cref="PresentMonTrust.Verify"/>); recusado = não executa.</param>
    public static async Task<PresentMonRunResult> RunAsync(PresentMonCaptureRequest r, CancellationToken cancel, IProgress<string>? progress = null,
        Func<ProcessStartInfo, ProcessStartInfo>? customize = null, Func<string, PresentMonVerdict>? verify = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        var args = Arguments(r);
        if (customize is null && !File.Exists(r.PresentMonExe))
            return new(false, $"PresentMon não encontrado em {r.PresentMonExe}.", null, null, "");
        if (customize is null && verify?.Invoke(r.PresentMonExe) is { Trusted: false } refused)
            return new(false, refused.Reason, null, null, "");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(r.OutputCsv))!);
        if (File.Exists(r.OutputCsv)) File.Delete(r.OutputCsv);

        var psi = new ProcessStartInfo(r.PresentMonExe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (customize is not null) psi = customize(psi);

        var log = new System.Text.StringBuilder();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is { } d) { lock (log) log.AppendLine(d); progress?.Report(d); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) { lock (log) log.AppendLine(d); progress?.Report(d); } };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { return new(false, $"Não foi possível iniciar o PresentMon: {ex.Message}", null, null, ""); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        progress?.Report($"Capturando {r.ProcessName} por {r.Seconds} s…");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(r.Seconds + 60));
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(false, cancel.IsCancellationRequested ? "Captura cancelada." : "O PresentMon não terminou no tempo esperado e foi encerrado.", null, null, log.ToString());
        }

        // Com --restart_as_admin, este processo termina logo e uma cópia elevada grava o arquivo: espera o arquivo parar de crescer.
        if (r.RequestElevation && p.ExitCode == 0)
            await WaitForStableFileAsync(r.OutputCsv, TimeSpan.FromSeconds(r.Seconds + 30), cancel).ConfigureAwait(false);

        var text = log.ToString();
        if (File.Exists(r.OutputCsv) && new FileInfo(r.OutputCsv).Length > 0)
            return new(true, "Captura concluída.", r.OutputCsv, p.ExitCode, text);
        return new(false, Explain(p.ExitCode, text, r), null, p.ExitCode, text);
    }

    internal static string Explain(int exitCode, string log, PresentMonCaptureRequest r)
    {
        if (log.Contains("access", StringComparison.OrdinalIgnoreCase) || log.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("elevat", StringComparison.OrdinalIgnoreCase) || log.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase))
            return "O Windows negou o rastreamento. Marque \"Pedir permissão ao Windows\" para o PresentMon solicitar (você aceita no aviso do Windows), " +
                   "ou adicione sua conta ao grupo \"Usuários de log de desempenho\".";
        return $"O PresentMon terminou sem gravar frames de {r.ProcessName} (código {exitCode}). Confira se o jogo estava aberto e com esse nome de executável.";
    }

    private static async Task WaitForStableFileAsync(string path, TimeSpan max, CancellationToken cancel)
    {
        var sw = Stopwatch.StartNew();
        long last = -1;
        var stableFor = 0;
        while (sw.Elapsed < max && !cancel.IsCancellationRequested)
        {
            await Task.Delay(1000, cancel).ConfigureAwait(false);
            var len = File.Exists(path) ? new FileInfo(path).Length : -1;
            stableFor = len > 0 && len == last ? stableFor + 1 : 0;
            if (stableFor >= 3) return;
            last = len;
        }
    }
}
