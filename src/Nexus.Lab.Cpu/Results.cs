using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Lab.Cpu;

public enum CpuRunKind { Quick, Sustained }

/// <summary>Uma rodada de medição, como fica guardada em %LocalAppData%\NEXUS\lab\cpu\*.json para comparar antes/depois.</summary>
public sealed record CpuLabRun
{
    public int FormatVersion { get; init; } = 1;
    public DateTimeOffset TakenAt { get; init; }
    public CpuRunKind Kind { get; init; }
    public string? Brand { get; init; }
    public bool? OnBattery { get; init; }
    public string? PowerMode { get; init; }
    public bool Pinned { get; init; }
    public IReadOnlyList<CoreFrequency> CoreFrequencies { get; init; } = [];
    public IReadOnlyList<LatencyPoint> Latency { get; init; } = [];
    public IReadOnlyList<CacheStep> CacheSteps { get; init; } = [];
    public IReadOnlyList<int> CoreToCoreCores { get; init; } = [];
    public double?[][] CoreToCoreNs { get; init; } = [];
    public IReadOnlyList<FrequencySample> AllCoreSeries { get; init; } = [];
    /// <summary>Frequência média com todos os núcleos carregados (GHz).</summary>
    public double? AllCoreGhz { get; init; }
    /// <summary>Operações dependentes/s de um núcleo (o melhor) e de todos juntos, em bilhões/s.</summary>
    public double? SingleThreadGops { get; init; }
    public double? MultiThreadGops { get; init; }
    public int Threads { get; init; }
    public double DurationSeconds { get; init; }
    public bool Cancelled { get; init; }

    [JsonIgnore] public CoreFrequency? BestCore => CoreFrequencies.Count == 0 ? null : CoreFrequencies.MaxBy(c => c.Ghz);
    [JsonIgnore] public double? Scaling => SingleThreadGops is > 0 && MultiThreadGops is { } mt ? mt / SingleThreadGops : null;
    [JsonIgnore] public CoreToCoreMatrix? Matrix => CoreToCoreCores.Count < 2 ? null : new CoreToCoreMatrix(CoreToCoreCores, CoreToCoreNs);
    [JsonIgnore] public ThrottleResult? Throttle => AllCoreSeries.Count >= 4 ? ThrottleDetector.Analyze(AllCoreSeries) : null;

    /// <summary>Dispersão entre núcleos de desempenho (%): (mais rápido − mais lento) / mais rápido.</summary>
    [JsonIgnore]
    public double? SpreadPercent
    {
        get
        {
            var p = CoreFrequencies.Where(c => c.PerformanceCore && c.Ghz > 0).ToList();
            if (p.Count < 2) return null;
            var max = p.Max(c => c.Ghz);
            return (max - p.Min(c => c.Ghz)) / max * 100;
        }
    }
}

/// <summary>Guarda e lê as rodadas. Só escreve na pasta de dados do próprio NEXUS (nada do sistema).</summary>
public sealed class CpuLabStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public CpuLabStore(string directory) => Directory = directory;

    public string Directory { get; }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "lab", "cpu");

    public string Save(CpuLabRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        System.IO.Directory.CreateDirectory(Directory);
        var name = string.Create(CultureInfo.InvariantCulture, $"cpu-{run.TakenAt.ToUniversalTime():yyyyMMdd-HHmmss}-{(run.Kind == CpuRunKind.Quick ? "rapida" : "sustentada")}.json");
        var path = Path.Combine(Directory, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(run, Json));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Todas as rodadas legíveis, da mais nova para a mais antiga. Arquivos corrompidos são ignorados.</summary>
    public IReadOnlyList<CpuLabRun> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<CpuLabRun>();
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "cpu-*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<CpuLabRun>(File.ReadAllText(f), Json) is { } r) list.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { /* arquivo ilegível: ignora */ }
        }
        return list.OrderByDescending(r => r.TakenAt).ToList();
    }

    public static string Serialize(CpuLabRun run) => JsonSerializer.Serialize(run, Json);

    public static CpuLabRun? Deserialize(string json) => JsonSerializer.Deserialize<CpuLabRun>(json, Json);
}

/// <summary>Diferenças entre duas rodadas, em frases curtas.</summary>
public static class RunComparer
{
    public static IReadOnlyList<string> Compare(CpuLabRun before, CpuLabRun after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var lines = new List<string>();
        void Delta(string what, double? a, double? b, string unit, bool higherIsBetter, string fmt = "0.00")
        {
            if (a is not { } x || b is not { } y || x <= 0) return;
            var pct = (y - x) / x * 100;
            var verdict = Math.Abs(pct) < 2 ? "igual (dentro do ruído)" : (pct > 0) == higherIsBetter ? "melhor" : "pior";
            lines.Add(string.Format(pt, "{0}: {1} → {2} {3} ({4:+0;-0}%, {5})", what, x.ToString(fmt, pt), y.ToString(fmt, pt), unit, pct, verdict));
        }
        Delta("Núcleo mais rápido", before.BestCore?.Ghz, after.BestCore?.Ghz, "GHz", true);
        Delta("Todos os núcleos", before.AllCoreGhz, after.AllCoreGhz, "GHz", true);
        Delta("Desempenho multi-thread", before.MultiThreadGops, after.MultiThreadGops, "bi op/s", true, "0.0");
        var ramA = before.CacheSteps.FirstOrDefault(s => s.Level == "RAM")?.Ns;
        var ramB = after.CacheSteps.FirstOrDefault(s => s.Level == "RAM")?.Ns;
        Delta("Latência da RAM", ramA, ramB, "ns", false, "0");
        Delta("Queda sob carga", before.Throttle?.DropPercent, after.Throttle?.DropPercent, "%", false, "0");
        return lines;
    }
}
