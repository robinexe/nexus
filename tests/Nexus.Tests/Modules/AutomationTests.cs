using System.Xml;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.HardwareDiscovery;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

internal static class AutoFakes
{
    public const ulong Gb = 1024UL * 1024 * 1024;

    public static SystemFacts Facts(string? gpu = null, string? gpuVendor = null, int ramGb = 16, bool vm = false, string cpuVendor = "Intel",
        int logical = 12, int build = 26100, ulong freeGb = 300, ulong totalGb = 1000) => new()
    {
        WindowsBuild = build,
        CpuVendor = cpuVendor,
        CpuModel = "CPU de teste",
        LogicalProcessors = logical,
        Gpus = gpu is null ? [] : [new GpuInfo(gpu, gpuVendor, "1.0", null, null, null)],
        MemoryModules = ramGb % 2 == 0
            ? [new MemoryModule("A1", (ulong)ramGb / 2 * Gb, "DDR4", 3200, 3200, null, null), new MemoryModule("B1", (ulong)ramGb / 2 * Gb, "DDR4", 3200, 3200, null, null)]
            : [new MemoryModule("A1", (ulong)ramGb * Gb, "DDR4", 3200, 3200, null, null)],
        IsVirtualMachine = vm,
        IsElevated = false,
        SystemDrive = "C:",
        Drives = [new DriveFacts("C:", "NTFS", totalGb * Gb, freeGb * Gb)],
    };

    public const string Catalog = """
    {
      "catalogVersion": "t", "schema": "nexus.catalog/1",
      "actions": [
        { "id": "win.gamemode.enable", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "User",
          "target": { "hive": "HKCU", "path": "Software\\Microsoft\\GameBar", "name": "AutoGameModeEnabled", "type": "DWord" }, "desiredValue": 1 },
        { "id": "win.gamedvr.background-capture.disable", "classification": "Conditional", "handlerKind": "RegistryValue", "privileges": "User",
          "target": { "hive": "HKCU", "path": "Software\\X", "name": "AppCaptureEnabled", "type": "DWord" }, "desiredValue": 0 },
        { "id": "power.active-scheme.high-performance.session", "classification": "Experimental", "handlerKind": "PowerActiveScheme", "privileges": "User",
          "persistence": "Session", "target": { "schemeGuid": "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" } },
        { "id": "svc.sysmain.manual.lab", "classification": "Experimental", "handlerKind": "ServiceStartType", "privileges": "Admin",
          "target": { "service": "SysMain" }, "desiredValue": "Manual" },
        { "id": "cleaning.temp.user", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User",
          "target": { "rule": "temp-user" }, "desiredType": "Cleaned", "desiredValue": "clean" },
        { "id": "cleaning.wu.cache", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "Admin",
          "target": { "rule": "wu" }, "desiredType": "Cleaned", "desiredValue": "clean" },
        { "id": "cleaning.risky", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User", "risk": "Medium",
          "target": { "rule": "risky" }, "desiredType": "Cleaned", "desiredValue": "clean" },
        { "id": "cleaning.app.cache", "classification": "SupportedDocumented", "handlerKind": "CleaningRule", "privileges": "User",
          "target": { "rule": "{app}" }, "userParameters": { "app": "^[a-z]+$" }, "desiredType": "Cleaned", "desiredValue": "clean" },
        { "id": "boot.entry.disable", "classification": "Conditional", "handlerKind": "BootStartupEntry", "privileges": "User",
          "target": { "entry": "{name}" }, "userParameters": { "name": "^[A-Za-z]+$" }, "desiredType": "State", "desiredValue": "disabled" }
      ]
    }
    """;

    public static CatalogDocument Doc(string json = Catalog) => CatalogLoader.Parse(json);

    public static RecommendationSet Recommend(SystemFacts facts, PcTraits traits, CatalogDocument? doc = null, IReadOnlyList<DiagnosticFinding>? findings = null)
    {
        var d = doc ?? Doc();
        var profile = PcProfiler.Classify(facts, traits, findings ?? DiagnosticRules.Evaluate(facts));
        var risks = RiskModel.ParseDeclared(Catalog).ToDictionary(x => x.Key, x => x.Risk);
        return RecommendationEngine.Default().Evaluate(profile, d, risks);
    }

    public static DiagnosticFinding Attention(string id) => new(id, id, DiagnosticSeverity.Attention, FactStatus.Detected, "teste");
}

[Trait("Category", "Unit")]
public sealed class AutomationProfileTests
{
    [Fact]
    public void Gaming_laptop_on_battery()
    {
        var p = PcProfiler.Classify(AutoFakes.Facts("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA"),
            new PcTraits(ChassisType: 10, HasSystemBattery: true, OnAcPower: false, BatteryPercent: 64, SystemDriveIsSsd: true), []);
        Assert.Equal(FormFactor.Laptop, p.Form);
        Assert.Equal("Notebook gamer na bateria · RTX 4060 · 16 GB · SSD", p.Headline);
        Assert.True(p.Has("laptop") && p.Has("on-battery") && p.Has("gamer") && p.Has("ssd") && p.Has("ram-16plus"));
        Assert.False(p.Has("low-end"));
    }

    [Fact]
    public void Gaming_desktop()
    {
        var p = PcProfiler.Classify(AutoFakes.Facts("AMD Radeon RX 7800 XT", "AMD", ramGb: 32, cpuVendor: "AMD"),
            new PcTraits(ChassisType: 3, HasSystemBattery: false, OnAcPower: true, SystemDriveIsSsd: true), []);
        Assert.Equal("Desktop gamer · RX 7800 XT · 32 GB · SSD", p.Headline);
        Assert.True(p.Has("desktop") && p.Has("gamer") && p.Has("cpu-amd") && p.Has("on-ac"));
    }

    [Fact]
    public void Weak_pc_with_hdd_and_little_ram()
    {
        var p = PcProfiler.Classify(AutoFakes.Facts("Intel(R) UHD Graphics 620", "Intel", ramGb: 4, logical: 4, freeGb: 20, totalGb: 500),
            new PcTraits(ChassisType: 3, SystemDriveIsSsd: false), []);
        Assert.True(p.Has("low-end") && p.Has("low-ram") && p.Has("hdd") && p.Has("few-cores") && p.Has("gpu-integrated-only") && p.Has("low-disk"));
        Assert.False(p.Has("gamer"));
        Assert.Equal("Desktop modesto · UHD Graphics 620 · 4 GB · HDD", p.Headline);
    }

    [Fact]
    public void Virtual_machine_is_never_gamer()
    {
        var p = PcProfiler.Classify(AutoFakes.Facts("NVIDIA GeForce RTX 3080", "NVIDIA", vm: true), new PcTraits(ChassisType: 1), []);
        Assert.Equal(FormFactor.VirtualMachine, p.Form);
        Assert.True(p.Has("vm"));
        Assert.False(p.Has("gamer"));
        Assert.StartsWith("Máquina virtual", p.Kind, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_read_means_nothing_invented()
    {
        var p = PcProfiler.Classify(new SystemFacts(), PcTraits.Unknown, []);
        Assert.Equal(FormFactor.Unknown, p.Form);
        Assert.Equal("PC", p.Headline);
        Assert.True(p.Has("form-unknown") && p.Has("disk-unknown"));
        Assert.DoesNotContain(p.Tags, t => t is "laptop" or "desktop" or "ssd" or "hdd" or "low-ram" or "gamer" or "on-battery");
        Assert.Contains(p.Notes, n => n.Contains("não determinado", StringComparison.Ordinal));
    }

    [Fact]
    public void Battery_without_chassis_is_inferred_laptop()
    {
        var p = PcProfiler.Classify(AutoFakes.Facts(), new PcTraits(ChassisType: 2, HasSystemBattery: true, OnAcPower: true), []);
        Assert.Equal(FormFactor.Laptop, p.Form);
        Assert.Contains(p.Notes, n => n.Contains("inferido", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnostics_become_tags()
    {
        var p = PcProfiler.Classify(AutoFakes.Facts(), PcTraits.Unknown, [AutoFakes.Attention("win.gamemode")]);
        Assert.True(p.Has("diag:win.gamemode:attention"));
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1650", "NVIDIA", true)]
    [InlineData("AMD Radeon(TM) Graphics", "AMD", false)]
    [InlineData("AMD Radeon Vega 8 Graphics", "AMD", false)]
    [InlineData("AMD Radeon RX 6600", "AMD", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Intel", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Intel", false)]
    [InlineData("Microsoft Basic Display Adapter", "Microsoft", false)]
    public void Discrete_gpu_by_name(string name, string vendor, bool discrete) =>
        Assert.Equal(discrete, PcProfiler.IsDiscrete(new GpuInfo(name, vendor, null, null, null, null)));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", "RTX 4060")]
    [InlineData("AMD Radeon RX 7800 XT", "RX 7800 XT")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Arc A770 Graphics")]
    public void Short_gpu_names(string name, string expected) => Assert.Equal(expected, PcProfiler.ShortGpuName(name));

    [Fact]
    public void Chassis_type_from_smbios_enclosure()
    {
        byte[] raw =
        [
            0, 3, 4, 0, 19, 0, 0, 0,                 // cabeçalho RSMB: versão 3.4, 19 bytes de tabela
            3, 9, 1, 0, 1, 0x8A, 0, 0, 0, 65, 0, 0,  // tipo 3: gabinete 0x8A (trava + 10 = notebook), string "A"
            127, 4, 2, 0, 0, 0,                      // fim
        ];
        Assert.Equal(10, PcTraitsProbe.ChassisTypeFrom(SmbiosParser.Parse(raw)));
        Assert.True(PcTraits.IsPortableChassis(10));
        Assert.False(PcTraits.IsPortableChassis(3));
        Assert.Null(PcTraits.IsPortableChassis(2));
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationRecommendationTests
{
    [Fact]
    public void Default_rules_parse_and_reference_real_catalog_ids()
    {
        var engine = RecommendationEngine.Default();
        Assert.NotEmpty(engine.Rules);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var ids = catalog.Actions.Select(a => a.Definition.Id).ToHashSet(StringComparer.Ordinal);
        // Regras com id exato dos módulos que já existem no catálogo principal não podem ter erro de digitação.
        foreach (var r in engine.Rules.Where(r => r.Action is { } a && (a.StartsWith("win.", StringComparison.Ordinal) || a.StartsWith("power.", StringComparison.Ordinal)
                     || a.StartsWith("gpu.", StringComparison.Ordinal) || a.StartsWith("sched.", StringComparison.Ordinal) || a == "svc.sysmain.manual.lab")))
            Assert.True(ids.Contains(r.Action!), $"Regra {r.Id} aponta para ação inexistente: {r.Action}");
    }

    [Fact]
    public void Laptop_on_battery_avoids_high_performance_and_recommends_game_mode()
    {
        var set = AutoFakes.Recommend(AutoFakes.Facts("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA"),
            new PcTraits(ChassisType: 10, HasSystemBattery: true, OnAcPower: false, SystemDriveIsSsd: true),
            findings: [AutoFakes.Attention("win.gamemode"), AutoFakes.Attention("win.gamedvr.capture")]);
        var hp = Assert.Single(set.Items, i => i.ActionId == "power.active-scheme.high-performance.session");
        Assert.Equal(RecommendationKind.Avoid, hp.Kind);
        Assert.Contains("bateria", hp.Reason, StringComparison.Ordinal);
        Assert.False(hp.OneClick);

        var gm = Assert.Single(set.Items, i => i.ActionId == "win.gamemode.enable");
        Assert.Equal(RecommendationKind.Recommend, gm.Kind);
        Assert.True(gm.OneClick);
        Assert.Equal(ActionRisk.Low, gm.Risk);

        var dvr = Assert.Single(set.Items, i => i.ActionId == "win.gamedvr.background-capture.disable");
        Assert.Equal("gamedvr.capture.laptop", dvr.RuleId); // motivo personalizado para notebook
        Assert.False(dvr.OneClick); // condicional = risco médio: fora do um clique
        Assert.Contains(set.Info, i => i.RuleId == "info.on-battery");
    }

    [Fact]
    public void Desktop_gamer_gets_high_performance_only_as_measured_test()
    {
        var set = AutoFakes.Recommend(AutoFakes.Facts("NVIDIA GeForce RTX 4070", "NVIDIA"), new PcTraits(ChassisType: 3, OnAcPower: true, SystemDriveIsSsd: true));
        var hp = Assert.Single(set.Items, i => i.ActionId == "power.active-scheme.high-performance.session");
        Assert.Equal(RecommendationKind.Consider, hp.Kind);
        Assert.Equal(ActionRisk.High, hp.Risk);
        Assert.False(hp.OneClick);
        Assert.DoesNotContain(set.Items, i => i.ActionId == "win.gamemode.enable"); // Modo de Jogo já ligado: nada a recomendar
    }

    [Fact]
    public void Amd_desktop_does_not_get_high_performance_suggestion()
    {
        var set = AutoFakes.Recommend(AutoFakes.Facts("AMD Radeon RX 7800 XT", "AMD", cpuVendor: "AMD"), new PcTraits(ChassisType: 3, SystemDriveIsSsd: true));
        Assert.DoesNotContain(set.Items, i => i.ActionId == "power.active-scheme.high-performance.session");
    }

    [Fact]
    public void Hdd_pc_avoids_sysmain_and_gets_safe_cleaning_with_reason()
    {
        var set = AutoFakes.Recommend(AutoFakes.Facts("Intel(R) UHD Graphics", "Intel", ramGb: 4), new PcTraits(ChassisType: 3, SystemDriveIsSsd: false));
        var sysmain = Assert.Single(set.Items, i => i.ActionId == "svc.sysmain.manual.lab");
        Assert.Equal(RecommendationKind.Avoid, sysmain.Kind);
        Assert.Contains("HDD", sysmain.Reason, StringComparison.Ordinal);

        var temp = Assert.Single(set.Items, i => i.ActionId == "cleaning.temp.user");
        Assert.Equal("cleaning.hdd", temp.RuleId);
        Assert.True(temp.OneClick);
        Assert.Contains(set.Items, i => i.ActionId == "cleaning.wu.cache" && i.OneClick && i.Privileges == PrivilegeLevel.Admin);
        Assert.DoesNotContain(set.Items, i => i.ActionId == "cleaning.risky");     // risco declarado médio
        Assert.DoesNotContain(set.Items, i => i.ActionId == "cleaning.app.cache"); // pede escolha do usuário
        Assert.Contains(set.Info, i => i.RuleId == "info.hdd");
        Assert.Contains(set.Info, i => i.RuleId == "info.low-ram" && i.Reason.Contains("4 GB", StringComparison.Ordinal));
        var review = Assert.Single(set.Items, i => i.Kind == RecommendationKind.Review && i.ModulePrefix == "boot.");
        Assert.Equal("review.startup.slow-pc", review.RuleId);
    }

    [Fact]
    public void Avoid_wins_over_recommend_for_the_same_action()
    {
        // Notebook na bateria + desktop gamer não coexistem; usa a regra de VM para provar a precedência.
        var set = AutoFakes.Recommend(AutoFakes.Facts("NVIDIA GeForce RTX 4070", "NVIDIA", vm: true), new PcTraits(SystemDriveIsSsd: true));
        Assert.All(set.Items.Where(i => i.ActionId == "power.active-scheme.high-performance.session"), i => Assert.Equal(RecommendationKind.Avoid, i.Kind));
    }

    [Fact]
    public void Actions_missing_from_catalog_are_ignored()
    {
        var onlyGameMode = AutoFakes.Doc("""
        { "catalogVersion": "t", "schema": "nexus.catalog/1", "actions": [
          { "id": "win.gamemode.enable", "classification": "SupportedDocumented", "handlerKind": "RegistryValue", "privileges": "User",
            "target": { "hive": "HKCU", "path": "P", "name": "N", "type": "DWord" }, "desiredValue": 1 } ] }
        """);
        var set = AutoFakes.Recommend(AutoFakes.Facts(), new PcTraits(ChassisType: 10, HasSystemBattery: true, OnAcPower: false, SystemDriveIsSsd: false),
            onlyGameMode, [AutoFakes.Attention("win.gamemode")]);
        Assert.All(set.Items.Where(i => i.ActionKey is not null), i => Assert.Equal("win.gamemode.enable", i.ActionId));
        Assert.DoesNotContain(set.Items, i => i.Kind == RecommendationKind.Review); // sem boot./cleaning. no catálogo
    }

    [Theory]
    [InlineData("""{ "schema": "nexus.automation.rules/1", "rules": [ { "id": "x", "kind": "Recommend", "reason": "r" } ] }""")]
    [InlineData("""{ "schema": "nexus.automation.rules/1", "rules": [ { "id": "x", "kind": "Recommend", "action": "a", "actionPrefix": "b.", "reason": "r" } ] }""")]
    [InlineData("""{ "schema": "nexus.automation.rules/1", "rules": [ { "id": "x", "kind": "Info", "reason": "r" } ] }""")]
    [InlineData("""{ "schema": "nexus.automation.rules/1", "rules": [ { "id": "x", "kind": "Avoid", "action": "a", "reason": "r", "priority": 9 } ] }""")]
    [InlineData("""{ "schema": "outro", "rules": [] }""")]
    public void Malformed_rules_are_refused(string json) => Assert.Throws<InvalidDataException>(() => RecommendationEngine.ParseRules(json));

    [Fact]
    public void Declared_risk_never_makes_experimental_look_safe()
    {
        var doc = AutoFakes.Doc();
        var hp = doc.Actions.Single(a => a.Definition.Id == "power.active-scheme.high-performance.session");
        Assert.Equal(ActionRisk.High, RiskModel.Of(hp, ActionRisk.Low));
        var param = doc.Actions.Single(a => a.Definition.Id == "cleaning.app.cache");
        Assert.Equal(ActionRisk.Medium, RiskModel.Of(param));
    }

    [Fact]
    public void Batch_number_generated_by_nexus_is_not_a_user_choice()
    {
        // Achado no teste ponta a ponta (VM): a limpeza segura do Assistente vinha toda "risco médio" e desmarcada.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(dir!.FullName, "catalog"));
        var temp = catalog.Actions.Single(a => a.Definition.Key == "cleaning.temp-user@1");
        Assert.Equal(["batch"], temp.UserParameters.Keys.ToArray());
        Assert.Equal(ActionRisk.Low, RiskModel.Of(temp));
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationOneClickTests
{
    private static IReadOnlyList<(Recommendation, ActionRequest)> Candidates(out RecommendationSet set)
    {
        set = AutoFakes.Recommend(AutoFakes.Facts("Intel(R) UHD Graphics", "Intel", ramGb: 4), new PcTraits(ChassisType: 3, SystemDriveIsSsd: false),
            findings: [AutoFakes.Attention("win.gamemode")]);
        return OneClickPlanner.Candidates(set, AutoFakes.Doc());
    }

    [Fact]
    public void Candidates_are_only_low_risk_one_click_items()
    {
        var c = Candidates(out _);
        Assert.Equal(["cleaning.temp.user@1", "cleaning.wu.cache@1", "win.gamemode.enable@1"], c.Select(x => x.Item2.ActionId).Order(StringComparer.Ordinal));
        Assert.All(c, x => Assert.Null(x.Item2.Value)); // valor fixo no catálogo: o pedido não manda valor
    }

    [Fact]
    public void Plan_splits_by_level_and_skips_with_reason()
    {
        var c = Candidates(out _);
        var results = new List<ActionResultDto>
        {
            new("win.gamemode.enable@1", "Pending", null, "DWord:0", "DWord:1"),
            new("cleaning.temp.user@1", "AlreadyConfigured", null, "Cleaned:clean", "Cleaned:clean"),
            new("cleaning.wu.cache@1", "Blocked", OneClickPlanner.NeedsElevation),
        };
        var plan = OneClickPlanner.Build(c, results);
        Assert.Equal("win.gamemode.enable@1", Assert.Single(plan.User).Request.ActionId);
        Assert.Equal("cleaning.wu.cache@1", Assert.Single(plan.Admin).Request.ActionId);
        Assert.Contains(plan.Skipped, s => s.Recommendation.ActionId == "cleaning.temp.user" && s.Reason.Contains("Já está assim", StringComparison.Ordinal));
        Assert.Equal(2, plan.Count);
        Assert.Contains("administrador", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("ponto de restauração", plan.Confirmation(BrokerLevel.Admin), StringComparison.Ordinal);
        Assert.Contains("permissão", plan.Confirmation(BrokerLevel.Admin), StringComparison.Ordinal);
        Assert.DoesNotContain("ponto de restauração", plan.Confirmation(BrokerLevel.User), StringComparison.Ordinal);
        Assert.Single(plan.Requests(BrokerLevel.User));
    }

    [Fact]
    public void User_action_blocked_for_other_reasons_never_goes_to_admin()
    {
        var c = Candidates(out _);
        var plan = OneClickPlanner.Build(c,
        [
            new("win.gamemode.enable@1", "Blocked", OneClickPlanner.NeedsElevation),
            new("cleaning.wu.cache@1", "Blocked", "Valor gerenciado (PolicyEnforced)"),
            new("cleaning.temp.user@1", "NotApplicable", "Nada para limpar."),
        ]);
        Assert.True(plan.IsEmpty);
        Assert.Equal(3, plan.Skipped.Count);
    }

    [Fact]
    public async Task Simulation_falls_back_to_one_by_one_when_the_batch_is_refused()
    {
        var calls = new List<int>();
        Task<BrokerCallResult> Plan(IReadOnlyList<ActionRequest> q)
        {
            calls.Add(q.Count);
            if (q.Any(r => r.ActionId == "bad@1"))
                return Task.FromResult(new BrokerCallResult(true, BrokerResponse.Fail(Guid.NewGuid(), "unknown-action", "não existe"), "recusado"));
            return Task.FromResult(new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true, Results: q.Select(r => new ActionResultDto(r.ActionId, "Pending")).ToList()), "ok"));
        }
        var (results, problem) = await OneClickPlanner.SimulateAsync([new("a@1"), new("bad@1"), new("b@1")], Plan);
        Assert.Null(problem);
        Assert.Equal([3, 1, 1, 1], calls);
        Assert.Equal(["Pending", "Refused", "Pending"], results.Select(r => r.Outcome));
    }

    [Fact]
    public async Task Simulation_without_broker_reports_the_problem()
    {
        var (results, problem) = await OneClickPlanner.SimulateAsync([new("a@1")],
            _ => Task.FromResult(new BrokerCallResult(false, null, "O aplicador do NEXUS não respondeu.")));
        Assert.Empty(results);
        Assert.Contains("não respondeu", problem, StringComparison.Ordinal);
    }
}

/// <summary>Agendador falso: guarda o XML por nome.</summary>
internal sealed class FakeScheduler : ITaskSchedulerBackend
{
    public Dictionary<string, string> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Writes { get; private set; }
    public string? QueryXml(string taskName) => Tasks.GetValueOrDefault(taskName);
    public void CreateOrReplace(string taskName, string xml) { Writes++; ScheduledScanTask.Inspect(xml); Tasks[taskName] = xml; }
    public void Delete(string taskName) { Writes++; Tasks.Remove(taskName); }
}

[Trait("Category", "Unit")]
public sealed class AutomationScheduledScanTests
{
    private const string Exe = @"C:\Program Files\Jogos & Cia\O'Neil <beta>\nexus.exe";
    private const string SafeExe = @"C:\Program Files\Jogos & Cia\O'Neil\nexus.exe";
    private static readonly DateTime Now = new(2026, 10, 31, 15, 0, 0); // sábado

    [Fact]
    public void Xml_escapes_paths_with_spaces_ampersand_and_apostrophe()
    {
        var xml = ScheduledScanTask.BuildXml(SafeExe, ScanSchedule.TryParse("SUN 19:00")!, Now);
        Assert.Contains("Jogos &amp; Cia", xml, StringComparison.Ordinal);
        var info = ScheduledScanTask.Inspect(xml);
        Assert.True(info.Ours);
        Assert.Equal(SafeExe, info.Command);
        Assert.Equal(ScheduledScanTask.Arguments, info.Arguments);
        Assert.Equal("SUN 19:00", info.Schedule!.ToString());
        Assert.Contains("<StartBoundary>2026-11-01T19:00:00</StartBoundary>", xml, StringComparison.Ordinal);
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml, StringComparison.Ordinal);
        Assert.Contains(@"<WorkingDirectory>C:\Program Files\Jogos &amp; Cia\O'Neil</WorkingDirectory>", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Exe)]                                                   // < > poderiam fechar o elemento
    [InlineData(@"C:\Jogos\x""\nexus.exe")]                             // aspas
    [InlineData(@"C:\Jogos\nexus.exe"" & calc.exe & """)]               // tentativa de comando extra
    [InlineData(@"%LOCALAPPDATA%\NEXUS\nexus.exe")]                     // variável (o Agendador expandiria)
    [InlineData(@"C:\%TEMP%\nexus.exe")]
    [InlineData(@"\\servidor\share\nexus.exe")]                         // rede/UNC
    [InlineData(@"nexus.exe")]                                          // relativo
    [InlineData(@"C:\Jogos\..\Windows\nexus.exe")]                      // pasta relativa
    [InlineData(@"C:\Jogos\outro.exe")]                                 // outro programa
    [InlineData("C:\\Jogos\\a\tb\\nexus.exe")]                          // caractere de controle
    [InlineData(@"C:\Jogos\nexus.exe </Command><Arguments>x")]
    [InlineData("")]
    public void Unsafe_executable_paths_are_refused(string path)
    {
        Assert.NotNull(ScheduledScanTask.ValidateExecutable(path));
        Assert.Throws<ArgumentException>(() => ScheduledScanTask.BuildXml(path, ScanSchedule.TryParse("SUN 19:00")!, Now));
    }

    [Fact]
    public void Inspect_refuses_dtd_and_marks_foreign_tasks()
    {
        const string dtd = """<?xml version="1.0"?><!DOCTYPE t [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">&x;</Task>""";
        Assert.ThrowsAny<XmlException>(() => ScheduledScanTask.Inspect(dtd));
        var foreign = ScheduledScanTask.BuildXml(SafeExe, ScanSchedule.TryParse("SUN 19:00")!, Now).Replace(ScheduledScanTask.Marker, "outro", StringComparison.Ordinal);
        Assert.False(ScheduledScanTask.Inspect(foreign).Ours);
        Assert.Throws<XmlException>(() => ScheduledScanTask.Inspect("<Outro/>"));
    }

    [Theory]
    [InlineData("SUN 19:00", true)]
    [InlineData("WED 20:00", true)]
    [InlineData("sun 19:00", false)]
    [InlineData("SUN 24:00", false)]
    [InlineData("SUN 19:00; rm", false)]
    public void Schedule_format(string text, bool valid) => Assert.Equal(valid, ScanSchedule.TryParse(text) is not null);

    [Fact]
    public void Next_occurrence_is_in_the_future()
    {
        Assert.Equal(new DateTime(2026, 11, 1, 19, 0, 0), ScanSchedule.TryParse("SUN 19:00")!.NextAfter(Now));
        Assert.Equal(new DateTime(2026, 11, 7, 12, 0, 0), ScanSchedule.TryParse("SAT 12:00")!.NextAfter(Now)); // sábado 12h já passou
    }

    private static ActionInstance Action(string value) => new(
        new ActionDefinition(AutomationHandlers.ScheduledScanActionId, 1, "t", Classification.SupportedDocumented, AutomationScheduledScanHandler.Kind, "", [], "",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None, AllowedValues: ["SUN 19:00", "SAT 12:00", "WED 20:00", "OFF"]),
        new Dictionary<string, string> { ["taskName"] = ScheduledScanTask.TaskName },
        StateSnapshot.Of(AutomationScheduledScanHandler.StateType, value));

    private static (FakeScheduler Fake, ActionExecutor Exec, DesiredStateResolver Resolver, InMemoryJournal Journal) Engine(string? exe = SafeExe, bool otherAccount = false)
    {
        var fake = new FakeScheduler();
        var handlers = new HandlerRegistry().Add(new AutomationScheduledScanHandler(fake, () => exe, otherAccount));
        var journal = new InMemoryJournal();
        return (fake, new ActionExecutor(handlers, journal), new DesiredStateResolver(handlers), journal);
    }

    [Fact]
    public void Turning_on_creates_the_task_and_revert_removes_it()
    {
        var (fake, exec, resolver, journal) = Engine();
        var run = journal.BeginRun("t", "1");
        var r = exec.Execute(run, resolver.Resolve([Action("SUN 19:00")], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.True(ScheduledScanTask.Inspect(fake.Tasks[ScheduledScanTask.TaskName]).Ours);
        Assert.Equal("OFF", journal.Entries(run).Single().Original!.Value);

        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Empty(fake.Tasks);
    }

    [Fact]
    public void Changing_the_day_and_reverting_restores_the_exact_previous_xml()
    {
        var (fake, exec, resolver, journal) = Engine();
        var previous = ScheduledScanTask.BuildXml(SafeExe, ScanSchedule.TryParse("WED 20:00")!, Now);
        fake.Tasks[ScheduledScanTask.TaskName] = previous;
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Action("SUN 19:00")], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal("SUN 19:00", ScheduledScanTask.Inspect(fake.Tasks[ScheduledScanTask.TaskName]).Schedule!.ToString());

        exec.RevertRun(run);
        Assert.Equal(previous, fake.Tasks[ScheduledScanTask.TaskName]);
    }

    [Fact]
    public void Turning_off_removes_and_revert_recreates()
    {
        var (fake, exec, resolver, journal) = Engine();
        var previous = ScheduledScanTask.BuildXml(SafeExe, ScanSchedule.TryParse("SAT 12:00")!, Now);
        fake.Tasks[ScheduledScanTask.TaskName] = previous;
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Action("OFF")], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Empty(fake.Tasks);
        exec.RevertRun(run);
        Assert.Equal(previous, fake.Tasks[ScheduledScanTask.TaskName]);
    }

    [Fact]
    public void Foreign_task_with_the_same_name_is_never_touched()
    {
        var (fake, _, resolver, _) = Engine();
        const string foreign = """<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><Exec><Command>C:\outro.exe</Command></Exec></Actions></Task>""";
        fake.Tasks[ScheduledScanTask.TaskName] = foreign;
        var item = resolver.Resolve([Action("SUN 19:00")], T.Ctx).Items.Single();
        Assert.Equal(PlanStatus.Blocked, item.Status);
        Assert.Equal(0, fake.Writes);
        Assert.Equal(foreign, fake.Tasks[ScheduledScanTask.TaskName]);
    }

    [Fact]
    public void Other_account_and_missing_executable_are_refused()
    {
        var (_, _, other, _) = Engine(otherAccount: true);
        Assert.Equal(PlanStatus.Blocked, other.Resolve([Action("SUN 19:00")], T.Ctx).Items.Single().Status);
        var (_, _, missing, _) = Engine(exe: null);
        Assert.Equal(PlanStatus.NotApplicable, missing.Resolve([Action("SUN 19:00")], T.Ctx).Items.Single().Status);
        var (_, _, unsafeExe, _) = Engine(exe: @"C:\%TEMP%\nexus.exe");
        Assert.Equal(PlanStatus.Blocked, unsafeExe.Resolve([Action("SUN 19:00")], T.Ctx).Items.Single().Status);
    }

    [Fact]
    public void Task_pointing_to_another_folder_is_stale()
    {
        var (fake, _, _, _) = Engine();
        fake.Tasks[ScheduledScanTask.TaskName] = ScheduledScanTask.BuildXml(@"C:\Antigo\nexus.exe", ScanSchedule.TryParse("SUN 19:00")!, Now);
        var handler = new AutomationScheduledScanHandler(fake, () => SafeExe, false);
        Assert.Equal(AutomationScheduledScanHandler.Stale, handler.Detect(Action("SUN 19:00")).Value);
    }

    [Fact]
    public void Restore_validation_refuses_tampered_journal_xml()
    {
        var handler = new AutomationScheduledScanHandler(new FakeScheduler(), () => SafeExe, false);
        var good = ScheduledScanTask.BuildXml(SafeExe, ScanSchedule.TryParse("SUN 19:00")!, Now);
        StateSnapshot Orig(string xml) => StateSnapshot.Of("ScanSchedule", "SUN 19:00", ValueOrigin.Explicit, new Dictionary<string, string> { ["xml"] = xml });
        Assert.Null(handler.CheckRestore(Action("OFF"), Orig(good)));
        Assert.NotNull(handler.CheckRestore(Action("OFF"), Orig(good.Replace(ScheduledScanTask.Arguments, "apply cleaning.x --yes", StringComparison.Ordinal))));
        Assert.NotNull(handler.CheckRestore(Action("OFF"), Orig(good.Replace(SafeExe.Replace("&", "&amp;", StringComparison.Ordinal), @"C:\Windows\System32\cmd.exe", StringComparison.Ordinal))));
        Assert.NotNull(handler.CheckRestore(Action("OFF"), Orig(good.Replace(ScheduledScanTask.Marker, "x", StringComparison.Ordinal))));
        Assert.NotNull(handler.CheckRestore(Action("OFF"), StateSnapshot.Of("ScanSchedule", "SUN 19:00")));
        Assert.Null(handler.CheckRestore(Action("SUN 19:00"), StateSnapshot.Of("ScanSchedule", "OFF")));
        var wrongTarget = Action("OFF") with { Target = new Dictionary<string, string> { ["taskName"] = "Outra" } };
        Assert.NotNull(handler.CheckRestore(wrongTarget, StateSnapshot.Of("ScanSchedule", "OFF")));
    }

    [Fact]
    public void Catalog_loads_and_every_automation_handler_kind_is_registered()
    {
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        Assert.Equal(catalog.Actions.Count, catalog.Actions.Select(a => a.Definition.Key).Distinct().Count());
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), OtherAccount: false, Elevated: false);
        var kinds = AutomationHandlers.Create(env).Select(h => h.HandlerKind).ToHashSet(StringComparer.Ordinal);
        var mine = catalog.Actions.Where(a => a.Definition.Id.StartsWith("automation.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(mine);
        Assert.All(mine, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        var weekly = mine.Single(a => a.Definition.Id == AutomationHandlers.ScheduledScanActionId);
        Assert.Equal(PrivilegeLevel.User, weekly.Definition.Privileges);
        Assert.Null(weekly.TryInstantiate(new Dictionary<string, string>(), out _)); // sem escolha de horário não instancia
        var inst = (weekly with { DesiredValue = "SUN 19:00" }).TryInstantiate(new Dictionary<string, string>(), out var why);
        Assert.True(inst is not null, why);
        Assert.Equal("ScanSchedule:SUN 19:00", inst!.Desired.Describe());
        Assert.Contains(catalog.Rejected, r => r.Id == "automation.scheduled-apply");
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationCliTests
{
    [Fact]
    public void No_arguments_or_help_show_portuguese_help()
    {
        Assert.True(CliParser.Parse([]).Help);
        Assert.True(CliParser.Parse(["--help"]).Help);
        Assert.True(CliParser.Parse(["-h"]).Help);
        var topic = CliParser.Parse(["apply", "--help"]);
        Assert.True(topic.Help);
        Assert.Equal("apply", topic.HelpTopic);
        Assert.Contains("Uso: nexus <comando>", CliParser.Help, StringComparison.Ordinal);
        Assert.Contains("Códigos de saída", CliParser.Help, StringComparison.Ordinal);
        foreach (var c in CliParser.Commands.Where(c => c is not ("help" or "version")))
            Assert.Contains(c, CliParser.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_with_flags()
    {
        var c = CliParser.Parse(["scan", "--report", "--json", "--out", @"C:\Relatórios com espaço"]).Command!;
        Assert.Equal("scan", c.Name);
        Assert.True(c.Has("--report") && c.Json);
        Assert.Equal(@"C:\Relatórios com espaço", c.Option("--out"));
    }

    [Fact]
    public void Apply_with_ids_values_and_yes_alias()
    {
        var c = CliParser.Parse(["apply", "win.gamemode.enable", "automation.scheduled-scan.weekly=SUN 19:00", "-y"]).Command!;
        Assert.True(c.Yes);
        Assert.Equal(2, c.Arguments.Count);
        Assert.Equal(("automation.scheduled-scan.weekly", (int?)null, "SUN 19:00"), CliParser.ParseActionArgument(c.Arguments[1]));
        Assert.Equal(("win.gamemode.enable", (int?)2, (string?)null), CliParser.ParseActionArgument("win.gamemode.enable@2"));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("a b")]
    [InlineData("x@v")]
    [InlineData("=valor")]
    public void Invalid_action_ids_are_refused(string text) => Assert.Throws<FormatException>(() => CliParser.ParseActionArgument(text));

    [Theory]
    [InlineData("frob")]
    [InlineData("apply")]
    [InlineData("scan", "--yes")]
    [InlineData("scan", "--out")]
    [InlineData("revert", "123")]
    [InlineData("history", "--limit", "0")]
    [InlineData("history", "--limit", "abc")]
    [InlineData("compare", "--before", "a.csv")]
    [InlineData("compare", "--before", "--after", "b.csv")]
    [InlineData("schedule", "sometimes")]
    [InlineData("schedule", "on", "--at", "domingo")]
    [InlineData("measure")]
    [InlineData("scan", "extra")]
    public void Bad_usage_is_an_error(params string[] args)
    {
        var r = CliParser.Parse(args);
        Assert.Null(r.Command);
        Assert.False(r.Help);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Compare_takes_lists()
    {
        var c = CliParser.Parse(["compare", "--before", "a.csv", "b.csv", "--after", "c.csv", "--process", "cs2.exe"]).Command!;
        Assert.Equal(["a.csv", "b.csv"], c.List("--before"));
        Assert.Equal(["c.csv"], c.List("--after"));
        Assert.Equal("cs2.exe", c.Option("--process"));
    }

    [Fact]
    public void Revert_and_schedule()
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal(id, CliParser.Parse(["revert", id, "--admin"]).Command!.Arguments[0]);
        var s = CliParser.Parse(["schedule", "on", "--at", "WED 20:00"]).Command!;
        Assert.Equal("WED 20:00", s.Option("--at"));
        Assert.Equal("version", CliParser.Parse(["--version"]).Command!.Name);
    }

    [Fact]
    public void Exit_codes_are_distinct()
    {
        int[] codes = [CliExit.Ok, CliExit.Partial, CliExit.Usage, CliExit.NotFound, CliExit.Cancelled, CliExit.BrokerRefused, CliExit.Unsupported];
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}

[Trait("Category", "Unit")]
public sealed class AutomationReportTests
{
    [Fact]
    public void Report_html_is_escaped_and_files_are_written()
    {
        var facts = AutoFakes.Facts("NVIDIA <script>alert(1)</script>", "NVIDIA");
        var findings = DiagnosticRules.Evaluate(facts);
        var set = AutoFakes.Recommend(facts, new PcTraits(ChassisType: 3, SystemDriveIsSsd: true), findings: findings);
        var report = ScanReport.From(facts, findings, set, "t", new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var html = report.ToHtml();
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("\"profile\"", report.ToJson(), StringComparison.Ordinal);
        Assert.Contains("Seu PC:", report.ToText(), StringComparison.Ordinal);

        var dir = Path.Combine(Path.GetTempPath(), "nexus-auto-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = report.Save(dir, html: true);
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.True(File.Exists(f)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
