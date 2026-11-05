using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Nexus.NetRoute.Probe;

/// <summary>Um salto da rota: endereço que respondeu (null = não respondeu), nome reverso e as amostras de RTT até ele.</summary>
public sealed record TraceHop(int Ttl, IPAddress? Address, string? HostName, IReadOnlyList<double?> Samples)
{
    public double? BestMs => Samples.Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty(double.NaN).Min() is var m && double.IsFinite(m) ? m : null;
    public bool Responded => Address is not null && Samples.Any(s => s.HasValue);
}

public sealed record TraceResult(IPAddress Target, IReadOnlyList<TraceHop> Hops, bool ReachedTarget);

/// <summary>Sondas de rota. Abstração para os testes (sem rede) e para trocar ICMP por UDP no futuro.</summary>
public interface IPathProber
{
    /// <summary><paramref name="count"/> pings com intervalo <paramref name="interval"/>; null = sem resposta no prazo.</summary>
    Task<IReadOnlyList<double?>> PingAsync(IPAddress target, int count, TimeSpan interval, TimeSpan timeout, CancellationToken cancellationToken = default);

    Task<TraceResult> TraceAsync(IPAddress target, int maxHops, int probesPerHop, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// ICMP pelo <see cref="Ping"/> do .NET (sem administrador no Windows). O traceroute usa TTL crescente: cada roteador que
/// descarta o pacote responde "TTL expirado" com o próprio endereço. Roteadores costumam tratar ICMP com prioridade baixa,
/// então perda num salto do meio não prova perda real — só a perda que começa num salto e segue até o destino conta.
/// </summary>
public sealed class IcmpPathProber : IPathProber
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<IReadOnlyList<double?>> PingAsync(IPAddress target, int count, TimeSpan interval, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var results = new List<double?>(count);
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = DateTime.UtcNow;
            results.Add(await OnceAsync(ping, target, timeout, ttl: 128).ConfigureAwait(false) is { Status: IPStatus.Success } r ? r.RoundtripTime : null);
            var wait = interval - (DateTime.UtcNow - started);
            if (i + 1 < count && wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
        return results;
    }

    public async Task<TraceResult> TraceAsync(IPAddress target, int maxHops, int probesPerHop, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var hops = new List<TraceHop>();
        var reached = false;
        using var ping = new Ping();
        for (var ttl = 1; ttl <= maxHops && !reached; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IPAddress? address = null;
            var samples = new List<double?>(probesPerHop);
            for (var p = 0; p < probesPerHop; p++)
            {
                var started = DateTime.UtcNow;
                var reply = await OnceAsync(ping, target, timeout, ttl).ConfigureAwait(false);
                if (reply is { Status: IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded } && reply.Address is { } a && !a.Equals(IPAddress.Any))
                {
                    address ??= a;
                    // Em "TTL expirado" o RoundtripTime do .NET vem 0: mede pelo relógio.
                    var rtt = reply.Status == IPStatus.Success && reply.RoundtripTime > 0 ? reply.RoundtripTime : (DateTime.UtcNow - started).TotalMilliseconds;
                    samples.Add(rtt);
                    if (reply.Status == IPStatus.Success) reached = true;
                }
                else samples.Add(null);
            }
            hops.Add(new TraceHop(ttl, address, null, samples));
        }
        return new TraceResult(target, hops, reached);
    }

    private static async Task<PingReply?> OnceAsync(Ping ping, IPAddress target, TimeSpan timeout, int ttl)
    {
        try
        {
            return await ping.SendPingAsync(target, timeout, Payload, new PingOptions(ttl, dontFragment: true)).ConfigureAwait(false);
        }
        catch (PingException) { return null; }
        catch (SocketException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

/// <summary>Nome reverso (rDNS) com prazo curto: muitos roteadores não têm, e esperar o tempo padrão travaria o diagnóstico.</summary>
public static class ReverseDns
{
    public static async Task<string?> LookupAsync(IPAddress address, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            var lookup = Dns.GetHostEntryAsync(address);
            var done = await Task.WhenAny(lookup, Task.Delay(timeout)).ConfigureAwait(false);
            if (done != lookup) return null;
            var name = (await lookup.ConfigureAwait(false)).HostName;
            return string.IsNullOrWhiteSpace(name) || name == address.ToString() ? null : name;
        }
        catch (SocketException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static async Task<TraceResult> NameHopsAsync(TraceResult trace, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var named = await Task.WhenAll(trace.Hops.Select(async h =>
            h.Address is { } a ? h with { HostName = await LookupAsync(a, timeout).ConfigureAwait(false) } : h)).ConfigureAwait(false);
        return trace with { Hops = named };
    }
}
