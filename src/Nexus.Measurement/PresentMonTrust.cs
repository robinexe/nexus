using System.Security.Cryptography;

namespace Nexus.Measurement;

/// <summary>De onde veio o PresentMon que o NEXUS vai usar.</summary>
public enum PresentMonSource
{
    /// <summary>Cópia que acompanha o NEXUS (<c>tools\PresentMon\PresentMon.exe</c>, ao lado dos executáveis).</summary>
    Bundled,
    /// <summary>Caminho que o usuário indicou.</summary>
    Configured,
    /// <summary>Achado numa pasta conhecida (app da Intel, CapFrameX, Downloads...).</summary>
    Found,
}

/// <summary>Resultado da conferência da assinatura Authenticode (WinVerifyTrust) e o nome do assinante.</summary>
/// <param name="Valid">Assinatura íntegra e cadeia até uma raiz confiável do Windows.</param>
/// <param name="Signer">Nome simples do certificado do assinante (ex.: "Intel Corporation"), ou null.</param>
/// <param name="Detail">Explicação curta (para mensagens e relatório).</param>
public sealed record PresentMonSignature(bool Valid, string? Signer, string Detail)
{
    public static PresentMonSignature Invalid(string detail) => new(false, null, detail);
}

/// <summary>Leituras usadas na conferência (o Windows real ou um falso nos testes).</summary>
public interface IPresentMonTrustProbe
{
    /// <summary>SHA-256 do arquivo em hexadecimal minúsculo, ou null se não der para ler.</summary>
    string? Sha256(string path);

    /// <summary>Assinatura Authenticode do arquivo.</summary>
    PresentMonSignature Signature(string path);
}

/// <summary>Veredito da conferência: só um PresentMon <see cref="Trusted"/> é executado.</summary>
public sealed record PresentMonVerdict(bool Trusted, string? Sha256, string? Signer, string Reason);

/// <summary>
/// Conferência do PresentMon ANTES de executar (ele pode pedir elevação, então é tratado como código que roda com privilégio):
/// aceito se o SHA-256 é o de uma versão oficial conhecida (a que acompanha o NEXUS) OU se tem assinatura Authenticode válida
/// cujo assinante é exatamente "Intel Corporation". Qualquer outra coisa é recusada com o motivo.
/// </summary>
public static class PresentMonTrust
{
    /// <summary>Versão que acompanha o NEXUS (tools/presentmon no repositório).</summary>
    public const string BundledVersion = "2.6.0";

    /// <summary>Assinante aceito (campo CN do certificado de assinatura de código da Intel).</summary>
    public const string ExpectedSigner = "Intel Corporation";

    /// <summary>SHA-256 do PresentMon-2.6.0-x64.exe oficial (github.com/GameTechDev/PresentMon, release v2.6.0).</summary>
    public const string BundledSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";

    /// <summary>Hashes aceitos sem depender da assinatura (versões oficiais conferidas pelo projeto).</summary>
    public static IReadOnlySet<string> KnownSha256 { get; } = new HashSet<string>(StringComparer.Ordinal) { BundledSha256 };

    /// <summary>Pasta, relativa à pasta do NEXUS, onde fica a cópia embutida.</summary>
    public static readonly string[] BundledFolder = ["tools", "PresentMon"];
    public const string BundledFileName = "PresentMon.exe";

    /// <summary>Caminho da cópia embutida ao lado dos executáveis do NEXUS.</summary>
    public static string BundledPath(string appBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(appBaseDirectory);
        return Path.Combine(appBaseDirectory, BundledFolder[0], BundledFolder[1], BundledFileName);
    }

    public static PresentMonVerdict Verify(string path, IPresentMonTrustProbe probe)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(probe);
        var hash = probe.Sha256(path)?.ToLowerInvariant();
        if (hash is null) return new(false, null, null, $"Não deu para ler {Path.GetFileName(path)} para conferir.");
        if (KnownSha256.Contains(hash))
            return new(true, hash, ExpectedSigner, $"PresentMon {BundledVersion} oficial da Intel (SHA-256 conferido).");
        PresentMonSignature sig;
        try { sig = probe.Signature(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { sig = PresentMonSignature.Invalid("não deu para conferir a assinatura: " + ex.Message); }
        if (sig.Valid && string.Equals(sig.Signer?.Trim(), ExpectedSigner, StringComparison.OrdinalIgnoreCase))
            return new(true, hash, sig.Signer, $"Assinatura digital válida de {ExpectedSigner}.");
        var why = !sig.Valid ? $"a assinatura digital não é válida ({sig.Detail})"
                             : $"está assinado por \"{sig.Signer}\", não pela {ExpectedSigner}";
        return new(false, hash, sig.Signer,
            $"O NEXUS não vai executar {Path.GetFileName(path)}: não é a versão oficial conhecida e {why}. " +
            "Use o PresentMon que acompanha o NEXUS ou baixe o oficial em github.com/GameTechDev/PresentMon.");
    }

    /// <summary>SHA-256 de um arquivo (leitura compartilhada; null se não der para ler).</summary>
    public static string? FileSha256(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Probe sem assinatura (Linux, testes): só confere o hash.</summary>
public sealed class HashOnlyTrustProbe : IPresentMonTrustProbe
{
    public string? Sha256(string path) => PresentMonTrust.FileSha256(path);
    public PresentMonSignature Signature(string path) => PresentMonSignature.Invalid("conferência de assinatura só existe no Windows");
}

/// <summary>Um PresentMon localizado e o veredito da conferência.</summary>
public sealed record PresentMonLocation(string Path, PresentMonSource Source, PresentMonVerdict Verdict)
{
    public bool Usable => Verdict.Trusted;

    public string SourceText => Source switch
    {
        PresentMonSource.Bundled => "acompanha o NEXUS",
        PresentMonSource.Configured => "indicado por você",
        _ => "achado em " + (System.IO.Path.GetDirectoryName(Path) ?? Path),
    };
}

/// <summary>Arquivos vistos pelo localizador (o disco real ou um falso nos testes).</summary>
public interface IPresentMonFiles
{
    bool Exists(string path);
    /// <summary>PresentMon*.exe da pasta (só o nível de cima; pasta inexistente = vazio).</summary>
    IEnumerable<string> Executables(string directory);
}

public sealed class DiskPresentMonFiles : IPresentMonFiles
{
    public static DiskPresentMonFiles Instance { get; } = new();

    public bool Exists(string path)
    {
        try
        {
            // Sem seguir link: um link no lugar do PresentMon conta como "não existe".
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException) { return false; }
    }

    public IEnumerable<string> Executables(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "PresentMon*.exe", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return []; }
    }
}

/// <summary>
/// Onde o Medir procura o PresentMon, nesta ordem: (1) a cópia embutida <c>tools\PresentMon\PresentMon.exe</c> ao lado do NEXUS;
/// (2) o caminho que o usuário indicou; (3) as pastas conhecidas de <see cref="PresentMonCli.CandidateFolders"/>. Cada candidato é
/// conferido (<see cref="PresentMonTrust.Verify"/>); vale o primeiro aprovado. Se nenhum for aprovado, devolve o primeiro
/// encontrado com o motivo da recusa (para a página explicar), e ele NÃO é executado.
/// </summary>
public static class PresentMonLocator
{
    public static IReadOnlyList<(string Path, PresentMonSource Source)> Candidates(string? configured, string appBaseDirectory, IPresentMonFiles files,
        Func<Environment.SpecialFolder, string>? folder = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var list = new List<(string, PresentMonSource)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p, PresentMonSource s)
        {
            if (string.IsNullOrWhiteSpace(p) || !seen.Add(p) || !files.Exists(p)) return;
            list.Add((p, s));
        }
        Add(PresentMonTrust.BundledPath(appBaseDirectory), PresentMonSource.Bundled);
        Add(configured, PresentMonSource.Configured);
        foreach (var dir in PresentMonCli.CandidateFolders(appBaseDirectory, folder))
            Add(PresentMonCli.PickExecutable(files.Executables(dir)), PresentMonSource.Found);
        return list;
    }

    public static PresentMonLocation? Locate(string? configured, string appBaseDirectory, IPresentMonTrustProbe probe, IPresentMonFiles? files = null,
        Func<Environment.SpecialFolder, string>? folder = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        PresentMonLocation? firstRefused = null;
        foreach (var (path, source) in Candidates(configured, appBaseDirectory, files ?? DiskPresentMonFiles.Instance, folder))
        {
            var verdict = PresentMonTrust.Verify(path, probe);
            var location = new PresentMonLocation(path, source, verdict);
            if (verdict.Trusted) return location;
            firstRefused ??= location;
        }
        return firstRefused;
    }

    /// <summary>Probe do sistema atual: Authenticode no Windows; só hash nos outros sistemas.</summary>
    public static IPresentMonTrustProbe SystemProbe() =>
        OperatingSystem.IsWindows() ? new Windows.WindowsPresentMonTrustProbe() : new HashOnlyTrustProbe();
}
