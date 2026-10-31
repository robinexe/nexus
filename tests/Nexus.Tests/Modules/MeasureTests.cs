using System.Text;
using Nexus.Experimentation;
using Nexus.Measurement;

namespace Nexus.Tests.Modules;

/// <summary>Medir v2: bibliotecas, detector, PresentMon ao vivo, estatística incremental, antes x depois, preferências, mostrador.</summary>
[Trait("Category", "Unit")]
public sealed class MeasureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-medir-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ bibliotecas (manifestos de amostra)

    [Fact]
    public void Steam_library_lists_game_folders_from_vdf_and_manifests_and_skips_tools()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"    "C:\\Program Files (x86)\\Steam" }
                "1" { "path"    "D:\\SteamLibrary" }
            }
            """;
        var manifests = new Dictionary<string, (string, string)[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(@"C:\Program Files (x86)\Steam", "steamapps")] =
                [("a.acf", "\"AppState\" { \"appid\" \"730\" \"name\" \"Counter-Strike 2\" \"installdir\" \"Counter-Strike Global Offensive\" }"),
                 ("b.acf", "\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"Steamworks Shared\" }")],
            [Path.Combine(@"D:\SteamLibrary", "steamapps")] =
                [("c.acf", "\"AppState\" { \"appid\" \"570\" \"name\" \"Dota 2\" \"installdir\" \"dota 2 beta\" }"),
                 ("d.acf", "\"AppState\" { \"appid\" \"1\" \"name\" \"Mal\" \"installdir\" \"..\\\\..\\\\Windows\" }")],
        };
        var games = GameLibrary.ParseSteam(@"C:\Program Files (x86)\Steam", vdf, apps => manifests.TryGetValue(apps, out var m) ? m : []);
        Assert.Equal(2, games.Count);
        Assert.Contains(games, g => g.Name == "Counter-Strike 2" && g.Store == "Steam" && g.InstallDir!.EndsWith("Counter-Strike Global Offensive", StringComparison.Ordinal));
        Assert.Contains(games, g => g.Name == "Dota 2" && g.InstallDir!.Contains("SteamLibrary", StringComparison.Ordinal));
    }

    [Fact]
    public void Epic_riot_battlenet_and_xbox_manifests_are_parsed()
    {
        var epic = GameLibrary.ParseEpic("""{"DisplayName":"Fortnite","InstallLocation":"C:\\Program Files\\Epic Games\\Fortnite","LaunchExecutable":"FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe","bIsApplication":true}""");
        Assert.NotNull(epic);
        Assert.Equal("Fortnite", epic!.Name);
        Assert.Equal("FortniteClient-Win64-Shipping.exe", epic.ExeName);
        Assert.Null(GameLibrary.ParseEpic("{ não é json"));

        var riot = GameLibrary.ParseRiotProductSettings("product_install_full_path: \"C:/Riot Games/VALORANT/live\"\nproduct_install_root: \"C:/Riot Games\"\n", "valorant.live");
        Assert.NotNull(riot);
        Assert.Equal("VALORANT", riot!.Name);
        Assert.Equal(@"C:\Riot Games\VALORANT\live", riot.InstallDir);
        Assert.Null(GameLibrary.ParseRiotProductSettings("product_install_full_path: \"../../x\"\n", "x.live"));

        var db = new List<byte> { 0x0A, 0x12, 0x00 };
        db.AddRange(Encoding.UTF8.GetBytes("prometheus"));
        db.Add(0x1A);
        db.AddRange(Encoding.UTF8.GetBytes("C:/Program Files (x86)/Overwatch"));
        db.AddRange([0x22, 0x05]);
        db.AddRange(Encoding.UTF8.GetBytes("C:/Program Files (x86)/Battle.net"));
        db.Add(0x00);
        var bnet = GameLibrary.ParseBattleNetProductDb([.. db]);
        var ow = Assert.Single(bnet);
        Assert.Equal("Overwatch", ow.Name);
        Assert.Equal(@"C:\Program Files (x86)\Overwatch", ow.InstallDir);

        var root = new List<byte>(Encoding.ASCII.GetBytes("RGBX")) { 1, 0, 0, 0 };
        root.AddRange(Encoding.Unicode.GetBytes("XboxGames\0"));
        Assert.Equal([Path.Combine(@"E:\", "XboxGames").Replace('/', '\\')], GameLibrary.ParseGamingRoot([.. root], @"E:\"));
        Assert.Empty(GameLibrary.ParseGamingRoot(Encoding.ASCII.GetBytes("XXXX\0\0\0\0abc"), @"E:\"));
    }

    [Fact]
    public void Scan_reads_store_folders_on_disk_and_survives_missing_ones()
    {
        var programData = Path.Combine(_dir, "ProgramData");
        var epic = Directory.CreateDirectory(Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests")).FullName;
        File.WriteAllText(Path.Combine(epic, "x.item"), """{"DisplayName":"Rocket League","InstallLocation":"D:\\Epic\\rocketleague","LaunchExecutable":"Binaries/Win64/RocketLeague.exe"}""");
        var riot = Directory.CreateDirectory(Path.Combine(programData, "Riot Games", "Metadata", "valorant.live")).FullName;
        File.WriteAllText(Path.Combine(riot, "valorant.live.product_settings.yaml"), "product_install_full_path: \"C:/Riot Games/VALORANT/live\"\n");
        var steam = Directory.CreateDirectory(Path.Combine(_dir, "Steam", "steamapps")).FullName;
        File.WriteAllText(Path.Combine(steam, "appmanifest_730.acf"), "\"AppState\" { \"appid\" \"730\" \"name\" \"Counter-Strike 2\" \"installdir\" \"cs2\" }");

        var notes = new List<string>();
        var games = GameLibrary.Scan(new GameLibrary.Roots(Path.Combine(_dir, "Steam"), programData, [Path.Combine(_dir, "nao-existe")]), notes);
        Assert.Contains(games, g => g.Store == "Epic" && g.Name == "Rocket League");
        Assert.Contains(games, g => g.Store == "Riot" && g.Name == "VALORANT");
        Assert.Contains(games, g => g.Store == "Steam" && g.Name == "Counter-Strike 2");
        Assert.Empty(notes);
    }

    // ------------------------------------------------------------------ detector (regras com falsos)

    private static readonly KnownGame Valorant = new("VALORANT", "Riot", @"C:\Riot Games\VALORANT\live", null);
    private static DetectionRules Rules(bool fullscreen = false, string[]? user = null, string[]? excluded = null) =>
        new([Valorant, new KnownGame("Counter-Strike 2", "Steam", @"D:\SteamLibrary\steamapps\common\cs2", null)], user ?? [], excluded ?? [], fullscreen);

    [Fact]
    public void Process_inside_a_library_folder_is_the_game_but_helpers_and_launchers_are_not()
    {
        var snap = new ProcessSnapshot(
        [
            new RunningProcess(10, "RiotClientServices.exe", @"C:\Riot Games\Riot Client\RiotClientServices.exe"),
            new RunningProcess(11, "VALORANT.exe", @"C:\Riot Games\VALORANT\live\VALORANT.exe".Replace("VALORANT.exe", "CrashReportClient.exe")),
            new RunningProcess(12, "VALORANT-Win64-Shipping.exe", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"),
        ], null);
        var g = GameMatcher.Find(snap, Rules());
        Assert.NotNull(g);
        Assert.Equal(12, g!.Pid);
        Assert.Equal(DetectionReason.Library, g.Reason);
        Assert.Equal("VALORANT", g.Name);
        Assert.Equal("valorant-win64-shipping.exe", g.Key);

        Assert.Null(GameMatcher.Find(new ProcessSnapshot([new RunningProcess(1, "steam.exe", @"D:\SteamLibrary\steamapps\common\cs2\steam.exe")], null), Rules()));
    }

    [Fact]
    public void User_list_built_in_list_and_exclusions_follow_the_documented_order()
    {
        var snap = new ProcessSnapshot([new RunningProcess(5, "cs2.exe", null), new RunningProcess(6, "meujogo.exe", @"C:\Jogos\meujogo.exe")], null);
        var g = GameMatcher.Find(snap, Rules(user: ["meujogo.exe"]));
        Assert.Equal(DetectionReason.UserList, g!.Reason);
        Assert.Equal(6, g.Pid);

        g = GameMatcher.Find(snap, Rules());
        Assert.Equal(DetectionReason.BuiltIn, g!.Reason);           // cs2.exe está na lista embutida
        Assert.Null(GameMatcher.Find(snap, Rules(excluded: ["cs2.exe"]))); // exclusão vence
    }

    [Fact]
    public void Foreground_wins_among_several_candidates()
    {
        var snap = new ProcessSnapshot([new RunningProcess(5, "cs2.exe", null), new RunningProcess(7, "dota2.exe", null)], new ForegroundWindow(7, 1920, 1080, true));
        Assert.Equal(7, GameMatcher.Find(snap, Rules())!.Pid);
    }

    [Fact]
    public void Fullscreen_rule_is_opt_in_and_ignores_browsers_small_windows_and_windows_components()
    {
        var unknown = new RunningProcess(9, "indie.exe", @"C:\Games\Indie\indie.exe");
        var fg = new ForegroundWindow(9, 2560, 1440, true);
        Assert.Null(GameMatcher.Find(new ProcessSnapshot([unknown], fg), Rules(fullscreen: false)));
        var g = GameMatcher.Find(new ProcessSnapshot([unknown], fg), Rules(fullscreen: true));
        Assert.Equal(DetectionReason.Fullscreen, g!.Reason);
        Assert.Equal("indie", g.Name);

        Assert.Null(GameMatcher.Find(new ProcessSnapshot([unknown], fg with { CoversMonitor = false }), Rules(fullscreen: true)));
        Assert.Null(GameMatcher.Find(new ProcessSnapshot([unknown], fg with { Width = 400, Height = 300 }), Rules(fullscreen: true)));
        Assert.Null(GameMatcher.Find(new ProcessSnapshot([new RunningProcess(9, "chrome.exe", @"C:\Program Files\Google\chrome.exe")], fg), Rules(fullscreen: true)));
        Assert.Null(GameMatcher.Find(new ProcessSnapshot([new RunningProcess(9, "video.exe", @"C:\Windows\System32\video.exe")], fg), Rules(fullscreen: true)));
    }

    [Fact]
    public void Tracker_waits_before_starting_and_survives_a_flicker_before_stopping()
    {
        var t0 = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
        var tracker = new GameSessionTracker(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(4));
        var game = new DetectedGame("CS2", "cs2.exe", 5, DetectionReason.BuiltIn, "x");
        Assert.Equal(GameEventKind.None, tracker.Update(game, false, t0).Kind);
        Assert.Equal(GameEventKind.None, tracker.Update(game, false, t0.AddSeconds(4)).Kind);
        Assert.Equal(GameEventKind.Started, tracker.Update(game, false, t0.AddSeconds(8)).Kind);
        Assert.Equal(GameEventKind.None, tracker.Update(null, false, t0.AddSeconds(20)).Kind);   // sumiu agora: espera
        Assert.Equal(GameEventKind.None, tracker.Update(game, true, t0.AddSeconds(22)).Kind);    // voltou: segue
        Assert.Equal(GameEventKind.None, tracker.Update(null, false, t0.AddSeconds(30)).Kind);
        var stop = tracker.Update(null, false, t0.AddSeconds(35));
        Assert.Equal(GameEventKind.Stopped, stop.Kind);
        Assert.Equal(TimeSpan.FromSeconds(27), stop.PlayedFor);

        // Lançador que abre e fecha antes do tempo mínimo não dispara nada.
        var t = new GameSessionTracker(TimeSpan.FromSeconds(8));
        t.Update(game, false, t0);
        Assert.Equal(GameEventKind.None, t.Update(null, false, t0.AddSeconds(3)).Kind);
        Assert.Equal(GameEventKind.None, t.Update(game, false, t0.AddSeconds(4)).Kind);
        Assert.Null(t.Current);
    }

    // ------------------------------------------------------------------ PresentMon tempo real

    [Fact]
    public void Live_parser_reads_presentmon_2x_lines_and_skips_noise()
    {
        var p = new PresentMonLineParser();
        Assert.False(p.TryParse("Started recording ETW session", out _));
        Assert.False(p.TryParse("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime", out _));
        Assert.True(p.HasHeader);
        Assert.Equal("FrameTime", p.FrametimeColumn);
        Assert.True(p.TryParse("cs2.exe,4242,0x000001A,DXGI,0,0,1,Hardware: Independent Flip,Application,1234.5,6.944,4.1,2.8,0.5,5.0,4.9,2.0,10.2,6.94", out var f));
        Assert.Equal("cs2.exe", f.Application);
        Assert.Equal(4242, f.ProcessId);
        Assert.Equal(6.944, f.FrametimeMs, 6);
        Assert.Equal(4.1, f.CpuBusyMs!.Value, 6);
        Assert.Equal(4.9, f.GpuBusyMs!.Value, 6);
        Assert.Equal("Hardware: Independent Flip", f.PresentMode);
        Assert.False(p.TryParse("cs2.exe,4242,0x1,DXGI,0,0,1,Composed: Flip,Application,1240,NA,NA,NA,NA,NA,NA,NA,NA,NA", out _)); // NA
        Assert.False(p.TryParse("cs2.exe,4242,0x1", out _));                                                                   // colunas a menos
        Assert.Equal(3, p.Skipped);                                    // aviso sem vírgula, NA, colunas a menos
    }

    [Fact]
    public void Live_parser_reads_presentmon_20_and_1x_headers()
    {
        var v20 = new PresentMonLineParser();
        Assert.False(v20.TryParse("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsUntilDisplayed,MsBetweenDisplayChange", out _));
        Assert.Equal("MsBetweenAppStart", v20.FrametimeColumn);
        Assert.True(v20.TryParse("game.exe,1,0x1,DXGI,1,0,0,Hardware: Legacy Flip,10.0,16.67,8.0,8.6,1,7,6.5,1,20,16.6", out var a));
        Assert.Equal(16.67, a.FrametimeMs, 6);
        Assert.Equal(8.0, a.CpuBusyMs!.Value, 6);

        var v1 = new PresentMonLineParser();
        Assert.False(v1.TryParse("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents,msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange", out _));
        Assert.True(v1.TryParse("\"Jogo, o Retorno.exe\",77,0x1,DXGI,0,0,1,Hardware: Independent Flip,0,1.5,0.2,8.33,1.0,9.0,8.3", out var b));
        Assert.Equal("Jogo, o Retorno.exe", b.Application);
        Assert.Equal(8.33, b.FrametimeMs, 6);
        Assert.Null(b.CpuBusyMs);
    }

    [Fact]
    public void Tail_only_emits_complete_lines()
    {
        var pending = new StringBuilder();
        Assert.Empty(PresentMonLiveCapture.TakeLines(pending, "cs2.exe,1,6.9"));
        var lines = PresentMonLiveCapture.TakeLines(pending, "44\r\nnext,2,");
        Assert.Equal(["cs2.exe,1,6.944"], lines);
        Assert.Equal("next,2,", pending.ToString());
    }

    [Theory]
    [InlineData(@"C:\PM\PresentMon-2.3.0-x64.exe", PresentMonGeneration.V2)]
    [InlineData(@"C:\PM\PresentMon-1.10.0-x64.exe", PresentMonGeneration.V1)]
    [InlineData(@"C:\PM\PresentMon64-1.9.2.exe", PresentMonGeneration.V1)]
    [InlineData(@"C:\PM\PresentMon.exe", PresentMonGeneration.V2)]
    public void Generation_comes_from_the_file_name(string path, PresentMonGeneration expected) =>
        Assert.Equal(expected, PresentMonCli.GenerationOf(path));

    [Fact]
    public void Live_arguments_are_read_only_and_match_each_generation()
    {
        var v2 = PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "cs2.exe", true, @"C:\tmp\a.csv"), PresentMonGeneration.V2);
        Assert.Equal(["--process_name", "cs2.exe", "--output_file", @"C:\tmp\a.csv", "--terminate_on_proc_exit", "--timed", "14400", "--terminate_after_timed",
            "--stop_existing_session", "--session_name", "NEXUS_Live", "--no_console_stats", "--restart_as_admin"], v2);
        var v2Stdout = PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "cs2.exe", false, null), PresentMonGeneration.V2);
        Assert.Contains("--output_stdout", v2Stdout);
        Assert.DoesNotContain("--restart_as_admin", v2Stdout);
        var v1 = PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "cs2.exe", false, null), PresentMonGeneration.V1);
        Assert.Contains("-output_stdout", v1);
        Assert.Contains("-dont_restart_as_admin", v1);
        Assert.All(v1, a => Assert.False(a.StartsWith("--", StringComparison.Ordinal)));
        foreach (var bad in new[] { "cs2", @"C:\x\cs2.exe", "a\"b.exe", "*.exe", "..\\x.exe" })
            Assert.Throws<ArgumentException>(() => PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", bad, false, null), PresentMonGeneration.V2));
    }

    [Fact]
    public void Executable_picker_prefers_the_newest_console_build()
    {
        Assert.Equal(@"D:\Downloads\PresentMon-2.3.0-x64.exe", PresentMonCli.PickExecutable(
            [@"D:\Downloads\PresentMon-1.9.2-x64.exe", @"D:\Downloads\PresentMon-2.3.0-x64.exe", @"D:\Downloads\PresentMon-2.10.0-x64.exe.part", @"D:\Downloads\PresentMon.exe",
             @"D:\Downloads\PresentMonService.exe"]));
        Assert.Null(PresentMonCli.PickExecutable([@"D:\x\setup.exe"]));
    }

    // ------------------------------------------------------------------ estatística ao vivo

    [Fact]
    public void Live_stats_compute_current_fps_sliding_low_and_session_low()
    {
        var s = new LiveFrameStats(windowSeconds: 10);
        for (var i = 0; i < 1000; i++) s.Add(10);                      // 10 s a 100 FPS
        var r = s.Read();
        Assert.Equal(100, r.CurrentFps, 1);
        Assert.Equal(100, r.AverageFps, 1);
        Assert.Equal(100, r.Low1Fps, 1);
        Assert.Equal(10, r.FrametimeMs, 3);
        Assert.Equal(10, r.ElapsedSeconds, 3);

        for (var i = 0; i < 10; i++) s.Add(50);                        // 10 engasgos de 50 ms
        r = s.Read();
        Assert.True(r.Low1Fps < 25, $"1% low deslizante deveria cair: {r.Low1Fps}");
        Assert.True(r.SessionLow1Fps < 25);

        for (var i = 0; i < 1100; i++) s.Add(10);                      // 11 s lisos: os engasgos saem da janela de 10 s
        r = s.Read();
        Assert.Equal(100, r.Low1Fps, 1);
        Assert.True(r.SessionLow1Fps < 40, "a sessão inteira ainda lembra dos engasgos");
    }

    [Fact]
    public void Session_low_matches_the_exact_formula_within_the_bin_resolution()
    {
        var rng = new Random(7);
        var s = new LiveFrameStats();
        var all = new List<double>();
        for (var i = 0; i < 20000; i++)
        {
            var ft = 6 + rng.NextDouble() * 3 + (i % 997 == 0 ? 40 : 0) + (i == 5000 ? 400 : 0);
            all.Add(ft);
            s.Add(ft);
        }
        var exact = FrametimeMetrics.Summarize(all).OnePercentLowAverageFps;
        Assert.InRange(s.Read().SessionLow1Fps, exact * 0.99, exact * 1.01);
        Assert.Equal(1000.0 / 20, LiveFrameStats.Low1([1, 2, 3, 20]), 6);
    }

    [Fact]
    public void Live_trace_keeps_the_worst_frame_per_bucket_and_only_the_last_minute()
    {
        var s = new LiveFrameStats(traceSeconds: 60, traceBucketMs: 50);
        for (var i = 0; i < 9000; i++) s.Add(i == 8000 ? 90 : 10);     // 90 s
        var t = s.Trace();
        Assert.True(t[^1].Seconds - t[0].Seconds <= 60.01);
        Assert.Contains(t, p => p.FrametimeMs == 90);
        Assert.True(t.Count <= 60_000 / 50 + 2);
    }

    [Fact]
    public void Live_delta_waits_30_seconds_and_compares_with_the_reference()
    {
        var reference = Session("cs2.exe", Day(1), 100);
        var s = new LiveFrameStats();
        for (var i = 0; i < 1000; i++) s.Add(1000.0 / 110);
        Assert.Null(LiveComparison.Delta(s.Read(), reference));       // ~9 s: cedo demais
        for (var i = 0; i < 3000; i++) s.Add(1000.0 / 110);
        var d = LiveComparison.Delta(s.Read(), reference);
        Assert.NotNull(d);
        Assert.InRange(d!.AverageFpsPercent, 8, 12);
        Assert.Contains("prévia", d.Text, StringComparison.Ordinal);
        var window = LiveComparison.ReferenceWindow(reference, 10, 40);
        Assert.All(window, p => Assert.InRange(p.Seconds, 10, 40));
        Assert.NotEmpty(window);
    }

    // ------------------------------------------------------------------ antes x depois automático

    private static DateTimeOffset Day(int d, int h = 20) => new(2026, 10, d, h, 0, 0, TimeSpan.Zero);

    /// <summary>Sessão sintética: <paramref name="seconds"/> s a ~<paramref name="fps"/> FPS, com ruído e engasgos raros.</summary>
    private static MeasuredSession Session(string exe, DateTimeOffset at, double fps, double seconds = 300, int seed = 1, SessionConditions? cond = null, bool excluded = false, bool spikes = true)
    {
        var rng = new Random(seed);
        var ft = new List<double>();
        double total = 0;
        while (total < seconds * 1000)
        {
            var f = 1000.0 / fps * (0.9 + rng.NextDouble() * 0.2) + (spikes && rng.NextDouble() < 0.005 ? 15 : 0);
            ft.Add(f);
            total += f;
        }
        var summary = FrametimeMetrics.Summarize(ft);
        var trace = new List<TracePoint>();
        double t = 0;
        for (var i = 0; i < ft.Count; i += Math.Max(1, ft.Count / 500)) { t = ft.Take(i).Sum() / 1000; trace.Add(new TracePoint(t, ft[i])); }
        return new MeasuredSession(Guid.NewGuid().ToString("N"), exe, exe, at, "automático", SessionTag.None, "x.csv", summary, trace, [], 0, "v2")
        {
            Segments = SessionAnalyzer.Segment(ft),
            Conditions = cond,
            Excluded = excluded,
        };
    }

    private static AppliedChange Change(DateTimeOffset at, string title = "Plano de energia: alto desempenho") => new("x.power", title, at, true, Guid.NewGuid());

    [Fact]
    public void Reference_is_the_last_session_before_the_latest_change_of_the_same_game()
    {
        var a = Session("cs2.exe", Day(1), 100);
        var b = Session("cs2.exe", Day(2), 100);
        var other = Session("dota2.exe", Day(3), 80);
        var shortOne = Session("cs2.exe", Day(3), 100, seconds: 30);
        var excluded = Session("cs2.exe", Day(3, 21), 100, excluded: true);
        var c = Session("cs2.exe", Day(5), 110);
        var all = new[] { a, b, other, shortOne, excluded, c };

        var none = AutoCompare.ChooseReference(a, all, []);
        Assert.Null(none.Reference);
        Assert.Contains("Primeira partida", none.Why, StringComparison.Ordinal);

        var noChange = AutoCompare.ChooseReference(c, all, []);
        Assert.Equal(b.Id, noChange.Reference!.Id);                    // anterior válida (curta e excluída ignoradas)
        Assert.Empty(noChange.ChangesBetween);

        var withChange = AutoCompare.ChooseReference(c, all, [Change(Day(4))]);
        Assert.Equal(b.Id, withChange.Reference!.Id);
        Assert.Single(withChange.ChangesBetween);
        Assert.Contains("Plano de energia", withChange.Why, StringComparison.Ordinal);

        var changeBetweenAandB = AutoCompare.ChooseReference(c, all, [Change(Day(1, 22))]);
        Assert.Equal(a.Id, changeBetweenAandB.Reference!.Id);         // o "antes" é a última partida antes da mudança

        Assert.Equal(c.Id, AutoCompare.LiveReference("CS2.EXE", Day(6), all, [])!.Id);
        Assert.Equal(a.Id, AutoCompare.LiveReference("cs2.exe", Day(6), all, [Change(Day(1, 22))])!.Id);
        Assert.Null(AutoCompare.LiveReference("valorant.exe", Day(6), all, []));
    }

    [Fact]
    public void Different_conditions_produce_warnings()
    {
        var a = Session("cs2.exe", Day(1), 100, cond: new SessionConditions { Resolution = "1920x1080", GpuDriver = "32.0.15.6094", OnBattery = false, PresentMode = "Hardware: Independent Flip" });
        var b = Session("cs2.exe", Day(2), 100, seconds: 1200, cond: new SessionConditions { Resolution = "1280x720", GpuDriver = "32.0.15.6614", OnBattery = true, PresentMode = "Composed: Flip" });
        var w = AutoCompare.Warnings(a, b, []);
        Assert.Contains(w, x => x.Contains("Resolução diferente", StringComparison.Ordinal));
        Assert.Contains(w, x => x.Contains("driver de vídeo", StringComparison.Ordinal));
        Assert.Contains(w, x => x.Contains("bateria", StringComparison.Ordinal));
        Assert.Contains(w, x => x.Contains("modo de tela", StringComparison.Ordinal));
        Assert.Contains(w, x => x.Contains("duração muito diferente", StringComparison.Ordinal));
        Assert.Contains(w, x => x.Contains("Nenhuma mudança do NEXUS", StringComparison.Ordinal));

        var same = AutoCompare.Warnings(a, a with { Id = "y" }, [Change(Day(1, 21))]);
        Assert.Single(same);                                           // só o lembrete do mapa/cena
    }

    [Fact]
    public void Segments_comparison_gives_an_honest_verdict()
    {
        var before = Session("cs2.exe", Day(1), 100, seed: 1);
        var better = Session("cs2.exe", Day(2), 115, seed: 2);
        var r = AutoCompare.CompareSegments(before, better);
        Assert.True(r.Paired);
        Assert.Equal(Verdict.Improved, r.Metrics.Single(m => m.Metric.Key == "fps").Verdict);
        var (verdict, tone) = AutoCompare.Verdict(r);
        Assert.Equal("Good", tone);
        Assert.StartsWith("Melhorou ", verdict, StringComparison.Ordinal);
        Assert.Contains("±", verdict, StringComparison.Ordinal);

        var same = AutoCompare.CompareSegments(before, Session("cs2.exe", Day(2), 100, seed: 3));
        Assert.DoesNotContain(same.Metrics, m => m.Verdict is Verdict.Improved or Verdict.Regressed);
        Assert.StartsWith("Sem diferença mensurável", AutoCompare.Verdict(same).Verdict, StringComparison.Ordinal);

        var tiny = AutoCompare.CompareSegments(Session("cs2.exe", Day(1), 100, seconds: 70), Session("cs2.exe", Day(2), 120, seconds: 70));
        Assert.False(tiny.Paired);
        Assert.All(tiny.Metrics, m => Assert.Equal(Verdict.InsufficientData, m.Verdict));
    }

    [Fact]
    public void Three_sessions_each_side_of_a_change_are_compared_as_whole_sessions()
    {
        var sessions = new List<MeasuredSession>();
        for (var i = 0; i < 3; i++) sessions.Add(Session("cs2.exe", Day(1 + i), 100, seed: 10 + i, spikes: false));
        for (var i = 0; i < 3; i++) sessions.Add(Session("cs2.exe", Day(10 + i), 120, seed: 20 + i, spikes: false));
        var c = AutoCompare.Compare(sessions[^1], sessions, [Change(Day(5))]);
        Assert.NotNull(c);
        Assert.Equal("partidas", c!.Mode);
        Assert.True(c.Result.Paired);
        Assert.Equal("Good", c.Tone);
        Assert.Contains("Antes x depois", SessionLibrary.Report(c), StringComparison.Ordinal);

        var two = AutoCompare.Compare(sessions[3], sessions.Take(4).ToList(), [Change(Day(5))]);
        Assert.Equal("trechos", two!.Mode);
    }

    [Fact]
    public void Change_history_tracks_applied_and_reverted_actions()
    {
        var run = Guid.NewGuid();
        var rows = new[]
        {
            new JournalRow(run, "power.plan", "Plano de energia", "t=1", "Planned", Day(1)),
            new JournalRow(run, "power.plan", "Plano de energia", "t=1", "Verified", Day(1, 21)),
            new JournalRow(run, "gpu.hags", "Agendamento de GPU", "t=2", "Verified", Day(2)),
            new JournalRow(run, "gpu.hags", "Agendamento de GPU", "t=2", "Reverted", Day(3)),
            new JournalRow(run, "only.planned", "Nunca aplicada", "t=3", "Planned", Day(3)),
            new JournalRow(run, "failed", "Falhou", "t=4", "Failed", Day(3)),
        };
        var changes = ChangeHistory.From(rows);
        Assert.Equal(2, changes.Count);
        Assert.True(changes.Single(c => c.ActionId == "power.plan").Active);
        Assert.False(changes.Single(c => c.ActionId == "gpu.hags").Active);
        Assert.Equal("gpu.hags", changes[0].ActionId);                  // mais recente primeiro
        Assert.Single(ChangeHistory.Between(changes, Day(1, 22), Day(4)));
    }

    // ------------------------------------------------------------------ sessões: gravação ao vivo, biblioteca, limpeza, exportação

    [Fact]
    public void Live_session_writes_a_csv_that_the_analyzer_reads_back()
    {
        var game = new DetectedGame("CS2", "cs2.exe", 4242, DetectionReason.BuiltIn, "x");
        var live = new LiveSession(game, Day(1), automatic: true);
        for (var i = 0; i < 6000; i++) live.Add(new LiveFrame("cs2.exe", 4242, i % 100 == 0 ? 20 : 7, 4, 5, "Hardware: Independent Flip"));
        live.Add(new LiveFrame("chrome.exe", 1, 16, null, null, null));   // outro processo: ignorado
        Assert.Equal(6000, live.Frames);
        var csv = Path.Combine(_dir, "ao-vivo.csv");
        live.WriteCsv(csv);
        var s = SessionAnalyzer.Analyze("id", "n", csv, "automático", Day(1), "cs2.exe");
        Assert.Equal(6000, s.Summary.Frames);
        Assert.Equal("Hardware: Independent Flip", s.Conditions!.PresentMode);
        Assert.InRange(s.Summary.AverageFps, 135, 145);
        Assert.NotEmpty(s.Segments);
    }

    [Fact]
    public void Store_keeps_new_fields_cleans_old_sessions_but_keeps_latest_per_game_and_tagged()
    {
        var store = new SessionStore(Path.Combine(_dir, "sessoes"));
        var old1 = Session("cs2.exe", DateTimeOffset.Now.AddDays(-40), 100) with { CsvFile = "a.csv" };
        var old2 = Session("cs2.exe", DateTimeOffset.Now.AddDays(-35), 100) with { CsvFile = "b.csv", Tag = SessionTag.Before };
        var recent = Session("cs2.exe", DateTimeOffset.Now.AddDays(-1), 100) with { CsvFile = "c.csv", Applied = [Change(Day(1))], GameName = "Counter-Strike 2" };
        var onlyDota = Session("dota2.exe", DateTimeOffset.Now.AddDays(-60), 90) with { CsvFile = "d.csv" };
        foreach (var s in new[] { old1, old2, recent, onlyDota }) store.Save(s);

        var (list, bad) = store.List();
        Assert.Equal(0, bad);
        var loaded = list.Single(s => s.Id == recent.Id);
        Assert.Equal("Counter-Strike 2", loaded.DisplayName);
        Assert.Single(loaded.Applied);
        Assert.NotEmpty(loaded.Segments);

        Assert.Equal(1, store.Cleanup(DateTimeOffset.Now.AddDays(-30)));   // só old1 sai
        var left = store.List().Sessions.Select(s => s.Id).ToHashSet();
        Assert.DoesNotContain(old1.Id, left);
        Assert.Contains(old2.Id, left);                                 // marcada como antes
        Assert.Contains(onlyDota.Id, left);                             // última do jogo
    }

    [Fact]
    public void Library_groups_by_game_with_trend_and_exports_csv()
    {
        var sessions = new[]
        {
            Session("cs2.exe", Day(1), 100, seed: 1), Session("cs2.exe", Day(2), 110, seed: 2) with { GameName = "Counter-Strike 2" },
            Session("dota2.exe", Day(3), 80, seed: 3),
        };
        var games = SessionLibrary.Group(sessions);
        Assert.Equal("dota2.exe", games[0].Key);                         // jogado por último
        var cs = games.Single(g => g.Key == "cs2.exe");
        Assert.Equal(2, cs.Sessions);
        Assert.Equal("Counter-Strike 2", cs.Name);
        Assert.Equal(2, cs.AverageFpsTrend.Count);
        Assert.NotNull(cs.Low1ChangePercent);

        var csv = SessionLibrary.SummaryCsv(sessions);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("jogo;executavel;data", lines[0], StringComparison.Ordinal);
        Assert.Contains(",", lines[1], StringComparison.Ordinal);             // vírgula decimal (pt-BR)
    }

    // ------------------------------------------------------------------ motor automático (falsos)

    private sealed class FakeProbe : IProcessProbe
    {
        public List<RunningProcess> Processes { get; } = [];
        public ProcessSnapshot Read() => new(Processes.ToList(), null);
        public bool IsAlive(int pid) => Processes.Any(p => p.Pid == pid);
    }

    private sealed class FakeCapture : ILiveCapture
    {
        public string Log => "";
        public bool Ended { get; private set; }
        public void Finish() => Ended = true;
        public void Dispose() { }
    }

    private sealed class FakeStarter : ILiveCaptureStarter
    {
        public bool Available { get; set; } = true;
        public Action<LiveFrame>? OnFrame { get; private set; }
        public int Starts { get; private set; }
        public ILiveCapture Start(string processName, Action<LiveFrame> onFrame) { Starts++; OnFrame = onFrame; return new FakeCapture(); }
    }

    [Fact]
    public void Engine_starts_measuring_when_the_game_opens_and_hands_over_the_session_when_it_closes()
    {
        var probe = new FakeProbe();
        var starter = new FakeStarter();
        var engine = new AutoMeasureEngine(probe, starter, new GameSessionTracker(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2)));
        var rules = new DetectionRules([], [], [], false);
        var t = Day(1);
        Assert.Equal(GameEventKind.None, engine.Poll(rules, true, t).Kind);
        probe.Processes.Add(new RunningProcess(42, "cs2.exe", null));
        Assert.Equal(GameEventKind.None, engine.Poll(rules, true, t.AddSeconds(2)).Kind);
        var started = engine.Poll(rules, true, t.AddSeconds(6));
        Assert.Equal(GameEventKind.Started, started.Kind);
        Assert.True(started.CaptureStarted);
        Assert.Equal(1, starter.Starts);
        for (var i = 0; i < 500; i++) starter.OnFrame!(new LiveFrame("cs2.exe", 42, 5, null, null, null));
        Assert.Equal(500, engine.Session!.Frames);

        probe.Processes.Clear();
        engine.Poll(rules, true, t.AddSeconds(60));
        var stopped = engine.Poll(rules, true, t.AddSeconds(63));
        Assert.Equal(GameEventKind.Stopped, stopped.Kind);
        Assert.Equal(500, stopped.Finished!.Frames);
        Assert.Null(engine.Session);
    }

    [Fact]
    public void Engine_does_not_capture_when_automatic_is_off_or_presentmon_is_missing()
    {
        var probe = new FakeProbe { Processes = { new RunningProcess(42, "cs2.exe", null) } };
        var starter = new FakeStarter();
        var engine = new AutoMeasureEngine(probe, starter, new GameSessionTracker(TimeSpan.Zero));
        var ev = engine.Poll(new DetectionRules([], [], [], false), false, Day(1));
        Assert.Equal(GameEventKind.Started, ev.Kind);
        Assert.False(ev.CaptureStarted);
        Assert.Equal(0, starter.Starts);

        var noPm = new AutoMeasureEngine(probe, new FakeStarter { Available = false }, new GameSessionTracker(TimeSpan.Zero));
        var ev2 = noPm.Poll(new DetectionRules([], [], [], false), true, Day(1));
        Assert.False(ev2.CaptureStarted);
        Assert.Contains("PresentMon", ev2.Note, StringComparison.Ordinal);

        Assert.True(engine.StartManual(engine.CurrentGame!, Day(1)));   // à mão continua possível
        Assert.False(engine.StartManual(engine.CurrentGame!, Day(1)));  // uma captura por vez
    }

    // ------------------------------------------------------------------ preferências e atalho

    [Fact]
    public void Preferences_round_trip_and_drop_invalid_entries()
    {
        var path = Path.Combine(_dir, "medir.json");
        var p = new MeasurePreferences
        {
            AutoMeasure = true, OverlayOnGameStart = true, OverlayCorner = OverlayCorner.BottomRight, HotkeyEnabled = true, OverlayHotkey = "alt+f10",
            UserGames = ["meujogo.exe", "MEUJOGO.exe", @"C:\x\y.exe", "a*b.exe", "semextensao"], ExcludedGames = ["obs64.exe"], MinSessionSeconds = 5, KeepDays = -3,
        };
        Assert.True(p.Save(path));
        var l = MeasurePreferences.Load(path);
        Assert.True(l.AutoMeasure);
        Assert.True(l.OverlayOnGameStart);
        Assert.Equal(OverlayCorner.BottomRight, l.OverlayCorner);
        Assert.Equal("Alt+F10", l.OverlayHotkey);
        Assert.Equal(["meujogo.exe"], l.UserGames);
        Assert.Equal(["obs64.exe"], l.ExcludedGames);
        Assert.Equal(20, l.MinSessionSeconds);
        Assert.Equal(0, l.KeepDays);

        File.WriteAllText(path, "{ quebrado");
        var d = MeasurePreferences.Load(path);
        Assert.False(d.AutoMeasure);
        Assert.False(d.HotkeyEnabled);                                  // atalho global desligado por padrão
        Assert.Equal("Ctrl+Shift+F", d.OverlayHotkey);

        var moved = new MeasurePreferences().WithUserGame("x.exe").WithExcluded("x.exe");
        Assert.Empty(moved.UserGames);
        Assert.Equal(["x.exe"], moved.ExcludedGames);
        Assert.Empty(new MeasurePreferences().WithUserGame(@"C:\mal.exe").UserGames);
    }

    [Theory]
    [InlineData("Ctrl+Shift+F", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x46, "Ctrl+Shift+F")]
    [InlineData("shift + ctrl + f", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x46, "Ctrl+Shift+F")]
    [InlineData("Alt+F10", HotkeyModifiers.Alt, 0x79, "Alt+F10")]
    [InlineData("Win+Alt+9", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x39, "Alt+Win+9")]
    public void Hotkey_parses_to_registerhotkey_values(string text, HotkeyModifiers mods, int vk, string normalized)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(mods, h.Modifiers);
        Assert.Equal(vk, h.VirtualKey);
        Assert.Equal(normalized, h.ToString());
    }

    [Theory]
    [InlineData("F")]
    [InlineData("Shift+F")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Esc")]
    [InlineData("Ctrl+A+B")]
    [InlineData("")]
    public void Hotkey_rejects_unsafe_or_unknown_combinations(string text) => Assert.False(Hotkey.TryParse(text, out _));

    // ------------------------------------------------------------------ mostrador (sem abrir janela)

    private sealed class FakeSurface : IOverlaySurface
    {
        public List<string> Calls { get; } = [];
        public OverlayContent? Last { get; private set; }
        public void Show(OverlayCorner corner) => Calls.Add("show:" + corner);
        public void Hide() => Calls.Add("hide");
        public void Render(OverlayContent content) { Calls.Add("render"); Last = content; }
    }

    [Fact]
    public void Overlay_shows_only_when_enabled_and_a_game_is_running()
    {
        var surface = new FakeSurface();
        var o = new OverlayController(surface);
        o.SetEnabled(true);
        Assert.False(o.Visible);                                        // ligado, mas sem jogo
        o.OnGame(true, autoEnable: false);
        Assert.True(o.Visible);
        Assert.Equal("show:TopLeft", surface.Calls[0]);
        o.Toggle();                                                     // atalho desliga
        Assert.False(o.Visible);
        Assert.Equal("hide", surface.Calls[^1]);
        o.OnGame(false, false);
        o.OnGame(true, autoEnable: true);                               // "ligar ao abrir jogo"
        Assert.True(o.Enabled);
        Assert.True(o.Visible);
        o.SetCorner(OverlayCorner.BottomRight);
        Assert.Equal("show:BottomRight", surface.Calls[^1]);
        o.OnGame(false, false);
        Assert.False(o.Visible);
        o.SetPreview(true);                                             // prévia na página, sem jogo
        Assert.True(o.Visible);
    }

    [Fact]
    public void Overlay_renders_readings_and_positions_in_corners()
    {
        var surface = new FakeSurface();
        var o = new OverlayController(surface);
        o.Update(new LiveReading(144, 140, 98, 95, 6.9, 1000, 10), default);
        Assert.DoesNotContain("render", surface.Calls);                 // invisível: não desenha
        o.SetPreview(true);
        o.SetEnabled(true);
        Assert.True(surface.Last!.Waiting);
        o.Update(new LiveReading(144, 140, 98, 95, 6.94, 1000, 10), new SystemLoad(37, 99, 71));
        Assert.Equal("144 FPS", surface.Last!.Fps);
        Assert.Equal("1% low 98", surface.Last.Low1);
        Assert.Equal("6,9 ms", surface.Last.Frametime);
        Assert.Equal("CPU 37% · GPU 99% · 71 °C", surface.Last.System);
        o.ShowSystem = false;
        o.Update(new LiveReading(144, 140, 98, 95, 6.94, 1000, 10), new SystemLoad(37, 99, 71));
        Assert.Null(surface.Last.System);

        Assert.Equal((16.0, 16.0), OverlayController.Position(OverlayCorner.TopLeft, 0, 0, 1920, 1080, 160, 90));
        Assert.Equal((1920 - 160 - 16.0, 1080 - 90 - 16.0), OverlayController.Position(OverlayCorner.BottomRight, 0, 0, 1920, 1080, 160, 90));
        Assert.Equal((1920 - 160 - 16.0, 16.0), OverlayController.Position(OverlayCorner.TopRight, 0, 0, 1920, 1080, 160, 90));
    }

    [Fact]
    public void Segmenting_drops_short_tails_and_keeps_equal_chunks()
    {
        var ft = Enumerable.Repeat(10.0, 9500).ToList();               // 95 s a 100 FPS
        var seg = SessionAnalyzer.Segment(ft);
        Assert.Equal(3, seg.Count);                                     // 30+30+30; os 5 s finais ficam de fora
        Assert.All(seg, s => Assert.Equal(100, s.AverageFps, 3));
        Assert.Equal(30, seg[1].StartSeconds, 3);
        Assert.Equal(4, SessionAnalyzer.Segment(Enumerable.Repeat(10.0, 11500).ToList()).Count); // 115 s: o último (25 s ≥ 80%) entra
        Assert.Equal(1000.0 / 10, seg[0].Low1Fps, 3);
    }
}
