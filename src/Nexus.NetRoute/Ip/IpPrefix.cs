using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Nexus.NetRoute.Ip;

/// <summary>
/// Bloco de endereços (CIDR), IPv4 ou IPv6, sempre normalizado (bits de host zerados). É o que vai para o AllowedIPs do túnel:
/// só o tráfego para estes blocos passa pelo relay; o resto do PC continua pela rota normal.
/// </summary>
public readonly record struct IpPrefix
{
    public IPAddress Network { get; }
    public int Length { get; }

    public IpPrefix(IPAddress address, int length)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("Só IPv4 ou IPv6.", nameof(address));
        if (length < 0 || length > bits) throw new ArgumentOutOfRangeException(nameof(length), $"Prefixo /{length} fora de 0..{bits}.");
        Network = Mask(address, length);
        Length = length;
    }

    public bool IsV4 => Network.AddressFamily == AddressFamily.InterNetwork;
    public int MaxLength => IsV4 ? 32 : 128;

    public static IpPrefix Host(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return new IpPrefix(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }

    /// <summary>"a.b.c.d/nn" ou "a.b.c.d" (= /32); IPv6 idem. Recusa espaços, zonas (%) e máscaras fora do intervalo.</summary>
    public static bool TryParse(string? text, out IpPrefix prefix)
    {
        prefix = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 || text.Contains('%', StringComparison.Ordinal) || text.Trim() != text) return false;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addrText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addrText, out var addr)) return false;
        if (addr.AddressFamily == AddressFamily.InterNetwork && addrText.Count(c => c == '.') != 3) return false; // "1" ou "1.2" não
        if (addr.IsIPv4MappedToIPv6) return false;
        var max = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var len = max;
        if (slash >= 0)
        {
            var lenText = text[(slash + 1)..];
            if (lenText.Length is 0 or > 3 || !lenText.All(char.IsAsciiDigit)) return false;
            len = int.Parse(lenText, NumberStyles.None, CultureInfo.InvariantCulture);
            if (len > max) return false;
        }
        prefix = new IpPrefix(addr, len);
        return true;
    }

    public static IpPrefix Parse(string text) =>
        TryParse(text, out var p) ? p : throw new FormatException($"Bloco de endereços inválido: '{text}'.");

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6 && IsV4) address = address.MapToIPv4();
        if (address.AddressFamily != Network.AddressFamily) return false;
        return Mask(address, Length).Equals(Network);
    }

    public bool Contains(IpPrefix other) => other.Network.AddressFamily == Network.AddressFamily && other.Length >= Length && Contains(other.Network);

    public override string ToString() => $"{Network}/{Length.ToString(CultureInfo.InvariantCulture)}";

    private static IPAddress Mask(IPAddress address, int length)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitStart = i * 8;
            if (length >= bitStart + 8) continue;
            var keep = Math.Max(0, length - bitStart);
            bytes[i] &= (byte)(keep == 0 ? 0 : 0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }
}

/// <summary>
/// Endereços que nunca podem ir para o túnel nem ser alvo de medição pelo relay: redes privadas, loopback, link-local,
/// CGNAT, documentação, multicast e reservados (RFC 6890). Assim o túnel não captura a rede de casa e o relay não vira
/// ponte para a rede interna de ninguém.
/// </summary>
public static class PublicAddress
{
    private static readonly IpPrefix[] NonPublicV4 =
    [
        IpPrefix.Parse("0.0.0.0/8"), IpPrefix.Parse("10.0.0.0/8"), IpPrefix.Parse("100.64.0.0/10"), IpPrefix.Parse("127.0.0.0/8"),
        IpPrefix.Parse("169.254.0.0/16"), IpPrefix.Parse("172.16.0.0/12"), IpPrefix.Parse("192.0.0.0/24"), IpPrefix.Parse("192.0.2.0/24"),
        IpPrefix.Parse("192.88.99.0/24"), IpPrefix.Parse("192.168.0.0/16"), IpPrefix.Parse("198.18.0.0/15"), IpPrefix.Parse("198.51.100.0/24"),
        IpPrefix.Parse("203.0.113.0/24"), IpPrefix.Parse("224.0.0.0/4"), IpPrefix.Parse("240.0.0.0/4"),
    ];

    private static readonly IpPrefix GlobalUnicastV6 = IpPrefix.Parse("2000::/3");
    private static readonly IpPrefix DocumentationV6 = IpPrefix.Parse("2001:db8::/32");
    private static readonly IpPrefix TeredoV6 = IpPrefix.Parse("2001::/32");
    private static readonly IpPrefix SixToFourV6 = IpPrefix.Parse("2002::/16");

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return !NonPublicV4.Any(p => p.Contains(address));
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        return GlobalUnicastV6.Contains(address) && !DocumentationV6.Contains(address) && !TeredoV6.Contains(address) && !SixToFourV6.Contains(address);
    }

    /// <summary>Um bloco é público se o endereço de rede é público e ele não cobre nenhuma rede não pública.</summary>
    public static bool IsPublic(IpPrefix prefix)
    {
        if (!IsPublic(prefix.Network)) return false;
        if (!prefix.IsV4) return prefix.Length >= 16;
        return !NonPublicV4.Any(n => prefix.Contains(n) || n.Contains(prefix));
    }
}

/// <summary>Lista de blocos do túnel: validação (só públicos, tamanho mínimo, limite de quantidade) e forma canônica.</summary>
public static class PrefixList
{
    /// <summary>Limite de blocos num túnel: o suficiente para servidores de vários jogos, pequeno o bastante para não virar VPN do PC inteiro.</summary>
    public const int MaxPrefixes = 128;

    /// <summary>Bloco IPv4 mais largo aceito (/12 = 1 milhão de endereços). Mais largo que isso arrastaria serviços que não são do jogo.</summary>
    public const int MinLengthV4 = 12;

    public const int MinLengthV6 = 32;

    /// <summary>"-" = nenhum bloco (modo medição: o túnel só alcança o agente do relay).</summary>
    public const string None = "-";

    /// <summary>Lê "a/b,c/d" (forma do parâmetro da ação). Devolve null e o motivo quando algo não pode ir para o túnel.</summary>
    public static (IReadOnlyList<IpPrefix>? Prefixes, string? Problem) Parse(string? text)
    {
        if (text is null) return (null, "Lista de blocos ausente.");
        if (text == None) return ([], null);
        var parts = text.Split(',');
        if (parts.Length > MaxPrefixes) return (null, $"Mais de {MaxPrefixes} blocos.");
        var list = new List<IpPrefix>(parts.Length);
        foreach (var part in parts)
        {
            if (!IpPrefix.TryParse(part, out var p)) return (null, $"Bloco inválido: '{part}'.");
            if (p.IsV4 ? p.Length < MinLengthV4 : p.Length < MinLengthV6) return (null, $"Bloco largo demais: {p} (mínimo /{(p.IsV4 ? MinLengthV4 : MinLengthV6)}).");
            if (!PublicAddress.IsPublic(p)) return (null, $"Bloco não público: {p} (rede de casa, privada ou reservada nunca vai para o túnel).");
            list.Add(p);
        }
        return (Canonical(list), null);
    }

    /// <summary>Sem repetidos e sem blocos contidos em outros, em ordem estável (IPv4 antes, depois endereço e tamanho).</summary>
    public static IReadOnlyList<IpPrefix> Canonical(IEnumerable<IpPrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var sorted = prefixes.Distinct().OrderBy(p => p.IsV4 ? 0 : 1).ThenBy(p => p.Length).ToList();
        var kept = new List<IpPrefix>();
        foreach (var p in sorted)
            if (!kept.Any(k => k.Contains(p))) kept.Add(p);
        return kept.OrderBy(p => p.IsV4 ? 0 : 1).ThenBy(p => p.Network.GetAddressBytes(), ByteOrder.Instance).ThenBy(p => p.Length).ToList();
    }

    /// <summary>Tamanho máximo do parâmetro "prefixes" de uma ação (limite do catálogo para argumentos do usuário).</summary>
    public const int MaxParameterLength = 260;

    /// <summary>Regra do parâmetro no catálogo: "-" ou até 16 blocos IPv4 separados por vírgula.</summary>
    public const string ParameterPattern = @"-|[0-9]{1,3}(?:\.[0-9]{1,3}){3}/[0-9]{1,2}(?:,[0-9]{1,3}(?:\.[0-9]{1,3}){3}/[0-9]{1,2}){0,15}";

    /// <summary>
    /// Cabe a lista no parâmetro da ação: só IPv4 (o túnel do NEXUS é IPv4), em ordem de prioridade (os primeiros são os mais
    /// vistos). Se não couber, agrupa em /24; se ainda não couber, fica com os primeiros que cabem (no máximo 16).
    /// </summary>
    public static IReadOnlyList<IpPrefix> FitToParameter(IEnumerable<IpPrefix> byPriority)
    {
        ArgumentNullException.ThrowIfNull(byPriority);
        var v4 = byPriority.Where(p => p.IsV4 && p.Length >= MinLengthV4 && PublicAddress.IsPublic(p)).ToList();
        if (Format(Canonical(v4)).Length <= MaxParameterLength && Canonical(v4).Count <= 16) return Canonical(v4);
        var grouped = v4.Select(p => p.Length > 24 ? new IpPrefix(p.Network, 24) : p).Distinct().ToList();
        var kept = new List<IpPrefix>();
        foreach (var p in grouped)
        {
            var trial = Canonical([.. kept, p]);
            if (trial.Count > 16 || Format(trial).Length > MaxParameterLength) break;
            kept.Add(p);
        }
        return Canonical(kept);
    }

    public static string Format(IReadOnlyList<IpPrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        return prefixes.Count == 0 ? None : string.Join(",", prefixes.Select(p => p.ToString()));
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null || y is null) return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return x.Length.CompareTo(y.Length);
        }
    }
}
