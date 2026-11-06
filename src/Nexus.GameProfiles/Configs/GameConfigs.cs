namespace Nexus.GameProfiles.Configs;

/// <summary>Pasta do perfil onde o jogo guarda a configuração.</summary>
public enum ConfigRoot
{
    /// <summary>%APPDATA% (AppData\Roaming).</summary>
    Roaming,
    /// <summary>Documentos (também a pasta Documentos do OneDrive, quando o Windows a redireciona).</summary>
    Documents,
    /// <summary>Jogos Salvos (Saved Games).</summary>
    SavedGames,
    /// <summary>Pasta da Steam (InstallPath no registro da máquina): userdata\&lt;conta&gt;\730\local\cfg do CS2 (pasta "local" = da máquina, fora do Steam Cloud).</summary>
    Steam,
}

/// <summary>Uma chave que o NEXUS pode trocar e os únicos valores que ele grava (os do menu do próprio jogo).</summary>
public sealed record GameConfigKey(string Key, IReadOnlyList<string> Values);

/// <param name="Id">"minecraft", "r6", "ow2", "apex": o campo <c>game</c> do alvo no catálogo.</param>
/// <param name="RelativePath">Caminho a partir da raiz; um segmento "*" = uma subpasta (ex.: id da conta Ubisoft), que precisa ser única.</param>
/// <param name="Processes">Processos do jogo: aberto, a edição é recusada (ele regrava o arquivo ao sair).</param>
public sealed record GameConfig(string Id, string Name, ConfigRoot Root, string RelativePath, ConfigSyntax Syntax,
    IReadOnlyList<string> Processes, IReadOnlyList<GameConfigKey> Keys)
{
    public GameConfigKey? Find(string? key) =>
        key is null ? null : Keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Lista FECHADA de jogos e chaves que o handler "GameConfigValue" edita (pesquisa de 06/11/2026: arquivos de configuração que
/// o próprio jogo grava, editáveis com o jogo fechado e sem risco de anticheat; valores sempre dentro do menu do jogo). Ficam
/// de fora, só com orientação: VALORANT, League of Legends, Call of Duty, Marvel Rivals (anticheat/arquivo sincronizado na conta)
/// e o que o CS2 guarda no Steam Cloud (pasta "remote"; o NEXUS só mexe na pasta "local", da máquina)
/// e Rocket League (exigiria arquivo somente leitura). Diretriz: nunca limitar FPS; o máximo do menu ou "sem limite".
/// </summary>
public static class GameConfigs
{
    public const string HandlerKind = "GameConfigValue";

    public static IReadOnlyList<GameConfig> All { get; } =
    [
        new("minecraft", "Minecraft (Java)", ConfigRoot.Roaming, @".minecraft\options.txt", ConfigSyntax.Colon,
            ["javaw", "Minecraft", "MinecraftLauncher"],
            [
                new("enableVsync", ["false"]),
                new("maxFps", ["260"]),                // 260 = "Ilimitado" no controle deslizante
                new("graphicsMode", ["0"]),            // Rápido
                new("renderClouds", ["false"]),
                new("particles", ["2"]),               // Mínimo
                new("entityShadows", ["false"]),
            ]),
        new("r6", "Rainbow Six Siege", ConfigRoot.Documents, @"My Games\Rainbow Six - Siege\*\GameSettings.ini", ConfigSyntax.Equals,
            ["RainbowSix", "RainbowSix_BE", "RainbowSix_Vulkan", "RainbowSix_DX12"],
            [
                new("FPSLimit", ["0"]),                // abaixo de 30 = sem limite (comentário do próprio arquivo)
                new("NVReflex", ["2"]),                // ligado + boost
                new("VSync", ["0"]),
                new("MaxGPUBufferedFrame", ["1"]),     // padrão 3: menos quadros na fila = menos atraso
            ]),
        new("ow2", "Overwatch 2", ConfigRoot.Documents, @"Overwatch\Settings\Settings_v0.ini", ConfigSyntax.Equals,
            ["Overwatch"],
            [
                new("UseCustomFrameRates", ["1"]),
                new("FrameRateCap", ["600"]),          // máximo do menu
                new("LimitToRefresh", ["0"]),
                new("ReflexMode", ["2"]),              // ligado + boost
                new("DynamicRenderScale", ["0"]),
            ]),
        new("cs2video", "Counter-Strike 2", ConfigRoot.Steam, @"userdata\*\730\local\cfg\cs2_video.txt", ConfigSyntax.KeyValues,
            ["cs2"],
            [
                new("setting.mat_vsync", ["0"]),
                new("setting.r_low_latency", ["1"]),               // NVIDIA Reflex "Ativado"
                new("setting.videocfg_shadow_quality", ["0"]),
                new("setting.videocfg_particle_detail", ["0"]),
                new("setting.videocfg_ao_detail", ["0"]),
                new("setting.shaderquality", ["0"]),
                new("setting.msaa_samples", ["0"]),
            ]),
        new("cs2convars", "Counter-Strike 2", ConfigRoot.Steam, @"userdata\*\730\local\cfg\cs2_machine_convars.vcfg", ConfigSyntax.KeyValues,
            ["cs2"],
            [
                new("fps_max", ["0"]),                             // 0 = sem limite (o padrão do jogo é 400)
            ]),
        new("apex", "Apex Legends", ConfigRoot.SavedGames, @"Respawn\Apex\local\videoconfig.txt", ConfigSyntax.KeyValues,
            ["r5apex", "r5apex_dx12"],
            [
                new("setting.mat_vsync_mode", ["0"]),
                new("setting.ssao_enabled", ["0"]),
                new("setting.volumetric_lighting", ["0"]),
                new("setting.mat_depthfeather_enable", ["0"]),
                new("setting.csm_enabled", ["0"]),     // sombras do sol desligadas (valor do menu)
            ]),
    ];

    public static GameConfig? Find(string? id) => id is null ? null : All.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));

    /// <summary>Pastas candidatas da raiz a partir da pasta do perfil (C:\Users\nome).</summary>
    public static IEnumerable<string> RootCandidates(string profileDir, ConfigRoot root, string? steamDir = null)
    {
        ArgumentNullException.ThrowIfNull(profileDir);
        switch (root)
        {
            case ConfigRoot.Steam:
                if (steamDir is not null) yield return steamDir;
                break;
            case ConfigRoot.Roaming:
                yield return Path.Combine(profileDir, "AppData", "Roaming");
                break;
            case ConfigRoot.SavedGames:
                yield return Path.Combine(profileDir, "Saved Games");
                break;
            default:
                yield return Path.Combine(profileDir, "Documents");
                yield return Path.Combine(profileDir, "OneDrive", "Documents");
                yield return Path.Combine(profileDir, "OneDrive", "Documentos");
                yield return Path.Combine(profileDir, "OneDrive", "Documentos ");
                break;
        }
    }

    /// <summary>Resultado da busca do arquivo: o caminho, ou o motivo (não instalado / várias contas).</summary>
    public sealed record Located(string? Path, bool Ambiguous, string? Problem);

    /// <summary>
    /// Acha o arquivo do jogo no perfil. Segmento "*": entre as subpastas que têm o arquivo, precisa haver uma só (com várias
    /// contas, o NEXUS não escolhe por você). Nunca sai da pasta do perfil (links não são seguidos: <see cref="SafeFiles"/>).
    /// </summary>
    public static Located Locate(string profileDir, GameConfig game, string? steamDir = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        var parts = game.RelativePath.Split('\\');
        var found = new List<string>();
        foreach (var root in RootCandidates(profileDir, game.Root, steamDir).Where(Directory.Exists))
        {
            var star = Array.IndexOf(parts, "*");
            if (star < 0)
            {
                var p = Path.Combine([root, .. parts]);
                if (File.Exists(p)) found.Add(p);
                continue;
            }
            var parent = Path.Combine([root, .. parts[..star]]);
            if (!Directory.Exists(parent)) continue;
            foreach (var sub in Directory.EnumerateDirectories(parent))
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var p = Path.Combine([sub, .. parts[(star + 1)..]]);
                if (File.Exists(p)) found.Add(p);
            }
        }
        var distinct = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count switch
        {
            0 => new Located(null, false, $"{game.Name}: o arquivo de configuração não existe neste perfil (jogo não instalado ou nunca aberto)."),
            1 => new Located(SafeFiles.EnsureUnderRoot(game.Root == ConfigRoot.Steam ? steamDir! : profileDir, distinct[0]), false, null),
            _ => new Located(null, true, $"{game.Name}: há {distinct.Count} arquivos de configuração (várias contas); ajuste pelo menu do jogo."),
        };
    }
}
