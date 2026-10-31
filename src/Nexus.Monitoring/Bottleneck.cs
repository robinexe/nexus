using System.Globalization;

namespace Nexus.Monitoring;

public enum BottleneckKind
{
    Collecting,
    Idle,
    None,
    Gpu,
    Cpu,
    SingleThread,
    Memory,
    Disk,
}

public sealed record BottleneckVerdict(BottleneckKind Kind, string Title, string Explanation)
{
    /// <summary>"good" (sem gargalo), "warn" (algo no limite) ou "info" (coletando/ocioso). A página escolhe a cor.</summary>
    public string Tone => Kind switch
    {
        BottleneckKind.None => "good",
        BottleneckKind.Idle or BottleneckKind.Collecting => "info",
        _ => "warn",
    };
}

/// <summary>Médias de uma janela recente (padrão: 10 s). Gargalo só conta se for sustentado, não um pico de 1 s.</summary>
public sealed record BottleneckInput(int Samples, double CpuPercent, double MaxCorePercent, double? GpuPercent, double? MemoryUsedPercent,
    double? CommitPercent, double DiskActivePercent, double DiskQueue)
{
    public static BottleneckInput From(IReadOnlyCollection<MonitorSnapshot> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Count == 0) return new BottleneckInput(0, 0, 0, null, null, null, 0, 0);
        var withGpu = window.Where(s => s.Gpu is not null).ToList();
        var withMem = window.Where(s => s.Memory is not null).ToList();
        return new BottleneckInput(
            window.Count,
            window.Average(s => s.Cpu.TotalPercent),
            window.Average(s => s.Cpu.MaxCorePercent),
            withGpu.Count == 0 ? null : withGpu.Average(s => s.Gpu!.OverallPercent),
            withMem.Count == 0 ? null : withMem.Average(s => s.Memory!.UsedPercent),
            withMem.Count == 0 ? null : withMem.Average(s => s.Memory!.CommitPercent),
            window.Average(s => s.DiskActiveMax),
            window.Average(s => s.DiskQueueMax));
    }
}

/// <summary>
/// Detector de gargalo simples e honesto. Olha só contadores do Windows: não vê o jogo por dentro (frametime, limite de FPS,
/// V-Sync), por isso as frases dizem "parece" e explicam o porquê. Limiares documentados em docs/modules/monitor.md.
/// </summary>
public static class BottleneckDetector
{
    public const int MinimumSamples = 5;
    public const double GpuBusy = 95, CpuBusy = 90, CpuNotBusyWithGpu = 85, CoreBusy = 95, CpuLowForCore = 60, GpuLowForCore = 80;
    public const double MemoryBusy = 92, CommitBusy = 90, DiskBusy = 90, DiskQueueBusy = 2, IdleBelow = 15;

    public static BottleneckVerdict Evaluate(BottleneckInput i)
    {
        ArgumentNullException.ThrowIfNull(i);
        if (i.Samples < MinimumSamples)
            return new(BottleneckKind.Collecting, "Coletando", "Juntando alguns segundos de leitura antes de opinar. Picos de 1 segundo não contam.");

        var cpu = P(i.CpuPercent);
        var gpuText = i.GpuPercent is { } gg ? P(gg) : "sem leitura";

        if (i.CommitPercent >= CommitBusy || i.MemoryUsedPercent >= MemoryBusy)
            return new(BottleneckKind.Memory, "Memória quase cheia",
                $"RAM em uso {P(i.MemoryUsedPercent ?? 0)} e memória comprometida {P(i.CommitPercent ?? 0)} do limite. O Windows passa a usar o arquivo de " +
                "paginação no disco, o que causa travadas. Feche programas abertos em segundo plano; \"limpadores de RAM\" não resolvem.");

        if (i.GpuPercent >= GpuBusy && i.CpuPercent < CpuNotBusyWithGpu)
            return new(BottleneckKind.Gpu, "Limitado pela GPU",
                $"GPU a {gpuText} e CPU a {cpu}: quem segura os quadros é a placa de vídeo. Em jogo pesado sem limite de FPS isso é normal e esperado. " +
                "Menos resolução/qualidade ou um limite de FPS aliviam; mexer em ajustes de CPU não muda nada aqui.");

        if (i.CpuPercent >= CpuBusy)
            return new(BottleneckKind.Cpu, "CPU no limite",
                $"CPU a {cpu} em média (GPU {gpuText}). Todos os núcleos estão ocupados: veja na lista abaixo qual processo está usando. " +
                "Se for o jogo, a CPU é o limite; se for outro programa, fechá-lo libera desempenho.");

        if (i.MaxCorePercent >= CoreBusy && i.CpuPercent < CpuLowForCore && (i.GpuPercent is null || i.GpuPercent < GpuLowForCore))
            return new(BottleneckKind.SingleThread, "Parece limitado por um núcleo",
                $"Um núcleo está a {P(i.MaxCorePercent)} enquanto a CPU toda está a {cpu} e a GPU a {gpuText}. Muitos jogos dependem de uma " +
                "thread principal: ela chega ao limite e o resto espera. É uma pista, não prova: o Windows troca threads de núcleo e isso dilui o número.");

        if (i.DiskActivePercent >= DiskBusy && i.DiskQueue >= DiskQueueBusy)
            return new(BottleneckKind.Disk, "Disco saturado",
                $"Um disco está ativo {P(i.DiskActivePercent)} do tempo com fila média de {i.DiskQueue.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"))} pedidos. " +
                "Carregamentos e travadas ao abrir áreas novas vêm daqui. Atualizações, antivírus ou downloads costumam ser a causa.");

        if (i.CpuPercent < IdleBelow && (i.GpuPercent is null || i.GpuPercent < IdleBelow))
            return new(BottleneckKind.Idle, "PC ocioso", $"CPU a {cpu} e GPU a {gpuText}. Abra o jogo para ver onde está o limite.");

        return new(BottleneckKind.None, "Nenhum gargalo evidente",
            $"CPU a {cpu}, GPU a {gpuText}, nada no limite. Se o jogo tem limite de FPS ou V-Sync, é ele que está segurando os quadros (e isso é bom).");
    }

    private static string P(double v) => $"{Math.Round(v).ToString("0", CultureInfo.InvariantCulture)}%";
}
