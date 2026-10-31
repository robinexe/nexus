namespace Nexus.Lab.Memory.Problems;

public enum ProcessGuard
{
    /// <summary>Pode ser aparado/fechado com uma confirmação.</summary>
    Normal,
    /// <summary>Pode, mas com confirmação dupla (jogo aberto, Explorer, launcher de jogo com download em andamento...).</summary>
    Sensitive,
    /// <summary>Nunca: núcleo do Windows, segurança, anticheat, serviços (use "reiniciar serviço"), o próprio NEXUS.</summary>
    Forbidden,
}

public sealed record ProcessGuardVerdict(ProcessGuard Level, string Reason);

/// <summary>
/// Regras de proteção para aparar/fechar processos, aplicadas na página (para não oferecer o botão) E no handler do aplicador
/// (para recusar mesmo que o pedido chegue). Lista fechada e conservadora: na dúvida, proíbe.
/// </summary>
public static class ProcessProtection
{
    // Núcleo do Windows: fechar derruba a sessão ou o PC (tela azul CRITICAL_PROCESS_DIED), ou o Windows reabre na hora.
    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "secure system", "memory compression", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "lsaiso", "svchost", "fontdrvhost", "dwm", "sihost", "ctfmon", "conhost", "taskhostw", "runtimebroker",
        "startmenuexperiencehost", "shellexperiencehost", "searchhost", "textinputhost", "lockapp", "logonui", "userinit",
        "spoolsv", "audiodg", "wudfhost", "dllhost", "consent", "smartscreen", "securityhealthsystray", "securityhealthservice",
        "msmpeng", "nissrv", "mpdefendercoreservice", "mssense", "sgrmbroker", "wlanext", "dashost", "trustedinstaller",
        "tiworker", "msiexec", "wuauclt", "usocoreworker", "mousocoreworker", "vmcompute", "vmms",
    };

    // Anticheat e proteção de jogos: mexer pode banir a conta ou derrubar o jogo. Prefixos.
    private static readonly string[] AntiCheatPrefixes =
    [
        "vgc", "vgtray", "vgk", "easyanticheat", "eac_", "beservice", "bedaisy", "battleye", "faceit", "esea", "pnkbstr",
        "mhyprot", "ace-", "acebase", "anticheatexpert", "xigncode", "nprotect", "gameguard", "ricochet", "eaanticheat",
        "javelin",
    ];

    // Pastas típicas de jogos instalados por lojas/launchers (caminho em minúsculas, com \).
    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\", @"\epic games\", @"\xboxgames\", @"\windowsapps\", @"\riot games\", @"\gog galaxy\games\",
        @"\gog games\", @"\ubisoft game launcher\games\", @"\ea games\", @"\battle.net\", @"\rockstar games\", @"\games\",
    ];

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "steam", "epicgameslauncher", "battle.net", "eadesktop", "upc", "galaxyclient", "riotclientservices",
        "obs64", "obs32",
    };

    /// <summary>O executável está numa pasta típica de jogos de loja/launcher?</summary>
    public static bool LooksLikeGame(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return GameFolders.Any(f => p.Contains(f, StringComparison.Ordinal)) && !p.Contains(@"\windowsapps\microsoft.", StringComparison.Ordinal);
    }

    public static ProcessGuardVerdict Check(string name, int pid, int sessionId, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = AppGrouping.NormalizeName(name);
        if (pid is 0 or 4) return new(ProcessGuard.Forbidden, "Processo do núcleo do Windows.");
        if (n.Length == 0) return new(ProcessGuard.Forbidden, "Processo sem nome: não identificado com segurança.");
        if (n.StartsWith("nexus", StringComparison.OrdinalIgnoreCase)) return new(ProcessGuard.Forbidden, "É o próprio NEXUS.");
        if (Core.Contains(n)) return new(ProcessGuard.Forbidden, n.Equals("svchost", StringComparison.OrdinalIgnoreCase)
            ? "svchost hospeda serviços do Windows: use \"reiniciar o serviço\", que sabe quais serviços estão nele."
            : "Processo do Windows: fechar pode travar a sessão ou o PC.");
        var lower = n.ToLowerInvariant();
        if (AntiCheatPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
            return new(ProcessGuard.Forbidden, "Anticheat/proteção de jogo: mexer nele pode derrubar o jogo ou causar banimento.");
        if (sessionId == 0) return new(ProcessGuard.Forbidden, "Roda na sessão de serviços (sessão 0): use a página Serviços ou \"reiniciar o serviço\".");
        if (path is not null)
        {
            var p = path.Replace('/', '\\').ToLowerInvariant();
            if (p.Contains(@"\windows\system32\", StringComparison.Ordinal) || p.Contains(@"\windows\syswow64\", StringComparison.Ordinal))
                return new(ProcessGuard.Sensitive, "Programa do Windows (System32): confirme que é isso mesmo.");
            if (LooksLikeGame(path))
                return new(ProcessGuard.Sensitive, "Parece um jogo: fechar perde o progresso não salvo.");
        }
        if (SensitiveNames.Contains(n)) return new(ProcessGuard.Sensitive, n.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? "Explorer: a barra de tarefas e as pastas abertas somem por alguns segundos e voltam."
            : "Launcher/gravação: fechar pode interromper download ou gravação em andamento.");
        return new(ProcessGuard.Normal, "Programa comum.");
    }
}
