using System.Text;
using Nexus.Core;

namespace Nexus.Security;

/// <summary>Acesso ao arquivo hosts (falso nos testes).</summary>
public interface IHostsStore
{
    string Path { get; }
    /// <summary>Conteúdo atual, ou null se o arquivo não existe.</summary>
    byte[]? Read();
    /// <summary>Reescreve no lugar (mantém ACL e atributos do arquivo existente).</summary>
    void Write(byte[] content);
    /// <summary>O caminho é link simbólico/junção (nunca seguido).</summary>
    bool IsReparsePoint();
}

/// <summary>Arquivo hosts real: sempre <c>%SystemRoot%\System32\drivers\etc\hosts</c> (nunca um caminho vindo de fora).</summary>
public sealed class SystemHostsStore : IHostsStore
{
    public string Path { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");

    public byte[]? Read()
    {
        var info = new FileInfo(Path);
        if (!info.Exists) return null;
        if (info.Length > HostsFile.MaxBytes) throw new IOException("Arquivo hosts grande demais para tratar com segurança.");
        return File.ReadAllBytes(Path);
    }

    public void Write(byte[] content)
    {
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.Read);
        fs.SetLength(0);
        fs.Write(content);
        fs.Flush(flushToDisk: true);
    }

    public bool IsReparsePoint()
    {
        var info = new FileInfo(Path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}

/// <summary>
/// Handler "SecurityHostsFile": desativa (comenta) as linhas do hosts que redirecionam ou bloqueiam domínios de segurança,
/// atualização e login (<see cref="HostsFile.Analyze"/>). Linhas de telemetria/anúncios não são tocadas.
/// Estado: <c>HostsState:clean</c> (nada a corrigir) ou <c>HostsState:suspicious:&lt;sha256&gt;</c>.
/// Antes de escrever, o arquivo original é copiado para <c>DataDirectory\quarantine\security\hosts\&lt;sha256&gt;.bak</c> e o hash
/// do conteúdo escrito vai ao lado (".written"). Restore devolve o original byte a byte, mas só se o hosts atual ainda for
/// exatamente o que o NEXUS escreveu (nunca apaga edições posteriores do usuário).
/// </summary>
public sealed class HostsFileHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "SecurityHostsFile";
    public const string StateType = "HostsState";
    public const string Clean = "clean";
    private const string Extension = ".bak";
    private readonly IHostsStore _hosts;
    private readonly BackupStore _store;

    public HostsFileHandler(IHostsStore hosts, string dataDirectory)
    {
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _store = new BackupStore(dataDirectory, "hosts");
    }

    public string HandlerKind => Kind;

    /// <summary>Lê o texto preservando o BOM (se houver) para reescrever igual.</summary>
    internal static (string Text, byte[] Preamble) Decode(byte[] bytes)
    {
        var utf8Bom = Encoding.UTF8.GetPreamble();
        if (bytes.AsSpan().StartsWith(utf8Bom)) return (Encoding.UTF8.GetString(bytes, utf8Bom.Length, bytes.Length - utf8Bom.Length), utf8Bom);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), [0xFF, 0xFE]);
        // Sem BOM: o hosts padrão é ASCII; UTF-8 sem BOM cobre os dois. Latin-1 inválido em UTF-8 vira U+FFFD → recusado no Apply.
        return (Encoding.UTF8.GetString(bytes), []);
    }

    internal static byte[] Encode(string text, byte[] preamble)
    {
        var enc = preamble.Length == 2 ? Encoding.Unicode : (Encoding)new UTF8Encoding(false);
        return [.. preamble, .. enc.GetBytes(text)];
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!action.Desired.Exists || action.Desired.Value != Clean) return Applicability.Blocked("Estado desejado inválido para o hosts.");
        if (_hosts.IsReparsePoint()) return Applicability.Blocked("O arquivo hosts é um link (reparse point); o NEXUS não segue links.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var bytes = _hosts.Read();
        if (bytes is null) return StateSnapshot.Of(StateType, Clean, ValueOrigin.Explicit, new Dictionary<string, string> { ["file"] = "ausente" });
        var (text, _) = Decode(bytes);
        var fixable = HostsFile.Analyze(HostsFile.Parse(text)).Where(i => i.Fixable).ToList();
        var detail = new Dictionary<string, string>
        {
            ["sha256"] = BackupStore.Hash(bytes),
            ["fixable"] = fixable.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["hosts"] = Text.Clip(string.Join(", ", fixable.Select(i => i.Host).Distinct()), 600),
        };
        return fixable.Count == 0
            ? StateSnapshot.Of(StateType, Clean, ValueOrigin.Explicit, detail)
            : StateSnapshot.Of(StateType, "suspicious:" + detail["sha256"], ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        var bytes = _hosts.Read() ?? throw new FileNotFoundException("Arquivo hosts não encontrado.", _hosts.Path);
        if (_hosts.IsReparsePoint()) throw new InvalidOperationException("O arquivo hosts é um link; nada foi alterado.");
        var (text, preamble) = Decode(bytes);
        if (text.Contains('�', StringComparison.Ordinal)) throw new InvalidDataException("O hosts tem caracteres fora de UTF-8; para não corromper o arquivo, o NEXUS não o altera.");
        var updated = HostsFile.DisableFixable(text, out var disabled);
        if (disabled == 0) return;
        var originalHash = _store.Save(bytes, Extension);           // 1) cópia conferida
        var written = Encode(updated, preamble);
        _store.SaveSidecar(originalHash, "written", BackupStore.Hash(written));
        _hosts.Write(written);                                     // 2) só então escreve
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Value is null || !original.Value.StartsWith("suspicious:", StringComparison.Ordinal)) return; // já estava limpo: nada a devolver
        var hash = original.Value["suspicious:".Length..];
        var backup = _store.Load(hash, Extension);
        var current = _hosts.Read();
        var expected = _store.LoadSidecar(hash, "written");
        // Sem o registro do que o NEXUS escreveu não há como provar que o hosts atual é o dele: recusar em vez de sobrescrever
        // (revisão de segurança, baixo). Antes, faltando o ".written", a cópia era devolvida por cima de qualquer edição.
        if (expected is null)
            throw new InvalidOperationException("Falta o registro do conteúdo que o NEXUS escreveu (.written); para não apagar edições posteriores, o hosts não foi restaurado. A cópia original está em " + _store.Directory + ".");
        if (current is not null && BackupStore.Hash(current) != expected.Trim())
            throw new InvalidOperationException("O hosts mudou depois da correção (outra edição). Para não apagar essa edição, o NEXUS não restaurou; a cópia original está em " + _store.Directory + ".");
        if (_hosts.IsReparsePoint()) throw new InvalidOperationException("O arquivo hosts virou um link; nada foi restaurado.");
        _hosts.Write(backup);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Value is null || !original.Value.StartsWith("suspicious:", StringComparison.Ordinal)) return null;
        try
        {
            var hash = original.Value["suspicious:".Length..];
            if (!_store.Exists(hash, Extension)) return "Cópia do hosts original não encontrada.";
            return _store.LoadSidecar(hash, "written") is null
                ? "Falta o registro do conteúdo que o NEXUS escreveu (.written); restaurar poderia apagar edições posteriores. Nada foi escrito."
                : null;
        }
        catch (ArgumentException ex) { return ex.Message; }
    }
}
