using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Automation;

/// <summary>Uma regra de limpeza que pode entrar na limpeza segura (um clique ou agendada).</summary>
public sealed record SafeCleaningItem(CatalogEntry Entry, CleaningRule Rule)
{
    public string ActionKey => Entry.Definition.Key;
    public string Title => Rule.Title;
}

/// <summary>Regra de limpeza que ficou de fora da limpeza segura, com o motivo.</summary>
public sealed record SafeCleaningExclusion(string ActionId, string Title, string Reason);

/// <summary>
/// Seleção da LIMPEZA SEGURA (usada no "Otimizar agora" e na limpeza agendada). Só entra a regra que cumpre TUDO:
/// ação <c>cleaning.*</c> do handler CleaningRule; privilégio User (nunca administrador); não irreversível; classificação
/// SupportedDocumented (nada condicional/experimental); risco declarado, se houver, baixo; sem reinício; o único parâmetro é o
/// lote; a regra embutida existe, é de quarentena, do perfil do usuário, não é sensível (histórico/cookies), não tem aviso
/// (ex.: shaders) e vem marcada por padrão na página Limpeza. Tudo vai para a quarentena, com diário e desfazer.
/// </summary>
public static class SafeCleaningSelector
{
    public static (IReadOnlyList<SafeCleaningItem> Included, IReadOnlyList<SafeCleaningExclusion> Excluded) Select(
        CatalogDocument catalog, CleaningRuleSet rules, IReadOnlyDictionary<string, ActionRisk>? declaredRisk = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(rules);
        var included = new List<SafeCleaningItem>();
        var excluded = new List<SafeCleaningExclusion>();
        var latest = catalog.Actions
            .Where(a => a.Definition.HandlerKind == CleaningRuleSet.HandlerKind && a.Definition.Id.StartsWith(CleaningRuleSet.ActionPrefix, StringComparison.Ordinal))
            .GroupBy(a => a.Definition.Id, StringComparer.Ordinal)
            .Select(g => g.MaxBy(a => a.Definition.Version)!)
            .OrderBy(a => a.Definition.Id, StringComparer.Ordinal);
        foreach (var entry in latest)
        {
            var ruleId = entry.TargetTemplate.GetValueOrDefault("rule");
            var rule = ruleId is null ? null : rules.Find(ruleId);
            ActionRisk? declared = declaredRisk is not null && declaredRisk.TryGetValue(entry.Definition.Key, out var r) ? r : null;
            var reason = ExclusionReason(entry, rule, declared);
            if (reason is null) included.Add(new SafeCleaningItem(entry, rule!));
            else excluded.Add(new SafeCleaningExclusion(entry.Definition.Id, rule?.Title ?? entry.Definition.Title, reason));
        }
        return (included, excluded);
    }

    /// <summary>Null = entra na limpeza segura; senão, o motivo de ficar de fora.</summary>
    public static string? ExclusionReason(CatalogEntry entry, CleaningRule? rule, ActionRisk? declaredRisk)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var d = entry.Definition;
        if (d.HandlerKind != CleaningRuleSet.HandlerKind) return "Não é uma regra de limpeza.";
        if (d.Privileges != PrivilegeLevel.User) return "Pede administrador (nunca entra na limpeza automática).";
        if (d.Irreversible) return "Irreversível.";
        if (d.Classification != Classification.SupportedDocumented) return "Condicional: fica para você escolher na página Limpeza.";
        if (declaredRisk is { } risk && risk != ActionRisk.Low) return $"Risco {RiskModel.Text(risk)}.";
        if (d.Reboot != RebootRequirement.None) return "Pede reinício.";
        if (entry.UserParameters.Count != 1 || !entry.UserParameters.ContainsKey("batch")) return "Pede escolha do usuário.";
        if (rule is null) return "Regra desconhecida por esta versão do NEXUS.";
        if (entry.TargetTemplate.GetValueOrDefault("rule") != rule.Id) return "Regra não confere com o catálogo.";
        if (rule.Mode != CleaningMode.Quarantine) return "Só medição (usa a ferramenta do Windows).";
        if (rule.Admin || rule.Scope != CleaningScope.User) return "Pasta do sistema (pede administrador).";
        if (rule.Sensitive) return "Dados pessoais (histórico ou cookies): só com você escolhendo.";
        if (rule.Warning is not null) return "Tem efeito colateral (ex.: engasgos depois de limpar shaders): só com você escolhendo.";
        if (!rule.DefaultSelected) return "Não vem marcada por padrão na página Limpeza.";
        return null;
    }

    /// <summary>Pedidos ao aplicador do usuário: todos no mesmo lote (uma limpeza = um lote da quarentena).</summary>
    public static IReadOnlyList<ActionRequest> Requests(IEnumerable<SafeCleaningItem> items, string batch)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!CleaningBatchId.IsValid(batch)) throw new ArgumentException("Lote inválido.", nameof(batch));
        return items.Select(i => new ActionRequest(i.ActionKey, null, new Dictionary<string, string> { ["batch"] = batch })).ToList();
    }

    /// <summary>Pastas da conta atual (para medir). Inclui as quarentenas como proibidas.</summary>
    [SupportedOSPlatform("windows")]
    public static CleaningFolders CurrentUserFolders(string userDataDirectory, string adminDataDirectory) =>
        WindowsCleaningFolders.ForCurrentProcess(new WindowsRegistryBackend()) with
        {
            ExtraForbidden = [Path.Combine(userDataDirectory, "quarantine"), Path.Combine(adminDataDirectory, "quarantine")],
        };

    /// <summary>Soma (somente leitura) do que as regras liberariam agora. Regras não medidas contam zero.</summary>
    public static async Task<long> MeasureAsync(IEnumerable<CleaningRule> rules, CleaningFolders folders, IProcessProbe processes, CancellationToken cancel = default)
    {
        var results = await CleaningScanner.ScanAsync(rules, folders, processes, DateTime.UtcNow, cancel: cancel).ConfigureAwait(false);
        return results.Where(r => r.Measured).Sum(r => r.Bytes);
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        return u == 0 ? string.Create(pt, $"{bytes} B") : v.ToString(v >= 100 ? "0" : "0.0", pt) + " " + units[u];
    }
}

/// <summary>Um item do resultado de uma limpeza.</summary>
public sealed record CleaningRunItem(string Action, string Title, string Outcome, string? Detail);

/// <summary>
/// Relatório de uma limpeza segura (agendada ou pela linha de comando): antes/depois medidos, o que foi para a quarentena, o
/// que foi pulado e como desfazer. Gravado em <c>%LocalAppData%\NEXUS\reports\clean-AAAAMMDD-HHMMSS.json/.html</c>.
/// </summary>
public sealed record CleaningRunReport(
    DateTimeOffset TakenAt,
    bool Scheduled,
    string? SkippedReason,
    string? Batch,
    Guid? RunId,
    long? BeforeBytes,
    long? AfterBytes,
    long MovedBytes,
    long MovedFiles,
    IReadOnlyList<CleaningRunItem> Items,
    IReadOnlyList<SafeCleaningExclusion> Excluded,
    string Message)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Uma linha para a notificação.</summary>
    public string Headline => SkippedReason is not null
        ? $"Limpeza agendada pulada: {SkippedReason}"
        : MovedFiles == 0
            ? "Limpeza segura: nada para limpar agora."
            : $"Limpeza segura: {SafeCleaningBytes(MovedBytes)} em {MovedFiles} arquivo(s) foram para a quarentena.";

    private static string SafeCleaningBytes(long b) => SafeCleaningSelector.Bytes(b);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Headline);
        if (BeforeBytes is { } before) sb.AppendLine(CultureInfo.InvariantCulture, $"  Antes: {SafeCleaningBytes(before)} recuperáveis nas regras seguras.");
        if (AfterBytes is { } after) sb.AppendLine(CultureInfo.InvariantCulture, $"  Depois: {SafeCleaningBytes(after)}.");
        foreach (var i in Items) sb.AppendLine(CultureInfo.InvariantCulture, $"  {i.Outcome,-18} {i.Title}{(i.Detail is null ? "" : $" — {i.Detail}")}");
        if (RunId is { } run) sb.AppendLine(CultureInfo.InvariantCulture, $"  Desfazer: nexus quarantine restore {Batch}  (ou nexus revert {run})");
        sb.AppendLine("  Os arquivos ficam na quarentena, no mesmo disco: o espaço só volta de fato quando ela for esvaziada.");
        return sb.ToString();
    }

    public string ToHtml()
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append(ReportStyle.Head("NEXUS — limpeza segura"));
        sb.Append(CultureInfo.InvariantCulture, $"<h1>{E(Headline)}</h1><div class=\"muted\">{(Scheduled ? "Limpeza agendada" : "Limpeza segura")} em {E(TakenAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))}{(Batch is null ? "" : $" · lote {E(Batch)}")}</div>");
        if (BeforeBytes is not null || AfterBytes is not null)
        {
            sb.Append("<div class=\"card\">");
            if (BeforeBytes is { } b) sb.Append(CultureInfo.InvariantCulture, $"<div>Antes: <b class=\"att\">{E(SafeCleaningBytes(b))}</b> recuperáveis nas regras seguras</div>");
            if (AfterBytes is { } a) sb.Append(CultureInfo.InvariantCulture, $"<div>Depois: <b class=\"ok\">{E(SafeCleaningBytes(a))}</b></div>");
            sb.Append("</div>");
        }
        if (Items.Count > 0)
        {
            sb.Append("<h2>O que aconteceu</h2>");
            foreach (var i in Items)
                sb.Append(CultureInfo.InvariantCulture, $"<div class=\"card\"><b>{E(i.Title)}</b> <span class=\"muted\">{E(i.Outcome)}</span>{(i.Detail is null ? "" : $"<div class=\"muted\">{E(i.Detail)}</div>")}</div>");
        }
        if (Excluded.Count > 0)
        {
            sb.Append("<h2>Ficou de fora (de propósito)</h2>");
            foreach (var x in Excluded)
                sb.Append(CultureInfo.InvariantCulture, $"<div class=\"card\"><b class=\"sig\">{E(x.Title)}</b><div class=\"muted\">{E(x.Reason)}</div></div>");
        }
        sb.Append("<p class=\"muted\">Nada foi apagado: os arquivos estão na quarentena do NEXUS, no mesmo disco, e podem voltar em Limpeza &gt; Histórico ou com ");
        sb.Append(CultureInfo.InvariantCulture, $"\"nexus quarantine restore {E(Batch ?? "<lote>")}\". O espaço só volta de fato quando a quarentena for esvaziada.</p></body></html>");
        return sb.ToString();
    }

    /// <summary>Grava JSON e HTML. Retorna os caminhos (o HTML por último).</summary>
    public IReadOnlyList<string> Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, "clean-" + TakenAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        File.WriteAllText(stem + ".json", ToJson(), new UTF8Encoding(false));
        File.WriteAllText(stem + ".html", ToHtml(), new UTF8Encoding(false));
        return [stem + ".json", stem + ".html"];
    }
}

/// <summary>Resumo da quarentena da limpeza para o relatório ("nexus report --html") e a linha de comando.</summary>
public sealed record CleaningOverview(long BytesInQuarantine, int Batches, IReadOnlyList<CleaningOverviewBatch> Recent)
{
    public static CleaningOverview Read(params string[] dataDirectories)
    {
        ArgumentNullException.ThrowIfNull(dataDirectories);
        var all = new List<QuarantineBatch>();
        foreach (var dir in dataDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { all.AddRange(QuarantineStore.ReadBatches(dir)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var grouped = all.GroupBy(b => b.BatchId, StringComparer.Ordinal)
            .Select(g => new CleaningOverviewBatch(g.Key, g.Min(b => b.CreatedUtc),
                string.Join(", ", g.SelectMany(b => b.Rules).Where(r => r.MovedFiles > 0).Select(r => r.RuleTitle).Distinct(StringComparer.Ordinal)),
                g.Sum(b => b.Rules.Sum(r => r.MovedBytes)), g.Sum(b => b.BytesInQuarantine), g.All(b => b.FullyRestored)))
            .OrderByDescending(b => b.CreatedUtc).ToList();
        return new CleaningOverview(grouped.Sum(b => b.BytesInQuarantine), grouped.Count, grouped.Take(10).ToList());
    }
}

public sealed record CleaningOverviewBatch(string Batch, DateTime CreatedUtc, string Rules, long MovedBytes, long BytesInQuarantine, bool Restored);

/// <summary>Cabeçalho HTML comum dos relatórios (mesmo visual da interface).</summary>
internal static class ReportStyle
{
    public static string Head(string title) => $$"""
        <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>
        body{background:#0E1320;color:#E6EAF2;font:15px/1.5 "Segoe UI",system-ui,sans-serif;margin:0;padding:24px;max-width:960px}
        h1{font:600 26px "Bahnschrift","Segoe UI",sans-serif;margin:0 0 4px}h2{font:600 18px "Bahnschrift","Segoe UI",sans-serif;margin:28px 0 8px}
        .muted{color:#8A94A8}.card{background:#161D2E;border:1px solid #26304A;border-radius:10px;padding:12px 16px;margin:8px 0}
        .tag{display:inline-block;border:1px solid #26304A;border-radius:9px;padding:0 8px;margin:2px;font-size:12px;color:#8A94A8}
        .att{color:#FFB547}.ok{color:#4ADE80}.bad{color:#F87171}.sig{color:#38BDF8}
        </style></head><body>
        """;
}
