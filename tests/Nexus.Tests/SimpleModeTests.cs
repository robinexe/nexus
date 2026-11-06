using Nexus.Assistant;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.HardwareDiscovery;

namespace Nexus.Tests;

/// <summary>
/// Modo simples (pedido: "facilitar a vida do usuário leigo"): que páginas aparecem em cada modo, a escolha na primeira
/// abertura, a nota de saúde da tela Início, os três cartões "O que dá para melhorar", os achados em palavras simples e o
/// que o Assistente aplica sozinho no modo automático.
/// </summary>
[Trait("Category", "Unit")]
[Collection(LocalizationState.Name)]
public sealed class SimpleModeTests
{
    private static readonly string[] FullPages =
    [
        "OverviewPage", "HardwarePage", "DiagnosticsPage", "CpuLabPage", "GpuLabPage", "MemoryLabPage", "MemoryDoctorPage", "AssistantPage",
        "AutomationPage", "CatalogPage", "PlanPage", "BrokerPage", "StartupPage", "ServicesPage", "CleaningPage", "DiskPage", "ProgramsPage",
        "PrivacyPage", "SecurityPage", "ScannerPage", "SystemToolsPage", "DriverUpdatesPage", "MeasurePage", "AbLabPage", "MonitorPage", "HistoryPage",
        "SettingsPage", "CapabilitiesPage",
    ];

    private static IEnumerable<string> AllPages => FullPages.Concat(ShellNavigation.SimplePages);

    public SimpleModeTests() => Loc.Use("pt-BR");

    // ------------------------------------------------------------------ navegação (interface simples para todos)

    // Requisito mudou em 05/11/2026 (diretriz de produto, docs/13 e docs/14): a lista lateral deixou de ser as 6 telas simples e
    // passou a ser 5 áreas; as telas simples viraram abas das áreas e as ferramentas completas ficam em "Avançado" de cada área.

    [Fact]
    public void Sidebar_has_the_five_areas_in_a_fixed_order()
    {
        Assert.Equal(["home", "optimize", "performance", "diagnostics", "settings"], ShellNavigation.Areas.Select(a => a.Key));
        var nav = ShellNavigation.Navigation(AllPages.Reverse().Append(ShellNavigation.AdvancedPage), p => p);
        Assert.Equal(["HomePage", "SimpleOptimizePage", "SimpleGamesPage", "SimpleProblemsPage", "SettingsPage"], nav);
        Assert.Equal("HomePage", ShellNavigation.StartPage);
    }

    [Fact]
    public void Every_page_lives_in_exactly_one_area()
    {
        foreach (var page in AllPages.Append(ShellNavigation.AdvancedPage))
            Assert.Single(ShellNavigation.Areas, a => a.Contains(page));
        // e nenhuma área aponta para página que não existe
        var known = AllPages.Append(ShellNavigation.AdvancedPage).ToHashSet();
        Assert.All(ShellNavigation.Areas.SelectMany(a => a.Tabs.Concat(a.Advanced)), p => Assert.Contains(p, known));
    }

    [Fact]
    public void Simple_screens_are_tabs_not_hidden_in_advanced()
    {
        foreach (var page in ShellNavigation.SimplePages)
            Assert.Contains(ShellNavigation.Areas, a => a.Tabs.Contains(page));
        Assert.Equal(1, ShellNavigation.AreaIndexOf("SimpleCleanPage"));
        Assert.Equal(4, ShellNavigation.AreaIndexOf("SimpleHistoryPage"));
        Assert.Equal(0, ShellNavigation.AreaIndexOf("PaginaQueNaoExiste"));
    }

    [Fact]
    public void Pick_keeps_the_order_of_the_names()
    {
        var area = ShellNavigation.Areas[2];
        Assert.Equal(area.Tabs, ShellNavigation.Pick(AllPages.Reverse(), p => p, area.Tabs));
    }

    [Fact]
    public void Advanced_tools_list_every_full_page_and_never_the_simple_screens()
    {
        var advanced = ShellNavigation.Advanced(AllPages.Append(ShellNavigation.AdvancedPage), p => p);
        Assert.Equal(FullPages, advanced);
        Assert.All(ShellNavigation.SimplePages, p => Assert.DoesNotContain(p, advanced));
        Assert.False(ShellNavigation.IsInNavigation(ShellNavigation.AdvancedPage));
    }

    [Fact]
    public void New_install_gets_the_welcome()
    {
        Assert.Equal(new UiStartup(ShowWelcome: true, SaveSimple: false), UiModeStartup.Decide(null, hadPriorUse: false));
    }

    [Fact]
    public void Existing_user_gets_the_simple_interface_without_welcome_or_notice()
    {
        Assert.Equal(new UiStartup(ShowWelcome: false, SaveSimple: true), UiModeStartup.Decide(null, hadPriorUse: true));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("FULL")]
    [InlineData("completo")]
    public void Saved_full_mode_is_migrated_to_simple(string stored)
    {
        Assert.Equal(new UiStartup(false, true), UiModeStartup.Decide(stored, hadPriorUse: true));
        Assert.Equal(new UiStartup(false, true), UiModeStartup.Decide(stored, hadPriorUse: false));
    }

    [Theory]
    [InlineData("simple", true)]
    [InlineData(" Simple ", false)]
    public void Already_simple_needs_nothing(string stored, bool prior) =>
        Assert.Equal(new UiStartup(false, false), UiModeStartup.Decide(stored, prior));

    [Fact]
    public void Settings_keep_the_marker_and_default_to_unset()
    {
        Assert.Null(NexusSettings.Defaults.UiMode);
        var path = Path.Combine(Path.GetTempPath(), "nexus-simple-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null((NexusSettings.Defaults with { UiMode = ShellNavigation.StoredSimple }).Save(path));
            Assert.Equal("simple", NexusSettings.Load(path).UiMode);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ nota de saúde

    private static DiagnosticFinding F(string id, DiagnosticSeverity s = DiagnosticSeverity.Attention, OwnerAdvice? owner = null) =>
        new(id, "título técnico " + id, s, FactStatus.Detected, "Valor = 0 (DWord)", null, owner);

    private static readonly DateTime Now = new(2026, 11, 2);
    private const double Gb = 1024d * 1024 * 1024;

    private static HealthInputs Clean() => new()
    {
        Findings = [F("win.gamemode", DiagnosticSeverity.Ok), F("disk.system-free", DiagnosticSeverity.Ok)],
        RecoverableBytes = 0.2 * Gb, PendingRecommendations = 0, GpuDriverDate = Now.AddMonths(-2), MemoryModules = 2, MemorySlots = 2, Now = Now,
    };

    [Fact]
    public void Healthy_pc_is_great()
    {
        var r = SimpleHealth.Compute(Clean());
        Assert.Equal(100, r.Score);
        Assert.Equal(HealthLevel.Great, r.Level);
        Assert.Equal("Seu PC está ótimo", SimpleHealth.Headline(r.Level));
    }

    [Fact]
    public void Unread_inputs_do_not_count_against_the_pc()
    {
        var r = SimpleHealth.Compute(new HealthInputs { Findings = [F("x", DiagnosticSeverity.Ok)], Now = Now });
        Assert.Equal(100, r.Score);
    }

    [Fact]
    public void Each_problem_lowers_the_score_with_a_cap_per_topic()
    {
        Assert.Equal(92, SimpleHealth.Compute(Clean() with { Findings = [F("win.gamemode")] }).Score);
        Assert.Equal(60, SimpleHealth.Compute(Clean() with { Findings = Enumerable.Range(0, 9).Select(i => F("x" + i)).ToList() }).Score); // teto 40
        Assert.Equal(95, SimpleHealth.Compute(Clean() with { RecoverableBytes = 2 * Gb }).Score);
        Assert.Equal(90, SimpleHealth.Compute(Clean() with { RecoverableBytes = 9.3 * Gb }).Score);
        Assert.Equal(85, SimpleHealth.Compute(Clean() with { RecoverableBytes = 40 * Gb }).Score);
        Assert.Equal(85, SimpleHealth.Compute(Clean() with { PendingRecommendations = 30 }).Score); // teto 15
        Assert.Equal(90, SimpleHealth.Compute(Clean() with { GpuDriverDate = Now.AddYears(-1).AddDays(-10) }).Score);
        Assert.Equal(85, SimpleHealth.Compute(Clean() with { GpuDriverDate = new DateTime(2022, 3, 14) }).Score);
        Assert.Equal(90, SimpleHealth.Compute(Clean() with { MemoryModules = 1, MemorySlots = 2 }).Score);
    }

    [Fact]
    public void Single_channel_is_not_counted_twice()
    {
        var both = Clean() with { MemoryModules = 1, Findings = [F("mem.channels")] };
        Assert.Equal(90, SimpleHealth.Compute(both).Score);
    }

    [Fact]
    public void The_users_pc_needs_attention_and_the_levels_have_fixed_bounds()
    {
        // Acer Nitro do responsável: 1 pente, driver de 2022, 9,3 GB de lixo, 12 ajustes pendentes, 2 pontos de atenção.
        var nitro = new HealthInputs
        {
            Findings = [F("mem.channels"), F("win.gamedvr.capture"), F("owner.mux", owner: new OwnerAdvice("e", "g", "s", "https://x"))],
            RecoverableBytes = 9.3 * Gb, PendingRecommendations = 12, GpuDriverDate = new DateTime(2022, 3, 14), MemoryModules = 1, MemorySlots = 2, Now = Now,
        };
        var r = SimpleHealth.Compute(nitro);
        Assert.Equal(100 - 16 - 10 - 15 - 15 - 10, r.Score);
        Assert.Equal(HealthLevel.NeedsAttention, r.Level);
        Assert.Equal(HealthLevel.Great, SimpleHealth.LevelOf(80));
        Assert.Equal(HealthLevel.CouldImprove, SimpleHealth.LevelOf(79.9));
        Assert.Equal(HealthLevel.CouldImprove, SimpleHealth.LevelOf(55));
        Assert.Equal(HealthLevel.NeedsAttention, SimpleHealth.LevelOf(54.9));
    }

    [Theory]
    [InlineData("3-14-2022", 2022, 3, 14)]
    [InlineData("2023-05-01", 2023, 5, 1)]
    [InlineData("20240115", 2024, 1, 15)]
    [InlineData("driver de 2021", 2021, 7, 1)]
    public void Driver_dates_in_the_formats_windows_writes(string text, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), SimpleHealth.ParseDriverDate(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("31.0.15.1694")]
    public void Unreadable_driver_dates_are_null(string? text) => Assert.Null(SimpleHealth.ParseDriverDate(text));

    [Fact]
    public void Main_gpu_is_the_dedicated_one()
    {
        var gpus = new[]
        {
            new GpuInfo("AMD Radeon(TM) Graphics", "AMD", "31.0", "1-1-2024", null, 512UL << 20),
            new GpuInfo("NVIDIA GeForce GTX 1650", "NVIDIA", "31.0.15.1274", "3-14-2022", null, 4UL << 30),
        };
        Assert.Equal("NVIDIA GeForce GTX 1650", SimpleHealth.MainGpu(gpus)!.Name);
        Assert.Null(SimpleHealth.MainGpu([]));
    }

    // ------------------------------------------------------------------ "O que dá para melhorar"

    [Fact]
    public void Top_cards_are_at_most_three_ordered_by_impact_with_one_sentence_and_a_button()
    {
        var nitro = new HealthInputs
        {
            Findings = [F("mem.channels"), F("win.gamedvr.capture"), F("win.gamemode")],
            RecoverableBytes = 9.3 * Gb, PendingRecommendations = 12, GpuDriverDate = new DateTime(2022, 3, 14), MemoryModules = 1, Now = Now,
        };
        var top = ImproveCards.Top(nitro);
        Assert.Equal(3, top.Count);
        Assert.Equal([ImproveKind.GpuDriver, ImproveKind.SingleChannel, ImproveKind.PendingTweaks], top.Select(c => c.Kind));
        Assert.Equal("Driver da placa de vídeo de 2022", top[0].Text);
        Assert.Equal("Atualizar", top[0].ActionText);
        Assert.Equal("DriverUpdatesPage", top[0].Target);
        Assert.False(string.IsNullOrEmpty(top[1].HowTo)); // "Ver como" explica
        Assert.All(top, c =>
        {
            Assert.DoesNotContain("DWord", c.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("@", c.Text, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(c.ActionText));
        });
        Assert.True(top.Zip(top.Skip(1)).All(p => p.First.Impact >= p.Second.Impact));
    }

    [Fact]
    public void Cleanup_card_shows_the_space_in_plain_words()
    {
        var top = ImproveCards.Top(Clean() with { RecoverableBytes = 9.3 * Gb });
        var card = Assert.Single(top);
        Assert.Equal("9,3 GB de arquivos inúteis", card.Text);
        Assert.Equal("Limpar", card.ActionText);
        Assert.Equal("SimpleCleanPage", card.Target);
    }

    [Fact]
    public void Healthy_pc_has_no_cards_and_small_values_do_not_make_cards()
    {
        Assert.Empty(ImproveCards.Top(Clean()));
        Assert.Empty(ImproveCards.Top(Clean() with { RecoverableBytes = 0.9 * Gb, GpuDriverDate = Now.AddMonths(-11) }));
    }

    [Fact]
    public void Several_findings_become_one_card_that_opens_problems()
    {
        var top = ImproveCards.Top(Clean() with { Findings = [F("win.gamemode"), F("win.gamedvr.capture"), F("sec.x")] });
        var card = Assert.Single(top);
        Assert.Equal("SimpleProblemsPage", card.Target);
        Assert.Equal("O que fazer", card.ActionText);
    }

    [Fact]
    public void Low_disk_space_and_cleanup_do_not_both_open_the_cleaning_screen()
    {
        var top = ImproveCards.Top(Clean() with { Findings = [F("disk.system-free")], RecoverableBytes = 3 * Gb });
        Assert.Single(top, c => c.Target == "SimpleCleanPage");
    }

    // ------------------------------------------------------------------ achados em palavras simples

    [Theory]
    [InlineData("win.gamemode")]
    [InlineData("win.gamedvr.capture")]
    [InlineData("mem.speed")]
    [InlineData("owner.xmp")]
    [InlineData("disk.system-free")]
    [InlineData("disk.system-media")]
    [InlineData("owner.rebar")]
    [InlineData("owner.mux")]
    [InlineData("owner.pcie")]
    [InlineData("sec.hvci")]
    [InlineData("gpu.hags")]
    public void Known_findings_never_show_the_technical_title_or_evidence(string id)
    {
        var p = PlainFindings.For(F(id));
        Assert.DoesNotContain("título técnico", p.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("DWord", p.Title + p.WhatToDo, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(p.WhatToDo));
        Assert.True(p.WhatToDo.Count(ch => ch == '.') <= 2, $"{id}: \"O que fazer\" com mais de 2 frases");
    }

    [Fact]
    public void Unknown_findings_use_the_title_and_never_the_evidence()
    {
        var p = PlainFindings.For(F("novo.achado"));
        Assert.Equal("título técnico novo.achado", p.Title);
        Assert.DoesNotContain("DWord", p.WhatToDo, StringComparison.Ordinal);
    }

    [Fact]
    public void Fine_topics_exclude_topics_with_problems_and_unreadable_findings()
    {
        var topics = PlainFindings.FineTopics(
        [
            F("mem.speed", DiagnosticSeverity.Ok), F("mem.channels"), F("disk.system-free", DiagnosticSeverity.Ok),
            F("sec.hvci", DiagnosticSeverity.Info), new DiagnosticFinding("gpu.hags", "HAGS", DiagnosticSeverity.Info, FactStatus.Unavailable, "?"),
            F("proc.not-elevated", DiagnosticSeverity.Ok),
        ]);
        Assert.Equal(["disco", "segurança"], topics);
    }

    [Fact]
    public void Problems_are_ordered_by_impact()
    {
        Assert.True(PlainFindings.ImpactOf(F("disk.system-media")) > PlainFindings.ImpactOf(F("mem.channels")));
        Assert.True(PlainFindings.ImpactOf(F("mem.channels")) > PlainFindings.ImpactOf(F("win.gamemode")));
    }

    // ------------------------------------------------------------------ Assistente automático

    private static ProposedChange C(string action, ActionRisk risk = ActionRisk.Low, bool experimental = false, bool irreversible = false,
        bool undoable = true, bool optIn = false, bool confirmIrreversible = false) =>
        new("id:" + action + risk + experimental + optIn, AssistantStepId.Performance, "t", "d",
            new ActionRequest(action, null, null, confirmIrreversible ? true : null), BrokerLevel.User, risk, experimental, irreversible, undoable, OptIn: optIn);

    [Fact]
    public void Automatic_mode_applies_only_low_risk_recommended_changes()
    {
        Assert.True(AutoPilot.IsAutomatic(C("a@1")));
        Assert.True(AutoPilot.IsAutomatic(C(StepPlanners.RestorePointAction, undoable: false))); // a rede de segurança entra
        Assert.False(AutoPilot.IsAutomatic(C("b@1", ActionRisk.Medium)));
        Assert.False(AutoPilot.IsAutomatic(C("c@1", ActionRisk.High)));
        Assert.False(AutoPilot.IsAutomatic(C("d@1", experimental: true)));
        Assert.False(AutoPilot.IsAutomatic(C("e@1", optIn: true)));                // opcional / avançado
        Assert.False(AutoPilot.IsAutomatic(C("f@1", irreversible: true)));
        Assert.False(AutoPilot.IsAutomatic(C("g@1", confirmIrreversible: true)));
        Assert.False(AutoPilot.IsAutomatic(C("h@1", undoable: false)));            // efeito imediato sem desfazer
        Assert.False(AutoPilot.IsAutomatic(C(StepPlanners.DriverInstallAction)));  // instalar driver fica para o usuário
        Assert.False(AutoPilot.IsAutomatic(C(Nexus.Lab.Gpu.GpuDiagnostics.DisplayModeAction))); // a tela pode apagar
    }

    [Fact]
    public void Automatic_selection_drops_duplicates_and_keeps_order()
    {
        var picked = AutoPilot.Select([C("a@1"), C("x@1", ActionRisk.Medium), C("b@1"), C("a@1"), C("e@1", optIn: true)]);
        Assert.Equal(["a@1", "b@1"], picked.Select(p => p.Request.ActionId));
    }

    [Fact]
    public void Automatic_steps_follow_the_assistant_order_and_cover_the_four_bullets()
    {
        Assert.Equal(AutoPilot.Steps.OrderBy(s => (int)s), AutoPilot.Steps);
        Assert.DoesNotContain(AssistantStepId.Report, AutoPilot.Steps);
        Assert.DoesNotContain(AssistantStepId.Programs, AutoPilot.Steps);
        Assert.Equal(Enum.GetValues<AutoPilot.Phase>().Order(), AutoPilot.Steps.Select(AutoPilot.PhaseOf).Distinct().Order());
    }

    // ------------------------------------------------------------------ "Otimizar meu PC" = Desempenho máximo (06/11/2026)

    private static ProposedChange Cx(string action, string? value = null, Nexus.Core.Classification cls = Nexus.Core.Classification.SupportedDocumented,
        ActionRisk risk = ActionRisk.Low, bool optIn = false, bool undoable = true, bool irreversible = false) =>
        new("id:" + action + value, AssistantStepId.Performance, "t", "d", new ActionRequest(action, value), BrokerLevel.Admin, risk,
            cls is not Nexus.Core.Classification.SupportedDocumented and not Nexus.Core.Classification.Conditional, irreversible, undoable, OptIn: optIn)
        { Classification = cls };

    private static HardwareProfile Hw(bool laptop = false, bool? battery = null, GpuMaker maker = GpuMaker.Nvidia, GpuClass gpu = GpuClass.Mid,
        int ram = 16, bool ssd = true, string? cpu = null) =>
        new(gpu, maker, "GPU", 8, "teste", ram, laptop, battery, ssd, false) { CpuModel = cpu };

    [Fact]
    public void Max_performance_takes_advanced_optional_experimental_and_medium_risk_changes()
    {
        // Diretriz do responsável: o clique aplica TUDO o que dá desempenho e pode ser desfeito, em qualquer PC.
        Assert.True(AutoPilot.IsMaxPerformance(Cx("fps.power.min-processor-state.100.ac@1", risk: ActionRisk.Medium, optIn: true), Hw()));
        Assert.True(AutoPilot.IsMaxPerformance(Cx("hw.nvme.latency-tolerance.ac@1", cls: Nexus.Core.Classification.Experimental, risk: ActionRisk.High, optIn: true), Hw()));
        Assert.True(AutoPilot.IsMaxPerformance(Cx("gpu.nv.fps-cap.off.global@1", optIn: true), Hw()));
        Assert.True(AutoPilot.IsMaxPerformance(Cx(StepPlanners.RestorePointAction, undoable: false), Hw()));
        Assert.True(AutoPilot.IsMaxPerformance(Cx("fortnite.fps.limit@1", "0.000000"), Hw()));
        Assert.True(AutoPilot.IsMaxPerformance(Cx("cs2.cfg.fps_max@1", "0"), Hw()));
    }

    [Fact]
    public void Max_performance_ignores_laptop_battery_heat_and_noise_on_every_kind_of_pc()
    {
        var profiles = new[]
        {
            Hw(laptop: true, battery: false, maker: GpuMaker.Nvidia, gpu: GpuClass.Entry, ram: 8, cpu: "AMD Ryzen 5 5600H"),
            Hw(laptop: true, battery: true, maker: GpuMaker.Intel, gpu: GpuClass.Integrated, ram: 8),
            Hw(), Hw(gpu: GpuClass.Strong), Hw(maker: GpuMaker.Amd, gpu: GpuClass.Strong), Hw(ssd: false, ram: 4, gpu: GpuClass.Entry),
        };
        foreach (var p in profiles)
            foreach (var id in new[] { "fps.power.min-processor-state.100.ac@1", "fps.power.epp.performance.ac@1", "fps.power.pcie-aspm.off.ac@1",
                                       "fps.power.throttling.off@1", "power.processor.min-cores-parking@1", "hw.nvme.latency-tolerance.ac@1" })
                Assert.True(AutoPilot.IsMaxPerformance(Cx(id, optIn: true), p), $"{id} ficou fora em {p}");
        // Em notebook na tomada o modo seguro antigo vetava; o máximo não.
        Assert.False(AutoPilot.IsAutomatic(Cx("fps.power.epp.performance.ac@1"), profiles[0]));
    }

    [Fact]
    public void Max_performance_keeps_vendor_and_worse_here_vetoes()
    {
        Assert.False(AutoPilot.IsMaxPerformance(Cx("gpu.nv.lowlatency.on.per-app@1"), Hw(maker: GpuMaker.Amd)));      // NVIDIA numa AMD
        Assert.False(AutoPilot.IsMaxPerformance(Cx("svc.sysmain.manual.lab@1"), Hw(ssd: false)));                     // SysMain ajuda no HD
        Assert.False(AutoPilot.IsMaxPerformance(Cx("power.processor.min-cores-parking@1"), Hw(cpu: "AMD Ryzen 9 7950X3D 16-Core Processor")));
        Assert.True(AutoPilot.IsMaxPerformance(Cx("power.processor.min-cores-parking@1"), Hw(cpu: "AMD Ryzen 7 7800X3D 8-Core Processor"))); // um CCD só
    }

    [Theory]
    [InlineData("gpu.nv.fps-cap.per-app@1", "141")]
    [InlineData("gpu.nv.vsync.force-on.per-app@1", null)]
    [InlineData("fortnite.fps.limit@1", "141.000000")]
    [InlineData("cs2.cfg.fps_max@1", "400")]
    [InlineData("fps.mouse.acceleration.off.speed@1", null)]
    [InlineData("sched.win32priorityseparation.lab@1", "38")]
    [InlineData("boot.service.xblauthmanager.disabled@1", null)]
    [InlineData("boot.service.spooler.disabled@1", null)]
    [InlineData("cleaning.shader-nvidia@1", null)]
    [InlineData("cleaning.steam-shadercache@1", null)]
    [InlineData("security.defender.realtime@1", null)]
    [InlineData("memorylab.standby.purge@1", null)]
    [InlineData("fps.lab.sched.prefer-performant.ac@1", null)]
    public void Max_performance_never_caps_fps_breaks_features_or_touches_protection(string action, string? value) =>
        Assert.False(AutoPilot.IsMaxPerformance(Cx(action, value, optIn: true), Hw()));

    [Fact]
    public void Max_performance_never_takes_myths_irreversible_or_momentary_changes()
    {
        foreach (var cls in new[] { Nexus.Core.Classification.Rejected, Nexus.Core.Classification.Obsolete, Nexus.Core.Classification.Ineffective, Nexus.Core.Classification.Incompatible })
            Assert.False(AutoPilot.IsMaxPerformance(Cx("fps.some.myth@1", cls: cls), Hw()));
        Assert.False(AutoPilot.IsMaxPerformance(Cx("x.y@1", irreversible: true, undoable: false), Hw()));
        Assert.False(AutoPilot.IsMaxPerformance(Cx("x.z@1", undoable: false), Hw()));
        Assert.False(AutoPilot.IsMaxPerformance(Cx(StepPlanners.DriverInstallAction), Hw()));
    }

    [Fact]
    public void Max_performance_security_guard_matches_every_protection_term_and_no_catalog_action_needs_it()
    {
        foreach (var id in new[] { "x.defender.off", "x.firewall.off", "x.uac.off", "x.smartscreen.off", "x.vbs.off", "x.hvci.off", "x.memory-integrity.off",
                                   "x.mitigation.off", "x.spectre.off", "x.windowsupdate.pause", "x.smart-app-control.off" })
            Assert.True(AutoPilot.TouchesSecurity(id), id);
        Assert.False(AutoPilot.TouchesSecurity("fps.power.epp.performance.ac"));
        // Nenhuma ação de desempenho do catálogo esbarra na trava (senão a trava esconderia um ajuste legítimo, ou o catálogo teria um problema).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        var doc = Nexus.Core.CatalogLoader.LoadDirectory(Path.Combine(dir!.FullName, "catalog"));
        var hits = doc.Actions.Select(a => a.Definition.Id)
            .Where(id => !id.StartsWith("security.", StringComparison.Ordinal) && !id.StartsWith("scanner.", StringComparison.Ordinal) && AutoPilot.TouchesSecurity(id)).ToList();
        Assert.True(hits.Count == 0, "Ações que a trava de segurança bloqueia: " + string.Join(", ", hits));
    }

    [Fact]
    public void Max_performance_report_says_the_real_reason_never_unchecked_by_you()
    {
        Assert.Contains("nunca limita", AutoPilot.MaxExclusionReason(Cx("gpu.nv.fps-cap.per-app@1", "141"), Hw()), StringComparison.Ordinal);
        Assert.Contains("placa de vídeo", AutoPilot.MaxExclusionReason(Cx("gpu.nv.lowlatency.on.per-app@1"), Hw(maker: GpuMaker.Amd)), StringComparison.Ordinal);
        Assert.Contains("X3D", AutoPilot.MaxExclusionReason(Cx("power.processor.min-cores-parking@1"), Hw(cpu: "Ryzen 9 9950X3D")), StringComparison.Ordinal);
        var state = new AssistantState();
        AssistantRunner.RecordUnselected(state, [Cx("fps.mouse.acceleration.off.speed@1")], Hw(), maxPerformance: true);
        Assert.DoesNotContain("Desmarcado por você", Assert.Single(state.Skipped).Why, StringComparison.Ordinal);
    }

    private static readonly string[] TestCatalogKeys = ["t.user.low@1", "t.admin.low@1", "t.user.medium@1", "t.exp@1", "t.ephemeral@1"];

    [Fact]
    public void Automatic_mode_on_the_test_catalog_never_picks_experimental_optional_or_irreversible()
    {
        var c = Modules.AssistantFakes.Cat();
        var all = TestCatalogKeys.Select(k => Modules.AssistantFakes.Change(c, k)).ToList();
        all.Add(Modules.AssistantFakes.Change(c, "t.user.low2@1", optIn: true));
        var picked = AutoPilot.Select(all).Select(p => p.Request.ActionId).ToList();
        Assert.Equal(["t.user.low@1", "t.admin.low@1"], picked);
    }

    // ------------------------------------------------------------------ Histórico simples

    [Fact]
    public void History_lines_never_show_action_keys()
    {
        var cleaning = new[] { "cleaning.chrome-cache@1", "cleaning.temp@1", "cleaning.edge-cache@1", "system.restore.create-point@1" };
        var line = SimpleHistoryText.Describe(cleaning);
        Assert.Equal("Limpeza: 4 itens limpos", line);
        Assert.DoesNotContain("@", line, StringComparison.Ordinal);
        Assert.Equal("Privacidade: 1 mudança", SimpleHistoryText.Describe(["privacy.ads.off@1"]));
        Assert.Equal("Inicialização: 2 mudanças", SimpleHistoryText.Describe(["boot.startup.x@1", "boot.startup.y@1"]));
        Assert.Equal("Ajustes do Windows: 1 mudança", SimpleHistoryText.Describe(["sys.something@1"]));
    }

    [Fact]
    public void History_dates_are_friendly()
    {
        var now = new DateTime(2026, 11, 2, 18, 0, 0);
        Assert.Equal("Hoje 14:30", SimpleHistoryText.When(new DateTime(2026, 11, 2, 14, 30, 0), now));
        Assert.Equal("Ontem 09:05", SimpleHistoryText.When(new DateTime(2026, 11, 1, 9, 5, 0), now));
        Assert.Equal("28/10 14:30", SimpleHistoryText.When(new DateTime(2026, 10, 28, 14, 30, 0), now));
    }

    [Fact]
    public void Optimize_summary_is_short_and_friendly()
    {
        Assert.Equal("Pronto! Liberamos 9,3 GB e ajustamos 12 coisas.", SimpleOptimizeText.Summary(9.3 * Gb, 12));
        Assert.Equal("Pronto! Ajustamos 3 coisas.", SimpleOptimizeText.Summary(0, 3));
        Assert.Equal("Pronto! Seu PC já estava em dia: nada precisou mudar.", SimpleOptimizeText.Summary(0, 0));
    }

    // ------------------------------------------------------------------ Ajustes rápidos e um clique

    [Theory]
    [InlineData("privacy.ads.off", QuickCategory.Privacy)]
    [InlineData("power.active-scheme.high-performance", QuickCategory.Power)]
    [InlineData("sys.power-throttling.off", QuickCategory.Power)]
    [InlineData("fps.mpo.off", QuickCategory.Games)]
    [InlineData("win.gamemode.enable", QuickCategory.Games)]
    [InlineData("gpu.hags.lab", QuickCategory.Games)]
    [InlineData("sys.ntfs.lastaccess", QuickCategory.Speed)]
    [InlineData("boot.fast-startup.off", QuickCategory.Speed)]
    public void Quick_tweaks_fall_into_plain_groups(string id, QuickCategory expected) => Assert.Equal(expected, QuickTweakText.CategoryOf(id));

    [Fact]
    public void Quick_tweak_groups_and_risk_words_are_plain()
    {
        Assert.Equal(["Velocidade", "Jogos", "Privacidade", "Energia"], Enum.GetValues<QuickCategory>().Select(QuickTweakText.CategoryTitle));
        Assert.Equal("pode gastar mais bateria", QuickTweakText.RiskWord(QuickCategory.Power, ActionRisk.Low, false));
        Assert.Equal("experimental: pode não ajudar", QuickTweakText.RiskWord(QuickCategory.Power, ActionRisk.Low, true));
        Assert.Equal("pode ter efeito colateral", QuickTweakText.RiskWord(QuickCategory.Speed, ActionRisk.Medium, false));
        Assert.Equal("você decide", QuickTweakText.RiskWord(QuickCategory.Privacy, ActionRisk.Low, false));
    }

    [Theory]
    [InlineData("Libera a placa de vídeo. Ganho medido em jogos leves.", "Libera a placa de vídeo.")]
    [InlineData("Uma frase só", "Uma frase só")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Quick_tweak_sentence_is_the_first_sentence(string? text, string expected) => Assert.Equal(expected, QuickTweakText.FirstSentence(text));

    [Fact]
    public void Long_sentences_are_cut()
    {
        var s = QuickTweakText.FirstSentence(new string('a', 300));
        Assert.True(s.Length <= 140);
        Assert.EndsWith("…", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Finding_cards_carry_the_fix_action_for_one_click()
    {
        var card = Assert.Single(ImproveCards.Top(Clean() with
        {
            Findings = [new DiagnosticFinding("win.gamemode", "Modo de Jogo desativado", DiagnosticSeverity.Attention, FactStatus.Detected, "x", "win.gamemode.enable")],
        }));
        Assert.Equal("win.gamemode", card.FindingId);
        Assert.Equal("win.gamemode.enable", card.FixActionId);
    }

    // ------------------------------------------------------------------ Início: "Seu PC agora" e a faixa "Situação" (05/11/2026)

    [Theory]
    [InlineData(5, "Trabalhando tranquilo.")]
    [InlineData(50, "Trabalhando em ritmo normal.")]
    [InlineData(80, "Trabalhando bastante.")]
    [InlineData(95, "Seu processador está trabalhando intensamente.")]
    public void Live_cpu_speaks_plain_language(double percent, string expected) => Assert.Equal(expected, TelemetryText.Cpu(percent));

    [Fact]
    public void Live_gpu_warns_when_hot_and_never_invents_usage()
    {
        Assert.Equal("Quente: pode reduzir o desempenho para se proteger.", TelemetryText.Gpu(20, 90));
        Assert.Equal("Em repouso.", TelemetryText.Gpu(3, 45));
        Assert.Equal("O Windows não mostra o uso desta placa.", TelemetryText.Gpu(double.NaN, null));
        Assert.Equal("", TelemetryText.Celsius(null));
        Assert.Equal("47 °C", TelemetryText.Celsius(46.6));
        Assert.Equal("3,3 GHz", TelemetryText.Ghz(3290));
        Assert.Equal("", TelemetryText.Ghz(null));
    }

    [Fact]
    public void Live_memory_and_disk_flag_only_real_limits()
    {
        Assert.False(TelemetryText.Ram(0.4).Warn);
        Assert.Equal("Sua memória RAM está próxima do limite.", TelemetryText.Ram(0.9).Text);
        Assert.True(TelemetryText.Ram(0.9).Warn);
        const double Gb = 1024d * 1024 * 1024;
        Assert.False(TelemetryText.Disk(134 * Gb, 476 * Gb).Warn);
        Assert.True(TelemetryText.Disk(10 * Gb, 476 * Gb).Warn);   // menos de 15 GB
        Assert.True(TelemetryText.Disk(90 * Gb, 1000 * Gb).Warn);  // menos de 10 %
        Assert.False(TelemetryText.Disk(double.NaN, 0).Warn);
        Assert.Equal("6,1", TelemetryText.Gigabytes(6.1 * Gb));
        Assert.Equal("134", TelemetryText.Gigabytes(134 * Gb));
    }

    [Fact]
    public void Status_strip_reads_only_what_was_measured()
    {
        var empty = StatusStrip.Build(new HealthInputs());
        Assert.Equal(4, empty.Count);
        Assert.Equal("—", empty[0].Value);   // driver sem leitura
        Assert.Equal("—", empty[2].Value);   // limpeza ainda medindo
        Assert.Equal("—", empty[3].Value);   // memória sem leitura

        var now = new DateTime(2026, 11, 5);
        var strip = StatusStrip.Build(new HealthInputs
        {
            Now = now,
            GpuDriverDate = new DateTime(2023, 3, 1),
            Findings = [new DiagnosticFinding("win.gamemode", "x", DiagnosticSeverity.Attention, FactStatus.Detected, "x")],
            RecoverableBytes = 4.2 * 1024 * 1024 * 1024,
            MemoryModules = 1,
            MemorySlots = 2,
        });
        Assert.Equal("De 2023", strip[0].Value);
        Assert.True(strip[0].IsWarn);
        Assert.Equal("1 ponto", strip[1].Value);
        Assert.Equal("SimpleProblemsPage", strip[1].Page);
        Assert.True(strip[2].IsWarn);
        Assert.Equal("Um pente só", strip[3].Value);
        Assert.Equal("MemoryLabPage", strip[3].Page);
    }
}
