using System.Globalization;
using Nexus.Broker;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.Measurement.AbLab;
using V = Nexus.Experimentation.Verdict;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

// Laboratório A/B — lógica pura da tela (sem WPF, testada em AbLabPageTests): montar candidatos a partir das opções da tela
// Jogos e dos ajustes rápidos do catálogo, converter de volta em pedidos ao aplicador, e as frases em linguagem simples
// (passo atual, veredito com números, motivos de recusa de uma partida, histórico).

/// <summary>Candidatos do laboratório: de uma opção de cartão de jogo (GameOptionPlan) ou de um ajuste do catálogo.</summary>
public static class AbLabCandidates
{
    /// <summary>Uma opção da tela Jogos vira um candidato com as mesmas ações, valores, argumentos e nível.</summary>
    public static AbCandidate FromPlan(string title, GameOptionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new AbCandidate(plan.Id, title,
            [.. plan.Requests.Select(r => new AbActionRef(r.Request.ActionId, r.Request.Value, r.Request.Arguments, r.Level == BrokerLevel.Admin))]);
    }

    /// <summary>Um ajuste do catálogo (uma ação, valor já resolvido pela interface, sem argumentos do usuário).</summary>
    public static AbCandidate FromCatalog(string key, string title, ActionRequest request, BrokerLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(request);
        return new AbCandidate("catalog|" + key, title, [new AbActionRef(request.ActionId, request.Value, request.Arguments, level == BrokerLevel.Admin)]);
    }

    /// <summary>Os pedidos ao aplicador de um candidato (mesmos campos; o nível vem de cada ação).</summary>
    public static IReadOnlyList<(ActionRequest Request, BrokerLevel Level)> Requests(AbCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return [.. candidate.Actions.Select(a => (new ActionRequest(a.ActionId, a.Value, a.Arguments), a.Admin ? BrokerLevel.Admin : BrokerLevel.User))];
    }
}

/// <summary>Frases da tela em linguagem simples (chaves em Strings.resx; números no formato do idioma).</summary>
public static class AbLabText
{
    private static CultureInfo Pt => Loc.Culture;

    /// <summary>Minutos mínimos de uma partida para o teste (regra do laboratório, arredondada para cima).</summary>
    public static int MinMinutes(AbRules? rules = null) => (int)Math.Ceiling((rules ?? AbRules.Default).MinSeconds / 60.0);

    /// <summary>Título do passo atual ("Partida 1 de 2: jogue sem o ajuste").</summary>
    public static string StepTitle(AbTestState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var n = AbLabMachine.PlayIndex(s);
        var total = AbLabMachine.PlaysNeeded(s.Design);
        return s.Step switch
        {
            AbStep.WaitBaseline => Loc.F("Ab.Step.PlayWithout", n, total),
            AbStep.WaitTreatment => Loc.F("Ab.Step.PlayWith", n, total),
            AbStep.WaitBaseline2 => Loc.F("Ab.Step.PlayWithoutAgain", n, total),
            AbStep.Applying or AbStep.Reapplying => Loc.T("Ab.Step.Applying"),
            AbStep.Reverting or AbStep.Undoing => Loc.T("Ab.Step.Reverting"),
            AbStep.Deciding => Loc.T("Ab.Step.Deciding"),
            AbStep.AskUser => Loc.T("Ab.Step.Ask"),
            _ => Loc.T("Ab.Step.Done"),
        };
    }

    /// <summary>Frase de apoio do passo ("Jogue pelo menos 3 minutos; a partida é medida sozinha e entra no teste quando acabar.").</summary>
    public static string StepDetail(AbTestState s, AbRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (AbLabMachine.IsWaitingForPlay(s)) return Loc.F("Ab.Step.PlayDetail", MinMinutes(rules));
        return s.Step switch
        {
            AbStep.Applying or AbStep.Reapplying or AbStep.Reverting or AbStep.Undoing => Loc.T("Ab.Step.WorkingDetail"),
            AbStep.Deciding => Loc.T("Ab.Step.DecidingDetail"),
            AbStep.AskUser => Loc.T("Ab.Step.AskDetail"),
            _ => "",
        };
    }

    /// <summary>Por que a partida medida não entrou no teste (uma frase; vazio quando era de outro jogo).</summary>
    public static string Rejection(IReadOnlyList<AbRejection> reasons, AbRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        rules ??= AbRules.Default;
        if (reasons.Count == 0 || reasons.Contains(AbRejection.OtherGame)) return "";
        var first = reasons.Contains(AbRejection.TooShort) ? AbRejection.TooShort : reasons[0];
        return first switch
        {
            AbRejection.NotWaiting => Loc.T("Ab.Reject.NotWaiting"),
            AbRejection.StartedBeforeStep => Loc.T("Ab.Reject.StartedBefore"),
            AbRejection.Excluded => Loc.T("Ab.Reject.Excluded"),
            AbRejection.TooShort => Loc.F("Ab.Reject.TooShort", MinMinutes(rules)),
            AbRejection.TooFewFrames => Loc.T("Ab.Reject.TooFewFrames"),
            AbRejection.ResolutionChanged => Loc.T("Ab.Reject.Resolution"),
            AbRejection.BatteryChanged => Loc.T("Ab.Reject.Battery"),
            AbRejection.DriverChanged => Loc.T("Ab.Reject.Driver"),
            AbRejection.Duplicate => "",
            _ => "",
        };
    }

    /// <summary>Nome da métrica para pessoas.</summary>
    public static string MetricName(string key) => key switch
    {
        "fps" => Loc.T("Ab.Metric.Fps"),
        "low1" => Loc.T("Ab.Metric.Low1"),
        "p99" => Loc.T("Ab.Metric.P99"),
        "stutter" => Loc.T("Ab.Metric.Stutter"),
        AbLabJudge.VarianceKey => Loc.T("Ab.Metric.Variance"),
        _ => key,
    };

    public static bool HigherIsBetter(string key) => key is "fps" or "low1";

    /// <summary>Valor com unidade ("118 FPS", "9,8 ms", "1,2/min").</summary>
    public static string Value(string key, double v)
    {
        if (!double.IsFinite(v)) return "—";
        return key switch
        {
            "fps" or "low1" => Loc.F("Ab.Unit.Fps", v.ToString("0", Pt)),
            "p99" or AbLabJudge.VarianceKey => Loc.F("Ab.Unit.Ms", v.ToString("0.0", Pt)),
            "stutter" => Loc.F("Ab.Unit.PerMinute", v.ToString("0.0", Pt)),
            _ => v.ToString("0.0", Pt),
        };
    }

    /// <summary>"+18% ± 3%" (o ± quando houver intervalo de confiança).</summary>
    public static string Delta(AbMetric m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (!double.IsFinite(m.DeltaPercent)) return "—";
        var d = m.DeltaPercent.ToString("+0;-0;0", Pt) + "%";
        if (m.CiLowPercent is { } lo && m.CiHighPercent is { } hi && double.IsFinite(lo) && double.IsFinite(hi))
            d += " ± " + (Math.Abs(hi - lo) / 2).ToString("0", Pt) + "%";
        return d;
    }

    /// <summary>Selo da métrica: "Melhorou"/"Piorou" só com prova; o resto neutro.</summary>
    public static (string Text, Tone Tone) MetricVerdict(AbMetric m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.Noisy) return (Loc.T("Ab.Verdict.Noisy"), Tone.Warn);
        if (!m.Decisive && m.Verdict is V.Improved or V.Regressed)
            return (m.Verdict == V.Improved ? Loc.T("Ab.Verdict.LooksBetter") : Loc.T("Ab.Verdict.LooksWorse"), Tone.Info);
        return m.Verdict switch
        {
            V.Improved => (Loc.T("Ab.Verdict.Improved"), Tone.Good),
            V.Regressed => (Loc.T("Ab.Verdict.Regressed"), Tone.Bad),
            V.NoMeaningfulChange => (Loc.T("Ab.Verdict.Same"), Tone.Neutral),
            V.Inconclusive => (Loc.T("Ab.Verdict.WithinNoise"), Tone.Neutral),
            _ => (Loc.T("Ab.Verdict.NotEnough"), Tone.Neutral),
        };
    }

    /// <summary>Frase curta do destaque ("+18% no 1% low"), ou vazio.</summary>
    public static string Highlight(AbJudgement j)
    {
        ArgumentNullException.ThrowIfNull(j);
        if (j.HighlightKey is null || j.HighlightPercent is not { } p || !double.IsFinite(p)) return "";
        return Loc.F("Ab.Highlight", p.ToString("+0;-0;0", Pt) + "%", MetricName(j.HighlightKey));
    }

    /// <summary>O veredito em uma linha: "Ajudou", "Não ajudou", "Inconclusivo" com o motivo, e o tom.</summary>
    public static (string Title, string Detail, Tone Tone) Verdict(AbJudgement j)
    {
        ArgumentNullException.ThrowIfNull(j);
        var h = Highlight(j);
        return j.Reason switch
        {
            AbReason.Improved => (Loc.T("Ab.Result.Helped"), h.Length > 0 ? Loc.F("Ab.Result.HelpedDetail", h) : Loc.T("Ab.Result.HelpedPlain"), Tone.Good),
            AbReason.Regressed => (Loc.T("Ab.Result.Hurt"), h.Length > 0 ? Loc.F("Ab.Result.HurtDetail", h) : Loc.T("Ab.Result.HurtPlain"), Tone.Bad),
            AbReason.Mixed => (Loc.T("Ab.Result.Mixed"), Loc.T("Ab.Result.MixedDetail"), Tone.Warn),
            AbReason.NoChange => (Loc.T("Ab.Result.NoChange"), Loc.T("Ab.Result.NoChangeDetail"), Tone.Neutral),
            AbReason.Noisy => (Loc.T("Ab.Result.Inconclusive"), Loc.T("Ab.Result.NoisyDetail"), Tone.Warn),
            AbReason.Insufficient => (Loc.T("Ab.Result.Inconclusive"), Loc.T("Ab.Result.InsufficientDetail"), Tone.Warn),
            _ => (Loc.T("Ab.Result.Inconclusive"), Loc.T("Ab.Result.InconclusiveDetail"), Tone.Neutral),
        };
    }

    /// <summary>Avisos de comparabilidade, em frases.</summary>
    public static IReadOnlyList<string> Warnings(AbJudgement j)
    {
        ArgumentNullException.ThrowIfNull(j);
        var list = new List<string>();
        foreach (var w in j.Warnings)
        {
            switch (w)
            {
                case AbWarning.DurationMismatch: list.Add(Loc.T("Ab.Warn.Duration")); break;
                case AbWarning.PresentModeChanged: list.Add(Loc.F("Ab.Warn.PresentMode", j.PresentModeBefore ?? "?", j.PresentModeAfter ?? "?")); break;
                case AbWarning.BaselineDrift: list.Add(Loc.T("Ab.Warn.Drift")); break;
                case AbWarning.SameScene: list.Add(Loc.T("Ab.Warn.SameScene")); break;
            }
        }
        return list;
    }

    /// <summary>Como o teste terminou ("mantido", "desfeito", "cancelado"…), para o resumo e o histórico.</summary>
    public static (string Text, Tone Tone) Outcome(AbOutcome outcome, AbFailure failure) => outcome switch
    {
        AbOutcome.Kept => (Loc.T("Ab.Outcome.Kept"), Tone.Good),
        AbOutcome.KeptByUser => (Loc.T("Ab.Outcome.KeptByUser"), Tone.Info),
        AbOutcome.Undone => (Loc.T("Ab.Outcome.Undone"), Tone.Neutral),
        AbOutcome.AlreadyOn => (Loc.T("Ab.Outcome.AlreadyOn"), Tone.Warn),
        AbOutcome.Cancelled => (Loc.T("Ab.Outcome.Cancelled"), Tone.Neutral),
        AbOutcome.Failed => (failure switch
        {
            AbFailure.ApplyFailed => Loc.T("Ab.Outcome.ApplyFailed"),
            AbFailure.RevertFailed => Loc.T("Ab.Outcome.RevertFailed"),
            AbFailure.ReapplyFailed => Loc.T("Ab.Outcome.ReapplyFailed"),
            AbFailure.Interrupted => Loc.T("Ab.Outcome.Interrupted"),
            _ => Loc.T("Ab.Outcome.Failed"),
        }, Tone.Bad),
        _ => ("", Tone.Neutral),
    };

    /// <summary>Linha do histórico: "03/11 20:15 — Fortnite: Modo desempenho — Ajudou (+18% no 1% low) — mantido".</summary>
    public static string HistoryLine(AbResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var when = r.At.ToLocalTime().ToString("dd/MM HH:mm", Pt);
        var verdict = r.Judgement is { } j ? Verdict(j).Title + (Highlight(j) is { Length: > 0 } h ? $" ({h})" : "") : Outcome(r.Outcome, r.Failure).Text;
        return Loc.F("Ab.History.Line", when, r.GameName, r.CandidateTitle, verdict);
    }

    /// <summary>Frase de confirmação do começo do teste (consentimento: aplicar entre as partidas e desfazer se não ajudar).</summary>
    public static string StartConfirmation(string gameName, IReadOnlyList<AbCandidate> candidates, AbDesign design, bool autoUndo)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var what = candidates.Count == 1 ? $"\"{candidates[0].Title}\"" : Loc.F("Ab.Confirm.NTweaks", candidates.Count);
        var plays = AbLabMachine.PlaysNeeded(design);
        var tail = autoUndo ? Loc.T("Ab.Confirm.AutoUndo") : Loc.T("Ab.Confirm.AskAtEnd");
        return Loc.F("Ab.Confirm.Start", what, gameName, plays, tail);
    }
}
