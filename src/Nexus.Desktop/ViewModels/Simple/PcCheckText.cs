using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.ViewModels.Simple;

// =====================================================================================================================
// Checagens do PC na interface simples (lógica pura, testada em PcChecksDesktopTests): o que foi lido por último e os achados
// em palavras simples, no idioma da interface, com os números de cada leitura ("O monitor está a 60 Hz, mas aceita 144 Hz").
// As regras e a evidência técnica ficam em Nexus.HardwareDiscovery.PcChecks (a página Diagnóstico mostra essa parte).
// =====================================================================================================================

/// <summary>A última leitura de cada checagem (null = ainda não leu).</summary>
public sealed record PcCheckState
{
    public NetworkCheckFacts? Network { get; init; }
    public IReadOnlyList<DiskHealthFacts>? Disks { get; init; }
    public IReadOnlyList<GameOnDisk> Games { get; init; } = [];
    public IReadOnlyList<DisplayCheckFacts>? Displays { get; init; }
    public ThrottleFacts? Throttle { get; init; }

    /// <summary>Todos os achados das checagens, pela ordem: rede, discos, monitor, processador.</summary>
    public IReadOnlyList<DiagnosticFinding> Findings()
    {
        var list = new List<DiagnosticFinding>();
        if (Network is { } n) list.AddRange(PcChecks.Network(n));
        if (Disks is { } d) list.AddRange(PcChecks.Disks(d, Games));
        if (Displays is { } m) list.AddRange(PcChecks.Displays(m));
        if (Throttle is { } t && PcChecks.Throttle(t) is { } tf) list.Add(tf);
        return list;
    }

    /// <summary>O monitor de um achado "display.refresh.&lt;device&gt;" (para o botão "Usar 144 Hz").</summary>
    public DisplayCheckFacts? DisplayFor(string findingId) =>
        findingId.StartsWith(PcChecks.DisplayRefreshPrefix, StringComparison.Ordinal)
            ? Displays?.FirstOrDefault(m => m.Device == findingId[PcChecks.DisplayRefreshPrefix.Length..]) : null;
}

public static class PcCheckText
{
    /// <summary>Frases simples para os achados que pedem atenção (o resto aparece só nas páginas avançadas).</summary>
    public static IReadOnlyDictionary<string, PlainFinding> Plain(PcCheckState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var map = new Dictionary<string, PlainFinding>(StringComparer.Ordinal);
        foreach (var f in state.Findings().Where(f => f.Severity == DiagnosticSeverity.Attention))
            if (For(f, state) is { } p) map[f.Id] = p;
        return map;
    }

    public static PlainFinding? For(DiagnosticFinding f, PcCheckState s)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(s);
        var topic = PlainFindings.TopicOf(f.Id);
        var net = s.Network;
        switch (f.Id)
        {
            case PcChecks.LinkId:
                return net?.WifiSignalPercent is { } sig && sig < PcChecks.WeakWifiPercent
                    ? new(Loc.F("Pc.Net.WeakWifi", sig), Loc.T("Pc.Net.WifiDo"), topic, "Icon.Network")
                    : new(Loc.T("Pc.Net.WifiUnstable"), Loc.T("Pc.Net.WifiDo"), topic, "Icon.Network");
            case PcChecks.LocalId:
                return new(Loc.T("Pc.Net.Local"), Loc.T("Pc.Net.LocalDo"), topic, "Icon.Network");
            case PcChecks.InternetId when net?.Internet is { } i:
                if (i.LossPercent >= PcChecks.LossAttentionPercent)
                    return new(Loc.F("Pc.Net.Loss", Math.Round(i.LossPercent)), Loc.T("Pc.Net.IspDo"), topic, "Icon.Network");
                return new(Loc.F("Pc.Net.Jitter", Math.Round(i.JitterMs ?? 0)), Loc.T("Pc.Net.IspDo"), topic, "Icon.Network");
            case PcChecks.BufferbloatId when net?.Bufferbloat?.WorstIncreaseMs is { } inc:
                return new(Loc.F("Pc.Net.Bloat", Math.Round(inc)), Loc.T("Pc.Net.BloatDo"), topic, "Icon.Network");
            case PcChecks.GamesOnSlowDiskId:
                var onHdd = s.Games.Count(g => s.Disks?.FirstOrDefault(d => d.Number == g.DiskNumber)?.Media == DiskMedia.Hdd);
                return new(onHdd == 1 ? Loc.T("Pc.Disk.Games1") : Loc.F("Pc.Disk.GamesN", onHdd), Loc.T("Pc.Disk.GamesDo"), topic, "Icon.Games");
            case PcChecks.ThrottleId:
                return new(Loc.T("Pc.Cpu.Limited"), Loc.T("Pc.Cpu.LimitedDo"), topic, "Icon.Cpu");
        }
        if (f.Id.StartsWith(PcChecks.DiskPrefix, StringComparison.Ordinal) &&
            s.Disks?.FirstOrDefault(d => PcChecks.DiskPrefix + (d.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? d.Name) == f.Id) is { } disk)
        {
            var name = DiskName(disk);
            return PcChecks.DiskRank(disk) switch
            {
                0 => new(Loc.F("Pc.Disk.Failing", name), Loc.T("Pc.Disk.FailingDo"), topic, "Icon.Disk"),
                1 => new(Loc.F("Pc.Disk.Worn", name), Loc.T("Pc.Disk.WornDo"), topic, "Icon.Disk"),
                _ => new(Loc.F("Pc.Disk.Hot", name, disk.TemperatureC ?? 0), Loc.T("Pc.Disk.HotDo"), topic, "Icon.Disk"),
            };
        }
        if (s.DisplayFor(f.Id) is { MaxHz: { } max } m)
            return new(Loc.F("Pc.Display.Below", m.CurrentHz, max), Loc.F("Pc.Display.BelowDo", max), topic, "Icon.Monitor");
        return null;
    }

    /// <summary>"Disco 1 (WDC WD10EZEX)" no idioma da interface.</summary>
    public static string DiskName(DiskHealthFacts d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var name = string.IsNullOrWhiteSpace(d.Name) ? "" : d.Name.Trim();
        return d.Number is { } n ? (name.Length > 0 ? Loc.F("Pc.Disk.NameN", n, name) : Loc.F("Pc.Disk.NameOnly", n)) : name;
    }

    /// <summary>"Usar 144 Hz" (botão do monitor).</summary>
    public static string UseHz(int hz) => Loc.F("Pc.Display.Use", hz);

    /// <summary>Valor ou "não informado".</summary>
    public static string OrNotInformed<T>(T? value, string format) where T : struct =>
        value is { } v ? string.Format(Loc.Culture, format, v) : Loc.T("Pc.NotInformed");
}
