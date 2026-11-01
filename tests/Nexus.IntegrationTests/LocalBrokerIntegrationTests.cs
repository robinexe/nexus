using System.Security.Principal;
using Microsoft.Win32;
using Nexus.Broker;
using Nexus.Core;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Services;

namespace Nexus.IntegrationTests;

/// <summary>
/// Aplicar neste PC sem serviço instalado: o mesmo caminho da interface (BrokerSession.ForThisPc), que inicia o aplicador
/// sob demanda. Nível usuário (sem aviso) e nível administrador (runas; na VM do CI o UAC não pergunta). Só roda na VM liberada.
/// </summary>
[Trait("Category", "LocalBrokerIntegration")]
public sealed class LocalBrokerIntegrationTests
{
    private static readonly string[] Applied = ["Verified", "AlreadyConfigured"];
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;

    public LocalBrokerIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        Vm.Require(); // nada é escrito fora da VM liberada
    }

    private static string? ReadHkcu(string path, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    [Fact]
    public void Whole_catalog_like_the_apply_page_validates_applies_and_reverts()
    {
        // Mesmo caminho da página Aplicar com "tudo marcado": RequestsFor + lotes de 64 + aplicador de administrador.
        // Visto no PC real: uma ação com valor indevido derrubava o pedido inteiro ("não aceita valor").
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        try { p["activeScheme"] = new WindowsPowerBackend().GetActiveScheme().ToString("D"); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        var chosen = catalog.Actions
            .Where(e => e.UserParameters.Count == 0 && !e.Definition.Irreversible)
            .Select(e => (Entry: e, Instance: e.TryInstantiate(p, out _)))
            .Where(x => x.Instance is not null)
            .Select(x => (x.Entry, x.Instance!))
            .ToList();
        _out.WriteLine($"{chosen.Count} ações instanciáveis");
        var requests = BrokerSession.RequestsFor(chosen);
        var session = BrokerSession.ForThisPc(BrokerLevel.Admin, AppContext.BaseDirectory, Sid, m => _out.WriteLine("[sob demanda] " + m));

        var plan = session.Plan(requests);
        _out.WriteLine("[plano] " + plan.Message);
        var refusedInPlan = plan.Response?.Results?.Where(r => r.Outcome == "Refused").ToList() ?? [];
        Assert.True(refusedInPlan.Count == 0, "recusadas na validação: " + string.Join("; ", refusedInPlan.Select(r => r.ActionKey + ": " + r.Detail)));

        var exec = session.Execute(requests);
        _out.WriteLine("[aplicar] " + exec.Message);
        foreach (var g in (exec.Response?.Results ?? []).GroupBy(r => r.Outcome)) _out.WriteLine($"  {g.Key}: {g.Count()} ({string.Join(", ", g.Take(6).Select(r => r.ActionKey))})");
        Assert.True(exec.Ok, exec.Message);
        Assert.DoesNotContain(exec.Response!.Results!, r => r.Outcome == "Refused");
        Assert.True(exec.AllRunIds.Count >= 2, "mais de 64 ações = vários lotes");

        foreach (var run in exec.AllRunIds.Reverse())
        {
            var revert = session.RevertRun(run);
            _out.WriteLine($"[desfazer {run}] " + revert.Message);
            Assert.True(revert.Ok, revert.Message);
        }
    }

    [Fact]
    public void User_level_on_demand_broker_applies_and_reverts_in_hkcu()
    {
        const string path = @"Software\Microsoft\GameBar", name = "AutoGameModeEnabled";
        var before = ReadHkcu(path, name);
        var session = BrokerSession.ForThisPc(BrokerLevel.User, AppContext.BaseDirectory, Sid, m => _out.WriteLine("[sob demanda] " + m));

        var status = session.Status();
        _out.WriteLine("[usuário] " + status.Message);
        Assert.True(status.Ok, status.Message);
        Assert.Contains("autorizada pelo usuário", status.Message, StringComparison.Ordinal);

        var exec = session.Execute([new ActionRequest("win.gamemode.enable@1")]);
        _out.WriteLine("[usuário] " + exec.Message);
        Assert.True(exec.Ok, exec.Message);
        Assert.Equal("1", ReadHkcu(path, name));
        // Processo elevado (o runner do CI é administrador): o pedido "de usuário" vai para o aplicador de administrador,
        // com diário protegido em %ProgramData%; nunca um aplicador elevado gravando em %LocalAppData%.
        var level = LocalBroker.EffectiveLevel(BrokerLevel.User, LocalBroker.CurrentProcessIsElevated());
        Assert.True(File.Exists(LocalBroker.JournalPath(level)), "diário em " + LocalBroker.JournalPath(level));

        var revert = session.RevertRun(exec.Response!.RunId!.Value);
        Assert.True(revert.Ok && revert.Response!.FullyRestored == true, revert.Message);
        Assert.Equal(before, ReadHkcu(path, name));
    }

    [Fact]
    public void Admin_level_on_demand_broker_applies_system_change_with_restore_point_attempt_and_reverts()
    {
        var svc = new WindowsServiceBackend();
        var before = svc.GetStartType("SysMain");
        Assert.NotNull(before);
        var session = BrokerSession.ForThisPc(BrokerLevel.Admin, AppContext.BaseDirectory, Sid, m => _out.WriteLine("[sob demanda] " + m));

        var exec = session.Execute([new ActionRequest("svc.sysmain.manual.lab@1")]);
        _out.WriteLine("[admin] " + exec.Message);
        Assert.True(exec.Ok, exec.Message);
        var outcome = Assert.Single(exec.Response!.Results!).Outcome;
        Assert.Contains(outcome, Applied);
        if (outcome == "Verified")
        {
            Assert.Contains("onto de restauração", exec.Message, StringComparison.Ordinal); // criado, ou o motivo de não ter sido
            Assert.Equal("Manual", svc.GetStartType("SysMain"));
        }
        Assert.True(File.Exists(LocalBroker.JournalPath(BrokerLevel.Admin)), "diário da máquina em %ProgramData%\\NEXUS");

        var revert = session.RevertRun(exec.Response!.RunId!.Value);
        Assert.True(revert.Ok && revert.Response!.FullyRestored == true, revert.Message);
        Assert.Equal(before, svc.GetStartType("SysMain"));
    }
}
