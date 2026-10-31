using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Nexus.Core;

namespace Nexus.SystemTools.Appx;

public interface IAppxBackend
{
    /// <summary>O pacote principal com esse nome para o usuário (userSid null = a conta do aplicador), ou null.</summary>
    AppxPackageInfo? Find(string name, string? userSid);

    void Remove(string packageFullName, string? userSid);

    /// <summary><c>Add-AppxPackage -Register manifesto -DisableDevelopmentMode</c> (sempre para a conta do aplicador).</summary>
    void Register(string manifestPath);

    bool ManifestExists(string manifestPath);
}

internal sealed record AppxBackup(string Name, string PackageFullName, string PackageFamilyName, string Version, string? InstallLocation, string? UserSid, DateTime CreatedUtc);

/// <summary>
/// Remove um app do Windows (Appx/MSIX) da lista segura, só para o usuário (nunca o pacote provisionado, que afeta contas novas).
/// Detect: "presente"/"removido". Apply: guarda nome, família e local do manifesto em DataDirectory\backup\appx e remove.
/// Restore (LIMITADO): reinstala do manifesto se o pacote ainda estiver no disco (provisionado ou instalado para outra conta);
/// senão falha com o link da Microsoft Store para reinstalar. A confirmação da página diz isso antes de remover.
/// </summary>
public sealed class AppxPackageHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "SystemAppxPackage";
    public const string StateType = "AppxPackage";

    private readonly IAppxBackend _appx;
    private readonly string _backupDir;
    private readonly bool _otherAccount;

    public AppxPackageHandler(IAppxBackend appx, string dataDirectory, bool otherAccount)
    {
        _appx = appx ?? throw new ArgumentNullException(nameof(appx));
        _backupDir = Path.Combine(dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)), "backup", "appx");
        _otherAccount = otherAccount;
    }

    public string HandlerKind => Kind;

    private static string NameOf(ActionInstance a) => a.Target.TryGetValue("packageName", out var n) ? n : "";

    private static string? SidOf(ActionInstance a) => a.Target.TryGetValue(UserProfileScope.TargetKey, out var s) ? s : null;

    private string BackupPath(string name, string? sid) => Path.Combine(_backupDir, sid ?? "conta-do-aplicador", name + ".json");

    private string? CheckTarget(ActionInstance action)
    {
        if (!AppxCatalog.IsValidName(NameOf(action))) return "Nome de pacote inválido.";
        var sid = SidOf(action);
        if (sid is not null && !UserProfileScope.IsValidSid(sid)) return "Perfil de usuário inválido.";
        if (_otherAccount && sid is null) return "O aplicador roda com outra conta e o pedido não identificou o usuário: nada é removido.";
        return null;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckTarget(action) is { } bad) return Applicability.Blocked(bad);
        if (action.Desired.Type != StateType || action.Desired.Value != PresenceState.Removed) return Applicability.Blocked("Valor desejado inválido.");
        var pkg = _appx.Find(NameOf(action), SidOf(action));
        if (pkg is null) return Applicability.NotApplicable($"{NameOf(action)} não está instalado para este usuário.");
        return AppxCatalog.CheckRemoval(pkg) is { } why ? Applicability.Blocked(why) : Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return PresenceState.Of(StateType, _appx.Find(NameOf(action), SidOf(action)) is not null);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckTarget(action) is { } bad) throw new InvalidOperationException(bad);
        var name = NameOf(action);
        var sid = SidOf(action);
        var pkg = _appx.Find(name, sid) ?? throw new InvalidOperationException($"{name} não está instalado.");
        if (AppxCatalog.CheckRemoval(pkg) is { } why) throw new InvalidOperationException(why);
        if (!AppxCatalog.IsValidFullName(pkg.PackageFullName)) throw new InvalidOperationException("Nome completo do pacote inesperado: nada foi removido.");
        var backup = new AppxBackup(pkg.Name, pkg.PackageFullName, pkg.PackageFamilyName, pkg.Version, pkg.InstallLocation, sid, DateTime.UtcNow);
        Text.WriteAtomic(BackupPath(name, sid), JsonSerializer.SerializeToUtf8Bytes(backup));
        _appx.Remove(pkg.PackageFullName, sid);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Value != PresenceState.Present) return;
        var name = NameOf(action);
        var sid = SidOf(action);
        var backup = ReadBackup(name, sid) ?? throw new InvalidOperationException($"Registro do app {name} não encontrado; reinstale pela Microsoft Store.");
        var store = AppxCatalog.IsValidFamilyName(backup.PackageFamilyName) ? " Reinstale pela Microsoft Store: " + AppxCatalog.StoreLink(backup.PackageFamilyName) : "";
        if (_otherAccount)
            throw new InvalidOperationException("O aplicador roda com outra conta: o Windows só reinstala o app na sessão do próprio usuário." + store);
        var loc = backup.InstallLocation ?? "";
        var manifest = Path.Combine(loc, "AppxManifest.xml");
        if (loc.Length == 0 || !IsWindowsAppsPath(loc) || !_appx.ManifestExists(manifest))
            throw new InvalidOperationException("Os arquivos do app não estão mais no PC; o NEXUS não consegue reinstalá-lo sozinho." + store);
        _appx.Register(manifest);
    }

    /// <summary>Só reinstala de pastas de apps do Windows (WindowsApps/SystemApps), nunca de um caminho qualquer do backup.</summary>
    internal static bool IsWindowsAppsPath(string path) =>
        !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\'', StringComparison.Ordinal) &&
        (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\SystemApps\", StringComparison.OrdinalIgnoreCase));

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (!AppxCatalog.IsValidName(NameOf(action))) return "Nome de pacote inválido.";
        if (SidOf(action) is { } sid && !UserProfileScope.IsValidSid(sid)) return "Perfil de usuário inválido.";
        if (original.Type != StateType || (original.Value != PresenceState.Present && original.Value != PresenceState.Removed)) return "Original inválido.";
        return null;
    }

    private AppxBackup? ReadBackup(string name, string? sid)
    {
        var path = BackupPath(name, sid);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AppxBackup>(File.ReadAllBytes(path)); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Appx pelo PowerShell do Windows (Get-AppxPackage/Remove-AppxPackage/Add-AppxPackage) em processo oculto.</summary>
[SupportedOSPlatform("windows")]
public sealed class PowerShellAppxBackend(IProcessRunner runner) : IAppxBackend
{
    private const string Select =
        "Select-Object Name,PackageFullName,PackageFamilyName,Publisher,@{n='Version';e={\"$($_.Version)\"}},InstallLocation,IsFramework,NonRemovable," +
        "@{n='SignatureKind';e={\"$($_.SignatureKind)\"}}";

    private static string? Self()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User?.Value;
    }

    private static string UserArg(string? sid) =>
        sid is not null && !string.Equals(sid, Self(), StringComparison.OrdinalIgnoreCase) ? " -User " + PowerShellRunner.Quote(sid) : "";

    public AppxPackageInfo? Find(string name, string? userSid)
    {
        if (!AppxCatalog.IsValidName(name)) throw new ArgumentException("Nome inválido.", nameof(name));
        var r = PowerShellRunner.Run(runner,
            $"$ErrorActionPreference='Stop';$p=Get-AppxPackage -Name {PowerShellRunner.Quote(name)}{UserArg(userSid)}|Where-Object {{-not $_.IsResourcePackage}}|{Select};" +
            "ConvertTo-Json -Compress -Depth 2 -InputObject @($p)", TimeSpan.FromSeconds(60));
        if (r.ExitCode != 0) throw new InvalidOperationException("Get-AppxPackage falhou: " + r.Error.Trim());
        return AppxParsers.Parse(r.Output).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && !p.IsFramework);
    }

    public IReadOnlyList<AppxPackageInfo> ListUser()
    {
        var r = PowerShellRunner.Run(runner,
            $"$ErrorActionPreference='Stop';$p=Get-AppxPackage|Where-Object {{-not $_.IsFramework -and -not $_.IsResourcePackage}}|{Select};" +
            "ConvertTo-Json -Compress -Depth 2 -InputObject @($p)", TimeSpan.FromSeconds(90));
        if (r.ExitCode != 0) throw new InvalidOperationException("Get-AppxPackage falhou: " + r.Error.Trim());
        return AppxParsers.Parse(r.Output);
    }

    /// <summary>Nomes dos pacotes provisionados (instalados em toda conta nova), ou null sem administrador.</summary>
    public IReadOnlyList<string>? ListProvisioned()
    {
        try
        {
            var r = PowerShellRunner.Run(runner,
                "$ErrorActionPreference='Stop';ConvertTo-Json -Compress -InputObject @(Get-AppxProvisionedPackage -Online|ForEach-Object {$_.DisplayName})",
                TimeSpan.FromSeconds(90));
            if (r.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(r.Output.Trim().Length == 0 ? "[]" : r.Output);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void Remove(string packageFullName, string? userSid)
    {
        if (!AppxCatalog.IsValidFullName(packageFullName)) throw new ArgumentException("Nome completo inválido.", nameof(packageFullName));
        var r = PowerShellRunner.Run(runner,
            $"$ErrorActionPreference='Stop';Remove-AppxPackage -Package {PowerShellRunner.Quote(packageFullName)}{UserArg(userSid)}", TimeSpan.FromMinutes(5));
        if (r.ExitCode != 0) throw new InvalidOperationException("Remove-AppxPackage falhou: " + r.Error.Trim());
    }

    public void Register(string manifestPath)
    {
        var r = PowerShellRunner.Run(runner,
            $"$ErrorActionPreference='Stop';Add-AppxPackage -Register {PowerShellRunner.Quote(manifestPath)} -DisableDevelopmentMode", TimeSpan.FromMinutes(5));
        if (r.ExitCode != 0) throw new InvalidOperationException("Add-AppxPackage falhou: " + r.Error.Trim());
    }

    public bool ManifestExists(string manifestPath) => File.Exists(manifestPath);
}
