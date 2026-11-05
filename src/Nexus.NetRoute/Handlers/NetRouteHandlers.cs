using Nexus.Core;
using Nexus.NetRoute.Ip;
using Nexus.NetRoute.Relay;
using Nexus.NetRoute.WireGuard;
using Nexus.WindowsActions;

namespace Nexus.NetRoute.Handlers;

/// <summary>
/// Ação <c>netroute.client.install</c>: cliente WireGuard OFICIAL (instalador MSI da WireGuard LLC, assinatura conferida).
/// Desfazer remove só se foi o NEXUS que instalou e não há rota ligada.
/// </summary>
public sealed class NetRouteClientHandler(INetRouteSystem system, INetRouteStore store) : IActionHandler
{
    public const string Kind = "NetRouteWireGuardClient";
    public const string StateType = "NetRouteClient";
    public const string Installed = "installed";

    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        return action.Target.TryGetValue("client", out var c) && c == "wireguard-official"
            ? Applicability.Ok : Applicability.Blocked("Só o cliente WireGuard oficial é aceito.");
    }

    public StateSnapshot Detect(ActionInstance action) => system.ClientInstalled ? StateSnapshot.Of(StateType, Installed) : StateSnapshot.Absent();

    public void Apply(ActionInstance action)
    {
        if (system.ClientInstalled) return;
        system.InstallClient();
        store.ClientInstalledByNexus = true;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Exists || !system.ClientInstalled) return;
        if (system.TunnelInstalled) throw new InvalidOperationException("A rota de jogo está ligada: desligue a rota antes de remover o WireGuard.");
        if (!store.ClientInstalledByNexus) return; // instalado por outra pessoa/programa depois: não é nosso para remover
        system.UninstallClient();
        store.ClientInstalledByNexus = false;
    }
}

/// <summary>
/// Ação <c>netroute.identity.create</c>: par de chaves WireGuard do PC. A chave privada é gerada aqui, dentro do aplicador, e
/// nunca sai do PC; a pública vai para client.pub (a interface mostra para levar ao relay).
/// </summary>
public sealed class NetRouteIdentityHandler(INetRouteStore store, INetRouteSystem system, TimeProvider? clock = null) : IActionHandler
{
    public const string Kind = "NetRouteIdentity";
    public const string StateType = "NetRouteIdentity";
    public const string Present = "present";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        return action.Target.TryGetValue("identity", out var i) && i == "pc" ? Applicability.Ok : Applicability.Blocked("Alvo de identidade inválido.");
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var id = store.ReadIdentity();
        return id is null ? StateSnapshot.Absent()
            : StateSnapshot.Of(StateType, Present, ValueOrigin.Explicit, new Dictionary<string, string>(StringComparer.Ordinal) { ["publicKey"] = id.PublicKey });
    }

    public void Apply(ActionInstance action)
    {
        if (store.ReadIdentity() is not null) return;
        var (priv, pub) = WireGuardKey.GenerateKeyPair();
        store.WriteIdentity(new NetRouteIdentity(priv, pub, _clock.GetUtcNow()));
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Exists) return;
        if (system.TunnelInstalled) throw new InvalidOperationException("A rota de jogo está ligada: desligue a rota antes de apagar a chave do PC.");
        store.DeleteIdentity();
    }
}

/// <summary>
/// Ação <c>netroute.relay.add</c>: cadastra um relay pelo link impresso no servidor (<c>nexus_relay.py add</c>). O link precisa
/// ter sido gerado para a chave pública DESTE PC; senão o túnel nunca fecharia e o pedido é recusado com o motivo.
/// </summary>
public sealed class NetRouteRelayHandler(INetRouteStore store) : IActionHandler
{
    public const string Kind = "NetRouteRelay";
    public const string StateType = "NetRouteRelay";
    public const string Registered = "registered";

    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        var (link, problem) = RelayLink.Parse(action.Target.GetValueOrDefault("link"));
        if (link is null) return Applicability.Blocked(problem ?? "Link inválido.");
        var pub = store.ReadIdentity()?.PublicKey;
        if (pub is null) return Applicability.Blocked("Crie primeiro a chave deste PC (netroute.identity.create) e gere o link no relay com ela.");
        if (!link.AuthorizedFor(pub))
            return Applicability.Blocked("Este link foi gerado para outra chave. No relay, rode o comando 'add' com o código deste PC.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var link = Link(action);
        var stored = store.ReadRelay(link.Id);
        return stored is not null && stored == link ? StateSnapshot.Of(StateType, Registered) : StateSnapshot.Absent();
    }

    public void Apply(ActionInstance action) => store.WriteRelay(Link(action));

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Exists) return;
        store.DeleteRelay(Link(action).Id);
    }

    private static RelayLink Link(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RelayLink.Parse(action.Target.GetValueOrDefault("link")).Link ?? throw new InvalidOperationException("Link de relay inválido; nada foi alterado.");
    }
}

/// <summary>
/// Ação <c>netroute.tunnel.on</c>: liga o túnel WireGuard até o relay, levando SÓ os blocos do jogo (e o agente de medição do
/// relay). Com blocos "-" é o modo medição: só o agente passa pelo túnel e o jogo continua na rota direta. Um túnel por vez:
/// se já houver outro ligado, o pedido é recusado (desligue antes).
/// </summary>
public sealed class NetRouteTunnelHandler(INetRouteStore store, INetRouteSystem system) : IActionHandler
{
    public const string Kind = "NetRouteTunnel";
    public const string StateType = "NetRouteTunnel";
    public const string On = "on";
    public const string Other = "other";

    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        var (spec, problem) = Spec(action);
        if (spec is null) return Applicability.Blocked(problem!);
        if (!system.ClientInstalled) return Applicability.Blocked("O cliente WireGuard oficial não está instalado (netroute.client.install).");
        if (system.TunnelInstalled && Fingerprint(system.ReadTunnelConfig()) != spec.Value.Fingerprint)
            return Applicability.Blocked("Já existe uma rota de jogo ligada com outro relay, jogo ou blocos. Desligue a rota atual antes.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var (spec, _) = Spec(action);
        if (!system.TunnelInstalled) return StateSnapshot.Absent();
        var current = Fingerprint(system.ReadTunnelConfig());
        var detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["fingerprint"] = current ?? "" };
        return StateSnapshot.Of(StateType, spec is not null && current == spec.Value.Fingerprint ? On : Other, ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        var (spec, problem) = Spec(action);
        if (spec is null) throw new InvalidOperationException(problem);
        var s = spec.Value;
        if (system.TunnelInstalled)
        {
            if (Fingerprint(system.ReadTunnelConfig()) == s.Fingerprint) return;
            throw new InvalidOperationException("Já existe outra rota de jogo ligada. Desligue a rota atual antes.");
        }
        var identity = store.ReadIdentity() ?? throw new InvalidOperationException("Chave do PC ausente.");
        system.InstallTunnel(TunnelConfig.Build(identity.PrivateKey, s.Relay, s.Prefixes, s.GameId));
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Exists) return; // havia túnel antes: o Assess recusa sobrepor, então nada nosso a desfazer
        if (!system.TunnelInstalled) return;
        var (spec, _) = Spec(action);
        // Só remove o túnel que ESTA ação ligou (mesma impressão digital); outro túnel ligado depois não é desfeito por engano.
        if (spec is not null && Fingerprint(system.ReadTunnelConfig()) != spec.Value.Fingerprint)
            throw new InvalidOperationException("A rota ligada agora não é a desta entrada do histórico; desfaça a entrada mais recente.");
        system.RemoveTunnel();
    }

    internal readonly record struct TunnelSpec(RelayLink Relay, IReadOnlyList<IpPrefix> Prefixes, string GameId, string Fingerprint);

    internal (TunnelSpec? Spec, string? Problem) Spec(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var relayId = action.Target.GetValueOrDefault("relay");
        var gameId = action.Target.GetValueOrDefault("game");
        if (!FileNetRouteStore.IsRelayId(relayId)) return (null, "Id de relay inválido.");
        if (!TunnelConfig.IsSafeId(gameId)) return (null, "Id de jogo inválido.");
        var (prefixes, problem) = PrefixList.Parse(action.Target.GetValueOrDefault("prefixes"));
        if (prefixes is null) return (null, problem);
        var relay = store.ReadRelay(relayId!);
        if (relay is null) return (null, "Relay não cadastrado neste PC (netroute.relay.add).");
        var identity = store.ReadIdentity();
        if (identity is null) return (null, "Chave do PC ausente (netroute.identity.create).");
        if (!relay.AuthorizedFor(identity.PublicKey))
            return (null, "O relay foi autorizado para outra chave. Gere o link de novo no relay com o código deste PC.");
        return (new TunnelSpec(relay, prefixes, gameId!, TunnelConfig.Fingerprint(relay, prefixes, gameId!)), null);
    }

    private static string? Fingerprint(string? config) => config is null ? null : TunnelConfig.ReadFingerprint(config);
}

/// <summary>Handlers da rota de jogo (registrados pelo aplicador em ModuleHandlers).</summary>
public static class NetRouteHandlersFactory
{
    public static IEnumerable<IActionHandler> Create(INetRouteStore store, INetRouteSystem system) =>
    [
        new NetRouteClientHandler(system, store),
        new NetRouteIdentityHandler(store, system),
        new NetRouteRelayHandler(store),
        new NetRouteTunnelHandler(store, system),
    ];
}
