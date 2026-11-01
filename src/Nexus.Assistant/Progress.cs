using System.Globalization;
using Nexus.Broker;

namespace Nexus.Assistant;

/// <summary>Estado de uma tarefa da tela de progresso.</summary>
public enum ProcessTaskStatus { Pending, Running, Done, Skipped, Failed }

/// <summary>Um resultado dentro de uma tarefa (ex.: cada mudança de um lote, com o que o aplicador respondeu).</summary>
public sealed record ProcessItem(string Title, ProcessTaskStatus Status, string? Detail = null);

/// <summary>
/// Uma linha do "feed" da tela de progresso. <see cref="ExpectedSeconds"/> é o peso dela na barra e a duração típica usada
/// para estimar o andamento enquanto ela roda (o aplicador só responde no fim de cada lote).
/// </summary>
public sealed class ProcessTask
{
    private readonly List<ProcessItem> _items = [];

    internal ProcessTask(string key, string title, double expectedSeconds)
    {
        Key = key;
        Title = title;
        ExpectedSeconds = Math.Max(0.3, expectedSeconds);
    }

    public string Key { get; }
    public string Title { get; internal set; }
    public double ExpectedSeconds { get; internal set; }
    public ProcessTaskStatus Status { get; internal set; }
    /// <summary>O que está acontecendo agora nesta tarefa ("Aguardando sua confirmação…").</summary>
    public string? RunningText { get; internal set; }
    /// <summary>O texto de agora é estimado pelo tempo típico (o aplicador não informa item a item).</summary>
    public bool TextIsEstimate { get; internal set; }
    /// <summary>Parada esperando o usuário (confirmação, aviso do Windows): o relógio da estimativa não anda.</summary>
    public bool Waiting { get; internal set; }
    /// <summary>Resultado curto depois de terminar ("7 feitas · 4 s").</summary>
    public string? Detail { get; internal set; }
    public TimeSpan Took { get; internal set; }
    public IReadOnlyList<ProcessItem> Items => _items;
    internal TimeSpan ActiveStart { get; set; }
    internal TimeSpan WallStart { get; set; }

    internal void AddItem(ProcessItem item) => _items.Add(item);
    internal void ClearItems() => _items.Clear();

    public bool IsFinished => Status is ProcessTaskStatus.Done or ProcessTaskStatus.Skipped or ProcessTaskStatus.Failed;
}

/// <summary>
/// Tempo restante a partir do tempo decorrido e do andamento (0–1), suavizado: entre leituras ele continua contando para
/// baixo e só se aproxima aos poucos do valor bruto — nunca fica negativo nem pula de 30 s para 3 min de uma vez.
/// Null ("calculando…") até haver andamento e tempo suficientes para uma estimativa honesta.
/// </summary>
public sealed class EtaEstimator(double minProgress = 0.04, double minSeconds = 2.0, double smoothing = 0.12)
{
    private double? _smoothed;
    private double _lastElapsed;

    public TimeSpan? Remaining => _smoothed is { } s ? TimeSpan.FromSeconds(s) : null;

    public TimeSpan? Update(TimeSpan elapsed, double progress)
    {
        var t = Math.Max(0, elapsed.TotalSeconds);
        if (!double.IsFinite(progress)) return Remaining;
        if (progress >= 1)
        {
            _smoothed = 0;
            _lastElapsed = t;
            return Remaining;
        }
        if (progress < minProgress || t < minSeconds)
        {
            _lastElapsed = t;
            return Remaining;
        }
        var raw = t * (1 - progress) / progress;
        if (_smoothed is not { } prev) _smoothed = raw;
        else
        {
            var predicted = Math.Max(0, prev - Math.Max(0, t - _lastElapsed));
            _smoothed = predicted + smoothing * (raw - predicted);
        }
        _smoothed = Math.Max(0, _smoothed.Value);
        _lastElapsed = t;
        return Remaining;
    }

    public void Reset()
    {
        _smoothed = null;
        _lastElapsed = 0;
    }
}

/// <summary>
/// O andamento de uma etapa (leitura ou aplicação) para a tela de progresso: lista de tarefas com peso, relógio que para
/// enquanto o usuário responde a uma pergunta, % que só sobe e tempo restante suavizado. Sem WPF; o relógio é injetado.
/// O % vem do que terminou de verdade; dentro da tarefa em andamento, do tempo típico dela (rotulado como estimativa).
/// </summary>
public sealed class ProcessTracker
{
    private readonly Func<TimeSpan> _now;
    private readonly List<ProcessTask> _tasks = [];
    private readonly EtaEstimator _eta = new();
    private readonly TimeSpan _start;
    private TimeSpan _paused;
    private TimeSpan? _pausedAt;
    private double _shown;
    private TimeSpan? _endedAt;

    /// <param name="now">Relógio monotônico (Stopwatch nos testes, Environment.TickCount64 na interface).</param>
    public ProcessTracker(string title, Func<TimeSpan> now)
    {
        Title = title ?? "";
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _start = now();
    }

    public string Title { get; set; }
    /// <summary>Muda quando a lista ou o estado de uma tarefa muda (a interface reconstrói as linhas).</summary>
    public int Version { get; private set; }
    public IReadOnlyList<ProcessTask> Tasks => _tasks;

    public ProcessTask Add(string key, string title, double expectedSeconds)
    {
        var t = new ProcessTask(key, title, expectedSeconds);
        _tasks.Add(t);
        Version++;
        return t;
    }

    public ProcessTask? Find(string key) => _tasks.FirstOrDefault(t => t.Key == key);

    /// <summary>Tira uma tarefa que deixou de existir (ex.: lote replanejado). O % mostrado não volta.</summary>
    public void Remove(ProcessTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_tasks.Remove(task)) Version++;
    }

    public ProcessTask? Current => _tasks.FirstOrDefault(t => t.Status == ProcessTaskStatus.Running);

    public bool IsWaiting => Current?.Waiting == true;

    public bool IsComplete => _endedAt is not null || (_tasks.Count > 0 && _tasks.All(t => t.IsFinished));

    /// <summary>Tempo de relógio desde o começo (para o usuário: "decorrido").</summary>
    public TimeSpan Elapsed => (_endedAt ?? _now()) - _start;

    /// <summary>Tempo trabalhando (sem as esperas pelo usuário): base da estimativa.</summary>
    public TimeSpan ActiveElapsed
    {
        get
        {
            var now = _endedAt ?? _now();
            var paused = _paused + (_pausedAt is { } p ? now - p : TimeSpan.Zero);
            return now - _start - paused;
        }
    }

    public void Start(ProcessTask task, string? text = null, bool estimate = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsFinished) return;
        task.Status = ProcessTaskStatus.Running;
        task.ActiveStart = ActiveElapsed;
        task.WallStart = Elapsed;
        task.RunningText = text ?? task.Title;
        task.TextIsEstimate = estimate;
        Version++;
    }

    public void SetText(ProcessTask task, string text, bool estimate = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.RunningText == text && task.TextIsEstimate == estimate) return;
        task.RunningText = text;
        task.TextIsEstimate = estimate;
        Version++;
    }

    public void SetExpected(ProcessTask task, double seconds)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.ExpectedSeconds = Math.Max(0.3, seconds);
        Version++;
    }

    /// <summary>A tarefa espera o usuário: o relógio da estimativa para (o decorrido continua).</summary>
    public void Wait(ProcessTask task, string text)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != ProcessTaskStatus.Running) Start(task);
        task.Waiting = true;
        task.RunningText = text;
        task.TextIsEstimate = false;
        _pausedAt ??= _now();
        Version++;
    }

    /// <summary>O usuário respondeu: o relógio volta a andar e a tarefa recomeça a contar daqui.</summary>
    public void Resume(ProcessTask task, string? text = null, bool estimate = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_pausedAt is { } p)
        {
            _paused += _now() - p;
            _pausedAt = null;
        }
        task.Waiting = false;
        task.ActiveStart = ActiveElapsed;
        if (text is not null) task.RunningText = text;
        task.TextIsEstimate = estimate;
        Version++;
    }

    public void Finish(ProcessTask task, ProcessTaskStatus status, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Waiting) Resume(task);
        if (task.Status == ProcessTaskStatus.Pending) task.WallStart = Elapsed;
        task.Status = status;
        task.Detail = detail;
        task.RunningText = null;
        task.TextIsEstimate = false;
        task.Took = Elapsed - task.WallStart;
        Version++;
    }

    /// <summary>Troca os resultados da tarefa (ex.: as mudanças do lote, pendentes → o que o aplicador respondeu).</summary>
    public void SetItems(ProcessTask task, IEnumerable<ProcessItem> items)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(items);
        task.ClearItems();
        foreach (var i in items) task.AddItem(i);
        Version++;
    }

    public void AddItem(ProcessTask task, ProcessItem item)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(item);
        task.AddItem(item);
        Version++;
    }

    /// <summary>Termina tudo o que ficou pendente (pulado) e congela o relógio.</summary>
    public void Complete()
    {
        foreach (var t in _tasks.Where(t => !t.IsFinished).ToList())
            Finish(t, t.Status == ProcessTaskStatus.Running ? ProcessTaskStatus.Done : ProcessTaskStatus.Skipped, t.Detail);
        if (_pausedAt is { } p)
        {
            _paused += _now() - p;
            _pausedAt = null;
        }
        _endedAt ??= _now();
        _shown = 1;
        _eta.Update(ActiveElapsed, 1);
        Version++;
    }

    /// <summary>Andamento bruto agora (0–1): terminadas contam inteiras; a em andamento, pelo tempo típico.</summary>
    public double RawProgress()
    {
        if (_tasks.Count == 0) return 0;
        if (IsComplete) return 1;
        double total = 0, done = 0;
        var active = ActiveElapsed;
        foreach (var t in _tasks)
        {
            total += t.ExpectedSeconds;
            if (t.IsFinished) done += t.ExpectedSeconds;
            else if (t.Status == ProcessTaskStatus.Running)
                done += t.ExpectedSeconds * ProcessText.TimeFraction((active - t.ActiveStart).TotalSeconds, t.ExpectedSeconds);
        }
        return total <= 0 ? 0 : Math.Clamp(done / total, 0, 1);
    }

    /// <summary>Lê o relógio: atualiza o % mostrado (só sobe) e a estimativa. Chame a cada quadro do cronômetro da tela.</summary>
    public void Sample()
    {
        var raw = RawProgress();
        _shown = Math.Max(_shown, raw);
        if (!IsWaiting) _eta.Update(ActiveElapsed, _shown);
    }

    /// <summary>% mostrado (0–1), que nunca volta.</summary>
    public double Progress => _shown;

    /// <summary>Tempo restante estimado; null = ainda calculando.</summary>
    public TimeSpan? Remaining => IsComplete ? TimeSpan.Zero : _eta.Remaining;

    public string CurrentText => Current is { } c ? c.RunningText ?? c.Title : IsComplete ? "Concluído" : "Preparando…";
}

/// <summary>Textos e contas da tela de progresso (pt-BR), puros e testados.</summary>
public static class ProcessText
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// Fração de uma tarefa que roda há <paramref name="elapsed"/> s e costuma levar <paramref name="expected"/> s: sobe em
    /// linha reta até 90% no tempo típico e depois se aproxima de 99% sem nunca chegar (só o fim de verdade completa).
    /// </summary>
    public static double TimeFraction(double elapsed, double expected)
    {
        if (!(expected > 0) || !(elapsed > 0)) return 0;
        if (elapsed <= expected) return 0.9 * elapsed / expected;
        return 0.9 + 0.09 * (1 - Math.Exp(-(elapsed - expected) / expected));
    }

    /// <summary>"calculando…", "quase lá", "faltam ~45 s", "faltam ~1 min 20 s", "faltam ~12 min".</summary>
    public static string Remaining(TimeSpan? remaining)
    {
        if (remaining is not { } r) return "calculando…";
        var s = Math.Max(0, r.TotalSeconds);
        if (s < 5) return "quase lá";
        if (s < 60) return string.Create(Pt, $"faltam ~{RoundTo(s, s < 20 ? 1 : 5):0} s");
        if (s < 600)
        {
            var rounded = RoundTo(s, 10);
            var min = (int)(rounded / 60);
            var sec = (int)(rounded % 60);
            return sec == 0 ? string.Create(Pt, $"faltam ~{min} min") : string.Create(Pt, $"faltam ~{min} min {sec:00} s");
        }
        return string.Create(Pt, $"faltam ~{Math.Round(s / 60):0} min");
    }

    /// <summary>Cronômetro: "0:07", "1:23", "1:02:03".</summary>
    public static string Clock(TimeSpan elapsed)
    {
        var t = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        return t.TotalHours >= 1
            ? string.Create(Pt, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(Pt, $"{(int)t.TotalMinutes}:{t.Seconds:00}");
    }

    /// <summary>Duração para o resumo: "menos de 1 s", "12 s", "1 min 05 s".</summary>
    public static string Duration(TimeSpan d)
    {
        var s = Math.Max(0, d.TotalSeconds);
        if (s < 1) return "menos de 1 s";
        if (s < 60) return string.Create(Pt, $"{Math.Round(s):0} s");
        var total = (int)Math.Round(s);
        return total % 60 == 0 ? string.Create(Pt, $"{total / 60} min") : string.Create(Pt, $"{total / 60} min {total % 60:00} s");
    }

    /// <summary>"etapa 3 de 13".</summary>
    public static string Position(AssistantStepId step) => string.Create(Pt, $"etapa {(int)step} de {AssistantSteps.Count}");

    private static double RoundTo(double value, double step) => Math.Round(value / step) * step;
}

/// <summary>
/// Durações típicas (em segundos) para a tela de progresso. São estimativas declaradas: servem para a barra andar entre os
/// marcos reais (o aplicador responde uma vez por lote) e a tela diz "estimativa" onde as usa.
/// </summary>
public static class ProcessTimeModel
{
    /// <summary>Ponto de restauração automático do aplicador antes de mudanças de sistema (costuma levar 20–60 s).</summary>
    public const double AdminRestorePoint = 30;
    public const double StartApplier = 2;
    public const double Refresh = 4;
    public const double Planning = 3;
    public const double RestorePointLookup = 4;

    /// <summary>Leitura de uma etapa. <paramref name="deep"/>: a variante pesada (procurar no Windows Update/winget).</summary>
    public static double ScanSeconds(AssistantStepId step, bool deep = false) => step switch
    {
        AssistantStepId.Welcome => 3,
        AssistantStepId.MyPc => 6,
        AssistantStepId.Cleaning => 12,
        AssistantStepId.Programs => deep ? 25 : 5,
        AssistantStepId.Startup => 4,
        AssistantStepId.Privacy => 3,
        AssistantStepId.Security => 6,
        AssistantStepId.Drivers => deep ? 40 : 4,
        AssistantStepId.Memory => 4,
        AssistantStepId.Performance => 3,
        AssistantStepId.Measurement => 1,
        AssistantStepId.Schedules => 2,
        AssistantStepId.Report => 15,
        _ => 4,
    };

    /// <summary>Uma mudança dentro do aplicador.</summary>
    public static double ChangeSeconds(ProposedChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var id = change.Request.ActionId;
        if (id == StepPlanners.RestorePointAction) return 6; // o automático do lote já pesa AdminRestorePoint
        if (id == StepPlanners.DriverInstallAction) return 45;
        if (id == StepPlanners.ScheduleScanAction || id == StepPlanners.ScheduleCleanAction) return 2;
        if (change.Step == AssistantStepId.Cleaning)
        {
            var gb = BytesOf(change.Before) / (1024d * 1024 * 1024);
            return Math.Clamp(1 + gb * 2, 1, 20);
        }
        return 0.7;
    }

    /// <summary>Peso de um lote: partida do aplicador + ponto automático (administrador) + mudanças + releitura do PC.</summary>
    public static double BatchSeconds(ApplyBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return StartApplier + (batch.Level == BrokerLevel.Admin ? AdminRestorePoint : 0) + batch.Changes.Sum(ChangeSeconds) + Refresh;
    }

    /// <summary>"9,3 GB" → bytes (0 quando não é tamanho).</summary>
    public static double BytesOf(string? text)
    {
        var t = (text ?? "").Trim();
        var space = t.LastIndexOf(' ');
        if (space <= 0) return 0;
        if (!double.TryParse(t[..space], NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out var v)) return 0;
        return t[(space + 1)..].ToUpperInvariant() switch
        {
            "B" => v,
            "KB" => v * 1024,
            "MB" => v * 1024 * 1024,
            "GB" => v * 1024 * 1024 * 1024,
            "TB" => v * 1024 * 1024 * 1024 * 1024,
            _ => 0,
        };
    }

    /// <summary>Verbo do que o aplicador faz com uma mudança desta etapa ("Movendo para a quarentena: Cache do Chrome…").</summary>
    public static string Doing(AssistantStepId step, string title) => step switch
    {
        AssistantStepId.Welcome => "Criando o ponto de restauração…",
        AssistantStepId.Cleaning => $"Movendo para a quarentena: {title}…",
        AssistantStepId.Startup => $"Tirando da inicialização: {title}…",
        AssistantStepId.Privacy => $"Ajustando a privacidade: {title}…",
        AssistantStepId.Drivers => $"Instalando pelo Windows Update: {title}…",
        AssistantStepId.Schedules => $"Agendando: {title}…",
        _ => $"Aplicando: {title}…",
    };

    /// <summary>
    /// O que provavelmente está acontecendo dentro de um lote que roda há <paramref name="runningSeconds"/> s, pela ordem do
    /// pedido e pelo tempo típico de cada parte. É estimativa (o aplicador só responde no fim) — a tela diz isso.
    /// </summary>
    public static string BatchText(ApplyBatch batch, double runningSeconds)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var t = Math.Max(0, runningSeconds);
        if (batch.Level == BrokerLevel.Admin)
        {
            if (t < AdminRestorePoint) return "Criando um ponto de restauração do Windows antes (costuma levar de 20 a 60 s)…";
            t -= AdminRestorePoint;
        }
        foreach (var c in batch.Changes)
        {
            var s = ChangeSeconds(c);
            if (t < s) return Doing(batch.Step, c.Title);
            t -= s;
        }
        return batch.Changes.Count == 0 ? "Aplicando…" : "Conferindo cada item com uma releitura…";
    }

    /// <summary>Título da tarefa de um lote no feed.</summary>
    public static string BatchTitle(ApplyBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var n = batch.Changes.Count;
        var what = n == 1 ? "1 mudança" : string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{n} mudanças");
        return batch.Level == BrokerLevel.Admin ? $"Mudanças de sistema: {what}" : $"Na sua conta: {what}";
    }

    public static string BatchKey(ApplyBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return string.Create(CultureInfo.InvariantCulture, $"batch:{batch.Level}:{batch.Part}");
    }
}
