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
        "PrivacyPage", "SecurityPage", "ScannerPage", "SystemToolsPage", "DriverUpdatesPage", "MeasurePage", "MonitorPage", "HistoryPage",
        "SettingsPage", "CapabilitiesPage",
    ];

    private static IEnumerable<string> AllPages => FullPages.Concat(ShellModes.SimplePages);

    public SimpleModeTests() => Loc.Use("pt-BR");

    // ------------------------------------------------------------------ modos

    [Fact]
    public void Simple_mode_shows_at_most_six_pages_in_a_fixed_order()
    {
        var nav = ShellModes.Navigation(UiMode.Simple, AllPages.Reverse(), p => p);
        Assert.True(nav.Count <= 6);
        Assert.Equal(["HomePage", "SimpleOptimizePage", "SimpleCleanPage", "SimpleGamesPage", "SimpleProblemsPage", "SimpleHistoryPage"], nav);
        Assert.Equal("HomePage", ShellModes.StartPage(UiMode.Simple));
    }

    [Fact]
    public void Full_mode_keeps_every_existing_page_and_hides_the_simple_screens()
    {
        var nav = ShellModes.Navigation(UiMode.Full, AllPages, p => p);
        Assert.Equal(FullPages, nav);
        Assert.All(ShellModes.SimplePages, p => Assert.DoesNotContain(p, nav));
        Assert.Equal("OverviewPage", ShellModes.StartPage(UiMode.Full));
        Assert.All(FullPages, p => Assert.False(ShellModes.IsInNavigation(UiMode.Simple, p)));
    }

    [Theory]
    [InlineData("simple", UiMode.Simple)]
    [InlineData("FULL", UiMode.Full)]
    [InlineData(" full ", UiMode.Full)]
    public void Stored_mode_round_trips(string stored, UiMode expected)
    {
        Assert.Equal(expected, ShellModes.Parse(stored));
        Assert.Equal(expected, ShellModes.Parse(ShellModes.Store(expected)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("advanced")]
    public void Unknown_stored_mode_means_not_chosen(string? stored) => Assert.Null(ShellModes.Parse(stored));

    [Fact]
    public void New_install_gets_the_welcome_and_simple_mode()
    {
        Assert.Equal(new UiStartup(UiMode.Simple, true, false), UiModeStartup.Decide(null, hadPriorUse: false));
    }

    [Fact]
    public void Existing_user_after_the_update_gets_simple_mode_with_a_one_time_notice()
    {
        Assert.Equal(new UiStartup(UiMode.Simple, false, true), UiModeStartup.Decide(null, hadPriorUse: true));
    }

    [Theory]
    [InlineData("full", true)]
    [InlineData("full", false)]
    [InlineData("simple", true)]
    public void A_stored_choice_is_respected_without_welcome_or_notice(string stored, bool prior)
    {
        var s = UiModeStartup.Decide(stored, prior);
        Assert.Equal(ShellModes.Parse(stored), s.Mode);
        Assert.False(s.ShowWelcome);
        Assert.False(s.ShowSimpleNotice);
    }

    [Fact]
    public void Settings_keep_the_mode_and_default_to_not_chosen()
    {
        Assert.Null(NexusSettings.Defaults.UiMode);
        var path = Path.Combine(Path.GetTempPath(), "nexus-simple-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null((NexusSettings.Defaults with { UiMode = "full" }).Save(path));
            Assert.Equal("full", NexusSettings.Load(path).UiMode);
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
}
