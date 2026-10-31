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

    /// <summary>Tipo do valor desejado para handlers sem conversão própria no núcleo (campo "desiredType" do catálogo).</summary>
    public string? DesiredType { get; init; }

    /// <summary>
    /// Parâmetros que o USUÁRIO escolhe (ex.: qual driver antigo remover), cada um com a expressão regular que o valor precisa
    /// casar por inteiro (campo "userParameters"). Só podem ocupar o valor inteiro de um campo do alvo ("{nome}"), para a
    /// restauração reconstruir e revalidar o alvo a partir do diário.
    /// </summary>
    public IReadOnlyDictionary<string, string> UserParameters { get; init; } = new Dictionary<string, string>();

    public const int MaxUserArguments = 8;
    public const int MaxUserArgumentLength = 260;

    /// <summary>Valida argumentos do usuário: só os declarados, todos presentes, cada um casando sua regra por inteiro.</summary>
    public IReadOnlyDictionary<string, string>? ValidateUserArguments(IReadOnlyDictionary<string, string>? args, out string? reason)
    {
        reason = null;
        args ??= new Dictionary<string, string>();
        if (args.Count > MaxUserArguments) { reason = "Argumentos demais."; return null; }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in args)
        {
            if (!UserParameters.TryGetValue(name, out var pattern)) { reason = $"Argumento '{name}' não é aceito por esta ação."; return null; }
            if (value is null || value.Length == 0 || value.Length > MaxUserArgumentLength) { reason = $"Argumento '{name}' vazio ou longo demais."; return null; }
            if (!MatchesWhole(pattern, value)) { reason = $"Valor de '{name}' fora do formato permitido."; return null; }
            result[name] = value;
        }
        var missing = UserParameters.Keys.Where(k => !result.ContainsKey(k)).ToList();
        if (missing.Count > 0) { reason = $"Falta escolher: {string.Join(", ", missing)}."; return null; }
        return result;
    }

    /// <summary>Recupera os argumentos do usuário a partir de um alvo registrado (diário) e os revalida.</summary>
    public IReadOnlyDictionary<string, string>? UserArgumentsFromTarget(IReadOnlyDictionary<string, string> target, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, template) in TargetTemplate)
        {
            var m = PlaceholderPattern().Match(template);
            if (!m.Success || m.Value != template || !UserParameters.ContainsKey(m.Groups[1].Value)) continue;
            if (!target.TryGetValue(key, out var v)) { reason = $"Alvo registrado sem '{key}'."; return null; }
            args[m.Groups[1].Value] = v;
        }
        return ValidateUserArguments(args, out reason);
    }

    internal static bool MatchesWhole(string pattern, string value)
    {
        try { return Regex.IsMatch(value, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (RegexMatchTimeoutException) { return false; }
    }

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

        if (DesiredValue is not null && DesiredType is not null)
            return StateSnapshot.Of(DesiredType, DesiredValue);

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
    public const string MainFile = "actions.v1.json";

    public static CatalogDocument Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Catálogo completo: <see cref="MainFile"/> mais os módulos (outros <c>*.json</c> da pasta, em ordem alfabética), cada um
    /// no mesmo formato. Uma chave id@versão repetida entre arquivos é erro (nunca uma ação "sobrescreve" outra em silêncio).
    /// </summary>
    public static CatalogDocument LoadDirectory(string directory)
    {
        var main = Load(Path.Combine(directory, MainFile));
        var actions = new List<CatalogEntry>(main.Actions);
        var rejected = new List<RejectedCatalogItem>(main.Rejected);
        var modules = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Where(f => !Path.GetFileName(f).Equals(MainFile, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var m = Load(file);
            if (m.Schema != main.Schema)
                throw new InvalidDataException($"{Path.GetFileName(file)}: esquema {m.Schema}, esperado {main.Schema}.");
            foreach (var a in m.Actions)
            {
                if (actions.Any(x => x.Definition.Key == a.Definition.Key))
                    throw new InvalidDataException($"{Path.GetFileName(file)}: ação {a.Definition.Key} já existe em outro arquivo do catálogo.");
                actions.Add(a);
            }
            rejected.AddRange(m.Rejected);
            modules.Add($"{Path.GetFileNameWithoutExtension(file)}@{m.CatalogVersion}");
        }
        var version = modules.Count == 0 ? main.CatalogVersion : $"{main.CatalogVersion}+{string.Join("+", modules)}";
        return new CatalogDocument(version, main.Schema, actions, rejected);
    }

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
            compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("maxBuild", out var ma) ? ma.GetInt32() : null)
        {
            Irreversible = a.TryGetProperty("irreversible", out var irr) && irr.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException($"{id}: \"irreversible\" deve ser true ou false."),
            },
        };
        if (definition.Irreversible && definition.Persistence == PersistenceKind.Session)
            throw new InvalidDataException($"{id}: ação irreversível não pode ser de sessão (a sessão é revertida ao encerrar).");

        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a.TryGetProperty("target", out var t))
            foreach (var p in t.EnumerateObject())
                target[p.Name] = Scalar(p.Value);

        var userParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a.TryGetProperty("userParameters", out var up) && up.ValueKind == JsonValueKind.Object)
            foreach (var p in up.EnumerateObject())
            {
                var pattern = p.Value.GetString() ?? throw new InvalidDataException($"{id}: regra vazia para '{p.Name}'.");
                try { _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException ex) { throw new InvalidDataException($"{id}: regra inválida para '{p.Name}': {ex.Message}"); }
                // O parâmetro do usuário só pode ser o valor INTEIRO de um campo do alvo.
                var uses = target.Values.Where(v => v.Contains("{" + p.Name + "}", StringComparison.Ordinal)).ToList();
                if (uses.Count == 0 || uses.Any(v => v != "{" + p.Name + "}"))
                    throw new InvalidDataException($"{id}: parâmetro do usuário '{p.Name}' precisa ser o valor inteiro de um campo do alvo.");
                userParameters[p.Name] = pattern;
            }

        return new CatalogEntry(definition, target,
            a.TryGetProperty("desiredValue", out var dv) ? Scalar(dv) : null,
            Str(a, "condition"), Str(a, "verification"), Str(a, "revert"), privilegesRaw)
        {
            DesiredType = Str(a, "desiredType"),
            UserParameters = userParameters,
        };
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
