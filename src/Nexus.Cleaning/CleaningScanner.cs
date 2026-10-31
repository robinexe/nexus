using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nexus.Cleaning;

/// <summary>Arquivo elegível: dentro de uma raiz, casa o padrão, não excluído, mais velho que a idade mínima, sem link/junção.</summary>
public sealed record CleaningCandidate(string Path, long Size, DateTime LastWriteUtc);

/// <summary>Contadores do que a varredura deixou de fora.</summary>
public sealed class CleaningEnumerationStats
{
    private int _reparse, _inaccessible, _tooNew, _excluded;
    public int ReparseSkipped => _reparse;
    public int Inaccessible => _inaccessible;
    public int TooNew => _tooNew;
    public int Excluded => _excluded;
    internal void Reparse() => Interlocked.Increment(ref _reparse);
    internal void Denied() => Interlocked.Increment(ref _inaccessible);
    internal void New() => Interlocked.Increment(ref _tooNew);
    internal void Skip() => Interlocked.Increment(ref _excluded);
}

/// <summary>Enumeração somente leitura dos arquivos elegíveis de uma regra. Nunca segue links simbólicos ou junções.</summary>
public static class CleaningEnumerator
{
    /// <summary>Teto por regra (o manifesto precisa caber com folga em disco e memória).</summary>
    public const int MaxFilesPerRule = 200_000;

    public static IEnumerable<CleaningCandidate> Enumerate(CleaningRule rule, IReadOnlyList<ResolvedRoot> roots, DateTime nowUtc,
        CleaningEnumerationStats? stats = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(roots);
        stats ??= new CleaningEnumerationStats();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var root in roots)
        {
            var stack = new Stack<string>();
            stack.Push(root.Path);
            while (stack.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                List<FileSystemInfo> entries;
                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { stats.Reparse(); continue; }
                    entries = info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    stats.Denied();
                    continue;
                }
                foreach (var e in entries)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) { stats.Reparse(); continue; }
                    if (e is DirectoryInfo d)
                    {
                        if (!root.Root.Recursive) continue;
                        if (rule.ExcludeDirectories.Any(x => CleaningPaths.SegmentMatches(x, d.Name))) { stats.Skip(); continue; }
                        stack.Push(d.FullName);
                        continue;
                    }
                    if (e is not FileInfo f) continue;
                    if (!root.Root.Patterns.Any(p => CleaningPaths.SegmentMatches(p, f.Name))) continue;
                    if (rule.Exclude.Any(x => CleaningPaths.SegmentMatches(x, f.Name))) { stats.Skip(); continue; }
                    DateTime lastWrite;
                    long size;
                    try { lastWrite = f.LastWriteTimeUtc; size = f.Length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stats.Denied(); continue; }
                    // Data no futuro (relógio errado, arquivo de outra máquina) nunca conta como "velho".
                    if (nowUtc - lastWrite < rule.MinAge || (lastWrite > nowUtc.AddMinutes(5) && rule.MinAgeHours > 0)) { stats.New(); continue; }
                    if (!seen.Add(f.FullName)) continue;
                    yield return new CleaningCandidate(f.FullName, size, lastWrite);
                    if (++count >= MaxFilesPerRule) yield break;
                }
            }
        }
    }
}

/// <summary>Saber se um programa está aberto (para pular o cache dele e avisar).</summary>
public interface IProcessProbe
{
    bool IsRunning(string processName);
}

/// <summary>Leitura da lista de processos do Windows (sem abrir nem alterar processos).</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public static readonly SystemProcessProbe Instance = new();

    public bool IsRunning(string processName)
    {
        try
        {
            var found = Process.GetProcessesByName(processName);
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return false; }
    }
}

/// <summary>Resultado da medição de uma regra.</summary>
public sealed record RuleScanResult(
    CleaningRule Rule,
    long Files,
    long Bytes,
    int RootsFound,
    int Inaccessible,
    int ReparseSkipped,
    IReadOnlyList<string> RunningApps,
    string? Problem)
{
    public bool Measured => Problem is null;
}

/// <summary>Varredura rápida, paralela, cancelável e somente leitura. Pode rodar na interface.</summary>
public static class CleaningScanner
{
    /// <param name="recycleBin">Leitura da Lixeira (arquivos, bytes), ou null quando indisponível.</param>
    public static async Task<IReadOnlyList<RuleScanResult>> ScanAsync(IEnumerable<CleaningRule> rules, CleaningFolders folders, IProcessProbe processes,
        DateTime nowUtc, Func<(long Files, long Bytes)?>? recycleBin = null, int parallelism = 4, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(processes);
        var list = rules.ToList();
        var results = new ConcurrentDictionary<string, RuleScanResult>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancel }, (rule, ct) =>
        {
            results[rule.Id] = ScanOne(rule, folders, processes, nowUtc, recycleBin, ct);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return list.Select(r => results[r.Id]).ToList();
    }

    public static RuleScanResult ScanOne(CleaningRule rule, CleaningFolders folders, IProcessProbe processes, DateTime nowUtc,
        Func<(long Files, long Bytes)?>? recycleBin = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(processes);
        var running = rule.CloseApps.Where(processes.IsRunning).ToList();
        if (rule.Special == "RecycleBin")
        {
            var rb = recycleBin?.Invoke();
            return rb is { } v ? new RuleScanResult(rule, v.Files, v.Bytes, 1, 0, 0, running, null)
                               : new RuleScanResult(rule, 0, 0, 0, 0, 0, running, "Não foi possível ler a Lixeira.");
        }
        try
        {
            var problems = new List<string>();
            var roots = CleaningPaths.Expand(rule, folders, problems);
            var stats = new CleaningEnumerationStats();
            long files = 0, bytes = 0;
            foreach (var c in CleaningEnumerator.Enumerate(rule, roots, nowUtc, stats, cancel))
            {
                files++;
                bytes += c.Size;
            }
            string? problem = null;
            if (roots.Count == 0 && problems.Count > 0) problem = problems[0];
            else if (roots.Count > 0 && files == 0 && stats.Inaccessible > 0 && rule.Admin) problem = "Precisa de administrador para medir.";
            return new RuleScanResult(rule, files, bytes, roots.Count, stats.Inaccessible, stats.ReparseSkipped, running, problem);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return new RuleScanResult(rule, 0, 0, 0, 0, 0, running, ex.Message);
        }
    }
}
