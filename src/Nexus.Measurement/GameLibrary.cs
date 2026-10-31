using System.Text;
using System.Text.RegularExpressions;
using Nexus.Lab.Gpu;

namespace Nexus.Measurement;

/// <summary>Jogo conhecido: das bibliotecas (pasta de instalação) ou da lista embutida/do usuário (nome do executável).</summary>
/// <param name="InstallDir">Pasta de instalação (processo cujo executável está dentro dela = este jogo). Null para jogos por nome.</param>
/// <param name="ExeName">Nome do executável, quando conhecido (lista embutida, do usuário ou manifesto da Epic).</param>
public sealed record KnownGame(string Name, string Store, string? InstallDir, string? ExeName);

/// <summary>
/// Lê as bibliotecas das lojas para saber QUAIS pastas têm jogos. Só leitura de arquivos de texto/dados das próprias lojas;
/// nada é executado, nenhum processo de jogo é aberto. Fontes:
/// <list type="bullet">
/// <item>Steam: libraryfolders.vdf + appmanifest_*.acf (parsers do módulo GPU, <see cref="GameLibraries"/>);</item>
/// <item>Epic: %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item (JSON);</item>
/// <item>Riot: %ProgramData%\Riot Games\Metadata\*\*.product_settings.yaml (product_install_full_path);</item>
/// <item>Battle.net: %ProgramData%\Battle.net\Agent\product.db (protobuf; extraímos os caminhos de instalação como texto);</item>
/// <item>Xbox/Game Pass: arquivo .GamingRoot na raiz de cada unidade (pasta dos jogos, padrão X:\XboxGames).</item>
/// </list>
/// </summary>
public static partial class GameLibrary
{
    /// <summary>Lançadores, navegadores e utilitários que nunca são "o jogo", mesmo dentro da pasta de uma loja ou em tela cheia.</summary>
    public static readonly IReadOnlySet<string> NeverGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "gameoverlayui.exe", "epicgameslauncher.exe", "epicwebhelper.exe",
        "battle.net.exe", "agent.exe", "blizzard error.exe", "riotclientservices.exe", "riotclientux.exe", "riotclientuxrender.exe",
        "riotclientcrashhandler.exe", "leagueclient.exe", "leagueclientux.exe", "leagueclientuxrender.exe", "vgc.exe", "vgtray.exe",
        "xboxpcapp.exe", "gamingservices.exe", "gamebar.exe", "eadesktop.exe", "eabackgroundservice.exe", "ubisoftconnect.exe", "upc.exe",
        "explorer.exe", "dwm.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "discord.exe",
        "spotify.exe", "vlc.exe", "mpc-hc64.exe", "mpv.exe", "obs64.exe", "obs32.exe", "nexus.desktop.exe", "applicationframehost.exe",
        "textinputhost.exe", "searchhost.exe", "startmenuexperiencehost.exe", "lockapp.exe", "shellexperiencehost.exe", "powerpnt.exe",
        "presentmon.exe", "nvcontainer.exe", "nvidia overlay.exe", "nvidia share.exe", "radeonsoftware.exe", "msiafterburner.exe", "rtss.exe",
        "wallpaper32.exe", "wallpaper64.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "slack.exe", "code.exe", "devenv.exe",
    };

    private static readonly string[] HelperNoise =
    [
        "unins", "setup", "redist", "vcredist", "dxsetup", "crash", "report", "easyanticheat", "beservice", "battleye", "installer",
        "update", "launcher", "cefprocess", "helper", "webview", "overlay", "server", "service", "handler", "bugsplat",
    ];

    /// <summary>Executável auxiliar dentro da pasta do jogo (instalador, relatório de erro, anticheat, lançador).</summary>
    public static bool IsHelperExe(string exeName) =>
        NeverGames.Contains(exeName) || HelperNoise.Any(n => exeName.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Jogos populares por executável (o processo que desenha os quadros). Serve quando o jogo foi instalado fora das lojas
    /// lidas ou quando a biblioteca não pôde ser lida. Os nomes vêm das próprias instalações (Gerenciador de Tarefas, aba Detalhes).
    /// </summary>
    public static IReadOnlyList<KnownGame> BuiltIn { get; } =
    [
        new("Counter-Strike 2", "Lista do NEXUS", null, "cs2.exe"),
        new("VALORANT", "Lista do NEXUS", null, "VALORANT-Win64-Shipping.exe"),
        new("League of Legends", "Lista do NEXUS", null, "League of Legends.exe"),
        new("Fortnite", "Lista do NEXUS", null, "FortniteClient-Win64-Shipping.exe"),
        new("Apex Legends", "Lista do NEXUS", null, "r5apex.exe"),
        new("Apex Legends (DX12)", "Lista do NEXUS", null, "r5apex_dx12.exe"),
        new("Dota 2", "Lista do NEXUS", null, "dota2.exe"),
        new("Rocket League", "Lista do NEXUS", null, "RocketLeague.exe"),
        new("Overwatch 2", "Lista do NEXUS", null, "Overwatch.exe"),
        new("Call of Duty", "Lista do NEXUS", null, "cod.exe"),
        new("Rainbow Six Siege", "Lista do NEXUS", null, "RainbowSix.exe"),
        new("PUBG: Battlegrounds", "Lista do NEXUS", null, "TslGame.exe"),
        new("Minecraft (Bedrock)", "Lista do NEXUS", null, "Minecraft.Windows.exe"),
        new("GTA V", "Lista do NEXUS", null, "GTA5.exe"),
        new("GTA V Enhanced", "Lista do NEXUS", null, "GTA5_Enhanced.exe"),
        new("Roblox", "Lista do NEXUS", null, "RobloxPlayerBeta.exe"),
        new("Marvel Rivals", "Lista do NEXUS", null, "Marvel-Win64-Shipping.exe"),
        new("Free Fire (emulador)", "Lista do NEXUS", null, "HD-Player.exe"),
        new("Rust", "Lista do NEXUS", null, "RustClient.exe"),
        new("Escape from Tarkov", "Lista do NEXUS", null, "EscapeFromTarkov.exe"),
        new("Elden Ring", "Lista do NEXUS", null, "eldenring.exe"),
        new("Cyberpunk 2077", "Lista do NEXUS", null, "Cyberpunk2077.exe"),
        new("Warzone", "Lista do NEXUS", null, "cod24-cod.exe"),
        new("Path of Exile 2", "Lista do NEXUS", null, "PathOfExile.exe"),
        new("World of Warcraft", "Lista do NEXUS", null, "Wow.exe"),
        new("Diablo IV", "Lista do NEXUS", null, "Diablo IV.exe"),
        new("EA SPORTS FC", "Lista do NEXUS", null, "FC25.exe"),
    ];

    // ------------------------------------------------------------------ parsers (puros)

    /// <summary>Steam: pastas de jogo a partir do texto de libraryfolders.vdf e dos appmanifest (lidos pela função dada).</summary>
    public static IReadOnlyList<KnownGame> ParseSteam(string steamPath, string? libraryFoldersVdf, Func<string, IEnumerable<(string Path, string Text)>> manifestsOf)
    {
        ArgumentNullException.ThrowIfNull(manifestsOf);
        var libs = new List<string> { steamPath };
        if (libraryFoldersVdf is not null) libs.AddRange(GameLibraries.SteamLibraryPaths(libraryFoldersVdf));
        var games = new List<KnownGame>();
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            foreach (var (_, text) in manifestsOf(apps))
            {
                if (GameLibraries.ParseAppManifest(text) is not { } app) continue;
                games.Add(new KnownGame(app.Name, "Steam", Path.Combine(apps, "common", app.InstallDir), null));
            }
        }
        return games;
    }

    /// <summary>Epic: um manifesto .item (JSON) → jogo com pasta e executável.</summary>
    public static KnownGame? ParseEpic(string json)
    {
        if (GameLibraries.ParseEpicItem(json) is not { } g) return null;
        return new KnownGame(g.Name, "Epic", g.InstallDir, g.ExePath is null ? null : WinFileName(g.ExePath));
    }

    [GeneratedRegex(@"(?m)^\s*product_install_full_path\s*:\s*""?(?<p>[^""\r\n]+?)""?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RiotInstallPath();

    /// <summary>Riot: um *.product_settings.yaml → pasta do jogo (VALORANT, League of Legends...). Nome = pasta do produto.</summary>
    public static KnownGame? ParseRiotProductSettings(string yaml, string productFolderName)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var m = RiotInstallPath().Match(yaml);
        if (!m.Success) return null;
        var path = m.Groups["p"].Value.Trim().Replace('/', '\\');
        if (!IsAbsoluteWindowsPath(path)) return null;
        var product = (productFolderName ?? "").Split('.')[0];
        var name = product.ToLowerInvariant() switch
        {
            "valorant" => "VALORANT",
            "league_of_legends" => "League of Legends",
            "bacon" => "Legends of Runeterra",
            "lion" => "2XKO",
            _ => product.Replace('_', ' '),
        };
        return new KnownGame(name, "Riot", path.TrimEnd('\\'), null);
    }

    [GeneratedRegex(@"[A-Za-z]:[\\/][^\x00-\x1f""*?<>|]{2,200}", RegexOptions.CultureInvariant)]
    private static partial Regex PathInBytes();

    private static readonly string[] BattleNetNotGames = ["Battle.net", "Blizzard App", "Agent", "Cache", "Temp", "Downloads"];

    /// <summary>
    /// Battle.net: product.db é protobuf sem esquema público; os caminhos de instalação aparecem nele como texto UTF-8
    /// ("C:/Program Files (x86)/Overwatch"). Extraímos esses caminhos (heurística declarada) e usamos o nome da pasta.
    /// </summary>
    public static IReadOnlyList<KnownGame> ParseBattleNetProductDb(byte[] productDb)
    {
        ArgumentNullException.ThrowIfNull(productDb);
        var text = Encoding.UTF8.GetString(productDb);
        var games = new List<KnownGame>();
        foreach (Match m in PathInBytes().Matches(text))
        {
            var path = m.Value.Replace('/', '\\').TrimEnd('\\', ' ');
            if (!IsAbsoluteWindowsPath(path)) continue;
            var name = WinFileName(path);
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || BattleNetNotGames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
            if (path.Count(c => c == '\\') > 4) continue; // pasta de jogo fica perto da raiz; caminhos fundos são arquivos internos
            if (games.Any(g => g.InstallDir!.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            games.Add(new KnownGame(name, "Battle.net", path, null));
        }
        return games;
    }

    /// <summary>
    /// Xbox/Game Pass: .GamingRoot = "RGBX" + uint32 (contagem) + caminhos UTF-16 terminados em zero, relativos à unidade
    /// ("XboxGames"). Devolve as pastas raiz de jogos daquela unidade.
    /// </summary>
    public static IReadOnlyList<string> ParseGamingRoot(byte[] data, string driveRoot)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(driveRoot);
        var list = new List<string>();
        if (data.Length < 10 || data[0] != (byte)'R' || data[1] != (byte)'G' || data[2] != (byte)'B' || data[3] != (byte)'X') return list;
        var text = Encoding.Unicode.GetString(data, 8, (data.Length - 8) / 2 * 2);
        foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length is < 1 or > 200 || part.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || part.Contains("..", StringComparison.Ordinal)) continue;
            var rel = part.TrimStart('\\', '/');
            if (rel.Length > 1 && rel[1] == ':') rel = rel[2..].TrimStart('\\', '/'); // alguns gravam "C:\XboxGames"
            list.Add(Path.Combine(driveRoot, rel).Replace('/', '\\').TrimEnd('\\'));
        }
        return list;
    }

    /// <summary>Nome do arquivo num caminho de Windows (aceita \ e /, em qualquer SO).</summary>
    public static string WinFileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>Caminho absoluto de Windows (X:\...), sem "..".</summary>
    public static bool IsAbsoluteWindowsPath(string? p) =>
        p is { Length: >= 3 and <= 260 } && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/') && !p.Contains("..", StringComparison.Ordinal);

    /// <summary>O executável está dentro da pasta (comparação por prefixo de caminho, sem diferenciar maiúsculas, com separador).</summary>
    public static bool IsUnder(string exePath, string folder)
    {
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(folder)) return false;
        var f = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
        return exePath.Replace('/', '\\').StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ leitura do disco (somente leitura, com tetos)

    /// <summary>Pastas conhecidas para ler (injetáveis nos testes).</summary>
    public sealed record Roots(string? SteamPath, string ProgramData, IReadOnlyList<string> Drives);

    /// <summary>Lê todas as bibliotecas. Erros de leitura de uma loja não impedem as outras; ficam em <paramref name="notes"/>.</summary>
    public static IReadOnlyList<KnownGame> Scan(Roots roots, List<string>? notes = null, int maxPerStore = 300)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var games = new List<KnownGame>();
        void Try(string store, Action read)
        {
            try { read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            { notes?.Add($"{store}: não deu para ler ({ex.Message})."); }
        }

        if (roots.SteamPath is { } steam && Directory.Exists(steam))
            Try("Steam", () =>
            {
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                games.AddRange(ParseSteam(steam, File.Exists(vdf) ? File.ReadAllText(vdf) : null, apps =>
                    Directory.Exists(apps) ? Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(maxPerStore).Select(f => (f, SafeRead(f))) : []));
            });

        var epic = Path.Combine(roots.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(epic))
            Try("Epic", () => games.AddRange(Directory.EnumerateFiles(epic, "*.item").Take(maxPerStore).Select(f => ParseEpic(SafeRead(f))).OfType<KnownGame>()));

        var riot = Path.Combine(roots.ProgramData, "Riot Games", "Metadata");
        if (Directory.Exists(riot))
            Try("Riot", () =>
            {
                foreach (var dir in Directory.EnumerateDirectories(riot).Take(50))
                foreach (var f in Directory.EnumerateFiles(dir, "*.product_settings.yaml").Take(5))
                    if (ParseRiotProductSettings(SafeRead(f), Path.GetFileName(dir)) is { } g) games.Add(g);
            });

        var bnet = Path.Combine(roots.ProgramData, "Battle.net", "Agent", "product.db");
        if (File.Exists(bnet))
            Try("Battle.net", () =>
            {
                if (new FileInfo(bnet).Length <= 4 * 1024 * 1024) games.AddRange(ParseBattleNetProductDb(File.ReadAllBytes(bnet)));
            });

        foreach (var drive in roots.Drives)
        {
            var marker = Path.Combine(drive, ".GamingRoot");
            if (!File.Exists(marker)) continue;
            Try("Xbox", () =>
            {
                if (new FileInfo(marker).Length > 64 * 1024) return;
                foreach (var root in ParseGamingRoot(File.ReadAllBytes(marker), drive))
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var dir in Directory.EnumerateDirectories(root).Take(maxPerStore))
                    {
                        var info = new DirectoryInfo(dir);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        games.Add(new KnownGame(info.Name, "Xbox", dir, null));
                    }
                }
            });
        }

        // Mesma pasta vinda de duas fontes: fica a primeira.
        return games.Where(g => g.InstallDir is null || IsAbsoluteWindowsPath(g.InstallDir) || Path.IsPathFullyQualified(g.InstallDir))
            .GroupBy(g => g.InstallDir ?? g.ExeName ?? g.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static string SafeRead(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 1024 * 1024) return "";
        return File.ReadAllText(path);
    }
}
