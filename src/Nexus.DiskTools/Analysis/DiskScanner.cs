using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nexus.DiskTools.Analysis;

/// <summary>
/// Varredura SOMENTE LEITURA de uma unidade ou pasta. Lê só os metadados da listagem (nome, tamanho, data, atributos): não abre
/// arquivos, não altera datas de acesso, não escreve nada.
/// <list type="bullet">
/// <item>Paralela nos primeiros níveis (cada ramo grande numa thread) e cancelável a qualquer momento.</item>
/// <item>Nunca segue links simbólicos nem junções (<see cref="FileAttributes.ReparsePoint"/> com destino): o conteúdo é contado
/// onde ele realmente está, sem laços nem contagem dupla (ex.: "C:\Documents and Settings").</item>
/// <item>Pastas sem permissão são contadas e puladas; o tamanho delas fica como mínimo (<see cref="DirNode.Incomplete"/>).</item>
/// <item>Arquivos só na nuvem (OneDrive sob demanda) não ocupam disco: contados à parte.</item>
/// </list>
/// </summary>
public static class DiskScanner
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN (0x40000) e FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (0x400000): conteúdo não está no disco.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static readonly EnumerationOptions Listing = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0, // inclui ocultos e de sistema (hiberfil.sys, pagefile.sys)
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static Task<DiskScanResult> ScanAsync(string root, DiskScanOptions? options = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancel = default) =>
        Task.Run(() => Scan(root, options, progress, cancel), cancel);

    public static DiskScanResult Scan(string root, DiskScanOptions? options = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        options ??= new DiskScanOptions();
        var dir = new DirectoryInfo(root);
        if (!dir.Exists) throw new DirectoryNotFoundException($"Pasta não encontrada: {root}");

        var state = new ScanState(options, progress, cancel);
        var name = dir.Parent is null ? dir.FullName : dir.Name;
        var rootNode = new DirNode(name, dir.FullName, null);
        var ctx = FileCategories.ContextFor(dir.Name, dir.Parent?.Name, FolderContext.None);
        try
        {
            ScanDirectory(dir, rootNode, ctx, 0, state);
        }
        catch (AggregateException ex) when (cancel.IsCancellationRequested && ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException("Varredura cancelada.", ex, cancel);
        }
        state.Report(dir.FullName, force: true);

        return new DiskScanResult
        {
            Root = rootNode,
            TopFiles = state.TopFiles(),
            Categories = state.Categories(),
            Files = state.Files,
            Directories = state.Directories,
            AccessDenied = state.Denied,
            Unreadable = state.Unreadable,
            ReparsePointsSkipped = state.Reparse,
            CloudOnlyBytes = state.CloudBytes,
            CloudOnlyFiles = state.CloudFiles,
            DeniedSamples = state.DeniedSamples.Take(20).ToList(),
            Elapsed = state.Clock.Elapsed,
        };
    }

    private static void ScanDirectory(DirectoryInfo dir, DirNode node, FolderContext ctx, int depth, ScanState s)
    {
        s.Cancel.ThrowIfCancellationRequested();
        Interlocked.Increment(ref s.Directories);
        var subdirs = new List<DirectoryInfo>();
        var largest = new List<(FileInfo Info, long Length)>(s.Options.FilesPerDirectory + 1);
        long own = 0, ownCount = 0, old = 0, cloud = 0, sinceReport = 0;
        var oldLimit = s.Options.NowUtc - s.Options.OldFileAge;

        try
        {
            var entries = s.Options.EnumerateOverride?.Invoke(dir.FullName) ?? dir.EnumerateFileSystemInfos("*", Listing);
            foreach (var info in entries)
            {
                var attrs = info.Attributes;
                if (info is DirectoryInfo d)
                {
                    if ((attrs & FileAttributes.ReparsePoint) != 0 && IsLink(d))
                    {
                        Interlocked.Increment(ref s.Reparse);
                        continue;
                    }
                    subdirs.Add(d);
                    continue;
                }
                if (info is not FileInfo f) continue;
                if ((attrs & FileAttributes.ReparsePoint) != 0 && IsLink(f))
                {
                    Interlocked.Increment(ref s.Reparse); // link simbólico de arquivo: o alvo é contado onde está
                    continue;
                }
                long length;
                try { length = f.Length; }
                catch (IOException) { Interlocked.Increment(ref s.Unreadable); continue; }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref s.Unreadable); continue; }

                if ((attrs & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
                {
                    cloud += length;
                    Interlocked.Increment(ref s.CloudFiles);
                    continue;
                }

                own += length;
                ownCount++;
                var lastWrite = f.LastWriteTimeUtc;
                if (lastWrite < oldLimit) old += length;
                var category = FileCategories.Classify(f.Name, ctx);
                s.AddCategory(category, length);
                s.OfferTop(f, length, lastWrite, category);
                KeepLargest(largest, f, length, s.Options.FilesPerDirectory);
                if (++sinceReport >= 512)
                {
                    Interlocked.Add(ref s.Files, sinceReport);
                    sinceReport = 0;
                    s.Report(dir.FullName);
                }
                if ((ownCount & 0x3FF) == 0) s.Cancel.ThrowIfCancellationRequested();
            }
        }
        catch (UnauthorizedAccessException) { node.Incomplete = true; s.AddDenied(dir.FullName); }
        catch (System.Security.SecurityException) { node.Incomplete = true; s.AddDenied(dir.FullName); }
        catch (DirectoryNotFoundException) { node.Incomplete = true; Interlocked.Increment(ref s.Unreadable); } // removida durante a varredura
        catch (IOException) { node.Incomplete = true; Interlocked.Increment(ref s.Unreadable); } // caminho longo, dispositivo não pronto...
        Interlocked.Add(ref s.Files, sinceReport);
        Interlocked.Add(ref s.Bytes, own);
        Interlocked.Add(ref s.CloudBytes, cloud);

        var children = subdirs.Select(d => new DirNode(d.Name, d.FullName, node)).ToArray();
        if (depth + 1 > s.Options.MaxDepth)
        {
            node.Incomplete |= children.Length > 0;
            Interlocked.Add(ref s.Unreadable, children.Length);
            children = [];
        }
        else if (depth < s.Options.ParallelDepth && children.Length > 1)
        {
            Parallel.For(0, children.Length, new ParallelOptions { CancellationToken = s.Cancel, MaxDegreeOfParallelism = s.Options.MaxDegreeOfParallelism },
                i => ScanDirectory(subdirs[i], children[i], FileCategories.ContextFor(subdirs[i].Name, dir.Name, ctx), depth + 1, s));
        }
        else
        {
            for (var i = 0; i < children.Length; i++)
                ScanDirectory(subdirs[i], children[i], FileCategories.ContextFor(subdirs[i].Name, dir.Name, ctx), depth + 1, s);
        }

        node.OwnFilesSize = own;
        node.OwnFileCount = ownCount;
        node.Size = own + children.Sum(c => c.Size);
        node.FileCount = ownCount + children.Sum(c => c.FileCount);
        node.OldBytes = old + children.Sum(c => c.OldBytes);
        node.CloudOnlyBytes = cloud + children.Sum(c => c.CloudOnlyBytes);
        node.Incomplete |= children.Any(c => c.Incomplete);
        node.Children = children.OrderByDescending(c => c.Size).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        node.LargestFiles = largest.OrderByDescending(x => x.Length)
            .Select(x => new FileItem(x.Info.Name, x.Info.FullName, x.Length, x.Info.LastWriteTimeUtc, FileCategories.Classify(x.Info.Name, ctx)))
            .ToList();
    }

    /// <summary>Link simbólico ou junção (tem destino). Outros reparse points (arquivos de nuvem, deduplicação) são conteúdo real.</summary>
    private static bool IsLink(FileSystemInfo info)
    {
        try { return info.LinkTarget is not null; }
        catch (IOException) { return true; }               // na dúvida, não segue
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void KeepLargest(List<(FileInfo Info, long Length)> list, FileInfo f, long length, int max)
    {
        if (max <= 0) return;
        if (list.Count < max) { list.Add((f, length)); return; }
        var minIndex = 0;
        for (var i = 1; i < list.Count; i++) if (list[i].Length < list[minIndex].Length) minIndex = i;
        if (length > list[minIndex].Length) list[minIndex] = (f, length);
    }

    private sealed class ScanState(DiskScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancel)
    {
        public readonly DiskScanOptions Options = options;
        public readonly CancellationToken Cancel = cancel;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public long Files, Directories, Bytes, CloudBytes, CloudFiles;
        public int Denied, Unreadable, Reparse;
        public readonly ConcurrentQueue<string> DeniedSamples = new();
        private readonly long[] _categoryBytes = new long[Enum.GetValues<FileCategory>().Length];
        private readonly long[] _categoryFiles = new long[Enum.GetValues<FileCategory>().Length];
        private readonly PriorityQueue<FileItem, long> _top = new();
        private readonly Lock _topLock = new();
        private long _topThreshold = -1;
        private long _lastReportMs = -1000;

        public void AddCategory(FileCategory c, long bytes)
        {
            Interlocked.Add(ref _categoryBytes[(int)c], bytes);
            Interlocked.Increment(ref _categoryFiles[(int)c]);
        }

        public void AddDenied(string path)
        {
            if (Interlocked.Increment(ref Denied) <= 20) DeniedSamples.Enqueue(path);
        }

        public void OfferTop(FileInfo f, long length, DateTime lastWrite, FileCategory category)
        {
            if (Options.TopFiles <= 0 || length <= Volatile.Read(ref _topThreshold)) return;
            lock (_topLock)
            {
                if (_top.Count >= Options.TopFiles && length <= _top.Peek().Size) return;
                _top.Enqueue(new FileItem(f.Name, f.FullName, length, lastWrite, category), length);
                if (_top.Count > Options.TopFiles) _top.Dequeue();
                if (_top.Count >= Options.TopFiles) Volatile.Write(ref _topThreshold, _top.Peek().Size);
            }
        }

        public List<FileItem> TopFiles()
        {
            lock (_topLock) return _top.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Size).ThenBy(x => x.FullPath, StringComparer.Ordinal).ToList();
        }

        public List<CategoryTotal> Categories() =>
            Enum.GetValues<FileCategory>()
                .Select(c => new CategoryTotal(c, FileCategories.Label(c), Interlocked.Read(ref _categoryBytes[(int)c]), Interlocked.Read(ref _categoryFiles[(int)c])))
                .Where(c => c.Files > 0)
                .OrderByDescending(c => c.Bytes)
                .ToList();

        public void Report(string current, bool force = false)
        {
            if (progress is null) return;
            var now = Clock.ElapsedMilliseconds;
            var last = Interlocked.Read(ref _lastReportMs);
            if (!force && (now - last < 150 || Interlocked.CompareExchange(ref _lastReportMs, now, last) != last)) return;
            progress.Report(new ScanProgress(Interlocked.Read(ref Files), Interlocked.Read(ref Directories), Interlocked.Read(ref Bytes),
                Volatile.Read(ref Denied), current, Clock.Elapsed));
        }
    }
}
