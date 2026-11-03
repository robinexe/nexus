namespace Nexus.Measurement;

// =====================================================================================================================
// Modo jogo automático (desligado por padrão): o usuário liga uma vez, com confirmação, e escolhe um conjunto fechado de ações
// do catálogo seguras para uma partida (rápidas, reversíveis, sem reinício, sem aviso do Windows a cada jogo). Quando a detecção
// do Medir vê um jogo aberto há StartDelay, o conjunto é aplicado pelo aplicador; quando o jogo fecha há StopDelay (configurável,
// para não desfazer num relançamento), tudo é desfeito pelas EXECUÇÕES do diário (RevertRun), nunca por "valor esperado".
// Esta parte é a lógica pura (testada): a máquina de estados e a seleção das ações. A interface (GameModeAutoPanel) só liga
// isto ao aplicador, à bandeja e ao arquivo game-session.json.
// =====================================================================================================================

/// <summary>O que o modo deve fazer agora.</summary>
public enum GameModeAutoStep { None, Apply, Restore }

/// <summary>Fase, em palavras, do que está acontecendo (para a tela).</summary>
public enum GameModeAutoPhase
{
    /// <summary>Desligado e nada aplicado.</summary>
    Off,
    /// <summary>Ligado, esperando um jogo abrir (ou abrir há tempo suficiente).</summary>
    Waiting,
    /// <summary>Aplicado e o jogo está aberto.</summary>
    Active,
    /// <summary>Aplicado, o jogo fechou agora: esperando a folga (pode ser um relançamento).</summary>
    Closing,
    /// <summary>Aplicado e já deveria ter sido desfeito (desfazendo, ou nova tentativa agendada).</summary>
    Restoring,
    /// <summary>Desfazer falhou repetidas vezes: parou de tentar sozinho; o usuário resolve em Histórico ou "Tentar de novo".</summary>
    UndoFailed,
}

/// <summary>
/// Máquina de estados "jogo abriu → aplicar → jogo fechou (ou NEXUS fechou/PC reiniciou) → desfazer". Sem relógio próprio: quem
/// chama passa o instante. Regras:
/// <list type="bullet">
/// <item>aplica só com o modo ligado e o jogo aberto há <see cref="StartDelay"/> (lançadores abrem e fecham processos);</item>
/// <item>desfaz com o jogo fechado há <see cref="StopDelay"/> (relançamentos e telas de carregamento não desfazem), ou na hora
/// se o usuário desligar o modo;</item>
/// <item>aplicação recusada não se repete até o jogo fechar (sem insistir num aviso negado);</item>
/// <item>desfazer que falha tenta de novo depois de <see cref="RestoreRetryDelay"/>, até <see cref="MaxRestoreFailures"/> vezes;
/// depois para (<see cref="GameModeAutoPhase.UndoFailed"/>) até <see cref="RetryUndo"/>;</item>
/// <item><see cref="Resume"/> retoma um estado aplicado gravado antes (o NEXUS fechou com o jogo aberto): o próximo
/// <see cref="Next"/> sem jogo desfaz depois da folga.</item>
/// </list>
/// </summary>
public sealed class GameModeAutoMachine
{
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultStopDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MinStopDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxStopDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromSeconds(30);
    public const int MaxRestoreFailures = 3;

    private readonly TimeSpan _startDelay;
    private TimeSpan _stopDelay;
    private DateTimeOffset? _runningSince;
    private DateTimeOffset? _stoppedSince;
    private DateTimeOffset? _lastRestoreFailure;
    private bool _attempted;
    private bool _lastEnabled;
    private bool _lastGameRunning;

    public GameModeAutoMachine(TimeSpan? startDelay = null, TimeSpan? stopDelay = null)
    {
        _startDelay = startDelay ?? DefaultStartDelay;
        _stopDelay = ClampStopDelay(stopDelay ?? DefaultStopDelay);
    }

    public TimeSpan StartDelay => _startDelay;

    /// <summary>Folga entre o jogo fechar e o desfazer (configurável pelo usuário; limitada a 5 s–5 min).</summary>
    public TimeSpan StopDelay { get => _stopDelay; set => _stopDelay = ClampStopDelay(value); }

    public static TimeSpan ClampStopDelay(TimeSpan value) => value < MinStopDelay ? MinStopDelay : value > MaxStopDelay ? MaxStopDelay : value;

    /// <summary>O conjunto está aplicado (há execuções para desfazer).</summary>
    public bool Applied { get; private set; }

    /// <summary>Jogo pelo qual o conjunto foi aplicado (o último visto aberto enquanto aplicado).</summary>
    public string? ActiveGame { get; private set; }

    /// <summary>Quantas vezes seguidas o desfazer falhou.</summary>
    public int RestoreFailures { get; private set; }

    /// <summary>Fase atual, a partir da última chamada de <see cref="Next"/>.</summary>
    public GameModeAutoPhase Phase
    {
        get
        {
            if (!Applied) return _lastEnabled ? GameModeAutoPhase.Waiting : GameModeAutoPhase.Off;
            if (RestoreFailures >= MaxRestoreFailures) return GameModeAutoPhase.UndoFailed;
            if (!_lastEnabled) return GameModeAutoPhase.Restoring;
            if (_lastGameRunning) return GameModeAutoPhase.Active;
            return _stoppedSince is { } s && _lastSeen - s < _stopDelay ? GameModeAutoPhase.Closing : GameModeAutoPhase.Restoring;
        }
    }

    private DateTimeOffset _lastSeen;

    /// <summary>Segundos que faltam para desfazer, na fase <see cref="GameModeAutoPhase.Closing"/> (0 fora dela).</summary>
    public int SecondsUntilRestore => Applied && !_lastGameRunning && _stoppedSince is { } s
        ? Math.Max(0, (int)Math.Ceiling((_stopDelay - (_lastSeen - s)).TotalSeconds))
        : 0;

    /// <param name="enabled">O modo está ligado (o usuário ligou e confirmou).</param>
    /// <param name="game">Nome do jogo aberto agora (detecção do Medir), ou null.</param>
    public GameModeAutoStep Next(bool enabled, string? game, DateTimeOffset now)
    {
        var gameRunning = game is not null;
        _lastEnabled = enabled;
        _lastGameRunning = gameRunning;
        _lastSeen = now;
        if (gameRunning)
        {
            _stoppedSince = null;
            _runningSince ??= now;
            if (Applied) ActiveGame = game;
        }
        else
        {
            _runningSince = null;
            _stoppedSince ??= now;
            if (!Applied) _attempted = false; // o jogo fechou: a próxima partida pode tentar de novo
        }

        if (Applied)
        {
            if (RestoreFailures >= MaxRestoreFailures) return GameModeAutoStep.None;
            if (_lastRestoreFailure is { } f && now - f < RestoreRetryDelay) return GameModeAutoStep.None;
            if (!enabled || (!gameRunning && now - _stoppedSince!.Value >= _stopDelay)) return GameModeAutoStep.Restore;
            return GameModeAutoStep.None;
        }
        if (enabled && gameRunning && !_attempted && now - _runningSince!.Value >= _startDelay) return GameModeAutoStep.Apply;
        return GameModeAutoStep.None;
    }

    /// <summary>Resultado da aplicação pedida por <see cref="Next"/>.</summary>
    public void Applying(bool ok, string? game)
    {
        _attempted = true;
        Applied = ok;
        if (ok)
        {
            ActiveGame = game;
            RestoreFailures = 0;
            _lastRestoreFailure = null;
        }
    }

    /// <summary>Resultado do desfazer pedido por <see cref="Next"/> (falhou: nova tentativa depois de <see cref="RestoreRetryDelay"/>).</summary>
    public void Restored(bool ok, DateTimeOffset now)
    {
        if (ok)
        {
            Applied = false;
            ActiveGame = null;
            RestoreFailures = 0;
            _lastRestoreFailure = null;
            return;
        }
        RestoreFailures++;
        _lastRestoreFailure = now;
    }

    /// <summary>O usuário pediu para tentar desfazer de novo (depois de <see cref="GameModeAutoPhase.UndoFailed"/>).</summary>
    public void RetryUndo()
    {
        RestoreFailures = 0;
        _lastRestoreFailure = null;
    }

    /// <summary>Retoma um conjunto que ficou aplicado (o NEXUS fechou com o jogo aberto, ou o PC reiniciou).</summary>
    public void Resume(string? game)
    {
        Applied = true;
        ActiveGame = game;
    }
}

/// <summary>
/// Uma ação do catálogo segura para uma partida. <paramref name="Admin"/>: exige o aplicador de administrador — só entra quando o
/// NEXUS já está aberto como administrador (nenhum aviso do Windows a cada jogo). <paramref name="DefaultOn"/>: marcada ao ligar
/// o modo pela primeira vez.
/// </summary>
public sealed record GameModeAction(string Key, bool Admin, bool DefaultOn)
{
    /// <summary>Id sem a versão (para a tela achar o título no catálogo).</summary>
    public string Id => Key.IndexOf('@', StringComparison.Ordinal) is var i && i > 0 ? Key[..i] : Key;
}

/// <summary>O que o modo vai pedir ao aplicador, por nível, e o que ficou de fora (com o motivo em dados, não em texto).</summary>
public sealed record GameModePlan(IReadOnlyList<string> User, IReadOnlyList<string> Admin, IReadOnlyList<string> SkippedNeedAdmin)
{
    public bool IsEmpty => User.Count == 0 && Admin.Count == 0;
}

/// <summary>
/// Lista FECHADA das ações que o modo jogo automático pode aplicar (em código, não no catálogo, para o teste conferir cada uma
/// contra o catálogo: existe, sem reinício, reversível, nível declarado igual). Critério: rápida, reversível pelo diário, sem
/// reinício, sem efeito fora da partida que o usuário sinta depois, sem aviso do Windows a cada jogo. Sem limpeza, sem serviços,
/// sem registro de sistema, sem nada que "fique".
/// </summary>
public static class GameModeActions
{
    /// <summary>Plano de energia Alto desempenho durante a sessão (o aplicador o reverte ao encerrar; aqui, ao fechar o jogo).</summary>
    public const string PowerPlan = "power.active-scheme.high-performance.session@1";
    /// <summary>Nenhuma notificação pop-up por cima do jogo.</summary>
    public const string Notifications = "sys.notifications.toasts.off@1";
    /// <summary>Gravação em segundo plano da Xbox Game Bar desligada só durante a partida.</summary>
    public const string BackgroundCapture = "win.gamedvr.background-capture.disable@1";
    /// <summary>Downloads da Microsoft Store e do Windows a 20% da internet (política de máquina: administrador).</summary>
    public const string DeliveryCap = "game.session.do-foreground-cap@1";

    public static readonly IReadOnlyList<GameModeAction> All =
    [
        new(PowerPlan, Admin: false, DefaultOn: true),
        new(Notifications, Admin: false, DefaultOn: true),
        new(BackgroundCapture, Admin: false, DefaultOn: false),
        new(DeliveryCap, Admin: true, DefaultOn: false),
    ];

    public static IReadOnlyList<string> DefaultKeys => [.. All.Where(a => a.DefaultOn).Select(a => a.Key)];

    public static GameModeAction? Find(string key) => All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Escolha do usuário saneada: só chaves da lista fechada, sem repetição, na ordem da lista. Null (nunca escolheu) = padrão.
    /// Uma lista vazia continua vazia (o usuário desmarcou tudo).
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? chosen)
    {
        if (chosen is null) return DefaultKeys;
        var set = new HashSet<string>(chosen, StringComparer.Ordinal);
        return [.. All.Where(a => set.Contains(a.Key)).Select(a => a.Key)];
    }

    /// <summary>
    /// O que pedir, por nível. Ações de administrador só entram com <paramref name="processElevated"/> (NEXUS aberto como
    /// administrador: o aplicador de administrador nasce sem aviso do Windows); senão ficam em <see cref="GameModePlan.SkippedNeedAdmin"/>.
    /// </summary>
    public static GameModePlan Plan(IEnumerable<string>? chosen, bool processElevated)
    {
        var keys = Normalize(chosen);
        var user = new List<string>();
        var admin = new List<string>();
        var skipped = new List<string>();
        foreach (var key in keys)
        {
            var action = Find(key)!;
            if (!action.Admin) user.Add(key);
            else if (processElevated) admin.Add(key);
            else skipped.Add(key);
        }
        return new GameModePlan(user, admin, skipped);
    }
}
