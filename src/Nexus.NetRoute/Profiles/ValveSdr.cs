using System.Net;
using System.Text.Json;
using Nexus.NetRoute.Ip;

namespace Nexus.NetRoute.Profiles;

/// <summary>Um ponto de presença (POP) da rede de relays da Valve, com os endereços dos relays.</summary>
public sealed record SdrPop(string Code, string Description, IReadOnlyList<IPAddress> Relays);

/// <summary>
/// Relays do Steam Datagram Relay (CS2, Dota 2). A Valve publica a lista em
/// <c>https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730</c> ("pops" → código → "relays" → "ipv4"). No CS2
/// o jogo não fala com o servidor, fala com esses relays; por isso o túnel do CS2 leva só até os relays da região.
/// </summary>
public static class ValveSdr
{
    public const string ConfigUrl = "https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730";

    /// <summary>POP da Valve em São Paulo (os servidores brasileiros de CS2 ficam atrás dele).</summary>
    public const string SaoPauloPop = "gru";

    public static IReadOnlyList<SdrPop> Parse(string json)
    {
        var pops = new List<SdrPop>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pops", out var p) || p.ValueKind != JsonValueKind.Object) return pops;
            foreach (var pop in p.EnumerateObject())
            {
                var relays = new List<IPAddress>();
                if (pop.Value.TryGetProperty("relays", out var r) && r.ValueKind == JsonValueKind.Array)
                    foreach (var relay in r.EnumerateArray())
                        if (relay.ValueKind == JsonValueKind.Object && relay.TryGetProperty("ipv4", out var ip) && ip.ValueKind == JsonValueKind.String &&
                            IPAddress.TryParse(ip.GetString(), out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && PublicAddress.IsPublic(addr))
                            relays.Add(addr);
                var desc = pop.Value.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? pop.Name : pop.Name;
                pops.Add(new SdrPop(pop.Name, desc, relays));
            }
        }
        catch (JsonException) { }
        return pops;
    }

    /// <summary>Blocos /32 dos relays de um POP (vazio se o POP não existir ou não tiver relays).</summary>
    public static IReadOnlyList<IpPrefix> PrefixesFor(IReadOnlyList<SdrPop> pops, string popCode)
    {
        ArgumentNullException.ThrowIfNull(pops);
        var pop = pops.FirstOrDefault(p => string.Equals(p.Code, popCode, StringComparison.OrdinalIgnoreCase));
        return pop is null ? [] : PrefixList.Canonical(pop.Relays.Take(PrefixList.MaxPrefixes).Select(IpPrefix.Host));
    }

    public static async Task<IReadOnlyList<SdrPop>> FetchAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var json = await http.GetStringAsync(new Uri(ConfigUrl), cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }
}
