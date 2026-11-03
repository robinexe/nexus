using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Nexus.HardwareDiscovery;

namespace Nexus.SystemTools.Checks;

// =====================================================================================================================
// Rede para jogos (somente leitura): ping, variação (jitter) e perda até o roteador e até um servidor, Wi-Fi ou cabo com o
// sinal, e o teste de bufferbloat (ping com a conexão cheia). Nada é gravado no disco; o teste com carga só roda depois de
// uma confirmação explícita na interface, baixa/envia bytes aleatórios do teste público da Cloudflare (nunca executáveis) e
// joga fora o que recebe. Regras e textos: Nexus.HardwareDiscovery.PcChecks.
// =====================================================================================================================

/// <summary>Um ping. Devolve o tempo de ida e volta em ms, ou null se não houve resposta.</summary>
public interface IPingClient
{
    Task<double?> PingAsync(string host, int timeoutMs, CancellationToken cancellationToken);
}

/// <summary>A conexão em uso: tipo, nome do adaptador, velocidade do link, roteador (gateway) e sinal do Wi-Fi.</summary>
public sealed record ActiveLink(LinkKind Kind, string? Name, long? SpeedMbps, string? Gateway);

public interface INetworkInfo
{
    /// <summary>A conexão que tem roteador (gateway IPv4); null se nenhuma.</summary>
    ActiveLink? Active();

    /// <summary>Qualidade do sinal do Wi-Fi conectado (0–100), ou null.</summary>
    int? WifiSignalPercent();
}

/// <summary>Carga para o teste de bufferbloat: baixa/envia até <c>maxBytes</c>, parando quando cancelado. Devolve os bytes transferidos.</summary>
public interface ILoadSource
{
    Task<long> DownloadAsync(long maxBytes, CancellationToken cancellationToken);
    Task<long> UploadAsync(long maxBytes, CancellationToken cancellationToken);
}

public static partial class GameNetworkTest
{
    /// <summary>Servidor padrão do ping até a internet (DNS público da Cloudflare, responde a ping no mundo todo).</summary>
    public const string DefaultHost = "1.1.1.1";
    public const int DefaultCount = 30;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);
    public const int TimeoutMs = 1000;

    [GeneratedRegex(@"\A(?=.{1,253}\z)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();

    /// <summary>Endereço que o usuário digitou: IPv4/IPv6 ou nome de host (letras, números, hífen e ponto). Nada mais é aceito.</summary>
    public static bool IsValidHost(string? host)
    {
        var h = host?.Trim();
        if (string.IsNullOrEmpty(h) || h.Length > 253) return false;
        if (IPAddress.TryParse(h, out var ip)) return ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
        return HostName().IsMatch(h);
    }

    /// <summary>"N" pings seguidos, um a cada <paramref name="interval"/>.</summary>
    public static async Task<PingStats> PingSeriesAsync(IPingClient ping, string host, int count, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ping);
        var rtts = new List<double?>(count);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            rtts.Add(await ping.PingAsync(host, TimeoutMs, cancellationToken).ConfigureAwait(false));
            var left = interval - Stopwatch.GetElapsedTime(started);
            if (left > TimeSpan.Zero && i < count - 1) await Task.Delay(left, cancellationToken).ConfigureAwait(false);
        }
        return PingStats.From(host, rtts);
    }

    /// <summary>Teste rápido (só ping): Wi-Fi/cabo, roteador e, se pedido, o servidor.</summary>
    public static async Task<NetworkCheckFacts> QuickAsync(INetworkInfo info, IPingClient ping, string? internetHost, int count, TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(ping);
        var link = info.Active();
        var kind = link?.Kind ?? LinkKind.Unknown;
        var signal = kind == LinkKind.WiFi ? info.WifiSignalPercent() : null;
        static async Task<PingStats?> Maybe(Task<PingStats> t) => await t.ConfigureAwait(false);
        var gatewayTask = link?.Gateway is { } gw ? Maybe(PingSeriesAsync(ping, gw, count, interval, cancellationToken)) : Task.FromResult<PingStats?>(null);
        var internetTask = internetHost is not null && IsValidHost(internetHost)
            ? Maybe(PingSeriesAsync(ping, internetHost.Trim(), count, interval, cancellationToken)) : Task.FromResult<PingStats?>(null);
        var gateway = await gatewayTask.ConfigureAwait(false);
        var internet = await internetTask.ConfigureAwait(false);
        return new NetworkCheckFacts(kind, link?.Name, signal, link?.SpeedMbps, gateway, internet);
    }
}

/// <summary>Plano do teste de bufferbloat (quanto tempo, quantos dados no máximo).</summary>
public sealed record BufferbloatPlan
{
    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan Phase { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>Os primeiros instantes de cada fase não contam (a conexão ainda está enchendo).</summary>
    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public int DownloadStreams { get; init; } = 4;
    public long DownloadBytesPerStream { get; init; } = 25L * 1000 * 1000;
    public int UploadStreams { get; init; } = 2;
    public long UploadBytesPerStream { get; init; } = 10L * 1000 * 1000;

    /// <summary>O máximo de dados que o teste pode usar (o que a confirmação mostra: "usa até ~120 MB").</summary>
    public long MaxBytes => DownloadStreams * DownloadBytesPerStream + UploadStreams * UploadBytesPerStream;
    public long MaxMegabytes => (MaxBytes + 999_999) / 1_000_000;
}

public static class BufferbloatTest
{
    /// <summary>
    /// Ping sem carga; ping enquanto baixa; ping enquanto envia. A latência com carga é a mediana dos pings depois do aquecimento.
    /// Cancelável a qualquer momento (para de baixar/enviar na hora).
    /// </summary>
    public static async Task<BufferbloatResult> RunAsync(IPingClient ping, ILoadSource load, string host, BufferbloatPlan plan,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ping);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(plan);
        progress?.Report("idle");
        var idleCount = Math.Max(3, (int)(plan.Idle / plan.PingInterval));
        var idle = await GameNetworkTest.PingSeriesAsync(ping, host, idleCount, plan.PingInterval, cancellationToken).ConfigureAwait(false);
        var idleMs = idle.MinMs is { } min && idle.AverageMs is { } avg ? (min + avg) / 2 : 0; // base: perto do melhor caso, sem um pico atrapalhar

        progress?.Report("download");
        var (downMs, downBytes, downSeconds) = await LoadedAsync(ping, host, plan,
            ct => Enumerable.Range(0, plan.DownloadStreams).Select(_ => load.DownloadAsync(plan.DownloadBytesPerStream, ct)), cancellationToken).ConfigureAwait(false);
        progress?.Report("upload");
        var (upMs, upBytes, upSeconds) = await LoadedAsync(ping, host, plan,
            ct => Enumerable.Range(0, plan.UploadStreams).Select(_ => load.UploadAsync(plan.UploadBytesPerStream, ct)), cancellationToken).ConfigureAwait(false);
        static double? Mbps(long bytes, double seconds) => bytes > 0 && seconds > 0.2 ? bytes * 8 / seconds / 1_000_000 : null;
        return new BufferbloatResult(idleMs, downMs, upMs, Mbps(downBytes, downSeconds), Mbps(upBytes, upSeconds), downBytes + upBytes);
    }

    private static async Task<(double? LoadedMs, long Bytes, double Seconds)> LoadedAsync(IPingClient ping, string host, BufferbloatPlan plan,
        Func<CancellationToken, IEnumerable<Task<long>>> start, CancellationToken cancellationToken)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phase.CancelAfter(plan.Phase);
        var started = Stopwatch.GetTimestamp();
        var transfers = start(phase.Token).Select(t => Swallow(t)).ToList();
        var all = Task.WhenAll(transfers);
        var samples = new List<double>();
        while (!all.IsCompleted && !phase.IsCancellationRequested)
        {
            var tick = Stopwatch.GetTimestamp();
            double? rtt;
            try { rtt = await ping.PingAsync(host, GameNetworkTest.TimeoutMs, phase.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (rtt is { } r && Stopwatch.GetElapsedTime(started) >= plan.Warmup) samples.Add(r);
            var left = plan.PingInterval - Stopwatch.GetElapsedTime(tick);
            if (left > TimeSpan.Zero)
                try { await Task.Delay(left, phase.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        await phase.CancelAsync().ConfigureAwait(false);
        var bytes = (await all.ConfigureAwait(false)).Sum();
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.Count == 0) return (null, bytes, seconds);
        samples.Sort();
        var median = samples.Count % 2 == 1 ? samples[samples.Count / 2] : (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2;
        return (median, bytes, seconds);
    }

    /// <summary>Transferência que falhou ou foi cancelada conta o que já tinha passado (0 se nada): o teste continua.</summary>
    private static async Task<long> Swallow(Task<long> t)
    {
        try { return await t.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException) { return 0; }
    }
}

/// <summary>"Sinal : 85%" da saída de <c>netsh wlan show interfaces</c> (em qualquer idioma: é a única linha "nome : NN%").</summary>
public static partial class WifiSignalParser
{
    [GeneratedRegex(@"^\s*[^:\r\n]{2,40}:\s*(\d{1,3})\s*%\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PercentLine();

    public static int? Parse(string? netshOutput)
    {
        if (string.IsNullOrEmpty(netshOutput)) return null;
        var m = PercentLine().Match(netshOutput);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v <= 100 ? v : null;
    }
}

/// <summary>Ping pelo .NET (ICMP, o mesmo do comando ping).</summary>
public sealed class SystemPingClient : IPingClient
{
    public async Task<double?> PingAsync(string host, int timeoutMs, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(timeoutMs), null, null, cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? Math.Max(0.5, reply.RoundtripTime) : null; // 0 ms = menos de 1 ms
        }
        catch (PingException) { return null; }
    }
}

/// <summary>Conexão em uso pelo .NET (NetworkInterface) e sinal do Wi-Fi pelo netsh (só leitura).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkInfo(IProcessRunner runner) : INetworkInfo
{
    public ActiveLink? Active()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var gw = ni.GetIPProperties().GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw is null) continue;
            var kind = ni.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => LinkKind.WiFi,
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
                    or NetworkInterfaceType.Ethernet3Megabit => LinkKind.Wired,
                _ => LinkKind.Other,
            };
            long? speed = ni.Speed > 0 ? ni.Speed / 1_000_000 : null;
            return new ActiveLink(kind, ni.Description, speed, gw.ToString());
        }
        return null;
    }

    public int? WifiSignalPercent()
    {
        try { return WifiSignalParser.Parse(runner.Run(SystemPaths.Tool("netsh.exe"), ["wlan", "show", "interfaces"], TimeSpan.FromSeconds(10)).Output); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { return null; }
    }
}

/// <summary>
/// Carga do teste de bufferbloat pelo servidor público de teste de velocidade da Cloudflare (speed.cloudflare.com), o mesmo que
/// a biblioteca oficial de código aberto da Cloudflare (@cloudflare/speedtest, licença MIT) usa por padrão. Recebe bytes
/// aleatórios (nunca um executável) e os descarta na memória; envia zeros. Não manda resultado nenhum para lugar nenhum.
/// </summary>
public sealed class CloudflareLoadSource : ILoadSource, IDisposable
{
    public const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=";
    public const string UploadUrl = "https://speed.cloudflare.com/__up";
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<long> DownloadAsync(long maxBytes, CancellationToken cancellationToken)
    {
        long total = 0;
        try
        {
            using var response = await _http.GetAsync(new Uri(DownloadUrl + maxBytes.ToString(CultureInfo.InvariantCulture)), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            int n;
            while (total < maxBytes && (n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) total += n;
        }
        catch (OperationCanceledException) { }
        return total;
    }

    public async Task<long> UploadAsync(long maxBytes, CancellationToken cancellationToken)
    {
        var counter = new CountingZeroStream(maxBytes);
        try
        {
            using var content = new StreamContent(counter, 64 * 1024);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await _http.PostAsync(new Uri(UploadUrl), content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        return counter.Position;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Fluxo de zeros com tamanho fixo que conta o que já foi lido (enviado).</summary>
    private sealed class CountingZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => Interlocked.Read(ref _position); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            Interlocked.Add(ref _position, n);
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
