namespace Nexus.Assistant;

/// <summary>
/// Modo automático do Assistente ("Otimizar meu PC com segurança", modo simples): quais etapas rodam sozinhas, em que grupo
/// cada uma aparece para o usuário e o que é aplicado sem perguntar item por item (o usuário confirmou tudo uma vez, no começo).
/// Só entra o que já viria marcado no passo a passo (risco baixo, documentado, não opcional, não experimental), que pode ser
/// desfeito (ou o ponto de restauração, que é a rede de segurança) e que não instala nada. Lógica pura, testada.
/// </summary>
public static class AutoPilot
{
    /// <summary>Etapas que rodam no automático, na ordem do assistente. Programas, Medição e Agenda ficam para o passo a passo.</summary>
    public static IReadOnlyList<AssistantStepId> Steps { get; } =
    [
        AssistantStepId.Welcome, AssistantStepId.MyPc, AssistantStepId.Cleaning, AssistantStepId.Startup, AssistantStepId.Privacy,
        AssistantStepId.Security, AssistantStepId.Drivers, AssistantStepId.Memory, AssistantStepId.Performance,
    ];

    /// <summary>Os 4 itens que a tela mostra ("Limpar arquivos temporários", "Ajustar o Windows para jogos", …).</summary>
    public enum Phase { Clean = 0, Tune = 1, Startup = 2, Check = 3 }

    public static Phase PhaseOf(AssistantStepId step) => step switch
    {
        AssistantStepId.Cleaning => Phase.Clean,
        AssistantStepId.Startup => Phase.Startup,
        AssistantStepId.Welcome or AssistantStepId.Security or AssistantStepId.Drivers => Phase.Check,
        _ => Phase.Tune,
    };

    /// <summary>
    /// Ações que nunca entram no automático, mesmo de risco baixo: instalar driver (baixa e instala; fica para o usuário decidir) e
    /// trocar o modo do monitor (a tela pode apagar; no passo a passo há o "Manter esta taxa?" que volta sozinho).
    /// </summary>
    public static IReadOnlySet<string> NeverAutomatic { get; } = new HashSet<string>(StringComparer.Ordinal)
        { StepPlanners.DriverInstallAction, Nexus.Lab.Gpu.GpuDiagnostics.DisplayModeAction };

    /// <summary>Entra no automático? Só risco baixo recomendado (marcado por padrão), desfazível ou o ponto de restauração, nunca irreversível.</summary>
    public static bool IsAutomatic(ProposedChange c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (!ChangeSelection.IsSelectedByDefault(c)) return false;            // inclui: permitido, não experimental, não opcional, risco baixo
        if (c.Irreversible || c.Request.ConfirmIrreversible == true) return false;
        if (NeverAutomatic.Contains(c.Request.ActionId)) return false;
        return c.Undoable || c.Request.ActionId == StepPlanners.RestorePointAction;
    }

    /// <summary>
    /// Entra no automático neste PC? <see cref="IsAutomatic(ProposedChange)"/> e, com o perfil de hardware, nunca o que
    /// <see cref="HardwareRules"/> marca como "não recomendado para este PC" (ex.: desempenho máximo em notebook na bateria).
    /// </summary>
    public static bool IsAutomatic(ProposedChange c, HardwareProfile? profile)
    {
        if (!IsAutomatic(c)) return false;
        return profile is null || !HardwareRules.ForAction(profile, c.Request.ActionId).IsNotRecommended;
    }

    // ------------------------------------------------------------------ Desempenho máximo (o "Otimizar meu PC")

    /// <summary>
    /// Nunca entram no "Desempenho máximo", mesmo podendo desfazer: limitam o FPS (diretriz: o NEXUS nunca limita), são só
    /// correção de defeito ou preferência pessoal, quebram recursos que o jogador usa (Game Pass/Xbox, impressora), deixam o
    /// jogo travando (apagar cache de shader obriga a recompilar) ou não têm evidência (prioridade do agendador).
    /// </summary>
    public static IReadOnlySet<string> MaxExcludedIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "gpu.nv.fps-cap.per-app", "gpu.nv.vsync.force-on.per-app",           // põem limite
        "fps.dwm.mpo.disable", "fps.dwm.overlay-minfps.zero",                // correções de defeito, não ganho
        "net.ipv6.prefer-ipv4", "sys.notifications.toasts.off",              // preferência, não ganho
        "memorylab.standby.purge",                                           // momentâneo, não desfaz
        "cpulab.sched.prefer-performant.ac", "cpulab.sched-short.prefer-performant.ac", // laboratório: só com medição (pode piorar)
        // Game Pass, login Xbox e a detecção de jogo dos Ryzen X3D dependem destes; impressora é recurso que a pessoa usa.
        "boot.service.xblauthmanager.disabled", "boot.service.xblgamesave.disabled", "boot.service.xboxnetapisvc.disabled",
        "boot.service.spooler.disabled", "boot.service.fax.disabled",
        // Apagar cache de shader obriga o jogo a recompilar: travadas na próxima partida.
        "cleaning.shader-nvidia", "cleaning.shader-amd", "cleaning.shader-intel", "cleaning.shader-directx", "cleaning.steam-shadercache",
    };

    /// <summary>Prefixos fora do "Desempenho máximo" (mesmos motivos de <see cref="MaxExcludedIds"/>).</summary>
    public static IReadOnlyList<string> MaxExcludedPrefixes { get; } =
    [
        "fps.mouse.",                         // muda a sensação da mira: escolha da pessoa
        "sched.",                             // prioridade do agendador: sem evidência de ganho (mito no catálogo)
        "security.", "scanner.",              // proteção do Windows: nunca mexer por desempenho
        "power.active-scheme.",               // plano "de sessão" (segura o aplicador); a tela de sessão cuida
    ];

    /// <summary>
    /// Proteção do Windows: nada que contenha estes termos entra no automático por desempenho (regra do produto, HANDOFF §2:
    /// nunca desligar proteção por suposto ganho). Nenhuma ação do catálogo bate hoje; é a segunda tranca, com teste.
    /// </summary>
    public static IReadOnlyList<string> SecurityTerms { get; } =
    [
        "defender", "firewall", "uac", "smartscreen", "vbs", "hvci", "memory-integrity", "core-isolation", "mitigation",
        "spectre", "meltdown", "windowsupdate", "windows-update", "wuauserv", "update.pause", "smart-app-control", "antivirus",
    ];

    /// <summary>Vetos do hardware que valem mesmo no máximo: fabricante errado, coisa que PIORA neste PC. Calor/bateria/ruído não.</summary>
    private static readonly HashSet<FitReason> MaxKeptVetoes =
    [
        FitReason.NotNvidia, FitReason.NotAmd, FitReason.NotIntel, FitReason.IntegratedGpu, FitReason.SysMainHelpsHere,
        FitReason.WeakGpuRelief, FitReason.X3dParking, // modo desempenho do Fortnite entra até em placa forte: é o que dá mais FPS
    ];

    /// <summary>A ação toca em proteção do Windows (id com um dos <see cref="SecurityTerms"/>)?</summary>
    public static bool TouchesSecurity(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        var id = actionId.ToLowerInvariant();
        return SecurityTerms.Any(t => id.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>
    /// "Desempenho máximo": entra tudo o que pode ser desfeito e dá desempenho, inclusive avançado, opcional, experimental e de
    /// risco médio, em qualquer PC (notebook, na bateria, desktop, gamer). Ficam de fora: irreversível, momentâneo (menos o
    /// ponto de restauração), mito (rejeitado/obsoleto/inútil/incompatível), <see cref="NeverAutomatic"/>, a lista de exclusão,
    /// proteção do Windows, limite de FPS e os vetos de hardware de <see cref="MaxKeptVetoes"/>.
    /// </summary>
    public static bool IsMaxPerformance(ProposedChange c, HardwareProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.Request.ActionId;
        if (id == StepPlanners.RestorePointAction) return true;
        if (!ChangeSelection.IsAllowed(c) || !c.Undoable || NeverAutomatic.Contains(id)) return false;
        if (c.Classification is Nexus.Core.Classification.Rejected or Nexus.Core.Classification.Obsolete
            or Nexus.Core.Classification.Incompatible or Nexus.Core.Classification.Ineffective) return false;
        var bare = id.Split('@')[0];
        if (MaxExcludedIds.Contains(bare) || MaxExcludedPrefixes.Any(p => bare.StartsWith(p, StringComparison.Ordinal)) || TouchesSecurity(bare)) return false;
        if (PutsFpsCap(bare, c.Request.Value)) return false;
        if (profile is not null && HardwareRules.ForAction(profile, id) is { IsNotRecommended: true } fit && MaxKeptVetoes.Contains(fit.Reason)) return false;
        return true;
    }

    /// <summary>Por que um item ficou fora do Desempenho máximo (texto do relatório).</summary>
    public static string MaxExclusionReason(ProposedChange c, HardwareProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(c);
        var bare = c.Request.ActionId.Split('@')[0];
        if (!ChangeSelection.IsAllowed(c)) return "Não tem volta: o NEXUS nunca faz sozinho.";
        if (!c.Undoable) return "Momentâneo: não fica registrado para desfazer, fica na página dele.";
        if (NeverAutomatic.Contains(c.Request.ActionId)) return "Pede a sua decisão (instalar driver, trocar o modo do monitor).";
        if (TouchesSecurity(bare) || bare.StartsWith("security.", StringComparison.Ordinal) || bare.StartsWith("scanner.", StringComparison.Ordinal))
            return "Proteção do Windows: o NEXUS nunca mexe por desempenho.";
        if (c.Classification is Nexus.Core.Classification.Rejected or Nexus.Core.Classification.Obsolete
            or Nexus.Core.Classification.Incompatible or Nexus.Core.Classification.Ineffective)
            return "Mito ou sem efeito nos testes: não entra.";
        if (PutsFpsCap(bare, c.Request.Value) || bare is "gpu.nv.fps-cap.per-app" or "gpu.nv.vsync.force-on.per-app")
            return "Limitaria o FPS: o NEXUS nunca limita.";
        if (profile is not null && HardwareRules.ForAction(profile, c.Request.ActionId) is { IsNotRecommended: true } fit && MaxKeptVetoes.Contains(fit.Reason))
            return fit.Reason switch
            {
                FitReason.NotNvidia or FitReason.NotAmd or FitReason.NotIntel => "Não serve para a placa de vídeo deste PC.",
                FitReason.X3dParking => "Neste Ryzen X3D, diminuiria o FPS.",
                _ => "Neste PC, pioraria o desempenho.",
            };
        return "Correção de defeito, preferência pessoal ou recurso que você usa (Xbox, impressora, mira): fica na página dele.";
    }

    /// <summary>Pedido que limitaria o FPS (Fortnite/CS2 com valor diferente de "sem limite").</summary>
    public static bool PutsFpsCap(string bareId, string? value) => bareId switch
    {
        "fortnite.fps.limit" => value is not null && value != "0.000000",
        "cs2.cfg.fps_max" => value is not null && value != "0",
        _ => false,
    };

    /// <summary>O que o "Desempenho máximo" aplica de uma etapa, sem duplicatas, na ordem proposta.</summary>
    public static IReadOnlyList<ProposedChange> SelectMaxPerformance(IEnumerable<ProposedChange> changes, HardwareProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return ChangeSelection.Sanitize(changes.Where(c => IsMaxPerformance(c, profile)));
    }

    /// <summary>O que aplicar de uma etapa, sem duplicatas, na ordem proposta.</summary>
    public static IReadOnlyList<ProposedChange> Select(IEnumerable<ProposedChange> changes) => Select(changes, null);

    /// <summary>O que aplicar de uma etapa neste PC (perfil de hardware), sem duplicatas, na ordem proposta.</summary>
    public static IReadOnlyList<ProposedChange> Select(IEnumerable<ProposedChange> changes, HardwareProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return ChangeSelection.Sanitize(changes.Where(c => IsAutomatic(c, profile)));
    }
}
