using Nexus.Core;
using Nexus.DriverUpdates;
using Nexus.Lab.Gpu;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

// ====================================================================== fakes (interfaces sobre o COM / SetupAPI / pnputil)

internal sealed class FakeWuRecord : IWuUpdateRecord
{
    public string Title { get; init; } = "NVIDIA - Display - 32.0.15.6636";
    public string UpdateId { get; init; } = "0d8a1c3e-1111-4a2b-9c3d-000000000001";
    public int Revision { get; init; } = 1;
    public int Type { get; init; } = 2;
    public bool IsInstalled { get; init; }
    public bool IsHidden { get; init; }
    public bool BrowseOnly { get; init; }
    public bool EulaAccepted { get; init; } = true;
    public bool IsDownloaded { get; init; }
    public decimal MaxDownloadSize { get; init; } = 734_003_200m;
    public IReadOnlyList<string> KbArticleIds { get; init; } = [];
    public string? SupportUrl { get; init; }
    public IReadOnlyList<string> MoreInfoUrls { get; init; } = [];
    public DateTime? LastDeploymentChangeTime { get; init; }
    public string? DriverClass { get; init; } = "Display";
    public string? DriverHardwareId { get; init; } = @"PCI\VEN_10DE&DEV_25A2&SUBSYS_14E51025";
    public string? DriverManufacturer { get; init; } = "NVIDIA";
    public string? DriverModel { get; init; } = "NVIDIA GeForce RTX 3050 Laptop GPU";
    public string? DriverProvider { get; init; } = "NVIDIA";
    public DateTime? DriverVerDate { get; init; } = new DateTime(2024, 12, 4);
}

internal sealed class FakeInventory : IDeviceInventory
{
    public List<DeviceDriver> Devices { get; } = [];
    public IReadOnlyList<DeviceDriver> List() => Devices.ToList();

    public void SetVersion(string instance, string version, string? inf = null)
    {
        var i = Devices.FindIndex(d => d.InstanceId == instance);
        Devices[i] = Devices[i] with { DriverVersion = version, InfName = inf ?? Devices[i].InfName };
    }
}

internal sealed class FakeAgent(FakeInventory inventory) : IWindowsUpdateAgent
{
    public List<WuDriverUpdate> Updates { get; } = [];
    public WuSearchOutcome? Failure { get; set; }
    public int Searches { get; private set; }
    public List<string> Installed { get; } = [];
    public bool RebootOnInstall { get; set; }
    public bool ApplyVersionOnInstall { get; set; } = true;
    public WuInstallResult? Result { get; set; }
    /// <summary>Dispositivo que recebe a versão oferecida ao instalar (por UpdateID).</summary>
    public Dictionary<string, string> TargetDevice { get; } = [];

    public WuSearchOutcome SearchDrivers(TimeSpan timeout, CancellationToken cancel)
    {
        Searches++;
        return Failure ?? WuSearchOutcome.Success(Updates.ToList());
    }

    public WuInstallResult DownloadAndInstall(string updateId, int revision, TimeSpan timeout)
    {
        Installed.Add(updateId);
        if (Result is { Succeeded: false } fail) return fail;
        var u = Updates.Single(x => x.UpdateId == updateId && x.Revision == revision);
        if (ApplyVersionOnInstall) inventory.SetVersion(TargetDevice[updateId], u.OfferedVersion!, "oem77.inf");
        Updates.Remove(u); // instalada: some da busca "IsInstalled=0"
        return Result ?? new WuInstallResult(2, RebootOnInstall, 0, null);
    }

    public bool? RebootRequired() => false;
}

internal sealed class FakeTool(FakeInventory inventory, string inboxDir) : IDriverPackageTool
{
    public Dictionary<string, string> Store { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ExportProducesNothing { get; set; }
    public List<string> Added { get; } = [];
    public List<(string HardwareId, string Inf)> Forced { get; } = [];
    /// <summary>Versão que o "driver anterior" devolve ao dispositivo quando reinstalado à força.</summary>
    public Dictionary<string, (string Instance, string Version, string Inf)> RestoresTo { get; } = [];

    public string? OriginalInfName(string publishedName) => Store.TryGetValue(publishedName, out var o) ? o : null;

    public void Export(string publishedName, string directory)
    {
        if (ExportProducesNothing) return;
        var sub = Path.Combine(directory, Path.GetFileNameWithoutExtension(Store[publishedName]) + ".inf_amd64_abc");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, Store[publishedName]), "[Version]");
    }

    public void Add(string infPath) => Added.Add(infPath);

    public bool ForceInstall(string hardwareId, string infPath)
    {
        Forced.Add((hardwareId, infPath));
        if (RestoresTo.TryGetValue(hardwareId, out var r)) inventory.SetVersion(r.Instance, r.Version, r.Inf);
        return false;
    }

    public string InboxInfDirectory => inboxDir;
}

internal sealed class FakeClock : IBootClock
{
    public DateTime LastBootUtc { get; set; } = DateTime.UtcNow.AddHours(-3);
}

// ====================================================================== mapeamento do WUA

[Trait("Category", "Unit")]
public sealed class DriverUpdatesParsingTests
{
    [Theory]
    [InlineData("NVIDIA - Display - 32.0.15.6636", "32.0.15.6636")]
    [InlineData("Apple, Inc. USBDevice Driver Update (552.0.0.0)", "552.0.0.0")] // título real do PC do responsável
    [InlineData("Realtek Semiconductor Corp. - Extension - 10.0.22621.31274", "10.0.22621.31274")]
    [InlineData("Intel - System - 10.1.1.42 ", "10.1.1.42")]
    [InlineData("Advanced Micro Devices, Inc. - System - 2.0", "2.0")]
    [InlineData("Atualização de driver sem versão", null)]
    [InlineData("Driver - 2024", null)]
    [InlineData("", null)]
    public void Version_comes_from_the_title(string title, string? expected) =>
        Assert.Equal(expected, WindowsUpdateQueries.VersionFromTitle(title));

    [Fact]
    public void Map_translates_the_com_record()
    {
        var r = new FakeWuRecord
        {
            UpdateId = "0D8A1C3E-1111-4A2B-9C3D-000000000001",
            BrowseOnly = true,
            KbArticleIds = ["5034123", "x<script>"],
            MoreInfoUrls = ["http://evil.example/driver.exe", "https://evil.example.microsoft.com.attacker.net/x", "https://support.microsoft.com/help/5034123"],
        };
        var u = WindowsUpdateQueries.Map(r);
        Assert.Equal("0d8a1c3e-1111-4a2b-9c3d-000000000001", u.UpdateId);
        Assert.True(u.IsDriver);
        Assert.True(u.Optional);
        Assert.Equal("32.0.15.6636", u.OfferedVersion);
        Assert.Equal(["KB5034123"], u.KbArticles);
        Assert.Equal("https://support.microsoft.com/help/5034123", u.DetailsUrl);
        Assert.Equal(734_003_200, u.SizeBytes);
        Assert.Equal(new DateOnly(2024, 12, 4), u.DriverDate);
        Assert.Equal(@"PCI\VEN_10DE&DEV_25A2&SUBSYS_14E51025", u.HardwareId);
        Assert.False(WindowsUpdateQueries.Map(new FakeWuRecord { Type = 1 }).IsDriver);
        Assert.Null(WindowsUpdateQueries.Map(new FakeWuRecord { SupportUrl = "https://example.com" }).DetailsUrl);
    }

    [Theory]
    [InlineData("https://www.catalog.update.microsoft.com/x", true)]
    [InlineData("https://catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=1", true)]
    [InlineData("https://go.microsoft.com/fwlink/?LinkId=1", true)]
    [InlineData("http://support.microsoft.com/", false)]
    [InlineData("https://microsoft.com.evil.net/", false)]
    [InlineData("https://user@support.microsoft.com/", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ms-settings:windowsupdate", false)]
    public void Details_links_only_from_microsoft_over_https(string url, bool ok) =>
        Assert.Equal(ok, WindowsUpdateQueries.IsAllowedDetailsUrl(url));

    [Theory]
    [InlineData(unchecked((int)0x8024402C), WuErrorKind.Offline)]
    [InlineData(unchecked((int)0x80072EE2), WuErrorKind.Offline)]
    [InlineData(unchecked((int)0x80070422), WuErrorKind.ServiceDisabled)]
    [InlineData(unchecked((int)0x8024002E), WuErrorKind.Policy)]
    [InlineData(unchecked((int)0x8024500C), WuErrorKind.Policy)]
    [InlineData(unchecked((int)0x80240016), WuErrorKind.Busy)]
    [InlineData(unchecked((int)0x80004005), WuErrorKind.Other)]
    public void Errors_are_explained(int hresult, WuErrorKind kind)
    {
        var (k, message) = WindowsUpdateQueries.DescribeError(hresult);
        Assert.Equal(kind, k);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void Search_criteria_is_the_official_driver_query() =>
        Assert.Equal("IsInstalled=0 and Type='Driver' and IsHidden=0", WindowsUpdateQueries.DriverSearch);

    [Fact]
    public void Catalog_link_requires_a_valid_id()
    {
        Assert.StartsWith("https://catalog.update.microsoft.com/", WindowsUpdateQueries.CatalogUrl("0d8a1c3e-1111-4a2b-9c3d-000000000001"), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WindowsUpdateQueries.CatalogUrl("x' or 1=1"));
    }
}

// ====================================================================== cruzamento com o inventário e "o que reverter"

[Trait("Category", "Unit")]
public sealed class DriverUpdatesMatchingTests
{
    internal static DeviceDriver Gpu(string version = "31.0.15.5222", string? inf = "oem42.inf", string? matching = @"pci\ven_10de&dev_25a2&subsys_14e51025") =>
        new(@"PCI\VEN_10DE&DEV_25A2&SUBSYS_14E51025&REV_A1\4&1", "NVIDIA GeForce RTX 3050 Laptop GPU",
            [@"PCI\VEN_10DE&DEV_25A2&SUBSYS_14E51025&REV_A1", @"PCI\VEN_10DE&DEV_25A2&SUBSYS_14E51025"], [@"PCI\VEN_10DE&DEV_25A2", @"PCI\CC_030000"],
            "Display", "{4d36e968-e325-11ce-bfc1-08002be10318}", "NVIDIA", version, new DateOnly(2024, 1, 10), "NVIDIA", inf, matching);

    internal static DeviceDriver Audio(string? version = "6.0.9600.1", string? inf = "oem10.inf") =>
        new(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256\4&2", "Realtek(R) Audio", [@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10251367"], [],
            "MEDIA", "{4d36e96c-e325-11ce-bfc1-08002be10318}", "Realtek", version, new DateOnly(2023, 5, 1), "Realtek", inf,
            version is null ? null : @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10251367");

    internal static WuDriverUpdate Update(string id = "0d8a1c3e-1111-4a2b-9c3d-000000000001", string title = "NVIDIA - Display - 32.0.15.6636",
        string? hw = @"pci\ven_10de&dev_25a2&subsys_14e51025", bool optional = false, string cls = "Display", bool driver = true, int revision = 1, bool eula = true) =>
        new(id, revision, title, driver, optional, eula, false, 700_000_000, cls, hw, "NVIDIA", "NVIDIA GeForce RTX 3050 Laptop GPU", "NVIDIA",
            new DateOnly(2024, 12, 4), WindowsUpdateQueries.VersionFromTitle(title), ["KB5034123"], null);

    [Fact]
    public void Match_by_hardware_id_ignores_case_and_uses_compatible_ids()
    {
        var devices = new[] { Gpu(), Audio() };
        Assert.Single(DeviceMatching.Match(Update(), devices));
        Assert.Single(DeviceMatching.Match(Update(hw: @"PCI\VEN_10DE&DEV_25A2"), devices)); // compatible ID
        Assert.Empty(DeviceMatching.Match(Update(hw: @"PCI\VEN_1002&DEV_1638"), devices)); // hardware ID decide, mesmo com modelo igual
        // Sem hardware ID na oferta: cai para o modelo (descrição).
        Assert.Single(DeviceMatching.Match(Update(hw: null), devices));
    }

    [Theory]
    [InlineData("32.0.15.6636", "32.0.15.6636", true)]
    [InlineData("1.2", "1.2.0.0", true)]
    [InlineData("10.0.1.5", "10.0.1.50", false)]
    [InlineData(null, "1.0", false)]
    public void Same_version(string? a, string? b, bool same) => Assert.Equal(same, DeviceMatching.SameVersion(a, b));

    [Fact]
    public void Combine_groups_and_crosses_installed_version()
    {
        var updates = new[]
        {
            Update(),
            Update("0d8a1c3e-1111-4a2b-9c3d-000000000002", "Realtek Semiconductor Corp. - MEDIA - 6.0.9700.1", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10251367", optional: true, cls: "MEDIA"),
            Update("0d8a1c3e-1111-4a2b-9c3d-000000000003", "Intel - System - 1.0.0.1", @"ACPI\INT0000", cls: "System"),
            Update("0d8a1c3e-1111-4a2b-9c3d-000000000004", "Software qualquer - 1.0", driver: false),
            Update("0d8a1c3e-1111-4a2b-9c3d-000000000005", "Contoso - Net - sem versao", @"PCI\VEN_8086&DEV_0001", cls: "Net"),
        };
        var offers = DriverUpdatesReader.Combine(updates, [Gpu(), Audio()]);
        Assert.Equal(4, offers.Count); // o software ficou de fora
        var gpu = offers.Single(o => o.Update.UpdateId.EndsWith('1'));
        Assert.Equal(OfferGroup.Graphics, gpu.Group);
        Assert.Equal("31.0.15.5222", gpu.InstalledVersion);
        Assert.Contains("mais nova", gpu.VersionLine, StringComparison.Ordinal);
        Assert.Null(gpu.Blocked);
        Assert.Equal(RollbackSource.Backup, gpu.Rollback.Source);
        Assert.Equal(OfferGroup.Optional, offers.Single(o => o.Update.UpdateId.EndsWith('2')).Group);
        var orphan = offers.Single(o => o.Update.UpdateId.EndsWith('3'));
        Assert.Equal(OfferGroup.Recommended, orphan.Group);
        Assert.NotNull(orphan.Blocked); // sem dispositivo presente
        Assert.NotNull(offers.Single(o => o.Update.UpdateId.EndsWith('5')).Blocked); // versão desconhecida
    }

    [Fact]
    public void Older_offer_is_flagged()
    {
        var offer = DriverUpdatesReader.Combine([Update(title: "NVIDIA - Display - 31.0.15.1000")], [Gpu()]).Single();
        Assert.Contains("MAIS ANTIGA", offer.VersionLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_selection()
    {
        Assert.Equal(RollbackSource.Backup, RollbackRules.Plan(Gpu()).Source);
        var inbox = RollbackRules.Plan(Gpu(version: "10.0.26100.1", inf: "display.inf"));
        Assert.Equal(RollbackSource.Inbox, inbox.Source);
        Assert.Equal("display.inf", inbox.InfName);
        Assert.False(RollbackRules.Plan(Audio(version: null, inf: null)).Possible); // sem driver
        Assert.False(RollbackRules.Plan(Gpu(matching: null)).Possible);              // sem ID registrado
        Assert.False(RollbackRules.Plan(Gpu(inf: @"..\..\evil.inf")).Possible);     // nome estranho
        Assert.False(RollbackRules.Plan(null).Possible);
        Assert.False(RollbackRules.IsInboxInfName("oem3.inf"));
    }

    [Fact]
    public void Confirmation_lists_everything_and_is_honest_about_rollback()
    {
        var offers = DriverUpdatesReader.Combine(
            [Update(), Update("0d8a1c3e-1111-4a2b-9c3d-000000000002", "Realtek Semiconductor Corp. - MEDIA - 6.0.9700.1", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10251367", optional: true, cls: "MEDIA")],
            [Gpu(), Audio(version: null, inf: null)]);
        var text = DriverUpdatesReader.ConfirmationText(offers);
        Assert.Contains("NVIDIA GeForce RTX 3050 Laptop GPU", text, StringComparison.Ordinal);
        Assert.Contains("Realtek(R) Audio", text, StringComparison.Ordinal);
        Assert.Contains("ponto de restauração", text, StringComparison.Ordinal);
        Assert.Contains("reinício", text, StringComparison.Ordinal);
        Assert.Contains("SEM DESFAZER AUTOMÁTICO para: Realtek(R) Audio", text, StringComparison.Ordinal);
    }
}

// ====================================================================== placa de vídeo

[Trait("Category", "Unit")]
public sealed class DriverUpdatesGpuTests
{
    [Fact]
    public void Gpu_status_uses_the_gpu_lab_reading()
    {
        var igpu = new GpuAdapter { Name = "AMD Radeon(TM) Graphics", VendorId = 0x1002, DeviceId = 0x1638, DedicatedVideoBytes = 512UL << 20, DriverVersion = "31.0.12027.9001", DriverDate = new DateOnly(2023, 1, 1) };
        var dgpu = new GpuAdapter { Name = "NVIDIA GeForce RTX 3050 Laptop GPU", VendorId = 0x10DE, DeviceId = 0x25A2, DedicatedVideoBytes = 4UL << 30, DriverVersion = "32.0.15.6636", DriverDate = new DateOnly(2024, 12, 4) };
        var soft = new GpuAdapter { Name = "Microsoft Basic Render Driver", VendorId = 0x1414, DeviceId = 0x8C, IsSoftware = true };
        var app = new VendorApp("NVIDIA App", @"C:\Program Files\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe");
        var list = GpuDrivers.From([igpu, dgpu, soft], new DateOnly(2026, 10, 31), v => v == GpuVendor.Nvidia ? app : null);
        Assert.Equal(2, list.Count);
        var nv = list.Single(g => g.Vendor == GpuVendor.Nvidia);
        Assert.Equal("566.36", nv.VendorVersion);
        Assert.Equal("warn", nv.Level); // mais de 1 ano (696 dias)
        Assert.Equal("https://www.nvidia.com/pt-br/drivers/", nv.OfficialPage);
        Assert.Same(app, nv.App);
        Assert.True(list.Single(g => g.Vendor == GpuVendor.Amd).Integrated);
    }

    [Fact]
    public void Official_app_is_found_only_in_fixed_paths()
    {
        var root = @"C:\Program Files";
        var expected = Path.Combine(root, @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe");
        var found = GpuDrivers.FindApp(GpuVendor.Nvidia, [root], p => p == expected);
        Assert.Equal("NVIDIA App", found?.Name);
        Assert.Null(GpuDrivers.FindApp(GpuVendor.Intel, [root], _ => true)); // Intel: só a página
        Assert.Null(GpuDrivers.FindApp(GpuVendor.Nvidia, [root], _ => false));
    }

    [Fact]
    public void Display_offers_go_to_the_graphics_group()
    {
        Assert.True(GpuDrivers.IsDisplay(DriverUpdatesMatchingTests.Update()));
        Assert.False(GpuDrivers.IsDisplay(DriverUpdatesMatchingTests.Update(title: "Realtek - MEDIA - 1.0", cls: "MEDIA")));
        Assert.Contains("jogo", GpuDrivers.WhenToUpdate, StringComparison.Ordinal);
    }
}

// ====================================================================== handler (Detect/Apply/Restore com backends falsos)

[Trait("Category", "Unit")]
public sealed class DriverUpdatesHandlerTests : IDisposable
{
    private const string Id1 = "0d8a1c3e-1111-4a2b-9c3d-000000000001";
    private const string Id2 = "0d8a1c3e-1111-4a2b-9c3d-000000000002";
    private const string GpuInstance = @"PCI\VEN_10DE&DEV_25A2&SUBSYS_14E51025&REV_A1\4&1";
    private const string AudioInstance = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256\4&2";
    private const string AudioHw = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10251367";
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-drvupd-").FullName;
    private readonly FakeInventory _inv = new();
    private readonly FakeAgent _agent;
    private readonly FakeTool _tool;
    private readonly FakeClock _clock = new();
    private readonly DriverUpdateInstallHandler _handler;

    public DriverUpdatesHandlerTests()
    {
        _agent = new FakeAgent(_inv);
        var inbox = Path.Combine(_dir, "INF");
        Directory.CreateDirectory(inbox);
        File.WriteAllText(Path.Combine(inbox, "hdaudio.inf"), "[Version]");
        _tool = new FakeTool(_inv, inbox);
        _tool.Store["oem42.inf"] = "nv_dispig.inf";
        _tool.Store["oem10.inf"] = "hdxrt.inf";
        _inv.Devices.Add(DriverUpdatesMatchingTests.Gpu());
        _inv.Devices.Add(DriverUpdatesMatchingTests.Audio());
        _agent.Updates.Add(DriverUpdatesMatchingTests.Update());
        _agent.Updates.Add(DriverUpdatesMatchingTests.Update(Id2, "Realtek Semiconductor Corp. - MEDIA - 6.0.9700.1", AudioHw, optional: true, cls: "MEDIA"));
        _agent.TargetDevice[Id1] = GpuInstance;
        _agent.TargetDevice[Id2] = AudioInstance;
        _tool.RestoresTo[@"pci\ven_10de&dev_25a2&subsys_14e51025"] = (GpuInstance, "31.0.15.5222", "oem42.inf");
        _tool.RestoresTo[AudioHw] = (AudioInstance, "6.0.9600.1", "oem10.inf");
        _handler = new DriverUpdateInstallHandler(_agent, _inv, _tool, _clock, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ActionInstance Instance(string id, string revision = "1")
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "driverupdates.install");
        var args = entry.ValidateUserArguments(new Dictionary<string, string> { ["updateId"] = id, ["updateRevision"] = revision }, out var reason);
        Assert.True(args is not null, reason);
        var inst = entry.TryInstantiate(args!, out reason);
        Assert.True(inst is not null, reason);
        return inst!;
    }

    private (IReadOnlyList<ExecutionResult> Results, ActionExecutor Exec, Guid Run) Execute(params ActionInstance[] actions)
    {
        var handlers = new HandlerRegistry().Add(_handler);
        var exec = new ActionExecutor(handlers, new InMemoryJournal());
        var run = exec.BeginRun("teste", "1");
        var plan = new DesiredStateResolver(handlers).Resolve(actions, T.Ctx);
        return (exec.Execute(run, plan, T.Ctx), exec, run);
    }

    // ------------------------------------------------------------------ catálogo

    [Fact]
    public void Catalog_loads_and_the_kind_is_registered()
    {
        var mine = Catalog.Actions.Where(a => a.Definition.Id.StartsWith("driverupdates.", StringComparison.Ordinal)).ToList();
        var a = Assert.Single(mine);
        Assert.Equal(Catalog.Actions.Count, Catalog.Actions.Select(x => x.Definition.Key).Distinct().Count());
        var kinds = DriverUpdatesHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), _dir, false, true)).Select(h => h.HandlerKind).ToList();
        Assert.Equal([DriverUpdateInstallHandler.Kind], kinds);
        Assert.Equal(DriverUpdateInstallHandler.Kind, a.Definition.HandlerKind);
        Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges);
        Assert.Equal(PersistenceKind.Persistent, a.Definition.Persistence);
        Assert.False(a.Definition.Irreversible);
        Assert.True(a.Definition.Evidence.Count > 0 && !string.IsNullOrWhiteSpace(a.Revert) && !string.IsNullOrWhiteSpace(a.Definition.Mechanism));
        Assert.Equal(WindowsUpdateQueries.UpdateIdRegex, a.UserParameters["updateId"]);
        Assert.Equal(WindowsUpdateQueries.RevisionRegex, a.UserParameters["updateRevision"]);
    }

    [Theory]
    [InlineData("0D8A1C3E-1111-4A2B-9C3D-000000000001", "1")]  // maiúsculas: a página sempre manda minúsculo
    [InlineData("0d8a1c3e-1111-4a2b-9c3d-000000000001\n", "1")]
    [InlineData("{0d8a1c3e-1111-4a2b-9c3d-000000000001}", "1")]
    [InlineData("0d8a1c3e-1111-4a2b-9c3d-000000000001' or 1=1", "1")]
    [InlineData("0d8a1c3e-1111-4a2b-9c3d-000000000001", "1 or 1=1")]
    [InlineData("0d8a1c3e-1111-4a2b-9c3d-000000000001", "1234567")]
    [InlineData("0d8a1c3e-1111-4a2b-9c3d-000000000001", "-1")]
    public void User_parameters_reject_bad_ids(string id, string revision)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "driverupdates.install");
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["updateId"] = id, ["updateRevision"] = revision }, out _));
    }

    // ------------------------------------------------------------------ ida e volta

    [Fact]
    public void Install_backs_up_first_and_undo_reinstalls_the_previous_driver()
    {
        var (results, exec, run) = Execute(Instance(Id1));
        var r = Assert.Single(results);
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.Equal([Id1], _agent.Installed);
        Assert.Equal("32.0.15.6636", _inv.Devices[0].DriverVersion);
        var backup = _handler.BackupDirectory(Id1);
        Assert.NotNull(DriverUpdateInstallHandler.FindInf(backup, "nv_dispig.inf"));
        var m = _handler.ReadManifest(Id1)!;
        Assert.Equal(RollbackSource.Backup, m.Source);
        Assert.Equal("31.0.15.5222", m.PreviousVersion);
        Assert.NotNull(m.InstalledUtc);

        var undo = Assert.Single(exec.RevertRun(run));
        Assert.Equal(RevertOutcome.Reverted, undo.Outcome);
        var (hw, inf) = Assert.Single(_tool.Forced);
        Assert.Equal(@"pci\ven_10de&dev_25a2&subsys_14e51025", hw);
        Assert.StartsWith(backup, inf, StringComparison.Ordinal);
        Assert.Equal(inf, Assert.Single(_tool.Added));
        Assert.Equal("31.0.15.5222", _inv.Devices[0].DriverVersion);
    }

    [Fact]
    public void Batch_installs_and_reverts_several_updates_in_one_run()
    {
        var (results, exec, run) = Execute(Instance(Id1), Instance(Id2));
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));
        var undo = exec.RevertRun(run);
        Assert.Equal(2, undo.Count);
        Assert.All(undo, u => Assert.Equal(RevertOutcome.Reverted, u.Outcome));
        Assert.Equal("6.0.9600.1", _inv.Devices[1].DriverVersion);
    }

    [Fact]
    public void Inbox_driver_is_reinstalled_from_the_windows_inf_folder()
    {
        _inv.Devices[1] = DriverUpdatesMatchingTests.Audio(inf: "hdaudio.inf");
        _tool.RestoresTo[AudioHw] = (AudioInstance, "6.0.9600.1", "hdaudio.inf");
        var (results, exec, run) = Execute(Instance(Id2));
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results).Outcome);
        Assert.Equal(RollbackSource.Inbox, _handler.ReadManifest(Id2)!.Source);
        Assert.Equal(RevertOutcome.Reverted, Assert.Single(exec.RevertRun(run)).Outcome);
        Assert.Equal(Path.Combine(_tool.InboxInfDirectory, "hdaudio.inf"), Assert.Single(_tool.Forced).Inf);
        Assert.Empty(_tool.Added);
    }

    [Fact]
    public void Without_a_previous_driver_undo_is_refused_honestly()
    {
        _inv.Devices[1] = DriverUpdatesMatchingTests.Audio(version: null, inf: null);
        var (results, exec, run) = Execute(Instance(Id2));
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results).Outcome);
        Assert.Equal(RollbackSource.None, _handler.ReadManifest(Id2)!.Source);
        var undo = Assert.Single(exec.RevertRun(run));
        Assert.Equal(RevertOutcome.Failed, undo.Outcome);
        Assert.Contains("Restauração do Sistema", undo.Detail, StringComparison.Ordinal);
        Assert.Empty(_tool.Forced);
    }

    [Fact]
    public void Reboot_pending_counts_as_installed_until_the_next_boot()
    {
        _agent.ApplyVersionOnInstall = false;
        _agent.RebootOnInstall = true;
        var (results, _, _) = Execute(Instance(Id1));
        var r = Assert.Single(results);
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        var state = _handler.Detect(Instance(Id1));
        Assert.Equal(DriverUpdateInstallHandler.Installed, state.Value);
        Assert.Equal("pendente", state.Detail!["reinicio"]);
        // Reiniciou e a versão não mudou: a instalação não valeu (Detect volta a "pendente").
        _clock.LastBootUtc = DateTime.UtcNow.AddMinutes(1);
        Assert.Equal(DriverUpdateInstallHandler.Pending, _handler.Detect(Instance(Id1)).Value);
    }

    // ------------------------------------------------------------------ recusas

    [Fact]
    public void Refuses_an_id_that_is_not_a_driver()
    {
        _agent.Updates[0] = _agent.Updates[0] with { IsDriver = false };
        var a = _handler.Assess(Instance(Id1), T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, a.Kind);
        Assert.Contains("não é de uma atualização de driver", a.Reason, StringComparison.Ordinal);
        Assert.Equal(ExecutionOutcome.Blocked, Assert.Single(Execute(Instance(Id1)).Results).Outcome);
        Assert.Empty(_agent.Installed);
    }

    [Fact]
    public void Refuses_an_update_that_is_no_longer_available_or_changed_revision()
    {
        Assert.Equal(ApplicabilityKind.NotApplicable, _handler.Assess(Instance("0d8a1c3e-1111-4a2b-9c3d-0000000000ff"), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, _handler.Assess(Instance(Id1, "2"), T.Ctx).Kind);
        Assert.Empty(_agent.Installed);
    }

    [Fact]
    public void Search_failure_blocks_with_a_clear_message()
    {
        _agent.Failure = WuSearchOutcome.Fail(WuErrorKind.Policy, WindowsUpdateQueries.DescribeError(unchecked((int)0x8024002E)).Message);
        var a = _handler.Assess(Instance(Id1), T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, a.Kind);
        Assert.Contains("política", a.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Eula_and_unknown_version_are_refused()
    {
        _agent.Updates[0] = _agent.Updates[0] with { EulaAccepted = false };
        Assert.Contains("termos", _handler.Assess(Instance(Id1), T.Ctx).Reason, StringComparison.Ordinal);
        _agent.Updates[0] = _agent.Updates[0] with { EulaAccepted = true, OfferedVersion = null };
        Assert.Equal(ApplicabilityKind.Blocked, _handler.Assess(Instance(Id1), T.Ctx).Kind);
    }

    [Fact]
    public void Missing_device_is_not_applicable()
    {
        _inv.Devices.RemoveAt(0);
        Assert.Equal(ApplicabilityKind.NotApplicable, _handler.Assess(Instance(Id1), T.Ctx).Kind);
    }

    [Fact]
    public void Failed_backup_installs_nothing()
    {
        _tool.ExportProducesNothing = true;
        var r = Assert.Single(Execute(Instance(Id1)).Results);
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Empty(_agent.Installed);
        Assert.Equal("31.0.15.5222", _inv.Devices[0].DriverVersion);
    }

    [Fact]
    public void Failed_install_changes_nothing()
    {
        _agent.Result = new WuInstallResult(4, false, unchecked((int)0x80240016), "O Windows Update está ocupado.");
        var r = Assert.Single(Execute(Instance(Id1)).Results);
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, r.Outcome);
        Assert.Empty(_tool.Forced);
        Assert.Equal("31.0.15.5222", _inv.Devices[0].DriverVersion);
    }

    [Fact]
    public void Restore_validation_refuses_tampered_or_missing_backups()
    {
        var pending = StateSnapshot.Of(DriverUpdateInstallHandler.StateType, DriverUpdateInstallHandler.Pending);
        Assert.NotNull(_handler.CheckRestore(Instance(Id1), pending)); // sem manifesto
        Assert.NotNull(_handler.CheckRestore(Instance(Id1), StateSnapshot.Of("RegistryValue", "1")));
        Assert.Null(_handler.CheckRestore(Instance(Id1), StateSnapshot.Of(DriverUpdateInstallHandler.StateType, DriverUpdateInstallHandler.Installed)));
        var bad = Instance(Id1);
        bad = bad with { Target = new Dictionary<string, string> { ["updateId"] = @"..\..\x", ["revision"] = "1" } };
        Assert.NotNull(_handler.CheckRestore(bad, pending));
        Assert.Null(_handler.ReadManifest(@"..\..\x"));

        Execute(Instance(Id1));
        Assert.Null(_handler.CheckRestore(Instance(Id1), pending));
        Directory.Delete(_handler.BackupDirectory(Id1) + Path.DirectorySeparatorChar + "nv_dispig.inf_amd64_abc", true);
        Assert.NotNull(_handler.CheckRestore(Instance(Id1), pending)); // pacote anterior sumiu do backup
    }

    [Fact]
    public void Previous_backup_is_never_overwritten()
    {
        var dir = _handler.BackupDirectory(Id1);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "antigo.txt"), "x");
        Execute(Instance(Id1));
        Assert.Contains(Directory.EnumerateDirectories(Path.GetDirectoryName(dir)!), d => Path.GetFileName(d).StartsWith("wu-" + Id1 + "-", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(dir, "antigo.txt")));
    }
}
