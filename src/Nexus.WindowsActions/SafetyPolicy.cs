namespace Nexus.WindowsActions;

/// <summary>
/// Lista de alvos proibidos aplicada pelos handlers independentemente do catálogo.
/// Mesmo um catálogo assinado não consegue tocar segurança, anticheat ou boot.
/// </summary>
public static class SafetyPolicy
{
    private static readonly string[] ForbiddenRegistryPrefixes =
    [
        @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard",
        @"HKLM\SYSTEM\CurrentControlSet\Control\CI",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa",
        @"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot",
        @"HKLM\SYSTEM\CurrentControlSet\Services\vgc",
        @"HKLM\SYSTEM\CurrentControlSet\Services\vgk",
        @"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend",
        @"HKLM\SYSTEM\CurrentControlSet\Services\WdFilter",
        @"HKLM\SYSTEM\CurrentControlSet\Services\mpssvc",
        @"HKLM\SOFTWARE\Microsoft\Windows Defender",
        @"HKLM\SOFTWARE\Policies",
        @"HKCU\SOFTWARE\Policies",
        @"HKLM\BCD00000000",
        @"HKLM\SAM",
        @"HKLM\SECURITY",
    ];

    private static readonly string[] ForbiddenServices = ["vgc", "vgk", "WinDefend", "WdNisSvc", "mpssvc", "BFE", "EventLog", "RpcSs", "Winmgmt"];

    public static string? CheckRegistry(string hive, string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('\\') || path.Contains("\\\\", StringComparison.Ordinal))
            return "Caminho de registro inválido.";
        if (string.IsNullOrEmpty(name))
            return "Valor padrão '(Default)' não é alvo permitido.";
        var full = Normalize($@"{hive}\{path}").TrimEnd('\\') + "\\";
        foreach (var p in ForbiddenRegistryPrefixes)
            if (full.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return $"Alvo protegido pela política de segurança do NEXUS ({p}).";
        return null;
    }

    /// <summary>
    /// Forma canônica para comparação: "ControlSet001/002..." vira "CurrentControlSet" (são o mesmo conjunto ativo ou um
    /// alternativo que o boot pode usar), "WOW6432Node\" é removido (visão 32 bits da mesma chave) e separadores repetidos
    /// são colapsados. Sem isso, "HKLM\SYSTEM\ControlSet001\Control\Lsa" escaparia da lista.
    /// </summary>
    internal static string Normalize(string fullPath)
    {
        var parts = fullPath.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        parts.RemoveAll(p => p.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i < parts.Count; i++)
            if (parts[i].StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase) && parts[i].Length == 13 && parts[i][10..].All(char.IsAsciiDigit))
                parts[i] = "CurrentControlSet";
        return string.Join('\\', parts);
    }

    public static string? CheckService(string serviceName) =>
        ForbiddenServices.Contains(serviceName, StringComparer.OrdinalIgnoreCase)
            ? $"Serviço '{serviceName}' é protegido (segurança/anticheat/núcleo do sistema)."
            : null;
}
