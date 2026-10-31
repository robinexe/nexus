using System.Globalization;
using System.Text.Json;
using Nexus.Core;

namespace Nexus.Automation;

/// <summary>
/// Tipo de recomendação. <see cref="Recommend"/> pode entrar no "Otimizar agora" (se o risco for baixo);
/// <see cref="Consider"/> só com medição antes/depois; <see cref="Review"/> abre a página do módulo para o usuário escolher;
/// <see cref="Avoid"/> é o que NÃO fazer neste PC; <see cref="Info"/> é conselho de hardware, sem ação.
/// </summary>
public enum RecommendationKind { Recommend, Consider, Review, Avoid, Info }

/// <summary>Uma regra declarativa (JSON). Condições: todas de <see cref="When"/>, ao menos uma de <see cref="Any"/>, nenhuma de <see cref="Unless"/>.</summary>
public sealed record RecommendationRule(
    string Id,
    RecommendationKind Kind,
    string? Action,
    string? ActionPrefix,
    string? Module,
    string? Title,
    IReadOnlyList<string> When,
    IReadOnlyList<string> Any,
    IReadOnlyList<string> Unless,
    int Priority,
    string Reason,
    string? Gain,
    string? Value,
    ActionRisk MaxRisk)
{
    public bool Matches(IReadOnlySet<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return When.All(tags.Contains) && (Any.Count == 0 || Any.Any(tags.Contains)) && !Unless.Any(tags.Contains);
    }
}

/// <param name="ActionKey">id@versão do catálogo (null em Info/Review sem ação).</param>
/// <param name="ModulePrefix">Prefixo do módulo dono (ex.: "cleaning."), para "Ver detalhes".</param>
/// <param name="OneClick">Entra no "Otimizar agora": recomendação, risco baixo, sem escolha do usuário, valor definido.</param>
public sealed record Recommendation(
    string RuleId,
    RecommendationKind Kind,
    string Title,
    string Reason,
    string? Gain,
    int Priority,
    string? ActionKey,
    string? ActionId,
    ActionRisk? Risk,
    PrivilegeLevel? Privileges,
    string? Value,
    string? ModulePrefix,
    bool OneClick)
{
    public string PriorityText => Priority switch { 1 => "alta", 2 => "média", _ => "baixa" };
    public string RiskText => Risk is { } r ? RiskModel.Text(r) : "—";
}

public sealed record RecommendationSet(PcProfile Profile, IReadOnlyList<Recommendation> Items)
{
    public IEnumerable<Recommendation> Actionable => Items.Where(i => i.Kind is RecommendationKind.Recommend or RecommendationKind.Consider or RecommendationKind.Review);
    public IEnumerable<Recommendation> Avoid => Items.Where(i => i.Kind == RecommendationKind.Avoid);
    public IEnumerable<Recommendation> Info => Items.Where(i => i.Kind == RecommendationKind.Info);
    public IEnumerable<Recommendation> OneClick => Items.Where(i => i.OneClick);
}

/// <summary>Motor pequeno: aplica as regras às etiquetas do perfil e liga cada uma às ações que EXISTEM no catálogo carregado.</summary>
public sealed class RecommendationEngine
{
    public const string Schema = "nexus.automation.rules/1";
    public const int MaxPerPrefixRule = 12;

    public IReadOnlyList<RecommendationRule> Rules { get; }

    public RecommendationEngine(IReadOnlyList<RecommendationRule> rules) => Rules = rules ?? throw new ArgumentNullException(nameof(rules));

    /// <summary>Regras de fábrica (recurso embutido Rules/recommendations.v1.json).</summary>
    public static RecommendationEngine Default()
    {
        using var s = typeof(RecommendationEngine).Assembly.GetManifestResourceStream("Nexus.Automation.Rules.recommendations.v1.json")
            ?? throw new InvalidOperationException("Regras de recomendação ausentes do assembly.");
        using var r = new StreamReader(s);
        return new RecommendationEngine(ParseRules(r.ReadToEnd()));
    }

    public static IReadOnlyList<RecommendationRule> ParseRules(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var schema = root.TryGetProperty("schema", out var sc) ? sc.GetString() : null;
        if (schema != Schema) throw new InvalidDataException($"Esquema das regras: {schema}, esperado {Schema}.");
        var list = new List<RecommendationRule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in root.GetProperty("rules").EnumerateArray())
        {
            string? S(string n) => r.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            IReadOnlyList<string> L(string n) => r.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
            var id = S("id") ?? throw new InvalidDataException("Regra sem id.");
            if (!ids.Add(id)) throw new InvalidDataException($"Regra repetida: {id}.");
            var kind = Enum.Parse<RecommendationKind>(S("kind") ?? throw new InvalidDataException($"{id}: sem kind."), ignoreCase: true);
            var rule = new RecommendationRule(id, kind, S("action"), S("actionPrefix"), S("module"), S("title"), L("when"), L("any"), L("unless"),
                r.TryGetProperty("priority", out var pr) ? pr.GetInt32() : 2,
                S("reason") ?? throw new InvalidDataException($"{id}: sem motivo."),
                S("gain"), S("value"),
                Enum.Parse<ActionRisk>(S("maxRisk") ?? "Low", ignoreCase: true));
            var targets = new[] { rule.Action, rule.ActionPrefix, rule.Module }.Count(x => x is not null);
            if (kind == RecommendationKind.Info ? targets != 0 || rule.Title is null : targets != 1)
                throw new InvalidDataException($"{id}: Info exige título e nenhum alvo; as demais, exatamente um de action/actionPrefix/module.");
            if (rule.Priority is < 1 or > 3) throw new InvalidDataException($"{id}: prioridade deve ser 1, 2 ou 3.");
            list.Add(rule);
        }
        return list;
    }

    /// <summary>
    /// Avalia as regras. Ordem: "evitar" primeiro (uma ação que não convém a este PC nunca aparece como recomendada), depois as
    /// demais na ordem do arquivo; cada ação aparece uma vez (a primeira regra que casar). Ações ausentes do catálogo são
    /// ignoradas em silêncio (o módulo dono ainda não existe neste build).
    /// </summary>
    public RecommendationSet Evaluate(PcProfile profile, CatalogDocument catalog, IReadOnlyDictionary<string, ActionRisk>? declaredRisk = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);
        declaredRisk ??= new Dictionary<string, ActionRisk>();
        parameters ??= new Dictionary<string, string>();
        // Última versão de cada id.
        var entries = catalog.Actions.GroupBy(a => a.Definition.Id, StringComparer.Ordinal)
            .Select(g => g.MaxBy(a => a.Definition.Version)!).ToList();
        var byId = entries.ToDictionary(e => e.Definition.Id, StringComparer.Ordinal);
        ActionRisk Risk(CatalogEntry e) => RiskModel.Of(e, declaredRisk.TryGetValue(e.Definition.Key, out var r) ? r : null);

        var items = new List<Recommendation>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var usedModules = new HashSet<string>(StringComparer.Ordinal);
        var matching = Rules.Where(r => r.Matches(profile.Tags)).ToList();

        foreach (var rule in matching.Where(r => r.Kind == RecommendationKind.Avoid).Concat(matching.Where(r => r.Kind != RecommendationKind.Avoid)))
        {
            var reason = Fill(rule.Reason, profile);
            switch (rule.Kind)
            {
                case RecommendationKind.Info:
                    items.Add(new(rule.Id, rule.Kind, Fill(rule.Title!, profile), reason, rule.Gain, rule.Priority, null, null, null, null, null, null, false));
                    break;

                case RecommendationKind.Review:
                {
                    var prefix = rule.Module ?? rule.ActionPrefix ?? ModuleOf(rule.Action!);
                    var title = rule.Title ?? $"Revisar {prefix.TrimEnd('.')}";
                    if (!entries.Any(e => e.Definition.Id.StartsWith(prefix, StringComparison.Ordinal))) break;
                    if (!usedModules.Add(prefix) || !usedModules.Add("title:" + title)) break;
                    items.Add(new(rule.Id, rule.Kind, title, reason, rule.Gain, rule.Priority,
                        null, null, null, null, null, prefix, false));
                    break;
                }

                default:
                {
                    var targets = rule.Action is not null
                        ? (byId.TryGetValue(rule.Action, out var one) ? [one] : [])
                        : entries.Where(e => e.Definition.Id.StartsWith(rule.ActionPrefix ?? rule.Module!, StringComparison.Ordinal)
                                             && Risk(e) <= rule.MaxRisk).Take(MaxPerPrefixRule).ToList();
                    foreach (var e in targets)
                    {
                        if (!used.Add(e.Definition.Id)) continue;
                        var risk = Risk(e);
                        var value = rule.Value;
                        var oneClick = rule.Kind == RecommendationKind.Recommend && risk == ActionRisk.Low && IsOneClickShape(e, value, parameters);
                        // Prefixo: só entra o que dá para aplicar sem escolha (o resto fica para a página do módulo).
                        if (rule.Action is null && rule.Kind != RecommendationKind.Avoid && !IsOneClickShape(e, value, parameters)) { used.Remove(e.Definition.Id); continue; }
                        items.Add(new(rule.Id, rule.Kind, rule.Title ?? e.Definition.Title, reason, rule.Gain, rule.Priority, e.Definition.Key, e.Definition.Id,
                            risk, e.Definition.Privileges, value, ModuleOf(e.Definition.Id), oneClick));
                    }
                    break;
                }
            }
        }

        var order = new Dictionary<RecommendationKind, int>
        {
            [RecommendationKind.Recommend] = 0, [RecommendationKind.Review] = 1, [RecommendationKind.Consider] = 2,
            [RecommendationKind.Info] = 3, [RecommendationKind.Avoid] = 4,
        };
        return new RecommendationSet(profile, items.OrderBy(i => order[i.Kind]).ThenBy(i => i.Priority).ToList());
    }

    /// <summary>A ação pode ser pedida sem perguntar nada: sem parâmetro do usuário, sem placeholder pendente e com valor definido.</summary>
    public static bool IsOneClickShape(CatalogEntry e, string? value, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.UserParameters.Count > 0) return false;
        if (e.Placeholders(parameters).Count > 0) return false;
        if (e.DesiredValue is not null) return value is null || value == e.DesiredValue;
        return value is not null && e.Definition.AllowedValues is { } allowed && allowed.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>"cleaning.temp.user" → "cleaning."; "win.gamemode.enable" → "win.".</summary>
    public static string ModuleOf(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        var dot = actionId.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? actionId : actionId[..(dot + 1)];
    }

    private static string Fill(string text, PcProfile p) => text
        .Replace("{gpu}", p.GpuShortName ?? "sua GPU", StringComparison.Ordinal)
        .Replace("{memoryGb}", p.MemoryGb?.ToString(CultureInfo.InvariantCulture) ?? "?", StringComparison.Ordinal)
        .Replace("{kind}", p.Kind, StringComparison.Ordinal);
}
