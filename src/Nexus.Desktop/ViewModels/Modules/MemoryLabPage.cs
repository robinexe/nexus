namespace Nexus.Desktop.ViewModels;

/// <summary>Página Memória RAM (esqueleto: o agente do módulo preenche).</summary>
public sealed class MemoryLabPage : ModulePage
{
    public override string Title => "Memória RAM";
    public override string Group => "Laboratório";
    public override string Subtitle => "Engenharia reversa da sua memória: módulos, canais, velocidade real e latência medida.";
}
