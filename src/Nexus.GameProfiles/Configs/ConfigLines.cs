namespace Nexus.GameProfiles.Configs;

/// <summary>Sintaxe de uma linha "chave + valor" num arquivo de configuração de jogo.</summary>
public enum ConfigSyntax
{
    /// <summary><c>Chave=Valor</c> ou <c>Chave = "Valor"</c> (.ini do R6, Overwatch 2…). Comentários com ';' ou '#'.</summary>
    Equals,
    /// <summary><c>chave:valor</c> (options.txt do Minecraft Java).</summary>
    Colon,
    /// <summary><c>"chave"   "valor"</c> (KeyValues da Valve/Respawn: videoconfig.txt do Apex).</summary>
    KeyValues,
}

/// <summary>Como uma chave aparece no arquivo: ausente, uma vez (com o valor) ou repetida (o editor não mexe).</summary>
public enum ConfigKeyState { Absent, Present, Ambiguous }

/// <summary>
/// Arquivo de configuração editado sem perdas: toda linha fica como estava (ordem, espaços, comentários, aspas, quebra de linha);
/// só o VALOR da linha da chave muda. A chave é procurada no arquivo inteiro (sem diferenciar maiúsculas) e precisa aparecer uma
/// vez só; repetida = ambígua, o editor recusa. O editor NUNCA acrescenta chave: só troca o valor de uma que o jogo já gravou
/// (assim nada estranho entra no arquivo e desfazer é só devolver o valor). Valor com aspas continua com aspas.
/// </summary>
public sealed class ConfigLines
{
    private sealed record Line(string Raw, string? Key, int ValueStart, int ValueLength);

    private readonly List<Line> _lines;
    private readonly ConfigSyntax _syntax;

    private ConfigLines(List<Line> lines, ConfigSyntax syntax, string newLine, bool endsWithNewLine)
    {
        _lines = lines;
        _syntax = syntax;
        NewLine = newLine;
        EndsWithNewLine = endsWithNewLine;
    }

    public string NewLine { get; }
    public bool EndsWithNewLine { get; }

    public static ConfigLines Parse(string text, ConfigSyntax syntax)
    {
        ArgumentNullException.ThrowIfNull(text);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var ends = text.EndsWith('\n');
        var raw = text.Length == 0 ? [] : text.Split(nl).ToList();
        if (ends && raw.Count > 0) raw.RemoveAt(raw.Count - 1);
        return new ConfigLines(raw.Select(r => ParseLine(r, syntax)).ToList(), syntax, nl, ends || text.Length == 0);
    }

    private static Line ParseLine(string raw, ConfigSyntax syntax)
    {
        var t = raw.TrimStart();
        if (t.Length == 0 || t[0] is ';' or '#' || t.StartsWith("//", StringComparison.Ordinal) || t[0] == '[') return new Line(raw, null, 0, 0);
        return syntax switch
        {
            ConfigSyntax.Equals => Separated(raw, '='),
            ConfigSyntax.Colon => Separated(raw, ':'),
            _ => KeyValuesLine(raw),
        };
    }

    /// <summary>"chave SEP valor": o valor vai do primeiro caractere não-espaço depois do separador até o fim (sem espaços finais);
    /// entre aspas, só o miolo é o valor (as aspas ficam).</summary>
    private static Line Separated(string raw, char sep)
    {
        var at = raw.IndexOf(sep, StringComparison.Ordinal);
        if (at <= 0) return new Line(raw, null, 0, 0);
        var key = raw[..at].Trim();
        if (key.Length == 0 || key.Any(char.IsWhiteSpace) || key.Contains('"', StringComparison.Ordinal)) return new Line(raw, null, 0, 0);
        var start = at + 1;
        while (start < raw.Length && raw[start] is ' ' or '\t') start++;
        var end = raw.Length;
        while (end > start && raw[end - 1] is ' ' or '\t') end--;
        if (end - start >= 2 && raw[start] == '"' && raw[end - 1] == '"') { start++; end--; }
        return new Line(raw, key, start, end - start);
    }

    /// <summary><c>"chave" "valor"</c>: duas strings entre aspas na linha (nada depois além de espaço ou comentário //).</summary>
    private static Line KeyValuesLine(string raw)
    {
        var q1 = raw.IndexOf('"', StringComparison.Ordinal);
        if (q1 < 0) return new Line(raw, null, 0, 0);
        var q2 = raw.IndexOf('"', q1 + 1);
        if (q2 < 0) return new Line(raw, null, 0, 0);
        var q3 = raw.IndexOf('"', q2 + 1);
        if (q3 < 0) return new Line(raw, null, 0, 0);
        var q4 = raw.IndexOf('"', q3 + 1);
        if (q4 < 0) return new Line(raw, null, 0, 0);
        if (raw[(q2 + 1)..q3].Trim().Length != 0) return new Line(raw, null, 0, 0);
        var rest = raw[(q4 + 1)..].Trim();
        if (rest.Length != 0 && !rest.StartsWith("//", StringComparison.Ordinal)) return new Line(raw, null, 0, 0);
        var key = raw[(q1 + 1)..q2];
        return key.Length == 0 ? new Line(raw, null, 0, 0) : new Line(raw, key, q3 + 1, q4 - q3 - 1);
    }

    private int Locate(string key, out ConfigKeyState state)
    {
        ArgumentNullException.ThrowIfNull(key);
        state = ConfigKeyState.Absent;
        var found = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (!string.Equals(_lines[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            if (found >= 0) { state = ConfigKeyState.Ambiguous; return -1; }
            found = i;
        }
        if (found >= 0) state = ConfigKeyState.Present;
        return found;
    }

    public (ConfigKeyState State, string? Value) Get(string key)
    {
        var at = Locate(key, out var state);
        if (state != ConfigKeyState.Present) return (state, null);
        var l = _lines[at];
        return (state, l.Raw.Substring(l.ValueStart, l.ValueLength));
    }

    /// <summary>Valor aceito: uma linha, sem controle, sem aspas, até 256 caracteres.</summary>
    public static bool IsSafeValue(string? value) =>
        value is { Length: <= 256 } && !value.Any(c => char.IsControl(c) || c == '"');

    /// <summary>Troca o valor de uma chave que já existe (nunca acrescenta). Falso quando a chave não está no arquivo.</summary>
    public bool Set(string key, string value)
    {
        if (!IsSafeValue(value)) throw new ArgumentException("Valor com quebra de linha, aspas ou caractere de controle.", nameof(value));
        var at = Locate(key, out var state);
        if (state == ConfigKeyState.Ambiguous) throw new InvalidOperationException($"'{key}' aparece mais de uma vez; edição recusada.");
        if (state == ConfigKeyState.Absent) return false;
        var l = _lines[at];
        var raw = l.Raw[..l.ValueStart] + value + l.Raw[(l.ValueStart + l.ValueLength)..];
        _lines[at] = ParseLine(raw, _syntax);
        return true;
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _lines.Count; i++)
        {
            sb.Append(_lines[i].Raw);
            if (i < _lines.Count - 1 || EndsWithNewLine) sb.Append(NewLine);
        }
        return sb.ToString();
    }
}
