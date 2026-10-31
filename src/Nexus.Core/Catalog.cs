using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Core;

/// <summary>Item rejeitado/mito do catálogo (com o motivo).</summary>
public sealed record RejectedCatalogItem(string Id, string Classification, string Reason, string? Source);

/// <summary>
/// Entrada do catálogo: definição + modelo de alvo (com possíveis placeholders como <c>{gameExePath}</c>) + valor desejado.
/// Não executa nada; <see cref="TryInstantiate"/> só monta uma <see cref="ActionInstance"/> quando todos os parâmetros
/// estão resolvidos e o valor desejado é único.
/// </summary>
public sealed partial record CatalogEntry(
    ActionDefinition Definition,
    IReadOnlyDictionary<string, string> TargetTemplate,
    string? DesiredValue,
    string? Condition,
    string? Verification,
    string? Revert,
    string PrivilegesRaw)
{
    [GeneratedRegex(@"\{([A-Za-z0-9|]+)\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Placeholders do alvo que ainda não foram resolvidos (ex.: gameExePath).</summary>
    public IReadOnlyList<string> Placeholders(IReadOnlyDictionary<string, string>? parameters = null) =>
        TargetTemplate.Values
            .SelectMany(v => PlaceholderPattern().Matches(v).Select(m => m.Groups[1].Value))
            .Where(p => parameters is null || !parameters.ContainsKey(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Tenta montar a ação concreta. Retorna null e o motivo quando falta parâmetro ou valor.</summary>
    public ActionInstance? TryInstantiate(IReadOnlyDictionary<string, string> parameters, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var missing = Placeholders(parameters);
        if (missing.Count > 0)
        {
            reason = $"Requer parâmetro: {string.Join(", ", missing)}.";
            return null;
        }

        var target = TargetTemplate.ToDictionary(kv => kv.Key,
            kv => PlaceholderPattern().Replace(kv.Value, m => parameters[m.Groups[1].Value]), StringComparer.Ordinal);

        // O valor AC é sempre lido/escrito no esquema ativo capturado no planejamento.
        if (Definition.HandlerKind == "PowerSettingValue" && !target.ContainsKey("scheme"))
        {
            if (!parameters.TryGetValue("activeScheme", out var scheme))
            {
                reason = "Requer o esquema de energia ativo (indisponível na descoberta).";
                return null;
            }
            target["scheme"] = scheme;
        }

        var desired = DesiredSnapshot(target, out reason);
        return desired is null ? null : new ActionInstance(Definition, target, desired);
    }

    private StateSnapshot? DesiredSnapshot(Dictionary<string, string> target, out string? reason)
    {
        reason = null;
        switch (Definition.HandlerKind)
        {
            case "PowerActiveScheme" when target.TryGetValue("schemeGuid", out var g):
                return StateSnapshot.Of("Guid", g.ToLowerInvariant());
        }

        if (DesiredValue is null)
        {
            reason = Definition.AllowedValues is { Count: > 0 } allowed
                ? $"Requer escolha de valor entre: {string.Join(", ", allowed)}."
                : "Catálogo não define valor desejado.";
            return null;
        }

        switch (Definition.HandlerKind)
        {
            case "RegistryValue" when target.TryGetValue("type", out var type):
                return StateSnapshot.Of(type, DesiredValue);
            case "PowerSettingValue":
                return StateSnapshot.Of("UInt32", DesiredValue);
            case "ServiceStartType":
                return StateSnapshot.Of("StartType", DesiredValue);
            case "DirectXUserGpuPreference":
                return StateSnapshot.Of("Token", DesiredValue);
            case "SourceCfgConvar":
                return StateSnapshot.Of("Convar", DesiredValue);
            case "StartupApproved":
                return StateSnapshot.Of("StartupState", DesiredValue);
            default:
                reason = $"Sem conversão de valor desejado para '{Definition.HandlerKind}'.";
                return null;
        }
    }
}

public sealed record CatalogDocument(string CatalogVersion, string Schema, IReadOnlyList<CatalogEntry> Actions, IReadOnlyList<RejectedCatalogItem> Rejected);

/// <summary>Lê catalog/actions.v1.json. Campos desconhecidos são ignorados; campos obrigatórios ausentes geram erro.</summary>
public static class CatalogLoader
{
    public static CatalogDocument Load(string path) => Parse(File.ReadAllText(path));

    public static CatalogDocument Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var actions = new List<CatalogEntry>();
        foreach (var a in root.GetProperty("actions").EnumerateArray())
            actions.Add(ParseAction(a));

        var rejected = new List<RejectedCatalogItem>();
        if (root.TryGetProperty("rejected", out var rej))
            foreach (var r in rej.EnumerateArray())
                rejected.Add(new RejectedCatalogItem(Str(r, "id")!, Str(r, "classification") ?? "?", Str(r, "reason") ?? "", Str(r, "source")));

        return new CatalogDocument(Str(root, "catalogVersion") ?? "?", Str(root, "schema") ?? "?", actions, rejected);
    }

    private static CatalogEntry ParseAction(JsonElement a)
    {
        var id = Str(a, "id") ?? throw new InvalidDataException("Ação sem id.");
        var privilegesRaw = Str(a, "privileges") ?? "Admin";
        // "User|Admin" depende do escopo do alvo: na dúvida, o mais restritivo.
        var privileges = privilegesRaw.Equals("User", StringComparison.OrdinalIgnoreCase) ? PrivilegeLevel.User : PrivilegeLevel.Admin;

        var compat = a.TryGetProperty("compatibility", out var c) ? c : default;
        var definition = new ActionDefinition(
            id,
            a.TryGetProperty("version", out var v) ? v.GetInt32() : 1,
            Str(a, "title") ?? id,
            Enum.Parse<Classification>(Str(a, "classification") ?? "Rejected", ignoreCase: true),
            Str(a, "handlerKind") ?? throw new InvalidDataException($"{id}: sem handlerKind."),
            Str(a, "mechanism") ?? "",
            a.TryGetProperty("evidence", out var ev)
                ? ev.EnumerateArray().Select(e => new EvidenceRef(Enum.Parse<EvidenceLevel>(Str(e, "level") ?? "Hypothesis", true), Str(e, "source") ?? "")).ToList()
                : [],
            Str(a, "risks") ?? "",
            privileges,
            Enum.Parse<PersistenceKind>(Str(a, "persistence") ?? "Persistent", true),
            Enum.Parse<RebootRequirement>(Str(a, "reboot") ?? "None", true),
            StrList(a, "dependsOn"),
            StrList(a, "conflictsWith"),
            a.TryGetProperty("allowedValues", out var av) ? av.EnumerateArray().Select(Scalar).ToList() : null,
            compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("minBuild", out var mi) ? mi.GetInt32() : null,
            compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("maxBuild", out var ma) ? ma.GetInt32() : null);

        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a.TryGetProperty("target", out var t))
            foreach (var p in t.EnumerateObject())
                target[p.Name] = Scalar(p.Value);

        return new CatalogEntry(definition, target,
            a.TryGetProperty("desiredValue", out var dv) ? Scalar(dv) : null,
            Str(a, "condition"), Str(a, "verification"), Str(a, "revert"), privilegesRaw);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static List<string>? StrList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(Scalar).ToList() : null;

    private static string Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Number => e.TryGetUInt64(out var u) ? u.ToString(CultureInfo.InvariantCulture) : e.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        _ => e.GetRawText(),
    };
}
