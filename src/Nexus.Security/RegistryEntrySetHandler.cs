using System.Text;
using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Security;

/// <summary>Entrada candidata à remoção (chave inteira quando <see cref="ValueName"/> é null).</summary>
public sealed record EntryCandidate(string Category, RegKeyRef Key, string? ValueName, string Selection);

/// <summary>Cópia completa de uma entrada removida.</summary>
public sealed record EntryBackup(string Category, string Hive, string View, string Path, string? ValueName, RegKeyExport? Key, RegValue? Value);

public sealed record EntrySetManifest(int Version, IReadOnlyList<EntryBackup> Entries);

/// <summary>
/// Motor comum dos handlers que removem entradas de registro ESCOLHIDAS pelo usuário (seleção por identificador curto,
/// nunca por caminho livre). Alvo do catálogo: <c>hive</c> (HKLM/HKCU) e <c>selection</c> (lista "id8,id8,..." validada pelo
/// catálogo e aqui). Só entram entradas que a regra do handler reconhece AGORA (órfã / suspeita); o resto é ignorado.
/// Estado: <c>RegistryEntrySet:absent</c> ou <c>RegistryEntrySet:present:&lt;sha256 do manifesto&gt;</c>. Antes de remover, o
/// manifesto completo (tipo e dados de cada valor, subchaves inteiras) é gravado e conferido em
/// <c>DataDirectory\quarantine\security\registry</c>; Restore recria a partir dele, sem sobrescrever o que existir.
/// </summary>
public abstract partial class RegistryEntrySetHandler : IActionHandler, IRestoreValidator
{
    public const string StateType = "RegistryEntrySet";
    public const string Absent = "absent";
    private const string Extension = ".json";

    [GeneratedRegex(@"^[0-9a-f]{8}(,[0-9a-f]{8}){0,27}$", RegexOptions.CultureInvariant)]
    private static partial Regex SelectionPattern();

    protected ISecurityRegistry Registry { get; }
    private readonly BackupStore _store;
    private readonly bool _otherAccount;

    protected RegistryEntrySetHandler(ISecurityRegistry registry, string dataDirectory, bool otherAccount)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = new BackupStore(dataDirectory, "registry");
        _otherAccount = otherAccount;
    }

    public abstract string HandlerKind { get; }

    /// <summary>Entradas que a regra reconhece agora (órfãs, suspeitas) no escopo do alvo.</summary>
    protected abstract IEnumerable<EntryCandidate> Candidates(RegHive hive, string? userSid);

    /// <summary>A entrada fica sob uma raiz permitida por este handler (revalidado antes de restaurar).</summary>
    protected abstract bool IsAllowedLocation(RegHive hive, string path, string? valueName);

    public static IReadOnlySet<string> ParseSelection(string selection) =>
        selection.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static (RegHive Hive, string? Sid, string? Problem) Scope(ActionInstance action)
    {
        if (!action.Target.TryGetValue("hive", out var h) || !Enum.TryParse<RegHive>(h, true, out var hive)) return (default, null, "Alvo sem hive válida.");
        if (UserHive.Check(action, hive) is { } p) return (hive, null, p);
        return (hive, UserHive.Of(action), null);
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (hive, sid, problem) = Scope(action);
        if (problem is not null) return Applicability.Blocked(problem);
        if (hive == RegHive.HKCU && sid is null && _otherAccount) return Applicability.Blocked("Perfil do usuário não identificado (aplicador com outra conta).");
        if (!action.Target.TryGetValue("selection", out var sel) || !SelectionPattern().IsMatch(sel)) return Applicability.Blocked("Seleção inválida (até 28 identificadores de 8 hex).");
        if (!action.Desired.Exists || action.Desired.Value != Absent) return Applicability.Blocked("Este handler só remove entradas (estado desejado 'absent').");
        foreach (var c in Selected(action))
        {
            if (!IsAllowedLocation(c.Key.Hive, c.ValueName is null ? Parent(c.Key.Path) : c.Key.Path, c.ValueName))
                return Applicability.Blocked($"Local não permitido: {c.Key}.");
            if (SafetyPolicy.CheckRegistry(c.Key.Hive.ToString(), c.Key.Path, c.ValueName is { Length: > 0 } n ? n : "(chave)") is { } safety)
                return Applicability.Blocked(safety);
            if (c.ValueName is null && !RegKinds.AllRestorable(RegKinds.Export(Registry, c.Key, LastSegment(c.Key.Path))))
                return Applicability.Blocked($"{c.Key} tem valores de tipo que o NEXUS não sabe recriar exatamente; nada será removido.");
            if (c.ValueName is not null && Registry.Value(c.Key, c.ValueName) is { } v && !RegKinds.Restorable.Contains(v.Kind))
                return Applicability.Blocked($"Valor de tipo {v.Kind} não suportado para cópia fiel.");
        }
        return Applicability.Ok;
    }

    private List<EntryCandidate> Selected(ActionInstance action)
    {
        var (hive, sid, problem) = Scope(action);
        if (problem is not null || !action.Target.TryGetValue("selection", out var sel)) return [];
        var wanted = ParseSelection(sel);
        return Candidates(hive, sid).Where(c => wanted.Contains(c.Selection)).Take(28).ToList();
    }

    private EntrySetManifest Snapshot(IEnumerable<EntryCandidate> candidates)
    {
        var entries = new List<EntryBackup>();
        foreach (var c in candidates.OrderBy(c => c.Selection, StringComparer.Ordinal))
        {
            if (c.ValueName is null)
            {
                if (!Registry.KeyExists(c.Key)) continue;
                entries.Add(new EntryBackup(c.Category, c.Key.Hive.ToString(), c.Key.View.ToString(), c.Key.Path, null, RegKinds.Export(Registry, c.Key, LastSegment(c.Key.Path)), null));
            }
            else if (Registry.Value(c.Key, c.ValueName) is { } v)
            {
                entries.Add(new EntryBackup(c.Category, c.Key.Hive.ToString(), c.Key.View.ToString(), c.Key.Path, c.ValueName, null, v));
            }
        }
        return new EntrySetManifest(1, entries);
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var manifest = Snapshot(Selected(action));
        if (manifest.Entries.Count == 0) return StateSnapshot.Of(StateType, Absent);
        var json = RegKinds.Serialize(manifest);
        return StateSnapshot.Of(StateType, "present:" + RegKinds.Hash(json), ValueOrigin.Explicit,
            new Dictionary<string, string> { ["count"] = manifest.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["entries"] = Text.Clip(string.Join("; ", manifest.Entries.Select(e => e.ValueName is null ? e.Path : e.Path + " :: " + e.ValueName)), 900) });
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (_, sid, _) = Scope(action);
        var manifest = Snapshot(Selected(action));
        if (manifest.Entries.Count == 0) return;
        // 1) cópia completa gravada e conferida; 2) só então remove.
        var hash = _store.Save(Encoding.UTF8.GetBytes(RegKinds.Serialize(manifest)), Extension);
        _ = RegKinds.Deserialize<EntrySetManifest>(Encoding.UTF8.GetString(_store.Load(hash, Extension))) ?? throw new InvalidDataException("Manifesto ilegível.");
        foreach (var e in manifest.Entries)
        {
            var key = RefOf(e, sid);
            if (e.ValueName is null) Registry.DeleteKeyTree(key);
            else Registry.DeleteValue(key, e.ValueName);
        }
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Value == Absent || !original.Exists)
        {
            Apply(action); // "ausente" antes: remover de novo o que voltou (com cópia).
            return;
        }
        var (_, sid, _) = Scope(action);
        var manifest = LoadManifest(original) ?? throw new FileNotFoundException("Cópia de segurança do registro não encontrada; nada foi restaurado.");
        foreach (var e in manifest.Entries)
        {
            var key = RefOf(e, sid);
            if (!IsAllowedLocation(key.Hive, e.ValueName is null ? Parent(key.Path) : key.Path, e.ValueName))
                throw new InvalidOperationException($"Manifesto aponta para local não permitido: {key}.");
            if (e.ValueName is null && e.Key is not null)
            {
                if (Registry.KeyExists(key)) continue; // algo novo no lugar: não sobrescrever
                RegKinds.Import(Registry, key, e.Key);
            }
            else if (e.Value is not null)
            {
                if (Registry.Value(key, e.Value.Name) is not null) continue;
                Registry.SetValue(key, e.Value);
            }
        }
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        var (_, _, problem) = Scope(action);
        if (problem is not null) return problem;
        if (!action.Target.TryGetValue("selection", out var sel) || !SelectionPattern().IsMatch(sel)) return "Seleção inválida no diário.";
        if (original.Value == Absent) return null;
        try
        {
            var manifest = LoadManifest(original);
            if (manifest is null) return "Cópia de segurança do registro não encontrada.";
            foreach (var e in manifest.Entries)
                if (!Enum.TryParse<RegHive>(e.Hive, out var h) || !IsAllowedLocation(h, e.ValueName is null ? Parent(e.Path) : e.Path, e.ValueName))
                    return $"Manifesto aponta para local não permitido: {e.Path}.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            return $"Cópia de segurança inválida: {ex.Message}";
        }
    }

    private EntrySetManifest? LoadManifest(StateSnapshot original)
    {
        if (original.Value is null || !original.Value.StartsWith("present:", StringComparison.Ordinal)) return null;
        var hash = original.Value["present:".Length..];
        if (!_store.Exists(hash, Extension)) return null;
        return RegKinds.Deserialize<EntrySetManifest>(Encoding.UTF8.GetString(_store.Load(hash, Extension)));
    }

    private static RegKeyRef RefOf(EntryBackup e, string? sid)
    {
        var hive = Enum.Parse<RegHive>(e.Hive);
        return new RegKeyRef(hive, e.Path, Enum.Parse<RegView>(e.View), hive == RegHive.HKCU ? sid : null);
    }

    protected static string Parent(string path)
    {
        var i = path.TrimEnd('\\').LastIndexOf('\\');
        return i < 0 ? "" : path[..i];
    }

    protected static string LastSegment(string path)
    {
        var i = path.TrimEnd('\\').LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }
}

/// <summary>Handler "SecurityRegistryOrphan": remove sobras SELECIONADAS do inspetor de registro (com cópia completa).</summary>
public sealed class RegistryOrphanHandler : RegistryEntrySetHandler
{
    public const string Kind = "SecurityRegistryOrphan";
    private readonly IFileProbe _files;

    public RegistryOrphanHandler(ISecurityRegistry registry, IFileProbe files, string dataDirectory, bool otherAccount)
        : base(registry, dataDirectory, otherAccount) => _files = files ?? throw new ArgumentNullException(nameof(files));

    public override string HandlerKind => Kind;

    protected override IEnumerable<EntryCandidate> Candidates(RegHive hive, string? userSid) =>
        RegistryInspector.Scan(Registry, _files, hive == RegHive.HKLM ? RegScope.Machine : RegScope.User, userSid)
            .Where(o => o.Removable)
            .Select(o => new EntryCandidate(o.Category, o.Key, o.ValueName, o.Selection));

    protected override bool IsAllowedLocation(RegHive hive, string path, string? valueName) =>
        RegistryInspector.AllowedParents.Any(p => p.Hive == hive && string.Equals(p.Path, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Handler "SecurityPersistenceValue": remove valores de persistência suspeitos SELECIONADOS: <c>Debugger</c> em Image File
/// Execution Options (HKLM, 64 e 32 bits) e <c>Shell</c> no Winlogon do perfil do usuário (HKCU). Guarda tipo e dados.
/// </summary>
public sealed class PersistenceValueHandler : RegistryEntrySetHandler
{
    public const string Kind = "SecurityPersistenceValue";
    public const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    public const string WinlogonPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string IfeoCategory = "IFEO Debugger";
    public const string UserShellCategory = "Shell do usuário";

    public PersistenceValueHandler(ISecurityRegistry registry, string dataDirectory, bool otherAccount) : base(registry, dataDirectory, otherAccount) { }

    public override string HandlerKind => Kind;

    /// <summary>Entradas IFEO com Debugger (leitura também usada pela página).</summary>
    public static IEnumerable<(EntryCandidate Candidate, string Image, string Debugger)> IfeoDebuggers(ISecurityRegistry reg)
    {
        ArgumentNullException.ThrowIfNull(reg);
        foreach (var view in new[] { RegView.Registry64, RegView.Registry32 })
        {
            var parent = new RegKeyRef(RegHive.HKLM, IfeoPath, view);
            foreach (var image in reg.SubKeyNames(parent).Take(2000))
            {
                var key = parent.Child(image);
                if (reg.Value(key, "Debugger") is not { } v || string.IsNullOrWhiteSpace(v.Data)) continue;
                yield return (new EntryCandidate(IfeoCategory, key, "Debugger", RegistryInspector.SelectionId(IfeoCategory, RegHive.HKLM, view, key.Path, "Debugger")), image, v.Data);
            }
        }
    }

    public static EntryCandidate? UserShell(ISecurityRegistry reg, string? userSid)
    {
        ArgumentNullException.ThrowIfNull(reg);
        var key = new RegKeyRef(RegHive.HKCU, WinlogonPath, RegView.Default, userSid);
        return reg.Value(key, "Shell") is { Data.Length: > 0 }
            ? new EntryCandidate(UserShellCategory, key, "Shell", RegistryInspector.SelectionId(UserShellCategory, RegHive.HKCU, RegView.Default, key.Path, "Shell"))
            : null;
    }

    protected override IEnumerable<EntryCandidate> Candidates(RegHive hive, string? userSid)
    {
        if (hive == RegHive.HKLM)
        {
            // Mesma chave nas duas visões quando não há redirecionamento: fica uma.
            return IfeoDebuggers(Registry).Select(x => x.Candidate).GroupBy(c => c.Selection).Select(g => g.First());
        }
        return UserShell(Registry, userSid) is { } c ? [c] : [];
    }

    protected override bool IsAllowedLocation(RegHive hive, string path, string? valueName)
    {
        if (hive == RegHive.HKLM)
            return valueName == "Debugger" && string.Equals(Parent(path), IfeoPath, StringComparison.OrdinalIgnoreCase);
        return valueName == "Shell" && string.Equals(path, WinlogonPath, StringComparison.OrdinalIgnoreCase);
    }
}
