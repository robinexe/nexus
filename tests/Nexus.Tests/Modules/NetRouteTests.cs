using System.Net;
using Nexus.Core;
using Nexus.NetRoute;
using Nexus.NetRoute.Analysis;
using Nexus.NetRoute.Decision;
using Nexus.NetRoute.Discovery;
using Nexus.NetRoute.Geo;
using Nexus.NetRoute.Handlers;
using Nexus.NetRoute.Ip;
using Nexus.NetRoute.Probe;
using Nexus.NetRoute.Profiles;
using Nexus.NetRoute.Relay;
using Nexus.NetRoute.Stats;
using Nexus.NetRoute.Windows;
using Nexus.NetRoute.WireGuard;

namespace Nexus.Tests.Modules;

/// <summary>Rota de jogo (docs/modules/netroute.md): lógica pura, handlers com backends falsos, catálogo e formato do link.</summary>
[Trait("Category", "Unit")]
public sealed class NetRouteTests
{
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly SystemContext Admin = new(26100, "cpu", null, IsElevated: true);

    // Mesmo vetor de tools/netroute-relay/test_nexus_relay.py.
    private const string ServerKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
    private const string ClientKey = "ZWZnaGlqa2xtbm9wcXJzdHV2d3h5ent8fX5/gIGCg4Q=";
    private const string Vector = "nexus-relay://v1/AQECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8g_yKIUFeVIEcKQgAHCkIAASJTymwLNDUuNzkuMTAuMjANRm9ydGFsZXphIFZQUwlmb3J0YWxlemE";

    // ================================================================== blocos de endereço

    [Theory]
    [InlineData("45.79.10.20/24", "45.79.10.0/24")]
    [InlineData("45.79.10.20", "45.79.10.20/32")]
    [InlineData("2606:4700::1111/48", "2606:4700::/48")]
    public void Prefix_parses_and_normalizes(string text, string expected) => Assert.Equal(expected, IpPrefix.Parse(text).ToString());

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4/33")]
    [InlineData(" 1.2.3.4")]
    [InlineData("1.2.3.4/")]
    [InlineData("fe80::1%3")]
    [InlineData("::ffff:1.2.3.4")]
    public void Prefix_rejects_malformed(string text) => Assert.False(IpPrefix.TryParse(text, out _));

    [Theory]
    [InlineData("10.0.0.0/8", false)]
    [InlineData("192.168.0.0/24", false)]
    [InlineData("100.64.1.0/24", false)]
    [InlineData("169.254.169.254/32", false)]
    [InlineData("8.0.0.0/7", false)] // não contém privado, mas é largo demais (testado na lista)
    [InlineData("172.0.0.0/8", false)] // cobre 172.16/12
    [InlineData("45.79.10.0/24", true)]
    public void Only_public_prefixes(string text, bool expected)
    {
        var p = IpPrefix.Parse(text);
        if (text == "8.0.0.0/7") Assert.True(PublicAddress.IsPublic(p));
        else Assert.Equal(expected, PublicAddress.IsPublic(p));
    }

    [Fact]
    public void Prefix_list_validates_and_canonicalizes()
    {
        var (ok, problem) = PrefixList.Parse("45.79.10.7/32,45.79.10.0/24,18.228.0.0/16");
        Assert.Null(problem);
        Assert.Equal("18.228.0.0/16,45.79.10.0/24", PrefixList.Format(ok!));
        Assert.Empty(PrefixList.Parse("-").Prefixes!);
        Assert.Contains("largo demais", PrefixList.Parse("8.0.0.0/7").Problem);
        Assert.Contains("não público", PrefixList.Parse("45.79.10.0/24,192.168.1.0/24").Problem);
        Assert.NotNull(PrefixList.Parse("45.79.10.0/24,lixo").Problem);
    }

    [Fact]
    public void Fit_to_parameter_respects_the_catalog_limit_and_priority()
    {
        var many = Enumerable.Range(1, 60).Select(i => IpPrefix.Parse($"45.{i}.{i}.{i}/32")).ToList();
        var fit = PrefixList.FitToParameter(many);
        var text = PrefixList.Format(fit);
        Assert.True(text.Length <= PrefixList.MaxParameterLength);
        Assert.True(fit.Count <= 16);
        Assert.All(fit, p => Assert.Equal(24, p.Length)); // agrupou em /24 antes de cortar
        Assert.Contains(fit, p => p.Contains(IPAddress.Parse("45.1.1.1"))); // o primeiro (mais visto) ficou
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "netroute.tunnel.on");
        Assert.NotNull(entry.ValidateUserArguments(new Dictionary<string, string> { ["relay"] = "rb01198f80d", ["game"] = "valorant", ["prefixes"] = text }, out var why));
        Assert.Null(why);
    }

    // ================================================================== régua física

    [Fact]
    public void Distances_and_floor_match_the_report()
    {
        var sp = Places.Find("sao-paulo")!;
        var fortaleza = Places.Find("Fortaleza")!;
        var miami = Places.Find("miami")!;
        Assert.InRange(PhysicsFloor.DistanceKm(sp, fortaleza), 2300, 2450);
        Assert.InRange(PhysicsFloor.DistanceKm(sp, miami), 6450, 6650);
        Assert.InRange(PhysicsFloor.FloorMs(PhysicsFloor.DistanceKm(sp, miami)), 64, 67); // medido: ~74 ms (hatsnet)
        Assert.Equal(0, PhysicsFloor.ExcessMs(40, PhysicsFloor.DistanceKm(sp, fortaleza)));
        Assert.InRange(PhysicsFloor.ExcessMs(80, PhysicsFloor.DistanceKm(sp, fortaleza)), 25, 30);
        Assert.True(PhysicsFloor.ImpossiblyFast(5, PhysicsFloor.DistanceKm(sp, fortaleza)));
        Assert.Equal("São Paulo", Places.Find("sao paulo")!.Name);
    }

    [Theory]
    [InlineData("ae-1.gru3.example.net", "sao-paulo")]
    [InlineData("be2.for01.isp.com.br", "fortaleza")]
    [InlineData("xe-0-0-1.mia10.carrier.net", "miami")]
    [InlineData("core.saopaulo.example", "sao-paulo")]
    [InlineData("for.example.com", null)] // "for" solto é inglês
    [InlineData("the-core.example.com", null)]
    [InlineData(null, null)]
    public void Router_names_suggest_cities(string? host, string? expected) => Assert.Equal(expected, Places.FromHostName(host)?.Id);

    // ================================================================== estatística

    [Fact]
    public void Stats_percentiles_jitter_and_loss()
    {
        var s = LatencyStats.From([10, 12, null, 11, 50, 10, 11, 12, 10, 11]);
        Assert.Equal(10, s.Sent);
        Assert.Equal(9, s.Received);
        Assert.Equal(10, s.LossPercent, 3);
        Assert.Equal(11, s.P50Ms);
        Assert.Equal(50, s.P99Ms);
        Assert.Equal(10, s.MinMs);
        Assert.True(s.JitterMs > 5);
        Assert.False(LatencyStats.From([null, null]).HasData);
    }

    [Fact]
    public void Chained_paths_add_rtt_and_compose_loss()
    {
        var a = LatencyStats.From([10, 10, 10, 10, null, 10, 10, 10, 10, 10]); // 10% perda
        var b = LatencyStats.From([20, 20, 20, 20, 20, 20, 20, 20, 20, 20]);
        var c = LatencyStats.Chain(a, b);
        Assert.Equal(30, c.P50Ms);
        Assert.Equal(10, c.LossPercent, 1);
        Assert.False(LatencyStats.Chain(a, LatencyStats.From([null])).HasData);
    }

    // ================================================================== diagnóstico

    private static TraceResult Trace(params (string? Ip, string? Host, double? Ms)[] hops) =>
        new(IPAddress.Parse("45.79.10.20"),
            hops.Select((h, i) => new TraceHop(i + 1, h.Ip is null ? null : IPAddress.Parse(h.Ip), h.Host, [h.Ms, h.Ms, h.Ms])).ToList(), ReachedTarget: true);

    [Fact]
    public void Diagnosis_flags_detour_abroad_and_high_excess()
    {
        var user = Places.Find("fortaleza");
        var trace = Trace(("192.168.0.1", null, 1), ("100.64.0.1", null, 4), ("200.1.1.1", "be1.for01.isp.com.br", 6),
            ("200.1.1.2", "xe-1.mia01.transit.net", 62), ("200.1.1.3", "xe-2.mia02.transit.net", 63), ("45.79.10.1", "ae1.gru3.host.net", 118), ("45.79.10.20", null, 120));
        var direct = LatencyStats.From(Enumerable.Repeat<double?>(120, 20).ToList());
        var r = RouteDiagnosis.Diagnose(IPAddress.Parse("45.79.10.20"), user, Places.Find("sao-paulo"), direct, trace);
        Assert.Equal(RouteVerdict.HighExcess, r.Verdict);
        Assert.True(r.ExcessMs > 60);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.DetourAbroad && f.Text.Contains("Miami", StringComparison.Ordinal));
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.BigJump);
        Assert.Contains("desvio", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnosis_says_good_route_when_near_the_floor()
    {
        var direct = LatencyStats.From(Enumerable.Repeat<double?>(38, 20).ToList());
        var r = RouteDiagnosis.Diagnose(IPAddress.Parse("45.79.10.20"), Places.Find("fortaleza"), Places.Find("sao-paulo"), direct, null);
        Assert.Equal(RouteVerdict.Good, r.Verdict);
        Assert.Contains("Nenhum relay", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnosis_drops_the_ruler_when_the_server_city_is_impossible()
    {
        var direct = LatencyStats.From(Enumerable.Repeat<double?>(4, 20).ToList());
        var r = RouteDiagnosis.Diagnose(IPAddress.Parse("45.79.10.20"), Places.Find("fortaleza"), Places.Find("sao-paulo"), direct, null);
        Assert.Contains(r.Findings, f => f.Kind == FindingKind.ServerPlaceImpossible);
        Assert.Equal(RouteVerdict.UnknownServerPlace, r.Verdict);
    }

    [Fact]
    public void Diagnosis_ignores_mid_path_icmp_loss_but_flags_loss_that_reaches_the_target()
    {
        var hops = new List<TraceHop>
        {
            new(1, IPAddress.Parse("192.168.0.1"), null, [1, 1, 1]),
            new(2, IPAddress.Parse("200.1.1.1"), null, [null, null, 5]), // ICMP limitado no meio: não conta (o salto 3 responde tudo)
            new(3, IPAddress.Parse("200.1.1.2"), "x", [30, 30, 30]),
            new(4, IPAddress.Parse("200.1.1.3"), "y", [null, 31, null]),
            new(5, IPAddress.Parse("45.79.10.20"), null, [null, 32, null]),
        };
        var r = RouteDiagnosis.TraceFindings(new TraceResult(IPAddress.Parse("45.79.10.20"), hops, true), null, null);
        var loss = Assert.Single(r, f => f.Kind == FindingKind.LossFromHop);
        Assert.Equal(4, loss.Ttl);
    }

    [Fact]
    public void Comparable_target_falls_back_to_the_last_public_hop()
    {
        var trace = Trace(("192.168.0.1", null, 1), ("200.1.1.9", null, 20), (null, null, null));
        var r = RouteDiagnosis.Diagnose(IPAddress.Parse("45.79.10.20"), null, null, LatencyStats.From([null, null]), trace with { ReachedTarget = false });
        var t = NetRouteEngine.ComparableTarget(r);
        Assert.Equal(IPAddress.Parse("200.1.1.9"), t!.Value.Target);
        Assert.False(t.Value.IsServer);
    }

    // ================================================================== decisão A/B

    private static LatencyStats Flat(double ms, int lost = 0) =>
        LatencyStats.From(Enumerable.Repeat<double?>(ms, 10 - lost).Concat(Enumerable.Repeat<double?>(null, lost)).ToList());

    [Fact]
    public void Relay_wins_only_with_margin_and_majority()
    {
        var win = TunnelDecision.Decide(Enumerable.Range(0, 5).Select(_ => new AbRound(Flat(80), Flat(55))).ToList());
        Assert.Equal(TunnelChoice.UseRelayLatency, win.Choice);
        Assert.Equal(25, win.GainP50Ms);

        var small = TunnelDecision.Decide(Enumerable.Range(0, 5).Select(_ => new AbRound(Flat(40), Flat(38))).ToList());
        Assert.Equal(TunnelChoice.StayDirect, small.Choice);
        Assert.Contains("pequeno", small.Text, StringComparison.Ordinal);

        var worse = TunnelDecision.Decide(Enumerable.Range(0, 5).Select(_ => new AbRound(Flat(40), Flat(52))).ToList());
        Assert.Equal(TunnelChoice.StayDirect, worse.Choice);
        Assert.Contains("maior", worse.Text, StringComparison.Ordinal);

        // ganha na mediana em só 2 de 5 rodadas: não liga
        var mixed = TunnelDecision.Decide([new(Flat(80), Flat(50)), new(Flat(80), Flat(50)), new(Flat(50), Flat(80)), new(Flat(50), Flat(80)), new(Flat(60), Flat(60))]);
        Assert.Equal(TunnelChoice.StayDirect, mixed.Choice);
    }

    [Fact]
    public void Relay_can_win_by_cutting_loss()
    {
        var v = TunnelDecision.Decide(Enumerable.Range(0, 4).Select(_ => new AbRound(Flat(50, lost: 2), Flat(51))).ToList());
        Assert.Equal(TunnelChoice.UseRelayLoss, v.Choice);
    }

    [Fact]
    public void Too_few_rounds_is_inconclusive()
    {
        var v = TunnelDecision.Decide([new(Flat(80), Flat(50)), new(Flat(80), LatencyStats.From([null]))]);
        Assert.Equal(TunnelChoice.Inconclusive, v.Choice);
        Assert.False(v.UseRelay);
    }

    [Fact]
    public void Health_monitor_falls_back_after_consecutive_bad_windows()
    {
        var m = new TunnelHealthMonitor(baselineP50Ms: 20);
        Assert.Equal(HealthAction.Keep, m.Observe(Flat(21)));
        Assert.Equal(HealthAction.Keep, m.Observe(Flat(60)));
        Assert.Equal(HealthAction.Keep, m.Observe(Flat(22))); // zera a sequência
        Assert.Equal(HealthAction.Keep, m.Observe(Flat(20, lost: 3)));
        Assert.Equal(HealthAction.Keep, m.Observe(LatencyStats.From([null])));
        Assert.Equal(HealthAction.FallBackToDirect, m.Observe(Flat(70)));
    }

    // ================================================================== descoberta

    [Fact]
    public void Tally_picks_the_bidirectional_game_flow_and_ignores_noise()
    {
        var t = new ConnectionTally();
        var server = IPAddress.Parse("45.79.10.20");
        for (var i = 0; i < 400; i++)
        {
            var at = Now.AddMilliseconds(i * 30);
            t.Add(new FlowEvent(100, FlowProtocol.Udp, server, 7777, 80, Outbound: i % 2 == 0, at));
            if (i % 40 == 0) t.Add(new FlowEvent(100, FlowProtocol.Udp, IPAddress.Parse("8.8.8.8"), 53, 60, true, at)); // DNS
            if (i % 2 == 0) t.Add(new FlowEvent(100, FlowProtocol.Udp, IPAddress.Parse("192.168.0.10"), 9000, 60, true, at)); // LAN
            if (i % 2 == 0) t.Add(new FlowEvent(200, FlowProtocol.Udp, IPAddress.Parse("45.79.99.1"), 3478, 60, true, at)); // outro processo
            t.Add(new FlowEvent(100, FlowProtocol.Tcp, IPAddress.Parse("45.79.50.1"), 443, 1200, false, at)); // HTTPS do lançador
        }
        var servers = t.GameServers(pid => pid == 100);
        var first = Assert.Single(servers);
        Assert.Equal(server, first.Address);
        Assert.Equal(7777, first.Port);
        Assert.True(first.PacketsPerSecond >= ConnectionTally.MinPacketsPerSecond);
    }

    [Fact]
    public void Tally_uses_tcp_only_when_there_is_no_udp_game_flow()
    {
        var t = new ConnectionTally();
        for (var i = 0; i < 300; i++)
            t.Add(new FlowEvent(7, FlowProtocol.Tcp, IPAddress.Parse("45.79.10.30"), 3724, 100, i % 2 == 0, Now.AddMilliseconds(i * 40)));
        Assert.Equal(3724, Assert.Single(t.GameServers(p => p == 7)).Port);
    }

    [Fact]
    public void Etw_payload_is_parsed_and_remote_side_chosen_by_local_addresses()
    {
        var local = new HashSet<IPAddress> { IPAddress.Parse("192.168.0.50") };
        byte[] V4(int pid, string a1, string a2, ushort p1, ushort p2)
        {
            var b = new byte[24];
            BitConverter.TryWriteBytes(b.AsSpan(0), pid);
            BitConverter.TryWriteBytes(b.AsSpan(4), 120);
            IPAddress.Parse(a1).GetAddressBytes().CopyTo(b, 8);
            IPAddress.Parse(a2).GetAddressBytes().CopyTo(b, 12);
            b[16] = (byte)(p1 >> 8); b[17] = (byte)p1;
            b[18] = (byte)(p2 >> 8); b[19] = (byte)p2;
            return b;
        }
        var send = EtwKernelNetwork.Parse(42, V4(4242, "45.79.10.20", "192.168.0.50", 7777, 50000), local, Now)!;
        Assert.Equal((4242, FlowProtocol.Udp, "45.79.10.20", 7777, true), (send.ProcessId, send.Protocol, send.Remote.ToString(), send.RemotePort, send.Outbound));
        // ordem invertida: o endereço local veio primeiro → o remoto é o segundo
        var swapped = EtwKernelNetwork.Parse(43, V4(4242, "192.168.0.50", "45.79.10.20", 50000, 7777), local, Now)!;
        Assert.Equal(("45.79.10.20", 7777, false), (swapped.Remote.ToString(), swapped.RemotePort, swapped.Outbound));
        Assert.Null(EtwKernelNetwork.Parse(99, V4(1, "45.79.10.20", "192.168.0.50", 1, 2), local, Now)); // outro evento
        Assert.Null(EtwKernelNetwork.Parse(42, new byte[10], local, Now)); // curto demais
    }

    // ================================================================== perfis e blocos aprendidos

    [Fact]
    public void Profiles_cover_valorant_cs2_fortnite_and_generic_games()
    {
        Assert.Equal("valorant", GameNetProfiles.ForProcess("VALORANT-Win64-Shipping")!.Id);
        Assert.Equal("cs2", GameNetProfiles.ForProcess("cs2.exe")!.Id);
        Assert.Equal("fortnite", GameNetProfiles.ForProcess("FortniteClient-Win64-Shipping")!.Id);
        Assert.Null(GameNetProfiles.ForProcess("Discord"));
        Assert.Contains("discord", GameNetProfiles.NeverGame);
        Assert.Equal("game-eldenring", GameNetProfiles.GenericIdFor("eldenring.exe"));
        Assert.True(TunnelConfig.IsSafeId(GameNetProfiles.GenericIdFor("Rocket League Game!.exe")));
    }

    [Fact]
    public void Learned_prefixes_aggregate_expire_and_round_trip()
    {
        var store = new LearnedPrefixStore();
        store.Learn("fortnite", IPAddress.Parse("18.228.10.20"), Now);
        store.Learn("fortnite", IPAddress.Parse("18.228.10.99"), Now.AddMinutes(1));
        store.Learn("fortnite", IPAddress.Parse("192.168.0.1"), Now); // privado: ignorado
        store.Learn("fortnite", IPAddress.Parse("54.94.1.1"), Now.AddDays(-40)); // vencido
        Assert.Equal("18.228.10.0/24", Assert.Single(store.PrefixesFor("fortnite", Now)).ToString());
        var entry = store.Entries("fortnite").Single(e => e.Prefix == "18.228.10.0/24");
        Assert.Equal(2, entry.Sightings);
        Assert.Equal("18.228.10.99", entry.LastAddress);
        var copy = LearnedPrefixStore.FromJson(store.ToJson());
        Assert.Equal(store.PrefixesFor("fortnite", Now), copy.PrefixesFor("fortnite", Now));
        Assert.Empty(LearnedPrefixStore.FromJson("{lixo").Games);
    }

    [Fact]
    public void Valve_sdr_config_gives_sao_paulo_relays()
    {
        const string json = """{"revision":1,"pops":{"gru":{"desc":"Sao Paulo (Brazil)","relays":[{"ipv4":"155.133.227.4","port_range":[27015,27060]},{"ipv4":"155.133.227.5"},{"ipv4":"10.0.0.1"}]},"iad":{"relays":[{"ipv4":"162.254.192.1"}]}}}""";
        var pops = ValveSdr.Parse(json);
        var gru = ValveSdr.PrefixesFor(pops, ValveSdr.SaoPauloPop);
        Assert.Equal(["155.133.227.4/32", "155.133.227.5/32"], gru.Select(p => p.ToString()));
        Assert.Empty(ValveSdr.PrefixesFor(pops, "xyz"));
        Assert.Empty(ValveSdr.Parse("nada"));
    }

    // ================================================================== chaves e configuração

    [Fact]
    public void X25519_matches_rfc7748_vectors()
    {
        var k = Convert.FromHexString("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4");
        var u = Convert.FromHexString("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c");
        Assert.Equal("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552", Convert.ToHexStringLower(Curve25519.ScalarMult(k, u)));
        var alice = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        Assert.Equal("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", Convert.ToHexStringLower(Curve25519.PublicKey(alice)));
        var bob = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var shared1 = Curve25519.ScalarMult(alice, Curve25519.PublicKey(bob));
        var shared2 = Curve25519.ScalarMult(bob, Curve25519.PublicKey(alice));
        Assert.Equal("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742", Convert.ToHexStringLower(shared1));
        Assert.Equal(shared1, shared2);
    }

    [Fact]
    public void Generated_keys_are_valid_wireguard_keys()
    {
        var (priv, pub) = WireGuardKey.GenerateKeyPair();
        Assert.True(WireGuardKey.IsValid(priv));
        Assert.True(WireGuardKey.IsValid(pub));
        Assert.Equal(pub, WireGuardKey.PublicFromPrivate(priv));
        Assert.NotEqual(priv, WireGuardKey.GenerateKeyPair().PrivateKey);
        Assert.False(WireGuardKey.IsValid("abc"));
    }

    [Fact]
    public void Relay_link_reads_the_python_vector_and_round_trips()
    {
        var (link, problem) = RelayLink.Parse(Vector);
        Assert.Null(problem);
        Assert.NotNull(link);
        Assert.Equal("Fortaleza VPS", link.Name);
        Assert.Equal("45.79.10.20:51820", link.Endpoint);
        Assert.Equal(ServerKey, link.ServerPublicKey);
        Assert.Equal("10.66.0.7/32", link.ClientAddressCidr);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.66.0.1"), 8787), link.AgentEndPoint);
        Assert.Equal("fortaleza", link.Place);
        Assert.Equal("rb01198f80d", link.Id);
        Assert.True(link.AuthorizedFor(ClientKey));
        Assert.False(link.AuthorizedFor(ServerKey));
        Assert.Equal(Vector, link.ToLink());
        Assert.Matches("\\A(?:" + RelayLink.LinkPattern + ")\\z", Vector);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://exemplo.com")]
    [InlineData("nexus-relay://v1/curto")]
    [InlineData("nexus-relay://v1/AQECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8g_yKIUFeVIEcKQgAHCkIAASJTymwLNDUuNzkuMTAuMjANRm9ydGFsZXphIFZQUwlmb3J0YWxl")]
    public void Relay_link_rejects_garbage(string text) => Assert.NotNull(RelayLink.Parse(text).Problem);

    [Fact]
    public void Relay_link_rejects_private_endpoints_and_home_network_addresses()
    {
        var good = RelayLink.Parse(Vector).Link!;
        Assert.NotNull((good with { Host = "192.168.0.10" }).Validate());
        Assert.NotNull((good with { ClientAddress = IPAddress.Parse("192.168.0.7") }).Validate());
        Assert.NotNull((good with { AgentAddress = good.ClientAddress }).Validate());
        Assert.NotNull((good with { Name = "a=b" }).Validate());
        Assert.NotNull((good with { Place = "atlantida" }).Validate());
        Assert.Null((good with { Host = "relay.exemplo.com.br", Place = null }).Validate());
    }

    private static (string Priv, RelayLink Link) AuthorizedLink()
    {
        var (priv, pub) = WireGuardKey.GenerateKeyPair();
        var link = RelayLink.Parse(Vector).Link! with { ClientKeyFingerprint = RelayLink.FingerprintOf(pub) };
        return (priv, link);
    }

    [Fact]
    public void Tunnel_config_routes_only_game_prefixes_and_the_agent()
    {
        var (priv, link) = AuthorizedLink();
        var prefixes = PrefixList.Parse("45.79.20.0/24,18.228.10.0/24").Prefixes!;
        var conf = TunnelConfig.Build(priv, link, prefixes, "fortnite");
        Assert.Contains("AllowedIPs = 10.66.0.1/32, 18.228.10.0/24, 45.79.20.0/24", conf, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0/0", conf, StringComparison.Ordinal);
        Assert.DoesNotContain("DNS", conf, StringComparison.Ordinal);
        Assert.Contains("Endpoint = 45.79.10.20:51820", conf, StringComparison.Ordinal);
        Assert.Equal(TunnelConfig.Fingerprint(link, prefixes, "fortnite"), TunnelConfig.ReadFingerprint(conf));
        Assert.NotEqual(TunnelConfig.Fingerprint(link, prefixes, "fortnite"), TunnelConfig.Fingerprint(link, [], "fortnite"));
        // link para outra chave: recusado
        Assert.Throws<ArgumentException>(() => TunnelConfig.Build(WireGuardKey.GenerateKeyPair().PrivateKey, link, prefixes, "fortnite"));
        Assert.Throws<ArgumentException>(() => TunnelConfig.Build(priv, link, [IpPrefix.Parse("192.168.0.0/24")], "fortnite"));
        Assert.Throws<ArgumentException>(() => TunnelConfig.Build(priv, link, prefixes, "../x"));
    }

    [Fact]
    public void WireGuard_release_list_is_parsed_and_newest_msi_picked()
    {
        var sha = new string('a', 64);
        var list = $"untrusted comment: signify\nRWxxxx\n{sha}  wireguard-amd64-0.5.3.msi\n{new string('b', 64)}  wireguard-amd64-0.5.10.msi\n{sha}  wireguard-arm64-0.5.3.msi\n{sha}  wireguard-installer.exe\n";
        var parsed = WireGuardRelease.ParseList(list);
        Assert.Equal(3, parsed.Count);
        var pick = WireGuardRelease.Pick(parsed, "amd64")!;
        Assert.Equal("wireguard-amd64-0.5.10.msi", pick.FileName);
        Assert.Equal(new Uri("https://download.wireguard.com/windows-client/wireguard-amd64-0.5.10.msi"), pick.Url);
        Assert.Null(WireGuardRelease.Pick(parsed, "x86"));
    }

    [Fact]
    public void Agent_probe_response_is_parsed_defensively()
    {
        Assert.Equal([12.5, null, 11.0], RelayAgentClient.ParseProbe("""{"target":"1.1.1.1","samples":[12.5,null,11.0]}"""));
        Assert.Equal([null], RelayAgentClient.ParseProbe("""{"samples":[-1]}"""));
        Assert.Empty(RelayAgentClient.ParseProbe("nada"));
    }

    // ================================================================== handlers (backends falsos)

    private sealed class FakeSystem : INetRouteSystem
    {
        public bool ClientInstalled { get; set; }
        public string? Config { get; set; }
        public int Installs, Uninstalls;
        public bool TunnelInstalled => Config is not null;
        public void InstallClient() { ClientInstalled = true; Installs++; }
        public void UninstallClient() { ClientInstalled = false; Uninstalls++; }
        public string? ReadTunnelConfig() => Config;
        public void InstallTunnel(string configText) => Config = configText;
        public void RemoveTunnel() => Config = null;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "nexus-netroute-" + Guid.NewGuid().ToString("N"));

    private static ActionInstance Instance(string id, Dictionary<string, string>? args = null)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == id);
        var inst = entry.TryInstantiate(args ?? new Dictionary<string, string>(), out var why);
        Assert.True(inst is not null, why);
        return inst!;
    }

    [Fact]
    public void Catalog_actions_exist_with_admin_privilege_and_translations()
    {
        var ids = new[] { "netroute.client.install", "netroute.identity.create", "netroute.relay.add", "netroute.tunnel.on" };
        foreach (var id in ids)
        {
            var e = Catalog.Actions.Single(a => a.Definition.Id == id);
            Assert.Equal(PrivilegeLevel.Admin, e.Definition.Privileges);
            Assert.Equal(PersistenceKind.Persistent, e.Definition.Persistence);
            Assert.True(e.Translations.ContainsKey("en") && e.Translations.ContainsKey("es"));
        }
        Assert.Contains(Catalog.Rejected, r => r.Id == "netroute.rejected.packet-injection");
        var kinds = NetRouteHandlersFactory.Create(new FileNetRouteStore(TempDir()), new FakeSystem()).Select(h => h.HandlerKind).ToHashSet();
        Assert.All(ids, id => Assert.Contains(Catalog.Actions.Single(a => a.Definition.Id == id).Definition.HandlerKind, kinds));
    }

    [Fact]
    public void Full_flow_identity_relay_tunnel_and_undo()
    {
        var dir = TempDir();
        try
        {
            var store = new FileNetRouteStore(dir);
            var sys = new FakeSystem();
            var handlers = NetRouteHandlersFactory.Create(store, sys).ToDictionary(h => h.HandlerKind);

            // cliente
            var client = Instance("netroute.client.install");
            var h = handlers[NetRouteClientHandler.Kind];
            Assert.Equal(ApplicabilityKind.Applicable, h.Assess(client, Admin).Kind);
            var clientBefore = h.Detect(client);
            h.Apply(client);
            Assert.True(h.Detect(client).SameValueAs(client.Desired));
            Assert.True(store.ClientInstalledByNexus);

            // identidade
            var idInst = Instance("netroute.identity.create");
            var hi = handlers[NetRouteIdentityHandler.Kind];
            var idBefore = hi.Detect(idInst);
            hi.Apply(idInst);
            Assert.True(hi.Detect(idInst).SameValueAs(idInst.Desired));
            var pub = store.ReadPublicKey()!;
            Assert.Equal(pub, store.ReadIdentity()!.PublicKey);

            // relay: link para OUTRA chave é recusado; para esta, aceito
            var hr = handlers[NetRouteRelayHandler.Kind];
            var foreign = Instance("netroute.relay.add", new() { ["link"] = Vector });
            Assert.Equal(ApplicabilityKind.Blocked, hr.Assess(foreign, Admin).Kind);
            var mine = RelayLink.Parse(Vector).Link! with { ClientKeyFingerprint = RelayLink.FingerprintOf(pub) };
            var relayInst = Instance("netroute.relay.add", new() { ["link"] = mine.ToLink() });
            Assert.Equal(ApplicabilityKind.Applicable, hr.Assess(relayInst, Admin).Kind);
            var relayBefore = hr.Detect(relayInst);
            hr.Apply(relayInst);
            Assert.True(hr.Detect(relayInst).SameValueAs(relayInst.Desired));
            Assert.Equal(mine, Assert.Single(store.ReadRelays()));

            // túnel
            var ht = handlers[NetRouteTunnelHandler.Kind];
            var tunnel = Instance("netroute.tunnel.on", new() { ["relay"] = mine.Id, ["game"] = "valorant", ["prefixes"] = "45.79.20.0/24" });
            Assert.Equal(ApplicabilityKind.Applicable, ht.Assess(tunnel, Admin).Kind);
            var tunnelBefore = ht.Detect(tunnel);
            Assert.False(tunnelBefore.Exists);
            ht.Apply(tunnel);
            Assert.True(ht.Detect(tunnel).SameValueAs(tunnel.Desired));
            Assert.Contains("45.79.20.0/24", sys.Config, StringComparison.Ordinal);

            // outro túnel com config diferente é recusado enquanto este estiver ligado
            var other = Instance("netroute.tunnel.on", new() { ["relay"] = mine.Id, ["game"] = "fortnite", ["prefixes"] = "-" });
            Assert.Equal(ApplicabilityKind.Blocked, ht.Assess(other, Admin).Kind);
            Assert.Equal(NetRouteTunnelHandler.Other, ht.Detect(other).Value);

            // não dá para apagar a chave nem remover o cliente com o túnel ligado
            Assert.Throws<InvalidOperationException>(() => hi.Restore(idInst, idBefore));
            Assert.Throws<InvalidOperationException>(() => h.Restore(client, clientBefore));

            // desfazer na ordem inversa
            ht.Restore(tunnel, tunnelBefore);
            Assert.False(sys.TunnelInstalled);
            hr.Restore(relayInst, relayBefore);
            Assert.Empty(store.ReadRelays());
            hi.Restore(idInst, idBefore);
            Assert.Null(store.ReadIdentity());
            h.Restore(client, clientBefore);
            Assert.False(sys.ClientInstalled);
            Assert.Equal(1, sys.Uninstalls);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Client_installed_by_someone_else_is_never_uninstalled_by_undo()
    {
        var dir = TempDir();
        try
        {
            var store = new FileNetRouteStore(dir);
            var sys = new FakeSystem { ClientInstalled = true };
            var h = new NetRouteClientHandler(sys, store);
            var inst = Instance("netroute.client.install");
            var before = h.Detect(inst);
            h.Apply(inst);
            Assert.Equal(0, sys.Installs);
            h.Restore(inst, before);
            Assert.True(sys.ClientInstalled);
            Assert.Equal(0, sys.Uninstalls);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Tunnel_is_blocked_without_client_identity_or_relay()
    {
        var dir = TempDir();
        try
        {
            var store = new FileNetRouteStore(dir);
            var sys = new FakeSystem();
            var ht = new NetRouteTunnelHandler(store, sys);
            var inst = Instance("netroute.tunnel.on", new() { ["relay"] = "rb01198f80d", ["game"] = "valorant", ["prefixes"] = "-" });
            Assert.Equal(ApplicabilityKind.Blocked, ht.Assess(inst, Admin).Kind);
            Assert.Equal(ApplicabilityKind.Blocked, ht.Assess(inst, Admin with { IsElevated = false }).Kind);
            Assert.Throws<InvalidOperationException>(() => ht.Apply(inst));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Catalog_rejects_bad_tunnel_arguments()
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "netroute.tunnel.on");
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["relay"] = "../etc", ["game"] = "valorant", ["prefixes"] = "-" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["relay"] = "rb01198f80d", ["game"] = "Valorant", ["prefixes"] = "-" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["relay"] = "rb01198f80d", ["game"] = "valorant", ["prefixes"] = "0.0.0.0/0\n" }, out _));
        var relay = Catalog.Actions.Single(a => a.Definition.Id == "netroute.relay.add");
        Assert.NotNull(relay.ValidateUserArguments(new Dictionary<string, string> { ["link"] = Vector }, out _));
        Assert.Null(relay.ValidateUserArguments(new Dictionary<string, string> { ["link"] = Vector + " x" }, out _));
    }

    [Fact]
    public void Store_rejects_path_tricks_in_relay_ids()
    {
        Assert.True(FileNetRouteStore.IsRelayId("rb01198f80d"));
        Assert.False(FileNetRouteStore.IsRelayId("r../../../x"));
        Assert.False(FileNetRouteStore.IsRelayId("RB01198F80D"));
        Assert.Null(new FileNetRouteStore(TempDir()).ReadRelay("..\\..\\x"));
    }

    // ================================================================== motor (sondas falsas)

    private sealed class FakeProber(Func<IPAddress, double?> rtt) : IPathProber
    {
        public Task<IReadOnlyList<double?>> PingAsync(IPAddress target, int count, TimeSpan interval, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double?>>(Enumerable.Repeat(rtt(target), count).ToList());

        public Task<TraceResult> TraceAsync(IPAddress target, int maxHops, int probesPerHop, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TraceResult(target, [new TraceHop(1, IPAddress.Parse("192.168.0.1"), null, [1.0, 1.0, 1.0]), new TraceHop(2, target, null, [rtt(target), rtt(target), rtt(target)])], true));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
    }

    [Fact]
    public async Task Engine_ab_test_chains_client_to_relay_and_relay_to_target()
    {
        var server = IPAddress.Parse("45.79.10.20");
        var link = RelayLink.Parse(Vector).Link!;
        // direto 90 ms; PC→relay 30 ms; relay→servidor 25 ms → pelo relay 55 ms
        var prober = new FakeProber(ip => ip.Equals(server) ? 90 : ip.Equals(link.AgentAddress) ? 30 : null);
        using var http = new HttpClient(new StubHandler(r => r.RequestUri!.AbsolutePath == "/health" ? """{"ok":true}""" : """{"samples":[25,25,25,25,25,25,25,25,25,25]}"""));
        var agent = new RelayAgentClient(http, link.AgentEndPoint);
        Assert.True(await agent.HealthAsync());
        var engine = new NetRouteEngine(prober) { PingInterval = TimeSpan.Zero };
        var result = await engine.AbTestAsync(server, true, link, agent);
        Assert.Equal(TunnelChoice.UseRelayLatency, result.Verdict.Choice);
        Assert.Equal(35, result.Verdict.GainP50Ms);
        var report = await engine.DiagnoseAsync(server, Places.Find("fortaleza"), Places.Find("sao-paulo"), withTrace: false);
        Assert.Equal(RouteVerdict.HighExcess, report.Verdict);
    }

    [Fact]
    public async Task Discover_returns_the_game_server_from_observer_events()
    {
        var server = IPAddress.Parse("45.79.10.20");
        var observer = new ScriptedObserver(Enumerable.Range(0, 300).Select(i =>
            new FlowEvent(55, FlowProtocol.Udp, server, 7000, 90, i % 2 == 0, Now.AddMilliseconds(i * 30))).ToList());
        var found = await NetRouteEngine.DiscoverAsync(observer, pid => pid == 55, TimeSpan.FromMilliseconds(10));
        Assert.Equal(server, Assert.Single(found).Address);
        Assert.True(observer.Stopped);
    }

    private sealed class ScriptedObserver(IReadOnlyList<FlowEvent> events) : IConnectionObserver
    {
        public bool Stopped { get; private set; }
        public void Start(Action<FlowEvent> onEvent) { foreach (var e in events) onEvent(e); }
        public void StopObserving() => Stopped = true;
        public void Dispose() => StopObserving();
    }
}
