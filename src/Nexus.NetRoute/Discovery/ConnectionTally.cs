using System.Net;
using Nexus.NetRoute.Ip;

namespace Nexus.NetRoute.Discovery;

public enum FlowProtocol { Udp, Tcp }

/// <summary>Um pacote visto pela descoberta: processo, protocolo, endereço e porta REMOTOS, bytes e sentido.</summary>
public sealed record FlowEvent(int ProcessId, FlowProtocol Protocol, IPAddress Remote, int RemotePort, int Bytes, bool Outbound, DateTimeOffset At);

/// <summary>Fluxo agregado (processo, protocolo, endereço remoto, porta remota).</summary>
public sealed record FlowSummary(int ProcessId, FlowProtocol Protocol, IPAddress Remote, int RemotePort, long Packets, long Bytes,
    long OutboundPackets, long InboundPackets, DateTimeOffset First, DateTimeOffset Last)
{
    public double Seconds => Math.Max(1, (Last - First).TotalSeconds);
    public double PacketsPerSecond => Packets / Seconds;
    /// <summary>Partida de jogo troca pacotes nos dois sentidos o tempo todo (atualizações do mundo e comandos do jogador).</summary>
    public bool Bidirectional => OutboundPackets > 0 && InboundPackets > 0;
}

/// <summary>Servidor de partida candidato, com a evidência que o escolheu.</summary>
public sealed record GameServerCandidate(IPAddress Address, int Port, FlowProtocol Protocol, double PacketsPerSecond, double Seconds, int ProcessId);

/// <summary>
/// Junta os pacotes do jogo e aponta o servidor da partida: fluxo UDP com endereço público, nos dois sentidos, ritmo de jogo
/// (pelo menos <see cref="MinPacketsPerSecond"/> pacotes/s) e por tempo suficiente. DNS, portas de serviço conhecidas e
/// endereços privados ficam de fora. TCP só entra se não houver UDP (jogos como WoW e Minecraft Java usam TCP).
/// </summary>
public sealed class ConnectionTally
{
    public const double MinPacketsPerSecond = 8.0;
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(5);

    /// <summary>Portas que nunca são a partida: DNS, NTP, mDNS/LLMNR, SSDP, NetBIOS e HTTP(S) por TCP.</summary>
    private static readonly HashSet<int> ServicePorts = [53, 67, 68, 123, 137, 138, 1900, 5353, 5355];
    private static readonly HashSet<int> WebTcpPorts = [80, 443, 8080];

    private readonly Dictionary<(int Pid, FlowProtocol Proto, IPAddress Remote, int Port), Accumulator> _flows = [];
    private readonly object _gate = new();

    public long EventsSeen { get; private set; }

    public void Add(FlowEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var remote = e.Remote.IsIPv4MappedToIPv6 ? e.Remote.MapToIPv4() : e.Remote;
        lock (_gate)
        {
            EventsSeen++;
            var key = (e.ProcessId, e.Protocol, remote, e.RemotePort);
            if (!_flows.TryGetValue(key, out var acc)) _flows[key] = acc = new Accumulator(e.At);
            acc.Packets++;
            acc.Bytes += Math.Max(0, e.Bytes);
            if (e.Outbound) acc.Out++; else acc.In++;
            if (e.At > acc.Last) acc.Last = e.At;
            if (e.At < acc.First) acc.First = e.At;
        }
    }

    public IReadOnlyList<FlowSummary> Flows(Func<int, bool>? processFilter = null)
    {
        lock (_gate)
            return _flows.Where(kv => processFilter?.Invoke(kv.Key.Pid) ?? true)
                .Select(kv => new FlowSummary(kv.Key.Pid, kv.Key.Proto, kv.Key.Remote, kv.Key.Port, kv.Value.Packets, kv.Value.Bytes, kv.Value.Out, kv.Value.In, kv.Value.First, kv.Value.Last))
                .OrderByDescending(f => f.Packets).ToList();
    }

    /// <summary>Servidores da partida dos processos aceitos pelo filtro, do mais provável ao menos.</summary>
    public IReadOnlyList<GameServerCandidate> GameServers(Func<int, bool> processFilter)
    {
        ArgumentNullException.ThrowIfNull(processFilter);
        var flows = Flows(processFilter).Where(IsPlausible).ToList();
        var udp = flows.Where(f => f.Protocol == FlowProtocol.Udp).ToList();
        var chosen = udp.Count > 0 ? udp : flows.Where(f => f.Protocol == FlowProtocol.Tcp && !WebTcpPorts.Contains(f.RemotePort)).ToList();
        return chosen.OrderByDescending(f => f.PacketsPerSecond)
            .Select(f => new GameServerCandidate(f.Remote, f.RemotePort, f.Protocol, f.PacketsPerSecond, f.Seconds, f.ProcessId)).ToList();
    }

    private static bool IsPlausible(FlowSummary f) =>
        PublicAddress.IsPublic(f.Remote) && !ServicePorts.Contains(f.RemotePort) && f.Bidirectional &&
        f.PacketsPerSecond >= MinPacketsPerSecond && f.Last - f.First >= MinDuration;

    public void Clear()
    {
        lock (_gate) { _flows.Clear(); EventsSeen = 0; }
    }

    private sealed class Accumulator(DateTimeOffset at)
    {
        public long Packets, Bytes, Out, In;
        public DateTimeOffset First = at, Last = at;
    }
}

/// <summary>Fonte de pacotes por processo (no Windows, ETW Microsoft-Windows-Kernel-Network).</summary>
public interface IConnectionObserver : IDisposable
{
    /// <summary>Começa a observar. Lança <see cref="UnauthorizedAccessException"/> sem permissão para sessões de rastreamento.</summary>
    void Start(Action<FlowEvent> onEvent);

    void Stop();
}
