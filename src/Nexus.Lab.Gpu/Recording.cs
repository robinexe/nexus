using System.Globalization;

namespace Nexus.Lab.Gpu;

/// <summary>Uma amostra de telemetria da GPU (1 por segundo durante a gravação). Null = o driver não informou.</summary>
public sealed record GpuTelemetrySample(
    double Seconds,
    double? UtilizationPercent,
    double? GraphicsClockMhz,
    double? MemoryClockMhz,
    double? PowerWatts,
    double? PowerLimitWatts,
    double? TemperatureC,
    ulong? ReasonsMask,
    int? PcieGen,
    int? PcieWidth,
    ulong? VramUsedBytes,
    ulong? VramTotalBytes,
    double? FanPercent = null);

/// <summary>Trecho contínuo de uma mesma limitação (para pintar faixas no gráfico).</summary>
public sealed record LimitBand(double FromSeconds, double ToSeconds, LimitCategory Category);

/// <summary>Um achado da gravação, com a evidência medida.</summary>
public sealed record RecordingFinding(string Level, string Title, string Evidence, string? Advice = null);

/// <summary>Resultado da análise de uma gravação.</summary>
public sealed record RecordingAnalysis(
    int Samples,
    int LoadSamples,
    string Verdict,
    string VerdictLevel,
    IReadOnlyList<RecordingFinding> Findings,
    IReadOnlyList<LimitBand> Bands,
    double? AvgClockUnderLoad,
    double? MaxClock,
    double? AvgPowerUnderLoad,
    double? MaxTemperature,
    IReadOnlyDictionary<LimitCategory, double> LimitShare);

/// <summary>
/// Análise da telemetria gravada enquanto o usuário joga. Regras simples e explicadas (sem "nota mágica"):
/// "sob carga" = utilização ≥ 60%; cada motivo de limitação é a fração das amostras sob carga em que o driver o informou.
/// </summary>
public static class RecordingAnalyzer
{
    public const double LoadThresholdPercent = 60;
    public const int MinLoadSamples = 10;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <param name="maxGraphicsClockMhz">Clock gráfico máximo informado pelo driver (para comparar o clock médio).</param>
    /// <param name="attainablePcie">Melhor link possível (placa + porta), para saber se a geração caiu sob carga.</param>
    /// <param name="isLaptop">Notebook: limite de potência/temperatura é desenho de fábrica, a explicação muda.</param>
    public static RecordingAnalysis Analyze(IReadOnlyList<GpuTelemetrySample> samples, double? maxGraphicsClockMhz, PcieLinkState? attainablePcie, bool isLaptop)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var load = samples.Where(s => s.UtilizationPercent >= LoadThresholdPercent).ToList();
        var bands = Bands(samples);
        var findings = new List<RecordingFinding>();
        var share = new Dictionary<LimitCategory, double>();

        double? avgClock = Avg(load.Select(s => s.GraphicsClockMhz));
        double? maxClockSeen = samples.Select(s => s.GraphicsClockMhz).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty().Max() is var mc && mc > 0 ? mc : null;
        double? avgPower = Avg(load.Select(s => s.PowerWatts));
        double? maxTemp = samples.Select(s => s.TemperatureC).Where(v => v is not null).Select(v => v!.Value).DefaultIfEmpty().Max() is var mt && mt > 0 ? mt : null;

        if (samples.Count == 0)
            return new(0, 0, "Nada gravado.", "info", [], [], null, null, null, null, share);

        if (load.Count < MinLoadSamples)
        {
            var avgUtil = Avg(samples.Select(s => s.UtilizationPercent));
            findings.Add(new("info", "A GPU quase não trabalhou",
                $"{load.Count} de {samples.Count} segundos com uso ≥ {LoadThresholdPercent:0}% (média {F0(avgUtil)}%).",
                "Abra o jogo (não o menu), jogue durante a gravação e grave de novo. Se o jogo estava rodando, o gargalo pode ser o processador ou um limite de FPS/V-Sync."));
            return new(samples.Count, load.Count, "Sem carga suficiente para concluir.", "info", findings, bands, avgClock, maxClockSeen, avgPower, maxTemp, share);
        }

        var withMask = load.Where(s => s.ReasonsMask is not null).ToList();
        if (withMask.Count > 0)
        {
            foreach (var cat in new[] { LimitCategory.Power, LimitCategory.Thermal, LimitCategory.Hardware, LimitCategory.Other })
                share[cat] = (double)withMask.Count(s => ThrottleReasons.Categorize(s.ReasonsMask!.Value) == cat) / withMask.Count;
            share[LimitCategory.None] = (double)withMask.Count(s => ThrottleReasons.Categorize(s.ReasonsMask!.Value) is LimitCategory.None or LimitCategory.Idle) / withMask.Count;
        }

        // Potência
        if (share.GetValueOrDefault(LimitCategory.Power) >= 0.3)
        {
            var limit = Avg(load.Select(s => s.PowerLimitWatts));
            findings.Add(new("warn", "Limitada por potência",
                $"Em {Pct(share[LimitCategory.Power])} do tempo sob carga o driver segurou o clock pelo limite de potência (média {F0(avgPower)} W de {F0(limit)} W permitidos).",
                isLaptop
                    ? "Em notebook é o comportamento de fábrica. Ligue o carregador, use o modo Desempenho do software do fabricante (no Acer: NitroSense) e uma base com ventilação; isso libera mais watts quando o fabricante permite."
                    : "O limite padrão é do fabricante da placa. Verifique se os cabos de energia estão todos ligados; aumentar o limite é overclock e o NEXUS não faz."));
        }
        // Temperatura
        var thermalShare = share.GetValueOrDefault(LimitCategory.Thermal);
        if (thermalShare >= 0.1)
            findings.Add(new(thermalShare >= 0.3 ? "bad" : "warn", "Limitada por temperatura",
                $"Em {Pct(thermalShare)} do tempo sob carga o driver reduziu o clock por temperatura (máxima {F0(maxTemp)} °C).",
                isLaptop
                    ? "Limpe as entradas de ar, use o notebook sobre superfície dura ou base com ventoinha e o perfil de ventoinha mais forte do fabricante."
                    : "Melhore o fluxo de ar do gabinete, limpe a poeira dos dissipadores e confira a curva das ventoinhas."));
        if (share.GetValueOrDefault(LimitCategory.Hardware) >= 0.1)
            findings.Add(new("bad", "Redução por hardware",
                $"Em {Pct(share[LimitCategory.Hardware])} do tempo sob carga a placa sinalizou redução por hardware (HW Slowdown).",
                isLaptop ? "Em notebook costuma ser carregador fraco/genérico ou bateria muito baixa: use o carregador original." : "Pode ser fonte insuficiente ou temperatura crítica: confira fonte e cabos."));

        // PCIe sob carga
        var gens = load.Where(s => s.PcieGen is not null).Select(s => s.PcieGen!.Value).ToList();
        var widths = load.Where(s => s.PcieWidth is not null).Select(s => s.PcieWidth!.Value).ToList();
        if (gens.Count > 0 && attainablePcie?.AttainableGen is { } attGen && gens.Min() < attGen)
        {
            var low = (double)gens.Count(g => g < attGen) / gens.Count;
            if (low >= 0.5)
                findings.Add(new("warn", "PCIe abaixo do possível sob carga",
                    $"Sob carga o link ficou em PCIe {Pcie.GenLabel(gens.Min())} em {Pct(low)} do tempo; placa e porta suportam {Pcie.GenLabel(attGen)}.",
                    "Confira na BIOS se o slot está em Auto/Gen máxima e se a placa está no slot principal (ligado ao processador)."));
        }
        if (widths.Count > 0 && attainablePcie?.AttainableWidth is { } attW && widths.Max() < attW)
            findings.Add(new("warn", $"PCIe em x{widths.Max()} sob carga",
                $"A placa trabalhou em x{widths.Max()} com placa e porta suportando x{attW}.",
                "Em desktop: troque para o slot x16 principal e reencaixe a placa. Em notebook a largura é fixa de fábrica."));

        // Clock oscilando
        var clocks = load.Where(s => s.GraphicsClockMhz is not null).Select(s => s.GraphicsClockMhz!.Value).ToList();
        if (clocks.Count >= MinLoadSamples)
        {
            var mean = clocks.Average();
            var sd = Math.Sqrt(clocks.Sum(c => (c - mean) * (c - mean)) / clocks.Count);
            var cv = mean > 0 ? sd / mean : 0;
            if (cv > 0.08)
                findings.Add(new("warn", "Clock oscilando",
                    $"O clock gráfico variou bastante sob carga (média {F0(mean)} MHz, desvio {F0(sd)} MHz, {cv.ToString("P0", Pt)}).",
                    "Oscilação forte costuma vir de limite de potência/temperatura alternando; nos motivos acima está a causa."));
            if (maxGraphicsClockMhz is { } maxClk && maxClk > 0 && mean < maxClk * 0.7 && share.GetValueOrDefault(LimitCategory.None) > 0.6)
                findings.Add(new("info", "Clock bem abaixo do máximo sem limite informado",
                    $"Média {F0(mean)} MHz contra máximo de {F0(maxClk)} MHz, sem motivo de limitação na maior parte do tempo.",
                    "Normalmente quer dizer que o jogo não pede mais (limite de FPS, V-Sync ou gargalo no processador)."));
        }

        // VRAM
        var vram = load.Where(s => s.VramUsedBytes is not null && s.VramTotalBytes is > 0).ToList();
        if (vram.Count > 0)
        {
            var full = (double)vram.Count(s => (double)s.VramUsedBytes!.Value / s.VramTotalBytes!.Value >= 0.95) / vram.Count;
            if (full >= 0.1)
                findings.Add(new("warn", "Memória de vídeo cheia",
                    $"A VRAM ficou acima de 95% em {Pct(full)} do tempo sob carga ({Gb(vram.Max(s => s.VramUsedBytes!.Value))} de {Gb(vram[0].VramTotalBytes!.Value)}).",
                    "Baixe a qualidade das texturas no jogo: com a VRAM cheia o jogo usa a memória do sistema pelo PCIe e aparecem travadas."));
        }

        // Utilização média: pista de gargalo fora da GPU
        var avgLoadUtil = Avg(samples.Select(s => s.UtilizationPercent)) ?? 0;
        if (avgLoadUtil < 80 && findings.Count == 0)
            findings.Add(new("info", "GPU sem trabalhar no máximo",
                $"Uso médio de {avgLoadUtil:0}% na gravação.",
                "Se o FPS está baixo, o limite está fora da placa de vídeo: processador, limite de FPS ou V-Sync."));

        string verdict, level;
        var top = share.Where(kv => kv.Key is LimitCategory.Power or LimitCategory.Thermal or LimitCategory.Hardware).OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (top.Value >= 0.3)
        {
            verdict = $"Principal limite: {ThrottleReasons.CategoryLabel(top.Key)} ({Pct(top.Value)} do tempo sob carga).";
            level = top.Key == LimitCategory.Thermal || top.Key == LimitCategory.Hardware ? "bad" : "warn";
        }
        else if (findings.Any(f => f.Level is "warn" or "bad"))
        {
            verdict = "A GPU trabalhou com ressalvas: veja os achados.";
            level = "warn";
        }
        else
        {
            verdict = withMask.Count > 0 ? "A GPU entregou o que pode: sem limitação relevante sob carga." : "Sem motivos de limitação disponíveis para esta placa; veja clocks e temperatura.";
            level = withMask.Count > 0 ? "good" : "info";
        }
        return new(samples.Count, load.Count, verdict, level, findings, bands, avgClock, maxClockSeen, avgPower, maxTemp, share);
    }

    /// <summary>Agrupa amostras consecutivas da mesma categoria (ignora ociosa/livre) em faixas.</summary>
    public static IReadOnlyList<LimitBand> Bands(IReadOnlyList<GpuTelemetrySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var bands = new List<LimitBand>();
        LimitCategory? cur = null;
        double from = 0, to = 0;
        foreach (var s in samples)
        {
            var c = s.ReasonsMask is { } m ? ThrottleReasons.Categorize(m) : LimitCategory.None;
            if (c is LimitCategory.None or LimitCategory.Idle)
            {
                if (cur is { } done) bands.Add(new(from, to, done));
                cur = null;
                continue;
            }
            if (cur == c) { to = s.Seconds + 1; continue; }
            if (cur is { } prev) bands.Add(new(from, to, prev));
            cur = c;
            from = s.Seconds;
            to = s.Seconds + 1;
        }
        if (cur is { } last) bands.Add(new(from, to, last));
        return bands;
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static string F0(double? v) => v is { } x ? x.ToString("0", Pt) : "?";
    private static string Pct(double v) => v.ToString("P0", Pt);
    private static string Gb(ulong b) => (b / 1073741824.0).ToString("0.0", Pt) + " GB";
}
