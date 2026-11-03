using System.Globalization;

namespace Nexus.HardwareDiscovery;

// =====================================================================================================================
// Checagens do PC (somente leitura): rede para jogos, saúde dos discos, monitor, limitação do processador pelo firmware e
// notebook na bateria. Aqui ficam só os dados lidos e as regras (puras, testadas com dados falsos). Os leitores do Windows
// ficam em Nexus.SystemTools/Checks e Nexus.Lab.Gpu; a interface traduz os achados para frases simples.
// Nenhuma regra promete FPS; cada achado diz a evidência e, quando há, a fonte. Nada aqui grava nada.
// =====================================================================================================================

/// <summary>Como o PC está ligado na rede agora.</summary>
public enum LinkKind { Unknown, Wired, WiFi, Other }

/// <summary>
/// Resultado de uma série de pings. RTT nulo na lista = resposta perdida. Jitter = média da diferença absoluta entre pings
/// seguidos (a mesma ideia do RFC 3550, sem suavização), que é como os testes de jogo costumam mostrar.
/// </summary>
public sealed record PingStats(string Target, int Sent, int Received, double? AverageMs, double? MinMs, double? MaxMs, double? JitterMs, double? P95Ms)
{
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
    public bool AllLost => Sent > 0 && Received == 0;

    public static PingStats From(string target, IReadOnlyList<double?> rtts)
    {
        ArgumentNullException.ThrowIfNull(rtts);
        var ok = rtts.Where(r => r is not null).Select(r => r!.Value).ToList();
        if (ok.Count == 0) return new PingStats(target, rtts.Count, 0, null, null, null, null, null);
        double? jitter = null;
        if (ok.Count >= 2)
        {
            double sum = 0;
            for (var i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
            jitter = sum / (ok.Count - 1);
        }
        var sorted = ok.OrderBy(x => x).ToList();
        var p95 = sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * 0.95) - 1, 0, sorted.Count - 1)];
        return new PingStats(target, rtts.Count, ok.Count, ok.Average(), sorted[0], sorted[^1], jitter, p95);
    }
}

/// <summary>
/// Teste de bufferbloat: ping sem carga e ping enquanto um download (e depois um upload) enche a conexão. O que importa é
/// quanto a latência SOBE com a conexão cheia — é o que faz o jogo "travar" quando alguém da casa assiste vídeo ou baixa algo.
/// </summary>
public sealed record BufferbloatResult(double IdleMs, double? DownloadLoadedMs, double? UploadLoadedMs, double? DownloadMbps, double? UploadMbps, long BytesUsed)
{
    /// <summary>Maior aumento de latência com carga (ms); null se nenhuma fase com carga teve resposta.</summary>
    public double? WorstIncreaseMs
    {
        get
        {
            var worst = new[] { DownloadLoadedMs, UploadLoadedMs }.Where(x => x is not null).Select(x => x!.Value - IdleMs).DefaultIfEmpty(double.NaN).Max();
            return double.IsNaN(worst) ? null : Math.Max(0, worst);
        }
    }

    /// <summary>Nota no padrão dos testes públicos de bufferbloat (A+ até F) pelo aumento de latência.</summary>
    public string Grade => WorstIncreaseMs switch
    {
        null => "?",
        < 5 => "A+",
        < 30 => "A",
        < 60 => "B",
        < 200 => "C",
        < 400 => "D",
        _ => "F",
    };
}

public sealed record NetworkCheckFacts(LinkKind Link, string? AdapterName = null, int? WifiSignalPercent = null, long? LinkSpeedMbps = null,
    PingStats? Gateway = null, PingStats? Internet = null, BufferbloatResult? Bufferbloat = null);

/// <summary>Saúde de um disco físico (MSFT_PhysicalDisk + MSFT_StorageReliabilityCounter). Null = o Windows não informou.</summary>
/// <param name="Health">"Healthy", "Warning", "Unhealthy" ou "Unknown" (MSFT_PhysicalDisk.HealthStatus 0/1/2/5).</param>
/// <param name="ReliabilityRead">false = os contadores de confiabilidade não vieram (muitos exigem administrador).</param>
public sealed record DiskHealthFacts(
    int? Number,
    string Name,
    DiskMedia Media,
    string? Bus,
    string? Health,
    IReadOnlyList<string> OperationalStatus,
    int? TemperatureC = null,
    int? TemperatureMaxC = null,
    int? WearPercent = null,
    long? ReadErrorsUncorrected = null,
    long? WriteErrorsUncorrected = null,
    long? PowerOnHours = null,
    bool ReliabilityRead = false,
    ulong? SizeBytes = null)
{
    public bool IsSataSsd => Media == DiskMedia.Ssd && string.Equals(Bus, "SATA", StringComparison.OrdinalIgnoreCase);
    public bool IsNvme => string.Equals(Bus, "NVMe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Um jogo instalado e o disco onde ele está.</summary>
public sealed record GameOnDisk(string Name, string InstallDir, int DiskNumber);

/// <summary>Um monitor: taxa atual contra a maior que ele lista na mesma resolução, e o estado do HDR.</summary>
/// <param name="BestModeKey">Modo "2560x1440@144" para a ação reversível gpulab.display.mode (null se já está no máximo).</param>
public sealed record DisplayCheckFacts(string Device, string Name, int Width, int Height, int CurrentHz, int? MaxHz, string? BestModeKey,
    bool? HdrSupported, bool? HdrEnabled, bool Internal = false);

/// <summary>Limitação do processador pelo firmware.</summary>
/// <param name="Events37">Eventos Kernel-Processor-Power 37 no log Sistema nos últimos <see cref="WindowDays"/> dias (null = não leu).</param>
/// <param name="PerformanceLimitPercent">Menor "% Performance Limit" amostrado agora (100 = sem limite; null = não leu).</param>
public sealed record ThrottleFacts(int? Events37, DateTimeOffset? LastEvent, double? PerformanceLimitPercent, bool? OnBattery = null, int WindowDays = 7);

/// <summary>Regras das checagens do PC (puras). Ids estáveis: a interface traduz pelo id.</summary>
public static class PcChecks
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public const string LinkId = "net.link";
    public const string LocalId = "net.local";
    public const string InternetId = "net.internet";
    public const string BufferbloatId = "net.bufferbloat";
    public const string DiskPrefix = "diskhealth.";
    public const string GamesOnSlowDiskId = "diskhealth.games";
    public const string DisplayRefreshPrefix = "display.refresh.";
    public const string DisplayHdrPrefix = "display.hdr.";
    public const string DisplayOkId = "display.ok";
    public const string ThrottleId = "cpu.firmware-limit";

    /// <summary>Limites (testados): perda de pacotes, jitter, ping e sinal do Wi-Fi.</summary>
    public const double LossAttentionPercent = 2;
    public const double JitterAttentionMs = 15;
    public const double LocalJitterAttentionMs = 10;
    public const double HighPingMs = 100;
    public const double LocalPingAttentionMs = 20;
    public const int WeakWifiPercent = 50;
    public const double BloatAttentionMs = 60;
    public const double BloatInfoMs = 30;
    public const int HotSsdC = 70;
    public const int HotHddC = 55;
    public const int WornPercent = 90;
    public const double LimitAttentionPercent = 90;

    public static readonly (string Title, string Url) BufferbloatSource =
        ("Oppedahl: você tem bufferbloat? (latência com a conexão cheia; correção com SQM)", "https://blog.oppedahl.com/do-you-have-bufferbloat/");
    public static readonly (string Title, string Url) DiskCounterSource =
        ("Microsoft Learn: Get-StorageReliabilityCounter (desgaste, temperatura, erros, horas ligadas)", "https://learn.microsoft.com/en-us/powershell/module/storage/get-storagereliabilitycounter");
    public static readonly (string Title, string Url) LoadTimeSource =
        ("TechSpot: tempo de carregamento de jogos em HD, SSD SATA e NVMe", "https://www.techspot.com/article/3023-ssd-gaming-comparison-load-times/");
    public static readonly (string Title, string Url) RefreshSource =
        ("Blur Busters: \"estou na taxa de atualização errada\" (muito comum)", "https://www.blurbusters.com/oh-no-im-at-the-wrong-refresh-rate/");
    public static readonly (string Title, string Url) ThrottleSource =
        ("Intel: evento 37 do Kernel-Processor-Power (velocidade limitada pelo firmware)", "https://www.intel.com/content/www/us/en/support/articles/000090904.html");
    public static readonly (string Title, string Url) BatterySource =
        ("TechSpot: desempenho de notebooks gamer na bateria", "https://www.techspot.com/article/1571-gaming-laptop-battery-performance/");

    /// <summary>Fontes fixas que a interface aceita abrir a partir destes achados.</summary>
    public static IReadOnlyList<string> SourceUrls { get; } =
        [BufferbloatSource.Url, DiskCounterSource.Url, LoadTimeSource.Url, RefreshSource.Url, ThrottleSource.Url, BatterySource.Url];

    /// <summary>O achado veio destas checagens (para trocar a leva antiga pela nova).</summary>
    public static bool IsPcCheckId(string? id) =>
        id is not null && (id.StartsWith("net.", StringComparison.Ordinal) || id.StartsWith(DiskPrefix, StringComparison.Ordinal) ||
                           id.StartsWith("display.", StringComparison.Ordinal) || id == ThrottleId);

    private static DiagnosticFinding F(string id, string title, DiagnosticSeverity sev, string evidence, string explanation = "", string gain = "",
        (string Title, string Url)? source = null) =>
        new(id, title, sev, FactStatus.Detected, evidence, null,
            source is { } s ? new OwnerAdvice(explanation, gain, s.Title, s.Url) : null);

    private static string Ms(double? v) => v is { } x ? x.ToString(x >= 10 ? "0" : "0.0", Pt) + " ms" : "—";
    private static string Pct(double v) => v.ToString(v >= 10 || v == 0 ? "0" : "0.0", Pt) + "%";

    // ------------------------------------------------------------------ 1. rede para jogos

    public static IReadOnlyList<DiagnosticFinding> Network(NetworkCheckFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var list = new List<DiagnosticFinding>();
        // Wi-Fi ou cabo
        switch (f.Link)
        {
            case LinkKind.WiFi:
                var signal = f.WifiSignalPercent is { } sp ? string.Create(Pt, $" Sinal: {sp}%.") : " O Windows não informou o sinal.";
                var weak = f.WifiSignalPercent is { } s && s < WeakWifiPercent;
                list.Add(F(LinkId, weak ? "Wi-Fi com sinal fraco" : "Jogando pelo Wi-Fi", weak ? DiagnosticSeverity.Attention : DiagnosticSeverity.Info,
                    $"Conexão atual: Wi-Fi ({f.AdapterName ?? "adaptador sem nome"}).{signal}" +
                    (f.LinkSpeedMbps is { } mb ? string.Create(Pt, $" Velocidade do link: {mb} Mb/s.") : "")));
                break;
            case LinkKind.Wired:
                list.Add(F(LinkId, "Conectado por cabo", DiagnosticSeverity.Ok, $"Conexão atual: cabo ({f.AdapterName ?? "adaptador sem nome"})." +
                    (f.LinkSpeedMbps is { } mb2 ? string.Create(Pt, $" Velocidade do link: {mb2} Mb/s.") : "")));
                break;
        }

        // Entre o PC e o roteador (problema dentro de casa)
        if (f.Gateway is { Sent: > 0 } g)
        {
            var localBad = !g.AllLost &&
                (g.LossPercent >= LossAttentionPercent || g.JitterMs >= LocalJitterAttentionMs || g.AverageMs >= LocalPingAttentionMs);
            var ev = string.Create(Pt, $"Roteador ({g.Target}): {g.Received}/{g.Sent} respostas, média {Ms(g.AverageMs)}, variação {Ms(g.JitterMs)}, perda {Pct(g.LossPercent)}.");
            if (g.AllLost)
                list.Add(F(LocalId, "O roteador não respondeu ao ping", DiagnosticSeverity.Info, ev + " Muitos roteadores ignoram ping; isso sozinho não é defeito."));
            else
                list.Add(F(LocalId, localBad ? "Instabilidade entre o PC e o roteador" : "Ligação com o roteador estável",
                    localBad ? DiagnosticSeverity.Attention : DiagnosticSeverity.Ok, ev));
        }

        // Até a internet / servidor do jogo
        if (f.Internet is { Sent: > 0 } i)
        {
            var ev = string.Create(Pt, $"{i.Target}: {i.Received}/{i.Sent} respostas, média {Ms(i.AverageMs)}, variação {Ms(i.JitterMs)}, perda {Pct(i.LossPercent)}.");
            if (i.AllLost)
                list.Add(F(InternetId, "O servidor não respondeu ao ping", DiagnosticSeverity.Info, ev + " Alguns servidores bloqueiam ping; teste outro endereço."));
            else if (i.LossPercent >= LossAttentionPercent)
                list.Add(F(InternetId, "Perda de pacotes até a internet", DiagnosticSeverity.Attention, ev));
            else if (i.JitterMs >= JitterAttentionMs)
                list.Add(F(InternetId, "Ping instável até a internet", DiagnosticSeverity.Attention, ev));
            else if (i.AverageMs >= HighPingMs)
                list.Add(F(InternetId, "Ping alto até esse servidor", DiagnosticSeverity.Info, ev + " Ping alto e estável costuma ser distância até o servidor."));
            else
                list.Add(F(InternetId, "Ping estável até a internet", DiagnosticSeverity.Ok, ev));
        }

        // Bufferbloat
        if (f.Bufferbloat is { WorstIncreaseMs: { } inc } b)
        {
            var ev = string.Create(Pt, $"Sem carga: {Ms(b.IdleMs)}. Baixando: {Ms(b.DownloadLoadedMs)}. Enviando: {Ms(b.UploadLoadedMs)}. ") +
                     string.Create(Pt, $"Aumento: +{inc:0} ms (nota {b.Grade}).") +
                     (b.DownloadMbps is { } d ? string.Create(Pt, $" Download medido: {d:0} Mb/s.") : "") +
                     (b.UploadMbps is { } u ? string.Create(Pt, $" Upload medido: {u:0} Mb/s.") : "");
            const string explanation = "Quando a conexão enche (download, vídeo, backup), o roteador forma fila e o ping sobe. A correção é no roteador: " +
                                       "ligue SQM/QoS (às vezes \"Smart Queue\", \"fq_codel\" ou \"CAKE\") com o limite um pouco abaixo da velocidade contratada. Internet mais rápida não resolve.";
            const string gain = "Com SQM o aumento costuma cair para poucos milissegundos (nota A); sem ele, aumentos de centenas de ms são comuns.";
            var sev = inc >= BloatAttentionMs ? DiagnosticSeverity.Attention : inc >= BloatInfoMs ? DiagnosticSeverity.Info : DiagnosticSeverity.Ok;
            list.Add(F(BufferbloatId, sev == DiagnosticSeverity.Ok ? "Ping firme com a conexão cheia" : "Ping sobe quando a conexão enche (bufferbloat)",
                sev, ev, explanation, gain, BufferbloatSource));
        }
        return list;
    }

    // ------------------------------------------------------------------ 2. saúde dos discos

    public static IReadOnlyList<DiagnosticFinding> Disks(IReadOnlyList<DiskHealthFacts> disks, IReadOnlyList<GameOnDisk> games)
    {
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(games);
        var list = new List<DiagnosticFinding>();
        foreach (var d in disks) list.Add(Disk(d));

        // Jogos em HD (ou SSD SATA): carregamento, não FPS.
        var byNumber = disks.Where(d => d.Number is not null).GroupBy(d => d.Number!.Value).ToDictionary(g => g.Key, g => g.First());
        var onHdd = games.Where(g => byNumber.TryGetValue(g.DiskNumber, out var d) && d.Media == DiskMedia.Hdd).ToList();
        var onSata = games.Where(g => byNumber.TryGetValue(g.DiskNumber, out var d) && d.IsSataSsd).ToList();
        var hasFaster = disks.Any(d => d.IsNvme || (d.Media == DiskMedia.Ssd && onHdd.Count > 0));
        const string gain = "Nos testes da TechSpot, num HD o Kingdom Come: Deliverance II passou de 4 minutos para carregar e o Cyberpunk 2077 recarregou até 3,8× mais devagar; " +
                            "no SSD SATA, o Ratchet & Clank carregou quase 5× mais devagar que no NVMe e vários jogos levaram 36–70% mais tempo. O FPS quase não muda; o que muda é o carregamento e as travadas ao carregar áreas.";
        static string Names(List<GameOnDisk> g) => string.Join(", ", g.Take(5).Select(x => x.Name)) + (g.Count > 5 ? $" e mais {g.Count - 5}" : "");
        if (onHdd.Count > 0)
            list.Add(F(GamesOnSlowDiskId, onHdd.Count == 1 ? "Um jogo instalado num HD" : string.Create(Pt, $"{onHdd.Count} jogos instalados num HD"), DiagnosticSeverity.Attention,
                $"Num disco rígido (HD): {Names(onHdd)}.",
                hasFaster
                    ? "Mova os jogos para o SSD pela própria loja (Steam: Propriedades › Arquivos instalados › Mover pasta; Epic/Xbox têm opção parecida). Nada se perde."
                    : "Um SSD (de preferência NVMe, se a placa-mãe tiver encaixe M.2) carrega muito mais rápido. Depois, mova os jogos pela própria loja.",
                gain, LoadTimeSource));
        else if (onSata.Count > 0 && disks.Any(d => d.IsNvme))
            list.Add(F(GamesOnSlowDiskId, onSata.Count == 1 ? "Um jogo num SSD SATA (há um NVMe mais rápido)" : string.Create(Pt, $"{onSata.Count} jogos num SSD SATA (há um NVMe mais rápido)"),
                DiagnosticSeverity.Info, $"No SSD SATA: {Names(onSata)}.",
                "Se sobrar espaço no NVMe, mover os jogos que você mais joga encurta o carregamento. Pela própria loja, sem perder nada.", gain, LoadTimeSource));
        return list;
    }

    public static DiagnosticFinding Disk(DiskHealthFacts d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var id = DiskPrefix + (d.Number?.ToString(CultureInfo.InvariantCulture) ?? d.Name);
        var label = DiskLabel(d);
        var ev = DiskEvidence(d);
        const string backup = "Faça backup agora: copie o que importa (fotos, documentos, saves) para outro disco ou para a nuvem. Depois, troque o disco.";
        var errors = (d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0);
        var predicted = d.OperationalStatus.Any(o => o is "Falha prevista" or "Erro" or "Erro não recuperável");
        if (string.Equals(d.Health, "Unhealthy", StringComparison.OrdinalIgnoreCase) || errors > 0 || predicted)
            return F(id, $"{label}: o disco informa defeito", DiagnosticSeverity.Attention, ev, backup,
                "Erros que o disco não conseguiu corrigir ou estado \"não íntegro\" costumam piorar.", DiskCounterSource);
        if (string.Equals(d.Health, "Warning", StringComparison.OrdinalIgnoreCase) || d.WearPercent >= WornPercent)
            return F(id, $"{label}: perto do fim da vida útil", DiagnosticSeverity.Attention, ev,
                "Ainda funciona, mas faça backup do que importa e planeje a troca.", "O desgaste é a fração da vida útil prevista pelo fabricante que já foi usada.", DiskCounterSource);
        var hot = d.TemperatureC is { } t && t >= (d.Media == DiskMedia.Hdd ? HotHddC : HotSsdC);
        if (hot)
            return F(id, $"{label}: esquentando ({d.TemperatureC} °C)", DiagnosticSeverity.Attention, ev,
                "Disco quente reduz a velocidade para se proteger. Confira a ventilação do gabinete/notebook; em SSD NVMe de PC de mesa, um dissipador ajuda.",
                "SSDs NVMe costumam reduzir a velocidade perto de 70–80 °C; HDs preferem ficar abaixo de ~50 °C.", DiskCounterSource);
        return F(id, string.Equals(d.Health, "Healthy", StringComparison.OrdinalIgnoreCase) ? $"{label}: saudável" : $"{label}: sem alerta",
            DiagnosticSeverity.Ok, ev);
    }

    public static string DiskLabel(DiskHealthFacts d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var name = string.IsNullOrWhiteSpace(d.Name) ? "Disco" : d.Name.Trim();
        return d.Number is { } n ? string.Create(Pt, $"Disco {n} ({name})") : name;
    }

    private static string DiskEvidence(DiskHealthFacts d)
    {
        const string na = "não informado";
        string V(object? v, string unit = "") => v is null ? na : string.Create(Pt, $"{v}{unit}");
        return $"Estado: {TranslateHealth(d.Health)}. Tipo: {StorageClassifier.Describe(d.Media, d.Bus)}. Temperatura: {V(d.TemperatureC, " °C")}" +
               (d.TemperatureMaxC is { } tm ? string.Create(Pt, $" (máx. {tm} °C)") : "") +
               $". Desgaste: {V(d.WearPercent, "%")}. Erros não corrigidos: {(d.ReadErrorsUncorrected is null && d.WriteErrorsUncorrected is null ? na : V((d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0)))}" +
               $". Horas ligado: {V(d.PowerOnHours)}." +
               (d.ReliabilityRead ? "" : " Os contadores de confiabilidade não vieram (alguns exigem administrador ou o disco não informa).");
    }

    public static string TranslateHealth(string? health) => health?.ToLowerInvariant() switch
    {
        "healthy" => "íntegro",
        "warning" => "alerta",
        "unhealthy" => "não íntegro",
        _ => "não informado",
    };

    /// <summary>Em que disco está cada jogo: pela letra da pasta e pelas unidades de cada disco (C:\ → disco 0).</summary>
    public static IReadOnlyList<GameOnDisk> PlaceGames(IEnumerable<(string Name, string? InstallDir)> games, IReadOnlyList<PhysicalDiskFacts> disks)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(disks);
        var list = new List<GameOnDisk>();
        foreach (var (name, dir) in games)
        {
            if (string.IsNullOrWhiteSpace(dir) || dir.Length < 3 || dir[1] != ':') continue;
            var root = char.ToUpperInvariant(dir[0]) + @":\";
            var disk = disks.FirstOrDefault(d => d.Volumes.Any(v => string.Equals(v, root, StringComparison.OrdinalIgnoreCase)));
            if (disk is not null) list.Add(new GameOnDisk(name, dir, disk.Number));
        }
        return list;
    }

    // ------------------------------------------------------------------ 3. monitor

    public static IReadOnlyList<DiagnosticFinding> Displays(IReadOnlyList<DisplayCheckFacts> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var list = new List<DiagnosticFinding>();
        foreach (var m in monitors)
        {
            var name = string.IsNullOrWhiteSpace(m.Name) ? m.Device : m.Name;
            if (m.MaxHz is { } max && max > m.CurrentHz && m.BestModeKey is not null)
                list.Add(F(DisplayRefreshPrefix + m.Device, string.Create(Pt, $"{name} a {m.CurrentHz} Hz, mas aceita {max} Hz"), DiagnosticSeverity.Attention,
                    string.Create(Pt, $"Modo atual {m.Width}×{m.Height} a {m.CurrentHz} Hz; o monitor lista {max} Hz na mesma resolução (EnumDisplaySettingsEx)."),
                    "Mais Hz deixa a imagem mais fluida e reduz o atraso. Acontece muito depois de formatar, atualizar driver ou trocar cabo. O NEXUS só troca a taxa (nunca a resolução) e volta sozinho em 15 s se você não confirmar.",
                    "Os quadros aparecem até 2,4× mais vezes por segundo (60 → 144 Hz).", RefreshSource));
            if (m.HdrSupported == true && m.HdrEnabled == false)
                list.Add(F(DisplayHdrPrefix + m.Device, $"{name}: HDR disponível e desligado", DiagnosticSeverity.Info,
                    "O monitor informa suporte a cores avançadas (HDR) e ele está desligado (DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO).",
                    "Escolha sua: ligue em Configurações › Sistema › Tela › HDR se quiser. HDR não aumenta FPS e, em alguns monitores, deixa a área de trabalho apagada."));
        }
        if (monitors.Count > 0 && !list.Any(x => x.Severity == DiagnosticSeverity.Attention))
            list.Add(F(DisplayOkId, monitors.Count == 1 ? "Monitor na maior taxa que ele aceita" : "Monitores na maior taxa que aceitam", DiagnosticSeverity.Ok,
                string.Join(" ", monitors.Select(m => string.Create(Pt, $"{(string.IsNullOrWhiteSpace(m.Name) ? m.Device : m.Name)}: {m.Width}×{m.Height} a {m.CurrentHz} Hz.")))));
        return list;
    }

    // ------------------------------------------------------------------ 5. limitação pelo firmware

    public static DiagnosticFinding? Throttle(ThrottleFacts t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Events37 is null && t.PerformanceLimitPercent is null) return null;
        var parts = new List<string>();
        if (t.Events37 is { } n)
            parts.Add(n == 0 ? string.Create(Pt, $"Nenhum evento 37 (Kernel-Processor-Power) no log Sistema nos últimos {t.WindowDays} dias.")
                : string.Create(Pt, $"{n} evento(s) 37 (Kernel-Processor-Power) no log Sistema nos últimos {t.WindowDays} dias") +
                  (t.LastEvent is { } last ? string.Create(Pt, $"; o último em {last.ToLocalTime():dd/MM HH:mm}.") : "."));
        if (t.PerformanceLimitPercent is { } p)
            parts.Add(string.Create(Pt, $"Contador \"% Performance Limit\" agora: {p:0}% (100% = sem limite)."));
        if (t.OnBattery == true) parts.Add("O notebook está na bateria agora (limitar na bateria é esperado).");
        var ev = string.Join(" ", parts);
        var limited = (t.Events37 ?? 0) > 0 || t.PerformanceLimitPercent < LimitAttentionPercent;
        if (!limited) return F(ThrottleId, "Processador sem limitação do firmware", DiagnosticSeverity.Ok, ev);
        const string explanation = "O firmware limitou o processador: pode ser temperatura, energia ou limite do fabricante — confira ventilação e use o modo de energia do fabricante na tomada. " +
                                   "O NEXUS só lê; não muda nada aqui.";
        const string gain = "Segundo a Intel, o evento 37 indica limite de temperatura, de energia ou de corrente imposto pelo firmware; nem sempre é superaquecimento.";
        var strong = (t.Events37 ?? 0) >= 3 || t.PerformanceLimitPercent < 80;
        return F(ThrottleId, "O firmware limitou a velocidade do processador", strong && t.OnBattery != true ? DiagnosticSeverity.Attention : DiagnosticSeverity.Info,
            ev, explanation, gain, ThrottleSource);
    }

    // ------------------------------------------------------------------ 4. notebook na bateria

    /// <summary>Intervalo mínimo entre dois avisos de bateria (vários jogos seguidos não viram uma enxurrada de avisos).</summary>
    public static readonly TimeSpan BatteryNoticeInterval = TimeSpan.FromMinutes(30);

    /// <summary>Mostrar "Na bateria o FPS cai bastante; ligue na tomada" quando um jogo começa?</summary>
    public static bool ShouldNotifyBattery(bool enabled, bool? hasBattery, bool? onBattery, DateTimeOffset? lastShown, DateTimeOffset now) =>
        enabled && hasBattery != false && onBattery == true && (lastShown is not { } last || now - last >= BatteryNoticeInterval);
}
