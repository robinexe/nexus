using Nexus.Core;
using Nexus.WindowsActions.Network;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>QoS por jogo (DSCP 46): política completa, recusa política alheia, desfazer apaga tudo; placa de rede pelo caminho fechado.</summary>
[Trait("Category", "Unit")]
public sealed class QosAndNicTests
{
    private static ActionInstance Qos(string exe)
    {
        var e = Wiring.Entry("net.qos.game@1") ?? throw new InvalidOperationException("ação ausente");
        return e.TryInstantiate(new Dictionary<string, string> { ["qosExe"] = exe }, out var why) ?? throw new InvalidOperationException(why);
    }

    private static RegistryTarget V(string exe, string name) => new(RegHive.HKLM, QosGamePolicyHandler.KeyPath(exe), name, RegView.Registry64);

    [Fact]
    public void Qos_policy_is_written_whole_and_undo_removes_it()
    {
        var reg = new InMemoryRegistryBackend();
        var h = new QosGamePolicyHandler(reg);
        var a = Qos("cs2.exe");
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var original = h.Detect(a);
        Assert.False(original.Exists);
        h.Apply(a);
        Assert.Equal("cs2.exe", reg.Read(V("cs2.exe", "Application Name")).Value);
        Assert.Equal("46", reg.Read(V("cs2.exe", "DSCP Value")).Value);
        Assert.Equal("-1", reg.Read(V("cs2.exe", "Throttle Rate")).Value);
        Assert.Equal("46", h.Detect(a).Value);
        Assert.Null(h.CheckRestore(a, original));
        h.Restore(a, original);
        Assert.False(reg.Read(V("cs2.exe", "DSCP Value")).ValueExists);
        Assert.False(reg.Read(V("cs2.exe", "Version")).ValueExists);
    }

    [Fact]
    public void Someone_elses_policy_or_a_path_in_the_exe_is_refused()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(V("cs2.exe", "Application Name"), "String", "outro.exe");
        var h = new QosGamePolicyHandler(reg);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Qos("cs2.exe"), T.Ctx).Kind);
        var e = Wiring.Entry("net.qos.game@1")!;
        Assert.Null(e.ValidateUserArguments(new Dictionary<string, string> { ["qosExe"] = @"C:\x\cs2.exe" }, out _));
    }

    [Fact]
    public void Nic_options_only_target_the_network_adapter_class()
    {
        var e = Wiring.Entry("net.nic.eee.off@1")!;
        const string ok = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0007";
        Assert.NotNull(e.ValidateUserArguments(new Dictionary<string, string> { ["nicPath"] = ok }, out _));
        foreach (var bad in new[] { @"SYSTEM\CurrentControlSet\Services\Tcpip", ok + @"\..\0001", @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000" })
            Assert.Null(e.ValidateUserArguments(new Dictionary<string, string> { ["nicPath"] = bad }, out _));
    }
}
