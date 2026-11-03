namespace Nexus.Measurement.AbLab;

// Laboratório A/B ("Testar no meu PC"): prova, NESTE PC, se um ajuste ajuda num jogo. O usuário joga uma partida sem o ajuste
// (A), o NEXUS aplica o ajuste pelo aplicador, ele joga de novo (B) e, se pediu mais certeza, mais uma sem (A de novo). A
// comparação usa a estatística que o Medir já usa (trechos de 30 s pareados, IC por bootstrap, diferença mínima relevante). Só
// fica o que melhorar além da variação normal; o resto é desfeito (com o consentimento dado no começo). Tudo aqui é lógica pura:
// a interface só executa os comandos (aplicar/desfazer) e entrega as partidas medidas. Textos para pessoas ficam no Desktop
// (três línguas): aqui só códigos e números.

/// <summary>Desenho do teste: A/B (sem, com) ou A/B/A (sem, com, sem — controla a deriva: aquecimento, cena, horário).</summary>
public enum AbDesign { AB, ABA }

/// <summary>Uma ação do catálogo que o ajuste pede (mesmos campos do pedido ao aplicador), serializável.</summary>
public sealed record AbActionRef(string ActionId, string? Value = null, IReadOnlyDictionary<string, string>? Arguments = null, bool Admin = false);

/// <summary>Um ajuste candidato. <paramref name="Title"/> já vem na língua da interface (fica no histórico como foi mostrado).</summary>
public sealed record AbCandidate(string Id, string Title, IReadOnlyList<AbActionRef> Actions);

/// <summary>Execução do diário que desfaz o ajuste aplicado (com o nível do aplicador que a gravou).</summary>
public sealed record AbRunRef(Guid Run, bool Admin);

/// <summary>Em que passo o teste está.</summary>
public enum AbStep
{
    /// <summary>Esperando a partida sem o ajuste (A).</summary>
    WaitBaseline,
    /// <summary>Comando: aplicar o ajuste.</summary>
    Applying,
    /// <summary>Esperando a partida com o ajuste (B).</summary>
    WaitTreatment,
    /// <summary>A/B/A: comando desfazer antes da partida de conferência.</summary>
    Reverting,
    /// <summary>A/B/A: esperando a segunda partida sem o ajuste.</summary>
    WaitBaseline2,
    /// <summary>Comando: comparar as partidas.</summary>
    Deciding,
    /// <summary>Sem desfazer automático: perguntar ao usuário se mantém.</summary>
    AskUser,
    /// <summary>Comando: desfazer o ajuste (não melhorou, cancelado ou falhou).</summary>
    Undoing,
    /// <summary>A/B/A que melhorou: aplicar de novo (a última partida foi sem o ajuste).</summary>
    Reapplying,
    Done,
}

/// <summary>O que a interface precisa fazer agora.</summary>
public enum AbCommand { None, Apply, Revert, Decide, Ask }

/// <summary>Como o teste terminou.</summary>
public enum AbOutcome { None, Kept, Undone, KeptByUser, AlreadyOn, Cancelled, Failed }

/// <summary>Por que parou (com <see cref="AbOutcome.Failed"/>).</summary>
public enum AbFailure { None, ApplyFailed, RevertFailed, ReapplyFailed, Interrupted }

/// <summary>Por que uma partida medida não entrou no teste.</summary>
public enum AbRejection
{
    /// <summary>Outro jogo: ignorada sem aviso.</summary>
    OtherGame,
    /// <summary>Não estamos esperando partida agora (aplicando, comparando…).</summary>
    NotWaiting,
    /// <summary>A partida começou antes deste passo (pode ter sido com o estado anterior).</summary>
    StartedBeforeStep,
    Excluded,
    TooShort,
    TooFewFrames,
    ResolutionChanged,
    BatteryChanged,
    DriverChanged,
    /// <summary>A mesma partida de novo (o Medir regravou).</summary>
    Duplicate,
}

/// <summary>Limites do teste. Mínimo de 150 s: dá pelo menos 3 trechos de 30 s além do primeiro (carregamento).</summary>
public sealed record AbRules(double MinSeconds = 150, int MinFrames = 3000, double DurationRatioWarning = 2.0)
{
    public static AbRules Default { get; } = new();
}

/// <summary>Uma métrica do resultado. <paramref name="Decisive"/> = entra na decisão (a variação do frametime é só indicativa).</summary>
public sealed record AbMetric(string Key, double Before, double After, double DeltaPercent, Nexus.Experimentation.Verdict Verdict,
    double? CiLowPercent, double? CiHighPercent, bool Noisy, bool Decisive);

/// <summary>Resumo da decisão.</summary>
public enum AbReason { Improved, Regressed, Mixed, NoChange, Inconclusive, Noisy, Insufficient }

/// <summary>Avisos de comparabilidade (a cena não é conhecida pelo NEXUS).</summary>
public enum AbWarning { DurationMismatch, PresentModeChanged, BaselineDrift, SameScene }

/// <summary>Resultado da comparação e a decisão: manter só com melhora comprovada e nenhuma piora.</summary>
/// <param name="HighlightKey">Métrica para a frase curta ("+18% no 1% low"): a maior melhora (ou a pior piora).</param>
/// <param name="Pairs">Trechos pareados usados (por comparação).</param>
/// <param name="Bracketed">A/B/A: a partida com o ajuste foi comparada com as duas sem.</param>
public sealed record AbJudgement(bool Keep, AbReason Reason, IReadOnlyList<AbMetric> Metrics, string? HighlightKey, double? HighlightPercent,
    IReadOnlyList<AbWarning> Warnings, int Pairs, bool Bracketed)
{
    public string? PresentModeBefore { get; init; }
    public string? PresentModeAfter { get; init; }
}

/// <summary>Estado de um teste (imutável; gravado em disco a cada passo para sobreviver a fechar o NEXUS no meio).</summary>
public sealed record AbTestState
{
    public required string Id { get; init; }
    /// <summary>Chave do jogo (executável em minúsculas, <see cref="GameKey"/>).</summary>
    public required string GameKey { get; init; }
    public required string GameName { get; init; }
    public required AbCandidate Candidate { get; init; }
    public AbDesign Design { get; init; }
    /// <summary>Consentimento dado no começo: desfazer sozinho se não melhorar.</summary>
    public bool AutoUndo { get; init; } = true;
    public AbStep Step { get; init; }
    /// <summary>Quando o passo atual começou (partidas que começaram antes não contam).</summary>
    public DateTimeOffset StepSince { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? Baseline { get; init; }
    public string? Treatment { get; init; }
    public string? Baseline2 { get; init; }
    /// <summary>Condições da primeira partida aceita (resolução, bateria, driver devem ficar iguais).</summary>
    public SessionConditions? Reference { get; init; }
    /// <summary>Execuções que desfazem o ajuste enquanto ele está aplicado.</summary>
    public IReadOnlyList<AbRunRef> Runs { get; init; } = [];
    public AbJudgement? Judgement { get; init; }
    public AbOutcome Outcome { get; init; }
    public AbFailure Failure { get; init; }
    public bool CancelRequested { get; init; }
}

/// <summary>Um teste terminado, para o histórico por jogo ("Testado no seu PC: Modo desempenho +18% no 1% low — mantido").</summary>
public sealed record AbResult(string Id, string GameKey, string GameName, string CandidateId, string CandidateTitle, DateTimeOffset At,
    AbDesign Design, AbOutcome Outcome, AbFailure Failure, AbJudgement? Judgement)
{
    public static AbResult From(AbTestState s, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.GameKey, s.GameName, s.Candidate.Id, s.Candidate.Title, at, s.Design, s.Outcome, s.Failure, s.Judgement);
    }
}

/// <summary>Tudo o que o laboratório guarda: o teste em andamento, a fila dos próximos ajustes e o histórico.</summary>
public sealed record AbLabData
{
    public AbTestState? Active { get; init; }
    /// <summary>Próximos ajustes escolhidos (testados um de cada vez, no mesmo jogo, com as mesmas opções).</summary>
    public IReadOnlyList<AbCandidate> Queue { get; init; } = [];
    public IReadOnlyList<AbResult> History { get; init; } = [];

    public static AbLabData Empty { get; } = new();
}
