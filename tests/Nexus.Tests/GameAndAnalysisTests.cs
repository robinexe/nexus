using Nexus.Core;
using Nexus.Experimentation;
using Nexus.GameProfiles;
using Nexus.GameProfiles.SourceCfg;
using Nexus.TraceAnalysis;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public class SourceCfgTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nexus-cfg-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string UserCfg =
        "// minha config\r\n" +
        "sensitivity \"1.25\" // não mexer\r\n" +
        "bind \"mouse4\" \"+voicerecord\"\r\n" +
        "fps_max 0\r\n" +
        "fps_max \"300\" // efetivo\r\n";

    [Fact]
    public void Edits_only_the_effective_assignment_and_preserves_everything_else()
    {
        var doc = SourceCfgDocument.Parse(UserCfg);
        Assert.Equal("300", doc.GetEffective("fps_max"));
        doc.Set("fps_max", "400", "cs2.cfg.fps_max");
        var after = doc.ToString();
        Assert.Equal(UserCfg.Replace("fps_max \"300\" // efetivo", "fps_max \"400\" // efetivo", StringComparison.Ordinal), after);
        Assert.Equal(2, SourceCfgDocument.Diff(UserCfg, after).Count);
    }

    [Fact]
    public void Appends_tagged_line_when_absent_and_removes_only_tagged_lines()
    {
        var doc = SourceCfgDocument.Parse("sensitivity 1\n");
        doc.Set("cl_showfps", "1", "x");
        Assert.Equal("sensitivity 1\ncl_showfps \"1\" // nexus:x\n", doc.ToString());
        Assert.Equal(1, doc.RemoveTagged("cl_showfps"));
        Assert.Equal("sensitivity 1\n", doc.ToString());
    }

    [Fact]
    public void Compound_lines_are_opaque()
    {
        var doc = SourceCfgDocument.Parse("alias x \"fps_max 100; echo hi\"\nfps_max 0; cl_showfps 1\n");
        Assert.Null(doc.GetEffective("fps_max"));
        Assert.True(doc.AppearsInOpaqueLine("fps_max"));
    }

    private ActionInstance CfgAction(string value, string file = "autoexec.cfg") =>
        new(T.Def("cs2.cfg.fps_max", SourceCfgConvarHandler.Kind),
            new Dictionary<string, string> { ["root"] = _root, ["file"] = file, ["convar"] = "fps_max" },
            StateSnapshot.Of("Convar", value));

    private static CvarCatalog Dump() => CvarCatalog.FromCvarListDump("fps_max : 400 : , \"cl\" : limite\nsensitivity : 1 : : x\n", "dump de teste");

    [Fact]
    public void Handler_requires_cvar_dump_and_rejects_unknown_convars()
    {
        Assert.Equal(ApplicabilityKind.Blocked, new SourceCfgConvarHandler(() => null).Assess(CfgAction("400"), T.Ctx).Kind);
        var unknown = CfgAction("1") with { Target = new Dictionary<string, string> { ["root"] = _root, ["file"] = "autoexec.cfg", ["convar"] = "r_dynamic" } };
        Assert.Equal(ApplicabilityKind.NotApplicable, new SourceCfgConvarHandler(Dump).Assess(unknown, T.Ctx).Kind);
    }

    [Fact]
    public void Handler_blocks_path_escape()
    {
        var a = CfgAction("400", file: Path.Combine("..", "evil.cfg"));
        Assert.Equal(ApplicabilityKind.Blocked, new SourceCfgConvarHandler(Dump).Assess(a, T.Ctx).Kind);
    }

    [Fact]
    public void Full_cycle_on_real_file_with_bom_and_crlf()
    {
        var path = Path.Combine(_root, "autoexec.cfg");
        File.WriteAllText(path, UserCfg, new System.Text.UTF8Encoding(true));
        var handlers = new HandlerRegistry().Add(new SourceCfgConvarHandler(Dump));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("cs2", "1");

        var r = exec.Execute(run, new DesiredStateResolver(handlers).Resolve([CfgAction("400")], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Equal(UserCfg, File.ReadAllText(path));
    }

    [Fact]
    public void Atomic_write_refuses_when_file_changed_after_read()
    {
        var path = Path.Combine(_root, "a.cfg");
        File.WriteAllText(path, "fps_max 1\n");
        var read = SafeFiles.Read(path);
        File.WriteAllText(path, "fps_max 2\n"); // Steam Cloud ou o usuário mudou
        Assert.Throws<IOException>(() => SafeFiles.WriteAtomic(path, "fps_max 3\n", read.Encoding, read.Sha256));
        Assert.Equal("fps_max 2\n", File.ReadAllText(path));
    }
}

[Trait("Category", "Unit")]
public class MetricsTests
{
    /// <summary>Sequência sintética determinística — fixture de teste, não é medição.</summary>
    private static double[] Fixture()
    {
        var ft = Enumerable.Repeat(10.0, 1000).ToArray();
        foreach (var i in new[] { 100, 200, 300, 400, 500, 600, 700, 800, 900, 950 }) ft[i] = 50.0;
        return ft;
    }

    [Fact]
    public void Formulas_match_their_declared_definitions()
    {
        var s = FrametimeMetrics.Summarize(Fixture());
        Assert.Equal(1000, s.Frames);
        Assert.Equal(10_400, s.DurationMs, 6);
        Assert.Equal(1000.0 * 1000 / 10_400, s.AverageFps, 6);
        Assert.Equal(10.0, s.P99FrametimeMs);          // posto ⌈0,99·1000⌉ = 990 → ainda 10 ms
        Assert.Equal(100.0, s.FpsP1, 6);
        Assert.Equal(20.0, s.OnePercentLowAverageFps, 6); // média dos 10 piores = 50 ms
        Assert.Equal(10, s.StutterCount);
        Assert.Contains("mediana", s.StutterCriterion);
    }

    [Fact]
    public void Rejects_tiny_or_invalid_samples()
    {
        Assert.Throws<ArgumentException>(() => FrametimeMetrics.Summarize(Enumerable.Repeat(10.0, 50).ToArray()));
        var bad = Fixture();
        bad[5] = double.NaN;
        Assert.Throws<ArgumentException>(() => FrametimeMetrics.Summarize(bad));
    }

    [Fact]
    public void Comparison_uses_runs_as_units_and_can_say_inconclusive()
    {
        double[] baseline = [200, 205, 198, 202, 201];
        Assert.Equal(Verdict.Improved,
            RunComparison.ComparePaired("FPS P1", baseline, [215, 219, 213, 216, 214], Direction.HigherIsBetter, 3).Verdict);
        Assert.Equal(Verdict.Regressed,
            RunComparison.ComparePaired("FPS P1", baseline, [190, 193, 188, 191, 189], Direction.HigherIsBetter, 3).Verdict);
        Assert.Equal(Verdict.Inconclusive,
            RunComparison.ComparePaired("FPS P1", baseline, [210, 196, 205, 194, 209], Direction.HigherIsBetter, 3).Verdict);
        Assert.Equal(Verdict.NoMeaningfulChange,
            RunComparison.ComparePaired("FPS P1", baseline, [200.5, 205.2, 198.4, 202.1, 201.3], Direction.HigherIsBetter, 3).Verdict);
        Assert.Equal(Verdict.InsufficientData,
            RunComparison.ComparePaired("FPS P1", [200, 201], [230, 231], Direction.HigherIsBetter, 3).Verdict);
    }

    [Fact]
    public void Lower_is_better_metrics_flip_direction()
    {
        var r = RunComparison.ComparePaired("P99 ms", [8.0, 8.2, 7.9, 8.1], [6.0, 6.1, 5.9, 6.2], Direction.LowerIsBetter, 0.5);
        Assert.Equal(Verdict.Improved, r.Verdict);
        Assert.True(r.MeanDifference < 0);
    }

    [Fact]
    public void Comparison_is_reproducible_with_fixed_seed()
    {
        double[] b = [1, 2, 3, 4, 5], c = [2, 2, 4, 5, 5];
        var x = RunComparison.ComparePaired("m", b, c, Direction.HigherIsBetter, 0.1);
        var y = RunComparison.ComparePaired("m", b, c, Direction.HigherIsBetter, 0.1);
        Assert.Equal((x.CiLow, x.CiHigh), (y.CiLow, y.CiHigh));
    }

    [Fact]
    public void Run_validator_invalidates_incomparable_runs()
    {
        var reference = new RunConditions("cs2.exe", "1920x1080", "dust2-route-a", 45, 0, false, false, false);
        Assert.Empty(RunValidator.Invalidations(reference, reference));
        var bad = reference with { Resolution = "1280x960", GpuTempStartC = 62, LostEtwEvents = 12 };
        Assert.Equal(3, RunValidator.Invalidations(reference, bad).Count);
    }
}

[Trait("Category", "Unit")]
public class PresentMonCsvTests
{
    [Fact]
    public void Parses_v2_columns_by_name_and_keeps_missing_values_null()
    {
        var rows = new List<string> { "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency,ClickToPhotonLatency" };
        for (var i = 0; i < 20; i++)
            rows.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"cs2.exe,4242,0x1,DXGI,0,0,1,Hardware: Independent Flip,{i * 4.0},4.0,2.5,1.5,0.3,3.1,3.0,1.0,8.0,4.0,0.01,{i * 4.0},0,NA,NA"));
        rows.Add("explorer.exe,10,0x2,DXGI,1,0,0,Composed: Flip,0,16.6,1,1,1,1,1,1,1,16.6,0,0,0,NA,NA");

        var cap = PresentMonCsvReader.Read(new StringReader(string.Join('\n', rows)), "cs2.exe");

        Assert.Equal("v2", cap.MetricsFlavor);
        Assert.Equal(20, cap.Frames.Count);
        Assert.All(cap.Frames, f => Assert.Null(f.ClickToPhotonMs));
        Assert.Equal(4.0, cap.Frames[0].FrameTimeMs);
        Assert.Equal(76.0, cap.Frames[^1].TimeMs);
        Assert.Equal("Hardware: Independent Flip", cap.Frames[0].PresentMode);
    }

    [Fact]
    public void Converts_v1_seconds_to_milliseconds()
    {
        var csv = "Application,ProcessID,TimeInSeconds,MsBetweenPresents\n" +
                  string.Join('\n', Enumerable.Range(0, 15).Select(i => $"VALORANT-Win64-Shipping.exe,1,{(i * 0.005).ToString(System.Globalization.CultureInfo.InvariantCulture)},5.0"));
        var cap = PresentMonCsvReader.Read(new StringReader(csv));
        Assert.Equal("v1", cap.MetricsFlavor);
        Assert.Equal(70.0, cap.Frames[^1].TimeMs, 6);
    }
}
