using System.Runtime.Versioning;
using Nexus.Core;
using Nexus.Programs.Inventory;
using Nexus.WindowsActions.Registry;

namespace Nexus.Programs.Leftovers;

/// <summary>Raízes permitidas no aplicador. <see cref="User"/> retorna null quando o perfil não pode ser determinado com segurança.</summary>
public interface ILeftoverRootsProvider
{
    IReadOnlyList<string> Machine();
    IReadOnlyList<string>? User(string? userSid);
}

/// <summary>
/// Handler <c>ProgramLeftover</c>: remove UMA sobra de programa escolhida pelo usuário, de forma reversível.
/// Alvo (do catálogo): <c>kind</c> = file|registry, <c>scope</c> = HKCU (perfil do usuário) | HKLM (máquina), <c>path</c>
/// (parâmetro do usuário, já validado por regex pelo broker) e, com o aplicador como serviço, <c>userSid</c>.
/// <list type="bullet">
/// <item>Pasta/arquivo → quarentena em <c>DataDirectory\quarantine\programs</c> com manifesto (tamanho + SHA-256 de cada arquivo).</item>
/// <item>Chave → árvore completa (valores com tipos) exportada para o manifesto, depois removida. Restore recria.</item>
/// </list>
/// O handler REVALIDA tudo (independente do regex): raízes permitidas, nomes protegidos, programas ainda instalados, links.
/// Estados: <c>present</c> (existe no lugar), <c>quarantined</c> (fora do lugar, guardado pelo NEXUS) ou ausente.
/// </summary>
public sealed class ProgramLeftoverHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "ProgramLeftover";
    public const string StateType = "ProgramLeftover";
    public const string Present = "present";
    public const string Quarantined = "quarantined";

    private static readonly HashSet<string> RestorableKinds = new(StringComparer.OrdinalIgnoreCase)
        { "String", "ExpandString", "DWord", "QWord", "Binary", "MultiString", "None" };

    private readonly IRegistryTreeBackend _registry;
    private readonly ILeftoverRootsProvider _roots;
    private readonly Func<string?, IReadOnlyList<InstalledProgram>> _installed;
    private readonly LeftoverQuarantine _quarantine;
    private readonly string _dataDirectory;
    private readonly bool _elevated;
    private readonly bool _otherAccount;
    private readonly Func<string, string?>? _machineManifestTrust;

    /// <param name="installed">Programas ainda instalados (por SID do usuário, quando houver). Nada que pertença a eles é removido.</param>
    /// <param name="machineManifestTrust">Windows: confere que o manifesto de uma sobra da máquina só pode ter sido escrito por
    /// administradores (<c>AdminOwnedFile.Check</c>); impede que um usuário comum troque o conteúdo que volta para Program Files.</param>
    public ProgramLeftoverHandler(IRegistryTreeBackend registry, ILeftoverRootsProvider roots, Func<string?, IReadOnlyList<InstalledProgram>> installed,
        string dataDirectory, bool elevated, bool otherAccount, Func<string, string?>? machineManifestTrust = null, bool forceCrossVolume = false)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _installed = installed ?? throw new ArgumentNullException(nameof(installed));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _quarantine = new LeftoverQuarantine(dataDirectory) { ForceCrossVolume = forceCrossVolume };
        _elevated = elevated;
        _otherAccount = otherAccount;
        _machineManifestTrust = machineManifestTrust;
    }

    public string HandlerKind => Kind;
    public LeftoverQuarantine Quarantine => _quarantine;

    public static StateSnapshot DesiredState => StateSnapshot.Of(StateType, Quarantined);

    // ------------------------------------------------------------------ alvo

    public static LeftoverTarget? TargetOf(ActionInstance action, out string? error)
    {
        ArgumentNullException.ThrowIfNull(action);
        error = null;
        var t = action.Target;
        if (!t.TryGetValue("kind", out var kind) || kind is not ("file" or "registry")) { error = "Alvo sem 'kind' válido (file|registry)."; return null; }
        if (!t.TryGetValue("scope", out var scope) || scope is not ("HKCU" or "HKLM")) { error = "Alvo sem 'scope' válido (HKCU|HKLM)."; return null; }
        if (!t.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path) || path.Length > 260) { error = "Alvo sem caminho válido."; return null; }
        t.TryGetValue(UserHive.TargetKey, out var sid);
        if (sid is not null && (scope != "HKCU" || !UserHive.IsValidSid(sid))) { error = "Perfil de usuário inválido no alvo."; return null; }
        var extra = t.Keys.Except(["kind", "scope", "path", UserHive.TargetKey], StringComparer.Ordinal).ToList();
        if (extra.Count > 0) { error = $"Campo inesperado no alvo: {extra[0]}."; return null; }
        return new LeftoverTarget(kind == "registry", scope == "HKLM", path, sid);
    }

    /// <summary>Revalida o alvo inteiro (sem escrever). null = permitido.</summary>
    public string? Validate(LeftoverTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!t.Machine && t.UserSid is null && _otherAccount)
            return "O aplicador roda com outra conta e não recebeu o perfil do usuário: a sobra do perfil não pode ser localizada com segurança.";
        IReadOnlyList<InstalledProgram> installed;
        try { installed = _installed(t.UserSid); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return $"Não foi possível ler os programas instalados ({ex.Message}); nada é removido sem essa conferência."; }

        if (t.IsRegistry)
            return LeftoverPolicy.CheckRegistryKey(t.Scope, t.Path, installed);

        var roots = t.Machine ? _roots.Machine() : _roots.User(t.UserSid);
        if (roots is null || roots.Count == 0) return "Pastas do perfil do usuário indeterminadas.";
        if (LeftoverPolicy.Overlaps(t.Path, _dataDirectory)) return "A pasta de dados do NEXUS nunca é alvo.";
        if (LeftoverPolicy.CheckFolder(t.Path, roots, installed) is { } problem) return problem;
        if ((File.Exists(t.Path) || Directory.Exists(t.Path)) && LeftoverScanner.IsLinkOrUnderLink(t.Path, roots))
            return "O alvo é (ou está dentro de) um link/junção: o NEXUS nunca segue links.";
        return null;
    }

    // ------------------------------------------------------------------ ciclo

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        var t = TargetOf(action, out var error);
        if (t is null) return Applicability.Blocked(error!);
        if (Validate(t) is { } problem) return Applicability.Blocked(problem);
        if (t.Machine && !_elevated) return Applicability.Blocked("Sobra de todos os usuários: requer o aplicador como administrador.");
        if (!action.Desired.SameValueAs(DesiredState)) return Applicability.Blocked("Estado desejado inválido para ProgramLeftover.");
        var now = Detect(action);
        if (!now.Exists) return Applicability.NotApplicable("A sobra já não existe.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var t = TargetOf(action, out var error) ?? throw new InvalidOperationException(error);
        var present = t.IsRegistry
            ? _registry.KeyExists(t.Machine ? RegHive.HKLM : RegHive.HKCU, t.Path, t.UserSid)
            : File.Exists(t.Path) || Directory.Exists(t.Path);
        var detail = new Dictionary<string, string> { ["quarantine"] = LeftoverQuarantine.IdOf(t) };
        if (present) return StateSnapshot.Of(StateType, Present, ValueOrigin.Explicit, detail);
        var m = _quarantine.ReadManifest(t);
        if (m is not null && (t.IsRegistry ? m.Registry is not null : _quarantine.PayloadExists(t)))
            return StateSnapshot.Of(StateType, Quarantined, ValueOrigin.Explicit, detail);
        return StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action)
    {
        var t = TargetOf(action, out var error) ?? throw new InvalidOperationException(error);
        if (Validate(t) is { } problem) throw new InvalidOperationException(problem);
        if (t.IsRegistry) ApplyRegistry(t);
        else ApplyFiles(t);
    }

    private void ApplyFiles(LeftoverTarget t)
    {
        try
        {
            _quarantine.QuarantinePath(t);
        }
        catch when (File.Exists(t.Path) || Directory.Exists(t.Path))
        {
            // Nada saiu do lugar (ex.: arquivo em uso, link dentro, grande demais): descarta o manifesto pendente.
            if (!_quarantine.PayloadExists(t)) _quarantine.Discard(t);
            throw;
        }
    }

    private void ApplyRegistry(LeftoverTarget t)
    {
        var hive = t.Machine ? RegHive.HKLM : RegHive.HKCU;
        var tree = _registry.Export(hive, t.Path, t.UserSid) ?? throw new InvalidOperationException("A chave já não existe.");
        if (tree.CountKeys() > RegistryTreeLimits.MaxKeys || tree.CountValues() > RegistryTreeLimits.MaxValues)
            throw new InvalidOperationException("Chave grande demais para ser sobra de um programa; nada foi removido.");
        if (_quarantine.ReadManifest(t) is { State: "done" } && !_quarantine.PayloadExists(t) && _registry.KeyExists(hive, t.Path, t.UserSid))
            _quarantine.Discard(t); // manifesto antigo de uma chave que voltou a existir (programa reinstalado)
        var manifest = new LeftoverManifest(LeftoverManifest.CurrentFormat, t.Kind, t.Scope, t.UserSid, t.Path, DateTimeOffset.UtcNow, false, 0, null, tree, "pending");
        _quarantine.WriteManifest(t, manifest); // backup completo ANTES de remover
        _registry.DeleteTree(hive, t.Path, t.UserSid);
        if (_registry.KeyExists(hive, t.Path, t.UserSid))
            throw new InvalidOperationException("A chave continua existindo depois da remoção (sem permissão em alguma subchave?).");
        _quarantine.WriteManifest(t, manifest with { State = "done" });
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var t = TargetOf(action, out var error) ?? throw new InvalidOperationException(error);
        if (!original.Exists) return; // não existia antes: nada a devolver
        var m = _quarantine.ReadManifest(t) ?? throw new InvalidOperationException("Manifesto da quarentena não encontrado.");
        if (t.IsRegistry)
        {
            var hive = t.Machine ? RegHive.HKLM : RegHive.HKCU;
            _registry.Import(hive, t.Path, m.Registry ?? throw new InvalidOperationException("Manifesto sem a árvore do registro."), t.UserSid);
            _quarantine.Discard(t);
        }
        else
        {
            _quarantine.RestorePath(t, m);
        }
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var t = TargetOf(action, out var error);
        if (t is null) return error;
        if (original.Exists && !original.SameValueAs(StateSnapshot.Of(StateType, Present)))
            return $"Estado original '{original.Describe()}' não restaurável.";
        if (!original.Exists) return null;
        // As mesmas regras do Apply valem para devolver (ex.: um programa reinstalado ocupa a pasta → recusa em vez de misturar).
        if (Validate(t) is { } problem) return problem;
        var manifestPath = _quarantine.ManifestPath(t);
        LeftoverManifest? m;
        try { m = _quarantine.ReadManifest(t); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return $"Manifesto ilegível: {ex.Message}"; }
        if (m is null) return "Manifesto da quarentena ausente ou de outro alvo.";
        if (t.Machine && _machineManifestTrust?.Invoke(manifestPath) is { } untrusted)
            return $"Manifesto não confiável: {untrusted}";
        if (t.IsRegistry)
        {
            if (m.Registry is null) return "Manifesto sem a árvore do registro.";
            if (!AllKindsRestorable(m.Registry)) return "Manifesto com tipo de valor não restaurável.";
            if (!NamesValid(m.Registry, root: true)) return "Manifesto com nome de subchave inválido.";
            return null;
        }
        return _quarantine.VerifyPayload(t, m);
    }

    private static bool AllKindsRestorable(RegKeyTree tree) =>
        tree.Values.All(v => RestorableKinds.Contains(v.Kind)) && tree.SubKeys.All(AllKindsRestorable);

    private static bool NamesValid(RegKeyTree tree, bool root) =>
        (root || (tree.Name.Length > 0 && !tree.Name.Contains('\\', StringComparison.Ordinal))) && tree.SubKeys.All(s => NamesValid(s, false));
}

/// <summary>Raízes reais: Program Files (64/32) e ProgramData; perfil pelo SID (ProfileList) ou pelas pastas do próprio processo.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLeftoverRoots(IRegistryTreeBackend registry, bool otherAccount) : ILeftoverRootsProvider
{
    public IReadOnlyList<string> Machine() => MachineRoots();

    public static IReadOnlyList<string> MachineRoots() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string>? User(string? userSid)
    {
        if (userSid is not null)
        {
            if (!UserHive.IsValidSid(userSid)) return null;
            var profile = registry.ReadMachineString($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{userSid}", "ProfileImagePath");
            if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile)) return null;
            var appData = Path.Combine(profile, "AppData");
            return [Path.Combine(appData, "Roaming"), Path.Combine(appData, "Local"), Path.Combine(appData, "LocalLow")];
        }
        return otherAccount ? null : CurrentUserRoots();
    }

    public static IReadOnlyList<string> CurrentUserRoots()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<string> { roaming, local };
        if (Path.GetDirectoryName(local) is { } parent) list.Add(Path.Combine(parent, "LocalLow"));
        return list.Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
