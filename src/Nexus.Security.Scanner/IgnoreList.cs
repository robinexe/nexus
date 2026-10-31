using System.Text.Json;

namespace Nexus.Security.Scanner;

public sealed record IgnoreEntry(string Path, string Sha256, DateTimeOffset AddedUtc);

/// <summary>
/// Exceções locais do usuário ("ignorar este achado"). Vale para o par caminho + SHA-256: se o arquivo mudar, volta a ser
/// verificado. Guardada em <c>%LocalAppData%\NEXUS\scanner\ignore.json</c> (dados do próprio NEXUS, não do sistema).
/// </summary>
public sealed class IgnoreList
{
    public const int MaxEntries = 5000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<string, IgnoreEntry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;
    public IReadOnlyCollection<IgnoreEntry> Entries => _entries.Values;

    private static string Key(string path, string sha256) => path.ToUpperInvariant() + "|" + sha256.ToLowerInvariant();

    public bool Contains(string path, string? sha256) => sha256 is not null && _entries.ContainsKey(Key(path, sha256));

    public bool Add(string path, string sha256, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (_entries.Count >= MaxEntries) return false;
        return _entries.TryAdd(Key(path, sha256), new IgnoreEntry(path, sha256.ToLowerInvariant(), now));
    }

    public bool Remove(string path, string sha256) => _entries.Remove(Key(path, sha256));

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "scanner", "ignore.json");

    /// <summary>Lê a lista; arquivo ausente ou ilegível = lista vazia (a verificação nunca falha por causa dela).</summary>
    public static IgnoreList Load(string path)
    {
        var list = new IgnoreList();
        try
        {
            if (!File.Exists(path)) return list;
            var items = JsonSerializer.Deserialize<List<IgnoreEntry>>(File.ReadAllText(path), Json) ?? [];
            foreach (var e in items.Take(MaxEntries))
                if (!string.IsNullOrWhiteSpace(e.Path) && e.Sha256 is { Length: 64 }) list._entries.TryAdd(Key(e.Path, e.Sha256), e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return list;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries.Values.OrderBy(e => e.AddedUtc).ToList(), Json));
        File.Move(tmp, path, overwrite: true);
    }
}
