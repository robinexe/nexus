using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nexus.NetRoute.Ip;
using Nexus.NetRoute.WireGuard;

namespace Nexus.NetRoute.Relay;

/// <summary>
/// Relay cadastrado: o que o NEXUS precisa para montar o túnel. Vem do link que o script do relay imprime
/// (<c>nexus_relay.py add &lt;código do PC&gt;</c>): <c>nexus-relay://v1/&lt;base64url&gt;</c>. Só informação pública — a chave privada
/// do PC nunca sai do PC (é gerada pelo aplicador e fica em %ProgramData%\NEXUS\netroute com acesso só de administradores).
/// <para>Formato binário compacto (cabe no limite de 260 caracteres de um parâmetro de ação), inteiros em ordem de rede:
/// versão(1)=1 · chave do relay(32) · impressão da chave do PC(8 = SHA-256[0..8] dos 32 bytes) · IPv4 do PC no túnel(4) ·
/// IPv4 do agente(4) · porta do agente(2) · porta do WireGuard(2) · host(1+n, ASCII ≤60) · nome(1+n, UTF-8 ≤40 bytes) ·
/// cidade(1+n, id ASCII ≤20; 0 = sem cidade). O mesmo formato está em tools/netroute-relay/nexus_relay.py (há teste com vetor
/// comum).</para>
/// </summary>
public sealed record RelayLink(string Name, string Host, int Port, string ServerPublicKey, IPAddress ClientAddress, IPAddress AgentAddress, int AgentPort,
    string? Place, string ClientKeyFingerprint)
{
    public const string Scheme = "nexus-relay://v1/";

    /// <summary>Regra do parâmetro da ação (catálogo): o link inteiro, sem espaços nem quebras, até 260 caracteres.</summary>
    public const string LinkPattern = @"nexus-relay://v1/[A-Za-z0-9_\-]{60,243}";

    public const int MaxHost = 60;
    public const int MaxNameBytes = 40;
    public const int MaxPlace = 20;

    /// <summary>Id estável do relay: "r" + 10 primeiros hex do SHA-256 da chave pública do relay.</summary>
    public string Id => IdFor(ServerPublicKey);

    public string Endpoint => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port.ToString(CultureInfo.InvariantCulture)}" : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";
    public string ClientAddressCidr => ClientAddress + "/32";
    public IPEndPoint AgentEndPoint => new(AgentAddress, AgentPort);

    public static string IdFor(string serverPublicKey) =>
        "r" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(serverPublicKey)))[..10];

    /// <summary>Impressão curta de uma chave pública (16 hex): o relay grava no link para quem foi autorizado.</summary>
    public static string FingerprintOf(string publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(WireGuardKey.Decode(publicKey)))[..16];

    public bool AuthorizedFor(string clientPublicKey) =>
        WireGuardKey.IsValid(clientPublicKey) && string.Equals(FingerprintOf(clientPublicKey), ClientKeyFingerprint, StringComparison.Ordinal);

    public static (RelayLink? Link, string? Problem) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "Link vazio.");
        text = text.Trim();
        if (!text.StartsWith(Scheme, StringComparison.Ordinal)) return (null, $"O link precisa começar com {Scheme}");
        if (!Regex.IsMatch(text, "\\A" + LinkPattern + "\\z", RegexOptions.None, TimeSpan.FromMilliseconds(200)))
            return (null, "Link com caracteres inválidos ou tamanho fora do esperado (copie a linha inteira que o relay imprimiu).");
        byte[] b;
        try { b = FromBase64Url(text[Scheme.Length..]); }
        catch (FormatException) { return (null, "Link corrompido (base64)."); }
        try
        {
            var r = new Reader(b);
            if (r.Byte() != 1) return (null, "Versão do link desconhecida: atualize o NEXUS ou o script do relay.");
            var serverKey = WireGuardKey.Encode(r.Bytes(32));
            var fp = Convert.ToHexStringLower(r.Bytes(8));
            var client = new IPAddress(r.Bytes(4));
            var agent = new IPAddress(r.Bytes(4));
            var agentPort = r.UInt16();
            var port = r.UInt16();
            var host = Encoding.ASCII.GetString(r.Bytes(r.Byte()));
            var name = Encoding.UTF8.GetString(r.Bytes(r.Byte()));
            var placeLen = r.Byte();
            var place = placeLen == 0 ? null : Encoding.ASCII.GetString(r.Bytes(placeLen));
            if (!r.AtEnd) return (null, "Link com dados a mais.");
            var link = new RelayLink(name, host, port, serverKey, client, agent, agentPort, place, fp);
            return link.Validate() is { } problem ? (null, problem) : (link, null);
        }
        catch (ArgumentOutOfRangeException) { return (null, "Link incompleto (copie a linha inteira)."); }
    }

    public string ToLink()
    {
        if (Validate() is { } problem) throw new InvalidOperationException(problem);
        var host = Encoding.ASCII.GetBytes(Host);
        var name = Encoding.UTF8.GetBytes(Name);
        var place = Place is null ? [] : Encoding.ASCII.GetBytes(Place);
        var buf = new List<byte> { 1 };
        buf.AddRange(WireGuardKey.Decode(ServerPublicKey));
        buf.AddRange(Convert.FromHexString(ClientKeyFingerprint));
        buf.AddRange(ClientAddress.GetAddressBytes());
        buf.AddRange(AgentAddress.GetAddressBytes());
        Span<byte> u16 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)AgentPort); buf.AddRange(u16.ToArray());
        BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)Port); buf.AddRange(u16.ToArray());
        buf.Add((byte)host.Length); buf.AddRange(host);
        buf.Add((byte)name.Length); buf.AddRange(name);
        buf.Add((byte)place.Length); buf.AddRange(place);
        return Scheme + ToBase64Url(buf.ToArray());
    }

    /// <summary>Confere todos os campos; devolve o motivo em pt-BR ou null se estiver tudo certo.</summary>
    public string? Validate()
    {
        if (Name.Length == 0 || Encoding.UTF8.GetByteCount(Name) > MaxNameBytes || Name.Any(ch => char.IsControl(ch) || ch is '[' or ']' or '=' or '#'))
            return "Nome do relay inválido (1 a 40 bytes, sem [ ] = #).";
        if (!ValidHost(Host)) return $"Endereço do relay inválido: '{Host}' (IPv4, IPv6 ou nome DNS até {MaxHost} caracteres).";
        if (IPAddress.TryParse(Host, out var hostIp) && !PublicAddress.IsPublic(hostIp)) return $"O relay precisa de endereço público ({Host}).";
        if (Port is < 1 or > 65535) return "Porta do relay inválida.";
        if (!WireGuardKey.IsValid(ServerPublicKey)) return "Chave do relay inválida.";
        if (ClientKeyFingerprint.Length != 16 || !ClientKeyFingerprint.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) return "Impressão da chave do PC inválida.";
        if (ClientAddress.AddressFamily != AddressFamily.InterNetwork || !InTunnelNet(ClientAddress)) return $"Endereço do PC no túnel inválido: {ClientAddress} (esperado 10.x.y.z ou 100.64-127.x.y).";
        if (AgentAddress.AddressFamily != AddressFamily.InterNetwork || !InTunnelNet(AgentAddress) || AgentAddress.Equals(ClientAddress)) return $"Agente do relay inválido: {AgentAddress}.";
        if (AgentPort is < 1 or > 65535) return "Porta do agente inválida.";
        if (Place is not null && (Place.Length > MaxPlace || Geo.Places.Find(Place) is null)) return $"Cidade do relay desconhecida: '{Place}'.";
        return null;
    }

    /// <summary>Redes aceitas dentro do túnel (nunca a rede de casa): 10/8 e CGNAT 100.64/10.</summary>
    private static readonly IpPrefix[] TunnelNets = [IpPrefix.Parse("10.0.0.0/8"), IpPrefix.Parse("100.64.0.0/10")];

    private static bool InTunnelNet(IPAddress a) => TunnelNets.Any(n => n.Contains(a));

    private static bool ValidHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > MaxHost) return false;
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetworkV6 ? !host.Contains('%', StringComparison.Ordinal) : host.Count(c => c == '.') == 3;
        return Regex.IsMatch(host, @"\A([A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}\z", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        b += (b.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException("base64url inválido") };
        return Convert.FromBase64String(b);
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;
        public bool AtEnd => _pos == data.Length;

        public byte Byte() => Bytes(1)[0];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));

        public byte[] Bytes(int n)
        {
            if (n < 0 || _pos + n > data.Length) throw new ArgumentOutOfRangeException(nameof(n));
            var r = data.AsSpan(_pos, n).ToArray();
            _pos += n;
            return r;
        }
    }
}
