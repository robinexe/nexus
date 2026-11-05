using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Nexus.NetRoute.Relay;

/// <summary>
/// Agente de medição do relay (tools/netroute-relay/nexus_relay.py, comando serve). Só escuta no endereço do relay DENTRO do
/// túnel (ex.: 10.66.0.1:8787), então só quem tem túnel autorizado fala com ele. Responde:
/// <list type="bullet">
/// <item><c>GET /health</c> → {"ok":true,"name":...,"version":...}</item>
/// <item><c>GET /probe?target=&lt;ip&gt;&amp;count=&lt;n&gt;</c> → {"target":...,"samples":[ms ou null...]} (ping a partir do relay; só
/// endereços públicos, com limite de taxa).</item>
/// </list>
/// </summary>
public sealed class RelayAgentClient(HttpClient http, IPEndPoint agent)
{
    public const int MaxCount = 30;

    public Uri BaseUri { get; } = new($"http://{agent?.ToString() ?? throw new ArgumentNullException(nameof(agent))}/");

    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await http.GetAsync(new Uri(BaseUri, "health"), cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (JsonException) { return false; }
    }

    /// <summary>RTTs do relay até o alvo (null = sem resposta). Lista vazia = o agente recusou ou não respondeu.</summary>
    public async Task<IReadOnlyList<double?>> ProbeAsync(IPAddress target, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        count = Math.Clamp(count, 1, MaxCount);
        var uri = new Uri(BaseUri, string.Create(CultureInfo.InvariantCulture, $"probe?target={Uri.EscapeDataString(target.ToString())}&count={count}"));
        try
        {
            using var resp = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            return ParseProbe(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException) { return []; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return []; }
    }

    public static IReadOnlyList<double?> ParseProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("samples", out var s) || s.ValueKind != JsonValueKind.Array) return [];
            var list = new List<double?>();
            foreach (var v in s.EnumerateArray().Take(MaxCount))
                list.Add(v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 && d < 10_000 ? d : null);
            return list;
        }
        catch (JsonException) { return []; }
    }
}
