using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Cleaning;

/// <summary>Id de um lote de limpeza (um clique em "Limpar"): data UTC + sufixo aleatório. Ex.: 20261031T120000Z-1a2b3c4d.</summary>
public static partial class CleaningBatchId
{
    /// <summary>Regra fechada usada também em <c>userParameters</c> do catálogo.</summary>
    public const string Pattern = "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}";

    [GeneratedRegex("^" + Pattern + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();

    public static bool IsValid(string? id) => id is not null && Regex().IsMatch(id);

    public static string New(DateTime utcNow) =>
        utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" +
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public static DateTime? TimeOf(string id) =>
        IsValid(id) && DateTime.TryParseExact(id[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

/// <summary>Estados de um arquivo no manifesto.</summary>
public static class QuarantineEntryState
{
    public const string Pending = "pending";
    public const string Moved = "moved";
    public const string SkippedInUse = "skipped-in-use";
    public const string SkippedError = "skipped-error";
    public const string Restored = "restored";
    public const string RestoredRenamed = "restored-renamed";
    public const string RestoreFailed = "restore-failed";
}

public sealed class QuarantineEntry
{
    public int Index { get; set; }
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string State { get; set; } = QuarantineEntryState.Pending;
    public string? RestoredTo { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Manifesto de uma regra dentro de um lote: <c>DataDirectory\quarantine\cleaning\&lt;lote&gt;\&lt;regra&gt;\manifest.json</c>, com os
/// arquivos em <c>files\&lt;índice&gt;</c>. É gravado ANTES do primeiro arquivo sair (lista completa, tudo "pending"); a presença do
/// arquivo em <c>files\</c> é a prova de que o NEXUS o moveu, mesmo se o processo morrer no meio.
/// </summary>
public sealed class QuarantineManifest
{
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string Restored = "restored";

    public int Format { get; set; } = 1;
    public string Batch { get; set; } = "";
    public string Rule { get; set; } = "";
    public string RuleTitle { get; set; } = "";
    public string Category { get; set; } = "";
    public string? UserSid { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public DateTime? RestoredUtc { get; set; }
    public string Status { get; set; } = Applying;
    public long MovedFiles { get; set; }
    public long MovedBytes { get; set; }
    public int SkippedInUse { get; set; }
    public int SkippedErrors { get; set; }
    public IList<QuarantineEntry> Entries { get; init; } = [];
}

/// <summary>Um lote (um clique) com as regras que ele limpou, numa pasta de dados (conta do usuário ou administrador).</summary>
public sealed record QuarantineBatch(string BatchId, string DataDirectory, DateTime CreatedUtc, IReadOnlyList<QuarantineManifest> Rules)
{
    /// <summary>Bytes ainda na quarentena (regras não restauradas).</summary>
    public long BytesInQuarantine => Rules.Where(r => r.Status != QuarantineManifest.Restored).Sum(r => r.MovedBytes);
    public long FilesInQuarantine => Rules.Where(r => r.Status != QuarantineManifest.Restored).Sum(r => r.MovedFiles);
    public bool FullyRestored => Rules.All(r => r.Status == QuarantineManifest.Restored);
}

/// <summary>Leitura da quarentena da limpeza (somente leitura; pode rodar na interface).</summary>
public static class QuarantineStore
{
    public const string ManifestFile = "manifest.json";
    public const string FilesDirectory = "files";

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string CleaningRoot(string dataDirectory) => Path.Combine(dataDirectory, "quarantine", "cleaning");

    public static string RuleDirectory(string dataDirectory, string batch, string ruleId)
    {
        if (!CleaningBatchId.IsValid(batch)) throw new ArgumentException("Id de lote inválido.", nameof(batch));
        return Path.Combine(CleaningRoot(dataDirectory), batch, ruleId);
    }

    public static string QuarantinedFile(string ruleDirectory, int index) =>
        Path.Combine(ruleDirectory, FilesDirectory, index.ToString(CultureInfo.InvariantCulture));

    public static QuarantineManifest? ReadManifest(string ruleDirectory)
    {
        var path = Path.Combine(ruleDirectory, ManifestFile);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Manifesto é um link; recusado.");
        using var s = File.OpenRead(path);
        return JsonSerializer.Deserialize<QuarantineManifest>(s, Json) ?? throw new InvalidDataException("Manifesto vazio.");
    }

    /// <summary>Lotes da pasta de dados, do mais recente ao mais antigo. Manifestos ilegíveis são ignorados.</summary>
    public static IReadOnlyList<QuarantineBatch> ReadBatches(string dataDirectory)
    {
        var root = CleaningRoot(dataDirectory);
        var result = new List<QuarantineBatch>();
        if (!Directory.Exists(root)) return result;
        IEnumerable<string> batches;
        try { batches = Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var dir in batches)
        {
            var id = Path.GetFileName(dir);
            if (!CleaningBatchId.IsValid(id)) continue;
            var rules = new List<QuarantineManifest>();
            try
            {
                foreach (var ruleDir in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        if (ReadManifest(ruleDir) is { } m && m.Batch == id) rules.Add(m);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            // Lote sem nada movido e sem restauração (ex.: tudo em uso) não interessa ao histórico, mas continua listado.
            if (rules.Count > 0)
                result.Add(new QuarantineBatch(id, dataDirectory, rules.Min(r => r.CreatedUtc), rules.OrderBy(r => r.RuleTitle, StringComparer.CurrentCulture).ToList()));
        }
        return result.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    /// <summary>Quantos arquivos do manifesto estão de fato na pasta da quarentena.</summary>
    public static int PresentInQuarantine(string ruleDirectory, QuarantineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Entries.Count(e => File.Exists(QuarantinedFile(ruleDirectory, e.Index)));
    }
}

/// <summary>Operações de arquivo da quarentena (trocáveis nos testes para simular arquivo em uso).</summary>
internal interface IQuarantineFileOps
{
    /// <summary>Outro processo está com o arquivo aberto (abrir sem compartilhamento falha).</summary>
    bool IsInUse(string path);

    /// <summary>Move sem sobrescrever. Outro volume: copia, confere tamanho e hash, só então remove a origem.</summary>
    void MoveFile(string source, string destination);
}

internal sealed class RealQuarantineFileOps : IQuarantineFileOps
{
    public static readonly RealQuarantineFileOps Instance = new();

    public bool IsInUse(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; } // sem permissão de leitura: o move decide (e conta como erro se falhar)
    }

    public void MoveFile(string source, string destination)
    {
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            File.Move(source, destination, overwrite: false);
            return;
        }
        var partial = destination + ".partial";
        if (File.Exists(partial)) File.Delete(partial); // cópia incompleta do próprio NEXUS
        File.Copy(source, partial, overwrite: false);
        if (!QuarantineMover.SameContent(source, partial))
        {
            File.Delete(partial);
            throw new IOException("A cópia para outro volume não confere (tamanho ou hash); o original ficou onde estava.");
        }
        File.Move(partial, destination, overwrite: false);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        File.Delete(source);
    }
}

/// <summary>Resultado de um Apply/Restore de uma regra.</summary>
internal sealed record QuarantineOutcome(long Moved, long Bytes, int InUse, int Errors, int Restored, int Renamed, int Failed);

/// <summary>
/// Move arquivos elegíveis para a quarentena e de volta. Só o handler do aplicador usa (internal: a interface não tem acesso).
/// </summary>
internal sealed class QuarantineMover
{
    private readonly IQuarantineFileOps _ops;
    private readonly TimeProvider _clock;

    public QuarantineMover(IQuarantineFileOps ops, TimeProvider clock)
    {
        _ops = ops;
        _clock = clock;
    }

    public QuarantineOutcome Apply(CleaningRule rule, CleaningFolders folders, string dataDirectory, string batch, string? userSid)
    {
        var ruleDir = QuarantineStore.RuleDirectory(dataDirectory, batch, rule.Id);
        if (File.Exists(Path.Combine(ruleDir, QuarantineStore.ManifestFile)))
            throw new InvalidOperationException("Este lote de limpeza já foi usado; peça uma nova limpeza.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var roots = CleaningPaths.Expand(rule, folders);
        var candidates = CleaningEnumerator.Enumerate(rule, roots, now).ToList();

        EnsureQuarantineDirectories(dataDirectory, ruleDir);
        var manifest = new QuarantineManifest
        {
            Batch = batch, Rule = rule.Id, RuleTitle = rule.Title, Category = rule.Category.ToString(), UserSid = userSid, CreatedUtc = now,
            Entries = candidates.Select((c, i) => new QuarantineEntry { Index = i, Path = c.Path, Size = c.Size, LastWriteUtc = c.LastWriteUtc }).ToList(),
        };
        WriteManifest(ruleDir, manifest); // antes do primeiro arquivo sair

        foreach (var e in manifest.Entries)
        {
            try
            {
                var info = new FileInfo(e.Path);
                if (!info.Exists) { e.State = QuarantineEntryState.SkippedError; e.Note = "sumiu antes de mover"; continue; }
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || !CleaningPaths.IsWithinRule(rule, folders, e.Path))
                {
                    e.State = QuarantineEntryState.SkippedError;
                    e.Note = "fora das raízes da regra ou link";
                    continue;
                }
                if (_ops.IsInUse(e.Path)) { e.State = QuarantineEntryState.SkippedInUse; continue; }
                // Imediatamente antes de mover: nenhuma pasta entre a raiz da regra e o arquivo pode ter virado link/junção
                // (senão o aplicador, talvez como administrador, moveria um arquivo de fora da regra).
                if (CleaningPaths.ReparseInChain(rule, folders, e.Path) is { } link)
                {
                    e.State = QuarantineEntryState.SkippedError;
                    e.Note = Short($"pasta virou link ou junção ({link})");
                    continue;
                }
                _ops.MoveFile(e.Path, QuarantineStore.QuarantinedFile(ruleDir, e.Index));
                e.State = QuarantineEntryState.Moved;
            }
            catch (UnauthorizedAccessException ex) { e.State = QuarantineEntryState.SkippedError; e.Note = Short(ex.Message); }
            catch (IOException ex) { e.State = QuarantineEntryState.SkippedInUse; e.Note = Short(ex.Message); }
        }

        manifest.MovedFiles = manifest.Entries.Count(x => x.State == QuarantineEntryState.Moved);
        manifest.MovedBytes = manifest.Entries.Where(x => x.State == QuarantineEntryState.Moved).Sum(x => x.Size);
        manifest.SkippedInUse = manifest.Entries.Count(x => x.State == QuarantineEntryState.SkippedInUse);
        manifest.SkippedErrors = manifest.Entries.Count(x => x.State == QuarantineEntryState.SkippedError);
        manifest.Status = QuarantineManifest.Applied;
        manifest.FinishedUtc = _clock.GetUtcNow().UtcDateTime;
        WriteManifest(ruleDir, manifest);
        return new QuarantineOutcome(manifest.MovedFiles, manifest.MovedBytes, manifest.SkippedInUse, manifest.SkippedErrors, 0, 0, 0);
    }

    /// <summary>
    /// Devolve cada arquivo que está na quarentena ao caminho original. Nunca sobrescreve: se já existe outro arquivo lá, devolve
    /// com o sufixo " (restaurado)". Só caminhos que a regra permite (revalidados pelo modelo da regra) são aceitos.
    /// </summary>
    public QuarantineOutcome Restore(CleaningRule rule, CleaningFolders folders, string dataDirectory, string batch)
    {
        var ruleDir = QuarantineStore.RuleDirectory(dataDirectory, batch, rule.Id);
        var manifest = QuarantineStore.ReadManifest(ruleDir);
        if (manifest is null) return new QuarantineOutcome(0, 0, 0, 0, 0, 0, 0);
        if (manifest.Rule != rule.Id || manifest.Batch != batch) throw new InvalidDataException("Manifesto não corresponde à regra/lote.");
        EnsureRealChain(dataDirectory, ruleDir);

        int restored = 0, renamed = 0, failed = 0;
        foreach (var e in manifest.Entries)
        {
            var q = QuarantineStore.QuarantinedFile(ruleDir, e.Index);
            try
            {
                if (File.Exists(q + ".partial")) File.Delete(q + ".partial"); // cópia incompleta do próprio NEXUS (a origem ficou intacta)
                var qi = new FileInfo(q);
                if (!qi.Exists) continue;
                if ((qi.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("arquivo da quarentena virou link; recusado");
                if (!CleaningPaths.IsWithinRule(rule, folders, e.Path)) throw new IOException("caminho original fora das raízes da regra; recusado");

                var parent = Path.GetDirectoryName(e.Path)!;
                if (CleaningPaths.ReparseInChain(rule, folders, e.Path) is { } link) throw new IOException($"'{link}' é um link ou junção; recusado");
                EnsureParent(rule, folders, parent);
                var dest = e.Path;
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    if (File.Exists(dest) && SameContent(q, dest))
                    {
                        File.Delete(q); // cópia idêntica (ex.: queda entre copiar e remover a origem em outro volume)
                        e.State = QuarantineEntryState.Restored;
                        e.RestoredTo = dest;
                        restored++;
                        continue;
                    }
                    dest = UniqueName(dest);
                }
                // De novo imediatamente antes de escrever (pastas recriadas acima, ou trocadas nesse meio-tempo).
                if (CleaningPaths.ReparseInChain(rule, folders, dest) is { } late) throw new IOException($"'{late}' é um link ou junção; recusado");
                _ops.MoveFile(q, dest);
                e.State = dest == e.Path ? QuarantineEntryState.Restored : QuarantineEntryState.RestoredRenamed;
                e.RestoredTo = dest;
                if (dest == e.Path) restored++; else renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                e.State = QuarantineEntryState.RestoreFailed;
                e.Note = Short(ex.Message);
                failed++;
            }
        }

        if (QuarantineStore.PresentInQuarantine(ruleDir, manifest) == 0)
        {
            manifest.Status = QuarantineManifest.Restored;
            manifest.RestoredUtc = _clock.GetUtcNow().UtcDateTime;
        }
        WriteManifest(ruleDir, manifest);
        return new QuarantineOutcome(0, 0, 0, 0, restored, renamed, failed);
    }

    internal static string UniqueName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, i == 1 ? $"{name} (restaurado){ext}" : $"{name} (restaurado {i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Não há nome livre para devolver o arquivo sem sobrescrever.");
    }

    /// <summary>Recria pastas que sumiram (ex.: o app apagou a pasta de cache), sem passar por links/junções.</summary>
    private static void EnsureParent(CleaningRule rule, CleaningFolders folders, string parent)
    {
        var missing = new Stack<string>();
        var cur = parent;
        while (!Directory.Exists(cur))
        {
            if (File.Exists(cur)) throw new IOException($"'{cur}' é um arquivo; a pasta original não pode ser recriada.");
            missing.Push(cur);
            cur = Path.GetDirectoryName(cur) ?? throw new IOException("Pasta original sem pai.");
        }
        if (!CleaningPaths.IsRealDirectory(cur)) throw new IOException($"'{cur}' é um link ou junção; recusado.");
        if (CleaningPaths.ForbiddenReason(cur, folders, allowWindowsRoot: true) is { } why && missing.Count > 0) throw new IOException($"'{cur}': {why}.");
        _ = rule;
        while (missing.Count > 0) Directory.CreateDirectory(missing.Pop());
    }

    private static void EnsureQuarantineDirectories(string dataDirectory, string ruleDir)
    {
        Directory.CreateDirectory(Path.Combine(ruleDir, QuarantineStore.FilesDirectory));
        EnsureRealChain(dataDirectory, Path.Combine(ruleDir, QuarantineStore.FilesDirectory));
    }

    /// <summary>Da pasta de dados até a pasta da regra, nenhuma pasta pode ser link/junção (evita desviar as escritas do aplicador).</summary>
    private static void EnsureRealChain(string dataDirectory, string dir)
    {
        var top = CleaningPaths.Normalize(dataDirectory);
        var cur = CleaningPaths.Normalize(dir);
        if (!cur.StartsWith(top, StringComparison.OrdinalIgnoreCase)) throw new IOException("Quarentena fora da pasta de dados.");
        while (cur.Length >= top.Length)
        {
            if (Directory.Exists(cur) && !CleaningPaths.IsRealDirectory(cur)) throw new IOException($"'{cur}' é um link ou junção; quarentena recusada.");
            var parent = Path.GetDirectoryName(cur);
            if (parent is null || parent == cur) break;
            cur = parent;
        }
    }

    private static void WriteManifest(string ruleDir, QuarantineManifest manifest)
    {
        var path = Path.Combine(ruleDir, QuarantineStore.ManifestFile);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, manifest, QuarantineStore.Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    internal static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200];
}
