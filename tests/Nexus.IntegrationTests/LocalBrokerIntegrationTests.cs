using System.Security.Principal;
using Microsoft.Win32;
using Nexus.Broker;
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
