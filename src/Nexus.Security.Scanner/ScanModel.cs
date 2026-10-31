namespace Nexus.Security.Scanner;

/// <summary>Camada que produziu um sinal: lista de hashes, regra de padrão ou heurística (PE/nome/local).</summary>
public enum ScanLayer { Hash, Pattern, Heuristic }

/// <summary>
/// Resultado de um arquivo. "Suspeito" NÃO é "malware": só a correspondência com a lista de hashes (que o usuário carregou)
/// afirma identidade, e mesmo assim a palavra final é do Microsoft Defender.
/// </summary>
public enum Verdict { Clean = 0, Review = 1, Suspicious = 2, HighlySuspicious = 3, KnownBad = 4 }

/// <summary>Um sinal com a explicação em português simples. Pontos negativos/zero são só explicativos.</summary>
/// <param name="TestOnly">Sinal de um padrão/hash de TESTE (EICAR, amostra do NEXUS): não é ameaça.</param>
public sealed record ScanHit(ScanLayer Layer, string RuleId, string Title, string Why, int Points, bool TestOnly = false);

public sealed record FileVerdict(
    string Path,
    long Size,
    string? Sha256,
    Verdict Verdict,
    int Score,
    IReadOnlyList<ScanHit> Hits,
    bool? ValidSignature,
    DateTime ScannedUtc)
{
    /// <summary>Todo sinal relevante veio de padrões/hashes de teste (ex.: EICAR).</summary>
    public bool IsTestSample => Hits.Any(h => h.TestOnly) && Hits.Where(h => h.Points > 0).All(h => h.TestOnly);

    public string VerdictText => IsTestSample ? "Arquivo de teste (não é ameaça)" : Scoring.Describe(Verdict);
}

/// <summary>Pontuação e limiares (documentados em docs/modules/scanner.md).</summary>
public static class Scoring
{
    public const int ReviewAt = 20;
    public const int SuspiciousAt = 45;
    public const int HighlySuspiciousAt = 75;

    /// <summary>Teto da heurística de PE quando a assinatura digital é válida: fica abaixo de "para revisar".</summary>
    public const int SignedHeuristicCap = 15;

    public static Verdict FromScore(int score, bool hashMatch) => hashMatch ? Verdict.KnownBad
        : score >= HighlySuspiciousAt ? Verdict.HighlySuspicious
        : score >= SuspiciousAt ? Verdict.Suspicious
        : score >= ReviewAt ? Verdict.Review
        : Verdict.Clean;

    public static string Describe(Verdict v) => v switch
    {
        Verdict.KnownBad => "Está na sua lista de hashes",
        Verdict.HighlySuspicious => "Muito suspeito",
        Verdict.Suspicious => "Suspeito",
        Verdict.Review => "Para revisar",
        _ => "Sem sinais",
    };
}

/// <summary>Progresso de uma verificação (para a interface mostrar arquivos/s e o arquivo atual).</summary>
public sealed record ScanProgress(long Files, long Bytes, long Skipped, int Findings, string? Current, double FilesPerSecond, TimeSpan Elapsed);

public sealed record ScanSummary(long Files, long Bytes, long Skipped, long Errors, TimeSpan Elapsed, bool Cancelled, IReadOnlyList<FileVerdict> Findings);
