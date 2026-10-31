using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.Security;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Handlers do módulo Segurança com escrita e reversão REAIS, pelo broker e pelo catálogo real. Só em VM liberada (portão
/// antes de tudo). Cada teste cria as próprias entradas falsas e as apaga no fim; o hosts é devolvido byte a byte.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class SecurityIntegrationTests : IDisposable
{
    private readonly string _dir;
    private string JournalPath => Path.Combine(_dir, "journal.db");

    public SecurityIntegrationTests()
    {
        Vm.Require();
        _dir = Directory.CreateTempSubdirectory("nexus-vm-sec-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private (BrokerService Broker, SqliteJournal Journal) Broker()
    {
        var journal = new SqliteJournal(JournalPath);
        var reg = new WindowsRegistryBackend();
        var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        foreach (var h in SecurityHandlers.Create(new HandlerEnvironment(reg, _dir, false, Vm.Elevated))) handlers.Add(h);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return (new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(JournalPath), Vm.Gate, () => ctx, () => new Dictionary<string, string>()), journal);
    }

    private static BrokerRequest Exec(string actionId, string? selection = null) =>
        new(1, Guid.NewGuid(), BrokerOperation.Execute,
            [new ActionRequest(actionId, null, selection is null ? null : new Dictionary<string, string> { ["selection"] = selection })]);

    private static BrokerRequest Revert(Guid? run) => new(1, Guid.NewGuid(), BrokerOperation.RevertRun, null, run);

    [Fact]
    public void User_uninstall_orphan_is_removed_with_full_copy_and_recreated()
    {
        const string path = RegistryInspector.UninstallPath + @"\NexusVmOrphanTest";
        Registry.CurrentUser.DeleteSubKeyTree(path, false);
        using (var k = Registry.CurrentUser.CreateSubKey(path))
        {
            k.SetValue("DisplayName", "NEXUS teste órfão");
            k.SetValue("UninstallString", @"C:\nexus-nao-existe\unins000.exe");
            k.SetValue("EstimatedSize", 1234, RegistryValueKind.DWord);
            using var sub = k.CreateSubKey("Extra");
            sub.SetValue("Bin", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        }
        try
        {
            var orphan = RegistryInspector.Scan(new WindowsSecurityRegistry(), new SystemFileProbe(), RegScope.User)
                .Single(o => o.Key.Path.EndsWith("NexusVmOrphanTest", StringComparison.Ordinal));
            var (broker, journal) = Broker();
            using (broker) using (journal)
            {
                var r = broker.Handle(Exec("security.registry.orphans.user@1", orphan.Selection));
                Assert.True(r.Ok, r.Error?.Detail);
                Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
                Assert.Null(Registry.CurrentUser.OpenSubKey(path));

                var back = broker.Handle(Revert(r.RunId));
                Assert.True(back.Ok && back.FullyRestored == true, string.Join("; ", back.Results?.Select(x => x.Detail) ?? []));
                using var k = Registry.CurrentUser.OpenSubKey(path)!;
                Assert.Equal(1234, k.GetValue("EstimatedSize"));
                using var sub = k.OpenSubKey("Extra")!;
                Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])sub.GetValue("Bin")!);
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Fact]
    public void Ifeo_debugger_is_removed_and_restored_when_elevated()
    {
        if (!Vm.Elevated) return; // HKLM exige administrador; a VM do CI roda elevada
        const string path = PersistenceValueHandler.IfeoPath + @"\nexus-vm-test.exe";
        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            root.DeleteSubKeyTree(path, false);
            using var k = root.CreateSubKey(path);
            k.SetValue("Debugger", @"C:\Windows\System32\notepad.exe");
        }
        try
        {
            var sel = PersistenceValueHandler.IfeoDebuggers(new WindowsSecurityRegistry()).First(x => x.Image == "nexus-vm-test.exe").Candidate.Selection;
            var (broker, journal) = Broker();
            using (broker) using (journal)
            {
                var r = broker.Handle(Exec("security.persistence.machine.remove@1", sel));
                Assert.True(r.Ok, r.Error?.Detail);
                Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = root.OpenSubKey(path)!)
                    Assert.Null(k.GetValue("Debugger"));
                var back = broker.Handle(Revert(r.RunId));
                Assert.True(back.Ok && back.FullyRestored == true);
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = root.OpenSubKey(path)!)
                    Assert.Equal(@"C:\Windows\System32\notepad.exe", k.GetValue("Debugger"));
            }
        }
        finally
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            root.DeleteSubKeyTree(path, false);
        }
    }

    [Fact]
    public void Hosts_hijack_line_is_disabled_and_file_restored_byte_for_byte_when_elevated()
    {
        if (!Vm.Elevated) return;
        var store = new SystemHostsStore();
        var original = store.Read() ?? [];
        try
        {
            store.Write([.. original, .. "\r\n10.9.8.7 steamcommunity.com\r\n"u8.ToArray()]);
            var planted = store.Read()!;
            var (broker, journal) = Broker();
            using (broker) using (journal)
            {
                var r = broker.Handle(Exec("security.hosts.disable-hijack@1"));
                Assert.True(r.Ok, r.Error?.Detail);
                Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
                Assert.Contains(HostsFile.DisabledPrefix + "10.9.8.7 steamcommunity.com", System.Text.Encoding.UTF8.GetString(store.Read()!), StringComparison.Ordinal);
                var back = broker.Handle(Revert(r.RunId));
                Assert.True(back.Ok && back.FullyRestored == true, string.Join("; ", back.Results?.Select(x => x.Detail) ?? []));
                Assert.Equal(planted, store.Read());
            }
        }
        finally { store.Write(original); }
    }
}
