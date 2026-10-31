using System.Security.Cryptography;

namespace Nexus.Security;

/// <summary>
/// Cópias de segurança endereçadas pelo conteúdo (nome = SHA-256), em <c>DataDirectory\quarantine\security\&lt;tipo&gt;</c>.
/// Escrita atômica (temporário + rename) e conferência do hash depois de gravar; leitura confere o hash de novo.
/// Nada aqui é apagado pelo NEXUS: as cópias ficam para o usuário (ou para a recuperação) até ele decidir.
/// </summary>
public sealed class BackupStore
{
    private readonly string _dir;

    public BackupStore(string dataDirectory, string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        _dir = Path.Combine(dataDirectory, "quarantine", "security", kind);
    }

    public string Directory => _dir;

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private string PathOf(string hash, string extension)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Hash inválido.", nameof(hash));
        return Path.Combine(_dir, hash + extension);
    }

    /// <summary>Grava (se ainda não existir) e devolve o hash. Falha se a releitura não bater.</summary>
    public string Save(byte[] content, string extension)
    {
        ArgumentNullException.ThrowIfNull(content);
        var hash = Hash(content);
        var path = PathOf(hash, extension);
        System.IO.Directory.CreateDirectory(_dir);
        if (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != hash)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        if (Hash(File.ReadAllBytes(path)) != hash) throw new IOException("A cópia de segurança gravada não confere; nada foi alterado.");
        return hash;
    }

    public bool Exists(string hash, string extension)
    {
        var path = PathOf(hash, extension);
        return File.Exists(path) && Hash(File.ReadAllBytes(path)) == hash;
    }

    public byte[] Load(string hash, string extension)
    {
        var path = PathOf(hash, extension);
        if (!File.Exists(path)) throw new FileNotFoundException("Cópia de segurança não encontrada.", path);
        var bytes = File.ReadAllBytes(path);
        if (Hash(bytes) != hash) throw new InvalidDataException("Cópia de segurança adulterada ou corrompida (hash diferente).");
        return bytes;
    }

    /// <summary>Metadados ao lado da cópia (ex.: hash do que o NEXUS escreveu). Sobrescrevível; não entra no hash.</summary>
    public void SaveSidecar(string hash, string name, string text) => File.WriteAllText(PathOf(hash, "." + name), text);

    public string? LoadSidecar(string hash, string name)
    {
        var p = PathOf(hash, "." + name);
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }
}
