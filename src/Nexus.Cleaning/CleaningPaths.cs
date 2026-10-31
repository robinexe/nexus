using System.IO.Enumeration;

namespace Nexus.Cleaning;

/// <summary>
/// Pastas concretas que os placeholders das regras representam, para UM perfil (o de quem pede) e para a máquina.
/// No aplicador rodando com outra conta (serviço), as pastas de perfil vêm do SID (<see cref="WindowsCleaningFolders"/>),
/// nunca do próprio processo. Qualquer pasta nula = regras que dependem dela ficam indisponíveis.
/// </summary>
public sealed record CleaningFolders(
    string? UserProfile,
    string? UserTemp,
    string? LocalAppData,
    string? LocalAppDataLow,
    string? AppData,
    string? ProgramData,
    string? WinDir,
    string? SteamPath)
{
    /// <summary>Pastas sob as quais nada é limpo, além das fixas (ex.: a própria quarentena do NEXUS).</summary>
    public IReadOnlyList<string> ExtraForbidden { get; init; } = [];

    /// <summary>Raiz do disco do sistema (ex.: C:\), derivada da pasta do Windows. Só para %SystemDrive%\NVIDIA e %SystemDrive%\AMD.</summary>
    public string? SystemDrive { get; init; }

    public string? Resolve(string placeholder) => placeholder switch
    {
        "%UserTemp%" => UserTemp,
        "%LocalAppData%" => LocalAppData,
        "%LocalAppDataLow%" => LocalAppDataLow,
        "%AppData%" => AppData,
        "%ProgramData%" => ProgramData,
        "%WinDir%" => WinDir,
        "%SteamPath%" => SteamPath,
        "%UserProfile%" => UserProfile,
        "%SystemDrive%" => SystemDrive,
        _ => null,
    };
}

/// <summary>Uma raiz concreta (pasta existente, sem link/junção no caminho a partir da pasta-base).</summary>
public sealed record ResolvedRoot(string Path, CleaningRoot Root);

/// <summary>
/// Resolução de raízes e regras de contenção. Tudo aqui é leitura: lista pastas, nunca cria nem altera nada.
/// </summary>
public static class CleaningPaths
{
    private static readonly StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>Divide "%X%\a\*\b" em placeholder e segmentos.</summary>
    internal static (string Placeholder, string[] Segments) Split(string template)
    {
        var end = template.IndexOf('%', 1);
        var placeholder = template[..(end + 1)];
        var segments = template[(end + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return (placeholder, segments);
    }

    private static bool HasWildcard(string s) => s.IndexOfAny(['*', '?']) >= 0;

    internal static bool SegmentMatches(string pattern, string name) =>
        HasWildcard(pattern) ? FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true) : string.Equals(pattern, name, Cmp);

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Prefixo "pasta\" para testar se um caminho fica DENTRO da pasta (a raiz do disco já termina com separador).</summary>
    internal static string ChildPrefix(string directory) =>
        Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;

    /// <summary>A pasta-base de um placeholder, validada (absoluta, local, existente ou não).</summary>
    public static string? BaseOf(string placeholder, CleaningFolders folders, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(folders);
        problem = null;
        var b = folders.Resolve(placeholder);
        if (string.IsNullOrWhiteSpace(b)) { problem = $"Pasta {placeholder} indisponível."; return null; }
        if (!Path.IsPathFullyQualified(b) || b.StartsWith(@"\\", StringComparison.Ordinal) || b.StartsWith("//", StringComparison.Ordinal))
        {
            problem = $"Pasta {placeholder} não é um caminho local absoluto.";
            return null;
        }
        var full = Normalize(b);
        // Pastas do perfil vêm de variáveis e pastas conhecidas que o próprio usuário pode redirecionar (TEMP, LOCALAPPDATA...).
        // O aplicador pode rodar como administrador/SYSTEM: elas só valem se ficarem DENTRO da pasta do perfil.
        if (placeholder is "%UserProfile%" && full.Length <= 3)
        {
            problem = "Pasta do perfil inválida.";
            return null;
        }
        if (placeholder is "%UserTemp%" or "%LocalAppData%" or "%LocalAppDataLow%" or "%AppData%")
        {
            var profile = string.IsNullOrWhiteSpace(folders.UserProfile) || !Path.IsPathFullyQualified(folders.UserProfile) ? null : Normalize(folders.UserProfile);
            if (profile is null || profile.Length <= 3 || !full.StartsWith(profile + Path.DirectorySeparatorChar, Cmp))
            {
                problem = $"Pasta {placeholder} ({full}) fica fora da pasta do perfil; por segurança, as regras dela ficam indisponíveis.";
                return null;
            }
        }
        return full;
    }

    /// <summary>Raízes concretas e existentes de uma regra. Curingas só casam pastas de verdade (nunca links/junções).</summary>
    public static IReadOnlyList<ResolvedRoot> Expand(CleaningRule rule, CleaningFolders folders, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var result = new List<ResolvedRoot>();
        foreach (var root in rule.Roots)
        {
            var (placeholder, segments) = Split(root.Template);
            var baseDir = BaseOf(placeholder, folders, out var problem);
            if (baseDir is null) { problems?.Add(problem!); continue; }
            if (!IsRealDirectory(baseDir)) continue;

            IEnumerable<string> current = [baseDir];
            foreach (var seg in segments)
            {
                var next = new List<string>();
                foreach (var dir in current)
                {
                    if (HasWildcard(seg))
                    {
                        try
                        {
                            next.AddRange(new DirectoryInfo(dir).EnumerateDirectories(seg, new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, MatchCasing = MatchCasing.CaseInsensitive })
                                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0 && SegmentMatches(seg, d.Name))
                                .Select(d => d.FullName));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    else
                    {
                        var p = Path.Combine(dir, seg);
                        if (IsRealDirectory(p)) next.Add(p);
                    }
                }
                current = next;
            }
            foreach (var dir in current)
            {
                var full = Normalize(dir);
                if (ForbiddenReason(full, folders, allowWindowsRoot: segments.Length == 0) is { } why) { problems?.Add($"{full}: {why}"); continue; }
                if (!full.StartsWith(baseDir, Cmp)) continue;
                if (result.Any(r => string.Equals(r.Path, full, Cmp) && r.Root == root)) continue;
                result.Add(new ResolvedRoot(full, root));
            }
        }
        return result;
    }

    /// <summary>Pasta que existe e não é link simbólico nem junção.</summary>
    internal static bool IsRealDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// O arquivo está dentro de uma raiz da regra e casa o padrão dela? Verificado pelo MODELO da regra (não exige que a pasta
    /// exista), para validar caminhos vindos de um manifesto antes de restaurar. Recusa também alvos proibidos.
    /// </summary>
    public static bool IsWithinRule(CleaningRule rule, CleaningFolders folders, string path)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path)) return false;
        var full = Normalize(path);
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(path), Cmp)) return false; // nada de "..", "." ou separadores duplos
        var name = Path.GetFileName(full);
        foreach (var root in rule.Roots)
        {
            var (placeholder, segments) = Split(root.Template);
            var baseDir = BaseOf(placeholder, folders, out _);
            if (baseDir is null || !full.StartsWith(ChildPrefix(baseDir), Cmp)) continue;
            var rel = full[ChildPrefix(baseDir).Length..].Split(Path.DirectorySeparatorChar);
            if (rel.Length < segments.Length + 1) continue;
            if (!root.Recursive && rel.Length != segments.Length + 1) continue;
            var ok = true;
            for (var i = 0; i < segments.Length && ok; i++) ok = SegmentMatches(segments[i], rel[i]);
            if (!ok) continue;
            if (!root.Patterns.Any(p => SegmentMatches(p, name))) continue;
            if (ForbiddenReason(Path.GetDirectoryName(full)!, folders, allowWindowsRoot: segments.Length == 0 && !root.Recursive) is not null) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Primeira pasta entre a pasta-base da regra (inclusive) e a pasta do arquivo (inclusive) que é link simbólico ou junção,
    /// ou null se nenhuma é. Pastas que não existem são ignoradas. Caminho fora de todas as raízes da regra devolve o próprio
    /// caminho (recusado). Usado imediatamente antes de mover um arquivo para a quarentena ou de volta: o aplicador pode rodar
    /// como administrador/SYSTEM, e uma pasta intermediária trocada por uma junção desviaria a escrita para fora da regra
    /// (ex.: <c>Temp\a</c> → <c>C:\Windows\System32</c>).
    /// </summary>
    public static string? ReparseInChain(CleaningRule rule, CleaningFolders folders, string path)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(path);
        if (!Path.IsPathFullyQualified(path)) return path;
        var full = Normalize(path);
        string? baseDir = null;
        foreach (var root in rule.Roots)
        {
            var b = BaseOf(Split(root.Template).Placeholder, folders, out _);
            if (b is not null && full.StartsWith(ChildPrefix(b), Cmp) && (baseDir is null || b.Length > baseDir.Length)) baseDir = b;
        }
        if (baseDir is null) return path;
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length >= baseDir.Length; dir = Path.GetDirectoryName(dir))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(dir); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return dir; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) return dir;
        }
        return null;
    }

    /// <summary>
    /// Motivo para recusar uma pasta como raiz de limpeza (ou null). Nunca: raiz de unidade, System32/SysWOW64/WinSxS, a pasta
    /// do perfil inteira, Documentos/Imagens/Área de Trabalho/Vídeos/Músicas/Downloads, Arquivos de Programas, a quarentena.
    /// </summary>
    public static string? ForbiddenReason(string directory, CleaningFolders folders, bool allowWindowsRoot = false)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(folders);
        if (!Path.IsPathFullyQualified(directory)) return "caminho relativo";
        if (directory.StartsWith(@"\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal)) return "caminho de rede";
        var full = Normalize(directory);
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), full, Cmp) || full.Length <= 3) return "raiz da unidade";

        static bool Under(string p, string? parent) =>
            parent is not null && (string.Equals(p, parent, Cmp) || p.StartsWith(parent + Path.DirectorySeparatorChar, Cmp));
        static bool Same(string p, string? other) => other is not null && string.Equals(p, other, Cmp);
        string? N(string? p) => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p) ? null : Normalize(p);

        var win = N(folders.WinDir);
        if (win is not null)
        {
            foreach (var sub in new[] { "System32", "SysWOW64", "WinSxS", "SystemApps", "servicing", "Boot" })
                if (Under(full, Path.Combine(win, sub))) return $"pasta protegida do Windows ({sub})";
            if (Same(full, win) && !allowWindowsRoot) return "pasta do Windows inteira";
        }
        var profile = N(folders.UserProfile);
        if (profile is not null)
        {
            if (Same(full, profile)) return "pasta do perfil inteira";
            foreach (var sub in new[] { "Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "OneDrive", "Favorites", "Contacts", "Saved Games" })
                if (Under(full, Path.Combine(profile, sub))) return $"pasta pessoal ({sub})";
        }
        foreach (var whole in new[] { folders.LocalAppData, folders.AppData, folders.LocalAppDataLow, folders.ProgramData })
            if (Same(full, N(whole))) return "pasta de dados inteira";
        foreach (var special in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var p = N(Environment.GetFolderPath(special));
            if (p is null || p.Length <= 3) continue;
            if (special is Environment.SpecialFolder.ProgramFiles or Environment.SpecialFolder.ProgramFilesX86 ? Same(full, p) : Under(full, p))
                return "pasta pessoal ou de programas";
        }
        foreach (var extra in folders.ExtraForbidden)
            if (Under(full, N(extra))) return "pasta do próprio NEXUS";
        return null;
    }
}
