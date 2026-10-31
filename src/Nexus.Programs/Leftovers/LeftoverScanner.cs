using System.Globalization;
using Nexus.Programs.Inventory;
using Nexus.WindowsActions.Registry;

namespace Nexus.Programs.Leftovers;

public enum LeftoverKind { Folder, RegistryKey }

/// <summary>Possível sobra de um programa desinstalado, com a pontuação de confiança e o motivo em português simples.</summary>
public sealed record LeftoverCandidate(LeftoverKind Kind, bool MachineScope, string Path, int Score, string Reason, long? SizeBytes = null, int? FileCount = null)
{
    public string Hive => MachineScope ? "HKLM" : "HKCU";
    public string DisplayPath => Kind == LeftoverKind.RegistryKey ? $@"{Hive}\{Path}" : Path;
    public string Confidence => Score >= 75 ? "Alta" : Score >= 50 ? "Média" : "Baixa";
    /// <summary>Marcada de início só com confiança alta.</summary>
    public bool Recommended => Score >= 75;
    public string KindText => Kind == LeftoverKind.Folder ? (MachineScope ? "Pasta (todos os usuários)" : "Pasta (seu perfil)") : (MachineScope ? "Registro (máquina)" : "Registro (seu usuário)");
}

/// <summary>
/// Procura (só leitura) sobras de um programa: pastas com o nome do programa/editor em Program Files, ProgramData e AppData,
/// e chaves <c>Software\Editor\Programa</c> no HKCU/HKLM. Cada achado passa pelas MESMAS regras do handler do aplicador
/// (<see cref="LeftoverPolicy"/>); links/junções são ignorados e nunca seguidos.
/// </summary>
public static class LeftoverScanner
{
    public const int InstallLocationScore = 95;
    public const int UninstallerFolderScore = 90;
    public const int VendorProgramFolderScore = 85;
    public const int ProgramFolderScore = 80;
    public const int VendorProgramKeyScore = 80;
    public const int ProgramKeyScore = 70;
    public const int EmptyVendorFolderScore = 60;
    public const int VendorKeyScore = 50;
    public const int SimilarNameScore = 40;

    public static IReadOnlyList<LeftoverCandidate> Scan(InstalledProgram removed, IReadOnlyList<InstalledProgram> stillInstalled, LeftoverRoots roots,
        IRegistryKeyReader? registry, bool computeSizes = true)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(stillInstalled);
        ArgumentNullException.ThrowIfNull(roots);
        var others = stillInstalled.Where(p => p.Id != removed.Id).ToList();
        var forms = NameMatching.ProgramForms(removed.Name, removed.Publisher);
        var found = new Dictionary<string, LeftoverCandidate>(StringComparer.OrdinalIgnoreCase);

        void AddFolder(string path, bool machine, int score, string reason)
        {
            var scopeRoots = machine ? roots.Machine : roots.User;
            if (LeftoverPolicy.CheckFolder(path, scopeRoots, others) is not null) return;
            if (!Directory.Exists(path) || IsLinkOrUnderLink(path, scopeRoots)) return;
            if (found.TryGetValue(path, out var prev) && prev.Score >= score) return;
            found[path] = new LeftoverCandidate(LeftoverKind.Folder, machine, path, score, reason);
        }

        // 1) O que o próprio programa registrou (vale também quando o desinstalador deixou a pasta para trás).
        foreach (var (folder, i) in LeftoverPolicy.InstalledFolders(removed).Select((f, i) => (f, i)))
        {
            var machine = roots.Machine.Any(r => LeftoverPolicy.IsStrictlyUnder(folder, r));
            var user = roots.User.Any(r => LeftoverPolicy.IsStrictlyUnder(folder, r));
            if (machine || user)
                AddFolder(folder, machine, i == 0 && removed.InstallLocation is not null ? InstallLocationScore : UninstallerFolderScore,
                    i == 0 && removed.InstallLocation is not null ? "Pasta de instalação registrada pelo próprio programa." : "Pasta onde ficava o desinstalador.");
        }

        // 2) Pastas com o nome do programa ou do editor.
        foreach (var (root, machine) in roots.Machine.Select(r => (r, true)).Concat(roots.User.Select(r => (r, false))))
        {
            var bases = new List<string> { root };
            var programs = Path.Combine(root, "Programs");
            if (!machine && Directory.Exists(programs) && !IsReparse(programs)) bases.Add(programs);
            foreach (var b in bases)
            {
                foreach (var dir in SafeSubdirectories(b))
                {
                    var name = Path.GetFileName(dir);
                    if (b == root && name.Equals("Programs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (NameMatching.MatchesProgram(name, forms))
                    {
                        AddFolder(dir, machine, ProgramFolderScore, "Pasta com o nome do programa.");
                    }
                    else if (NameMatching.MatchesPublisher(name, removed.Publisher))
                    {
                        var subs = SafeSubdirectories(dir).ToList();
                        var matching = subs.Where(s => NameMatching.MatchesProgram(Path.GetFileName(s), forms)).ToList();
                        var onlyThis = matching.Count == subs.Count && !SafeHasFiles(dir);
                        if (onlyThis && !NameMatching.BelongsToInstalled(name, others))
                            AddFolder(dir, machine, matching.Count == 0 ? EmptyVendorFolderScore : VendorProgramFolderScore,
                                matching.Count == 0 ? "Pasta do editor, vazia." : "Pasta do editor que só tem este programa.");
                        if (!found.ContainsKey(dir))
                            foreach (var s in matching) AddFolder(s, machine, VendorProgramFolderScore, "Pasta do programa dentro da pasta do editor.");
                    }
                    else if (NameMatching.ContainsProgram(name, forms))
                    {
                        AddFolder(dir, machine, SimilarNameScore, "Nome parecido com o do programa: confira antes de marcar.");
                    }
                }
            }
        }

        // Pasta-mãe e filha ao mesmo tempo: fica só a mãe (remover a filha depois falharia por "já não existe").
        foreach (var path in found.Keys.ToList())
            if (found.Keys.Any(other => other != path && LeftoverPolicy.IsStrictlyUnder(path, other))) found.Remove(path);

        var result = found.Values.ToList();
        if (computeSizes)
            result = result.Select(c => { var (bytes, files) = MeasureFolder(c.Path); return c with { SizeBytes = bytes, FileCount = files }; }).ToList();

        if (registry is not null) result.AddRange(ScanRegistry(removed, others, forms, registry));
        return result.OrderByDescending(c => c.Score).ThenBy(c => c.DisplayPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<LeftoverCandidate> ScanRegistry(InstalledProgram removed, List<InstalledProgram> others, IReadOnlyList<string> forms, IRegistryKeyReader registry)
    {
        var list = new List<LeftoverCandidate>();
        foreach (var hive in new[] { RegHive.HKCU, RegHive.HKLM })
        {
            var machine = hive == RegHive.HKLM;
            foreach (var b in new[] { "Software", @"Software\WOW6432Node" })
            {
                foreach (var name in SafeSubKeys(registry, hive, b))
                {
                    var path = $@"{b}\{name}";
                    if (NameMatching.MatchesProgram(name, forms))
                    {
                        Add(path, ProgramKeyScore, "Chave de registro com o nome do programa.");
                    }
                    else if (NameMatching.MatchesPublisher(name, removed.Publisher))
                    {
                        var subs = SafeSubKeys(registry, hive, path);
                        var matching = subs.Where(s => NameMatching.MatchesProgram(s, forms)).ToList();
                        foreach (var s in matching) Add($@"{path}\{s}", VendorProgramKeyScore, "Chave do programa dentro da chave do editor.");
                        if (matching.Count == subs.Count && !NameMatching.BelongsToInstalled(name, others) && matching.Count == 0)
                            Add(path, VendorKeyScore, "Chave do editor sem subchaves (pode guardar só configurações).");
                    }
                }
            }

            void Add(string path, int score, string reason)
            {
                if (LeftoverPolicy.CheckRegistryKey(hive.ToString(), path, others) is null && !list.Any(c => c.MachineScope == machine && string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new LeftoverCandidate(LeftoverKind.RegistryKey, machine, path, score, reason));
            }
        }
        return list;
    }

    private static IReadOnlyList<string> SafeSubKeys(IRegistryKeyReader registry, RegHive hive, string path)
    {
        try { return registry.SubKeyNames(hive, path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException) { return []; }
    }

    private static List<string> SafeSubdirectories(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return []; }
    }

    private static bool SafeHasFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>O alvo, ou alguma pasta entre ele e a raiz, é link/junção: nunca seguir.</summary>
    public static bool IsLinkOrUnderLink(string path, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var root = roots.Select(LeftoverPolicy.TrimSep).FirstOrDefault(r => LeftoverPolicy.IsStrictlyUnder(path, r));
        if (root is null) return true;
        for (var p = path; p is not null && p.Length > root.Length; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && IsReparse(p)) return true;
        return false;
    }

    /// <summary>Tamanho e número de arquivos sem seguir links (para mostrar; limite de 200 mil arquivos).</summary>
    public static (long Bytes, int Files) MeasureFolder(string path)
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                bytes += f.Length;
                if (++files >= 200_000) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (bytes, files);
    }

    public static string Summary(IReadOnlyList<LeftoverCandidate> items) =>
        string.Create(CultureInfo.GetCultureInfo("pt-BR"),
            $"{items.Count} {(items.Count == 1 ? "sobra encontrada" : "sobras encontradas")} · {ProgramInventory.FormatSize(items.Sum(i => i.SizeBytes ?? 0))}");
}
