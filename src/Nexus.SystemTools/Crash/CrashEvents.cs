using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nexus.SystemTools.Crash;

// =====================================================================================================================
// "Juntar informações do travamento": o que o próprio Windows registrou quando um jogo fechou sozinho ou o driver de vídeo
// reiniciou. Tudo aqui é leitura pura (testada em qualquer SO): eventos do Log de Eventos renderizados em XML e relatórios do
// Relatório de Erros do Windows (Report.wer). Nada de despejos de memória (.dmp): eles guardam a memória do processo e podem
// conter dados pessoais. Fontes:
//   eventos 1000/1001 (Application Error / Windows Error Reporting) e 4101 (Display, TDR):
//     https://learn.microsoft.com/en-us/archive/technet-wiki/1462.event-id-4101-display-driver-timeout-detection-and-recovery
//   LiveKernelEvent 141/117 (motor da GPU travou e foi reiniciado) e LocalDumps:
//     https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps
// =====================================================================================================================

/// <summary>Um evento do Log de Eventos (XML do EvtRender). <see cref="Data"/> mantém a ordem: os eventos 1000/1001 antigos não têm nome.</summary>
public sealed record LoggedEvent(int EventId, string? Provider, DateTimeOffset TimeUtc, IReadOnlyList<KeyValuePair<string?, string>> Data)
{
    public string? Named(params string[] names)
    {
        foreach (var n in names)
            foreach (var d in Data)
                if (d.Key is not null && string.Equals(d.Key, n, StringComparison.OrdinalIgnoreCase) && d.Value.Length > 0) return d.Value;
        return null;
    }

    public string? At(int index) => index >= 0 && index < Data.Count && Data[index].Value.Length > 0 ? Data[index].Value : null;
}

/// <summary>Programa que fechou com erro (evento 1000, Application Error).</summary>
public sealed record AppCrash(DateTimeOffset TimeUtc, string AppName, string? AppVersion, string? ModuleName, string? ModuleVersion, string? ExceptionCode, string? AppPath);

/// <summary>Relatório do Windows Error Reporting (evento 1001): AppHangB1 (parou de responder), LiveKernelEvent, APPCRASH…</summary>
public sealed record WerEvent(DateTimeOffset TimeUtc, string EventName, string? P1, string? P2);

/// <summary>Reinício do driver de vídeo (evento 4101 do provedor Display: "o driver de vídeo parou de responder e se recuperou").</summary>
public sealed record DisplayReset(DateTimeOffset TimeUtc, string? Driver);

/// <summary>Um Report.wer lido de ReportArchive/ReportQueue, já sem o nome do usuário nos caminhos.</summary>
public sealed record WerReportFile(string Folder, string EventType, string? AppName, DateTimeOffset? TimeUtc, string RedactedText);

public static partial class CrashEvents
{
    public const string ApplicationChannel = "Application";
    public const string SystemChannel = "System";
    /// <summary>Últimos 30 dias (timediff em milissegundos).</summary>
    public const long WindowMs = 30L * 24 * 3600 * 1000;
    public static readonly string ApplicationQuery = string.Create(CultureInfo.InvariantCulture,
        $"*[System[(EventID=1000 or EventID=1001) and TimeCreated[timediff(@SystemTime) <= {WindowMs}]]]");
    public static readonly string DisplayQuery = string.Create(CultureInfo.InvariantCulture,
        $"*[System[Provider[@Name='Display'] and EventID=4101 and TimeCreated[timediff(@SystemTime) <= {WindowMs}]]]");

    /// <summary>Códigos de LiveKernelEvent que são o motor da GPU travando (TDR): 117 e 141 (VIDEO_ENGINE_TIMEOUT_DETECTED), 1a8.</summary>
    public static readonly IReadOnlySet<string> VideoLiveKernelCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "117", "141", "1a8", "116" };

    /// <summary>Lê um evento renderizado em XML. Null quando o XML não é um evento reconhecível (nunca lança).</summary>
    public static LoggedEvent? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var root = XDocument.Load(reader).Root;
            if (root is null) return null;
            XNamespace ns = root.Name.Namespace;
            var system = root.Element(ns + "System");
            if (system is null) return null;
            if (!int.TryParse(system.Element(ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
            var timeText = system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                return null;
            var provider = system.Element(ns + "Provider")?.Attribute("Name")?.Value;
            var data = new List<KeyValuePair<string?, string>>();
            foreach (var d in root.Element(ns + "EventData")?.Elements(ns + "Data") ?? [])
                data.Add(new(d.Attribute("Name")?.Value, d.Value.Trim()));
            return new LoggedEvent(id, provider, time, data);
        }
        catch (XmlException) { return null; }
    }

    /// <summary>Evento 1000: nomes (Windows 11 recente) ou posições (0 app, 1 versão, 3 módulo, 4 versão do módulo, 6 exceção, 10 caminho).</summary>
    public static AppCrash? AsAppCrash(LoggedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.EventId != 1000) return null;
        var named = e.Data.Any(d => d.Key is not null);
        var app = named ? e.Named("AppName") : e.At(0);
        if (string.IsNullOrWhiteSpace(app)) return null;
        return named
            ? new AppCrash(e.TimeUtc, app, e.Named("AppVersion"), e.Named("ModuleName"), e.Named("ModuleVersion"), NormalizeCode(e.Named("ExceptionCode")), e.Named("AppPath"))
            : new AppCrash(e.TimeUtc, app, e.At(1), e.At(3), e.At(4), NormalizeCode(e.At(6)), e.At(10));
    }

    /// <summary>Evento 1001: 2 = nome do evento (APPCRASH, AppHangB1, LiveKernelEvent…), 5 = P1, 6 = P2.</summary>
    public static WerEvent? AsWerEvent(LoggedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.EventId != 1001) return null;
        var name = e.Named("EventName") ?? e.At(2);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new WerEvent(e.TimeUtc, name, e.Named("P1") ?? e.At(5), e.Named("P2") ?? e.At(6));
    }

    public static DisplayReset? AsDisplayReset(LoggedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.EventId != 4101 || !string.Equals(e.Provider, "Display", StringComparison.OrdinalIgnoreCase)) return null;
        return new DisplayReset(e.TimeUtc, e.At(0));
    }

    /// <summary>"0xc0000005" e "c0000005" viram "c0000005".</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        return c.StartsWith("0x", StringComparison.Ordinal) ? c[2..] : c;
    }

    public static bool IsVideoLiveKernel(WerEvent w) =>
        string.Equals(w.EventName, "LiveKernelEvent", StringComparison.OrdinalIgnoreCase) && w.P1 is { } p && VideoLiveKernelCodes.Contains(p.Trim());

    public static bool IsHang(WerEvent w) => w.EventName.StartsWith("AppHang", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Report.wer

    [GeneratedRegex(@"(?i)\b([A-Z]):\\Users\\[^\\\r\n=]+", RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePath();

    /// <summary>Chaves do Report.wer que nunca entram no pacote (identificam a pessoa ou a máquina).</summary>
    private static readonly string[] DroppedKeys = ["MachineName", "UserName", "ComputerName", "OsInfo[", "TargetAppId", "MachineID", "UserSID"];

    /// <summary>Troca "C:\Users\fulano" por "%USERPROFILE%" (o nome da conta não vai para o pacote).</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UserProfilePath().Replace(text, "%USERPROFILE%");
    }

    /// <summary>Lê o texto de um Report.wer (chave=valor). Null se não parecer um relatório do WER.</summary>
    public static WerReportFile? ParseReport(string folderName, string text)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kept = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = line[..eq];
            if (DroppedKeys.Any(k => key.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
            values.TryAdd(key, line[(eq + 1)..]);
            kept.Append(Redact(line)).Append('\n');
        }
        if (!values.TryGetValue("EventType", out var type) || type.Length == 0) return null;
        string? app = null;
        for (var i = 0; i < 12 && app is null; i++)
            if (values.TryGetValue(string.Create(CultureInfo.InvariantCulture, $"Sig[{i}].Name"), out var n) &&
                (n.Equals("Application Name", StringComparison.OrdinalIgnoreCase) || n.Contains("Nome do Aplicativo", StringComparison.OrdinalIgnoreCase) ||
                 n.Equals("AppName", StringComparison.OrdinalIgnoreCase)))
                values.TryGetValue(string.Create(CultureInfo.InvariantCulture, $"Sig[{i}].Value"), out app);
        if (app is null && values.TryGetValue("AppPath", out var appPath)) app = FileNameOf(appPath);
        DateTimeOffset? time = values.TryGetValue("EventTime", out var ft) && long.TryParse(ft, NumberStyles.None, CultureInfo.InvariantCulture, out var fileTime) &&
                               fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc()
            ? DateTimeOffset.FromFileTime(fileTime).ToUniversalTime()
            : null;
        return new WerReportFile(folderName, type, app, time, kept.ToString());
    }

    /// <summary>Relatório que interessa ao pacote: falha de programa (jogo) ou travamento do driver de vídeo.</summary>
    public static bool IsRelevantReport(WerReportFile r, Func<string, string?, bool> isGame)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(isGame);
        if (r.EventType.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase) || r.Folder.StartsWith("Kernel_", StringComparison.OrdinalIgnoreCase)) return true;
        return r.AppName is { } app && (r.EventType.Equals("APPCRASH", StringComparison.OrdinalIgnoreCase) || r.EventType.StartsWith("AppHang", StringComparison.OrdinalIgnoreCase) ||
                                         r.EventType.StartsWith("BEX", StringComparison.OrdinalIgnoreCase)) && isGame(app, null);
    }

    public static string FileNameOf(string path)
    {
        var p = (path ?? "").Replace('/', '\\');
        var i = p.LastIndexOf('\\');
        return i >= 0 ? p[(i + 1)..] : p;
    }
}
