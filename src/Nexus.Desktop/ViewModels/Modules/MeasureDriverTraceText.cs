using System.Globalization;
using Nexus.Broker;
using Nexus.DriverUpdates;
using Nexus.HardwareDiscovery;
using Nexus.Measurement;

namespace Nexus.Desktop.ViewModels;

/// <summary>Um culpado típico de atraso de drivers e o que fazer, em palavras simples.</summary>
public sealed record DriverAdviceRow(string Title, string Advice);

/// <summary>Uma linha do "quem mais segurou o processador" (texto pronto; o kernel vem marcado para a tela explicar).</summary>
public sealed record DriverTraceRow(string Text, bool IsKernelCore);

/// <summary>
/// Frases e decisões do "Atraso de drivers" com a investigação como administrador (lógica pura, sem WPF, testada em
/// MeasureDriverTraceTests): provável culpado com o dispositivo do inventário, top 5, limites honestos, conselho por categoria, o
/// resumo para o Diagnóstico e a leitura do resultado do aplicador.
/// </summary>
public static class DriverTraceText
{
    public const string TraceActionId = "measure.driver-latency.trace@1";
    private static CultureInfo Pt => Loc.Culture;

    /// <summary>"Provável culpado: ndis.sys (Intel(R) Wi-Fi 6 AX201) — DPC máximo 1,8 ms"; vazio sem culpado.</summary>
    public static string Culprit(DriverTraceResult? trace, Func<string, DriverModuleInfo?> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        if (trace?.Culprit is not { } c) return "";
        var worst = c.DpcMaxUs >= c.IsrMaxUs ? Loc.F("Dpc.WorstDpc", Ms(c.DpcMaxUs)) : Loc.F("Dpc.WorstIsr", Ms(c.IsrMaxUs));
        return describe(c.Module)?.Device is { } dev ? Loc.F("Dpc.CulpritLine", c.Module, dev, worst) : Loc.F("Dpc.CulpritLineNoDevice", c.Module, worst);
    }

    /// <summary>Investigação feita e nenhum driver passou de 0,5 ms; vazio nos outros casos.</summary>
    public static string NoCulprit(DriverTraceResult? trace) => trace is { Culprit: null } t ? Loc.F("Dpc.NoCulprit", t.DurationSeconds) : "";

    /// <summary>Top 5 por pior chamada (inclui o kernel, marcado), com o dispositivo ou a descrição do arquivo quando há.</summary>
    public static IReadOnlyList<DriverTraceRow> Top(DriverTraceResult? trace, Func<string, DriverModuleInfo?> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        if (trace is null) return [];
        return trace.Top5.Select(m =>
        {
            var info = describe(m.Module);
            var who = info?.Device ?? info?.FileDescription;
            var name = who is null ? m.Module : Loc.F("Dpc.ModuleWithDevice", m.Module, who);
            return new DriverTraceRow(Loc.F("Dpc.TopLine", name, Ms(m.WorstUs), m.DpcCount + m.IsrCount, m.TotalMs), m.IsKernelCore);
        }).ToList();
    }

    /// <summary>Limites honestos: amostra curta; máquina virtual; chamadas sem módulo (quando ≥ 10%).</summary>
    public static string Note(DriverTraceResult? trace, bool hostIsVm)
    {
        if (trace is not { } t) return "";
        var parts = new List<string> { Loc.F("Dpc.TraceNote", t.DurationSeconds) };
        if (t.VirtualMachine == true || hostIsVm) parts.Add(Loc.T("Dpc.TraceVm"));
        if (t.Unresolved > 0 && t.EventCount > 0 && t.Unresolved * 10 >= t.EventCount) parts.Add(Loc.F("Dpc.TraceUnresolved", Math.Round(100.0 * t.Unresolved / t.EventCount)));
        return string.Join(" ", parts);
    }

    /// <summary>Com culpado apontado: o conselho da categoria dele (rede/áudio/armazenamento/vídeo/USB), sem prometer. Sem culpado e
    /// veredito fora de "Bom": os culpados típicos. Senão, nada.</summary>
    public static IReadOnlyList<DriverAdviceRow> Advice(DriverLatencyResult? result, DriverTraceResult? trace, Func<string, DriverModuleInfo?> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        if (trace?.Culprit is { } c)
            return [AdviceFor(describe(c.Module)?.Category ?? DriverModuleLookup.CategoryOf(c.Module, null), c.Module)];
        if (result is not { Level: DriverLatencyLevel.MayStutter or DriverLatencyLevel.Bad }) return [];
        return
        [
            new(Loc.T("Dpc.Culprit.Wifi"), Loc.T("Dpc.Advice.Wifi")),
            new(Loc.T("Dpc.Culprit.Audio"), Loc.T("Dpc.Advice.Audio")),
            new(Loc.T("Dpc.Culprit.Network"), Loc.T("Dpc.Advice.Network")),
            new(Loc.T("Dpc.Culprit.Video"), Loc.T("Dpc.Advice.Video")),
        ];
    }

    public static DriverAdviceRow AdviceFor(DriverCategory category, string module) => category switch
    {
        DriverCategory.Network => new(Loc.T("Dpc.Culprit.Network"), Loc.T("Dpc.Advice.NetworkTraced")),
        DriverCategory.Audio => new(Loc.T("Dpc.Culprit.Audio"), Loc.T("Dpc.Advice.Audio")),
        DriverCategory.Storage => new(Loc.T("Dpc.Culprit.Storage"), Loc.T("Dpc.Advice.Storage")),
        DriverCategory.Video => new(Loc.T("Dpc.Culprit.Video"), Loc.T("Dpc.Advice.Video")),
        DriverCategory.Usb => new(Loc.T("Dpc.Culprit.Usb"), Loc.T("Dpc.Advice.Usb")),
        _ => new(module, Loc.T("Dpc.Advice.Unknown")),
    };

    /// <summary>Resumo para o Diagnóstico/Problemas (null enquanto não há veredito dos contadores).</summary>
    public static DriverLatencyFacts? Facts(DriverLatencyResult? result, DriverTraceResult? trace, Func<string, DriverModuleInfo?> describe)
    {
        ArgumentNullException.ThrowIfNull(describe);
        if (result is not { Level: not DriverLatencyLevel.Unknown } r) return null;
        var culprit = trace?.Culprit;
        return new DriverLatencyFacts(r.Level.ToString(), r.PeakPercent, trace is not null, culprit?.Module,
            culprit is null ? null : describe(culprit.Module)?.Device, culprit?.WorstUs, trace?.VirtualMachine);
    }

    /// <summary>
    /// O que o aplicador respondeu + o arquivo que ele gravou: (resultado, "") quando deu certo; (null, frase) quando cancelou, falhou
    /// ou o arquivo não apareceu/é velho (antes de <paramref name="started"/> menos 2 min).
    /// </summary>
    public static (DriverTraceResult? Trace, string Status) Outcome(BrokerCallResult? reply, Func<DriverTraceResult?> load, DateTimeOffset started)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (reply is null) return (null, Loc.T("Dpc.Trace.Cancelled"));
        var item = reply.Response?.Results is { Count: > 0 } items ? items[0] : null;
        if (item?.Outcome != "Verified")
            return (null, Loc.F("Dpc.Trace.Failed", item?.Detail ?? reply.Response?.Error?.Detail ?? reply.Message));
        var trace = load();
        return trace is null || trace.TakenAt < started.AddMinutes(-2) ? (null, Loc.T("Dpc.Trace.NoFile")) : (trace, "");
    }

    /// <summary>µs → "1,8 ms" (≥ 1000) ou "420 µs".</summary>
    public static string Ms(double us) => us >= 1000 ? (us / 1000).ToString("0.#", Pt) + " ms" : us.ToString("0", Pt) + " µs";
}
