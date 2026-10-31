using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nexus.Automation;

public enum ScheduleFrequency { Daily, Weekly, Monthly }

/// <summary>
/// Quando uma tarefa do NEXUS roda: "DAILY 03:30", "WEEKLY SUN 19:00" ou "MONTHLY 15 19:00" (dia do mês de 1 a 28, para existir
/// em todo mês). Hora local do PC (o Agendador interpreta sem fuso).
/// </summary>
public sealed partial record TaskSchedule(ScheduleFrequency Frequency, DayOfWeek Day, int DayOfMonth, int Hour, int Minute)
{
    /// <summary>Mesma expressão do campo <c>userParameters.schedule</c> do catálogo (o broker valida com ela).</summary>
    public const string Pattern =
        "(?:DAILY|WEEKLY (?:SUN|MON|TUE|WED|THU|FRI|SAT)|MONTHLY (?:[1-9]|1[0-9]|2[0-8])) (?:[01][0-9]|2[0-3]):[0-5][0-9]";

    internal static readonly string[] Codes = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
    private static readonly string[] Names = ["domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado"];

    [GeneratedRegex(@"\A(?:DAILY|WEEKLY (SUN|MON|TUE|WED|THU|FRI|SAT)|MONTHLY ([1-9]|1[0-9]|2[0-8])) ([01][0-9]|2[0-3]):([0-5][0-9])\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();

    public static TaskSchedule Daily(int hour, int minute) => new(ScheduleFrequency.Daily, DayOfWeek.Sunday, 1, hour, minute);
    public static TaskSchedule Weekly(DayOfWeek day, int hour, int minute) => new(ScheduleFrequency.Weekly, day, 1, hour, minute);
    public static TaskSchedule Monthly(int dayOfMonth, int hour, int minute) => new(ScheduleFrequency.Monthly, DayOfWeek.Sunday, dayOfMonth, hour, minute);

    public static TaskSchedule? TryParse(string? text)
    {
        var m = text is null ? null : Regex().Match(text);
        if (m is null || !m.Success) return null;
        var hour = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        if (text!.StartsWith("DAILY", StringComparison.Ordinal)) return Daily(hour, minute);
        if (m.Groups[1].Success) return Weekly((DayOfWeek)Array.IndexOf(Codes, m.Groups[1].Value), hour, minute);
        return Monthly(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), hour, minute);
    }

    /// <summary>Valida os números (o construtor aceita qualquer coisa; use antes de gerar XML).</summary>
    public bool IsValid => Hour is >= 0 and <= 23 && Minute is >= 0 and <= 59 && Enum.IsDefined(Day) && Enum.IsDefined(Frequency)
                           && (Frequency != ScheduleFrequency.Monthly || DayOfMonth is >= 1 and <= 28);

    public override string ToString() => Frequency switch
    {
        ScheduleFrequency.Daily => string.Create(CultureInfo.InvariantCulture, $"DAILY {Hour:00}:{Minute:00}"),
        ScheduleFrequency.Weekly => string.Create(CultureInfo.InvariantCulture, $"WEEKLY {Codes[(int)Day]} {Hour:00}:{Minute:00}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"MONTHLY {DayOfMonth} {Hour:00}:{Minute:00}"),
    };

    /// <summary>"todo dia às 03:30", "toda segunda às 19:00", "todo dia 15 do mês às 19:00".</summary>
    public string Describe() => Frequency switch
    {
        ScheduleFrequency.Daily => string.Create(CultureInfo.InvariantCulture, $"todo dia às {Hour:00}:{Minute:00}"),
        ScheduleFrequency.Weekly => string.Create(CultureInfo.InvariantCulture,
            $"{(Day is DayOfWeek.Sunday or DayOfWeek.Saturday ? "todo" : "toda")} {Names[(int)Day]} às {Hour:00}:{Minute:00}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"todo dia {DayOfMonth} do mês às {Hour:00}:{Minute:00}"),
    };

    /// <summary>Próxima ocorrência estritamente depois de <paramref name="now"/> (hora local).</summary>
    public DateTime NextAfter(DateTime now)
    {
        switch (Frequency)
        {
            case ScheduleFrequency.Daily:
            {
                var d = now.Date.AddHours(Hour).AddMinutes(Minute);
                return d <= now ? d.AddDays(1) : d;
            }
            case ScheduleFrequency.Weekly:
            {
                var d = now.Date.AddDays(((int)Day - (int)now.DayOfWeek + 7) % 7).AddHours(Hour).AddMinutes(Minute);
                return d <= now ? d.AddDays(7) : d;
            }
            default:
            {
                var d = new DateTime(now.Year, now.Month, DayOfMonth, Hour, Minute, 0, now.Kind);
                return d <= now ? d.AddMonths(1) : d;
            }
        }
    }
}

/// <summary>
/// Condições para uma tarefa agendada rodar: "none", "battery" (não rodar na bateria), "game" (não rodar com jogo ou app em tela
/// cheia) ou "battery-game". A tarefa só pula; nunca espera nem tenta de novo sozinha fora do horário.
/// </summary>
public sealed record TaskConditions(bool SkipOnBattery, bool SkipWhenGaming)
{
    /// <summary>Mesma expressão do campo <c>userParameters.conditions</c> do catálogo.</summary>
    public const string Pattern = "none|battery|game|battery-game";

    public static readonly TaskConditions None = new(false, false);
    public static readonly TaskConditions Both = new(true, true);

    public static TaskConditions? TryParse(string? text) => text switch
    {
        "none" => None,
        "battery" => new(true, false),
        "game" => new(false, true),
        "battery-game" => Both,
        _ => null,
    };

    public override string ToString() => (SkipOnBattery, SkipWhenGaming) switch
    {
        (false, false) => "none",
        (true, false) => "battery",
        (false, true) => "game",
        _ => "battery-game",
    };

    public string Describe() => (SkipOnBattery, SkipWhenGaming) switch
    {
        (false, false) => "sem condições",
        (true, false) => "não roda na bateria",
        (false, true) => "não roda com jogo aberto ou app em tela cheia",
        _ => "não roda na bateria nem com jogo aberto ou app em tela cheia",
    };

    /// <summary>Flags da linha de comando (fixas, sem texto livre).</summary>
    public string Flags => (SkipOnBattery ? " --skip-on-battery" : "") + (SkipWhenGaming ? " --skip-when-gaming" : "");

    public static IReadOnlyList<TaskConditions> All { get; } = [None, new(true, false), new(false, true), Both];
}

/// <summary>Os tipos de tarefa que o NEXUS sabe agendar. Cada um tem nome, marcador e argumentos FIXOS.</summary>
public sealed record NexusTaskKind(string Id, string TaskName, string Marker, string Verb, string Title, string Explanation, string TimeLimit, string ActionId)
{
    public static readonly NexusTaskKind Scan = new(
        "scan", "NEXUS-Verificacao", "nexus.automation.task.scan/1", "scan --report --quiet --scheduled",
        "Verificação agendada (somente leitura)",
        "Lê o PC e grava um relatório em %LocalAppData%\\NEXUS\\reports. Não altera nada.",
        "PT15M", "automation.schedule.scan");

    public static readonly NexusTaskKind Clean = new(
        "clean", "NEXUS-LimpezaSegura", "nexus.automation.task.clean/1", "clean --safe --scheduled --quiet",
        "Limpeza segura agendada",
        "Move para a quarentena do NEXUS só caches e temporários de risco baixo da sua conta (nunca de administrador, histórico ou cookies). Tudo fica no diário e pode ser desfeito.",
        "PT30M", "automation.schedule.clean");

    public static IReadOnlyList<NexusTaskKind> All { get; } = [Scan, Clean];

    public static NexusTaskKind? Find(string? id) => All.FirstOrDefault(k => k.Id == id);

    public string ArgumentsFor(TaskConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return Verb + conditions.Flags;
    }

    /// <summary>Quais condições correspondem a estes argumentos (null = argumentos que o NEXUS não gera).</summary>
    public TaskConditions? ConditionsFromArguments(string? arguments) =>
        TaskConditions.All.FirstOrDefault(c => string.Equals(ArgumentsFor(c), arguments, StringComparison.Ordinal));
}

/// <summary>O que se leu de uma tarefa no Agendador.</summary>
public sealed record NexusTaskInfo(bool Ours, string? Command, string? Arguments, TaskSchedule? Schedule, TaskConditions? Conditions, bool DisallowOnBatteries);

/// <summary>
/// XML das tarefas do NEXUS (esquema 1.2 do Agendador). Gerado com <see cref="XmlWriter"/> (escapa tudo) a partir de um caminho
/// validado e de argumentos fixos; lido sem DTD nem entidades externas.
/// </summary>
public static class NexusTaskXml
{
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly string[] Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public static string Build(NexusTaskKind kind, string executable, TaskSchedule schedule, TaskConditions conditions, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(conditions);
        if (ScheduledScanTask.ValidateExecutable(executable) is { } problem) throw new ArgumentException(problem, nameof(executable));
        if (!schedule.IsValid) throw new ArgumentException("Horário inválido.", nameof(schedule));
        XNamespace ns = Ns;
        var start = schedule.NextAfter(now).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        XElement plan = schedule.Frequency switch
        {
            ScheduleFrequency.Daily => new XElement(ns + "ScheduleByDay", new XElement(ns + "DaysInterval", "1")),
            ScheduleFrequency.Weekly => new XElement(ns + "ScheduleByWeek",
                new XElement(ns + "DaysOfWeek", new XElement(ns + schedule.Day.ToString())),
                new XElement(ns + "WeeksInterval", "1")),
            _ => new XElement(ns + "ScheduleByMonth",
                new XElement(ns + "DaysOfMonth", new XElement(ns + "Day", schedule.DayOfMonth.ToString(CultureInfo.InvariantCulture))),
                new XElement(ns + "Months", Months.Select(m => new XElement(ns + m)))),
        };
        var battery = conditions.SkipOnBattery ? "true" : "false";
        var doc = new XDocument(
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Author", "NEXUS"),
                    new XElement(ns + "Description",
                        $"NEXUS: {kind.Title} ({schedule.Describe()}; {conditions.Describe()}). {kind.Explanation} Para remover, desligue no NEXUS (Ajustes). [{kind.Marker}]"),
                    new XElement(ns + "URI", "\\" + kind.TaskName)),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", start),
                        new XElement(ns + "Enabled", "true"),
                        plan)),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", battery),
                    new XElement(ns + "StopIfGoingOnBatteries", battery),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", kind.TimeLimit),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", kind.ArgumentsFor(conditions)),
                        new XElement(ns + "WorkingDirectory", executable[..executable.LastIndexOf('\\')])))));
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
            doc.Save(w);
        return sb.ToString();
    }

    /// <summary>Lê o XML de uma tarefa. <see cref="NexusTaskInfo.Ours"/> só é verdadeiro com o marcador DESTE tipo e uma única ação.</summary>
    public static NexusTaskInfo Inspect(string xml, NexusTaskKind kind)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(kind);
        if (xml.Length > ScheduledScanTask.MaxXmlChars * 4) throw new XmlException("XML da tarefa grande demais.");
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc = XDocument.Load(reader);
        XNamespace ns = Ns;
        var root = doc.Root ?? throw new XmlException("XML vazio.");
        if (root.Name != ns + "Task") throw new XmlException("Não é o XML de uma tarefa agendada.");
        var description = root.Element(ns + "RegistrationInfo")?.Element(ns + "Description")?.Value ?? "";
        var actions = root.Element(ns + "Actions");
        var exec = actions?.Elements(ns + "Exec").ToList() ?? [];
        var command = exec.Count == 1 ? exec[0].Element(ns + "Command")?.Value?.Trim().Trim('"') : null;
        var args = exec.Count == 1 ? exec[0].Element(ns + "Arguments")?.Value?.Trim() : null;
        var ours = description.Contains(kind.Marker, StringComparison.Ordinal) && exec.Count == 1 && actions!.Elements().Count() == 1;
        var battery = string.Equals(root.Element(ns + "Settings")?.Element(ns + "DisallowStartIfOnBatteries")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        TaskSchedule? schedule = null;
        var triggers = root.Element(ns + "Triggers")?.Elements().ToList() ?? [];
        if (triggers.Count == 1 && triggers[0].Name == ns + "CalendarTrigger"
            && DateTime.TryParse(triggers[0].Element(ns + "StartBoundary")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            var t = triggers[0];
            if (t.Element(ns + "ScheduleByDay") is { } byDay && byDay.Element(ns + "DaysInterval")?.Value is null or "1")
                schedule = TaskSchedule.Daily(at.Hour, at.Minute);
            else if (t.Element(ns + "ScheduleByWeek") is { } week && week.Element(ns + "WeeksInterval")?.Value is null or "1"
                     && week.Element(ns + "DaysOfWeek")?.Elements().ToList() is { Count: 1 } days
                     && Enum.TryParse<DayOfWeek>(days[0].Name.LocalName, out var day) && Enum.IsDefined(day))
                schedule = TaskSchedule.Weekly(day, at.Hour, at.Minute);
            else if (t.Element(ns + "ScheduleByMonth") is { } month
                     && month.Element(ns + "DaysOfMonth")?.Elements(ns + "Day").ToList() is { Count: 1 } dom
                     && int.TryParse(dom[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 28
                     && (month.Element(ns + "Months")?.Elements().Count() ?? 12) == 12)
                schedule = TaskSchedule.Monthly(d, at.Hour, at.Minute);
        }
        return new NexusTaskInfo(ours, command, args, schedule, kind.ConditionsFromArguments(args), battery);
    }
}
