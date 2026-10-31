using System.Globalization;

namespace Nexus.Lab.Memory;

public enum PageFileMode { Unknown, None, AutomaticAllDrives, SystemManaged, Custom }

public sealed record PageFileEntry(string Path, int? InitialMb, int? MaximumMb)
{
    public bool SystemManaged => InitialMb is null or 0 && MaximumMb is null or 0;
}

/// <summary>
/// Configuração do arquivo de paginação lida do valor <c>PagingFiles</c> (REG_MULTI_SZ) em
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management</c>:
/// "?:\pagefile.sys" = automático em todas as unidades; "C:\pagefile.sys 0 0" = tamanho gerenciado pelo sistema;
/// "C:\pagefile.sys 1024 4096" = tamanho fixo (inicial e máximo em MB); vazio = sem arquivo de paginação.
/// </summary>
public sealed record PageFileConfig(PageFileMode Mode, IReadOnlyList<PageFileEntry> Entries)
{
    public static readonly PageFileConfig Unknown = new(PageFileMode.Unknown, []);

    public static PageFileConfig Parse(IReadOnlyList<string>? multiSz)
    {
        if (multiSz is null) return Unknown;
        var entries = new List<PageFileEntry>();
        foreach (var line in multiSz)
        {
            var t = line?.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int? Num(int i) => parts.Length > i && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
            entries.Add(new PageFileEntry(parts[0], Num(1), Num(2)));
        }
        if (entries.Count == 0) return new PageFileConfig(PageFileMode.None, entries);
        if (entries.Any(e => e.Path.StartsWith("?:", StringComparison.Ordinal))) return new PageFileConfig(PageFileMode.AutomaticAllDrives, entries);
        return new PageFileConfig(entries.All(e => e.SystemManaged) ? PageFileMode.SystemManaged : PageFileMode.Custom, entries);
    }

    /// <summary>Valor canônico (linhas separadas por \0) como o backend de registro do NEXUS o representa.</summary>
    public static string Canonical(IReadOnlyList<string> lines) => string.Join('\u0000', lines);

    public string Describe() => Mode switch
    {
        PageFileMode.None => "Desligado (sem arquivo de paginação)",
        PageFileMode.AutomaticAllDrives => "Automático (o Windows gerencia em todas as unidades)",
        PageFileMode.SystemManaged => $"Gerenciado pelo sistema em {string.Join(", ", Entries.Select(e => e.Path))}",
        PageFileMode.Custom => string.Join("; ", Entries.Select(e => e.SystemManaged
            ? $"{e.Path}: gerenciado pelo sistema"
            : string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{e.Path}: tamanho fixo {e.InitialMb ?? 0:N0}–{e.MaximumMb ?? 0:N0} MB"))),
        _ => "Não foi possível ler",
    };
}

/// <summary>Estado da memória do Windows (somente leitura). Null = não lido.</summary>
public sealed record MemoryState
{
    public ulong? TotalPhysicalBytes { get; init; }
    public ulong? AvailablePhysicalBytes { get; init; }
    public ulong? CommitTotalBytes { get; init; }
    public ulong? CommitLimitBytes { get; init; }
    /// <summary>Maior uso de memória comprometida desde que o PC ligou.</summary>
    public ulong? CommitPeakBytes { get; init; }
    public ulong? SystemCacheBytes { get; init; }
    public ulong? StandbyBytes { get; init; }
    public ulong? ModifiedBytes { get; init; }
    public ulong? FreeZeroBytes { get; init; }
    public double? PageFaultsPerSec { get; init; }
    /// <summary>Páginas lidas do disco por falta de página "dura" (o que de fato deixa o PC lento quando falta RAM).</summary>
    public double? PagesInputPerSec { get; init; }
    public bool? CompressionEnabled { get; init; }
    public string CompressionSource { get; init; } = "";
    public bool? PageCombining { get; init; }
    public PageFileConfig PageFile { get; init; } = PageFileConfig.Unknown;
    public ulong? PageFileCurrentBytes { get; init; }
    public string? SysMainState { get; init; }
    public string? SysMainStartType { get; init; }
    public int? EnablePrefetcher { get; init; }
    public TimeSpan? Uptime { get; init; }
    public IReadOnlyDictionary<string, string> Problems { get; init; } = new Dictionary<string, string>();
}
