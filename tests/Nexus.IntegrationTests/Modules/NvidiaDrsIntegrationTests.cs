using Nexus.Broker;
using Nexus.Core;
using Nexus.Lab.Gpu;
using Nexus.Lab.Gpu.Windows;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Ajustes do driver NVIDIA (DRS) numa VM descartável do GitHub Actions, que NÃO tem placa NVIDIA: carregar a NVAPI não pode
/// derrubar o processo, e as ações do catálogo nvidia passam pelo broker terminando como "não se aplica" (ou bloqueadas sem
/// administrador) sem gravar nada. Numa máquina COM NVIDIA o teste só confere a leitura (não grava no driver de quem roda).
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class NvidiaDrsIntegrationTests : IDisposable
{
    private readonly string _dir;

    public NvidiaDrsIntegrationTests()
    {
        Vm.Require();
        _dir = Directory.CreateTempSubdirectory("nexus-vm-nvdrs-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void Without_nvidia_the_actions_are_not_applicable_and_nothing_is_written()
    {
        var drs = new WindowsNvidiaDrs();
        var unavailable = drs.Unavailable();
        Assert.Equal(unavailable, drs.Unavailable()); // segunda chamada: mesmo resultado, sem recarregar
        if (unavailable is null)
        {
            // Máquina com NVIDIA: só leitura (perfil global e um executável que não existe em perfil nenhum).
            _ = drs.Read(DrsTarget.Base, NvidiaDrsSettings.ShaderCacheMaxSize);
            Assert.False(drs.Read(DrsTarget.App("nexus-vm-nao-existe.exe"), NvidiaDrsSettings.PrerenderLimit).ProfileFound);
            return;
        }
        Assert.StartsWith("Sem placa/driver NVIDIA", unavailable, StringComparison.Ordinal);
        Assert.Throws<NvidiaDrsException>(() => drs.Read(DrsTarget.Base, NvidiaDrsSettings.ShaderCacheMaxSize));

        var path = Path.Combine(_dir, "journal.db");
        using var journal = new SqliteJournal(path);
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        var reg = new WindowsRegistryBackend();
        var handlers = new HandlerRegistry();
        foreach (var h in GpuLabHandlers.Create(new HandlerEnvironment(reg, _dir, false, Vm.Elevated))) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        using var broker = new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(path), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>());
        var exe = new Dictionary<string, string> { ["exe"] = "cs2.exe" };
        var r = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute,
        [
            new ActionRequest(NvidiaDrsActions.LowLatency, null, exe),
            new ActionRequest(NvidiaDrsActions.FpsCap, "141", exe),
            new ActionRequest(NvidiaDrsActions.ShaderCache, "102400"),
        ]));
        // Nada verificado/gravado: cada ação termina "não se aplica" (VM elevada) ou bloqueada (sem administrador).
        Assert.True(r.Results is null || r.Results.All(x => x.Outcome is "NotApplicable" or "Blocked"),
            string.Join("; ", r.Results?.Select(x => x.Outcome + ": " + x.Detail) ?? [r.Error?.Detail ?? ""]));
        if (Vm.Elevated && r.Results is not null)
            Assert.All(r.Results, x => Assert.Equal("NotApplicable", x.Outcome));
    }
}
