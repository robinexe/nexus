using Nexus.Programs.Inventory;
using Nexus.WindowsActions;

namespace Nexus.Programs.Leftovers;

/// <summary>Raízes onde sobras podem estar. Máquina: Program Files (64/32) e ProgramData. Usuário: AppData Roaming/Local/LocalLow.</summary>
public sealed record LeftoverRoots(IReadOnlyList<string> Machine, IReadOnlyList<string> User)
{
    public static LeftoverRoots Empty { get; } = new([], []);
}

/// <summary>
/// Regras fortes, as MESMAS na varredura (o que é oferecido) e no handler do aplicador (o que pode ser removido):
/// nunca pastas compartilhadas do Windows/Microsoft/drivers, nunca a raiz, nunca algo que contenha ou esteja dentro da pasta de
/// um programa ainda instalado, nunca um nome que pertence a um programa ou editor ainda instalado, nunca links/junções.
/// </summary>
public static class LeftoverPolicy
{
    /// <summary>Nomes de primeiro nível que nunca são sobra de um programa (comparação sem diferenciar maiúsculas).</summary>
    public static readonly IReadOnlySet<string> ProtectedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft Corporation", "Microsoft Office", "Microsoft Office 15", "Microsoft Visual Studio", "Microsoft SDKs", "Microsoft SQL Server",
        "Microsoft.NET", "Microsoft Update Health Tools", "Microsoft OneDrive", "Microsoft Analysis Services", "MicrosoftEdgeUpdate",
        "Windows", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player", "Windows NT",
        "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsApps", "WindowsPowerShell", "Windows Kits",
        "ModifiableWindowsApps", "Common Files", "Internet Explorer", "Reference Assemblies", "MSBuild", "dotnet", "IIS", "IIS Express",
        "Package Cache", "Packages", "PackageManagement", "Programs", "Temp", "Tmp", "CrashDumps", "D3DSCache", "ConnectedDevicesPlatform", "Comms",
        "Application Data", "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "History", "Temporary Internet Files", "Publishers",
        "regid.1991-06.com.microsoft", "USOShared", "USOPrivate", "ssh", "SoftwareDistribution", "WinSxS", "System32", "SysWOW64", "Users", "Default",
        "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "ATI Technologies", "Intel", "Intel Corporation", "Realtek", "Synaptics", "Dolby",
        "NEXUS", "Nexus.Desktop", "Java", "Mozilla Maintenance Service", "VirtualStore", "fontconfig", "pip", "npm", "npm-cache", "nuget", "NuGet",
    };

    /// <summary>
    /// Pastas de editores que costumam guardar vários produtos (ou dados de jogos): a pasta em si nunca é removida, mas um
    /// programa dentro dela pode ser (ex.: <c>Google\Earth</c>, se nenhum outro produto Google estiver instalado).
    /// </summary>
    public static readonly IReadOnlySet<string> SharedVendorFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "Origin", "EA Desktop", "EA Games", "Electronic Arts", "Epic Games", "Riot Games", "Battle.net", "Blizzard Entertainment", "Ubisoft",
        "Ubisoft Game Launcher", "GOG.com", "GOG Galaxy", "Rockstar Games", "Google", "Apple", "Apple Computer", "Adobe", "Mozilla", "Oracle", "Sun",
        "JetBrains", "Autodesk", "Corsair", "Logitech", "Razer", "ASUS", "MSI", "Gigabyte", "Valve", "Python", "Git", "Unity", "Unity Technologies",
    };

    /// <summary>Nomes de chave sob <c>Software\</c> que nunca são removidos.</summary>
    public static readonly IReadOnlySet<string> ProtectedRegistryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "ODBC", "Windows", "WindowsUpdate", "Intel", "NVIDIA",
        "NVIDIA Corporation", "AMD", "ATI", "ATI Technologies", "Realtek", "Synaptics", "Dolby", "Khronos", "Partner", "MozillaPlugins",
        "AppDataLow", "System", "DefaultUserEnvironment", "NEXUS", "Nexus", "JavaSoft",
    };

    /// <summary>Chaves de editores com vários produtos: a chave em si nunca é removida; uma subchave de programa pode ser.</summary>
    public static readonly IReadOnlySet<string> SharedVendorRegistryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Mozilla", "Google", "Apple Inc.", "Apple Computer, Inc.", "Adobe", "Oracle", "Valve", "Epic Games", "Riot Games", "Blizzard Entertainment",
        "Ubisoft", "Electronic Arts", "EA Games", "Chromium", "Python", "JetBrains", "Autodesk", "Logitech", "Razer", "Corsair", "Unity Technologies",
    };

    public const int MaxRegistryDepth = 4; // Software\Editor\Programa\Sub

    /// <summary>
    /// Valida uma pasta/arquivo candidato. <paramref name="roots"/> = raízes do escopo certo. Retorna null se permitido.
    /// Não toca o disco (links/junções são conferidos à parte, por quem tem acesso ao disco).
    /// </summary>
    public static string? CheckFolder(string path, IReadOnlyList<string> roots, IReadOnlyList<InstalledProgram> stillInstalled)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(stillInstalled);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return "Caminho não absoluto.";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "Caminho inválido."; }
        if (!string.Equals(full, path, StringComparison.Ordinal) || path.EndsWith(Path.DirectorySeparatorChar) || path.Contains("..", StringComparison.Ordinal))
            return "Caminho não canônico (com '..', barra final ou separadores repetidos).";

        var root = roots.Where(r => r.Length > 0).Select(TrimSep).FirstOrDefault(r => IsStrictlyUnder(path, r));
        if (root is null) return "Fora das pastas onde o NEXUS procura sobras (Program Files, ProgramData, AppData).";
        var relative = path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (relative.Length == 0) return "A própria raiz nunca é removida.";
        if (relative.Length > 4) return "Fundo demais na árvore: sobra de programa fica no máximo em Raiz\\Editor\\Programa.";
        var first = relative[0];
        // LocalAppData\Programs\<App>: instalação por usuário. "Programs" sozinho é protegido; um nível abaixo é permitido.
        if (first.Equals("Programs", StringComparison.OrdinalIgnoreCase) && relative.Length >= 2)
        {
            if (ProtectedFolderNames.Contains(relative[1])) return $"'{relative[1]}' é uma pasta compartilhada.";
        }
        else if (ProtectedFolderNames.Contains(first) || first.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                 first.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
            return $"'{first}' é uma pasta compartilhada do Windows, de drivers ou de várias aplicações.";
        else if (relative.Length == 1 && SharedVendorFolderNames.Contains(first))
            return $"'{first}' é a pasta de um editor com vários produtos: só uma subpasta de programa pode ser removida.";

        foreach (var p in stillInstalled)
        {
            foreach (var loc in InstalledFolders(p))
                if (Overlaps(path, loc))
                    return $"Sobrepõe a pasta de um programa ainda instalado ({p.Name}).";
        }
        // Nome de um programa/editor ainda instalado em qualquer nível do caminho relativo: pode ser compartilhado.
        var named = relative.Where(s => !s.Equals("Programs", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var seg in named)
            if (NameMatching.BelongsToInstalled(seg, stillInstalled))
                return $"'{seg}' tem o nome de um programa ou editor ainda instalado.";
        return null;
    }

    /// <summary>Valida uma chave de registro <c>Software\Editor[\Programa]</c> do HKCU/HKLM.</summary>
    public static string? CheckRegistryKey(string hive, string path, IReadOnlyList<InstalledProgram> stillInstalled)
    {
        ArgumentNullException.ThrowIfNull(stillInstalled);
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('\\') || path.EndsWith('\\') || path.Contains("\\\\", StringComparison.Ordinal))
            return "Caminho de registro inválido.";
        if (hive is not ("HKCU" or "HKLM")) return "Hive não permitida.";
        var parts = path.Split('\\').ToList();
        if (!parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase)) return "Só chaves sob Software\\ são sobras de programa.";
        var named = parts.Skip(1).ToList();
        if (named.Count > 0 && named[0].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase)) named.RemoveAt(0);
        if (named.Count == 0) return "A raiz Software nunca é removida.";
        if (named.Count > MaxRegistryDepth - 1) return "Fundo demais: sobra fica em Software\\Editor\\Programa.";
        if (ProtectedRegistryNames.Contains(named[0]) || named[0].StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
            return $"'{named[0]}' é uma chave compartilhada (Windows, drivers ou várias aplicações).";
        if (named.Count == 1 && SharedVendorRegistryNames.Contains(named[0]))
            return $"'{named[0]}' é a chave de um editor com vários produtos: só a subchave do programa pode ser removida.";
        if (named.Any(s => s.Length == 0 || s.Trim() != s)) return "Nome de chave inválido.";
        if (SafetyPolicy.CheckRegistry(hive, path, "*") is { } problem) return problem;
        foreach (var seg in named)
            if (NameMatching.BelongsToInstalled(seg, stillInstalled))
                return $"'{seg}' tem o nome de um programa ou editor ainda instalado.";
        return null;
    }

    /// <summary>Pastas que um programa instalado usa: InstallLocation e a pasta do desinstalador (quando fora do sistema).</summary>
    public static IEnumerable<string> InstalledFolders(InstalledProgram p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.InstallLocation is { Length: > 3 } loc && Path.IsPathFullyQualified(loc)) yield return TrimSep(loc);
        var exe = Uninstall.UninstallCommandParser.Parse(p.UninstallString, _ => true, s => s, @"C:\Windows\System32", out _);
        if (exe is { IsMsi: false } && Path.GetDirectoryName(exe.FileName) is { Length: > 3 } dir &&
            !dir.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) && !dir.EndsWith(@"\Windows", StringComparison.OrdinalIgnoreCase) &&
            !dir.Contains("Package Cache", StringComparison.OrdinalIgnoreCase) && !dir.Contains("Installer", StringComparison.OrdinalIgnoreCase))
            yield return TrimSep(dir);
    }

    public static bool IsStrictlyUnder(string path, string root)
    {
        var r = TrimSep(root);
        return path.Length > r.Length + 1 && path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && path[r.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>Um contém o outro (ou são iguais).</summary>
    public static bool Overlaps(string a, string b)
    {
        var x = TrimSep(a);
        var y = TrimSep(b);
        return x.Equals(y, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(x, y) || IsStrictlyUnder(y, x);
    }

    public static string TrimSep(string p) => p.Length > 3 ? p.TrimEnd('\\', '/') : p;
}
