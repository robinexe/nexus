using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>
/// Uma das 5 áreas da navegação lateral ("Instrumento de precisão", docs/14-redesign-brief.md): o nome e o ícone na lista, as
/// abas que aparecem no topo do conteúdo (a primeira é a que a lista abre) e as ferramentas recolhidas em "Avançado".
/// </summary>
public sealed record NavArea(string Key, string IconKey, IReadOnlyList<string> Tabs, IReadOnlyList<string> Advanced)
{
    public string Entry => Tabs[0];
    public bool Contains(string pageTypeName) => Tabs.Contains(pageTypeName, StringComparer.Ordinal) || Advanced.Contains(pageTypeName, StringComparer.Ordinal);
}

/// <summary>
/// Navegação da interface (lógica pura, testada em SimpleModeTests). A interface é uma só (decisão do responsável de 02/11). Desde
/// 05/11 (diretriz de produto, docs/13): 5 áreas na lista lateral — Início, Otimizar, Desempenho, Diagnóstico, Configurações. Cada
/// página do NEXUS pertence a exatamente uma área: como aba (o dia a dia) ou em "Avançado" (recolhida). As páginas continuam as
/// mesmas; muda só onde cada uma fica.
/// </summary>
public static class ShellNavigation
{
    /// <summary>As 6 telas simples (vitrines sobre as páginas completas). Não são mais a lista lateral: viraram abas das áreas.</summary>
    public static readonly IReadOnlyList<string> SimplePages =
        ["HomePage", "SimpleOptimizePage", "SimpleCleanPage", "SimpleGamesPage", "SimpleProblemsPage", "SimpleHistoryPage"];

    /// <summary>A página que lista todas as ferramentas em ladrilhos (Configurações › Avançado).</summary>
    public const string AdvancedPage = "AdvancedToolsPage";

    /// <summary>Página aberta ao iniciar.</summary>
    public const string StartPage = "HomePage";

    /// <summary>As 5 áreas, na ordem da lista lateral.</summary>
    public static readonly IReadOnlyList<NavArea> Areas =
    [
        new("home", "Icon.Home", ["HomePage"], []),
        new("optimize", "Icon.Bolt", ["SimpleOptimizePage", "SimpleCleanPage"],
            ["CleaningPage", "StartupPage", "ProgramsPage", "ServicesPage", "PrivacyPage", "DiskPage", "CatalogPage", "AssistantPage", "PlanPage", "BrokerPage"]),
        new("performance", "Icon.Measure", ["SimpleGamesPage", "MeasurePage", "AbLabPage", "MonitorPage"],
            ["CpuLabPage", "GpuLabPage", "MemoryLabPage", "MemoryDoctorPage", "HardwarePage", "OverviewPage"]),
        new("diagnostics", "Icon.Diagnostics", ["SimpleProblemsPage", "DriverUpdatesPage", "SecurityPage"],
            ["DiagnosticsPage", "ScannerPage", "SystemToolsPage", "CapabilitiesPage"]),
        new("settings", "Icon.Settings", ["SettingsPage", "AutomationPage", "SimpleHistoryPage"],
            ["HistoryPage", AdvancedPage]),
    ];

    /// <summary>O que a lista lateral abre: a primeira aba de cada área.</summary>
    public static IReadOnlyList<string> SidebarPages { get; } = [.. Areas.Select(a => a.Entry)];

    public static bool IsInNavigation(string pageTypeName) => SidebarPages.Contains(pageTypeName, StringComparer.Ordinal);

    /// <summary>Índice da área da página (aba ou avançado). Página desconhecida: Início (0).</summary>
    public static int AreaIndexOf(string? pageTypeName)
    {
        if (pageTypeName is null) return 0;
        for (var i = 0; i < Areas.Count; i++) if (Areas[i].Contains(pageTypeName)) return i;
        return 0;
    }

    /// <summary>As páginas que a lista lateral abre, na ordem das áreas.</summary>
    public static IReadOnlyList<T> Navigation<T>(IEnumerable<T> pages, Func<T, string> typeName)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(typeName);
        return [.. pages.Where(p => IsInNavigation(typeName(p))).OrderBy(p => IndexIn(SidebarPages, typeName(p)))];
    }

    /// <summary>As páginas de uma lista de nomes (abas ou avançado de uma área), na ordem da lista.</summary>
    public static IReadOnlyList<T> Pick<T>(IEnumerable<T> pages, Func<T, string> typeName, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(names);
        return [.. pages.Where(p => names.Contains(typeName(p), StringComparer.Ordinal)).OrderBy(p => IndexIn(names, typeName(p)))];
    }

    /// <summary>Todas as ferramentas completas (a página "Ferramentas avançadas"): tudo menos as telas simples e ela mesma.</summary>
    public static IReadOnlyList<T> Advanced<T>(IEnumerable<T> pages, Func<T, string> typeName)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(typeName);
        return [.. pages.Where(p => !SimplePages.Contains(typeName(p), StringComparer.Ordinal) && typeName(p) != AdvancedPage)];
    }

    private static int IndexIn(IReadOnlyList<string> list, string name)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == name) return i;
        return list.Count;
    }

    /// <summary>Valor gravado em settings.json depois da primeira abertura desta versão.</summary>
    public const string StoredSimple = "simple";
}

/// <summary>O que fazer ao abrir: mostrar a boas-vindas (só quem nunca usou) e gravar "simple" (marca que já passou por aqui).</summary>
public sealed record UiStartup(bool ShowWelcome, bool SaveSimple);

public static class UiModeStartup
{
    /// <summary>
    /// Todos usam a interface simples. "simple" gravado: nada a fazer. Nada gravado: quem nunca usou o NEXUS vê a boas-vindas;
    /// quem já usava não (e grava). Qualquer outro valor (o antigo "full" do modo completo) é migrado para "simple".
    /// </summary>
    public static UiStartup Decide(string? storedMode, bool hadPriorUse)
    {
        var stored = storedMode?.Trim().ToLowerInvariant();
        if (stored == ShellNavigation.StoredSimple) return new UiStartup(false, false);
        if (string.IsNullOrEmpty(stored) && !hadPriorUse) return new UiStartup(true, false); // grava ao fechar a boas-vindas
        return new UiStartup(false, true);
    }
}

// ================================================================== saúde do PC (anel da tela Início)

/// <summary>"Ótimo / pode melhorar / precisa de atenção" — a palavra grande da tela Início.</summary>
public enum HealthLevel { Great, CouldImprove, NeedsAttention }

/// <summary>
/// O que já foi lido do PC e entra na nota. NaN/null = ainda não lido (não pesa). Tudo vem de leituras existentes:
/// diagnóstico (DiagnosticRules + OwnerChecks), espaço recuperável (Limpeza), ajustes recomendados ainda não feitos
/// (Automático + catálogo), driver de vídeo e módulos de memória.
/// </summary>
public sealed record HealthInputs
{
    public IReadOnlyList<DiagnosticFinding> Findings { get; init; } = [];
    /// <summary>Bytes que a limpeza segura libera (NaN = ainda medindo).</summary>
    public double RecoverableBytes { get; init; } = double.NaN;
    /// <summary>Ajustes de risco baixo recomendados para este PC que ainda não estão aplicados (null = sem leitura).</summary>
    public int? PendingRecommendations { get; init; }
    /// <summary>Data do driver da placa de vídeo principal (null = sem leitura).</summary>
    public DateTime? GpuDriverDate { get; init; }
    /// <summary>Pentes de memória com tamanho lido (0 = sem leitura).</summary>
    public int MemoryModules { get; init; }
    /// <summary>Slots listados pelo firmware (inclui vazios quando o firmware lista).</summary>
    public int MemorySlots { get; init; }
    /// <summary>Hoje (testes fixam).</summary>
    public DateTime Now { get; init; } = DateTime.Now;
    /// <summary>Frases simples já montadas com os números da leitura (checagens do PC: rede, discos, monitor, processador).</summary>
    public IReadOnlyDictionary<string, PlainFinding>? Plain { get; init; }

    public bool HasData => Findings.Count > 0;
    public bool SingleChannel => MemoryModules == 1;
    /// <summary>Anos desde o driver de vídeo (null = sem leitura).</summary>
    public double? DriverAgeYears => GpuDriverDate is { } d && d <= Now ? (Now - d).TotalDays / 365.25 : null;
    public int AttentionCount => Findings.Count(f => f.Severity == DiagnosticSeverity.Attention);
}

public sealed record HealthResult(double Score, HealthLevel Level);

/// <summary>
/// Nota de 0 a 100 (lógica pura, testada). Parte de 100 e desconta o que dá para melhorar, com teto por assunto para um
/// único problema não dominar: pontos de atenção (8 cada, até 40), lixo recuperável (5–15), ajustes pendentes (2 cada, até
/// 15), driver de vídeo com mais de 1 ano (10; 2 anos: 15) e memória num canal só (10). Não é nota de desempenho: é "quanto
/// do que o NEXUS sabe resolver ainda está por fazer".
/// </summary>
public static partial class SimpleHealth
{
    public const double GreatAt = 80;
    public const double ImproveAt = 55;
    public const double Gigabyte = 1024d * 1024 * 1024;

    public static HealthResult Compute(HealthInputs i)
    {
        ArgumentNullException.ThrowIfNull(i);
        double score = 100;
        // O canal único já desconta abaixo: o achado "número ímpar de módulos" não desconta de novo.
        var attention = i.Findings.Count(f => f.Severity == DiagnosticSeverity.Attention && !(i.SingleChannel && f.Id == "mem.channels"));
        score -= Math.Min(40, attention * 8);
        if (double.IsFinite(i.RecoverableBytes))
            score -= i.RecoverableBytes >= 20 * Gigabyte ? 15 : i.RecoverableBytes >= 5 * Gigabyte ? 10 : i.RecoverableBytes >= 1 * Gigabyte ? 5 : 0;
        if (i.PendingRecommendations is { } pending) score -= Math.Min(15, pending * 2);
        if (i.DriverAgeYears is { } age) score -= age >= 2 ? 15 : age >= 1 ? 10 : 0;
        if (i.SingleChannel) score -= 10;
        score = Math.Clamp(score, 0, 100);
        return new HealthResult(score, LevelOf(score));
    }

    public static HealthLevel LevelOf(double score) => score >= GreatAt ? HealthLevel.Great : score >= ImproveAt ? HealthLevel.CouldImprove : HealthLevel.NeedsAttention;

    /// <summary>"Seu PC está ótimo" etc.</summary>
    public static string Headline(HealthLevel level) => level switch
    {
        HealthLevel.Great => Loc.T("Simple.Health.Great"),
        HealthLevel.CouldImprove => Loc.T("Simple.Health.Improve"),
        _ => Loc.T("Simple.Health.Attention"),
    };

    /// <summary>A palavra do centro do anel ("ótimo").</summary>
    public static string Word(HealthLevel level) => level switch
    {
        HealthLevel.Great => Loc.T("Simple.Health.WordGreat"),
        HealthLevel.CouldImprove => Loc.T("Simple.Health.WordImprove"),
        _ => Loc.T("Simple.Health.WordAttention"),
    };

    /// <summary>
    /// Data do driver como o Windows grava (DriverDate "M-d-yyyy", às vezes "yyyy-MM-dd" ou "dd/MM/yyyy"). Só o ano: 1º de
    /// julho daquele ano. Ilegível: null.
    /// </summary>
    public static DateTime? ParseDriverDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        string[] formats = ["M-d-yyyy", "MM-dd-yyyy", "yyyy-MM-dd", "yyyyMMdd", "M/d/yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-M-d"];
        if (DateTime.TryParseExact(t.Split(' ')[0], formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        var y = YearRegex().Match(t);
        return y.Success && int.TryParse(y.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? new DateTime(year, 7, 1) : null;
    }

    /// <summary>A placa de vídeo que roda os jogos (dedicada antes da integrada; o mesmo critério da página Meu PC).</summary>
    public static GpuInfo? MainGpu(IEnumerable<GpuInfo> gpus)
    {
        ArgumentNullException.ThrowIfNull(gpus);
        static int Rank(GpuInfo g)
        {
            var n = g.Name ?? "";
            if (string.Equals(g.Vendor, "Microsoft", StringComparison.OrdinalIgnoreCase) || n.Contains("Basic", StringComparison.OrdinalIgnoreCase)) return 0;
            if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("RTX", StringComparison.OrdinalIgnoreCase) || n.Contains("Quadro", StringComparison.OrdinalIgnoreCase)) return 3;
            if (n.Contains(" RX ", StringComparison.OrdinalIgnoreCase) || n.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }
        return gpus.OrderByDescending(Rank).ThenByDescending(g => g.MemoryBytes ?? 0).FirstOrDefault();
    }

    [GeneratedRegex(@"(?<!\d)(19|20)\d{2}(?!\d)")]
    private static partial Regex YearRegex();
}

// ================================================================== "O que dá para melhorar" (no máximo 3 cartões)

/// <summary>Que tipo de melhoria (decide o botão e a ordem quando o impacto empata).</summary>
public enum ImproveKind { Cleanup, GpuDriver, SingleChannel, PendingTweaks, LowDisk, SlowDisk, Finding, DiskHealth, Monitor }

/// <summary>Um cartão da tela Início: uma frase, um botão, a página que o botão abre e o impacto (para ordenar).</summary>
public sealed record ImproveCard(ImproveKind Kind, string Text, string ActionText, string Target, int Impact, string IconKey)
{
    /// <summary>Explicação de uma ou duas frases (só alguns cartões, como o da memória: "Ver como" abre isto).</summary>
    public string? HowTo { get; init; }
    /// <summary>Achado do diagnóstico que gerou o cartão (null nos outros).</summary>
    public string? FindingId { get; init; }
    /// <summary>Ação do catálogo que resolve o achado (o botão a aplica com um clique quando é de risco baixo).</summary>
    public string? FixActionId { get; init; }
}

public static class ImproveCards
{
    public const int Max = 3;

    /// <summary>Todas as melhorias possíveis a partir do que foi lido, sem ordem.</summary>
    public static IReadOnlyList<ImproveCard> Candidates(HealthInputs i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var list = new List<ImproveCard>();
        if (double.IsFinite(i.RecoverableBytes) && i.RecoverableBytes >= 1 * SimpleHealth.Gigabyte)
            list.Add(new ImproveCard(ImproveKind.Cleanup, Loc.F("Simple.Card.Cleanup", Bytes(i.RecoverableBytes)), Loc.T("Simple.Card.CleanupAction"),
                "SimpleCleanPage", (int)Math.Min(70, 30 + i.RecoverableBytes / SimpleHealth.Gigabyte * 2), "Icon.Clean"));
        if (i.DriverAgeYears is >= 1 && i.GpuDriverDate is { } date)
            list.Add(new ImproveCard(ImproveKind.GpuDriver, Loc.F("Simple.Card.Driver", date.Year), Loc.T("Simple.Card.DriverAction"),
                "DriverUpdatesPage", i.DriverAgeYears >= 2 ? 65 : 50, "Icon.Drivers"));
        if (i.SingleChannel)
            list.Add(new ImproveCard(ImproveKind.SingleChannel, Loc.T("Simple.Card.SingleChannel"), Loc.T("Simple.Card.SeeHow"), "MemoryLabPage", 60, "Icon.Ram")
            { HowTo = Loc.T("Simple.Card.SingleChannelHow") });
        if (i.PendingRecommendations is > 0 and var n)
            list.Add(new ImproveCard(ImproveKind.PendingTweaks, n == 1 ? Loc.T("Simple.Card.Tweaks1") : Loc.F("Simple.Card.TweaksN", n),
                Loc.T("Simple.Card.TweaksAction"), "SimpleOptimizePage", Math.Min(60, 25 + n * 3), "Icon.Bolt"));
        foreach (var f in i.Findings.Where(f => f.Severity == DiagnosticSeverity.Attention))
        {
            if (f.Id == "mem.channels" && i.SingleChannel) continue; // já é o cartão da memória
            var plain = PlainFindings.For(f, i.Plain);
            var kind = f.Id switch
            {
                "disk.system-free" => ImproveKind.LowDisk,
                "disk.system-media" => ImproveKind.SlowDisk,
                _ when f.Id.StartsWith(PcChecks.DisplayRefreshPrefix, StringComparison.Ordinal) => ImproveKind.Monitor,
                _ when f.Id.StartsWith(PcChecks.DiskPrefix, StringComparison.Ordinal) && f.Id != PcChecks.GamesOnSlowDiskId => ImproveKind.DiskHealth,
                _ => ImproveKind.Finding,
            };
            var (action, target) = kind switch
            {
                ImproveKind.LowDisk => (Loc.T("Simple.Card.CleanupAction"), "SimpleCleanPage"),
                _ => (Loc.T("Simple.Card.WhatToDo"), "SimpleProblemsPage"),
            };
            var impact = kind switch { ImproveKind.LowDisk => 68, ImproveKind.DiskHealth => 80, ImproveKind.Monitor => 62, _ => f.Owner is not null ? 45 : 40 };
            list.Add(new ImproveCard(kind, plain.Title, action, target, impact, plain.IconKey)
            { HowTo = plain.WhatToDo, FindingId = f.Id, FixActionId = f.RelatedActionId });
        }
        return list;
    }

    /// <summary>
    /// Os três de maior impacto, um por tipo (o primeiro achado do diagnóstico representa os outros: "Problemas" lista todos),
    /// e no máximo um por página de destino além de "Problemas".
    /// </summary>
    public static IReadOnlyList<ImproveCard> Top(HealthInputs i, int max = Max)
    {
        var picked = new List<ImproveCard>();
        foreach (var c in Candidates(i).OrderByDescending(c => c.Impact).ThenBy(c => (int)c.Kind))
        {
            if (picked.Count >= max) break;
            if (picked.Any(p => p.Kind == c.Kind)) continue;
            if (c.Target != "SimpleProblemsPage" && picked.Any(p => p.Target == c.Target)) continue;
            picked.Add(c);
        }
        return picked;
    }

    /// <summary>"9,3 GB" no idioma da interface.</summary>
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var v = Math.Max(0, bytes);
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} B" : v.ToString(v >= 100 ? "0" : "0.0", Loc.Culture) + " " + units[u];
    }
}

// ================================================================== achados em palavras simples

/// <summary>Um achado do diagnóstico em linguagem de leigo: uma frase, o que fazer (1–2 frases), assunto e ícone.</summary>
public sealed record PlainFinding(string Title, string WhatToDo, string Topic, string IconKey);

/// <summary>
/// Tradução dos achados conhecidos para frases simples (sem valores de registro, siglas ou nomes de enum). Achado desconhecido:
/// o título da biblioteca e "abra os detalhes" — nunca a evidência técnica.
/// </summary>
public static class PlainFindings
{
    public static PlainFinding For(DiagnosticFinding f) => For(f, null);

    /// <summary>Com as frases já montadas pelas checagens do PC (têm os números da leitura); o resto pela tabela abaixo.</summary>
    public static PlainFinding For(DiagnosticFinding f, IReadOnlyDictionary<string, PlainFinding>? plain)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (plain is not null && plain.TryGetValue(f.Id, out var ready)) return ready;
        var topic = TopicOf(f.Id);
        return f.Id switch
        {
            "win.gamemode" => new(Loc.T("Simple.F.GameModeOff"), Loc.T("Simple.F.GameModeOffDo"), topic, "Icon.Games"),
            "win.gamedvr.capture" => new(Loc.T("Simple.F.CaptureOn"), Loc.T("Simple.F.CaptureOnDo"), topic, "Icon.Games"),
            "mem.speed" or "owner.xmp" => new(Loc.T("Simple.F.MemSlow"), Loc.T("Simple.F.MemSlowDo"), topic, "Icon.Ram"),
            "mem.channels" => new(Loc.T("Simple.Card.SingleChannel"), Loc.T("Simple.Card.SingleChannelHow"), topic, "Icon.Ram"),
            "disk.system-free" => new(Loc.T("Simple.F.LowDisk"), Loc.T("Simple.F.LowDiskDo"), topic, "Icon.Disk"),
            "disk.system-media" => new(Loc.T("Simple.F.Hdd"), Loc.T("Simple.F.HddDo"), topic, "Icon.Disk"),
            "owner.rebar" => new(Loc.T("Simple.F.Rebar"), Loc.T("Simple.F.RebarDo"), topic, "Icon.Gpu"),
            "owner.mux" => new(Loc.T("Simple.F.Mux"), Loc.T("Simple.F.MuxDo"), topic, "Icon.Gpu"),
            "owner.pcie" => new(Loc.T("Simple.F.Pcie"), Loc.T("Simple.F.PcieDo"), topic, "Icon.Gpu"),
            _ when f.Id.StartsWith("sec.", StringComparison.Ordinal) => new(Loc.T("Simple.F.Security"), Loc.T("Simple.F.SecurityDo"), topic, "Icon.Security"),
            _ when f.Id.StartsWith("gpu.", StringComparison.Ordinal) => new(Loc.T("Simple.F.Gpu"), Loc.T("Simple.F.GpuDo"), topic, "Icon.Gpu"),
            _ when f.Id.StartsWith("startup.", StringComparison.Ordinal) => new(Loc.T("Simple.F.Startup"), Loc.T("Simple.F.StartupDo"), topic, "Icon.Startup"),
            _ => new(f.Title, Loc.T("Simple.F.GenericDo"), topic, "Icon.Info"),
        };
    }

    /// <summary>Assunto do achado em uma palavra ("memória", "disco"…), para "Está tudo certo com: …".</summary>
    public static string TopicOf(string? id)
    {
        var x = id ?? "";
        if (x.StartsWith("net.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Network");
        if (x.StartsWith("display.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Monitor");
        if (x.StartsWith(PcChecks.DiskPrefix, StringComparison.Ordinal)) return Loc.T("Simple.Topic.Disk");
        if (x.StartsWith("mem.", StringComparison.Ordinal) || x == "owner.xmp") return Loc.T("Simple.Topic.Memory");
        if (x.StartsWith("disk.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Disk");
        if (x.StartsWith("gpu.", StringComparison.Ordinal) || x.StartsWith("owner.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Gpu");
        if (x.StartsWith("sec.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Security");
        if (x.StartsWith("power.", StringComparison.Ordinal) || x.StartsWith("cpu.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Power");
        if (x.StartsWith("win.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Games");
        if (x.StartsWith("startup.", StringComparison.Ordinal) || x.StartsWith("svc.", StringComparison.Ordinal)) return Loc.T("Simple.Topic.Startup");
        return Loc.T("Simple.Topic.Windows");
    }

    /// <summary>Ordem de impacto na lista "Problemas": os do hardware que mais pesam primeiro.</summary>
    public static int ImpactOf(DiagnosticFinding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return f.Id switch
        {
            PcChecks.GamesOnSlowDiskId => 60,
            _ when f.Id.StartsWith(PcChecks.DiskPrefix, StringComparison.Ordinal) => 95, // disco falhando: backup antes de tudo
            _ when f.Id.StartsWith(PcChecks.DisplayRefreshPrefix, StringComparison.Ordinal) => 72,
            PcChecks.ThrottleId => 65,
            _ when f.Id.StartsWith("net.", StringComparison.Ordinal) => 55,
            "disk.system-media" => 90,
            "disk.system-free" => 85,
            "mem.channels" or "mem.speed" or "owner.xmp" => 80,
            "owner.mux" or "owner.rebar" or "owner.pcie" => 70,
            _ when f.Id.StartsWith("sec.", StringComparison.Ordinal) => 75,
            "win.gamemode" or "win.gamedvr.capture" => 50,
            _ => 40,
        };
    }

    /// <summary>Assuntos sem nenhum ponto de atenção ("memória, disco, segurança"), na ordem em que aparecem.</summary>
    public static IReadOnlyList<string> FineTopics(IEnumerable<DiagnosticFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var all = findings.Where(f => f.Basis != FactStatus.Unavailable && f.Id is not ("proc.elevated" or "proc.not-elevated" or "env.vm")).ToList();
        var bad = all.Where(f => f.Severity == DiagnosticSeverity.Attention).Select(f => TopicOf(f.Id)).ToHashSet(StringComparer.Ordinal);
        return all.Select(f => TopicOf(f.Id)).Distinct(StringComparer.Ordinal).Where(t => !bad.Contains(t)).ToList();
    }
}

// ================================================================== Histórico em palavras simples

/// <summary>
/// O diagnóstico é lido ao abrir/reler o PC; depois de aplicar, só o catálogo é relido (Detect). Um achado de atenção cuja ação do
/// catálogo já está como ela deixa ("same") não volta como pendente em Início/Problemas: vira "está bem" até a próxima leitura.
/// </summary>
public static class ResolvedFindings
{
    public static IReadOnlyList<DiagnosticFinding> Apply(IReadOnlyList<DiagnosticFinding> findings, Func<string, string?> catalogReadState)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(catalogReadState);
        return [.. findings.Select(f => f.Severity == DiagnosticSeverity.Attention && f.RelatedActionId is { } id && catalogReadState(id) == "same"
            ? f with { Severity = DiagnosticSeverity.Ok }
            : f)];
    }
}

/// <summary>"Hoje 14:30 — Limpeza: 12 itens limpos" a partir do diário (lógica pura, testada): nunca chave de ação nem estado cru.</summary>
public static class SimpleHistoryText
{
    /// <summary>Assunto de uma execução pela maioria das ações (prefixo do id): Limpeza, Privacidade, Inicialização…</summary>
    public static string Category(IEnumerable<string> actionKeys)
    {
        ArgumentNullException.ThrowIfNull(actionKeys);
        var top = actionKeys.Select(CategoryKey).GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
        return top switch
        {
            "clean" => Loc.T("Simple.Hist.Cleaning"),
            "privacy" => Loc.T("Simple.Hist.Privacy"),
            "startup" => Loc.T("Simple.Hist.Startup"),
            "gpu" => Loc.T("Simple.Hist.Gpu"),
            "restore" => Loc.T("Simple.Hist.RestorePoint"),
            "drivers" => Loc.T("Simple.Hist.Drivers"),
            "programs" => Loc.T("Simple.Hist.Programs"),
            "security" => Loc.T("Simple.Hist.Security"),
            _ => Loc.T("Simple.Hist.Windows"),
        };
    }

    private static string CategoryKey(string key)
    {
        var k = key ?? "";
        if (k.StartsWith("cleaning.", StringComparison.Ordinal) || k.StartsWith("disk.", StringComparison.Ordinal) || k.StartsWith("files.", StringComparison.Ordinal)) return "clean";
        if (k.StartsWith("privacy.", StringComparison.Ordinal)) return "privacy";
        if (k.StartsWith("boot.", StringComparison.Ordinal) || k.StartsWith("startup.", StringComparison.Ordinal) || k.StartsWith("svc.", StringComparison.Ordinal)) return "startup";
        if (k.StartsWith("nvidia.", StringComparison.Ordinal) || k.StartsWith("gpu", StringComparison.Ordinal) || k.StartsWith("fps.", StringComparison.Ordinal) || k.StartsWith("cs2.", StringComparison.Ordinal) || k.StartsWith("fortnite.", StringComparison.Ordinal)) return "gpu";
        if (k.StartsWith("system.restore.create-point", StringComparison.Ordinal)) return "restore";
        if (k.StartsWith("driverupdates.", StringComparison.Ordinal)) return "drivers";
        if (k.StartsWith("programs.", StringComparison.Ordinal)) return "programs";
        if (k.StartsWith("security.", StringComparison.Ordinal) || k.StartsWith("scanner.", StringComparison.Ordinal)) return "security";
        return "windows";
    }

    /// <summary>"Limpeza: 12 itens limpos", "Privacidade: 3 mudanças".</summary>
    public static string Describe(IReadOnlyCollection<string> actionKeys)
    {
        ArgumentNullException.ThrowIfNull(actionKeys);
        var n = actionKeys.Count;
        var cleaning = actionKeys.Count(k => CategoryKey(k) == "clean") * 2 > n;
        var what = cleaning
            ? (n == 1 ? Loc.T("Simple.Hist.Cleaned1") : Loc.F("Simple.Hist.CleanedN", n))
            : (n == 1 ? Loc.T("Simple.Hist.Changes1") : Loc.F("Simple.Hist.ChangesN", n));
        return Loc.F("Simple.Hist.Line", Category(actionKeys), what);
    }

    /// <summary>"Hoje 14:30", "Ontem 09:12" ou "28/10 14:30" (data no formato do idioma).</summary>
    public static string When(DateTime local, DateTime now)
    {
        var time = local.ToString("t", Loc.Culture);
        if (local.Date == now.Date) return Loc.F("Simple.Hist.Today", time);
        if (local.Date == now.Date.AddDays(-1)) return Loc.F("Simple.Hist.Yesterday", time);
        var day = local.ToString(Loc.Language == "en" ? "M/d" : "dd/MM", Loc.Culture);
        return $"{day} {time}";
    }
}

/// <summary>Resumo amigável do "Otimizar meu PC com segurança" ("Pronto! Liberamos 9,3 GB, ajustamos 12 coisas.").</summary>
public static class SimpleOptimizeText
{
    public static string Summary(double freedBytes, int applied)
    {
        var freed = freedBytes >= 1024 * 1024; // menos de 1 MB não vale a frase
        var things = Math.Max(0, applied);
        return (freed, things) switch
        {
            (true, > 1) => Loc.F("Simple.Opt.DoneFreedN", ImproveCards.Bytes(freedBytes), things),
            (true, 1) => Loc.F("Simple.Opt.DoneFreed1", ImproveCards.Bytes(freedBytes)),
            (true, _) => Loc.F("Simple.Opt.DoneFreedOnly", ImproveCards.Bytes(freedBytes)),
            (false, > 1) => Loc.F("Simple.Opt.DoneN", things),
            (false, 1) => Loc.T("Simple.Opt.Done1"),
            _ => Loc.T("Simple.Opt.DoneNothing"),
        };
    }
}

// ================================================================== Ajustes rápidos (ligar/desligar em palavras simples)

/// <summary>Os grupos dos ajustes rápidos, na ordem da tela.</summary>
public enum QuickCategory { Speed, Games, Privacy, Power }

/// <summary>Classificação e textos dos ajustes rápidos (lógica pura, testada): grupo pelo id da ação, palavra de risco.</summary>
public static class QuickTweakText
{
    /// <summary>Grupo do ajuste pelo id da ação do catálogo (o id nunca aparece na tela).</summary>
    public static QuickCategory CategoryOf(string actionId)
    {
        var id = (actionId ?? "").ToLowerInvariant();
        if (id.StartsWith("privacy.", StringComparison.Ordinal)) return QuickCategory.Privacy;
        if (id.StartsWith("power.", StringComparison.Ordinal) || id.Contains("power", StringComparison.Ordinal) || id.Contains("energy", StringComparison.Ordinal) ||
            id.Contains("throttl", StringComparison.Ordinal) || id.Contains("aspm", StringComparison.Ordinal) || id.Contains(".epp", StringComparison.Ordinal) ||
            id.Contains("battery", StringComparison.Ordinal) || id.Contains("parking", StringComparison.Ordinal)) return QuickCategory.Power;
        if (id.StartsWith("fps.", StringComparison.Ordinal) || id.StartsWith("gpu", StringComparison.Ordinal) || id.StartsWith("nvidia.", StringComparison.Ordinal) ||
            id.StartsWith("cs2.", StringComparison.Ordinal) || id.StartsWith("fortnite.", StringComparison.Ordinal) || id.Contains("game", StringComparison.Ordinal) || id.Contains("mpo", StringComparison.Ordinal) ||
            id.Contains("mouse", StringComparison.Ordinal) || id.Contains("windowed", StringComparison.Ordinal) || id.Contains("directx", StringComparison.Ordinal)) return QuickCategory.Games;
        return QuickCategory.Speed;
    }

    public static string CategoryTitle(QuickCategory c) => c switch
    {
        QuickCategory.Speed => Loc.T("Simple.Quick.Speed"),
        QuickCategory.Games => Loc.T("Simple.Quick.Games"),
        QuickCategory.Privacy => Loc.T("Simple.Quick.Privacy"),
        _ => Loc.T("Simple.Quick.Power"),
    };

    public static string CategoryIcon(QuickCategory c) => c switch
    {
        QuickCategory.Speed => "Icon.Bolt",
        QuickCategory.Games => "Icon.Games",
        QuickCategory.Privacy => "Icon.Privacy",
        _ => "Icon.Cpu",
    };

    /// <summary>
    /// A palavra de risco dos ajustes de "Mostrar mais opções" (os recomendados não têm): energia gasta bateria; experimental pode
    /// não ajudar; risco médio/alto pode ter efeito colateral; o resto é "você decide".
    /// </summary>
    public static string RiskWord(QuickCategory category, Nexus.Automation.ActionRisk? risk, bool experimental)
    {
        if (experimental) return Loc.T("Simple.Quick.RiskExperimental");
        if (category == QuickCategory.Power) return Loc.T("Simple.Quick.RiskBattery");
        return risk switch
        {
            Nexus.Automation.ActionRisk.High => Loc.T("Simple.Quick.RiskHigh"),
            Nexus.Automation.ActionRisk.Medium => Loc.T("Simple.Quick.RiskMedium"),
            _ => Loc.T("Simple.Quick.RiskChoice"),
        };
    }

    /// <summary>A primeira frase de um texto (as explicações das regras têm várias); no máximo ~140 caracteres.</summary>
    public static string FirstSentence(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return "";
        var end = -1;
        for (var i = 0; i < t.Length - 1; i++)
            if (t[i] is '.' or '!' or '?' && t[i + 1] == ' ') { end = i; break; }
        var first = end >= 0 ? t[..(end + 1)] : t;
        return first.Length <= 140 ? first : first[..137].TrimEnd() + "…";
    }
}

/// <summary>Faixa de esforço de uma peça (uso em %): tranquila, normal, forte, intensa.</summary>
public enum LoadBand { Calm, Normal, Busy, Intense }

/// <summary>
/// Painel "Seu PC agora" do Início (lógica pura, testada em SimpleModeTests): as leituras ao vivo em frases de quem só joga
/// ("Seu processador está trabalhando intensamente", não "CPU 87%"). Nunca inventa um número: sem leitura, diz que não há.
/// </summary>
public static class TelemetryText
{
    /// <summary>GPU a partir desta temperatura reduz o desempenho para se proteger (GeForce: limite típico de 87–93 °C).</summary>
    public const double GpuHotC = 87;

    public static LoadBand Band(double percent) =>
        !double.IsFinite(percent) || percent < 30 ? LoadBand.Calm : percent < 70 ? LoadBand.Normal : percent < 90 ? LoadBand.Busy : LoadBand.Intense;

    public static string Cpu(double percent) => Band(percent) switch
    {
        LoadBand.Calm => Loc.T("Live.Cpu.Calm"),
        LoadBand.Normal => Loc.T("Live.Cpu.Normal"),
        LoadBand.Busy => Loc.T("Live.Cpu.Busy"),
        _ => Loc.T("Live.Cpu.Intense"),
    };

    public static string Gpu(double percent, double? temperatureC)
    {
        if (temperatureC is { } t && double.IsFinite(t) && t >= GpuHotC) return Loc.T("Live.Gpu.Hot");
        if (!double.IsFinite(percent)) return Loc.T("Live.Gpu.NoData");
        return Band(percent) switch
        {
            LoadBand.Calm => Loc.T("Live.Gpu.Idle"),
            LoadBand.Normal => Loc.T("Live.Gpu.Normal"),
            _ => Loc.T("Live.Gpu.Busy"),
        };
    }

    /// <summary>Memória em uso (fração 0–1): frase e se merece atenção (perto do limite).</summary>
    public static (string Text, bool Warn) Ram(double usedFraction)
    {
        if (!double.IsFinite(usedFraction)) return (Loc.T("Live.NoData"), false);
        if (usedFraction >= 0.85) return (Loc.T("Live.Ram.Near"), true);
        return usedFraction >= 0.6 ? (Loc.T("Live.Ram.Normal"), false) : (Loc.T("Live.Ram.Plenty"), false);
    }

    /// <summary>Disco do Windows: pouco espaço = menos de 10 % ou menos de 15 GB livres.</summary>
    public static (string Text, bool Warn) Disk(double freeBytes, double totalBytes)
    {
        if (!double.IsFinite(freeBytes) || !(totalBytes > 0)) return (Loc.T("Live.NoData"), false);
        var low = freeBytes / totalBytes < 0.10 || freeBytes < 15d * 1024 * 1024 * 1024;
        return low ? (Loc.T("Live.Disk.Low"), true) : (Loc.T("Live.Disk.Plenty"), false);
    }

    /// <summary>"3,3 GHz" no idioma da interface; sem leitura, vazio.</summary>
    public static string Ghz(double? mhz) =>
        mhz is { } m && double.IsFinite(m) && m > 0 ? (m / 1000).ToString("0.0", Loc.Culture) + " GHz" : "";

    /// <summary>"47 °C"; sem leitura, vazio.</summary>
    public static string Celsius(double? c) =>
        c is { } v && double.IsFinite(v) && v > 0 ? Math.Round(v).ToString("0", Loc.Culture) + " °C" : "";

    /// <summary>Gigabytes com uma casa até 100 ("6,1"), sem casa acima ("134").</summary>
    public static string Gigabytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return "—";
        var gb = bytes / (1024d * 1024 * 1024);
        return gb.ToString(gb >= 100 ? "0" : "0.0", Loc.Culture);
    }
}

/// <summary>Um item da faixa "Situação" do Início: rótulo, valor curto, uma frase e a página que explica. Nível: 0 neutro, 1 bom, 2 atenção.</summary>
public sealed record StatusItem(string Label, string Value, string Detail, int Level, string Page)
{
    public bool IsGood => Level == 1;
    public bool IsWarn => Level == 2;
}

/// <summary>
/// Faixa "Situação" do Início (lógica pura, testada): drivers de vídeo, o que pede atenção, espaço a liberar e memória. Só o que
/// já foi lido; sem leitura, diz "—" e a frase de espera (nunca um número inventado).
/// </summary>
public static class StatusStrip
{
    public static IReadOnlyList<StatusItem> Build(HealthInputs i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var list = new List<StatusItem>(4);

        list.Add(i.DriverAgeYears switch
        {
            null => new StatusItem(Loc.T("Live.Strip.Drivers"), "—", Loc.T("Live.Strip.DriversNoData"), 0, "DriverUpdatesPage"),
            >= 1 when i.GpuDriverDate is { } d => new StatusItem(Loc.T("Live.Strip.Drivers"), Loc.F("Live.Strip.DriverFrom", d.Year), Loc.T("Live.Strip.DriverOld"), 2, "DriverUpdatesPage"),
            _ => new StatusItem(Loc.T("Live.Strip.Drivers"), Loc.T("Live.Strip.DriverOk"), Loc.T("Live.Strip.DriverOkDetail"), 1, "DriverUpdatesPage"),
        });

        var attention = i.AttentionCount;
        list.Add(attention == 0
            ? new StatusItem(Loc.T("Live.Strip.Attention"), Loc.T("Live.Strip.AttentionNone"), Loc.T("Live.Strip.AttentionNoneDetail"), 1, "SimpleProblemsPage")
            : new StatusItem(Loc.T("Live.Strip.Attention"), attention == 1 ? Loc.F("Live.Strip.AttentionOne", attention) : Loc.F("Live.Strip.AttentionMany", attention),
                Loc.T("Live.Strip.AttentionDetail"), 2, "SimpleProblemsPage"));

        list.Add(double.IsFinite(i.RecoverableBytes)
            ? new StatusItem(Loc.T("Live.Strip.Space"), Nexus.Desktop.Views.Controls.DesignMath.Bytes(i.RecoverableBytes), Loc.T("Live.Strip.SpaceDetail"),
                i.RecoverableBytes >= 1024d * 1024 * 1024 ? 2 : 0, "SimpleCleanPage")
            : new StatusItem(Loc.T("Live.Strip.Space"), "—", Loc.T("Live.Strip.SpaceMeasuring"), 0, "SimpleCleanPage"));

        list.Add(i.MemoryModules switch
        {
            0 => new StatusItem(Loc.T("Live.Strip.Memory"), "—", Loc.T("Live.Strip.MemoryNoData"), 0, "MemoryLabPage"),
            1 => new StatusItem(Loc.T("Live.Strip.Memory"), Loc.T("Live.Strip.MemoryOne"), Loc.T("Live.Strip.MemoryOneDetail"), 2, "MemoryLabPage"),
            var n => new StatusItem(Loc.T("Live.Strip.Memory"), Loc.F("Live.Strip.MemoryMany", n), Loc.T("Live.Strip.MemoryManyDetail"), 1, "MemoryLabPage"),
        });
        return list;
    }
}
