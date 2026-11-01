using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates.Vendor;

/// <summary>Falha esperada (sem internet, servidor recusou, tamanho errado...) com mensagem em português para a tela.</summary>
public sealed class VendorDriverException : Exception
{
    public VendorDriverException() { }
    public VendorDriverException(string message) : base(message) { }
    public VendorDriverException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Progresso do download: bytes, velocidade média recente e tempo restante estimado.</summary>
public readonly record struct VendorDownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Received / Total, 0, 1);
    public TimeSpan? Remaining => BytesPerSecond > 1 && Total > Received ? TimeSpan.FromSeconds((Total - Received) / BytesPerSecond) : null;
}

/// <summary>
/// Rede do módulo (só leitura do PC; grava apenas o instalador na pasta de downloads do usuário). Regras do Nexus.Updates
/// reaproveitadas: redirecionamentos seguidos à mão, cada destino conferido na lista de hosts ANTES de ler um byte; limite de
/// tamanho; nome temporário ".partial" (continua de onde parou com Range) e renomeado só no fim. A assinatura e o SHA-256 são
/// conferidos depois, e de novo no aplicador elevado.
/// </summary>
public sealed class VendorDriverClient : IDisposable
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NEXUS-Drivers/1.0";
    public static TimeSpan LookupTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan StallTimeout { get; } = TimeSpan.FromMinutes(2);
    private const int MaxTextBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Func<long> _clockMs;

    public VendorDriverClient(HttpMessageHandler? handler = null, Func<long>? clockMs = null)
    {
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _clockMs = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------ consultas (texto pequeno)

    /// <summary>GET de texto (XML/JSON/HTML) de um host de consulta permitido, com limite de tamanho e tempo.</summary>
    public async Task<string> GetTextAsync(Uri url, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            using var response = await SendFollowingAsync(url, VendorDriverPolicy.IsAllowedLookup, HttpMethod.Get, null, null, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new VendorDriverException($"O site {url.Host} respondeu {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxTextBytes) throw new VendorDriverException("Resposta grande demais.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxTextBytes) throw new VendorDriverException("Resposta grande demais.");
                buffer.Write(chunk, 0, read);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new VendorDriverException($"Sem resposta de {url.Host} (tempo esgotado). Confira a internet.");
        }
        catch (HttpRequestException ex)
        {
            throw new VendorDriverException($"Sem internet ou {url.Host} fora do ar ({ex.Message}).", ex);
        }
    }

    // ------------------------------------------------------------------ instalador

    /// <summary>Tamanho EXATO do instalador (HEAD, seguindo e conferindo cada redirecionamento).</summary>
    public async Task<long> ProbeSizeAsync(GpuVendor vendor, Uri url, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            using var response = await SendFollowingAsync(url, u => VendorDriverPolicy.IsAllowedDownload(vendor, u), HttpMethod.Head, null, RefererFor(vendor), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new VendorDriverException($"O servidor do fabricante recusou ({(int)response.StatusCode}).");
            var length = response.Content.Headers.ContentLength ?? 0;
            if (length <= 0 || length > VendorDriverPolicy.MaxInstallerBytes) throw new VendorDriverException("Tamanho do instalador desconhecido ou acima do limite.");
            return length;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new VendorDriverException("Sem resposta do servidor do fabricante (tempo esgotado). Confira a internet.");
        }
        catch (HttpRequestException ex)
        {
            throw new VendorDriverException($"Sem internet ou servidor do fabricante fora do ar ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// Baixa para <paramref name="destination"/> (nome final) usando "<paramref name="destination"/>.partial". Se o .partial
    /// existe e é menor, continua com Range (o servidor precisa responder 206 com o mesmo início; senão recomeça do zero). Confere
    /// o tamanho exato. Se o arquivo final já existe com o tamanho certo, é reaproveitado. Não executa nada.
    /// </summary>
    public async Task<string> DownloadAsync(GpuVendor vendor, Uri url, long exactSize, string destination, IProgress<VendorDownloadProgress>? progress,
        CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        if (!VendorDriverPolicy.IsAllowedDownload(vendor, url)) throw new VendorDriverException("Endereço do instalador fora dos servidores oficiais: recusado.");
        if (exactSize <= 0 || exactSize > VendorDriverPolicy.MaxInstallerBytes) throw new VendorDriverException("Tamanho do instalador fora do limite.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var final = new FileInfo(destination);
        if (final.Exists)
        {
            if (final.Length == exactSize && !final.Attributes.HasFlag(FileAttributes.ReparsePoint)) { progress?.Report(new(exactSize, exactSize, 0)); return destination; }
            final.Delete();
        }
        var partial = destination + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial) : null;
        if (existing is not null && (existing.Attributes.HasFlag(FileAttributes.ReparsePoint) || existing.Length >= exactSize)) { File.Delete(partial); existing = null; }
        long start = existing?.Length ?? 0;

        var attempt = 0;
        while (true)
        {
            try
            {
                await DownloadOnceAsync(vendor, url, exactSize, partial, start, progress, cancel).ConfigureAwait(false);
                break;
            }
            catch (VendorDriverException) when (attempt++ < 2 && !cancel.IsCancellationRequested && File.Exists(partial))
            {
                // Queda no meio: tenta continuar de onde parou (até 2 vezes).
                start = new FileInfo(partial).Length;
                if (start >= exactSize) { File.Delete(partial); start = 0; }
            }
        }
        if (new FileInfo(partial).Length != exactSize)
        {
            TryDelete(partial);
            throw new VendorDriverException("Download incompleto. Tente de novo.");
        }
        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private async Task DownloadOnceAsync(GpuVendor vendor, Uri url, long exactSize, string partial, long start, IProgress<VendorDownloadProgress>? progress,
        CancellationToken cancel)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        try
        {
            var range = start > 0 ? new RangeHeaderValue(start, null) : null;
            using var response = await SendFollowingAsync(url, u => VendorDriverPolicy.IsAllowedDownload(vendor, u), HttpMethod.Get, range, RefererFor(vendor), stall.Token).ConfigureAwait(false);
            if (start > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { From: { } from, Length: { } total } &&
                from == start && total == exactSize)
            {
                // continua
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                start = 0; // servidor não aceitou continuar: recomeça
                if (response.Content.Headers.ContentLength is { } len && len != exactSize)
                    throw new VendorDriverException("O servidor mandou um arquivo de tamanho diferente do conferido: recusado.");
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                TryDelete(partial);
                throw new VendorDriverException("O download parcial não confere com o servidor; recomeçando.");
            }
            else throw new VendorDriverException($"Download recusado pelo servidor ({(int)response.StatusCode}).");

            await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(partial, start == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true);
            var buffer = new byte[1 << 17];
            long received = start;
            long windowStartMs = _clockMs(), windowStartBytes = received;
            double speed = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout); // só cai se ficar parado
                received += read;
                if (received > exactSize) throw new VendorDriverException("O instalador veio maior que o conferido: descartado.");
                await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                var now = _clockMs();
                if (now - windowStartMs >= 1000)
                {
                    var instant = (received - windowStartBytes) * 1000.0 / (now - windowStartMs);
                    speed = speed <= 0 ? instant : speed * 0.7 + instant * 0.3;
                    windowStartMs = now;
                    windowStartBytes = received;
                }
                progress?.Report(new(received, exactSize, speed));
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new VendorDriverException("O download ficou parado por 2 minutos. Confira a internet e tente de novo (continua de onde parou).");
        }
        catch (HttpRequestException ex)
        {
            throw new VendorDriverException($"A conexão caiu durante o download ({ex.Message}).", ex);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new VendorDriverException("Espaço insuficiente no disco para o instalador. Libere espaço (página Limpeza) e tente de novo.", ex);
        }
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    private static Uri? RefererFor(GpuVendor vendor) => vendor == GpuVendor.Amd ? AmdDriverLookup.Referer : null;

    private async Task<HttpResponseMessage> SendFollowingAsync(Uri url, Func<Uri, bool> allowed, HttpMethod method, RangeHeaderValue? range, Uri? referer,
        CancellationToken ct)
    {
        for (var hop = 0; hop <= VendorDriverPolicy.MaxRedirects; hop++)
        {
            if (!allowed(url)) throw new VendorDriverException($"Endereço não permitido: {url.Scheme}://{url.Host}. Nada foi baixado.");
            using var request = new HttpRequestMessage(method, url);
            if (range is not null) request.Headers.Range = range;
            if (referer is not null) request.Headers.Referrer = referer;
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new VendorDriverException("Redirecionamento sem destino.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            return response;
        }
        throw new VendorDriverException("Redirecionamentos demais.");
    }

    /// <summary>SHA-256 (hex minúsculo) do arquivo, sem seguir link.</summary>
    public static string Sha256(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new FileNotFoundException("Instalador não encontrado.", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 17);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
