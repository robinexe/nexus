using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.Programs;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Sobras de programa com escrita REAL (VM descartável): pasta em %AppData% e chave em HKCU\Software com valores de vários
/// tipos vão para a quarentena pelo broker (catálogo real, handler ProgramLeftover real) e voltam pelo RevertRun.
/// Elevado: também uma pasta em ProgramData e uma chave em HKLM\Software.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class ProgramsIntegrationTests : IDisposable
{
    private const string Vendor = "NexusTestVendor";
    private const string App = "NexusTestLeftoverApp";
    private static readonly byte[] BlobValue = [0, 1, 255];
    private static readonly string[] ListValue = ["a", "b"];
    private static readonly string[] Refused = ["Blocked", "NotApplicable"];
    private readonly string _dir;

    public ProgramsIntegrationTests()
    {
        Vm.Require(); // nada é criado antes do portão
        _dir = Directory.CreateTempSubdirectory("nexus-vm-programs-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private BrokerService Broker(string journalPath)
    {
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var env = new HandlerEnvironment(new WindowsRegistryBackend(), _dir, OtherAccount: false, Elevated: Vm.Elevated);
        var handlers = new HandlerRegistry();
        foreach (var h in ProgramsHandlers.Create(env)) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return new BrokerService(catalog, handlers, new SqliteJournal(journalPath), FileRunCoordinator.ForJournal(journalPath), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>());
    }

    private static ActionRequest Leftover(string slot, string kind, string path) =>
        new($"programs.leftover.{slot}@1", null, new Dictionary<string, string> { ["kind"] = kind, ["path"] = path });

    [Fact]
    public void Sobras_do_usuario_vao_para_a_quarentena_e_voltam()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), App);
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "settings.json"), "{\"a\":1}");
        var keyPath = $@"Software\{Vendor}\{App}";
        using (var k = Registry.CurrentUser.CreateSubKey(keyPath + @"\Deep"))
        {
            k.SetValue("Text", "olá", RegistryValueKind.String);
            k.SetValue("Number", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
            k.SetValue("Blob", BlobValue, RegistryValueKind.Binary);
            k.SetValue("List", ListValue, RegistryValueKind.MultiString);
        }
        try
        {
            using var broker = Broker(Path.Combine(_dir, "journal.db"));
            var exec = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute,
                [Leftover("user", "file", folder), Leftover("user", "registry", keyPath)]));
            Assert.True(exec.Ok, exec.Error?.Detail);
            Assert.All(exec.Results!, r => Assert.True(r.Outcome == "Verified", $"{r.ActionKey}: {r.Outcome} {r.Detail}"));
            Assert.False(Directory.Exists(folder));
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));

            var revert = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: exec.RunId));
            Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(folder, "sub", "settings.json")));
            using var back = Registry.CurrentUser.OpenSubKey(keyPath + @"\Deep");
            Assert.NotNull(back);
            Assert.Equal("olá", back.GetValue("Text"));
            Assert.Equal(unchecked((int)0xFFFFFFFF), back.GetValue("Number"));
            Assert.Equal(BlobValue, (byte[])back.GetValue("Blob")!);
            Assert.Equal(ListValue, (string[])back.GetValue("List")!);
            Assert.Equal(RegistryValueKind.MultiString, back.GetValueKind("List"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{Vendor}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Alvos_protegidos_sao_recusados_pelo_broker()
    {
        using var broker = Broker(Path.Combine(_dir, "journal-refused.db"));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exec = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute,
            [Leftover("user", "file", Path.Combine(appData, "Microsoft")), Leftover("user", "registry", @"Software\Microsoft\Windows")]));
        Assert.All(exec.Results ?? [], r => Assert.Contains(r.Outcome, Refused));
        Assert.True(Directory.Exists(Path.Combine(appData, "Microsoft")));
    }

    [Fact]
    public void Sobras_da_maquina_com_administrador()
    {
        if (!Vm.Elevated) return; // a VM do CI roda elevada; fora dela este caso não se aplica
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), App);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "log.txt"), "x");
        var keyPath = $@"SOFTWARE\{Vendor}\{App}";
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var k = hklm.CreateSubKey(keyPath))
            k.SetValue("Path", @"C:\x", RegistryValueKind.ExpandString);
        try
        {
            using var broker = Broker(Path.Combine(_dir, "journal-machine.db"));
            var exec = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute,
                [Leftover("machine", "file", folder), Leftover("machine", "registry", keyPath)]));
            Assert.All(exec.Results!, r => Assert.True(r.Outcome == "Verified", $"{r.ActionKey}: {r.Outcome} {r.Detail}"));
            Assert.False(Directory.Exists(folder));
            var revert = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: exec.RunId));
            Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
            Assert.True(File.Exists(Path.Combine(folder, "log.txt")));
            using var hklm2 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var back = hklm2.OpenSubKey(keyPath);
            Assert.Equal(RegistryValueKind.ExpandString, back!.GetValueKind("Path"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            hklm.DeleteSubKeyTree($@"SOFTWARE\{Vendor}", throwOnMissingSubKey: false);
        }
    }
}
