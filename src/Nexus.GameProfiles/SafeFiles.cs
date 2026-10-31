using System.Security.Cryptography;
using System.Text;

namespace Nexus.GameProfiles;

/// <summary>
/// Leitura/escrita de arquivos de configuração de jogos com:
/// confinamento a uma raiz detectada, recusa de reparse points (symlink/junction),
/// detecção de alteração externa por hash e escrita atômica (temp no mesmo diretório + replace).
/// </summary>
public static class SafeFiles
{
    public sealed record FileRead(string Text, string Sha256, Encoding Encoding, bool Exists);

    public static string EnsureUnderRoot(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Caminho fora da raiz permitida: {full}");

        // Nenhum componente entre a raiz e o arquivo pode ser reparse point.
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir is not null && dir.FullName.Length >= fullRoot.Length - 1; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException($"Diretório é link/junção: {dir.FullName}");
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException($"Arquivo é link: {full}");
        return full;
    }

    public static FileRead Read(string path)
    {
        if (!File.Exists(path)) return new FileRead(string.Empty, Hash([]), new UTF8Encoding(false), false);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            throw new InvalidDataException($"Arquivo UTF-16/32 não suportado para edição: {path}");
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        // Estrito: bytes que não são UTF-8 válido (ex.: Windows-1252) lançam em vez de virar U+FFFD e corromper o arquivo.
        var enc = new UTF8Encoding(hasBom, throwOnInvalidBytes: true);
        var text = enc.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        return new FileRead(text, Hash(bytes), enc, true);
    }

    /// <summary>Escreve somente se o conteúdo atual ainda tiver o hash lido antes (proteção contra alteração externa).</summary>
    public static void WriteAtomic(string path, string text, Encoding encoding, string expectedSha256)
    {
        var current = Read(path);
        if (!string.Equals(current.Sha256, expectedSha256, StringComparison.Ordinal))
            throw new IOException($"Arquivo alterado externamente desde a leitura: {path}");

        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.nexus-{Guid.NewGuid():N}.tmp");
        var payload = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(payload);
            fs.Flush(flushToDisk: true);
        }
        try
        {
            if (current.Exists) File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
