using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.NetRoute.Ip;

namespace Nexus.NetRoute.Profiles;

/// <summary>De onde vêm os blocos de endereço que vão para o túnel deste jogo.</summary>
public enum PrefixSource
{
    /// <summary>Aprendidos das partidas anteriores (servidores vistos pela descoberta, agrupados em /24).</summary>
    Learned,
    /// <summary>Relays da Valve (Steam Datagram Relay) da região, publicados pela própria Valve (GetSDRConfig).</summary>
    ValveSdr,
}

/// <summary>
/// Perfil de rede de um jogo. Perfis especiais (VALORANT, CS2, Fortnite) trazem processos, cidade provável dos servidores do
/// Brasil e observações; qualquer outro jogo usa o perfil genérico (processo em primeiro plano + blocos aprendidos).
/// </summary>
public sealed record GameNetProfile(
    string Id,
    string Name,
    IReadOnlyList<string> Processes,
    PrefixSource Source,
    string? BrazilServerPlace,
    bool OwnNetwork,
    string Note)
{
    public bool Matches(string processName) =>
        Processes.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Path.GetFileNameWithoutExtension(p), processName, StringComparison.OrdinalIgnoreCase));
}

public static class GameNetProfiles
{
    public const string GenericId = "generic";

    public static GameNetProfile Valorant { get; } = new("valorant", "VALORANT", ["VALORANT-Win64-Shipping.exe"], PrefixSource.Learned, "sao-paulo", OwnNetwork: true,
        "Usa a Riot Direct (rede própria com peering direto com provedores). O ganho de um relay tende a ser pequeno no Brasil; o teste A/B decide.");

    public static GameNetProfile CounterStrike2 { get; } = new("cs2", "Counter-Strike 2", ["cs2.exe"], PrefixSource.ValveSdr, "sao-paulo", OwnNetwork: true,
        "Usa o Steam Datagram Relay: o jogo fala com relays da Valve, não com o servidor. O túnel leva só até os relays da Valve de São Paulo (experimental).");

    public static GameNetProfile Fortnite { get; } = new("fortnite", "Fortnite", ["FortniteClient-Win64-Shipping.exe"], PrefixSource.Learned, "sao-paulo", OwnNetwork: false,
        "Servidores em nuvem comum: bom candidato. Os blocos vêm das partidas anteriores; jogue uma partida com a descoberta ligada antes.");

    public static GameNetProfile Generic { get; } = new(GenericId, "Outro jogo", [], PrefixSource.Learned, null, OwnNetwork: false,
        "Jogo detectado pelo processo em primeiro plano. Os blocos vêm das partidas anteriores; sem cidade conhecida do servidor, a régua física pode faltar.");

    public static IReadOnlyList<GameNetProfile> Special { get; } = [Valorant, CounterStrike2, Fortnite];

    public static GameNetProfile? Find(string? id) =>
        id is null ? null : Special.FirstOrDefault(p => p.Id == id) ?? (id == GenericId ? Generic : null);

    /// <summary>Perfil para um processo; null se não for jogo especial (o chamador decide se usa o genérico).</summary>
    public static GameNetProfile? ForProcess(string processName) => Special.FirstOrDefault(p => p.Matches(processName));

    /// <summary>
    /// Processos que nunca são "o jogo" no modo genérico (navegadores, chat, lançadores, o próprio Windows). Sem isto, abrir o
    /// Discord em primeiro plano faria o NEXUS aprender servidores do Discord.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverGame = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "discord", "steam", "steamwebhelper", "epicgameslauncher",
        "riotclientservices", "riotclientux", "battle.net", "origin", "eadesktop", "ubisoftconnect", "spotify", "teams", "ms-teams", "slack",
        "obs64", "obs32", "whatsapp", "telegram", "zoom", "code", "devenv", "nexus.desktop", "nexus", "searchhost", "startmenuexperiencehost",
        "applicationframehost", "textinputhost", "shellexperiencehost", "lockapp", "taskmgr", "mmc", "powershell", "cmd", "windowsterminal",
    };

    /// <summary>Id seguro para um jogo genérico: "game-" + nome do processo em minúsculas (só letras, dígitos e hífen).</summary>
    public static string GenericIdFor(string processName)
    {
        var clean = new string(Path.GetFileNameWithoutExtension(processName).ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (clean.Length > 40) clean = clean[..40];
        return clean.Length == 0 ? GenericId : "game-" + clean;
    }
}

/// <summary>Bloco aprendido para um jogo: quando foi visto pela última vez e quantas vezes.</summary>
public sealed record LearnedPrefix(string Prefix, DateTimeOffset LastSeen, int Sightings, string? Place = null, string? LastAddress = null);

/// <summary>
/// Blocos aprendidos por jogo (%LocalAppData%\NEXUS\netroute\learned.json). Cada servidor visto vira um /24 (IPv4) ou /48 (IPv6):
/// estreito o bastante para não arrastar outros serviços, largo o bastante para pegar o servidor da próxima partida na mesma
/// sala. Validade de 30 dias e no máximo 64 blocos por jogo (os mais vistos ficam).
/// </summary>
public sealed class LearnedPrefixStore
{
    public const int MaxPerGame = 64;
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Dictionary<string, List<LearnedPrefix>> _games;

    public LearnedPrefixStore() : this(new Dictionary<string, List<LearnedPrefix>>(StringComparer.Ordinal)) { }

    private LearnedPrefixStore(Dictionary<string, List<LearnedPrefix>> games) => _games = games;

    public IReadOnlyCollection<string> Games => _games.Keys;

    public static IpPrefix? Aggregate(IPAddress server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.IsIPv4MappedToIPv6) server = server.MapToIPv4();
        if (!PublicAddress.IsPublic(server)) return null;
        return new IpPrefix(server, server.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 24 : 48);
    }

    public void Learn(string gameId, IPAddress server, DateTimeOffset now, string? place = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        if (Aggregate(server) is not { } prefix) return;
        if (!_games.TryGetValue(gameId, out var list)) _games[gameId] = list = [];
        var key = prefix.ToString();
        var i = list.FindIndex(x => x.Prefix == key);
        var address = (server.IsIPv4MappedToIPv6 ? server.MapToIPv4() : server).ToString();
        if (i >= 0) list[i] = list[i] with { LastSeen = now, Sightings = list[i].Sightings + 1, Place = place ?? list[i].Place, LastAddress = address };
        else list.Add(new LearnedPrefix(key, now, 1, place, address));
        Trim(list, now);
    }

    /// <summary>Blocos válidos (não vencidos) do jogo, na forma canônica.</summary>
    public IReadOnlyList<IpPrefix> PrefixesFor(string gameId, DateTimeOffset now)
    {
        if (!_games.TryGetValue(gameId, out var list)) return [];
        var valid = list.Where(x => now - x.LastSeen <= Expiry).Select(x => IpPrefix.TryParse(x.Prefix, out var p) ? p : (IpPrefix?)null)
            .Where(p => p is { } v && PublicAddress.IsPublic(v)).Select(p => p!.Value);
        return PrefixList.Canonical(valid);
    }

    public IReadOnlyList<LearnedPrefix> Entries(string gameId) => _games.TryGetValue(gameId, out var l) ? l : [];

    public bool Forget(string gameId) => _games.Remove(gameId);

    private static void Trim(List<LearnedPrefix> list, DateTimeOffset now)
    {
        list.RemoveAll(x => now - x.LastSeen > Expiry);
        if (list.Count <= MaxPerGame) return;
        var keep = list.OrderByDescending(x => x.Sightings).ThenByDescending(x => x.LastSeen).Take(MaxPerGame).ToList();
        list.Clear();
        list.AddRange(keep);
    }

    public string ToJson() => JsonSerializer.Serialize(_games, Options);

    public static LearnedPrefixStore FromJson(string json)
    {
        try
        {
            var games = JsonSerializer.Deserialize<Dictionary<string, List<LearnedPrefix>>>(json, Options);
            return games is null ? new LearnedPrefixStore() : new LearnedPrefixStore(new Dictionary<string, List<LearnedPrefix>>(games, StringComparer.Ordinal));
        }
        catch (JsonException) { return new LearnedPrefixStore(); }
    }

    public static LearnedPrefixStore Load(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path)) : new LearnedPrefixStore(); }
        catch (IOException) { return new LearnedPrefixStore(); }
        catch (UnauthorizedAccessException) { return new LearnedPrefixStore(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }
}
