using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nexus.Core;

namespace Nexus.Security.Scanner;

/// <summary>Pastas que a política da quarentena precisa conhecer (no aplicador; testes usam pastas temporárias).</summary>
public interface IQuarantineLocations
{
    /// <summary>Pasta do Windows: nunca é alvo.</summary>
    string? WindowsDirectory { get; }

    /// <summary>Pastas de todos os usuários (Arquivos de Programas, ProgramData): só com a vaga de administrador.</summary>
    IReadOnlyList<string> AdminOnlyRoots { get; }

    /// <summary>Pasta que contém os perfis (C:\Users). Dentro dela, a vaga de usuário só alcança o próprio perfil.</summary>
    string? UsersRoot { get; }

    /// <summary>Perfil do usuário (pelo SID, ou do próprio processo quando não há SID). null = indeterminado.</summary>
    string? UserProfile(string? userSid);
}

/// <summary>Política pura: o que pode ir para a quarentena em cada vaga. null = permitido; senão o motivo.</summary>
public static class QuarantinePolicy
{
    public static string? Check(QuarantineTarget t, IQuarantineLocations locations, string dataDirectory, bool otherAccount)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(locations);
        var p = t.Path;
        if (string.IsNullOrWhiteSpace(p) || p.Length > 260 || !Path.IsPathFullyQualified(p)) return "Caminho inválido (precisa ser absoluto, até 260 caracteres).";
        if (p.IndexOfAny(['"', '\0', '\n', '\r', '*', '?', '<', '>', '|']) >= 0) return "Caminho com caractere proibido.";
        if (OperatingSystem.IsWindows() && p.LastIndexOf(':') > 1) return "Fluxos alternativos (arquivo:fluxo) não são aceitos.";
        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Caminho inválido."; }
        if (!string.Equals(full, p, StringComparison.Ordinal) || p.Split('\\', '/').Any(s => s is "." or ".."))
            return "Caminho não normalizado (.., pontos ou espaços no fim): recusado.";
        if (Under(p, locations.WindowsDirectory)) return "Arquivos da pasta do Windows nunca vão para a quarentena do NEXUS: peça ao Microsoft Defender.";
        if (Under(p, dataDirectory)) return "A pasta de dados do NEXUS nunca é alvo.";
        if (!t.Machine)
        {
            if (locations.AdminOnlyRoots.Any(r => Under(p, r)))
                return "Arquivo de Arquivos de Programas/ProgramData: isolar exige o aplicador como administrador (vaga de administrador).";
            if (otherAccount && t.UserSid is null)
                return "O aplicador roda com outra conta e não recebeu o perfil do usuário: o alvo não pode ser conferido com segurança.";
            if (Under(p, locations.UsersRoot))
            {
                var profile = locations.UserProfile(t.UserSid);
                if (profile is null) return "Perfil do usuário indeterminado.";
                if (!Under(p, profile)) return "O arquivo está no perfil de outra conta: isolar exige administrador.";
            }
        }
        if (IsOrUnderLink(p)) return "O alvo é (ou está dentro de) um link/junção: o NEXUS nunca segue links.";
        if (Directory.Exists(p)) return "O alvo é uma pasta; a quarentena do verificador só move arquivos.";
        return null;
    }

    public static bool Under(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(r, cmp) || path.StartsWith(r + Path.DirectorySeparatorChar, cmp) || path.StartsWith(r + Path.AltDirectorySeparatorChar, cmp);
    }

    /// <summary>O próprio caminho ou alguma pasta existente acima dele é link/junção.</summary>
    public static bool IsOrUnderLink(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        return false;
    }
}

/// <summary>
/// Handler <c>ScannerQuarantine</c>: move UM arquivo apontado pelo verificador para a quarentena do NEXUS, de forma reversível.
/// Alvo (do catálogo): <c>scope</c> = HKCU (vaga de usuário) | HKLM (vaga de administrador), <c>path</c> e <c>sha256</c>
/// (parâmetros do usuário, já validados por regex pelo broker) e, com o aplicador como serviço, <c>userSid</c>.
/// O handler REVALIDA tudo: política de pastas, links, e que o SHA-256 ainda é o que a verificação viu (antes e depois de
/// mover). Estados: <c>present</c> (no lugar, mesmo hash), <c>changed</c> (no lugar, outro conteúdo: recusa),
/// <c>quarantined</c> (guardado pelo NEXUS) ou ausente. Nunca apaga.
/// </summary>
public sealed partial class ScannerQuarantineHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "ScannerQuarantine";
    public const string StateType = "ScannerQuarantine";
    public const string Present = "present";
    public const string Changed = "changed";
    public const string Quarantined = "quarantined";

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private readonly IQuarantineLocations _locations;
    private readonly string _dataDirectory;
    private readonly bool _elevated;
    private readonly bool _otherAccount;
    private readonly Func<string, string?>? _machineManifestTrust;

    /// <param name="machineManifestTrust">Windows: confere que o manifesto de uma vaga de administrador só pode ter sido escrito
    /// por administradores (<c>AdminOwnedFile.Check</c>).</param>
    public ScannerQuarantineHandler(IQuarantineLocations locations, string dataDirectory, bool elevated, bool otherAccount,
        Func<string, string?>? machineManifestTrust = null, bool forceCrossVolume = false)
    {
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        Store = new ScannerQuarantineStore(dataDirectory) { ForceCrossVolume = forceCrossVolume };
        _elevated = elevated;
        _otherAccount = otherAccount;
        _machineManifestTrust = machineManifestTrust;
    }

    public string HandlerKind => Kind;
    public ScannerQuarantineStore Store { get; }
    public static StateSnapshot DesiredState => StateSnapshot.Of(StateType, Quarantined);

    public static QuarantineTarget? TargetOf(ActionInstance action, out string? error)
    {
        ArgumentNullException.ThrowIfNull(action);
        error = null;
        var t = action.Target;
        if (!t.TryGetValue("scope", out var scope) || scope is not ("HKCU" or "HKLM")) { error = "Alvo sem 'scope' válido (HKCU|HKLM)."; return null; }
        if (!t.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path) || path.Length > 260) { error = "Alvo sem caminho válido."; return null; }
        if (!t.TryGetValue("sha256", out var sha) || !Sha256Pattern().IsMatch(sha)) { error = "Alvo sem SHA-256 válido."; return null; }
        t.TryGetValue(UserProfileScope.TargetKey, out var sid);
        if (sid is not null && (scope != "HKCU" || !UserProfileScope.IsValidSid(sid))) { error = "Perfil de usuário inválido no alvo."; return null; }
        var extra = t.Keys.Except(["scope", "path", "sha256", UserProfileScope.TargetKey], StringComparer.Ordinal).ToList();
        if (extra.Count > 0) { error = $"Campo inesperado no alvo: {extra[0]}."; return null; }
        return new QuarantineTarget(scope == "HKLM", path, sha, sid);
    }

    public string? Validate(QuarantineTarget t) => QuarantinePolicy.Check(t, _locations, _dataDirectory, _otherAccount);

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        var t = TargetOf(action, out var error);
        if (t is null) return Applicability.Blocked(error!);
        if (t.Machine && !_elevated) return Applicability.Blocked("Vaga de administrador: requer o aplicador como administrador.");
        if (!action.Desired.SameValueAs(DesiredState)) return Applicability.Blocked("Estado desejado inválido para ScannerQuarantine.");
        var now = Detect(action);
        if (now.SameValueAs(DesiredState)) return Applicability.Ok; // já isolado: o planejador marca "já configurado"
        if (Validate(t) is { } problem) return Applicability.Blocked(problem);
        if (now.Exists && now.Value == Changed) return Applicability.Blocked("O arquivo mudou desde a verificação (SHA-256 diferente). Verifique de novo.");
        if (!now.Exists) return Applicability.NotApplicable("O arquivo já não existe.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var t = TargetOf(action, out var error) ?? throw new InvalidOperationException(error);
        var detail = new Dictionary<string, string> { ["quarantine"] = ScannerQuarantineStore.IdOf(t) };
        // A quarentena concluída tem prioridade: um arquivo novo no mesmo caminho não apaga o fato de que o NEXUS guardou o original.
        if (Store.PayloadExists(t) && SafeManifest(t) is { State: "done" })
            return StateSnapshot.Of(StateType, Quarantined, ValueOrigin.Explicit, detail);
        if (File.Exists(t.Path))
        {
            var info = new FileInfo(t.Path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > ScannerQuarantineStore.MaxBytes)
                return StateSnapshot.Of(StateType, Changed, ValueOrigin.Explicit, detail);
            string sha;
            try { sha = ScannerQuarantineStore.HashFile(t.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return StateSnapshot.Of(StateType, Changed, ValueOrigin.Explicit, detail); }
            return StateSnapshot.Of(StateType, sha == t.Sha256 ? Present : Changed, ValueOrigin.Explicit, detail);
        }
        if (Directory.Exists(t.Path)) return StateSnapshot.Of(StateType, Changed, ValueOrigin.Explicit, detail);
        return StateSnapshot.Absent(detail);
    }

    private ScannerQuarantineManifest? SafeManifest(QuarantineTarget t)
    {
        try { return Store.ReadManifest(t); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    public void Apply(ActionInstance action)
    {
        var t = TargetOf(action, out var error) ?? throw new InvalidOperationException(error);
        if (t.Machine && !_elevated) throw new InvalidOperationException("Vaga de administrador sem o aplicador elevado.");
        if (Validate(t) is { } problem) throw new InvalidOperationException(problem);
        try
        {
            Store.Quarantine(t, () => Validate(t));
        }
        catch when (File.Exists(t.Path))
        {
            // Nada saiu do lugar (em uso, hash diferente, link...): descarta o manifesto pendente.
            if (!Store.PayloadExists(t)) Store.Discard(t);
            throw;
        }
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var t = TargetOf(action, out var error) ?? throw new InvalidOperationException(error);
        if (!original.Exists) return; // não existia antes: nada a devolver
        var m = Store.ReadManifest(t) ?? throw new InvalidOperationException("Manifesto da quarentena não encontrado.");
        if (Validate(t) is { } problem) throw new InvalidOperationException(problem + " Nada foi devolvido.");
        Store.Restore(t, m, () => Validate(t));
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var t = TargetOf(action, out var error);
        if (t is null) return error;
        if (!original.Exists) return null;
        if (!original.SameValueAs(StateSnapshot.Of(StateType, Present))) return $"Estado original '{original.Describe()}' não restaurável.";
        if (t.Machine && !_elevated) return "Vaga de administrador: desfazer exige o aplicador como administrador.";
        if (Validate(t) is { } problem) return problem;
        ScannerQuarantineManifest? m;
        try { m = Store.ReadManifest(t); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return $"Manifesto ilegível: {ex.Message}"; }
        if (m is null) return "Manifesto da quarentena ausente ou de outro arquivo.";
        if (t.Machine && _machineManifestTrust?.Invoke(Store.ManifestPath(t)) is { } untrusted) return $"Manifesto não confiável: {untrusted}";
        return Store.VerifyPayload(t, m);
    }
}

/// <summary>Pastas reais do Windows. Perfil pelo SID (ProfileList) ou do próprio processo (aplicador na conta de quem pede).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsQuarantineLocations(bool otherAccount) : IQuarantineLocations
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public string? WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public IReadOnlyList<string> AdminOnlyRoots =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
            .Select(Environment.GetFolderPath).Where(p => p.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public string? UsersRoot
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ProfileList);
                if (key?.GetValue("ProfilesDirectory") is string dir) return Environment.ExpandEnvironmentVariables(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            return null;
        }
    }

    public string? UserProfile(string? userSid)
    {
        if (userSid is not null)
        {
            if (!UserProfileScope.IsValidSid(userSid)) return null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{ProfileList}\{userSid}");
                var path = key?.GetValue("ProfileImagePath") as string;
                return string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        }
        return otherAccount ? null : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
