using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.GameProfiles.SourceCfg;

/// <summary>
/// Documento .cfg do Source/Source 2 editado sem perdas: cada linha é preservada byte a byte,
/// exceto a linha de atribuição alterada. Linhas compostas (com ';'), binds, aliases e execs
/// são opacas e nunca reescritas.
/// </summary>
public sealed partial class SourceCfgDocument
{
    public sealed record Line(string Raw, string? Name, string? Value, bool Quoted, string Indent, string Trailing)
    {
        public bool IsAssignment => Name is not null;
        public bool IsTagged => Trailing.Contains("nexus:", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> NonConvarCommands = new(StringComparer.OrdinalIgnoreCase)
        { "bind", "unbind", "unbindall", "alias", "exec", "echo", "say", "host_writeconfig", "toggle", "incrementvar" };

    // indent, name, ws, ("quoted" | token), rest (whitespace + optional // comment)
    [GeneratedRegex("""^(?<indent>\s*)(?<name>[A-Za-z_][A-Za-z0-9_\.]*)(?<ws>\s+)(?:"(?<q>[^"]*)"|(?<v>[^\s";/]+))(?<rest>\s*(?://.*)?)$""")]
    private static partial Regex AssignmentRegex();

    private readonly List<Line> _lines;
    public string NewLine { get; }
    public bool EndsWithNewLine { get; }

    private SourceCfgDocument(List<Line> lines, string newLine, bool endsWithNewLine)
    {
        _lines = lines;
        NewLine = newLine;
        EndsWithNewLine = endsWithNewLine;
    }

    public IReadOnlyList<Line> Lines => _lines;

    public static SourceCfgDocument Parse(string text)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var ends = text.EndsWith('\n');
        var rawLines = text.Length == 0 ? [] : text.Split(nl).ToList();
        if (ends && rawLines.Count > 0) rawLines.RemoveAt(rawLines.Count - 1);
        return new SourceCfgDocument(rawLines.Select(ParseLine).ToList(), nl, ends || text.Length == 0);
    }

    private static Line ParseLine(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            return new Line(raw, null, null, false, "", "");
        var codePart = StripComment(raw);
        if (codePart.Contains(';')) return new Line(raw, null, null, false, "", ""); // linha composta: opaca
        var m = AssignmentRegex().Match(raw);
        if (!m.Success || NonConvarCommands.Contains(m.Groups["name"].Value))
            return new Line(raw, null, null, false, "", "");
        var quoted = m.Groups["q"].Success;
        return new Line(raw, m.Groups["name"].Value, quoted ? m.Groups["q"].Value : m.Groups["v"].Value, quoted,
            m.Groups["indent"].Value, m.Groups["rest"].Value);
    }

    // Para busca de nomes dentro de alias "...": remove só o comentário, mantendo o conteúdo entre aspas.
    private static string StripCommentKeepQuotes(string s) => StripComment(s);

    private static string StripComment(string s)
    {
        var inQuote = false;
        for (var i = 0; i < s.Length - 1; i++)
        {
            if (s[i] == '"') inQuote = !inQuote;
            else if (!inQuote && s[i] == '/' && s[i + 1] == '/') return s[..i];
        }
        return s;
    }

    /// <summary>Último valor atribuído (o efetivo quando o arquivo é executado).</summary>
    public string? GetEffective(string name) =>
        _lines.LastOrDefault(l => l.IsAssignment && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Indica se o nome aparece em uma linha opaca (composta), que o editor não altera.</summary>
    public bool AppearsInOpaqueLine(string name) =>
        _lines.Any(l => !l.IsAssignment && !l.Raw.TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                        Regex.IsMatch(StripCommentKeepQuotes(l.Raw), $@"(^|[\s;""]){Regex.Escape(name)}(\s|$|[;""])", RegexOptions.IgnoreCase));

    public int CountAssignments(string name) =>
        _lines.Count(l => l.IsAssignment && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Set(string name, string value, string tag)
    {
        if (value.Length == 0 || value.Contains('"') || value.Contains('\n') || value.Contains('\r') || value.Contains(';'))
            throw new ArgumentException("Valor contém caracteres não permitidos.", nameof(value));
        var idx = _lines.FindLastIndex(l => l.IsAssignment && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var l = _lines[idx];
            var formatted = l.Quoted || value.Contains(' ') ? $"\"{value}\"" : value;
            var raw = $"{l.Indent}{l.Name} {formatted}{l.Trailing}";
            _lines[idx] = l with { Raw = raw, Value = value, Quoted = l.Quoted || value.Contains(' ') };
            return;
        }
        var trailing = $" // nexus:{tag}";
        _lines.Add(new Line($"{name} \"{value}\"{trailing}", name, value, true, "", trailing));
    }

    /// <summary>Remove somente atribuições marcadas pelo NEXUS para este nome. Retorna quantas removeu.</summary>
    public int RemoveTagged(string name) =>
        _lines.RemoveAll(l => l.IsAssignment && l.IsTagged && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _lines.Count; i++)
        {
            sb.Append(_lines[i].Raw);
            if (i < _lines.Count - 1 || EndsWithNewLine) sb.Append(NewLine);
        }
        return sb.ToString();
    }

    /// <summary>Diff linha a linha para a aba "Alterações técnicas".</summary>
    public static IReadOnlyList<string> Diff(string before, string after)
    {
        var a = Parse(before).Lines.Select(l => l.Raw).ToList();
        var b = Parse(after).Lines.Select(l => l.Raw).ToList();
        var result = new List<string>();
        var max = Math.Max(a.Count, b.Count);
        for (var i = 0; i < max; i++)
        {
            var x = i < a.Count ? a[i] : null;
            var y = i < b.Count ? b[i] : null;
            if (x == y) continue;
            if (x is not null) result.Add($"- {i + 1}: {x}");
            if (y is not null) result.Add($"+ {i + 1}: {y}");
        }
        return result;
    }
}
