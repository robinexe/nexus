using System.Text.Json;
using Nexus.NetRoute.Relay;
using Nexus.NetRoute.WireGuard;

namespace Nexus.NetRoute.Handlers;

/// <summary>Identidade WireGuard do PC (par de chaves). A privada fica só no arquivo protegido do aplicador.</summary>
public sealed record NetRouteIdentity(string PrivateKey, string PublicKey, DateTimeOffset Created);

/// <summary>
/// Onde o aplicador guarda a rota de jogo: %ProgramData%\NEXUS\netroute (pasta de dados do aplicador de administrador).
/// <list type="bullet">
/// <item><c>identity.json</c>: par de chaves; acesso só de Administradores e SYSTEM.</item>
/// <item><c>client.pub</c>: chave pública (a interface mostra para o usuário levar ao relay).</item>
/// <item><c>relays\&lt;id&gt;.link</c>: links dos relays cadastrados (só informação pública).</item>
/// <item><c>NexusRota.conf</c>: configuração do túnel (contém a chave privada; acesso só de Administradores e SYSTEM).</item>
/// <item><c>wireguard-by-nexus</c>: marca de que o cliente WireGuard foi instalado pelo NEXUS (só então desfazer o remove).</item>
/// </list>
/// </summary>
public interface INetRouteStore
{
    string Directory { get; }
    NetRouteIdentity? ReadIdentity();
    void WriteIdentity(NetRouteIdentity identity);
    void DeleteIdentity();
    string? ReadPublicKey();
    IReadOnlyList<RelayLink> ReadRelays();
    RelayLink? ReadRelay(string id);
    void WriteRelay(RelayLink relay);
    void DeleteRelay(string id);
    bool ClientInstalledByNexus { get; set; }
}

/// <summary>Implementação em arquivos. No Windows, os arquivos com segredo recebem ACL só de Administradores e SYSTEM.</summary>
public sealed class FileNetRouteStore(string directory, Action<string>? protectSecretFile = null, Action<string>? protectDirectory = null) : INetRouteStore
{
    public const string IdentityFile = "identity.json";
    public const string PublicKeyFile = "client.pub";
    public const string RelaysFolder = "relays";
    public const string MarkerFile = "wireguard-by-nexus";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>Pasta padrão (lida também pela interface, sem administrador, para mostrar chave pública, relays e estado).</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", "netroute");

    public static string ForDataDirectory(string dataDirectory) => Path.Combine(dataDirectory, "netroute");

    /// <summary>Cria a pasta (e aplica a ACL: Administradores/SYSTEM total, Usuários só leitura) antes da primeira escrita.</summary>
    private void EnsureDirectory()
    {
        if (System.IO.Directory.Exists(Directory)) return;
        System.IO.Directory.CreateDirectory(Directory);
        protectDirectory?.Invoke(Directory);
    }

    public NetRouteIdentity? ReadIdentity()
    {
        var path = Path.Combine(Directory, IdentityFile);
        if (!File.Exists(path)) return null;
        var id = JsonSerializer.Deserialize<NetRouteIdentity>(File.ReadAllText(path), Json);
        if (id is null || !WireGuardKey.IsValid(id.PrivateKey) || WireGuardKey.PublicFromPrivate(id.PrivateKey) != id.PublicKey)
            throw new InvalidDataException("identity.json corrompido: apague a identidade pelo histórico e crie de novo.");
        return id;
    }

    public void WriteIdentity(NetRouteIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        EnsureDirectory();
        var path = Path.Combine(Directory, IdentityFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, "");
        protectSecretFile?.Invoke(tmp); // ACL antes de escrever o segredo
        File.WriteAllText(tmp, JsonSerializer.Serialize(identity, Json));
        File.Move(tmp, path, overwrite: true);
        File.WriteAllText(Path.Combine(Directory, PublicKeyFile), identity.PublicKey + "\n");
    }

    public void DeleteIdentity()
    {
        File.Delete(Path.Combine(Directory, IdentityFile));
        File.Delete(Path.Combine(Directory, PublicKeyFile));
    }

    public string? ReadPublicKey()
    {
        var path = Path.Combine(Directory, PublicKeyFile);
        try
        {
            if (!File.Exists(path)) return null;
            var key = File.ReadAllText(path).Trim();
            return WireGuardKey.IsValid(key) ? key : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<RelayLink> ReadRelays()
    {
        var folder = Path.Combine(Directory, RelaysFolder);
        if (!System.IO.Directory.Exists(folder)) return [];
        var list = new List<RelayLink>();
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.link").Order(StringComparer.Ordinal))
        {
            try
            {
                if (RelayLink.Parse(File.ReadAllText(file)).Link is { } l && Path.GetFileNameWithoutExtension(file) == l.Id) list.Add(l);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    public RelayLink? ReadRelay(string id) => IsRelayId(id) ? ReadRelays().FirstOrDefault(r => r.Id == id) : null;

    public void WriteRelay(RelayLink relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        EnsureDirectory();
        var folder = Path.Combine(Directory, RelaysFolder);
        System.IO.Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, relay.Id + ".link"), relay.ToLink());
    }

    public void DeleteRelay(string id)
    {
        if (!IsRelayId(id)) throw new ArgumentException("Id de relay inválido.", nameof(id));
        File.Delete(Path.Combine(Directory, RelaysFolder, id + ".link"));
    }

    public bool ClientInstalledByNexus
    {
        get => File.Exists(Path.Combine(Directory, MarkerFile));
        set
        {
            var path = Path.Combine(Directory, MarkerFile);
            if (value) { EnsureDirectory(); File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); }
            else File.Delete(path);
        }
    }

    /// <summary>"r" + 10 hex (forma de <see cref="RelayLink.Id"/>): nada de caminho, ponto ou barra.</summary>
    public static bool IsRelayId(string? id) =>
        id is { Length: 11 } && id[0] == 'r' && id.Skip(1).All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}

/// <summary>O que o sistema oferece aos handlers (no Windows: cliente WireGuard oficial e o serviço do túnel).</summary>
public interface INetRouteSystem
{
    /// <summary>Cliente WireGuard oficial instalado e com assinatura válida da WireGuard LLC.</summary>
    bool ClientInstalled { get; }
    void InstallClient();
    void UninstallClient();
    /// <summary>Serviço do túnel do NEXUS existe (WireGuardTunnel$NexusRota).</summary>
    bool TunnelInstalled { get; }
    /// <summary>Texto da configuração do túnel instalado (null se não houver).</summary>
    string? ReadTunnelConfig();
    void InstallTunnel(string configText);
    void RemoveTunnel();
}
