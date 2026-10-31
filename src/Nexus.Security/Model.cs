namespace Nexus.Security;

/// <summary>Gravidade de um ponto suspeito. Nunca vira remoção automática: é um pedido de revisão.</summary>
public enum Severity { Info, Low, Medium, High }

/// <summary>Estado de um item do placar de proteção.</summary>
public enum Tone { Good, Warn, Bad, Unknown }

/// <summary>
/// Ponto suspeito para revisão. <paramref name="FixActionId"/> é a ação de catálogo que corrige de forma reversível
/// (quando existe); <paramref name="FixSelection"/> é o identificador curto da entrada para ações por seleção.
/// </summary>
public sealed record Finding(
    string Category,
    Severity Severity,
    string Title,
    string Evidence,
    string Why,
    string HowToVerify,
    string Suggested,
    string? FixActionId = null,
    string? FixSelection = null);

/// <summary>Item do placar (Defender, firewall, BitLocker...). <paramref name="OpenUri"/> abre a tela oficial do Windows.</summary>
public sealed record ProtectionItem(string Name, Tone Tone, string State, string Detail, string? Advice = null, string? OpenUri = null);

/// <summary>Uma detecção do histórico do Defender (somente leitura).</summary>
public sealed record ThreatRecord(
    string Name,
    string Severity,
    DateTimeOffset? Detected,
    string Status,
    bool Active,
    IReadOnlyList<string> Resources,
    string Recommendation);

/// <summary>Resultado de uma leitura completa de segurança.</summary>
public sealed record SecurityReport(
    IReadOnlyList<ProtectionItem> Protection,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<ThreatRecord> Threats,
    IReadOnlyList<string> Notes,
    DateTimeOffset ReadAt);

internal static class Text
{
    public static string Clip(string? s, int max = 300)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
