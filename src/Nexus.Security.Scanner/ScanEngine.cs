using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Nexus.Security.Scanner;

/// <summary>Verificação de assinatura digital (Windows: WinVerifyTrust sem rede). null = não verificado/indeterminado.</summary>
public interface ISignatureChecker
{
    bool? IsSigned(string path);
}

public sealed class NoSignatureChecker : ISignatureChecker
{
    public static readonly NoSignatureChecker Instance = new();
    public bool? IsSigned(string path) => null;
}

/// <summary>Reaproveita <see cref="Authenticode.IsSigned"/> do módulo Segurança (sem rede, sem interface).</summary>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeSignatureChecker : ISignatureChecker
{
    public bool? IsSigned(string path) => Authenticode.IsSigned(path);
}

public sealed class ScanOptions
{
    /// <summary>Arquivos até este tamanho são lidos inteiros (hash + regras + PE).</summary>
    public long MaxContentBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Acima disto, só heurística de nome (sem hash): ler 4 GB para um hash deixaria a verificação lenta demais.</summary>
    public long MaxHashBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>Lê o conteúdo de qualquer arquivo pequeno (pega o EICAR em .txt), não só os tipos executáveis/documentos.</summary>
    public bool ScanSmallFiles { get; init; } = true;
    public long SmallFileBytes { get; init; } = 4096;
    /// <summary>Lê o começo dos arquivos de tipo desconhecido para achar executáveis renomeados (verificação personalizada).</summary>
    public bool SniffUnknownForPe { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
    public int MaxDepth { get; init; } = 48;
    /// <summary>Pastas nunca percorridas (caminho completo): quarentena do NEXUS, WinSxS etc.</summary>
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];
    public string? WindowsDirectory { get; init; } = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : null;
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// Motor de verificação SOMENTE LEITURA: lista arquivos sem seguir links/junções (nem arquivos só na nuvem, que forçariam
/// download), lê cada arquivo uma vez (compartilhando com quem estiver escrevendo), calcula SHA-256 e passa pelas três
/// camadas (lista de hashes, regras de padrão, heurística de PE/nome). Paralelo e cancelável. Nunca altera nada.
/// </summary>
public sealed class ScanEngine
{
    /// <summary>Tipos cujo conteúdo é sempre lido.</summary>
    public static readonly IReadOnlySet<string> ContentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".scr", ".com", ".pif", ".cpl", ".sys", ".ocx", ".drv", ".efi", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".ps1", ".psm1", ".psd1", ".msi", ".jar", ".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps",
    };

    /// <summary>Atributos que fazem o arquivo/pasta ser pulado: link/junção/arquivo de nuvem e arquivos que não estão no disco.</summary>
    public const FileAttributes SkipAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ |
                                                 (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    private const int PrefixBytes = 1024 * 1024;
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase) { "System Volume Information", "WinSxS" };

    private readonly HashDatabase _hashes;
    private readonly RuleSet _rules;
    private readonly ISignatureChecker _signatures;
    private readonly ScanOptions _options;
    private readonly IgnoreList? _ignore;

    public ScanEngine(HashDatabase hashes, RuleSet rules, ISignatureChecker signatures, ScanOptions? options = null, IgnoreList? ignore = null)
    {
        _hashes = hashes ?? throw new ArgumentNullException(nameof(hashes));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        _options = options ?? new ScanOptions();
        _ignore = ignore;
    }

    public ScanOptions Options => _options;

    // ------------------------------------------------------------------ avaliação de um conteúdo (puro, testável)

    /// <param name="content">Conteúdo lido (inteiro, ou o começo do arquivo quando <paramref name="complete"/> = false).</param>
    /// <param name="zone">Zona da pasta (null = deduzida do caminho).</param>
    public FileVerdict Evaluate(string path, ReadOnlyMemory<byte> content, bool complete, long size, string? sha256, DateTime createdUtc, PathZone? zone = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var now = _options.UtcNow();
        var hits = new List<ScanHit>();
        var fileName = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        hits.AddRange(NameHeuristics.Analyze(fileName));

        var hash = _hashes.Lookup(sha256);
        if (hash is not null)
            hits.Add(new ScanHit(ScanLayer.Hash, "hash.match", hash.Test ? "Hash de um arquivo de teste" : "SHA-256 está na sua lista de hashes",
                hash.Test
                    ? $"O SHA-256 é o de \"{hash.Label}\", do conjunto de teste do NEXUS. Não é ameaça: serve para conferir que a verificação funciona."
                    : $"O SHA-256 deste arquivo aparece na lista \"{hash.Source}\" com o rótulo \"{hash.Label}\". O hash identifica o arquivo byte a byte; a lista é sua (importada), então confira a origem dela e peça a opinião do Defender.",
                100, hash.Test));

        var span = content.Span;
        var pe = PeFile.LooksLikePe(span) ? PeFile.TryParse(span, size) : null;
        foreach (var rule in _rules.Rules)
        {
            if (!rule.AppliesTo(ext, size, pe is not null)) continue;
            if (!complete && rule.MaxSize > content.Length) continue; // só regras que cabem no trecho lido
            if (rule.Matches(content, pe))
                hits.Add(new ScanHit(ScanLayer.Pattern, rule.Id, rule.Title, rule.Why, rule.Points, rule.Test));
        }

        bool? signed = null;
        if (pe is not null)
        {
            var ctx = new FileContext(path, size, createdUtc, zone ?? CommandHeuristics.Zone(path, _options.WindowsDirectory ?? @"C:\Windows"), now);
            var peHits = PeHeuristics.Analyze(pe, ctx);
            // A assinatura (WinVerifyTrust) é a parte cara: só para executáveis em pastas de risco ou que já somam pontos.
            var needSignature = PeHeuristics.IsRiskZone(ctx.Zone) || peHits.Sum(h => h.Points) + hits.Sum(h => h.Points) >= 15;
            if (needSignature) signed = _signatures.IsSigned(path);
            hits.AddRange(PeHeuristics.ApplySignature(peHits, signed, ctx));
        }

        var score = Math.Max(0, hits.Sum(h => h.Points));
        var verdict = Scoring.FromScore(score, hash is not null);
        return new FileVerdict(path, size, sha256, verdict, score, hits, signed, now);
    }

    // ------------------------------------------------------------------ um arquivo do disco

    /// <summary>Verifica um arquivo. null = pulado (link, nuvem, em uso, sem permissão).</summary>
    public FileVerdict? ScanFile(string path) => ScanFile(new FileInfo(path));

    public FileVerdict? ScanFile(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.Exists || (file.Attributes & SkipAttributes) != 0) return null;
        var size = file.Length;
        var ext = file.Extension;
        var wantContent = ContentExtensions.Contains(ext) || (_options.ScanSmallFiles && size <= _options.SmallFileBytes) || _options.SniffUnknownForPe;
        if (!wantContent || size > _options.MaxHashBytes)
            return Evaluate(file.FullName, ReadOnlyMemory<byte>.Empty, complete: false, size, null, file.CreationTimeUtc);

        try
        {
            using var fs = new FileStream(file.FullName, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan, BufferSize = 0,
            });
            var known = ContentExtensions.Contains(ext) || (_options.ScanSmallFiles && size <= _options.SmallFileBytes);
            if (!known)
            {
                // Tipo desconhecido: só segue se começar com "MZ" (executável renomeado).
                Span<byte> head = stackalloc byte[2];
                if (fs.ReadAtLeast(head, 2, throwOnEndOfStream: false) < 2 || !PeFile.LooksLikePe(head))
                    return Evaluate(file.FullName, ReadOnlyMemory<byte>.Empty, complete: false, size, null, file.CreationTimeUtc);
                fs.Position = 0;
            }
            if (size <= _options.MaxContentBytes)
            {
                var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, size));
                try
                {
                    var read = fs.ReadAtLeast(buffer.AsSpan(0, (int)size), (int)size, throwOnEndOfStream: false);
                    var data = new ReadOnlyMemory<byte>(buffer, 0, read);
                    var sha = Convert.ToHexStringLower(SHA256.HashData(data.Span));
                    return Evaluate(file.FullName, data, complete: true, read, sha, file.CreationTimeUtc);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            // Grande: hash do arquivo inteiro em blocos + o começo (cabeçalho PE, seções iniciais) para as heurísticas.
            var prefix = new byte[PrefixBytes];
            var got = fs.ReadAtLeast(prefix, PrefixBytes, throwOnEndOfStream: false);
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            inc.AppendData(prefix, 0, got);
            var block = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                int n;
                while ((n = fs.Read(block, 0, block.Length)) > 0) inc.AppendData(block, 0, n);
            }
            finally { ArrayPool<byte>.Shared.Return(block); }
            return Evaluate(file.FullName, prefix.AsMemory(0, got), complete: false, size, Convert.ToHexStringLower(inc.GetHashAndReset()), file.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // em uso com bloqueio exclusivo, sem permissão, removido durante a leitura: conta como pulado
        }
    }

    // ------------------------------------------------------------------ percorrer pastas

    /// <summary>Arquivos sob as raízes (arquivos ou pastas), sem seguir links/junções, sem repetir pastas, respeitando exclusões.</summary>
    public IEnumerable<FileInfo> Enumerate(IReadOnlyList<string> roots, CancellationToken cancel, Action? onInaccessible = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var excluded = _options.ExcludedDirectories.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e))).ToList();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = SkipAttributes, ReturnSpecialDirectories = false,
        };
        foreach (var root in roots)
        {
            if (cancel.IsCancellationRequested) yield break;
            string full;
            try { full = Path.GetFullPath(root); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                if ((fi.Attributes & SkipAttributes) == 0) yield return fi;
                continue;
            }
            if (!Directory.Exists(full) || (new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((Path.TrimEndingDirectorySeparator(full), 0));
            while (stack.Count > 0)
            {
                if (cancel.IsCancellationRequested) yield break;
                var (dir, depth) = stack.Pop();
                if (!visited.Add(dir) || IsExcluded(dir, excluded)) continue;
                var files = new List<FileInfo>();
                try
                {
                    foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                    {
                        if (entry is DirectoryInfo d)
                        {
                            if (depth < _options.MaxDepth && !SkippedDirectoryNames.Contains(d.Name)) stack.Push((d.FullName, depth + 1));
                        }
                        else if (entry is FileInfo f) files.Add(f);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    onInaccessible?.Invoke();
                }
                foreach (var f in files) yield return f;
            }
        }
    }

    private static bool IsExcluded(string dir, List<string> excluded)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var e in excluded)
            if (dir.Equals(e, cmp) || dir.StartsWith(e + Path.DirectorySeparatorChar, cmp)) return true;
        return false;
    }

    /// <summary>Verifica tudo sob as raízes em paralelo. Achados (≥ "para revisar", ou arquivo de teste) vão para <paramref name="onFinding"/>.</summary>
    public ScanSummary Scan(IReadOnlyList<string> roots, IProgress<ScanProgress>? progress, Action<FileVerdict>? onFinding, CancellationToken cancel)
    {
        long files = 0, bytes = 0, skipped = 0, errors = 0;
        string? current = null;
        var findings = new ConcurrentBag<FileVerdict>();
        var sw = Stopwatch.StartNew();
        void Report() => progress?.Report(new ScanProgress(Interlocked.Read(ref files), Interlocked.Read(ref bytes), Interlocked.Read(ref skipped),
            findings.Count, Volatile.Read(ref current), sw.Elapsed.TotalSeconds > 0.2 ? Interlocked.Read(ref files) / sw.Elapsed.TotalSeconds : 0, sw.Elapsed));
        var cancelled = false;
        using (new Timer(_ => Report(), null, 250, 250))
        {
            try
            {
                var source = System.Collections.Concurrent.Partitioner.Create(Enumerate(roots, cancel, () => Interlocked.Increment(ref skipped)),
                    EnumerablePartitionerOptions.NoBuffering);
                Parallel.ForEach(source, new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism, CancellationToken = cancel }, file =>
                {
                    Volatile.Write(ref current, file.FullName);
                    FileVerdict? v;
                    try { v = ScanFile(file); }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        Interlocked.Increment(ref errors);
                        return;
                    }
                    if (v is null) { Interlocked.Increment(ref skipped); return; }
                    Interlocked.Increment(ref files);
                    Interlocked.Add(ref bytes, v.Size);
                    if (!IsFinding(v) || (_ignore?.Contains(v.Path, v.Sha256) ?? false)) return;
                    findings.Add(v);
                    onFinding?.Invoke(v);
                });
            }
            catch (OperationCanceledException) { cancelled = true; }
        }
        sw.Stop();
        Volatile.Write(ref current, null);
        Report();
        var list = findings.OrderByDescending(f => f.Verdict).ThenByDescending(f => f.Score).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new ScanSummary(files, bytes, skipped, errors, sw.Elapsed, cancelled || cancel.IsCancellationRequested, list);
    }

    public static bool IsFinding(FileVerdict v) => v is not null && (v.Verdict >= Verdict.Review || v.IsTestSample);
}
