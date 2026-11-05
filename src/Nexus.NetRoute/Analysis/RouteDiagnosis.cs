using System.Globalization;
using System.Net;
using Nexus.NetRoute.Geo;
using Nexus.NetRoute.Probe;
using Nexus.NetRoute.Stats;

namespace Nexus.NetRoute.Analysis;

public enum RouteVerdict
{
    /// <summary>Sem resposta do servidor nem dos últimos saltos: não dá para afirmar nada.</summary>
    NoData,
    /// <summary>Dentro do esperado para a distância: nenhum relay vai melhorar o ping.</summary>
    Good,
    /// <summary>Excesso entre 8 e 25 ms: um relay bem posicionado pode ajudar; só a medição A/B decide.</summary>
    ModerateExcess,
    /// <summary>Excesso acima de 25 ms: a rota do provedor faz desvio; relay tem boa chance.</summary>
    HighExcess,
    /// <summary>Perda que começa num salto e segue até o destino.</summary>
    LossOnPath,
    /// <summary>Oscilação alta (jitter/p99) com mediana normal.</summary>
    Unstable,
    /// <summary>A cidade do servidor não é conhecida: só dá para mostrar a rota, sem régua física.</summary>
    UnknownServerPlace,
}

public enum FindingKind { BigJump, DetourAbroad, LossFromHop, LocalNetworkSlow, ServerPlaceGuessed, ServerPlaceImpossible }

/// <summary>Um achado do diagnóstico, com o salto onde aparece e o texto em pt-BR (a interface traduz pelo Kind).</summary>
public sealed record RouteFinding(FindingKind Kind, int? Ttl, string Text);

/// <summary>Resultado do diagnóstico de UMA rota: régua física, excesso, saltos e o veredito em palavras simples.</summary>
public sealed record RouteReport(
    IPAddress Server,
    Place? UserPlace,
    Place? ServerPlace,
    double? DistanceKm,
    double? FloorMs,
    double? GoodUpperMs,
    LatencyStats Direct,
    double? ExcessMs,
    RouteVerdict Verdict,
    IReadOnlyList<RouteFinding> Findings,
    TraceResult? Trace)
{
    public string Summary => RouteDiagnosis.Summarize(this);
}

/// <summary>
/// Diagnóstico da rota até o servidor. Não mexe em nada: junta a medição direta, o traceroute e a régua física
/// (<see cref="PhysicsFloor"/>) e diz se existe excesso que um relay poderia remover. A decisão de ligar o túnel é outra
/// etapa (<see cref="Decision.TunnelDecision"/>), sempre com medição A/B.
/// </summary>
public static class RouteDiagnosis
{
    /// <summary>Excesso a partir do qual vale testar um relay (abaixo disso a variação normal já engole o ganho).</summary>
    public const double ModerateExcessMs = 8.0;
    public const double HighExcessMs = 25.0;
    /// <summary>Aumento entre saltos consecutivos que chama atenção (num trecho curto não deveria passar disso).</summary>
    public const double BigJumpMs = 20.0;
    public const double LossThresholdPercent = 2.0;
    public const double UnstableP99OverP50Ms = 30.0;

    public static RouteReport Diagnose(IPAddress server, Place? userPlace, Place? serverPlaceHint, LatencyStats direct, TraceResult? trace)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(direct);
        var findings = new List<RouteFinding>();
        var c = CultureInfo.InvariantCulture;

        // Cidade do servidor: a indicada pelo perfil do jogo; senão, o nome do último salto que tem cidade conhecida.
        var serverPlace = serverPlaceHint;
        if (serverPlace is null && trace is not null)
        {
            var named = trace.Hops.Where(h => h.Responded).Reverse().Take(3).Select(h => Places.FromHostName(h.HostName)).FirstOrDefault(p => p is not null);
            if (named is not null)
            {
                serverPlace = named;
                findings.Add(new RouteFinding(FindingKind.ServerPlaceGuessed, null, $"Cidade do servidor deduzida pelo nome dos últimos roteadores: {named.Name}."));
            }
        }

        // RTT usado na régua: o do servidor; se ele não responde a ping, o do último salto que respondeu (aproximação, avisada).
        var observed = direct.P50Ms;
        if (observed is null && trace?.Hops.LastOrDefault(h => h.Responded) is { } lastHop)
            observed = lastHop.BestMs;

        double? distance = null, floor = null, upper = null, excess = null;
        if (userPlace is not null && serverPlace is not null)
        {
            distance = PhysicsFloor.DistanceKm(userPlace, serverPlace);
            floor = PhysicsFloor.FloorMs(distance.Value);
            upper = PhysicsFloor.GoodUpperMs(distance.Value);
            if (observed is { } o)
            {
                if (PhysicsFloor.ImpossiblyFast(o, distance.Value))
                {
                    findings.Add(new RouteFinding(FindingKind.ServerPlaceImpossible, null,
                        string.Create(c, $"{o:0} ms é rápido demais para {serverPlace.Name} (piso {floor:0} ms): o servidor está mais perto. A régua não vale aqui.")));
                    serverPlace = null; distance = null; floor = null; upper = null;
                }
                else excess = PhysicsFloor.ExcessMs(o, distance.Value);
            }
        }

        if (trace is not null) findings.AddRange(TraceFindings(trace, userPlace, serverPlace));

        var verdict = Verdict(direct, observed, excess, serverPlace is not null && userPlace is not null, findings);
        return new RouteReport(server, userPlace, serverPlace, distance, floor, upper, direct, excess, verdict, findings, trace);
    }

    private static RouteVerdict Verdict(LatencyStats direct, double? observed, double? excess, bool hasRuler, List<RouteFinding> findings)
    {
        if (observed is null) return RouteVerdict.NoData;
        if (findings.Any(f => f.Kind == FindingKind.LossFromHop) || direct.LossPercent >= LossThresholdPercent * 2) return RouteVerdict.LossOnPath;
        if (!hasRuler) return RouteVerdict.UnknownServerPlace;
        if (excess >= HighExcessMs) return RouteVerdict.HighExcess;
        if (excess >= ModerateExcessMs) return RouteVerdict.ModerateExcess;
        if (direct is { P99Ms: { } p99, P50Ms: { } p50 } && p99 - p50 >= UnstableP99OverP50Ms) return RouteVerdict.Unstable;
        return RouteVerdict.Good;
    }

    public static IReadOnlyList<RouteFinding> TraceFindings(TraceResult trace, Place? userPlace, Place? serverPlace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var c = CultureInfo.InvariantCulture;
        var list = new List<RouteFinding>();
        var responded = trace.Hops.Where(h => h.Responded && h.BestMs is not null).ToList();

        // Rede de casa lenta: o primeiro salto (roteador) já passa de 10 ms → Wi-Fi ruim ou roteador sobrecarregado.
        if (responded.FirstOrDefault() is { Ttl: 1, BestMs: { } first } && first > 10)
            list.Add(new RouteFinding(FindingKind.LocalNetworkSlow, 1, string.Create(c, $"O primeiro salto (seu roteador) já leva {first:0} ms: a rede de casa (Wi-Fi ou roteador) pesa antes do provedor.")));

        // Saltos: um aumento grande que SE MANTÉM nos saltos seguintes é desvio real (um pico isolado é só ICMP lento).
        for (var i = 1; i < responded.Count; i++)
        {
            var prev = responded[i - 1].BestMs!.Value;
            var cur = responded[i].BestMs!.Value;
            if (cur - prev < BigJumpMs) continue;
            var sustained = responded.Skip(i + 1).Take(2).All(h => h.BestMs >= cur - BigJumpMs / 2);
            if (!sustained) continue;
            var where = responded[i].HostName ?? responded[i].Address?.ToString() ?? "?";
            list.Add(new RouteFinding(FindingKind.BigJump, responded[i].Ttl, string.Create(c, $"Salto {responded[i].Ttl} ({where}) acrescenta {cur - prev:0} ms de uma vez e o aumento se mantém até o fim.")));
        }

        // Desvio pelo exterior: os dois lados no Brasil, mas algum roteador do meio tem cidade de fora.
        if (userPlace is { InBrazil: true } && serverPlace is { InBrazil: true })
            foreach (var hop in responded)
                if (Places.FromHostName(hop.HostName) is { InBrazil: false } abroad)
                {
                    list.Add(new RouteFinding(FindingKind.DetourAbroad, hop.Ttl, $"Você e o servidor estão no Brasil, mas o salto {hop.Ttl} ({hop.HostName}) parece estar em {abroad.Name}: a rota sai do país e volta."));
                    break;
                }

        // Perda que começa num salto e continua até o destino (perda só no meio é ICMP limitado, não conta).
        if (trace.ReachedTarget && trace.Hops.Count >= 2)
        {
            static double Loss(TraceHop h) => h.Samples.Count == 0 ? 0 : 100.0 * h.Samples.Count(s => s is null) / h.Samples.Count;
            var last = trace.Hops[^1];
            if (Loss(last) >= 20)
            {
                var start = trace.Hops.Count - 1;
                while (start > 0 && Loss(trace.Hops[start - 1]) >= 20 && trace.Hops[start - 1].Responded) start--;
                var origin = trace.Hops[start];
                list.Add(new RouteFinding(FindingKind.LossFromHop, origin.Ttl, string.Create(c,
                    $"Perda de {Loss(last):0}% no destino que já aparece desde o salto {origin.Ttl} ({origin.HostName ?? origin.Address?.ToString() ?? "?"}).")));
            }
        }
        return list;
    }

    public static string Summarize(RouteReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var c = CultureInfo.InvariantCulture;
        var ping = r.Direct.P50Ms is { } p ? string.Create(c, $"{p:0} ms") : "sem resposta ao ping";
        var ruler = r.FloorMs is { } f && r.GoodUpperMs is { } u && r.ServerPlace is { } sp && r.UserPlace is { } up
            ? string.Create(c, $"De {up.Name} até {sp.Name} o mínimo físico é {f:0} ms e uma rota boa fica até {u:0} ms.")
            : "";
        return r.Verdict switch
        {
            RouteVerdict.NoData => "O servidor e os últimos roteadores não responderam: não dá para medir esta rota agora.",
            RouteVerdict.Good => $"Rota boa: {ping}. {ruler} Nenhum relay vai baixar este ping.".Trim(),
            RouteVerdict.ModerateExcess => string.Create(c, $"Rota com excesso de {r.ExcessMs:0} ms ({ping}). {ruler} Um relay pode ajudar; só o teste com e sem decide."),
            RouteVerdict.HighExcess => string.Create(c, $"Rota com desvio: {r.ExcessMs:0} ms acima do esperado ({ping}). {ruler} Vale testar um relay."),
            RouteVerdict.LossOnPath => $"Perda de pacotes no caminho ({r.Direct.Describe()}). Perda causa teleporte e tiro que não registra; o teste com relay mostra se outro caminho evita.",
            RouteVerdict.Unstable => $"Ping médio normal ({ping}), mas com picos ({r.Direct.Describe()}). Picos assim costumam vir da rede de casa (Wi-Fi, alguém baixando) ou de congestionamento no provedor.",
            RouteVerdict.UnknownServerPlace => $"Ping {ping}. A cidade do servidor não é conhecida, então não há régua física; o teste com e sem relay ainda funciona.",
            _ => ping,
        };
    }
}
