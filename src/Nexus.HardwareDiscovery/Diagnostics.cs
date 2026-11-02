using System.Globalization;

namespace Nexus.HardwareDiscovery;

public enum DiagnosticSeverity { Ok, Info, Attention }

/// <param name="Basis">Detected = conclusão direta de um valor lido; Inferred = depende de uma regra; Unavailable = não deu para avaliar.</param>
/// <param name="RelatedActionId">Ação do catálogo relacionada (só referência; nada é aplicado).</param>
/// <param name="Owner">Preenchido nos pontos que só o dono do PC resolve (BIOS, hardware): explicação, ganho medido e fonte.</param>
public sealed record DiagnosticFinding(string Id, string Title, DiagnosticSeverity Severity, FactStatus Basis, string Evidence, string? RelatedActionId = null,
    OwnerAdvice? Owner = null);

/// <summary>
/// Diagnóstico SOMENTE LEITURA sobre o retrato do sistema. Cada regra cita a evidência; nenhuma regra presume ganho de
/// desempenho. Regras que dependem de medição (frametime, stutter) ficam para a captura PresentMon (pendente).
/// </summary>
public static class DiagnosticRules
{
    public static IReadOnlyList<DiagnosticFinding> Evaluate(SystemFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var list = new List<DiagnosticFinding>();
        var sig = f.RegistrySignals;

        // Privilégio
        list.Add(f.IsElevated is null
            ? new("proc.elevated", "Elevação do aplicativo", DiagnosticSeverity.Info, FactStatus.Unavailable, "Não determinada.")
            : f.IsElevated.Value
            ? new("proc.elevated", "Aplicativo em execução elevada", DiagnosticSeverity.Info, FactStatus.Detected,
                "A interface não precisa de elevação; o modo somente leitura funciona sem ela.")
            : new("proc.not-elevated", "Aplicativo sem elevação", DiagnosticSeverity.Ok, FactStatus.Detected,
                "Execução normal de usuário. Ações que exigem administrador aparecem como bloqueadas na simulação."));

        // Máquina virtual
        if (f.IsVirtualMachine is { } vm)
            list.Add(new("env.vm", vm ? "Executando em máquina virtual" : "Executando em hardware físico", DiagnosticSeverity.Info, FactStatus.Inferred,
                $"SMBIOS: {f.SystemManufacturer ?? "?"} / {f.SystemModel ?? "?"}."));

        // Plano de energia
        if (f.ActivePowerScheme is { } scheme)
            list.Add(new("power.scheme", $"Plano de energia ativo: {f.ActivePowerSchemeName ?? scheme.ToString("D")}", DiagnosticSeverity.Info, FactStatus.Detected,
                f.CpuVendor == "AMD" && scheme == Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e")
                    ? "CPU AMD no plano Equilibrado, que é o recomendado pelo driver de chipset da AMD. Ganho com Alto desempenho não é presumido: exige teste A/B."
                    : "Mudança de plano só deve ser avaliada com medição A/B (laboratório pendente).",
                "power.active-scheme.high-performance.session"));
        else
            list.Add(new("power.scheme", "Plano de energia ativo", DiagnosticSeverity.Info, FactStatus.Unavailable, "Não foi possível ler o esquema ativo."));

        // Sinal de registro: (lido?, valor, motivo se não lido). "Lido e ausente" ≠ "não lido".
        (bool Read, string? Value, string? Why) Signal(string key) =>
            f.UnreadableSignals.TryGetValue(key, out var err) ? (false, null, err)
            : f.ReadSignals.Contains(key) ? (true, sig.GetValueOrDefault(key), null)
            : (false, null, "Sinal não lido pela descoberta.");

        // Modo de Jogo
        var gm = Signal(DiscoverySignals.GameMode);
        list.Add(!gm.Read
            ? new("win.gamemode", "Modo de Jogo", DiagnosticSeverity.Info, FactStatus.Unavailable, gm.Why!, "win.gamemode.enable")
            : gm.Value == "0"
                ? new("win.gamemode", "Modo de Jogo desativado", DiagnosticSeverity.Attention, FactStatus.Detected, "AutoGameModeEnabled = 0 (explícito).", "win.gamemode.enable")
            : gm.Value is null
                ? new("win.gamemode", "Modo de Jogo ativado (padrão)", DiagnosticSeverity.Ok, FactStatus.Inferred, "Valor ausente; o padrão do Windows é ativado.", "win.gamemode.enable")
                : new("win.gamemode", "Modo de Jogo ativado", DiagnosticSeverity.Ok, FactStatus.Detected, $"AutoGameModeEnabled = {gm.Value}.", "win.gamemode.enable"));

        // Captura em segundo plano
        var cap = Signal(DiscoverySignals.AppCapture);
        list.Add(!cap.Read
            ? new("win.gamedvr.capture", "Captura em segundo plano da Game Bar", DiagnosticSeverity.Info, FactStatus.Unavailable, cap.Why!, "win.gamedvr.background-capture.disable")
            : cap.Value == "1"
                ? new("win.gamedvr.capture", "Captura em segundo plano da Game Bar ativada", DiagnosticSeverity.Attention, FactStatus.Detected,
                    "AppCaptureEnabled = 1. A captura contínua usa o encoder de vídeo; o impacto deve ser medido (VideoBusy no PresentMon).",
                    "win.gamedvr.background-capture.disable")
                : new("win.gamedvr.capture", "Captura em segundo plano da Game Bar", DiagnosticSeverity.Ok,
                    cap.Value is null ? FactStatus.Inferred : FactStatus.Detected,
                    cap.Value is null ? "Sem valor explícito de AppCaptureEnabled; comportamento efetivo depende da versão do Windows." : $"AppCaptureEnabled = {cap.Value}.",
                    "win.gamedvr.background-capture.disable"));

        // HAGS
        var hags = Signal(DiscoverySignals.Hags);
        list.Add(!hags.Read
            ? new("gpu.hags", "HAGS", DiagnosticSeverity.Info, FactStatus.Unavailable, hags.Why!, "gpu.hags.lab")
            : hags.Value is null
                ? new("gpu.hags", "HAGS no padrão do driver", DiagnosticSeverity.Info, FactStatus.Detected, "HwSchMode ausente (lido).", "gpu.hags.lab")
                : new("gpu.hags", hags.Value == "2" ? "HAGS configurado como ativado" : hags.Value == "1" ? "HAGS configurado como desativado" : $"HAGS: valor {hags.Value}",
                    DiagnosticSeverity.Info, FactStatus.Detected, "Configuração no registro. Efeito varia por driver; só com teste A/B e reinício.", "gpu.hags.lab"));

        // VBS / HVCI (somente informativo: fora do escopo)
        if (Signal(DiscoverySignals.HvciEnabled) is { Read: true, Value: "1" })
            list.Add(new("sec.hvci", "Integridade de memória (HVCI) configurada como ativada", DiagnosticSeverity.Info, FactStatus.Detected,
                "Recurso de segurança. O NEXUS não o altera (rejeitado no catálogo: myth.disable-security-features)."));

        // Memória
        if (f.MemoryModules.Count > 0)
        {
            var configured = f.MemoryModules.Select(m => m.ConfiguredSpeedMts).Where(v => v is not null).Distinct().ToList();
            var max = f.MemoryModules.Select(m => m.MaxSpeedMts).Where(v => v is not null).Distinct().ToList();
            var evidence = $"{f.MemoryModules.Count} módulo(s); anunciada {string.Join("/", max)} MT/s; configurada {string.Join("/", configured)} MT/s.";
            if (configured.Count == 1 && max.Count == 1 && configured[0] < max[0])
                list.Add(new("mem.speed", "Memória abaixo da velocidade anunciada pelo módulo", DiagnosticSeverity.Attention, FactStatus.Detected, evidence +
                    " Ajuste é feito no BIOS/UEFI (fora do escopo do NEXUS)."));
            else
                list.Add(new("mem.speed", "Velocidade da memória", DiagnosticSeverity.Info, FactStatus.Inferred, evidence +
                    " A velocidade anunciada costuma ser o perfil JEDEC; se o kit tem perfil XMP/EXPO maior, só o BIOS/UEFI mostra se está ativo."));
            if (f.MemoryModules.Count % 2 == 1 && f.MemoryModules.Count < 4)
                list.Add(new("mem.channels", "Número ímpar de módulos", DiagnosticSeverity.Attention, FactStatus.Inferred,
                    $"{f.MemoryModules.Count} módulo(s) instalado(s): em plataformas de dois canais, provavelmente operando em canal único em parte da capacidade."));
        }
        else
        {
            list.Add(new("mem.speed", "Velocidade da memória", DiagnosticSeverity.Info, FactStatus.Unavailable, "Módulos SMBIOS indisponíveis."));
        }

        // Espaço no disco do sistema
        var sys = f.Drives.FirstOrDefault(d => string.Equals(d.Name, f.SystemDrive, StringComparison.OrdinalIgnoreCase));
        if (sys is { TotalBytes: > 0, FreeBytes: { } free })
        {
            var pct = 100.0 * free / sys.TotalBytes!.Value;
            list.Add(new("disk.system-free", pct < 10 ? "Pouco espaço livre no disco do sistema" : "Espaço livre no disco do sistema",
                pct < 10 ? DiagnosticSeverity.Attention : DiagnosticSeverity.Ok, FactStatus.Detected,
                $"{sys.Name}: {Units.Bytes(free)} livres de {Units.Bytes(sys.TotalBytes)} ({pct.ToString("0", CultureInfo.InvariantCulture)}%)."));
        }
        else
        {
            list.Add(new("disk.system-free", "Espaço livre no disco do sistema", DiagnosticSeverity.Info, FactStatus.Unavailable, "Unidade do sistema não lida."));
        }

        // Tipo do disco do sistema (SSD/HDD pelo próprio disco)
        list.Add(SystemDiskFinding(f));

        // Inicialização
        if (f.StartupItems.Count > 0)
        {
            var enabled = f.StartupItems.Count(i => i.Enabled != false);
            list.Add(new("startup.count", $"{enabled} item(ns) de inicialização habilitado(s)", DiagnosticSeverity.Info, FactStatus.Inferred,
                "Desativar itens só faz sentido para residentes com atividade concorrente observada durante o jogo. A página Inicialização mostra o impacto de cada um e desliga com Desfazer."));
        }

        // SysMain
        var sysmain = f.Services.FirstOrDefault(s => s.Name.Equals("SysMain", StringComparison.OrdinalIgnoreCase));
        if (sysmain is not null)
            list.Add(new("svc.sysmain", $"SysMain: {sysmain.State} ({sysmain.StartType})", DiagnosticSeverity.Info, FactStatus.Detected,
                SysMainAdvice(f.SystemDiskMedia), "svc.sysmain.manual.lab"));

        return list;
    }

    /// <summary>
    /// Windows num HD é o gargalo de carregamento mais perceptível (abrir programas, carregar jogos); em SSD, nada a fazer.
    /// Não promete FPS: o disco quase não muda o FPS depois que o jogo carregou.
    /// </summary>
    public static DiagnosticFinding SystemDiskFinding(SystemFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var what = StorageClassifier.Describe(f.SystemDiskMedia, f.SystemDiskBus);
        return f.SystemDiskMedia switch
        {
            DiskMedia.Hdd => new("disk.system-media", "Windows instalado num HD (disco rígido)", DiagnosticSeverity.Attention, FactStatus.Detected,
                $"Disco do sistema: {what}. Um HD lê arquivos espalhados dezenas de vezes mais devagar que um SSD: é o que mais pesa para ligar o PC, " +
                "abrir programas e carregar jogos. Trocar por um SSD é a melhoria mais perceptível; nenhum ajuste de software compensa. O FPS depois de carregar quase não muda."),
            DiskMedia.Ssd or DiskMedia.Scm => new("disk.system-media", $"Windows num {what}", DiagnosticSeverity.Ok, FactStatus.Detected,
                "O disco do sistema não tem cabeça mecânica: carregamentos rápidos. Nada a fazer aqui (\"desfragmentar\" SSD não ajuda; o Windows já faz TRIM sozinho)."),
            _ => new("disk.system-media", "Tipo do disco do sistema", DiagnosticSeverity.Info, FactStatus.Unavailable,
                "O disco não informou se é SSD ou HD (sem penalidade de busca nem barramento NVMe na resposta)."),
        };
    }

    /// <summary>O SysMain (pré-carregamento) ajuda mais em HD; em SSD o efeito é pequeno nos dois sentidos.</summary>
    public static string SysMainAdvice(DiskMedia systemDisk) => systemDisk switch
    {
        DiskMedia.Hdd => "Windows num HD: o SysMain pré-carrega na memória o que você mais abre e costuma deixar os programas abrirem mais rápido. " +
                         "Não desligue sem medir; travadinhas por disco são hipótese de laboratório (exige I/O concorrente observado durante stutters).",
        DiskMedia.Ssd or DiskMedia.Scm => "Windows num SSD: o ganho do SysMain é pequeno, e desligá-lo também não aumenta FPS. " +
                         "Hipótese de laboratório apenas: exige I/O concorrente observado durante stutters.",
        _ => "Hipótese de laboratório apenas: exige I/O concorrente observado durante stutters.",
    };
}
