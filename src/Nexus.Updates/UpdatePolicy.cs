using System.Security.Cryptography;
using System.Text;

namespace Nexus.Updates;

/// <summary>Regras fixas de confiança da atualização (endereços, limites, chave pública embutida).</summary>
public static class UpdatePolicy
{
    /// <summary>Repositório PÚBLICO só com os pacotes (o repositório do código é privado e seus assets não baixam sem login).</summary>
    public const string ReleasesRepository = "robinexe/nexus-releases";
    public const string ManifestFileName = "nexus-update.json";
    public const string SignatureFileName = "nexus-update.json.sig";
    public static readonly Uri DefaultFeed = new($"https://github.com/{ReleasesRepository}/releases/latest/download/{ManifestFileName}");
    public static readonly Uri ReleasesPage = new($"https://github.com/{ReleasesRepository}/releases/latest");
    /// <summary>Variável de ambiente que troca o endereço do manifesto (testes). A assinatura continua obrigatória.</summary>
    public const string FeedVariable = "NEXUS_UPDATE_FEED";

    public const long MaxInstallerBytes = 300L * 1024 * 1024;
    public const int MaxRedirects = 8;

    /// <summary>Hosts de onde o instalador pode vir (o GitHub redireciona o download para os dois últimos).</summary>
    public static IReadOnlySet<string> DownloadHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    /// <summary>Chave pública de confiança (ECDSA P-256). A privada fica só no segredo NEXUS_UPDATE_SIGNING_KEY do CI.</summary>
    public const string TrustedPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjwrUo8tTQXJT4E/SxZ5Y7QPjeRT0
        M6zHUc1xRFhxoACk6UA5BFe7ZFtRgqHyyMsNGzKyhA6WY0rA2PBCXdR3mg==
        -----END PUBLIC KEY-----
        """;

    /// <summary>Endereço do manifesto: <see cref="FeedVariable"/> (se for https/http absoluto) ou o padrão.</summary>
    public static Uri FeedUri(Func<string, string?>? env = null)
    {
        var value = (env ?? Environment.GetEnvironmentVariable)(FeedVariable);
        return Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u : DefaultFeed;
    }

    /// <summary>Endereço da assinatura: o do manifesto + ".sig" (no mesmo Release).</summary>
    public static Uri SignatureUri(Uri feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var b = new UriBuilder(feed) { Path = feed.AbsolutePath + ".sig" };
        return b.Uri;
    }

    /// <summary>Só https, porta padrão, sem usuário/senha, host da lista. Vale para o endereço inicial e para cada redirecionamento.</summary>
    public static bool IsAllowedDownload(Uri? url) =>
        url is { IsAbsoluteUri: true } && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo)
        && DownloadHosts.Contains(url.IdnHost);

    /// <summary>Nome de arquivo simples de instalador (.exe), sem caminho.</summary>
    public static bool IsSafeFileName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 120 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' || c is '-' || c is '_');

    /// <summary>Chave de confiança embutida.</summary>
    public static ECDsa CreateTrustedKey() => ImportPublicKey(TrustedPublicKeyPem);

    public static ECDsa ImportPublicKey(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize != 256) throw new UpdateException("A chave de atualização precisa ser ECDSA P-256.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Confere a assinatura (texto base64 de uma assinatura DER/RFC 3279, igual a <c>openssl dgst -sha256 -sign</c>) sobre os
    /// bytes exatos do manifesto. Qualquer problema (base64 ruim, DER ruim, chave diferente) = false.
    /// </summary>
    public static bool VerifySignature(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signatureFile, ECDsa trustedKey)
    {
        ArgumentNullException.ThrowIfNull(trustedKey);
        if (signatureFile.Length == 0 || signatureFile.Length > 4096) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(Encoding.ASCII.GetString(signatureFile).Trim()); }
        catch (FormatException) { return false; }
        if (sig.Length is < 8 or > 80) return false;
        try { return trustedKey.VerifyData(manifest, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Assina (para o CI e os testes): base64 da assinatura DER sobre os bytes exatos.</summary>
    public static string Sign(ReadOnlySpan<byte> manifest, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        return Convert.ToBase64String(privateKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }
}
