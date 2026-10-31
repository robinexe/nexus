using System.Globalization;

namespace Nexus.Lab.Memory.Problems;

/// <summary>Ids (id@versão) das ações de catálogo usadas pelo diagnóstico — deste módulo e reaproveitadas de outros.</summary>
public static class DoctorActions
{
    public const string StandbyPurge = "memorylab.standby.purge@1";
    public const string ProcessTrim = "memorylab.process.trim@1";
    public const string ProcessClose = "memorylab.process.close@1";
    public const string ProcessEnd = "memorylab.process.end@1";
    public const string ServiceRestart = "memorylab.service.restart@1";
    public const string CompressionOn = "memorylab.compression.enable@1";
    public const string PageFileAutomatic = "memorylab.pagefile.automatic@1";
    public const string EdgeStartupBoostOff = "memorylab.browser.edge-startupboost-off@1";
    public const string EdgeBackgroundOff = "memorylab.browser.edge-background-off@1";
    public const string EdgeSleepingTabsOn = "memorylab.browser.edge-sleepingtabs-on@1";
    public const string ChromeBackgroundOff = "memorylab.browser.chrome-background-off@1";
    /// <summary>Módulo Serviços (Nexus.Boot): tipo de início escolhido pelo usuário.</summary>
    public const string ServiceStartType = "boot.service.starttype@1";
}

public enum FixTerm { Temporary, Permanent }

public enum FixKind
{
    /// <summary>Ação de catálogo pelo aplicador (com confirmação).</summary>
    Apply,
    /// <summary>Desligar da inicialização: a página procura a entrada no inventário do módulo Inicialização.</summary>
    DisableStartup,
    /// <summary>Abrir outra página do NEXUS (ex.: Atualizar drivers, Memória RAM).</summary>
    Navigate,
    /// <summary>Só orientação (o que fazer à mão, com fonte).</summary>
    Info,
}

/// <summary>
/// Uma solução para um suspeito. Toda solução diz o que faz, quanto dura e o custo; temporárias dizem que o problema volta.
/// <see cref="ActionKey"/> + <see cref="Value"/> + <see cref="Arguments"/> formam o pedido ao aplicador.
/// </summary>
public sealed record FixOption
{
    public required string Id { get; init; }
    public required FixTerm Term { get; init; }
    public required FixKind Kind { get; init; }
    public required string Title { get; init; }
    public required string WhatItDoes { get; init; }
    public required string HowLong { get; init; }
    public required string Cost { get; init; }
    public string? ActionKey { get; init; }
    public string? Value { get; init; }
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }
    public bool Admin { get; init; }
    /// <summary>Navigate: "DriverUpdates", "MemoryLab", "Services", "Startup". DisableStartup: nome do executável.</summary>
    public string? Target { get; init; }
    /// <summary>Processos: nível de proteção (Sensitive = confirmação dupla na página).</summary>
    public ProcessGuard Guard { get; init; } = ProcessGuard.Normal;
    public string GuardReason { get; init; } = "";
    public bool Reversible { get; init; }
}

/// <summary>Um suspeito de "comer a memória", com a evidência medida.</summary>
public sealed record Suspect
{
    public required string Id { get; init; }
    /// <summary>"bad", "warn" ou "info".</summary>
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required string Evidence { get; init; }
    public required string Explanation { get; init; }
    public IReadOnlyList<FixOption> Temporary { get; init; } = [];
    public IReadOnlyList<FixOption> Permanent { get; init; } = [];
    public IReadOnlyList<MemorySource> Sources { get; init; } = [];
    public string? GroupKey { get; init; }
}

/// <summary>Contexto que não vem da amostragem (lido uma vez): configuração do Windows e da RAM.</summary>
public sealed record DoctorContext
{
    public bool? CompressionEnabled { get; init; }
    public PageFileMode PageFileMode { get; init; } = PageFileMode.Unknown;
    public long InstalledRamBytes { get; init; }
    public bool? SingleChannel { get; init; }
    public bool? FreeSlot { get; init; }
    public IReadOnlyDictionary<string, (string Owner, string Description)>? LocalPoolTags { get; init; }
    /// <summary>Tag → drivers que contêm o texto da tag (busca sob demanda, findstr da Microsoft).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TagDrivers { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>Histórico das leituras (só em memória, enquanto a página está aberta). Guarda até <see cref="Capacity"/> quadros.</summary>
public sealed class MemoryHistory
{
    private readonly List<(MemorySnapshot Snapshot, IReadOnlyList<AppGroup> Groups)> _frames = [];

    public MemoryHistory(int capacity = 480) => Capacity = Math.Max(2, capacity);

    public int Capacity { get; }
    public int Count => _frames.Count;
    public IReadOnlyList<MemorySnapshot> Snapshots => _frames.Select(f => f.Snapshot).ToList();
    public MemorySnapshot? Latest => _frames.Count == 0 ? null : _frames[^1].Snapshot;
    public IReadOnlyList<AppGroup> LatestGroups => _frames.Count == 0 ? [] : _frames[^1].Groups;

    public void Add(MemorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _frames.Add((snapshot, AppGrouping.Group(snapshot.Processes, snapshot.ServicesByPid)));
        if (_frames.Count > Capacity) _frames.RemoveRange(0, _frames.Count - Capacity);
    }

    public void Clear() => _frames.Clear();

    public IReadOnlyList<TrendSample> System(Func<SystemMemorySample, double?> pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        return _frames.Where(f => pick(f.Snapshot.System) is not null).Select(f => new TrendSample(f.Snapshot.At, pick(f.Snapshot.System)!.Value)).ToList();
    }

    /// <summary>Série de um app: só o trecho final contínuo em que ele aparece (se fechou e abriu de novo, recomeça).</summary>
    public IReadOnlyList<TrendSample> Group(string key, Func<AppGroup, double?> pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        var list = new List<TrendSample>();
        for (var i = _frames.Count - 1; i >= 0; i--)
        {
            var g = _frames[i].Groups.FirstOrDefault(x => x.Key == key);
            if (g is null) break;
            if (pick(g) is { } v) list.Add(new TrendSample(_frames[i].Snapshot.At, v));
        }
        list.Reverse();
        return list;
    }

    /// <summary>Série de uma tag do pool (bytes paginado + não paginado), quando as tags foram lidas.</summary>
    public IReadOnlyList<TrendSample> PoolTag(string tag) =>
        _frames.Where(f => f.Snapshot.PoolTags is not null)
            .Select(f => new TrendSample(f.Snapshot.At, f.Snapshot.PoolTags!.FirstOrDefault(t => t.Tag == tag)?.TotalBytes ?? 0))
            .ToList();
}

/// <summary>
/// Regras do diagnóstico "quem está comendo a memória". Puras: recebem o histórico e o contexto e devolvem suspeitos com
/// evidência e soluções (temporárias e permanentes). Nunca prometem desempenho; dizem o que muda e por quanto tempo.
/// </summary>
public static class MemoryDoctorEngine
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private const double GiB = 1024.0 * 1024 * 1024, MiB = 1024.0 * 1024;

    public static readonly MemorySource MsPools = new("Microsoft: pools de memória do kernel (paginado e não paginado)", "https://learn.microsoft.com/windows/win32/memory/memory-pools");
    public static readonly MemorySource MsPoolLeak = new("Microsoft (blog NTDebugging): achar o driver de um vazamento de pool com poolmon + findstr", KnownPoolTags.FindstrSource);
    public static readonly MemorySource MsGdi = new("Microsoft: objetos GDI (limite padrão de 10.000 por processo)", "https://learn.microsoft.com/windows/win32/sysinfo/gdi-objects");
    public static readonly MemorySource MsUser = new("Microsoft: objetos USER (limite padrão de 10.000 por processo)", "https://learn.microsoft.com/windows/win32/sysinfo/user-objects");
    public static readonly MemorySource MsEmptyWs = new("Microsoft: EmptyWorkingSet (tira páginas da RAM; voltam por falta de página)", "https://learn.microsoft.com/windows/win32/api/psapi/nf-psapi-emptyworkingset");
    public static readonly MemorySource MsPageFile = new("Microsoft: introdução ao arquivo de paginação (limite de commit)", "https://learn.microsoft.com/troubleshoot/windows-client/performance/introduction-to-the-page-file");
    public static readonly MemorySource MsRamMap = new("Microsoft Sysinternals: RAMMap (mostra Driver Locked, AWE e a lista de espera)", "https://learn.microsoft.com/sysinternals/downloads/rammap");
    public static readonly MemorySource MsWsl = new("Microsoft: configurar o limite de memória do WSL (.wslconfig, memory=)", "https://learn.microsoft.com/windows/wsl/wsl-config");
    public static readonly MemorySource MsMmAgent = new("Microsoft: Enable-MMAgent (compressão de memória)", "https://learn.microsoft.com/powershell/module/mmagent/enable-mmagent");
    public static readonly MemorySource EdgeStartupBoost = new("Microsoft Edge: política StartupBoostEnabled", "https://learn.microsoft.com/deployedge/microsoft-edge-browser-policies/startupboostenabled");
    public static readonly MemorySource EdgeBackground = new("Microsoft Edge: política BackgroundModeEnabled", "https://learn.microsoft.com/deployedge/microsoft-edge-browser-policies/backgroundmodeenabled");
    public static readonly MemorySource EdgeSleeping = new("Microsoft Edge: política SleepingTabsEnabled (guias suspensas)", "https://learn.microsoft.com/deployedge/microsoft-edge-browser-policies/sleepingtabsenabled");
    public static readonly MemorySource ChromeBackground = new("Google Chrome Enterprise: política BackgroundModeEnabled", "https://chromeenterprise.google/policies/#BackgroundModeEnabled");
    public static readonly MemorySource NduLeak = new("Windows OS Hub: pool não paginado enorme (Ndu.sys com drivers de rede Killer/Realtek) — comunidade", "https://woshub.com/huge-memory-usage-non-paged-pool-windows/");
    public static readonly MemorySource IslcCommunity = new("Comunidade: Intelligent Standby List Cleaner (Wagnardsoft) e os engasgos com a lista de espera cheia", "https://www.wagnardsoft.com/");

    public static IReadOnlyList<Suspect> Evaluate(MemoryHistory history, DoctorContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        context ??= new DoctorContext();
        var latest = history.Latest;
        if (latest is null) return [];
        var sys = latest.System;
        var list = new List<Suspect>();

        // 1. Apps: vazamento de memória privada, handles, GDI/USER, threads.
        foreach (var g in history.LatestGroups.Where(g => g.Kind != AppKind.System))
        {
            var mem = LeakDetector.Evaluate(history.Group(g.Key, x => x.PrivateBytes), LeakThresholds.PrivateBytes);
            if (mem.IsLeak)
            {
                list.Add(AppSuspect(g, latest, "leak",
                    mem.FittedGrowth >= 1 * GiB || sys.CommitRatio >= 0.85 ? "bad" : "warn",
                    $"{g.DisplayName} cresceu {Bytes(mem.FittedGrowth)} em {LeakDetector.Minutes(mem.Span)}",
                    string.Create(Pt, $"Memória privada: {Bytes(mem.Start)} → {Bytes(mem.Last)} (≈ {Bytes(mem.RobustSlopePerHour)}/hora). {mem.Reason}"),
                    "Um programa que só cresce e não devolve memória costuma ter um vazamento (bug). Fechar e abrir de novo zera, mas ele volta a crescer no mesmo ritmo."));
                continue;
            }
            var handles = LeakDetector.Evaluate(history.Group(g.Key, x => x.Handles), LeakThresholds.Handles);
            var gdi = LeakDetector.Evaluate(history.Group(g.Key, x => x.MaxGdi), LeakThresholds.Gdi);
            var user = LeakDetector.Evaluate(history.Group(g.Key, x => x.MaxUser), LeakThresholds.User);
            var threads = LeakDetector.Evaluate(history.Group(g.Key, x => x.Threads), LeakThresholds.Threads);
            var nearGdi = g.MaxGdi is >= 8000;
            var nearUser = g.MaxUser is >= 8000;
            if (handles.IsLeak || gdi.IsLeak || user.IsLeak || threads.IsLeak || nearGdi || nearUser)
            {
                var parts = new List<string>();
                if (handles.IsLeak) parts.Add(string.Create(Pt, $"handles {handles.Start:N0} → {handles.Last:N0}"));
                if (gdi.IsLeak || nearGdi) parts.Add(string.Create(Pt, $"objetos GDI {(gdi.IsLeak ? $"{gdi.Start:N0} → " : "")}{g.MaxGdi:N0} (limite 10.000)"));
                if (user.IsLeak || nearUser) parts.Add(string.Create(Pt, $"objetos USER {(user.IsLeak ? $"{user.Start:N0} → " : "")}{g.MaxUser:N0} (limite 10.000)"));
                if (threads.IsLeak) parts.Add(string.Create(Pt, $"threads {threads.Start:N0} → {threads.Last:N0}"));
                list.Add(AppSuspect(g, latest, "handles", nearGdi || nearUser ? "bad" : "warn",
                    $"{g.DisplayName}: recursos do Windows crescendo sem parar",
                    string.Join(" · ", parts) + (handles.IsLeak ? $" em {LeakDetector.Minutes(handles.Span)}" : ""),
                    nearGdi || nearUser
                        ? "Perto de 10.000 objetos o programa começa a falhar ao desenhar janelas (menus em branco, travamentos). Isso é vazamento de recursos de interface, não de RAM."
                        : "Handles e objetos de interface que só sobem indicam vazamento no programa. Consomem memória do kernel e, perto do limite, o programa falha."));
            }
        }

        // 2. Navegadores com muitos processos/memória.
        foreach (var b in history.LatestGroups.Where(g => g.Kind == AppKind.Browser))
        {
            if (list.Any(s => s.GroupKey == b.Key)) continue;
            var heavy = b.PrivateBytes >= 2.5 * GiB || (sys.Total > 0 && b.PrivateBytes >= sys.Total * 0.25);
            if (!heavy || b.ProcessCount < 8) continue;
            list.Add(BrowserSuspect(b, sys));
        }

        // 3. Máquina virtual / WSL.
        foreach (var vm in history.LatestGroups.Where(g => g.Kind == AppKind.VirtualMachine && g.WorkingSet >= 1.5 * GiB))
            list.Add(new Suspect
            {
                Id = "vm:" + vm.Key, GroupKey = vm.Key, Severity = sys.FreeRatio < 0.15 ? "warn" : "info",
                Title = $"{vm.DisplayName} está com {Bytes(vm.WorkingSet)}",
                Evidence = string.Create(Pt, $"Vmmem é a memória da máquina virtual inteira (WSL, Docker, Hyper-V, Android): {Bytes(vm.WorkingSet)} na RAM agora."),
                Explanation = "O WSL 2 reserva memória conforme o Linux usa e demora a devolver (o cache de arquivos do Linux conta aqui). Não é vazamento do Windows.",
                Temporary =
                [
                    Info("vm-shutdown", FixTerm.Temporary, "Desligar a VM quando não estiver usando",
                        "No Terminal: wsl --shutdown (WSL/Docker) ou feche o Docker Desktop / a VM. A memória volta na hora.",
                        "Até abrir o WSL/Docker de novo.", "Tudo que roda dentro da VM é encerrado."),
                ],
                Permanent =
                [
                    Info("vm-limit", FixTerm.Permanent, "Limitar a memória do WSL",
                        "Crie %UserProfile%\\.wslconfig com [wsl2] e memory=4GB (ou o que fizer sentido) e rode wsl --shutdown.",
                        "Permanente (até você editar o arquivo).", "Programas Linux pesados podem ficar mais lentos ou sem memória."),
                ],
                Sources = [MsWsl],
            });

        // 4. Memória do kernel (pool) crescendo: vazamento de driver.
        list.AddRange(PoolSuspects(history, context, sys));

        // 5. Commit perto do limite (o que realmente trava/fecha programas).
        if (sys.CommitLimit > 0 && sys.CommitRatio >= 0.80)
        {
            var top = history.LatestGroups.Where(g => g.Kind is not AppKind.System).Take(3).ToList();
            var perm = new List<FixOption>();
            if (context.PageFileMode is PageFileMode.None or PageFileMode.Custom)
                perm.Add(Apply("pagefile-auto", FixTerm.Permanent, "Arquivo de paginação automático", DoctorActions.PageFileAutomatic, null, null, admin: true,
                    "O Windows passa a aumentar o arquivo de paginação quando precisa, subindo o limite de commit.",
                    "Permanente; vale depois de reiniciar. Desfazer volta a configuração anterior.", "Alguns GB no disco do sistema.", reversible: true));
            perm.Add(Navigate("more-ram", FixTerm.Permanent, "Ver se cabe mais RAM", "MemoryLab",
                "Abre o Laboratório da memória: slots livres, single/dual channel e o que comprar.", "Permanente.", "Custo do módulo de memória."));
            list.Add(new Suspect
            {
                Id = "commit", Severity = sys.CommitRatio >= 0.90 ? "bad" : "warn",
                Title = string.Create(Pt, $"Memória comprometida em {sys.CommitRatio * 100:0}% do limite"),
                Evidence = string.Create(Pt, $"{Bytes(sys.CommitTotal)} de {Bytes(sys.CommitLimit)} (limite = RAM + arquivo de paginação). Maiores: {string.Join(", ", top.Select(t => $"{t.DisplayName} {Bytes(t.PrivateBytes)}"))}."),
                Explanation = "Quando o commit chega no limite, o Windows recusa memória: jogos e programas fecham sozinhos ou travam (\"memória insuficiente\"). Isso é diferente da RAM \"em uso\".",
                Temporary =
                [
                    Info("commit-close", FixTerm.Temporary, "Fechar o maior consumidor",
                        "Use os cartões dos suspeitos acima ou feche o programa que mais usa (lista \"Maiores consumidores\").",
                        "Até ele voltar a crescer.", "Perde o que não estiver salvo no programa fechado."),
                ],
                Permanent = perm,
                Sources = [MsPageFile],
            });
        }

        // 6. Lista livre zerada com cache enorme (o caso em que esvaziar a lista de espera pode ajudar alguns jogos).
        if (sys.Total > 0 && sys.FreeZero is { } free && sys.Standby is { } standby &&
            free < Math.Max(256 * MiB, sys.Total * 0.03) && standby > sys.Total * 0.20)
            list.Add(new Suspect
            {
                Id = "standby", Severity = "info",
                Title = "Memória livre quase zerada (cache cheio)",
                Evidence = string.Create(Pt, $"Livre: {Bytes(free)} · lista de espera (cache): {Bytes(standby)}."),
                Explanation = "Isto é NORMAL: o Windows usa a RAM sobrando como cache e devolve na hora. Só vale esvaziar se você sente engasgos em um jogo específico com pouca RAM (casos relatados pela comunidade); fora disso, piora: o cache precisa ser lido do disco de novo.",
                Temporary = [PurgeFix()],
                Permanent =
                [
                    Navigate("standby-ram", FixTerm.Permanent, "Mais RAM (a causa real)", "MemoryLab",
                        "Com mais memória o cache e o jogo cabem juntos e o engasgo some.", "Permanente.", "Custo do módulo."),
                ],
                Sources = [MsRamMap, IslcCommunity],
            });

        // 7. Compressão desligada.
        if (context.CompressionEnabled == false)
            list.Add(new Suspect
            {
                Id = "compression", Severity = "info",
                Title = "Compressão de memória desligada",
                Evidence = "O Windows informa MemoryCompression = False (Get-MMAgent). O padrão do Windows 10/11 é ligada.",
                Explanation = "Com a compressão, páginas pouco usadas são compactadas na RAM em vez de irem para o disco. Em PCs com pouca RAM, desligar faz o PC paginar mais.",
                Permanent =
                [
                    Apply("compression-on", FixTerm.Permanent, "Ligar a compressão de memória", DoctorActions.CompressionOn, null, null, admin: true,
                        "Roda Enable-MMAgent -MemoryCompression (padrão do Windows).",
                        "Permanente; vale depois de reiniciar. Desfazer desliga de novo.", "Um pouco de CPU para compactar (pequeno). Exige o serviço SysMain ativo.", reversible: true),
                ],
                Sources = [MsMmAgent],
            });

        // 8. Memória "em uso" que não aparece em nenhum processo.
        var explained = latest.Processes.Sum(p => Math.Min(p.WorkingSet, Math.Max(p.PrivateBytes, 0))) + sys.NonPagedPool + sys.PagedPool;
        var gap = sys.InUse - explained;
        if (sys.Total > 0 && latest.Processes.Count > 10 && gap > Math.Max(1.5 * GiB, sys.Total * 0.20))
            list.Add(new Suspect
            {
                Id = "unexplained", Severity = "info",
                Title = $"≈ {Bytes(gap)} em uso sem dono visível",
                Evidence = string.Create(Pt, $"Em uso: {Bytes(sys.InUse)}; processos (parte privada na RAM) + pool do kernel ≈ {Bytes(explained)}. Estimativa: a diferença costuma ser memória travada por driver (placa de vídeo, rede, VM), AWE ou cache do sistema."),
                Explanation = "O Gerenciador de Tarefas não mostra essa memória. O RAMMap (Microsoft Sysinternals) mostra quanto é \"Driver Locked\". Se for muito e crescer, o culpado é um driver: atualize vídeo, rede e chipset.",
                Permanent =
                [
                    Navigate("unexplained-drivers", FixTerm.Permanent, "Atualizar drivers", "DriverUpdates",
                        "Abre a página Atualizar drivers (Windows Update) — vídeo, rede e chipset primeiro.", "Permanente, se o driver novo corrigiu.", "Reinício em alguns casos."),
                ],
                Sources = [MsRamMap],
            });

        // 9. Memória de GPU compartilhada alta.
        if (sys.GpuShared is { } shared && sys.Total > 0 && (shared >= 2 * GiB || shared >= sys.Total * 0.25))
        {
            var byPid = latest.GpuSharedByPid.OrderByDescending(kv => kv.Value).Take(2)
                .Select(kv => (latest.Processes.FirstOrDefault(p => p.Pid == kv.Key)?.Name ?? $"PID {kv.Key}", kv.Value)).ToList();
            list.Add(new Suspect
            {
                Id = "gpushared", Severity = "info",
                Title = $"A placa de vídeo está usando {Bytes(shared)} da RAM",
                Evidence = "Memória de GPU compartilhada (contador \\GPU Adapter Memory\\Shared Usage)" +
                           (byPid.Count > 0 ? ": " + string.Join(", ", byPid.Select(b => $"{b.Item1} {Bytes(b.Value)}")) : "") + ".",
                Explanation = "A placa integrada usa a RAM como memória de vídeo; numa placa dedicada, isso aparece quando o jogo passa da VRAM (texturas altas demais). Não é vazamento.",
                Permanent =
                [
                    Info("gpu-textures", FixTerm.Permanent, "Baixar a qualidade de texturas do jogo",
                        "Texturas um nível abaixo cabem na VRAM e liberam RAM.", "Enquanto mantiver a configuração.", "Visual um pouco pior de perto."),
                ],
            });
        }

        // 10. Pouca RAM para o uso medido.
        var peak = Math.Max(sys.CommitPeak, history.System(s => s.CommitTotal).Select(t => (long)t.Value).DefaultIfEmpty().Max());
        var ram = context.InstalledRamBytes > 0 ? context.InstalledRamBytes : sys.Total;
        if (ram > 0 && peak >= ram * 0.90)
        {
            var target = new[] { 16L, 32, 64, 128 }.Select(g => g * (long)GiB).FirstOrDefault(g => g >= peak * 1.25 && g > ram);
            var advice = context.SingleChannel == true && context.FreeSlot == true
                ? "Você tem um slot livre e está em single channel: um segundo módulo igual dobra a RAM E a banda."
                : "Veja no Laboratório da memória quantos slots livres existem.";
            list.Add(new Suspect
            {
                Id = "ram", Severity = peak >= ram ? "warn" : "info",
                Title = target > 0 ? $"Seu uso pede {Bytes(target)} de RAM" : "Seu uso está no limite da RAM instalada",
                Evidence = string.Create(Pt, $"Pico de memória comprometida: {Bytes(peak)} com {Bytes(ram)} instalados ({peak * 100.0 / ram:0}%). Acima de 100%, o excedente foi para o disco."),
                Explanation = advice,
                Permanent =
                [
                    Navigate("ram-lab", FixTerm.Permanent, "Abrir o Laboratório da memória", "MemoryLab",
                        "Mostra slots, canais e o módulo compatível para comprar.", "Permanente.", "Custo do módulo."),
                ],
            });
        }

        return list
            .OrderBy(s => s.Severity switch { "bad" => 0, "warn" => 1, _ => 2 })
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static Suspect AppSuspect(AppGroup g, MemorySnapshot latest, string kind, string severity, string title, string evidence, string explanation)
    {
        var temp = new List<FixOption>();
        var perm = new List<FixOption>();
        if (g.Kind is AppKind.ServiceHost or AppKind.Service)
        {
            foreach (var svc in g.Services.Take(3))
            {
                if (ServiceRestartHandler.CheckName(svc.Name) is { } blocked)
                {
                    temp.Add(Info("svc-protected-" + svc.Name, FixTerm.Temporary, $"{svc.DisplayName}: protegido", blocked, "—", "—"));
                    continue;
                }
                var shared = g.Services.Count > 1;
                temp.Add(Apply("svc-restart-" + svc.Name, FixTerm.Temporary, $"Reiniciar o serviço {svc.DisplayName}", DoctorActions.ServiceRestart, null,
                    new Dictionary<string, string> { ["service"] = svc.Name }, admin: true,
                    "Para e inicia o serviço de novo (o NEXUS garante que ele volta a rodar)." + (shared ? " Ele divide o processo com outros serviços: a memória só volta se for dele." : ""),
                    "Até o vazamento crescer de novo (horas ou dias).", "O recurso do serviço fica indisponível por alguns segundos."));
                perm.Add(Apply("svc-manual-" + svc.Name, FixTerm.Permanent, $"Pôr {svc.DisplayName} em Manual", DoctorActions.ServiceStartType, "Manual",
                    new Dictionary<string, string> { ["service"] = svc.Name }, admin: true,
                    "O serviço deixa de iniciar com o Windows; inicia só quando algo pedir.",
                    "Permanente; desfazer volta o tipo de início anterior.", "O recurso pode demorar mais na primeira vez que for usado.", reversible: true));
            }
        }
        else
        {
            var main = g.Processes.OrderByDescending(p => p.PrivateBytes).First();
            var guard = ProcessProtection.Check(main.Name, main.Pid, main.SessionId, main.Path);
            if (guard.Level != ProcessGuard.Forbidden && main.StartTimeUtcTicks is { } started)
            {
                var args = new Dictionary<string, string>
                {
                    ["pid"] = main.Pid.ToString(CultureInfo.InvariantCulture),
                    ["name"] = AppGrouping.NormalizeName(main.Name),
                    ["started"] = started.ToString(CultureInfo.InvariantCulture),
                };
                temp.Add(Apply("close", FixTerm.Temporary, $"Fechar {g.DisplayName}", DoctorActions.ProcessClose, null, args, admin: false,
                    "Pede para o programa fechar, como clicar no X (ele pode perguntar se quer salvar). Abra de novo em seguida.",
                    "Até ele crescer de novo — num vazamento, no mesmo ritmo.", "Perde o que não estiver salvo.") with { Guard = guard.Level, GuardReason = guard.Reason });
                temp.Add(Apply("trim", FixTerm.Temporary, "Aparar a memória (só se estiver em segundo plano)", DoctorActions.ProcessTrim, null, args, admin: false,
                    "Tira da RAM as páginas do processo (EmptyWorkingSet). A memória continua reservada para ele: o commit não muda.",
                    "Minutos: o que ele usar volta para a RAM.", "Costuma PIORAR se o programa estiver em uso (ele relê tudo do disco). Só serve para app parado que reservou e não usa.") with { Guard = guard.Level, GuardReason = guard.Reason });
            }
            else if (guard.Level == ProcessGuard.Forbidden)
                temp.Add(Info("forbidden", FixTerm.Temporary, "Protegido", guard.Reason, "—", "—"));
            if (g.Kind == AppKind.App)
                perm.Add(new FixOption
                {
                    Id = "startup", Term = FixTerm.Permanent, Kind = FixKind.DisableStartup, Target = AppGrouping.NormalizeName(main.Name),
                    Title = "Não abrir com o Windows",
                    WhatItDoes = "Desliga o item de inicialização (mesmo método do Gerenciador de Tarefas): ele só roda quando você abrir.",
                    HowLong = "Permanente; desfazer liga de novo.", Cost = "Recursos do app em segundo plano (notificações, sincronização) só com ele aberto.",
                    Reversible = true,
                });
            perm.Add(Info("update-app", FixTerm.Permanent, "Atualizar o programa",
                "Vazamentos são bugs: veja se há versão nova (página Programas > atualizar, ou no próprio app).", "Permanente se a versão nova corrigiu.", "Nenhum."));
        }
        return new Suspect
        {
            Id = $"{kind}:{g.Key}", GroupKey = g.Key, Severity = severity, Title = title, Evidence = evidence, Explanation = explanation,
            Temporary = temp, Permanent = perm, Sources = kind == "handles" ? [MsGdi, MsUser] : [MsEmptyWs],
        };
    }

    private static Suspect BrowserSuspect(AppGroup b, SystemMemorySample sys)
    {
        var edge = b.Key == "app:msedge";
        var chrome = b.Key == "app:chrome";
        var perm = new List<FixOption>();
        if (edge)
        {
            perm.Add(Policy("edge-sleeping", "Edge: guias suspensas sempre ligadas", DoctorActions.EdgeSleepingTabsOn,
                "Política SleepingTabsEnabled = 1: abas paradas são suspensas e liberam memória."));
            perm.Add(Policy("edge-boost", "Edge: desligar a Inicialização rápida", DoctorActions.EdgeStartupBoostOff,
                "Política StartupBoostEnabled = 0: o Edge para de deixar processos abertos depois de fechado (para abrir mais rápido)."));
            perm.Add(Policy("edge-bg", "Edge: não continuar em segundo plano", DoctorActions.EdgeBackgroundOff,
                "Política BackgroundModeEnabled = 0: extensões e apps do Edge não ficam rodando com o navegador fechado."));
        }
        if (chrome)
            perm.Add(Policy("chrome-bg", "Chrome: não continuar em segundo plano", DoctorActions.ChromeBackgroundOff,
                "Política BackgroundModeEnabled = 0: o Chrome não fica rodando depois de fechado."));
        return new Suspect
        {
            Id = "browser:" + b.Key, GroupKey = b.Key, Severity = sys.FreeRatio < 0.15 ? "warn" : "info",
            Title = $"{b.DisplayName}: {Bytes(b.PrivateBytes)} em {b.ProcessCount} processos",
            Evidence = string.Create(Pt, $"Somando todos os processos do navegador (um por aba, extensão e GPU): {Bytes(b.PrivateBytes)} comprometidos, {Bytes(b.WorkingSet)} na RAM."),
            Explanation = "Navegador com muitas abas é o maior consumidor na maioria dos PCs. Não é defeito: cada aba é um processo isolado por segurança.",
            Temporary =
            [
                Info("tabs", FixTerm.Temporary, "Fechar abas que não está usando",
                    "Ou, no Edge: Configurações > Sistema e desempenho > Guias suspensas; no Chrome: Configurações > Desempenho > Economia de memória.",
                    "Até abrir mais abas.", "Nenhum."),
            ],
            Permanent = perm,
            Sources = edge ? [EdgeSleeping, EdgeStartupBoost, EdgeBackground] : chrome ? [ChromeBackground] : [],
        };
    }

    private static FixOption Policy(string id, string title, string key, string what) =>
        Apply(id, FixTerm.Permanent, title, key, null, null, admin: true, what,
            "Permanente; desfazer devolve o valor anterior.",
            "Vira política: o navegador mostra \"gerenciado pela sua organização\" e a opção fica travada até desfazer.", reversible: true);

    private static IEnumerable<Suspect> PoolSuspects(MemoryHistory history, DoctorContext context, SystemMemorySample sys)
    {
        var np = LeakDetector.Evaluate(history.System(s => s.NonPagedPool), LeakThresholds.NonPagedPool);
        var pp = LeakDetector.Evaluate(history.System(s => s.PagedPool), LeakThresholds.PagedPool);
        var highNp = sys.NonPagedPool >= 1 * GiB;
        var highPp = sys.PagedPool >= 3 * GiB;
        if (!np.IsLeak && !pp.IsLeak && !highNp && !highPp) yield break;

        var tags = GrowingTags(history, context).Take(3).ToList();
        var which = np.IsLeak || highNp ? "não paginado" : "paginado";
        var evidence = np.IsLeak
            ? $"Pool não paginado: {Bytes(np.Start)} → {Bytes(np.Last)} em {LeakDetector.Minutes(np.Span)} (≈ {Bytes(np.SlopePerHour)}/hora)."
            : pp.IsLeak ? $"Pool paginado: {Bytes(pp.Start)} → {Bytes(pp.Last)} em {LeakDetector.Minutes(pp.Span)} (≈ {Bytes(pp.SlopePerHour)}/hora)."
            : string.Create(Pt, $"Pool não paginado {Bytes(sys.NonPagedPool)}, paginado {Bytes(sys.PagedPool)} (comum: até ~500 MB e ~1,5 GB).");
        if (tags.Count > 0) evidence += " Tags que mais cresceram: " + string.Join("; ", tags.Select(t => t.Text)) + ".";
        else if (history.Latest?.PoolTags is null) evidence += " Para ver qual driver, abra o NEXUS como administrador (as tags do pool exigem).";
        var title = tags.FirstOrDefault(t => t.Owner is not null) is { } first
            ? $"Memória do kernel crescendo — provável {first.Owner}"
            : $"Memória do kernel (pool {which}) crescendo — provável vazamento de driver";
        yield return new Suspect
        {
            Id = "pool", Severity = np.IsLeak || pp.IsLeak ? "bad" : "warn", Title = title, Evidence = evidence,
            Explanation = "O pool é memória do próprio Windows e dos drivers; o não paginado nunca sai da RAM. Se ele só cresce, um driver não está devolvendo memória. Casos comuns: drivers de rede (Killer, Realtek, Intel) com o monitor de uso de dados (Ndu.sys), placa de vídeo, antivírus e VPN.",
            Temporary =
            [
                Info("pool-reboot", FixTerm.Temporary, "Reiniciar o PC",
                    "É o único jeito de devolver a memória do kernel: nenhum programa consegue liberar o pool de um driver.",
                    "Até o driver vazar de novo (horas ou dias).", "Fechar tudo e esperar o reinício."),
            ],
            Permanent =
            [
                Navigate("pool-drivers", FixTerm.Permanent, "Atualizar o driver suspeito", "DriverUpdates",
                    "Abre Atualizar drivers. Se o suspeito é de rede ou vídeo e não houver versão no Windows Update, baixe no site do fabricante (Killer/Intel/Realtek/NVIDIA/AMD).",
                    "Permanente, se a versão nova corrigiu.", "Reinício."),
            ],
            Sources = [MsPools, MsPoolLeak, NduLeak],
        };
    }

    public sealed record TagGrowth(string Tag, double Growth, string? Owner, string Text);

    /// <summary>Tags do pool com tendência de alta (detector), com dono pela tabela, pooltag.txt local ou busca nos drivers.</summary>
    public static IEnumerable<TagGrowth> GrowingTags(MemoryHistory history, DoctorContext context)
    {
        var latest = history.Latest?.PoolTags;
        if (latest is null) yield break;
        foreach (var t in latest.OrderByDescending(t => t.TotalBytes).Take(60))
        {
            var v = LeakDetector.Evaluate(history.PoolTag(t.Tag), LeakThresholds.PoolTag);
            if (!v.IsLeak) continue;
            var owner = KnownPoolTags.Lookup(t.Tag, context.LocalPoolTags);
            context.TagDrivers.TryGetValue(t.Tag, out var drivers);
            var who = owner?.Owner ?? (drivers is { Count: > 0 } ? string.Join(" ou ", drivers.Take(3)) : null);
            yield return new TagGrowth(t.Tag, v.Growth, who,
                $"'{t.Tag}' +{Bytes(v.FittedGrowth)}" + (who is null ? " (driver ainda não identificado)" : $" ({who})"));
        }
    }

    private static FixOption PurgeFix() =>
        Apply("purge", FixTerm.Temporary, "Esvaziar a lista de espera", DoctorActions.StandbyPurge, null, null, admin: true,
            "Joga fora o cache de arquivos da RAM (o mesmo que \"Empty Standby List\" do RAMMap).",
            "Minutos: o cache se enche de novo conforme você usa o PC.",
            "Programas e jogos releem do disco o que estava em cache (carregamentos mais lentos logo depois). Exige administrador.");

    private static FixOption Apply(string id, FixTerm term, string title, string key, string? value, IReadOnlyDictionary<string, string>? args, bool admin,
        string what, string howLong, string cost, bool reversible = false) => new()
    {
        Id = id, Term = term, Kind = FixKind.Apply, Title = title, ActionKey = key, Value = value, Arguments = args, Admin = admin,
        WhatItDoes = what, HowLong = howLong, Cost = cost, Reversible = reversible,
    };

    private static FixOption Navigate(string id, FixTerm term, string title, string page, string what, string howLong, string cost) => new()
    {
        Id = id, Term = term, Kind = FixKind.Navigate, Title = title, Target = page, WhatItDoes = what, HowLong = howLong, Cost = cost,
    };

    private static FixOption Info(string id, FixTerm term, string title, string what, string howLong, string cost) => new()
    {
        Id = id, Term = term, Kind = FixKind.Info, Title = title, WhatItDoes = what, HowLong = howLong, Cost = cost,
    };

    public static string Bytes(double bytes)
    {
        var a = Math.Abs(bytes);
        return a >= GiB ? string.Create(Pt, $"{bytes / GiB:0.#} GB") : string.Create(Pt, $"{bytes / MiB:0} MB");
    }
}

// ============================================================================ modo "vigiar enquanto jogo"

public sealed record GameGuardSettings(bool Enabled = false, double FreeThreshold = 0.05, int CooldownMinutes = 10)
{
    public TimeSpan Cooldown => TimeSpan.FromMinutes(Math.Clamp(CooldownMinutes, 5, 120));
}

public sealed record GameGuardDecision(bool Suggest, string Reason);

/// <summary>
/// Decide quando o modo "vigiar enquanto jogo" deve sugerir esvaziar a lista de espera: ligado pelo usuário, jogo aberto,
/// memória LIVRE (lista livre+zerada, não a "disponível") abaixo do limite, cache grande (≥ 1 GB, senão não há o que esvaziar)
/// e fora do intervalo mínimo desde a última vez. Nada é feito sem o usuário confirmar (ver docs/modules/memorydoctor.md).
/// </summary>
public static class GameGuard
{
    public static GameGuardDecision Decide(GameGuardSettings settings, SystemMemorySample memory, string? runningGame, DateTimeOffset now, DateTimeOffset? last)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(memory);
        if (!settings.Enabled) return new(false, "Modo desligado.");
        if (runningGame is null) return new(false, "Nenhum jogo aberto.");
        if (memory.Total <= 0 || memory.FreeZero is not { } free || memory.Standby is not { } standby) return new(false, "Contadores de memória indisponíveis.");
        var ratio = (double)free / memory.Total;
        if (ratio >= settings.FreeThreshold) return new(false, string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"Memória livre em {ratio * 100:0.0}% (limite {settings.FreeThreshold * 100:0}%)."));
        if (standby < 1024L * 1024 * 1024) return new(false, "Cache pequeno: esvaziar não liberaria quase nada.");
        if (last is { } l && now - l < settings.Cooldown) return new(false, "Esvaziado há pouco tempo: aguardando o intervalo mínimo.");
        return new(true, string.Create(CultureInfo.GetCultureInfo("pt-BR"),
            $"{runningGame} aberto e só {MemoryDoctorEngine.Bytes(free)} livres ({ratio * 100:0.0}%) com {MemoryDoctorEngine.Bytes(standby)} de cache."));
    }
}
