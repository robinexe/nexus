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
            new("Base", "Compilação e testes unitários", CapabilityLabel.ImplementedValidated,
                "Windows (SDK 10.0.403) e VM Windows do CI: build com 0 avisos, todos os testes unitários aprovados."),
            new("Base", "Diário SQLite, estados e transições (esquema 3)", CapabilityLabel.ImplementedValidated,
                "Testes com diário real em arquivo temporário, inclusive migração de esquema e uso entre threads."),
            new("Base", "Coordenação entre executores (trava do SO)", CapabilityLabel.ImplementedValidated,
                "Testes com processos reais aprovados no Windows (inclui processo morto no meio da aplicação e da restauração)."),
            new("Recuperação", "nexus-recover runs/show/scan/accept", CapabilityLabel.ImplementedValidated,
                "Validado no Windows contra diário de teste; accept com processos reais."),
            new("Recuperação", "nexus-recover recover com alteração real", CapabilityLabel.ImplementedValidated,
                "Validado na VM do CI: escrita real no registro, crash simulado, CLI real restaurou e removeu a chave criada."),
            new("Descoberta", "Windows, CPU/topologia, GPU/driver, memória/SMBIOS, discos, rede, energia, serviços, inicialização", win,
                "Conferida em 31/10 contra uma coleta do próprio Windows: igual, exceto modelo do disco truncado e SSD/HDD (limitação conhecida)."),
            new("Interface", "Diagnóstico, catálogo com estado real, simulação local, histórico", win,
                "Somente leitura (handlers em SimulationOnlyHandler, diário aberto só para leitura)."),
            new("Interface", "Aplicar/reverter pela página Broker", CapabilityLabel.ImplementedNotValidated,
                "Única via de escrita da interface: pedidos ao serviço NexusBroker, com confirmação. Lógica testada; a interface com o serviço instalado ainda não foi exercitada."),
            new("Execução", "Broker com operações tipadas e restritas (console e serviço)", CapabilityLabel.ImplementedValidated,
                "VM do CI: serviço LocalSystem instalado; HKCU do cliente aplicado e revertido; HKCU do SYSTEM intocado; dono do pipe conferido; ACLs da trava e dos dados."),
            new("Execução", "Ações reais em VM (registro, energia, SysMain, core parking)", CapabilityLabel.ImplementedValidated,
                "VM do CI: 6/6 testes com alteração e reversão reais."),
            new("Execução", "Aplicar ações reais neste PC (hospedeiro físico)", CapabilityLabel.Pending,
                "Bloqueado de propósito: o portão do broker exige VM de teste liberada."),
            new("Execução", "Assinatura Authenticode do cliente do broker", CapabilityLabel.Pending, "Exige certificado de assinatura de código."),
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
