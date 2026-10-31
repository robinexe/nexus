using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Nexus.WindowsActions;

/// <summary>Ponto de interposição dos testes: chamado depois de abrir/conferir pela primeira vez e antes da conferência final.</summary>
public enum SafeMoveStage
{
    /// <summary>Handles abertos (Windows) ou primeira conferência feita (outros sistemas); falta a conferência final e o move.</summary>
    Opened,
}

/// <summary>O move foi recusado antes de qualquer mudança (link/junção, caminho diferente do esperado, em uso, destino ocupado).</summary>
public sealed class SafeMoveRefusedException : IOException
{
    public SafeMoveRefusedException() { }
    public SafeMoveRefusedException(string message) : base(message) { }
    public SafeMoveRefusedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Move um arquivo ou pasta NO MESMO VOLUME sem seguir links/junções e sem a corrida "confere o caminho, depois move pelo
/// caminho" (revisão de segurança, achado médio): entre a checagem e o <c>Directory.Move</c>, uma pasta-mãe trocada por uma
/// junção desviaria o move, feito talvez como administrador/SYSTEM, para fora das raízes permitidas.
/// <para><b>Windows</b> (operações relativas a handle):</para>
/// <list type="number">
/// <item>abre a pasta-mãe da origem e a do destino com <c>FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS</c> e
/// compartilhamento sem <c>FILE_SHARE_DELETE</c> (enquanto o NEXUS segura o handle, ninguém renomeia nem apaga essas pastas);</item>
/// <item>abre a própria origem (acesso DELETE, sem seguir link, sem <c>FILE_SHARE_DELETE</c>: arquivo em uso = recusa);</item>
/// <item>confere em cada handle que não é reparse point (<c>GetFileInformationByHandleEx(FileAttributeTagInfo)</c>) e que o
/// caminho final (<c>GetFinalPathNameByHandle</c>) é exatamente o esperado — uma junção em qualquer ponto da cadeia muda o
/// caminho final;</item>
/// <item>renomeia a origem PELO HANDLE (<c>SetFileInformationByHandle(FileRenameInfoEx)</c>, sem substituir) com
/// <c>RootDirectory</c> = handle da pasta de destino e nome relativo: nenhum caminho é resolvido de novo depois da conferência;</item>
/// <item>confere o caminho final da origem depois do move.</item>
/// </list>
/// <para><b>Outros sistemas</b> (só testes; o NEXUS escreve só no Windows): confere a cadeia de pastas sem links antes e de
/// novo depois do ponto de interposição, e move pelo caminho. Não fecha a corrida; serve para simular a troca nos testes.</para>
/// Outro volume: não é com este helper (o chamador copia, confere hash e só então apaga a origem).
/// </summary>
public static partial class SafeFileMover
{
    /// <summary>true quando o move é feito por handle (Windows).</summary>
    public static bool IsHandleRelative => OperatingSystem.IsWindows();

    /// <param name="source">Arquivo ou pasta (caminho completo) que existe e não é link.</param>
    /// <param name="destination">Caminho completo que NÃO existe; a pasta-mãe precisa existir e estar no mesmo volume.</param>
    /// <param name="interpose">Só testes: chamado entre a primeira conferência e a final.</param>
    public static void Move(string source, string destination, Action<SafeMoveStage>? interpose = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(destination))
            throw new SafeMoveRefusedException("Caminhos precisam ser completos; nada foi movido.");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (Path.GetDirectoryName(source) is not { Length: > 0 } || Path.GetDirectoryName(destination) is not { Length: > 0 })
            throw new SafeMoveRefusedException("A raiz de um volume não é movida; nada foi movido.");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new SafeMoveRefusedException("Origem e destino iguais; nada foi movido.");
        if (IsUnder(destination, source))
            throw new SafeMoveRefusedException("O destino fica dentro da origem; nada foi movido.");

        if (OperatingSystem.IsWindows()) WindowsMove(source, destination, interpose);
        else PortableMove(source, destination, interpose);
    }

    internal static bool IsUnder(string path, string folder) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
        (path[folder.Length] == Path.DirectorySeparatorChar || path[folder.Length] == Path.AltDirectorySeparatorChar);

    // ------------------------------------------------------------------ outros sistemas (testes)

    private static void PortableMove(string source, string destination, Action<SafeMoveStage>? interpose)
    {
        void Check()
        {
            CheckChain(Path.GetDirectoryName(source)!);
            CheckChain(Path.GetDirectoryName(destination)!);
            var item = new FileInfo(source);
            if (!item.Exists && !Directory.Exists(source)) throw new SafeMoveRefusedException($"{source} não existe; nada foi movido.");
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null)
                throw new SafeMoveRefusedException($"{source} é um link; o NEXUS não move links. Nada foi movido.");
            if (File.Exists(destination) || Directory.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
                throw new SafeMoveRefusedException($"{destination} já existe; nada foi movido (nunca sobrescreve).");
        }
        Check();
        interpose?.Invoke(SafeMoveStage.Opened);
        Check();
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination, overwrite: false);
    }

    /// <summary>Cada pasta da raiz até <paramref name="directory"/> existe e não é link.</summary>
    private static void CheckChain(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (!current.Exists) throw new SafeMoveRefusedException($"{current.FullName} não existe; nada foi movido.");
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget is not null)
                throw new SafeMoveRefusedException($"{current.FullName} é um link/junção; o NEXUS não segue links. Nada foi movido.");
            current = current.Parent;
        }
    }

    // ------------------------------------------------------------------ Windows (por handle)

    private const uint Delete = 0x00010000, Synchronize = 0x00100000, FileReadAttributes = 0x80, FileListDirectory = 0x1, FileTraverse = 0x20;
    private const uint ShareRead = 0x1, ShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000, FlagOpenReparsePoint = 0x00200000;
    private const uint AttrDirectory = 0x10, AttrReparsePoint = 0x400;
    private const int FileAttributeTagInfoClass = 9, FileRenameInfoClass = 3, FileRenameInfoExClass = 22;
    private const int ErrorInvalidParameter = 87, ErrorNotSameDevice = 17, ErrorSharingViolation = 32, ErrorAccessDenied = 5,
        ErrorAlreadyExists = 183, ErrorFileExists = 80;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsMove(string source, string destination, Action<SafeMoveStage>? interpose)
    {
        var sourceParent = Path.GetDirectoryName(source)!;
        var destinationParent = Path.GetDirectoryName(destination)!;
        var sameParent = string.Equals(LongPath(sourceParent), LongPath(destinationParent), StringComparison.OrdinalIgnoreCase);

        using var sp = Open(sourceParent, FileListDirectory | FileTraverse | FileReadAttributes | Synchronize, "a pasta de origem");
        using var dp = sameParent ? null : Open(destinationParent, FileListDirectory | FileTraverse | FileReadAttributes | Synchronize, "a pasta de destino");
        using var item = Open(source, Delete | FileReadAttributes | Synchronize, "o item a mover");

        interpose?.Invoke(SafeMoveStage.Opened);

        Verify(sp, sourceParent, mustBeDirectory: true);
        if (dp is not null) Verify(dp, destinationParent, mustBeDirectory: true);
        Verify(item, source, mustBeDirectory: null);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new SafeMoveRefusedException($"{destination} já existe; nada foi movido (nunca sobrescreve).");

        Rename(item, dp ?? sp, Path.GetFileName(destination));

        var after = FinalPath(item);
        if (!SamePath(after, destination))
            throw new IOException($"Movido, mas o caminho final ({after}) difere do esperado ({destination}). Confira a quarentena.");
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle Open(string path, uint access, string what)
    {
        var h = CreateFileW(path, access, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        var error = Marshal.GetLastPInvokeError();
        h.Dispose();
        throw error is ErrorSharingViolation or ErrorAccessDenied
            ? new SafeMoveRefusedException($"Não foi possível abrir {what} ({path}): em uso ou sem permissão (Win32 {error}). Nada foi movido.")
            : new SafeMoveRefusedException($"Não foi possível abrir {what} ({path}): Win32 {error}. Nada foi movido.");
    }

    [SupportedOSPlatform("windows")]
    private static void Verify(SafeFileHandle h, string expected, bool? mustBeDirectory)
    {
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new SafeMoveRefusedException($"Não foi possível ler os atributos de {expected} (Win32 {Marshal.GetLastPInvokeError()}). Nada foi movido.");
        if ((info.FileAttributes & AttrReparsePoint) != 0)
            throw new SafeMoveRefusedException($"{expected} é um link/junção (reparse point); o NEXUS não segue links. Nada foi movido.");
        var isDirectory = (info.FileAttributes & AttrDirectory) != 0;
        if (mustBeDirectory == true && !isDirectory)
            throw new SafeMoveRefusedException($"{expected} não é uma pasta. Nada foi movido.");
        var final = FinalPath(h);
        if (!SamePath(final, expected))
            throw new SafeMoveRefusedException($"O caminho real de {expected} é {final} (link/junção no caminho ou pasta trocada). Nada foi movido.");
    }

    [SupportedOSPlatform("windows")]
    private static unsafe string FinalPath(SafeFileHandle h)
    {
        var buffer = new char[1024];
        while (true)
        {
            uint n;
            fixed (char* p = buffer) n = GetFinalPathNameByHandleW(h, p, (uint)buffer.Length, 0);
            if (n == 0) throw new IOException($"GetFinalPathNameByHandle falhou (Win32 {Marshal.GetLastPInvokeError()}).");
            if (n < buffer.Length) return new string(buffer, 0, (int)n);
            if (n > 32_768) throw new IOException("Caminho longo demais.");
            buffer = new char[n + 1];
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void Rename(SafeFileHandle item, SafeFileHandle targetDirectory, string newName)
    {
        // FILE_RENAME_INFO: union { BOOLEAN ReplaceIfExists; DWORD Flags; } (4 bytes) | HANDLE RootDirectory (alinhado) |
        // DWORD FileNameLength | WCHAR FileName[]. Flags = 0: nunca substitui o que existir no destino.
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = newName.Length * sizeof(char);
        var buffer = new byte[nameOffset + nameBytes + sizeof(char)];
        var added = false;
        try
        {
            targetDirectory.DangerousAddRef(ref added);
            fixed (byte* b = buffer)
            {
                *(uint*)b = 0;
                *(IntPtr*)(b + rootOffset) = targetDirectory.DangerousGetHandle();
                *(uint*)(b + lengthOffset) = (uint)nameBytes;
                fixed (char* n = newName) Buffer.MemoryCopy(n, b + nameOffset, nameBytes, nameBytes);
                if (SetFileInformationByHandle(item, FileRenameInfoExClass, b, (uint)buffer.Length)) return;
                var error = Marshal.GetLastPInvokeError();
                // Windows 10 anterior à 1709: sem FileRenameInfoEx. Mesmo formato, ReplaceIfExists = FALSE.
                if (error == ErrorInvalidParameter && SetFileInformationByHandle(item, FileRenameInfoClass, b, (uint)buffer.Length)) return;
                error = Marshal.GetLastPInvokeError() is var e2 && e2 != 0 ? e2 : error;
                throw error switch
                {
                    ErrorNotSameDevice => new SafeMoveRefusedException("Origem e destino em volumes diferentes; nada foi movido."),
                    ErrorAlreadyExists or ErrorFileExists => new SafeMoveRefusedException("O destino já existe; nada foi movido (nunca sobrescreve)."),
                    ErrorAccessDenied or ErrorSharingViolation => new SafeMoveRefusedException($"Em uso ou sem permissão (Win32 {error}); nada foi movido."),
                    _ => new IOException($"Não foi possível mover (Win32 {error}); nada foi movido."),
                };
            }
        }
        finally
        {
            if (added) targetDirectory.DangerousRelease();
        }
    }

    /// <summary>Compara um caminho devolvido por GetFinalPathNameByHandle (<c>\\?\C:\…</c>) com o esperado (nomes longos).</summary>
    [SupportedOSPlatform("windows")]
    private static bool SamePath(string final, string expected) =>
        string.Equals(StripPrefix(final), Path.TrimEndingDirectorySeparator(LongPath(expected)), StringComparison.OrdinalIgnoreCase);

    internal static string StripPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>Expande nomes 8.3 (ex.: <c>RUNNER~1</c> no %TEMP%) para comparar com o caminho final; não resolve links.</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe string LongPath(string path)
    {
        if (!path.Contains('~', StringComparison.Ordinal)) return path;
        var buffer = new char[1024];
        uint n;
        fixed (char* p = buffer) n = GetLongPathNameW(path, p, (uint)buffer.Length);
        return n == 0 || n >= buffer.Length ? path : new string(buffer, 0, (int)n);
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, out FileAttributeTagInfo info, uint dwBufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, byte* lpFileInformation, uint dwBufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathNameW(string lpszShortPath, char* lpszLongPath, uint cchBuffer);
}
