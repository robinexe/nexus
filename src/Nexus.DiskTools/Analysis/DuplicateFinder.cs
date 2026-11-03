using System.Security.Cryptography;

namespace Nexus.DiskTools.Analysis;

/// <summary>Um arquivo candidato a duplicado.</summary>
public sealed record DupFile(string Path, long Size, DateTime LastWriteUtc);

/// <summary>Cópias com o mesmo tamanho e o mesmo SHA-256 (conteúdo idêntico).</summary>
public sealed record DuplicateGroup(long Size, string Sha256, IReadOnlyList<DupFile> Files)
{
    /// <summary>Espaço que sobra se ficar só uma cópia.</summary>
    public long Wasted => Size * Math.Max(0, Files.Count - 1);
}

public sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, int FilesSeen, int SkippedFolders, int Unreadable, bool Cancelled)
{
    public long Wasted => Groups.Sum(g => g.Wasted);
}

/// <summary>Andamento: arquivos vistos na listagem, e bytes conferidos por hash de quantos.</summary>
public sealed record DuplicateProgress(int FilesSeen, long HashedBytes, long BytesToHash, string Stage);

/// <summary>De onde vêm os arquivos (no PC: <see cref="FileSystemDuplicateSource"/>; testes: pastas temporárias).</summary>
public interface IDuplicateFileSource
{
    /// <summary>Arquivos e subpastas de uma pasta (sem seguir links: o filtro de links é da fonte).</summary>
    (IReadOnlyList<DupFile> Files, IReadOnlyList<string> Folders) List(string folder);
    Stream OpenRead(string path);
}

/// <summary>
/// Arquivos duplicados: agrupa por tamanho, depois pelo SHA-256 dos primeiros 64 KB e só então pelo SHA-256 do arquivo inteiro
/// (lido em blocos, sem carregar na memória). Pastas do sistema, de programas, de jogos e de dados de aplicativos ficam fora:
/// lá uma "cópia" quase sempre é usada por alguém. Só leitura: mover a cópia escolhida vai pelo aplicador (quarentena com desfazer).
/// </summary>
public static class DuplicateFinder
{
    public const long DefaultMinSize = 1024 * 1024;
    public const int PrefixBytes = 64 * 1024;
    public const int MaxGroups = 500;

    /// <summary>Nomes de pasta que nunca entram (em qualquer lugar da árvore).</summary>
    private static readonly HashSet<string> SkippedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin", "System Volume Information", "Recovery",
        "Windows.old", "$WINDOWS.~BT", "$Windows.~WS", "$SysReset", "PerfLogs", "MSOCache", "Config.Msi", "OneDriveTemp",
        "steamapps", "SteamLibrary", "Epic Games", "XboxGames", "WindowsApps", "Riot Games", "GOG Games", "EA Games", "Ubisoft Game Launcher",
        "Battle.net", "node_modules", ".git", ".svn", "__pycache__", ".nuget", ".gradle", ".m2", "packages",
    };

    /// <summary>Esta pasta (pelo nome) fica fora da busca?</summary>
    public static bool IsSkippedFolder(string path)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path ?? ""));
        return name.Length > 0 && SkippedFolderNames.Contains(name);
    }

    public static DuplicateScanResult Find(IDuplicateFileSource source, IEnumerable<string> roots, long minSize = DefaultMinSize,
        IProgress<DuplicateProgress>? progress = null, IEnumerable<string>? excludedRoots = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(roots);
        var excluded = (excludedRoots ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => System.IO.Path.TrimEndingDirectorySeparator(r)).ToList();
        var files = new List<DupFile>();
        var skipped = 0;
        var unreadable = 0;
        var pending = new Stack<string>(roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase));
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastReport = DateTime.UtcNow;
        try
        {
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var folder = pending.Pop();
                if (!seenFolders.Add(folder)) continue;
                (IReadOnlyList<DupFile> Files, IReadOnlyList<string> Folders) listing;
                try { listing = source.List(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { unreadable++; continue; }
                files.AddRange(listing.Files.Where(f => f.Size >= minSize));
                foreach (var sub in listing.Folders)
                {
                    if (IsSkippedFolder(sub) || excluded.Any(e => Under(sub, e))) { skipped++; continue; }
                    pending.Push(sub);
                }
                if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new DuplicateProgress(files.Count, 0, 0, "list"));
                }
            }

            var bySize = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList();
            var toHash = bySize.Sum(g => g.Key * (long)g.Count());
            long hashed = 0;
            var groups = new List<DuplicateGroup>();
            foreach (var sizeGroup in bySize.OrderByDescending(g => g.Key))
            {
                cancel.ThrowIfCancellationRequested();
                // 1) primeiros 64 KB (descarta rápido arquivos do mesmo tamanho com começo diferente)
                var byPrefix = new Dictionary<string, List<DupFile>>(StringComparer.Ordinal);
                foreach (var f in sizeGroup)
                {
                    var h = Hash(source, f.Path, PrefixBytes, cancel);
                    if (h is null) { unreadable++; continue; }
                    (byPrefix.TryGetValue(h, out var l) ? l : byPrefix[h] = []).Add(f);
                }
                // 2) arquivo inteiro, só onde o começo bateu
                foreach (var candidates in byPrefix.Values.Where(l => l.Count > 1))
                {
                    var byFull = new Dictionary<string, List<DupFile>>(StringComparer.Ordinal);
                    foreach (var f in candidates)
                    {
                        // Arquivo de até 64 KB: o hash do começo já é o do arquivo inteiro.
                        var h = sizeGroup.Key <= PrefixBytes ? byPrefix.First(kv => kv.Value == candidates).Key : Hash(source, f.Path, long.MaxValue, cancel);
                        hashed += sizeGroup.Key;
                        if (h is null) { unreadable++; continue; }
                        (byFull.TryGetValue(h, out var l) ? l : byFull[h] = []).Add(f);
                        progress?.Report(new DuplicateProgress(files.Count, hashed, toHash, "hash"));
                    }
                    foreach (var (sha, same) in byFull.Where(kv => kv.Value.Count > 1))
                        groups.Add(new DuplicateGroup(sizeGroup.Key, sha, [.. same.OrderBy(f => f.LastWriteUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)]));
                }
                if (groups.Count >= MaxGroups) break;
            }
            return new DuplicateScanResult([.. groups.OrderByDescending(g => g.Wasted)], files.Count, skipped, unreadable, false);
        }
        catch (OperationCanceledException)
        {
            return new DuplicateScanResult([], files.Count, skipped, unreadable, true);
        }
    }

    /// <summary>SHA-256 (hex minúsculo) dos primeiros <paramref name="maxBytes"/> bytes. Null se não deu para ler (em uso, sem acesso).</summary>
    public static string? Hash(IDuplicateFileSource source, string path, long maxBytes, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            using var stream = source.OpenRead(path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (total < maxBytes)
            {
                cancel.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, maxBytes - total);
                var n = stream.Read(buffer, 0, want);
                if (n <= 0) break;
                sha.AppendData(buffer, 0, n);
                total += n;
            }
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Cópias que sobram de cada grupo, mantendo a mais antiga (a "original"): o que "Selecionar cópias extras" marca.</summary>
    public static IReadOnlyList<DupFile> ExtraCopies(DuplicateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return [.. group.Files.Skip(1)];
    }

    /// <summary>Uma seleção nunca pode levar todas as cópias de um grupo (sempre fica pelo menos uma no lugar).</summary>
    public static bool KeepsOne(DuplicateGroup group, IReadOnlySet<string> selectedPaths)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        return group.Files.Any(f => !selectedPaths.Contains(f.Path));
    }

    private static bool Under(string path, string root)
    {
        var p = System.IO.Path.TrimEndingDirectorySeparator(path);
        return p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Fonte real: lista com <see cref="DirectoryInfo"/> sem seguir links/junções (ReparsePoint pulado), sem arquivos de sistema
/// ocultos e sem arquivos só na nuvem (abrir faria o OneDrive baixar). Abre para leitura compartilhada (não bloqueia quem usa).
/// </summary>
public sealed class FileSystemDuplicateSource : IDuplicateFileSource
{
    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | FileAttributes.Offline;

    public (IReadOnlyList<DupFile> Files, IReadOnlyList<string> Folders) List(string folder)
    {
        var dir = new DirectoryInfo(folder);
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return ([], []);
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        var files = new List<DupFile>();
        var folders = new List<string>();
        foreach (var e in dir.EnumerateFileSystemInfos("*", options))
        {
            if (e is DirectoryInfo d)
            {
                if ((d.Attributes & (FileAttributes.System | FileAttributes.Hidden)) == (FileAttributes.System | FileAttributes.Hidden)) continue;
                folders.Add(d.FullName);
            }
            else if (e is FileInfo f)
            {
                if ((f.Attributes & FileAttributes.System) != 0 || (f.Attributes & CloudOnly) != 0) continue;
                files.Add(new DupFile(f.FullName, f.Length, f.LastWriteTimeUtc));
            }
        }
        return (files, folders);
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0, Options = FileOptions.SequentialScan });
}
