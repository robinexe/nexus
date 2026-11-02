using System.Xml.Linq;
using Nexus.Automation;
using Nexus.Core;

namespace Nexus.Tests.Modules;

/// <summary>
/// nexus-task.exe: a linha de comando sem console que as tarefas agendadas rodam (sem janela preta). Lista fechada de
/// argumentos, validação do caminho, preferência pelo nexus-task.exe e migração das tarefas que apontavam para o nexus.exe.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WindowlessTaskTests
{
    private const string TaskExe = @"C:\Program Files\NEXUS\nexus-task.exe";
    private const string CliExe = @"C:\Program Files\NEXUS\nexus.exe";

    private static ActionInstance Action(NexusTaskKind kind, string desired, string schedule = "WEEKLY SUN 19:00", string conditions = "battery-game") => new(
        new ActionDefinition(kind.ActionId, 1, "t", Classification.SupportedDocumented, AutomationScheduledTaskHandler.Kind, "", [], "",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None, AllowedValues: ["ON", "OFF"]),
        new Dictionary<string, string> { ["task"] = kind.Id, ["taskName"] = kind.TaskName, ["schedule"] = schedule, ["conditions"] = conditions },
        StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, desired));

    [Fact]
    public void Every_command_line_written_into_a_task_is_accepted()
    {
        foreach (var kind in NexusTaskKind.All)
            foreach (var c in TaskConditions.All)
                Assert.True(WindowlessTask.IsAllowed(kind.ArgumentsFor(c).Split(' ')), kind.ArgumentsFor(c));
        Assert.True(WindowlessTask.IsAllowed(ScheduledScanTask.Arguments.Split(' ')));
        Assert.Equal(NexusTaskKind.All.Count * TaskConditions.All.Count + 1, WindowlessTask.AllowedCommandLines.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("version")]
    [InlineData("apply win.gamemode.enable --yes")]
    [InlineData("optimize --yes")]
    [InlineData("revert")]
    [InlineData("clean --safe")]                                       // sem --scheduled: perguntaria no terminal
    [InlineData("clean --safe --scheduled --quiet --yes")]             // argumento a mais
    [InlineData("scan --report --quiet --scheduled --out C:\\x")]
    [InlineData("SCAN --report --quiet --scheduled")]                  // maiúsculas: não é a linha gravada
    [InlineData("scan  --report --quiet --scheduled")]                 // argumento vazio
    public void Anything_else_is_refused(string line)
    {
        var args = line.Length == 0 ? [] : line.Split(' ');
        Assert.False(WindowlessTask.IsAllowed(args));
    }

    [Fact]
    public void Arguments_with_embedded_spaces_are_refused_even_if_the_joined_text_matches()
    {
        Assert.False(WindowlessTask.IsAllowed(["scan --report", "--quiet", "--scheduled"]));
        Assert.True(WindowlessTask.IsAllowed(["scan", "--report", "--quiet", "--scheduled"]));
    }

    [Theory]
    [InlineData(TaskExe)]
    [InlineData(CliExe)]
    [InlineData(@"D:\Jogos & Apps\NEXUS\NEXUS-TASK.EXE")]
    public void Both_nexus_executables_are_valid_task_commands(string path) => Assert.Null(ScheduledScanTask.ValidateExecutable(path));

    [Theory]
    [InlineData(@"C:\Program Files\NEXUS\nexus-task.exe.bak")]
    [InlineData(@"C:\Program Files\NEXUS\nexus-tasks.exe")]
    [InlineData(@"C:\Program Files\NEXUS\Nexus.Desktop.exe")]
    [InlineData(@"nexus-task.exe")]
    [InlineData(@"\\servidor\share\nexus-task.exe")]
    [InlineData(@"C:\%TEMP%\nexus-task.exe")]
    [InlineData(@"C:\x\..\Windows\nexus-task.exe")]
    [InlineData(@"C:\x""\nexus-task.exe")]
    public void Unsafe_or_other_paths_are_refused(string path)
    {
        Assert.NotNull(ScheduledScanTask.ValidateExecutable(path));
        Assert.Throws<ArgumentException>(() => NexusTaskXml.Build(NexusTaskKind.Scan, path, TaskSchedule.Daily(3, 0), TaskConditions.None, DateTime.Now));
    }

    [Fact]
    public void The_windowless_executable_is_preferred_and_nexus_exe_is_the_fallback()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(AutomationScheduledScanHandler.ExecutableIn(dir));
            File.WriteAllText(Path.Combine(dir, "nexus.exe"), "");
            Assert.Equal(Path.Combine(dir, "nexus.exe"), AutomationScheduledScanHandler.ExecutableIn(dir));
            File.WriteAllText(Path.Combine(dir, "nexus-task.exe"), "");
            Assert.Equal(Path.Combine(dir, "nexus-task.exe"), AutomationScheduledScanHandler.ExecutableIn(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_task_that_still_runs_nexus_exe_is_different_and_saving_again_moves_it_to_nexus_task_with_exact_revert()
    {
        var fake = new FakeTaskScheduler();
        var old = NexusTaskXml.Build(NexusTaskKind.Clean, CliExe, TaskSchedule.Weekly(DayOfWeek.Sunday, 19, 0), TaskConditions.Both, new DateTime(2026, 1, 1));
        fake.Tasks[NexusTaskKind.Clean.TaskName] = old;
        var handlers = new HandlerRegistry().Add(new AutomationScheduledTaskHandler(fake, () => TaskExe, otherAccount: false));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var resolver = new DesiredStateResolver(handlers);

        var status = AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, () => TaskExe, fake);
        Assert.Equal(AutomationScheduledTaskHandler.Different, status.State);
        Assert.False(status.PointsHere);

        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Action(NexusTaskKind.Clean, "ON")], T.Ctx), T.Ctx).Single().Outcome);
        var info = NexusTaskXml.Inspect(fake.Tasks[NexusTaskKind.Clean.TaskName], NexusTaskKind.Clean);
        Assert.Equal(TaskExe, info.Command);
        Assert.Equal(@"C:\Program Files\NEXUS", XDocument.Parse(fake.Tasks[NexusTaskKind.Clean.TaskName]).Descendants().Single(e => e.Name.LocalName == "WorkingDirectory").Value);
        Assert.True(WindowlessTask.IsAllowed(info.Arguments!.Split(' ')));
        Assert.True(AutomationHandlers.ReadTaskStatus(NexusTaskKind.Clean, () => TaskExe, fake).PointsHere);

        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Equal(old, fake.Tasks[NexusTaskKind.Clean.TaskName]);
    }

    [Fact]
    public void A_journal_entry_pointing_to_nexus_task_can_be_restored()
    {
        var fake = new FakeTaskScheduler();
        var handler = new AutomationScheduledTaskHandler(fake, () => TaskExe, otherAccount: false);
        var xml = NexusTaskXml.Build(NexusTaskKind.Scan, TaskExe, TaskSchedule.Daily(22, 0), TaskConditions.None, new DateTime(2026, 1, 1));
        var original = StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, AutomationScheduledTaskHandler.On, ValueOrigin.Explicit,
            new Dictionary<string, string> { ["present"] = "1", ["xml"] = xml });
        Assert.Null(handler.CheckRestore(Action(NexusTaskKind.Scan, "OFF"), original));
        var bad = StateSnapshot.Of(AutomationScheduledTaskHandler.StateType, AutomationScheduledTaskHandler.On, ValueOrigin.Explicit,
            new Dictionary<string, string> { ["present"] = "1", ["xml"] = xml.Replace("nexus-task.exe", "calc.exe", StringComparison.Ordinal) });
        Assert.NotNull(handler.CheckRestore(Action(NexusTaskKind.Scan, "OFF"), bad));
    }

    [Fact]
    public void The_task_project_is_the_same_cli_built_as_a_windowless_app_and_is_published()
    {
        var root = Wiring.Root;
        var csproj = XDocument.Load(Path.Combine(root, "src", "Nexus.Cli.Task", "Nexus.Cli.Task.csproj"));
        string Prop(string name) => csproj.Descendants(name).Single().Value;
        Assert.Equal("WinExe", Prop("OutputType"));
        Assert.Equal("nexus-task", Prop("AssemblyName"));
        Assert.Contains("NEXUS_WINDOWLESS", Prop("DefineConstants"), StringComparison.Ordinal);
        Assert.Contains(csproj.Descendants("Compile"), c => (string?)c.Attribute("Include") == @"..\Nexus.Cli\**\*.cs");

        var program = File.ReadAllText(Path.Combine(root, "src", "Nexus.Cli", "Program.cs"));
        Assert.Contains("#if NEXUS_WINDOWLESS", program, StringComparison.Ordinal);
        Assert.Contains("if (!WindowlessTask.IsAllowed(args)) return CliExit.Usage;", program, StringComparison.Ordinal);

        var publish = File.ReadAllText(Path.Combine(root, "tools", "packaging", "publish.py"));
        Assert.Contains("\"nexus-task.exe\"", publish, StringComparison.Ordinal);
        Assert.Contains("src/Nexus.Cli.Task/Nexus.Cli.Task.csproj", File.ReadAllText(Path.Combine(root, "Nexus.slnx")), StringComparison.Ordinal);
    }
}
