using System.Windows.Threading;
using Nexus.Broker;
using Nexus.Measurement;
using Nexus.Measurement.Windows;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>
/// Sessão de jogo (tela Jogos, desligada por padrão): com a opção ligada e um jogo aberto (detecção do Medir), aplica pelo
/// aplicador o limite temporário dos downloads da Microsoft (<see cref="CapAction"/>) e desfaz sozinho quando o jogo fecha. Avisa
/// se Steam/Epic/OneDrive/Backup estão baixando ou sincronizando agora. O ganho é pequeno e a tela diz isso. A execução aplicada
/// fica anotada em %LocalAppData%\NEXUS\game-session.json: se o NEXUS fechar com o jogo aberto, ele desfaz na próxima abertura.
/// </summary>
public sealed class GameSessionPanel : ObservableObject
{
    public const string CapAction = "game.session.do-foreground-cap@1";

    private readonly Func<IModuleHost?> _host;
    private readonly Func<MeasurePage?> _measure;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly GameSessionCap _cap = new();
    private readonly GameSessionStore _store;
    private GameSessionState _state = GameSessionState.Off;
    private bool _working;
    private string _status = "";
    private IReadOnlyList<string> _warnings = [];
    private string? _gameName;

    public GameSessionPanel(Func<IModuleHost?> host, Func<MeasurePage?> measure, string? statePath = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _measure = measure ?? throw new ArgumentNullException(nameof(measure));
        _store = new GameSessionStore(statePath ?? GameSessionStore.DefaultPath);
        _timer.Tick += async (_, _) => await TickAsync();
        ToggleCommand = new RelayCommand(Toggle, () => !_working);
        _state = _store.Load();
        if (_state.PendingRun is not null) _cap.Resume();
        UpdateStatus();
    }

    public RelayCommand ToggleCommand { get; }
    /// <summary>Ligada (o usuário ligou e confirmou).</summary>
    public bool Enabled => _state.Enabled;
    public string ToggleText => Enabled ? Loc.T("Session.TurnOff") : Loc.T("Session.TurnOn");
    public string Status { get => _status; private set => Set(ref _status, value); }
    public IReadOnlyList<string> Warnings { get => _warnings; private set { if (Set(ref _warnings, value)) OnPropertyChanged(nameof(HasWarnings)); } }
    public bool HasWarnings => _warnings.Count > 0;

    /// <summary>A janela já está montada: retoma (ligada, ou com uma sessão para desfazer).</summary>
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_state.Enabled || _state.PendingRun is not null)
        {
            _measure()?.StartWatching();
            _timer.Start();
        }
    }

    private void Toggle()
    {
        var host = _host();
        if (host is null) return;
        if (!Enabled)
        {
            if (!host.Confirm(Loc.T("Session.Title"), Loc.T("Session.ConfirmOn"))) return;
            Save(_state with { Enabled = true });
            _measure()?.StartWatching();
            _timer.Start();
        }
        else
        {
            Save(_state with { Enabled = false });
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
            switch (_cap.Next(_state.Enabled, game is not null, DateTimeOffset.Now))
            {
                case GameSessionCommand.Apply:
                    await ApplyAsync(host);
                    break;
                case GameSessionCommand.Restore:
                    await RestoreAsync(host);
                    break;
            }
            if (!_state.Enabled && _state.PendingRun is null) _timer.Stop();
            if (game is null && _warnings.Count > 0) Warnings = [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("Session.Error", ex.Message); }
        finally
        {
            _working = false;
            UpdateStatus();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ApplyAsync(IModuleHost host)
    {
        _ = CheckBackgroundAsync();
        var r = await host.ApplyConfirmedAsync(Loc.T("Session.Title"), [new ActionRequest(CapAction)], BrokerLevel.Admin, null);
        var outcome = r?.Response?.Results is { Count: > 0 } results ? results[0].Outcome : null;
        var ok = r is { Ok: true } && outcome is "Verified" or "AlreadyConfigured";
        _cap.Applying(ok);
        if (ok && outcome == "Verified" && r!.Response?.RunId is { } run) Save(_state with { PendingRun = run });
        if (!ok) Status = Loc.T("Session.NotApplied");
    }

    private async Task RestoreAsync(IModuleHost host)
    {
        if (_state.PendingRun is not { } run)
        {
            _cap.Restored(true); // já estava assim antes do jogo: nada a desfazer
            return;
        }
        var r = await host.RevertWithoutConfirmationAsync(run, BrokerLevel.Admin);
        var ok = r is { Ok: true };
        _cap.Restored(ok);
        if (ok) Save(_state with { PendingRun = null });
    }

    private async Task CheckBackgroundAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var busy = await Task.Run(() => WindowsBackgroundIo.Measure(TimeSpan.FromSeconds(2)));
            Warnings = [.. busy.Select(b => Loc.F("Session.Busy", AppName(b.App), b.MegabytesPerSecond.ToString("0.0", Loc.Culture)))];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Warnings = []; }
    }

    internal static string AppName(BackgroundApp app) => app switch
    {
        BackgroundApp.Steam => "Steam",
        BackgroundApp.Epic => "Epic Games",
        BackgroundApp.OneDrive => "OneDrive",
        BackgroundApp.WindowsBackup => Loc.T("Session.WindowsBackup"),
        BackgroundApp.BattleNet => "Battle.net",
        BackgroundApp.EaApp => "EA app",
        BackgroundApp.Ubisoft => "Ubisoft Connect",
        _ => "GOG Galaxy",
    };

    private void UpdateStatus()
    {
        if (_working) return;
        Status = (_state.Enabled, _cap.Applied) switch
        {
            (_, true) when _gameName is not null => Loc.F("Session.Active", _gameName),
            (_, true) => Loc.T("Session.Restoring"),
            (true, false) => Loc.T("Session.Waiting"),
            _ => Loc.T("Session.Off"),
        };
    }

    private void Save(GameSessionState state)
    {
        _state = state;
        _store.Save(state);
    }
}
