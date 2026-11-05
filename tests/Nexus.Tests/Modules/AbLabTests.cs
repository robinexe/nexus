using Nexus.Experimentation;
using Nexus.Measurement;
using Nexus.Measurement.AbLab;

namespace Nexus.Tests.Modules;

/// <summary>Laboratório A/B ("Testar no meu PC"): regras das partidas, veredito/ruído, decisão, máquina de estados, fila e persistência.</summary>
[Trait("Category", "Unit")]
public sealed class AbLabTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-ablab-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    private static readonly AbCandidate Perf = new("fortnite|perf", "Modo desempenho",
        [new AbActionRef("game.fortnite.rhi-dx12@1"), new AbActionRef("game.fortnite.performance-mode@1")]);

    private static readonly AbCandidate NvPower = new("fortnite.exe|nv.maxpower", "Máximo desempenho da placa",
        [new AbActionRef("nv.drs.max-power@1", null, new Dictionary<string, string> { ["exe"] = "fortniteclient-win64-shipping.exe" }, Admin: true)]);

    /// <summary>
    /// Partida sintética: <paramref name="seconds"/> s a ~<paramref name="fps"/> FPS com ruído de ±<paramref name="jitter"/>,
    /// e uma fração <paramref name="spikeRate"/> de quadros com +<paramref name="spikeMs"/> ms (engasgos/piores momentos).
    /// </summary>
    private static MeasuredSession Session(DateTimeOffset at, double fps, double seconds = 240, int seed = 1, double jitter = 0.1,
        double spikeRate = 0.005, double spikeMs = 15, string exe = "FortniteClient-Win64-Shipping.exe", SessionConditions? cond = null, bool excluded = false)
    {
        var rng = new Random(seed);
        var ft = new List<double>();
        double total = 0;
        while (total < seconds * 1000)
        {
            var f = 1000.0 / fps * (1 - jitter + rng.NextDouble() * 2 * jitter) + (rng.NextDouble() < spikeRate ? spikeMs : 0);
            ft.Add(f);
            total += f;
        }
        return new MeasuredSession(Guid.NewGuid().ToString("N"), exe, exe, at, "automático", SessionTag.None, "x.csv",
            FrametimeMetrics.Summarize(ft), [], [], 0, "v2")
        {
            Segments = SessionAnalyzer.Segment(ft),
            Conditions = cond,
            Excluded = excluded,
        };
    }

    private static int _ids;
    private static string NewId() => "t" + Interlocked.Increment(ref _ids);

    // ------------------------------------------------------------------ regras das partidas

    [Fact]
    public void Session_rules_reject_other_game_early_short_excluded_and_changed_conditions()
    {
        var s = AbLabMachine.Start("t", "FortniteClient-Win64-Shipping.exe", "Fortnite", Perf, AbDesign.AB, true, T0);
        Assert.Equal([AbRejection.OtherGame], AbLabMachine.Check(s, Session(T0.AddMinutes(1), 100, exe: "cs2.exe")));
        Assert.Contains(AbRejection.StartedBeforeStep, AbLabMachine.Check(s, Session(T0.AddMinutes(-1), 100)));
        Assert.Contains(AbRejection.TooShort, AbLabMachine.Check(s, Session(T0.AddMinutes(1), 100, seconds: 90)));
        Assert.Contains(AbRejection.TooFewFrames, AbLabMachine.Check(s, Session(T0.AddMinutes(1), 15, seconds: 180)));
        Assert.Contains(AbRejection.Excluded, AbLabMachine.Check(s, Session(T0.AddMinutes(1), 100, excluded: true)));
        Assert.Empty(AbLabMachine.Check(s, Session(T0.AddMinutes(1), 100)));

        var cond = new SessionConditions { Resolution = "1920x1080", OnBattery = false, GpuDriver = "566.36" };
        var (s2, rej) = AbLabMachine.Offer(s, Session(T0.AddMinutes(1), 100, cond: cond), T0.AddMinutes(6));
        Assert.Empty(rej);
        Assert.Equal(cond, s2.Reference);
        var waiting = AbLabMachine.Applied(s2, true, false, [new AbRunRef(Guid.NewGuid(), false)], T0.AddMinutes(7));
        var other = AbLabMachine.Check(waiting, Session(T0.AddMinutes(8), 100, cond: new SessionConditions { Resolution = "1280x720", OnBattery = true, GpuDriver = "572.16" }));
        Assert.Contains(AbRejection.ResolutionChanged, other);
        Assert.Contains(AbRejection.BatteryChanged, other);
        Assert.Contains(AbRejection.DriverChanged, other);
        // Condição desconhecida de um lado não reprova (não dá para afirmar que mudou).
        Assert.Empty(AbLabMachine.Check(waiting, Session(T0.AddMinutes(8), 100, cond: new SessionConditions { Resolution = "1920x1080" })));
    }

    [Fact]
    public void Sessions_are_not_accepted_while_the_applier_works_and_duplicates_are_ignored()
    {
        var s = AbLabMachine.Start("t", "fortniteclient-win64-shipping.exe", "Fortnite", Perf, AbDesign.AB, true, T0);
        var a = Session(T0.AddMinutes(1), 100);
        var (applying, _) = AbLabMachine.Offer(s, a, T0.AddMinutes(6));
        Assert.Equal(AbStep.Applying, applying.Step);
        Assert.Equal([AbRejection.NotWaiting], AbLabMachine.Check(applying, Session(T0.AddMinutes(7), 100)));
        var waiting = AbLabMachine.Applied(applying, true, false, [new AbRunRef(Guid.NewGuid(), false)], T0.AddMinutes(7));
        Assert.Equal([AbRejection.Duplicate], AbLabMachine.Check(waiting, a));
    }

    // ------------------------------------------------------------------ veredito e decisão

    [Fact]
    public void Clear_improvement_beyond_noise_is_kept_and_highlights_the_biggest_gain()
    {
        var a = Session(T0, 100, seed: 1, spikeRate: 0.02, spikeMs: 25);
        var b = Session(T0.AddMinutes(10), 118, seed: 2, spikeRate: 0.004, spikeMs: 10);
        var j = AbLabJudge.Judge(a, b);
        Assert.True(j.Keep);
        Assert.Equal(AbReason.Improved, j.Reason);
        Assert.False(j.Bracketed);
        Assert.True(j.Pairs >= 3);
        Assert.Equal(Verdict.Improved, j.Metrics.Single(m => m.Key == "fps").Verdict);
        Assert.Equal(Verdict.Improved, j.Metrics.Single(m => m.Key == "low1").Verdict);
        Assert.NotNull(j.HighlightKey);
        Assert.True(AbLabJudge.Goodness(j.Metrics.Single(m => m.Key == j.HighlightKey)) >= j.Metrics.Where(m => m.Decisive && m.Verdict == Verdict.Improved).Max(AbLabJudge.Goodness) - 1e-9);
        Assert.Contains(AbWarning.SameScene, j.Warnings);
        Assert.False(j.Metrics.Single(m => m.Key == AbLabJudge.VarianceKey).Decisive);
    }

    [Fact]
    public void Same_performance_is_not_kept()
    {
        var j = AbLabJudge.Judge(Session(T0, 100, seed: 3), Session(T0.AddMinutes(10), 100, seed: 4));
        Assert.False(j.Keep);
        Assert.Contains(j.Reason, new[] { AbReason.NoChange, AbReason.Inconclusive });
        Assert.Null(j.HighlightKey);
    }

    [Fact]
    public void A_small_gain_inside_the_minimum_relevant_effect_is_not_kept()
    {
        // +1,5% de FPS: abaixo da diferença mínima relevante (3%) — mesmo que "real", não importa.
        var j = AbLabJudge.Judge(Session(T0, 100, seed: 5, jitter: 0.02), Session(T0.AddMinutes(10), 101.5, seed: 6, jitter: 0.02));
        Assert.False(j.Keep);
        Assert.NotEqual(Verdict.Improved, j.Metrics.Single(m => m.Key == "fps").Verdict);
    }

    [Fact]
    public void Regression_is_undone_with_the_worst_metric_highlighted()
    {
        var j = AbLabJudge.Judge(Session(T0, 120, seed: 7), Session(T0.AddMinutes(10), 95, seed: 8, spikeRate: 0.02, spikeMs: 25));
        Assert.False(j.Keep);
        Assert.Equal(AbReason.Regressed, j.Reason);
        Assert.NotNull(j.HighlightKey);
        Assert.True(AbLabJudge.Goodness(j.Metrics.Single(m => m.Key == j.HighlightKey)) < 0);
    }

    [Fact]
    public void Mixed_result_is_not_kept()
    {
        // Limite de FPS típico: FPS médio cai, mas os piores momentos melhoram muito.
        var a = Session(T0, 160, seed: 9, jitter: 0.05, spikeRate: 0.03, spikeMs: 20);
        var b = Session(T0.AddMinutes(10), 140, seed: 10, jitter: 0.05, spikeRate: 0.0, spikeMs: 0);
        var j = AbLabJudge.Judge(a, b);
        Assert.Equal(AbReason.Mixed, j.Reason);
        Assert.False(j.Keep);
    }

    [Fact]
    public void Aba_requires_the_gain_against_both_baselines_and_flags_drift_as_noise()
    {
        // Ganho real e estável: A1 ≈ A2, B melhor que as duas → mantém.
        var j = AbLabJudge.Judge(Session(T0, 100, seed: 11), Session(T0.AddMinutes(10), 120, seed: 12), Session(T0.AddMinutes(20), 100, seed: 13));
        Assert.True(j.Bracketed);
        Assert.True(j.Keep);

        // Deriva: o PC foi esquentando e caindo sozinho (A1 120 → B 110 → A2 100). Contra A2 parece ganho; contra A1, perda.
        var drift = AbLabJudge.Judge(Session(T0, 120, seed: 14), Session(T0.AddMinutes(10), 110, seed: 15), Session(T0.AddMinutes(20), 100, seed: 16));
        Assert.False(drift.Keep);
        Assert.Contains(AbWarning.BaselineDrift, drift.Warnings);
        Assert.Equal(AbReason.Noisy, drift.Reason);
        Assert.True(drift.Metrics.Single(m => m.Key == "fps").Noisy);
    }

    [Theory]
    [InlineData(Verdict.Improved, Verdict.Improved, Verdict.Improved)]
    [InlineData(Verdict.Regressed, Verdict.Regressed, Verdict.Regressed)]
    [InlineData(Verdict.Improved, Verdict.NoMeaningfulChange, Verdict.Inconclusive)]
    [InlineData(Verdict.Improved, Verdict.Regressed, Verdict.Inconclusive)]
    [InlineData(Verdict.NoMeaningfulChange, Verdict.NoMeaningfulChange, Verdict.NoMeaningfulChange)]
    [InlineData(Verdict.Improved, Verdict.InsufficientData, Verdict.InsufficientData)]
    public void Aba_combination_requires_agreement(Verdict a, Verdict b, Verdict expected) => Assert.Equal(expected, AbLabJudge.Combine(a, b));

    [Fact]
    public void Too_short_sessions_give_insufficient_and_duration_mismatch_warns()
    {
        var rules = new AbRules(MinSeconds: 10, MinFrames: 100);
        var j = AbLabJudge.Judge(Session(T0, 100, seconds: 70, seed: 17), Session(T0.AddMinutes(5), 130, seconds: 300, seed: 18), rules: rules);
        Assert.Equal(AbReason.Insufficient, j.Reason);
        Assert.False(j.Keep);
        Assert.Contains(AbWarning.DurationMismatch, j.Warnings);
    }

    [Fact]
    public void Present_mode_change_is_reported_with_both_modes()
    {
        var j = AbLabJudge.Judge(Session(T0, 100, seed: 19, cond: new SessionConditions { PresentMode = "Composed: Flip" }),
            Session(T0.AddMinutes(5), 100, seed: 20, cond: new SessionConditions { PresentMode = "Hardware: Independent Flip" }));
        Assert.Contains(AbWarning.PresentModeChanged, j.Warnings);
        Assert.Equal("Composed: Flip", j.PresentModeBefore);
        Assert.Equal("Hardware: Independent Flip", j.PresentModeAfter);
    }

    [Fact]
    public void Frametime_variance_is_indicative_only()
    {
        var j = AbLabJudge.Judge(Session(T0, 100, seed: 21, jitter: 0.3), Session(T0.AddMinutes(5), 100, seed: 22, jitter: 0.05));
        var v = j.Metrics.Single(m => m.Key == AbLabJudge.VarianceKey);
        Assert.Equal(Verdict.Improved, v.Verdict);
        Assert.False(v.Decisive);
        Assert.True(v.DeltaPercent < -10);
    }

    // ------------------------------------------------------------------ máquina de estados

    private static AbJudgement Verdict_(bool keep) =>
        new(keep, keep ? AbReason.Improved : AbReason.NoChange, [], keep ? "low1" : null, keep ? 18 : null, [AbWarning.SameScene], 6, false);

    [Fact]
    public void Ab_improved_flow_keeps_the_tweak_applied()
    {
        var s = AbLabMachine.Start("t", "Fortnite.exe", "Fortnite", Perf, AbDesign.AB, true, T0);
        Assert.Equal(AbCommand.None, AbLabMachine.CommandFor(s));
        Assert.Equal(1, AbLabMachine.PlayIndex(s));
        (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(1), 100, exe: "fortnite.exe"), T0.AddMinutes(5));
        Assert.Equal(AbCommand.Apply, AbLabMachine.CommandFor(s));
        var run = new AbRunRef(Guid.NewGuid(), false);
        s = AbLabMachine.Applied(s, true, false, [run], T0.AddMinutes(6));
        Assert.Equal(AbStep.WaitTreatment, s.Step);
        Assert.True(AbLabMachine.TweakApplied(s));
        Assert.Equal(2, AbLabMachine.PlayIndex(s));
        // A partida que começou antes de aplicar não conta como "depois".
        var (same, rej) = AbLabMachine.Offer(s, Session(T0.AddMinutes(5.5), 100, exe: "fortnite.exe"), T0.AddMinutes(9));
        Assert.Same(s, same);
        Assert.Contains(AbRejection.StartedBeforeStep, rej);
        (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(7), 120, exe: "fortnite.exe"), T0.AddMinutes(12));
        Assert.Equal(AbCommand.Decide, AbLabMachine.CommandFor(s));
        s = AbLabMachine.Decided(s, Verdict_(true), T0.AddMinutes(12));
        Assert.Equal(AbStep.Done, s.Step);
        Assert.Equal(AbOutcome.Kept, s.Outcome);
        Assert.Equal([run], s.Runs);
        Assert.True(AbLabMachine.TweakApplied(s));
    }

    [Fact]
    public void Ab_without_gain_undoes_automatically_with_consent_or_asks_without_it()
    {
        AbTestState ToDeciding(bool autoUndo)
        {
            var s = AbLabMachine.Start("t", "game.exe", "Jogo", Perf, AbDesign.AB, autoUndo, T0);
            (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(1), 100, exe: "game.exe"), T0.AddMinutes(5));
            s = AbLabMachine.Applied(s, true, false, [new AbRunRef(Guid.NewGuid(), true)], T0.AddMinutes(6));
            (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(7), 100, exe: "game.exe"), T0.AddMinutes(12));
            return s;
        }

        var auto = AbLabMachine.Decided(ToDeciding(true), Verdict_(false), T0.AddMinutes(12));
        Assert.Equal(AbStep.Undoing, auto.Step);
        Assert.Equal(AbCommand.Revert, AbLabMachine.CommandFor(auto));
        auto = AbLabMachine.Reverted(auto, true, T0.AddMinutes(13));
        Assert.Equal(AbOutcome.Undone, auto.Outcome);
        Assert.Empty(auto.Runs);
        Assert.False(AbLabMachine.TweakApplied(auto));

        var ask = AbLabMachine.Decided(ToDeciding(false), Verdict_(false), T0.AddMinutes(12));
        Assert.Equal(AbCommand.Ask, AbLabMachine.CommandFor(ask));
        Assert.Equal(AbOutcome.KeptByUser, AbLabMachine.Answer(ask, true, T0.AddMinutes(13)).Outcome);
        var no = AbLabMachine.Answer(ask, false, T0.AddMinutes(13));
        Assert.Equal(AbStep.Undoing, no.Step);
        Assert.Equal(AbOutcome.Undone, AbLabMachine.Reverted(no, true, T0.AddMinutes(14)).Outcome);
    }

    [Fact]
    public void Aba_flow_reverts_for_the_check_game_and_reapplies_only_when_kept()
    {
        var s = AbLabMachine.Start("t", "game.exe", "Jogo", NvPower, AbDesign.ABA, true, T0);
        Assert.Equal(3, AbLabMachine.PlaysNeeded(AbDesign.ABA));
        (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(1), 100, exe: "game.exe"), T0.AddMinutes(5));
        s = AbLabMachine.Applied(s, true, false, [new AbRunRef(Guid.NewGuid(), true)], T0.AddMinutes(6));
        (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(7), 120, exe: "game.exe"), T0.AddMinutes(12));
        Assert.Equal(AbStep.Reverting, s.Step);
        Assert.Equal(AbCommand.Revert, AbLabMachine.CommandFor(s));
        s = AbLabMachine.Reverted(s, true, T0.AddMinutes(13));
        Assert.Equal(AbStep.WaitBaseline2, s.Step);
        Assert.Equal(3, AbLabMachine.PlayIndex(s));
        Assert.Empty(s.Runs);
        (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(14), 100, exe: "game.exe"), T0.AddMinutes(19));
        Assert.Equal(AbStep.Deciding, s.Step);

        var keep = AbLabMachine.Decided(s, Verdict_(true), T0.AddMinutes(19));
        Assert.Equal(AbStep.Reapplying, keep.Step);
        Assert.Equal(AbCommand.Apply, AbLabMachine.CommandFor(keep));
        var runs = new[] { new AbRunRef(Guid.NewGuid(), true) };
        keep = AbLabMachine.Applied(keep, true, false, runs, T0.AddMinutes(20));
        Assert.Equal(AbOutcome.Kept, keep.Outcome);
        Assert.Equal(runs, keep.Runs);

        var drop = AbLabMachine.Decided(s, Verdict_(false), T0.AddMinutes(19));
        Assert.Equal(AbStep.Done, drop.Step); // já está desfeito: nada a fazer
        Assert.Equal(AbOutcome.Undone, drop.Outcome);

        var ask = AbLabMachine.Decided(s with { AutoUndo = false }, Verdict_(false), T0.AddMinutes(19));
        Assert.Equal(AbStep.AskUser, ask.Step);
        Assert.Equal(AbStep.Reapplying, AbLabMachine.Answer(ask, true, T0.AddMinutes(20)).Step);
        Assert.Equal(AbOutcome.Undone, AbLabMachine.Answer(ask, false, T0.AddMinutes(20)).Outcome);
    }

    [Fact]
    public void Already_on_failure_and_cancel_paths()
    {
        var s = AbLabMachine.Start("t", "game.exe", "Jogo", Perf, AbDesign.AB, true, T0);
        (s, _) = AbLabMachine.Offer(s, Session(T0.AddMinutes(1), 100, exe: "game.exe"), T0.AddMinutes(5));
        Assert.False(AbLabMachine.CanCancel(s)); // aplicando
        Assert.Equal(AbOutcome.AlreadyOn, AbLabMachine.Applied(s, true, true, [], T0).Outcome);
        var failed = AbLabMachine.Applied(s, false, false, [], T0);
        Assert.Equal((AbStep.Done, AbOutcome.Failed, AbFailure.ApplyFailed), (failed.Step, failed.Outcome, failed.Failure));
        // Falhou pela metade: desfaz o que entrou e termina como falha.
        var partial = AbLabMachine.Applied(s, false, false, [new AbRunRef(Guid.NewGuid(), false)], T0);
        Assert.Equal(AbStep.Undoing, partial.Step);
        Assert.Equal(AbOutcome.Failed, AbLabMachine.Reverted(partial, true, T0).Outcome);

        var applied = AbLabMachine.Applied(s, true, false, [new AbRunRef(Guid.NewGuid(), false)], T0);
        var cancel = AbLabMachine.Cancel(applied, T0);
        Assert.Equal(AbStep.Undoing, cancel.Step);
        Assert.True(cancel.CancelRequested);
        Assert.Equal(AbOutcome.Cancelled, AbLabMachine.Reverted(cancel, true, T0).Outcome);
        var revertFail = AbLabMachine.Reverted(cancel, false, T0);
        Assert.Equal((AbOutcome.Failed, AbFailure.RevertFailed), (revertFail.Outcome, revertFail.Failure));
        Assert.NotEmpty(revertFail.Runs); // continua aplicado: o Histórico mostra o que desfazer

        var fresh = AbLabMachine.Start("t", "game.exe", "Jogo", Perf, AbDesign.AB, true, T0);
        Assert.Equal(AbOutcome.Cancelled, AbLabMachine.Cancel(fresh, T0).Outcome);
    }

    [Fact]
    public void Resume_stops_an_interrupted_apply_but_keeps_waiting_and_undo_steps()
    {
        var s = AbLabMachine.Start("t", "game.exe", "Jogo", Perf, AbDesign.AB, true, T0);
        Assert.Same(s, AbLabMachine.Resume(s, T0));
        var applying = s with { Step = AbStep.Applying };
        var r = AbLabMachine.Resume(applying, T0);
        Assert.Equal((AbStep.Done, AbOutcome.Failed, AbFailure.Interrupted), (r.Step, r.Outcome, r.Failure));
        var undoing = s with { Step = AbStep.Undoing, Runs = [new AbRunRef(Guid.NewGuid(), false)] };
        Assert.Equal(AbCommand.Revert, AbLabMachine.CommandFor(AbLabMachine.Resume(undoing, T0)));
    }

    [Fact]
    public void Start_requires_actions()
    {
        Assert.Throws<ArgumentException>(() => AbLabMachine.Start("t", "game.exe", "Jogo", new AbCandidate("x", "x", []), AbDesign.AB, true, T0));
    }

    // ------------------------------------------------------------------ fila e histórico

    [Fact]
    public void Queue_tests_one_tweak_at_a_time_and_history_keeps_the_latest_per_tweak()
    {
        var data = AbLabFlow.Begin(AbLabData.Empty, "game.exe", "Jogo", [Perf, NvPower], AbDesign.AB, true, T0, NewId);
        Assert.Equal(Perf, data.Active!.Candidate);
        Assert.Single(data.Queue);
        Assert.Throws<InvalidOperationException>(() => AbLabFlow.Begin(data, "game.exe", "Jogo", [Perf], AbDesign.AB, true, T0, NewId));
        Assert.Same(data, AbLabFlow.Advance(data, T0, NewId)); // ainda não terminou

        data = data with { Active = data.Active with { Step = AbStep.Done, Outcome = AbOutcome.Kept } };
        data = AbLabFlow.Advance(data, T0.AddMinutes(15), NewId);
        Assert.Equal(NvPower, data.Active!.Candidate);
        Assert.Equal(AbStep.WaitBaseline, data.Active.Step);
        Assert.Equal(T0.AddMinutes(15), data.Active.StepSince);
        Assert.Empty(data.Queue);
        Assert.Single(data.History);

        data = data with { Active = data.Active with { Step = AbStep.Done, Outcome = AbOutcome.Undone } };
        data = AbLabFlow.Advance(data, T0.AddMinutes(30), NewId);
        Assert.Null(data.Active);
        Assert.Equal(2, data.History.Count);

        // Novo teste do Modo desempenho: o último resultado vale; cancelados não aparecem no cartão.
        var again = AbLabFlow.Begin(data, "game.exe", "Jogo", [Perf], AbDesign.AB, true, T0.AddHours(1), NewId);
        again = again with { Active = again.Active! with { Step = AbStep.Done, Outcome = AbOutcome.Undone } };
        again = AbLabFlow.Advance(again, T0.AddHours(2), NewId);
        var cancelled = AbLabFlow.Begin(again, "GAME.EXE", "Jogo", [NvPower], AbDesign.AB, true, T0.AddHours(3), NewId);
        cancelled = AbLabFlow.Advance(cancelled with { Active = cancelled.Active! with { Step = AbStep.Done, Outcome = AbOutcome.Cancelled } }, T0.AddHours(3), NewId);
        var latest = AbLabFlow.LatestFor(cancelled.History, "Game.exe");
        Assert.Equal(2, latest.Count);
        Assert.Equal(AbOutcome.Undone, latest.Single(r => r.CandidateId == Perf.Id).Outcome);
        Assert.Equal(AbOutcome.Undone, latest.Single(r => r.CandidateId == NvPower.Id).Outcome);
        Assert.Empty(AbLabFlow.LatestFor(cancelled.History, "cs2.exe"));
    }

    [Fact]
    public void Cancel_or_failure_drops_the_rest_of_the_queue()
    {
        var data = AbLabFlow.Begin(AbLabData.Empty, "game.exe", "Jogo", [Perf, NvPower], AbDesign.AB, true, T0, NewId);
        data = data with { Active = AbLabMachine.Cancel(data.Active!, T0) };
        data = AbLabFlow.Advance(data, T0, NewId);
        Assert.Null(data.Active);
        Assert.Empty(data.Queue);
    }

    [Fact]
    public void History_is_capped()
    {
        var data = AbLabData.Empty;
        for (var i = 0; i < AbLabFlow.MaxHistory + 5; i++)
        {
            data = AbLabFlow.Begin(data, "game.exe", "Jogo", [Perf], AbDesign.AB, true, T0.AddMinutes(i), NewId);
            data = AbLabFlow.Advance(data with { Active = data.Active! with { Step = AbStep.Done, Outcome = AbOutcome.Undone } }, T0.AddMinutes(i), NewId);
        }
        Assert.Equal(AbLabFlow.MaxHistory, data.History.Count);
        Assert.Equal(T0.AddMinutes(AbLabFlow.MaxHistory + 4), data.History[^1].At);
    }

    // ------------------------------------------------------------------ persistência

    [Fact]
    public void Store_round_trips_an_active_test_with_runs_queue_and_history()
    {
        var store = new AbLabStore(Path.Combine(_dir, "sub", "ab-lab.json"));
        Assert.Same(AbLabData.Empty, store.Load());
        var data = AbLabFlow.Begin(AbLabData.Empty, "game.exe", "Jogo", [NvPower, Perf], AbDesign.ABA, false, T0, NewId);
        var judgement = AbLabJudge.Judge(Session(T0, 100, seed: 30), Session(T0.AddMinutes(5), 125, seed: 31));
        var run = Guid.NewGuid();
        data = data with
        {
            Active = data.Active! with { Step = AbStep.WaitTreatment, Runs = [new AbRunRef(run, true)], Baseline = "s1", Reference = new SessionConditions { Resolution = "1920x1080" } },
            History = [new AbResult("old", "game.exe", "Jogo", Perf.Id, Perf.Title, T0.AddDays(-1), AbDesign.AB, AbOutcome.Kept, AbFailure.None, judgement)],
        };
        store.Save(data);
        var back = store.Load();
        Assert.Equal(AbStep.WaitTreatment, back.Active!.Step);
        Assert.Equal(AbDesign.ABA, back.Active.Design);
        Assert.False(back.Active.AutoUndo);
        Assert.Equal(run, back.Active.Runs.Single().Run);
        Assert.True(back.Active.Runs.Single().Admin);
        Assert.Equal("fortniteclient-win64-shipping.exe", back.Active.Candidate.Actions[0].Arguments!["exe"]);
        Assert.Equal("1920x1080", back.Active.Reference!.Resolution);
        Assert.Equal(Perf.Id, back.Queue.Single().Id);
        var h = back.History.Single();
        Assert.Equal(judgement.Reason, h.Judgement!.Reason);
        Assert.Equal(judgement.Metrics.Count, h.Judgement.Metrics.Count);
        Assert.Equal(judgement.Metrics[0].Verdict, h.Judgement.Metrics[0].Verdict);
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Store_with_a_broken_file_starts_empty_and_keeps_a_copy()
    {
        var path = Path.Combine(_dir, "ab-lab.json");
        File.WriteAllText(path, "{ isto não é json");
        var store = new AbLabStore(path);
        Assert.Same(AbLabData.Empty, store.Load());
        Assert.True(File.Exists(path + ".bad"));
        Assert.True(File.Exists(path));
    }
}
