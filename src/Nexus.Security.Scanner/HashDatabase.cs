using System.Text.RegularExpressions;

namespace Nexus.Security.Scanner;

/// <summary>Uma entrada da lista de hashes. <see cref="Test"/> = amostra de teste (EICAR etc.), nunca ameaça real.</summary>
public sealed record HashEntry(string Sha256, string Label, string Source, bool Test);

/// <summary>
/// Banco local OPCIONAL de SHA-256 em formato aberto (texto, uma entrada por linha). O NEXUS só traz um conjunto de TESTE
/// (EICAR e uma amostra própria); listas reais são importadas pelo usuário (ex.: exportadas de fontes públicas) e nunca são
/// baixadas automaticamente. Formato (ver docs/modules/scanner.md):
/// <code>
/// # comentário
/// #! test                      (as linhas seguintes são amostras de teste)
/// &lt;64 hex&gt;  rótulo livre
/// "2024-01-01","&lt;64 hex&gt;","&lt;md5&gt;",...   (CSV também serve: vale o primeiro token de 64 hex da linha)
/// </code>
/// </summary>
public sealed partial class HashDatabase
{
    public const int MaxEntries = 2_000_000;
    public const int MaxLabelLength = 120;

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Token();

    private readonly Dictionary<string, HashEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _sources = [];

    public int Count => _entries.Count;
    public int RealCount => _entries.Values.Count(e => !e.Test);
    public IReadOnlyList<string> Sources => _sources;

    public HashEntry? Lookup(string? sha256) =>
        sha256 is not null && _entries.TryGetValue(sha256.ToLowerInvariant(), out var e) ? e : null;

    /// <summary>Conjunto de teste embutido (EICAR + amostra do NEXUS).</summary>
    public static HashDatabase LoadBuiltIn()
    {
        var db = new HashDatabase();
        using var stream = typeof(HashDatabase).Assembly.GetManifestResourceStream("Nexus.Security.Scanner.Hashes.test.hashes.txt")
                           ?? throw new InvalidOperationException("Conjunto de teste de hashes ausente do assembly.");
        using var reader = new StreamReader(stream);
        db.Add(reader, "teste embutido", out _, out _);
        return db;
    }

    /// <summary>Adiciona as entradas de um texto. Linhas sem hash viram "ignoradas" (contadas), nunca erro.</summary>
    public void Add(TextReader reader, string sourceName, out int added, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(reader);
        added = 0;
        skipped = 0;
        var test = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("#!", StringComparison.Ordinal))
            {
                var directive = t[2..].Trim();
                if (directive.Equals("test", StringComparison.OrdinalIgnoreCase)) test = true;
                else if (directive.Equals("real", StringComparison.OrdinalIgnoreCase)) test = false;
                continue;
            }
            if (t.StartsWith('#') || t.StartsWith("//", StringComparison.Ordinal)) continue;
            var m = Sha256Token().Match(t);
            if (!m.Success) { skipped++; continue; }
            if (_entries.Count >= MaxEntries) { skipped++; continue; }
            var hash = m.Value.ToLowerInvariant();
            var label = CleanLabel(t.Remove(m.Index, m.Length));
            if (_entries.TryAdd(hash, new HashEntry(hash, label.Length == 0 ? "(sem rótulo)" : label, sourceName, test))) added++;
            else skipped++;
        }
        if (added > 0 && !_sources.Contains(sourceName)) _sources.Add(sourceName);
    }

    public void AddFile(string path, out int added, out int skipped)
    {
        using var reader = new StreamReader(path);
        Add(reader, System.IO.Path.GetFileName(path), out added, out skipped);
    }

    private static string CleanLabel(string rest)
    {
        var chars = rest.Where(c => !char.IsControl(c)).ToArray();
        var s = new string(chars).Trim(' ', '\t', ',', ';', '"', '|', ':');
        while (s.Contains("\"\"", StringComparison.Ordinal)) s = s.Replace("\"\"", "\"", StringComparison.Ordinal);
        return s.Length > MaxLabelLength ? s[..MaxLabelLength] + "…" : s;
    }
}
