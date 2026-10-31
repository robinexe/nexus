using System.Text.Json;
using Nexus.Core;

namespace Nexus.Privacy;

/// <summary>Em qual perfil o item entra. Opcional = só no Personalizado (há perda real, ex.: localização).</summary>
public enum PrivacyProfile { Recommended, Strict, Optional }

/// <summary>Um interruptor da página: agrupa uma ou mais ações do catálogo que juntas protegem uma coisa.</summary>
/// <param name="ProtectedValues">Valores (por id de ação) que também contam como protegidos, além do desejado. Ex.: AllowTelemetry
/// 0 (Enterprise) protege tanto quanto 1.</param>
public sealed record PrivacyItem(
    string Id,
    string Theme,
    string Title,
    string Summary,
    string Loses,
    PrivacyProfile Profile,
    IReadOnlyList<CatalogEntry> Actions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProtectedValues,
    string? Warning)
{
    /// <summary>Alguma ação do item exige o aplicador elevado.</summary>
    public bool NeedsAdmin => Actions.Any(a => a.Definition.Privileges == PrivilegeLevel.Admin);

    /// <summary>Todas as ações do item são documentadas pelo fornecedor (nenhuma depende só de fonte da comunidade).</summary>
    public bool FullyDocumented => Actions.All(a => a.Definition.Evidence.Any(e => e.Level == EvidenceLevel.VendorDocumented));

    /// <summary>Entra no perfil pedido? Rígido inclui o Recomendado; Opcional nunca entra num perfil.</summary>
    public bool InProfile(PrivacyProfile profile) => profile switch
    {
        PrivacyProfile.Recommended => Profile == PrivacyProfile.Recommended,
        PrivacyProfile.Strict => Profile is PrivacyProfile.Recommended or PrivacyProfile.Strict,
        _ => false,
    };
}

/// <summary>Item só de leitura (permissões de câmera/microfone, referência ao serviço DiagTrack).</summary>
public sealed record PrivacyInfo(string Id, string Theme, string Title, string Text, string? SettingsUri, string? ConsentStore);

/// <summary>catalog/privacy.v1.json: as ações (formato comum do catálogo) + os itens da página + os itens informativos.</summary>
public sealed class PrivacyCatalog
{
    public const string FileName = "privacy.v1.json";
    public const string IdPrefix = "privacy.";

    public CatalogDocument Document { get; }
    public IReadOnlyList<PrivacyItem> Items { get; }
    public IReadOnlyList<PrivacyInfo> Info { get; }

    private PrivacyCatalog(CatalogDocument document, IReadOnlyList<PrivacyItem> items, IReadOnlyList<PrivacyInfo> info)
    {
        Document = document;
        Items = items;
        Info = info;
    }

    public static PrivacyCatalog LoadFromDirectory(string catalogDirectory) => Load(Path.Combine(catalogDirectory, FileName));

    public static PrivacyCatalog Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Lê e valida: cada ação pertence a exatamente um item, ids únicos e com o prefixo do módulo.</summary>
    public static PrivacyCatalog Parse(string json)
    {
        var document = CatalogLoader.Parse(json);
        var byId = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var a in document.Actions)
        {
            if (!a.Definition.Id.StartsWith(IdPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"Ação '{a.Definition.Id}' sem o prefixo '{IdPrefix}'.");
            if (!byId.TryAdd(a.Definition.Id, a))
                throw new InvalidDataException($"Ação repetida: '{a.Definition.Id}'.");
        }

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var items = new List<PrivacyItem>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("privacyItems", out var arr))
            foreach (var e in arr.EnumerateArray())
            {
                var id = Req(e, "id");
                if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || !itemIds.Add(id) || byId.ContainsKey(id))
                    throw new InvalidDataException($"Item com id inválido ou repetido: '{id}'.");
                var actions = new List<CatalogEntry>();
                foreach (var ref_ in e.GetProperty("actions").EnumerateArray())
                {
                    var aid = ref_.GetString() ?? "";
                    if (!byId.TryGetValue(aid, out var entry)) throw new InvalidDataException($"{id}: ação '{aid}' não existe no catálogo.");
                    if (!used.Add(aid)) throw new InvalidDataException($"{id}: ação '{aid}' já pertence a outro item.");
                    actions.Add(entry);
                }
                if (actions.Count == 0) throw new InvalidDataException($"{id}: item sem ações.");
                var protectedValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                if (e.TryGetProperty("protectedValues", out var pv))
                    foreach (var p in pv.EnumerateObject())
                    {
                        if (actions.All(a => a.Definition.Id != p.Name)) throw new InvalidDataException($"{id}: protectedValues cita ação de fora do item ('{p.Name}').");
                        protectedValues[p.Name] = p.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToList();
                    }
                items.Add(new PrivacyItem(id, Req(e, "theme"), Req(e, "title"), Req(e, "summary"), Req(e, "loses"),
                    Enum.Parse<PrivacyProfile>(Req(e, "profile"), ignoreCase: false), actions, protectedValues,
                    e.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null));
            }

        var orphan = byId.Keys.Where(k => !used.Contains(k)).ToList();
        if (orphan.Count > 0) throw new InvalidDataException($"Ações sem item na página: {string.Join(", ", orphan)}.");

        var info = new List<PrivacyInfo>();
        if (root.TryGetProperty("privacyInfo", out var inf))
            foreach (var e in inf.EnumerateArray())
            {
                string? Opt(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var id = Req(e, "id");
                if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || !itemIds.Add(id)) throw new InvalidDataException($"Informação com id inválido ou repetido: '{id}'.");
                var uri = Opt("settingsUri");
                if (uri is not null && !SettingsLauncher.IsAllowed(uri)) throw new InvalidDataException($"{id}: endereço de Configurações não permitido ('{uri}').");
                info.Add(new PrivacyInfo(id, Req(e, "theme"), Req(e, "title"), Req(e, "text"), uri, Opt("consentStore")));
            }

        return new PrivacyCatalog(document, items, info);
    }

    private static string Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!
            : throw new InvalidDataException($"Campo obrigatório ausente: '{name}'.");
}
