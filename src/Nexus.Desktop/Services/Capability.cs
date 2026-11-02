namespace Nexus.Desktop.Services;

/// <summary>Os quatro rótulos de capacidade exigidos. Nenhum outro é usado na interface.</summary>
public enum CapabilityLabel { ImplementedValidated, ImplementedNotValidated, NotSupportedHere, Pending }

public static class CapabilityText
{
    public static string Of(CapabilityLabel l) => l switch
    {
        CapabilityLabel.ImplementedValidated => Loc.T("Cap.ImplementadoValidado"),
        CapabilityLabel.ImplementedNotValidated => Loc.T("Cap.ImplementadoNaoValidado"),
        CapabilityLabel.NotSupportedHere => Loc.T("Cap.NaoSuportadoNesteAmbiente"),
        _ => "pendente",
    };
}

public sealed record CapabilityRow(string Area, string Capability, CapabilityLabel Label, string Note)
{
    public string LabelText => CapabilityText.Of(Label);
}

/// <summary>
/// Estado declarado das capacidades desta versão. "Validado" exige evidência registrada em docs/06-build-log.md.
/// Atualizado em 31/10/2026: Windows do responsável (163/163) e VM descartável do GitHub Actions (integração e serviço).
/// </summary>
public static class CapabilityCatalog
{
    public static IReadOnlyList<CapabilityRow> Rows(bool isWindows)
    {
        var win = isWindows ? CapabilityLabel.ImplementedNotValidated : CapabilityLabel.NotSupportedHere;
        return
        [
            new(Loc.T("Cap.Base"), Loc.T("Cap.CompilacaoTestesUnitarios"), CapabilityLabel.ImplementedValidated,
                Loc.T("Cap.WindowsSdk100")),
            new(Loc.T("Cap.Base"), Loc.T("Cap.DiarioSqliteEstadosTransicoes"), CapabilityLabel.ImplementedValidated,
                Loc.T("Cap.TestesDiarioRealArquivo")),
            new(Loc.T("Cap.Base"), Loc.T("Cap.CoordenacaoEntreExecutoresTrava"), CapabilityLabel.ImplementedValidated,
                Loc.T("Cap.TestesProcessosReaisAprovados")),
            new(Loc.T("Cap.Recuperacao"), Loc.T("Cap.NexusRecoverRunsShow"), CapabilityLabel.ImplementedValidated,
                Loc.T("Cap.ValidadoWindowsContraDiario")),
            new(Loc.T("Cap.Recuperacao"), Loc.T("Cap.NexusRecoverRecoverAlteracao"), CapabilityLabel.ImplementedValidated,
                Loc.T("Cap.ValidadoVmCiEscrita")),
            new(Loc.T("Cap.Descoberta"), Loc.T("Cap.WindowsCpuTopologiaGpu"), win,
                Loc.T("Cap.Conferida3110Contra")),
            new(Loc.T("Cap.Interface"), Loc.T("Cap.DiagnosticoCatalogoEstadoReal"), win,
                Loc.T("Cap.SomenteLeituraHandlersSimulationonly")),
            new(Loc.T("Cap.Interface"), Loc.T("Cap.AplicarReverterPelaPagina"), CapabilityLabel.ImplementedNotValidated,
                Loc.T("Cap.AplicaNestePcAplicador")),
            new(Loc.T("Cap.Execucao"), Loc.T("Cap.BrokerOperacoesTipadasRestritas"), CapabilityLabel.ImplementedValidated,
                Loc.T("Cap.VmCiServicoLocalsystem")),
            new(Loc.T("Cap.Execucao"), Loc.T("Cap.AcoesReaisVmRegistro"), CapabilityLabel.ImplementedValidated,
                Loc.T("Cap.VmCi66")),
            new(Loc.T("Cap.Execucao"), Loc.T("Cap.AplicarAcoesReaisNeste"), CapabilityLabel.ImplementedNotValidated,
                Loc.T("Cap.Liberado31102026")),
            new(Loc.T("Cap.Execucao"), Loc.T("Cap.AssinaturaAuthenticodeClienteBroker"), CapabilityLabel.Pending, Loc.T("Cap.ExigeCertificadoAssinaturaCodigo")),
            new(Loc.T("Cap.Acoes"), Loc.T("Cap.EcoqosProcessoProcesspowerthrottling"), CapabilityLabel.Pending, Loc.T("Cap.AdiadoForaCatalogoAtivo")),
            new(Loc.T("Cap.Acoes"), Loc.T("Cap.NvidiaDrs"), CapabilityLabel.Pending, Loc.T("Cap.CatalogadoHandlerNaoImplementado")),
            new(Loc.T("Cap.Acoes"), Loc.T("Cap.TarefasAgendadas"), CapabilityLabel.Pending, Loc.T("Cap.NemDescobertaNemHandler")),
            new(Loc.T("Cap.Medicao"), Loc.T("Cap.ImportarCsvVerTraco"), CapabilityLabel.ImplementedNotValidated,
                Loc.T("Cap.FuncionouWindowsCapturasSinteticas")),
            new(Loc.T("Cap.Medicao"), Loc.T("Cap.GravarPresentmonPeloNexus"), CapabilityLabel.ImplementedNotValidated,
                Loc.T("Cap.TestadoPresentmonFalsoArgumentos")),
            new(Loc.T("Cap.Jogos"), "CS2 .cfg", CapabilityLabel.ImplementedNotValidated, Loc.T("Cap.HandlerTestadoArquivosTemporarios")),
            new(Loc.T("Cap.Jogos"), "VALORANT", CapabilityLabel.Pending, ""),
            new(Loc.T("Cap.Outros"), Loc.T("Cap.InvestigadorStuttersLaboratorioB"), CapabilityLabel.Pending, ""),
        ];
    }

    /// <summary>Rótulo da LEITURA de estado de uma ação do catálogo, conforme exista handler.</summary>
    public static CapabilityLabel ForHandler(bool handlerImplemented, bool isWindows) =>
        !handlerImplemented ? CapabilityLabel.Pending
        : isWindows ? CapabilityLabel.ImplementedNotValidated
        : CapabilityLabel.NotSupportedHere;
}
