using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.HardwareDiscovery;

namespace Nexus.Automation;

public sealed record ReportFinding(string Id, string Title, string Severity, string Basis, string Evidence);

public sealed record ReportRecommendation(string Kind, string Title, string Reason, string? Gain, string Priority, string? Action, string? Risk, string? Privileges, bool OneClick);

/// <summary>Relatório da verificação (somente leitura): o que o PC é, o que foi visto e o que se recomenda. Sem nome do PC nem IPs.</summary>
public sealed record ScanReport(
    string Tool,
    DateTimeOffset TakenAt,
    string Profile,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> ProfileNotes,
    int? WindowsBuild,
    string? Cpu,
    IReadOnlyList<string> Gpus,
    int? MemoryGb,
    IReadOnlyList<ReportFinding> Findings,
    IReadOnlyList<ReportRecommendation> Recommendations,
    string CatalogVersion)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Resumo da quarentena da limpeza (opcional; "nexus report --html" preenche).</summary>
    public CleaningOverview? Cleaning { get; init; }

    public static ScanReport From(SystemFacts facts, IReadOnlyList<DiagnosticFinding> findings, RecommendationSet set, string catalogVersion, DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(set);
        return new ScanReport(
            "nexus scan",
            takenAt,
            set.Profile.Headline,
            set.Profile.Tags.Where(t => !t.StartsWith("diag:", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList(),
            set.Profile.Notes,
            facts.WindowsBuild,
            facts.CpuModel,
            facts.Gpus.Select(g => g.Name).ToList(),
            set.Profile.MemoryGb,
            findings.Select(f => new ReportFinding(f.Id, f.Title, f.Severity.ToString(), f.Basis.ToString(), f.Evidence)).ToList(),
            set.Items.Select(r => new ReportRecommendation(r.Kind.ToString(), r.Title, r.Reason, r.Gain, r.PriorityText, r.ActionKey,
                r.Risk is { } k ? RiskModel.Text(k) : null, r.Privileges?.ToString(), r.OneClick)).ToList(),
            catalogVersion);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static string KindText(string kind) => kind switch
    {
        "Recommend" => "Recomendado",
        "Consider" => "Testar com medição",
        "Review" => "Revisar",
        "Avoid" => "Não recomendado para este PC",
        "Info" => "Conselho",
        _ => kind,
    };

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Seu PC: {Profile}");
        foreach (var n in ProfileNotes) sb.AppendLine(CultureInfo.InvariantCulture, $"  · {n}");
        sb.AppendLine();
        var attention = Findings.Where(f => f.Severity == nameof(DiagnosticSeverity.Attention)).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Diagnóstico: {Findings.Count} verificações, {attention.Count} pedem atenção.");
        foreach (var f in attention) sb.AppendLine(CultureInfo.InvariantCulture, $"  ! {f.Title} — {f.Evidence}");
        foreach (var group in Recommendations.GroupBy(r => r.Kind))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{KindText(group.Key)}:");
            foreach (var r in group)
            {
                var tail = r.Action is null ? "" : $"  [{r.Action} · risco {r.Risk}{(r.Privileges == "Admin" ? " · administrador" : "")}{(r.OneClick ? " · Otimizar agora" : "")}]";
                sb.AppendLine(CultureInfo.InvariantCulture, $"  • {r.Title}{tail}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {r.Reason}");
                if (r.Gain is not null) sb.AppendLine(CultureInfo.InvariantCulture, $"    Ganho esperado: {r.Gain}");
            }
        }
        if (Cleaning is { } c)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Limpeza: {c.Batches} limpeza(s) registradas, {SafeCleaningSelector.Bytes(c.BytesInQuarantine)} na quarentena.");
            foreach (var b in c.Recent)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {b.CreatedUtc.ToLocalTime():dd/MM HH:mm}  {b.Batch}  {SafeCleaningSelector.Bytes(b.MovedBytes)}{(b.Restored ? " (desfeita)" : "")}  {b.Rules}");
        }
        return sb.ToString();
    }

    public string ToHtml()
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append(ReportStyle.Head("NEXUS — verificação"));
        sb.Append(CultureInfo.InvariantCulture, $"<h1>Seu PC: {E(Profile)}</h1><div class=\"muted\">Verificação somente leitura em {E(TakenAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))} · catálogo {E(CatalogVersion)}</div><div>");
        foreach (var t in Tags) sb.Append(CultureInfo.InvariantCulture, $"<span class=\"tag\">{E(t)}</span>");
        sb.Append("</div>");
        sb.Append("<h2>Diagnóstico</h2>");
        foreach (var f in Findings)
        {
            var cls = f.Severity == nameof(DiagnosticSeverity.Attention) ? "att" : f.Severity == nameof(DiagnosticSeverity.Ok) ? "ok" : "muted";
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"card\"><b class=\"{cls}\">{E(f.Title)}</b><div class=\"muted\">{E(f.Evidence)}</div></div>");
        }
        foreach (var group in Recommendations.GroupBy(r => r.Kind))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<h2>{E(KindText(group.Key))}</h2>");
            foreach (var r in group)
            {
                var cls = group.Key == "Avoid" ? "bad" : group.Key == "Recommend" ? "att" : "sig";
                sb.Append(CultureInfo.InvariantCulture, $"<div class=\"card\"><b class=\"{cls}\">{E(r.Title)}</b> <span class=\"muted\">prioridade {E(r.Priority)}");
                if (r.Action is not null) sb.Append(CultureInfo.InvariantCulture, $" · risco {E(r.Risk)} · {E(r.Action)}");
                sb.Append(CultureInfo.InvariantCulture, $"</span><div>{E(r.Reason)}</div>");
                if (r.Gain is not null) sb.Append(CultureInfo.InvariantCulture, $"<div class=\"muted\">Ganho esperado: {E(r.Gain)}</div>");
                sb.Append("</div>");
            }
        }
        if (Cleaning is { } c)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<h2>Limpeza</h2><div class=\"card\">{c.Batches} limpeza(s) registradas · <b class=\"att\">{E(SafeCleaningSelector.Bytes(c.BytesInQuarantine))}</b> na quarentena (o espaço volta quando ela for esvaziada)</div>");
            foreach (var b in c.Recent)
                sb.Append(CultureInfo.InvariantCulture, $"<div class=\"card\"><b>{E(b.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))}</b> <span class=\"muted\">lote {E(b.Batch)} · {E(SafeCleaningSelector.Bytes(b.MovedBytes))}{(b.Restored ? " · desfeita" : "")}</span><div class=\"muted\">{E(b.Rules)}</div></div>");
        }
        sb.Append("<p class=\"muted\">Nada foi alterado. Para aplicar, abra o NEXUS ou use \"nexus optimize\": tudo pede confirmação e pode ser desfeito.</p></body></html>");
        return sb.ToString();
    }

    /// <summary>Pasta padrão dos relatórios: %LocalAppData%\NEXUS\reports.</summary>
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "reports");

    /// <summary>Grava JSON (sempre) e HTML (opcional) com nome pela data. Retorna os caminhos gravados.</summary>
    public IReadOnlyList<string> Save(string directory, bool html)
    {
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, "scan-" + TakenAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var written = new List<string> { stem + ".json" };
        File.WriteAllText(stem + ".json", ToJson(), new UTF8Encoding(false));
        if (html)
        {
            File.WriteAllText(stem + ".html", ToHtml(), new UTF8Encoding(false));
            written.Add(stem + ".html");
        }
        return written;
    }
}
