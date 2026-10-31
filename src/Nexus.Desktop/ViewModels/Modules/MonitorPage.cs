namespace Nexus.Desktop.ViewModels;

/// <summary>Página do módulo Monitoring (esqueleto: o agente do módulo preenche).</summary>
public sealed class MonitorPage : PageViewModel
{
    public override string Title => "Monitor";
    public override string Group => "Medir";
    public override string Subtitle => "CPU, GPU, memória, disco e rede ao vivo, e a saúde dos discos.";
}
