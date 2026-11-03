using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.SystemTools.Crash;

/// <summary>
/// Leitura dos travamentos no Windows (só leitura): Log de Eventos pela wevtapi (EvtQuery/EvtNext/EvtRender, do mais novo para o
/// mais antigo) e os Report.wer das pastas ReportArchive/ReportQueue (do computador e da sua conta). Sem permissão ou sem log,
/// devolve o que conseguiu e anota o motivo: nada é inventado.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsCrashReader
{
    private const uint EvtQueryChannelPath = 0x1, EvtQueryReverseDirection = 0x200, EvtRenderEventXml = 1;
    private const int ErrorAccessDenied = 5, ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122, ErrorEvtChannelNotFound = 15007;
    public const int MaxReportBytes = 1024 * 1024;
    public const int MaxReportFolders = 300;

    public static CrashData Read(int maxEvents = 2000)
    {
        var notes = new List<string>();
        var app = Query(CrashEvents.ApplicationChannel, CrashEvents.ApplicationQuery, maxEvents, notes);
        var sys = Query(CrashEvents.SystemChannel, CrashEvents.DisplayQuery, 500, notes);
        var reports = ReadReports(notes);
        return CrashData.FromXml(app, sys, reports, notes);
    }

    /// <summary>Report.wer dos últimos 30 dias (no máximo <see cref="MaxReportFolders"/> pastas, cada arquivo até 1 MB).</summary>
    public static IReadOnlyList<WerReportFile> ReadReports(List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var roots = new List<string>();
        foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            if (!string.IsNullOrEmpty(baseDir))
                foreach (var sub in new[] { "ReportArchive", "ReportQueue" })
                    roots.Add(Path.Combine(baseDir, "Microsoft", "Windows", "WER", sub));
        var since = DateTime.UtcNow.AddDays(-30);
        var folders = new List<DirectoryInfo>();
        foreach (var root in roots)
        {
            try
            {
                var dir = new DirectoryInfo(root);
                if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                folders.AddRange(dir.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint) && d.LastWriteTimeUtc >= since));
            }
            catch (UnauthorizedAccessException) { notes.Add($"Sem permissão para ler {root} (abra o NEXUS como administrador para incluir esses relatórios)."); }
            catch (IOException ex) { notes.Add($"{root}: {ex.Message}"); }
        }
        var list = new List<WerReportFile>();
        foreach (var d in folders.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxReportFolders))
        {
            try
            {
                var file = new FileInfo(Path.Combine(d.FullName, "Report.wer"));
                if (!file.Exists || file.Length > MaxReportBytes || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var text = DecodeReport(File.ReadAllBytes(file.FullName));
                if (CrashEvents.ParseReport(d.Name, text) is { } r) list.Add(r);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return list;
    }

    /// <summary>Report.wer costuma ser UTF-16 com BOM; alguns são UTF-8.</summary>
    public static string DecodeReport(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // UTF-16 sem BOM: muitos zeros nos bytes ímpares.
        int zeros = 0, checkedBytes = 0;
        for (var i = 1; i < Math.Min(bytes.Length, 200); i += 2, checkedBytes++) if (bytes[i] == 0) zeros++;
        return checkedBytes > 0 && zeros * 10 >= checkedBytes * 8 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    private static unsafe List<string> Query(string channel, string query, int max, List<string> notes)
    {
        var list = new List<string>();
        var handle = EvtQuery(IntPtr.Zero, channel, query, EvtQueryChannelPath | EvtQueryReverseDirection);
        if (handle == IntPtr.Zero)
        {
            var e = Marshal.GetLastPInvokeError();
            notes.Add(e switch
            {
                ErrorAccessDenied => $"O log \"{channel}\" exige administrador neste PC.",
                ErrorEvtChannelNotFound => $"O log \"{channel}\" não existe neste Windows.",
                _ => $"O log \"{channel}\" recusou a leitura (código {e}).",
            });
            return list;
        }
        try
        {
            var handles = stackalloc IntPtr[32];
            while (list.Count < max)
            {
                if (!EvtNext(handle, 32, handles, 1000, 0, out var returned))
                {
                    var e = Marshal.GetLastPInvokeError();
                    if (e != ErrorNoMoreItems) notes.Add($"Leitura do log \"{channel}\" interrompida (código {e}).");
                    break;
                }
                for (var i = 0; i < returned; i++)
                {
                    try { if (Render(handles[i]) is { } xml) list.Add(xml); }
                    finally { EvtClose(handles[i]); }
                }
            }
        }
        finally { EvtClose(handle); }
        return list;
    }

    private static string? Render(IntPtr evt)
    {
        if (!EvtRender(IntPtr.Zero, evt, EvtRenderEventXml, 0, IntPtr.Zero, out var used, out _) && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            return null;
        var buffer = Marshal.AllocHGlobal((int)used);
        try
        {
            return EvtRender(IntPtr.Zero, evt, EvtRenderEventXml, used, buffer, out _, out _) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EvtNext(IntPtr resultSet, uint eventsSize, IntPtr* events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtRender(IntPtr context, IntPtr fragment, uint flags, uint bufferSize, IntPtr buffer, out uint bufferUsed, out uint propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(IntPtr handle);
}
