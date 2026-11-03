using System.Windows.Threading;
using Nexus.Broker;
using Nexus.Measurement;
using Nexus.Measurement.Windows;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>Uma ação do conjunto, em palavras simples, com a caixa de marcar (a escolha é gravada na hora).</summary>
public sealed class GameModeActionRow : ObservableObject
{
    private readonly Action<GameModeActionRow> _changed;
    private bool _isOn;

    internal GameModeActionRow(GameModeAction action, string text, bool isOn, bool available, string note, Action<GameModeActionRow> changed)
    {
        Action = action;
        Text = text;
        _isOn = isOn;
        Available = available;
        Note = note;
        _changed = changed;
    }

    public GameModeAction Action { get; }
    /// <summary>"Plano de energia Alto desempenho só enquanto o jogo está aberto".</summary>
    public string Text { get; }
    /// <summary>Falso = exige o NEXUS aberto como administrador (a caixa fica desligada, com a nota).</summary>
    public bool Available { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    public bool IsOn { get => _isOn; set { if (Set(ref _isOn, value)) _changed(this); } }
}

/// <summary>
/// Modo jogo automático (tela Jogos, desligado por padrão): o usuário liga uma vez, com confirmação, e escolhe o que ele faz
/// (lista fechada <see cref="GameModeActions"/>). Com um jogo aberto (detecção do Medir), aplica o conjunto pelo aplicador
/// (<see cref="IModuleHost.ApplyConfirmedAsync"/>: diário e releitura); com o jogo fechado há N segundos (configurável), desfaz as
/// execuções do diário (<see cref="IModuleHost.RevertWithoutConfirmationAsync"/>), nunca por "valor esperado". As execuções
/// aplicadas ficam em %LocalAppData%\NEXUS\game-session.json: se o NEXUS fechar com o jogo aberto (ou o PC reiniciar), desfaz na
/// próxima abertura. Se o desfazer falhar três vezes, para de tentar e o problema aparece em Problemas e no Histórico. Avisa na
/// bandeja ao aplicar e ao desfazer. Lógica pura em <see cref="GameModeAutoMachine"/>; aqui só a ligação com a interface.
/// </summary>
public sealed class GameModeAutoPanel : ObservableObject
{
    public const string ProblemKey = "gamemode:undo-failed";
    private static readonly int[] DelayChoices = [5, 15, 30, 60, 120];

    private readonly Func<IModuleHost?> _host;
    private readonly Func<MeasurePage?> _measure;
    private readonly Func<bool> _elevated;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly GameModeAutoMachine _machine = new();
    private readonly GameSessionStore _store;
    private GameSessionState _state = GameSessionState.Off;
    private bool _working;
    private string _status = "";
    private IReadOnlyList<string> _warnings = [];
    private string? _gameName;
    private GameModeAutoPhase _phase = GameModeAutoPhase.Off;
    private DelayOption _stopDelay;

    public GameModeAutoPanel(Func<IModuleHost?> host, Func<MeasurePage?> measure, string? statePath = null, Func<bool>? processElevated = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _measure = measure ?? throw new ArgumentNullException(nameof(measure));
        _elevated = processElevated ?? LocalBroker.CurrentProcessIsElevated;
        _store = new GameSessionStore(statePath ?? GameSessionStore.DefaultPath);
        _timer.Tick += async (_, _) => await TickAsync();
        ToggleCommand = new RelayCommand(Toggle, () => !_working);
        RetryUndoCommand = new RelayCommand(() => { _machine.RetryUndo(); _ = TickAsync(); }, () => !_working && _phase == GameModeAutoPhase.UndoFailed);
        StopDelayOptions = [.. DelayChoices.Select(s => new DelayOption(Loc.F("GameAuto.Seconds", s), s))];
        _state = _store.Load();
        _machine.StopDelay = TimeSpan.FromSeconds(_state.StopDelaySeconds ?? (int)GameModeAutoMachine.DefaultStopDelay.TotalSeconds);
        _stopDelay = StopDelayOptions.FirstOrDefault(o => o.Seconds == (int)_machine.StopDelay.TotalSeconds) ?? StopDelayOptions[1];
        if (_state.HasPending) _machine.Resume(_state.PendingGame);
        Items = [.. GameModeActions.All.Select(Row)];
        UpdateStatus();
    }

    /// <summary>Folga antes de desfazer, em palavras ("15 segundos").</summary>
    public sealed record DelayOption(string Label, int Seconds);

    public RelayCommand ToggleCommand { get; }
    /// <summary>"Tentar desfazer de novo" (só quando o desfazer automático desistiu).</summary>
    public RelayCommand RetryUndoCommand { get; }
    /// <summary>Ligado (o usuário ligou e confirmou).</summary>
    public bool Enabled => _state.Enabled;
    public string ToggleText => Enabled ? Loc.T("GameAuto.TurnOff") : Loc.T("GameAuto.TurnOn");
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>"O que ele faz": as ações do conjunto com caixa de marcar.</summary>
    public IReadOnlyList<GameModeActionRow> Items { get; }
    public IReadOnlyList<DelayOption> StopDelayOptions { get; }
    public DelayOption StopDelay
    {
        get => _stopDelay;
        set
        {
            if (value is null || !Set(ref _stopDelay, value)) return;
            _machine.StopDelay = TimeSpan.FromSeconds(value.Seconds);
            Save(_state with { StopDelaySeconds = value.Seconds });
            UpdateStatus();
        }
    }
    /// <summary>Aplicado agora, com o jogo aberto: a tela mostra "Ativo agora: &lt;jogo&gt;".</summary>
    public bool ActiveNow => _phase == GameModeAutoPhase.Active;
    public string ActiveText => _phase == GameModeAutoPhase.Active ? Loc.F("GameAuto.ActiveNow", _machine.ActiveGame ?? _gameName ?? "") : "";
    public bool UndoFailed => _phase == GameModeAutoPhase.UndoFailed;
    public IReadOnlyList<string> Warnings { get => _warnings; private set { if (Set(ref _warnings, value)) OnPropertyChanged(nameof(HasWarnings)); } }
    public bool HasWarnings => _warnings.Count > 0;
    /// <summary>Nenhuma ação marcada: ligar não faz nada (a tela avisa).</summary>
    public bool NothingChosen => Items.All(i => !i.IsOn || !i.Available);

    /// <summary>Aviso discreto para a bandeja (título, texto): ligou, aplicou, desfez, não conseguiu desfazer.</summary>
    public event EventHandler<(string Title, string Text)>? Notice;

    /// <summary>Linha para a tela Problemas quando o desfazer automático desistiu (null = nada a mostrar).</summary>
    public event EventHandler<ProblemRow?>? ProblemChanged;

    /// <summary>A janela já está montada: retoma (ligado, ou com execuções para desfazer).</summary>
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_state.Enabled || _state.HasPending)
        {
            _measure()?.StartWatching();
            _timer.Start();
        }
    }

    private GameModeActionRow Row(GameModeAction action)
    {
        var available = !action.Admin || _elevated();
        var chosen = GameModeActions.Normalize(_state.Actions);
        return new GameModeActionRow(action, ActionText(action.Key), chosen.Contains(action.Key, StringComparer.Ordinal), available,
            available ? "" : Loc.T("GameAuto.NeedsAdmin"), OnRowChanged);
    }

    /// <summary>Palavras simples de cada ação da lista fechada (nunca a chave do catálogo).</summary>
    internal static string ActionText(string key) => key switch
    {
        GameModeActions.PowerPlan => Loc.T("GameAuto.Act.PowerPlan"),
        GameModeActions.Notifications => Loc.T("GameAuto.Act.Notifications"),
        GameModeActions.BackgroundCapture => Loc.T("GameAuto.Act.Capture"),
        GameModeActions.DeliveryCap => Loc.T("GameAuto.Act.DeliveryCap"),
        _ => key,
    };

    private void OnRowChanged(GameModeActionRow row)
    {
        Save(_state with { Actions = Items.Where(i => i.IsOn).Select(i => i.Action.Key).ToList() });
        OnPropertyChanged(nameof(NothingChosen));
        UpdateStatus();
    }

    /// <summary>Chaves escolhidas e disponíveis agora (a escolha é lembrada mesmo quando a ação de administrador não está disponível).</summary>
    private IReadOnlyList<string> ChosenKeys() => [.. Items.Where(i => i.IsOn && i.Available).Select(i => i.Action.Key)];

    private void Toggle()
    {
        var host = _host();
        if (host is null) return;
        if (!Enabled)
        {
            var what = string.Join("\n", ChosenKeys().Select(k => "• " + ActionText(k)));
            if (what.Length == 0) what = Loc.T("GameAuto.ConfirmNothing");
            if (!host.Confirm(Loc.T("GameAuto.Title"), Loc.F("GameAuto.ConfirmOn", what, _stopDelay.Seconds))) return;
            Save(_state with { Enabled = true });
            _measure()?.StartWatching();
            _timer.Start();
            Notice?.Invoke(this, (Loc.T("GameAuto.Title"), Loc.T("GameAuto.Tray.On")));
        }
        else
        {
            Save(_state with { Enabled = false });
            Notice?.Invoke(this, (Loc.T("GameAuto.Title"), Loc.T("GameAuto.Tray.Off")));
            _ = TickAsync(); // desfaz agora se estiver aplicado
        }
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(ToggleText));
        UpdateStatus();
    }

    private async Task TickAsync()
    {
        if (_working) return;
        var host = _host();
        if (host is null) return;
        _working = true;
        try
        {
            var game = _measure()?.CurrentGame;
            _gameName = game?.Name;
            switch (_machine.Next(_state.Enabled, game?.Name, DateTimeOffset.Now))
            {
                case GameModeAutoStep.Apply:
                    await ApplyAsync(host, game!.Name);
                    break;
                case GameModeAutoStep.Restore:
                    await RestoreAsync(host);
                    break;
            }
            if (!_state.Enabled && !_state.HasPending) _timer.Stop();
            if (game is null && _warnings.Count > 0) Warnings = [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("GameAuto.Error", ex.Message); }
        finally
        {
            _working = false;
            UpdateStatus();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ApplyAsync(IModuleHost host, string game)
    {
        var plan = GameModeActions.Plan(ChosenKeys(), _elevated());
        if (plan.IsEmpty)
        {
            _machine.Applying(false, game); // nada marcado: não há o que aplicar nesta partida
            return;
        }
        _ = CheckBackgroundAsync();
        var runs = new List<PendingGameRun>();
        var anyOk = false;
        var allOk = true;
        foreach (var (keys, level) in new[] { (plan.User, BrokerLevel.User), (plan.Admin, BrokerLevel.Admin) })
        {
            if (keys.Count == 0) continue;
            var (ok, applied) = await ApplyLevelAsync(host, keys, level);
            runs.AddRange(applied);
            anyOk |= ok;
            allOk &= ok;
        }
        _machine.Applying(runs.Count > 0 || anyOk, game);
        if (runs.Count > 0) Save(_state with { PendingRuns = runs, PendingGame = game, PendingRun = null });
        if (runs.Count > 0 || anyOk) Notice?.Invoke(this, (Loc.T("GameAuto.Title"), Loc.F("GameAuto.Tray.Applied", game)));
        else Status = Loc.T("GameAuto.NotApplied");
        if (!allOk && runs.Count > 0) Notice?.Invoke(this, (Loc.T("GameAuto.Title"), Loc.T("GameAuto.Tray.Partial")));
    }

    /// <summary>
    /// Um pedido por nível. Já há outro ajuste de sessão aberto (Laboratório A/B, Ajustes rápidos)? O aplicador recusa o pedido
    /// inteiro ("session-active"): repete sem o plano de energia, para o resto do conjunto valer.
    /// </summary>
    private async Task<(bool Ok, IReadOnlyList<PendingGameRun> Runs)> ApplyLevelAsync(IModuleHost host, IReadOnlyList<string> keys, BrokerLevel level)
    {
        var effective = LocalBroker.EffectiveLevel(level, _elevated());
        var r = await host.ApplyConfirmedAsync(Loc.T("GameAuto.Title"), [.. keys.Select(k => new ActionRequest(k))], level, null);
        if (r is { Ok: false, Response.Error.Code: "session-active" } && keys.Contains(GameModeActions.PowerPlan, StringComparer.Ordinal))
        {
            var rest = keys.Where(k => k != GameModeActions.PowerPlan).ToList();
            if (rest.Count == 0) return (false, []);
            r = await host.ApplyConfirmedAsync(Loc.T("GameAuto.Title"), [.. rest.Select(k => new ActionRequest(k))], level, null);
        }
        if (r is null || !r.Ok) return (false, []);
        var results = r.Response?.Results ?? [];
        var ok = results.All(x => x.Outcome is "Verified" or "AlreadyConfigured" or "NotApplicable");
        // Só execuções que mudaram algo precisam ser desfeitas; "já estava assim" não entra no diário como mudança.
        var runs = results.Any(x => x.Outcome == "Verified") ? r.AllRunIds.Select(id => new PendingGameRun(id, effective)).ToList() : [];
        return (ok, runs);
    }

    private async Task RestoreAsync(IModuleHost host)
    {
        var pending = _state.AllPendingRuns;
        if (pending.Count == 0)
        {
            _machine.Restored(true, DateTimeOffset.Now); // já estava assim antes do jogo: nada a desfazer
            return;
        }
        var remaining = new List<PendingGameRun>();
        foreach (var run in pending.Reverse())
        {
            var r = await host.RevertWithoutConfirmationAsync(run.RunId, run.Level);
            if (r is not { Ok: true } || r.Response?.FullyRestored == false) remaining.Add(run);
        }
        var ok = remaining.Count == 0;
        _machine.Restored(ok, DateTimeOffset.Now);
        Save(ok ? _state.Settled() : _state with { PendingRuns = remaining, PendingRun = null });
        if (ok) Notice?.Invoke(this, (Loc.T("GameAuto.Title"), Loc.T("GameAuto.Tray.Restored")));
        else if (_machine.RestoreFailures >= GameModeAutoMachine.MaxRestoreFailures) Notice?.Invoke(this, (Loc.T("GameAuto.Title"), Loc.T("GameAuto.Tray.UndoFailed")));
    }

    private async Task CheckBackgroundAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var busy = await Task.Run(() => WindowsBackgroundIo.Measure(TimeSpan.FromSeconds(2)));
            Warnings = [.. busy.Select(b => Loc.F("GameAuto.Busy", AppName(b.App), b.MegabytesPerSecond.ToString("0.0", Loc.Culture)))];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Warnings = []; }
    }

    internal static string AppName(BackgroundApp app) => app switch
    {
        BackgroundApp.Steam => "Steam",
        BackgroundApp.Epic => "Epic Games",
        BackgroundApp.OneDrive => "OneDrive",
        BackgroundApp.WindowsBackup => Loc.T("GameAuto.WindowsBackup"),
        BackgroundApp.BattleNet => "Battle.net",
        BackgroundApp.EaApp => "EA app",
        BackgroundApp.Ubisoft => "Ubisoft Connect",
        _ => "GOG Galaxy",
    };

    private void UpdateStatus()
    {
        if (_working) return;
        var before = _phase;
        _phase = _machine.Phase;
        Status = _phase switch
        {
            GameModeAutoPhase.Active => Loc.F("GameAuto.Active", _machine.ActiveGame ?? _gameName ?? ""),
            GameModeAutoPhase.Closing => Loc.F("GameAuto.Closing", _machine.SecondsUntilRestore),
            GameModeAutoPhase.Restoring => Loc.T("GameAuto.Restoring"),
            GameModeAutoPhase.UndoFailed => Loc.T("GameAuto.UndoFailed"),
            GameModeAutoPhase.Waiting => NothingChosen ? Loc.T("GameAuto.WaitingNothing") : Loc.T("GameAuto.Waiting"),
            _ => _state.HasPending ? Loc.T("GameAuto.PendingUndo") : Loc.T("GameAuto.Off"),
        };
        foreach (var n in new[] { nameof(ActiveNow), nameof(ActiveText), nameof(UndoFailed) }) OnPropertyChanged(n);
        if (before != _phase && (before == GameModeAutoPhase.UndoFailed || _phase == GameModeAutoPhase.UndoFailed))
            ProblemChanged?.Invoke(this, _phase == GameModeAutoPhase.UndoFailed
                ? new ProblemRow(ProblemKey, Loc.T("GameAuto.Problem.Title"), Loc.T("GameAuto.Problem.WhatToDo"), "Icon.Games", 40, "SimpleHistoryPage", Loc.T("GameAuto.Problem.Action"))
                : null);
    }

    private void Save(GameSessionState state)
    {
        _state = state;
        _store.Save(state);
    }
}
