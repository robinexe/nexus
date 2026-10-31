using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexus.Security.Scanner;

/// <summary>Alvo da quarentena: o arquivo (caminho) e o SHA-256 que a verificação viu. <see cref="Machine"/> = vaga de administrador.</summary>
public sealed record QuarantineTarget(bool Machine, string Path, string Sha256, string? UserSid)
{
    public string Scope => Machine ? "HKLM" : "HKCU";
}

/// <summary>Manifesto gravado ANTES de mover. <see cref="State"/> = "pending" até o arquivo sair do lugar, depois "done".</summary>
public sealed record ScannerQuarantineManifest(
    string Format,
    string Scope,
    string? UserSid,
    string OriginalPath,
    string Sha256,
    long Size,
    DateTimeOffset QuarantinedUtc,
    string State)
{
    public const string CurrentFormat = "nexus.scanner.quarantine/1";

    public bool Matches(QuarantineTarget t) =>
        t is not null && Format == CurrentFormat && Scope == t.Scope && string.Equals(UserSid, t.UserSid, StringComparison.Ordinal) &&
        string.Equals(OriginalPath, t.Path, StringComparison.Ordinal) && string.Equals(Sha256, t.Sha256, StringComparison.Ordinal);
}

/// <summary>
/// Quarentena do verificador em <c>DataDirectory\quarantine\scanner\&lt;id&gt;\</c> (<c>manifest.json</c> + <c>payload.bin</c>;
/// a extensão .bin impede abrir por engano com duplo clique). Mesmo volume: renomear (atômico, preserva a marca "baixado da
/// internet"). Outro volume: copia, confere SHA-256, confere que a origem não mudou durante a cópia, e só então remove a origem.
/// Nada é apagado de verdade: o Desfazer devolve o arquivo ao lugar (ou ao lado, com sufixo, se algo novo ocupar o caminho).
/// </summary>
public sealed class ScannerQuarantineStore
{
    public const long MaxBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ScannerQuarantineStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Root = System.IO.Path.Combine(dataDirectory, "quarantine", "scanner");
    }

    public string Root { get; }

    /// <summary>Força o caminho "outro volume" (testes).</summary>
    public bool ForceCrossVolume { get; init; }

    public static string IdOf(QuarantineTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{t.Scope}|{t.UserSid}|{t.Path.ToUpperInvariant()}|{t.Sha256}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    public string FolderOf(QuarantineTarget t) => System.IO.Path.Combine(Root, IdOf(t));
    public string ManifestPath(QuarantineTarget t) => System.IO.Path.Combine(FolderOf(t), "manifest.json");
    public string PayloadPath(QuarantineTarget t) => System.IO.Path.Combine(FolderOf(t), "payload.bin");
    public bool PayloadExists(QuarantineTarget t) => File.Exists(PayloadPath(t));

    public ScannerQuarantineManifest? ReadManifest(QuarantineTarget t)
    {
        var path = ManifestPath(t);
        if (!File.Exists(path)) return null;
        var m = JsonSerializer.Deserialize<ScannerQuarantineManifest>(File.ReadAllText(path), Json);
        return m is not null && m.Matches(t) ? m : null;
    }

    public void WriteManifest(QuarantineTarget t, ScannerQuarantineManifest m)
    {
        Directory.CreateDirectory(FolderOf(t));
        var final = ManifestPath(t);
        var tmp = final + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, m, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, final, overwrite: true);
    }

    public void Discard(QuarantineTarget t)
    {
        var folder = FolderOf(t);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>Move o arquivo para a quarentena. <paramref name="revalidate"/> é chamado logo antes de mover (texto = recusa).</summary>
    public void Quarantine(QuarantineTarget t, Func<string?> revalidate)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(revalidate);
        var payload = PayloadPath(t);
        if (PayloadExists(t))
        {
            var stale = ReadManifest(t);
            if (stale is null || stale.State != "pending") throw new IOException("Já existe uma quarentena deste arquivo; restaure-a antes.");
            File.Delete(payload); // sobra de tentativa interrompida com a origem intacta (a origem é a verdade)
        }
        var info = new FileInfo(t.Path);
        if (!info.Exists) throw new FileNotFoundException("O arquivo não existe mais.", t.Path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("O alvo é um link: o NEXUS não segue nem move links.");
        if (info.Length > MaxBytes) throw new IOException($"Arquivo grande demais para a quarentena verificada (máx. {MaxBytes / (1024 * 1024)} MB).");
        if (!string.Equals(HashFile(t.Path), t.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("O arquivo mudou desde a verificação (SHA-256 diferente). Verifique de novo antes de isolar.");

        var manifest = new ScannerQuarantineManifest(ScannerQuarantineManifest.CurrentFormat, t.Scope, t.UserSid, t.Path, t.Sha256, info.Length, DateTimeOffset.UtcNow, "pending");
        WriteManifest(t, manifest);
        if (revalidate() is { } late) throw new InvalidOperationException(late + " Nada foi movido.");
        MoveVerified(t.Path, payload, t.Sha256);
        WriteManifest(t, manifest with { State = "done" });
    }

    /// <summary>Confere a quarentena: conteúdo presente e com o SHA-256 do manifesto. null = íntegra.</summary>
    public string? VerifyPayload(QuarantineTarget t, ScannerQuarantineManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var payload = PayloadPath(t);
        if (!File.Exists(payload)) return "A quarentena não tem mais o arquivo.";
        if ((File.GetAttributes(payload) & FileAttributes.ReparsePoint) != 0) return "O conteúdo da quarentena virou um link.";
        if (!string.Equals(m.Sha256, t.Sha256, StringComparison.Ordinal)) return "Manifesto de outro arquivo.";
        try
        {
            if (!string.Equals(HashFile(payload), m.Sha256, StringComparison.Ordinal)) return "O arquivo da quarentena foi alterado (SHA-256 difere do manifesto).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"Quarentena ilegível: {ex.Message}"; }
        return null;
    }

    /// <summary>Devolve ao caminho original (ou ao lado, com sufixo, sem sobrescrever). Retorna o caminho final.</summary>
    public string Restore(QuarantineTarget t, ScannerQuarantineManifest m, Func<string?> revalidate)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(revalidate);
        if (VerifyPayload(t, m) is { } bad) throw new InvalidOperationException(bad + " Nada foi devolvido.");
        var dest = AvailableName(t.Path);
        var parent = System.IO.Path.GetDirectoryName(dest);
        if (parent is not null) Directory.CreateDirectory(parent);
        if (revalidate() is { } late) throw new InvalidOperationException(late + " Nada foi devolvido.");
        MoveVerified(PayloadPath(t), dest, m.Sha256);
        Discard(t);
        return dest;
    }

    public static string AvailableName(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!File.Exists(original) && !Directory.Exists(original)) return original;
        var dir = System.IO.Path.GetDirectoryName(original) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(original);
        var ext = System.IO.Path.GetExtension(original);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{name} (restaurado pelo NEXUS{(i == 1 ? "" : " " + i.ToString(System.Globalization.CultureInfo.InvariantCulture))}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Não há nome livre para restaurar sem sobrescrever.");
    }

    private void MoveVerified(string from, string to, string expectedSha)
    {
        var sameVolume = !ForceCrossVolume &&
            string.Equals(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(from)), System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            File.Move(from, to, overwrite: false);
            // Revalida DEPOIS de mover: se o arquivo foi trocado entre a conferência e a movimentação, desfaz e recusa.
            if (!string.Equals(HashFile(to), expectedSha, StringComparison.Ordinal))
            {
                if (!File.Exists(from)) File.Move(to, from, overwrite: false);
                throw new InvalidOperationException("O arquivo mudou durante a operação (SHA-256 diferente); foi devolvido ao lugar.");
            }
            return;
        }
        var created = false;
        try
        {
            DateTime stamp;
            long length;
            using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read)) // ninguém escreve durante a cópia
            {
                var info = new FileInfo(from);
                stamp = info.LastWriteTimeUtc;
                length = info.Length;
                using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                src.CopyTo(dst);
                dst.Flush(flushToDisk: true);
            }
            if (!string.Equals(HashFile(to), expectedSha, StringComparison.Ordinal))
                throw new IOException("A cópia não conferiu (SHA-256). A origem foi mantida.");
            var again = new FileInfo(from);
            if (again.LastWriteTimeUtc != stamp || again.Length != length)
                throw new IOException("A origem mudou durante a cópia. Nada foi removido.");
        }
        catch
        {
            if (created && File.Exists(to)) File.Delete(to); // cópia parcial: a origem continua intacta
            throw;
        }
        File.Delete(from);
    }
}
