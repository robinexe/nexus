using System.Globalization;

namespace Nexus.TraceAnalysis;

/// <summary>
/// Um frame lido do CSV do PresentMon 2.x. Campos ausentes na captura ficam nulos — nunca são inventados.
/// Unidades: milissegundos. <see cref="TimeMs"/> é relativo ao início da captura.
/// </summary>
public sealed record PresentFrame(
    string Application,
    int ProcessId,
    double TimeMs,
    double FrameTimeMs,
    double? CpuBusyMs,
    double? GpuBusyMs,
    double? DisplayedTimeMs,
    double? ClickToPhotonMs,
    string? PresentMode);

public sealed record PresentMonCapture(
    IReadOnlyList<PresentFrame> Frames,
    string MetricsFlavor,
    int SkippedRows,
    IReadOnlyList<string> Columns);

/// <summary>
/// Parser por nome de coluna (não por posição) para suportar os cabeçalhos v1 ("MsBetweenPresents", "TimeInSeconds")
/// e v2 ("FrameTime", "CPUStartTime", "CPUBusy", "GPUBusy", "DisplayedTime"), conforme CsvOutput.cpp do PresentMon 2.6.0.
/// </summary>
public static class PresentMonCsvReader
{
    public static PresentMonCapture Read(TextReader reader, string? processNameFilter = null)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException("CSV vazio.");
        var cols = SplitCsv(header);
        var idx = cols.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i, StringComparer.OrdinalIgnoreCase);

        int Col(params string[] names)
        {
            foreach (var n in names) if (idx.TryGetValue(n, out var i)) return i;
            return -1;
        }

        var app = Col("Application");
        var pid = Col("ProcessID");
        var v2 = idx.ContainsKey("FrameTime");
        var frameTime = v2 ? Col("FrameTime") : Col("MsBetweenPresents");
        var time = Col("CPUStartTime", "CPUStartTimeInMs", "TimeInSeconds", "TimeInMs");
        var timeIsSeconds = time >= 0 && cols[time].Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase);
        var cpuBusy = Col("CPUBusy", "MsCPUBusy");
        var gpuBusy = Col("GPUBusy", "MsGPUBusy");
        var displayed = Col("DisplayedTime", "MsBetweenDisplayChange");
        var click = Col("ClickToPhotonLatency", "MsClickToPhotonLatency");
        var mode = Col("PresentMode");

        if (app < 0 || frameTime < 0 || time < 0)
            throw new InvalidDataException("Cabeçalho do PresentMon sem Application, tempo ou frametime.");

        var frames = new List<PresentFrame>();
        var skipped = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            if (f.Count != cols.Count) { skipped++; continue; }
            if (processNameFilter is not null && !f[app].Equals(processNameFilter, StringComparison.OrdinalIgnoreCase)) continue;
            var ft = Num(f[frameTime]);
            var t = Num(f[time]);
            if (ft is null || t is null || ft <= 0) { skipped++; continue; }
            frames.Add(new PresentFrame(
                f[app],
                pid >= 0 && int.TryParse(f[pid], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0,
                timeIsSeconds ? t.Value * 1000.0 : t.Value,
                ft.Value,
                cpuBusy >= 0 ? Num(f[cpuBusy]) : null,
                gpuBusy >= 0 ? Num(f[gpuBusy]) : null,
                displayed >= 0 ? Num(f[displayed]) : null,
                click >= 0 ? Num(f[click]) : null,
                mode >= 0 ? f[mode] : null));
        }
        // A unidade de "CPUStartTime" depende de --time_units; inferimos pelos dados em vez de presumir:
        // se o intervalo mediano entre frames for ~1/1000 do frametime mediano, a coluna está em segundos.
        if (!timeIsSeconds && frames.Count > 10)
        {
            var dts = frames.Zip(frames.Skip(1), (a, b) => b.TimeMs - a.TimeMs).Where(d => d > 0).Order().ToList();
            var fts = frames.Select(fr => fr.FrameTimeMs).Order().ToList();
            if (dts.Count > 0)
            {
                var ratio = dts[dts.Count / 2] / fts[fts.Count / 2];
                if (ratio is > 0.0005 and < 0.002)
                    frames = frames.Select(fr => fr with { TimeMs = fr.TimeMs * 1000.0 }).ToList();
            }
        }
        return new PresentMonCapture(frames, v2 ? "v2" : "v1", skipped, cols);
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null; // "NA" → null

    internal static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') q = false;
                else cur.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result;
    }
}
