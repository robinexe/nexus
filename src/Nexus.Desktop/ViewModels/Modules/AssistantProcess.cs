using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using Nexus.Assistant;
using Nexus.Desktop.Views.Controls;
using Nexus.Desktop.Views.Shell;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Uma linha do feed da tela de progresso (tarefa ou, recuada, um resultado dentro dela).</summary>
public sealed class ProcessFeedRow(string key) : ObservableObject
{
    private string _title = "";
    private string? _detail;
    private ProcessTaskStatus _status;
    private bool _isChild;
    private bool _isEstimate;

    public string Key { get; } = key;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string? Detail { get => _detail; set { if (Set(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); } }
    public bool HasDetail => !string.IsNullOrWhiteSpace(_detail);
    public bool IsChild { get => _isChild; set => Set(ref _isChild, value); }
    /// <summary>O texto de detalhe é estimativa pelo tempo típico (o aplicador não informa item a item).</summary>
    public bool IsEstimate { get => _isEstimate; set => Set(ref _isEstimate, value); }

    public ProcessTaskStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            foreach (var n in new[] { nameof(IconKey), nameof(Tone), nameof(IsRunning), nameof(IsPending), nameof(StatusText) }) OnPropertyChanged(n);
        }
    }

    public bool IsRunning => _status == ProcessTaskStatus.Running;
    public bool IsPending => _status == ProcessTaskStatus.Pending;
    /// <summary>Âmbar = rodando, verde = feito, vermelho = não aplicado, ciano = não precisou/pulado, neutro = na fila.</summary>
    public Tone Tone => AssistantProcess.ToneOf(_status);
    public string IconKey => AssistantProcess.IconOf(_status);
    /// <summary>Para o leitor de tela (o ícone não carrega o significado sozinho).</summary>
    public string StatusText => _status switch
    {
        ProcessTaskStatus.Running => "em andamento",
        ProcessTaskStatus.Done => "feito",
        ProcessTaskStatus.Skipped => "não precisou",
        ProcessTaskStatus.Failed => "não aplicado",
        _ => "na fila",
    };
}

/// <summary>Um segmento da barra das 13 etapas na tela de progresso.</summary>
public sealed record ProcessSegment(int Number, string Name, string State)
{
    public bool IsDone => State == "done";
    public bool IsCurrent => State == "current";
    public bool IsSkipped => State == "skipped";
    public string Tip => $"Etapa {Number}: {Name}" + State switch { "current" => " (agora)", "done" => " (feita)", "skipped" => " (pulada)", _ => "" };
}

/// <summary>
/// Tela de progresso do Assistente (estilo painel de telemetria): anel com %, tempo restante suavizado, decorrido, o que está
/// acontecendo agora, o feed de tarefas com estado e a barra das 13 etapas. O andamento vem do <see cref="ProcessTracker"/>
/// (marcos reais + tempo típico dentro de cada parte, rotulado como estimativa). Um cronômetro de 200 ms lê o tracker
/// enquanto a tela está visível; não há animação em loop aqui (os laços visuais respeitam as animações do Windows).
/// </summary>
public sealed class AssistantProcess : ObservableObject
{
    private static readonly TimeSpan HoldApply = TimeSpan.FromMilliseconds(1700);
    private static readonly TimeSpan HoldScan = TimeSpan.FromMilliseconds(450);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private ProcessTracker? _tracker;
    private ApplyProgressObserver? _observer;
    private int _seenVersion = -1;
    private bool _isVisible, _isRunning, _isFinished, _waiting, _currentIsEstimate, _canCancel, _isApply;
    private double _percent;
    private string _title = "", _mode = "", _position = "", _remaining = "calculando…", _elapsed = "0:00", _current = "", _summary = "", _iconKey = "Icon.Assistant";
    private Tone _ringTone = Tone.Warn;
    private IReadOnlyList<ProcessSegment> _segments = [];
    private TimeSpan _offset;

    public AssistantProcess()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>A tela de progresso está na frente (o resto da página some).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set { if (Set(ref _isVisible, value)) OnPropertyChanged(nameof(IsHidden)); }
    }

    public bool IsHidden => !_isVisible;
    /// <summary>Trabalhando (anel girando, pulso "ao vivo"). Falso na tela de conclusão.</summary>
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool IsFinished { get => _isFinished; private set => Set(ref _isFinished, value); }
    /// <summary>Parado esperando o usuário (confirmação ou aviso do Windows): o tempo restante não conta.</summary>
    public bool IsWaiting { get => _waiting; private set => Set(ref _waiting, value); }
    public bool IsApply { get => _isApply; private set => Set(ref _isApply, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    /// <summary>"Aplicando", "Verificando", "Montando o relatório".</summary>
    public string Mode { get => _mode; private set => Set(ref _mode, value); }
    /// <summary>"etapa 3 de 13".</summary>
    public string Position { get => _position; private set => Set(ref _position, value); }
    public string IconKey { get => _iconKey; private set => Set(ref _iconKey, value); }
    /// <summary>0–100, só sobe.</summary>
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string RemainingText { get => _remaining; private set => Set(ref _remaining, value); }
    public string ElapsedText { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string CurrentText { get => _current; private set => Set(ref _current, value); }
    public bool CurrentIsEstimate { get => _currentIsEstimate; private set => Set(ref _currentIsEstimate, value); }
    public string SummaryText { get => _summary; private set => Set(ref _summary, value); }
    public Tone RingTone { get => _ringTone; private set => Set(ref _ringTone, value); }
    public IReadOnlyList<ProcessSegment> Segments { get => _segments; private set => Set(ref _segments, value); }
    public ObservableCollection<ProcessFeedRow> Rows { get; } = [];
    /// <summary>Só na leitura (o pedido ao aplicador não pode ser interrompido no meio com segurança).</summary>
    public bool CanCancel { get => _canCancel; private set => Set(ref _canCancel, value); }
    public static bool Motion => ShellMotion.Enabled;
    public static string CancelNote => "Não dá para interromper no meio: o aplicador termina, confere cada item e grava tudo no diário (dá para desfazer depois).";

    public ProcessTracker? Tracker => _tracker;
    public ApplyProgressObserver? Observer => _observer;

    public static Tone ToneOf(ProcessTaskStatus s) => s switch
    {
        ProcessTaskStatus.Running => Tone.Warn,
        ProcessTaskStatus.Done => Tone.Good,
        ProcessTaskStatus.Failed => Tone.Bad,
        ProcessTaskStatus.Skipped => Tone.Info,
        _ => Tone.Neutral,
    };

    public static string IconOf(ProcessTaskStatus s) => s switch
    {
        ProcessTaskStatus.Running => "Icon.Refresh",
        ProcessTaskStatus.Done => "Icon.Check",
        ProcessTaskStatus.Failed => "Icon.Error",
        ProcessTaskStatus.Skipped => "Icon.ChevronRight",
        _ => "Icon.Plus",
    };

    /// <summary>Começa uma tela de progresso nova (leitura ou aplicação) para a etapa.</summary>
    public ProcessTracker Begin(AssistantStepId step, string mode, bool apply, IReadOnlyList<ProcessSegment> segments, bool canCancel = false, TimeSpan offset = default)
    {
        _offset = offset;
        _tracker = new ProcessTracker(AssistantSteps.Info(step).Title, Now);
        _observer = apply ? new ApplyProgressObserver(_tracker, step) : null;
        _seenVersion = -1;
        Rows.Clear();
        Title = AssistantSteps.Info(step).Title;
        Mode = mode;
        IsApply = apply;
        Position = ProcessText.Position(step);
        IconKey = AssistantVisuals.IconFor(step);
        Segments = segments;
        CanCancel = canCancel;
        SummaryText = "";
        Percent = 0;
        RemainingText = ProcessText.Remaining(null);
        ElapsedText = ProcessText.Clock(TimeSpan.Zero);
        CurrentText = "Preparando…";
        CurrentIsEstimate = false;
        RingTone = apply ? Tone.Warn : Tone.Info;
        IsFinished = false;
        IsRunning = true;
        IsVisible = true;
        _timer.Start();
        return _tracker;
    }

    private TimeSpan Now() => _clock.Elapsed + _offset;

    /// <summary>Só para a demonstração do autoteste: adianta o relógio desta tela (simula tempo passado).</summary>
    internal void AdvanceClock(TimeSpan by) => _offset += by;

    /// <summary>Termina: anel cheio, resumo; depois de um instante a tela some e a página mostra o resultado.</summary>
    public async Task FinishAsync(ProcessSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (_tracker is null) { IsVisible = false; return; }
        _tracker.Complete();
        Tick();
        _timer.Stop();
        CanCancel = false;
        IsRunning = false;
        IsWaiting = false;
        IsFinished = true;
        SummaryText = summary.Text;
        RingTone = summary.Status switch { ProcessTaskStatus.Failed => Tone.Bad, ProcessTaskStatus.Skipped => Tone.Info, _ => Tone.Good };
        CurrentText = summary.Status switch
        {
            ProcessTaskStatus.Failed => "Não deu certo",
            ProcessTaskStatus.Skipped => IsApply ? "Nada foi aplicado" : "Concluído",
            _ => IsApply ? "Etapa aplicada" : "Leitura concluída",
        };
        CurrentIsEstimate = false;
        var mine = _tracker;
        await Task.Delay(IsApply ? HoldApply : HoldScan);
        if (!ReferenceEquals(mine, _tracker)) return; // outra etapa já tomou a tela durante a pausa
        IsFinished = false;
        IsVisible = false;
    }

    /// <summary>Some na hora (cancelado, erro ou saiu do assistente).</summary>
    public void Close()
    {
        _timer.Stop();
        IsRunning = false;
        IsFinished = false;
        IsWaiting = false;
        IsVisible = false;
    }

    /// <summary>Lê o tracker e atualiza a tela (o cronômetro chama a cada 200 ms; dá para chamar a qualquer hora).</summary>
    public void Tick()
    {
        if (_tracker is not { } t) return;
        _observer?.Tick();
        t.Sample();
        Percent = Math.Round(t.Progress * 100, 1);
        IsWaiting = t.IsWaiting;
        RemainingText = t.IsComplete ? "pronto" : t.IsWaiting ? "esperando você" : ProcessText.Remaining(t.Remaining);
        ElapsedText = ProcessText.Clock(t.Elapsed);
        if (!t.IsComplete)
        {
            CurrentText = t.CurrentText;
            CurrentIsEstimate = t.Current?.TextIsEstimate == true;
        }
        if (t.Version != _seenVersion)
        {
            _seenVersion = t.Version;
            SyncRows(t);
        }
    }

    /// <summary>Atualiza as linhas no lugar (sem recriar a lista: nada pisca nem reanima).</summary>
    private void SyncRows(ProcessTracker t)
    {
        var wanted = new List<(string Key, string Title, ProcessTaskStatus Status, string? Detail, bool Child, bool Estimate)>();
        foreach (var task in t.Tasks)
        {
            var detail = task.Status == ProcessTaskStatus.Running ? task.RunningText : task.Detail;
            wanted.Add((task.Key, task.Title, task.Status, detail == task.Title ? null : detail, false, task.Status == ProcessTaskStatus.Running && task.TextIsEstimate));
            var i = 0;
            foreach (var item in task.Items.Take(12))
                wanted.Add(($"{task.Key}/{i++}", item.Title, item.Status, item.Detail, true, false));
            if (task.Items.Count > 12)
                wanted.Add(($"{task.Key}/more", $"e mais {task.Items.Count - 12}", task.Items.Skip(12).All(x => x.Status == ProcessTaskStatus.Done) ? ProcessTaskStatus.Done : ProcessTaskStatus.Pending, null, true, false));
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            var existing = i < Rows.Count && Rows[i].Key == w.Key ? Rows[i] : Rows.FirstOrDefault(r => r.Key == w.Key);
            if (existing is null)
            {
                existing = new ProcessFeedRow(w.Key);
                Rows.Insert(Math.Min(i, Rows.Count), existing);
            }
            else if (Rows.IndexOf(existing) != i) Rows.Move(Rows.IndexOf(existing), i);
            existing.Title = w.Title;
            existing.Status = w.Status;
            existing.Detail = w.Detail;
            existing.IsChild = w.Child;
            existing.IsEstimate = w.Estimate;
        }
        while (Rows.Count > wanted.Count) Rows.RemoveAt(Rows.Count - 1);
    }
}
