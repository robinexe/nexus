using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using Nexus.Assistant;
using Nexus.Broker;
using Nexus.Desktop.Views.Controls;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Tela de progresso da janela para QUALQUER aplicar/desfazer pedido por uma página (IModuleHost.ApplyAsync/RevertAsync e a
/// página Aplicar). Mesmo visual e mesma lógica da tela do Assistente (ProcessRing, ProcessTracker, EtaEstimator, feed), por
/// cima do conteúdo. O Assistente tem a sua própria tela embutida e não usa esta (não aparece duas vezes).
/// No fim fica o resumo com "Ver resultado" e "Fechar".
/// </summary>
public sealed class ApplyOverlay : ObservableObject
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private HostApplyProgress? _progress;
    private int _seenVersion = -1;
    private bool _isVisible, _isRunning, _isFinished, _waiting, _currentIsEstimate;
    private double _percent;
    private string _title = "", _mode = "", _remaining = Loc.T("Shell.Calculando"), _elapsed = "0:00", _current = "", _summary = "", _levelText = "";
    private Tone _ringTone = Tone.Warn;
    private TimeSpan _offset;

    public ApplyOverlay(Action showResult)
    {
        ArgumentNullException.ThrowIfNull(showResult);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        CloseCommand = new RelayCommand(Close, () => _isFinished);
        ShowResultCommand = new RelayCommand(() => { Close(); showResult(); }, () => _isFinished);
    }

    public bool IsVisible { get => _isVisible; private set => Set(ref _isVisible, value); }
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool IsFinished { get => _isFinished; private set => Set(ref _isFinished, value); }
    public bool IsWaiting { get => _waiting; private set => Set(ref _waiting, value); }
    /// <summary>Título da operação (o mesmo da confirmação).</summary>
    public string Title { get => _title; private set => Set(ref _title, value); }
    /// <summary>"Aplicando" ou "Desfazendo".</summary>
    public string Mode { get => _mode; private set => Set(ref _mode, value); }
    /// <summary>"na sua conta" ou "de sistema (administrador)".</summary>
    public string LevelText { get => _levelText; private set => Set(ref _levelText, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string RemainingText { get => _remaining; private set => Set(ref _remaining, value); }
    public string ElapsedText { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string CurrentText { get => _current; private set => Set(ref _current, value); }
    public bool CurrentIsEstimate { get => _currentIsEstimate; private set => Set(ref _currentIsEstimate, value); }
    public string SummaryText { get => _summary; private set => Set(ref _summary, value); }
    public Tone RingTone { get => _ringTone; private set => Set(ref _ringTone, value); }
    public ObservableCollection<ProcessFeedRow> Rows { get; } = [];
    public RelayCommand CloseCommand { get; }
    public RelayCommand ShowResultCommand { get; }

    public HostApplyProgress? Progress => _progress;

    private TimeSpan Now() => _clock.Elapsed + _offset;

    /// <summary>Abre a tela para um pedido (antes da confirmação: ela mostra "aguardando a sua confirmação").</summary>
    public HostApplyProgress Begin(string title, IReadOnlyList<(string Key, string Name)> actions, BrokerLevel level, bool revert = false, Func<string, string>? name = null)
    {
        _offset = TimeSpan.Zero;
        var tracker = new ProcessTracker(title, Now);
        _progress = new HostApplyProgress(tracker, actions, level, revert, name);
        _seenVersion = -1;
        Rows.Clear();
        Title = string.IsNullOrWhiteSpace(title) ? (revert ? Loc.T("Shell.Desfazer") : Loc.T("Shell.Aplicar")) : title;
        Mode = revert ? Loc.T("Shell.Desfazendo") : Loc.T("Shell.Aplicando");
        LevelText = level == BrokerLevel.Admin ? Loc.T("Shell.MudancasSistemaAdministrador") : Loc.T("Shell.SuaConta");
        SummaryText = "";
        Percent = 0;
        RingTone = Tone.Warn;
        IsFinished = false;
        IsRunning = true;
        IsVisible = true;
        _timer.Start();
        Tick();
        return _progress;
    }

    /// <summary>Terminou: anel cheio, resumo e os botões. A tela fica até o usuário fechar.</summary>
    public void Finish()
    {
        if (_progress is not { } p) return;
        p.Done();
        Tick();
        _timer.Stop();
        var summary = p.Summary();
        IsRunning = false;
        IsWaiting = false;
        IsFinished = true;
        SummaryText = summary.Text;
        RingTone = summary.Status switch { ProcessTaskStatus.Failed => Tone.Bad, ProcessTaskStatus.Skipped => Tone.Info, _ => Tone.Good };
        CurrentText = summary.Status switch
        {
            ProcessTaskStatus.Failed => p.IsRevert ? Loc.T("Shell.DesfazerNaoDeuCerto") : Loc.T("Shell.NaoDeuCerto"),
            ProcessTaskStatus.Skipped => Loc.T("Shell.NadaFoiAlterado"),
            _ => p.IsRevert ? Loc.T("Shell.Desfeito") : Loc.T("Shell.AplicadoConferido"),
        };
        CurrentIsEstimate = false;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Some (cancelado na confirmação, erro, ou o usuário fechou).</summary>
    public void Close()
    {
        _timer.Stop();
        IsRunning = false;
        IsFinished = false;
        IsWaiting = false;
        IsVisible = false;
    }

    /// <summary>Só para a demonstração do autoteste: adianta o relógio desta tela.</summary>
    internal void AdvanceClock(TimeSpan by) => _offset += by;

    public void Tick()
    {
        if (_progress is not { } p) return;
        var t = p.Tracker;
        p.Tick();
        t.Sample();
        Percent = Math.Round(t.Progress * 100, 1);
        IsWaiting = t.IsWaiting;
        RemainingText = t.IsComplete ? Loc.T("Pg.Pronto") : t.IsWaiting ? Loc.T("Shell.EsperandoVoce") : ProcessText.Remaining(t.Remaining);
        ElapsedText = ProcessText.Clock(t.Elapsed);
        if (!t.IsComplete)
        {
            CurrentText = t.CurrentText;
            CurrentIsEstimate = t.Current?.TextIsEstimate == true;
        }
        if (t.Version != _seenVersion)
        {
            _seenVersion = t.Version;
            ProcessFeed.Sync(Rows, t);
        }
    }
}
