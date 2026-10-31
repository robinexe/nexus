namespace Nexus.DiskTools.Analysis;

/// <summary>Tipo de arquivo para o agrupamento "o que ocupa o disco".</summary>
public enum FileCategory { Video, Audio, Image, Document, Archive, Installer, DiskImage, Game, Development, System, Other }

/// <summary>Um arquivo grande encontrado na varredura.</summary>
public sealed record FileItem(string Name, string FullPath, long Size, DateTime LastWriteUtc, FileCategory Category)
{
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

/// <summary>Total de um tipo de arquivo.</summary>
public sealed record CategoryTotal(FileCategory Category, string Label, long Bytes, long Files);

/// <summary>Andamento da varredura (para a barra de progresso).</summary>
public sealed record ScanProgress(long Files, long Directories, long Bytes, int AccessDenied, string CurrentPath, TimeSpan Elapsed);

/// <summary>Pasta na árvore do resultado. Tamanhos são lógicos (tamanho dos arquivos), somando subpastas.</summary>
public sealed class DirNode
{
    internal DirNode(string name, string fullPath, DirNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public DirNode? Parent { get; }

    /// <summary>Total da pasta (arquivos dela + subpastas).</summary>
    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    /// <summary>Só os arquivos que estão diretamente nesta pasta.</summary>
    public long OwnFilesSize { get; internal set; }
    public long OwnFileCount { get; internal set; }
    /// <summary>Bytes em arquivos não modificados há mais de <see cref="DiskScanOptions.OldFileAge"/> (inclui subpastas).</summary>
    public long OldBytes { get; internal set; }
    /// <summary>Arquivos só na nuvem (OneDrive "sob demanda"): não ocupam o disco e não entram em <see cref="Size"/>.</summary>
    public long CloudOnlyBytes { get; internal set; }
    /// <summary>A pasta (ou parte dela) não pôde ser lida: o tamanho é um mínimo.</summary>
    public bool Incomplete { get; internal set; }

    /// <summary>Subpastas, da maior para a menor.</summary>
    public IReadOnlyList<DirNode> Children { get; internal set; } = [];
    /// <summary>Maiores arquivos diretamente nesta pasta (poucos; para o treemap), do maior para o menor.</summary>
    public IReadOnlyList<FileItem> LargestFiles { get; internal set; } = [];

    public IEnumerable<DirNode> SelfAndDescendants()
    {
        var stack = new Stack<DirNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    /// <summary>Caminho da raiz até esta pasta (para as migalhas de pão).</summary>
    public IReadOnlyList<DirNode> Lineage()
    {
        var list = new List<DirNode>();
        for (var n = this; n is not null; n = n.Parent) list.Add(n);
        list.Reverse();
        return list;
    }

    /// <summary>
    /// Blocos do treemap desta pasta: subpastas, os maiores arquivos e um bloco "outros arquivos" com o resto dos arquivos
    /// diretos. Só tamanhos positivos; do maior para o menor.
    /// </summary>
    public IReadOnlyList<DiskTile> Tiles()
    {
        var tiles = new List<DiskTile>();
        foreach (var c in Children.Where(c => c.Size > 0)) tiles.Add(new DiskTile(c.Name, c.Size, DiskTileKind.Folder, c, null));
        foreach (var f in LargestFiles.Where(f => f.Size > 0)) tiles.Add(new DiskTile(f.Name, f.Size, DiskTileKind.File, null, f));
        var rest = OwnFilesSize - LargestFiles.Sum(f => f.Size);
        if (rest > 0) tiles.Add(new DiskTile($"Outros {OwnFileCount - LargestFiles.Count} arquivos", rest, DiskTileKind.OtherFiles, null, null));
        return tiles.OrderByDescending(t => t.Size).ToList();
    }

    public override string ToString() => FullPath;
}

public enum DiskTileKind { Folder, File, OtherFiles }

/// <summary>Bloco do treemap: pasta (clicável), arquivo grande ou "outros arquivos".</summary>
public sealed record DiskTile(string Label, long Size, DiskTileKind Kind, DirNode? Folder, FileItem? File)
{
    public FileCategory? Category => File?.Category;
}

/// <summary>Resultado de uma varredura (somente leitura: nada é alterado no disco).</summary>
public sealed class DiskScanResult
{
    public required DirNode Root { get; init; }
    /// <summary>Maiores arquivos do volume/pasta, do maior para o menor.</summary>
    public required IReadOnlyList<FileItem> TopFiles { get; init; }
    /// <summary>Totais por tipo, do maior para o menor (só tipos presentes).</summary>
    public required IReadOnlyList<CategoryTotal> Categories { get; init; }
    public long Files { get; init; }
    public long Directories { get; init; }
    /// <summary>Pastas sem permissão de leitura (contadas e puladas).</summary>
    public int AccessDenied { get; init; }
    /// <summary>Pastas ou arquivos ilegíveis por outros erros (caminho longo demais, removidos durante a varredura...).</summary>
    public int Unreadable { get; init; }
    /// <summary>Links simbólicos e junções ignorados (nunca seguidos: o conteúdo é contado onde ele realmente está).</summary>
    public int ReparsePointsSkipped { get; init; }
    public long CloudOnlyBytes { get; init; }
    public long CloudOnlyFiles { get; init; }
    /// <summary>Alguns caminhos sem acesso (até 20), para mostrar ao usuário.</summary>
    public IReadOnlyList<string> DeniedSamples { get; init; } = [];
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Opções da varredura.</summary>
public sealed class DiskScanOptions
{
    public int TopFiles { get; init; } = 50;
    /// <summary>Quantos maiores arquivos guardar por pasta (para o treemap). O resto vira "outros arquivos".</summary>
    public int FilesPerDirectory { get; init; } = 6;
    public TimeSpan OldFileAge { get; init; } = TimeSpan.FromDays(90);
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);
    /// <summary>Até que profundidade as subpastas são lidas em paralelo (abaixo disso, em sequência dentro de cada ramo).</summary>
    public int ParallelDepth { get; init; } = 2;
    /// <summary>Profundidade máxima (proteção contra árvores patológicas).</summary>
    public int MaxDepth { get; init; } = 512;

    /// <summary>Substitui a listagem de uma pasta (testes: simular acesso negado).</summary>
    internal Func<string, IEnumerable<FileSystemInfo>>? EnumerateOverride { get; init; }
}
