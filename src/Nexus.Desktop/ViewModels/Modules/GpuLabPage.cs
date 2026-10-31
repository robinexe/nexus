namespace Nexus.Desktop.ViewModels;

/// <summary>Página Placa de vídeo (esqueleto: o agente do módulo preenche).</summary>
public sealed class GpuLabPage : ModulePage
{
    public override string Title => "Placa de vídeo";
    public override string Group => "Laboratório";
    public override string Subtitle => "Engenharia reversa da sua placa de vídeo: barramento, clocks, limites e monitores.";
}
