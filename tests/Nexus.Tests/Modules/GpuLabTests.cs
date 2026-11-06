using Nexus.Core;
using Nexus.Lab.Gpu;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

[Trait("Category", "Unit")]
public sealed class GpuLabTests
{
    // ------------------------------------------------------------------ apoio

    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private static CatalogDocument Catalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private static ActionInstance FromCatalog(string key, Dictionary<string, string> args)
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == key);
        var validated = entry.ValidateUserArguments(args, out var reason);
        Assert.True(validated is not null, reason);
        var inst = entry.TryInstantiate(new Dictionary<string, string>(validated!), out reason);
        Assert.True(inst is not null, reason);
        return inst!;
    }

    /// <summary>Monitor falso: lista de modos e modo atual; registra cada troca (teste e real).</summary>
    private sealed class FakeDisplay : IDisplayModeBackend
    {
        public Dictionary<string, DisplayMode> CurrentModes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<DisplayMode>> Listed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Device, DisplayMode Mode, bool Test)> Calls { get; } = [];
        public DisplayChangeResult TestResult { get; set; } = DisplayChangeResult.Successful;

        public DisplayMode? Current(string device) => CurrentModes.TryGetValue(device, out var m) ? m : null;
        public IReadOnlyList<DisplayMode> Modes(string device) => Listed.TryGetValue(device, out var l) ? l : [];

        public DisplayChangeResult Change(string device, DisplayMode mode, bool test)
        {
            Calls.Add((device, mode, test));
            if (test) return TestResult;
            CurrentModes[device] = mode with { BitsPerPixel = CurrentModes[device].BitsPerPixel };
            return DisplayChangeResult.Successful;
        }
    }

    private static FakeDisplay Monitor144At60()
    {
        var d = new FakeDisplay();
        d.CurrentModes[@"\\.\DISPLAY1"] = new DisplayMode(1920, 1080, 60);
        d.Listed[@"\\.\DISPLAY1"] = [new(1920, 1080, 60), new(1920, 1080, 120), new(1920, 1080, 144), new(1280, 720, 60), new(1920, 1080, 50)];
        return d;
    }

    // ------------------------------------------------------------------ identidade

    [Theory]
    [InlineData(0x10DE, 0x25A2, "Ampere")]       // RTX 3050 Laptop (GA107)
    [InlineData(0x10DE, 0x2504, "Ampere")]       // RTX 3060 (GA106)
    [InlineData(0x10DE, 0x2684, "Ada Lovelace")] // RTX 4090
    [InlineData(0x10DE, 0x28E0, "Ada Lovelace")] // RTX 4060 Laptop (AD107)
    [InlineData(0x10DE, 0x2B85, "Blackwell")]    // RTX 5090
    [InlineData(0x10DE, 0x2C02, "Blackwell")]    // RTX 5080
    [InlineData(0x10DE, 0x1F08, "Turing")]       // RTX 2060
    [InlineData(0x10DE, 0x2184, "Turing")]       // GTX 1660 (TU116)
    [InlineData(0x10DE, 0x1C82, "Pascal")]       // GTX 1050 Ti
    [InlineData(0x10DE, 0x1B80, "Pascal")]       // GTX 1080
    [InlineData(0x10DE, 0x13C2, "Maxwell")]      // GTX 970
    [InlineData(0x1002, 0x73BF, "RDNA 2")]       // RX 6900 XT
    [InlineData(0x1002, 0x73DF, "RDNA 2")]       // RX 6700 XT
    [InlineData(0x1002, 0x744C, "RDNA 3")]       // RX 7900 XTX
    [InlineData(0x1002, 0x7550, "RDNA 4")]       // RX 9070 XT
    [InlineData(0x1002, 0x731F, "RDNA 1")]       // RX 5700 XT
    [InlineData(0x1002, 0x67DF, "GCN 4 (Polaris)")] // RX 580
    [InlineData(0x1002, 0x1638, "GCN 5 (Vega integrada)")] // Radeon do Ryzen 5 5600H
    [InlineData(0x1002, 0x15BF, "RDNA 3/3.5 (integrada)")] // 780M
    [InlineData(0x8086, 0x56A0, "Xe-HPG (Alchemist)")]     // Arc A770
    [InlineData(0x8086, 0xE20B, "Xe2-HPG (Battlemage)")]   // Arc B580
    [InlineData(0x8086, 0x9A49, "Xe-LP")]        // Iris Xe (Tiger Lake)
    [InlineData(0x8086, 0x7D55, "Xe-LPG")]       // Core Ultra (Meteor Lake)
    public void Architecture_table_maps_known_device_ids(uint vendor, uint device, string arch) =>
        Assert.Equal(arch, GpuIdentity.Architecture(vendor, device).Name);

    [Fact]
    public void Unknown_ids_are_not_guessed()
    {
        Assert.Same(GpuArchitecture.Unknown, GpuIdentity.Architecture(0x1002, 0x0001));
        Assert.Same(GpuArchitecture.Unknown, GpuIdentity.Architecture(0x1414, 0x008C)); // Microsoft Basic Render
        Assert.Same(GpuArchitecture.Unknown, GpuIdentity.Architecture(0x10DE, 0x3FFF));
        Assert.Equal(GpuVendor.Microsoft, GpuIdentity.VendorOf(0x1414));
    }

    [Fact]
    public void Integrated_detection_uses_table_then_dxgi_memory()
    {
        Assert.True(GpuIdentity.IsIntegrated(0x1002, 0x1638, 512UL << 20));
        Assert.False(GpuIdentity.IsIntegrated(0x10DE, 0x25A2, 4UL << 30));
        Assert.False(GpuIdentity.IsIntegrated(0x1002, 0x73DF, 12UL << 30));
        Assert.True(GpuIdentity.IsIntegrated(0x8086, 0x9A49, 128UL << 20));
        Assert.False(GpuIdentity.IsIntegrated(0x8086, 0x56A0, 16UL << 30));
    }

    [Fact]
    public void Hardware_id_is_parsed()
    {
        var ids = GpuIdentity.ParseHardwareId(@"PCI\VEN_10DE&DEV_25A2&SUBSYS_14E51025&REV_A1");
        Assert.Equal((0x10DEu, 0x25A2u, (uint?)0x14E51025u), ids);
        Assert.Equal((0x1002u, 0x1638u, (uint?)null), GpuIdentity.ParseHardwareId(@"pci\ven_1002&dev_1638"));
        Assert.Null(GpuIdentity.ParseHardwareId(@"ROOT\BasicDisplay"));
        Assert.Null(GpuIdentity.ParseHardwareId(null));
        Assert.Equal("luid_0x00000000_0x0000d5f2_phys_0", GpuIdentity.PdhAdapterKey(0xD5F2));
    }

    // ------------------------------------------------------------------ driver

    [Theory]
    [InlineData("32.0.15.6636", "566.36")]
    [InlineData("31.0.15.3623", "536.23")]
    [InlineData("32.0.15.7283", "572.83")]
    [InlineData("27.21.14.5671", "456.71")]
    [InlineData("30.0.14.7141", "471.41")]
    public void Nvidia_driver_version_is_decoded(string windows, string vendor) =>
        Assert.Equal(vendor, DriverInfo.Decode(GpuVendor.Nvidia, windows).VendorVersion);

    [Fact]
    public void Other_vendors_are_decoded_honestly()
    {
        Assert.Equal("101.5590", DriverInfo.Decode(GpuVendor.Intel, "31.0.101.5590").VendorVersion);
        var amd = DriverInfo.Decode(GpuVendor.Amd, "31.0.24027.1012");
        Assert.Null(amd.VendorVersion);
        Assert.Contains("Adrenalin", amd.Explanation, StringComparison.Ordinal);
        Assert.Null(DriverInfo.Decode(GpuVendor.Nvidia, "abc").VendorVersion);
        Assert.Null(DriverInfo.Decode(GpuVendor.Nvidia, "").VendorVersion);
    }

    [Fact]
    public void Driver_date_and_age()
    {
        Assert.Equal(new DateOnly(2024, 12, 4), DriverInfo.ParseDate("12-4-2024"));
        Assert.Equal(new DateOnly(2024, 12, 4), DriverInfo.ParseDate("2024-12-04"));
        Assert.Null(DriverInfo.ParseDate("ontem"));
        var today = new DateOnly(2026, 10, 31);
        Assert.Equal(696, DriverInfo.AgeDays(new DateOnly(2024, 12, 4), today));
        Assert.Equal(0, DriverInfo.AgeDays(today.AddDays(3), today));
        Assert.Equal("warn", DriverInfo.AgeVerdict(696, integrated: false).Level);
        Assert.Equal("info", DriverInfo.AgeVerdict(696, integrated: true).Level);
        Assert.Equal("bad", DriverInfo.AgeVerdict(800, integrated: false).Level);
        Assert.Equal("good", DriverInfo.AgeVerdict(30, integrated: false).Level);
        Assert.StartsWith("https://www.nvidia.com/", DriverInfo.OfficialDownloadPage(GpuVendor.Nvidia), StringComparison.Ordinal);
        Assert.Null(DriverInfo.OfficialDownloadPage(GpuVendor.Unknown));
    }

    // ------------------------------------------------------------------ PCIe e motivos de throttle

    [Fact]
    public void Pcie_codes_and_verdicts()
    {
        Assert.Equal(4, Pcie.GenFromSpeedCode(4));
        Assert.Null(Pcie.GenFromSpeedCode(0));
        Assert.Null(Pcie.GenFromSpeedCode(9));
        Assert.Equal(8, Pcie.Width(8));
        Assert.Null(Pcie.Width(3));
        Assert.Equal(15.76, Pcie.BandwidthGBs(3, 16), 2);

        // Desktop: x8 numa placa x16 em porta x16 → aviso.
        var narrow = new PcieLinkState(4, 4, 8, 16, 4, 16);
        Assert.Equal("warn", Pcie.IdleVerdict(narrow, false).Level);
        // Repouso em Gen1 com o resto certo: só informação (ASPM), não aviso.
        var idle = new PcieLinkState(1, 4, 16, 16, 4, 16);
        var v = Pcie.IdleVerdict(idle, false);
        Assert.Equal("good", v.Level);
        Assert.Contains("repouso", v.Text, StringComparison.Ordinal);
        // Notebook: placa Gen4 x8 numa porta Gen3 x8 (Ryzen 5600H) → teto da plataforma explicado.
        var laptop = new PcieLinkState(3, 4, 8, 8, 3, 8);
        Assert.Equal(3, laptop.AttainableGen);
        Assert.Contains("teto da plataforma", Pcie.IdleVerdict(laptop, false).Text, StringComparison.Ordinal);
        Assert.Equal("info", Pcie.IdleVerdict(new PcieLinkState(null, null, null, null), true).Level);
    }

    [Fact]
    public void Throttle_bitmask_is_decoded_in_portuguese()
    {
        var mask = (ulong)(ClockEventReasons.SwPowerCap | ClockEventReasons.SwThermalSlowdown);
        var texts = ThrottleReasons.Describe(mask);
        Assert.Equal(2, texts.Count);
        Assert.Contains(texts, t => t.Contains("potência", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("térmico", StringComparison.Ordinal));
        Assert.Equal(LimitCategory.Thermal, ThrottleReasons.Categorize(mask));
        Assert.Equal(LimitCategory.Power, ThrottleReasons.Categorize(0x4));
        Assert.Equal(LimitCategory.Power, ThrottleReasons.Categorize(0x80));
        Assert.Equal(LimitCategory.Hardware, ThrottleReasons.Categorize(0x8));
        Assert.Equal(LimitCategory.Idle, ThrottleReasons.Categorize(0x1));
        Assert.Equal(LimitCategory.None, ThrottleReasons.Categorize(0));
        Assert.Contains(ThrottleReasons.Describe(0x1000), t => t.Contains("0x1000", StringComparison.Ordinal));
        Assert.Empty(ThrottleReasons.Describe(0));
    }

    // ------------------------------------------------------------------ gravação

    private static List<GpuTelemetrySample> Synthetic(int seconds, Func<int, GpuTelemetrySample> f) => Enumerable.Range(0, seconds).Select(f).ToList();

    [Fact]
    public void Recording_detects_power_limit_and_bands()
    {
        // 60 s de jogo: 40 s limitada por potência, 20 s livre; PCIe Gen3 x8 (máximo da plataforma).
        var s = Synthetic(60, i => new GpuTelemetrySample(i, 97, 1700 + (i % 2) * 20, 6000, 60, 60, 74, i < 40 ? 0x4UL : 0, 3, 8, 3UL << 30, 4UL << 30));
        var a = RecordingAnalyzer.Analyze(s, 1950, new PcieLinkState(3, 4, 8, 8, 3, 8), isLaptop: true);
        Assert.Equal(60, a.LoadSamples);
        Assert.Contains(a.Findings, f => f.Title == "Limitada por potência" && f.Advice!.Contains("NitroSense", StringComparison.Ordinal));
        Assert.Contains("potência", a.Verdict, StringComparison.Ordinal);
        Assert.DoesNotContain(a.Findings, f => f.Title.StartsWith("PCIe", StringComparison.Ordinal));
        var band = Assert.Single(a.Bands);
        Assert.Equal(LimitCategory.Power, band.Category);
        Assert.Equal(0, band.FromSeconds);
        Assert.Equal(40, band.ToSeconds);
        Assert.Equal(40.0 / 60, a.LimitShare[LimitCategory.Power], 3);
    }

    [Fact]
    public void Recording_detects_thermal_pcie_drop_vram_and_oscillation()
    {
        var s = Synthetic(60, i => new GpuTelemetrySample(i, 99, i % 2 == 0 ? 1900 : 1300, 7000, 150, 170, 88, i % 3 == 0 ? 0x20UL : 0,
            i < 50 ? 1 : 4, 16, (ulong)(7.9 * (1UL << 30)), 8UL << 30));
        var a = RecordingAnalyzer.Analyze(s, 2100, new PcieLinkState(4, 4, 16, 16, 4, 16), isLaptop: false);
        Assert.Contains(a.Findings, f => f.Title == "Limitada por temperatura");
        Assert.Contains(a.Findings, f => f.Title == "PCIe abaixo do possível sob carga");
        Assert.Contains(a.Findings, f => f.Title == "Memória de vídeo cheia");
        Assert.Contains(a.Findings, f => f.Title == "Clock oscilando");
        Assert.Equal(88, a.MaxTemperature);
    }

    [Fact]
    public void Recording_without_load_asks_to_record_again()
    {
        var s = Synthetic(60, i => new GpuTelemetrySample(i, 8, 300, 400, 8, 60, 45, 0x1, 1, 8, 1UL << 30, 4UL << 30));
        var a = RecordingAnalyzer.Analyze(s, 1950, null, isLaptop: true);
        Assert.Equal("info", a.VerdictLevel);
        Assert.Contains(a.Findings, f => f.Title == "A GPU quase não trabalhou");
        Assert.Empty(a.Bands); // ociosa não vira faixa
        Assert.Equal("Nada gravado.", RecordingAnalyzer.Analyze([], null, null, false).Verdict);
    }

    [Fact]
    public void Recording_clean_run_is_good_and_without_mask_is_info()
    {
        var clean = Synthetic(30, i => new GpuTelemetrySample(i, 98, 1900, 7000, 100, 170, 70, 0, 4, 16, 4UL << 30, 8UL << 30));
        Assert.Equal("good", RecordingAnalyzer.Analyze(clean, 1950, new PcieLinkState(4, 4, 16, 16), false).VerdictLevel);
        var amd = Synthetic(30, i => new GpuTelemetrySample(i, 98, null, null, null, null, null, null, null, null, 4UL << 30, 8UL << 30));
        Assert.Equal("info", RecordingAnalyzer.Analyze(amd, null, null, false).VerdictLevel);
    }

    // ------------------------------------------------------------------ monitores

    [Fact]
    public void Best_mode_only_changes_refresh_at_current_resolution()
    {
        var current = new DisplayMode(1920, 1080, 60);
        var listed = new[]
        {
            new DisplayMode(1920, 1080, 60), new DisplayMode(1920, 1080, 144), new DisplayMode(1920, 1080, 165, Interlaced: true),
            new DisplayMode(1920, 1080, 240, BitsPerPixel: 16), new DisplayMode(2560, 1440, 60), new DisplayMode(1280, 720, 240),
        };
        var advice = DisplayModes.Advise(current, listed);
        Assert.True(advice.BelowMaxRefresh);
        Assert.Equal(new DisplayMode(1920, 1080, 144), advice.Best);
        Assert.True(advice.BelowNativeResolution); // 2560×1440 listado (pode ser DSR): só informação
        Assert.Equal("1920x1080@144", advice.Best!.Key);

        var ok = DisplayModes.Advise(new DisplayMode(1920, 1080, 144), listed);
        Assert.False(ok.BelowMaxRefresh);
    }

    [Theory]
    [InlineData("2560x1440@144", true)]
    [InlineData("1920x1080@60", true)]
    [InlineData("1920x1080@60\n", false)]
    [InlineData("320x200@60", false)]
    [InlineData("1920x1080@999", false)]
    [InlineData("1920X1080@60", false)]
    [InlineData("1920x1080", false)]
    public void Display_mode_key_is_strict(string key, bool valid) => Assert.Equal(valid, DisplayMode.TryParse(key) is not null);

    [Theory]
    [InlineData(@"\\.\DISPLAY1", true)]
    [InlineData(@"\\.\DISPLAY12", true)]
    [InlineData(@"\\.\DISPLAY123", false)]
    [InlineData(@"\\?\DISPLAY1", false)]
    [InlineData(@"DISPLAY1", false)]
    public void Display_device_is_strict(string device, bool valid) => Assert.Equal(valid, DisplayModes.IsValidDevice(device));

    // ------------------------------------------------------------------ handler de modo de vídeo

    [Fact]
    public void Display_mode_handler_detect_apply_restore()
    {
        var display = Monitor144At60();
        var h = new DisplayModeHandler(display, otherAccount: false);
        var a = FromCatalog(GpuDiagnostics.DisplayModeAction, new() { ["device"] = @"\\.\DISPLAY1", ["mode"] = "1920x1080@144" });

        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var before = h.Detect(a);
        Assert.Equal("1920x1080@60", before.Value);
        Assert.False(before.SameValueAs(a.Desired));

        h.Apply(a);
        Assert.Equal([true, false], display.Calls.Select(c => c.Test)); // CDS_TEST antes
        Assert.True(h.Detect(a).SameValueAs(a.Desired));
        Assert.Equal(144, display.CurrentModes[@"\\.\DISPLAY1"].Hz);

        Assert.Null(h.CheckRestore(a, before));
        h.Restore(a, before);
        Assert.Equal(new DisplayMode(1920, 1080, 60), display.CurrentModes[@"\\.\DISPLAY1"]);
        Assert.True(h.Detect(a).SameValueAs(before));
    }

    [Fact]
    public void Display_mode_handler_refuses_unlisted_mode_other_account_and_failed_test()
    {
        var display = Monitor144At60();
        var h = new DisplayModeHandler(display, otherAccount: false);
        var bad = FromCatalog(GpuDiagnostics.DisplayModeAction, new() { ["device"] = @"\\.\DISPLAY1", ["mode"] = "1920x1080@240" });
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(bad, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(bad));
        Assert.Empty(display.Calls);

        var missing = FromCatalog(GpuDiagnostics.DisplayModeAction, new() { ["device"] = @"\\.\DISPLAY2", ["mode"] = "1920x1080@144" });
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(missing, T.Ctx).Kind);
        Assert.False(h.Detect(missing).Exists);
        h.Restore(missing, StateSnapshot.Absent()); // monitor ausente antes: nada a fazer
        Assert.Empty(display.Calls);

        var good = FromCatalog(GpuDiagnostics.DisplayModeAction, new() { ["device"] = @"\\.\DISPLAY1", ["mode"] = "1920x1080@144" });
        Assert.Equal(ApplicabilityKind.Blocked, new DisplayModeHandler(display, otherAccount: true).Assess(good, T.Ctx).Kind);

        display.TestResult = DisplayChangeResult.BadMode;
        Assert.Throws<InvalidOperationException>(() => h.Apply(good));
        Assert.DoesNotContain(display.Calls, c => !c.Test); // o teste falhou: nada real foi pedido
        Assert.Equal(60, display.CurrentModes[@"\\.\DISPLAY1"].Hz);
    }

    [Fact]
    public void Display_mode_restore_refuses_garbage_or_unlisted_original()
    {
        var display = Monitor144At60();
        var h = new DisplayModeHandler(display, otherAccount: false);
        var a = FromCatalog(GpuDiagnostics.DisplayModeAction, new() { ["device"] = @"\\.\DISPLAY1", ["mode"] = "1920x1080@144" });
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of(DisplayModeHandler.ValueType, "1920x1080@60;rm")));
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of(DisplayModeHandler.ValueType, "3840x2160@60")));
        Assert.Null(h.CheckRestore(a, StateSnapshot.Of(DisplayModeHandler.ValueType, DisplayModeHandler.AppliedValue)));
        Assert.Throws<InvalidOperationException>(() => h.Restore(a, StateSnapshot.Of(DisplayModeHandler.ValueType, "3840x2160@60")));
        Assert.Empty(display.Calls);
    }

    [Fact]
    public void Display_mode_runs_through_executor_and_reverts()
    {
        var display = Monitor144At60();
        var registry = new HandlerRegistry().Add(new DisplayModeHandler(display, false));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(registry, journal);
        var resolver = new DesiredStateResolver(registry);
        var a = FromCatalog(GpuDiagnostics.DisplayModeAction, new() { ["device"] = @"\\.\DISPLAY1", ["mode"] = "1920x1080@144" });
        var plan = resolver.Resolve([a], T.Ctx);
        var run = Guid.NewGuid();
        var results = exec.Execute(run, plan, T.Ctx);
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));
        Assert.Equal(144, display.CurrentModes[@"\\.\DISPLAY1"].Hz);
        var reverted = exec.RevertRun(run);
        Assert.All(reverted, r => Assert.Equal(RevertOutcome.Reverted, r.Outcome));
        Assert.Equal(60, display.CurrentModes[@"\\.\DISPLAY1"].Hz);
    }

    // ------------------------------------------------------------------ preferência de GPU por app (handler do núcleo)

    [Fact]
    public void App_gpu_preference_uses_core_handler_and_preserves_tokens()
    {
        var reg = new InMemoryRegistryBackend();
        const string exe = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe";
        var t = new RegistryTarget(RegHive.HKCU, DirectXUserGpuPreferenceHandler.KeyPath, exe, RegView.Default, Sid);
        reg.Write(t, "String", "SwapEffectUpgradeEnable=1;");
        var h = new DirectXUserGpuPreferenceHandler(reg);
        var a = UserProfileScope.Bind(FromCatalog(GpuDiagnostics.AppPreferenceAction, new() { ["exePath"] = exe }), Sid);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var before = h.Detect(a);
        Assert.False(before.Exists);
        h.Apply(a);
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", reg.Read(t).Value);
        h.Restore(a, before);
        Assert.Equal("SwapEffectUpgradeEnable=1;", reg.Read(t).Value);
    }

    [Theory]
    [InlineData(@"C:\Games\Jogo\jogo.exe", true)]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Elden Ring\Game\eldenring.exe", true)]
    [InlineData(@"C:\Games\Jogo\jogo.bat", false)]
    [InlineData(@"jogo.exe", false)]
    [InlineData(@"\\servidor\share\jogo.exe", false)]
    [InlineData("C:\\Games\\jogo.exe\n", false)]
    public void Exe_path_rule_matches_catalog(string path, bool valid)
    {
        Assert.Equal(valid, GameLibraries.IsValidExePath(path));
        var entry = Catalog().Actions.Single(e => e.Definition.Key == GpuDiagnostics.AppPreferenceAction);
        Assert.Equal(valid, entry.ValidateUserArguments(new Dictionary<string, string> { ["exePath"] = path }, out _) is not null);
    }

    // ------------------------------------------------------------------ jogos

    [Fact]
    public void Steam_and_epic_manifests_are_parsed()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "label" "" }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameLibraries.SteamLibraryPaths(vdf));
        const string acf = """
            "AppState" { "appid" "1091500" "name" "Cyberpunk 2077" "installdir" "Cyberpunk 2077" }
            """;
        Assert.Equal(("1091500", "Cyberpunk 2077", "Cyberpunk 2077"), GameLibraries.ParseAppManifest(acf));
        Assert.Null(GameLibraries.ParseAppManifest("""{ "appid" "228980" "name" "Steamworks Common Redistributables" "installdir" "Steamworks Shared" }"""));
        Assert.Null(GameLibraries.ParseAppManifest("""{ "appid" "1" "name" "X" "installdir" "..\\..\\Windows" }"""));

        var epic = GameLibraries.ParseEpicItem("""{ "DisplayName": "Fortnite", "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite", "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe" }""");
        Assert.NotNull(epic);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", epic!.ExePath);
        Assert.True(epic.ExePathSure);
        Assert.Null(GameLibraries.ParseEpicItem("não é json"));
    }

    [Fact]
    public void Game_exe_is_picked_by_depth_size_and_shipping()
    {
        const string dir = @"D:\SteamLibrary\steamapps\common\Jogo";
        var exe = GameLibraries.PickGameExe(dir, [
            (dir + @"\UnityCrashHandler64.exe", 2_000_000), (dir + @"\Jogo.exe", 600_000), (dir + @"\redist\vcredist_x64.exe", 15_000_000),
            (dir + @"\Tools\Editor.exe", 90_000_000),
        ]);
        Assert.Equal(dir + @"\Jogo.exe", exe);
        var unreal = GameLibraries.PickGameExe(dir, [(dir + @"\Jogo.exe", 300_000), (dir + @"\Jogo\Binaries\Win64\Jogo-Win64-Shipping.exe", 80_000_000)]);
        Assert.EndsWith("-Shipping.exe", unreal, StringComparison.Ordinal);
        Assert.Null(GameLibraries.PickGameExe(dir, [(dir + @"\unins000.exe", 1)]));
        Assert.Equal(2, GameLibraries.ParseGpuPreference("SwapEffectUpgradeEnable=1;GpuPreference=2;"));
        Assert.Null(GameLibraries.ParseGpuPreference("SwapEffectUpgradeEnable=1;"));
    }

    // ------------------------------------------------------------------ diagnóstico

    private static GpuLabReport AcerNitro(int panelHz = 60, int? hwSch = 1)
    {
        var igpu = new GpuAdapter { Name = "AMD Radeon(TM) Graphics", VendorId = 0x1002, DeviceId = 0x1638, Luid = 0xA1, DedicatedVideoBytes = 512UL << 20, IsMinimumPower = true,
            DriverVersion = "31.0.12027.9001", DriverDate = new DateOnly(2023, 3, 1) };
        var dgpu = new GpuAdapter { Name = "NVIDIA GeForce RTX 3050 Laptop GPU", VendorId = 0x10DE, DeviceId = 0x25A2, Luid = 0xB2, DedicatedVideoBytes = 4UL << 30,
            IsHighPerformance = true, DriverVersion = "32.0.15.6636", DriverDate = new DateOnly(2024, 12, 4), Pcie = new PcieLinkState(1, 4, 8, 8, 3, 8),
            Nvidia = new NvidiaReading { PowerLimitWatts = 60, DefaultPowerLimitWatts = 60, VramTotalBytes = 4UL << 30, Bar1TotalBytes = 256UL << 20 } };
        var panel = new MonitorReport
        {
            Device = @"\\.\DISPLAY1", FriendlyName = "", Internal = true, AdapterLuid = 0xA1, Current = new DisplayMode(1920, 1080, panelHz),
            Advice = DisplayModes.Advise(new DisplayMode(1920, 1080, panelHz), [new(1920, 1080, 60), new(1920, 1080, 144)]), ListedModes = 2,
        };
        return new GpuLabReport
        {
            Adapters = [igpu, dgpu, new GpuAdapter { Name = "Microsoft Basic Render Driver", VendorId = 0x1414, DeviceId = 0x8C, IsSoftware = true }],
            Monitors = [panel],
            Settings = new GraphicsSettings { HwSchMode = hwSch, HasBattery = true, OnBattery = false, AutoGameModeEnabled = 0 },
            Games = [new GameEntry("Cyberpunk 2077", "Steam", @"D:\S\Cyberpunk 2077", @"D:\S\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", false),
                     new GameEntry("Fortnite", "Epic", @"C:\E\Fortnite", @"C:\E\Fortnite\F.exe", true)],
            AppPreferences = [new AppGpuPreference(@"C:\E\Fortnite\F.exe", 2, "GpuPreference=2;")],
        };
    }

    [Fact]
    public void Diagnostics_for_the_owners_laptop()
    {
        var r = AcerNitro();
        Assert.Equal("NVIDIA GeForce RTX 3050 Laptop GPU", r.Primary!.Name);
        Assert.Equal("NVIDIA GeForce RTX 3050 Laptop GPU · Ampere · 4 GB · driver 566.36 de 2024-12", GpuDiagnostics.HeroLine(r.Primary));
        Assert.True(r.IsLaptop);
        var hybrid = r.Hybrid;
        Assert.True(hybrid.IsHybrid);
        Assert.Equal("AMD Radeon(TM) Graphics", hybrid.InternalPanelGpu);

        var f = GpuDiagnostics.Evaluate(r, new DateOnly(2026, 10, 31));
        var refresh = Assert.Single(f, x => x.Fix == GpuFixKind.DisplayMode);
        Assert.Equal("1920x1080@144", refresh.FixArguments!["mode"]);
        Assert.Equal(@"\\.\DISPLAY1", refresh.FixArguments["device"]);
        // Só o jogo sem preferência ganha "Usar a dedicada"; Fortnite já está em alto desempenho.
        var pref = Assert.Single(f, x => x.Fix == GpuFixKind.AppGpuPreference);
        Assert.Contains("Cyberpunk", pref.Title, StringComparison.Ordinal);
        Assert.Contains(f, x => x.Id == "hybrid.panel");
        Assert.Contains(f, x => x.Fix == GpuFixKind.GameMode);
        Assert.Contains(f, x => x.Id.StartsWith("driver.age.10de", StringComparison.Ordinal) && x.Fix == GpuFixKind.OpenLink);
        Assert.Contains(f, x => x.Id == "rebar.off");
        Assert.Contains(f, x => x.Id == "hags.state"); // Ampere: HAGS é só informação, sem "Corrigir"
        Assert.DoesNotContain(f, x => x.Fix == GpuFixKind.Hags);
        Assert.DoesNotContain(f, x => x.Id.StartsWith("pcie.", StringComparison.Ordinal)); // repouso em Gen1 x8/x8 não é defeito

        Assert.DoesNotContain(GpuDiagnostics.Evaluate(AcerNitro(panelHz: 144), new DateOnly(2026, 10, 31)), x => x.Fix == GpuFixKind.DisplayMode);
    }

    [Fact]
    public void Diagnostics_hags_fix_only_for_frame_generation_gpus()
    {
        var gpu = new GpuAdapter { Name = "NVIDIA GeForce RTX 4070", VendorId = 0x10DE, DeviceId = 0x2786, Luid = 1, DedicatedVideoBytes = 12UL << 30, IsHighPerformance = true, HagsEnabled = false };
        var r = new GpuLabReport { Adapters = [gpu] };
        var f = GpuDiagnostics.Evaluate(r, new DateOnly(2026, 10, 31));
        Assert.Contains(f, x => x.Fix == GpuFixKind.Hags);
        Assert.False(r.Hybrid.IsHybrid);
        Assert.Contains(GpuDiagnostics.Evaluate(new GpuLabReport(), new DateOnly(2026, 10, 31)), x => x.Id == "gpu.none");
    }

    // ------------------------------------------------------------------ catálogo

    [Fact]
    public void Catalog_loads_and_every_handler_kind_is_registered()
    {
        var all = Catalog();
        var mine = all.Actions.Where(a => a.Definition.Id.StartsWith("gpulab.", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Equal(all.Actions.Count, all.Actions.Select(a => a.Definition.Key).Distinct(StringComparer.Ordinal).Count());
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, false);
        var handlers = GpuLabHandlers.Create(env).ToList();
        // NvidiaDrsSetting (catalog/nvidia.v1.json) e GpuVendorSetting (AMD/Intel, catalog/gpuvendors.v1.json) também vêm deste
        // módulo; os testes deles ficam em NvidiaDrsTests e GpuVendorTests.
        Assert.All(handlers.Where(h => h.HandlerKind is not (NvidiaDrsHandler.Kind or Nexus.Lab.Gpu.Vendors.GpuVendorSettingHandler.Kind)),
            h => Assert.StartsWith("GpuLab", h.HandlerKind, StringComparison.Ordinal));
        var kinds = handlers.Select(h => h.HandlerKind).ToHashSet(StringComparer.Ordinal);
        kinds.Add(DirectXUserGpuPreferenceHandler.Kind); // reaproveitado (registrado pelo broker)
        foreach (var a in mine)
        {
            Assert.Contains(a.Definition.HandlerKind, kinds);
            Assert.NotEmpty(a.Definition.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(a.Definition.Mechanism));
            Assert.False(string.IsNullOrWhiteSpace(a.Definition.Risks));
            Assert.False(string.IsNullOrWhiteSpace(a.Revert));
            Assert.Equal(PrivilegeLevel.User, a.Definition.Privileges);
        }
        // Ações do catálogo principal usadas pelos achados existem.
        Assert.Contains(all.Actions, a => a.Definition.Key == GpuDiagnostics.GameModeAction);
        Assert.Contains(all.Actions, a => a.Definition.Key == GpuDiagnostics.HagsAction && a.Definition.AllowedValues!.Contains("2"));
        Assert.Contains(all.Actions, a => a.Definition.Key == GpuDiagnostics.DisplayModeAction);
        Assert.Contains(all.Actions, a => a.Definition.Key == GpuDiagnostics.AppPreferenceAction);
    }

    [Fact]
    public void Display_mode_catalog_rules_reject_injection()
    {
        var entry = Catalog().Actions.Single(e => e.Definition.Key == GpuDiagnostics.DisplayModeAction);
        Assert.NotNull(entry.ValidateUserArguments(new Dictionary<string, string> { ["device"] = @"\\.\DISPLAY3", ["mode"] = "2560x1440@165" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["device"] = @"\\.\DISPLAY3\..", ["mode"] = "2560x1440@165" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["device"] = @"\\.\DISPLAY3", ["mode"] = "2560x1440@165\n" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["device"] = @"\\.\DISPLAY3" }, out _));
    }
}
