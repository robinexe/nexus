using System.Security.Cryptography;
using System.Text;
using Nexus.WindowsActions.Registry;

namespace Nexus.Security;

/// <summary>Consulta ao sistema de arquivos usada pelas regras (falsa nos testes).</summary>
public interface IFileProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    /// <summary>Expande %VARIÁVEIS%. O aplicador como outra conta não deve expandir variáveis de perfil (ver <see cref="SystemFileProbe"/>).</summary>
    string Expand(string path);
}

/// <summary>Sistema de arquivos real. Não segue nada: só pergunta se existe.</summary>
public sealed class SystemFileProbe : IFileProbe
{
    private readonly bool _expandUserVariables;

    /// <param name="expandUserVariables">false quando o processo roda com outra conta (variáveis de perfil seriam as dela).</param>
    public SystemFileProbe(bool expandUserVariables = true) => _expandUserVariables = expandUserVariables;

    private static readonly string[] UserVariables = ["%USERPROFILE%", "%APPDATA%", "%LOCALAPPDATA%", "%TEMP%", "%TMP%", "%HOMEPATH%", "%ONEDRIVE%"];

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_expandUserVariables && UserVariables.Any(v => path.Contains(v, StringComparison.OrdinalIgnoreCase))) return path; // fica não-enraizado → regra ignora
        return Environment.ExpandEnvironmentVariables(path);
    }
}

public enum RegScope { Machine, User }

/// <summary>Entrada de registro órfã (ou só informativa). <see cref="ValueName"/> null = a chave inteira.</summary>
public sealed record RegistryOrphan(
    string Category,
    RegScope Scope,
    RegKeyRef Key,
    string? ValueName,
    string Display,
    string Reason,
    string Selection,
    bool Removable);

/// <summary>
/// INSPETOR de registro (não "limpador"). Encontra sobras que apontam para arquivos ou pastas que não existem mais.
/// Remover essas sobras NÃO deixa o PC mais rápido: o registro é um banco de dados indexado, e entradas a mais não pesam
/// (a Microsoft não dá suporte a "registry cleaners": https://support.microsoft.com/help/2563254). Serve para arrumar a lista
/// de Programas e Recursos, atalhos de "Executar" quebrados e confusão em diagnósticos.
/// Regras conservadoras: na dúvida, a entrada não é órfã. Disco ausente (pendrive/HD externo desligado) nunca conta como órfã.
/// </summary>
public static class RegistryInspector
{
    public const string Uninstall = "Desinstalação";
    public const string AppPaths = "Caminho de aplicativo";
    public const string MuiCache = "Cache de nomes (MUICache)";
    public const string SharedDlls = "DLL compartilhada";
    public const string FileExtension = "Extensão de arquivo";

    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string AppPathsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    public const string MuiCachePath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    public const string SharedDllsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
    public const string UserClassesPath = @"Software\Classes";

    public const int MaxPerCategory = 500;

    /// <summary>Raízes (pai das entradas) que os handlers aceitam remover/recriar. Qualquer outra é recusada.</summary>
    public static readonly IReadOnlyList<(RegHive Hive, string Path)> AllowedParents =
    [
        (RegHive.HKLM, UninstallPath), (RegHive.HKCU, UninstallPath),
        (RegHive.HKLM, AppPathsPath), (RegHive.HKCU, AppPathsPath),
        (RegHive.HKCU, MuiCachePath), (RegHive.HKLM, SharedDllsPath),
    ];

    /// <summary>Identificador curto e estável de uma entrada (8 hex). Não inclui o SID: interface e aplicador calculam igual.</summary>
    public static string SelectionId(string category, RegHive hive, RegView view, string path, string? valueName) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{category}|{hive}|{view}|{path.TrimEnd('\\')}|{valueName}".ToLowerInvariant())))[..8];

    private static RegView[] MachineViews => [RegView.Registry64, RegView.Registry32];

    public static IReadOnlyList<RegistryOrphan> Scan(ISecurityRegistry reg, IFileProbe files, RegScope scope, string? userSid = null)
    {
        ArgumentNullException.ThrowIfNull(reg);
        ArgumentNullException.ThrowIfNull(files);
        var list = new List<RegistryOrphan>();
        if (scope == RegScope.Machine)
        {
            foreach (var view in MachineViews) list.AddRange(ScanUninstall(reg, files, new RegKeyRef(RegHive.HKLM, UninstallPath, view), scope));
            foreach (var view in MachineViews) list.AddRange(ScanAppPaths(reg, files, new RegKeyRef(RegHive.HKLM, AppPathsPath, view), scope));
            foreach (var view in MachineViews) list.AddRange(ScanPathValues(reg, files, new RegKeyRef(RegHive.HKLM, SharedDllsPath, view), SharedDlls, scope, n => n));
        }
        else
        {
            list.AddRange(ScanUninstall(reg, files, new RegKeyRef(RegHive.HKCU, UninstallPath, RegView.Default, userSid), scope));
            list.AddRange(ScanAppPaths(reg, files, new RegKeyRef(RegHive.HKCU, AppPathsPath, RegView.Default, userSid), scope));
            list.AddRange(ScanPathValues(reg, files, new RegKeyRef(RegHive.HKCU, MuiCachePath, RegView.Default, userSid), MuiCache, scope, MuiCacheTarget));
            list.AddRange(ScanExtensions(reg, new RegKeyRef(RegHive.HKCU, UserClassesPath, RegView.Default, userSid)));
        }
        // A mesma chave pode aparecer nas duas visões quando não há redirecionamento: fica a primeira.
        return list.GroupBy(o => (o.Category, o.Selection)).Select(g => g.First()).ToList();
    }

    /// <summary>Caminho de arquivo absoluto (X:\...) com o disco presente. Null = não dá para julgar (não é órfã).</summary>
    private static string? Judgeable(IFileProbe files, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var p = files.Expand(raw.Trim().Trim('"')).Trim();
        if (p.Length < 4 || p[1] != ':' || p[2] != '\\' || !char.IsAsciiLetter(p[0])) return null;
        if (p.IndexOfAny(['%', '*', '?', '<', '>', '|']) >= 0) return null;
        return files.DirectoryExists(p[..3]) ? p : null;
    }

    private static IEnumerable<RegistryOrphan> ScanUninstall(ISecurityRegistry reg, IFileProbe files, RegKeyRef parent, RegScope scope)
    {
        var count = 0;
        foreach (var name in reg.SubKeyNames(parent))
        {
            if (count >= MaxPerCategory) yield break;
            var key = parent.Child(name);
            string? V(string n) => reg.Value(key, n)?.Data;
            if (V("SystemComponent") == "1" || V("WindowsInstaller") == "1" || V("ParentKeyName") is not null || V("ReleaseType") is not null) continue;
            var uninstall = V("UninstallString");
            if (string.IsNullOrWhiteSpace(uninstall) || uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue;
            var exe = Judgeable(files, CommandHeuristics.ExecutablePath(uninstall));
            if (exe is null || files.FileExists(exe)) continue;
            var location = V("InstallLocation");
            if (!string.IsNullOrWhiteSpace(location))
            {
                var loc = Judgeable(files, location);
                if (loc is null || files.DirectoryExists(loc)) continue;
            }
            count++;
            var display = V("DisplayName") ?? name;
            yield return new RegistryOrphan(Uninstall, scope, key, null, display,
                $"O desinstalador não existe mais ({Text.Clip(exe, 120)}). O programa aparece em Programas e Recursos, mas não pode ser removido por lá.",
                SelectionId(Uninstall, parent.Hive, parent.View, key.Path, null), true);
        }
    }

    private static IEnumerable<RegistryOrphan> ScanAppPaths(ISecurityRegistry reg, IFileProbe files, RegKeyRef parent, RegScope scope)
    {
        var count = 0;
        foreach (var name in reg.SubKeyNames(parent))
        {
            if (count >= MaxPerCategory) yield break;
            var key = parent.Child(name);
            var target = Judgeable(files, reg.Value(key, "")?.Data);
            if (target is null || files.FileExists(target)) continue;
            count++;
            yield return new RegistryOrphan(AppPaths, scope, key, null, name,
                $"\"{name}\" no Executar aponta para um arquivo que não existe ({Text.Clip(target, 120)}).",
                SelectionId(AppPaths, parent.Hive, parent.View, key.Path, null), true);
        }
    }

    /// <summary>Valores cujo NOME é um caminho de arquivo (SharedDLLs, MUICache).</summary>
    private static IEnumerable<RegistryOrphan> ScanPathValues(ISecurityRegistry reg, IFileProbe files, RegKeyRef key, string category, RegScope scope, Func<string, string?> pathOf)
    {
        var count = 0;
        foreach (var v in reg.Values(key))
        {
            if (count >= MaxPerCategory) yield break;
            var target = Judgeable(files, pathOf(v.Name));
            if (target is null || files.FileExists(target)) continue;
            count++;
            yield return new RegistryOrphan(category, scope, key, v.Name, Text.Clip(v.Name, 160),
                category == SharedDlls
                    ? "Contador de DLL compartilhada para um arquivo que não existe mais."
                    : "Nome em cache de um programa que não existe mais. O Windows recria se o programa voltar.",
                SelectionId(category, key.Hive, key.View, key.Path, v.Name), true);
        }
    }

    /// <summary>MUICache: "C:\...\app.exe.FriendlyAppName" → "C:\...\app.exe". Recursos ("@shell32.dll,-1") não são caminhos.</summary>
    public static string? MuiCacheTarget(string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        if (valueName.StartsWith('@') || valueName.Equals("LangID", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var suffix in new[] { ".FriendlyAppName", ".ApplicationCompany" })
            if (valueName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return valueName[..^suffix.Length];
        return null;
    }

    /// <summary>Extensões do usuário cujo programa (ProgID) não existe. Só informativo: o Windows reconstrói associações.</summary>
    private static IEnumerable<RegistryOrphan> ScanExtensions(ISecurityRegistry reg, RegKeyRef classes)
    {
        var count = 0;
        foreach (var name in reg.SubKeyNames(classes))
        {
            if (!name.StartsWith('.') || name.Length > 32) continue;
            if (count >= 50) yield break;
            var progId = reg.Value(classes.Child(name), "")?.Data;
            if (string.IsNullOrWhiteSpace(progId) || progId.Contains('\\', StringComparison.Ordinal)) continue;
            if (reg.KeyExists(classes.Child(progId)) || reg.KeyExists(new RegKeyRef(RegHive.HKLM, @"SOFTWARE\Classes\" + progId, RegView.Default))) continue;
            count++;
            yield return new RegistryOrphan(FileExtension, RegScope.User, classes.Child(name), null, name,
                $"A extensão {name} aponta para \"{Text.Clip(progId, 60)}\", que não existe. Só informativo: o Windows pergunta com qual programa abrir.",
                SelectionId(FileExtension, RegHive.HKCU, RegView.Default, classes.Child(name).Path, null), false);
        }
    }
}
