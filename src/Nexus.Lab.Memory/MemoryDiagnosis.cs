using System.Globalization;
using Nexus.HardwareDiscovery;

namespace Nexus.Lab.Memory;

public sealed record MemorySource(string Title, string Url);

/// <summary>Achado do diagnóstico. <see cref="Severity"/>: "bad", "warn", "info" ou "good".</summary>
public sealed record MemoryFinding(
    string Id, string Severity, string Title, string Evidence, string Recommendation,
    IReadOnlyList<MemorySource> Sources, IReadOnlyList<string> Steps, string? ActionId = null);

/// <summary>O que se sabe do PC além da memória (fabricante, placa, se é notebook, placas de vídeo).</summary>
public sealed record MachineInfo(string? SystemMaker, string? Model, string? BoardMaker, bool? Portable, bool HasIntegratedGpu, bool HasDiscreteGpu);

/// <summary>Tudo que o diagnóstico usa. Medições e estado do Windows são opcionais (null = ainda não lidos).</summary>
public sealed record MemoryLabFacts(
    MemoryInventory Inventory,
    ChannelLayout Layout,
    IReadOnlyList<PartNumberInfo> Parts,
    MachineInfo Machine,
    MemoryState? State = null,
    BandwidthResult? Bandwidth = null,
    ChannelCheck? Check = null,
    LatencyResult? Latency = null);

public sealed record GamingImpact(string Headline, string Detail, IReadOnlyList<MemorySource> Sources);

/// <summary>Regras do diagnóstico da memória. Puras (testáveis com fatos falsos); cada achado traz evidência e fonte.</summary>
public static class MemoryDiagnosis
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public const string PageFileActionId = "memorylab.pagefile.automatic";
    private const ulong GiB = 1024UL * 1024 * 1024;

    public static readonly MemorySource TechSpot = new("TechSpot: Single Stick vs. Dual Channel RAM (Ryzen 7 9700X + RTX 5090, DDR5-6000)", "https://www.techspot.com/article/3066-single-stick-vs-dual-channel-ram/");
    public static readonly MemorySource TomsLaptop = new("Tom's Hardware: guia de RAM para notebooks Ryzen 4000 (Ryzen 7 4800H + RTX 2060)", "https://www.tomshardware.com/features/ryzen-4000-ram-guide-picking-the-best-ram-for-your-laptop");
    public static readonly MemorySource UltrabookReview = new("UltrabookReview: single vs dual channel no Zephyrus G14 (Ryzen 7 4800HS + RTX 2060)", "https://www.ultrabookreview.com/36308-single-dual-channel-ram-amd");
    public static readonly MemorySource LaptopMedia = new("LaptopMedia: single vs dual channel em notebooks (Core i5-11300H/i7-11370H + RTX 3050)", "https://laptopmedia.com/highlights/the-bottleneck-effect-gaming-performance-with-single-vs-dual-channel-memory/");
    public static readonly MemorySource AsusXmp = new("ASUS: como ativar XMP/EXPO/DOCP no BIOS", "https://www.asus.com/support/faq/1042256/");
    public static readonly MemorySource CorsairXmp = new("Corsair: como ativar XMP/EXPO no BIOS (ASUS, MSI, Gigabyte, ASRock)", "https://www.corsair.com/us/en/explorer/diy-builder/memory/how-to-enable-xmpexpo-in-bios/");
    public static readonly MemorySource MsPageFile = new("Microsoft: como determinar o tamanho do arquivo de paginação", "https://learn.microsoft.com/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows");
    public static readonly MemorySource MsMmAgent = new("Microsoft: Get-MMAgent / Enable-MMAgent (compressão de memória)", "https://learn.microsoft.com/powershell/module/mmagent/enable-mmagent");
    public static readonly MemorySource Smbios = new("DMTF SMBIOS (DSP0134): tipos 16 e 17", "https://www.dmtf.org/standards/smbios");

    public static IReadOnlyList<MemoryFinding> Evaluate(MemoryLabFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var list = new List<MemoryFinding>();
        var populated = f.Inventory.Populated;
        if (populated.Count == 0) return list;
        var portable = f.Machine.Portable == true;

        SingleChannel(f, list, portable);
        Speed(f, list, portable);
        Mixed(f, list);
        KitSplit(f, list);
        Capacity(f, list);
        PageFile(f, list);
        Compression(f, list);
        Efficiency(f, list);
        return list;
    }

    private static string Gb(ulong? bytes) => bytes is { } b ? (b / (double)GiB).ToString(b % GiB == 0 ? "0" : "0.0", Pt) + " GB" : "?";

    private static string Describe(MemoryDevice d)
    {
        var speed = d.SpeedMts is { } s ? $"-{s}" : "";
        var form = d.FormFactor switch { MemoryFormFactor.SoDimm => " SODIMM", MemoryFormFactor.Dimm => " DIMM", _ => "" };
        return $"{Gb(d.SizeBytes)} {d.Type ?? "DDR"}{speed}{form}";
    }

    private static void SingleChannel(MemoryLabFacts f, List<MemoryFinding> list, bool portable)
    {
        var layout = f.Layout;
        if (layout.Mode == ChannelMode.DualChannel && f.Check?.Evidence == ChannelEvidence.ProvesMultiple)
        {
            list.Add(new MemoryFinding("dual-channel", "good", "Dual channel funcionando",
                f.Check.Text, "Nada a fazer: a memória já usa os dois canais.", [Smbios], []));
            return;
        }
        if (layout.Mode != ChannelMode.SingleChannel) return;

        var populated = f.Inventory.Populated;
        var first = populated[0];
        var soldered = populated.Any(d => d.LooksSoldered);
        var emptySlots = f.Inventory.Empty.Where(d => !d.LooksSoldered).ToList();
        var evidence = $"{populated.Count} módulo{(populated.Count > 1 ? "s" : "")} ({Describe(first)}) ocupando só o canal {(layout.PopulatedChannels.Count > 0 ? layout.PopulatedChannels[0] : "?")}"
                       + (layout.EmptyChannels.Count > 0 ? $"; canal {string.Join(", ", layout.EmptyChannels)} vazio." : ".");
        if (f.Check is { Evidence: not ChannelEvidence.NotMeasured } c) evidence += " " + c.Text;
        if (f.Check is { Evidence: ChannelEvidence.ProvesMultiple }) return; // a medição desmente os nomes dos slots

        var pn = f.Parts.FirstOrDefault(p => p.Decoded)?.PartNumber ?? first.PartNumber;
        string recommendation;
        var steps = new List<string>();
        string severity;
        if (emptySlots.Count > 0)
        {
            severity = "bad";
            recommendation = soldered
                ? $"Parte da memória é soldada e ocupa um canal; o slot livre ativa o outro. Instale um módulo de {Gb(first.SizeBytes)} {first.Type}"
                  + (first.SpeedMts is { } s0 ? $"-{s0}" : "") + " para dobrar a banda."
                : $"Instale um segundo módulo igual: {Describe(first)}{(pn is null ? "" : $" (de preferência o mesmo número de peça, {pn})")}. Com os dois canais a banda teórica dobra (~2×).";
            steps.Add($"O SMBIOS lista {emptySlots.Count} slot{(emptySlots.Count > 1 ? "s" : "")} vazio{(emptySlots.Count > 1 ? "s" : "")}: {string.Join(", ", emptySlots.Select(s => s.Locator ?? s.BankLocator ?? "sem nome"))}.");
            steps.Add("Compre o mesmo tamanho, a mesma geração e a mesma velocidade (MT/s). Mesma marca e modelo é o mais seguro.");
            if (portable) steps.Add("Notebook: desligue, tire da tomada e remova a bateria interna (ou use o modo de envio do BIOS) antes de abrir a tampa inferior. Confira no manual do fabricante onde fica o slot.");
            else steps.Add("PC de mesa: veja no manual da placa quais slots formam o par de canais (geralmente A2 e B2).");
            steps.Add("Depois de instalar, volte aqui e meça de novo: a banda de leitura deve quase dobrar.");
        }
        else if (f.Inventory.MaxCapacityBytes is not null && f.Inventory.Devices.Count > populated.Count)
        {
            severity = "warn";
            recommendation = "Há posição de memória sem módulo, mas o firmware não diz se é um slot acessível. Confira no manual do seu modelo antes de comprar.";
        }
        else
        {
            severity = "warn";
            recommendation = $"O SMBIOS não lista slot livre{(f.Machine.Model is null ? "" : $" no {f.Machine.Model}")}. A saída para dual channel seria trocar por dois módulos iguais (ex.: 2×{Gb(first.SizeBytes)}), se o modelo tiver dois slots. Confira o manual.";
        }
        list.Add(new MemoryFinding("single-channel", severity, "Memória em single channel (um canal só)", evidence, recommendation,
            [TechSpot, TomsLaptop, UltrabookReview, LaptopMedia], steps));
    }

    private static void Speed(MemoryLabFacts f, List<MemoryFinding> list, bool portable)
    {
        var populated = f.Inventory.Populated;
        var configured = populated.Select(d => d.ConfiguredSpeedMts).FirstOrDefault(s => s is not null);
        if (configured is not { } conf) return;
        var nominal = populated.Select(d => d.SpeedMts).Where(s => s is not null).DefaultIfEmpty(null).Min();
        var advertised = f.Parts.Where(p => p.Decoded && p.SpeedMts is not null).Select(p => p.SpeedMts).DefaultIfEmpty(null).Min();
        var profile = f.Parts.FirstOrDefault(p => p.Profile is "XMP" or "EXPO" or "XMP/EXPO")?.Profile;
        var target = Math.Max(nominal ?? 0, (uint)(advertised ?? 0));
        if (target == 0 || conf >= target * 0.97) return;

        var evidence = $"A memória está rodando a {conf} MT/s";
        if (advertised is { } adv && adv >= target) evidence += $"; o número de peça anuncia {adv} MT/s{(profile is null ? "" : $" com perfil {profile}")}";
        if (nominal is { } nom) evidence += $"; o módulo informa {nom} MT/s no SMBIOS";
        evidence += ".";

        if (portable)
        {
            list.Add(new MemoryFinding("speed-limited", "info", "A memória roda abaixo do que o módulo suporta",
                evidence,
                "Em notebook isso normalmente é limite da plataforma (processador ou BIOS do fabricante), não erro: notebooks quase nunca têm opção de XMP/EXPO. Comprar módulo mais rápido não adianta acima do que o notebook aceita.",
                [CorsairXmp], []));
            return;
        }

        var maker = (f.Machine.BoardMaker ?? f.Machine.SystemMaker ?? "").ToUpperInvariant();
        var steps = new List<string> { "Reinicie e entre no BIOS (geralmente tecla Del ou F2 logo ao ligar)." };
        if (maker.Contains("ASUS", StringComparison.Ordinal))
            steps.Add("ASUS: F7 (modo avançado) > Ai Tweaker > Ai Overclock Tuner > XMP I (Intel) ou EXPO / D.O.C.P. (AMD).");
        else if (maker.Contains("MICRO-STAR", StringComparison.Ordinal) || maker.Contains("MSI", StringComparison.Ordinal))
            steps.Add("MSI: no topo do Click BIOS, ative o botão A-XMP / EXPO (ou OC > Extreme Memory Profile > Profile 1).");
        else if (maker.Contains("GIGABYTE", StringComparison.Ordinal))
            steps.Add("Gigabyte: Tweaker > Extreme Memory Profile (X.M.P.) / EXPO > Profile1.");
        else if (maker.Contains("ASROCK", StringComparison.Ordinal))
            steps.Add("ASRock: OC Tweaker > DRAM Configuration > Load XMP Setting (ou EXPO) > Profile 1.");
        else
            steps.Add("Procure por XMP, EXPO, D.O.C.P. ou Memory Profile (menus de overclock/memória) e escolha o Profile 1.");
        steps.Add("Salve com F10 e reinicie. Se o PC não ligar ou travar, ele volta sozinho ao padrão depois de algumas tentativas (ou limpe o CMOS).");
        steps.Add("Volte aqui e meça: a velocidade configurada deve mostrar o valor do perfil.");
        list.Add(new MemoryFinding("xmp-off", "warn", $"Perfil {profile ?? "XMP/EXPO"} provavelmente desligado no BIOS", evidence,
            $"Ligar o perfil {profile ?? "XMP/EXPO"} no BIOS faz a memória rodar na velocidade anunciada (é um ajuste do fabricante do módulo, testado por ele). O NEXUS não muda o BIOS: siga os passos.",
            [AsusXmp, CorsairXmp], steps));
    }

    private static void Mixed(MemoryLabFacts f, List<MemoryFinding> list)
    {
        var p = f.Inventory.Populated.Where(d => !d.LooksSoldered).ToList();
        if (p.Count < 2) return;
        var sizes = p.Select(d => d.SizeBytes).Distinct().Count();
        var speeds = p.Select(d => d.SpeedMts).Distinct().Count();
        var parts = p.Select(d => d.PartNumber ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (sizes == 1 && speeds == 1 && parts == 1) return;
        var what = new List<string>();
        if (sizes > 1) what.Add("tamanhos diferentes (" + string.Join(" + ", p.Select(d => Gb(d.SizeBytes))) + ")");
        if (speeds > 1) what.Add("velocidades diferentes (" + string.Join(" / ", p.Select(d => d.SpeedMts?.ToString(CultureInfo.InvariantCulture) ?? "?")) + " MT/s)");
        if (parts > 1 && sizes == 1 && speeds == 1) what.Add("modelos diferentes");
        list.Add(new MemoryFinding("mixed-modules", sizes > 1 || speeds > 1 ? "warn" : "info", "Módulos diferentes misturados",
            "Módulos com " + string.Join(", ", what) + ".",
            "Funciona, mas todos rodam na velocidade do mais lento; com tamanhos diferentes só a parte igual fica em dual channel (o resto em single). Num upgrade, prefira um par igual.",
            [TechSpot], []));
    }

    private static void KitSplit(MemoryLabFacts f, List<MemoryFinding> list)
    {
        foreach (var part in f.Parts.Where(p => p.Decoded && p.KitModules is > 1).DistinctBy(p => p.PartNumber))
        {
            var installed = f.Inventory.Populated.Count(d => string.Equals(d.PartNumber, part.PartNumber, StringComparison.OrdinalIgnoreCase));
            if (installed >= part.KitModules) continue;
            list.Add(new MemoryFinding("kit-split", "info", "Kit incompleto",
                $"O número {part.PartNumber} é de um kit de {part.KitModules} módulos, mas só {installed} está instalado.",
                "O outro módulo do kit é o par ideal para dual channel (mesmo lote, já testado junto).", [], []));
        }
    }

    private static void Capacity(MemoryLabFacts f, List<MemoryFinding> list)
    {
        var installed = f.Inventory.InstalledBytes;
        var total = f.State?.TotalPhysicalBytes ?? installed;
        var peak = f.State?.CommitPeakBytes;
        var hard = f.State?.PagesInputPerSec;
        var evidence = $"{Gb(installed)} instalados.";
        var pressure = false;
        if (peak is { } pk && total > 0)
        {
            evidence += $" Desde que o PC ligou, o pico de memória comprometida foi {Gb(pk)}";
            if (pk > total * 0.9) { evidence += $" — acima de 90% da RAM ({Gb(total)}): o Windows teve de usar o arquivo de paginação."; pressure = true; }
            else evidence += ".";
        }
        if (hard is { } h && h > 50) { evidence += string.Create(Pt, $" Agora: {h:N0} páginas/s lidas do disco por falta de RAM."); pressure = true; }

        if (installed <= 8 * GiB + GiB / 2)
            list.Add(new MemoryFinding("low-capacity", pressure ? "bad" : "warn", "Pouca RAM para jogos atuais", evidence,
                "Jogos recentes pedem 16 GB nos requisitos recomendados; com 8 GB, o jogo divide a memória com o Windows, navegador e Discord, e o que não cabe vai para o SSD (travadas). Fechar programas ajuda; o upgrade para 16 GB (2×8 GB em dual channel) resolve capacidade e banda de uma vez.",
                [TechSpot], []));
        else if (pressure)
            list.Add(new MemoryFinding("commit-pressure", "warn", "A memória já chegou perto do limite", evidence,
                "Veja no Monitor quais programas usam mais memória quando isso acontece. Se for o jogo, mais RAM ajuda.", [], []));
    }

    private static void PageFile(MemoryLabFacts f, List<MemoryFinding> list)
    {
        var s = f.State;
        if (s is null) return;
        var total = s.TotalPhysicalBytes ?? f.Inventory.InstalledBytes;
        if (s.PageFile.Mode == PageFileMode.None)
        {
            list.Add(new MemoryFinding("pagefile-off", total <= 16 * GiB ? "bad" : "warn", "Arquivo de paginação desligado",
                $"Não há arquivo de paginação, com {Gb(total)} de RAM. O limite de memória comprometida fica igual à RAM ({Gb(s.CommitLimitBytes)}).",
                "Sem paginação, quando a RAM acaba o programa fecha com \"memória insuficiente\" em vez de ficar só mais lento, e o Windows não grava despejo de erro (tela azul). A Microsoft recomenda deixar o Windows gerenciar. O NEXUS pode voltar para o modo automático (reversível; vale depois de reiniciar).",
                [MsPageFile], [], PageFileActionId));
        }
        else if (s.PageFile.Mode == PageFileMode.Custom && s.PageFile.Entries.Where(e => !e.SystemManaged).Sum(e => (long)(e.MaximumMb ?? 0)) is var maxMb
                 && maxMb > 0 && (ulong)maxMb * 1024 * 1024 < total / 2 && total <= 16 * GiB)
        {
            list.Add(new MemoryFinding("pagefile-small", "warn", "Arquivo de paginação fixo e pequeno",
                string.Create(Pt, $"O arquivo de paginação está fixo em no máximo {maxMb:N0} MB, com {Gb(total)} de RAM. ") + s.PageFile.Describe() + ".",
                "Com pouca RAM, um limite baixo faz jogos fecharem quando a memória enche. O modo automático deixa o Windows crescer o arquivo quando precisa.",
                [MsPageFile], [], PageFileActionId));
        }
    }

    private static void Compression(MemoryLabFacts f, List<MemoryFinding> list)
    {
        if (f.State?.CompressionEnabled != false) return;
        list.Add(new MemoryFinding("compression-off", "info", "Compressão de memória desligada",
            $"Fonte: {f.State.CompressionSource}.",
            "A compressão vem ligada no Windows 10/11: páginas pouco usadas são comprimidas na RAM em vez de irem para o disco, o que ajuda PCs com pouca memória. Se alguém desligou, dá para religar com Enable-MMAgent -MemoryCompression (PowerShell como administrador) e reiniciar.",
            [MsMmAgent], []));
    }

    private static void Efficiency(MemoryLabFacts f, List<MemoryFinding> list)
    {
        if (f.Check is not { Evidence: ChannelEvidence.Inconclusive } c) return;
        list.Add(new MemoryFinding("bandwidth-low", "info", "Banda medida muito abaixo do esperado", c.Text,
            "No notebook, meça com o carregador ligado e no modo de energia de desempenho; feche jogos e navegador.", [], []));
    }

    /// <summary>Impacto esperado em jogos, sem número exato prometido: faixas de testes públicos e o motivo.</summary>
    public static GamingImpact Gaming(ChannelLayout layout, MachineInfo machine)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(machine);
        if (layout.Mode != ChannelMode.SingleChannel)
            return new GamingImpact("Sem perda por canal", "A memória já trabalha em mais de um canal; o ganho de banda já está aproveitado.", []);
        var detail =
            "Com placa de vídeo dedicada, o efeito aparece quando o jogo depende do processador (1080p, muitos FPS, mundos abertos): " +
            "em testes públicos, dois módulos deram de 3% a 19% mais FPS médios em notebooks (Ryzen 4800H/4800HS + RTX 2060) e cerca de 12% na média com " +
            "16% nos 1% mais lentos num desktop (Ryzen 9700X + RTX 5090), com casos de 25% a 70% em cenas leves para a placa de vídeo. Em 1440p/4K, limitado pela placa, a diferença costuma ser pequena.";
        if (machine.HasIntegratedGpu)
            detail += " Quando o jogo roda na GPU integrada (Radeon/Intel do processador), ela usa a própria RAM como memória de vídeo, então metade da banda pesa direto nos FPS — muito mais que com a placa dedicada.";
        return new GamingImpact("Perda típica de 5% a 20% nos FPS (mais nos 1% mais lentos)", detail, [TechSpot, TomsLaptop, UltrabookReview, LaptopMedia]);
    }
}
