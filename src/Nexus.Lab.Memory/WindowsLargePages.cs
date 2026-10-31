using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Lab.Memory;

/// <summary>
/// Páginas grandes (2 MB) para a medição de latência. Exigem o direito "Bloquear páginas na memória" (SeLockMemoryPrivilege),
/// que contas comuns não têm: o NEXUS só tenta ATIVAR no próprio processo um direito que a conta já possui (não muda nada
/// no sistema) e, sem ele, usa páginas normais e explica o efeito do TLB.
/// Documentação: https://learn.microsoft.com/windows/win32/memory/large-page-support
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class WindowsLargePages
{
    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemLargePages = 0x20000000, MemRelease = 0x8000, PageReadWrite = 0x04;
    private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x08, SePrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [LibraryImport("kernel32.dll")]
    private static partial nuint GetLargePageMinimum();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* address, nuint size, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, nint previous, nint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static bool TryAllocate(long bytes, out NativeBlock? block, out string note)
    {
        block = null;
        try
        {
            var min = GetLargePageMinimum();
            if (min == 0) { note = "Páginas normais de 4 KB: este processador/Windows não oferece páginas grandes."; return false; }
            if (!EnableLockMemoryPrivilege())
            {
                note = "Páginas normais de 4 KB: sua conta não tem o direito \"Bloquear páginas na memória\" (padrão do Windows). " +
                       "Isso soma à latência aleatória o custo de traduzir endereços (falta de TLB); a medida \"por página\" mostra a latência sem esse custo.";
                return false;
            }
            var size = (nuint)((bytes + (long)min - 1) / (long)min * (long)min);
            var p = VirtualAlloc(null, size, MemCommit | MemReserve | MemLargePages, PageReadWrite);
            if (p is null)
            {
                note = "Páginas normais de 4 KB: o Windows não achou memória contígua para páginas grandes agora (memória fragmentada).";
                return false;
            }
            block = new NativeBlock((byte*)p, (long)size, true, ptr => VirtualFree((void*)ptr, 0, MemRelease));
            note = "Páginas grandes de 2 MB: quase sem falta de TLB, mede a latência da RAM em si.";
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            note = "Páginas normais de 4 KB.";
            return false;
        }
    }

    /// <summary>Ativa no token do próprio processo o direito de bloquear páginas, se a conta já o tiver. Não altera contas nem políticas.</summary>
    private static bool EnableLockMemoryPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out var luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0)) return false;
            return Marshal.GetLastPInvokeError() != ErrorNotAllAssigned;
        }
        finally { CloseHandle(token); }
    }
}
