using Nexus.Assistant;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Nomes, títulos e explicações das 13 etapas do Assistente no idioma da interface. A biblioteca (Nexus.Assistant/Steps.cs)
/// guarda o texto em pt-BR, usado também no relatório e na linha de comando; a tela usa esta versão traduzida.
/// Etapa nova na biblioteca sem tradução aqui aparece em pt-BR (ramo padrão).
/// </summary>
public static class AssistantStepTexts
{
    private static IReadOnlyList<StepInfo>? _all;

    public static IReadOnlyList<StepInfo> All => _all ??= AssistantSteps.All.Select(Localize).ToList();

    public static StepInfo Info(AssistantStepId id) => All[(int)id - 1];

    public static StepInfo Localize(StepInfo s) => s.Id switch
    {
        AssistantStepId.Welcome => s with { ShortName = Loc.T("AsstStep.Inicio"), Title = Loc.T("AsstStep.AntesComecar"),
            Checks = Loc.T("AsstStep.TomadaOuBateriaEspaco"),
            Why = Loc.T("AsstStep.PontoRestauracaoRedeSeguranca") },
        AssistantStepId.MyPc => s with { ShortName = Loc.T("Pg.SeuPc"), Title = Loc.T("AsstStep.ConhecendoSeuPc"),
            Checks = Loc.T("AsstStep.ProcessadorPlacaVideoMemoria"),
            Why = Loc.T("AsstStep.MuitosPcsPerdemDesempenho") },
        AssistantStepId.Cleaning => s with { ShortName = Loc.T("AsstStep.Limpeza"), Title = Loc.T("Settings.LimpezaSegura"),
            Checks = Loc.T("AsstStep.CachesTemporariosProgramasRecriam"),
            Why = Loc.T("AsstStep.LiberaEspacoSemTocar") },
        AssistantStepId.Programs => s with { ShortName = Loc.T("AsstStep.Programas"), Title = Loc.T("AsstStep.Programas"),
            Checks = Loc.T("AsstStep.AtualizacoesDisponiveisProgramasMaio"),
            Why = Loc.T("AsstStep.ProgramaDesatualizadoPortaEntrada") },
        AssistantStepId.Startup => s with { ShortName = Loc.T("AsstStep.Inicializacao"), Title = Loc.T("AsstStep.InicializacaoServicos"),
            Checks = Loc.T("AsstStep.AbreJuntoWindowsQuanto"),
            Why = Loc.T("AsstStep.CadaProgramaAbreBoot") },
        AssistantStepId.Privacy => s with { ShortName = Loc.T("Settings.Privacidade"), Title = Loc.T("Settings.Privacidade"),
            Checks = Loc.T("AsstStep.TelemetriaOpcionalAnunciosSugestoes"),
            Why = Loc.T("AsstStep.MenosDadosEnviadosMenos") },
        AssistantStepId.Security => s with { ShortName = Loc.T("AsstStep.Seguranca"), Title = Loc.T("AsstStep.Seguranca"),
            Checks = Loc.T("AsstStep.AntivirusFirewallAtualizacoesPontos"),
            Why = Loc.T("AsstStep.DesempenhoNaoValeNada") },
        AssistantStepId.Drivers => s with { ShortName = Loc.T("AsstStep.Drivers"), Title = Loc.T("AsstStep.Drivers"),
            Checks = Loc.T("AsstStep.AtualizacoesDriverWindowsUpdate"),
            Why = Loc.T("AsstStep.DriverVideoNovoCorrige") },
        AssistantStepId.Memory => s with { ShortName = Loc.T("Pg.Memoria"), Title = Loc.T("Pg.Memoria"),
            Checks = Loc.T("AsstStep.QuemMaisUsaRam"),
            Why = Loc.T("AsstStep.PoucaMemoriaLivreVerdade") },
        AssistantStepId.Performance => s with { ShortName = Loc.T("AsstStep.Desempenho"), Title = Loc.T("AsstStep.Desempenho"),
            Checks = Loc.T("AsstStep.AjustesAutomaticoRecomendaPerfil"),
            Why = Loc.T("AsstStep.SoEntraTemFonte") },
        AssistantStepId.Measurement => s with { ShortName = Loc.T("Cap.Medicao"), Title = Loc.T("AsstStep.MedirVerdade"),
            Checks = Loc.T("AsstStep.MedicaoAutomaticaFpsProxima"),
            Why = Loc.T("AsstStep.SemMedirOtimizacaoChute") },
        AssistantStepId.Schedules => s with { ShortName = Loc.T("AsstStep.Agenda"), Title = Loc.T("AsstStep.ManterDia"),
            Checks = Loc.T("AsstStep.VerificacaoSemanalSoLeitura"),
            Why = Loc.T("AsstStep.PcVoltaAcumularLixo") },
        AssistantStepId.Report => s with { ShortName = Loc.T("AsstStep.Relatorio"), Title = Loc.T("AsstStep.RelatorioFinal"),
            Checks = Loc.T("AsstStep.TudoFoiVerificadoAlterado"),
            Why = Loc.T("AsstStep.VoceSaberMudouGuardar") },
        _ => s,
    };
}
