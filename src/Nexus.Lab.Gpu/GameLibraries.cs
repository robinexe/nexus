using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexus.Lab.Gpu;

/// <summary>Jogo instalado encontrado num manifesto de loja (somente leitura).</summary>
/// <param name="ExePathSure">true quando o manifesto diz qual é o executável (Epic); false = escolhido pelo maior .exe da pasta (Steam).</param>
public sealed record GameEntry(string Name, string Store, string InstallDir, string? ExePath, bool ExePathSure);

/// <summary>
/// Lê manifestos da Steam (libraryfolders.vdf + appmanifest_*.acf) e da Epic (Manifests\*.item). Só leitura de arquivos de
/// texto das lojas; nada é executado. Não segue links simbólicos/junções ao procurar o executável.
/// </summary>
public static partial class GameLibraries
{
    public const string ExePattern = @"(?i)[A-Z]:\\(?:[^\\/:*?""<>|\x00-\x1f]{1,120}\\){0,16}[^\\/:*?""<>|\x00-\x1f]{1,120}\.exe";

    [GeneratedRegex(@"""(?<k>[^""]*)""\s+""(?<v>(?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex VdfPair();

    [GeneratedRegex(@"\A(?:" + ExePattern + @")\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExeRegex();

    /// <summary>Caminho de executável aceito pelo catálogo (mesma regra de userParameters do gpulab).</summary>
    public static bool IsValidExePath(string? path) => path is { Length: <= 260 } && ExeRegex().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);

    /// <summary>Pares "chave" "valor" de um VDF/ACF (formato texto da Steam), em ordem. Barras invertidas duplicadas viram simples.</summary>
    public static IReadOnlyList<(string Key, string Value)> ParseVdfPairs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return VdfPair().Matches(text).Select(m => (m.Groups["k"].Value, m.Groups["v"].Value.Replace(@"\\", @"\", StringComparison.Ordinal))).ToList();
    }

    /// <summary>Pastas de biblioteca listadas em libraryfolders.vdf (chaves "path").</summary>
    public static IReadOnlyList<string> SteamLibraryPaths(string libraryFoldersVdf) =>
        ParseVdfPairs(libraryFoldersVdf).Where(p => p.Key.Equals("path", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static readonly string[] NotGames = ["Redistributable", "Steamworks Common", "Proton", "Steam Linux Runtime", "SteamVR", "Wallpaper Engine", "Soundtrack"];

    /// <summary>appmanifest_*.acf → (appid, nome, installdir). Null se faltar campo ou for ferramenta/redistribuível.</summary>
    public static (string AppId, string Name, string InstallDir)? ParseAppManifest(string acf)
    {
        var pairs = ParseVdfPairs(acf);
        string? Get(string k) => pairs.FirstOrDefault(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;
        var (id, name, dir) = (Get("appid"), Get("name"), Get("installdir"));
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dir)) return null;
        if (id == "228980" || NotGames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return null;
        if (dir.Contains("..", StringComparison.Ordinal) || dir.IndexOfAny(['\\', '/', ':']) >= 0) return null;
        return (id, name, dir);
    }

    /// <summary>Manifesto .item da Epic (JSON): DisplayName, InstallLocation, LaunchExecutable.</summary>
    public static GameEntry? ParseEpicItem(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? S(string n) => r.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var (name, loc, exe) = (S("DisplayName"), S("InstallLocation"), S("LaunchExecutable"));
            if (r.TryGetProperty("bIsApplication", out var app) && app.ValueKind == JsonValueKind.False) return null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) return null;
            string? full = null;
            if (!string.IsNullOrWhiteSpace(exe) && !exe.Contains("..", StringComparison.Ordinal))
                full = loc.TrimEnd('\\', '/') + "\\" + exe.Replace('/', '\\');
            return new GameEntry(name, "Epic", loc, IsValidExePath(full) ? full : null, true);
        }
        catch (JsonException) { return null; }
    }

    private static readonly string[] ExeNoise =
    [
        "unins", "setup", "redist", "vcredist", "dxsetup", "crash", "report", "unitycrashhandler", "easyanticheat", "beservice",
        "battleye", "dotnet", "installer", "update", "launcherpatcher", "cefprocess", "helper", "overlay", "benchmark", "server",
    ];

    /// <summary>
    /// Escolhe o executável principal entre os .exe da pasta do jogo: descarta instaladores, relatórios de erro e anticheat,
    /// prefere o mais raso e, entre os rasos, o maior. É uma ESTIMATIVA: a página mostra "provável".
    /// </summary>
    public static string? PickGameExe(string installDir, IEnumerable<(string Path, long Size)> exes)
    {
        ArgumentNullException.ThrowIfNull(installDir);
        var root = installDir.TrimEnd('\\', '/');
        var candidates = exes
            .Where(e => e.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(e => !ExeNoise.Any(n => Path.GetFileName(e.Path.Replace('\\', '/')).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Select(e => (e.Path, e.Size, Depth: e.Path.Length > root.Length ? e.Path[root.Length..].Count(c => c is '\\' or '/') : 99))
            .ToList();
        if (candidates.Count == 0) return null;
        // Unreal: Binaries\Win64\Jogo-Win64-Shipping.exe é o processo real (o .exe da raiz só lança).
        var shipping = candidates.Where(c => c.Path.Contains("-Shipping.exe", StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.Size).FirstOrDefault();
        if (shipping.Path is not null) return shipping.Path;
        return candidates.OrderBy(c => c.Depth).ThenByDescending(c => c.Size).First().Path;
    }

    /// <summary>Lista .exe até 3 níveis abaixo, sem seguir pontos de reanálise (links/junções) e com teto de arquivos.</summary>
    public static IReadOnlyList<(string Path, long Size)> ScanExes(string dir, int maxDepth = 3, int maxFiles = 4000)
    {
        var list = new List<(string, long)>();
        var seen = 0;
        void Walk(DirectoryInfo d, int depth)
        {
            if (depth > maxDepth || seen > maxFiles) return;
            try
            {
                foreach (var f in d.EnumerateFiles("*.exe"))
                {
                    if (++seen > maxFiles) return;
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    list.Add((f.FullName, f.Length));
                }
                foreach (var sub in d.EnumerateDirectories())
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    Walk(sub, depth + 1);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
        var root = new DirectoryInfo(dir);
        if (root.Exists && (root.Attributes & FileAttributes.ReparsePoint) == 0) Walk(root, 0);
        return list;
    }

    /// <summary>Jogos da Steam a partir da pasta da Steam (HKCU\Software\Valve\Steam\SteamPath).</summary>
    public static IReadOnlyList<GameEntry> SteamGames(string steamPath, int maxGames = 200)
    {
        var games = new List<GameEntry>();
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        var libraries = new List<string> { steamPath };
        try { if (File.Exists(vdf)) libraries.AddRange(SteamLibraryPaths(File.ReadAllText(vdf))); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var m in manifests)
            {
                if (games.Count >= maxGames) return games;
                try
                {
                    if (ParseAppManifest(File.ReadAllText(m)) is not { } app) continue;
                    var dir = Path.Combine(apps, "common", app.InstallDir);
                    if (!Directory.Exists(dir)) continue;
                    var exe = PickGameExe(dir, ScanExes(dir));
                    games.Add(new GameEntry(app.Name, "Steam", dir, IsValidExePath(exe) ? exe : null, false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return games;
    }

    /// <summary>Jogos da Epic a partir de %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests.</summary>
    public static IReadOnlyList<GameEntry> EpicGames(string manifestsDir, int maxGames = 200)
    {
        var games = new List<GameEntry>();
        if (!Directory.Exists(manifestsDir)) return games;
        try
        {
            foreach (var f in Directory.EnumerateFiles(manifestsDir, "*.item").Take(maxGames))
            {
                try { if (ParseEpicItem(File.ReadAllText(f)) is { } g) games.Add(g); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return games;
    }

    /// <summary>"GpuPreference=2;SwapEffectUpgradeEnable=1;" → 2. Null se o token não existir.</summary>
    public static int? ParseGpuPreference(string? raw) => ParseToken(raw, "GpuPreference");

    /// <summary>Valor inteiro de um token ("Nome=valor;") da string de UserGpuPreferences; null se ausente.</summary>
    public static int? ParseToken(string? raw, string token)
    {
        if (raw is null) return null;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq] == token && int.TryParse(part[(eq + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
        }
        return null;
    }

    public static string PreferenceLabel(int? pref) => pref switch
    {
        2 => "alto desempenho (dedicada)",
        1 => "economia de energia (integrada)",
        0 => "o Windows decide",
        null => "o Windows decide (sem preferência)",
        _ => $"valor {pref}",
    };
}
