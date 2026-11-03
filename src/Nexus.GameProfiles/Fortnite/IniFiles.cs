using System.Text;

namespace Nexus.GameProfiles.Fortnite;

/// <summary>
/// Leitura/escrita de um .ini do Unreal mantendo a codificação do arquivo. O Unreal grava ANSI/UTF-8 quando o texto é só ASCII
/// e UTF-16 LE com BOM quando há acentos (ex.: nome do microfone "Microfone (Áudio de Alta Definição)"), então as duas formas
/// são aceitas: UTF-8 (com ou sem BOM, estrito) e UTF-16 LE/BE com BOM. Qualquer outra coisa (bytes que não são UTF-8 válido)
/// é recusada em vez de virar "?" e corromper o arquivo. Escrita atômica (temporário na mesma pasta + troca) e só se o
/// conteúdo ainda for o lido (hash), como <see cref="SafeFiles"/>.
/// </summary>
public static class IniFiles
{
    /// <summary>Maior arquivo aceito (o GameUserSettings.ini real tem dezenas de KB).</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    public sealed record IniRead(string Text, string Sha256, Encoding Encoding, bool Exists, bool ReadOnly);

    public static IniRead Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var info = new FileInfo(path);
        if (!info.Exists) return new IniRead(string.Empty, SafeFiles.Hash([]), new UTF8Encoding(false), false, false);
        if (info.Length > MaxBytes) throw new InvalidDataException($"Arquivo grande demais para ser um GameUserSettings.ini ({info.Length} bytes).");
        var bytes = File.ReadAllBytes(path);
        var (encoding, skip) = Detect(bytes);
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        return new IniRead(text, SafeFiles.Hash(bytes), encoding, true, info.Attributes.HasFlag(FileAttributes.ReadOnly));
    }

    private static (Encoding Encoding, int Skip) Detect(byte[] b)
    {
        if (b.Length >= 4 && ((b[0] == 0xFF && b[1] == 0xFE && b[2] == 0 && b[3] == 0) || (b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)))
            throw new InvalidDataException("Arquivo em UTF-32: edição recusada.");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        var bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
        return (new UTF8Encoding(bom, throwOnInvalidBytes: true), bom ? 3 : 0);
    }

    /// <summary>Grava somente se o arquivo ainda tiver o hash lido antes; nunca cria um arquivo que não existia.</summary>
    public static void WriteAtomic(string path, string text, Encoding encoding, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        var current = Read(path);
        if (!current.Exists) throw new IOException($"Arquivo não existe mais: {path}");
        if (!string.Equals(current.Sha256, expectedSha256, StringComparison.Ordinal))
            throw new IOException($"Arquivo alterado por outro programa desde a leitura: {path}");
        if (current.ReadOnly) throw new UnauthorizedAccessException($"Arquivo marcado como somente leitura: {path}");

        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.nexus-{Guid.NewGuid():N}.tmp");
        var payload = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(payload);
            fs.Flush(flushToDisk: true);
        }
        try
        {
            File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
