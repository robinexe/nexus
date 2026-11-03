using System.Globalization;
using System.Text;
using Nexus.Desktop.ViewModels;
using Nexus.Measurement;

namespace Nexus.Tests.Modules;

/// <summary>"Por que travou?" (picos e causa provável) e "Atraso de drivers" (DPC/ISR por contadores), com dados sintéticos.</summary>
[Trait("Category", "Unit")]
public sealed class MeasureStutterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-travadas-").FullName;

    public MeasureStutterTests() => Loc.Use("pt-BR");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ CSV sintético do PresentMon 2.6 (conjunto padrão)

    private const string Header26 =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime," +
        "FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,VideoBusy,DisplayLatency,DisplayedTime,AnimationError,AnimationTime,MsFlipDelay";

    /// <summary>Uma linha 2.6: quadro normal de ~7 ms (CPU 4 + espera 3; GPU 5).</summary>
    private static string Row26(string app, double ft, double cpuBusy, double cpuWait, double gpuBusy, double gpuWait, string frameType = "Application") =>
        string.Create(CultureInfo.InvariantCulture,
            $"{app},4242,0x1,DXGI,0,0,1,Hardware: Independent Flip,{frameType},0,{ft},{cpuBusy},{cpuWait},0.5,{gpuBusy},{gpuBusy},{gpuWait},0,{ft + 5},{ft},0.1,0,0");

    private string WriteCsv(IEnumerable<string> rows, string header = Header26)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllLines(path, new[] { header }.Concat(rows), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Partida sintética: 7 ms por quadro, com picos nos índices dados (cada um de um tipo).</summary>
    private static IEnumerable<string> Session(int frames, Func<int, string?> spike, string app = "game.exe")
    {
        for (var i = 0; i < frames; i++) yield return spike(i) ?? Row26(app, 7, 4, 3, 5, 2);
    }

    [Fact]
    public void Parser_reads_the_PresentMon_26_extra_columns_by_name_with_and_without_the_Ms_prefix()
    {
        var p = new PresentMonLineParser();
        Assert.False(p.TryParse(Header26, out _));
        Assert.True(p.TryParse(Row26("game.exe", 7, 4, 3, 5, 2), out var f));
        Assert.Equal(3, f.CpuWaitMs);
        Assert.Equal(2, f.GpuWaitMs);
        Assert.Equal(12, f.DisplayLatencyMs);
        Assert.Equal(0.1, f.AnimationErrorMs);
        Assert.Equal("Application", f.FrameType);
        Assert.False(f.IsGenerated);

        // --v2_metrics: nomes com "Ms" (MsBetweenAppStart, MsCPUBusy, MsCPUWait, MsGPUBusy, MsGPUWait, MsAnimationError)
        var v2 = new PresentMonLineParser();
        Assert.False(v2.TryParse("Application,ProcessID,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPUBusy,MsGPUWait,DisplayLatency,MsAnimationError", out _));
        Assert.True(v2.TryParse("x.exe,1,10,6,4,8,2,NA,0.5", out var g));
        Assert.Equal(4, g.CpuWaitMs);
        Assert.Equal(2, g.GpuWaitMs);
        Assert.Null(g.DisplayLatencyMs); // "NA" = não mostrado, nunca inventado
        Assert.Equal(0.5, g.AnimationErrorMs);
        Assert.Null(g.FrameType);
    }

    [Theory]
    [InlineData("Application", false)]
    [InlineData("NotSet", false)]
    [InlineData("NA", false)]
    [InlineData("", false)]
    [InlineData("Intel XeSS-FG", true)]
    [InlineData("AMD AFMF", true)]
    [InlineData("Repeated", true)]
    public void Frame_generation_types_are_recognised(string type, bool generated) => Assert.Equal(generated, LiveFrame.IsGeneratedType(type));

    [Fact]
    public void Live_arguments_ask_for_frame_type_only_when_requested_and_only_on_2x()
    {
        var r = new LiveCaptureRequest(@"C:\pm.exe", "game.exe", false, null, TrackFrameType: true);
        Assert.Contains("--track_frame_type", PresentMonCli.LiveArguments(r, PresentMonGeneration.V2));
        Assert.DoesNotContain("-track_frame_type", PresentMonCli.LiveArguments(r, PresentMonGeneration.V1));
        Assert.DoesNotContain("--track_frame_type", PresentMonCli.LiveArguments(r with { TrackFrameType = false }, PresentMonGeneration.V2));
    }

    [Fact]
    public void Steady_session_has_no_stutters()
    {
        var report = StutterAnalyzer.Analyze(StutterAnalyzer.ReadCsv(WriteCsv(Session(3000, _ => null))));
        Assert.Equal(0, report.Events);
        Assert.Null(report.Main);
        Assert.True(report.HasCpuGpuData);
        Assert.Equal(StutterWords.Verdict(report), Loc.T("Stutter.None"));
    }

    [Fact]
    public void Spike_rule_is_twice_the_median_and_median_plus_8_ms()
    {
        // mediana 7 ms: 14 ms é 2× mas não passa de 7 + 8 = 15 → não é travada; 16 ms é.
        var frames = Enumerable.Range(0, 200).Select(i => new StutterFrame(i == 100 ? 14.5 : i == 150 ? 16 : 7, 4, 3, 5, 2)).ToList();
        var r = StutterAnalyzer.Analyze(frames);
        Assert.Equal(1, r.Events);
        Assert.Equal(16, r.Worst[0].FrametimeMs);
        Assert.Equal(7, r.Worst[0].MedianMs);
    }

    [Fact]
    public void Gpu_spike_late_in_the_session_is_classified_as_gpu()
    {
        // ~5 min a 7 ms = 43 mil quadros; pico aos ~4 min com a GPU trabalhando 40 ms.
        var csv = WriteCsv(Session(43_000, i => i == 34_000 ? Row26("game.exe", 45, 5, 40, 42, 1) : null));
        var r = StutterAnalyzer.Analyze(StutterAnalyzer.ReadCsv(csv, "game.exe"));
        Assert.Equal(1, r.Events);
        Assert.Equal(1, r.Gpu);
        Assert.Equal(StutterCause.Gpu, r.Main);
        Assert.InRange(r.Worst[0].Seconds, 230, 245);
    }

    [Fact]
    public void Cpu_spikes_early_are_shader_and_later_are_cpu_when_there_is_no_activity_data()
    {
        // 30 s e 4 min: CPU 40 ms, GPU normal.
        var csv = WriteCsv(Session(43_000, i => i is 4_300 or 34_000 ? Row26("game.exe", 45, 40, 5, 5, 38) : null));
        var r = StutterAnalyzer.Analyze(StutterAnalyzer.ReadCsv(csv));
        Assert.Equal(2, r.Events);
        Assert.Equal(1, r.ShaderOrStreaming);
        Assert.Equal(1, r.Cpu);
        Assert.False(r.HasActivityData);
    }

    [Fact]
    public void Game_waiting_while_the_gpu_is_idle_counts_as_cpu_or_game_side()
    {
        // CPUBusy normal, CPUWait enorme, GPU normal: o jogo esperou algo (carregamento/sincronização) — não é a placa de vídeo.
        var frames = Enumerable.Range(0, 40_000).Select(i => i == 35_000 ? new StutterFrame(50, 4, 46, 5, 45) : new StutterFrame(7, 4, 3, 5, 2)).ToList();
        var r = StutterAnalyzer.Analyze(frames);
        Assert.Equal(1, r.Cpu);
    }

    [Fact]
    public void Background_process_with_notable_cpu_at_that_moment_is_blamed_by_name()
    {
        var frames = Enumerable.Range(0, 43_000).Select(i => i is 34_000 or 38_000 ? new StutterFrame(45, 40, 5, 5, 38) : new StutterFrame(7, 4, 3, 5, 2)).ToList();
        // amostras a cada 2 s; perto de 238 s (quadro 34 000) o "MsMpEng" usou 22% da máquina; perto de 266 s ninguém.
        var activity = Enumerable.Range(1, 160).Select(k => new ActivitySample(k * 2,
            k is 119 or 120 ? [new ProcessCpuShare("MsMpEng", 22), new ProcessCpuShare("Discord", 4)] : [new ProcessCpuShare("Discord", 4)], 100_000)).ToList();
        var r = StutterAnalyzer.Analyze(frames, activity);
        Assert.Equal(2, r.Events);
        Assert.Equal(1, r.Background);
        Assert.Equal(1, r.Cpu);
        Assert.Equal("MsMpEng", r.TopBackgroundProcess);
        Assert.Contains(r.Worst, e => e.Cause == StutterCause.Background && e.Process == "MsMpEng");
        Assert.True(r.HasActivityData);
        Assert.True(r.HasDiskData);
    }

    [Fact]
    public void Gpu_spike_is_not_blamed_on_a_background_process()
    {
        var frames = Enumerable.Range(0, 43_000).Select(i => i == 34_000 ? new StutterFrame(45, 5, 40, 42, 1) : new StutterFrame(7, 4, 3, 5, 2)).ToList();
        var activity = Enumerable.Range(1, 160).Select(k => new ActivitySample(k * 2, [new ProcessCpuShare("MsMpEng", 40)], null)).ToList();
        Assert.Equal(1, StutterAnalyzer.Analyze(frames, activity).Gpu);
    }

    [Fact]
    public void Game_reading_a_lot_from_disk_makes_a_late_cpu_spike_streaming_and_an_idle_disk_keeps_early_ones_cpu()
    {
        // pico aos ~238 s com o jogo lendo 50 MB/s → carregamento; pico aos ~30 s com o jogo sem ler nada → CPU (há dado de disco).
        var frames = Enumerable.Range(0, 43_000).Select(i => i is 4_300 or 34_000 ? new StutterFrame(45, 40, 5, 5, 38) : new StutterFrame(7, 4, 3, 5, 2)).ToList();
        var activity = Enumerable.Range(1, 160).Select(k => new ActivitySample(k * 2, [], k is 119 or 120 ? 50.0 * 1024 * 1024 : 0)).ToList();
        var r = StutterAnalyzer.Analyze(frames, activity);
        Assert.Equal(1, r.ShaderOrStreaming);
        Assert.Equal(1, r.Cpu);
        Assert.Contains(r.Worst, e => e.Cause == StutterCause.ShaderOrStreaming && e.GameReadMBps >= 49);
    }

    [Fact]
    public void Consecutive_spikes_merge_into_one_stutter_and_keep_the_worst_frame()
    {
        var frames = Enumerable.Range(0, 1000).Select(i => i switch { 500 => new StutterFrame(30), 501 => new StutterFrame(60), 503 => new StutterFrame(25), _ => new StutterFrame(7) }).ToList();
        var r = StutterAnalyzer.Analyze(frames);
        Assert.Equal(1, r.Events);
        Assert.Equal(60, r.Worst[0].FrametimeMs);
        Assert.Equal(1, r.Unknown); // sem CPUBusy/GPUBusy: "sem dados"
        Assert.False(r.HasCpuGpuData);
    }

    [Fact]
    public void Generated_frames_are_ignored_by_the_analysis_but_still_advance_the_clock()
    {
        var rows = Session(2000, i => i % 2 == 1 ? Row26("game.exe", 3.5, 0, 0, 0, 0, "Intel XeSS-FG") : Row26("game.exe", 3.5, 2, 1.5, 2.5, 1));
        var r = StutterAnalyzer.Analyze(StutterAnalyzer.ReadCsv(WriteCsv(rows)));
        Assert.Equal(1000, r.GeneratedFrames);
        Assert.Equal(0, r.Events);
        Assert.InRange(r.DurationSeconds, 6.9, 7.1);
    }

    [Fact]
    public void Reading_a_multi_process_csv_picks_the_process_with_most_frames()
    {
        var rows = Session(500, _ => null, "game.exe").Concat(Enumerable.Range(0, 50).Select(_ => Row26("dwm.exe", 16, 1, 15, 1, 15)));
        Assert.Equal(500, StutterAnalyzer.ReadCsv(WriteCsv(rows)).Count);
    }

    [Fact]
    public void Imported_session_gets_a_stutter_report_and_the_json_round_trips()
    {
        var csv = WriteCsv(Session(3000, i => i == 2000 ? Row26("game.exe", 45, 5, 40, 42, 1) : null));
        var store = new SessionStore(Path.Combine(_dir, "sessoes"));
        var s = store.Import(csv, "teste", "game.exe");
        Assert.NotNull(s.Stutters);
        Assert.Equal(1, s.Stutters!.Gpu);
        var back = store.List().Sessions.Single();
        Assert.Equal(1, back.Stutters!.Events);
        Assert.Equal(StutterCause.Gpu, back.Stutters.Worst[0].Cause);
    }

    [Fact]
    public void Live_session_keeps_the_extra_columns_and_activity_and_writes_them_to_the_normalized_csv()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
        var now = t0;
        var live = new LiveSession(new DetectedGame("Jogo", "game.exe", 4242, DetectionReason.UserList, "lista"), t0, true, () => now);
        live.AddActivity(t0, [new ProcessCpuShare("x", 50)], null); // antes do primeiro quadro: ignorada
        for (var i = 0; i < 3000; i++)
            live.Add(new LiveFrame("game.exe", 4242, i == 2000 ? 45 : 7, i == 2000 ? 40 : 4, 5, "Hardware: Independent Flip")
            { CpuWaitMs = 3, GpuWaitMs = 2, DisplayLatencyMs = 12, AnimationErrorMs = 0.2, FrameType = "Application" });
        now = t0.AddSeconds(14);
        live.AddActivity(now, [new ProcessCpuShare("MsMpEng", 30)], 0);
        Assert.Single(live.Activity());
        Assert.Equal(14, live.Activity()[0].Seconds, 3);
        var frames = live.StutterFrames();
        Assert.Equal(3000, frames.Count);
        Assert.Equal(3, frames[10].CpuWaitMs!.Value, 2);
        var csv = Path.Combine(_dir, "live.csv");
        live.WriteCsv(csv);
        var back = StutterAnalyzer.ReadCsv(csv);
        Assert.Equal(3000, back.Count);
        Assert.Equal(12, back[5].DisplayLatencyMs!.Value, 1);
        Assert.Equal(0.2, back[5].AnimationErrorMs!.Value, 2);
        Assert.False(back[5].Generated);
        // ~14 s: pico de CPU com MsMpEng a 30% → segundo plano
        var r = StutterAnalyzer.Analyze(frames, live.Activity());
        Assert.Equal(1, r.Background);
        Assert.Equal("MsMpEng", r.TopBackgroundProcess);
    }

    // ------------------------------------------------------------------ amostrador de atividade (falso)

    private sealed class FakeActivity : IActivitySource
    {
        public List<ProcessCpuReading> Cpu { get; set; } = [];
        public ulong? Read { get; set; }
        public IReadOnlyList<ProcessCpuReading> ProcessCpu(DateTimeOffset now) => Cpu;
        public ulong? ReadTransferBytes(int pid) => pid == 4242 ? Read : null;
    }

    [Fact]
    public void Sampler_excludes_the_game_and_idle_keeps_the_top_three_and_computes_the_read_rate()
    {
        var t = new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
        var clock = t;
        var live = new LiveSession(new DetectedGame("Jogo", "game.exe", 4242, DetectionReason.UserList, "lista"), t, true, () => clock);
        live.Add(new LiveFrame("game.exe", 4242, 7, 4, 5, null));
        var src = new FakeActivity { Read = 1_000_000 };
        using var sampler = new CaptureActivitySampler(src, live, clock: () => clock);
        sampler.Sample(); // base
        Assert.Empty(live.Activity());
        clock = t.AddSeconds(2);
        src.Read = 1_000_000 + 20 * 1024 * 1024;
        src.Cpu = [new(4242, "game", 60), new(0, "Idle", 30), new(10, "MsMpEng", 15), new(11, "chrome", 9), new(12, "Discord", 5),
                   new(13, "explorer", 4), new(14, "tiny", 1)];
        sampler.Sample();
        var a = Assert.Single(live.Activity());
        Assert.Equal(["MsMpEng", "chrome", "Discord"], a.Others.Select(o => o.Name));
        Assert.Equal(10 * 1024 * 1024, a.GameReadBytesPerSec!.Value, 1);
    }

    [Fact]
    public void Engine_starts_and_stops_the_activity_sampler_with_the_capture()
    {
        var disposed = 0;
        var started = 0;
        var probe = new MeasureTestsProbe();
        var engine = new AutoMeasureEngine(probe, new NullStarter(), activity: _ => { started++; return new Disposer(() => disposed++); });
        Assert.True(engine.StartManual(new DetectedGame("Jogo", "game.exe", 1, DetectionReason.UserList, "x"), DateTimeOffset.Now));
        Assert.Equal(1, started);
        engine.StopCapture();
        Assert.Equal(1, disposed);

        // fábrica que falha não impede a medição
        var failing = new AutoMeasureEngine(probe, new NullStarter(), activity: _ => throw new InvalidOperationException("sem acesso"));
        Assert.True(failing.StartManual(new DetectedGame("Jogo", "game.exe", 1, DetectionReason.UserList, "x"), DateTimeOffset.Now));
        Assert.NotNull(failing.Session);
    }

    private sealed class Disposer(Action a) : IDisposable { public void Dispose() => a(); }

    private sealed class MeasureTestsProbe : IProcessProbe
    {
        public ProcessSnapshot Read() => new([], null);
        public bool IsAlive(int pid) => true;
    }

    private sealed class NullStarter : ILiveCaptureStarter
    {
        public bool Available => true;
        public ILiveCapture Start(string processName, Action<LiveFrame> onFrame) => new NullCapture();
        private sealed class NullCapture : ILiveCapture
        {
            public string Log => "";
            public bool Ended => false;
            public void Finish() { }
            public void Dispose() { }
        }
    }

    // ------------------------------------------------------------------ frases (pt-BR, sempre "provavelmente")

    [Fact]
    public void Verdict_says_most_likely_shader_with_the_normal_in_the_first_minutes_hint()
    {
        var r = new StutterReport { Events = 12, ShaderOrStreaming = 9, Cpu = 2, Gpu = 1, DurationSeconds = 600 };
        Assert.Equal("12 travadas: a maioria provavelmente por compilação de shader ou carregamento — normal nos primeiros minutos de um jogo novo",
            StutterWords.Verdict(r));
    }

    [Fact]
    public void Verdict_names_the_background_program_and_mixed_causes_list_the_top_two()
    {
        var bg = new StutterReport { Events = 3, Background = 2, Cpu = 1, TopBackgroundProcess = "MsMpEng" };
        Assert.Contains("MsMpEng", StutterWords.Verdict(bg));
        Assert.Contains("provavelmente", StutterWords.Verdict(bg));
        var mixed = new StutterReport { Events = 4, Gpu = 2, Cpu = 2 };
        Assert.Equal("4 travadas: causas variadas — provavelmente placa de vídeo no limite e processador ou o próprio jogo", StutterWords.Verdict(mixed));
        Assert.Equal("1 travada: provavelmente por placa de vídeo no limite — baixar a qualidade gráfica ou limitar o FPS costuma ajudar",
            StutterWords.Verdict(new StutterReport { Events = 1, Gpu = 1 }));
        Assert.Equal("", StutterWords.Verdict(null));
    }

    [Fact]
    public void Cause_rows_and_worst_lines_are_plain_language()
    {
        var r = new StutterReport
        {
            Events = 2, Gpu = 1, ShaderOrStreaming = 1,
            Worst = [new StutterEvent(133, 87, 7, StutterCause.Gpu), new StutterEvent(20, 40, 7, StutterCause.ShaderOrStreaming)],
        };
        var rows = StutterWords.Rows(r);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Placa de vídeo no limite", rows[0].Title);
        Assert.Equal("aos 2:13 · 87 ms (normal: 7 ms) · provável: placa de vídeo no limite", StutterWords.Worst(r)[0]);
        Assert.Contains("ETW", StutterWords.Notes(r), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ atraso de drivers (DPC/ISR por contadores)

    private static DpcSample Sample(double s, params (double Dpc, double Isr, double Dpcs, double Ints)[] cores) =>
        new(s, cores.Select((c, i) => new CoreDpcReading("0," + i, c.Dpc, c.Isr, c.Dpcs, c.Ints)).ToList());

    [Fact]
    public void Quiet_system_is_good()
    {
        var samples = Enumerable.Range(1, 60).Select(s => Sample(s, (0.2, 0.1, 800, 1500), (0.1, 0.05, 300, 600))).ToList();
        var r = DriverLatencyAnalyzer.Analyze(samples);
        Assert.Equal(DriverLatencyLevel.Good, r.Level);
        Assert.Equal(60, r.Samples);
        Assert.Equal(0, r.SecondsAboveWarn);
        // núcleo 0: 0,2% de 1 s = 2000 µs ÷ 800 DPCs = 2,5 µs; núcleo 1: 1000 µs ÷ 300 = 3,3 µs (o pior núcleo vale)
        Assert.Equal(3.3, r.WorstAverageDpcMicroseconds!.Value, 1);
        Assert.Equal(2.5, r.Cores.Single(c => c.Core == "0,0").WorstAverageDpcMicroseconds!.Value, 1);
    }

    [Fact]
    public void A_core_spiking_above_5_percent_may_stutter_and_above_20_is_bad()
    {
        var may = Enumerable.Range(1, 60).Select(s => Sample(s, s == 30 ? (6.0, 1.0, 900, 1000) : (0.2, 0.1, 800, 1500), (0.1, 0.05, 300, 600))).ToList();
        var r = DriverLatencyAnalyzer.Analyze(may);
        Assert.Equal(DriverLatencyLevel.MayStutter, r.Level);
        Assert.Equal("0,0", r.PeakCore);
        Assert.Equal(30, r.PeakAtSeconds);
        Assert.Equal(7, r.PeakPercent, 2);
        Assert.Equal(1, r.SecondsAboveWarn);

        var bad = Enumerable.Range(1, 60).Select(s => Sample(s, (0.2, 0.1, 800, 1500), s == 12 ? (18.0, 4.0, 2000, 3000) : (0.1, 0.05, 300, 600))).ToList();
        var b = DriverLatencyAnalyzer.Analyze(bad);
        Assert.Equal(DriverLatencyLevel.Bad, b.Level);
        Assert.Equal("0,1", b.PeakCore);
        Assert.Equal("0,1", b.Cores[0].Core); // núcleo com o pior pico primeiro
    }

    [Fact]
    public void Sustained_average_load_alone_can_make_it_bad()
    {
        var samples = Enumerable.Range(1, 60).Select(s => Sample(s, (2.5, 1.5, 5000, 8000), (2.5, 1.5, 5000, 8000))).ToList();
        Assert.Equal(DriverLatencyLevel.Bad, DriverLatencyAnalyzer.Analyze(samples).Level); // 4% de média, nenhum pico ≥ 20%
    }

    [Fact]
    public void No_samples_is_unknown_and_few_calls_do_not_produce_an_average_duration()
    {
        Assert.Equal(DriverLatencyLevel.Unknown, DriverLatencyAnalyzer.Analyze([]).Level);
        var r = DriverLatencyAnalyzer.Analyze([Sample(1, (0.5, 0.1, 3, 5))]);
        Assert.Null(r.WorstAverageDpcMicroseconds);
        Assert.Null(r.WorstAverageIsrMicroseconds);
    }

    private sealed class FakeDpc(Func<int, IReadOnlyList<CoreDpcReading>?> read) : IDpcCounterSource
    {
        private int _n;
        public bool Disposed { get; private set; }
        public IReadOnlyList<string> Unavailable => [];
        public IReadOnlyList<CoreDpcReading>? Read() => read(_n++);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Test_runner_reads_once_per_interval_skips_the_base_and_reports_progress()
    {
        var src = new FakeDpc(n => n == 0 ? null : [new CoreDpcReading("0,0", n == 40 ? 25 : 0.3, 0.1, 500, 900)]);
        var reported = new List<double>();
        var (result, samples, cancelled) = await DriverLatencyTest.RunAsync(src, TimeSpan.FromSeconds(60), new SyncProgress(reported.Add),
            delay: (_, _) => Task.CompletedTask);
        Assert.False(cancelled);
        Assert.Equal(60, samples.Count);
        Assert.Equal(DriverLatencyLevel.Bad, result.Level);
        Assert.Equal(40, result.PeakAtSeconds);
        Assert.Equal(1.0, reported[^1]);
    }

    [Fact]
    public async Task Test_runner_rejects_out_of_range_durations_and_returns_partial_results_when_cancelled()
    {
        var src = new FakeDpc(_ => [new CoreDpcReading("0,0", 0.2, 0.1, 500, 900)]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DriverLatencyTest.RunAsync(src, TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DriverLatencyTest.RunAsync(src, TimeSpan.FromSeconds(181)));

        using var cts = new CancellationTokenSource();
        var calls = 0;
        var (result, samples, cancelled) = await DriverLatencyTest.RunAsync(src, TimeSpan.FromSeconds(120),
            delay: (_, token) => { if (++calls > 10) { cts.Cancel(); token.ThrowIfCancellationRequested(); } return Task.CompletedTask; }, cancel: cts.Token);
        Assert.True(cancelled);
        Assert.Equal(10, samples.Count);
        Assert.Equal(DriverLatencyLevel.Good, result.Level);
    }

    private sealed class SyncProgress(Action<double> a) : IProgress<double> { public void Report(double value) => a(value); }

    [Fact]
    public void Measurement_never_touches_timer_resolution()
    {
        // O teste de drivers é só leitura: nada de timeBeginPeriod/NtSetTimerResolution na biblioteca de medição.
        var dir = Path.GetDirectoryName(typeof(DriverLatencyTest).Assembly.Location)!;
        var bytes = File.ReadAllBytes(Path.Combine(dir, "Nexus.Measurement.dll"));
        var text = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("timeBeginPeriod", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NtSetTimerResolution", text, StringComparison.Ordinal);
    }
}
