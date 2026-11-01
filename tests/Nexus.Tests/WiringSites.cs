using System.Text.RegularExpressions;

namespace Nexus.Tests;

/// <summary>
/// Um <c>new ActionRequest(...)</c> do código-fonte. <see cref="Keys"/> nulo = chave não resolvida estaticamente (vem de dados);
/// <see cref="Values"/> nulo = valor dinâmico; <see cref="ArgKeys"/> nulo = argumentos montados fora do ponto;
/// <see cref="Level"/> nulo = nível não literal ou pedido não enviado aqui.
/// </summary>
internal sealed record ActionSite(string File, int Line, string KeyExpr, IReadOnlyList<string>? Keys, IReadOnlyList<string?>? Values,
    IReadOnlyCollection<string>? ArgKeys, bool ArgsOptional, bool ConfirmIrreversible, string? Level, bool CatalogDerived);

/// <summary>
/// Chave(s) de ação e nível escritos juntos numa tupla ou chamada — ("boot.task.admin@1", BrokerLevel.Admin, new() {...}),
/// EphemeralAsync(título, texto, ArpAction, BrokerLevel.Admin), Apply(..., chave, valor, args, admin: true) — onde o pedido
/// é montado depois a partir desses dados.
/// </summary>
internal sealed record PairSite(string File, int Line, IReadOnlyList<string> Keys, string Level, IReadOnlyList<string?>? Values, IReadOnlyCollection<string>? ArgKeys);

/// <summary>Leitura estática dos pontos que montam pedidos ao aplicador (src/**/*.cs, fora Nexus.Broker, que define o tipo).</summary>
internal static partial class ActionSites
{
    public static List<ActionSite> All() => Scan().Sites;

    public static List<PairSite> Pairs() => Scan().Pairs;

    private static readonly Lazy<(List<ActionSite> Sites, List<PairSite> Pairs)> Cache = new(() =>
    {
        var list = new List<ActionSite>();
        var pairs = new List<PairSite>();
        var src = Path.Combine(Wiring.Root, "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal) || rel.StartsWith("Nexus.Broker/", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            var hasRequest = text.Contains("ActionRequest(", StringComparison.Ordinal);
            var hasLevel = text.Contains("BrokerLevel.", StringComparison.Ordinal) || text.Contains("admin: ", StringComparison.Ordinal);
            if (!hasRequest && !hasLevel) continue;
            var mask = Wiring.CodeMask(text);
            var source = new SourceFile(Path.GetFileName(file), text, mask);
            foreach (Match m in NewRequestRegex().Matches(text))
            {
                if (!mask[m.Index]) continue;
                list.Add(source.Site(m.Index, m.Index + m.Length - 1));
            }
            if (hasLevel) pairs.AddRange(source.Pairs());
        }
        return (list, pairs);
    });

    private static (List<ActionSite> Sites, List<PairSite> Pairs) Scan() => Cache.Value;

    [GeneratedRegex(@"new\s+ActionRequest\s*\(")]
    private static partial Regex NewRequestRegex();
}

internal sealed partial class SourceFile(string name, string text, bool[] mask)
{
    private readonly HashSet<string> _classes = ClassRegex().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    public ActionSite Site(int start, int open)
    {
        var close = Wiring.MatchClose(text, mask, open);
        var args = Wiring.SplitArgs(text, mask, open, close);
        string? keyExpr = null, valueExpr = null, argsExpr = null, confirmExpr = null;
        var positional = 0;
        foreach (var a in args)
        {
            var named = NamedArgRegex().Match(a);
            var (name, expr) = named.Success ? (named.Groups[1].Value, a[named.Length..].Trim()) : (positional++ switch
            {
                0 => "ActionId",
                1 => "Value",
                2 => "Arguments",
                _ => "ConfirmIrreversible",
            }, a);
            switch (name)
            {
                case "ActionId": keyExpr = expr; break;
                case "Value": valueExpr = expr; break;
                case "Arguments": argsExpr = expr; break;
                default: confirmExpr = expr; break;
            }
        }
        keyExpr ??= "";
        var catalogDerived = keyExpr.EndsWith("Definition.Key", StringComparison.Ordinal);
        var keys = catalogDerived ? null : Resolve(keyExpr, start, 0);
        IReadOnlyList<string?>? values = valueExpr is null or "null" ? [null] : Resolve(valueExpr, start, 0);
        var (argKeys, optional) = ArgKeys(argsExpr, start);
        var level = Level(start, close);
        return new ActionSite(name, Wiring.LineOf(text, start), keyExpr, keys?.Select(k => k ?? "null").ToList(), values, argKeys, optional,
            confirmExpr?.Trim() == "true", level, catalogDerived);
    }

    // ------------------------------------------------------------------ expressões

    /// <summary>Valores possíveis de uma expressão string (literal, constante, concatenação, ternário, variável local). Null = dinâmico.</summary>
    private List<string?>? Resolve(string expr, int at, int depth)
    {
        if (depth > 6) return null;
        expr = expr.Trim();
        while (expr.EndsWith('!')) expr = expr[..^1].TrimEnd();
        if (expr.Length == 0) return null;
        if (expr == "null") return [null];
        var m = MaskOf(expr);
        if (expr[0] == '(' && Wiring.MatchClose(expr, m, 0) == expr.Length - 1) return Resolve(expr[1..^1], at, depth + 1);

        // ternário de nível superior: cond ? a : b
        if (TopLevel(expr, m, '?') is var q and > 0 && TopLevel(expr, m, ':', q + 1) is var c and > 0)
        {
            var a = Resolve(expr[(q + 1)..c], at, depth + 1);
            var b = Resolve(expr[(c + 1)..], at, depth + 1);
            return a is null || b is null ? null : [.. a, .. b];
        }
        // concatenação
        var parts = SplitTop(expr, m, '+');
        if (parts.Count > 1)
        {
            List<string?> acc = [""];
            foreach (var p in parts)
            {
                var r = Resolve(p, at, depth + 1);
                if (r is null || r.Any(x => x is null)) return null;
                acc = acc.SelectMany(x => r.Select(y => (string?)(x + y))).ToList();
            }
            return acc;
        }
        var literal = StringLiteralRegex().Match(expr);
        if (literal.Success) return [Regex.Unescape(literal.Groups[1].Value)];
        var interp = InterpolatedRegex().Match(expr);
        if (interp.Success)
        {
            List<string?> acc = [""];
            var body = interp.Groups[1].Value;
            var last = 0;
            foreach (Match h in HoleRegex().Matches(body))
            {
                var r = Resolve(h.Groups[1].Value, at, depth + 1);
                if (r is null || r.Any(x => x is null)) return null;
                var prefix = body[last..h.Index];
                acc = acc.SelectMany(x => r.Select(y => (string?)(x + prefix + y))).ToList();
                last = h.Index + h.Length;
            }
            var tail = body[last..];
            return acc.Select(x => (string?)(x + tail)).ToList();
        }
        var call = KeyCallRegex().Match(expr);
        if (call.Success)
        {
            var r = Resolve(call.Groups[1].Value, at, depth + 1);
            return r?.Select(x => x is null || x.Contains('@', StringComparison.Ordinal) ? x : x + "@1").ToList();
        }
        if (!IdentifierRegex().IsMatch(expr)) return null;
        var segments = expr.Split('.');
        if (segments.Length >= 2)
        {
            var k = $"{segments[^2]}.{segments[^1]}";
            return Wiring.Constants.TryGetValue(k, out var set) ? set.Cast<string?>().ToList() : null;
        }
        // variável local (a declaração mais próxima antes do uso) ou constante de uma classe deste arquivo
        if (LocalInitializer(expr, at) is { } init) return Resolve(init.Expr, init.At, depth + 1);
        foreach (var cls in _classes)
            if (Wiring.Constants.TryGetValue($"{cls}.{expr}", out var set)) return set.Cast<string?>().ToList();
        return null;
    }

    private (string Expr, int At)? LocalInitializer(string name, int at)
    {
        // Constantes (const) vêm dos binários; aqui só variáveis locais.
        var regex = new Regex(@"(?<!\bconst\s+)\b(?:var|string|BrokerLevel)\s+" + Regex.Escape(name) + @"\s*=(?!=)");
        Match? best = null;
        foreach (Match d in regex.Matches(text))
        {
            if (d.Index >= at) break;
            if (mask[d.Index]) best = d;
        }
        if (best is null) return null;
        var start = best.Index + best.Length;
        var end = StatementEnd(start);
        return (text[start..end], best.Index);
    }

    private int StatementEnd(int from)
    {
        var depth = 0;
        for (var i = from; i < text.Length; i++)
        {
            if (!mask[i]) continue;
            var ch = text[i];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') { if (depth == 0) return i; depth--; }
            else if (ch == ';' && depth == 0) return i;
        }
        return text.Length;
    }

    // ------------------------------------------------------------------ chave + nível juntos

    public IEnumerable<PairSite> Pairs()
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '(' || !mask[i]) continue;
            var close = Wiring.MatchClose(text, mask, i);
            if (close < 0) continue;
            var args = Wiring.SplitArgs(text, mask, i, close);
            if (args.Count < 2) continue;
            string? level = null;
            var admin = false;
            foreach (var a in args)
            {
                var lit = LevelLiteralRegex().Match(a);
                if (lit.Success) { level = lit.Groups[1].Value; break; }
                var flag = AdminFlagRegex().Match(a);
                if (flag.Success) { level = flag.Groups[1].Value == "true" ? "Admin" : "User"; admin = true; break; }
            }
            if (level is null) continue;
            var keys = new List<string>();
            IReadOnlyList<string?>? values = null;
            IReadOnlyCollection<string>? argKeys = null;
            for (var k = 0; k < args.Count; k++)
            {
                var a = args[k];
                if (a.Contains("ActionRequest", StringComparison.Ordinal)) { keys.Clear(); break; }
                if (a.Contains("new()", StringComparison.Ordinal) || a.Contains("new Dictionary", StringComparison.Ordinal))
                {
                    argKeys = DictKeyRegex().Matches(a).Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                    continue;
                }
                if (!PlainRegex().IsMatch(a)) continue;
                var r = Resolve(a, i, 0);
                if (r is null || r.Count == 0 || !r.All(x => x is not null && WiringTests.KeyRegex().IsMatch(x))) continue;
                keys.AddRange(r!);
                if (admin && k + 1 < args.Count)
                {
                    var next = args[k + 1];
                    values = next == "null" ? [null] : StringLiteralRegex().IsMatch(next) || PlainRegex().IsMatch(next) ? Resolve(next, i, 0) : null;
                    if (k + 2 < args.Count)
                    {
                        var third = args[k + 2];
                        if (third == "null") argKeys = [];
                        else if (IdentifierRegex().IsMatch(third) && LocalInitializer(third, i) is { } init &&
                                 init.Expr.Contains("new Dictionary", StringComparison.Ordinal))
                            argKeys = DictKeyRegex().Matches(init.Expr).Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                    }
                }
            }
            if (keys.Count > 0) yield return new PairSite(name, Wiring.LineOf(text, i), keys, level, values, argKeys);
        }
    }

    // ------------------------------------------------------------------ argumentos

    private (IReadOnlyCollection<string>? Keys, bool Optional) ArgKeys(string? expr, int at, int depth = 0)
    {
        if (expr is null || expr.Trim() == "null") return ([], false);
        expr = expr.Trim();
        if (expr.Contains("new Dictionary", StringComparison.Ordinal))
        {
            var keys = DictKeyRegex().Matches(expr).Select(k => k.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            return (keys, OptionalRegex().IsMatch(expr));
        }
        if (depth < 3 && IdentifierRegex().IsMatch(expr) && !expr.Contains('.', StringComparison.Ordinal) && LocalInitializer(expr, at) is { } init)
            return ArgKeys(init.Expr, init.At, depth + 1);
        var copy = CopyCallRegex().Match(expr);
        if (depth < 3 && copy.Success && LocalInitializer(copy.Groups[1].Value, at) is { } init2)
            return ArgKeys(init2.Expr, init2.At, depth + 1);
        return (null, false);
    }

    // ------------------------------------------------------------------ nível

    /// <summary>Nível literal (User/Admin) do ApplyAsync que leva este pedido; null se não literal ou não enviado neste bloco.</summary>
    private string? Level(int site, int siteClose)
    {
        // 1) dentro de uma chamada (ApplyAsync, ApplyBatchAsync, um ajudante da página...) que recebe o nível: a mais interna que tiver
        var enclosing = new List<(int Open, int Close)>();
        foreach (Match a in CallRegex().Matches(text))
        {
            if (!mask[a.Index]) continue;
            var open = a.Index + a.Length - 1;
            if (open > site) break;
            var close = Wiring.MatchClose(text, mask, open);
            if (close > siteClose) enclosing.Add((open, close));
        }
        foreach (var (open, close) in enclosing.OrderByDescending(e => e.Open))
            if (LevelOfCall(open, close, site) is { } found) return found;

        // 2) atribuído a uma variável (var x = ...; lista.Add(...)) enviada por um ApplyAsync no mesmo bloco
        var stmtStart = StatementStart(site);
        var prefix = text[stmtStart..site];
        var v = AssignRegex().Match(prefix);
        var add = AddRegex().Match(prefix);
        var variable = add.Success ? add.Groups[1].Value : v.Success ? v.Groups[1].Value : null;
        if (variable is null) return null;
        var blockClose = BlockClose(site);
        var uses = new Regex(@"\b" + Regex.Escape(variable) + @"\b");
        foreach (Match a in CallRegex().Matches(text))
        {
            if (a.Index < siteClose || !mask[a.Index]) continue;
            if (a.Index > blockClose) break;
            var open = a.Index + a.Length - 1;
            var close = Wiring.MatchClose(text, mask, open);
            if (uses.IsMatch(text[open..close]) && LevelOfCall(open, close, a.Index) is { } found) return found;
        }
        return null;
    }

    private string? LevelOfCall(int open, int close, int at)
    {
        foreach (var arg in Wiring.SplitArgs(text, mask, open, close))
        {
            var lit = LevelLiteralRegex().Match(arg);
            if (lit.Success) return lit.Groups[1].Value;
            if (IdentifierRegex().IsMatch(arg) && !arg.Contains('.', StringComparison.Ordinal) && LocalInitializer(arg, at) is { } init &&
                LevelLiteralRegex().Match(init.Expr.Trim()) is { Success: true } l2)
                return l2.Groups[1].Value;
        }
        return null;
    }

    private int StatementStart(int from)
    {
        var depth = 0;
        for (var i = from - 1; i >= 0; i--)
        {
            if (!mask[i]) continue;
            var ch = text[i];
            if (ch is ')' or ']') depth++;
            else if (ch is '(' or '[') { if (depth > 0) depth--; }
            else if (ch == '}') depth++;
            else if (ch == '{') { if (depth > 0) depth--; else return i + 1; }
            else if (ch == ';' && depth == 0) return i + 1;
        }
        return 0;
    }

    private int BlockClose(int from)
    {
        var depth = 0;
        for (var i = from; i < text.Length; i++)
        {
            if (!mask[i]) continue;
            if (text[i] == '{') depth++;
            else if (text[i] == '}') { if (depth == 0) return i; depth--; }
        }
        return text.Length;
    }

    // ------------------------------------------------------------------ apoio

    private static bool[] MaskOf(string expr) => Wiring.CodeMask(expr);

    private static int TopLevel(string s, bool[] m, char target, int from = 0)
    {
        var depth = 0;
        for (var i = from; i < s.Length; i++)
        {
            if (!m[i]) continue;
            var ch = s[i];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == target && depth == 0)
            {
                if (target == '?' && (i + 1 < s.Length && s[i + 1] is '.' or '?' or '[' || i > 0 && s[i - 1] == '?')) { i++; continue; }
                return i;
            }
        }
        return -1;
    }

    private static List<string> SplitTop(string s, bool[] m, char sep)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (!m[i]) continue;
            var ch = s[i];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == sep && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    [GeneratedRegex(@"\A(ActionId|Value|Arguments|ConfirmIrreversible)\s*:(?!:)")]
    private static partial Regex NamedArgRegex();

    [GeneratedRegex(@"\A""((?:[^""\\]|\\.)*)""\z")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"\A\$""(.*)""\z", RegexOptions.Singleline)]
    private static partial Regex InterpolatedRegex();

    [GeneratedRegex(@"\{([^{}:]+)(?::[^{}]*)?\}")]
    private static partial Regex HoleRegex();

    [GeneratedRegex(@"\AActionKey\((.+)\)\z")]
    private static partial Regex KeyCallRegex();

    [GeneratedRegex(@"\ACopy\((\w+)\)\z")]
    private static partial Regex CopyCallRegex();

    [GeneratedRegex(@"\A@?[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*\z")]
    private static partial Regex IdentifierRegex();

    /// <summary>Literal, constante ou concatenação simples (o que pode ser uma chave de ação).</summary>
    [GeneratedRegex(@"\A(?:""[^""\\]*""|@?[A-Za-z_][\w.]*)(?:\s*\+\s*(?:""[^""\\]*""|@?[A-Za-z_][\w.]*))*\z")]
    private static partial Regex PlainRegex();

    [GeneratedRegex(@"\[\s*""(\w+)""\s*\]\s*=(?!=)")]
    private static partial Regex DictKeyRegex();

    [GeneratedRegex(@"[?:]\s*null\b")]
    private static partial Regex OptionalRegex();

    [GeneratedRegex(@"(?:\b\w+\s*)?\(")]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"\Aadmin\s*:\s*(true|false)\z")]
    private static partial Regex AdminFlagRegex();

    [GeneratedRegex(@"\A(?:\w+\s*:\s*)?BrokerLevel\.(User|Admin)\z")]
    private static partial Regex LevelLiteralRegex();

    [GeneratedRegex(@"(\w+)\s*=(?!=)[^=;]*\z", RegexOptions.Singleline)]
    private static partial Regex AssignRegex();

    [GeneratedRegex(@"(\w+)\.Add\(\s*\z")]
    private static partial Regex AddRegex();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+(\w+)")]
    private static partial Regex ClassRegex();
}
