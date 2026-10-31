using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nexus.Broker;

namespace Nexus.Assistant;

/// <summary>Perfil do PC para o relatório: só hardware e Windows (nunca nome do computador, usuário, IPs ou números de série).</summary>
public sealed record PcSummary(string Kind, string? Cpu, string? Gpu, string? Ram, string? Windows, string? Disk);

public sealed record ReportStep(int Number, string Title, string Status, string? Summary);

/// <summary>Relatório final do assistente. Montado a partir do estado; o HTML é autocontido, legível na tela e impresso.</summary>
public sealed record AssistantReport(
    DateTimeOffset At,
    PcSummary Pc,
    IReadOnlyList<ReportStep> Steps,
    IReadOnlyList<ChangeRecord> Changed,
    IReadOnlyList<SkipRecord> Skipped,
    IReadOnlyList<UserAction> UserActions,
    IReadOnlyList<MetricRecord> Metrics,
    string NextStep,
    bool CanUndo)
{
    public int AppliedCount => Changed.Count(c => c.Outcome == ChangeOutcome.Applied);

    public static AssistantReport From(AssistantState state, PcSummary pc, DateTimeOffset at, ReportRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pc);
        var r = redactor ?? ReportRedactor.None;
        var steps = AssistantSteps.All.Where(s => s.Id != AssistantStepId.Report).Select(s =>
        {
            state.Steps.TryGetValue(s.Id, out var rec);
            var status = rec?.Status switch { StepStatus.Done => "verificada", StepStatus.Skipped => "pulada", _ => "não feita" };
            return new ReportStep(s.Number, s.Title, status, r.Clean(rec?.Summary));
        }).ToList();
        var changed = state.Changes.Where(c => c.Outcome is ChangeOutcome.Applied or ChangeOutcome.AlreadyConfigured)
            .Select(c => c with { Title = r.Clean(c.Title)!, Before = r.Clean(c.Before), After = r.Clean(c.After), Detail = r.Clean(c.Detail) })
            .ToList();
        var skipped = state.Skipped.Select(s => s with { What = r.Clean(s.What)!, Why = r.Clean(s.Why)! }).ToList();
        var actions = state.UserActions.Select(a => a with { Title = r.Clean(a.Title)!, Why = r.Clean(a.Why)! }).ToList();
        var metrics = state.Metrics.Values.Select(m => m with { Label = r.Clean(m.Label)!, Before = r.Clean(m.Before), After = r.Clean(m.After), Note = r.Clean(m.Note) }).ToList();
        var safePc = pc with { Kind = r.Clean(pc.Kind)!, Cpu = r.Clean(pc.Cpu), Gpu = r.Clean(pc.Gpu), Ram = r.Clean(pc.Ram), Windows = r.Clean(pc.Windows), Disk = r.Clean(pc.Disk) };
        return new AssistantReport(at, safePc, steps, changed, skipped, actions, metrics, NextStepFor(state), state.HasUndoableRuns);
    }

    /// <summary>O próximo passo mais útil: o que exige o usuário primeiro, depois reiniciar, depois medir.</summary>
    public static string NextStepFor(AssistantState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.UserActions.Count > 0)
            return $"Faça \"{state.UserActions[0].Title}\": é o que mais pode mudar o desempenho e o NEXUS não consegue fazer por você.";
        if (state.Changes.Any(c => c.Outcome == ChangeOutcome.Applied && c.Level == BrokerLevel.Admin))
            return "Reinicie o PC para as mudanças de sistema valerem e jogue uma partida com a medição automática ligada para ver o antes e depois.";
        if (state.Changes.Any(c => c.Outcome == ChangeOutcome.Applied))
            return "Jogue uma partida com a medição automática ligada para ver o antes e depois.";
        return "Nada foi alterado. Rode o assistente de novo quando quiser; a verificação semanal avisa se algo mudar.";
    }

    public string ToHtml()
    {
        var sb = new StringBuilder();
        var date = At.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        sb.Append(Head("NEXUS · Relatório do assistente"));
        sb.Append("<header><p class=\"eyebrow\">NEXUS · Assistente completo</p><h1>Relatório da otimização</h1>")
          .Append("<p class=\"muted\">").Append(E(date)).Append(" · ").Append(AppliedCount.ToString(CultureInfo.InvariantCulture))
          .Append(AppliedCount == 1 ? " mudança aplicada" : " mudanças aplicadas")
          .Append(CanUndo ? " · tudo pode ser desfeito no NEXUS (Assistente completo › Desfazer tudo)" : "").Append("</p></header>");

        // Números
        if (Metrics.Count > 0)
        {
            sb.Append("<h2>Números</h2><div class=\"grid\">");
            foreach (var m in Metrics)
            {
                sb.Append("<div class=\"card metric\"><div class=\"label\">").Append(E(m.Label)).Append("</div><div class=\"num\">");
                if (m.Before is not null && m.After is not null && m.Before != m.After)
                    sb.Append("<span class=\"muted\">").Append(E(m.Before)).Append("</span> <span class=\"arrow\">→</span> <span class=\"ok\">").Append(E(m.After)).Append("</span>");
                else sb.Append(E(m.After ?? m.Before ?? "—"));
                sb.Append("</div>");
                if (m.Note is not null) sb.Append("<div class=\"small muted\">").Append(E(m.Note)).Append("</div>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        // Próximo passo
        sb.Append("<div class=\"card next\"><div class=\"label\">Próximo passo</div><p>").Append(E(NextStep)).Append("</p></div>");

        // Perfil
        sb.Append("<h2>Seu PC</h2><table><tbody>");
        Row(sb, "Tipo", Pc.Kind);
        Row(sb, "Processador", Pc.Cpu);
        Row(sb, "Placa de vídeo", Pc.Gpu);
        Row(sb, "Memória", Pc.Ram);
        Row(sb, "Windows", Pc.Windows);
        Row(sb, "Disco do sistema", Pc.Disk);
        sb.Append("</tbody></table>");

        // Ações do usuário
        if (UserActions.Count > 0)
        {
            sb.Append("<h2>O que só você pode fazer</h2>");
            foreach (var a in UserActions)
            {
                sb.Append("<div class=\"card att-card\"><strong>").Append(E(a.Title)).Append("</strong><p>").Append(E(a.Why)).Append("</p>");
                if (a.Link is not null && a.Link.StartsWith("https://", StringComparison.Ordinal))
                    sb.Append("<p class=\"small\"><a href=\"").Append(E(a.Link)).Append("\">").Append(E(a.Link)).Append("</a></p>");
                sb.Append("</div>");
            }
        }

        // Verificado
        sb.Append("<h2>O que foi verificado</h2><table><thead><tr><th>Etapa</th><th>Situação</th><th>Resumo</th></tr></thead><tbody>");
        foreach (var s in Steps)
            sb.Append("<tr><td>").Append(s.Number.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(E(s.Title)).Append("</td><td class=\"")
              .Append(s.Status == "verificada" ? "ok" : s.Status == "pulada" ? "att" : "muted").Append("\">").Append(E(s.Status)).Append("</td><td>")
              .Append(E(s.Summary ?? "")).Append("</td></tr>");
        sb.Append("</tbody></table>");

        // Alterado
        sb.Append("<h2>O que foi alterado</h2>");
        if (Changed.Count == 0) sb.Append("<p class=\"muted\">Nada foi alterado.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Etapa</th><th>Mudança</th><th>Antes → depois</th><th>Nível</th><th>Risco</th><th>Desfaz?</th></tr></thead><tbody>");
            foreach (var c in Changed)
                sb.Append("<tr><td>").Append(((int)c.Step).ToString(CultureInfo.InvariantCulture)).Append("</td><td>").Append(E(c.Title))
                  .Append(c.Outcome == ChangeOutcome.AlreadyConfigured ? " <span class=\"muted\">(já estava assim)</span>" : "").Append("</td><td>")
                  .Append(E(c.Before is null && c.After is null ? "—" : $"{c.Before ?? "?"} → {c.After ?? "?"}")).Append("</td><td>")
                  .Append(c.Level == BrokerLevel.Admin ? "administrador" : "sua conta").Append("</td><td>").Append(E(c.Risk)).Append("</td><td>")
                  .Append(c.Undoable ? "sim" : c.Outcome == ChangeOutcome.AlreadyConfigured ? "—" : "não precisa").Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        // Pulado
        sb.Append("<h2>O que ficou de fora e por quê</h2>");
        if (Skipped.Count == 0) sb.Append("<p class=\"muted\">Nada ficou de fora.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Etapa</th><th>Item</th><th>Motivo</th></tr></thead><tbody>");
            foreach (var s in Skipped)
                sb.Append("<tr><td>").Append(((int)s.Step).ToString(CultureInfo.InvariantCulture)).Append("</td><td>").Append(E(s.What)).Append("</td><td>").Append(E(s.Why)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<footer class=\"small muted\"><p>Gerado pelo NEXUS no seu PC. Nada foi enviado pela internet. O relatório não inclui nome do computador, ")
          .Append("nome de usuário, endereços de rede nem números de série. Cada mudança está no diário do NEXUS (página Histórico).</p></footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public string Save(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"assistente-{At:yyyy-MM-dd-HHmm}.html");
        File.WriteAllText(path, ToHtml(), new UTF8Encoding(false));
        return path;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "relatorios");

    /// <summary>Escapa só o necessário (&amp; &lt; &gt; aspas): o arquivo é UTF-8 e o português fica legível no código-fonte.</summary>
    internal static string E(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ when char.IsControl(ch) && ch is not ('\n' or '\t') => "",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string? value) =>
        sb.Append("<tr><th scope=\"row\">").Append(E(label)).Append("</th><td>").Append(E(value ?? "não lido")).Append("</td></tr>");

    private static string Head(string title) => $$"""
        <!DOCTYPE html>
        <html lang="pt-BR"><head><meta charset="utf-8"/><meta name="viewport" content="width=device-width,initial-scale=1"/>
        <title>{{E(title)}}</title>
        <style>
        :root{--bg:#0E1320;--surface:#161D2E;--line:#2A3550;--text:#E8ECF5;--muted:#8A96B3;--trace:#FFB547;--signal:#4FD1E8;--good:#5EE6A0;--bad:#FF6B7A}
        body{background:var(--bg);color:var(--text);font:15px/1.55 "Segoe UI",system-ui,sans-serif;margin:0 auto;padding:28px 24px;max-width:980px}
        h1{font:600 30px "Bahnschrift","Segoe UI",sans-serif;margin:2px 0 6px}
        h2{font:600 19px "Bahnschrift","Segoe UI",sans-serif;margin:30px 0 10px;color:var(--text)}
        .eyebrow,.label{font-size:12px;letter-spacing:.04em;color:var(--muted);margin:0}
        .muted{color:var(--muted)}.small{font-size:13px}.ok{color:var(--good)}.att{color:var(--trace)}.bad{color:var(--bad)}
        .arrow{color:var(--signal)}
        .card{background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:12px 16px;margin:8px 0}
        .att-card{border-left:3px solid var(--trace)}.next{border-left:3px solid var(--signal)}
        .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:10px}
        .metric .num{font:600 22px "Bahnschrift","Segoe UI",sans-serif;margin-top:2px}
        table{width:100%;border-collapse:collapse;margin:6px 0}
        th,td{text-align:left;vertical-align:top;padding:7px 10px;border-bottom:1px solid var(--line)}
        thead th{font-size:12px;color:var(--muted);font-weight:600}
        tbody th{width:180px;color:var(--muted);font-weight:500}
        a{color:var(--signal)}
        footer{margin-top:34px}
        @media print{
          :root{--bg:#fff;--surface:#fff;--line:#c9ced8;--text:#111;--muted:#555;--trace:#9a5b00;--signal:#00657a;--good:#126b3c;--bad:#a1121f}
          body{padding:0;max-width:none;font-size:12px}.card,tr{break-inside:avoid}a{color:#111}
        }
        </style></head><body>
        """;
}

/// <summary>
/// Tira do relatório o que é pessoal: caminho do perfil (C:\Users\nome → %USERPROFILE%), nome do usuário, nome do computador,
/// e-mails e endereços IP. Aplicado a todo texto que vai para o relatório.
/// </summary>
public sealed partial class ReportRedactor
{
    private readonly string[] _users;
    private readonly string[] _machines;
    private readonly string? _profile;

    public ReportRedactor(string? userName, string? machineName, string? userProfile)
    {
        _users = string.IsNullOrWhiteSpace(userName) || userName.Length < 2 ? [] : [userName];
        _machines = string.IsNullOrWhiteSpace(machineName) || machineName.Length < 2 ? [] : [machineName];
        _profile = string.IsNullOrWhiteSpace(userProfile) ? null : userProfile.TrimEnd('\\', '/');
    }

    public static ReportRedactor None { get; } = new(null, null, null);

    public static ReportRedactor ForCurrentUser() =>
        new(Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\/\s""'<>]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();

    public string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var t = text;
        if (_profile is not null) t = t.Replace(_profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        t = ProfilePath().Replace(t, "%USERPROFILE%");
        t = Email().Replace(t, "<e-mail>");
        t = Ipv4().Replace(t, m => LooksLikeVersion(m.Value) ? m.Value : "<IP>");
        foreach (var m in _machines) t = ReplaceWord(t, m, "<computador>");
        foreach (var u in _users) t = ReplaceWord(t, u, "<usuário>");
        return t;
    }

    /// <summary>Versões de driver (31.0.15.3623) também têm 4 grupos: só é IP se cada parte couber em 0–255 e não for versão longa.</summary>
    private static bool LooksLikeVersion(string s) => s.Split('.').Any(p => p.Length > 3 || int.Parse(p, CultureInfo.InvariantCulture) > 255);

    private static string ReplaceWord(string text, string word, string replacement) =>
        Regex.Replace(text, @"(?<![\p{L}\p{N}_])" + Regex.Escape(word) + @"(?![\p{L}\p{N}_])", replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
