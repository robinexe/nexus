using System.Diagnostics;
using Microsoft.Win32;
using Nexus.Boot;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Startup;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Módulo Inicialização com handlers REAIS, pelo broker e pelo catálogo real (catalog/boot.v1.json, services.v1.json).
/// Só em VM liberada: o portão é avaliado antes de qualquer escrita. Cada teste cria o próprio alvo (valor Run, atalho, tarefa)
/// e confere que Desfazer devolve exatamente o estado anterior.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class BootIntegrationTests : IDisposable
{
    private static readonly string[] TaskOutcomes = ["Verified", "Blocked"];
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private readonly string _dir;
    private readonly WindowsRegistryBackend _reg = new();

    public BootIntegrationTests()
    {
        Vm.Require(); // nada é criado antes do portão
        _dir = Directory.CreateTempSubdirectory("nexus-vm-boot-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private (BrokerService Broker, SqliteJournal Journal) Broker()
    {
        var path = Path.Combine(_dir, "journal.db");
        var journal = new SqliteJournal(path);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var handlers = new HandlerRegistry().Add(new StartupApprovedHandler(_reg));
        foreach (var h in BootHandlers.Create(new HandlerEnvironment(_reg, _dir, false, Vm.Elevated))) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return (new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(path), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>()), journal);
    }

    private static BrokerRequest Exec(string id, string? value, Dictionary<string, string>? args) =>
        new(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest(id, value, args)]);

    private static BrokerRequest Revert(Guid? run) => new(1, Guid.NewGuid(), BrokerOperation.RevertRun, null, run);

    [Fact]
    public void Orphan_run_entry_is_removed_and_recreated_exactly()
    {
        const string name = "NexusVmOrfa";
        using (var k = Registry.CurrentUser.CreateSubKey(Run)) k.SetValue(name, @"""C:\NexusNaoExiste\fantasma.exe"" /tray", RegistryValueKind.ExpandString);
        try
        {
            var (broker, journal) = Broker();
            using (broker) using (journal)
            {
                var r = broker.Handle(Exec("boot.orphan.user.run@1", null, new() { ["entryName"] = name }));
                Assert.True(r.Ok, r.Error?.Detail);
                Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
                using (var k = Registry.CurrentUser.OpenSubKey(Run)) Assert.Null(k!.GetValue(name));

                var back = broker.Handle(Revert(r.RunId));
                Assert.True(back.Ok && back.FullyRestored == true, back.Error?.Detail);
                using var k2 = Registry.CurrentUser.OpenSubKey(Run)!;
                Assert.Equal(RegistryValueKind.ExpandString, k2.GetValueKind(name));
                Assert.Equal(@"""C:\NexusNaoExiste\fantasma.exe"" /tray", k2.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            }
        }
        finally { using var k = Registry.CurrentUser.OpenSubKey(Run, true); k?.DeleteValue(name, false); }
    }

    [Fact]
    public void Startup_folder_shortcut_is_disabled_by_flag_and_restored()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(folder);
        const string file = "NexusVmTeste.lnk";
        var full = Path.Combine(folder, file);
        File.WriteAllBytes(full, [0x4C, 0, 0, 0]); // conteúdo irrelevante: o handler só grava a marca do Gerenciador de Tarefas
        try
        {
            var (broker, journal) = Broker();
            using (broker) using (journal)
            {
                var r = broker.Handle(Exec("boot.startupfolder.user@1", "disabled", new() { ["fileName"] = file }));
                Assert.True(r.Ok, r.Error?.Detail);
                Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
                using (var k = Registry.CurrentUser.OpenSubKey(Approved + @"\StartupFolder")) Assert.Equal((byte)3, ((byte[])k!.GetValue(file)!)[0]);
                Assert.True(File.Exists(full));
                var back = broker.Handle(Revert(r.RunId));
                Assert.True(back.Ok && back.FullyRestored == true, back.Error?.Detail);
                using var k2 = Registry.CurrentUser.OpenSubKey(Approved + @"\StartupFolder");
                Assert.Null(k2?.GetValue(file));
            }
        }
        finally { File.Delete(full); }
    }

    [Fact]
    public void Logon_task_is_disabled_and_reenabled()
    {
        const string task = @"\NexusVmTarefa";
        var schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        static int RunProcess(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(30_000);
            return p.ExitCode;
        }
        Assert.Equal(0, RunProcess(schtasks, "/create", "/tn", task, "/tr", @"C:\Windows\System32\notepad.exe", "/sc", "onlogon", "/f"));
        try
        {
            var (broker, journal) = Broker();
            using (broker) using (journal)
            {
                var r = broker.Handle(Exec("boot.task.admin@1", "disabled", new() { ["taskPath"] = task }));
                Assert.True(r.Ok, r.Error?.Detail);
                Assert.Contains(Assert.Single(r.Results!).Outcome, TaskOutcomes); // Blocked = VM sem elevação
                if (r.Results![0].Outcome != "Verified") return;
                Assert.False(new Nexus.Boot.Windows.WindowsTaskScheduler().Find(task)!.Enabled);
                var back = broker.Handle(Revert(r.RunId));
                Assert.True(back.Ok && back.FullyRestored == true, back.Error?.Detail);
                Assert.True(new Nexus.Boot.Windows.WindowsTaskScheduler().Find(task)!.Enabled);
            }
        }
        finally { RunProcess(schtasks, "/delete", "/tn", task, "/f"); }
    }

    [Fact]
    public void Forbidden_service_is_refused_without_writing()
    {
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var before = new Nexus.WindowsActions.Services.WindowsServiceBackend().GetStartType("RpcSs");
            var r = broker.Handle(Exec(ServicePresets.IndividualActionKey, "Manual", new() { ["service"] = "RpcSs" }));
            Assert.True(!r.Ok || r.Results!.All(x => x.Outcome is "Blocked" or "NotApplicable"), "RpcSs não pode ser alterado");
            Assert.Equal(before, new Nexus.WindowsActions.Services.WindowsServiceBackend().GetStartType("RpcSs"));
        }
    }

    [Fact]
    public void Real_inventory_and_boot_report_do_not_throw()
    {
        var report = Nexus.Boot.Windows.WindowsBootEventReader.Read();
        var inv = new Nexus.Boot.Inventory.StartupInventory(new Nexus.Boot.Windows.WindowsBootRegistryReader(), new Nexus.Boot.Windows.WindowsFileInspector(),
                Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .Read(Nexus.Boot.Windows.WindowsStartupFolders.Current(), new Nexus.Boot.Windows.WindowsTaskScheduler(), new Nexus.Boot.Windows.WindowsServiceReader(), report);
        Assert.NotNull(inv);
        Assert.NotEmpty(new Nexus.Boot.Windows.WindowsServiceReader().All());
        Assert.True(report.Unavailable is not null || report.Boots.Count > 0);
    }
}
