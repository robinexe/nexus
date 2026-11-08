using Nexus.GameProfiles.Configs;

namespace Nexus.GameProfiles.Minecraft;

/// <summary>
/// Regras fixas do Minecraft no PC (pesquisa de 08/11/2026, docs/modules/minecraft.md). Duas edições, dois arquivos
/// <c>options.txt</c> com a mesma sintaxe <c>chave:valor</c>:
/// <list type="bullet">
/// <item><b>Java:</b> <c>%APPDATA%\.minecraft\options.txt</c> (jogo <c>minecraft</c> da lista embutida de <see cref="GameConfigs"/>);</item>
/// <item><b>Bedrock</b> (Microsoft Store): <c>%LOCALAPPDATA%\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang\minecraftpe\options.txt</c>
/// (jogo <c>minecraft-bedrock</c>, registrado aqui por <see cref="IGameConfigProvider"/>).</item>
/// </list>
/// Só o valor de linhas que o jogo já gravou, com o jogo fechado; valores do menu. A memória do Java (-Xmx em
/// launcher_profiles.json) fica de fora: o lançador regrava o JSON e a Mojang não documenta um valor melhor que o padrão.
/// </summary>
public static class MinecraftSettings
{
    public const string JavaId = "minecraft";
    public const string BedrockId = "minecraft-bedrock";

    /// <summary>Executáveis (o que desenha primeiro): Java roda no javaw.exe; Bedrock é Minecraft.Windows.exe; o lançador só abre.</summary>
    public static readonly IReadOnlyList<string> Executables = ["javaw.exe", "Minecraft.Windows.exe", "MinecraftLauncher.exe"];

    // ---- ações do catálogo (catalog/games.v1.json e catalog/minecraft.v1.json); o teste confere que existem e batem com a lista
    public const string JavaVsyncOffAction = "minecraft.cfg.vsync.off@1";
    public const string JavaMaxFpsUnlimitedAction = "minecraft.cfg.maxfps.unlimited@1";
    public const string JavaGraphicsFastAction = "minecraft.cfg.graphics.fast@1";
    public const string JavaCloudsOffAction = "minecraft.cfg.clouds.off@1";
    public const string JavaParticlesMinimalAction = "minecraft.cfg.particles.minimal@1";
    public const string JavaEntityShadowsOffAction = "minecraft.cfg.entity-shadows.off@1";
    public const string JavaRenderDistanceNearAction = "minecraft.cfg.render-distance.near@1";
    public const string JavaSimulationDistanceNearAction = "minecraft.cfg.simulation-distance.near@1";
    public const string BedrockVsyncOffAction = "minecraft.bedrock.vsync.off@1";

    /// <summary>Valores que as ações gravam (os mesmos de <see cref="GameConfigs"/>; o teste confere).</summary>
    public const string JavaMaxFpsUnlimited = "260";
    public const string JavaRenderDistanceNear = "8";
    public const string JavaSimulationDistanceNear = "6";

    /// <summary>Lista fechada da edição Bedrock: só o V-Sync (única chave confirmada por moderador da Mojang no rastreador de bugs).</summary>
    public static GameConfig Bedrock { get; } = new(BedrockId, "Minecraft (Bedrock)", ConfigRoot.LocalAppData,
        @"Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang\minecraftpe\options.txt", ConfigSyntax.Colon,
        ["Minecraft.Windows"],
        [
            new("gfx_vsync", ["0"]),
        ]);
}

/// <summary>Registra a edição Bedrock na lista fechada do handler GameConfigValue (a Java já está na lista embutida).</summary>
public sealed class MinecraftConfigProvider : IGameConfigProvider
{
    public IReadOnlyList<GameConfig> Games { get; } = [MinecraftSettings.Bedrock];
}
