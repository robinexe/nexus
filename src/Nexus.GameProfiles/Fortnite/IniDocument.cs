namespace Nexus.GameProfiles.Fortnite;

/// <summary>Como uma chave aparece numa seção: ausente, uma vez (valor) ou de um jeito que o editor não mexe.</summary>
public enum IniKeyState { Absent, Present, Ambiguous }

/// <summary>
/// Arquivo .ini do Unreal Engine (ex.: GameUserSettings.ini do Fortnite) editado sem perdas: toda linha é preservada como
/// estava (ordem, espaços, comentários, linhas desconhecidas, quebra de linha do arquivo); só a linha da chave alterada muda.
/// <list type="bullet">
/// <item>Seção: linha <c>[nome]</c>. Chave: <c>nome=valor</c> (o nome é comparado sem diferenciar maiúsculas, como o Unreal).</item>
/// <item>Ambíguo (o editor recusa): a chave repetida na seção, a seção repetida no arquivo, ou a chave com os prefixos de
/// lista do Unreal (<c>+</c>, <c>-</c>, <c>.</c>, <c>!</c>), que mudam o significado da linha.</item>
/// <item>Valor novo nunca leva quebra de linha nem caractere de controle (não dá para "injetar" outra chave).</item>
/// </list>
/// </summary>
public sealed class IniDocument
{
    private sealed record Line(string Raw, string? Section, string? Key, string? Value, bool ListPrefix);

    private readonly List<Line> _lines;

    private IniDocument(List<Line> lines, string newLine, bool endsWithNewLine)
    {
        _lines = lines;
        NewLine = newLine;
        EndsWithNewLine = endsWithNewLine;
    }

    public string NewLine { get; }
    public bool EndsWithNewLine { get; }
    public int LineCount => _lines.Count;

    public static IniDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var ends = text.EndsWith('\n');
        var raw = text.Length == 0 ? [] : text.Split(nl).ToList();
        if (ends && raw.Count > 0) raw.RemoveAt(raw.Count - 1);
        return new IniDocument(raw.Select(ParseLine).ToList(), nl, ends || text.Length == 0);
    }

    private static Line ParseLine(string raw)
    {
        var t = raw.Trim();
        if (t.Length == 0 || t[0] is ';' or '#') return new Line(raw, null, null, null, false);
        if (t[0] == '[' && t[^1] == ']') return new Line(raw, t[1..^1].Trim(), null, null, false);
        var eq = raw.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0) return new Line(raw, null, null, null, false);
        var key = raw[..eq].Trim();
        if (key.Length == 0) return new Line(raw, null, null, null, false);
        var list = key[0] is '+' or '-' or '.' or '!';
        return new Line(raw, null, list ? key[1..].Trim() : key, raw[(eq + 1)..], list);
    }

    private static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Índices [início, fim) das linhas de cada ocorrência da seção (o cabeçalho fica em início-1).</summary>
    private List<(int Header, int Start, int End)> Sections(string section)
    {
        var result = new List<(int, int, int)>();
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Section is null || !SameName(_lines[i].Section, section)) continue;
            var end = i + 1;
            while (end < _lines.Count && _lines[end].Section is null) end++;
            result.Add((i, i + 1, end));
        }
        return result;
    }

    /// <summary>Estado e valor da chave na seção. Valor = texto depois do primeiro "=", sem mexer em espaços.</summary>
    public (IniKeyState State, string? Value) Get(string section, string key)
    {
        var where = Locate(section, key, out var state);
        return state == IniKeyState.Present ? (state, _lines[where].Value) : (state, null);
    }

    public bool HasSection(string section) => Sections(section).Count > 0;

    private int Locate(string section, string key, out IniKeyState state)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        state = IniKeyState.Absent;
        var sections = Sections(section);
        if (sections.Count > 1) { state = IniKeyState.Ambiguous; return -1; }
        if (sections.Count == 0) return -1;
        var (_, start, end) = sections[0];
        var found = -1;
        for (var i = start; i < end; i++)
        {
            var l = _lines[i];
            if (l.Key is null || !SameName(l.Key, key)) continue;
            if (l.ListPrefix || found >= 0) { state = IniKeyState.Ambiguous; return -1; }
            found = i;
        }
        if (found >= 0) state = IniKeyState.Present;
        return found;
    }

    /// <summary>Valor aceito numa linha: uma linha só, sem caracteres de controle, até 512 caracteres.</summary>
    public static bool IsSafeValue(string? value) =>
        value is { Length: <= 512 } && !value.Any(char.IsControl);

    /// <summary>
    /// Troca o valor da chave (mantém o nome como estava escrito). Ausente: entra depois da última chave da seção; seção
    /// ausente: vai para o fim do arquivo, com uma linha em branco antes (como o Unreal grava).
    /// </summary>
    public void Set(string section, string key, string value)
    {
        if (!IsSafeValue(value)) throw new ArgumentException("Valor com quebra de linha ou caractere de controle.", nameof(value));
        if (!IsSafeName(section) || !IsSafeName(key)) throw new ArgumentException("Nome de seção ou chave inválido.");
        var at = Locate(section, key, out var state);
        if (state == IniKeyState.Ambiguous) throw new InvalidOperationException($"'{key}' aparece de forma ambígua em [{section}]; edição recusada.");
        if (state == IniKeyState.Present)
        {
            var old = _lines[at];
            var eq = old.Raw.IndexOf('=', StringComparison.Ordinal);
            var raw = old.Raw[..(eq + 1)] + value;
            _lines[at] = old with { Raw = raw, Value = value };
            return;
        }
        var line = new Line($"{key}={value}", null, key, value, false);
        var sections = Sections(section);
        if (sections.Count == 1)
        {
            var (header, start, end) = sections[0];
            var insert = end;
            while (insert > start && _lines[insert - 1].Raw.Trim().Length == 0) insert--; // antes das linhas em branco do fim da seção
            if (insert == start) insert = header + 1;
            _lines.Insert(insert, line);
            return;
        }
        if (_lines.Count > 0 && _lines[^1].Raw.Trim().Length != 0) _lines.Add(new Line("", null, null, null, false));
        _lines.Add(new Line($"[{section}]", section, null, null, false));
        _lines.Add(line);
    }

    /// <summary>Remove a chave da seção (só a linha dela). Retorna se removeu.</summary>
    public bool Remove(string section, string key)
    {
        var at = Locate(section, key, out var state);
        if (state == IniKeyState.Ambiguous) throw new InvalidOperationException($"'{key}' aparece de forma ambígua em [{section}]; edição recusada.");
        if (state != IniKeyState.Present) return false;
        _lines.RemoveAt(at);
        return true;
    }

    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 128 && !name.Any(c => char.IsControl(c) || c is '[' or ']' or '=');

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
