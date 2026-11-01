using System.Xml.Linq;
using Nexus.Assistant;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Programs.Inventory;

namespace Nexus.Tests.Modules;

internal static class AssistantFakes
{
    public const string Catalog = """
    {
      "catalogVersion": "t", "schema": "nexus.catalog/1",
      "actions": [
        { "id": "t.user.low", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "User",
          "target": { "hive": "HKCU", "path": "Software\\T", "name": "A", "type": "DWord" }, "desiredValue": 1 },
        { "id": "t.user.low2", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "User",
          "target": { "hive": "HKCU", "path": "Software\\T", "name": "B", "type": "DWord" }, "desiredValue": 1 },
        { "id": "t.admin.low", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "Admin",
          "target": { "hive": "HKLM", "path": "SOFTWARE\\T", "name": "C", "type": "DWord" }, "desiredValue": 1 },
        { "id": "t.user.medium", "classification": "Conditional", "handlerKind": "RegistryValue", "privileges": "User",
          "target": { "hive": "HKCU", "path": "Software\\T", "name": "D", "type": "DWord" }, "desiredValue": 1 },
        { "id": "t.exp", "classification": "Experimental", "handlerKind": "RegistryValue", "privileges": "User",
          "target": { "hive": "HKCU", "path": "Software\\T", "name": "E", "type": "DWord" }, "desiredValue": 1 },
        { "id": "t.declared.low", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "User", "risk": "Low",
          "target": { "hive": "HKCU", "path": "Software\\T", "name": "{n}", "type": "DWord" }, "userParameters": { "n": "[A-Z]" }, "desiredValue": 1 },
        { "id": "t.ephemeral", "classification": "SupportedDocumented", "handlerKind": "SystemRestorePoint", "privileges": "Admin",
          "persistence": "Ephemeral", "target": { "kind": "manual" }, "desiredType": "Ephemeral", "desiredValue": "executado" },
        { "id": "t.irreversible", "classification": "Conditional", "handlerKind": "CleaningQuarantinePurge", "privileges": "User", "irreversible": true,
          "persistence": "Persistent", "target": { "batch": "{batch}" }, "userParameters": { "batch": "[0-9]+" }, "desiredType": "CleaningQuarantine", "desiredValue": "purged" }
      ]
    }
    """;

    public static AssistantCatalog Cat() => new(CatalogLoader.Parse(Catalog), RiskModel.ParseDeclared(Catalog).ToDictionary(x => x.Key, x => x.Risk));

    public static AssistantCatalog Real()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "catalog");
        return AssistantCatalog.Load(dir);
    }

    public static ProposedChange Change(AssistantCatalog c, string key, AssistantStepId step = AssistantStepId.Performance, string? value = null,
        IReadOnlyDictionary<string, string>? args = null, bool optIn = false) =>
        c.Change(step, new ActionRequest(key, value, args), key, "detalhe", optIn: optIn) ?? throw new InvalidOperationException(key);
}

/// <summary>Aplicador falso: registra os pedidos e responde com o que o teste programar.</summary>
internal sealed class FakeWriter : IAssistantWriter
{
    public List<(string Title, string Confirmation, IReadOnlyList<ActionRequest> Requests, BrokerLevel Level)> Applied { get; } = [];
    public List<(Guid Run, BrokerLevel Level)> Reverted { get; } = [];
    public Func<IReadOnlyList<ActionRequest>, BrokerLevel, WriteOutcome?> OnApply { get; set; } = (reqs, _) =>
        new WriteOutcome(true, Guid.NewGuid(), "ok", reqs.Select((r, i) => new ActionResultDto(r.ActionId, "Verified", Index: i)).ToList());
    public Func<Guid, WriteOutcome?> OnRevert { get; set; } = run => new WriteOutcome(true, run, "desfeito", [], FullyRestored: true);

    public Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level)
    {
        Applied.Add((title, confirmation, requests, level));
        return Task.FromResult(OnApply(requests, level));
    }

    public Task<WriteOutcome?> RevertAsync(Guid runId, BrokerLevel level)
    {
        Reverted.Add((runId, level));
        return Task.FromResult(OnRevert(runId));
    }
}

[Trait("Category", "Unit")]
public sealed class AssistantFlowTests
{
    [Fact]
    public void Steps_are_thirteen_in_order_with_report_last()
    {
        Assert.Equal(13, AssistantSteps.Count);
        Assert.Equal(AssistantStepId.Welcome, AssistantSteps.First);
        Assert.Equal(AssistantStepId.Report, AssistantSteps.Last);
        Assert.All(AssistantSteps.All.Select((s, i) => (s, i)), x => Assert.Equal(x.i + 1, x.s.Number));
        Assert.All(AssistantSteps.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Checks + s.Why + s.Title + s.ShortName)));
    }

    [Fact]
    public void Advance_skip_back_and_finish()
    {
        var state = new AssistantState();
        var flow = new AssistantFlow(state);
        Assert.False(flow.CanGoBack);
        Assert.Equal(AssistantStepId.MyPc, flow.Advance("ok"));
        Assert.Equal(AssistantStepId.Cleaning, flow.Skip("sem tempo"));
        Assert.Equal(StepStatus.Done, state.StatusOf(AssistantStepId.Welcome));
        Assert.Equal(StepStatus.Skipped, state.StatusOf(AssistantStepId.MyPc));
        Assert.Contains(state.Skipped, s => s.Step == AssistantStepId.MyPc && s.Why == "sem tempo");
        Assert.Equal(2, flow.Completed);

        Assert.Equal(AssistantStepId.MyPc, flow.Back());
        Assert.Equal(StepStatus.Skipped, state.StatusOf(AssistantStepId.MyPc)); // voltar não apaga o histórico
        Assert.Equal(AssistantStepId.Cleaning, flow.Advance("refeita"));
        Assert.Equal(StepStatus.Done, state.StatusOf(AssistantStepId.MyPc));

        while (!flow.IsLast) flow.Advance();
        Assert.False(state.Finished);
        flow.Advance("relatório");
        Assert.True(state.Finished);
        Assert.Equal(AssistantStepId.Report, state.Current);
    }

    [Fact]
    public void GoTo_only_reaches_visited_steps_or_the_first_pending()
    {
        var flow = new AssistantFlow(new AssistantState());
        flow.Advance();
        flow.Advance();
        Assert.False(flow.GoTo(AssistantStepId.Security)); // pendente à frente
        Assert.True(flow.GoTo(AssistantStepId.Welcome));
        Assert.True(flow.GoTo(AssistantStepId.Cleaning)); // primeira pendente
        Assert.False(flow.GoTo((AssistantStepId)99));
    }

    [Fact]
    public void Skipping_twice_does_not_duplicate_the_whole_step_record()
    {
        var state = new AssistantState();
        var flow = new AssistantFlow(state);
        flow.Skip("a");
        flow.Back();
        flow.Skip("b");
        Assert.Single(state.Skipped, s => s.Step == AssistantStepId.Welcome);
        Assert.Equal("b", state.Skipped.Single().Why);
    }
}

[Trait("Category", "Unit")]
public sealed class AssistantStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-assistente-" + Guid.NewGuid().ToString("N"));
    private string PathOf => System.IO.Path.Combine(_dir, "estado.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_is_new()
    {
        var (state, status) = new AssistantStateStore(PathOf).Load();
        Assert.Equal(LoadStatus.New, status);
        Assert.Equal(AssistantStepId.Welcome, state.Current);
    }

    [Fact]
    public void Save_and_resume_keeps_step_runs_changes_and_metrics()
    {
        var store = new AssistantStateStore(PathOf);
        var state = new AssistantState();
        var flow = new AssistantFlow(state);
        flow.Advance("ok", [new StepFact("Espaço", "10 GB", "good")]);
        flow.Skip("depois");
        var run = Guid.NewGuid();
        state.Runs.Add(new RunRecord(AssistantStepId.Welcome, run, BrokerLevel.Admin, DateTimeOffset.Now, 1));
        state.Changes.Add(new ChangeRecord(AssistantStepId.Welcome, "x", "a", "b", BrokerLevel.User, "baixo", true, ChangeOutcome.Applied, null, run));
        state.SetMetric("space", "Espaço", before: "10 GB");
        state.SetMetric("space", "Espaço", after: "12 GB");
        state.AddUserAction(new UserAction("Comprar RAM", "porque"));
        state.AddUserAction(new UserAction("Comprar RAM", "duplicada"));
        store.Save(state);

        var (loaded, status) = store.Load();
        Assert.Equal(LoadStatus.Resumed, status);
        Assert.Equal(AssistantStepId.Cleaning, loaded.Current);
        Assert.Equal(StepStatus.Skipped, loaded.StatusOf(AssistantStepId.MyPc));
        Assert.Equal("10 GB", loaded.Steps[AssistantStepId.Welcome].Facts![0].Value);
        Assert.Equal(run, loaded.Runs.Single().RunId);
        Assert.Equal(BrokerLevel.Admin, loaded.Runs.Single().Level);
        Assert.Equal("10 GB → 12 GB", loaded.Metrics["space"].Display);
        Assert.Single(loaded.UserActions);
        Assert.False(File.Exists(PathOf + ".tmp"));
    }

    [Theory]
    [InlineData("{ isto não é json")]
    [InlineData("""{ "Version": 99, "Current": "Welcome" }""")]
    [InlineData("""{ "Version": 1, "Current": "EtapaQueNaoExiste" }""")]
    [InlineData("""{ "Version": 1, "Current": 77 }""")]
    [InlineData("null")]
    public void Corrupt_state_starts_over_and_keeps_the_bad_copy(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf, content);
        var (state, status) = new AssistantStateStore(PathOf).Load();
        Assert.Equal(LoadStatus.Corrupt, status);
        Assert.Equal(AssistantStepId.Welcome, state.Current);
        Assert.Empty(state.Runs);
        Assert.False(File.Exists(PathOf));
        Assert.Single(Directory.GetFiles(_dir, "estado.json.corrompido-*"));
    }
}

[Trait("Category", "Unit")]
public sealed class AssistantSelectionTests
{
    [Fact]
    public void Level_risk_and_reversibility_come_from_the_catalog()
    {
        var c = AssistantFakes.Cat();
        var low = AssistantFakes.Change(c, "t.user.low@1");
        Assert.Equal(BrokerLevel.User, low.Level);
        Assert.Equal(ActionRisk.Low, low.Risk);
        Assert.True(low.Undoable);
        Assert.True(low.SelectedByDefault);

        var admin = AssistantFakes.Change(c, "t.admin.low@1");
        Assert.Equal(BrokerLevel.Admin, admin.Level);
        Assert.True(admin.SelectedByDefault);

        var eph = AssistantFakes.Change(c, "t.ephemeral@1");
        Assert.False(eph.Undoable);
        Assert.True(eph.SelectedByDefault);

        var declared = AssistantFakes.Change(c, "t.declared.low@1", args: new Dictionary<string, string> { ["n"] = "X" });
        Assert.Equal(ActionRisk.Low, declared.Risk); // parâmetro do usuário, mas o catálogo declara risco baixo
    }

    [Fact]
    public void Medium_experimental_and_opt_in_start_unchecked()
    {
        var c = AssistantFakes.Cat();
        var medium = AssistantFakes.Change(c, "t.user.medium@1");
        Assert.Equal(ActionRisk.Medium, medium.Risk);
        Assert.False(medium.SelectedByDefault);

        var exp = AssistantFakes.Change(c, "t.exp@1");
        Assert.True(exp.Experimental);
        Assert.Equal("experimental", exp.RiskText);
        Assert.False(exp.SelectedByDefault);

        var optIn = AssistantFakes.Change(c, "t.user.low@1", optIn: true);
        Assert.False(optIn.SelectedByDefault);
    }

    [Fact]
    public void Irreversible_actions_and_unknown_actions_are_never_proposed()
    {
        var c = AssistantFakes.Cat();
        Assert.Null(c.Change(AssistantStepId.Cleaning, new ActionRequest("t.irreversible@1", null, new Dictionary<string, string> { ["batch"] = "1" }), "x", "y"));
        Assert.Null(c.Change(AssistantStepId.Cleaning, new ActionRequest("nao.existe@1"), "x", "y"));
        Assert.Null(c.Change(AssistantStepId.Cleaning, new ActionRequest("t.user.low@1", ConfirmIrreversible: true), "x", "y"));
    }

    [Fact]
    public void Real_catalog_irreversible_actions_are_refused()
    {
        var c = AssistantFakes.Real();
        var irreversible = c.Document.Actions.Where(a => a.Definition.Irreversible).ToList();
        Assert.NotEmpty(irreversible);
        foreach (var e in irreversible)
            Assert.Null(c.Change(AssistantStepId.Cleaning, new ActionRequest(e.Definition.Key), "x", "y"));
    }

    [Fact]
    public void Sanitize_drops_duplicates_and_forbidden()
    {
        var c = AssistantFakes.Cat();
        var a = AssistantFakes.Change(c, "t.user.low@1");
        var forged = a with { Irreversible = true };
        var list = ChangeSelection.Sanitize([a, a with { Title = "repetida" }, forged]);
        Assert.Single(list);
        Assert.Same(a, list[0]);
    }

    [Fact]
    public void Real_catalog_has_every_action_the_planners_use()
    {
        var c = AssistantFakes.Real();
        foreach (var key in new[]
                 {
                     StepPlanners.RestorePointAction, StepPlanners.ScheduleScanAction, StepPlanners.ScheduleCleanAction, StepPlanners.DriverInstallAction,
                     Nexus.Lab.Gpu.GpuDiagnostics.DisplayModeAction, Nexus.Lab.Gpu.GpuDiagnostics.GameModeAction, Nexus.Lab.Gpu.GpuDiagnostics.HagsAction,
                     Nexus.Lab.Gpu.GpuDiagnostics.AppPreferenceAction,
                 })
            Assert.True(c.Entry(key) is not null, key);
        var restore = AssistantFakes.Change(c, StepPlanners.RestorePointAction, AssistantStepId.Welcome);
        Assert.Equal(BrokerLevel.Admin, restore.Level);
        Assert.False(restore.Undoable);
        Assert.True(restore.SelectedByDefault);
    }
}

[Trait("Category", "Unit")]
public sealed class AssistantGroupingTests
{
    [Fact]
    public void User_batch_comes_first_then_admin_one_prompt_each()
    {
        var c = AssistantFakes.Cat();
        var batches = ApplyGrouping.Group(AssistantStepId.Privacy,
            [AssistantFakes.Change(c, "t.admin.low@1"), AssistantFakes.Change(c, "t.user.low@1"), AssistantFakes.Change(c, "t.user.low2@1")]);
        Assert.Equal(2, batches.Count);
        Assert.Equal(BrokerLevel.User, batches[0].Level);
        Assert.Equal(2, batches[0].Requests.Count);
        Assert.Equal(BrokerLevel.Admin, batches[1].Level);
        Assert.Contains("administrador", batches[1].Confirmation, StringComparison.Ordinal);
        Assert.DoesNotContain("permissão de administrador", batches[0].Confirmation, StringComparison.Ordinal);
        Assert.Contains("Privacidade", batches[0].Title, StringComparison.Ordinal);
        Assert.Contains("Desfazer tudo", batches[0].Confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public void Batches_never_exceed_the_broker_limit()
    {
        var c = AssistantFakes.Cat();
        var many = Enumerable.Range(0, 70).Select(i => AssistantFakes.Change(c, "t.declared.low@1", args: new Dictionary<string, string> { ["n"] = ((char)('A' + i % 26)).ToString() + i }))
            .ToList();
        var batches = ApplyGrouping.Group(AssistantStepId.Cleaning, many);
        Assert.Equal(2, batches.Count);
        Assert.Equal(64, batches[0].Requests.Count);
        Assert.Equal(6, batches[1].Requests.Count);
        Assert.Contains("Parte 1 de 2", batches[0].Confirmation, StringComparison.Ordinal);
    }

    [Fact]
    public void Grouping_drops_forbidden_changes_even_if_selected()
    {
        var c = AssistantFakes.Cat();
        var ok = AssistantFakes.Change(c, "t.user.low@1");
        var batches = ApplyGrouping.Group(AssistantStepId.Cleaning, [ok, ok with { Request = ok.Request with { ConfirmIrreversible = true }, Id = "x" }]);
        Assert.Single(batches);
        Assert.Single(batches[0].Requests);
        Assert.Null(batches[0].Requests[0].ConfirmIrreversible);
    }
}

[Trait("Category", "Unit")]
public sealed class AssistantRunnerTests
{
    [Fact]
    public async Task Apply_records_runs_and_outcomes_per_change()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        var writer = new FakeWriter
        {
            OnApply = (reqs, level) => new WriteOutcome(true, Guid.NewGuid(), "ok", reqs.Select((r, i) =>
                new ActionResultDto(r.ActionId, r.ActionId == "t.user.low2@1" ? "AlreadyConfigured" : r.ActionId == "t.admin.low@1" ? "Blocked" : "Verified",
                    r.ActionId == "t.admin.low@1" ? "política de grupo" : null, Index: i)).ToList()),
        };
        var r = await AssistantRunner.ApplyAsync(state, AssistantStepId.Privacy,
            [AssistantFakes.Change(c, "t.user.low@1"), AssistantFakes.Change(c, "t.user.low2@1"), AssistantFakes.Change(c, "t.admin.low@1")], writer);
        Assert.Equal(1, r.Applied);
        Assert.Equal(1, r.Already);
        Assert.Equal(1, r.Refused);
        Assert.Equal([BrokerLevel.User, BrokerLevel.Admin], writer.Applied.Select(a => a.Level));
        Assert.Single(state.Runs); // o lote do administrador não mudou nada: nada a desfazer
        Assert.Contains(state.Skipped, s => s.Why.Contains("política de grupo", StringComparison.Ordinal));
        Assert.Contains(state.Changes, x => x.Outcome == ChangeOutcome.Applied && x.Undoable);
    }

    [Fact]
    public async Task Broker_refusal_does_not_stop_the_next_batch()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        var writer = new FakeWriter();
        var defaultApply = writer.OnApply;
        writer.OnApply = (reqs, level) => level == BrokerLevel.User ? new WriteOutcome(false, null, "aplicador recusou o pedido", []) : defaultApply(reqs, level);
        var r = await AssistantRunner.ApplyAsync(state, AssistantStepId.Privacy, [AssistantFakes.Change(c, "t.user.low@1"), AssistantFakes.Change(c, "t.admin.low@1")], writer);
        Assert.Equal(2, writer.Applied.Count);
        Assert.Equal(1, r.Refused);
        Assert.Equal(1, r.Applied);
        Assert.Contains(state.Skipped, s => s.Why.Contains("aplicador recusou", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_stops_the_step_and_marks_the_rest_canceled()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        var writer = new FakeWriter { OnApply = (_, _) => null };
        var r = await AssistantRunner.ApplyAsync(state, AssistantStepId.Privacy, [AssistantFakes.Change(c, "t.user.low@1"), AssistantFakes.Change(c, "t.admin.low@1")], writer);
        Assert.Single(writer.Applied); // não pergunta o administrador depois de cancelar
        Assert.Equal(2, r.Canceled);
        Assert.Empty(state.Runs);
        Assert.False(r.AnyWritten);
    }

    [Fact]
    public async Task Ephemeral_only_run_is_not_offered_for_undo()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        await AssistantRunner.ApplyAsync(state, AssistantStepId.Welcome, [AssistantFakes.Change(c, "t.ephemeral@1", AssistantStepId.Welcome)], new FakeWriter());
        Assert.Empty(state.Runs);
        Assert.False(state.HasUndoableRuns);
        Assert.Contains(state.Changes, x => x.Outcome == ChangeOutcome.Applied && !x.Undoable);
    }

    [Fact]
    public async Task Undo_all_goes_in_reverse_order_and_stops_on_cancel()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        var writer = new FakeWriter();
        await AssistantRunner.ApplyAsync(state, AssistantStepId.Cleaning, [AssistantFakes.Change(c, "t.user.low@1", AssistantStepId.Cleaning)], writer);
        await AssistantRunner.ApplyAsync(state, AssistantStepId.Privacy, [AssistantFakes.Change(c, "t.user.low2@1", AssistantStepId.Privacy), AssistantFakes.Change(c, "t.admin.low@1", AssistantStepId.Privacy)], writer);
        Assert.Equal(3, state.Runs.Count);
        var expected = state.Runs.Select(r => (r.RunId, r.Level)).Reverse().ToList();

        var lines = await AssistantRunner.UndoAllAsync(state, writer);
        Assert.Equal(expected, writer.Reverted);
        Assert.Equal(3, lines.Count);
        Assert.All(state.Runs, r => Assert.True(r.Reverted));
        Assert.False(state.HasUndoableRuns);

        // De novo: nada a desfazer.
        writer.Reverted.Clear();
        await AssistantRunner.UndoAllAsync(state, writer);
        Assert.Empty(writer.Reverted);
    }

    [Fact]
    public async Task Undo_all_cancel_keeps_the_rest_pending()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        var writer = new FakeWriter();
        await AssistantRunner.ApplyAsync(state, AssistantStepId.Cleaning, [AssistantFakes.Change(c, "t.user.low@1")], writer);
        await AssistantRunner.ApplyAsync(state, AssistantStepId.Privacy, [AssistantFakes.Change(c, "t.user.low2@1")], writer);
        var calls = 0;
        writer.OnRevert = run => ++calls == 1 ? new WriteOutcome(true, run, "ok", [], true) : null;
        await AssistantRunner.UndoAllAsync(state, writer);
        Assert.True(state.Runs[1].Reverted);
        Assert.False(state.Runs[0].Reverted);
        Assert.True(state.HasUndoableRuns);
    }

    [Fact]
    public void Results_match_by_index_then_by_key()
    {
        var c = AssistantFakes.Cat();
        var a = AssistantFakes.Change(c, "t.declared.low@1", args: new Dictionary<string, string> { ["n"] = "A" });
        var b = AssistantFakes.Change(c, "t.declared.low@1", args: new Dictionary<string, string> { ["n"] = "B" });
        var m = AssistantRunner.Match([a, b], [new ActionResultDto("t.declared.low@1", "Verified", "b", Index: 1), new ActionResultDto("t.declared.low@1", "Failed", "a", Index: 0)]);
        Assert.Equal("a", m[0]!.Detail);
        Assert.Equal("b", m[1]!.Detail);
        var byKey = AssistantRunner.Match([a, b], [new ActionResultDto("t.declared.low@1", "Verified", "1"), new ActionResultDto("t.declared.low@1", "Verified", "2")]);
        Assert.Equal("1", byKey[0]!.Detail);
        Assert.Equal("2", byKey[1]!.Detail);
    }

    [Fact]
    public void Unselected_changes_are_recorded_with_the_reason()
    {
        var c = AssistantFakes.Cat();
        var state = new AssistantState();
        AssistantRunner.RecordUnselected(state, [AssistantFakes.Change(c, "t.exp@1"), AssistantFakes.Change(c, "t.user.medium@1"), AssistantFakes.Change(c, "t.user.low@1", optIn: true)]);
        Assert.Contains(state.Skipped, s => s.Why.StartsWith("Experimental", StringComparison.Ordinal));
        Assert.Contains(state.Skipped, s => s.Why.StartsWith("Risco médio", StringComparison.Ordinal));
        Assert.Contains(state.Skipped, s => s.Why.StartsWith("Opcional", StringComparison.Ordinal));
    }
}

[Trait("Category", "Unit")]
public sealed class AssistantReportTests
{
    private static (AssistantState State, PcSummary Pc) Synthetic()
    {
        var state = new AssistantState();
        var flow = new AssistantFlow(state);
        flow.Advance("Ponto de restauração criado.");
        flow.Advance("Ryzen 5 5600H; RAM em single channel. Perfil salvo em C:\\Users\\Matheus\\AppData\\Local\\NEXUS.");
        flow.Skip("Pulada por você.");
        state.Changes.Add(new ChangeRecord(AssistantStepId.Privacy, "Desligar anúncios <personalizados> & sugestões", "1", "0", BrokerLevel.User, "baixo", true, ChangeOutcome.Applied, null, Guid.NewGuid()));
        state.Changes.Add(new ChangeRecord(AssistantStepId.Startup, "Não abrir com o Windows: Discord (C:\\Users\\Matheus\\AppData\\Local\\Discord)", "abre", "não abre", BrokerLevel.User, "médio", true, ChangeOutcome.Applied, null, Guid.NewGuid()));
        state.Changes.Add(new ChangeRecord(AssistantStepId.Welcome, "Criar ponto de restauração", null, null, BrokerLevel.Admin, "baixo", false, ChangeOutcome.Applied, null, null));
        state.Changes.Add(new ChangeRecord(AssistantStepId.Privacy, "Recusada", null, null, BrokerLevel.Admin, "baixo", false, ChangeOutcome.Refused, "x", null));
        state.Skipped.Add(new SkipRecord(AssistantStepId.Drivers, "Driver do PC-DO-MATHEUS", "Contato matheus@example.com no IP 192.168.0.10"));
        state.Runs.Add(new RunRecord(AssistantStepId.Privacy, Guid.NewGuid(), BrokerLevel.User, DateTimeOffset.Now, 1));
        state.AddUserAction(new UserAction("Comprar um 2º módulo de RAM igual ao atual", "Single channel.", "https://www.techspot.com/article/3066-single-stick-vs-dual-channel-ram/"));
        state.AddUserAction(new UserAction("Link perigoso", "não deve virar link", "javascript:alert(1)"));
        state.SetMetric("space", "Espaço livre", before: "40,0 GB", after: "49,6 GB");
        state.SetMetric("privacy", "Privacidade", before: "8 de 30", after: "26 de 30");
        state.SetMetric("gpu", "Driver", after: "31.0.15.3623");
        var pc = new PcSummary("Notebook gamer", "AMD Ryzen 5 5600H (6 núcleos, 12 threads)", "NVIDIA GeForce RTX 3050 Laptop GPU", "8 GB em 1 módulo(s)", "Windows 11 (build 26200)", "NVMe (SSD)");
        return (state, pc);
    }

    [Fact]
    public void Report_html_is_well_formed_and_has_every_section()
    {
        var (state, pc) = Synthetic();
        var report = AssistantReport.From(state, pc, new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero), new ReportRedactor("Matheus", "PC-DO-MATHEUS", @"C:\Users\Matheus"));
        var html = report.ToHtml();
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        var doc = XDocument.Parse(html["<!DOCTYPE html>".Length..]); // XHTML bem formado: toda tag fecha, todo texto escapado
        Assert.Equal("html", doc.Root!.Name.LocalName);
        foreach (var section in new[] { "Números", "Próximo passo", "Seu PC", "O que só você pode fazer", "O que foi verificado", "O que foi alterado", "O que ficou de fora e por quê" })
            Assert.Contains(section, html, StringComparison.Ordinal);
        Assert.Contains("40,0 GB", html, StringComparison.Ordinal);
        Assert.Contains("49,6 GB", html, StringComparison.Ordinal);
        Assert.Contains("@media print", html, StringComparison.Ordinal);
        Assert.Contains("Ryzen 5 5600H", html, StringComparison.Ordinal);
        Assert.Contains("&lt;personalizados&gt; &amp; sugestões", html, StringComparison.Ordinal);
        Assert.Contains("31.0.15.3623", html, StringComparison.Ordinal); // versão não é confundida com IP
        Assert.Contains("href=\"https://www.techspot.com/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, report.AppliedCount);
        Assert.DoesNotContain(report.Changed, c => c.Outcome == ChangeOutcome.Refused);
        Assert.True(report.CanUndo);
        Assert.StartsWith("Faça \"Comprar um 2º módulo", report.NextStep, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_has_no_personal_data()
    {
        var (state, pc) = Synthetic();
        var html = AssistantReport.From(state, pc, DateTimeOffset.Now, new ReportRedactor("Matheus", "PC-DO-MATHEUS", @"C:\Users\Matheus")).ToHtml();
        Assert.DoesNotContain("Matheus", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PC-DO-MATHEUS", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@example.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.0.10", html, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", html, StringComparison.Ordinal);
        Assert.Contains("&lt;computador&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_handles_any_profile_path_even_without_the_user_name()
    {
        var r = ReportRedactor.None;
        Assert.Equal(@"%USERPROFILE%\AppData\x.exe", r.Clean(@"C:\Users\Fulano\AppData\x.exe"));
        Assert.Equal("versão 566.36 e 10.0.26200.1", r.Clean("versão 566.36 e 10.0.26200.1"));
        Assert.Equal("<IP>", r.Clean("10.0.0.1"));
        Assert.Null(r.Clean(null));
    }

    [Fact]
    public void Report_saves_with_the_expected_name()
    {
        var (state, pc) = Synthetic();
        var dir = Path.Combine(Path.GetTempPath(), "nexus-rel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = AssistantReport.From(state, pc, new DateTimeOffset(2026, 10, 31, 22, 5, 0, TimeSpan.Zero)).Save(dir);
            Assert.Equal("assistente-2026-10-31-2205.html", Path.GetFileName(path));
            Assert.Contains("Relatório da otimização", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Next_step_without_anything_applied_says_so()
    {
        Assert.StartsWith("Nada foi alterado", AssistantReport.NextStepFor(new AssistantState()), StringComparison.Ordinal);
        var s = new AssistantState();
        s.Changes.Add(new ChangeRecord(AssistantStepId.Memory, "x", null, null, BrokerLevel.Admin, "baixo", true, ChangeOutcome.Applied, null, null));
        Assert.StartsWith("Reinicie", AssistantReport.NextStepFor(s), StringComparison.Ordinal);
    }
}

[Trait("Category", "Unit")]
public sealed class AssistantPlannerTests
{
    [Fact]
    public void Performance_lists_the_rest_of_the_catalog_as_advanced_unchecked_and_never_session_or_with_bad_values()
    {
        // O usuário perguntou por que os ajustes da página Aplicar não estavam no assistente: entram como "avançado", desmarcados.
        var c = AssistantFakes.Real();
        var traits = new PcTraits(ChassisType: 10, HasSystemBattery: true, OnAcPower: true, BatteryPercent: 90);
        var set = AutoFakes.Recommend(AutoFakes.Facts(), traits, c.Document);
        var p = new Dictionary<string, string> { ["activeScheme"] = "381b4222-f694-41f0-9685-ff5bb260df2e" };
        var scan = StepPlanners.Performance(c, set, p);
        var advanced = scan.Changes.Where(x => x.Title.StartsWith("Avançado: ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(advanced);
        foreach (var x in advanced)
        {
            var e = c.Entry(x.Request.ActionId);
            Assert.NotNull(e);
            Assert.True(x.OptIn && !x.SelectedByDefault, x.Request.ActionId);
            Assert.NotEqual(PersistenceKind.Session, e!.Definition.Persistence);
            Assert.Null(x.Request.Value); // valor fixo no catálogo
        }
        Assert.Equal(scan.Changes.Count, scan.Changes.Select(x => x.Request.ActionId).Distinct().Count());
    }

    [Fact]
    public void Welcome_proposes_the_restore_point_unless_a_recovery_is_pending()
    {
        var c = AssistantFakes.Real();
        var traits = new PcTraits(ChassisType: 10, HasSystemBattery: true, OnAcPower: false, BatteryPercent: 40);
        var ok = StepPlanners.Welcome(c, traits, 30L << 30, 476L << 30, 0);
        var rp = Assert.Single(ok.Changes);
        Assert.Equal(StepPlanners.RestorePointAction, rp.Request.ActionId);
        Assert.True(rp.SelectedByDefault);
        Assert.Contains(ok.Facts, f => f.Label == "Energia" && f.Tone == "warn");
        Assert.True(ok.Metrics.ContainsKey("space"));

        var pending = StepPlanners.Welcome(c, traits, 30L << 30, 476L << 30, 2);
        Assert.Empty(pending.Changes);
        Assert.Contains("Recuperar", pending.Recommendation, StringComparison.Ordinal);
        Assert.Contains(pending.Facts, f => f.Tone == "bad");
    }

    [Fact]
    public void Schedules_weekly_scan_is_checked_and_scheduled_cleaning_is_opt_in()
    {
        var c = AssistantFakes.Real();
        var off = (NexusTaskKind k) => new NexusTaskStatus(k, AutomationScheduledTaskHandler.Off, null, null, false, null);
        var scan = StepPlanners.Schedules(c, off(NexusTaskKind.Scan), off(NexusTaskKind.Clean));
        Assert.Equal(2, scan.Changes.Count);
        var weekly = scan.Changes.Single(x => x.Request.ActionId == StepPlanners.ScheduleScanAction);
        Assert.True(weekly.SelectedByDefault);
        Assert.Equal("WEEKLY SUN 19:00", weekly.Request.Arguments!["schedule"]);
        Assert.Equal("battery-game", weekly.Request.Arguments["conditions"]);
        Assert.Matches(TaskSchedule.Pattern, weekly.Request.Arguments["schedule"]);
        var clean = scan.Changes.Single(x => x.Request.ActionId == StepPlanners.ScheduleCleanAction);
        Assert.False(clean.SelectedByDefault);
        Assert.True(clean.OptIn);

        var foreign = new NexusTaskStatus(NexusTaskKind.Scan, AutomationScheduledTaskHandler.Foreign, null, null, false, null);
        Assert.DoesNotContain(StepPlanners.Schedules(c, foreign, off(NexusTaskKind.Clean)).Changes, x => x.Request.ActionId == StepPlanners.ScheduleScanAction);
    }

    [Fact]
    public void Program_candidates_are_big_or_old_never_system_and_largest_first()
    {
        const long Mb = 1024L * 1024;
        static InstalledProgram P(string id, long? mb, DateOnly? date, bool noRemove = false) =>
            new(id, id, "Editor", "1", mb * Mb, date, null, "u.exe", null, null, InstallScope.Machine64, id, false, noRemove);
        var today = new DateOnly(2026, 10, 31);
        var list = StepPlanners.ProgramCandidates(
        [
            P("jogo", 60_000, today), P("pequeno", 50, new DateOnly(2020, 1, 1)), P("velho", 400, new DateOnly(2024, 1, 1)),
            P("novo-medio", 400, today.AddDays(-10)), P("sistema", 5_000, null, noRemove: true), P("sem-tamanho", null, null),
        ], today);
        Assert.Equal(["jogo", "velho"], list.Select(x => x.Id));
    }

    [Fact]
    public async Task Plan_filter_keeps_pending_and_admin_needing_elevation()
    {
        var c = AssistantFakes.Cat();
        var changes = new[] { AssistantFakes.Change(c, "t.user.low@1"), AssistantFakes.Change(c, "t.user.low2@1"), AssistantFakes.Change(c, "t.admin.low@1"), AssistantFakes.Change(c, "t.user.medium@1") };
        Task<BrokerCallResult> Plan(IReadOnlyList<ActionRequest> reqs) => Task.FromResult(new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true, Results:
        [
            new ActionResultDto("t.user.low@1", "Pending", Observed: "0", Desired: "1", Index: 0),
            new ActionResultDto("t.user.low2@1", "AlreadyConfigured", Index: 1),
            new ActionResultDto("t.admin.low@1", "Blocked", OneClickPlanner.NeedsElevation, Index: 2),
            new ActionResultDto("t.user.medium@1", "NotApplicable", "build antigo", Index: 3),
        ]), "ok"));
        var (keep, dropped, problem) = await StepPlanners.PlanAsync(changes, Plan);
        Assert.Null(problem);
        Assert.Equal(["t.user.low@1", "t.admin.low@1"], keep.Select(k => k.Request.ActionId));
        Assert.Equal("0", keep[0].Before);
        Assert.Equal("1", keep[0].After);
        Assert.Equal(2, dropped.Count);
        Assert.Contains(dropped, d => d.Why.Contains("build antigo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_filter_without_broker_keeps_everything_and_reports_the_problem()
    {
        var c = AssistantFakes.Cat();
        var changes = new[] { AssistantFakes.Change(c, "t.user.low@1") };
        var (keep, dropped, problem) = await StepPlanners.PlanAsync(changes, _ => Task.FromResult(new BrokerCallResult(false, null, "sem aplicador")));
        Assert.Single(keep);
        Assert.Empty(dropped);
        Assert.Equal("sem aplicador", problem);
    }

    [Fact]
    public void Measurement_step_never_writes_and_explains_presentmon()
    {
        var s = StepPlanners.Measurement(presentMonFound: false, autoMeasure: false, overlayOnGameStart: false);
        Assert.Empty(s.Changes);
        Assert.Contains(s.Notes, n => n.Contains("PresentMon", StringComparison.Ordinal));
    }
}
