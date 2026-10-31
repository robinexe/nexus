using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexus.Programs.Leftovers;

/// <summary>Alvo de uma remoção de sobra, como o catálogo o instancia (kind/scope/path, e o SID quando o aplicador é serviço).</summary>
public sealed record LeftoverTarget(bool IsRegistry, bool Machine, string Path, string? UserSid)
{
    public string Kind => IsRegistry ? "registry" : "file";
    public string Scope => Machine ? "HKLM" : "HKCU";
    public string Describe() => IsRegistry ? $@"{Scope}\{Path}" : Path;
}

public sealed record FileHash(string RelativePath, long Size, string Sha256);

/// <summary>
/// Manifesto gravado ANTES de mover/remover: o que era, onde estava, a lista de arquivos com tamanho e SHA-256 (ou a árvore
/// do registro com tipos). <see cref="State"/> = "pending" até a remoção terminar, depois "done".
/// </summary>
public sealed record LeftoverManifest(
    string Format,
    string Kind,
    string Scope,
    string? UserSid,
    string OriginalPath,
    DateTimeOffset QuarantinedUtc,
    bool IsDirectory,
    long Bytes,
    IReadOnlyList<FileHash>? Files,
    RegKeyTree? Registry,
    string State)
{
    public const string CurrentFormat = "nexus.programs.leftover/1";

    public bool Matches(LeftoverTarget t) =>
        Format == CurrentFormat && Kind == t.Kind && Scope == t.Scope && string.Equals(UserSid, t.UserSid, StringComparison.Ordinal) &&
        string.Equals(OriginalPath, t.Path, StringComparison.Ordinal);
}

/// <summary>
/// Quarentena de sobras em <c>DataDirectory\quarantine\programs\&lt;id&gt;\</c> (<c>manifest.json</c> + <c>payload</c>). No mesmo
/// volume, mover é renomear (atômico; arquivo em uso faz o Windows recusar e nada muda). Em outro volume: copia, confere
/// tamanho + SHA-256 de cada arquivo, e só então apaga a origem.
/// </summary>
public sealed class LeftoverQuarantine
{
    public const int MaxFiles = 20_000;
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _root;

    public LeftoverQuarantine(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _root = Path.Combine(dataDirectory, "quarantine", "programs");
    }

    public string Root => _root;

    /// <summary>Força o caminho de "outro volume" (testes).</summary>
    public bool ForceCrossVolume { get; init; }

    public static string IdOf(LeftoverTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{t.Kind}|{t.Scope}|{t.UserSid}|{t.Path.ToUpperInvariant()}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    public string FolderOf(LeftoverTarget t) => Path.Combine(_root, IdOf(t));
    public string ManifestPath(LeftoverTarget t) => Path.Combine(FolderOf(t), "manifest.json");
    public string PayloadPath(LeftoverTarget t) => Path.Combine(FolderOf(t), "payload");

    public LeftoverManifest? ReadManifest(LeftoverTarget t)
    {
        var path = ManifestPath(t);
        if (!File.Exists(path)) return null;
        var m = JsonSerializer.Deserialize<LeftoverManifest>(File.ReadAllText(path), Json);
        return m is not null && m.Matches(t) ? m : null;
    }

    public bool PayloadExists(LeftoverTarget t) => File.Exists(PayloadPath(t)) || Directory.Exists(PayloadPath(t));

    public void WriteManifest(LeftoverTarget t, LeftoverManifest m)
    {
        Directory.CreateDirectory(FolderOf(t));
        var final = ManifestPath(t);
        var tmp = final + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, m, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, final, overwrite: true);
    }

    public void Discard(LeftoverTarget t)
    {
        var folder = FolderOf(t);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    // ------------------------------------------------------------------ arquivos

    /// <summary>Lista arquivos com tamanho e SHA-256. Recusa links/junções dentro e conteúdo grande demais.</summary>
    public static IReadOnlyList<FileHash> Inventory(string path, out bool isDirectory)
    {
        isDirectory = Directory.Exists(path);
        if (!isDirectory)
        {
            var fi = new FileInfo(path);
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("O alvo é um link: o NEXUS não segue nem move links.");
            return [new FileHash("", fi.Length, Hash(path))];
        }
        var list = new List<FileHash>();
        long total = 0;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 }))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Há um link/junção dentro ({entry.FullName}): o NEXUS não move pastas com links. Nada foi alterado.");
            if (entry is not FileInfo f) continue;
            total += f.Length;
            if (list.Count >= MaxFiles || total > MaxBytes)
                throw new IOException($"Grande demais para uma quarentena verificada (mais de {MaxFiles} arquivos ou {MaxBytes / (1024 * 1024 * 1024)} GB). Nada foi alterado.");
            list.Add(new FileHash(Path.GetRelativePath(path, f.FullName), f.Length, Hash(f.FullName)));
        }
        return list;
    }

    private static string Hash(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>Move a pasta/arquivo para a quarentena. Manifesto (pendente) primeiro; "done" no fim.</summary>
    public void QuarantinePath(LeftoverTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var payload = PayloadPath(t);
        if (PayloadExists(t))
        {
            // Sobrou de uma tentativa interrompida com a origem intacta: a cópia parcial é descartada (a origem é a verdade).
            var stale = ReadManifest(t);
            if (stale is null || stale.State != "pending") throw new IOException("Já existe uma quarentena para este alvo; restaure-a antes.");
            DeletePath(payload);
        }
        var files = Inventory(t.Path, out var isDir);
        var manifest = new LeftoverManifest(LeftoverManifest.CurrentFormat, t.Kind, t.Scope, t.UserSid, t.Path, DateTimeOffset.UtcNow, isDir,
            files.Sum(f => f.Size), files, null, "pending");
        WriteManifest(t, manifest);
        MovePath(t.Path, payload, isDir, files);
        WriteManifest(t, manifest with { State = "done" });
    }

    /// <summary>Confere que a quarentena está íntegra: mesmos arquivos, tamanhos e SHA-256 do manifesto, sem links. null = ok.</summary>
    public string? VerifyPayload(LeftoverTarget t, LeftoverManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.Files is null) return "Manifesto sem lista de arquivos.";
        var payload = PayloadPath(t);
        if (!PayloadExists(t)) return "A quarentena não tem mais o conteúdo.";
        if (m.IsDirectory != Directory.Exists(payload)) return "Tipo do conteúdo em quarentena difere do manifesto.";
        IReadOnlyList<FileHash> now;
        try { now = Inventory(payload, out _); }
        catch (IOException ex) { return ex.Message; }
        if (now.Count != m.Files.Count) return "A quarentena foi alterada (número de arquivos difere do manifesto).";
        var expected = m.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var f in now)
            if (!expected.TryGetValue(f.RelativePath, out var e) || e.Size != f.Size || !string.Equals(e.Sha256, f.Sha256, StringComparison.Ordinal))
                return $"A quarentena foi alterada ({(f.RelativePath.Length == 0 ? "arquivo" : f.RelativePath)} difere do manifesto).";
        return null;
    }

    /// <summary>Devolve ao caminho original; se algo novo ocupa o lugar, restaura ao lado com sufixo (nunca sobrescreve).</summary>
    public string RestorePath(LeftoverTarget t, LeftoverManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var dest = AvailableName(t.Path);
        var parent = Path.GetDirectoryName(dest);
        if (parent is not null) Directory.CreateDirectory(parent);
        MovePath(PayloadPath(t), dest, m.IsDirectory, m.Files ?? []);
        Discard(t);
        return dest;
    }

    public static string AvailableName(string original)
    {
        if (!File.Exists(original) && !Directory.Exists(original)) return original;
        for (var i = 1; i < 1000; i++)
        {
            var candidate = original + (i == 1 ? " (restaurado pelo NEXUS)" : $" (restaurado pelo NEXUS {i})");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Não há nome livre para restaurar sem sobrescrever.");
    }

    private void MovePath(string from, string to, bool isDir, IReadOnlyList<FileHash> files)
    {
        var sameVolume = !ForceCrossVolume && string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            if (isDir) Directory.Move(from, to);
            else File.Move(from, to);
            return;
        }
        // Outro volume: copiar tudo, conferir cada arquivo, só então apagar a origem.
        try
        {
            if (isDir)
            {
                foreach (var f in files)
                {
                    var target = Path.Combine(to, f.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(from, f.RelativePath), target, overwrite: false);
                }
                foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
                if (Directory.Exists(from) && !Directory.Exists(to)) Directory.CreateDirectory(to);
            }
            else
            {
                File.Copy(from, to, overwrite: false);
            }
            foreach (var f in files)
            {
                var copy = f.RelativePath.Length == 0 ? to : Path.Combine(to, f.RelativePath);
                var fi = new FileInfo(copy);
                if (!fi.Exists || fi.Length != f.Size || !string.Equals(Hash(copy), f.Sha256, StringComparison.Ordinal))
                    throw new IOException($"Cópia não conferiu ({copy}). A origem foi mantida.");
            }
        }
        catch
        {
            DeletePath(to); // cópia parcial: a origem continua intacta.
            throw;
        }
        DeletePath(from);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}
