using System.Text.Json;

namespace Nexus.Boot;

/// <summary>Um serviço dentro de um preset: a ação do catálogo que o leva ao tipo de início proposto, e por quê.</summary>
public sealed record ServicePresetItem(string ActionKey, string Service, string StartType, string Why);

/// <summary>Grupo de ajustes explicado (campo "presets" de catalog/services.v1.json; o carregador do catálogo ignora este campo).</summary>
public sealed record ServicePreset(string Id, string Title, string Summary, string ForWho, string Honesty, IReadOnlyList<ServicePresetItem> Items);

public static class ServicePresets
{
    public const string FileName = "services.v1.json";

    /// <summary>Ação do catálogo para alterar um serviço escolhido pelo usuário (userParameters "service").</summary>
    public const string IndividualActionKey = "boot.service.starttype@1";

    public static IReadOnlyList<ServicePreset> Load(string catalogDirectory) => Parse(File.ReadAllText(Path.Combine(catalogDirectory, FileName)));

    public static IReadOnlyList<ServicePreset> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var list = new List<ServicePreset>();
        if (!doc.RootElement.TryGetProperty("presets", out var presets)) return list;
        foreach (var p in presets.EnumerateArray())
        {
            var items = p.GetProperty("items").EnumerateArray().Select(i => new ServicePresetItem(
                Req(i, "action"), Req(i, "service"), Req(i, "startType"), Req(i, "why"))).ToList();
            list.Add(new ServicePreset(Req(p, "id"), Req(p, "title"), Req(p, "summary"), Req(p, "forWho"), Req(p, "honesty"), items));
        }
        return list;
    }

    private static string Req(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"Preset sem '{name}'.");

    /// <summary>Rótulo em português do tipo de início canônico.</summary>
    public static string StartTypeLabel(string startType) => startType switch
    {
        "Automatic" => "Automático",
        "AutomaticDelayed" => "Automático (atrasado)",
        "Manual" => "Manual",
        "Disabled" => "Desativado",
        "Boot" => "Boot (driver)",
        "System" => "Sistema (driver)",
        _ => "Desconhecido",
    };

    public static string StateLabel(string state) => state switch
    {
        "Running" => "Em execução",
        "Stopped" => "Parado",
        "StartPending" => "Iniciando",
        "StopPending" => "Parando",
        "Paused" => "Pausado",
        _ => "Desconhecido",
    };
}
