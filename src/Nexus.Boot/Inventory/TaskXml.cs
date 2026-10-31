using System.Xml;
using System.Xml.Linq;

namespace Nexus.Boot.Inventory;

/// <summary>
/// Leitura do XML de tarefas agendadas (esquema do Agendador de Tarefas, learn.microsoft.com/windows/win32/taskschd/task-scheduler-schema),
/// no formato de <c>schtasks /query /xml</c>: várias tarefas seguidas, cada uma precedida por <c>&lt;!-- \caminho --&gt;</c>.
/// Lógica pura: usada como alternativa quando a API COM do Agendador não responde.
/// </summary>
public static class TaskXml
{
    public static IReadOnlyList<ScheduledTaskInfo> ParseMany(string output)
    {
        var list = new List<ScheduledTaskInfo>();
        if (string.IsNullOrEmpty(output)) return list;
        var pos = 0;
        while (true)
        {
            var c = output.IndexOf("<!--", pos, StringComparison.Ordinal);
            if (c < 0) break;
            var ce = output.IndexOf("-->", c, StringComparison.Ordinal);
            if (ce < 0) break;
            var path = output[(c + 4)..ce].Trim();
            var start = output.IndexOf("<Task", ce, StringComparison.Ordinal);
            var end = start < 0 ? -1 : output.IndexOf("</Task>", start, StringComparison.Ordinal);
            var nextComment = output.IndexOf("<!--", ce, StringComparison.Ordinal);
            pos = ce + 3;
            if (start < 0 || end < 0 || (nextComment >= 0 && start > nextComment)) continue;
            if (Parse(path, output[start..(end + 7)]) is { } t) list.Add(t);
            pos = end + 7;
        }
        return list;
    }

    /// <summary>Uma tarefa (elemento &lt;Task&gt;). Null se o XML for inválido.</summary>
    public static ScheduledTaskInfo? Parse(string path, string taskXml)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(taskXml), settings);
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Name.LocalName != "Task") return null;
            XNamespace ns = root.Name.Namespace;
            var uri = root.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value;
            if (string.IsNullOrWhiteSpace(path)) path = uri ?? "";
            if (!path.StartsWith('\\')) path = "\\" + path;

            var triggers = new List<TaskTriggerKind>();
            foreach (var t in root.Element(ns + "Triggers")?.Elements() ?? [])
            {
                var enabled = t.Element(ns + "Enabled")?.Value;
                if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase)) continue;
                triggers.Add(t.Name.LocalName switch
                {
                    "BootTrigger" => TaskTriggerKind.Boot,
                    "LogonTrigger" => TaskTriggerKind.Logon,
                    _ => TaskTriggerKind.Other,
                });
            }
            var taskEnabled = !string.Equals(root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
            var exec = root.Element(ns + "Actions")?.Element(ns + "Exec");
            var principal = root.Element(ns + "Principals")?.Element(ns + "Principal");
            return new ScheduledTaskInfo(
                path,
                taskEnabled,
                triggers,
                exec?.Element(ns + "Command")?.Value.Trim(),
                exec?.Element(ns + "Arguments")?.Value.Trim(),
                root.Element(ns + "RegistrationInfo")?.Element(ns + "Author")?.Value,
                principal?.Element(ns + "UserId")?.Value ?? principal?.Element(ns + "GroupId")?.Value,
                string.Equals(principal?.Element(ns + "RunLevel")?.Value, "HighestAvailable", StringComparison.OrdinalIgnoreCase));
        }
        catch (XmlException) { return null; }
    }
}
