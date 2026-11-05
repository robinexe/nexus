using System.Net;
using System.Net.Sockets;
using Nexus.Core;
using Nexus.NetRoute.Discovery;
using Nexus.NetRoute.Handlers;
using Nexus.NetRoute.Relay;
using Nexus.NetRoute.Windows;
using Nexus.NetRoute.WireGuard;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Rota de jogo com o Windows REAL, numa VM descartável do CI:
/// <list type="bullet">
/// <item>ETW (só leitura): a descoberta enxerga pacotes UDP deste processo com o PID e o endereço remoto certos — confere o
/// formato dos eventos do Microsoft-Windows-Kernel-Network que o parser supõe.</item>
/// <item>WireGuard oficial de verdade: baixa e instala (SHA-256 + Authenticode), cria a chave, cadastra um relay, liga o serviço do
/// túnel, relê e desfaz tudo na ordem inversa. O relay do teste não existe (o serviço sobe mesmo sem handshake), e os blocos
/// do túnel são de teste: nada sai da VM além de pacotes de handshake sem resposta.</item>
/// </list>
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class NetRouteIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Etw_discovery_sees_this_process_sending_udp()
    {
        Vm.Require();
        var target = IPAddress.Parse("198.51.100.7"); // documentação (RFC 5737): ninguém responde
        var tally = new ConnectionTally();
        using var observer = new EtwConnectionObserver();
        observer.Start(tally.Add);
        try
        {
            Thread.Sleep(1500); // a sessão leva um instante para começar a entregar eventos
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var payload = new byte[64];
            for (var i = 0; i < 40; i++)
            {
                udp.Send(payload, payload.Length, new IPEndPoint(target, 9));
                Thread.Sleep(25);
            }
            Thread.Sleep(2500);
        }
        finally { observer.StopObserving(); }
        output.WriteLine($"eventos lidos: {observer.Parsed}, ignorados: {observer.Ignored}, total na contagem: {tally.EventsSeen}");
        var mine = tally.Flows(pid => pid == Environment.ProcessId);
        foreach (var f in mine.Take(10)) output.WriteLine($"{f.Protocol} {f.Remote}:{f.RemotePort} pacotes={f.Packets} saída={f.OutboundPackets}");
        var flow = Assert.Single(mine, f => f.Protocol == FlowProtocol.Udp && f.Remote.Equals(target) && f.RemotePort == 9);
        Assert.True(flow.OutboundPackets >= 30, $"só {flow.OutboundPackets} pacotes vistos de 40");
    }

    [Fact]
    public void Official_wireguard_client_identity_relay_and_tunnel_apply_and_undo()
    {
        Vm.Require();
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            output.WriteLine("Fora do GitHub Actions: este teste instala o WireGuard e só roda na VM do CI.");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "nexus-netroute-it-" + Guid.NewGuid().ToString("N"));
        var store = new FileNetRouteStore(dir, NetRouteAcl.ProtectSecretFile, NetRouteAcl.ProtectDirectory);
        var sys = new WindowsNetRouteSystem(dir);
        var handlers = NetRouteHandlersFactory.Create(store, sys).ToDictionary(h => h.HandlerKind);
        var ctx = new SystemContext(Vm.Facts.WindowsBuild ?? 0, "vm", null, Vm.Elevated);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        ActionInstance Inst(string id, Dictionary<string, string>? args = null) =>
            catalog.Actions.Single(a => a.Definition.Id == id).TryInstantiate(args ?? new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);

        var client = Inst("netroute.client.install");
        var identity = Inst("netroute.identity.create");
        var hc = handlers[NetRouteClientHandler.Kind];
        var hi = handlers[NetRouteIdentityHandler.Kind];
        var hr = handlers[NetRouteRelayHandler.Kind];
        var ht = handlers[NetRouteTunnelHandler.Kind];
        var clientBefore = hc.Detect(client);
        var identityBefore = hi.Detect(identity);
        StateSnapshot? relayBefore = null, tunnelBefore = null;
        var cleanup = new List<string>();
        ActionInstance? relay = null, tunnel = null;
        try
        {
            Assert.Equal(ApplicabilityKind.Applicable, hc.Assess(client, ctx).Kind);
            output.WriteLine($"WireGuard antes: {clientBefore.Describe()}; baixando e instalando o oficial...");
            hc.Apply(client);
            output.WriteLine("WireGuard instalado.");
            Assert.True(hc.Detect(client).SameValueAs(client.Desired), "WireGuard não ficou instalado/assinado");
            Assert.True(WindowsNetRouteSystem.SignedByWireGuard(WindowsNetRouteSystem.WireGuardExe));

            hi.Apply(identity);
            var pub = store.ReadPublicKey()!;
            output.WriteLine($"chave pública do teste: {pub}");

            var serverKey = WireGuardKey.GenerateKeyPair().PublicKey;
            var link = new RelayLink("Relay CI", "1.1.1.1", 51820, serverKey, IPAddress.Parse("10.66.0.7"), IPAddress.Parse("10.66.0.1"), 8787, "sao-paulo",
                RelayLink.FingerprintOf(pub));
            relay = Inst("netroute.relay.add", new() { ["link"] = link.ToLink() });
            Assert.Equal(ApplicabilityKind.Applicable, hr.Assess(relay, ctx).Kind);
            relayBefore = hr.Detect(relay);
            hr.Apply(relay);

            tunnel = Inst("netroute.tunnel.on", new() { ["relay"] = link.Id, ["game"] = "valorant", ["prefixes"] = "1.0.0.0/24" });
            var assess = ht.Assess(tunnel, ctx);
            Assert.True(assess.Kind == ApplicabilityKind.Applicable, assess.Reason);
            tunnelBefore = ht.Detect(tunnel);
            Assert.False(tunnelBefore.Exists);
            ht.Apply(tunnel);
            Assert.True(ht.Detect(tunnel).SameValueAs(tunnel.Desired), "serviço do túnel não ficou em execução com a configuração pedida");
            Assert.Equal(ServiceControl.Running, ServiceControl.State(WindowsNetRouteSystem.ServiceName));
            Assert.Contains("AllowedIPs = 10.66.0.1/32, 1.0.0.0/24", File.ReadAllText(sys.ConfigPath), StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            output.WriteLine("FALHA: " + ex);
            throw;
        }
        finally
        {
            // A limpeza nunca esconde a falha original: cada passo é tentado e o erro dele só aparece no log.
            void Undo(string what, Action step)
            {
                try { step(); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
                {
                    cleanup.Add($"{what}: {ex.Message}");
                    output.WriteLine($"limpeza ({what}): {ex}");
                }
            }
            if (tunnel is not null && tunnelBefore is not null) Undo("túnel", () => ht.Restore(tunnel, tunnelBefore));
            if (relay is not null && relayBefore is not null) Undo("relay", () => hr.Restore(relay, relayBefore));
            Undo("chave", () => hi.Restore(identity, identityBefore));
            Undo("cliente", () => hc.Restore(client, clientBefore));
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Assert.Empty(cleanup);
        Assert.Null(ServiceControl.State(WindowsNetRouteSystem.ServiceName));
        Assert.Null(store.ReadIdentity());
        Assert.False(File.Exists(WindowsNetRouteSystem.WireGuardExe) && !clientBefore.Exists, "desfazer não removeu o WireGuard instalado pelo teste");
    }
}
