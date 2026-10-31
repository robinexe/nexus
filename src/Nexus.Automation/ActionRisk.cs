using System.Text.Json;
using Nexus.Core;

namespace Nexus.Automation;

public enum ActionRisk { Low, Medium, High }

/// <summary>
/// Risco de uma ação do catálogo para o Automático. Um campo opcional <c>"risk": "Low|Medium|High"</c> no catálogo (os módulos
/// podem declarar) é respeitado, mas nunca deixa uma ação experimental ou rejeitada parecer segura. Sem o campo:
/// documentada e sem escolha do usuário = baixo; condicional = médio; experimental/laboratório = alto.
/// </summary>
public static class RiskModel
{
    public static ActionRisk Of(CatalogEntry entry, ActionRisk? declared = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var d = entry.Definition;
        if (d.Classification is Classification.Experimental or Classification.Rejected or Classification.Obsolete
            or Classification.Incompatible or Classification.Ineffective)
            return ActionRisk.High;
        if (declared is { } r) return r;
        if (d.Classification == Classification.Conditional) return ActionRisk.Medium;
        if (entry.UserParameters.Count > 0 || d.Reboot is RebootRequirement.Required or RebootRequirement.Recommended) return ActionRisk.Medium;
        return ActionRisk.Low;
    }

    /// <summary>Lê o campo opcional "risk" de cada ação nos arquivos do catálogo (chave id@versão). Arquivo ilegível: ignorado.</summary>
    public static IReadOnlyDictionary<string, ActionRisk> ReadDeclared(string catalogDirectory)
    {
        var result = new Dictionary<string, ActionRisk>(StringComparer.Ordinal);
        if (!Directory.Exists(catalogDirectory)) return result;
        foreach (var file in Directory.EnumerateFiles(catalogDirectory, "*.json"))
        {
            try { foreach (var (k, v) in ParseDeclared(File.ReadAllText(file))) result[k] = v; }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return result;
    }

    public static IEnumerable<(string Key, ActionRisk Risk)> ParseDeclared(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        if (!doc.RootElement.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) yield break;
        foreach (var a in actions.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!a.TryGetProperty("risk", out var risk) || risk.ValueKind != JsonValueKind.String) continue;
            if (!Enum.TryParse<ActionRisk>(risk.GetString(), ignoreCase: true, out var r) || !Enum.IsDefined(r)) continue;
            var version = a.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;
            yield return ($"{id.GetString()}@{version}", r);
        }
    }

    public static string Text(ActionRisk r) => r switch
    {
        ActionRisk.Low => "baixo",
        ActionRisk.Medium => "médio",
        _ => "alto",
    };
}
