namespace Nexus.Measurement;

/// <summary>Por que o PresentMon parou sem entregar quadros.</summary>
public enum PresentMonFailure
{
    None,
    /// <summary>Sem permissão de rastreamento: "failed to start trace session: access denied" (código 6), conta fora do grupo
    /// Usuários do log de desempenho e sem administrador.</summary>
    AccessDenied,
    /// <summary>O usuário recusou (ou não viu) o aviso do Windows: "failed to elevate privilege: cancelled".</summary>
    ElevationCancelled,
    /// <summary>Não deu para pedir elevação por outro motivo ("failed to elevate privilege: …").</summary>
    ElevationFailed,
    /// <summary>Opção recusada ("unrecognized option '--x'") ou linha de comando inválida (código 1 + ajuda impressa).</summary>
    BadArguments,
    /// <summary>Sessão de rastreamento com o mesmo nome já existe e não pôde ser parada.</summary>
    SessionConflict,
    /// <summary>O processo filtrado fechou antes de desenhar (--terminate_on_proc_exit): era o lançador.</summary>
    TargetExited,
    /// <summary>O PresentMon não pôde nem ser iniciado (arquivo sumiu, bloqueado pelo antivírus…).</summary>
    NotStarted,
    /// <summary>Outro erro, com o texto no log.</summary>
    Other,
}

/// <summary>
/// Lê o código de saída e o texto de erro do PresentMon 2.6 e diz o que aconteceu. Textos conferidos no código-fonte oficial
/// (PresentMon/MainThread.cpp, Privilege.cpp e CommandLine.cpp, tag v2.6.0): "error: failed to start trace session: access
/// denied." + "PresentMon requires either administrative privileges or to be run by a user in the "Performance Log Users"
/// user group" (código 6); "error: failed to elevate privilege: cancelled." (código 2, o aviso do Windows foi recusado);
/// "error: unrecognized option '…'." (código 1, seguido da ajuda); "error: a trace session named … is already running" (6).
/// O aviso "warning: PresentMon requires elevated privilege in order to query processes…" NÃO é erro (só avisa que processos
/// de outra conta aparecem como "&lt;unknown&gt;").
/// </summary>
public static class PresentMonDiagnosis
{
    public static PresentMonFailure Classify(int? exitCode, string? log, bool framesSeen = false)
    {
        var text = log ?? "";
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("failed to elevate privilege"))
            return Has("cancelled") || Has("canceled") ? PresentMonFailure.ElevationCancelled : PresentMonFailure.ElevationFailed;
        if (Has("Performance Log Users") || (Has("failed to start trace session") && Has("access denied")) || Has("Access is denied") ||
            Has("requires either administrative privileges"))
            return PresentMonFailure.AccessDenied;
        if (Has("unrecognized option") || Has("expecting argument") || Has("only one of the following options")) return PresentMonFailure.BadArguments;
        if (Has("is already running") && Has("trace session")) return PresentMonFailure.SessionConflict;
        if (Has("Não foi possível iniciar o PresentMon")) return PresentMonFailure.NotStarted;
        if (exitCode is null) return PresentMonFailure.None; // ainda rodando
        if (exitCode == 0) return framesSeen ? PresentMonFailure.None : PresentMonFailure.TargetExited;
        return exitCode == 1 ? PresentMonFailure.BadArguments : PresentMonFailure.Other;
    }

    /// <summary>A opção indicada aparece num erro de opção recusada ("unrecognized option '--track_frame_type'").</summary>
    public static bool RejectedOption(string? log, string option) =>
        log is not null && log.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase) &&
        log.Contains(option, StringComparison.OrdinalIgnoreCase);

    /// <summary>Falta de permissão (o usuário precisa liberar a medição ou medir como administrador).</summary>
    public static bool IsPermission(PresentMonFailure f) =>
        f is PresentMonFailure.AccessDenied or PresentMonFailure.ElevationCancelled or PresentMonFailure.ElevationFailed;
}
