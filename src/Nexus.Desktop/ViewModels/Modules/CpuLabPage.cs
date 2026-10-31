namespace Nexus.Desktop.ViewModels;

/// <summary>Página Processador (esqueleto: o agente do módulo preenche).</summary>
public sealed class CpuLabPage : ModulePage
{
    public override string Title => "Processador";
    public override string Group => "Laboratório";
    public override string Subtitle => "Engenharia reversa do seu processador: o que ele é, como se comporta e o que o segura.";
}
