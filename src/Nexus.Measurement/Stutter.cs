using System.Globalization;

namespace Nexus.Measurement;

/// <summary>Causa PROVÁVEL de uma travada (a tela sempre diz "provável": é inferência sobre contadores, não prova).</summary>
public enum StutterCause
{
    /// <summary>A placa de vídeo levou mais tempo naquele quadro (GPUBusy cresceu junto com o frametime).</summary>
    Gpu,
    /// <summary>O processador/o próprio jogo levou mais tempo (CPUBusy ou a espera do jogo cresceu, a GPU não).</summary>
    Cpu,
    /// <summary>Do lado do jogo/CPU, no começo da partida ou com o jogo lendo muito do disco: compilação de shader ou carregamento.</summary>
    ShaderOrStreaming,
    /// <summary>Do lado da CPU, com outro programa usando bastante processador naquele momento.</summary>
    Background,
    /// <summary>Sem dados de CPU/GPU por quadro (PresentMon antigo ou CSV importado sem essas colunas).</summary>
    Unknown,
}

/// <summary>
/// Um quadro para a análise de travadas. Colunas do PresentMon 2.6 (nome padrão / com <c>--v2_metrics</c>): FrameTime /
/// MsBetweenAppStart, CPUBusy / MsCPUBusy, CPUWait / MsCPUWait, GPUBusy / MsGPUBusy, GPUWait / MsGPUWait, DisplayLatency,
/// AnimationError / MsAnimationError e FrameType (só com <c>--track_frame_type</c>). Ausente = null, nunca inventado.
/// </summary>
public readonly record struct StutterFrame(double FrametimeMs, double? CpuBusyMs = null, double? CpuWaitMs = null, double? GpuBusyMs = null,
    double? GpuWaitMs = null, double? DisplayLatencyMs = null, double? AnimationErrorMs = null, bool Generated = false);

/// <summary>Uso de CPU de outro processo numa amostra (0–100% da máquina inteira, como o Gerenciador de Tarefas).</summary>
public sealed record ProcessCpuShare(string Name, double CpuPercent);

/// <summary>
/// Amostra de atividade durante a captura (padrão: a cada 2 s). <paramref name="Seconds"/> é o tempo desde o primeiro quadro;
/// a amostra cobre o intervalo que termina nele. <paramref name="GameReadBytesPerSec"/>: leituras de E/S do jogo
/// (GetProcessIoCounters; inclui disco e outras E/S), null quando não deu para ler.
/// </summary>
public sealed record ActivitySample(double Seconds, IReadOnlyList<ProcessCpuShare> Others, double? GameReadBytesPerSec);

/// <summary>Uma travada (picos seguidos a menos de <see cref="StutterOptions.MergeWithinMs"/> contam como uma) e o pior quadro dela.</summary>
public sealed record StutterEvent(double Seconds, double FrametimeMs, double MedianMs, StutterCause Cause, string? Process = null,
    double? CpuBusyMs = null, double? GpuBusyMs = null, double? GameReadMBps = null);

/// <summary>Critérios (declarados no documento do módulo e na tela "como foi calculado").</summary>
public sealed record StutterOptions
{
    /// <summary>Pico: frametime &gt; max(<see cref="Ratio"/> × mediana, mediana + <see cref="ExtraMs"/>).</summary>
    public double Ratio { get; init; } = 2.0;
    public double ExtraMs { get; init; } = 8.0;
    /// <summary>Mediana dos N quadros anteriores (mesma janela do critério de engasgos do resumo).</summary>
    public int WindowFrames { get; init; } = 30;
    /// <summary>Picos a menos disto um do outro são a mesma travada.</summary>
    public double MergeWithinMs { get; init; } = 250;
    /// <summary>Começo da partida em que travadas do lado da CPU são provavelmente compilação de shader (jogo carregando tudo pela primeira vez).</summary>
    public double FirstMinutesSeconds { get; init; } = 180;
    /// <summary>Leitura do jogo a partir da qual ele está carregando dados (MB/s).</summary>
    public double StreamingReadMBps { get; init; } = 8;
    /// <summary>Outro processo com pelo menos isto da CPU da máquina conta como "programa em segundo plano".</summary>
    public double BackgroundCpuPercent { get; init; } = 10;
    /// <summary>Distância máxima (s) entre a travada e a amostra de atividade usada.</summary>
    public double ActivityToleranceSeconds { get; init; } = 2.5;

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"pico = frametime > max({Ratio:0.##} × mediana dos {WindowFrames} quadros anteriores, mediana + {ExtraMs:0.##} ms); picos a menos de {MergeWithinMs:0} ms = uma travada");
}

/// <summary>Resultado de "Por que travou?". Contagens por causa provável; sem nenhuma travada, <see cref="Events"/> = 0.</summary>
public sealed record StutterReport
{
    public int Events { get; init; }
    public double DurationSeconds { get; init; }
    public int Gpu { get; init; }
    public int Cpu { get; init; }
    public int ShaderOrStreaming { get; init; }
    public int Background { get; init; }
    public int Unknown { get; init; }
    /// <summary>Até 8 piores travadas, da pior para a menos pior.</summary>
    public IReadOnlyList<StutterEvent> Worst { get; init; } = [];
    /// <summary>Programa que mais apareceu nas travadas de segundo plano.</summary>
    public string? TopBackgroundProcess { get; init; }
    public bool HasCpuGpuData { get; init; }
    public bool HasActivityData { get; init; }
    public bool HasDiskData { get; init; }
    /// <summary>Quadros gerados (geração de quadros do driver) ignorados: não são do jogo.</summary>
    public int GeneratedFrames { get; init; }
    public string Criterion { get; init; } = "";

    public double PerMinute => DurationSeconds > 0 ? Events / (DurationSeconds / 60) : 0;

    public int Count(StutterCause c) => c switch
    {
        StutterCause.Gpu => Gpu,
        StutterCause.Cpu => Cpu,
        StutterCause.ShaderOrStreaming => ShaderOrStreaming,
        StutterCause.Background => Background,
        _ => Unknown,
    };

    /// <summary>A causa com mais travadas (null sem travadas). Empate: a ordem do enum (GPU, CPU, shader, segundo plano, sem dados).</summary>
    public StutterCause? Main
    {
        get
        {
            if (Events == 0) return null;
            var best = StutterCause.Gpu;
            foreach (var c in Enum.GetValues<StutterCause>()) if (Count(c) > Count(best)) best = c;
            return best;
        }
    }

    /// <summary>A causa principal tem mais da metade das travadas ("a maioria"); senão a tela diz "várias causas".</summary>
    public bool MainIsMajority => Main is { } m && Count(m) * 2 > Events;
}

/// <summary>
/// "Por que travou?": acha os picos de frametime e dá a causa PROVÁVEL de cada um, só com o que a captura já tem (colunas do
/// PresentMon por quadro + amostras de CPU por processo e leitura do jogo feitas pelo NEXUS durante a partida). Nível de usuário:
/// não usa rastreamento do kernel. Regras, nesta ordem, para o pior quadro de cada travada:
/// <list type="number">
/// <item>sem CPUBusy/GPUBusy no quadro: "sem dados" (a não ser que outro programa estivesse usando muita CPU: segundo plano);</item>
/// <item>o que a GPU cresceu (GPUBusy − mediana) cobre pelo menos metade do excesso do quadro e é maior que o que a CPU cresceu: GPU;</item>
/// <item>senão é do lado do jogo/CPU, e então: outro processo com ≥ 10% da CPU e o jogo sem ler muito: segundo plano;
/// nos primeiros 3 min (sem dado de disco, ou com o jogo lendo) ou com o jogo lendo ≥ 8 MB/s: compilação de shader/carregamento;
/// senão: CPU/jogo.</item>
/// </list>
/// </summary>
public static class StutterAnalyzer
{
    public const int MaxWorst = 8;

    public static StutterReport Analyze(IReadOnlyList<StutterFrame> frames, IReadOnlyList<ActivitySample>? activity = null, StutterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var o = options ?? new StutterOptions();
        activity ??= [];
        var samples = activity.OrderBy(a => a.Seconds).ToList();
        var hasDisk = samples.Any(s => s.GameReadBytesPerSec is not null);

        // Só quadros do jogo (gerados pelo driver não contam); o relógio inclui todos.
        var real = new List<(StutterFrame F, double T)>(frames.Count);
        double clock = 0;
        var generated = 0;
        var hasCpuGpu = false;
        foreach (var f in frames)
        {
            if (!(f.FrametimeMs > 0) || !double.IsFinite(f.FrametimeMs)) continue;
            clock += f.FrametimeMs;
            if (f.Generated) { generated++; continue; }
            if (f.CpuBusyMs is not null && f.GpuBusyMs is not null) hasCpuGpu = true;
            real.Add((f, clock / 1000.0));
        }

        var events = new List<StutterEvent>();
        var window = new SortedWindow(o.WindowFrames);
        StutterEvent? open = null;
        double lastSpikeT = double.NegativeInfinity;
        for (var i = 0; i < real.Count; i++)
        {
            var (f, t) = real[i];
            if (window.Full)
            {
                var median = window.Median;
                if (f.FrametimeMs > Math.Max(o.Ratio * median, median + o.ExtraMs))
                {
                    var ev = Classify(real, i, median, samples, hasDisk, o);
                    if (open is not null && (t - lastSpikeT) * 1000 - f.FrametimeMs <= o.MergeWithinMs)
                    {
                        if (ev.FrametimeMs > open.FrametimeMs) open = ev with { Seconds = open.Seconds };
                    }
                    else
                    {
                        if (open is not null) events.Add(open);
                        open = ev;
                    }
                    lastSpikeT = t;
                }
            }
            window.Push(f.FrametimeMs);
        }
        if (open is not null) events.Add(open);

        var bg = events.Where(e => e.Cause == StutterCause.Background && e.Process is not null)
            .GroupBy(e => e.Process!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        return new StutterReport
        {
            Events = events.Count,
            DurationSeconds = clock / 1000.0,
            Gpu = events.Count(e => e.Cause == StutterCause.Gpu),
            Cpu = events.Count(e => e.Cause == StutterCause.Cpu),
            ShaderOrStreaming = events.Count(e => e.Cause == StutterCause.ShaderOrStreaming),
            Background = events.Count(e => e.Cause == StutterCause.Background),
            Unknown = events.Count(e => e.Cause == StutterCause.Unknown),
            Worst = events.OrderByDescending(e => e.FrametimeMs).Take(MaxWorst).ToList(),
            TopBackgroundProcess = bg,
            HasCpuGpuData = hasCpuGpu,
            HasActivityData = samples.Count > 0,
            HasDiskData = hasDisk,
            GeneratedFrames = generated,
            Criterion = o.Describe(),
        };
    }

    private static StutterEvent Classify(List<(StutterFrame F, double T)> real, int i, double median, List<ActivitySample> samples, bool hasDisk, StutterOptions o)
    {
        var (f, t) = real[i];
        var sample = Nearest(samples, t, o.ActivityToleranceSeconds);
        var heavy = sample?.Others.Where(p => p.CpuPercent >= o.BackgroundCpuPercent).MaxBy(p => p.CpuPercent);
        var readMBps = sample?.GameReadBytesPerSec is { } r ? r / (1024 * 1024) : (double?)null;
        var reading = readMBps >= o.StreamingReadMBps;

        StutterCause cause;
        if (f.CpuBusyMs is not { } cpu || f.GpuBusyMs is not { } gpu)
            cause = heavy is not null ? StutterCause.Background : StutterCause.Unknown;
        else
        {
            var from = Math.Max(0, i - o.WindowFrames);
            var medCpu = MedianOf(real, from, i, x => x.CpuBusyMs);
            var medGpu = MedianOf(real, from, i, x => x.GpuBusyMs);
            var excess = f.FrametimeMs - median;
            var gpuGrew = gpu - (medGpu ?? gpu);
            var cpuGrew = cpu - (medCpu ?? cpu);
            if (gpuGrew >= 0.5 * excess && gpuGrew >= cpuGrew) cause = StutterCause.Gpu;
            else if (heavy is not null && !reading) cause = StutterCause.Background;
            else if (reading || (t <= o.FirstMinutesSeconds && (!hasDisk || readMBps is null || readMBps > 0.5))) cause = StutterCause.ShaderOrStreaming;
            else cause = StutterCause.Cpu;
        }
        return new StutterEvent(t, f.FrametimeMs, median, cause, cause == StutterCause.Background ? heavy?.Name : null, f.CpuBusyMs, f.GpuBusyMs,
            readMBps is { } mb ? Math.Round(mb, 1) : null);
    }

    private static ActivitySample? Nearest(List<ActivitySample> samples, double t, double tolerance)
    {
        // A amostra em T cobre (T − intervalo, T]: prefere a primeira com Seconds ≥ t; senão a anterior mais próxima.
        ActivitySample? best = null;
        var bestD = double.MaxValue;
        foreach (var s in samples)
        {
            var d = s.Seconds >= t ? s.Seconds - t : (t - s.Seconds) * 1.5;
            if (d < bestD) { bestD = d; best = s; }
        }
        return best is not null && Math.Abs(best.Seconds - t) <= tolerance ? best : null;
    }

    private static double? MedianOf(List<(StutterFrame F, double T)> real, int from, int to, Func<StutterFrame, double?> pick)
    {
        var values = new List<double>(to - from);
        for (var k = from; k < to; k++) if (pick(real[k].F) is { } v && double.IsFinite(v)) values.Add(v);
        if (values.Count == 0) return null;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>Mediana dos últimos N valores: vetor ordenado + fila (inserção/remoção por busca binária).</summary>
    private sealed class SortedWindow(int size)
    {
        private readonly List<double> _sorted = new(size);
        private readonly Queue<double> _order = new(size);

        public bool Full => _order.Count >= size;
        public double Median => _sorted[_sorted.Count / 2];

        public void Push(double v)
        {
            if (_order.Count >= size)
            {
                var old = _order.Dequeue();
                var at = _sorted.BinarySearch(old);
                _sorted.RemoveAt(at < 0 ? ~at : at);
            }
            _order.Enqueue(v);
            var pos = _sorted.BinarySearch(v);
            _sorted.Insert(pos < 0 ? ~pos : pos, v);
        }
    }

    /// <summary>
    /// Lê os quadros de um CSV do PresentMon (qualquer versão aceita por <see cref="PresentMonLineParser"/>) ou do CSV normalizado
    /// do NEXUS. Sem <paramref name="application"/>, usa o processo com mais quadros.
    /// </summary>
    public static IReadOnlyList<StutterFrame> ReadCsv(string path, string? application = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (application is null)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var p1 = new PresentMonLineParser();
            foreach (var line in File.ReadLines(path))
                if (p1.TryParse(line, out var lf)) counts[lf.Application] = counts.TryGetValue(lf.Application, out var n) ? n + 1 : 1;
            application = counts.Count == 0 ? null : counts.MaxBy(k => k.Value).Key;
            if (application is null) return [];
        }
        var parser = new PresentMonLineParser();
        var frames = new List<StutterFrame>();
        foreach (var line in File.ReadLines(path))
            if (parser.TryParse(line, out var f) && f.Application.Equals(application, StringComparison.OrdinalIgnoreCase)) frames.Add(f.ToStutterFrame());
        return frames;
    }
}
