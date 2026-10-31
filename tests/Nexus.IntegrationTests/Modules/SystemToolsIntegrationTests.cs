using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.SystemTools;
using Nexus.SystemTools.Appx;
using Nexus.SystemTools.Drivers;
using Nexus.SystemTools.Restore;
using Nexus.SystemTools.Wifi;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Ferramentas do sistema com handlers REAIS, só na VM descartável (portão avaliado antes de qualquer coisa). O que a VM não
/// tem (Wi-Fi, Restauração do Sistema no Windows Server, drivers sem uso) é pulado com mensagem, sem escrever nada.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class SystemToolsIntegrationTests : IDisposable
{
    private static readonly string[] RefusedDriver = ["FailedAndCompensated", "Blocked"];
    private static readonly string[] PointOutcomes = ["Verified", "FailedAndCompensated"];
    private readonly string _dir;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private string JournalPath => Path.Combine(_dir, "journal.db");

    public SystemToolsIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        Vm.Require(); // PRIMEIRO passo
        _dir = Directory.CreateTempSubdirectory("nexus-vm-sys-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private (BrokerService Broker, SqliteJournal Journal) Broker()
    {
        var journal = new SqliteJournal(JournalPath);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var env = new HandlerEnvironment(new WindowsRegistryBackend(), _dir, OtherAccount: false, Elevated: Vm.Elevated);
        var handlers = new HandlerRegistry();
        foreach (var h in SystemToolsHandlers.Create(env)) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return (new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>()), journal);
    }

    private static BrokerRequest Exec(string actionId, Dictionary<string, string>? args = null) =>
        new(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest(actionId, Arguments: args)]);

    private static BrokerRequest Revert(Guid? run) => new(1, Guid.NewGuid(), BrokerOperation.RevertRun, null, run);

    private static string Details(BrokerResponse r) => string.Join("; ", r.Results?.Select(x => $"{x.Outcome}: {x.Detail}") ?? []) + (r.Error?.Detail ?? "");

    [Fact]
    public void Dns_flush_runs_and_revert_is_nothing_to_restore()
    {
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Exec("system.network.dns-flush@1"));
            Assert.True(exec.Ok, Details(exec));
            Assert.Equal("Verified", Assert.Single(exec.Results!).Outcome);
            var revert = broker.Handle(Revert(exec.RunId));
            Assert.True(revert.Ok && revert.FullyRestored == true, Details(revert));
            _out.WriteLine("[dns] limpo e 'desfeito' (nada a restaurar): " + Details(revert));
        }
    }

    [Fact]
    public void Wifi_profile_round_trip_or_skip_without_wifi()
    {
        var wifi = new WindowsWifiBackend();
        IReadOnlyList<Guid> interfaces;
        try { interfaces = wifi.Interfaces(); }
        catch (WifiUnavailableException ex) { _out.WriteLine("[wifi] PULADO: " + ex.Message); return; }
        if (interfaces.Count == 0) { _out.WriteLine("[wifi] PULADO: nenhum adaptador Wi-Fi na VM."); return; }
        if (!Vm.Elevated) { _out.WriteLine("[wifi] PULADO: exige elevação."); return; }

        const string name = "NEXUS-teste-integracao";
        var xml = $"""
            <?xml version="1.0"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><name>{name}</name>
            <SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>manual</connectionMode>
            <MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
            <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>senha-de-teste-123</keyMaterial></sharedKey></security></MSM></WLANProfile>
            """;
        wifi.Import(interfaces[0], xml, userOnly: false);
        try
        {
            var (broker, journal) = Broker();
            using (broker) using (journal)
            {
                var exec = broker.Handle(Exec("system.wifi.profile-remove@1", new() { ["wifiProfile"] = name }));
                Assert.True(exec.Ok, Details(exec));
                Assert.Equal("Verified", Assert.Single(exec.Results!).Outcome);
                Assert.Empty(wifi.Find(name));
                var revert = broker.Handle(Revert(exec.RunId));
                Assert.True(revert.FullyRestored == true, Details(revert));
                Assert.NotEmpty(wifi.Find(name));
                _out.WriteLine("[wifi] removido com backup e reimportado.");
            }
        }
        finally
        {
            foreach (var r in wifi.Find(name)) wifi.Delete(r.Interface, name); // perfil do próprio teste
        }
    }

    [Fact]
    public void Driver_store_is_read_and_an_unused_package_round_trips_if_any()
    {
        var store = new PnpUtilDriverStore(new WindowsProcessRunner());
        var all = store.List();
        var inUse = store.InUse();
        _out.WriteLine($"[drivers] {all.Count} pacotes de terceiros; em uso conhecido: {(inUse is null ? "n/d" : inUse.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        Assert.All(all, p => Assert.True(DriverParsers.IsOemInf(p.PublishedName) && p.OriginalName.Length > 0, p.PublishedName));
        if ((Vm.Facts.WindowsBuild ?? 0) >= 19041) Assert.NotNull(inUse);

        var candidate = all.FirstOrDefault(p => DriverRules.CheckRemoval(p, all, inUse) is null);
        if (candidate is null || !Vm.Elevated) { _out.WriteLine("[drivers] PULADO: nenhum pacote sem uso e fora das classes protegidas (ou sem elevação)."); return; }

        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Exec("system.driver.package-remove@1", new() { ["driverInf"] = candidate.PublishedName }));
            Assert.True(exec.Ok, Details(exec));
            var r = Assert.Single(exec.Results!);
            _out.WriteLine($"[drivers] remover {candidate.PublishedName} ({candidate.OriginalName}): {r.Outcome} {r.Detail}");
            if (r.Outcome != "Verified") { Assert.Contains(r.Outcome, RefusedDriver); return; } // Windows recusou: nada mudou
            var revert = broker.Handle(Revert(exec.RunId));
            Assert.True(revert.FullyRestored == true, Details(revert));
            Assert.Contains(store.List(), p => p.Identity == candidate.Identity && p.VersionText == candidate.VersionText);
        }
    }

    [Fact]
    public void Driver_backup_all_exports_when_elevated()
    {
        if (!Vm.Elevated) { _out.WriteLine("[drivers/backup] PULADO: exige elevação."); return; }
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Exec("system.driver.backup-all@1"));
            Assert.True(exec.Ok, Details(exec));
            Assert.Equal("Verified", Assert.Single(exec.Results!).Outcome);
            Assert.Single(Directory.GetDirectories(DriverPackageHandler.BackupRoot(_dir), "todos-*"));
        }
    }

    [Fact]
    public void Restore_point_is_created_or_the_reason_is_explained()
    {
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var exec = broker.Handle(Exec("system.restore.create-point@1"));
            Assert.True(exec.Ok, Details(exec));
            var r = Assert.Single(exec.Results!);
            _out.WriteLine($"[ponto] elevado={Vm.Elevated}: {r.Outcome} {r.Detail}");
            if (!Vm.Elevated) { Assert.Equal("Blocked", r.Outcome); return; }
            // Windows Server (VM do CI) não tem Restauração do Sistema; Windows 10/11 pode estar com a proteção desligada.
            Assert.Contains(r.Outcome, PointOutcomes);
            if (r.Outcome != "Verified") Assert.False(string.IsNullOrWhiteSpace(r.Detail));
            _out.WriteLine("[ponto] lista: " + (new WindowsRestorePointBackend(new WindowsProcessRunner()).List() is var l && l.Items is not null ? $"{l.Items.Count} ponto(s)" : l.Error));
        }
    }

    [Fact]
    public void Appx_inventory_is_readable()
    {
        var b = new PowerShellAppxBackend(new WindowsProcessRunner());
        var list = b.ListUser();
        _out.WriteLine($"[appx] {list.Count} pacotes do usuário; provisionados: {b.ListProvisioned()?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/d"}");
        Assert.All(list, p => Assert.False(string.IsNullOrEmpty(p.PackageFullName)));
        var removable = list.Where(p => AppxCatalog.CheckRemoval(p) is null).Select(p => p.Name).ToList();
        _out.WriteLine("[appx] removíveis pela lista do NEXUS: " + (removable.Count == 0 ? "nenhum" : string.Join(", ", removable)));
    }
}
