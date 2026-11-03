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

    /// <summary>O que aplicar de uma etapa, sem duplicatas, na ordem proposta.</summary>
    public static IReadOnlyList<ProposedChange> Select(IEnumerable<ProposedChange> changes) => Select(changes, null);

    /// <summary>O que aplicar de uma etapa neste PC (perfil de hardware), sem duplicatas, na ordem proposta.</summary>
    public static IReadOnlyList<ProposedChange> Select(IEnumerable<ProposedChange> changes, HardwareProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return ChangeSelection.Sanitize(changes.Where(c => IsAutomatic(c, profile)));
    }
}
