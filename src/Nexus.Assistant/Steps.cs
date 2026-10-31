namespace Nexus.Assistant;

/// <summary>As etapas do assistente, na ordem em que acontecem. O número (1–13) é o que o usuário vê.</summary>
public enum AssistantStepId
{
    Welcome = 1,
    MyPc,
    Cleaning,
    Programs,
    Startup,
    Privacy,
    Security,
    Drivers,
    Memory,
    Performance,
    Measurement,
    Schedules,
    Report,
}

/// <summary>Texto fixo de cada etapa: nome curto (barra de etapas), título, o que verifica, por que importa e um ícone.</summary>
/// <param name="Glyph">Caractere da fonte Segoe Fluent Icons / MDL2 Assets (presente no Windows 10/11).</param>
public sealed record StepInfo(AssistantStepId Id, string ShortName, string Title, string Checks, string Why, string Glyph)
{
    public int Number => (int)Id;
}

public static class AssistantSteps
{
    public static IReadOnlyList<StepInfo> All { get; } =
    [
        new(AssistantStepId.Welcome, "Início", "Antes de começar",
            "Tomada ou bateria, espaço livre no disco, alterações que ficaram pela metade e um ponto de restauração.",
            "Um ponto de restauração é a rede de segurança do Windows: se algo der errado, dá para voltar o sistema inteiro.", ""),
        new(AssistantStepId.MyPc, "Seu PC", "Conhecendo o seu PC",
            "Processador, placa de vídeo e memória; canais da RAM, taxa do monitor, idade do driver de vídeo e o link PCIe.",
            "Muitos PCs perdem desempenho por detalhes de hardware que nenhum programa resolve sozinho: um módulo de RAM só, monitor a 60 Hz, driver antigo.", ""),
        new(AssistantStepId.Cleaning, "Limpeza", "Limpeza segura",
            "Caches e temporários que os programas recriam sozinhos.",
            "Libera espaço sem tocar em documentos, senhas ou histórico. Tudo vai para a quarentena e pode voltar.", ""),
        new(AssistantStepId.Programs, "Programas", "Programas",
            "Atualizações disponíveis e os programas maiores e mais antigos.",
            "Programa desatualizado é porta de entrada para ataque; programa esquecido ocupa espaço e às vezes abre com o Windows.", ""),
        new(AssistantStepId.Startup, "Inicialização", "Inicialização e serviços",
            "O que abre junto com o Windows e quanto atrasa o boot (medido pelo próprio Windows).",
            "Cada programa que abre no boot disputa disco e processador nos primeiros minutos.", ""),
        new(AssistantStepId.Privacy, "Privacidade", "Privacidade",
            "Telemetria opcional, anúncios, sugestões e rastreamento de atividade.",
            "Menos dados enviados e menos propaganda no Iniciar. Não acelera o PC; é sobre o que sai dele.", ""),
        new(AssistantStepId.Security, "Segurança", "Segurança",
            "Antivírus, firewall, atualizações e pontos suspeitos de inicialização.",
            "Desempenho não vale nada num PC comprometido.", ""),
        new(AssistantStepId.Drivers, "Drivers", "Drivers",
            "Atualizações de driver do Windows Update e a idade do driver de vídeo.",
            "Driver de vídeo novo corrige travamentos e melhora jogos recentes.", ""),
        new(AssistantStepId.Memory, "Memória", "Memória",
            "Quem mais usa a RAM agora, compressão de memória e arquivo de paginação.",
            "Pouca memória livre de verdade causa travadas; o arquivo de paginação evita que jogos fechem sozinhos.", ""),
        new(AssistantStepId.Performance, "Desempenho", "Desempenho",
            "Os ajustes que o Automático recomenda para o perfil do seu PC.",
            "Só entra o que tem fonte e risco baixo; o resto fica para você decidir.", ""),
        new(AssistantStepId.Measurement, "Medição", "Medir de verdade",
            "Medição automática de FPS na próxima partida e o mostrador na tela.",
            "Sem medir, \"otimização\" é chute. Uma partida antes e outra depois mostram se mudou algo.", ""),
        new(AssistantStepId.Schedules, "Agenda", "Manter em dia",
            "Verificação semanal (só leitura) e, se você quiser, limpeza segura agendada.",
            "O PC volta a acumular lixo e configurações mudam com atualizações. Uma checagem por semana avisa.", ""),
        new(AssistantStepId.Report, "Relatório", "Relatório final",
            "Tudo o que foi verificado, alterado e pulado, com números antes e depois.",
            "Para você saber o que mudou, guardar e desfazer quando quiser.", ""),
    ];

    public static StepInfo Info(AssistantStepId id) => All[(int)id - 1];

    public static AssistantStepId First => AssistantStepId.Welcome;
    public static AssistantStepId Last => AssistantStepId.Report;
    public static int Count => All.Count;

    public static AssistantStepId? Next(AssistantStepId id) => id == Last ? null : id + 1;
    public static AssistantStepId? Previous(AssistantStepId id) => id == First ? null : id - 1;
}
