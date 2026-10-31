using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Nexus.Automation;

/// <summary>Dia e hora da verificação semanal ("SUN 19:00").</summary>
public sealed partial record ScanSchedule(DayOfWeek Day, int Hour, int Minute)
{
    public const string Off = "OFF";
    private static readonly string[] Codes = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
    private static readonly string[] Names = ["domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado"];

    [GeneratedRegex(@"^(SUN|MON|TUE|WED|THU|FRI|SAT) ([01][0-9]|2[0-3]):([0-5][0-9])\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static ScanSchedule? TryParse(string? text)
    {
        var m = text is null ? null : Pattern().Match(text);
        if (m is null || !m.Success) return null;
        return new((DayOfWeek)Array.IndexOf(Codes, m.Groups[1].Value),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Codes[(int)Day]} {Hour:00}:{Minute:00}");

    /// <summary>"domingo às 19:00".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Names[(int)Day]} às {Hour:00}:{Minute:00}");

    /// <summary>Próxima ocorrência (hora local, sem fuso: o Agendador interpreta como hora local).</summary>
    public DateTime NextAfter(DateTime now)
    {
        var date = now.Date.AddDays(((int)Day - (int)now.DayOfWeek + 7) % 7).AddHours(Hour).AddMinutes(Minute);
        return date <= now ? date.AddDays(7) : date;
    }
}

/// <summary>O que se sabe de uma tarefa agendada lida do Agendador.</summary>
public sealed record ScheduledTaskInfo(bool Ours, string? Command, string? Arguments, ScanSchedule? Schedule);

/// <summary>
/// Tarefa do Agendador do Windows que roda <c>nexus scan --report</c> (SOMENTE LEITURA) uma vez por semana, na conta do usuário,
/// sem elevação. O XML é gerado com <see cref="XmlWriter"/> (escapa tudo) a partir de um caminho validado: nada vem de texto livre.
/// Decisão de segurança: o NEXUS NUNCA agenda ações que escrevem; aplicar é sempre com o usuário presente e confirmando.
/// </summary>
public static partial class ScheduledScanTask
{
    public const string TaskName = "NEXUS-VerificacaoSemanal";
    public const string Arguments = "scan --report --quiet";
    public const string Marker = "nexus.automation.scheduled-scan/1";
    public const string ExecutableName = "nexus.exe";
    public const int MaxXmlChars = 32 * 1024;
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [GeneratedRegex(@"^[A-Za-z]:\\(?:[^\\/:*?""<>|\x00-\x1F%]+\\)*[^\\/:*?""<>|\x00-\x1F%]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPath();

    /// <summary>
    /// Valida o executável que a tarefa vai rodar: caminho local absoluto (letra de unidade; sem rede/UNC), sem aspas, sem
    /// <c>%</c> (o Agendador expandiria variáveis), sem caracteres de controle, sem "." ou ".." como pasta, terminando em nexus.exe.
    /// Null = válido; senão, o motivo.
    /// </summary>
    public static string? ValidateExecutable(string? path) => ValidateExecutable(path, ExecutableName);

    /// <summary>Mesma validação para outro executável do NEXUS (ex.: Nexus.Desktop.exe ao iniciar com o Windows).</summary>
    public static string? ValidateExecutable(string? path, string expectedName)
    {
        ArgumentNullException.ThrowIfNull(expectedName);
        if (string.IsNullOrWhiteSpace(path)) return $"Caminho do {expectedName} vazio.";
        if (path.Length > 260) return $"Caminho do {expectedName} longo demais.";
        if (!LocalPath().IsMatch(path)) return $"O {expectedName} precisa estar numa pasta local (C:\\...) sem aspas, % ou caracteres especiais.";
        var parts = path.Split('\\');
        if (parts.Any(p => p is "." or ".." || p != p.Trim() || p.EndsWith('.'))) return $"Caminho do {expectedName} com pastas relativas ou nomes inválidos.";
        if (!parts[^1].Equals(expectedName, StringComparison.OrdinalIgnoreCase)) return $"Só o {expectedName} é aceito aqui.";
        return null;
    }

    /// <summary>Gera o XML da tarefa (esquema 1.2 do Agendador). Lança <see cref="ArgumentException"/> se o caminho não passar na validação.</summary>
    public static string BuildXml(string executable, ScanSchedule schedule, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (ValidateExecutable(executable) is { } problem) throw new ArgumentException(problem, nameof(executable));
        var start = schedule.NextAfter(now).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var day = schedule.Day.ToString(); // Sunday, Monday... (nomes do esquema)
        XNamespace ns = Ns;
        var doc = new XDocument(
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Author", "NEXUS"),
                    new XElement(ns + "Description",
                        $"NEXUS: verificação semanal somente leitura ({schedule.Describe()}). Gera um relatório em %LocalAppData%\\NEXUS\\reports e não altera nada no PC. Para remover, desligue o agendamento no NEXUS. [{Marker}]"),
                    new XElement(ns + "URI", "\\" + TaskName)),
                new XElement(ns + "Triggers",
                    new XElement(ns + "CalendarTrigger",
                        new XElement(ns + "StartBoundary", start),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "ScheduleByWeek",
                            new XElement(ns + "DaysOfWeek", new XElement(ns + day)),
                            new XElement(ns + "WeeksInterval", "1")))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
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
                    new XElement(ns + "ExecutionTimeLimit", "PT15M"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", Arguments),
                        new XElement(ns + "WorkingDirectory", executable[..executable.LastIndexOf('\\')])))));
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
            doc.Save(w);
        return sb.ToString();
    }

    /// <summary>Lê o XML de uma tarefa (sem DTD, sem entidades externas). Lança <see cref="XmlException"/> se não for XML de tarefa.</summary>
    public static ScheduledTaskInfo Inspect(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (xml.Length > MaxXmlChars * 4) throw new XmlException("XML da tarefa grande demais.");
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc = XDocument.Load(reader);
        XNamespace ns = Ns;
        var root = doc.Root ?? throw new XmlException("XML vazio.");
        if (root.Name != ns + "Task") throw new XmlException("Não é o XML de uma tarefa agendada.");
        var description = root.Element(ns + "RegistrationInfo")?.Element(ns + "Description")?.Value ?? "";
        var exec = root.Element(ns + "Actions")?.Elements(ns + "Exec").ToList() ?? [];
        var command = exec.Count == 1 ? exec[0].Element(ns + "Command")?.Value?.Trim().Trim('"') : null;
        var args = exec.Count == 1 ? exec[0].Element(ns + "Arguments")?.Value?.Trim() : null;
        var ours = description.Contains(Marker, StringComparison.Ordinal) && exec.Count == 1 && root.Element(ns + "Actions")!.Elements().Count() == 1;

        ScanSchedule? schedule = null;
        var triggers = root.Element(ns + "Triggers")?.Elements().ToList() ?? [];
        if (triggers.Count == 1 && triggers[0].Name == ns + "CalendarTrigger"
            && DateTime.TryParse(triggers[0].Element(ns + "StartBoundary")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startAt))
        {
            var week = triggers[0].Element(ns + "ScheduleByWeek");
            var days = week?.Element(ns + "DaysOfWeek")?.Elements().ToList() ?? [];
            if (days.Count == 1 && Enum.TryParse<DayOfWeek>(days[0].Name.LocalName, out var day) && week?.Element(ns + "WeeksInterval")?.Value is null or "1")
                schedule = new ScanSchedule(day, startAt.Hour, startAt.Minute);
        }
        return new ScheduledTaskInfo(ours, command, args, schedule);
    }
}
