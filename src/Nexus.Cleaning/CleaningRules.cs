using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Cleaning;

/// <summary>Desenvolvedor: caches de ferramentas de programação (npm, pip, NuGet, Gradle...), sempre desmarcados por padrão.</summary>
public enum CleaningCategory { Sistema, Navegadores, Apps, Jogos, Desenvolvedor }

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

    /// <summary>Regra escrita pelo usuário (pasta %AppData%\NEXUS\rules), não embutida. Vale só no aplicador da própria conta.</summary>
    public bool IsUserRule { get; init; }

    /// <summary>JSON original de uma regra do usuário (guardado no manifesto: o lote pode voltar mesmo se o arquivo sumir).</summary>
    public string? SourceJson { get; init; }

    /// <summary>Nome da pasta da regra dentro de um lote da quarentena. Regras do usuário usam "u_" (o "_" não existe em ids),
    /// então nunca colidem com uma regra embutida.</summary>
    public string QuarantineKey => IsUserRule ? CleaningRuleSet.UserRuleKeyPrefix + Id : Id;
}

/// <summary>Conjunto validado de regras. A fonte oficial é o recurso embutido; testes podem carregar outro JSON.</summary>
public sealed partial class CleaningRuleSet
{
    public const string ActionPrefix = "cleaning.";
    public const string HandlerKind = "CleaningRule";
    public const string UserRuleKeyPrefix = "u_";

    /// <summary>Máximo de regras do usuário num arquivo e tamanho máximo de um arquivo de regras.</summary>
    public const int MaxUserRules = 40;
    public const int MaxUserRuleFileBytes = 256 * 1024;

    /// <summary>Placeholders permitidos no início de uma raiz. Os de perfil só valem em regras User; os de máquina, em regras Machine.</summary>
    public static readonly IReadOnlyList<string> UserPlaceholders = ["%UserTemp%", "%LocalAppData%", "%LocalAppDataLow%", "%AppData%", "%UserProfile%"];
    public static readonly IReadOnlyList<string> MachinePlaceholders = ["%ProgramData%", "%WinDir%", "%SteamPath%", "%SystemDrive%"];

    /// <summary>Únicas pastas permitidas logo abaixo de %SystemDrive% (instaladores extraídos pelos drivers de vídeo).</summary>
    public static readonly IReadOnlyList<string> SystemDriveFolders = ["NVIDIA", "AMD"];

    /// <summary>
    /// Pastas que uma regra do USUÁRIO nunca pode ter como raiz (nem abaixo delas): segredos do Windows (DPAPI, credenciais,
    /// certificados), dados do próprio NEXUS e pastas de programas/perfil inteiras. As regras embutidas não as usam.
    /// </summary>
    public static readonly IReadOnlyList<string> UserRuleForbiddenRoots =
    [
        @"%AppData%\Microsoft\Protect", @"%AppData%\Microsoft\Credentials", @"%AppData%\Microsoft\Crypto", @"%AppData%\Microsoft\SystemCertificates",
        @"%AppData%\Microsoft\Windows\Start Menu", @"%AppData%\Microsoft\Windows\Recent", @"%AppData%\NEXUS",
        @"%LocalAppData%\Microsoft\Credentials", @"%LocalAppData%\Microsoft\Vault", @"%LocalAppData%\Microsoft\Windows\UsrClass.dat",
        @"%LocalAppData%\NEXUS", @"%LocalAppData%\Programs", @"%UserProfile%\AppData", @"%UserProfile%\.ssh", @"%UserProfile%\.gnupg",
        @"%UserProfile%\Documents", @"%UserProfile%\Desktop", @"%UserProfile%\Pictures", @"%UserProfile%\Videos", @"%UserProfile%\Music",
        @"%UserProfile%\Downloads", @"%UserProfile%\OneDrive*", @"%UserProfile%\Favorites", @"%UserProfile%\Contacts", @"%UserProfile%\Saved Games",
        @"%UserProfile%\Links", @"%UserProfile%\Searches",
    ];

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

    /// <summary>Conjunto vazio (ex.: nenhuma regra do usuário).</summary>
    public static CleaningRuleSet Empty { get; } = new([]);

    internal static CleaningRuleSet From(IReadOnlyList<CleaningRule> rules) => new(rules);

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 };

    public static CleaningRuleSet Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, JsonOptions);
        var list = new List<CleaningRule>();
        foreach (var r in doc.RootElement.GetProperty("rules").EnumerateArray())
            list.Add(ParseRule(r, userRule: false));
        if (list.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new InvalidDataException("Regras de limpeza com id repetido.");
        return new CleaningRuleSet(list);
    }

    /// <summary>
    /// Regras de UM arquivo do usuário. Mesmo formato das embutidas (<c>{"rules":[...]}</c>, comentários // permitidos) e, além da
    /// validação de sempre: só escopo de perfil (<c>"scope": "User"</c>, sem administrador), só quarentena (nada de "special"
    /// nem "nativeTool"), raízes com uma primeira pasta literal (nada de <c>%AppData%\*</c>), fora das pastas proibidas
    /// (<see cref="UserRuleForbiddenRoots"/>) e ids que não colidem com as regras embutidas. <c>"enabled": false</c> no topo
    /// desativa o arquivo inteiro (devolve um conjunto vazio, mas o arquivo é validado mesmo assim).
    /// </summary>
    public static CleaningRuleSet ParseUserRules(string json, out bool enabled)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxUserRuleFileBytes) throw new InvalidDataException("Arquivo de regras grande demais (máximo 256 KB).");
        using var doc = JsonDocument.Parse(json, JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("O arquivo precisa ser um objeto { \"rules\": [ ... ] }.");
        enabled = true;
        if (root.TryGetProperty("enabled", out var en))
            enabled = en.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException("\"enabled\" deve ser true ou false."),
            };
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Falta a lista \"rules\".");
        var list = new List<CleaningRule>();
        foreach (var r in rules.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Cada regra precisa ser um objeto.");
            list.Add(ParseRule(r, userRule: true));
        }
        if (list.Count > MaxUserRules) throw new InvalidDataException($"Regras demais num arquivo (máximo {MaxUserRules}).");
        if (list.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new InvalidDataException("Id de regra repetido no arquivo.");
        return new CleaningRuleSet(enabled ? list : []);
    }

    private static CleaningRule ParseRule(JsonElement r, bool userRule)
    {
        var id = Str(r, "id") ?? throw new InvalidDataException("Regra sem id.");
        if (!IdPattern().IsMatch(id) || id.Length > 48) throw new InvalidDataException($"Id de regra inválido: '{id}'.");
        string Req(string name) => Str(r, name) ?? throw new InvalidDataException($"{id}: falta '{name}'.");

        var scope = EnumOf<CleaningScope>(id, Req("scope"));
        var mode = EnumOf<CleaningMode>(id, Req("mode"));
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
            id, Req("title"), EnumOf<CleaningCategory>(id, Req("category")), scope, mode, roots,
            StrList(r, "exclude"), StrList(r, "excludeDirs"),
            r.TryGetProperty("minAgeHours", out var age) ? age.GetDouble() : 0,
            r.TryGetProperty("admin", out var adm) && adm.GetBoolean(),
            StrList(r, "closeApps"),
            r.TryGetProperty("defaultSelected", out var def) && def.GetBoolean(),
            r.TryGetProperty("sensitive", out var sen) && sen.GetBoolean(),
            Req("explanation"), Req("expectedGain"), Str(r, "warning"), Req("evidence"), Str(r, "nativeTool"), Str(r, "special"))
        { IsUserRule = userRule, SourceJson = userRule ? r.GetRawText() : null };
        Validate(rule);
        if (userRule) ValidateUserRule(rule);
        return rule;
    }

    private static T EnumOf<T>(string id, string value) where T : struct, Enum =>
        !int.TryParse(value, out _) && Enum.TryParse<T>(value, ignoreCase: false, out var v) && Enum.IsDefined(v)
            ? v : throw new InvalidDataException($"{id}: valor '{value}' inválido para {typeof(T).Name}.");

    /// <summary>Regras do usuário: só perfil, só quarentena, raízes estreitas e fora das pastas proibidas, ids próprios.</summary>
    private static void ValidateUserRule(CleaningRule rule)
    {
        var id = rule.Id;
        if (rule.Scope != CleaningScope.User || rule.Admin) throw new InvalidDataException($"{id}: regras do usuário só valem para o perfil (\"scope\": \"User\", sem \"admin\").");
        if (rule.Mode != CleaningMode.Quarantine) throw new InvalidDataException($"{id}: regras do usuário só podem mover para a quarentena (\"mode\": \"Quarantine\").");
        if (rule.Special is not null || rule.NativeTool is not null) throw new InvalidDataException($"{id}: \"special\" e \"nativeTool\" não são permitidos em regras do usuário.");
        if (_builtIn.Value.Find(id) is not null) throw new InvalidDataException($"{id}: já existe uma regra embutida com esse id; escolha outro.");
        if (rule.Title.Length > 120 || rule.Explanation.Length > 1000 || rule.ExpectedGain.Length > 300 || rule.Evidence.Length > 500 || (rule.Warning?.Length ?? 0) > 500)
            throw new InvalidDataException($"{id}: textos longos demais.");
        if (rule.Roots.Count > 12 || rule.Exclude.Count > 32 || rule.ExcludeDirectories.Count > 32 || rule.CloseApps.Count > 8)
            throw new InvalidDataException($"{id}: listas longas demais.");
        foreach (var root in rule.Roots)
        {
            var (ph, segments) = CleaningPaths.Split(root.Template);
            if (segments.Length == 0 || segments[0].IndexOfAny(['*', '?']) >= 0)
                throw new InvalidDataException($"{id}: a raiz '{root.Template}' precisa nomear uma pasta logo depois de {ph} (sem curinga), ex.: {ph}\\MeuApp\\Cache.");
            foreach (var forbidden in UserRuleForbiddenRoots)
            {
                var (fph, fseg) = CleaningPaths.Split(forbidden);
                if (fph != ph) continue;
                // Recusa a pasta protegida, qualquer coisa abaixo dela e qualquer pasta ACIMA dela (que a conteria), inclusive por curinga.
                var n = Math.Min(segments.Length, fseg.Length);
                var hit = true;
                for (var i = 0; i < n && hit; i++)
                    hit = CleaningPaths.SegmentMatches(segments[i], fseg[i]) || CleaningPaths.SegmentMatches(fseg[i], segments[i]);
                if (hit && (segments.Length >= fseg.Length || root.Recursive))
                    throw new InvalidDataException($"{id}: a raiz '{root.Template}' toca uma pasta protegida ({forbidden}).");
            }
        }
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
            // A pasta do perfil e a raiz do disco nunca são raiz: sempre uma pasta literal abaixo delas.
            if (ph is "%UserProfile%" && (segments.Length == 0 || segments[0].IndexOfAny(['*', '?']) >= 0))
                throw new InvalidDataException($"{id}: '%UserProfile%' só com uma pasta literal abaixo dele (ex.: %UserProfile%\\.gradle\\caches).");
            if (ph is "%SystemDrive%" && (segments.Length == 0 || !SystemDriveFolders.Contains(segments[0], StringComparer.Ordinal)))
                throw new InvalidDataException($"{id}: '%SystemDrive%' só com as pastas {string.Join(", ", SystemDriveFolders)}.");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static List<string> StrList(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException($"'{name}' com item vazio.")).ToList()
            : [];
}
