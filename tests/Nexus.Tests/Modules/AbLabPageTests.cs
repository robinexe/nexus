using Nexus.Broker;
using Nexus.Desktop.ViewModels;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.Experimentation;
using Nexus.Measurement;
using Nexus.Measurement.AbLab;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Tests.Modules;

/// <summary>
/// Tela do Laboratório A/B — a lógica que dá para isolar sem WPF (AbLabText.cs): candidatos a partir das opções da tela Jogos
/// e do catálogo, pedidos ao aplicador de volta, e as frases em linguagem simples (passo, veredito com números, recusas,
/// histórico, confirmação) nos três idiomas.
/// </summary>
[Trait("Category", "Unit")]
[Collection(LocalizationState.Name)]
public sealed class AbLabPageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    public AbLabPageTests() => Loc.Use("pt-BR");

    private static MeasuredSession Session(DateTimeOffset at, double fps, double seconds = 240, int seed = 1, double spikeRate = 0.005, double spikeMs = 15)
    {
        var rng = new Random(seed);
        var ft = new List<double>();
        double total = 0;
        while (total < seconds * 1000)
        {
            var f = 1000.0 / fps * (0.9 + rng.NextDouble() * 0.2) + (rng.NextDouble() < spikeRate ? spikeMs : 0);
            ft.Add(f);
            total += f;
        }
        return new MeasuredSession(Guid.NewGuid().ToString("N"), "x", "FortniteClient-Win64-Shipping.exe", at, "automático", SessionTag.None, "x.csv",
            FrametimeMetrics.Summarize(ft), [], [], 0, "v2") { Segments = SessionAnalyzer.Segment(ft) };
    }

    // ------------------------------------------------------------------ candidatos

    [Fact]
    public void Game_option_plan_becomes_a_candidate_with_the_same_actions_values_arguments_and_level()
    {
        var plan = GameOptionPlans.NvUncap("FortniteLauncher.exe");
        var c = AbLabCandidates.FromPlan("FPS sem limite", plan);
        Assert.Equal(plan.Id, c.Id);
        Assert.Equal("FPS sem limite", c.Title);
        Assert.Equal(plan.Requests.Count, c.Actions.Count);
        Assert.All(c.Actions, a => { Assert.True(a.Admin); Assert.Null(a.Value); Assert.NotNull(a.Arguments); Assert.True(a.Arguments!.ContainsKey("exe")); });
        // E volta exatamente como pedido ao aplicador.
        var back = AbLabCandidates.Requests(c);
        Assert.Equal(plan.Requests.Select(r => (r.Request.ActionId, r.Request.Value, r.Level)), back.Select(r => (r.Request.ActionId, r.Request.Value, r.Level)));
        Assert.Equal(plan.Requests[0].Request.Arguments!["exe"], back[0].Request.Arguments!["exe"]);
    }

    [Fact]
    public void Catalog_tweak_becomes_a_single_action_candidate_with_a_stable_id()
    {
        var c = AbLabCandidates.FromCatalog("win.gamemode@1", "Modo de jogo", new ActionRequest("win.gamemode@1", null), BrokerLevel.User);
        Assert.Equal("catalog|win.gamemode@1", c.Id);
        var only = Assert.Single(c.Actions);
        Assert.Equal("win.gamemode@1", only.ActionId);
        Assert.False(only.Admin);
        Assert.Equal(BrokerLevel.User, AbLabCandidates.Requests(c)[0].Level);
    }

    [Fact]
    public void Candidates_survive_the_json_store_round_trip_with_arguments()
    {
        var dir = Directory.CreateTempSubdirectory("nexus-ablab-page-").FullName;
        try
        {
            var store = new AbLabStore(Path.Combine(dir, "ab-lab.json"));
            var c = AbLabCandidates.FromPlan("Baixa latência", GameOptionPlans.NvLowLatency("cs2.exe"));
            var data = AbLabFlow.Begin(AbLabData.Empty, "cs2.exe", "Counter-Strike 2", [c], AbDesign.AB, true, T0, () => "t1");
            store.Save(data);
            var back = store.Load();
            Assert.NotNull(back.Active);
            var req = AbLabCandidates.Requests(back.Active!.Candidate);
            Assert.Equal("cs2.exe", req[0].Request.Arguments!["exe"]);
            Assert.Equal(BrokerLevel.Admin, req[0].Level);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ passo atual

    [Fact]
    public void Step_titles_count_the_rounds_and_follow_the_machine()
    {
        var c = AbLabCandidates.FromCatalog("k@1", "Ajuste", new ActionRequest("k@1"), BrokerLevel.User);
        var s = AbLabMachine.Start("t", "FortniteClient-Win64-Shipping.exe", "Fortnite", c, AbDesign.ABA, true, T0);
        Assert.Equal("Partida 1 de 3: jogue SEM o ajuste", AbLabText.StepTitle(s));
        Assert.Contains("3 minutos", AbLabText.StepDetail(s));
        var (applying, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(1), 100, seed: 1), T0.AddMinutes(6));
        Assert.Equal("Aplicando o ajuste…", AbLabText.StepTitle(applying));
        var waiting = AbLabMachine.Applied(applying, true, false, [new AbRunRef(Guid.NewGuid(), false)], T0.AddMinutes(7));
        Assert.Equal("Partida 2 de 3: jogue COM o ajuste", AbLabText.StepTitle(waiting));
        var (reverting, _) = AbLabMachine.Offer(waiting, Session(T0.AddMinutes(8), 100, seed: 2), T0.AddMinutes(13));
        Assert.Equal("Voltando ao estado inicial…", AbLabText.StepTitle(reverting));
        var wait2 = AbLabMachine.Reverted(reverting, true, T0.AddMinutes(14));
        Assert.Equal("Partida 3 de 3: jogue SEM o ajuste de novo", AbLabText.StepTitle(wait2));
        Loc.Use("en");
        Assert.Equal("Round 3 of 3: play WITHOUT the tweak again", AbLabText.StepTitle(wait2));
        Loc.Use("es");
        Assert.Equal("Partida 3 de 3: juega SIN el ajuste otra vez", AbLabText.StepTitle(wait2));
    }

    [Fact]
    public void Rejections_become_one_sentence_and_other_game_is_silent()
    {
        Assert.Equal("", AbLabText.Rejection([AbRejection.OtherGame]));
        Assert.Equal("", AbLabText.Rejection([]));
        Assert.Contains("3 minutos", AbLabText.Rejection([AbRejection.TooFewFrames, AbRejection.TooShort])); // curta demais vence
        Assert.Contains("resolução", AbLabText.Rejection([AbRejection.ResolutionChanged]), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("antes deste passo", AbLabText.Rejection([AbRejection.StartedBeforeStep]));
        Assert.Equal("", AbLabText.Rejection([AbRejection.Duplicate]));
    }

    // ------------------------------------------------------------------ veredito em palavras simples

    [Fact]
    public void Improvement_reads_as_helped_with_the_highlight_number_and_kept()
    {
        var a = Session(T0, 100, seed: 1, spikeRate: 0.02, spikeMs: 25);
        var b = Session(T0.AddMinutes(10), 118, seed: 2, spikeRate: 0.004, spikeMs: 10);
        var j = AbLabJudge.Judge(a, b);
        Assert.True(j.Keep);
        var (title, detail, tone) = AbLabText.Verdict(j);
        Assert.Equal("Ajudou", title);
        Assert.Equal(Tone.Good, tone);
        var h = AbLabText.Highlight(j);
        Assert.StartsWith("+", h);
        Assert.Contains(h, detail);
        Assert.Contains("Nenhuma métrica piorou", detail);
        // Métricas: FPS médio com unidade, selo "Melhorou" verde, diferença com ±.
        var fps = j.Metrics.Single(m => m.Key == "fps");
        Assert.Equal("FPS médio", AbLabText.MetricName("fps"));
        Assert.EndsWith(" FPS", AbLabText.Value("fps", fps.Before));
        Assert.Equal(("Melhorou", Tone.Good), AbLabText.MetricVerdict(fps));
        Assert.Matches(@"^\+\d+% ± \d+%$", AbLabText.Delta(fps));
        Assert.True(AbLabText.HigherIsBetter("fps"));
        Assert.False(AbLabText.HigherIsBetter("p99"));
        // Regularidade não decide e só "parece".
        var v = j.Metrics.Single(m => m.Key == AbLabJudge.VarianceKey);
        if (v.Verdict is Verdict.Improved or Verdict.Regressed) Assert.Equal(Tone.Info, AbLabText.MetricVerdict(v).Tone);
        Assert.Contains(AbLabText.Warnings(j), w => w.Contains("cena", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("mantido", Tone.Good), AbLabText.Outcome(AbOutcome.Kept, AbFailure.None));
    }

    [Fact]
    public void No_change_reads_as_did_not_help_and_never_as_helped()
    {
        var j = AbLabJudge.Judge(Session(T0, 100, seed: 3), Session(T0.AddMinutes(10), 100, seed: 4));
        Assert.False(j.Keep);
        var (title, _, tone) = AbLabText.Verdict(j);
        Assert.DoesNotContain("Ajudou", title);
        Assert.NotEqual(Tone.Good, tone);
        Assert.Equal("", AbLabText.Highlight(j));
        Loc.Use("en");
        Assert.DoesNotContain("helped", AbLabText.Verdict(j).Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Regression_reads_as_hurt_in_red()
    {
        var a = Session(T0, 120, seed: 5, spikeRate: 0.003, spikeMs: 10);
        var b = Session(T0.AddMinutes(10), 95, seed: 6, spikeRate: 0.03, spikeMs: 30);
        var j = AbLabJudge.Judge(a, b);
        Assert.Equal(AbReason.Regressed, j.Reason);
        var (title, detail, tone) = AbLabText.Verdict(j);
        Assert.Equal("Não ajudou: piorou", title);
        Assert.Equal(Tone.Bad, tone);
        Assert.Contains("%", detail); // a pior piora com o número ("+653% no Engasgos" ou "-20% no FPS médio")
        Assert.Equal(("Piorou", Tone.Bad), AbLabText.MetricVerdict(j.Metrics.Single(m => m.Key == j.HighlightKey)));
    }

    [Fact]
    public void Outcomes_and_failures_have_their_own_words_in_every_language()
    {
        foreach (var lang in new[] { "pt-BR", "en", "es" })
        {
            Loc.Use(lang);
            var texts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var o in new[] { AbOutcome.Kept, AbOutcome.KeptByUser, AbOutcome.Undone, AbOutcome.AlreadyOn, AbOutcome.Cancelled })
                Assert.True(texts.Add(AbLabText.Outcome(o, AbFailure.None).Text), $"{lang}: {o} repetido");
            foreach (var f in new[] { AbFailure.ApplyFailed, AbFailure.RevertFailed, AbFailure.ReapplyFailed, AbFailure.Interrupted, AbFailure.None })
            {
                var (text, tone) = AbLabText.Outcome(AbOutcome.Failed, f);
                Assert.True(texts.Add(text), $"{lang}: {f} repetido");
                Assert.Equal(Tone.Bad, tone);
            }
            Assert.Equal(("", Tone.Neutral), AbLabText.Outcome(AbOutcome.None, AbFailure.None));
        }
    }

    [Fact]
    public void History_line_has_date_game_tweak_and_verdict()
    {
        var c = AbLabCandidates.FromCatalog("k@1", "Modo desempenho", new ActionRequest("k@1"), BrokerLevel.User);
        var s = AbLabMachine.Start("t", "FortniteClient-Win64-Shipping.exe", "Fortnite", c, AbDesign.AB, true, T0);
        var j = AbLabJudge.Judge(Session(T0, 100, seed: 1, spikeRate: 0.02, spikeMs: 25), Session(T0.AddMinutes(10), 118, seed: 2, spikeRate: 0.004, spikeMs: 10));
        var done = s with { Step = AbStep.Done, Outcome = AbOutcome.Kept, Judgement = j };
        var line = AbLabText.HistoryLine(AbResult.From(done, T0.AddMinutes(20)));
        Assert.Contains("Fortnite", line);
        Assert.Contains("Modo desempenho", line);
        Assert.Contains("Ajudou", line);
        Assert.Contains("+", line);
        // Cancelado sem veredito: só o desfecho.
        var cancelled = AbLabText.HistoryLine(AbResult.From(s with { Step = AbStep.Done, Outcome = AbOutcome.Cancelled }, T0));
        Assert.Contains("cancelado", cancelled);
    }

    [Fact]
    public void Start_confirmation_states_the_consent_to_apply_and_undo()
    {
        var c = AbLabCandidates.FromCatalog("k@1", "Modo desempenho", new ActionRequest("k@1"), BrokerLevel.User);
        var one = AbLabText.StartConfirmation("Fortnite", [c], AbDesign.AB, autoUndo: true);
        Assert.Contains("\"Modo desempenho\"", one);
        Assert.Contains("Fortnite", one);
        Assert.Contains("2 partidas", one);
        Assert.Contains("volta sozinho", one);
        var many = AbLabText.StartConfirmation("Fortnite", [c, c, c], AbDesign.ABA, autoUndo: false);
        Assert.Contains("3 ajustes", many);
        Assert.Contains("3 partidas", many);
        Assert.Contains("pergunta", many);
    }

    [Fact]
    public void Minimum_minutes_come_from_the_rules()
    {
        Assert.Equal(3, AbLabText.MinMinutes());
        Assert.Equal(5, AbLabText.MinMinutes(new AbRules(MinSeconds: 270)));
    }
}
