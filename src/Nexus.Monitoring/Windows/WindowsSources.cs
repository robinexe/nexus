using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace Nexus.Monitoring.Windows;

/// <summary>Processos pela API do .NET (TotalProcessorTime e WorkingSet64). Processos protegidos: CPU sem leitura, memória sim.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessSource : IProcessSource
{
    public int LogicalProcessors => Environment.ProcessorCount;

    public IReadOnlyList<ProcessReading> Read()
    {
        var all = Process.GetProcesses();
        var list = new List<ProcessReading>(all.Length);
        foreach (var p in all)
        {
            using (p)
            {
                try
                {
                    TimeSpan? cpu = null;
                    DateTime? start = null;
                    try
                    {
                        cpu = p.TotalProcessorTime;
                        start = p.StartTime;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // sem acesso (processo protegido ou de outra sessão): fica sem CPU
                    }
                    list.Add(new ProcessReading(p.Id, p.ProcessName, cpu, p.WorkingSet64, start));
                }
                catch (InvalidOperationException)
                {
                    // terminou durante a leitura
                }
            }
        }
        return list;
    }
}

/// <summary>
/// nvidia-smi, a ferramenta de linha de comando do driver NVIDIA. Só caminhos fixos do driver (nunca o PATH, para não executar
/// um nvidia-smi plantado em outra pasta). Consulta de leitura, sem janela, com tempo-limite.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNvidiaSource : INvidiaSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private readonly string? _path;

    public WindowsNvidiaSource()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        ];
        _path = candidates.FirstOrDefault(File.Exists);
    }

    public bool Available => _path is not null;

    public IReadOnlyList<NvidiaGpuReading> Read()
    {
        if (_path is null) return [];
        var output = ProcessRunner.Run(_path, NvidiaSmi.Arguments, Timeout);
        return output is null ? [] : NvidiaSmi.Parse(output);
    }
}

/// <summary>
/// Saúde dos discos pelos cmdlets de armazenamento do próprio Windows (Get-PhysicalDisk / Get-StorageReliabilityCounter =
/// MSFT_PhysicalDisk / MSFT_StorageReliabilityCounter). PowerShell do sistema por caminho fixo, sem perfil, sem janela.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskHealthSource : IDiskHealthSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public Task<DiskHealthReport> ReadAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var elevated = IsElevated();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return new DiskHealthReport([], elevated, "PowerShell do Windows não encontrado: não dá para ler a saúde dos discos.");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(DiskHealthScript.Text));
        var output = ProcessRunner.Run(powershell, $"-NoProfile -NonInteractive -EncodedCommand {encoded}", Timeout);
        if (output is null) return new DiskHealthReport([], elevated, "A leitura da saúde dos discos não respondeu a tempo.");
        try
        {
            var disks = DiskHealthParser.Parse(output.Trim());
            return DiskHealthReport.From(disks, elevated, disks.Count == 0 ? "O Windows não listou discos físicos (Get-PhysicalDisk vazio)." : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return new DiskHealthReport([], elevated, "Resposta inesperada do Windows ao ler a saúde dos discos.");
        }
    }, cancellationToken);

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

[SupportedOSPlatform("windows")]
internal static class ProcessRunner
{
    /// <summary>Executa sem janela e devolve a saída padrão; null se falhar ou passar do tempo (o processo é encerrado).</summary>
    public static string? Run(string fileName, string arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System),
        };
        try
        {
            using var p = Process.Start(info);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
                return null;
            }
            return p.ExitCode == 0 && stdout.Wait(TimeSpan.FromSeconds(2)) ? stdout.Result : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
