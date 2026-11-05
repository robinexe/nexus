using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nexus.NetRoute.Ip;
using Nexus.NetRoute.Relay;

namespace Nexus.NetRoute.WireGuard;

/// <summary>
/// Configuração do túnel do NEXUS no formato do WireGuard. Pontos de segurança:
/// <list type="bullet">
/// <item>AllowedIPs = agente do relay (/32) + os blocos do jogo. Nunca 0.0.0.0/0: o resto do PC continua na rota normal.</item>
/// <item>Sem DNS: o túnel não troca o DNS do Windows.</item>
/// <item>Todos os campos são validados antes (link do relay, blocos públicos, chave) e nenhum texto livre entra no arquivo.</item>
/// </list>
/// </summary>
public static class TunnelConfig
{
    /// <summary>Nome do túnel no WireGuard (serviço WireGuardTunnel$NexusRota). Fixo: só existe UM túnel do NEXUS.</summary>
    public const string TunnelName = "NexusRota";

    /// <summary>MTU conservador: cabe em PPPoE (1492) e em redes móveis com sobra para o cabeçalho do WireGuard.</summary>
    public const int Mtu = 1380;

    public const int KeepaliveSeconds = 25;

    /// <summary>Linha de comentário com a impressão digital da configuração (Detect compara sem reler a chave privada).</summary>
    public const string FingerprintPrefix = "# nexus-fingerprint: ";

    public static string Build(string clientPrivateKey, RelayLink relay, IReadOnlyList<IpPrefix> gamePrefixes, string gameId)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(gamePrefixes);
        if (!WireGuardKey.IsValid(clientPrivateKey)) throw new ArgumentException("Chave privada do PC inválida.", nameof(clientPrivateKey));
        if (relay.Validate() is { } problem) throw new ArgumentException(problem, nameof(relay));
        if (!IsSafeId(gameId)) throw new ArgumentException("Id do jogo inválido.", nameof(gameId));
        if (gamePrefixes.Any(p => !PublicAddress.IsPublic(p))) throw new ArgumentException("Bloco não público na lista do túnel.", nameof(gamePrefixes));
        if (gamePrefixes.Count > PrefixList.MaxPrefixes) throw new ArgumentException("Blocos demais.", nameof(gamePrefixes));
        if (!relay.AuthorizedFor(WireGuardKey.PublicFromPrivate(clientPrivateKey)))
            throw new ArgumentException("Este relay foi autorizado para outra chave (outro PC ou chave recriada). Gere o link de novo no relay com o código deste PC.", nameof(relay));

        var allowed = new List<string> { $"{relay.AgentAddress}/32" };
        allowed.AddRange(PrefixList.Canonical(gamePrefixes).Select(p => p.ToString()));
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(FingerprintPrefix).Append(Fingerprint(relay, gamePrefixes, gameId)).Append('\n');
        sb.Append("# NEXUS: rota de jogo (").Append(gameId).Append(") pelo relay ").Append(relay.Id).Append(". Gerado pelo aplicador; não edite.\n");
        sb.Append("[Interface]\n");
        sb.Append("PrivateKey = ").Append(clientPrivateKey).Append('\n');
        sb.Append("Address = ").Append(relay.ClientAddressCidr).Append('\n');
        sb.Append("MTU = ").Append(Mtu.ToString(inv)).Append('\n');
        sb.Append("\n[Peer]\n");
        sb.Append("PublicKey = ").Append(relay.ServerPublicKey).Append('\n');
        sb.Append("Endpoint = ").Append(relay.Endpoint).Append('\n');
        sb.Append("AllowedIPs = ").Append(string.Join(", ", allowed)).Append('\n');
        sb.Append("PersistentKeepalive = ").Append(KeepaliveSeconds.ToString(inv)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Impressão digital do que define o túnel (relay, jogo, blocos), sem a chave privada.</summary>
    public static string Fingerprint(RelayLink relay, IReadOnlyList<IpPrefix> gamePrefixes, string gameId)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(gamePrefixes);
        var text = string.Join("|", relay.Id, relay.Endpoint, relay.ClientAddressCidr, gameId, PrefixList.Format(PrefixList.Canonical(gamePrefixes)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>Lê a impressão digital gravada no arquivo (null se não houver).</summary>
    public static string? ReadFingerprint(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);
        var first = configText.Split('\n', 2)[0].TrimEnd('\r');
        return first.StartsWith(FingerprintPrefix, StringComparison.Ordinal) ? first[FingerprintPrefix.Length..].Trim() : null;
    }

    /// <summary>Ids de jogo aceitos: os perfis especiais, "generic" e "game-&lt;processo&gt;".</summary>
    public static bool IsSafeId(string? id) =>
        id is { Length: > 0 and <= 46 } && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') && char.IsAsciiLetterLower(id[0]);
}
