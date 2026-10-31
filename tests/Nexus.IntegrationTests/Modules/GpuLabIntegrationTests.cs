using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Lab.Gpu;
using Nexus.Lab.Gpu.Windows;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Laboratório da placa de vídeo com APIs REAIS numa VM descartável: a leitura (DXGI, SetupAPI, D3DKMT, QueryDisplayConfig)
/// não pode lançar exceção, e as duas ações do catálogo gpulab passam pelo broker com Desfazer exato.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class GpuLabIntegrationTests : IDisposable
{
    private const string Prefs = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private readonly string _dir;
    private readonly WindowsRegistryBackend _reg = new();

    public GpuLabIntegrationTests()
    {
        Vm.Require();
        _dir = Directory.CreateTempSubdirectory("nexus-vm-gpulab-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private (BrokerService Broker, SqliteJournal Journal) Broker()
    {
        var path = Path.Combine(_dir, "journal.db");
        var journal = new SqliteJournal(path);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var handlers = new HandlerRegistry().Add(new DirectXUserGpuPreferenceHandler(_reg));
        foreach (var h in GpuLabHandlers.Create(new HandlerEnvironment(_reg, _dir, false, Vm.Elevated))) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        return (new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(path), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>()), journal);
    }

    private static BrokerRequest Exec(string id, Dictionary<string, string> args) =>
        new(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest(id, null, args)]);

    private static BrokerRequest Revert(Guid? run) => new(1, Guid.NewGuid(), BrokerOperation.RevertRun, null, run);

    [Fact]
    public void Scan_reads_the_machine_without_throwing()
    {
        using var nvml = NvmlSession.TryOpen(out var status);
        var report = GpuLabScanner.Scan(nvml, status, includeGames: true);
        Assert.NotEmpty(report.Adapters); // no mínimo o Microsoft Basic Render Driver
        Assert.All(report.Monitors, m => Assert.True(DisplayModes.IsValidDevice(m.Device)));
        _ = GpuDiagnostics.Evaluate(report, DateOnly.FromDateTime(DateTime.Now));
    }

    [Fact]
    public void App_gpu_preference_is_written_and_removed_exactly()
    {
        const string exe = @"C:\NexusVm\Jogo Teste\jogo.exe";
        using (var k = Registry.CurrentUser.CreateSubKey(Prefs)) k.DeleteValue(exe, throwOnMissingValue: false);
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            var r = broker.Handle(Exec(GpuDiagnostics.AppPreferenceAction, new() { ["exePath"] = exe }));
            Assert.True(r.Ok, r.Error?.Detail);
            Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
            using (var k = Registry.CurrentUser.OpenSubKey(Prefs)) Assert.Equal("GpuPreference=2;", k!.GetValue(exe));

            var back = broker.Handle(Revert(r.RunId));
            Assert.True(back.Ok && back.FullyRestored == true, back.Error?.Detail);
            using var k2 = Registry.CurrentUser.OpenSubKey(Prefs);
            Assert.Null(k2?.GetValue(exe));
        }
    }

    [Fact]
    public void Display_mode_refuses_unlisted_and_round_trips_a_listed_refresh()
    {
        var backend = new WindowsDisplayModeBackend();
        var report = GpuLabScanner.Scan(null, "", includeGames: false);
        var monitor = report.Monitors.FirstOrDefault();
        if (monitor is null) return; // VM sem área de trabalho ativa: só a leitura foi testada acima
        var (broker, journal) = Broker();
        using (broker) using (journal)
        {
            // Modo que nenhum monitor lista: recusado antes de qualquer troca.
            var bad = broker.Handle(Exec(GpuDiagnostics.DisplayModeAction, new() { ["device"] = monitor.Device, ["mode"] = "16000x9000@499" }));
            Assert.True(bad.Results is null || bad.Results.All(x => x.Outcome != "Verified"));
            Assert.Equal(monitor.Current.Key, backend.Current(monitor.Device)!.Key);

            var other = backend.Modes(monitor.Device)
                .Where(m => m.SameResolution(monitor.Current) && m.Hz != monitor.Current.Hz && !m.Interlaced && m.BitsPerPixel == monitor.Current.BitsPerPixel)
                .OrderByDescending(m => m.Hz).FirstOrDefault();
            if (other is null) return; // adaptador virtual com uma taxa só
            var r = broker.Handle(Exec(GpuDiagnostics.DisplayModeAction, new() { ["device"] = monitor.Device, ["mode"] = other.Key }));
            Assert.True(r.Ok, r.Error?.Detail);
            Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
            Assert.Equal(other.Hz, backend.Current(monitor.Device)!.Hz);
            var back = broker.Handle(Revert(r.RunId));
            Assert.True(back.Ok && back.FullyRestored == true, back.Error?.Detail);
            Assert.Equal(monitor.Current.Hz, backend.Current(monitor.Device)!.Hz);
        }
    }
}
