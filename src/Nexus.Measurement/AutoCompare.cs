using System.Globalization;
using System.Text;
using Nexus.Experimentation;

namespace Nexus.Measurement;

/// <summary>Condições da partida que mudam o FPS sozinhas. Null = não lido.</summary>
public sealed record SessionConditions
{
    /// <summary>Área do jogo em pixels ("1920x1080"), da janela em primeiro plano.</summary>
    public string? Resolution { get; init; }
    public string? GpuDriver { get; init; }
    public bool? OnBattery { get; init; }
    /// <summary>Modo de apresentação mais frequente (PresentMon): diz se era tela cheia, sem bordas ou janela.</summary>
    public string? PresentMode { get; init; }
    public string? WindowsBuild { get; init; }
}

/// <summary>Uma mudança do NEXUS, lida do diário (somente leitura).</summary>
/// <param name="Active">Continua aplicada (não foi desfeita depois).</param>
public sealed record AppliedChange(string ActionId, string Title, DateTimeOffset At, bool Active, Guid RunId);

/// <summary>Uma linha do diário reduzida ao que a medição precisa (a página lê o diário e entrega isto).</summary>
public sealed record JournalRow(Guid RunId, string ActionId, string Title, string Target, string State, DateTimeOffset UpdatedAt);

public static class ChangeHistory
{
    private static readonly HashSet<string> AppliedStates = new(StringComparer.OrdinalIgnoreCase) { "Applied", "Verified" };

    /// <summary>
    /// Mudanças a partir das linhas do diário: para cada (ação, alvo), o estado mais recente diz se ela continua ativa.
    /// Entradas só planejadas/falhas ficam de fora. Ordem: da mais recente para a mais antiga.
    /// </summary>
    public static IReadOnlyList<AppliedChange> From(IEnumerable<JournalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = new List<AppliedChange>();
        foreach (var g in rows.GroupBy(r => (r.ActionId, r.Target)))
        {
            var latest = g.MaxBy(r => r.UpdatedAt)!;
            var applied = g.Where(r => AppliedStates.Contains(r.State)).MaxBy(r => r.UpdatedAt);
            if (applied is null && !latest.State.Equals("Reverted", StringComparison.OrdinalIgnoreCase)) continue;
            var active = AppliedStates.Contains(latest.State);
            list.Add(new AppliedChange(latest.ActionId, latest.Title, latest.UpdatedAt, active, latest.RunId));
        }
        return list.OrderByDescending(c => c.At).ToList();
    }

    public static IReadOnlyList<AppliedChange> Between(IReadOnlyList<AppliedChange> changes, DateTimeOffset after, DateTimeOffset upTo) =>
        changes.Where(c => c.At > after && c.At <= upTo).OrderBy(c => c.At).ToList();
}

/// <summary>Qual sessão serve de "antes" para uma nova sessão do mesmo jogo, e por quê.</summary>
public sealed record ReferenceChoice(MeasuredSession? Reference, IReadOnlyList<MeasuredSession> BeforeGroup, IReadOnlyList<MeasuredSession> AfterGroup,
    IReadOnlyList<AppliedChange> ChangesBetween, string Why);

/// <summary>Resultado do antes x depois automático, pronto para a página e para exportar.</summary>
public sealed record AutoComparison(
    MeasuredSession Reference,
    MeasuredSession Current,
    SessionComparisonResult Result,
    string Mode,
    string Verdict,
    string Tone,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AppliedChange> ChangesBetween,
    string Why);

/// <summary>
/// Antes x depois automático. Escolha da referência: sessões do MESMO jogo, válidas (não excluídas, com pelo menos
/// <see cref="MinSeconds"/> s). Se houve mudança do NEXUS antes da sessão atual, a referência é a última partida ANTES da
/// mudança; senão, a partida anterior. Com 3+ partidas de cada lado da mudança, compara partidas (pareado, bootstrap);
/// com menos, compara trechos equivalentes de 30 s das duas partidas (pareado, bootstrap) e diz que isso é indicativo.
/// </summary>
public static class AutoCompare
{
    public const double MinSeconds = 60;
    public const int MaxGroup = 5;
    public const int MaxSegmentPairs = 12;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static bool Usable(MeasuredSession s) => !s.Excluded && s.DurationSeconds >= MinSeconds;

    public static ReferenceChoice ChooseReference(MeasuredSession current, IEnumerable<MeasuredSession> all, IReadOnlyList<AppliedChange> changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(changes);
        var same = all.Where(s => s.Id != current.Id && s.Game == current.Game && Usable(s)).ToList();
        var earlier = same.Where(s => s.CapturedAt < current.CapturedAt).OrderByDescending(s => s.CapturedAt).ToList();
        if (earlier.Count == 0)
            return new(null, [], [], [], $"Primeira partida medida de {current.DisplayName}: ela vira a referência das próximas.");

        // Última mudança do NEXUS que aconteceu depois de alguma partida anterior e antes desta.
        var oldest = earlier[^1].CapturedAt;
        var change = changes.Where(c => c.At < current.CapturedAt && c.At > oldest).MaxBy(c => c.At);
        if (change is null)
        {
            var prev = earlier[0];
            return new(prev, [prev], [current], [], "Partida anterior do mesmo jogo. Nenhuma mudança do NEXUS entre as duas.");
        }

        var before = earlier.Where(s => s.CapturedAt < change.At).ToList();
        // Antes do grupo "antes" pode haver outra mudança: o grupo vai só até ela.
        var previousChange = changes.Where(c => c.At < change.At && before.Count > 0 && c.At > before[^1].CapturedAt).MaxBy(c => c.At);
        if (previousChange is not null) before = before.Where(s => s.CapturedAt > previousChange.At).ToList();
        before = before.Take(MaxGroup).ToList();
        var after = same.Where(s => s.CapturedAt > change.At && s.CapturedAt <= current.CapturedAt).Append(current)
            .OrderByDescending(s => s.CapturedAt).Take(MaxGroup).ToList();
        var reference = before.FirstOrDefault() ?? earlier[0];
        var between = ChangeHistory.Between(changes, reference.CapturedAt, current.CapturedAt);
        var what = between.Count == 1 ? $"\"{between[0].Title}\"" : $"{between.Count} mudanças";
        return new(reference, before, after, between, $"Última partida antes de {what} ({change.At.ToLocalTime().ToString("dd/MM HH:mm", Pt)}).");
    }

    /// <summary>Avisos quando as duas partidas não são comparáveis em pé de igualdade.</summary>
    public static IReadOnlyList<string> Warnings(MeasuredSession reference, MeasuredSession current, IReadOnlyList<AppliedChange> between)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(current);
        var w = new List<string>();
        var a = reference.Conditions;
        var b = current.Conditions;
        if (Differs(a?.Resolution, b?.Resolution))
            w.Add($"Resolução diferente ({a!.Resolution} antes, {b!.Resolution} agora): o jogo desenha outra quantidade de pixels, então a diferença de FPS não vem da mudança.");
        if (Differs(a?.GpuDriver, b?.GpuDriver))
            w.Add($"O driver de vídeo mudou ({a!.GpuDriver} → {b!.GpuDriver}): isso sozinho pode explicar a diferença.");
        if (a?.OnBattery is { } ba && b?.OnBattery is { } bb && ba != bb)
            w.Add(bb ? "Antes na tomada, agora na bateria: na bateria o Windows e o notebook limitam processador e placa de vídeo."
                     : "Antes na bateria, agora na tomada: na tomada o notebook libera mais desempenho, independente da mudança.");
        if (Differs(a?.PresentMode, b?.PresentMode))
            w.Add($"O modo de tela mudou ({ModeText(a!.PresentMode)} → {ModeText(b!.PresentMode)}): tela cheia, sem bordas e janela têm desempenho diferente.");
        if (Differs(a?.WindowsBuild, b?.WindowsBuild))
            w.Add($"O Windows foi atualizado entre as partidas (versão {a!.WindowsBuild} → {b!.WindowsBuild}).");
        var ratio = Math.Max(reference.DurationSeconds, current.DurationSeconds) / Math.Max(1, Math.Min(reference.DurationSeconds, current.DurationSeconds));
        if (ratio >= 3)
            w.Add($"Partidas de duração muito diferente ({Minutes(reference.DurationSeconds)} e {Minutes(current.DurationSeconds)}): provavelmente cenas ou modos diferentes.");
        if (between.Count == 0)
            w.Add("Nenhuma mudança do NEXUS entre as duas partidas: se houver diferença, ela vem do jogo, da cena, de atualizações ou de outro programa.");
        w.Add("O NEXUS não sabe o mapa nem a cena. Para valer, compare partidas parecidas (mesmo mapa ou modo).");
        return w;
    }

    private static bool Differs(string? x, string? y) => !string.IsNullOrWhiteSpace(x) && !string.IsNullOrWhiteSpace(y) && !x.Equals(y, StringComparison.OrdinalIgnoreCase);

    public static string ModeText(string? presentMode) => presentMode switch
    {
        null => "desconhecido",
        _ when presentMode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase) => "tela cheia ou sem bordas otimizada",
        _ when presentMode.Contains("Legacy", StringComparison.OrdinalIgnoreCase) => "tela cheia exclusiva",
        _ when presentMode.Contains("Composed", StringComparison.OrdinalIgnoreCase) => "janela (composta)",
        _ => presentMode,
    };

    private static string Minutes(double s) => s < 90 ? $"{s:0} s" : $"{s / 60:0} min";

    /// <summary>Compara a sessão atual com a referência escolhida. Null quando não há referência.</summary>
    public static AutoComparison? Compare(MeasuredSession current, IEnumerable<MeasuredSession> all, IReadOnlyList<AppliedChange> changes)
    {
        var choice = ChooseReference(current, all, changes);
        if (choice.Reference is not { } reference) return null;
        SessionComparisonResult result;
        string mode;
        var pairs = Math.Min(choice.BeforeGroup.Count, choice.AfterGroup.Count);
        if (pairs >= 3)
        {
            result = SessionComparison.Compare(choice.BeforeGroup.Take(pairs).ToList(), choice.AfterGroup.Take(pairs).ToList());
            mode = "partidas";
        }
        else
        {
            result = CompareSegments(reference, current);
            mode = "trechos";
        }
        var (verdict, tone) = Verdict(result);
        return new AutoComparison(reference, current, result, mode, verdict, tone, Warnings(reference, current, choice.ChangesBetween), choice.ChangesBetween, choice.Why);
    }

    /// <summary>
    /// Trechos equivalentes: descarta o primeiro trecho (carregamento/aquecimento) quando há trechos sobrando, escolhe o mesmo
    /// número de trechos de cada partida espaçados igualmente (começo com começo, meio com meio, fim com fim) e faz a
    /// comparação pareada com IC bootstrap (<see cref="RunComparison.ComparePaired"/>). Menos de 3 pares: só descritivo.
    /// </summary>
    public static SessionComparisonResult CompareSegments(MeasuredSession before, MeasuredSession after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var b = Trim(before.Segments);
        var a = Trim(after.Segments);
        var n = Math.Min(Math.Min(b.Count, a.Count), MaxSegmentPairs);
        var rows = new List<MetricComparison>();
        if (n < 3)
        {
            foreach (var m in SessionComparison.Metrics)
            {
                var bv = m.Read(before.Summary);
                var av = m.Read(after.Summary);
                var delta = bv != 0 ? 100.0 * (av - bv) / bv : double.NaN;
                rows.Add(new(m, bv, av, delta, null, Nexus.Experimentation.Verdict.InsufficientData, null, null,
                    $"{m.Name}: {Pct(delta)} (partida curta demais para separar mudança de variação)."));
            }
            return new(1, 1, false, "Partidas curtas demais para um veredito: jogue pelo menos 2 minutos.",
                "Cada partida precisa de pelo menos 3 trechos de 30 s (além do primeiro, de carregamento).", rows);
        }
        var bs = Spread(b, n);
        var asel = Spread(a, n);
        foreach (var m in SessionComparison.Metrics)
        {
            var bv = bs.Select(s => Read(m.Key, s)).ToList();
            var av = asel.Select(s => Read(m.Key, s)).ToList();
            var bMean = bv.Average();
            var aMean = av.Average();
            var delta = bMean != 0 ? 100.0 * (aMean - bMean) / bMean : double.NaN;
            var r = RunComparison.ComparePaired(m.Name, bv, av, m.Direction, m.MinimumRelevant(bMean));
            double? lo = bMean != 0 ? 100.0 * r.CiLow / bMean : null;
            double? hi = bMean != 0 ? 100.0 * r.CiHigh / bMean : null;
            bool? better = r.Verdict switch { Nexus.Experimentation.Verdict.Improved => true, Nexus.Experimentation.Verdict.Regressed => false, _ => null };
            rows.Add(new(m, bMean, aMean, delta, better, r.Verdict, lo, hi, Sentence(m, r.Verdict, delta, lo, hi)));
        }
        var method = $"{n} trechos de 30 s de cada partida, pareados pela posição na partida (começo com começo, fim com fim); o primeiro trecho " +
                     "(carregamento) fica de fora. Diferença pareada por trecho; intervalo de 95% por bootstrap. Trechos da mesma partida não são " +
                     "totalmente independentes, então o resultado é indicativo: 3 partidas de cada lado dão um veredito mais firme.";
        return new(n, n, true, "", method, rows);
    }

    private static IReadOnlyList<SegmentSummary> Trim(IReadOnlyList<SegmentSummary> s) => s.Count >= 4 ? s.Skip(1).ToList() : s;

    private static List<SegmentSummary> Spread(IReadOnlyList<SegmentSummary> s, int n) =>
        Enumerable.Range(0, n).Select(i => s[(int)Math.Floor((i + 0.5) * s.Count / n)]).ToList();

    private static double Read(string key, SegmentSummary s) => key switch
    {
        "fps" => s.AverageFps,
        "low1" => s.Low1Fps,
        "p99" => s.P99Ms,
        "stutter" => s.StuttersPerMinute,
        _ => double.NaN,
    };

    private static string Pct(double v) => double.IsFinite(v) ? v.ToString("+0.0;-0.0;0.0", Pt) + "%" : "—";

    /// <summary>"± x%" = metade da largura do intervalo de 95%.</summary>
    public static string PlusMinus(double? lo, double? hi) => lo is { } l && hi is { } h && double.IsFinite(l) && double.IsFinite(h) ? " ± " + (Math.Abs(h - l) / 2).ToString("0.0", Pt) + "%" : "";

    private static string Sentence(MetricDefinition m, Nexus.Experimentation.Verdict v, double delta, double? lo, double? hi) => v switch
    {
        Nexus.Experimentation.Verdict.Improved => $"{m.Name} melhorou {Abs(delta)}{PlusMinus(lo, hi)}.",
        Nexus.Experimentation.Verdict.Regressed => $"{m.Name} piorou {Abs(delta)}{PlusMinus(lo, hi)}.",
        Nexus.Experimentation.Verdict.NoMeaningfulChange => $"{m.Name}: sem diferença mensurável ({Pct(delta)}{PlusMinus(lo, hi)}).",
        Nexus.Experimentation.Verdict.Inconclusive => $"{m.Name}: {Pct(delta)}{PlusMinus(lo, hi)}, dentro da variação normal.",
        _ => $"{m.Name}: {Pct(delta)}.",
    };

    private static string Abs(double v) => double.IsFinite(v) ? Math.Abs(v).ToString("0.0", Pt) + "%" : "—";

    /// <summary>Frase curta do veredito e tom ("Good", "Bad", "Neutral").</summary>
    public static (string Verdict, string Tone) Verdict(SessionComparisonResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!r.Paired || r.Metrics.Count == 0) return (r.Headline.Length > 0 ? r.Headline : "Ainda sem veredito.", "Neutral");
        var improved = r.Metrics.Where(m => m.Verdict == Nexus.Experimentation.Verdict.Improved).ToList();
        var regressed = r.Metrics.Where(m => m.Verdict == Nexus.Experimentation.Verdict.Regressed).ToList();
        string Items(IEnumerable<MetricComparison> ms) => string.Join(" e ", ms.Select(m => $"{m.Metric.Name} em {Abs(m.DeltaPercent)}{PlusMinus(m.CiLowPercent, m.CiHighPercent)}"));
        if (improved.Count > 0 && regressed.Count == 0) return ($"Melhorou {Items(improved)}.", "Good");
        if (regressed.Count > 0 && improved.Count == 0) return ($"Piorou {Items(regressed)}. Considere desfazer a mudança.", "Bad");
        if (improved.Count > 0) return ($"Misto: melhorou {Items(improved)}; piorou {Items(regressed)}.", "Neutral");
        return ("Sem diferença mensurável: as diferenças cabem na variação normal entre partidas.", "Neutral");
    }
}

/// <summary>Um jogo na lista da página, com a tendência das sessões.</summary>
public sealed record GameHistory(string Key, string Name, int Sessions, DateTimeOffset LastPlayed, IReadOnlyList<double> AverageFpsTrend,
    IReadOnlyList<double> Low1Trend, double LastAverageFps, double LastLow1Fps, double? Low1ChangePercent);

public static class SessionLibrary
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Agrupa por jogo (mais jogado recentemente primeiro); tendência = até 12 sessões, da mais antiga para a mais nova.</summary>
    public static IReadOnlyList<GameHistory> Group(IEnumerable<MeasuredSession> sessions, int trend = 12)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions.GroupBy(s => s.Game).Select(g =>
        {
            var ordered = g.OrderBy(s => s.CapturedAt).ToList();
            var last = ordered[^1];
            var window = ordered.Where(AutoCompare.Usable).TakeLast(trend).ToList();
            if (window.Count == 0) window = ordered.TakeLast(trend).ToList();
            double? change = window.Count >= 2 && window[0].Summary.OnePercentLowAverageFps > 0
                ? 100.0 * (window[^1].Summary.OnePercentLowAverageFps - window[0].Summary.OnePercentLowAverageFps) / window[0].Summary.OnePercentLowAverageFps
                : null;
            return new GameHistory(g.Key, last.DisplayName, ordered.Count, last.CapturedAt,
                window.Select(s => s.Summary.AverageFps).ToList(), window.Select(s => s.Summary.OnePercentLowAverageFps).ToList(),
                last.Summary.AverageFps, last.Summary.OnePercentLowAverageFps, change);
        }).OrderByDescending(h => h.LastPlayed).ToList();
    }

    /// <summary>CSV de resumo (uma linha por sessão), separador ";" e vírgula decimal para abrir direto no Excel em pt-BR.</summary>
    public static string SummaryCsv(IEnumerable<MeasuredSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var sb = new StringBuilder();
        sb.AppendLine("jogo;executavel;data;duracao_s;quadros;fps_medio;low_1pct;p99_ms;engasgos_min;origem;automatica;excluida;resolucao;driver;bateria;modo_tela;mudancas_ativas");
        foreach (var s in sessions.OrderBy(s => s.Game).ThenBy(s => s.CapturedAt))
        {
            var c = s.Conditions;
            string F(double v, string f) => double.IsFinite(v) ? v.ToString(f, Pt) : "";
            sb.AppendJoin(';',
                Field(s.DisplayName), Field(s.Application), s.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                F(s.DurationSeconds, "0"), s.Summary.Frames.ToString(CultureInfo.InvariantCulture), F(s.Summary.AverageFps, "0.0"),
                F(s.Summary.OnePercentLowAverageFps, "0.0"), F(s.Summary.P99FrametimeMs, "0.00"), F(s.Summary.StuttersPerMinute, "0.0"),
                Field(s.Source), s.Automatic ? "sim" : "não", s.Excluded ? "sim" : "não", Field(c?.Resolution), Field(c?.GpuDriver),
                c?.OnBattery switch { true => "sim", false => "não", _ => "" }, Field(c?.PresentMode),
                Field(string.Join(" | ", s.Applied.Where(a => a.Active).Select(a => a.Title))));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Relatório em texto de uma comparação (para colar num fórum ou guardar).</summary>
    public static string Report(AutoComparison c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"NEXUS · Antes x depois · {c.Current.DisplayName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Antes: {c.Reference.CapturedAt.ToLocalTime():dd/MM/yyyy HH:mm} · Depois: {c.Current.CapturedAt.ToLocalTime():dd/MM/yyyy HH:mm}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Veredito: {c.Verdict}");
        sb.AppendLine();
        foreach (var m in c.Result.Metrics) sb.AppendLine(CultureInfo.InvariantCulture, $"- {m.Sentence}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Como foi calculado: {c.Result.Method}");
        if (c.ChangesBetween.Count > 0)
        {
            sb.AppendLine("Mudanças do NEXUS entre as partidas:");
            foreach (var ch in c.ChangesBetween) sb.AppendLine(CultureInfo.InvariantCulture, $"- {ch.Title} ({ch.At.ToLocalTime():dd/MM HH:mm}){(ch.Active ? "" : " · desfeita depois")}");
        }
        if (c.Warnings.Count > 0)
        {
            sb.AppendLine("Atenção:");
            foreach (var w in c.Warnings) sb.AppendLine(CultureInfo.InvariantCulture, $"- {w}");
        }
        return sb.ToString();
    }

    private static string Field(string? s) => string.IsNullOrEmpty(s) ? "" : s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
}
