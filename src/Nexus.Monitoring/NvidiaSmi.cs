using System.Globalization;

namespace Nexus.Monitoring;

/// <summary>Leitura de uma placa NVIDIA pelo nvidia-smi (ferramenta do próprio driver). Nulo = "[N/A]" ou "[Not Supported]".</summary>
public sealed record NvidiaGpuReading(int Index, double? TemperatureC, double? UtilizationPercent, double? GraphicsClockMhz, double? PowerWatts);

public static class NvidiaSmi
{
    /// <summary>Argumentos fixos (somente leitura: a consulta não muda nada no driver).</summary>
    public const string Arguments = "--query-gpu=temperature.gpu,utilization.gpu,clocks.gr,power.draw --format=csv,noheader,nounits";

    /// <summary>Uma linha por placa: "45, 12, 1500, 35.20". Linhas que não têm 4 campos são ignoradas.</summary>
    public static IReadOnlyList<NvidiaGpuReading> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var list = new List<NvidiaGpuReading>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length != 4) continue;
            var values = f.Select(Number).ToArray();
            if (values.All(v => v is null)) continue;
            list.Add(new NvidiaGpuReading(list.Count, values[0], values[1], values[2], values[3]));
        }
        return list;
    }

    private static double? Number(string field)
    {
        var s = field.Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : null;
    }
}
