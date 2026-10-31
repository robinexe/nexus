using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Security.Scanner;

// ---------------------------------------------------------------------- formato JSON (nexus.scanrules/1)

public sealed record RuleFileDto(string? Schema, IReadOnlyList<RuleDto>? Rules);

public sealed record RuleDto(
    string? Id,
    string? Title,
    string? Why,
    int Points,
    bool Test,
    IReadOnlyList<string>? Extensions,
    long? MinSize,
    long? MaxSize,
    bool RequirePe,
    IReadOnlyList<RuleStringDto>? Strings,
    string? Condition);

/// <summary>Um padrão: <c>text</c> (com <c>nocase</c>, <c>wide</c> = UTF-16LE, <c>ascii</c>) ou <c>hex</c> ("4D 5A ?? [2-4] 50").
/// <c>at</c> = deslocamento exato; <c>section</c> = nome da seção do PE, "overlay" ou "entry" (seção do ponto de entrada).</summary>
public sealed record RuleStringDto(string? Id, string? Text, string? Hex, bool Nocase, bool Wide, bool? Ascii, int? At, string? Section);

// ---------------------------------------------------------------------- padrões compilados

internal enum TokenKind : byte { Exact, NoCase, Any, Jump }

internal readonly record struct PatternToken(TokenKind Kind, byte A, byte B, int Min, int Max);

/// <summary>Sequência de bytes com curingas (??) e saltos ([n-m]). Começa e termina sempre em byte literal.</summary>
internal sealed class BytePattern
{
    public const int MaxTokens = 512;
    public const int MaxJump = 1024;
    public const int MaxJumps = 4;
    private readonly PatternToken[] _tokens;

    public BytePattern(IReadOnlyList<PatternToken> tokens)
    {
        if (tokens.Count == 0 || tokens.Count > MaxTokens) throw new FormatException("padrão vazio ou longo demais");
        if (tokens[0].Kind is TokenKind.Any or TokenKind.Jump || tokens[^1].Kind is TokenKind.Any or TokenKind.Jump)
            throw new FormatException("o padrão precisa começar e terminar com um byte fixo");
        if (tokens.Count(t => t.Kind == TokenKind.Jump) > MaxJumps) throw new FormatException($"no máximo {MaxJumps} saltos por padrão");
        _tokens = [.. tokens];
        MinLength = _tokens.Sum(t => t.Kind == TokenKind.Jump ? t.Min : 1);
    }

    public int MinLength { get; }

    /// <summary>Há ocorrência que COMEÇA em [from, to)?</summary>
    public bool Find(ReadOnlySpan<byte> data, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(to, data.Length - MinLength + 1);
        var first = _tokens[0];
        var pos = from;
        while (pos < to)
        {
            var window = data[pos..Math.Min(data.Length, to)];
            var idx = first.Kind == TokenKind.Exact ? window.IndexOf(first.A) : window.IndexOfAny(first.A, first.B);
            if (idx < 0) return false;
            var start = pos + idx;
            if (MatchAt(data, 1, start + 1)) return true;
            pos = start + 1;
        }
        return false;
    }

    private bool MatchAt(ReadOnlySpan<byte> data, int ti, int pos)
    {
        for (; ti < _tokens.Length; ti++)
        {
            var t = _tokens[ti];
            if (t.Kind == TokenKind.Jump)
            {
                for (var skip = t.Min; skip <= t.Max; skip++)
                {
                    if (pos + skip >= data.Length) return false;
                    if (MatchAt(data, ti + 1, pos + skip)) return true;
                }
                return false;
            }
            if (pos >= data.Length) return false;
            var b = data[pos];
            var ok = t.Kind switch
            {
                TokenKind.Any => true,
                TokenKind.Exact => b == t.A,
                _ => b == t.A || b == t.B,
            };
            if (!ok) return false;
            pos++;
        }
        return true;
    }

    public static BytePattern FromHex(string hex)
    {
        var tokens = new List<PatternToken>();
        var s = hex.Trim();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '[')
            {
                var close = s.IndexOf(']', i);
                if (close < 0) throw new FormatException("salto sem ']'");
                var body = s[(i + 1)..close].Split('-');
                if (body.Length is < 1 or > 2) throw new FormatException("salto inválido");
                var min = int.Parse(body[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
                var max = body.Length == 2 ? int.Parse(body[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture) : min;
                if (min < 0 || max < min || max > MaxJump) throw new FormatException($"salto fora de 0..{MaxJump}");
                tokens.Add(new PatternToken(TokenKind.Jump, 0, 0, min, max));
                i = close + 1;
                continue;
            }
            if (i + 1 >= s.Length) throw new FormatException("byte hexadecimal incompleto");
            var pair = s.Substring(i, 2);
            if (pair == "??") tokens.Add(new PatternToken(TokenKind.Any, 0, 0, 0, 0));
            else if (byte.TryParse(pair, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b)) tokens.Add(new PatternToken(TokenKind.Exact, b, b, 0, 0));
            else throw new FormatException($"'{pair}' não é byte hexadecimal nem ??");
            i += 2;
        }
        return new BytePattern(tokens);
    }

    public static BytePattern FromText(string text, bool wide, bool nocase)
    {
        var bytes = wide ? Encoding.Unicode.GetBytes(text) : Encoding.UTF8.GetBytes(text);
        var tokens = new List<PatternToken>(bytes.Length);
        for (var k = 0; k < bytes.Length; k++)
        {
            var b = bytes[k];
            var letter = (b is >= (byte)'a' and <= (byte)'z') || (b is >= (byte)'A' and <= (byte)'Z');
            var isCharByte = !wide || k % 2 == 0;
            if (nocase && letter && isCharByte)
                tokens.Add(new PatternToken(TokenKind.NoCase, (byte)char.ToLowerInvariant((char)b), (byte)char.ToUpperInvariant((char)b), 0, 0));
            else tokens.Add(new PatternToken(TokenKind.Exact, b, b, 0, 0));
        }
        return new BytePattern(tokens);
    }
}

internal sealed record CompiledString(string Id, IReadOnlyList<BytePattern> Variants, int? At, string? Section);

// ---------------------------------------------------------------------- condições ("2 of ($a*)", "$a and not $b")

internal abstract record Cond
{
    public abstract bool Eval(Func<int, bool> matched);
}

internal sealed record RefCond(int Index) : Cond
{
    public override bool Eval(Func<int, bool> matched) => matched(Index);
}

internal sealed record OfCond(int Needed, IReadOnlyList<int> Set) : Cond
{
    public override bool Eval(Func<int, bool> matched)
    {
        var need = Needed < 0 ? Set.Count : Needed; // -1 = all
        if (need == 0) return true;
        var count = 0;
        foreach (var i in Set) if (matched(i) && ++count >= need) return true;
        return false;
    }
}

internal sealed record AndCond(Cond L, Cond R) : Cond
{
    public override bool Eval(Func<int, bool> matched) => L.Eval(matched) && R.Eval(matched);
}

internal sealed record OrCond(Cond L, Cond R) : Cond
{
    public override bool Eval(Func<int, bool> matched) => L.Eval(matched) || R.Eval(matched);
}

internal sealed record NotCond(Cond Inner) : Cond
{
    public override bool Eval(Func<int, bool> matched) => !Inner.Eval(matched);
}

/// <summary>Analisador da linguagem de condição (subconjunto do YARA): $id, and, or, not, parênteses, "N|all|any of (them|($a,$b*))".</summary>
internal sealed partial class ConditionParser
{
    [GeneratedRegex(@"\s*(\$[A-Za-z0-9_]*\*?|[A-Za-z_][A-Za-z0-9_]*|\d+|\(|\)|,)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    private readonly List<string> _tokens = [];
    private readonly IReadOnlyList<string> _ids;
    private int _pos;

    private ConditionParser(string text, IReadOnlyList<string> ids)
    {
        _ids = ids;
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var m = TokenPattern().Match(text, i);
            if (!m.Success || m.Index != i) throw new FormatException($"símbolo inesperado na condição perto de '{text[i..Math.Min(text.Length, i + 10)]}'");
            _tokens.Add(m.Groups[1].Value);
            i += m.Length;
        }
    }

    public static Cond Parse(string text, IReadOnlyList<string> ids)
    {
        var p = new ConditionParser(text, ids);
        var c = p.Or();
        if (p._pos != p._tokens.Count) throw new FormatException($"sobrou '{p._tokens[p._pos]}' na condição");
        return c;
    }

    private string? Peek => _pos < _tokens.Count ? _tokens[_pos] : null;
    private string Next() => _pos < _tokens.Count ? _tokens[_pos++] : throw new FormatException("condição terminou cedo");
    private bool Accept(string t) { if (string.Equals(Peek, t, StringComparison.OrdinalIgnoreCase)) { _pos++; return true; } return false; }

    private Cond Or()
    {
        var l = And();
        while (Accept("or")) l = new OrCond(l, And());
        return l;
    }

    private Cond And()
    {
        var l = Unary();
        while (Accept("and")) l = new AndCond(l, Unary());
        return l;
    }

    private Cond Unary()
    {
        if (Accept("not")) return new NotCond(Unary());
        if (Accept("("))
        {
            var inner = Or();
            if (!Accept(")")) throw new FormatException("falta ')'");
            return inner;
        }
        var t = Next();
        if (t.StartsWith('$'))
        {
            if (t.EndsWith('*')) throw new FormatException("curinga $x* só dentro de 'of'");
            return new RefCond(IndexOf(t[1..]));
        }
        int needed;
        if (t.Equals("all", StringComparison.OrdinalIgnoreCase)) needed = -1;
        else if (t.Equals("any", StringComparison.OrdinalIgnoreCase)) needed = 1;
        else if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) needed = n;
        else throw new FormatException($"'{t}' inesperado na condição");
        if (!Accept("of")) throw new FormatException("esperado 'of'");
        return new OfCond(needed, Set());
    }

    private List<int> Set()
    {
        if (Accept("them")) return [.. Enumerable.Range(0, _ids.Count)];
        if (!Accept("(")) throw new FormatException("esperado 'them' ou '('");
        var set = new SortedSet<int>();
        do
        {
            var t = Next();
            if (!t.StartsWith('$')) throw new FormatException($"esperado $id, veio '{t}'");
            var name = t[1..];
            if (name.EndsWith('*'))
            {
                var prefix = name[..^1];
                var any = false;
                for (var i = 0; i < _ids.Count; i++)
                    if (_ids[i].StartsWith(prefix, StringComparison.Ordinal)) { set.Add(i); any = true; }
                if (!any) throw new FormatException($"nenhum padrão começa com '{prefix}'");
            }
            else set.Add(IndexOf(name));
        } while (Accept(","));
        if (!Accept(")")) throw new FormatException("falta ')'");
        return [.. set];
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < _ids.Count; i++) if (_ids[i] == id) return i;
        throw new FormatException($"padrão '${id}' não declarado");
    }
}

// ---------------------------------------------------------------------- regras

public sealed class PatternRule
{
    internal PatternRule(string id, string title, string why, int points, bool test, IReadOnlySet<string>? extensions, long minSize, long maxSize,
        bool requirePe, IReadOnlyList<CompiledString> strings, Cond condition)
    {
        Id = id; Title = title; Why = why; Points = points; Test = test; Extensions = extensions; MinSize = minSize; MaxSize = maxSize;
        RequirePe = requirePe; Strings = strings; Condition = condition;
    }

    public string Id { get; }
    public string Title { get; }
    public string Why { get; }
    public int Points { get; }
    public bool Test { get; }
    /// <summary>Extensões (".ps1") a que a regra se aplica; null = qualquer arquivo.</summary>
    public IReadOnlySet<string>? Extensions { get; }
    public long MinSize { get; }
    public long MaxSize { get; }
    public bool RequirePe { get; }
    internal IReadOnlyList<CompiledString> Strings { get; }
    internal Cond Condition { get; }

    public bool AppliesTo(string extension, long size, bool isPe) =>
        size >= MinSize && size <= MaxSize && (!RequirePe || isPe) &&
        (Extensions is null || Extensions.Contains(extension.ToLowerInvariant()));

    /// <summary>Avalia a regra sobre o conteúdo (já filtrado por <see cref="AppliesTo"/>).</summary>
    public bool Matches(ReadOnlyMemory<byte> data, PeFile? pe)
    {
        var cache = new sbyte[Strings.Count]; // 0 = não avaliado, 1 = casou, -1 = não casou
        return Condition.Eval(i =>
        {
            if (cache[i] == 0) cache[i] = (sbyte)(StringMatches(Strings[i], data.Span, pe) ? 1 : -1);
            return cache[i] > 0;
        });
    }

    private static bool StringMatches(CompiledString s, ReadOnlySpan<byte> data, PeFile? pe)
    {
        var ranges = new List<(int From, int To)>(1);
        if (s.At is { } at) ranges.Add((at, at + 1));
        else if (s.Section is null) ranges.Add((0, data.Length));
        else
        {
            if (pe is null) return false;
            if (s.Section.Equals("overlay", StringComparison.OrdinalIgnoreCase))
                ranges.Add(((int)Math.Min(pe.EndOfImage, data.Length), data.Length));
            else
            {
                var section = s.Section.Equals("entry", StringComparison.OrdinalIgnoreCase)
                    ? pe.EntrySection
                    : pe.Sections.FirstOrDefault(x => x.Name.Equals(s.Section, StringComparison.Ordinal));
                if (section is null || section.RawSize == 0) return false;
                ranges.Add(((int)Math.Min(section.RawPointer, (uint)data.Length), (int)Math.Min((long)section.RawPointer + section.RawSize, data.Length)));
            }
        }
        foreach (var v in s.Variants)
            foreach (var (from, to) in ranges)
                if (v.Find(data, from, to)) return true;
        return false;
    }
}

/// <summary>Conjunto de regras de padrão no formato JSON <c>nexus.scanrules/1</c> (ver docs/modules/scanner.md).</summary>
public sealed partial class RuleSet
{
    public const string Schema = "nexus.scanrules/1";
    public const int MaxRules = 500;
    public const int MaxStringsPerRule = 64;
    public const long MaxRuleFileBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StringIdPattern();

    private readonly List<PatternRule> _rules = [];

    public IReadOnlyList<PatternRule> Rules => _rules;

    public static RuleSet LoadBuiltIn()
    {
        using var stream = typeof(RuleSet).Assembly.GetManifestResourceStream("Nexus.Security.Scanner.Rules.default.rules.json")
                           ?? throw new InvalidOperationException("Regras padrão ausentes do assembly.");
        using var reader = new StreamReader(stream);
        var set = new RuleSet();
        set.Add(reader.ReadToEnd());
        return set;
    }

    /// <summary>Acrescenta as regras de um JSON. Erro de formato: <see cref="FormatException"/> com o id da regra.</summary>
    public void Add(string json)
    {
        RuleFileDto? file;
        try { file = JsonSerializer.Deserialize<RuleFileDto>(json, Json); }
        catch (JsonException ex) { throw new FormatException($"JSON inválido: {ex.Message}", ex); }
        if (file is null || !string.Equals(file.Schema, Schema, StringComparison.Ordinal)) throw new FormatException($"esperado \"schema\": \"{Schema}\"");
        foreach (var dto in file.Rules ?? [])
        {
            if (_rules.Count >= MaxRules) throw new FormatException($"mais de {MaxRules} regras");
            var rule = Compile(dto);
            if (_rules.Any(r => r.Id == rule.Id)) throw new FormatException($"regra '{rule.Id}' repetida");
            _rules.Add(rule);
        }
    }

    public void AddFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxRuleFileBytes) throw new FormatException("arquivo de regras grande demais (máx. 2 MB)");
        Add(File.ReadAllText(path));
    }

    private static PatternRule Compile(RuleDto r)
    {
        var id = r.Id ?? "";
        if (!RuleIdPattern().IsMatch(id)) throw new FormatException($"id de regra inválido: '{id}'");
        try
        {
            if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Why)) throw new FormatException("title e why são obrigatórios (a explicação aparece para o usuário)");
            if (r.Points is < 0 or > 100) throw new FormatException("points entre 0 e 100");
            var strings = new List<CompiledString>();
            foreach (var s in r.Strings ?? [])
            {
                if (strings.Count >= MaxStringsPerRule) throw new FormatException($"mais de {MaxStringsPerRule} padrões");
                var sid = s.Id ?? "";
                if (!StringIdPattern().IsMatch(sid)) throw new FormatException($"id de padrão inválido: '{sid}'");
                if (strings.Any(x => x.Id == sid)) throw new FormatException($"padrão '{sid}' repetido");
                if ((s.Text is null) == (s.Hex is null)) throw new FormatException($"'{sid}': use text OU hex");
                if (s.At is < 0) throw new FormatException($"'{sid}': at negativo");
                if (s.At is not null && s.Section is not null) throw new FormatException($"'{sid}': at e section juntos");
                var variants = new List<BytePattern>();
                try
                {
                    if (s.Hex is not null) variants.Add(BytePattern.FromHex(s.Hex));
                    else
                    {
                        if (s.Text!.Length == 0) throw new FormatException("texto vazio");
                        if (s.Ascii ?? true) variants.Add(BytePattern.FromText(s.Text, wide: false, s.Nocase));
                        if (s.Wide) variants.Add(BytePattern.FromText(s.Text, wide: true, s.Nocase));
                        if (variants.Count == 0) throw new FormatException("ascii=false exige wide=true");
                    }
                }
                catch (FormatException ex) { throw new FormatException($"'{sid}': {ex.Message}", ex); }
                strings.Add(new CompiledString(sid, variants, s.At, s.Section));
            }
            if (strings.Count == 0) throw new FormatException("nenhum padrão");
            var condition = ConditionParser.Parse(string.IsNullOrWhiteSpace(r.Condition) ? "all of them" : r.Condition, strings.Select(x => x.Id).ToList());
            HashSet<string>? exts = null;
            if (r.Extensions is { Count: > 0 } list && !list.Contains("*"))
                exts = new HashSet<string>(list.Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant()), StringComparer.Ordinal);
            var min = r.MinSize ?? 0;
            var max = r.MaxSize ?? long.MaxValue;
            if (min < 0 || max < min) throw new FormatException("minSize/maxSize inválidos");
            return new PatternRule(id, r.Title!.Trim(), r.Why!.Trim(), r.Points, r.Test, exts, min, max, r.RequirePe, strings, condition);
        }
        catch (FormatException ex) when (!ex.Message.StartsWith("regra '", StringComparison.Ordinal))
        {
            throw new FormatException($"regra '{id}': {ex.Message}", ex);
        }
    }
}
