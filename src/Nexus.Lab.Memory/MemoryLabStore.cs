using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Lab.Memory;

/// <summary>Uma medição salva (para comparar antes/depois, por exemplo depois de instalar um segundo módulo).</summary>
public sealed record MemoryRun
{
    public DateTimeOffset TakenAt { get; init; }
    public string Configuration { get; init; } = "";
    public uint? ConfiguredMts { get; init; }
    public int ModuleCount { get; init; }
    public string ChannelMode { get; init; } = "";
    public double TheoreticalGbps { get; init; }
    public BandwidthResult? Bandwidth { get; init; }
    public LatencyResult? Latency { get; init; }
    public string? ChannelVerdict { get; init; }
}

/// <summary>Arquivos JSON em %LocalAppData%\NEXUS\lab\memory (um por medição). Só leitura/escrita de arquivos do próprio NEXUS.</summary>
public sealed class MemoryLabStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Directory { get; }

    public MemoryLabStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "lab", "memory");
    }

    public string Save(MemoryRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        System.IO.Directory.CreateDirectory(Directory);
        var name = run.TakenAt.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        var path = Path.Combine(Directory, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(run, Json));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Medições salvas, da mais nova para a mais antiga. Arquivos corrompidos são ignorados.</summary>
    public IReadOnlyList<MemoryRun> LoadAll(int max = 50)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<MemoryRun>();
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "*.json").OrderDescending(StringComparer.Ordinal).Take(max))
        {
            try
            {
                if (JsonSerializer.Deserialize<MemoryRun>(File.ReadAllText(f), Json) is { } r) list.Add(r);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(r => r.TakenAt).ToList();
    }
}
