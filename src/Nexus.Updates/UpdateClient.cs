using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Nexus.Updates;

public enum UpdateCheckStatus
{
    /// <summary>A versão publicada não é mais nova que esta.</summary>
    UpToDate,
    /// <summary>Há versão mais nova, com assinatura e manifesto válidos.</summary>
    Available,
    /// <summary>Ainda não há nada publicado (manifesto não encontrado).</summary>
    NotPublished,
    /// <summary>Sem internet, assinatura inválida, manifesto recusado etc. Nada é baixado.</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, UpdateManifest? Manifest = null);

/// <summary>Progresso do download (bytes recebidos de quantos).</summary>
public readonly record struct DownloadProgress(long Received, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Received / Total, 0, 1);
}

/// <summary>
/// Procura e baixa atualizações. Ordem das conferências (qualquer falha para tudo e nada é executado):
/// assinatura ECDSA do manifesto com a chave embutida → manifesto válido (schema, canal, host, SHA-256, tamanho) → versão
/// estritamente maior → download só de hosts permitidos (inclusive cada redirecionamento) → tamanho exato → SHA-256 →
/// renomeia do nome temporário para o final.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ECDsa _trustedKey;
    private readonly bool _ownsKey;

    public UpdateClient(SemVersion current, ECDsa? trustedKey = null, Uri? feed = null, HttpMessageHandler? handler = null)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
        _ownsKey = trustedKey is null;
        _trustedKey = trustedKey ?? UpdatePolicy.CreateTrustedKey();
        Feed = feed ?? UpdatePolicy.FeedUri();
        // Redirecionamentos seguidos à mão: cada destino passa pela lista de hosts antes de qualquer byte ser lido.
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NEXUS-Updater", current.ToString()));
    }

    public SemVersion Current { get; }
    public Uri Feed { get; }
    public static TimeSpan CheckTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DownloadTimeout { get; } = TimeSpan.FromMinutes(30);

    public void Dispose()
    {
        _http.Dispose();
        if (_ownsKey) _trustedKey.Dispose();
    }

    // ------------------------------------------------------------------ procurar

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        var ct = timeout.Token;
        try
        {
            var manifest = await FetchSmallAsync(Feed, UpdateManifest.MaxManifestBytes, ct).ConfigureAwait(false);
            if (manifest is null) return new(UpdateCheckStatus.NotPublished, "Nenhuma versão publicada ainda.");
            var signature = await FetchSmallAsync(UpdatePolicy.SignatureUri(Feed), 4096, ct).ConfigureAwait(false);
            if (signature is null) return new(UpdateCheckStatus.Failed, "Atualização publicada sem assinatura: ignorada.");
            return Evaluate(manifest, signature);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(UpdateCheckStatus.Failed, "Sem resposta do servidor (tempo esgotado).");
        }
        catch (HttpRequestException ex)
        {
            return new(UpdateCheckStatus.Failed, $"Sem conexão com {Feed.Host} ({ex.Message}).");
        }
        catch (UpdateException ex)
        {
            return new(UpdateCheckStatus.Failed, ex.Message);
        }
    }

    /// <summary>Confere assinatura, manifesto e versão (sem rede). Público para o <c>nexus update --verify-manifest</c> e testes.</summary>
    public UpdateCheckResult Evaluate(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureFile)
    {
        if (!UpdatePolicy.VerifySignature(manifestBytes, signatureFile, _trustedKey))
            return new(UpdateCheckStatus.Failed, "Assinatura da atualização inválida: ignorada.");
        UpdateManifest m;
        try { m = UpdateManifest.Parse(manifestBytes); }
        catch (UpdateException ex) { return new(UpdateCheckStatus.Failed, ex.Message); }
        return m.Version > Current
            ? new(UpdateCheckStatus.Available, $"NEXUS {m.Version} disponível.", m)
            : new(UpdateCheckStatus.UpToDate, $"Você já tem a versão mais nova ({Current}).", m);
    }

    private async Task<byte[]?> FetchSmallAsync(Uri url, int limit, CancellationToken ct)
    {
        using var response = await GetFollowingAsync(url, IsAllowedFeedHop, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new UpdateException($"O servidor respondeu {(int)response.StatusCode} para {url.Host}.");
        if (response.Content.Headers.ContentLength > limit) throw new UpdateException("Resposta grande demais.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new UpdateException("Resposta grande demais.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Manifesto/assinatura: https (ou http só para localhost, nos testes). O conteúdo é protegido pela assinatura.</summary>
    private static bool IsAllowedFeedHop(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);

    private async Task<HttpResponseMessage> GetFollowingAsync(Uri url, Func<Uri, bool> allowed, CancellationToken ct)
    {
        for (var hop = 0; hop <= UpdatePolicy.MaxRedirects; hop++)
        {
            if (!allowed(url)) throw new UpdateException($"Endereço não permitido: {url.Scheme}://{url.Host}.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new UpdateException("Redirecionamento sem destino.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            return response;
        }
        throw new UpdateException("Redirecionamentos demais.");
    }

    // ------------------------------------------------------------------ baixar

    /// <summary>
    /// Baixa o instalador para <c>&lt;root&gt;\&lt;versão&gt;\</c> com nome temporário, confere tamanho e SHA-256 e só então
    /// renomeia. Se o arquivo final já existe e confere, é reaproveitado. Devolve o caminho do instalador conferido.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, string root, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(root);
        var inst = manifest.Installer;
        if (!UpdatePolicy.IsAllowedDownload(inst.Url)) throw new UpdateException("Endereço do instalador não permitido.");
        if (inst.Size <= 0 || inst.Size > UpdatePolicy.MaxInstallerBytes) throw new UpdateException("Tamanho do instalador fora do limite.");

        var dir = Path.Combine(root, manifest.Version.ToString());
        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, inst.FileName(manifest.Version));
        if (File.Exists(final))
        {
            if (Matches(final, inst)) { progress?.Report(new(inst.Size, inst.Size)); return final; }
            File.Delete(final);
        }
        var partial = final + ".partial";
        File.Delete(partial);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        var ct = timeout.Token;
        try
        {
            using (var response = await GetFollowingAsync(inst.Url, UpdatePolicy.IsAllowedDownload, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new UpdateException($"Download recusado pelo servidor ({(int)response.StatusCode}).");
                if (response.Content.Headers.ContentLength is { } length && length != inst.Size)
                    throw new UpdateException("Tamanho do instalador diferente do anunciado.");
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > inst.Size) throw new UpdateException("O instalador veio maior que o anunciado.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(new(received, inst.Size));
                }
                if (received != inst.Size) throw new UpdateException("Download incompleto.");
                if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(inst.Sha256, StringComparison.Ordinal))
                    throw new UpdateException("O instalador baixado não confere (SHA-256 diferente): descartado.");
            }
            File.Move(partial, final, overwrite: true);
            return final;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(partial);
            throw new UpdateException("Download demorou demais e foi interrompido.");
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Tamanho e SHA-256 do arquivo conferem com o manifesto.</summary>
    public static bool Matches(string path, UpdateInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != installer.Size || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(installer.Sha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Apaga downloads de outras versões (só subpastas com nome de versão dentro de <paramref name="root"/>).</summary>
    public static void CleanOld(string root, SemVersion? keep)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || !SemVersion.TryParse(dir.Name, out var v) || v == keep) continue;
                foreach (var f in dir.EnumerateFiles()) TryDelete(f.FullName);
                try { dir.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
