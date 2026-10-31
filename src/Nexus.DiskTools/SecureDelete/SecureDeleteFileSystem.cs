using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Nexus.DiskTools.SecureDelete;

/// <summary>Fatos de um caminho lidos do disco (sem abrir o conteúdo).</summary>
public sealed record FileFacts(
    bool Exists,
    bool IsDirectory,
    FileAttributes Attributes,
    long Length,
    DateTime LastWriteUtc,
    bool IsLink,
    string? ReparseAncestor)
{
    public static readonly FileFacts Missing = new(false, false, 0, 0, default, false, null);
}

/// <summary>Acesso ao disco da exclusão segura (trocável nos testes).</summary>
public interface ISecureDeleteFileSystem
{
    FileFacts Probe(string path);

    /// <summary>Verdadeiro se outro processo está com o arquivo aberto (tentativa de abertura exclusiva só para leitura).</summary>
    bool IsInUse(string path);

    /// <summary>
    /// Verificações que exigem o arquivo aberto (Windows: links físicos, caminho final). Null = ok; senão o motivo.
    /// </summary>
    string? CheckOpenHandle(string path);

    /// <summary>Sobrescreve, renomeia, trunca e apaga. Lança exceção (com o nome temporário, se já renomeou) em falha.</summary>
    void OverwriteAndDelete(string path);
}

/// <summary>
/// Implementação real. Ordem: abre com acesso exclusivo (ninguém mais lê nem escreve) → no Windows confere pelo próprio handle
/// que é o arquivo pedido (caminho final igual, um único link físico, sem reparse point) → 1 passada de bytes aleatórios
/// (criptograficamente fortes) do início ao fim → grava no disco (FlushFileBuffers) → fecha → renomeia para nome aleatório →
/// trunca para 0 → zera datas → apaga.
/// </summary>
public sealed partial class SecureDeleteFileSystem : ISecureDeleteFileSystem
{
    private const int ChunkBytes = 1024 * 1024;
    private static readonly DateTime NeutralTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public FileFacts Probe(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        info.Refresh();
        string? ancestor = ReparseAncestor(path);
        if (Directory.Exists(path))
        {
            var d = new DirectoryInfo(path);
            return new FileFacts(true, true, d.Attributes, 0, d.LastWriteTimeUtc, d.LinkTarget is not null || (d.Attributes & FileAttributes.ReparsePoint) != 0, ancestor);
        }
        if (!info.Exists)
        {
            // Link simbólico quebrado: Exists é falso, mas o link existe.
            return (info.Attributes != (FileAttributes)(-1) && info.LinkTarget is not null)
                ? new FileFacts(true, false, info.Attributes, 0, default, true, ancestor)
                : FileFacts.Missing with { ReparseAncestor = ancestor };
        }
        var attrs = info.Attributes;
        var isLink = (attrs & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
        return new FileFacts(true, false, attrs, isLink ? 0 : info.Length, info.LastWriteTimeUtc, isLink, ancestor);
    }

    /// <summary>Primeira pasta acima do arquivo que é link simbólico ou junção (nesse caso o caminho real é outro).</summary>
    private static string? ReparseAncestor(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            try
            {
                var d = new DirectoryInfo(dir);
                if (d.Exists && ((d.Attributes & FileAttributes.ReparsePoint) != 0 || d.LinkTarget is not null)) return dir;
            }
            catch (IOException) { return dir; }
            catch (UnauthorizedAccessException) { return dir; }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public bool IsInUse(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (IOException) when (File.Exists(path)) { return true; }
    }

    public string? CheckOpenHandle(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
        return WindowsChecks(fs.SafeFileHandle, path);
    }

    public void OverwriteAndDelete(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        OverwriteInPlace(path);
        RenameTruncateDelete(path);
    }

    /// <summary>1. Sobrescrita com acesso exclusivo: uma passada aleatória do início ao fim, gravada no disco. Devolve os bytes escritos.</summary>
    internal static long OverwriteInPlace(string path)
    {
        long written = 0;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough))
        {
            if (OperatingSystem.IsWindows() && WindowsChecks(fs.SafeFileHandle, path) is { } refused)
                throw new InvalidOperationException(refused + " Nada foi alterado.");
            var length = fs.Length;
            var buffer = new byte[(int)Math.Min(ChunkBytes, Math.Max(1, length))];
            while (written < length)
            {
                var n = (int)Math.Min(buffer.Length, length - written);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
                fs.Write(buffer, 0, n);
                written += n;
            }
            fs.Flush(flushToDisk: true);
        }
        return written;
    }

    /// <summary>2 e 3. Renomeia para nome aleatório, trunca, neutraliza datas e apaga. Devolve o nome temporário usado.</summary>
    internal static string RenameTruncateDelete(string path)
    {
        // 2. Nome aleatório na mesma pasta (o nome antigo deixa de existir na tabela de arquivos).
        var dir = Path.GetDirectoryName(path)!;
        string temp;
        var attempts = 0;
        do
        {
            temp = Path.Combine(dir, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        } while (File.Exists(temp) && ++attempts < 10);
        File.Move(path, temp, overwrite: false);

        try
        {
            // 3. Trunca, neutraliza datas e apaga.
            using (var fs = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(0);
                fs.Flush(flushToDisk: true);
            }
            File.SetCreationTimeUtc(temp, NeutralTime);
            File.SetLastWriteTimeUtc(temp, NeutralTime);
            File.SetLastAccessTimeUtc(temp, NeutralTime);
            File.Delete(temp);
            return temp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"O conteúdo já foi sobrescrito e o arquivo renomeado para \"{temp}\", mas não foi possível apagá-lo: {ex.Message} " +
                                  "Apague esse arquivo pelo Explorer (o conteúdo original não existe mais nele).", ex);
        }
    }

    /// <summary>Confere pelo handle aberto: sem reparse point, um único link físico e caminho final igual ao pedido.</summary>
    [SupportedOSPlatform("windows")]
    internal static string? WindowsChecks(SafeFileHandle handle, string expectedPath)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            return $"Não foi possível ler as informações do arquivo (erro {Marshal.GetLastPInvokeError()}).";
        if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) return "O arquivo é um link (reparse point).";
        if (info.NumberOfLinks != 1)
            return $"O arquivo tem {info.NumberOfLinks} links físicos (o mesmo conteúdo aparece em outro caminho); sobrescrever alteraria os outros também.";
        var final = FinalPath(handle);
        if (final is null) return "Não foi possível confirmar o caminho real do arquivo.";
        var normalized = final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? null
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        if (normalized is null || !string.Equals(normalized, expectedPath, StringComparison.OrdinalIgnoreCase))
            return $"O caminho real do arquivo ({final}) difere do escolhido; pode haver junção, nome curto ou link no caminho.";
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            var n = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
            return n == 0 || n >= buffer.Length ? null : new string(p, 0, (int)n);
        }
    }

    // FILETIME são dois DWORD (alinhamento 4): Pack = 4 reproduz o layout nativo de 52 bytes.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint size, uint flags);
}
