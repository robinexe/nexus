using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Boot.Impact;

namespace Nexus.Boot.Windows;

/// <summary>
/// Lê o canal Microsoft-Windows-Diagnostics-Performance/Operational pela API do Log de Eventos (wevtapi: EvtQuery/EvtNext/
/// EvtRender), do mais novo para o mais antigo. Somente leitura. Sem permissão (o canal costuma exigir administrador ou o
/// grupo "Leitores de Log de Eventos"), devolve um relatório "indisponível" com o motivo, nunca números inventados.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsBootEventReader
{
    private const uint EvtQueryChannelPath = 0x1, EvtQueryReverseDirection = 0x200, EvtRenderEventXml = 1;
    private const int ErrorAccessDenied = 5, ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122,
        ErrorEvtChannelNotFound = 15007, ErrorEvtInvalidQuery = 15001;

    public static BootReport Read(int maxEvents = 400)
    {
        try
        {
            var xml = ReadXml(maxEvents, out var error);
            if (xml is null || (xml.Count == 0 && error is not null)) return BootReport.Unreadable(error ?? "leitura sem resultado.");
            var report = BootReport.FromXml(xml);
            return report.Boots.Count == 0 && report.Slow.Count == 0
                ? BootReport.Unreadable("o Windows ainda não registrou nenhuma inicialização neste log.")
                : report;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return BootReport.Unreadable($"falha ao ler o log ({ex.Message})."); }
    }

    private static unsafe List<string>? ReadXml(int max, out string? error)
    {
        error = null;
        var query = EvtQuery(IntPtr.Zero, BootEvents.Channel, BootEvents.Query, EvtQueryChannelPath | EvtQueryReverseDirection);
        if (query == IntPtr.Zero)
        {
            var e = Marshal.GetLastPInvokeError();
            error = e switch
            {
                ErrorAccessDenied => "ler o log de desempenho do boot exige administrador (ou o grupo \"Leitores de Log de Eventos\"). Abra o NEXUS como administrador para ver.",
                ErrorEvtChannelNotFound => "o log Diagnostics-Performance não existe ou está desativado neste Windows.",
                ErrorEvtInvalidQuery => "consulta recusada pelo Log de Eventos.",
                _ => $"o Log de Eventos recusou a leitura (código {e}).",
            };
            return null;
        }
        var list = new List<string>();
        try
        {
            var handles = stackalloc IntPtr[32];
            while (list.Count < max)
            {
                if (!EvtNext(query, 32, handles, 1000, 0, out var returned))
                {
                    var e = Marshal.GetLastPInvokeError();
                    if (e != ErrorNoMoreItems && list.Count == 0) error = e == ErrorAccessDenied
                        ? "ler o log de desempenho do boot exige administrador."
                        : $"leitura interrompida (código {e}).";
                    break;
                }
                for (var i = 0; i < returned; i++)
                {
                    try { if (Render(handles[i]) is { } xml) list.Add(xml); }
                    finally { EvtClose(handles[i]); }
                }
            }
        }
        finally { EvtClose(query); }
        return list;
    }

    private static string? Render(IntPtr evt)
    {
        if (!EvtRender(IntPtr.Zero, evt, EvtRenderEventXml, 0, IntPtr.Zero, out var used, out _) && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            return null;
        var buffer = Marshal.AllocHGlobal((int)used);
        try
        {
            if (!EvtRender(IntPtr.Zero, evt, EvtRenderEventXml, used, buffer, out _, out _)) return null;
            return Marshal.PtrToStringUni(buffer);
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
