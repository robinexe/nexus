namespace Nexus.Desktop.ViewModels;

/// <summary>Página do módulo Boot (esqueleto: o agente do módulo preenche).</summary>
public sealed class StartupPage : PageViewModel
{
    public override string Title => "Inicialização";
    public override string Group => "Sistema";
    public override string Subtitle => "O que abre junto com o Windows e quanto pesa.";
}
