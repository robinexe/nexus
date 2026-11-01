using System.Diagnostics;
using System.Globalization;

namespace Nexus.Measurement;

/// <summary>Resultado da prova curta do PresentMon.</summary>
/// <param name="Ran">O PresentMon executou e terminou sozinho (código 0).</param>
/// <param name="Frames">Linhas de quadro gravadas (sem o cabeçalho).</param>
public sealed record PresentMonSmokeResult(bool Ran, int? ExitCode, int Frames, bool HeaderWritten, string Message, string Log);

/// <summary>
/// Prova de que o PresentMon funciona neste PC: confere o executável, roda uma captura curta (padrão 5 s) de um processo que
/// sempre existe (dwm.exe, o compositor de janelas) SEM pedir elevação, conta as linhas gravadas num arquivo temporário do
/// NEXUS e apaga o arquivo. Só leitura (rastreamento ETW). Usado pelo autoteste com <c>--with-benchmarks</c>.
/// Numa máquina sem nada mudando na tela (ex.: VM sem GPU e sem ninguém mexendo), o dwm pode não apresentar quadros: zero
/// linhas com o PresentMon terminando bem ainda prova que ele executa e tem permissão de rastrear.
/// </summary>
public static class PresentMonSmokeTest
{
    public const string SessionName = "NEXUS_SelfTest";

    public static IReadOnlyList<string> Arguments(string processName, string outputCsv, int seconds)
    {
        if (!PresentMonCli.IsValidExeName(processName)) throw new ArgumentException("Nome de executável inválido.", nameof(processName));
        if (seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds), "Entre 1 e 60 segundos.");
        return
        [
            "--process_name", processName, "--output_file", outputCsv, "--timed", seconds.ToString(CultureInfo.InvariantCulture),
            "--terminate_after_timed", "--no_console_stats", "--stop_existing_session", "--session_name", SessionName,
        ];
    }

    /// <summary>Conta as linhas de quadro (não vazias, depois do cabeçalho) de um CSV do PresentMon.</summary>
    public static (bool Header, int Frames) CountFrames(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var header = false;
        var frames = 0;
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l)) continue;
            if (!header) { header = l.Contains("Application", StringComparison.OrdinalIgnoreCase) || l.Contains("ProcessID", StringComparison.OrdinalIgnoreCase); if (header) continue; }
            frames++;
        }
        return (header, frames);
    }

    public static async Task<PresentMonSmokeResult> RunAsync(string exe, IPresentMonTrustProbe trust, string processName = "dwm.exe", int seconds = 5,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(exe);
        ArgumentNullException.ThrowIfNull(trust);
        var verdict = PresentMonTrust.Verify(exe, trust);
        if (!verdict.Trusted) return new(false, null, 0, false, verdict.Reason, "");
        var output = Path.Combine(Path.GetTempPath(), $"nexus-autoteste-presentmon-{Guid.NewGuid():N}.csv");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Arguments(processName, output, seconds)) psi.ArgumentList.Add(a);
        var log = new System.Text.StringBuilder();
        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data is { } d) lock (log) { if (log.Length < 8000) log.AppendLine(d); } };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) lock (log) { if (log.Length < 8000) log.AppendLine(d); } };
            try { p.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { return new(false, null, 0, false, $"Não foi possível iniciar o PresentMon: {ex.Message}", ""); }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds + 30));
            try { await p.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new(false, null, 0, false, "O PresentMon não terminou no tempo esperado e foi encerrado.", Text(log));
            }
            var (header, frames) = File.Exists(output) ? CountFrames(ReadShared(output)) : (false, 0);
            var ran = p.ExitCode == 0;
            var message = !ran ? $"O PresentMon terminou com código {p.ExitCode} (sem permissão de rastreamento? precisa de administrador ou do grupo \"Usuários do log de desempenho\")."
                : frames > 0 ? $"{frames} quadro(s) de {processName} em {seconds} s."
                : $"Executou com permissão, mas {processName} não apresentou quadros em {seconds} s (nada mudou na tela?).";
            return new(ran, p.ExitCode, frames, header, message, Text(log));
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Text(System.Text.StringBuilder log) { lock (log) return log.ToString(); }

    private static List<string> ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } l) lines.Add(l);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
