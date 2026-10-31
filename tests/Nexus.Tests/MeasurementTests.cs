using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nexus.Experimentation;
using Nexus.Measurement;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public sealed class MeasurementTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-medicao-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>CSV no formato do PresentMon 2.x: o jogo com <paramref name="frames"/> quadros e um navegador com poucos.</summary>
    private string Csv(string name, int frames, double baseMs, int spikeAt = -1, double spikeMs = 60, int seed = 1)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder("Application,ProcessID,SwapChainAddress,PresentRuntime,CPUStartTime,FrameTime,CPUBusy,GPUBusy,DisplayedTime,PresentMode\n");
        double t = 0;
        for (var i = 0; i < frames; i++)
        {
            var ft = i == spikeAt ? spikeMs : baseMs + rng.NextDouble() * 0.6;
            t += ft;
            sb.Append(CultureInfo.InvariantCulture, $"cs2.exe,4242,0x1,DXGI,{t:0.000},{ft:0.000},{ft * 0.6:0.000},{ft * 0.8:0.000},{ft:0.000},Hardware: Independent Flip\n");
            if (i % 50 == 0) sb.Append(CultureInfo.InvariantCulture, $"chrome.exe,77,0x2,DXGI,{t:0.000},16.6,1,1,16.6,Composed: Flip\n");
        }
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public void Analyzer_picks_the_game_summarizes_and_keeps_spikes_when_downsampling()
    {
        var csv = Csv("jogo.csv", 6000, 4.0, spikeAt: 3001, spikeMs: 80);
        var s = SessionAnalyzer.Analyze("x", "teste", csv, "importado", DateTimeOffset.Now);

        Assert.Equal("cs2.exe", s.Application);                     // o processo com mais frames, não o navegador
        Assert.Equal(6000, s.Summary.Frames);
        Assert.InRange(s.Summary.AverageFps, 200, 260);
        Assert.True(s.Trace.Count <= SessionAnalyzer.MaxTracePoints);
        Assert.Contains(s.Trace, p => p.FrametimeMs == 80);          // o pico sobrevive à redução
        Assert.Equal(6000, s.Histogram.Sum(b => b.Count));
        Assert.Equal(1, s.Histogram[^1].Count);                      // o pico de 80 ms cai na faixa "acima de 50 ms"
    }

    [Fact]
    public void Analyzer_refuses_too_short_captures_and_unknown_processes()
    {
        var shortCsv = Csv("curto.csv", 50, 4.0);
        var ex = Assert.Throws<InvalidDataException>(() => SessionAnalyzer.Analyze("x", "c", shortCsv, "importado", DateTimeOffset.Now));
        Assert.Contains("pelo menos 100", ex.Message, StringComparison.Ordinal);
        var csv = Csv("ok.csv", 500, 4.0);
        Assert.Throws<InvalidDataException>(() => SessionAnalyzer.Analyze("x", "c", csv, "importado", DateTimeOffset.Now, "valorant.exe"));
    }

    [Fact]
    public void Store_imports_lists_retags_renames_and_deletes_only_its_own_files()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessoes"));
        var original = Csv("partida.csv", 1200, 6.0);
        var s = store.Import(original, "Partida 1");
        Assert.True(File.Exists(original));                          // o original não é tocado
        Assert.True(File.Exists(Path.Combine(store.Folder, s.CsvFile)));

        s = store.Retag(s, SessionTag.Before);
        s = store.Rename(s, "  Antes do ajuste  ");
        var (list, bad) = store.List();
        var loaded = Assert.Single(list);
        Assert.Equal(0, bad);
        Assert.Equal(SessionTag.Before, loaded.Tag);
        Assert.Equal("Antes do ajuste", loaded.Name);
        Assert.Equal(s.Summary.AverageFps, loaded.Summary.AverageFps, 9);

        store.Delete(loaded);
        Assert.Empty(store.List().Sessions);
        Assert.Empty(Directory.GetFiles(store.Folder));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void Invalid_csv_does_not_leave_files_in_the_store()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessoes"));
        var junk = Path.Combine(_dir, "lixo.csv");
        File.WriteAllText(junk, "nada,disso\n1,2\n");
        Assert.Throws<InvalidDataException>(() => store.Import(junk));
        Assert.Empty(Directory.GetFiles(store.Folder));
    }

    private MeasuredSession Session(string name, double baseMs, int seed, int minutesAgo) =>
        SessionAnalyzer.Analyze(name, name, Csv(name + ".csv", 3000, baseMs, seed: seed), "importado", DateTimeOffset.Now.AddMinutes(-minutesAgo));

    [Fact]
    public void Three_pairs_with_a_clear_gain_prove_an_improvement()
    {
        var before = new[] { Session("a1", 8.0, 1, 60), Session("a2", 8.1, 2, 50), Session("a3", 7.9, 3, 40) };   // ~123 FPS
        var after = new[] { Session("b1", 6.5, 4, 55), Session("b2", 6.6, 5, 45), Session("b3", 6.4, 6, 35) };    // ~150 FPS

        var r = SessionComparison.Compare(before, after);

        Assert.True(r.Paired);
        var fps = r.Metrics.Single(m => m.Metric.Key == "fps");
        Assert.Equal(Verdict.Improved, fps.Verdict);
        Assert.True(fps.Better);
        Assert.InRange(fps.DeltaPercent, 15, 30);
        Assert.Contains("melhorou", fps.Sentence, StringComparison.Ordinal);
        Assert.Contains("intervalo de 95%", fps.Sentence, StringComparison.Ordinal);
        Assert.Equal(Verdict.Improved, r.Metrics.Single(m => m.Metric.Key == "p99").Verdict); // frametime menor = melhor
        Assert.StartsWith("Melhora comprovada em 3 pares", r.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void One_session_each_side_is_shown_but_never_called_proof()
    {
        var r = SessionComparison.Compare([Session("a", 8.0, 1, 10)], [Session("b", 6.5, 2, 5)]);

        Assert.False(r.Paired);
        Assert.All(r.Metrics, m => Assert.Equal(Verdict.InsufficientData, m.Verdict));
        Assert.Contains("não para saber se ela é real", r.Headline, StringComparison.Ordinal);
        Assert.Contains("3 de cada lado", r.Headline, StringComparison.Ordinal);
        Assert.Contains("Ainda não dá para dizer", r.Metrics.Single(m => m.Metric.Key == "fps").Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_performance_in_three_pairs_is_reported_as_no_meaningful_change()
    {
        var before = new[] { Session("a1", 8.0, 1, 60), Session("a2", 8.0, 2, 50), Session("a3", 8.0, 3, 40) };
        var after = new[] { Session("b1", 8.0, 4, 55), Session("b2", 8.0, 5, 45), Session("b3", 8.0, 6, 35) };
        var r = SessionComparison.Compare(before, after);
        Assert.Equal(Verdict.NoMeaningfulChange, r.Metrics.Single(m => m.Metric.Key == "fps").Verdict);
        Assert.DoesNotContain("comprovada", r.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentMon_arguments_are_read_only_capture_flags_and_validate_input()
    {
        var args = PresentMonRunner.Arguments(new("pm.exe", "cs2.exe", 60, "out.csv", RequestElevation: false));
        Assert.Equal(["--process_name", "cs2.exe", "--output_file", "out.csv", "--timed", "60", "--terminate_after_timed", "--no_console_stats",
                      "--stop_existing_session", "--session_name", PresentMonRunner.SessionName], args);
        Assert.Contains("--restart_as_admin", PresentMonRunner.Arguments(new("pm.exe", "cs2.exe", 60, "out.csv", RequestElevation: true)));
        Assert.Throws<ArgumentException>(() => PresentMonRunner.Arguments(new("pm.exe", "C:\\jogos\\cs2.exe", 60, "o.csv", false)));
        Assert.Throws<ArgumentException>(() => PresentMonRunner.Arguments(new("pm.exe", "cs2.exe\" --x", 60, "o.csv", false)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresentMonRunner.Arguments(new("pm.exe", "cs2.exe", 5, "o.csv", false)));
    }

    private static Func<ProcessStartInfo, ProcessStartInfo> Fake(string sourceOrDeny) => psi =>
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var fake = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        fake.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Nexus.TestHelper.dll"));
        fake.ArgumentList.Add("fake-presentmon");
        fake.ArgumentList.Add(sourceOrDeny);
        foreach (var a in psi.ArgumentList) fake.ArgumentList.Add(a);
        return fake;
    };

    [Fact]
    public async Task Runner_captures_to_the_output_file_and_the_store_imports_it()
    {
        var source = Csv("origem.csv", 2000, 5.0);
        var output = Path.Combine(_dir, "captura", "cs2.csv");
        var lines = new List<string>();
        var r = await PresentMonRunner.RunAsync(new("PresentMon.exe", "cs2.exe", 30, output, false), CancellationToken.None,
            new Progress<string>(lines.Add), Fake(source));

        Assert.True(r.Ok, r.Message + "\n" + r.Log);
        Assert.Equal(output, r.OutputCsv);
        Assert.Contains("Started recording.", r.Log, StringComparison.Ordinal);
        var s = new SessionStore(Path.Combine(_dir, "sessoes")).Import(output, "Captura", "cs2.exe", "capturado");
        Assert.Equal(2000, s.Summary.Frames);
        Assert.Equal("capturado", s.Source);
    }

    [Fact]
    public async Task Runner_explains_access_denied_in_plain_language()
    {
        var r = await PresentMonRunner.RunAsync(new("PresentMon.exe", "cs2.exe", 30, Path.Combine(_dir, "x.csv"), false), CancellationToken.None,
            null, Fake("--deny"));
        Assert.False(r.Ok);
        Assert.Equal(5, r.ExitCode);
        Assert.Contains("Pedir permissão ao Windows", r.Message, StringComparison.Ordinal);
        Assert.Contains("Usuários de log de desempenho", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_reports_a_missing_presentmon_without_starting_anything()
    {
        var r = await PresentMonRunner.RunAsync(new(Path.Combine(_dir, "nao-existe.exe"), "cs2.exe", 30, Path.Combine(_dir, "x.csv"), false), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("não encontrado", r.Message, StringComparison.Ordinal);
    }

    private static List<TracePoint> Flat(int n, double ms, double seconds = 60) =>
        Enumerable.Range(0, n).Select(i => new TracePoint(i * seconds / Math.Max(1, n - 1), ms)).ToList();

    [Fact]
    public void Chart_scale_ignores_a_single_huge_spike_and_keeps_the_curve_readable()
    {
        var trace = Flat(1500, 4.0);
        trace[700] = new TracePoint(trace[700].Seconds, 250);
        var s = ChartScale.For(trace);
        Assert.InRange(s.YMax, ChartScale.MinYMax, 12); // a curva de 4 ms não é achatada por um pico de 250 ms
        Assert.True(s.IsClipped(250));
        Assert.Equal(0, s.Y(250, 100)); // pico cortado fica colado no topo
        Assert.Equal(60, s.XMax, 6);
        Assert.Contains(s.VisibleReferences(), r => r.Label == "144 FPS");
        Assert.DoesNotContain(s.VisibleReferences(), r => r.Label == "60 FPS");
    }

    [Fact]
    public void Chart_scale_survives_empty_single_point_equal_values_and_garbage()
    {
        var empty = ChartScale.For(null);
        Assert.Equal(ChartScale.EmptyXMax, empty.XMax);
        Assert.True(empty.YMax > 0 && empty.YStep > 0 && empty.XStep > 0);
        Assert.NotEmpty(empty.YGrid());

        var one = ChartScale.For([new TracePoint(0, 16.7)]);
        Assert.True(one.XMax > 0);
        Assert.True(one.YMax >= 16.7);
        Assert.True(double.IsFinite(one.X(0, 300)) && double.IsFinite(one.Y(16.7, 200)));

        var garbage = ChartScale.For([new TracePoint(double.NaN, double.PositiveInfinity), new TracePoint(-1, -5), new TracePoint(2, 0)]);
        Assert.True(double.IsFinite(garbage.YMax) && garbage.YMax > 0 && garbage.XMax > 0);

        var slow = ChartScale.For(Flat(200, 300)); // jogo a ~3 FPS: o teto cede para a curva caber
        Assert.True(slow.YMax >= 300);
    }

    [Fact]
    public void Chart_scale_puts_two_curves_on_one_axis_and_maps_inside_the_box()
    {
        var before = Flat(500, 7, seconds: 30);
        var after = Flat(500, 5, seconds: 60);
        var s = ChartScale.For(after, before);
        Assert.Equal(60, s.XMax, 6);
        Assert.True(s.YMax >= 7);
        Assert.Equal(0, s.X(-3, 400));
        Assert.Equal(400, s.X(999, 400));
        Assert.Equal(200, s.Y(0, 200));
        var grid = s.YGrid();
        Assert.Equal(s.YMax, grid[^1], 6);
        Assert.All(grid.Zip(grid.Skip(1)), p => Assert.Equal(s.YStep, p.Second - p.First, 6));
        Assert.Equal(0, s.XTicks()[0]);
    }

    [Theory]
    [InlineData(10, 4, 2.5)]
    [InlineData(60, 6, 10)]
    [InlineData(17, 4, 5)]
    [InlineData(0.9, 4, 0.25)]
    [InlineData(0, 4, 1)]
    [InlineData(double.NaN, 4, 1)]
    public void Nice_step_is_round(double range, int ticks, double expected) =>
        Assert.Equal(expected, ChartScale.NiceStep(range, ticks), 9);

    [Fact]
    public void Histogram_window_frames_the_occupied_bins()
    {
        var bins = SessionAnalyzer.Histogram([3.2, 3.8, 4.1, 4.4, 5.0, 80]);
        var (first, last) = ChartScale.HistogramWindow(bins);
        Assert.Equal(2, first);
        Assert.True(last - first + 1 >= 10);
        Assert.True(double.IsFinite(bins[last].ToMs));

        Assert.Equal((0, -1), ChartScale.HistogramWindow(null));
        Assert.Equal((0, -1), ChartScale.HistogramWindow([]));
        Assert.Equal((0, 9), ChartScale.HistogramWindow(SessionAnalyzer.Histogram([])));
        var high = ChartScale.HistogramWindow(SessionAnalyzer.Histogram([49.5]));
        Assert.Equal(49, high.Last);
        Assert.Equal(40, high.First);
    }
}
