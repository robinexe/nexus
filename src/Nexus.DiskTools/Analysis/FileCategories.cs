namespace Nexus.DiskTools.Analysis;

/// <summary>Classifica arquivos por tipo (extensão) e por onde estão (biblioteca de jogos, pastas de desenvolvimento).</summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) d["." + e] = c; }
        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "m2ts", "3gp");
        Add(FileCategory.Audio, "mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus", "aiff");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "raw", "cr2", "cr3", "nef", "arw", "dng", "psd", "svg");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "epub", "csv", "md");
        Add(FileCategory.Archive, "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "tgz", "cab");
        Add(FileCategory.Installer, "msi", "msix", "msixbundle", "appx", "appxbundle", "msu", "msp");
        Add(FileCategory.DiskImage, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "wim", "esd");
        Add(FileCategory.Game, "pak", "vpk", "bundle", "forge", "bsa", "ba2", "upk", "uasset", "ucas", "utoc", "wad", "rpf", "bnk", "pck");
        Add(FileCategory.Development, "cs", "js", "ts", "tsx", "py", "java", "cpp", "c", "h", "hpp", "rs", "go", "pdb", "obj", "ilk", "pch", "ipch", "nupkg", "jar", "class");
        Add(FileCategory.System, "sys", "dll", "mui", "cat", "etl", "evtx", "dmp", "log", "tmp");
        return d;
    }

    public static string Label(FileCategory c) => c switch
    {
        FileCategory.Video => "Vídeos",
        FileCategory.Audio => "Música e áudio",
        FileCategory.Image => "Imagens e fotos",
        FileCategory.Document => "Documentos",
        FileCategory.Archive => "Arquivos compactados",
        FileCategory.Installer => "Instaladores",
        FileCategory.DiskImage => "ISOs e discos virtuais",
        FileCategory.Game => "Jogos",
        FileCategory.Development => "Programação",
        FileCategory.System => "Sistema e logs",
        _ => "Outros",
    };

    /// <summary>Classifica um arquivo. <paramref name="context"/> vem da pasta (dentro de biblioteca de jogos, Downloads...).</summary>
    public static FileCategory Classify(string fileName, FolderContext context)
    {
        var ext = Path.GetExtension(fileName);
        // Na biblioteca de jogos, tudo é do jogo (vídeos de abertura, DLLs, pacotes): o usuário quer saber quanto o jogo ocupa.
        if (context.HasFlag(FolderContext.GameLibrary)) return FileCategory.Game;
        if (context.HasFlag(FolderContext.Development)) return FileCategory.Development;
        if (ext.Length > 0 && ByExtension.TryGetValue(ext, out var c)) return c;
        // .exe em Downloads é quase sempre instalador; em outros lugares, programa (fica em "Outros").
        if (context.HasFlag(FolderContext.Downloads) && ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return FileCategory.Installer;
        return FileCategory.Other;
    }

    /// <summary>Contexto de uma subpasta, herdado do pai e reconhecido pelo nome.</summary>
    public static FolderContext ContextFor(string folderName, string? parentName, FolderContext parent)
    {
        var ctx = parent;
        if (Eq(folderName, "common") && Eq(parentName, "steamapps")) ctx |= FolderContext.GameLibrary;
        else if (Eq(folderName, "Epic Games") || Eq(folderName, "XboxGames") || Eq(folderName, "Riot Games") || Eq(folderName, "GOG Games")
                 || Eq(folderName, "Ubisoft Game Launcher") || Eq(folderName, "EA Games"))
            ctx |= FolderContext.GameLibrary;
        if (Eq(folderName, "node_modules") || Eq(folderName, ".git") || Eq(folderName, ".nuget") || Eq(folderName, ".gradle")) ctx |= FolderContext.Development;
        if (Eq(folderName, "Downloads")) ctx |= FolderContext.Downloads;
        return ctx;
    }

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

[Flags]
public enum FolderContext { None = 0, GameLibrary = 1, Development = 2, Downloads = 4 }
