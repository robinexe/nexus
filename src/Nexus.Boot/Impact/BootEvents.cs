using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Nexus.Boot.Impact;

/// <summary>Evento do canal Microsoft-Windows-Diagnostics-Performance/Operational (XML de EvtRender).</summary>
public sealed record DiagnosticsEvent(int EventId, DateTimeOffset TimeUtc, IReadOnlyDictionary<string, string> Data)
{
    public string? Get(params string[] names)
    {
        foreach (var n in names)
            if (Data.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    public long? Milliseconds(params string[] names) =>
        long.TryParse(Get(names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
}

/// <summary>Uma inicialização registrada (evento 100).</summary>
/// <param name="TotalMs">BootTime: do início do boot até o fim da fase pós-área de trabalho.</param>
/// <param name="MainPathMs">MainPathBootTime: até a área de trabalho aparecer.</param>
/// <param name="PostBootMs">BootPostBootTime: depois da área de trabalho até o sistema ficar ocioso.</param>
public sealed record BootRecord(DateTimeOffset TimeUtc, long TotalMs, long? MainPathMs, long? PostBootMs, bool IsDegradation, int? StartupApps);

public enum SlowKind { Application, Driver, Service, Devices, Other }

/// <summary>Componente que atrasou a inicialização (eventos 101 a 110).</summary>
public sealed record SlowComponent(int EventId, SlowKind Kind, DateTimeOffset TimeUtc, string Name, string? FriendlyName, long TotalMs, long? DegradationMs)
{
    public string Label => string.IsNullOrWhiteSpace(FriendlyName) ? Name : $"{FriendlyName} ({Name})";
}

public static class BootEvents
{
    public const string Channel = "Microsoft-Windows-Diagnostics-Performance/Operational";
    /// <summary>Consulta XPath do leitor: o boot (100) e os componentes lentos no boot (101 a 110).</summary>
    public const string Query = "*[System[(EventID>=100 and EventID<=110)]]";

    /// <summary>Lê um evento renderizado em XML. Retorna null se o XML não for um evento reconhecível (nunca lança).</summary>
    public static DiagnosticsEvent? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = XDocument.Load(reader);
            var root = doc.Root;
            if (root is null) return null;
            XNamespace ns = root.Name.Namespace;
            var system = root.Element(ns + "System");
            if (system is null) return null;
            if (!int.TryParse(system.Element(ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
            var timeText = system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                return null;
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in root.Element(ns + "EventData")?.Elements(ns + "Data") ?? [])
            {
                var name = d.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name)) data[name] = d.Value.Trim();
            }
            return new DiagnosticsEvent(id, time, data);
        }
        catch (XmlException) { return null; }
    }

    public static SlowKind KindOf(int eventId) => eventId switch
    {
        101 => SlowKind.Application,
        102 => SlowKind.Driver,
        103 => SlowKind.Service,
        109 => SlowKind.Devices,
        _ => SlowKind.Other,
    };

    public static string KindLabel(SlowKind kind) => kind switch
    {
        SlowKind.Application => "programa",
        SlowKind.Driver => "driver",
        SlowKind.Service => "serviço",
        SlowKind.Devices => "dispositivo",
        _ => "etapa do sistema",
    };

    public static BootRecord? AsBoot(DiagnosticsEvent e)
    {
        if (e.EventId != 100) return null;
        var total = e.Milliseconds("BootTime");
        if (total is null || total == 0) return null;
        var degradation = string.Equals(e.Get("BootIsDegradation"), "true", StringComparison.OrdinalIgnoreCase) || e.Get("BootIsDegradation") == "1";
        int? apps = int.TryParse(e.Get("BootNumStartupApps"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new BootRecord(e.TimeUtc, total.Value, e.Milliseconds("MainPathBootTime"), e.Milliseconds("BootPostBootTime"), degradation, apps);
    }

    public static SlowComponent? AsSlow(DiagnosticsEvent e)
    {
        if (e.EventId is < 101 or > 110) return null;
        var total = e.Milliseconds("TotalTime");
        if (total is null) return null;
        var name = e.Get("Name", "FileName", "ServiceName", "Path") ?? e.Get("FriendlyName");
        if (string.IsNullOrWhiteSpace(name)) name = $"evento {e.EventId}";
        return new SlowComponent(e.EventId, KindOf(e.EventId), e.TimeUtc, name, e.Get("FriendlyName"), total.Value, e.Milliseconds("DegradationTime"));
    }
}

/// <summary>Impacto de um item de inicialização, sempre com o motivo.</summary>
public enum ImpactLevel { Unknown, Low, Medium, High }

public sealed record ImpactAssessment(ImpactLevel Level, string Reason)
{
    public string Label => Level switch
    {
        ImpactLevel.High => "Alto",
        ImpactLevel.Medium => "Médio",
        ImpactLevel.Low => "Baixo",
        _ => "Desconhecido",
    };
}

/// <summary>Histórico de inicializações + componentes lentos, montado a partir dos eventos lidos.</summary>
public sealed class BootReport
{
    /// <summary>Limites do NEXUS (heurística própria, não da Microsoft): tempo que o Windows mediu para o item no boot.</summary>
    public const long HighMs = 3000, MediumMs = 1000;

    public IReadOnlyList<BootRecord> Boots { get; }
    public IReadOnlyList<SlowComponent> Slow { get; }
    /// <summary>Null quando os eventos foram lidos; senão, por que não há dados (sem permissão, canal desativado...).</summary>
    public string? Unavailable { get; }

    public BootReport(IEnumerable<BootRecord> boots, IEnumerable<SlowComponent> slow, string? unavailable = null)
    {
        Boots = boots.OrderBy(b => b.TimeUtc).ToList();
        Slow = slow.OrderBy(s => s.TimeUtc).ToList();
        Unavailable = unavailable;
    }

    public static BootReport Unreadable(string reason) => new([], [], reason);

    public static BootReport FromXml(IEnumerable<string> xmlEvents)
    {
        var events = xmlEvents.Select(BootEvents.Parse).OfType<DiagnosticsEvent>().ToList();
        return new BootReport(events.Select(BootEvents.AsBoot).OfType<BootRecord>(), events.Select(BootEvents.AsSlow).OfType<SlowComponent>());
    }

    public BootRecord? Last => Boots.Count == 0 ? null : Boots[^1];

    /// <summary>Componentes lentos do boot <paramref name="boot"/>: registrados entre o boot anterior e o seguinte, perto deste.</summary>
    public IReadOnlyList<SlowComponent> SlowFor(BootRecord boot)
    {
        var i = Boots.ToList().IndexOf(boot);
        var next = i >= 0 && i + 1 < Boots.Count ? Boots[i + 1].TimeUtc : DateTimeOffset.MaxValue;
        var from = boot.TimeUtc - TimeSpan.FromMinutes(30);
        var to = boot.TimeUtc + TimeSpan.FromMinutes(30);
        return Slow.Where(s => s.TimeUtc >= from && s.TimeUtc <= to && s.TimeUtc < next).OrderByDescending(s => s.TotalMs).ToList();
    }

    /// <summary>Maior atraso do último boot registrado (null se o Windows não registrou nenhum).</summary>
    public SlowComponent? MainDelayOfLastBoot => Last is { } b && SlowFor(b) is { Count: > 0 } s ? s[0] : null;

    /// <summary>
    /// Impacto pelo que o Windows mediu: o maior tempo registrado para o executável (101: programa, 103: serviço). Sem registro, o
    /// impacto é desconhecido, não "baixo": o Windows só registra componentes que passaram do próprio limite dele.
    /// </summary>
    public ImpactAssessment ImpactOf(string? executableFileName, string? serviceName = null)
    {
        if (Unavailable is not null) return new(ImpactLevel.Unknown, "Sem dados de desempenho do boot: " + Unavailable);
        var matches = Slow.Where(s =>
            (executableFileName is not null && s.Kind == SlowKind.Application && SameFile(s.Name, executableFileName)) ||
            (serviceName is not null && s.Kind == SlowKind.Service && (string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase) ||
                                                                        SameFile(s.Name, executableFileName)))).ToList();
        if (matches.Count == 0)
            return new(ImpactLevel.Unknown, "O Windows não registrou atraso deste item nos boots guardados (ele só registra o que passa do limite dele).");
        var worst = matches.MaxBy(m => m.TotalMs)!;
        var level = worst.TotalMs >= HighMs ? ImpactLevel.High : worst.TotalMs >= MediumMs ? ImpactLevel.Medium : ImpactLevel.Low;
        var times = matches.Count == 1 ? "1 vez" : $"{matches.Count} vezes";
        return new(level, $"O Windows registrou este item atrasando o boot {times}; pior caso {Seconds(worst.TotalMs)} ({worst.TimeUtc.ToLocalTime():dd/MM}).");
    }

    private static bool SameFile(string eventName, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var a = Inventory.CommandLine.FileNameOf(eventName);
        return string.Equals(a, fileName, StringComparison.OrdinalIgnoreCase);
    }

    public static string Seconds(long ms) =>
        ms >= 10_000 ? $"{ms / 1000.0:0} s".Replace('.', ',') : $"{ms / 1000.0:0.0} s".Replace('.', ',');
}
