namespace Nexus.Desktop.Services;

/// <summary>Os quatro rótulos de capacidade exigidos. Nenhum outro é usado na interface.</summary>
public enum CapabilityLabel { ImplementedValidated, ImplementedNotValidated, NotSupportedHere, Pending }

public static class CapabilityText
{
    public static string Of(CapabilityLabel l) => l switch
    {
        CapabilityLabel.ImplementedValidated => "implementado e validado",
        CapabilityLabel.ImplementedNotValidated => "implementado, não validado",
        CapabilityLabel.NotSupportedHere => "não suportado neste ambiente",
        _ => "pendente",
    };
}

public sealed record CapabilityRow(string Area, string Capability, CapabilityLabel Label, string Note)
{
    public string LabelText => CapabilityText.Of(Label);
}

/// <summary>
/// Estado declarado das capacidades desta versão. "Validado" exige evidência registrada em docs/06-build-log.md
/// para ESTA versão; a validação de 30/10/2026 no Windows se refere à versão anterior (55 testes) e não é herdada.
/// </summary>
public static class CapabilityCatalog
{
    public static IReadOnlyList<CapabilityRow> Rows(bool isWindows)
    {
        var win = isWindows ? CapabilityLabel.ImplementedNotValidated : CapabilityLabel.NotSupportedHere;
        return
        [
            new("Base", "Compilação e testes unitários", CapabilityLabel.ImplementedValidated,
                "Validado no Windows em 30/10/2026 20:41 (SDK 10.0.403): build com 0 avisos, 99/99 testes. Mudanças posteriores: só a classificação de adaptadores de rede (106 testes em Linux)."),
            new("Base", "Diário SQLite, estados e transições", CapabilityLabel.ImplementedValidated,
                "Testes unitários aprovados no Windows (diário real em arquivo temporário, inclusive esquema 2 e uso entre threads)."),
            new("Base", "Coordenação entre executores (trava do SO)", CapabilityLabel.ImplementedValidated,
                "9 testes com processos reais aprovados no Windows (inclui processo morto no meio da aplicação e da restauração)."),
            new("Recuperação", "nexus-recover runs/show/scan (somente leitura)", CapabilityLabel.ImplementedValidated,
                "Validado no Windows contra diário de teste: exit 0/0/1/3 esperados; SHA-256 do diário inalterado."),
            new("Recuperação", "nexus-recover recover/revert com alteração real", CapabilityLabel.ImplementedNotValidated,
                "Nunca executado contra o sistema real. Só em VM Windows isolada."),
            new("Descoberta", "Windows, CPU/topologia, GPU/driver, memória/SMBIOS, discos, rede, energia, serviços, inicialização", win,
                "Executada no Windows em 30/10 sem falhas (0 valores indisponíveis). Falta você conferir os valores; rede corrigida depois (vínculos de filtro NDIS eram contados como adaptadores)."),
            new("Interface", "Diagnóstico somente leitura", win, "Regras executadas no Windows pelo Nexus.Discover; nesta interface, não conferido."),
            new("Interface", "Catálogo com estado real lido", win, "Leitura pelos handlers reais embrulhados em SimulationOnlyHandler."),
            new("Interface", "Simulação do plano (dry-run)", win, "DesiredStateResolver com handlers somente leitura; nada é aplicado."),
            new("Interface", "Histórico (diário em modo leitura)", win, "Lê %ProgramData%\\NEXUS\\journal.db, se existir."),
            new("Execução", "Aplicar ações reais no Windows hospedeiro", CapabilityLabel.Pending,
                "Bloqueado de propósito até os testes de integração e recuperação em VM Windows serem aprovados."),
            new("Execução", "Broker com operações tipadas e restritas (nexus-broker)", CapabilityLabel.ImplementedNotValidated,
                "Protocolo fechado, validação contra o catálogo do broker, portão de execução (só VM de teste liberada). 36 testes em Linux; não executado no Windows; instalação como serviço pendente."),
            new("Execução", "Testes de aplicação, rollback e recuperação reais em VM", CapabilityLabel.ImplementedNotValidated,
                "Kit pronto (tools\\vm, Windows Sandbox); nunca executado."),
            new("Ações", "EcoQoS do processo (ProcessPowerThrottling)", CapabilityLabel.Pending, "Catalogado; handler não implementado."),
            new("Ações", "NVIDIA DRS", CapabilityLabel.Pending, "Catalogado; handler não implementado."),
            new("Ações", "Tarefas agendadas", CapabilityLabel.Pending, "Nem descoberta nem handler."),
            new("Medição", "Captura PresentMon e comparação de sessões", CapabilityLabel.Pending,
                "Parser de CSV e estatística existem (testados); captura e integração não."),
            new("Jogos", "CS2 .cfg", CapabilityLabel.ImplementedNotValidated, "Handler testado com arquivos temporários; detecção da pasta do jogo pendente."),
            new("Jogos", "VALORANT", CapabilityLabel.Pending, ""),
            new("Outros", "Investigador de stutters, laboratório A/B, relatórios, assistente de IA", CapabilityLabel.Pending, ""),
        ];
    }

    /// <summary>Rótulo da LEITURA de estado de uma ação do catálogo, conforme exista handler.</summary>
    public static CapabilityLabel ForHandler(bool handlerImplemented, bool isWindows) =>
        !handlerImplemented ? CapabilityLabel.Pending
        : isWindows ? CapabilityLabel.ImplementedNotValidated
        : CapabilityLabel.NotSupportedHere;
}
