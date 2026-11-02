namespace Nexus.Desktop.ViewModels;

/// <summary>Estado da pergunta "Manter esta taxa?" depois de trocar o modo do monitor.</summary>
public enum KeepChangeState
{
    /// <summary>Contando: sem resposta ainda.</summary>
    Asking,
    /// <summary>O usuário clicou "Manter".</summary>
    Kept,
    /// <summary>O usuário clicou "Voltar agora".</summary>
    RevertRequested,
    /// <summary>O tempo acabou sem resposta: volta sozinho (a tela pode estar preta e o usuário sem ver o botão).</summary>
    TimedOut,
}

/// <summary>
/// Lógica pura da contagem "Manter esta taxa? Volta sozinho em 15 s" (como a janela do próprio Windows ao trocar o modo
/// de vídeo). Sem relógio próprio: quem usa chama <see cref="Tick"/> com o tempo passado. A primeira resposta vence; as
/// seguintes são ignoradas (um clique em "Manter" no mesmo instante em que o tempo acaba não desfaz nada duas vezes).
/// </summary>
public sealed class KeepChangeCountdown
{
    public const int DefaultSeconds = 15;

    private readonly TimeSpan _total;
    private TimeSpan _elapsed;

    public KeepChangeCountdown(int seconds = DefaultSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 1);
        _total = TimeSpan.FromSeconds(seconds);
    }

    public KeepChangeState State { get; private set; } = KeepChangeState.Asking;

    /// <summary>Terminou (mantido, pedido para voltar, ou tempo esgotado).</summary>
    public bool Finished => State != KeepChangeState.Asking;

    /// <summary>Deve desfazer (pedido ou tempo esgotado).</summary>
    public bool ShouldRevert => State is KeepChangeState.RevertRequested or KeepChangeState.TimedOut;

    /// <summary>Segundos inteiros que faltam (arredondados para cima: 15, 14, …, 1, 0).</summary>
    public int SecondsLeft => (int)Math.Ceiling(Math.Max(0, (_total - _elapsed).TotalSeconds));

    /// <summary>Fração restante (1 → 0), para a barra.</summary>
    public double Fraction => Math.Clamp(1 - _elapsed.TotalSeconds / _total.TotalSeconds, 0, 1);

    public string Prompt(string what) => State switch
    {
        KeepChangeState.Asking => Loc.F("Shell.ManterNVoltaSozinho", what, SecondsLeft),
        KeepChangeState.Kept => Loc.F("Shell.MantidoN", what),
        _ => Loc.T("Shell.VoltandoModoAnterior"),
    };

    /// <summary>Avança o relógio. Retorna true quando ESTE tique esgotou o tempo (desfazer agora).</summary>
    public bool Tick(TimeSpan elapsed)
    {
        if (Finished || elapsed <= TimeSpan.Zero) return false;
        _elapsed += elapsed;
        if (_elapsed < _total) return false;
        _elapsed = _total;
        State = KeepChangeState.TimedOut;
        return true;
    }

    /// <summary>"Manter". Retorna false se já tinha terminado.</summary>
    public bool Keep()
    {
        if (Finished) return false;
        State = KeepChangeState.Kept;
        return true;
    }

    /// <summary>"Voltar agora". Retorna false se já tinha terminado.</summary>
    public bool RevertNow()
    {
        if (Finished) return false;
        State = KeepChangeState.RevertRequested;
        return true;
    }
}
