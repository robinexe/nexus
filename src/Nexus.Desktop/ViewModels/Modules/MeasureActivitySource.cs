using System.Runtime.Versioning;
using Nexus.Measurement;
using Nexus.Measurement.Windows;
using Nexus.Monitoring;
using Nexus.Monitoring.Windows;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Fonte das amostras de atividade no Windows: a lista de processos do Monitor (<see cref="WindowsProcessSource"/> +
/// <see cref="ProcessCpuTracker"/>, o mesmo cálculo do Gerenciador de Tarefas) e as leituras do jogo por GetProcessIoCounters
/// (acesso limitado). Só leitura.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsActivitySource : IActivitySource
{
    private readonly WindowsProcessSource _processes = new();
    private readonly ProcessCpuTracker _tracker = new(Environment.ProcessorCount);

    public IReadOnlyList<ProcessCpuReading> ProcessCpu(DateTimeOffset now) =>
        _tracker.Update(_processes.Read(), now).Select(p => new ProcessCpuReading(p.Pid, p.Name, p.CpuPercent)).ToList();

    public ulong? ReadTransferBytes(int pid) => WindowsProcessIo.ReadTransferBytes(pid);
}
