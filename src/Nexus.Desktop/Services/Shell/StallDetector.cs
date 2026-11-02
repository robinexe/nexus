namespace Nexus.Desktop.Services.Shell;

/// <summary>O que o detector concluiu num tique do vigia.</summary>
public enum StallEventKind
{
    None,
    /// <summary>A thread da interface está sem responder há mais que o limite (anotar uma vez).</summary>
    Stalled,
    /// <summary>O processo inteiro ficou parado (suspensão/hibernação/Modern Standby): não é travamento da interface.</summary>
    ResumedFromSleep,
}

public readonly record struct StallEvent(StallEventKind Kind, double Seconds)
{
    public static StallEvent None => new(StallEventKind.None, 0);
}

/// <summary>
/// Lógica pura do vigia da interface (sem relógio próprio: quem chama passa os tempos, em ms).
/// <para>
/// Dois relógios: <c>wallMs</c> (conta o tempo de suspensão, ex.: GetTickCount64) e <c>awakeMs</c> (não conta, ex.:
/// QueryUnbiasedInterruptTime). O silêncio da interface é medido no relógio "acordado", então dormir não vira travamento.
/// Além disso, se o próprio vigia (um timer do pool, que não depende da thread da interface) ficou sem tiques por mais de
/// <see cref="FrozenMs"/>, o PROCESSO inteiro estava parado — PC suspenso, Modern Standby (onde o relógio "acordado" pode
/// continuar andando) ou depurador — e o silêncio é zerado em vez de anotado.
/// </para>
/// </summary>
public sealed class StallDetector
{
    private readonly long _thresholdMs;
    private long _lastPongAwake;
    private long _lastTickWall = -1, _lastTickAwake = -1;
    private bool _reported;

    /// <param name="thresholdMs">Silêncio da interface que conta como travamento (padrão 2 s).</param>
    /// <param name="frozenMs">Intervalo entre tiques do vigia (que tica a cada 1 s) acima do qual o processo é tido como suspenso.</param>
    public StallDetector(long startAwakeMs, long thresholdMs = 2000, long frozenMs = 5000)
    {
        _thresholdMs = thresholdMs;
        FrozenMs = frozenMs;
        _lastPongAwake = startAwakeMs;
    }

    public long FrozenMs { get; }

    /// <summary>Há um travamento anotado e ainda não encerrado por <see cref="Pong"/>.</summary>
    public bool Reported => _reported;

    /// <summary>Tique do vigia (thread do pool). Retorna o que anotar.</summary>
    public StallEvent Tick(long wallMs, long awakeMs)
    {
        var first = _lastTickWall < 0;
        var wallGap = first ? 0 : wallMs - _lastTickWall;
        var awakeGap = first ? 0 : awakeMs - _lastTickAwake;
        _lastTickWall = wallMs;
        _lastTickAwake = awakeMs;

        // O vigia não ticou por muito tempo (pelo relógio de parede ou pelo "acordado"), ou a parede andou muito mais que o
        // relógio acordado: o processo estava parado. Recomeça a contar do zero.
        if (!first && (wallGap > FrozenMs || awakeGap > FrozenMs || wallGap - awakeGap > FrozenMs))
        {
            _lastPongAwake = awakeMs;
            _reported = false;
            return new(StallEventKind.ResumedFromSleep, Math.Max(wallGap, awakeGap) / 1000.0);
        }

        var silent = awakeMs - _lastPongAwake;
        if (silent > _thresholdMs && !_reported)
        {
            _reported = true;
            return new(StallEventKind.Stalled, silent / 1000.0);
        }
        return StallEvent.None;
    }

    /// <summary>A thread da interface respondeu. Retorna os segundos parados se havia travamento anotado (para "voltou depois de").</summary>
    public double? Pong(long awakeMs)
    {
        double? recovered = _reported ? (awakeMs - _lastPongAwake) / 1000.0 : null;
        _reported = false;
        _lastPongAwake = awakeMs;
        return recovered;
    }

    /// <summary>Aviso do Windows (PowerModeChanged Suspend/Resume): zera o silêncio e esquece o último tique.</summary>
    public void ResetAt(long awakeMs)
    {
        _lastPongAwake = awakeMs;
        _reported = false;
        _lastTickWall = _lastTickAwake = -1;
    }
}
