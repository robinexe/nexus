using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Measurement.AbLab;

/// <summary>
/// Guarda o laboratório num JSON do próprio NEXUS (padrão: %LocalAppData%\NEXUS\ab-lab.json): o teste em andamento (para retomar
/// depois de fechar o NEXUS, inclusive o que desfazer), a fila e o histórico. Escrita atômica (arquivo temporário + troca).
/// Arquivo ilegível: começa vazio e guarda o antigo como .bad (nunca apaga).
/// </summary>
public sealed class AbLabStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "ab-lab.json");

    public string FilePath { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public AbLabData Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return AbLabData.Empty;
            return JsonSerializer.Deserialize<AbLabData>(File.ReadAllText(FilePath), Json) ?? AbLabData.Empty;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return AbLabData.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return AbLabData.Empty; }
    }

    public void Save(AbLabData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
