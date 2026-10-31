using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Experimentation;
using Nexus.TraceAnalysis;

namespace Nexus.Measurement;

/// <summary>Rótulo que o usuário dá a uma sessão para comparar: antes ou depois de uma mudança.</summary>
public enum SessionTag { None, Before, After }

/// <summary>Ponto do gráfico de frametime (tempo desde o início, em segundos; frametime em ms).</summary>
public sealed record TracePoint(double Seconds, double FrametimeMs);

/// <summary>Faixa do histograma de frametime (ms) e quantos frames caíram nela.</summary>
public sealed record HistogramBin(double FromMs, double ToMs, int Count);

/// <summary>
/// Uma sessão de medição guardada: resumo calculado, curva reduzida para desenhar e histograma. O CSV original fica ao lado
/// (nada é recalculado a partir de valores arredondados).
/// </summary>
public sealed record MeasuredSession(
    string Id,
    string Name,
    string Application,
    DateTimeOffset CapturedAt,
    string Source,
    SessionTag Tag,
    string CsvFile,
    FrametimeSummary Summary,
    IReadOnlyList<TracePoint> Trace,
    IReadOnlyList<HistogramBin> Histogram,
    int SkippedRows,
    string MetricsFlavor);

public static class SessionAnalyzer
{
    public const int MaxTracePoints = 1500;

    /// <summary>
    /// Lê um CSV do PresentMon (v1 ou v2). Sem filtro, usa o processo com mais frames (o jogo, normalmente), porque o
    /// PresentMon registra todos os processos que apresentam frames (navegador, overlay, Discord...).
    /// </summary>
    public static (PresentMonCapture Capture, string Application, IReadOnlyList<PresentFrame> Frames) Read(string csvPath, string? processFilter = null)
    {
        using var reader = new StreamReader(csvPath);
        var capture = PresentMonCsvReader.Read(reader, processFilter);
        if (capture.Frames.Count == 0)
            throw new InvalidDataException(processFilter is null
                ? "O CSV não tem frames válidos."
                : $"Nenhum frame do processo '{processFilter}' no CSV.");
        var app = processFilter ?? capture.Frames.GroupBy(f => f.Application, StringComparer.OrdinalIgnoreCase).MaxBy(g => g.Count())!.Key;
        var frames = capture.Frames.Where(f => f.Application.Equals(app, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.TimeMs).ToList();
        return (capture, app, frames);
    }

    public static MeasuredSession Analyze(string id, string name, string csvFile, string source, DateTimeOffset capturedAt, string? processFilter = null)
    {
        var (capture, app, frames) = Read(csvFile, processFilter);
        var ft = frames.Select(f => f.FrameTimeMs).ToList();
        if (ft.Count < 100)
            throw new InvalidDataException($"Só {ft.Count} frames de {app}: são precisos pelo menos 100 (capture por mais tempo).");
        var summary = FrametimeMetrics.Summarize(ft);
        return new MeasuredSession(id, name, app, capturedAt, source, SessionTag.None, Path.GetFileName(csvFile), summary,
            Downsample(frames), Histogram(ft), capture.SkippedRows, capture.MetricsFlavor);
    }

    /// <summary>
    /// Reduz a curva para no máximo <see cref="MaxTracePoints"/> pontos preservando picos: em cada balde, guarda o frame
    /// mais lento (um stutter não pode sumir do gráfico por causa da redução).
    /// </summary>
    public static IReadOnlyList<TracePoint> Downsample(IReadOnlyList<PresentFrame> frames, int maxPoints = MaxTracePoints)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) return [];
        var t0 = frames[0].TimeMs;
        if (frames.Count <= maxPoints) return frames.Select(f => new TracePoint((f.TimeMs - t0) / 1000.0, f.FrameTimeMs)).ToList();
        var bucket = (double)frames.Count / maxPoints;
        var result = new List<TracePoint>(maxPoints);
        for (var b = 0; b < maxPoints; b++)
        {
            var from = (int)(b * bucket);
            var to = Math.Min(frames.Count, (int)((b + 1) * bucket));
            var worst = frames[from];
            for (var i = from + 1; i < to; i++) if (frames[i].FrameTimeMs > worst.FrameTimeMs) worst = frames[i];
            result.Add(new TracePoint((worst.TimeMs - t0) / 1000.0, worst.FrameTimeMs));
        }
        return result;
    }

    /// <summary>Histograma em faixas de 1 ms até 50 ms; a última faixa acumula tudo acima.</summary>
    public static IReadOnlyList<HistogramBin> Histogram(IReadOnlyList<double> frametimesMs, double binMs = 1, double maxMs = 50)
    {
        var bins = (int)(maxMs / binMs);
        var counts = new int[bins + 1];
        foreach (var f in frametimesMs) counts[Math.Min(bins, (int)(f / binMs))]++;
        return counts.Select((c, i) => new HistogramBin(i * binMs, i == bins ? double.PositiveInfinity : (i + 1) * binMs, c)).ToList();
    }
}

/// <summary>
/// Guarda sessões numa pasta do usuário (padrão: %LocalAppData%\NEXUS\sessoes): para cada sessão, o CSV e um JSON com o
/// resumo. Só arquivos do próprio NEXUS; nada fora dessa pasta é lido ou apagado.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public SessionStore(string folder) => Folder = folder ?? throw new ArgumentNullException(nameof(folder));

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "sessoes");

    public string Folder { get; }

    /// <summary>Copia o CSV para a pasta de sessões e guarda o resumo. O arquivo original não é alterado.</summary>
    public MeasuredSession Import(string csvPath, string? name = null, string? processFilter = null, string source = "importado", DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(csvPath);
        Directory.CreateDirectory(Folder);
        var id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dest = Path.Combine(Folder, id + ".csv");
        File.Copy(csvPath, dest);
        try
        {
            var when = capturedAt ?? File.GetLastWriteTime(csvPath);
            var session = SessionAnalyzer.Analyze(id, name ?? Path.GetFileNameWithoutExtension(csvPath), dest, source, when, processFilter);
            Save(session);
            return session;
        }
        catch
        {
            File.Delete(dest); // CSV inválido não fica órfão na pasta
            throw;
        }
    }

    public void Save(MeasuredSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, session.Id + ".json"), JsonSerializer.Serialize(session, Json));
    }

    public MeasuredSession Retag(MeasuredSession session, SessionTag tag)
    {
        var updated = (session ?? throw new ArgumentNullException(nameof(session))) with { Tag = tag };
        Save(updated);
        return updated;
    }

    public MeasuredSession Rename(MeasuredSession session, string name)
    {
        var updated = (session ?? throw new ArgumentNullException(nameof(session))) with { Name = string.IsNullOrWhiteSpace(name) ? session.Name : name.Trim() };
        Save(updated);
        return updated;
    }

    /// <summary>Sessões guardadas, da mais nova para a mais antiga. Arquivos ilegíveis são ignorados e contados.</summary>
    public (IReadOnlyList<MeasuredSession> Sessions, int Unreadable) List()
    {
        if (!Directory.Exists(Folder)) return ([], 0);
        var list = new List<MeasuredSession>();
        var bad = 0;
        foreach (var f in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<MeasuredSession>(File.ReadAllText(f), Json) is { } s) list.Add(s); else bad++;
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { bad++; }
        }
        return (list.OrderByDescending(s => s.CapturedAt).ToList(), bad);
    }

    /// <summary>Remove a sessão (JSON e CSV) desta pasta.</summary>
    public void Delete(MeasuredSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (var f in new[] { session.Id + ".json", session.CsvFile })
        {
            var p = Path.Combine(Folder, Path.GetFileName(f)); // nunca sai da pasta de sessões
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
