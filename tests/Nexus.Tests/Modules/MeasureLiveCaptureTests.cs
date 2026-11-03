using Nexus.Measurement;

namespace Nexus.Tests.Modules;

/// <summary>
/// Captura ao vivo robusta ("aguardando quadros" para sempre no PC real): processo certo (lançador x quem desenha), recomeço sem
/// filtro escolhendo quem desenha, falta de permissão detectada pelo texto do PresentMon 2.6, nova tentativa sem
/// --track_frame_type, estados verdadeiros e prazos. PresentMon falso (executor de mentira) — nada roda de verdade.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MeasureLiveCaptureTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,FrameTime,CPUBusy,GPUBusy,PresentMode";
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

    private sealed class FakeRun(IReadOnlyList<string> args, string? file, Action<string> onLine, Action<string> onError) : IPresentMonRun
    {
        public IReadOnlyList<string> Args { get; } = args;
        public string? File { get; } = file;
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public bool OutputStarted { get; set; }
        public bool Terminated { get; private set; }
        public void Line(string l) { OutputStarted = true; onLine(l); }
        public void Error(string l) => onError(l);
        public void Frame(string app, int pid, double ms = 10) => Line($"{app},{pid},0x1,DXGI,{ms.ToString(System.Globalization.CultureInfo.InvariantCulture)},4,5,Hardware: Independent Flip");
        public void Exit(int code) { HasExited = true; ExitCode = code; }
        public void Terminate() => Terminated = true;
        public void Dispose() { }
    }

    private sealed class FakeRunner : IPresentMonRunner
    {
        public List<FakeRun> Runs { get; } = [];
        public FakeRun Last => Runs[^1];
        public IPresentMonRun Start(string exe, IReadOnlyList<string> arguments, string? outputCsv, Action<string> onLine, Action<string> onError)
        {
            var r = new FakeRun(arguments, outputCsv, onLine, onError);
            Runs.Add(r);
            return r;
        }
    }

    private sealed class Rig
    {
        public DateTimeOffset Now = T0;
        public FakeRunner Runner { get; } = new();
        public List<LiveFrame> Frames { get; } = [];
        public List<string> Diag { get; } = [];
        public PresentMonLiveCapture Capture { get; }

        public Rig(LiveTarget target, bool elevate = false, bool frameType = false)
        {
            var options = new LiveCaptureOptions(@"C:\NEXUS\tools\PresentMon\PresentMon.exe", PresentMonGeneration.V2, elevate, frameType) { OwnPid = 999 };
            Capture = new PresentMonLiveCapture(options, target, f => Frames.Add(f), Runner, () => Now, Diag.Add);
            Capture.Begin();
        }

        public void Tick(double seconds, ProcessSnapshot? snap = null)
        {
            Now = Now.AddSeconds(seconds);
            Capture.Tick(Now, snap ?? new ProcessSnapshot([], null));
        }

        /// <summary>Quadros em ritmo de jogo: <paramref name="count"/> quadros espalhados no próximo segundo.</summary>
        public void Frames60(FakeRun run, string app, int pid, int count = 60)
        {
            for (var i = 0; i < count; i++) { Now = Now.AddMilliseconds(1000.0 / count); run.Frame(app, pid, 1000.0 / count); }
        }
    }

    private static ProcessSnapshot Snap(int? foreground, params (int Pid, string Name)[] procs) =>
        new(procs.Select(p => new RunningProcess(p.Pid, p.Name, null)).ToList(), foreground is { } f ? new ForegroundWindow(f, 1920, 1080, true) : null);

    private static List<string> ValuesOf(IReadOnlyList<string> args, string option) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == option && x.i + 1 < args.Count).Select(x => args[x.i + 1]).ToList();

    // ------------------------------------------------------------------ nomes: lançador x quem desenha

    [Theory]
    [InlineData("FortniteClient-Win64-Shipping_EAC_EOS.exe", "FortniteClient-Win64-Shipping.exe")]
    [InlineData("FortniteClient-Win64-Shipping_EAC.exe", "FortniteClient-Win64-Shipping.exe")]
    [InlineData("FortniteClient-Win64-Shipping_BE.exe", "FortniteClient-Win64-Shipping.exe")]
    [InlineData("FortniteLauncher.exe", "FortniteClient-Win64-Shipping.exe")]
    [InlineData("VALORANT.exe", "VALORANT-Win64-Shipping.exe")]
    [InlineData("LeagueClient.exe", "League of Legends.exe")]
    [InlineData("PlayGTAV.exe", "GTA5.exe")]
    [InlineData("start_protected_game.exe", "eldenring.exe")]
    [InlineData("MarvelRivals_Launcher.exe", "Marvel-Win64-Shipping.exe")]
    [InlineData("Palworld.exe", "Palworld-Win64-Shipping.exe")]          // padrão Unreal genérico
    [InlineData("SomeGame_EAC.exe", "SomeGame.exe")]                     // sufixo de anticheat genérico
    public void Alias_map_points_launchers_to_the_process_that_draws(string launcher, string render)
    {
        Assert.Contains(render, GameAliases.RenderCandidates(launcher), StringComparer.OrdinalIgnoreCase);
        var names = GameAliases.CaptureNames(launcher);
        Assert.Equal(launcher, names[0]);
        Assert.Contains(render, names, StringComparer.OrdinalIgnoreCase);
        Assert.All(names, n => Assert.True(PresentMonCli.IsValidExeName(n)));
    }

    [Fact]
    public void Bootstrapper_detection_and_render_process_lookup()
    {
        Assert.True(GameAliases.IsBootstrapper("FortniteClient-Win64-Shipping_EAC_EOS.exe"));
        Assert.True(GameAliases.IsBootstrapper("FortniteLauncher.exe"));
        Assert.False(GameAliases.IsBootstrapper("FortniteClient-Win64-Shipping.exe"));
        Assert.False(GameAliases.IsBootstrapper("cs2.exe"));
        Assert.Empty(GameAliases.RenderCandidates("FortniteClient-Win64-Shipping.exe"));
        var running = new[] { new RunningProcess(10, "FortniteClient-Win64-Shipping_EAC_EOS", null), new RunningProcess(11, "FortniteClient-Win64-Shipping", @"C:\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe") };
        Assert.Equal(11, GameAliases.RunningRenderProcess("FortniteClient-Win64-Shipping_EAC_EOS.exe", running)!.Pid);
        Assert.Null(GameAliases.RunningRenderProcess("cs2.exe", running));
    }

    [Fact]
    public void Detector_prefers_the_process_that_draws_over_the_anticheat_bootstrapper()
    {
        var lib = new KnownGame("Fortnite", "Epic", @"C:\Program Files\Epic Games\Fortnite", null);
        var snap = new ProcessSnapshot(
        [
            new RunningProcess(100, "FortniteClient-Win64-Shipping_EAC_EOS.exe", @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping_EAC_EOS.exe"),
            new RunningProcess(200, "FortniteClient-Win64-Shipping.exe", @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe"),
        ], null);
        var g = GameMatcher.Find(snap, new DetectionRules([lib], [], [], false));
        Assert.Equal("FortniteClient-Win64-Shipping.exe", g!.ExeName);
        Assert.Equal(200, g.Pid);
    }

    [Fact]
    public void Engine_measures_the_render_process_when_the_bootstrapper_was_detected_and_keeps_the_game_alive_through_it()
    {
        var probe = new Probe { Processes = { new RunningProcess(100, "FortniteClient-Win64-Shipping_EAC_EOS.exe", null) } };
        var starter = new RecordingStarter();
        var engine = new AutoMeasureEngine(probe, starter, new GameSessionTracker(TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        var rules = new DetectionRules([], ["FortniteClient-Win64-Shipping_EAC_EOS.exe"], [], false);
        Assert.Equal(GameEventKind.Started, engine.Poll(rules, false, T0).Kind);   // só o inicializador por enquanto
        probe.Processes.Add(new RunningProcess(200, "FortniteClient-Win64-Shipping.exe", null));
        engine.Poll(rules, false, T0.AddSeconds(2));
        Assert.True(engine.StartManual(engine.CurrentGame!, T0.AddSeconds(2)));
        Assert.Equal("FortniteClient-Win64-Shipping.exe", starter.Target!.ExeName);  // partida agrupada por quem desenha
        Assert.Equal(200, starter.Target.Pid);
        starter.OnFrame!(new LiveFrame("FortniteClient-Win64-Shipping.exe", 200, 7, null, null, null));
        Assert.Equal(1, engine.Session!.Frames);
        // O inicializador fecha; o jogo (o que desenha) continua: a partida não termina.
        probe.Processes.RemoveAt(0);
        engine.Poll(rules, false, T0.AddSeconds(4));
        Assert.Equal(GameEventKind.None, engine.Poll(rules, false, T0.AddSeconds(10)).Kind);
        Assert.NotNull(engine.Session);
        Assert.True(starter.Ticks >= 2);   // o motor repassa o relógio para a captura
    }

    // ------------------------------------------------------------------ captura filtrada: nomes esperados

    [Fact]
    public void Filtered_capture_asks_for_launcher_and_render_names_and_locks_on_the_one_that_draws()
    {
        var rig = new Rig(LiveTarget.For(new DetectedGame("Fortnite", "FortniteClient-Win64-Shipping_EAC_EOS.exe", 100, DetectionReason.Library, "Epic")));
        var run = rig.Runner.Last;
        Assert.Equal(["FortniteClient-Win64-Shipping_EAC_EOS.exe", "FortniteClient-Win64-Shipping.exe"], ValuesOf(run.Args, "--process_name"));
        Assert.Contains("--terminate_on_proc_exit", run.Args);
        Assert.Equal(LiveCaptureState.Searching, rig.Capture.Status.State);
        run.Line(Header);
        rig.Frames60(run, "FortniteClient-Win64-Shipping.exe", 200);
        Assert.Equal(60, rig.Frames.Count);
        var st = rig.Capture.Status;
        Assert.Equal(LiveCaptureState.Measuring, st.State);
        Assert.Equal("FortniteClient-Win64-Shipping.exe", st.Target);
        Assert.Equal(200, st.Pid);
        Assert.False(st.Fallback);
        Assert.Contains(rig.Diag, d => d.Contains("medindo FortniteClient-Win64-Shipping.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_processes_with_the_game_name_do_not_mix_frames()
    {
        var rig = new Rig(new LiveTarget("game.exe", 0, ["game.exe"]));
        var run = rig.Runner.Last;
        run.Line(Header);
        run.Frame("game.exe", 1);
        run.Frame("game.exe", 2);
        run.Frame("game.exe", 1);
        Assert.Equal([1, 1], rig.Frames.Select(f => f.ProcessId));
    }

    // ------------------------------------------------------------------ recomeço sem filtro e escolha de quem desenha

    [Fact]
    public void No_frames_for_8_s_restarts_without_process_filter_and_locks_on_the_foreground_process_that_draws()
    {
        var rig = new Rig(LiveTarget.For(new DetectedGame("Jogo", "Jogo.exe", 50, DetectionReason.Library, "Steam")), frameType: true);
        var first = rig.Runner.Last;
        Assert.Contains("--track_frame_type", first.Args);
        first.Line(Header);
        rig.Tick(4);
        Assert.Single(rig.Runner.Runs);
        Assert.Equal(LiveCaptureState.Searching, rig.Capture.Status.State);
        rig.Tick(4.5);                                                     // 8,5 s sem quadros
        Assert.Equal(2, rig.Runner.Runs.Count);
        Assert.True(first.Terminated);
        var all = rig.Runner.Last;
        Assert.Empty(ValuesOf(all.Args, "--process_name"));                // sem filtro de processo
        Assert.DoesNotContain("--terminate_on_proc_exit", all.Args);
        Assert.DoesNotContain("--track_frame_type", all.Args);             // recomeço "seguro"
        Assert.Contains("dwm.exe", ValuesOf(all.Args, "--exclude"));
        Assert.Contains("Nexus.Desktop.exe", ValuesOf(all.Args, "--exclude"));
        Assert.True(rig.Capture.AllProcessesMode);

        all.Line(Header);
        // Navegador (que escapou da exclusão por ter outro nome), o próprio NEXUS e o jogo de verdade (nome inesperado) desenhando.
        for (var i = 0; i < 120; i++)
        {
            rig.Now = rig.Now.AddMilliseconds(10);
            all.Frame("SomeBrowser.exe", 300);
            all.Frame("Nexus.Desktop.exe", 999);
            if (i % 2 == 0) all.Frame("Jogo-Real.exe", 400);
        }
        Assert.Empty(rig.Frames);                                          // nada aceito antes da escolha
        rig.Tick(0.1, Snap(400, (400, "Jogo-Real.exe"), (300, "SomeBrowser.exe"), (50, "Jogo.exe")));
        var st = rig.Capture.Status;
        Assert.Equal("Jogo-Real.exe", st.Target);
        Assert.Equal(400, st.Pid);
        Assert.True(st.Fallback);
        all.Frame("SomeBrowser.exe", 300);
        all.Frame("Jogo-Real.exe", 400);
        Assert.Single(rig.Frames);
        Assert.Equal(400, rig.Frames[0].ProcessId);
        Assert.Equal(LiveCaptureState.Measuring, rig.Capture.Status.State);
    }

    [Fact]
    public void Filtered_target_that_closes_without_drawing_goes_straight_to_the_unfiltered_search()
    {
        var rig = new Rig(new LiveTarget("Launcher-ish.exe", 7, ["Launcher-ish.exe"]));
        rig.Runner.Last.Exit(0);                                           // --terminate_on_proc_exit: o lançador fechou
        rig.Tick(1);
        Assert.Equal(2, rig.Runner.Runs.Count);
        Assert.Empty(ValuesOf(rig.Runner.Last.Args, "--process_name"));
        Assert.False(rig.Capture.Ended);
    }

    [Fact]
    public void Elevated_fallback_stays_filtered_by_the_foreground_process_so_presentmon_still_ends_with_the_game()
    {
        var rig = new Rig(new LiveTarget("Jogo.exe", 50, ["Jogo.exe"]), elevate: true);
        var first = rig.Runner.Last;
        Assert.NotNull(first.File);
        Assert.Contains("--restart_as_admin", first.Args);
        first.OutputStarted = true;
        rig.Tick(9, Snap(400, (400, "Jogo-Real.exe"), (50, "Jogo.exe")));
        var second = rig.Runner.Last;
        Assert.Equal(2, rig.Runner.Runs.Count);
        Assert.Contains("Jogo-Real.exe", ValuesOf(second.Args, "--process_name"));
        Assert.Contains("--terminate_on_proc_exit", second.Args);
        Assert.Contains("--stop_existing_session", second.Args);           // a nova sessão encerra a do elevado anterior
    }

    [Fact]
    public void Picker_prefers_game_pid_then_expected_names_then_foreground_then_top_presenter()
    {
        var p = new List<PresenterInfo>
        {
            new(1, "dwm.exe", 144), new(2, "chrome.exe", 60), new(3, "Jogo-Win64-Shipping.exe", 90), new(4, "<unknown>", 120),
            new(5, "Overlay.exe", 30), new(999, "Nexus.Desktop.exe", 60), new(6, "Lento.exe", 2),
        };
        Assert.Equal(6, LiveTargetPicker.Pick([.. p.Where(x => x.Pid != 6), new(6, "Lento.exe", 6)], ["Jogo-Win64-Shipping.exe"], 6, 4, ownPid: 999)!.Pid); // PID do jogo
        Assert.Equal(3, LiveTargetPicker.Pick(p, ["Jogo.exe", "Jogo-Win64-Shipping.exe"], 50, 4, ownPid: 999)!.Pid);
        Assert.Equal(4, LiveTargetPicker.Pick(p, ["Outro.exe"], null, 4, ownPid: 999)!.Pid);       // em primeiro plano ("<unknown>" vale)
        Assert.Equal(4, LiveTargetPicker.Pick(p, [], null, 2, ownPid: 999)!.Pid);                  // navegador em primeiro plano: não
        Assert.Equal(3, LiveTargetPicker.Pick(p.Where(x => x.Pid != 4).ToList(), [], null, null, ownPid: 999)!.Pid); // o que mais desenha
        Assert.Null(LiveTargetPicker.Pick(p.Where(x => x.Pid is 1 or 2 or 999 or 6).ToList(), [], null, 999, ownPid: 999));
        Assert.Null(LiveTargetPicker.Pick([new(3, "Jogo-Win64-Shipping.exe", 90)], [], null, null, ["Jogo-Win64-Shipping.exe"]));  // excluído pelo usuário
    }

    [Fact]
    public void Presenter_window_counts_frames_per_process_in_the_recent_window()
    {
        var w = new PresenterWindow(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 120; i++) w.Add(10, "a.exe", T0.AddMilliseconds(i * 10));
        w.Add(11, "<unknown>", T0);
        w.Add(11, "b.exe", T0.AddSeconds(1));                              // nome conhecido depois substitui "<unknown>"
        var s = w.Snapshot(T0.AddSeconds(1.5));
        Assert.Equal(10, s[0].Pid);
        Assert.Equal(60, s[0].Fps, 1);
        Assert.Equal("b.exe", s.Single(x => x.Pid == 11).Name);
        Assert.Empty(w.Snapshot(T0.AddSeconds(10)));
    }

    // ------------------------------------------------------------------ permissão (textos reais do PresentMon 2.6)

    [Fact]
    public void Access_denied_from_presentmon_ends_with_no_permission_instead_of_waiting()
    {
        var rig = new Rig(new LiveTarget("cs2.exe", 1, ["cs2.exe"]));
        var run = rig.Runner.Last;
        run.Error("error: failed to start trace session: access denied.");
        run.Error("       PresentMon requires either administrative privileges or to be run by a user in the");
        run.Error("       \"Performance Log Users\" user group.  View the readme for more details.");
        run.Exit(6);
        rig.Tick(0.5);
        var st = rig.Capture.Status;
        Assert.Equal(LiveCaptureState.NoPermission, st.State);
        Assert.Equal(PresentMonFailure.AccessDenied, st.Failure);
        Assert.True(rig.Capture.Ended);
        Assert.Single(rig.Runner.Runs);                                    // não fica tentando
        Assert.Contains("access denied", rig.Capture.Log, StringComparison.Ordinal);
        Assert.Contains(rig.Diag, d => d.Contains("código 6", StringComparison.Ordinal));
        Assert.Contains(rig.Diag, d => d.Contains("Performance Log Users", StringComparison.Ordinal));
    }

    [Fact]
    public void Declined_windows_prompt_is_no_permission_and_pending_prompt_is_waiting_permission_with_timeout()
    {
        var rig = new Rig(new LiveTarget("cs2.exe", 1, ["cs2.exe"]), elevate: true);
        rig.Tick(2);
        Assert.Equal(LiveCaptureState.WaitingPermission, rig.Capture.Status.State);
        Assert.False(rig.Capture.Status.TimedOut);
        rig.Tick(14);
        Assert.True(rig.Capture.Status.TimedOut);                          // 16 s: a interface explica o que fazer
        rig.Runner.Last.Error("error: failed to elevate privilege: cancelled.");
        rig.Runner.Last.Exit(2);
        rig.Tick(1);
        Assert.Equal(LiveCaptureState.NoPermission, rig.Capture.Status.State);
        Assert.Equal(PresentMonFailure.ElevationCancelled, rig.Capture.Status.Failure);
    }

    [Theory]
    [InlineData(6, "error: failed to start trace session: access denied.", PresentMonFailure.AccessDenied)]
    [InlineData(2, "error: failed to elevate privilege: cancelled.", PresentMonFailure.ElevationCancelled)]
    [InlineData(2, "error: failed to elevate privilege: error code 1223.", PresentMonFailure.ElevationFailed)]
    [InlineData(1, "error: unrecognized option '--track_frame_type'.", PresentMonFailure.BadArguments)]
    [InlineData(6, "error: a trace session named \"NEXUS_Live\" is already running. Use --stop_existing_session", PresentMonFailure.SessionConflict)]
    [InlineData(0, "", PresentMonFailure.TargetExited)]
    [InlineData(null, "warning: PresentMon requires elevated privilege in order to query processes that are", PresentMonFailure.None)]
    [InlineData(5, "error: something else", PresentMonFailure.Other)]
    public void Diagnosis_reads_presentmon_26_messages(int? code, string log, PresentMonFailure expected) =>
        Assert.Equal(expected, PresentMonDiagnosis.Classify(code, log));

    [Fact]
    public void Warning_about_unknown_processes_is_not_an_error()
    {
        var rig = new Rig(new LiveTarget("cs2.exe", 1, ["cs2.exe"]));
        var run = rig.Runner.Last;
        run.Error("warning: PresentMon requires elevated privilege in order to query processes that are");
        run.Line(Header);
        rig.Frames60(run, "cs2.exe", 1);
        rig.Tick(0.1, Snap(1, (1, "cs2.exe")));
        Assert.Equal(LiveCaptureState.Measuring, rig.Capture.Status.State);
        Assert.False(rig.Capture.Ended);
    }

    // ------------------------------------------------------------------ --track_frame_type nunca quebra a captura

    [Fact]
    public void Rejected_track_frame_type_is_retried_without_the_option()
    {
        var rig = new Rig(new LiveTarget("cs2.exe", 1, ["cs2.exe"]), frameType: true);
        var first = rig.Runner.Last;
        Assert.Contains("--track_frame_type", first.Args);
        first.Error("error: unrecognized option '--track_frame_type'.");
        first.Exit(1);
        rig.Tick(0.5);
        Assert.Equal(2, rig.Runner.Runs.Count);
        var second = rig.Runner.Last;
        Assert.DoesNotContain("--track_frame_type", second.Args);
        Assert.Equal(["cs2.exe"], ValuesOf(second.Args, "--process_name")); // continua filtrado
        Assert.False(rig.Capture.TracksFrameType);
        second.Line(Header);
        second.Frame("cs2.exe", 1);
        Assert.Single(rig.Frames);
        Assert.Contains(rig.Diag, d => d.Contains("sem a opção", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_arguments_without_frame_type_fail_with_the_message_and_session_conflict_retries_with_another_name()
    {
        var rig = new Rig(new LiveTarget("cs2.exe", 1, ["cs2.exe"]));
        rig.Runner.Last.Error("error: a trace session named \"NEXUS_Live\" is already running. Use --stop_existing_session");
        rig.Runner.Last.Exit(6);
        rig.Tick(0.5);
        Assert.Equal(2, rig.Runner.Runs.Count);
        Assert.NotEqual(PresentMonCli.LiveSessionName, ValuesOf(rig.Runner.Last.Args, "--session_name").Single());
        rig.Runner.Last.Error("error: unrecognized option '--x'.");
        rig.Runner.Last.Exit(1);
        rig.Tick(0.5);
        Assert.Equal(LiveCaptureState.Failed, rig.Capture.Status.State);
        Assert.Equal(PresentMonFailure.BadArguments, rig.Capture.Status.Failure);
        Assert.Contains("unrecognized option", rig.Capture.Status.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ estados verdadeiros

    [Fact]
    public void Game_that_stops_presenting_is_not_drawing_and_a_closed_one_is_searched_again()
    {
        var rig = new Rig(new LiveTarget("cs2.exe", 1, ["cs2.exe"]));
        var run = rig.Runner.Last;
        run.Line(Header);
        rig.Frames60(run, "cs2.exe", 1);
        rig.Tick(4, Snap(77, (1, "cs2.exe")));                             // minimizado: processo existe, sem quadros
        Assert.Equal(LiveCaptureState.NotDrawing, rig.Capture.Status.State);
        Assert.False(rig.Capture.Status.TimedOut);
        rig.Tick(12, Snap(77, (1, "cs2.exe")));
        Assert.True(rig.Capture.Status.TimedOut);
        rig.Frames60(run, "cs2.exe", 1, 10);
        Assert.Equal(LiveCaptureState.Measuring, rig.Capture.Status.State);
        rig.Tick(4, Snap(null));                                           // fechou
        Assert.Equal(LiveCaptureState.Searching, rig.Capture.Status.State);
        Assert.Equal(0, rig.Capture.Status.Pid);
        run.Frame("cs2.exe", 2);                                           // reabriu com outro PID
        Assert.Equal(2, rig.Capture.Status.Pid);
    }

    [Fact]
    public void Splash_process_that_stops_drawing_hands_over_to_the_expected_process_that_draws()
    {
        var rig = new Rig(new LiveTarget("Jogo.exe", 0, ["Jogo.exe", "Jogo-Win64-Shipping.exe"]));
        var run = rig.Runner.Last;
        run.Line(Header);
        rig.Frames60(run, "Jogo.exe", 1, 30);                              // tela de abertura
        rig.Now = rig.Now.AddSeconds(3);
        rig.Frames60(run, "Jogo-Win64-Shipping.exe", 2);
        rig.Tick(0.1, Snap(2, (1, "Jogo.exe"), (2, "Jogo-Win64-Shipping.exe")));
        Assert.Equal(2, rig.Capture.Status.Pid);
        rig.Frames60(run, "Jogo-Win64-Shipping.exe", 2, 10);
        Assert.Equal(LiveCaptureState.Measuring, rig.Capture.Status.State);
        Assert.Equal(10, rig.Frames.Count(f => f.ProcessId == 2));
    }

    [Fact]
    public void Process_picked_by_the_user_is_measured_by_pid_even_with_an_unknown_name()
    {
        var rig = new Rig(new LiveTarget("<unknown>", 4321, [], Locked: true));
        var run = rig.Runner.Last;
        Assert.Empty(ValuesOf(run.Args, "--process_name"));
        run.Line(Header);
        run.Frame("<unknown>", 1234);
        run.Frame("<unknown>", 4321);
        Assert.Single(rig.Frames);
        rig.Tick(20, Snap(null, (4321, "x.exe")));
        Assert.Single(rig.Runner.Runs);                                    // escolhido pelo usuário: nada de recomeçar
        Assert.Equal(LiveCaptureState.NotDrawing, rig.Capture.Status.State);
    }

    [Fact]
    public void Finish_stops_the_run_and_marks_finished()
    {
        var rig = new Rig(new LiveTarget("cs2.exe", 1, ["cs2.exe"]));
        rig.Capture.Finish();
        Assert.True(rig.Runner.Last.Terminated);
        Assert.True(rig.Capture.Ended);
        Assert.Equal(LiveCaptureState.Finished, rig.Capture.Status.State);
    }

    // ------------------------------------------------------------------ linha de comando e "quem está desenhando?"

    [Fact]
    public void Live_arguments_for_unfiltered_and_multi_name_captures()
    {
        var multi = PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "a.exe", false, null) { ExtraProcessNames = ["b.exe", "A.exe"] }, PresentMonGeneration.V2);
        Assert.Equal(["a.exe", "b.exe"], ValuesOf(multi, "--process_name"));
        var all = PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "<unknown>", false, null) { AllProcesses = true, Exclude = ["dwm.exe"], SessionName = "NEXUS_Live_2" }, PresentMonGeneration.V2);
        Assert.Empty(ValuesOf(all, "--process_name"));
        Assert.Equal(["dwm.exe"], ValuesOf(all, "--exclude"));
        Assert.Equal(["NEXUS_Live_2"], ValuesOf(all, "--session_name"));
        Assert.Throws<ArgumentException>(() => PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "a.exe", false, null) { SessionName = "x y" }, PresentMonGeneration.V2));
        Assert.Throws<ArgumentException>(() => PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "a.exe", false, null) { ExtraProcessNames = ["b.exe\" --x"] }, PresentMonGeneration.V2));
        // 1.x não tem captura sem filtro aqui: continua exigindo o nome.
        Assert.Throws<ArgumentException>(() => PresentMonCli.LiveArguments(new LiveCaptureRequest("pm.exe", "<unknown>", false, null) { AllProcesses = true }, PresentMonGeneration.V1));
    }

    [Fact]
    public async Task Presenter_probe_lists_who_is_drawing_without_nexus_itself()
    {
        var runner = new ScriptedRunner(run =>
        {
            run.Line(Header);
            for (var i = 0; i < 90; i++) run.Frame("Jogo.exe", 10);
            for (var i = 0; i < 30; i++) run.Frame("Nexus.Desktop.exe", 999);
            run.Exit(0);
        });
        var r = await PresenterProbe.RunAsync("pm.exe", false, runner, seconds: 3, ownPid: 999);
        Assert.Equal(PresentMonFailure.None, r.Failure);
        var only = Assert.Single(r.Presenters);
        Assert.Equal("Jogo.exe", only.Name);
        Assert.Contains("--exclude", runner.Args!);
        Assert.Contains(PresenterProbe.SessionName, runner.Args!);

        var denied = await PresenterProbe.RunAsync("pm.exe", false, new ScriptedRunner(run =>
        {
            run.Error("error: failed to start trace session: access denied.");
            run.Exit(6);
        }), seconds: 1);
        Assert.Equal(PresentMonFailure.AccessDenied, denied.Failure);
        Assert.Empty(denied.Presenters);
    }

    [Fact]
    public void Starter_passes_target_names_and_never_tracks_frame_type_for_unknown_builds()
    {
        var runner = new FakeRunner();
        var starter = new PresentMonLiveStarter(() => @"C:\pm\PresentMon.exe", () => false, runner);
        Assert.True(starter.Available);
        using var c = starter.Start(LiveTarget.For(new DetectedGame("VALORANT", "VALORANT.exe", 1, DetectionReason.BuiltIn, "x")), _ => { });
        Assert.Equal(["VALORANT.exe", "VALORANT-Win64-Shipping.exe"], ValuesOf(runner.Last.Args, "--process_name"));
        Assert.DoesNotContain("--track_frame_type", runner.Last.Args);
        Assert.DoesNotContain("--restart_as_admin", runner.Last.Args);
    }

    // ------------------------------------------------------------------ falsos do motor

    private sealed class ScriptedRunner(Action<FakeRun> script) : IPresentMonRunner
    {
        public IReadOnlyList<string>? Args { get; private set; }
        public IPresentMonRun Start(string exe, IReadOnlyList<string> arguments, string? outputCsv, Action<string> onLine, Action<string> onError)
        {
            Args = arguments;
            var r = new FakeRun(arguments, outputCsv, onLine, onError);
            script(r);
            return r;
        }
    }

    private sealed class Probe : IProcessProbe
    {
        public List<RunningProcess> Processes { get; } = [];
        public ProcessSnapshot Read() => new(Processes.ToList(), null);
        public bool IsAlive(int pid) => Processes.Any(p => p.Pid == pid);
    }

    private sealed class RecordingStarter : ILiveCaptureStarter
    {
        public bool Available => true;
        public LiveTarget? Target { get; private set; }
        public Action<LiveFrame>? OnFrame { get; private set; }
        public int Ticks { get; private set; }
        public ILiveCapture Start(string processName, Action<LiveFrame> onFrame) => Start(new LiveTarget(processName, 0, [processName]), onFrame);
        public ILiveCapture Start(LiveTarget target, Action<LiveFrame> onFrame) { Target = target; OnFrame = onFrame; return new Capture(this); }

        private sealed class Capture(RecordingStarter owner) : ILiveCapture
        {
            public string Log => "";
            public bool Ended => false;
            public void Finish() { }
            public void Dispose() { }
            public void Tick(DateTimeOffset now, ProcessSnapshot? snapshot) => owner.Ticks++;
        }
    }
}
