using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.SelfTest;

/// <summary>Relatório do autoteste: texto (terminal), JSON (máquinas) e HTML (pessoas). Só monta texto; não grava nada.</summary>
public sealed record SelfTestReport(
    DateTimeOffset StartedAt,
    double Seconds,
    SelfTestEnvironment Environment,
    bool WithWindowsUpdate,
    bool WithBenchmarks,
    IReadOnlyList<CheckResult> Results)
{
    public const string Schema = "nexus.selftest/1";

    public int Ok => Results.Count(r => r.Status == CheckStatus.Ok);
    public int Warnings => Results.Count(r => r.Status == CheckStatus.Warning);
    public int Failures => Results.Count(r => r.Status == CheckStatus.Failure);
    public int Skipped => Results.Count(r => r.Status == CheckStatus.Skipped);

    /// <summary>0 se nenhuma verificação falhou; 1 se alguma falhou (AVISO e IGNORADO não contam).</summary>
    public int ExitCode => Failures > 0 ? 1 : 0;

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static string StatusText(CheckStatus s) => s switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Warning => "AVISO",
        CheckStatus.Failure => "FALHA",
        _ => "IGNORADO",
    };

    // ------------------------------------------------------------------ texto

    public static string Line(CheckResult r)
    {
        var head = $"[{StatusText(r.Status),-8}] {r.Module} · {r.Name} ({r.Seconds.ToString("0.0", Pt)} s)";
        var tail = r.Status switch
        {
            CheckStatus.Ok => r.Summary,
            CheckStatus.Skipped => r.Reason ?? "",
            _ => (r.Reason ?? "") + (r.Summary.Length > 0 ? $" | {r.Summary}" : ""),
        };
        return tail.Length == 0 ? head : $"{head} — {tail}";
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header());
        foreach (var r in Results) sb.AppendLine(Line(r));
        sb.AppendLine();
        sb.AppendLine(Footer());
        foreach (var f in Results.Where(r => r.Status == CheckStatus.Failure && r.Error is not null))
        {
            sb.AppendLine();
            sb.AppendLine(Pt, $"FALHA em {f.Module} · {f.Name}:");
            sb.AppendLine(f.Error);
        }
        return sb.ToString();
    }

    public string Header() =>
        $"NEXUS · autoteste no PC real (somente leitura) · {StartedAt.ToString("dd/MM/yyyy HH:mm", Pt)}\n" +
        $"{Environment.Os} · {Environment.Architecture} · administrador: {(Environment.Elevated is { } e ? (e ? "sim" : "não") : "desconhecido")} · NEXUS {Environment.NexusVersion}";

    public string Footer() =>
        $"{Results.Count} verificações em {Seconds.ToString("0.0", Pt)} s: {Ok} OK · {Warnings} AVISO · {Failures} FALHA · {Skipped} IGNORADO. " +
        (Failures == 0 ? "Nenhuma falha." : "Há falhas: veja os detalhes acima.") + " Nada foi alterado no sistema.";

    // ------------------------------------------------------------------ JSON

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        schema = Schema,
        startedAt = StartedAt,
        seconds = Seconds,
        environment = Environment,
        options = new { withWindowsUpdate = WithWindowsUpdate, withBenchmarks = WithBenchmarks },
        totals = new { checks = Results.Count, ok = Ok, warnings = Warnings, failures = Failures, skipped = Skipped },
        exitCode = ExitCode,
        results = Results.Select(r => new
        {
            module = r.Module,
            name = r.Name,
            status = r.Status,
            statusText = r.StatusText,
            seconds = r.Seconds,
            summary = r.Summary,
            reason = r.Reason,
            warnings = r.Warnings,
            error = r.Error,
            data = r.Data,
        }),
    }, Json);

    // ------------------------------------------------------------------ HTML

    /// <param name="ui">Resultado do autoteste da interface (opcional), incluído no mesmo relatório.</param>
    /// <param name="screenshotBase">Caminho (relativo ao HTML) da pasta onde estão as telas do <paramref name="ui"/>.</param>
    public string ToHtml(UiSelfTestReport? ui = null, string? screenshotBase = null)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>NEXUS · Autoteste</title>
            <style>
            :root{--bg:#0E1320;--panel:#151C2E;--line:#243049;--text:#E6EAF2;--muted:#8B97AD;--amber:#F5B544;--cyan:#4FD1E8;--good:#4ADE80;--bad:#F87171;--warn:#FBBF24;--skip:#64748B}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:1180px;margin:0 auto;padding:24px 16px 64px}
            h1{font-size:26px;margin:0 0 4px}h2{font-size:19px;margin:36px 0 12px;color:var(--cyan)}
            .muted{color:var(--muted)}.num{font-family:Bahnschrift,"Segoe UI",sans-serif;font-variant-numeric:tabular-nums}
            .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:12px;margin:20px 0}
            .tile{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:12px 14px}
            .tile b{display:block;font-size:30px;font-family:Bahnschrift,"Segoe UI",sans-serif}
            .ok b{color:var(--good)}.warn b{color:var(--warn)}.fail b{color:var(--bad)}.skip b{color:var(--skip)}
            table{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden}
            th,td{text-align:left;padding:8px 10px;border-top:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-weight:600;border-top:0}
            .chip{display:inline-block;min-width:74px;text-align:center;border-radius:999px;padding:1px 8px;font-size:12px;font-weight:700}
            .c-Ok{background:#163a26;color:var(--good)}.c-Warning{background:#3a2f12;color:var(--warn)}.c-Failure{background:#3d1717;color:var(--bad)}.c-Skipped{background:#1e2533;color:var(--skip)}
            details{margin-top:6px}summary{cursor:pointer;color:var(--cyan)}pre{white-space:pre-wrap;word-break:break-word;background:#0A0F1A;border:1px solid var(--line);border-radius:8px;padding:10px;font-size:12px}
            .wrap{overflow-x:auto}.shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:12px}
            .shot{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:8px}.shot img{width:100%;border-radius:6px;display:block}
            a{color:var(--cyan)}
            </style></head><body><main>
            """);
        sb.Append(Pt, $"<h1>Autoteste do NEXUS</h1><p class=\"muted\">{H(StartedAt.ToString("dd/MM/yyyy HH:mm", Pt))} · {H(Environment.Os)} · {H(Environment.Architecture)} · " +
                  $"administrador: {(Environment.Elevated is { } e ? (e ? "sim" : "não") : "desconhecido")} · NEXUS {H(Environment.NexusVersion)} · {H(Environment.Runtime)}</p>");
        sb.Append("<p>Somente leitura: o autoteste só lê o PC (nada foi alterado). O relatório não traz nomes de redes, programas nem caminhos do seu usuário; " +
                  "as telas da interface, se houver, mostram o que a janela mostrou. Você decide se compartilha.</p>");
        sb.Append("<div class=\"tiles\">");
        sb.Append(Pt, $"<div class=\"tile ok\"><b>{Ok}</b>OK</div><div class=\"tile warn\"><b>{Warnings}</b>aviso</div><div class=\"tile fail\"><b>{Failures}</b>falha</div><div class=\"tile skip\"><b>{Skipped}</b>ignorado</div>");
        sb.Append(Pt, $"<div class=\"tile\"><b class=\"num\">{H(Seconds.ToString("0", Pt))} s</b>duração</div>");
        if (ui is not null) sb.Append(Pt, $"<div class=\"tile {(ui.Failures > 0 ? "fail" : ui.Warnings > 0 ? "warn" : "ok")}\"><b>{ui.Pages.Count}</b>telas da interface</div>");
        sb.Append("</div>");

        sb.Append("<h2>Verificações (leituras de cada módulo)</h2><div class=\"wrap\"><table><tr><th>Resultado</th><th>Módulo</th><th>Verificação</th><th>Tempo</th><th>Detalhes</th></tr>");
        foreach (var r in Results.OrderBy(r => r.Status switch { CheckStatus.Failure => 0, CheckStatus.Warning => 1, CheckStatus.Ok => 2, _ => 3 }))
        {
            sb.Append(Pt, $"<tr><td><span class=\"chip c-{r.Status}\">{r.StatusText}</span></td><td>{H(r.Module)}</td><td>{H(r.Name)}</td><td class=\"num\">{H(r.Seconds.ToString("0.0", Pt))} s</td><td>");
            if (r.Reason is { Length: > 0 }) sb.Append(Pt, $"<div>{H(r.Reason)}</div>");
            if (r.Summary.Length > 0) sb.Append(Pt, $"<div class=\"muted\">{H(r.Summary)}</div>");
            if (r.Warnings.Count > 1) sb.Append("<details><summary>todos os avisos</summary><ul>" + string.Concat(r.Warnings.Select(w => $"<li>{H(w)}</li>")) + "</ul></details>");
            if (r.Error is { Length: > 0 }) sb.Append(Pt, $"<details><summary>exceção e pilha</summary><pre>{H(r.Error)}</pre></details>");
            sb.Append("</td></tr>");
        }
        sb.Append("</table></div>");

        if (ui is not null) AppendUi(sb, ui, screenshotBase);
        sb.Append("<p class=\"muted\" style=\"margin-top:32px\">Gerado por <code>nexus selftest</code>. Esquema JSON: ").Append(Schema).Append(". Ver docs/09-autoteste.md.</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void AppendUi(StringBuilder sb, UiSelfTestReport ui, string? shotBase)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        sb.Append("<h2>Interface (cada tela aberta de verdade)</h2>");
        sb.Append(Pt, $"<p class=\"muted\">Leitura do PC ao abrir: {(ui.InitialLoadCompleted ? "concluída" : "NÃO concluída")} em {H(ui.InitialLoadSeconds.ToString("0.0", Pt))} s · ");
        sb.Append(Pt, $"{ui.Pages.Count} telas · {ui.Failures} falha(s) · {ui.Warnings} aviso(s) · {ui.RefusedWrites} pedido(s) de escrita recusados pelo modo autoteste");
        sb.Append(ui.Aborted ? $" · INTERROMPIDO: {H(ui.AbortReason)}" : "").Append("</p>");
        if (ui.StartupExceptions.Count > 0)
            sb.Append("<details open><summary>Exceções ao abrir</summary><pre>" + H(string.Join("\n\n", ui.StartupExceptions)) + "</pre></details>");
        sb.Append("<div class=\"wrap\"><table><tr><th>Resultado</th><th>Tela</th><th>Carga</th><th>Parada</th><th>Binding</th><th>Detalhes</th></tr>");
        foreach (var p in ui.Pages)
        {
            sb.Append(Pt, $"<tr><td><span class=\"chip c-{p.Status}\">{p.StatusText}</span></td><td>{H(p.Title)}<div class=\"muted\">{H(p.Group)}</div></td>" +
                      $"<td class=\"num\">{H(p.LoadSeconds.ToString("0.0", Pt))} s{(p.ReachedIdle ? "" : " (tempo limite)")}</td>" +
                      $"<td class=\"num\">{H(p.LongestFreezeSeconds.ToString("0.0", Pt))} s</td><td class=\"num\">{p.BindingErrors}</td><td>");
            if (!p.HasTemplate) sb.Append("<div>Sem DataTemplate: a tela mostraria só o nome do tipo.</div>");
            foreach (var x in p.Exceptions) sb.Append(Pt, $"<details open><summary>exceção</summary><pre>{H(x)}</pre></details>");
            if (p.BindingSamples.Count > 0) sb.Append("<details><summary>erros de binding</summary><pre>" + H(string.Join("\n", p.BindingSamples)) + "</pre></details>");
            if (p.Message is { Length: > 0 }) sb.Append(Pt, $"<div class=\"muted\">Mensagem da tela: {H(p.Message)}</div>");
            foreach (var n in p.Notes) sb.Append(Pt, $"<div class=\"muted\">{H(n)}</div>");
            sb.Append("</td></tr>");
        }
        sb.Append("</table></div>");
        var shots = ui.Pages.Where(p => p.Screenshot is not null).ToList();
        if (shots.Count == 0) return;
        sb.Append("<h2>Telas</h2><div class=\"shots\">");
        foreach (var p in shots)
        {
            var src = string.IsNullOrEmpty(shotBase) ? p.Screenshot! : shotBase.TrimEnd('/', '\\') + "/" + p.Screenshot!.Replace('\\', '/');
            sb.Append(Pt, $"<div class=\"shot\"><a href=\"{H(src)}\"><img loading=\"lazy\" src=\"{H(src)}\" alt=\"{H(p.Title)}\"></a><div><span class=\"chip c-{p.Status}\">{p.StatusText}</span> {H(p.Title)}</div></div>");
        }
        sb.Append("</div>");
    }
}
