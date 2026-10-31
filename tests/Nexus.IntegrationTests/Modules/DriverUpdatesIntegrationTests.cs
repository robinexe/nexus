using System.Runtime.Versioning;
using Nexus.Broker;
using Nexus.Core;
using Nexus.DriverUpdates;
using Nexus.DriverUpdates.Windows;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Atualizar drivers com backends REAIS, só na VM descartável (portão avaliado antes de qualquer coisa). A VM do CI raramente
/// tem atualização de driver oferecida, então não instalamos nada de verdade: conferimos a leitura (Windows Update, SetupAPI)
/// e que o aplicador recusa um ID que não está disponível, sem escrever.
/// </summary>
[Trait("Category", "VmIntegration")]
[SupportedOSPlatform("windows")]
public sealed class DriverUpdatesIntegrationTests : IDisposable
{
    private static readonly string[] Refused = ["NotApplicable", "Blocked"];
    private readonly string _dir;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private string JournalPath => Path.Combine(_dir, "journal.db");

    public DriverUpdatesIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        Vm.Require(); // PRIMEIRO passo
        _dir = Directory.CreateTempSubdirectory("nexus-vm-drvupd-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void Inventory_lists_present_devices_with_drivers()
    {
        var devices = new SetupApiDeviceInventory().List();
        _out.WriteLine($"{devices.Count} dispositivos; com driver: {devices.Count(d => d.DriverVersion is not null)}");
        Assert.NotEmpty(devices);
        Assert.Contains(devices, d => d.DriverVersion is not null && d.InfName is not null);
    }

    [Fact]
    public void Search_finishes_with_offers_or_a_clear_error()
    {
        var r = new WuaAgent().SearchDrivers(TimeSpan.FromMinutes(8), CancellationToken.None);
        _out.WriteLine(r.Ok ? $"{r.Updates.Count} oferta(s): {string.Join(" | ", r.Updates.Select(u => u.Title))}" : $"{r.Error}: {r.Message}");
        Assert.True(r.Ok || !string.IsNullOrWhiteSpace(r.Message));
        Assert.All(r.Updates, u => Assert.True(WindowsUpdateQueries.IsValidUpdateId(u.UpdateId) && u.IsDriver));
    }

    [Fact]
    public void Broker_refuses_an_update_that_is_not_offered()
    {
        var journal = new SqliteJournal(JournalPath);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var env = new HandlerEnvironment(new WindowsRegistryBackend(), _dir, OtherAccount: false, Elevated: Vm.Elevated);
        var handlers = new HandlerRegistry();
        foreach (var h in DriverUpdatesHandlers.Create(env)) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var broker = new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx, () => new Dictionary<string, string>());
        var request = new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute,
            [new ActionRequest("driverupdates.install@1", Arguments: new Dictionary<string, string> { ["updateId"] = "00000000-0000-4000-8000-000000000000", ["updateRevision"] = "1" })]);
        var r = broker.Handle(request, null);
        var detail = string.Join("; ", r.Results?.Select(x => $"{x.Outcome}: {x.Detail}") ?? []) + (r.Error?.Detail ?? "");
        _out.WriteLine(detail);
        Assert.All(r.Results ?? [], x => Assert.Contains(x.Outcome, Refused));
        Assert.False(Directory.Exists(Path.Combine(_dir, "backup", "drivers", "wu-00000000-0000-4000-8000-000000000000")));
    }
}
