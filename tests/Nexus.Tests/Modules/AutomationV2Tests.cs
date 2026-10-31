using System.Xml;
using Nexus.Automation;
using Nexus.Automation.Notifications;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>Agendador falso para as tarefas genéricas: guarda o XML por nome e conta as escritas.</summary>
internal sealed class FakeTaskScheduler : ITaskSchedulerBackend
{
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Writes { get; private set; }
    public string? QueryXml(string taskName) => Tasks.GetValueOrDefault(taskName);
    public void CreateOrReplace(string taskName, string xml)
    {
        Writes++;
        using (var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit })) while (r.Read()) { }
        Tasks[taskName] = xml;
    }
    public void Delete(string taskName) { Writes++; Tasks.Remove(taskName); }
}

[Trait("Category", "Unit")]
public sealed class AutomationTaskScheduleTests
{
    private const string SafeExe = @"C:\Jogos & Apps\NEXUS d'Ouro\nexus.exe";
    private static readonly DateTime Now = new(2026, 10, 31, 15, 0, 0); // sábado

    [Theory]
    [InlineData("DAILY 03:30", true)]
    [InlineData("WEEKLY SUN 19:00", true)]
    [InlineData("MONTHLY 28 23:59", true)]
    [InlineData("MONTHLY 1 00:00", true)]
    [InlineData("MONTHLY 29 10:00", false)]
    [InlineData("MONTHLY 0 10:00", false)]
    [InlineData("WEEKLY sun 19:00", false)]
    [InlineData("DAILY 24:00", false)]
    [InlineData("DAILY 3:30", false)]
    [InlineData("DAILY 03:30\n", false)]
    [InlineData("WEEKLY SUN 19:00 & calc", false)]
    [InlineData("", false)]
    public void Schedule_format(string text, bool valid)
    {
        var s = TaskSchedule.TryParse(text);
        Assert.Equal(valid, s is not null);
        if (s is not null) Assert.Equal(text, s.ToString());
        Assert.Equal(valid, System.Text.RegularExpressions.Regex.IsMatch(text, @"\A(?:" + TaskSchedule.Pattern + @")\z"));
    }

    [Fact]
    public void Next_occurrence_for_each_frequency()
    {
        Assert.Equal(new DateTime(2026, 11, 1, 3, 30, 0), TaskSchedule.Daily(3, 30).NextAfter(Now));
        Assert.Equal(new DateTime(2026, 10, 31, 19, 0, 0), TaskSchedule.Daily(19, 0).NextAfter(Now));
        Assert.Equal(new DateTime(2026, 11, 1, 19, 0, 0), TaskSchedule.Weekly(DayOfWeek.Sunday, 19, 0).NextAfter(Now));
        Assert.Equal(new DateTime(2026, 11, 7, 12, 0, 0), TaskSchedule.Weekly(DayOfWeek.Saturday, 12, 0).NextAfter(Now));
        Assert.Equal(new DateTime(2026, 11, 15, 10, 0, 0), TaskSchedule.Monthly(15, 10, 0).NextAfter(Now));
        Assert.Equal("todo dia 15 do mês às 10:00", TaskSchedule.Monthly(15, 10, 0).Describe());
        Assert.Equal("toda segunda às 08:05", TaskSchedule.Weekly(DayOfWeek.Monday, 8, 5).Describe());
    }

    [Theory]
    [InlineData("none", false, false)]
    [InlineData("battery", true, false)]
    [InlineData("game", false, true)]
    [InlineData("battery-game", true, true)]
    public void Conditions_round_trip(string text, bool battery, bool game)
    {
        var c = TaskConditions.TryParse(text)!;
        Assert.Equal((battery, game), (c.SkipOnBattery, c.SkipWhenGaming));
        Assert.Equal(text, c.ToString());
        Assert.Null(TaskConditions.TryParse(text + " "));
        Assert.Equal(c, NexusTaskKind.Clean.ConditionsFromArguments(NexusTaskKind.Clean.ArgumentsFor(c)));
    }

    public static TheoryData<string> Schedules => new() { "DAILY 03:30", "WEEKLY WED 20:15", "MONTHLY 12 07:45" };

    [Theory]
    [MemberData(nameof(Schedules))]
    public void Xml_round_trips_every_frequency_with_hostile_path(string text)
    {
        var schedule = TaskSchedule.TryParse(text)!;
        foreach (var kind in NexusTaskKind.All)
        foreach (var conditions in TaskConditions.All)
        {
            var xml = NexusTaskXml.Build(kind, SafeExe, schedule, conditions, Now);
            Assert.Contains("C:\\Jogos &amp; Apps\\NEXUS d'Ouro\\nexus.exe", xml, StringComparison.Ordinal);
            var info = NexusTaskXml.Inspect(xml, kind);
            Assert.True(info.Ours);
            Assert.Equal(SafeExe, info.Command);
            Assert.Equal(kind.ArgumentsFor(conditions), info.Arguments);
            Assert.Equal(schedule, info.Schedule);
            Assert.Equal(conditions, info.Conditions);
            Assert.Equal(conditions.SkipOnBattery, info.DisallowOnBatteries);
            Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml, StringComparison.Ordinal);
            Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Monthly_xml_lists_all_months_and_one_day()
    {
        var xml = NexusTaskXml.Build(NexusTaskKind.Clean, SafeExe, TaskSchedule.Monthly(5, 9, 0), TaskConditions.Both, Now);
        Assert.Contains("<Day>5</Day>", xml, StringComparison.Ordinal);
        foreach (var m in new[] { "January", "June", "December" }) Assert.Contains($"<{m} />", xml, StringComparison.Ordinal);
        Assert.Contains("<StartBoundary>2026-11-05T09:00:00</StartBoundary>", xml, StringComparison.Ordinal);
        Assert.Contains("<Arguments>clean --safe --scheduled --quiet --skip-on-battery --skip-when-gaming</Arguments>", xml, StringComparison.Ordinal);
        Assert.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\a""b\nexus.exe")]
    [InlineData(@"C:\x</Command><Command>calc.exe\nexus.exe")]
    [InlineData(@"C:\x\nexus.exe"" & calc & """)]
    [InlineData(@"\\servidor\share\nexus.exe")]
    [InlineData(@"C:\%APPDATA%\nexus.exe")]
    [InlineData(@"C:\x\..\Windows\nexus.exe")]
    [InlineData(@"C:\x\calc.exe")]
    [InlineData("C:\\x\n\\nexus.exe")]
    [InlineData("nexus.exe")]
    public void Unsafe_paths_never_reach_the_xml(string exe)
    {
        Assert.Throws<ArgumentException>(() => NexusTaskXml.Build(NexusTaskKind.Clean, exe, TaskSchedule.Daily(1, 0), TaskConditions.None, Now));
    }

    [Fact]
    public void Inspect_is_strict_about_kind_dtd_and_extra_actions()
    {
        var scanXml = NexusTaskXml.Build(NexusTaskKind.Scan, SafeExe, TaskSchedule.Daily(1, 0), TaskConditions.None, Now);
        Assert.False(NexusTaskXml.Inspect(scanXml, NexusTaskKind.Clean).Ours); // marcador de outro tipo
        Assert.Throws<XmlException>(() => NexusTaskXml.Inspect("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/x\">]><Task/>", NexusTaskKind.Scan));
        var twoActions = scanXml.Replace("</Exec>", "</Exec><Exec><Command>C:\\x\\calc.exe</Command></Exec>", StringComparison.Ordinal);
        Assert.False(NexusTaskXml.Inspect(twoActions, NexusTaskKind.Scan).Ours);
        var otherArgs = scanXml.Replace(NexusTaskKind.Scan.Verb, "apply svc.x --yes", StringComparison.Ordinal);
        Assert.Null(NexusTaskXml.Inspect(otherArgs, NexusTaskKind.Scan).Conditions);
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationScheduledTaskHandlerTests
{
    private const string SafeExe = @"C:\Program Files\NEXUS\nexus.exe";

    private static ActionInstance Action(NexusTaskKind kind, string desired, string schedule = "WEEKLY SUN 19:00", string conditions = "battery-game", string? taskName = null) => new(
        new ActionDefinition(kind.ActionId, 1, "t", Classification.SupportedDocumented, AutomationScheduledTaskHandler.Kind, "", [], "",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None, AllowedValues: ["ON", "OFF"]),
        new Dictionary<string, string> { ["task"] = kind.Id, ["taskName"] = taskName ?? kind.TaskName, ["schedule"] = schedule, ["conditions"] = conditions },
        StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, desired));

    private static (FakeTaskScheduler Fake, ActionExecutor Exec, DesiredStateResolver Resolver, InMemoryJournal Journal) Engine(string? exe = SafeExe, bool otherAccount = false)
    {
        var fake = new FakeTaskScheduler();
        var handlers = new HandlerRegistry().Add(new AutomationScheduledTaskHandler(fake, () => exe, otherAccount));
        var journal = new InMemoryJournal();
        return (fake, new ActionExecutor(handlers, journal), new DesiredStateResolver(handlers), journal);
    }

    [Fact]
    public void Turning_on_the_safe_cleaning_creates_the_task_and_revert_removes_it()
    {
        var (fake, exec, resolver, journal) = Engine();
        var run = journal.BeginRun("t", "1");
        var r = exec.Execute(run, resolver.Resolve([Action(NexusTaskKind.Clean, "ON")], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        var info = NexusTaskXml.Inspect(fake.Tasks[NexusTaskKind.Clean.TaskName], NexusTaskKind.Clean);
        Assert.True(info.Ours);
        Assert.Equal("clean --safe --scheduled --quiet --skip-on-battery --skip-when-gaming", info.Arguments);
        Assert.Equal("OFF", journal.Entries(run).Single().Original!.Value);
        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Empty(fake.Tasks);
    }

    [Fact]
    public void Same_schedule_is_already_configured_and_a_new_one_restores_the_exact_previous_xml()
    {
        var (fake, exec, resolver, journal) = Engine();
        var previous = NexusTaskXml.Build(NexusTaskKind.Scan, SafeExe, TaskSchedule.Monthly(3, 8, 0), TaskConditions.None, new DateTime(2026, 1, 1));
        fake.Tasks[NexusTaskKind.Scan.TaskName] = previous;
        Assert.Equal(PlanStatus.AlreadyConfigured, resolver.Resolve([Action(NexusTaskKind.Scan, "ON", "MONTHLY 3 08:00", "none")], T.Ctx).Items.Single().Status);

        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Action(NexusTaskKind.Scan, "ON", "DAILY 22:00", "game")], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal(TaskSchedule.Daily(22, 0), NexusTaskXml.Inspect(fake.Tasks[NexusTaskKind.Scan.TaskName], NexusTaskKind.Scan).Schedule);
        Assert.Equal("DIFFERENT", journal.Entries(run).Single().Original!.Value);
        exec.RevertRun(run);
        Assert.Equal(previous, fake.Tasks[NexusTaskKind.Scan.TaskName]);
    }

    [Fact]
    public void Turning_off_removes_and_revert_recreates()
    {
        var (fake, exec, resolver, journal) = Engine();
        var previous = NexusTaskXml.Build(NexusTaskKind.Clean, SafeExe, TaskSchedule.Weekly(DayOfWeek.Friday, 21, 0), TaskConditions.Both, new DateTime(2026, 1, 1));
        fake.Tasks[NexusTaskKind.Clean.TaskName] = previous;
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Action(NexusTaskKind.Clean, "OFF")], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Empty(fake.Tasks);
        exec.RevertRun(run);
        Assert.Equal(previous, fake.Tasks[NexusTaskKind.Clean.TaskName]);
    }

    [Fact]
    public void Foreign_task_other_account_missing_exe_and_bad_targets_are_refused()
    {
        var (fake, _, resolver, _) = Engine();
        const string foreign = """<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><Exec><Command>C:\outro.exe</Command></Exec></Actions></Task>""";
        fake.Tasks[NexusTaskKind.Clean.TaskName] = foreign;
        Assert.Equal(PlanStatus.Blocked, resolver.Resolve([Action(NexusTaskKind.Clean, "ON")], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.Blocked, resolver.Resolve([Action(NexusTaskKind.Clean, "OFF")], T.Ctx).Items.Single().Status);
        Assert.Equal(0, fake.Writes);
        Assert.Equal(foreign, fake.Tasks[NexusTaskKind.Clean.TaskName]);

        Assert.Equal(PlanStatus.Blocked, Engine(otherAccount: true).Resolver.Resolve([Action(NexusTaskKind.Scan, "ON")], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.NotApplicable, Engine(exe: null).Resolver.Resolve([Action(NexusTaskKind.Scan, "ON")], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.Blocked, Engine(exe: @"C:\%TEMP%\nexus.exe").Resolver.Resolve([Action(NexusTaskKind.Scan, "ON")], T.Ctx).Items.Single().Status);
        var (_, _, r2, _) = Engine();
        Assert.Equal(PlanStatus.Blocked, r2.Resolve([Action(NexusTaskKind.Scan, "ON", taskName: "Outra")], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.Blocked, r2.Resolve([Action(NexusTaskKind.Scan, "ON", schedule: "WEEKLY SUN 19:00 & calc")], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.Blocked, r2.Resolve([Action(NexusTaskKind.Scan, "ON", conditions: "sempre")], T.Ctx).Items.Single().Status);
    }

    [Fact]
    public void Task_in_another_folder_is_different_not_on()
    {
        var fake = new FakeTaskScheduler();
        fake.Tasks[NexusTaskKind.Scan.TaskName] = NexusTaskXml.Build(NexusTaskKind.Scan, @"C:\Antigo\nexus.exe", TaskSchedule.Weekly(DayOfWeek.Sunday, 19, 0), TaskConditions.Both, DateTime.Now);
        var handler = new AutomationScheduledTaskHandler(fake, () => SafeExe, false);
        Assert.Equal("DIFFERENT", handler.Detect(Action(NexusTaskKind.Scan, "ON")).Value);
        var status = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Scan, () => SafeExe, fake);
        Assert.True(status.IsOn);
        Assert.False(status.PointsHere);
        Assert.Contains("outra pasta", status.Describe(), StringComparison.Ordinal);
        Assert.Equal("OFF", AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, () => SafeExe, fake).State);
        Assert.NotNull(AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, () => SafeExe, backend: null).Error);
    }

    [Fact]
    public void Restore_validation_refuses_tampered_journal_xml()
    {
        var handler = new AutomationScheduledTaskHandler(new FakeTaskScheduler(), () => SafeExe, false);
        var good = NexusTaskXml.Build(NexusTaskKind.Clean, SafeExe, TaskSchedule.Daily(3, 0), TaskConditions.Both, DateTime.Now);
        StateSnapshot Orig(string xml) => StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, "DIFFERENT", ValueOrigin.Explicit, new Dictionary<string, string> { ["xml"] = xml });
        var a = Action(NexusTaskKind.Clean, "OFF");
        Assert.Null(handler.CheckRestore(a, Orig(good)));
        Assert.NotNull(handler.CheckRestore(a, Orig(good.Replace("clean --safe --scheduled --quiet", "apply svc.sysmain.disable --yes", StringComparison.Ordinal))));
        Assert.NotNull(handler.CheckRestore(a, Orig(good.Replace(SafeExe, @"C:\Windows\System32\cmd.exe", StringComparison.Ordinal))));
        Assert.NotNull(handler.CheckRestore(a, Orig(good.Replace(NexusTaskKind.Clean.Marker, "x", StringComparison.Ordinal))));
        Assert.NotNull(handler.CheckRestore(a, Orig(NexusTaskXml.Build(NexusTaskKind.Scan, SafeExe, TaskSchedule.Daily(3, 0), TaskConditions.None, DateTime.Now)))); // XML de outro tipo
        Assert.NotNull(handler.CheckRestore(a, StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, "ON")));
        Assert.NotNull(handler.CheckRestore(a, StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, "FOREIGN")));
        Assert.Null(handler.CheckRestore(a, StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, "OFF")));
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationSafeCleaningTests
{
    private static CatalogDocument RealCatalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    [Fact]
    public void Real_catalog_selection_keeps_only_low_risk_user_rules()
    {
        var catalog = RealCatalog();
        var (included, excluded) = SafeCleaningSelector.Select(catalog, CleaningRuleSet.BuiltIn, RiskModel.ReadDeclared(Path.Combine(AppContext.BaseDirectory, "catalog")));
        var ids = included.Select(i => i.Rule.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("temp-user", ids);
        Assert.Contains("chrome-cache", ids);
        Assert.Contains("discord-cache", ids);
        foreach (var never in new[] { "temp-windows", "wer-system", "steam-logs", "chrome-history", "chrome-cookies", "edge-cookies", "brave-history",
                     "shader-nvidia", "shader-directx", "thumbnails", "logs-cbs", "battlenet-programdata" })
            Assert.DoesNotContain(never, ids);
        Assert.All(included, i =>
        {
            Assert.Equal(PrivilegeLevel.User, i.Entry.Definition.Privileges);
            Assert.False(i.Entry.Definition.Irreversible);
            Assert.False(i.Rule.Sensitive);
            Assert.False(i.Rule.Admin);
            Assert.Null(i.Rule.Warning);
            Assert.Equal(CleaningMode.Quarantine, i.Rule.Mode);
        });
        Assert.Contains(excluded, x => x.ActionId == "cleaning.chrome-cookies" && x.Reason.Contains("Condicional", StringComparison.Ordinal));
        Assert.Contains(excluded, x => x.ActionId == "cleaning.temp-windows" && x.Reason.Contains("administrador", StringComparison.Ordinal));
    }

    private const string Synthetic = """
    {
      "catalogVersion": "t", "schema": "nexus.catalog/1",
      "actions": [
        { "id": "cleaning.temp-user", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User",
          "target": { "rule": "temp-user", "batch": "{batch}", "scope": "HKCU" }, "desiredType": "CleaningBatch", "desiredValue": "applied",
          "userParameters": { "batch": "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}" } },
        { "id": "cleaning.wer-user", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User", "irreversible": true,
          "target": { "rule": "wer-user", "batch": "{batch}" }, "desiredType": "CleaningBatch", "desiredValue": "applied",
          "userParameters": { "batch": "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}" } },
        { "id": "cleaning.crashdumps-user", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User", "risk": "Medium",
          "target": { "rule": "crashdumps-user", "batch": "{batch}" }, "desiredType": "CleaningBatch", "desiredValue": "applied",
          "userParameters": { "batch": "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}" } },
        { "id": "cleaning.chrome-cache", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "Admin",
          "target": { "rule": "chrome-cache", "batch": "{batch}" }, "desiredType": "CleaningBatch", "desiredValue": "applied",
          "userParameters": { "batch": "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}" } },
        { "id": "cleaning.edge-cache", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User",
          "target": { "rule": "chrome-cache", "batch": "{batch}" }, "desiredType": "CleaningBatch", "desiredValue": "applied",
          "userParameters": { "batch": "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}" } },
        { "id": "cleaning.slack-cache", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User",
          "target": { "rule": "slack-cache", "batch": "{batch}", "path": "{path}" }, "desiredType": "CleaningBatch", "desiredValue": "applied",
          "userParameters": { "batch": "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}", "path": ".*" } },
        { "id": "cleaning.nao-existe", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User",
          "target": { "rule": "nao-existe", "batch": "{batch}" }, "desiredType": "CleaningBatch", "desiredValue": "applied",
          "userParameters": { "batch": "[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}" } }
      ]
    }
    """;

    [Fact]
    public void Irreversible_risky_admin_mismatched_and_unknown_entries_are_excluded()
    {
        var doc = CatalogLoader.Parse(Synthetic);
        var risks = RiskModel.ParseDeclared(Synthetic).ToDictionary(x => x.Key, x => x.Risk);
        var (included, excluded) = SafeCleaningSelector.Select(doc, CleaningRuleSet.BuiltIn, risks);
        Assert.Equal(["cleaning.temp-user"], included.Select(i => i.Entry.Definition.Id));
        string Why(string id) => excluded.Single(x => x.ActionId == id).Reason;
        Assert.Contains("Irreversível", Why("cleaning.wer-user"), StringComparison.Ordinal);
        Assert.Contains("médio", Why("cleaning.crashdumps-user"), StringComparison.Ordinal);
        Assert.Contains("administrador", Why("cleaning.chrome-cache"), StringComparison.Ordinal);
        Assert.Contains("não confere", Why("cleaning.edge-cache"), StringComparison.Ordinal);
        Assert.Contains("escolha", Why("cleaning.slack-cache"), StringComparison.Ordinal);
        Assert.Contains("desconhecida", Why("cleaning.nao-existe"), StringComparison.Ordinal);
    }

    [Fact]
    public void Requests_share_one_valid_batch()
    {
        var (included, _) = SafeCleaningSelector.Select(RealCatalog(), CleaningRuleSet.BuiltIn);
        var batch = CleaningBatchId.New(new DateTime(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc));
        var requests = SafeCleaningSelector.Requests(included, batch);
        Assert.Equal(included.Count, requests.Count);
        Assert.All(requests, r => Assert.Equal(batch, r.Arguments!["batch"]));
        Assert.True(requests.Count <= BrokerService.MaxActionsPerRequest);
        Assert.Throws<ArgumentException>(() => SafeCleaningSelector.Requests(included, "../../x"));
    }

    [Fact]
    public void Cleaning_report_is_escaped_and_honest()
    {
        var report = new CleaningRunReport(DateTimeOffset.Now, true, null, "20261031T220000Z-0123abcd", Guid.NewGuid(), 2048L * 1024 * 1024, 10, 1024 * 1024, 3,
            [new("cleaning.x", "<script>alert(1)</script>", "Verified", null)], [new("cleaning.y", "Cookies & cia", "Dados pessoais")], "");
        var html = report.ToHtml();
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("quarentena", html, StringComparison.Ordinal);
        Assert.Contains("1,0 MB", report.Headline, StringComparison.Ordinal);
        Assert.Contains("pulada", (report with { SkippedReason = "O PC está na bateria." }).Headline, StringComparison.Ordinal);
        var dir = Directory.CreateTempSubdirectory("nexus-clean-report").FullName;
        try
        {
            var files = report.Save(dir);
            Assert.All(files, f => Assert.True(File.Exists(f)));
            Assert.True(ReportFiles.IsReportFile(files[1], dir));
            Assert.False(ReportFiles.IsReportFile(files[0], dir)); // .json não abre
            Assert.False(ReportFiles.IsReportFile(Path.Combine(dir, "..", "x.html"), dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationRunConditionTests
{
    private sealed class FakePower(bool? battery) : IPowerSource { public bool? OnBattery() => battery; }
    private sealed class FakeGame(string? game) : IGameActivity { public string? Detect() => game; }
    private sealed class Boom : IPowerSource, IGameActivity
    {
        public bool? OnBattery() => throw new InvalidOperationException("x");
        public string? Detect() => throw new InvalidOperationException("x");
    }

    [Fact]
    public void Battery_and_game_conditions_skip_only_when_asked()
    {
        Assert.NotNull(RunConditionChecker.WhyNot(TaskConditions.Both, new FakePower(true), new FakeGame(null)));
        Assert.NotNull(RunConditionChecker.WhyNot(TaskConditions.Both, new FakePower(false), new FakeGame("Jogo aberto: cs2")));
        Assert.Null(RunConditionChecker.WhyNot(TaskConditions.Both, new FakePower(false), new FakeGame(null)));
        Assert.Null(RunConditionChecker.WhyNot(TaskConditions.Both, new FakePower(null), new FakeGame(null))); // desconhecido = roda
        Assert.Null(RunConditionChecker.WhyNot(TaskConditions.None, new FakePower(true), new FakeGame("Jogo aberto: cs2")));
        Assert.Null(RunConditionChecker.WhyNot(new TaskConditions(true, false), new FakePower(false), new FakeGame("Jogo aberto: cs2")));
        Assert.Contains("bateria", RunConditionChecker.WhyNot(new TaskConditions(true, false), new FakePower(true), new FakeGame(null))!, StringComparison.Ordinal);
        var boom = new Boom();
        Assert.Null(RunConditionChecker.WhyNot(TaskConditions.Both, boom, boom)); // leitura falhou: não inventa motivo
    }

    [Fact]
    public void Game_activity_uses_fullscreen_state_and_known_processes()
    {
        Assert.Null(new WindowsGameActivity(() => ["explorer", "chrome"], () => 5).Detect()); // QUNS_ACCEPTS_NOTIFICATIONS
        Assert.Contains("Direct3D", new WindowsGameActivity(() => [], () => 3).Detect()!, StringComparison.Ordinal);
        Assert.Contains("tela cheia", new WindowsGameActivity(() => [], () => 2).Detect()!, StringComparison.Ordinal);
        Assert.Contains("apresentação", new WindowsGameActivity(() => [], () => 4).Detect()!, StringComparison.Ordinal);
        Assert.Equal("Jogo aberto: cs2", new WindowsGameActivity(() => ["explorer", "CS2"], () => null).Detect());
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationStartWithWindowsTests
{
    private const string Exe = @"C:\Program Files\NEXUS\Nexus.Desktop.exe";
    private static readonly RegistryTarget Run = new(RegHive.HKCU, AutomationStartWithWindowsHandler.RunKey, "NEXUS", RegView.Default);

    private static ActionInstance Action(string desired) => new(
        new ActionDefinition(AutomationHandlers.StartWithWindowsActionId, 1, "t", Classification.SupportedDocumented, AutomationStartWithWindowsHandler.Kind, "", [], "",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None, AllowedValues: ["ON", "OFF"]),
        new Dictionary<string, string> { ["hive"] = "HKCU", ["path"] = AutomationStartWithWindowsHandler.RunKey, ["name"] = "NEXUS" },
        StateSnapshot.Of(AutomationStartWithWindowsHandler.StateType, desired));

    private static (InMemoryRegistryBackend Reg, ActionExecutor Exec, DesiredStateResolver Resolver, InMemoryJournal Journal) Engine(string? exe = Exe, bool otherAccount = false)
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new AutomationStartWithWindowsHandler(reg, () => exe, otherAccount));
        var journal = new InMemoryJournal();
        return (reg, new ActionExecutor(handlers, journal), new DesiredStateResolver(handlers), journal);
    }

    [Fact]
    public void On_writes_quoted_path_with_minimized_and_revert_removes()
    {
        var (reg, exec, resolver, journal) = Engine();
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Action("ON")], T.Ctx), T.Ctx).Single().Outcome);
        var read = reg.Read(Run);
        Assert.Equal("String", read.Kind);
        Assert.Equal("\"C:\\Program Files\\NEXUS\\Nexus.Desktop.exe\" --minimized", read.Value);
        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.False(reg.Read(Run).ValueExists);
    }

    [Fact]
    public void Stale_entry_is_updated_and_revert_writes_the_old_text_back()
    {
        var (reg, exec, resolver, journal) = Engine();
        const string old = "\"D:\\Antigo\\Nexus.Desktop.exe\" --minimized";
        reg.Seed(Run, "String", old);
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Action("ON")], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal("STALE", journal.Entries(run).Single().Original!.Value);
        exec.RevertRun(run);
        Assert.Equal(old, reg.Read(Run).Value);
    }

    [Theory]
    [InlineData("String", "C:\\Outro\\app.exe")]
    [InlineData("String", "\"C:\\Program Files\\NEXUS\\Nexus.Desktop.exe\" --minimized & calc")]
    [InlineData("String", "\"C:\\x\\Nexus.Desktop.exe\" --other")]
    [InlineData("ExpandString", "\"C:\\x\\Nexus.Desktop.exe\" --minimized")]
    [InlineData("String", "\"%APPDATA%\\Nexus.Desktop.exe\" --minimized")]
    public void Foreign_values_named_nexus_are_never_touched(string kind, string value)
    {
        var (reg, _, resolver, _) = Engine();
        reg.Seed(Run, kind, value);
        Assert.Equal(PlanStatus.Blocked, resolver.Resolve([Action("OFF")], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.Blocked, resolver.Resolve([Action("ON")], T.Ctx).Items.Single().Status);
        Assert.Equal(value, reg.Read(Run).Value);
    }

    [Fact]
    public void Other_account_missing_exe_and_tampered_journal_are_refused()
    {
        Assert.Equal(PlanStatus.Blocked, Engine(otherAccount: true).Resolver.Resolve([Action("ON")], T.Ctx).Items.Single().Status);
        Assert.Equal(PlanStatus.NotApplicable, Engine(exe: null).Resolver.Resolve([Action("ON")], T.Ctx).Items.Single().Status);
        Assert.Throws<ArgumentException>(() => AutomationStartWithWindowsHandler.CommandFor(@"C:\x\cmd.exe"));
        var handler = new AutomationStartWithWindowsHandler(new InMemoryRegistryBackend(), () => Exe, false);
        StateSnapshot Orig(string v) => StateSnapshot.Of(AutomationStartWithWindowsHandler.StateType, "STALE", ValueOrigin.Explicit, new Dictionary<string, string> { ["value"] = v });
        Assert.Null(handler.CheckRestore(Action("OFF"), Orig("\"D:\\A\\Nexus.Desktop.exe\" --minimized")));
        Assert.NotNull(handler.CheckRestore(Action("OFF"), Orig("C:\\Windows\\System32\\cmd.exe /c calc")));
        Assert.NotNull(handler.CheckRestore(Action("OFF"), StateSnapshot.Of(AutomationStartWithWindowsHandler.StateType, "ON")));
        var wrong = Action("ON") with { Target = new Dictionary<string, string> { ["hive"] = "HKLM", ["path"] = AutomationStartWithWindowsHandler.RunKey, ["name"] = "NEXUS" } };
        Assert.NotNull(handler.CheckRestore(wrong, StateSnapshot.Of(AutomationStartWithWindowsHandler.StateType, "OFF")));
        Assert.Equal("OFF", AutomationHandlers.ReadStartWithWindows(new InMemoryRegistryBackend(), () => Exe).State);
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationSettingsTests
{
    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory("nexus-settings").FullName, "settings.json");

    [Fact]
    public void Missing_file_gives_defaults_and_save_round_trips()
    {
        var path = TempFile();
        Assert.Equal(NexusSettings.Defaults, NexusSettings.Load(path, out var problem));
        Assert.Null(problem);
        Assert.True(NexusSettings.Defaults.Notifications);
        Assert.True(NexusSettings.Defaults.TrayIcon);
        Assert.False(NexusSettings.Defaults.CloseToTray);
        var changed = NexusSettings.Defaults with { CloseToTray = true, Notifications = false };
        Assert.Null(changed.Save(path));
        Assert.Equal(changed, NexusSettings.Load(path, out problem));
        Assert.Null(problem);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("{ isto não é json")]
    [InlineData("{\"notifications\": \"sim\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"format\": 99, \"closeToTray\": true}")]
    [InlineData("null")]
    public void Corrupted_or_strange_file_gives_defaults(string content)
    {
        var path = TempFile();
        File.WriteAllText(path, content);
        Assert.Equal(NexusSettings.Defaults, NexusSettings.Load(path, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Corrupted_file_is_preserved_and_unknown_fields_are_ignored()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ quebrado");
        NexusSettings.Load(path, out _);
        Assert.True(File.Exists(path + ".bad"));
        File.WriteAllText(path, "{\"format\": 1, \"closeToTray\": true, \"campoNovo\": 3, // comentário\n }");
        Assert.True(NexusSettings.Load(path, out var problem).CloseToTray);
        Assert.Null(problem);
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationCliV2Tests
{
    [Fact]
    public void Clean_variants()
    {
        var safe = CliParser.Parse(["clean"]).Command!;
        Assert.Equal("clean", safe.Name);
        var c = CliParser.Parse(["clean", "--safe", "--rules", "temp-user,cleaning.chrome-cache", "--dry-run", "-y"]).Command!;
        Assert.True(c.Has("--dry-run") && c.Yes && c.Has("--safe"));
        Assert.Equal(["temp-user", "chrome-cache"], CliParser.ParseRuleList(c.Option("--rules")!, out _)!);
        var sched = CliParser.Parse(["clean", "--safe", "--scheduled", "--quiet", "--skip-on-battery", "--skip-when-gaming"]).Command!;
        Assert.True(sched.Has("--scheduled") && sched.Has("--skip-when-gaming"));
        // Os argumentos da tarefa gerada são exatamente aceitos pelo parser.
        foreach (var kind in NexusTaskKind.All)
        foreach (var cond in TaskConditions.All)
            Assert.NotNull(CliParser.Parse(kind.ArgumentsFor(cond).Split(' ')).Command);
    }

    [Theory]
    [InlineData("clean", "--scheduled")]
    [InlineData("clean", "--safe", "--scheduled", "--rules", "temp-user")]
    [InlineData("clean", "--skip-on-battery")]
    [InlineData("clean", "--rules", "../x")]
    [InlineData("clean", "--rules", "Temp-User")]
    [InlineData("clean", "--rules", ",")]
    [InlineData("clean", "extra")]
    [InlineData("scan", "--skip-when-gaming")]
    [InlineData("quarantine")]
    [InlineData("quarantine", "restore")]
    [InlineData("quarantine", "restore", "20261031T190000Z-xyz")]
    [InlineData("quarantine", "list", "x")]
    [InlineData("quarantine", "apagar")]
    [InlineData("schedule", "add")]
    [InlineData("schedule", "add", "apply")]
    [InlineData("schedule", "add", "clean", "--freq", "hourly")]
    [InlineData("schedule", "add", "clean", "--freq", "monthly", "--day", "31")]
    [InlineData("schedule", "add", "clean", "--freq", "weekly", "--day", "domingo")]
    [InlineData("schedule", "add", "clean", "--freq", "daily", "--day", "SUN")]
    [InlineData("schedule", "add", "scan", "--at", "25:00")]
    [InlineData("schedule", "add", "scan", "--at", "7:00")]
    [InlineData("schedule", "add", "clean", "--always", "--skip-on-battery")]
    [InlineData("schedule", "remove", "clean", "--at", "10:00")]
    [InlineData("schedule", "list", "--freq", "daily")]
    [InlineData("schedule", "status", "x")]
    [InlineData("history", "--cleaning", "123")]
    public void Bad_usage_is_an_error(params string[] args)
    {
        var r = CliParser.Parse(args);
        Assert.Null(r.Command);
        Assert.False(r.Help);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Schedule_add_builds_schedule_and_conditions()
    {
        var c = CliParser.Parse(["schedule", "add", "clean", "--freq", "monthly", "--day", "15", "--at", "03:30"]).Command!;
        Assert.Equal(TaskSchedule.Monthly(15, 3, 30), CliParser.ScheduleFrom(c.Options, out _));
        Assert.Equal(TaskConditions.Both, CliParser.ConditionsFrom(c, NexusTaskKind.Clean)); // limpeza: as duas por padrão
        Assert.Equal(TaskConditions.None, CliParser.ConditionsFrom(c, NexusTaskKind.Scan));
        var w = CliParser.Parse(["schedule", "add", "scan", "--day", "wed"]).Command!;
        Assert.Equal(TaskSchedule.Weekly(DayOfWeek.Wednesday, 19, 0), CliParser.ScheduleFrom(w.Options, out _));
        var d = CliParser.Parse(["schedule", "add", "clean", "--freq", "daily", "--at", "22:15", "--always"]).Command!;
        Assert.Equal(TaskSchedule.Daily(22, 15), CliParser.ScheduleFrom(d.Options, out _));
        Assert.Equal(TaskConditions.None, CliParser.ConditionsFrom(d, NexusTaskKind.Clean));
        var b = CliParser.Parse(["schedule", "add", "clean", "--skip-on-battery"]).Command!;
        Assert.Equal(new TaskConditions(true, false), CliParser.ConditionsFrom(b, NexusTaskKind.Clean));
        Assert.Equal("remove", CliParser.Parse(["schedule", "remove", "scan", "--yes"]).Command!.Arguments[0]);
        Assert.Equal("list", CliParser.Parse(["schedule", "list", "--json"]).Command!.Arguments[0]);
        Assert.Equal("WED 20:00", CliParser.Parse(["schedule", "on", "--at", "WED 20:00"]).Command!.Option("--at")); // antiga continua
    }

    [Fact]
    public void Quarantine_history_and_help()
    {
        Assert.Equal("restore", CliParser.Parse(["quarantine", "restore", "20261031T190000Z-1a2b3c4d", "--yes"]).Command!.Arguments[0]);
        Assert.True(CliParser.Parse(["history", "--cleaning", "--limit", "5"]).Command!.Has("--cleaning"));
        Assert.True(CliParser.Parse(["report", "--html"]).Command!.Has("--html"));
        foreach (var topic in new[] { "clean", "quarantine", "schedule" })
        {
            var r = CliParser.Parse([topic, "--help"]);
            Assert.True(r.Help);
            Assert.NotEqual(CliParser.Help, CliParser.HelpFor(r.HelpTopic));
        }
        Assert.Contains("schedule add scan|clean", CliParser.Help, StringComparison.Ordinal);
        Assert.Contains("quarantine restore", CliParser.Help, StringComparison.Ordinal);
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationCatalogV2Tests
{
    [Fact]
    public void New_actions_load_are_user_level_and_registered()
    {
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), OtherAccount: false, Elevated: false);
        var kinds = AutomationHandlers.Create(env).Select(h => h.HandlerKind).ToHashSet(StringComparer.Ordinal);
        foreach (var id in new[] { NexusTaskKind.Scan.ActionId, NexusTaskKind.Clean.ActionId, AutomationHandlers.StartWithWindowsActionId })
        {
            var e = catalog.Actions.Single(a => a.Definition.Id == id);
            Assert.Equal(PrivilegeLevel.User, e.Definition.Privileges);
            Assert.False(e.Definition.Irreversible);
            Assert.Contains(e.Definition.HandlerKind, kinds);
            Assert.Equal(["ON", "OFF"], e.Definition.AllowedValues);
        }
        foreach (var kind in NexusTaskKind.All)
        {
            var e = catalog.Actions.Single(a => a.Definition.Id == kind.ActionId);
            Assert.Equal(TaskSchedule.Pattern, e.UserParameters["schedule"]);
            Assert.Equal(TaskConditions.Pattern, e.UserParameters["conditions"]);
            Assert.Equal(kind.TaskName, e.TargetTemplate["taskName"]);
            Assert.Equal(kind.Id, e.TargetTemplate["task"]);
            var args = e.ValidateUserArguments(new Dictionary<string, string> { ["schedule"] = "MONTHLY 2 04:00", ["conditions"] = "battery" }, out var why);
            Assert.True(args is not null, why);
            Assert.Null(e.ValidateUserArguments(new Dictionary<string, string> { ["schedule"] = "DAILY 04:00\n", ["conditions"] = "battery" }, out _));
            var inst = (e with { DesiredValue = "ON" }).TryInstantiate(args!, out why);
            Assert.True(inst is not null, why);
            Assert.Equal("TaskSchedule:ON", inst!.Desired.Describe());
            Assert.Equal("MONTHLY 2 04:00", inst.Target["schedule"]);
        }
        var rejected = catalog.Rejected.Single(r => r.Id == "automation.scheduled-apply");
        Assert.Contains("limpeza reversível", rejected.Reason, StringComparison.Ordinal);
    }
}
