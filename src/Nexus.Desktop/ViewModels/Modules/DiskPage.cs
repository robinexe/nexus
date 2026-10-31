namespace Nexus.Desktop.ViewModels;

/// <summary>Página do módulo DiskTools (esqueleto: o agente do módulo preenche).</summary>
public sealed class DiskPage : PageViewModel
{
    public override string Title => "Espaço em disco";
    public override string Group => "Limpar";
    public override string Subtitle => "Veja o que ocupa o seu disco, pasta por pasta.";
}
