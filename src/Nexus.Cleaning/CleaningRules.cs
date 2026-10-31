using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Cleaning;

public enum CleaningCategory { Sistema, Navegadores, Apps, Jogos }

/// <summary>User = perfil de quem pede (%TEMP%, %LocalAppData%...); Machine = pastas do sistema (Windows\Temp, ProgramData...).</summary>
public enum CleaningScope { User, Machine }

/// <summary>Quarantine = o handler move o que é elegível para a quarentena; MeasureOnly = só mede e indica a ferramenta oficial.</summary>
public enum CleaningMode { Quarantine, MeasureOnly }

/// <summary>Uma raiz da regra: modelo de caminho com placeholder no início e, opcionalmente, segmentos com curinga (* e ?).</summary>
public sealed record CleaningRoot(string Template, IReadOnlyList<string> Patterns, bool Recursive);

/// <summary>Regra declarativa de limpeza (dados, nenhum código). Ver Rules/cleaning-rules.json.</summary>
public sealed record CleaningRule(
    string Id,
    string Title,
    CleaningCategory Category,
    CleaningScope Scope,
    CleaningMode Mode,
    IReadOnlyList<CleaningRoot> Roots,
    IReadOnlyList<string> Exclude,
    IReadOnlyList<string> ExcludeDirectories,
    double MinAgeHours,
    bool Admin,
    IReadOnlyList<string> CloseApps,
    bool DefaultSelected,
    bool Sensitive,
    string Explanation,
    string ExpectedGain,
    string? Warning,
    string Evidence,
    string? NativeTool,
    string? Special)
{
    /// <summary>Id da ação no catálogo (catalog/cleaning.v1.json).</summary>
    public string ActionId => CleaningRuleSet.ActionPrefix + Id;
    public string ActionKey => ActionId + "@1";
    public TimeSpan MinAge => TimeSpan.FromHours(MinAgeHours);
}

/// <summary>Conjunto validado de regras. A fonte oficial é o recurso embutido; testes podem carregar outro JSON.</summary>
public sealed partial class CleaningRuleSet
{
    public const string ActionPrefix = "cleaning.";
    public const string HandlerKind = "CleaningRule";

    /// <summary>Placeholders permitidos no início de uma raiz. Os de perfil só valem em regras User; os de máquina, em regras Machine.</summary>
    public static readonly IReadOnlyList<string> UserPlaceholders = ["%UserTemp%", "%LocalAppData%", "%LocalAppDataLow%", "%AppData%"];
    public static readonly IReadOnlyList<string> MachinePlaceholders = ["%ProgramData%", "%WinDir%", "%SteamPath%"];

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private readonly Dictionary<string, CleaningRule> _byId;

    public IReadOnlyList<CleaningRule> Rules { get; }

    private CleaningRuleSet(IReadOnlyList<CleaningRule> rules)
    {
        Rules = rules;
        _byId = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public CleaningRule? Find(string id) => _byId.GetValueOrDefault(id);

    private static readonly Lazy<CleaningRuleSet> _builtIn = new(() =>
    {
        using var s = typeof(CleaningRuleSet).Assembly.GetManifestResourceStream("Nexus.Cleaning.Rules.cleaning-rules.json")
            ?? throw new InvalidOperationException("Regras de limpeza embutidas ausentes.");
        using var reader = new StreamReader(s);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>As regras que acompanham esta versão do NEXUS.</summary>
    public static CleaningRuleSet BuiltIn => _builtIn.Value;

    public static CleaningRuleSet Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var list = new List<CleaningRule>();
        foreach (var r in doc.RootElement.GetProperty("rules").EnumerateArray())
            list.Add(ParseRule(r));
        if (list.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new InvalidDataException("Regras de limpeza com id repetido.");
        return new CleaningRuleSet(list);
    }

    private static CleaningRule ParseRule(JsonElement r)
    {
        var id = Str(r, "id") ?? throw new InvalidDataException("Regra sem id.");
        if (!IdPattern().IsMatch(id) || id.Length > 48) throw new InvalidDataException($"Id de regra inválido: '{id}'.");
        string Req(string name) => Str(r, name) ?? throw new InvalidDataException($"{id}: falta '{name}'.");

        var scope = Enum.Parse<CleaningScope>(Req("scope"), ignoreCase: false);
        var mode = Enum.Parse<CleaningMode>(Req("mode"), ignoreCase: false);
        var defaultPatterns = StrList(r, "patterns");
        var defaultRecursive = r.TryGetProperty("recursive", out var rec) && rec.GetBoolean();
        var roots = new List<CleaningRoot>();
        if (r.TryGetProperty("roots", out var rootsEl))
            foreach (var root in rootsEl.EnumerateArray())
            {
                roots.Add(root.ValueKind == JsonValueKind.String
                    ? new CleaningRoot(root.GetString()!, defaultPatterns, defaultRecursive)
                    : new CleaningRoot(Str(root, "path") ?? throw new InvalidDataException($"{id}: raiz sem 'path'."),
                        root.TryGetProperty("patterns", out _) ? StrList(root, "patterns") : defaultPatterns,
                        root.TryGetProperty("recursive", out var rr) ? rr.GetBoolean() : defaultRecursive));
            }

        var rule = new CleaningRule(
            id, Req("title"), Enum.Parse<CleaningCategory>(Req("category"), ignoreCase: false), scope, mode, roots,
            StrList(r, "exclude"), StrList(r, "excludeDirs"),
            r.TryGetProperty("minAgeHours", out var age) ? age.GetDouble() : 0,
            r.TryGetProperty("admin", out var adm) && adm.GetBoolean(),
            StrList(r, "closeApps"),
            r.TryGetProperty("defaultSelected", out var def) && def.GetBoolean(),
            r.TryGetProperty("sensitive", out var sen) && sen.GetBoolean(),
            Req("explanation"), Req("expectedGain"), Str(r, "warning"), Req("evidence"), Str(r, "nativeTool"), Str(r, "special"));
        Validate(rule);
        return rule;
    }

    private static void Validate(CleaningRule rule)
    {
        var id = rule.Id;
        if (rule.MinAgeHours < 0 || double.IsNaN(rule.MinAgeHours)) throw new InvalidDataException($"{id}: idade mínima inválida.");
        if (rule.Sensitive && rule.DefaultSelected) throw new InvalidDataException($"{id}: regra sensível não pode vir marcada.");
        if (rule.Sensitive && string.IsNullOrWhiteSpace(rule.Warning)) throw new InvalidDataException($"{id}: regra sensível exige aviso.");
        if (rule.Admin != (rule.Scope == CleaningScope.Machine)) throw new InvalidDataException($"{id}: regras de máquina (e só elas) exigem administrador.");
        if (rule.Special is not null && rule.Special != "RecycleBin") throw new InvalidDataException($"{id}: especial desconhecido.");
        if (rule.Special is not null && rule.Mode != CleaningMode.MeasureOnly) throw new InvalidDataException($"{id}: especial só pode medir.");
        if (rule.Special is null && rule.Roots.Count == 0) throw new InvalidDataException($"{id}: sem raízes.");
        if (rule.Mode == CleaningMode.MeasureOnly && rule.NativeTool is null) throw new InvalidDataException($"{id}: medir-só precisa indicar a ferramenta oficial.");
        if (rule.NativeTool is not null && !(rule.NativeTool.StartsWith("ms-settings:", StringComparison.Ordinal) || rule.NativeTool.StartsWith("shell:", StringComparison.Ordinal)))
            throw new InvalidDataException($"{id}: ferramenta nativa só pode ser ms-settings: ou shell:.");
        foreach (var app in rule.CloseApps)
            if (app.Length == 0 || app.IndexOfAny(['\\', '/', ':', '*', '?']) >= 0 || app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{id}: nome de processo inválido '{app}'.");
        var allowed = rule.Scope == CleaningScope.User ? UserPlaceholders : MachinePlaceholders;
        foreach (var root in rule.Roots)
        {
            var t = root.Template;
            var ph = allowed.FirstOrDefault(p => t.Equals(p, StringComparison.Ordinal) || t.StartsWith(p + "\\", StringComparison.Ordinal));
            if (ph is null) throw new InvalidDataException($"{id}: raiz '{t}' precisa começar com um placeholder permitido para o escopo {rule.Scope}.");
            var rest = t[ph.Length..];
            if (rest.Contains('%') || rest.Contains('/') || rest.Contains("\\\\", StringComparison.Ordinal) || rest.EndsWith('\\'))
                throw new InvalidDataException($"{id}: raiz '{t}' malformada.");
            var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => s is "." or ".." || s.Contains(':')))
                throw new InvalidDataException($"{id}: raiz '{t}' com segmento proibido.");
            if (segments.Count(s => s.IndexOfAny(['*', '?']) >= 0) > 2)
                throw new InvalidDataException($"{id}: raiz '{t}' com curingas demais.");
            if (root.Patterns.Count == 0 || root.Patterns.Any(p => p.Length == 0 || p.IndexOfAny(['\\', '/', ':']) >= 0))
                throw new InvalidDataException($"{id}: padrões de arquivo inválidos em '{t}'.");
            // A pasta-base inteira (ex.: %WinDir%) só pode ser raiz sem recursão e com nomes exatos.
            if (segments.Length == 0 && (root.Recursive || root.Patterns.Any(p => p.IndexOfAny(['*', '?']) >= 0)) && ph is not "%UserTemp%")
                throw new InvalidDataException($"{id}: '{ph}' como raiz só com nomes exatos e sem subpastas.");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static List<string> StrList(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException($"'{name}' com item vazio.")).ToList()
            : [];
}
