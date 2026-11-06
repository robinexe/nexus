using Nexus.Broker;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.GameProfiles.Fortnite;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;

namespace Nexus.Tests;

/// <summary>
/// "Em Jogos, ao fazer alguma função, não salva que já fez": cada opção da tela Jogos mostra o estado REAL lido de onde a ação
/// escreve (registro, driver NVIDIA, arquivo do Fortnite) e acha no diário a execução que a desfaz. Reabrir o NEXUS = ler de novo
/// com uma instância nova: o que foi aplicado continua ligado.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameOptionStateTests
{
    // ------------------------------------------------------------------ falsos

    /// <summary>Banco de perfis do driver em memória: (exe, ajuste) → leitura. Exe sem perfil = NoProfile; exe "ambíguo" lança.</summary>
    private sealed class FakeDrs : INvidiaDrsReader
    {
        public const string BaseKey = "<global>";
        public Dictionary<(string Exe, uint Id), DrsReading> Values { get; } = [];
        public HashSet<string> Ambiguous { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Unavailable() => null;

        public DrsReading Read(DrsTarget target, uint settingId)
        {
            var key = target.Exe?.ToLowerInvariant() ?? BaseKey; // perfil global
            if (Ambiguous.Contains(key)) throw new NvidiaDrsException("NvAPI_DRS_FindApplicationByName", NvApiStatus.ExecutablePathIsAmbiguous);
            if (target.Exe is null) return Values.TryGetValue((key, settingId), out var b) ? b : new DrsReading(true, "Base Profile", false, null, false, null);
            return Values.TryGetValue((key, settingId), out var r) ? r : DrsReading.NoProfile;
        }

        public void Set(string exe, uint id, uint value) => Values[(exe.ToLowerInvariant(), id)] = new DrsReading(true, "NEXUS - " + exe, true, value, false, value);
    }

    private sealed class FakeSource : IGameStateSource
    {
        public FakeDrs Drs { get; } = new();
        public INvidiaDrsReader Nvidia => Drs;
        public Dictionary<string, int?> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);
        public FortniteState Ini { get; set; } = FortniteState.NotFound;
        public int? WindowedToken(string exePath) => Tokens.GetValueOrDefault(exePath);
        public FortniteState Fortnite() => Ini;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private static JournalRun Run(int minutes, BrokerLevel level, params (string Key, string? Target, bool Active)[] items) =>
        new(Guid.NewGuid(), level, T0.AddMinutes(minutes),
            [.. items.Select(i => new JournalItem(i.Key, i.Target is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["exe"] = i.Target }, i.Active))]);

    private static OptionStatus Status(GameOptionPlan plan, IGameStateSource source, IReadOnlyList<JournalRun> journal) =>
        GameStateReader.Read([plan], source, journal)[plan.Id];

    // ------------------------------------------------------------------ Modo de jogo (registro)

    [Theory]
    [InlineData(null, null, OptionState.Unknown)]   // sem leitura
    [InlineData(false, null, OptionState.Off)]      // AutoGameModeEnabled = 0
    [InlineData(true, null, OptionState.On)]        // 1 ou ausente (padrão do Windows)
    [InlineData(false, "same", OptionState.On)]     // aplicado agora: o diagnóstico ainda é o de antes, o catálogo já releu 1
    [InlineData(false, "change", OptionState.Off)]  // desfeito: volta a 0
    [InlineData(true, "change", OptionState.On)]    // ausente: ligado pelo padrão
    public void Game_mode_state_prefers_the_catalog_reread(bool? diagnosis, string? readState, OptionState expected) =>
        Assert.Equal(expected, GameOptionStates.GameMode(diagnosis, readState));

    // ------------------------------------------------------------------ driver NVIDIA por jogo

    [Fact]
    public void Nvidia_low_latency_absent_applied_and_different()
    {
        var src = new FakeSource();
        var plan = GameOptionPlans.NvLowLatency("cs2.exe");
        Assert.Equal(OptionState.Off, Status(plan, src, []).State); // sem perfil
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, 0);
        Assert.Equal(OptionState.Off, Status(plan, src, []).State); // padrão do driver
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, 3);
        Assert.Equal(OptionState.Different, Status(plan, src, []).State);
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, NvidiaDrsSettings.LowLatencyOn);
        Assert.Equal(OptionState.On, Status(plan, src, []).State);
    }

    [Fact]
    public void Nvidia_max_power_default_is_off()
    {
        var src = new FakeSource();
        var plan = GameOptionPlans.NvMaxPower("cs2.exe");
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PreferredPstate, NvidiaDrsSettings.PstateOptimalPower);
        Assert.Equal(OptionState.Off, Status(plan, src, []).State);
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PreferredPstate, 0); // adaptável (outro valor)
        Assert.Equal(OptionState.Different, Status(plan, src, []).State);
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PreferredPstate, NvidiaDrsSettings.PstatePreferMax);
        Assert.Equal(OptionState.On, Status(plan, src, []).State);
    }

    [Fact]
    public void Nvidia_uncap_is_on_only_without_fps_limit_and_with_vsync_forced_off()
    {
        // Diretriz do responsável (06/11/2026): "nunca limite o FPS". A opção da tela Jogos só TIRA limite.
        var src = new FakeSource();
        var plan = GameOptionPlans.NvUncap("cs2.exe");
        Assert.Equal(OptionState.Off, Status(plan, src, []).State); // V-Sync ainda é "o jogo decide"
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.VsyncMode, NvidiaDrsSettings.VsyncForceOff);
        Assert.Equal(OptionState.On, Status(plan, src, []).State);
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.FrameRateLimit, 141); // limite no perfil do jogo
        Assert.Equal(OptionState.Off, Status(plan, src, []).State);
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.FrameRateLimit, 0);
        Assert.Equal(OptionState.On, Status(plan, src, []).State);
        // Sem valor próprio no perfil do jogo, vale o global: um limite lá também trava este jogo.
        src.Drs.Values.Remove(("cs2.exe", NvidiaDrsSettings.FrameRateLimit));
        src.Drs.Set(FakeDrs.BaseKey, NvidiaDrsSettings.FrameRateLimit, 60);
        Assert.Equal(OptionState.Off, Status(plan, src, []).State);
        // Pedidos: tirar o limite e desligar o V-Sync no driver, valores fixos do catálogo, como administrador.
        Assert.Equal([NvidiaDrsActions.FpsCapOff, NvidiaDrsActions.VsyncOff], plan.Requests.Select(r => r.Request.ActionId));
        Assert.All(plan.Requests, r => { Assert.Null(r.Request.Value); Assert.Equal(BrokerLevel.Admin, r.Level); Assert.Equal("cs2.exe", r.Request.Arguments!["exe"]); });
    }

    [Fact]
    public void Nvidia_global_cap_or_forced_vsync_is_detected_and_the_fix_only_uncaps()
    {
        var src = new FakeSource();
        var plan = GameOptionPlans.NvGlobalUncap();
        Assert.Equal(OptionState.On, Status(plan, src, []).State); // global sem limite e V-Sync padrão
        src.Drs.Set(FakeDrs.BaseKey, NvidiaDrsSettings.VsyncMode, NvidiaDrsSettings.VsyncForceOn);
        Assert.Equal(OptionState.Off, Status(plan, src, []).State);
        src.Drs.Set(FakeDrs.BaseKey, NvidiaDrsSettings.VsyncMode, NvidiaDrsSettings.VsyncPassive);
        src.Drs.Set(FakeDrs.BaseKey, NvidiaDrsSettings.FrameRateLimit, 60);
        Assert.Equal(OptionState.Off, Status(plan, src, []).State);
        src.Drs.Ambiguous.Add(FakeDrs.BaseKey);
        Assert.Equal(OptionState.Unknown, Status(plan, src, []).State); // falha de leitura nunca vira "limitado"
        Assert.Equal([NvidiaDrsActions.FpsCapOffGlobal, NvidiaDrsActions.VsyncOffGlobal], plan.Requests.Select(r => r.Request.ActionId));
        Assert.All(plan.Requests, r => Assert.Null(r.Request.Arguments));
    }

    [Fact]
    public void Nvidia_inherited_value_counts_and_read_failure_is_unknown_not_off()
    {
        var src = new FakeSource();
        src.Drs.Values[("cs2.exe", NvidiaDrsSettings.PrerenderLimit)] = new DrsReading(true, "Counter-Strike 2", false, null, false, 1);
        Assert.Equal(OptionState.On, Status(GameOptionPlans.NvLowLatency("cs2.exe"), src, []).State);
        src.Drs.Ambiguous.Add("cs2.exe");
        Assert.Equal(OptionState.Unknown, Status(GameOptionPlans.NvLowLatency("cs2.exe"), src, []).State);
    }

    [Fact]
    public void Nvidia_launcher_also_targets_the_exe_that_draws()
    {
        var plan = GameOptionPlans.NvLowLatency("FortniteLauncher.exe");
        Assert.Equal(["FortniteLauncher.exe", "FortniteClient-Win64-Shipping.exe"], plan.Requests.Select(r => r.Request.Arguments!["exe"]));
        Assert.All(plan.Requests, r => Assert.Equal(BrokerLevel.Admin, r.Level));
        var src = new FakeSource();
        src.Drs.Set("FortniteLauncher.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        Assert.Equal(OptionState.Off, Status(plan, src, []).State); // só o lançador: o jogo ainda não tem
        src.Drs.Set("FortniteClient-Win64-Shipping.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        Assert.Equal(OptionState.On, Status(plan, src, []).State);
        // Executável comum: só ele (nada de inventar "-Win64-Shipping").
        Assert.Equal(["cs2.exe"], GameOptionStates.NvTargets("cs2.exe"));
    }

    // ------------------------------------------------------------------ jogos em janela (UserGpuPreferences)

    [Theory]
    [InlineData(null, OptionState.Off)]
    [InlineData(0, OptionState.Off)]
    [InlineData(1, OptionState.On)]
    public void Windowed_token(int? token, OptionState expected)
    {
        var src = new FakeSource();
        src.Tokens[@"C:\Games\Game\game.exe"] = token;
        Assert.Equal(expected, Status(GameOptionPlans.Windowed("game.exe", @"C:\Games\Game\game.exe"), src, []).State);
    }

    // ------------------------------------------------------------------ Fortnite (GameUserSettings.ini)

    private static FortniteState Ini(string rhi = "dx11", string level = "sm5", string fps = "0.000000", string reflex = "0", string blur = "True", string vsync = "False") =>
        FortniteState.FromText($"""
            [D3DRHIPreference]
            PreferredRHI={rhi}
            PreferredFeatureLevel={level}
            [/Script/FortniteGame.FortGameUserSettings]
            FrameRateLimit={fps}
            LatencyTweak2={reflex}
            bUseVSync={vsync}
            bMotionBlur={blur}
            """);

    [Fact]
    public void Fortnite_options_absent_applied_and_different()
    {
        var src = new FakeSource();
        var perf = GameOptionPlans.FortnitePerformance();
        var latency = GameOptionPlans.FortniteLatency(nvidia: true);
        var clear = GameOptionPlans.FortniteClear();
        Assert.All([perf, latency, clear], p => Assert.Equal(OptionState.Unknown, Status(p, src, []).State)); // arquivo ainda não existe

        src.Ini = Ini(fps: "141.000000");
        Assert.All([perf, latency, clear], p => Assert.Equal(OptionState.Off, Status(p, src, []).State));

        src.Ini = Ini(rhi: "dx12", level: "es31", fps: "0.000000", reflex: "1", blur: "False");
        Assert.All([perf, latency, clear], p => Assert.Equal(OptionState.On, Status(p, src, []).State));

        src.Ini = Ini(rhi: "dx12", level: "sm6", fps: "60.000000", reflex: "1", blur: "False"); // DX12 completo; com limite
        Assert.Equal(OptionState.Off, Status(perf, src, []).State);
        Assert.Equal(OptionState.Off, Status(latency, src, []).State);
        Assert.Equal(OptionState.Off, GameOptionStates.FortniteLatency(Ini(reflex: "1", vsync: "True"), nvidia: true)); // V-Sync prende o FPS
        Assert.Equal(OptionState.On, GameOptionStates.FortniteLatency(Ini(reflex: "2"), nvidia: true)); // Reflex + Boost também
        Assert.Equal(OptionState.On, GameOptionStates.FortniteLatency(Ini(), nvidia: false));
    }

    [Fact]
    public void Fortnite_one_click_never_sets_an_fps_limit()
    {
        foreach (var nvidia in new[] { true, false })
        {
            var plan = GameOptionPlans.FortniteLatency(nvidia);
            var fps = plan.Requests.Single(r => r.Request.ActionId == FortniteSettings.FpsLimitAction);
            Assert.Equal(FortniteSettings.FpsUnlimited, fps.Request.Value);
            Assert.Contains(plan.Requests, r => r.Request.ActionId == FortniteSettings.VSyncOffAction);
            Assert.Equal(nvidia, plan.Requests.Any(r => r.Request.ActionId == FortniteSettings.ReflexAction));
        }
    }

    // ------------------------------------------------------------------ diário: o que desfaz

    [Fact]
    public void Undo_uses_the_run_that_only_touched_this_option()
    {
        var src = new FakeSource();
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        var plan = GameOptionPlans.NvLowLatency("cs2.exe");
        var mine = Run(1, BrokerLevel.Admin, (NvidiaDrsActions.LowLatency, "cs2.exe", true));
        var otherGame = Run(2, BrokerLevel.Admin, (NvidiaDrsActions.LowLatency, "valorant.exe", true));
        var s = Status(plan, src, [otherGame, mine]);
        Assert.Equal(OptionState.On, s.State);
        Assert.Equal(UndoKind.Runs, s.Undo.Kind);
        Assert.Equal((mine.RunId, BrokerLevel.Admin), Assert.Single(s.Undo.Runs));
    }

    [Fact]
    public void Undo_points_to_history_when_applied_with_other_things()
    {
        var src = new FakeSource();
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        var batch = Run(1, BrokerLevel.Admin, (NvidiaDrsActions.LowLatency, "cs2.exe", true), (NvidiaDrsActions.MaxPower, "cs2.exe", true));
        var s = Status(GameOptionPlans.NvLowLatency("cs2.exe"), src, [batch]);
        Assert.Equal(OptionState.On, s.State);
        Assert.Equal(UndoKind.History, s.Undo.Kind);
        Assert.Empty(s.Undo.Runs);
    }

    [Fact]
    public void Undo_ignores_reverted_runs_and_state_done_outside_nexus_has_no_undo()
    {
        var src = new FakeSource();
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        var reverted = Run(1, BrokerLevel.Admin, (NvidiaDrsActions.LowLatency, "cs2.exe", false));
        var s = Status(GameOptionPlans.NvLowLatency("cs2.exe"), src, [reverted]);
        Assert.Equal(OptionState.On, s.State); // ligado pelo Painel NVIDIA, por exemplo
        Assert.Equal(UndoKind.None, s.Undo.Kind);
        // Desligado: nada a desfazer, mesmo com execução no diário (foi desfeita por fora).
        src.Drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, 0);
        Assert.Equal(UndoKind.None, Status(GameOptionPlans.NvLowLatency("cs2.exe"), src, [Run(1, BrokerLevel.Admin, (NvidiaDrsActions.LowLatency, "cs2.exe", true))]).Undo.Kind);
    }

    [Fact]
    public void Undo_of_a_two_action_option_and_of_two_exes_stays_here()
    {
        var src = new FakeSource { Ini = Ini(rhi: "dx12", level: "es31") };
        var perf = Run(1, BrokerLevel.User, (FortniteSettings.RhiDx12Action, null, true), (FortniteSettings.PerformanceModeAction, null, true));
        Assert.Equal(UndoKind.Runs, Status(GameOptionPlans.FortnitePerformance(), src, [perf]).Undo.Kind);

        src.Drs.Set("FortniteLauncher.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        src.Drs.Set("FortniteClient-Win64-Shipping.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        var both = Run(2, BrokerLevel.Admin, (NvidiaDrsActions.LowLatency, "FortniteLauncher.exe", true), (NvidiaDrsActions.LowLatency, "FortniteClient-Win64-Shipping.exe", true));
        Assert.Equal(UndoKind.Runs, Status(GameOptionPlans.NvLowLatency("FortniteLauncher.exe"), src, [both]).Undo.Kind);
    }

    [Fact]
    public void Undo_runs_are_oldest_first_so_the_newest_is_reverted_first()
    {
        var a = Run(1, BrokerLevel.User, (GameOptionPlans.WindowedAction, null, true));
        var b = Run(5, BrokerLevel.User, (GameOptionPlans.WindowedAction, null, true));
        var undo = OptionJournal.Find([b, a], [new OptionPart(GameOptionPlans.WindowedAction)]);
        Assert.Equal([a.RunId, b.RunId], undo.Runs.Select(r => r.Run));
    }

    [Fact]
    public void Journal_match_by_target_is_case_insensitive_and_by_path_for_windowed()
    {
        var item = new JournalItem(GameOptionPlans.WindowedAction, new Dictionary<string, string>
        {
            ["hive"] = "HKCU", ["name"] = @"c:\games\game\GAME.exe", ["token"] = "SwapEffectUpgradeEnable",
        }, true);
        Assert.True(new OptionPart(GameOptionPlans.WindowedAction, @"C:\Games\Game\game.exe").Matches(item));
        Assert.False(new OptionPart(GameOptionPlans.WindowedAction, @"C:\Games\Other\game.exe").Matches(item));
        Assert.False(new OptionPart(NvidiaDrsActions.LowLatency, @"C:\Games\Game\game.exe").Matches(item));
    }

    // ------------------------------------------------------------------ reabrir o NEXUS

    [Fact]
    public void Restart_reads_applied_options_again_with_a_new_instance()
    {
        // "Antes de fechar": aplicado pelo aplicador (o falso só guarda o que ficou no Windows e no diário).
        var drs = new FakeDrs();
        drs.Set("cs2.exe", NvidiaDrsSettings.PrerenderLimit, 1);
        var journal = new List<JournalRun> { Run(1, BrokerLevel.Admin, (NvidiaDrsActions.LowLatency, "cs2.exe", true)) };
        var tokens = new Dictionary<string, int?> { [@"C:\G\cs2.exe"] = 1 };

        // "Depois de reabrir": fonte nova, planos novos, nenhuma memória da execução anterior.
        var source = new FakeSource();
        foreach (var (k, v) in drs.Values) source.Drs.Values[k] = v;
        foreach (var (k, v) in tokens) source.Tokens[k] = v;
        var plans = new[] { GameOptionPlans.NvLowLatency("cs2.exe"), GameOptionPlans.NvMaxPower("cs2.exe"), GameOptionPlans.Windowed("cs2.exe", @"C:\G\cs2.exe") };
        var states = GameStateReader.Read(plans, source, journal);
        Assert.Equal(OptionState.On, states[plans[0].Id].State);
        Assert.Equal(UndoKind.Runs, states[plans[0].Id].Undo.Kind);
        Assert.Equal(OptionState.Off, states[plans[1].Id].State);
        Assert.Equal(OptionState.On, states[plans[2].Id].State);
        Assert.Equal(UndoKind.None, states[plans[2].Id].Undo.Kind); // ligado por fora do NEXUS: sem desfazer aqui
        // Ids estáveis: o mesmo botão é reaproveitado entre aberturas da tela.
        Assert.Equal(plans[0].Id, GameOptionPlans.NvLowLatency("cs2.exe").Id);
    }

    [Fact]
    public void Game_session_toggle_survives_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "game-session.json");
            Assert.Equal(GameSessionState.Off, new GameSessionStore(path).Load()); // nunca ligada
            var run = Guid.NewGuid();
            Assert.True(new GameSessionStore(path).Save(new GameSessionState(true, run)));
            Assert.Equal(new GameSessionState(true, run), new GameSessionStore(path).Load()); // instância nova = reinício
            File.WriteAllText(path, "{ quebrado");
            Assert.Equal(GameSessionState.Off, new GameSessionStore(path).Load());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ Início / Problemas

    [Fact]
    public void Applied_finding_is_not_pending_after_catalog_reread()
    {
        IReadOnlyList<DiagnosticFinding> findings =
        [
            new("win.gamemode", "Modo de Jogo desativado", DiagnosticSeverity.Attention, FactStatus.Detected, "0", "win.gamemode.enable"),
            new("win.gamedvr.capture", "Gravação", DiagnosticSeverity.Attention, FactStatus.Detected, "1", "win.gamedvr.background-capture.disable"),
            new("mem.single", "Um canal", DiagnosticSeverity.Attention, FactStatus.Detected, "1"),
        ];
        var after = ResolvedFindings.Apply(findings, id => id == "win.gamemode.enable" ? "same" : "change");
        Assert.Equal(DiagnosticSeverity.Ok, after[0].Severity);
        Assert.Equal(DiagnosticSeverity.Attention, after[1].Severity);
        Assert.Equal(DiagnosticSeverity.Attention, after[2].Severity); // sem ação: só a próxima leitura do PC muda
        Assert.All(ResolvedFindings.Apply(findings, _ => null), f => Assert.Equal(DiagnosticSeverity.Attention, f.Severity)); // catálogo não lido
    }
}
