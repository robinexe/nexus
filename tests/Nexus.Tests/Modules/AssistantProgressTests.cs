using Nexus.Assistant;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;

namespace Nexus.Tests.Modules;

/// <summary>Relógio manual para o tracker (monotônico, avança só quando o teste manda).</summary>
internal sealed class ManualClock
{
    public TimeSpan Now { get; set; }
    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
}

[Trait("Category", "Unit")]
public sealed class EtaEstimatorTests
{
    [Fact]
    public void Calculating_until_there_is_enough_progress_and_time()
    {
        var eta = new EtaEstimator();
        Assert.Null(eta.Update(TimeSpan.FromSeconds(0.5), 0.2)); // pouco tempo
        Assert.Null(eta.Update(TimeSpan.FromSeconds(5), 0.01)); // pouco andamento
        Assert.NotNull(eta.Update(TimeSpan.FromSeconds(5), 0.25));
        Assert.Equal("calculando…", ProcessText.Remaining(new EtaEstimator().Remaining));
    }

    [Fact]
    public void Steady_progress_converges_to_the_true_remaining_time()
    {
        // 100 s de trabalho em ritmo constante: aos 50 s faltam 50 s.
        var eta = new EtaEstimator();
        TimeSpan? r = null;
        for (var t = 2.0; t <= 50; t += 0.2) r = eta.Update(TimeSpan.FromSeconds(t), t / 100);
        Assert.NotNull(r);
        Assert.InRange(r!.Value.TotalSeconds, 45, 55);
    }

    [Fact]
    public void Never_negative_and_reaches_zero_when_done()
    {
        var eta = new EtaEstimator();
        eta.Update(TimeSpan.FromSeconds(3), 0.5);
        // Muito tempo sem andamento novo: a contagem regressiva para em zero, nunca fica negativa.
        for (var t = 3.0; t < 200; t += 1) Assert.True(eta.Update(TimeSpan.FromSeconds(t), 0.5)!.Value >= TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, eta.Update(TimeSpan.FromSeconds(201), 1));
    }

    [Fact]
    public void A_sudden_jump_in_progress_moves_the_estimate_gradually()
    {
        var eta = new EtaEstimator();
        for (var t = 2.0; t <= 20; t += 0.2) eta.Update(TimeSpan.FromSeconds(t), t / 200); // ritmo lento: faltam ~180 s
        var before = eta.Remaining!.Value.TotalSeconds;
        // Uma parte grande terminou de repente (10% → 60%): o valor bruto cai para ~13 s, mas a tela não pula de uma vez.
        var after = eta.Update(TimeSpan.FromSeconds(20.2), 0.6)!.Value.TotalSeconds;
        Assert.True(after < before);
        Assert.True(before - after < before * 0.25, $"pulou de {before:0} s para {after:0} s");
    }
}

[Trait("Category", "Unit")]
public sealed class ProcessTextTests
{
    [Theory]
    [InlineData(3, "quase lá")]
    [InlineData(12, "faltam ~12 s")]
    [InlineData(43, "faltam ~45 s")]
    [InlineData(80, "faltam ~1 min 20 s")]
    [InlineData(121, "faltam ~2 min")]
    [InlineData(1500, "faltam ~25 min")]
    [InlineData(-5, "quase lá")]
    public void Remaining_is_short_rounded_pt_br(double seconds, string expected) =>
        Assert.Equal(expected, ProcessText.Remaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Clock_and_duration()
    {
        Assert.Equal("0:07", ProcessText.Clock(TimeSpan.FromSeconds(7.4)));
        Assert.Equal("1:23", ProcessText.Clock(TimeSpan.FromSeconds(83)));
        Assert.Equal("1:02:03", ProcessText.Clock(TimeSpan.FromSeconds(3723)));
        Assert.Equal("0:00", ProcessText.Clock(TimeSpan.FromSeconds(-1)));
        Assert.Equal("12 s", ProcessText.Duration(TimeSpan.FromSeconds(12.3)));
        Assert.Equal("1 min 05 s", ProcessText.Duration(TimeSpan.FromSeconds(65)));
        Assert.Equal("menos de 1 s", ProcessText.Duration(TimeSpan.FromSeconds(0.2)));
        Assert.Equal("etapa 3 de 13", ProcessText.Position(AssistantStepId.Cleaning));
    }

    [Fact]
    public void Time_fraction_is_monotonic_and_never_completes_alone()
    {
        Assert.Equal(0, ProcessText.TimeFraction(0, 10));
        Assert.Equal(0.45, ProcessText.TimeFraction(5, 10), 3);
        Assert.Equal(0.9, ProcessText.TimeFraction(10, 10), 3);
        var last = 0.0;
        for (var t = 0.0; t < 200; t += 0.5)
        {
            var f = ProcessText.TimeFraction(t, 10);
            Assert.True(f >= last);
            Assert.True(f < 0.99 + 1e-9);
            last = f;
        }
        Assert.Equal(0, ProcessText.TimeFraction(5, 0));
    }
}

[Trait("Category", "Unit")]
public sealed class ProcessTrackerTests
{
    [Fact]
    public void Finished_tasks_count_whole_and_the_running_one_by_typical_time()
    {
        var clock = new ManualClock();
        var t = new ProcessTracker("x", () => clock.Now);
        var a = t.Add("a", "A", 10);
        var b = t.Add("b", "B", 10);
        t.Start(a);
        clock.Advance(5);
        Assert.Equal(0.45 * 10 / 20, t.RawProgress(), 3);
        t.Finish(a, ProcessTaskStatus.Done);
        t.Start(b);
        Assert.Equal(0.5, t.RawProgress(), 3);
        t.Finish(b, ProcessTaskStatus.Failed, "não");
        Assert.Equal(1, t.RawProgress());
        Assert.True(t.IsComplete);
    }

    [Fact]
    public void Shown_progress_never_goes_back_even_when_a_task_is_added()
    {
        var clock = new ManualClock();
        var t = new ProcessTracker("x", () => clock.Now);
        var a = t.Add("a", "A", 2);
        t.Start(a);
        t.Finish(a, ProcessTaskStatus.Done);
        t.Sample();
        Assert.Equal(1, t.Progress, 3);
        t.Add("b", "B", 50); // replanejado: o bruto cai para ~4%, a tela não
        t.Sample();
        Assert.Equal(1, t.Progress, 3);
        Assert.True(t.RawProgress() < 0.1);
    }

    [Fact]
    public void Waiting_for_the_user_stops_the_estimate_clock_but_not_elapsed()
    {
        var clock = new ManualClock();
        var t = new ProcessTracker("x", () => clock.Now);
        var a = t.Add("a", "A", 10);
        t.Start(a);
        clock.Advance(4);
        t.Sample();
        var before = t.RawProgress();
        t.Wait(a, "Aguardando a sua confirmação…");
        Assert.True(t.IsWaiting);
        Assert.Equal("Aguardando a sua confirmação…", t.CurrentText);
        clock.Advance(60);
        t.Sample();
        Assert.Equal(before, t.RawProgress(), 6);
        Assert.Equal(64, t.Elapsed.TotalSeconds, 3);
        Assert.Equal(4, t.ActiveElapsed.TotalSeconds, 3);
        t.Resume(a, "Aplicando…");
        Assert.False(t.IsWaiting);
        clock.Advance(1);
        Assert.Equal(5, t.ActiveElapsed.TotalSeconds, 3);
    }

    [Fact]
    public void Complete_freezes_the_clock_and_skips_what_did_not_run()
    {
        var clock = new ManualClock();
        var t = new ProcessTracker("x", () => clock.Now);
        var a = t.Add("a", "A", 1);
        var b = t.Add("b", "B", 1);
        t.Start(a);
        clock.Advance(2);
        t.Complete();
        clock.Advance(30);
        Assert.Equal(2, t.Elapsed.TotalSeconds, 3);
        Assert.Equal(ProcessTaskStatus.Done, a.Status);
        Assert.Equal(ProcessTaskStatus.Skipped, b.Status);
        Assert.Equal(1, t.Progress);
        Assert.Equal(TimeSpan.Zero, t.Remaining);
    }
}

[Trait("Category", "Unit")]
public sealed class ApplyProgressTests
{
    private static ProposedChange Clean(string title, string size, string key = "t.user.low@1") =>
        new($"c:{title}", AssistantStepId.Cleaning, title, "", new ActionRequest(key), BrokerLevel.User, ActionRisk.Low, false, false, true, size, "na quarentena");

    /// <summary>Aplicador falso que avisa os marcos como a janela faz.</summary>
    private sealed class StagedWriter : IAssistantWriter
    {
        public List<ApplyStage> Seen { get; } = [];
        public Func<IReadOnlyList<ActionRequest>, BrokerLevel, WriteOutcome?> OnApply { get; set; } = (reqs, _) =>
            new WriteOutcome(true, Guid.NewGuid(), "ok", reqs.Select((r, i) => new ActionResultDto(r.ActionId, "Verified", Index: i)).ToList());

        public Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level) =>
            ApplyAsync(title, confirmation, requests, level, null);

        public Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level, IProgress<ApplyStage>? stages)
        {
            foreach (var s in new[] { ApplyStage.Confirmed, level == BrokerLevel.Admin ? ApplyStage.WaitingWindows : ApplyStage.StartingApplier, ApplyStage.Running })
            {
                stages?.Report(s);
                Seen.Add(s);
            }
            var r = OnApply(requests, level);
            stages?.Report(ApplyStage.Refreshing);
            return Task.FromResult(r);
        }

        public Task<WriteOutcome?> RevertAsync(Guid runId, BrokerLevel level) => Task.FromResult<WriteOutcome?>(null);
    }

    [Fact]
    public async Task Batches_become_feed_rows_with_real_results_and_moved_bytes()
    {
        var c = AssistantFakes.Cat();
        var clock = new ManualClock();
        var tracker = new ProcessTracker("Limpeza", () => clock.Now);
        var observer = new ApplyProgressObserver(tracker, AssistantStepId.Cleaning);
        var writer = new StagedWriter
        {
            OnApply = (reqs, _) => new WriteOutcome(true, Guid.NewGuid(), "ok", reqs.Select((r, i) =>
                new ActionResultDto(r.ActionId, i == 1 ? "AlreadyConfigured" : i == 2 ? "Failed" : "Verified", i == 2 ? "arquivo em uso" : null, Index: i)).ToList()),
        };
        var chosen = new[]
        {
            Clean("Cache do Chrome", "8,0 GB"),
            Clean("Cache do Edge", "1,0 GB", "t.user.low2@1"),
            Clean("Temporários", "300 MB", "t.declared.low@1"),
            AssistantFakes.Change(c, "t.admin.low@1", AssistantStepId.Cleaning),
        };
        var result = await AssistantRunner.ApplyAsync(new AssistantState(), AssistantStepId.Cleaning, chosen, writer, observer);

        Assert.Equal(2, result.Applied);
        Assert.Equal(1, result.Already);
        Assert.Equal(1, result.Refused);
        var user = Assert.Single(tracker.Tasks, t => t.Key == "batch:User:1");
        var admin = Assert.Single(tracker.Tasks, t => t.Key == "batch:Admin:2");
        Assert.Equal("Na sua conta: 3 mudanças", user.Title);
        Assert.Equal("Mudanças de sistema: 1 mudança", admin.Title);
        Assert.Equal(ProcessTaskStatus.Done, user.Status);
        Assert.Equal([ProcessTaskStatus.Done, ProcessTaskStatus.Skipped, ProcessTaskStatus.Failed], user.Items.Select(i => i.Status));
        Assert.Equal("Não aplicada: arquivo em uso", user.Items[2].Detail);
        Assert.StartsWith("1 feita · 1 não precisou · 1 não aplicada", user.Detail, StringComparison.Ordinal);
        Assert.Equal(8.0 * 1024 * 1024 * 1024, observer.MovedBytes, 0); // só o que foi aplicado (o Edge já estava assim)
        Assert.Contains(ApplyStage.WaitingWindows, writer.Seen);
        Assert.True(tracker.IsComplete);
        Assert.False(tracker.IsWaiting);

        var summary = ProcessSummary.ForApply(result, observer.MovedBytes, TimeSpan.FromSeconds(12));
        Assert.Equal("8,0 GB movidos para a quarentena • 1 já estava assim • 1 não aplicada • 12 s", summary.Text);
        Assert.Equal(ProcessTaskStatus.Done, summary.Status);
    }

    [Fact]
    public void Stages_drive_the_text_and_the_estimate_while_the_applier_works()
    {
        var c = AssistantFakes.Cat();
        var clock = new ManualClock();
        var tracker = new ProcessTracker("x", () => clock.Now);
        var observer = new ApplyProgressObserver(tracker, AssistantStepId.Privacy);
        var batch = ApplyGrouping.Group(AssistantStepId.Privacy, [AssistantFakes.Change(c, "t.admin.low@1", AssistantStepId.Privacy)]).Single();
        observer.Planned([batch]);
        var task = tracker.Tasks.Single();

        observer.Stage(batch, ApplyStage.WaitingConfirmation);
        Assert.True(tracker.IsWaiting);
        clock.Advance(30); // usuário lendo a confirmação: não conta
        observer.Stage(batch, ApplyStage.Confirmed);
        observer.Stage(batch, ApplyStage.WaitingWindows);
        Assert.Contains("aviso do Windows", tracker.CurrentText, StringComparison.Ordinal);
        clock.Advance(10);
        observer.Stage(batch, ApplyStage.Running);
        Assert.False(tracker.IsWaiting);
        Assert.True(task.TextIsEstimate);
        Assert.Contains("ponto de restauração", tracker.CurrentText, StringComparison.Ordinal);
        clock.Advance(ProcessTimeModel.AdminRestorePoint + 0.1);
        observer.Tick();
        Assert.Equal("Ajustando a privacidade: t.admin.low@1…", tracker.CurrentText);
        Assert.Equal(ProcessTimeModel.AdminRestorePoint + 0.1, tracker.ActiveElapsed.TotalSeconds, 3);
        observer.Stage(batch, ApplyStage.Refreshing);
        Assert.False(task.TextIsEstimate);
        Assert.Contains("relendo", tracker.CurrentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recent_restore_point_is_not_requested_and_counts_as_not_needed()
    {
        var c = AssistantFakes.Real();
        var state = new AssistantState();
        var writer = new FakeWriter();
        var tracker = new ProcessTracker("x", () => TimeSpan.Zero);
        var observer = new ApplyProgressObserver(tracker, AssistantStepId.Welcome);
        var rp = AssistantFakes.Change(c, StepPlanners.RestorePointAction, AssistantStepId.Welcome);
        var now = new DateTime(2026, 11, 1, 18, 0, 0);
        observer.Planned(ApplyGrouping.Group(AssistantStepId.Welcome, [rp])); // a tela planeja antes de saber
        var r = await AssistantRunner.ApplyAsync(state, AssistantStepId.Welcome, [rp], writer, observer, recentRestorePoint: now.AddHours(-5), now: now);

        Assert.Empty(writer.Applied);
        Assert.Equal(1, r.NotNeeded);
        Assert.Equal(0, r.Refused);
        Assert.Equal("1 não precisou", r.Summary);
        var rec = Assert.Single(state.Changes);
        Assert.Equal(ChangeOutcome.AlreadyConfigured, rec.Outcome);
        Assert.Equal("Já existe um ponto de restauração recente (01/11 13:00): vale como proteção.", rec.Detail);
        Assert.Empty(state.Skipped);
        // O lote planejado antes saiu do feed; ficou a linha "não precisou".
        var row = Assert.Single(tracker.Tasks);
        Assert.Equal(ProcessTaskStatus.Skipped, row.Status);
        Assert.Contains("vale como proteção", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Old_or_unknown_restore_point_still_goes_to_the_applier()
    {
        var c = AssistantFakes.Real();
        var rp = AssistantFakes.Change(c, StepPlanners.RestorePointAction, AssistantStepId.Welcome);
        var now = new DateTime(2026, 11, 1, 18, 0, 0);
        var writer = new FakeWriter();
        await AssistantRunner.ApplyAsync(new AssistantState(), AssistantStepId.Welcome, [rp], writer, recentRestorePoint: now.AddHours(-30), now: now);
        Assert.Single(writer.Applied);
        writer = new FakeWriter();
        await AssistantRunner.ApplyAsync(new AssistantState(), AssistantStepId.Welcome, [rp], writer, recentRestorePoint: null, now: now);
        Assert.Single(writer.Applied);
        Assert.False(RestorePointRule.IsRecent(now.AddHours(2), now)); // data no futuro não vale
    }

    [Fact]
    public async Task Windows_24h_refusal_of_the_restore_point_is_neutral_not_a_failure()
    {
        var c = AssistantFakes.Real();
        var state = new AssistantState();
        const string windows = "O Windows não criou um ponto novo: por padrão ele cria no máximo um a cada 24 horas. O ponto mais recente continua valendo.";
        var writer = new FakeWriter
        {
            OnApply = (reqs, _) => new WriteOutcome(true, Guid.NewGuid(), "ok", reqs.Select((q, i) => new ActionResultDto(q.ActionId, "Failed", windows, Index: i)).ToList()),
        };
        var rp = AssistantFakes.Change(c, StepPlanners.RestorePointAction, AssistantStepId.Welcome);
        var r = await AssistantRunner.ApplyAsync(state, AssistantStepId.Welcome, [rp], writer);
        Assert.Equal(0, r.Refused);
        Assert.Equal(1, r.NotNeeded);
        Assert.DoesNotContain("recusada", r.Summary, StringComparison.Ordinal);
        Assert.Equal(ChangeOutcome.AlreadyConfigured, Assert.Single(state.Changes).Outcome);
        Assert.Empty(state.Skipped);
        Assert.Empty(state.Runs);
    }

    [Fact]
    public async Task Other_failures_are_described_in_words_not_enum_names()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        var writer = new FakeWriter
        {
            OnApply = (reqs, _) => new WriteOutcome(true, Guid.NewGuid(), "ok", reqs.Select((q, i) => new ActionResultDto(q.ActionId, "FailedAndCompensated", "acesso negado", Index: i)).ToList()),
        };
        await AssistantRunner.ApplyAsync(state, AssistantStepId.Privacy, [AssistantFakes.Change(c, "t.user.low@1", AssistantStepId.Privacy)], writer);
        var detail = Assert.Single(state.Changes).Detail;
        Assert.Equal("Não aplicada (voltou ao que era): acesso negado", detail);
        Assert.DoesNotContain("FailedAndCompensated", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_text_follows_request_order_with_the_admin_restore_point_first()
    {
        var batch = new ApplyBatch(AssistantStepId.Cleaning, BrokerLevel.Admin, 1, 1, "", "", [Clean("Logs do Windows Update", "1,0 GB"), Clean("Cache de entrega", "10 MB")]);
        Assert.Contains("ponto de restauração", ProcessTimeModel.BatchText(batch, 5), StringComparison.Ordinal);
        Assert.Equal("Movendo para a quarentena: Logs do Windows Update…", ProcessTimeModel.BatchText(batch, ProcessTimeModel.AdminRestorePoint + 0.5));
        Assert.Equal("Movendo para a quarentena: Cache de entrega…", ProcessTimeModel.BatchText(batch, ProcessTimeModel.AdminRestorePoint + 3.1));
        Assert.Contains("Conferindo", ProcessTimeModel.BatchText(batch, 500), StringComparison.Ordinal);
        Assert.Equal(ProcessTimeModel.StartApplier + ProcessTimeModel.AdminRestorePoint + 3 + 1 + ProcessTimeModel.Refresh, ProcessTimeModel.BatchSeconds(batch), 1);
        Assert.Equal(1.5 * 1024 * 1024 * 1024, ProcessTimeModel.BytesOf("1,5 GB"), 0);
        Assert.Equal(0, ProcessTimeModel.BytesOf("na quarentena"));
    }

    [Fact]
    public void Summaries_for_nothing_canceled_and_all_refused()
    {
        Assert.Equal(ProcessTaskStatus.Failed, ProcessSummary.ForApply(new StepApplyResult(0, 0, 2, 0, []), 0, TimeSpan.FromSeconds(3)).Status);
        Assert.Equal("2 canceladas • 3 s", ProcessSummary.ForApply(new StepApplyResult(0, 0, 0, 2, []), 0, TimeSpan.FromSeconds(3)).Text);
        Assert.Equal("5 feitas • 1 não precisou • 1 min 05 s", ProcessSummary.ForApply(new StepApplyResult(5, 0, 0, 0, [], 1), 0, TimeSpan.FromSeconds(65)).Text);
        Assert.Equal("6 itens lidos • 2 sugestões • 4 s", ProcessSummary.ForScan(6, 2, TimeSpan.FromSeconds(4)).Text);
    }
}

[Trait("Category", "Unit")]
public sealed class ApplySummaryTests
{
    private static BrokerCallResult Exec(params (string Outcome, string? Detail)[] results) =>
        new(true, new BrokerResponse(Guid.NewGuid(), true, Results: results.Select(r => new ActionResultDto("a@1", r.Outcome, r.Detail)).ToList(),
            RunId: Guid.NewGuid(), Message: "Execução encerrada."), "Execução …: técnico");

    [Fact]
    public void Status_bar_text_is_human_without_guid_or_enum_names()
    {
        var r = Exec(("Verified", null), ("Verified", null), ("Verified", null), ("Verified", null), ("Verified", null),
            ("Failed", "O Windows não criou um ponto novo: por padrão ele cria no máximo um a cada 24 horas. O ponto mais recente continua valendo."));
        var text = ApplySummary.Describe(r);
        Assert.Equal("Última aplicação: 5 feitas, 1 não precisou.", text);
        Assert.DoesNotContain(r.Response!.RunId!.Value.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_refusals_and_reverts_in_words()
    {
        Assert.Equal("Última aplicação: 1 feita, 1 já estava assim, 2 não aplicadas (motivo na página Aplicar).",
            ApplySummary.Describe(Exec(("Verified", null), ("AlreadyConfigured", null), ("Failed", "x"), ("Interrupted", null))));
        Assert.Equal("O aplicador recusou o pedido: sessão ativa",
            ApplySummary.Describe(new BrokerCallResult(true, BrokerResponse.Fail(Guid.NewGuid(), "session-active", "sessão ativa"), "Execute recusado")));
        var withPoint = new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true, Results: [new ActionResultDto("a@1", "Verified")],
            Message: "Execução encerrada. Ponto de restauração do Windows criado antes de aplicar."), "");
        Assert.Equal("Última aplicação: 1 feita. Ponto de restauração do Windows criado antes de aplicar.", ApplySummary.Describe(withPoint));
        Assert.Equal("Desfeito: tudo voltou ao que era.",
            ApplySummary.DescribeRevert(new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true, FullyRestored: true), "RevertRun: tudo restaurado.")));
        Assert.Equal("O aplicador não respondeu.", ApplySummary.Describe(new BrokerCallResult(false, null, "O aplicador não respondeu.")));
    }

    [Fact]
    public void The_restore_point_handler_message_is_recognized()
    {
        var sr = new FakeRestore { Skip = true };
        sr.Now = sr.Now.AddDays(3);
        var h = new Nexus.SystemTools.Restore.RestorePointHandler(sr, () => sr.Now);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var entry = catalog.Actions.Single(a => a.Definition.Id == "system.restore.create-point");
        var inst = entry.TryInstantiate(entry.ValidateUserArguments(new Dictionary<string, string>(), out _)!, out var reason);
        Assert.True(inst is not null, reason);
        var handlers = new HandlerRegistry().Add(h);
        var exec = new ActionExecutor(handlers, new InMemoryJournal());
        var run = exec.BeginRun("teste", "1");
        var plan = new DesiredStateResolver(handlers).Resolve([inst!], T.Ctx);
        var result = exec.Execute(run, plan, T.Ctx)[0];
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        var detail = result.Detail;
        Assert.True(ApplySummary.IsRecentRestorePointRefusal(detail), detail);
        Assert.False(ApplySummary.IsRecentRestorePointRefusal("A Restauração do Sistema está desligada."));
    }
}

[Trait("Category", "Unit")]
public sealed class HostApplyProgressTests
{
    private static readonly (string Key, string Name)[] Actions = [("a@1", "Modo de Jogo"), ("b@1", "Telemetria opcional"), ("c@1", "Anúncios")];

    private static BrokerCallResult Ok(string? message, params (string Key, string Outcome, string? Detail)[] results) =>
        new(true, new BrokerResponse(Guid.NewGuid(), true, Results: results.Select(r => new ActionResultDto(r.Key, r.Outcome, r.Detail)).ToList(),
            RunId: Guid.NewGuid(), Message: message), "técnico");

    [Fact]
    public void Admin_apply_walks_confirmation_windows_restore_point_actions_and_check()
    {
        var clock = new ManualClock();
        var t = new ProcessTracker("Aplicar", () => clock.Now);
        var p = new HostApplyProgress(t, Actions, BrokerLevel.Admin);
        Assert.Equal(["Sua confirmação", "Aviso do Windows (administrador)", "Ponto de restauração do Windows", "Aplicando 3 ações", "Conferindo e relendo o PC"],
            t.Tasks.Select(x => x.Title));
        Assert.Equal(["Modo de Jogo", "Telemetria opcional", "Anúncios"], t.Find("work")!.Items.Select(i => i.Title));

        p.Stage(ApplyStage.WaitingConfirmation);
        Assert.Equal("Aguardando a sua confirmação…", t.CurrentText);
        clock.Advance(20);
        p.Stage(ApplyStage.Confirmed);
        p.Stage(ApplyStage.WaitingWindows);
        Assert.True(t.IsWaiting);
        Assert.Contains("aviso do Windows", t.CurrentText, StringComparison.Ordinal);
        clock.Advance(5);
        p.Stage(ApplyStage.Running);
        Assert.Equal(ProcessTaskStatus.Done, t.Find("windows")!.Status);
        Assert.Contains("ponto de restauração", t.CurrentText, StringComparison.Ordinal);
        Assert.True(t.Current!.TextIsEstimate);
        clock.Advance(ProcessTimeModel.AdminRestorePoint + 0.8);
        p.Tick();
        Assert.Equal("Aplicando: Telemetria opcional…", t.CurrentText);

        p.Finished(Ok("Execução encerrada. Ponto de restauração do Windows criado antes de aplicar.",
            ("a@1", "Verified", null), ("b@1", "AlreadyConfigured", null), ("c@1", "Failed", "acesso negado")));
        Assert.Equal(ProcessTaskStatus.Done, t.Find("restore")!.Status);
        var work = t.Find("work")!;
        Assert.Equal([ProcessTaskStatus.Done, ProcessTaskStatus.Skipped, ProcessTaskStatus.Failed], work.Items.Select(i => i.Status));
        Assert.Equal("Não aplicada: acesso negado", work.Items[2].Detail);
        p.Stage(ApplyStage.Refreshing);
        Assert.Equal(ProcessTaskStatus.Running, t.Find("check")!.Status);
        clock.Advance(2);
        p.Done();
        Assert.True(t.IsComplete);
        var s = p.Summary();
        Assert.Equal("1 feita • 1 já estava assim • 1 não aplicada • 58 s", s.Text);
        Assert.Equal(ProcessTaskStatus.Done, s.Status);
    }

    [Fact]
    public void Already_running_applier_skips_the_windows_prompt_and_restore_point_reason_is_shown()
    {
        var t = new ProcessTracker("x", () => TimeSpan.Zero);
        var p = new HostApplyProgress(t, Actions, BrokerLevel.Admin);
        p.Stage(ApplyStage.WaitingConfirmation);
        p.Stage(ApplyStage.Confirmed);
        p.Stage(ApplyStage.Running);
        Assert.Equal(ProcessTaskStatus.Skipped, t.Find("windows")!.Status);
        p.Finished(Ok("Execução encerrada.", ("a@1", "Verified", null)));
        Assert.Equal(ProcessTaskStatus.Skipped, t.Find("restore")!.Status);
        Assert.Contains("já criado", t.Find("restore")!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void User_level_has_no_windows_or_restore_point_rows_and_cancel_sends_nothing()
    {
        var t = new ProcessTracker("x", () => TimeSpan.Zero);
        var p = new HostApplyProgress(t, Actions, BrokerLevel.User);
        Assert.Null(t.Find("windows"));
        Assert.Null(t.Find("restore"));
        p.Stage(ApplyStage.WaitingConfirmation);
        p.Canceled();
        Assert.True(t.IsComplete);
        Assert.Equal(ProcessTaskStatus.Skipped, t.Find("confirm")!.Status);
        Assert.StartsWith("Nada foi enviado", p.Summary().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_request_and_revert_are_described_for_people()
    {
        var t = new ProcessTracker("x", () => TimeSpan.Zero);
        var p = new HostApplyProgress(t, Actions, BrokerLevel.User);
        p.Stage(ApplyStage.Confirmed);
        p.Finished(new BrokerCallResult(true, BrokerResponse.Fail(Guid.NewGuid(), "session-active", "há uma sessão aberta"), "Execute recusado"));
        Assert.Equal(ProcessTaskStatus.Failed, t.Find("work")!.Status);
        Assert.Equal(ProcessTaskStatus.Failed, p.Summary().Status);
        Assert.Contains("há uma sessão aberta", p.Summary().Text, StringComparison.Ordinal);

        var t2 = new ProcessTracker("x", () => TimeSpan.Zero);
        var names = new Dictionary<string, string> { ["a@1"] = "Modo de Jogo" };
        var r = new HostApplyProgress(t2, [], BrokerLevel.User, revert: true, name: k => names.GetValueOrDefault(k, k));
        Assert.Equal("Desfazendo a execução", t2.Find("work")!.Title);
        r.Stage(ApplyStage.Confirmed);
        r.Stage(ApplyStage.Running);
        Assert.StartsWith("Desfazendo pelo diário", t2.CurrentText, StringComparison.Ordinal);
        r.Finished(new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true, Results: [new ActionResultDto("a@1", "Reverted"), new ActionResultDto("z@1", "Conflict", "mudou")],
            FullyRestored: false), ""));
        Assert.Equal(["Modo de Jogo", "z@1"], t2.Find("work")!.Items.Select(i => i.Title));
        Assert.Equal("Não voltou: mudou por fora depois: mudou", t2.Find("work")!.Items[1].Detail);
        r.Done();
        Assert.StartsWith("1 desfeita • 1 não voltou", r.Summary().Text, StringComparison.Ordinal);
        Assert.Equal(ProcessTaskStatus.Failed, r.Summary().Status);
    }

    [Fact]
    public void Broker_status_line_is_human_for_every_kind_of_answer()
    {
        var status = new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true,
            Status: new BrokerStatus("0.9.0-ci.7+abc", "2026.11", true, "Execução neste PC autorizada pelo usuário.", "livre")), "Broker 0.9.0 · escrita LIBERADA");
        Assert.Equal("Aplicador pronto · escrita liberada neste PC", ApplySummary.Human(status));
        var blocked = new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true, Status: new BrokerStatus("1", "1", false, "desativada", "livre")), "");
        Assert.Equal("Aplicador respondeu · escrita não liberada: desativada", ApplySummary.Human(blocked));
        var plan = new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true, Results: [new ActionResultDto("a@1", "Pending")]), "");
        Assert.Equal("Simulação pronta: 1 ação conferida. Nada foi aplicado.", ApplySummary.Human(plan));
        Assert.DoesNotContain("LIBERADA", ApplySummary.Human(status), StringComparison.Ordinal);
    }
}
