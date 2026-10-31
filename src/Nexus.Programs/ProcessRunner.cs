using System.Diagnostics;
using System.Text;

namespace Nexus.Programs;

public sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Roda uma ferramenta de LEITURA (versão, lista de atualizações) em processo oculto. Nunca usado para escrever.</summary>
public interface IProcessRunner
{
    Task<ProcessOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// Processo oculto com argumentos separados (<see cref="ProcessStartInfo.ArgumentList"/>: nada de texto concatenado nem
/// <c>cmd /c</c>), entrada fechada (se a ferramenta pedir algo, ela recebe fim de arquivo em vez de travar) e saída em UTF-8.
/// </summary>
public sealed class HiddenProcessRunner : IProcessRunner
{
    public async Task<ProcessOutput> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        // Saída sem cores nem barras de progresso quando a ferramenta respeita estas variáveis.
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CI"] = "1";

        using var p = new Process { StartInfo = psi };
        p.Start();
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = p.StandardError.ReadToEndAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessOutput(-1, "", "", TimedOut: true);
        }
        return new ProcessOutput(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
    }
}
