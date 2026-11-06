using Nexus.Assistant;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.GameProfiles.Cards;
using Nexus.HardwareDiscovery;

namespace Nexus.Tests.Modules;

/// <summary>
/// Recomendação automática pelo hardware (docs/modules/recommendation.md): classificação de PCs fictícios, as regras por ação e
/// por público de opção de jogo, a seleção padrão dos Ajustes rápidos/Automático e os textos (chaves resx nos 3 idiomas).
/// </summary>
[Trait("Category", "Unit")]
[Collection(LocalizationState.Name)]
public sealed class HardwareRecommendationTests
{
    public HardwareRecommendationTests() => Loc.Use("pt-BR");

    private const ulong Gb = 1024UL * 1024 * 1024;

    private static SystemFacts Facts(string? gpu = null, string? vendor = null, ulong? vram = null, int? ramGb = 16, DiskMedia media = DiskMedia.Unknown) => new()
    {
        Gpus = gpu is null ? [] : [new GpuInfo(gpu, vendor, null, null, null, vram)],
        TotalMemoryBytes = ramGb is { } r ? (ulong)r * Gb : null,
        SystemDiskMedia = media,
    };

    private static readonly PcTraits Laptop = new(ChassisType: 10, HasSystemBattery: true, OnAcPower: true, SystemDriveIsSsd: true);
    private static readonly PcTraits LaptopOnBattery = Laptop with { OnAcPower = false, BatteryPercent = 40 };
    private static readonly PcTraits Desktop = new(ChassisType: 3, HasSystemBattery: false, SystemDriveIsSsd: true);

    // ------------------------------------------------------------------ classificação

    [Fact]
    public void Laptop_with_gtx_1650_is_entry_nvidia_laptop_on_ac()
    {
        var p = HardwareProfiler.Classify(Facts("NVIDIA GeForce GTX 1650", "NVIDIA", 4 * Gb), Laptop);
        Assert.Equal(GpuClass.Entry, p.GpuClass);
        Assert.Equal(GpuMaker.Nvidia, p.GpuMaker);
        Assert.Equal("GTX 1650", p.GpuName);
        Assert.Equal(4, p.VramGb);
        Assert.True(p.IsLaptop);
        Assert.False(p.OnBattery);
        Assert.True(p.Ssd);
        Assert.Equal(16, p.MemoryGb);
        Assert.False(p.LowRam);
        Assert.True(p.WeakGpu);
        Assert.Contains("xx50", p.GpuClassSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_with_rtx_4070_is_strong_and_not_laptop()
    {
        var p = HardwareProfiler.Classify(Facts("NVIDIA GeForce RTX 4070", "NVIDIA", 12 * Gb, 32), Desktop);
        Assert.Equal(GpuClass.Strong, p.GpuClass);
        Assert.False(p.IsLaptop);
        Assert.Null(p.OnBattery);
        Assert.True(p.StrongGpu);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", GpuClass.Mid)]
    [InlineData("NVIDIA GeForce GTX 1080", "NVIDIA", GpuClass.Mid)]
    [InlineData("NVIDIA GeForce RTX 3050", "NVIDIA", GpuClass.Entry)]
    [InlineData("NVIDIA GeForce MX450", "NVIDIA", GpuClass.Entry)]
    [InlineData("NVIDIA GeForce RTX 5090", "NVIDIA", GpuClass.Strong)]
    [InlineData("AMD Radeon RX 6600", "AMD", GpuClass.Mid)]
    [InlineData("AMD Radeon RX 7800 XT", "AMD", GpuClass.Strong)]
    [InlineData("AMD Radeon RX 6500 XT", "AMD", GpuClass.Entry)]
    [InlineData("AMD Radeon RX 580", "AMD", GpuClass.Mid)]
    [InlineData("AMD Radeon RX 550", "AMD", GpuClass.Entry)]
    [InlineData("AMD Radeon RX 9070 XT", "AMD", GpuClass.Strong)]
    [InlineData("AMD Radeon RX 9060 XT", "AMD", GpuClass.Mid)]
    [InlineData("Intel(R) Arc(TM) A380 Graphics", "Intel", GpuClass.Entry)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Intel", GpuClass.Mid)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", "Intel", GpuClass.Mid)]
    public void Discrete_families_are_classified_by_the_documented_table(string name, string vendor, GpuClass expected)
    {
        Assert.Equal(expected, HardwareProfiler.Classify(Facts(name, vendor), Desktop).GpuClass);
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 620", "Intel")]
    [InlineData("AMD Radeon(TM) Graphics", "AMD")]
    [InlineData("AMD Radeon Vega 8 Graphics", "AMD")]
    public void Integrated_only_is_integrated(string name, string vendor)
    {
        var p = HardwareProfiler.Classify(Facts(name, vendor, 512UL << 20, 8), Laptop);
        Assert.Equal(GpuClass.Integrated, p.GpuClass);
        Assert.True(p.WeakGpu);
        Assert.True(p.LowRam);
    }

    [Fact]
    public void Discrete_beats_integrated_and_bigger_vram_is_the_main_card()
    {
        var facts = new SystemFacts
        {
            Gpus = [new GpuInfo("Intel(R) Iris(R) Xe Graphics", "Intel", null, null, null, 128UL << 20), new GpuInfo("NVIDIA GeForce RTX 3060 Laptop GPU", "NVIDIA", null, null, null, 6 * Gb)],
            TotalMemoryBytes = 16 * Gb,
        };
        var p = HardwareProfiler.Classify(facts, Laptop);
        Assert.Equal(GpuClass.Mid, p.GpuClass);
        Assert.Equal(GpuMaker.Nvidia, p.GpuMaker);
    }

    [Fact]
    public void Unknown_family_falls_back_to_vram_and_says_so()
    {
        Assert.Equal((GpuClass.Entry, "família desconhecida: 2 GB de VRAM"), HardwareProfiler.ClassOf("NVIDIA Quadro P620", GpuMaker.Nvidia, 2));
        Assert.Equal(GpuClass.Mid, HardwareProfiler.ClassOf("NVIDIA Quadro P2000", GpuMaker.Nvidia, 5).Class);
        Assert.Equal(GpuClass.Strong, HardwareProfiler.ClassOf("NVIDIA RTX A4000", GpuMaker.Nvidia, 16).Class);
        Assert.Equal(GpuClass.Mid, HardwareProfiler.ClassOf("Placa estranha", GpuMaker.Unknown, null).Class);
    }

    [Fact]
    public void Nothing_read_means_nothing_assumed()
    {
        var p = HardwareProfiler.Classify(new SystemFacts(), PcTraits.Unknown);
        Assert.Equal(GpuClass.Unknown, p.GpuClass);
        Assert.Null(p.IsLaptop);
        Assert.Null(p.OnBattery);
        Assert.Null(p.Ssd);
        Assert.Null(p.MemoryGb);
        Assert.False(p.LowRam);
        Assert.False(p.Hdd);
    }

    [Fact]
    public void Battery_only_without_chassis_infers_laptop_and_disk_media_comes_from_discovery_when_traits_lack_it()
    {
        var p = HardwareProfiler.Classify(Facts(media: DiskMedia.Hdd), new PcTraits(HasSystemBattery: true, OnAcPower: false));
        Assert.True(p.IsLaptop);
        Assert.True(p.OnBattery);
        Assert.False(p.Ssd);
        Assert.True(p.Hdd);
    }

    [Fact]
    public void Software_adapters_are_ignored()
    {
        var facts = new SystemFacts { Gpus = [new GpuInfo("Microsoft Basic Display Adapter", "Microsoft", null, null, null, null)] };
        Assert.Equal(GpuClass.Unknown, HardwareProfiler.Classify(facts).GpuClass);
    }

    // ------------------------------------------------------------------ regras por ação

    private static HardwareProfile Profile(GpuClass gpu = GpuClass.Mid, GpuMaker maker = GpuMaker.Nvidia, int? ram = 16, bool? laptop = false, bool? battery = null, bool? ssd = true, bool competitive = false) =>
        new(gpu, maker, "GPU", 8, "teste", ram, laptop, battery, ssd, competitive);

    [Fact]
    public void Max_performance_power_tweaks_are_not_recommended_on_laptops_and_worse_on_battery()
    {
        var onBattery = Profile(laptop: true, battery: true);
        var laptop = Profile(laptop: true, battery: false);
        var desktop = Profile();
        foreach (var id in new[] { "power.active-scheme.high-performance.session@1", "fps.power.epp.performance.ac@1", "fps.power.min-processor-state.100.ac", "gpu.nv.power.max.per-app@1" })
        {
            Assert.Equal(Fit.No(FitReason.OnBattery), HardwareRules.ForAction(onBattery, id));
            Assert.Equal(Fit.No(FitReason.LaptopHeat), HardwareRules.ForAction(laptop, id));
            Assert.Equal(Fit.Neutral, HardwareRules.ForAction(desktop, id)); // desktop: sem medição que prove ganho = neutro
        }
    }

    [Fact]
    public void Integrated_gpu_recommends_fortnite_performance_mode_and_rejects_hags_and_dx12_full()
    {
        var p = Profile(GpuClass.Integrated, GpuMaker.Intel, laptop: true);
        Assert.Equal(Fit.Yes(FitReason.WeakGpuRelief), HardwareRules.ForAction(p, "fortnite.render.performance-mode@1"));
        Assert.Equal(Fit.No(FitReason.WeakGpuRelief), HardwareRules.ForAction(p, "fortnite.render.dx12-full@1"));
        Assert.Equal(Fit.No(FitReason.IntegratedGpu), HardwareRules.ForAction(p, "gpu.hags.lab@1"));
        Assert.Equal(Fit.Yes(FitReason.WeakGpuRelief), HardwareRules.ForAction(p, "sys.ui.transparency.off@1"));
        Assert.Equal(Fit.Yes(FitReason.WeakGpuRelief), HardwareRules.ForAction(p, "fortnite.visual.shadows.off@1"));
    }

    [Fact]
    public void Strong_gpu_prefers_dx12_full_and_not_performance_mode()
    {
        var p = Profile(GpuClass.Strong);
        Assert.Equal(Fit.Yes(FitReason.StrongGpuUse), HardwareRules.ForAction(p, "fortnite.render.dx12-full@1"));
        Assert.Equal(Fit.No(FitReason.StrongGpuNoNeed), HardwareRules.ForAction(p, "fortnite.render.performance-mode@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(p, "fortnite.visual.shadows.off@1"));
    }

    [Fact]
    public void Low_ram_recommends_memory_relief_and_keeps_sysmain()
    {
        var p = Profile(ram: 8);
        Assert.Equal(Fit.Yes(FitReason.LowRamRelief), HardwareRules.ForAction(p, "memorylab.compression.enable@1"));
        Assert.Equal(Fit.Yes(FitReason.LowRamRelief), HardwareRules.ForAction(p, "memorylab.browser.edge-sleepingtabs-on"));
        Assert.Equal(Fit.No(FitReason.SysMainHelpsHere), HardwareRules.ForAction(p, "svc.sysmain.manual.lab@1"));
        Assert.Equal(Fit.No(FitReason.SysMainHelpsHere), HardwareRules.ForAction(p, "boot.service.sysmain.manual@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(ram: 16), "memorylab.compression.enable@1"));
    }

    [Fact]
    public void Hdd_keeps_sysmain_and_recommends_fewer_metadata_writes()
    {
        var p = Profile(ssd: false);
        Assert.Equal(Fit.No(FitReason.SysMainHelpsHere), HardwareRules.ForAction(p, "svc.sysmain.manual.lab@1"));
        Assert.Equal(Fit.Yes(FitReason.HddFewerWrites), HardwareRules.ForAction(p, "sys.ntfs.last-access.disable@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(ssd: true), "sys.ntfs.last-access.disable@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(ssd: null), "svc.sysmain.manual.lab@1"));
    }

    [Fact]
    public void Nvidia_driver_tweaks_are_rejected_on_amd_and_neutral_without_a_reading()
    {
        Assert.Equal(Fit.No(FitReason.NotNvidia), HardwareRules.ForAction(Profile(maker: GpuMaker.Amd), "gpu.nv.lowlatency.on.per-app@1"));
        Assert.Equal(Fit.No(FitReason.NotNvidia), HardwareRules.ForAction(Profile(maker: GpuMaker.Amd), "fortnite.latency.reflex@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(maker: GpuMaker.Unknown), "gpu.nv.lowlatency.on.per-app@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(maker: GpuMaker.Nvidia), "gpu.nv.lowlatency.on.per-app@1")); // sem preferência: neutro
    }

    [Fact]
    public void Removing_fps_caps_is_recommended_on_nvidia_and_setting_caps_never_is()
    {
        foreach (var id in new[] { "gpu.nv.fps-cap.off.per-app@1", "gpu.nv.vsync.off.per-app@1", "gpu.nv.fps-cap.off.global@1", "gpu.nv.vsync.off.global@1" })
        {
            Assert.Equal(Fit.Yes(FitReason.Uncapped), HardwareRules.ForAction(Profile(maker: GpuMaker.Nvidia), id));
            Assert.Equal(Fit.Yes(FitReason.Uncapped), HardwareRules.ForAction(Profile(maker: GpuMaker.Nvidia, laptop: true), id));
            Assert.Equal(Fit.No(FitReason.NotNvidia), HardwareRules.ForAction(Profile(maker: GpuMaker.Amd), id));
        }
        foreach (var p in new[] { Profile(), Profile(laptop: true), Profile(competitive: true), Profile(maker: GpuMaker.Nvidia, laptop: true) })
            foreach (var cap in new[] { "gpu.nv.fps-cap.per-app@1", "gpu.nv.vsync.force-on.per-app@1", "fortnite.fps.limit@1", "cs2.cfg.fps_max@1" })
                Assert.False(HardwareRules.ForAction(p, cap).IsRecommended, $"{cap} recomendado em {p}");
    }

    [Fact]
    public void Competitive_is_a_preference_not_hardware()
    {
        var p = Profile(competitive: true);
        Assert.Equal(Fit.Yes(FitReason.Competitive), HardwareRules.ForAction(p, "gpu.nv.lowlatency.on.per-app@1"));
        Assert.Equal(Fit.Yes(FitReason.Competitive), HardwareRules.ForAction(p, "fortnite.latency.reflex@1"));
        Assert.Equal(Fit.Yes(FitReason.Competitive), HardwareRules.ForAction(p, "fortnite.visual.motion-blur.off@1"));
        Assert.Equal(Fit.Yes(FitReason.Competitive), HardwareRules.ForAction(p, "gpu.nv.power.max.per-app@1"));
        // Na bateria a preferência não vence o hardware.
        Assert.Equal(Fit.No(FitReason.OnBattery), HardwareRules.ForAction(p with { IsLaptop = true, OnBattery = true }, "gpu.nv.power.max.per-app@1"));
    }

    [Fact]
    public void Laptop_specific_recommendations()
    {
        var p = Profile(laptop: true);
        Assert.Equal(Fit.Yes(FitReason.LaptopWifi), HardwareRules.ForAction(p, "net.wifi.power-saving.dc-max-perf@1"));
        // Diretriz do responsável (06/11/2026): limite de FPS nunca é recomendado, nem em notebook.
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(p, "fortnite.fps.limit@1"));
        Assert.Equal(Fit.Yes(FitReason.BackgroundCapture), HardwareRules.ForAction(p, "win.gamedvr.background-capture.disable@1"));
        Assert.Equal(Fit.Yes(FitReason.LaptopHeat), HardwareRules.ForAction(p, "sys.ui.transparency.off@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(), "net.wifi.power-saving.dc-max-perf@1"));
    }

    [Fact]
    public void Unknown_actions_and_empty_profile_are_neutral()
    {
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(), "cleaning.temp-user@1"));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAction(Profile(), "privacy.telemetry.required@1"));
        foreach (var id in new[] { "power.active-scheme.high-performance.session", "gpu.hags.lab", "fortnite.render.performance-mode", "svc.sysmain.manual.lab", "gpu.nv.power.max.per-app", "net.wifi.power-saving.dc-max-perf" })
            Assert.Equal(Fit.Neutral, HardwareRules.ForAction(HardwareProfile.Empty, id));
    }

    [Fact]
    public void Every_rule_reason_has_a_resx_key_in_the_three_languages()
    {
        try
        {
            foreach (var lang in new[] { "pt-BR", "en", "es" })
            {
                Loc.Use(lang);
                foreach (var r in Enum.GetValues<FitReason>().Where(r => r != FitReason.None))
                {
                    var text = FitText.Reason(r);
                    Assert.False(string.IsNullOrEmpty(text), r.ToString());
                    Assert.DoesNotContain("Fit.", text, StringComparison.Ordinal); // chave sem tradução viria como a própria chave
                }
            }
        }
        finally { Loc.Use("pt-BR"); }
        Assert.Equal("", FitText.Reason(FitReason.None));
        Assert.Equal("", FitText.Sentence(Fit.Neutral));
        Assert.StartsWith("Não indicado", FitText.Sentence(Fit.No(FitReason.LaptopHeat)), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ público das opções de jogo

    [Fact]
    public void Audience_matches_the_profile()
    {
        var weakNvidiaLaptop = Profile(GpuClass.Entry, GpuMaker.Nvidia, 8, laptop: true);
        Assert.Equal(Fit.Neutral, HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.Everyone));
        Assert.Equal(Fit.Yes(FitReason.WeakGpuRelief), HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.WeakGpu));
        Assert.Equal(Fit.No(FitReason.WeakGpuRelief), HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.StrongGpu));
        Assert.Equal(Fit.Yes(FitReason.LowRamRelief), HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.LowRam));
        Assert.Equal(Fit.Yes(FitReason.LaptopCooler), HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.Laptop));
        Assert.Equal(Fit.Yes(FitReason.NvidiaGpu), HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.Nvidia));
        Assert.Equal(Fit.No(FitReason.NotAmd), HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.Amd));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAudience(weakNvidiaLaptop, GameCardAudience.Competitive));
        Assert.Equal(Fit.Yes(FitReason.Competitive), HardwareRules.ForAudience(weakNvidiaLaptop with { Competitive = true }, GameCardAudience.Competitive));
    }

    [Fact]
    public void Audience_wrong_vendor_beats_any_match_and_unknown_profile_is_neutral()
    {
        var p = Profile(GpuClass.Entry, GpuMaker.Amd);
        Assert.Equal(Fit.No(FitReason.NotNvidia), HardwareRules.ForAudience(p, GameCardAudience.WeakGpu | GameCardAudience.Nvidia));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAudience(HardwareProfile.Empty, GameCardAudience.WeakGpu | GameCardAudience.Nvidia | GameCardAudience.Laptop));
        Assert.Equal(Fit.No(FitReason.StrongGpuNoNeed), HardwareRules.ForAudience(Profile(GpuClass.Strong), GameCardAudience.WeakGpu));
        Assert.Equal(Fit.Neutral, HardwareRules.ForAudience(Profile(GpuClass.Mid), GameCardAudience.WeakGpu)); // intermediária: nem fraca nem forte
    }

    // ------------------------------------------------------------------ seleção padrão

    [Fact]
    public void Default_selection_needs_low_risk_and_no_hardware_veto()
    {
        Assert.True(QuickTweakSelection.IsRecommended(true, Fit.Neutral));
        Assert.True(QuickTweakSelection.IsRecommended(true, Fit.Yes(FitReason.LaptopWifi)));
        Assert.False(QuickTweakSelection.IsRecommended(true, Fit.No(FitReason.LaptopHeat)));
        Assert.False(QuickTweakSelection.IsRecommended(false, Fit.Yes(FitReason.LaptopWifi))); // recomendado pelo hardware não dispensa o risco baixo
    }

    private static ProposedChange C(string action) =>
        new("id:" + action, AssistantStepId.Performance, "t", "d", new ActionRequest(action), BrokerLevel.User, ActionRisk.Low, false, false, true);

    [Fact]
    public void Automatic_mode_skips_what_the_hardware_advises_against()
    {
        var laptop = Profile(laptop: true, battery: true);
        var changes = new[] { C("power.active-scheme.high-performance.session@1"), C("win.gamemode.enable@1"), C("net.wifi.power-saving.dc-max-perf@1") };
        Assert.Equal(["win.gamemode.enable@1", "net.wifi.power-saving.dc-max-perf@1"], AutoPilot.Select(changes, laptop).Select(c => c.Request.ActionId));
        Assert.Equal(3, AutoPilot.Select(changes, null).Count); // sem perfil: comportamento de antes
        Assert.Equal(3, AutoPilot.Select(changes).Count);
        Assert.False(AutoPilot.IsAutomatic(C("gpu.nv.power.max.per-app@1"), laptop));
        Assert.True(AutoPilot.IsAutomatic(C("gpu.nv.power.max.per-app@1"), Profile()));
    }

    // ------------------------------------------------------------------ Ajustes › Perfil do PC

    [Fact]
    public void Profile_headline_in_simple_words()
    {
        var p = new HardwareProfile(GpuClass.Entry, GpuMaker.Nvidia, "GTX 1650", 4, "x", 16, true, false, true, false);
        Assert.Equal("Notebook com placa de entrada (GTX 1650, 4 GB), 16 GB de RAM, SSD", HardwareProfileText.Headline(p));
        Assert.Equal("", HardwareProfileText.Unknowns(p));

        var integrated = new HardwareProfile(GpuClass.Integrated, GpuMaker.Amd, "Graphics", null, "x", 8, true, true, false, false);
        Assert.Equal("Notebook com vídeo integrado (Graphics), 8 GB de RAM, disco rígido (HD), na bateria agora", HardwareProfileText.Headline(integrated));

        Assert.Equal("PC", HardwareProfileText.Headline(HardwareProfile.Empty));
        Assert.Contains("placa de vídeo, memória RAM, notebook ou desktop, tipo do disco", HardwareProfileText.Unknowns(HardwareProfile.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_headline_is_translated()
    {
        var p = new HardwareProfile(GpuClass.Strong, GpuMaker.Nvidia, "RTX 4070", 12, "x", 32, false, null, true, false);
        try
        {
            Loc.Use("en");
            Assert.Equal("Desktop with strong graphics card (RTX 4070, 12 GB), 32 GB of RAM, SSD", HardwareProfileText.Headline(p));
            Loc.Use("es");
            Assert.StartsWith("PC de escritorio con tarjeta gráfica potente", HardwareProfileText.Headline(p), StringComparison.Ordinal);
        }
        finally { Loc.Use("pt-BR"); }
    }

    [Fact]
    public void Prefer_low_latency_setting_defaults_off_and_round_trips()
    {
        Assert.False(NexusSettings.Defaults.PreferLowLatency);
        var dir = Path.Combine(Path.GetTempPath(), "nexus-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.Null((NexusSettings.Defaults with { PreferLowLatency = true }).Save(path));
            Assert.True(NexusSettings.Load(path).PreferLowLatency);
        }
        finally { Directory.Delete(dir, true); }
    }
}
