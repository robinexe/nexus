using System.Net;
using System.Text.Json;
using Nexus.NetRoute.Analysis;
using Nexus.NetRoute.Decision;
using Nexus.NetRoute.Discovery;
using Nexus.NetRoute.Geo;
using Nexus.NetRoute.Probe;
using Nexus.NetRoute.Relay;
using Nexus.NetRoute.Stats;

namespace Nexus.NetRoute;

/// <summary>Preferências da rota de jogo do usuário (%LocalAppData%\NEXUS\netroute\settings.json).</summary>
public sealed record NetRouteSettings(string? City = null, string? RelayId = null, Guid? TunnelRunId = null, string? TunnelGame = null, DateTimeOffset? TunnelSince = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "netroute");
    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");
    public static string LearnedPath => Path.Combine(DefaultDirectory, "learned.json");

    public static NetRouteSettings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<NetRouteSettings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Resultado do teste A/B: rodadas, alvo medido (servidor ou último salto) e o veredito.</summary>
public sealed record AbTestResult(IPAddress Target, bool TargetIsServer, IReadOnlyList<AbRound> Rounds, TunnelVerdict Verdict);

/// <summary>
/// Orquestra a rota de jogo do lado do usuário (só leitura e medição; quem liga o túnel é o aplicador): descoberta do servidor,
/// diagnóstico com régua física e o teste A/B direto × relay.
/// </summary>
public sealed class NetRouteEngine(IPathProber prober)
{
    public int PingsPerRound { get; init; } = 10;
    public int Rounds { get; init; } = 5;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Observa os pacotes durante <paramref name="duration"/> e devolve os servidores de partida dos processos aceitos.</summary>
    public static async Task<IReadOnlyList<GameServerCandidate>> DiscoverAsync(IConnectionObserver observer, Func<int, bool> processFilter, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(processFilter);
        var tally = new ConnectionTally();
        observer.Start(tally.Add);
        try { await Task.Delay(duration, cancellationToken).ConfigureAwait(false); }
        finally { observer.Stop(); }
        return tally.GameServers(processFilter);
    }

    public async Task<RouteReport> DiagnoseAsync(IPAddress server, Place? userPlace, Place? serverPlaceHint, bool withTrace = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var samples = await prober.PingAsync(server, PingsPerRound * 2, PingInterval, PingTimeout, cancellationToken).ConfigureAwait(false);
        var direct = LatencyStats.From(samples);
        TraceResult? trace = null;
        if (withTrace)
        {
            trace = await prober.TraceAsync(server, maxHops: 30, probesPerHop: 3, PingTimeout, cancellationToken).ConfigureAwait(false);
            trace = await ReverseDns.NameHopsAsync(trace, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        return RouteDiagnosis.Diagnose(server, userPlace, serverPlaceHint, direct, trace);
    }

    /// <summary>
    /// Alvo comparável pelos dois caminhos: o servidor, se responde a ping; senão o último salto que respondeu no traceroute
    /// (muitos servidores de jogo ignoram ping). Null = nada respondeu.
    /// </summary>
    public static (IPAddress Target, bool IsServer)? ComparableTarget(RouteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Direct.HasData) return (report.Server, true);
        var last = report.Trace?.Hops.LastOrDefault(h => h.Responded && h.Address is { } a && Ip.PublicAddress.IsPublic(a));
        return last?.Address is { } addr ? (addr, false) : null;
    }

    /// <summary>
    /// Rodadas alternadas: direto (PC → alvo) e relay (PC → agente pelo túnel + relay → alvo). O túnel precisa estar em modo
    /// medição (só o agente passa por ele), para a rota direta continuar sendo a do provedor.
    /// </summary>
    public async Task<AbTestResult> AbTestAsync(IPAddress target, bool targetIsServer, RelayLink relay, RelayAgentClient agent, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(agent);
        var agentIp = relay.AgentAddress;
        var rounds = new List<AbRound>();
        for (var i = 0; i < Rounds; i++)
        {
            progress?.Report($"Rodada {i + 1} de {Rounds}: rota direta");
            var direct = LatencyStats.From(await prober.PingAsync(target, PingsPerRound, PingInterval, PingTimeout, cancellationToken).ConfigureAwait(false));
            progress?.Report($"Rodada {i + 1} de {Rounds}: pelo relay {relay.Name}");
            var toRelayTask = prober.PingAsync(agentIp, PingsPerRound, PingInterval, PingTimeout, cancellationToken);
            var fromRelayTask = agent.ProbeAsync(target, PingsPerRound, cancellationToken);
            await Task.WhenAll(toRelayTask, fromRelayTask).ConfigureAwait(false);
            var toRelay = LatencyStats.From(await toRelayTask.ConfigureAwait(false));
            var fromRelay = LatencyStats.From(await fromRelayTask.ConfigureAwait(false));
            rounds.Add(new AbRound(direct, LatencyStats.Chain(toRelay, fromRelay)));
        }
        return new AbTestResult(target, targetIsServer, rounds, TunnelDecision.Decide(rounds));
    }
}
