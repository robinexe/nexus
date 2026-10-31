using System.Globalization;

namespace Nexus.Lab.Cpu;

public enum FindingSeverity { Good, Info, Warn, Bad }

/// <summary>
/// Um achado: o que foi visto (evidência lida ou medida), de onde vem a explicação (fonte), e o que fazer. Se houver ação no
/// catálogo, <see cref="ActionId"/> aponta para ela (aplicada pelo aplicador, com diário e desfazer); senão, <see cref="Instruction"/>.
/// </summary>
public sealed record CpuFinding(string Id, FindingSeverity Severity, string Title, string Evidence, string Explanation, string Source,
    string? ActionId = null, string? ActionValue = null, bool ActionNeedsAdmin = false, string? ActionLabel = null, string? Instruction = null);

/// <summary>Fatos que o diagnóstico considera. Tudo opcional: o que não foi lido não vira achado.</summary>
public sealed record CpuDiagnosisInput
{
    public CpuPowerState Power { get; init; } = new();
    public CpuIdentity? Identity { get; init; }
    public CpuTopology? Topology { get; init; }
    /// <summary>AutoGameModeEnabled do registro; null = valor ausente (o padrão do Windows é ligado) ou não lido.</summary>
    public bool? GameModeEnabled { get; init; }
    public bool? GameModeRead { get; init; }
    public bool? VbsConfigured { get; init; }
    public bool? HvciConfigured { get; init; }
    public CpuLabRun? Quick { get; init; }
    public CpuLabRun? Sustained { get; init; }
}

/// <summary>Regras do diagnóstico "o que está segurando o processador". Puras: testadas com fatos falsos.</summary>
public static class CpuDiagnosis
{
    public const string MaxStateActionId = "cpulab.power.max-state.ac@1";
    public const string BoostModeActionId = "cpulab.power.boost-mode.ac@1";
    public const string BoostModeValue = "2";
    public const string GameModeActionId = "win.gamemode.enable@1";
    public const string CoreParkingActionId = "power.processor.min-cores-parking@1";

    private const string MaxStateDoc = "Microsoft Learn: Maximum processor state (PROCTHROTTLEMAX) — https://learn.microsoft.com/windows-hardware/customize/power-settings/options-for-perf-state-engine-maxperformance";
    private const string BoostDoc = "Microsoft Learn: Processor performance boost mode (PERFBOOSTMODE) — https://learn.microsoft.com/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfboostmode";
    private const string PowerModeDoc = "Microsoft Support: Alterar o modo de energia do PC Windows — https://support.microsoft.com/topic/c2aff038-22c9-f46d-5ca0-78696fdf2de8";
    private const string PowerStatusDoc = "Microsoft Learn: SYSTEM_POWER_STATUS — https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-system_power_status";
    private const string GamingDoc = "Microsoft Support: \"Options to optimize gaming performance in Windows 11\" (out/2022: \"there may be a performance impact with Memory Integrity and VMP on\") — https://support.microsoft.com/windows/options-to-optimize-gaming-performance-in-windows-11-a255f612-2949-4373-a566-ff6f3f474613";

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<CpuFinding> Evaluate(CpuDiagnosisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var p = input.Power;
        var list = new List<CpuFinding>();
        var best = input.Quick?.BestCore?.Ghz ?? input.Sustained?.BestCore?.Ghz;
        var baseMhz = BaseMhz(input);

        // 1) Estado máximo do processador
        if (p.MaxStateAc is { } maxAc && maxAc < 100)
            list.Add(new("max-state-ac", FindingSeverity.Bad, $"Processador limitado a {maxAc}% na tomada",
                $"Plano de energia ativo: estado máximo do processador (PROCTHROTTLEMAX) = {maxAc}% com o carregador ligado." + Measured(best, baseMhz),
                "Abaixo de 100%, o Windows não deixa o processador passar dessa fração da frequência base — e o turbo fica desligado na prática. É comum depois de \"dicas para esfriar o notebook\" ou de programas de economia.",
                MaxStateDoc, MaxStateActionId, null, true, "Voltar para 100% (na tomada)"));
        if (p.MaxStateDc is { } maxDc && maxDc < 100)
            list.Add(new("max-state-dc", FindingSeverity.Info, $"Na bateria, o processador fica em {maxDc}%",
                $"Estado máximo do processador na bateria (DC) = {maxDc}%.",
                "Isso só vale fora da tomada e economiza bateria. Para jogar, use o carregador.",
                MaxStateDoc, Instruction: "Se quiser mudar: Painel de Controle > Opções de Energia > Alterar configurações avançadas > Gerenciamento de energia do processador > Estado máximo > Na bateria."));

        // 2) Modo de aumento de desempenho (turbo)
        if (p.BoostModeAc == 0)
            list.Add(new("boost-off-ac", FindingSeverity.Bad, "Turbo desligado no plano de energia",
                "Modo de aumento de desempenho do processador (PERFBOOSTMODE) = Desativado com o carregador ligado." + Measured(best, baseMhz),
                "Sem o modo de aumento, o processador fica preso na frequência base e nunca usa o turbo. Para jogos isso costuma custar de 15% a 30% de frequência. Às vezes é feito de propósito para baixar temperatura.",
                BoostDoc, BoostModeActionId, BoostModeValue, true, "Ligar o turbo (Agressivo, o padrão do Windows na tomada)"));
        if (p.BoostModeDc == 0)
            list.Add(new("boost-off-dc", FindingSeverity.Info, "Turbo desligado na bateria",
                "PERFBOOSTMODE = Desativado na bateria (DC).", "Só vale fora da tomada; economiza bateria e esquenta menos.", BoostDoc));

        // 3) Turbo não aparece na medição, sem causa óbvia no plano
        if (best is { } b && baseMhz is { } bm && bm > 0 && b * 1000 <= bm * 1.03 && p.BoostModeAc != 0 && (p.MaxStateAc ?? 100) >= 100)
            list.Add(new("no-turbo-measured", FindingSeverity.Warn, "O turbo não apareceu na medição",
                string.Format(Pt, "Núcleo mais rápido medido: {0:0.00} GHz; base anunciada: {1:0.00} GHz.", b, bm / 1000.0),
                "O plano de energia permite turbo, mas ele não apareceu. Causas comuns: notebook na bateria, modo \"melhor eficiência de energia\", perfil silencioso do fabricante (ex.: NitroSense / Armoury Crate) ou temperatura alta já no início.",
                BoostDoc, Instruction: "Ligue o carregador, escolha o perfil de desempenho do fabricante e meça de novo."));

        // 4) Bateria / economia de energia / modo de energia
        if (p.OnBattery == true)
            list.Add(new("on-battery", FindingSeverity.Warn, "Notebook na bateria",
                "GetSystemPowerStatus: sem carregador agora.",
                "Na bateria, o Windows e o fabricante limitam a potência do processador. Medições feitas assim não representam o desempenho na tomada.",
                PowerStatusDoc,
                Instruction: "Ligue o carregador antes de medir ou jogar."));
        if (p.BatterySaver == true)
            list.Add(new("battery-saver", FindingSeverity.Warn, "Economia de energia ligada",
                "GetSystemPowerStatus: SystemStatusFlag = 1 (economia de energia ativa).",
                "A economia de energia reduz a atividade em segundo plano e pode limitar o processador.",
                PowerStatusDoc,
                Instruction: "Configurações > Sistema > Energia e bateria > Economia de energia: desligar."));
        if (p.PowerModeOverlay is { } overlay)
        {
            if (overlay == ProcessorPowerSettings.OverlayBestEfficiency)
                list.Add(new("power-mode-efficiency", FindingSeverity.Warn, "Modo de energia: melhor eficiência",
                    "PowerGetEffectiveOverlayScheme: \"Melhor eficiência de energia\".",
                    "Esse modo pede ao processador para segurar a frequência e a potência. Bom para bateria; ruim para jogos.",
                    PowerModeDoc,
                    Instruction: "Configurações > Sistema > Energia e bateria > Modo de energia: Equilibrado ou Melhor desempenho."));
            else if (overlay == ProcessorPowerSettings.OverlayBestPerformance)
                list.Add(new("power-mode-performance", FindingSeverity.Good, "Modo de energia: melhor desempenho",
                    "PowerGetEffectiveOverlayScheme: \"Melhor desempenho\".", "O Windows está pedindo o máximo do processador.",
                    PowerModeDoc));
        }

        // 5) Limite de frequência imposto agora
        if (p.NtMhzLimit is { } lim && p.NtMaxMhz is { } nmax && nmax > 0 && lim < nmax * 0.95)
            list.Add(new("mhz-limit", FindingSeverity.Warn, "O Windows está limitando a frequência agora",
                $"CallNtPowerInformation: limite atual {lim} MHz, abaixo da base de {nmax} MHz.",
                "O limite cai quando o firmware avisa temperatura alta ou potência no teto, ou quando o plano/modo de energia pede economia.",
                "Microsoft Learn: PROCESSOR_POWER_INFORMATION (MhzLimit) — https://learn.microsoft.com/windows/win32/power/processor-power-information-str",
                Instruction: "Ligue o carregador, deixe as saídas de ar livres e meça de novo."));

        // 6) Estacionamento de núcleos
        if (p.CoreParkingMinAc is { } park && park < 100)
            list.Add(new("core-parking", FindingSeverity.Info, $"Estacionamento de núcleos ativo (mínimo {park}%)",
                $"CPMINCORES (mínimo de núcleos não estacionados, na tomada) = {park}%.",
                "O Windows desliga núcleos ociosos e os acorda em microssegundos quando há trabalho. Na maioria dos PCs isso não segura jogos; em alguns casos aparece como pequenos travamentos. Só mude se medir diferença.",
                "Catálogo NEXUS power.processor.min-cores-parking (CPMINCORES; efeito depende do escalonador e do driver de chipset)",
                CoreParkingActionId, null, true, "Testar sem estacionamento (experimental: medir antes e depois)"));

        // 7) Queda sob carga sustentada
        var throttle = input.Sustained?.Throttle;
        if (throttle is { Throttles: true } t)
            list.Add(new("sustained-drop", t.DropPercent >= 15 ? FindingSeverity.Bad : FindingSeverity.Warn,
                string.Format(Pt, "Perde {0:0}% de frequência sob carga contínua", t.DropPercent),
                t.Text + (input.Sustained?.OnBattery == true ? " (medido na bateria)" : ""),
                "Quando todos os núcleos ficam ocupados por mais de alguns segundos, o processador cai do turbo curto para o limite de potência contínuo ou esquenta até o limite de temperatura. Em notebook, poeira no dissipador e saídas de ar bloqueadas pioram muito. Não há garantia de ganho: depende do projeto de refrigeração.",
                "AMD/Intel: limites de potência (PPT/PL1/PL2) e temperatura; medição deste PC",
                Instruction: "Use o notebook numa superfície dura ou numa base com ventoinha, limpe as saídas de ar (com ar comprimido e o notebook desligado) e use o perfil de desempenho do fabricante. Meça de novo para comparar."));
        else if (throttle is { Throttles: false })
            list.Add(new("sustained-ok", FindingSeverity.Good, "Frequência estável sob carga contínua", throttle.Text,
                "Nesta duração, o processador não bateu em limite de temperatura ou potência.", "Medição deste PC"));

        // 8) Virtualização / integridade de memória
        if (p.HvciRunning == true || (p.HvciRunning is null && input.HvciConfigured == true))
            list.Add(new("hvci", FindingSeverity.Info,
                p.HvciRunning == true ? "Integridade de memória (HVCI) ligada" : "Integridade de memória (HVCI) configurada para ligar",
                p.HvciRunning == true ? "NtQuerySystemInformation(SystemCodeIntegrityInformation): HVCI ativo no kernel." : "Registro DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity\\Enabled = 1.",
                "A própria Microsoft diz que a integridade de memória e a plataforma de máquina virtual podem reduzir o desempenho em jogos, e sugere desligá-las durante o jogo. Em troca, elas bloqueiam drivers maliciosos: é proteção real. O impacto varia por jogo e por processador (os mais novos sofrem menos).",
                GamingDoc,
                Instruction: "Se decidir testar: Segurança do Windows > Segurança do dispositivo > Isolamento de núcleo > Integridade de memória (exige reiniciar). Meça antes e depois e religue se não houver diferença."));
        else if (input.Identity?.HypervisorPresent == true || input.VbsConfigured == true)
            list.Add(new("hypervisor", FindingSeverity.Info, "Windows rodando sobre o hipervisor",
                input.Identity?.HypervisorPresent == true ? "CPUID folha 1, ECX bit 31: hipervisor presente." : "Registro DeviceGuard\\EnableVirtualizationBasedSecurity = 1.",
                "Hyper-V, WSL2, a segurança baseada em virtualização (VBS) ou a plataforma de máquina virtual estão ativos. O custo costuma ser pequeno sem a integridade de memória, mas existe em alguns jogos.",
                GamingDoc));

        // 9) Modo de Jogo
        if (input.GameModeRead == true)
        {
            if (input.GameModeEnabled == false)
                list.Add(new("game-mode-off", FindingSeverity.Info, "Modo de Jogo desligado",
                    "HKCU\\Software\\Microsoft\\GameBar\\AutoGameModeEnabled = 0.",
                    "O Modo de Jogo dá prioridade ao jogo em primeiro plano e segura atualizações durante a partida. O ganho costuma ser pequeno, mas o risco também.",
                    GamingDoc, GameModeActionId, null, false, "Ligar o Modo de Jogo"));
            else
                list.Add(new("game-mode-on", FindingSeverity.Good, "Modo de Jogo ligado",
                    input.GameModeEnabled == true ? "AutoGameModeEnabled = 1." : "AutoGameModeEnabled ausente (o padrão do Windows é ligado).",
                    "O jogo em primeiro plano tem prioridade.", GamingDoc));
        }

        // 10) Núcleo preferido e dispersão (informativo, não é defeito)
        if (input.Quick is { } q && q.BestCore is { } bc && q.SpreadPercent is { } spread && spread >= 3)
            list.Add(new("preferred-core", FindingSeverity.Info,
                string.Format(Pt, "Núcleo preferido: {0} ({1:0.00} GHz)", bc.Core + 1, bc.Ghz),
                string.Format(Pt, "Entre os núcleos de desempenho, o mais lento ficou {0:0.#}% abaixo do mais rápido.", spread),
                "Os núcleos de um mesmo chip não são idênticos: o fabricante marca os melhores como \"preferidos\" (CPPC) e o Windows manda as tarefas de uma thread só para eles. Isso é normal.",
                "AMD: CPPC2 preferred cores; Intel: Turbo Boost Max 3.0"));

        if (!list.Any(f => f.Severity is FindingSeverity.Bad or FindingSeverity.Warn) && (best is not null || p.MaxStateAc is not null))
            list.Insert(0, new("nothing-holding", FindingSeverity.Good, "Nada segurando o processador nas leituras",
                Summary(input), "Plano de energia, bateria e medições não mostram limite artificial.", "Leituras e medições deste PC"));

        return list.OrderByDescending(f => f.Severity).ToList();
    }

    private static int? BaseMhz(CpuDiagnosisInput input)
    {
        if (input.Identity?.BaseMhz is > 0 and var id) return id;
        return input.Power.RegistryMhz ?? input.Power.NtMaxMhz;
    }

    private static string Measured(double? best, int? baseMhz) =>
        best is { } b ? string.Format(Pt, " Medido: {0:0.00} GHz no núcleo mais rápido{1}.", b, baseMhz is { } bm ? string.Format(Pt, " (base {0:0.00} GHz)", bm / 1000.0) : "") : "";

    private static string Summary(CpuDiagnosisInput input)
    {
        var parts = new List<string>();
        if (input.Power.MaxStateAc is { } m) parts.Add($"estado máximo {m}%");
        if (input.Power.BoostModeAc is { } bm) parts.Add($"turbo {ProcessorPowerSettings.BoostModeName(bm).ToLowerInvariant()}");
        if (input.Quick?.BestCore is { } bc) parts.Add(string.Format(Pt, "núcleo mais rápido {0:0.00} GHz", bc.Ghz));
        return parts.Count == 0 ? "Sem limites encontrados." : char.ToUpperInvariant(string.Join(", ", parts)[0]) + string.Join(", ", parts)[1..] + ".";
    }
}
