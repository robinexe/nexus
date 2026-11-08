using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.Minecraft;

/// <summary>
/// Cartão do Minecraft (Java e Bedrock) na tela Jogos. Opções em linguagem simples; cada clique = ações do catálogo
/// (handler GameConfigValue) com diário e desfazer. Textos nas chaves Card.minecraft.* de Strings.resx/.en/.es.
/// </summary>
public sealed class MinecraftCard : IGameCardProvider
{
    public const string Id = MinecraftSettings.JavaId;

    public const string OptionJavaFps = "java-fps";
    public const string OptionJavaLight = "java-light";
    public const string OptionJavaNear = "java-near";
    public const string OptionBedrockVsync = "bedrock-vsync";

    public int Order => 10;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Minecraft",
        MinecraftSettings.Executables,
        "#5B8C3E",
        "MC",
        "Card.minecraft.CloseFirst",
        [
            new(OptionJavaFps, "Card.minecraft.JavaFps.Title", "Card.minecraft.JavaFps.Detail",
                MinecraftSettings.JavaVsyncOffAction, ExtraActionKeys: [MinecraftSettings.JavaMaxFpsUnlimitedAction]),
            new(OptionJavaLight, "Card.minecraft.JavaLight.Title", "Card.minecraft.JavaLight.Detail",
                MinecraftSettings.JavaGraphicsFastAction, Audience: GameCardAudience.WeakGpu | GameCardAudience.Laptop,
                ExtraActionKeys: [MinecraftSettings.JavaCloudsOffAction, MinecraftSettings.JavaParticlesMinimalAction, MinecraftSettings.JavaEntityShadowsOffAction]),
            new(OptionJavaNear, "Card.minecraft.JavaNear.Title", "Card.minecraft.JavaNear.Detail",
                MinecraftSettings.JavaRenderDistanceNearAction, Audience: GameCardAudience.WeakGpu | GameCardAudience.LowRam,
                ExtraActionKeys: [MinecraftSettings.JavaSimulationDistanceNearAction]),
            new(OptionBedrockVsync, "Card.minecraft.BedrockVsync.Title", "Card.minecraft.BedrockVsync.Detail",
                MinecraftSettings.BedrockVsyncOffAction),
        ],
        "docs/modules/minecraft.md");
}

/// <summary>Estado real das opções do Minecraft, lido dos dois options.txt (só leitura).</summary>
public sealed class MinecraftCardState : GameConfigCardReader
{
    public override string GameId => MinecraftCard.Id;

    protected override IReadOnlyDictionary<string, IReadOnlyList<ConfigExpectation>> Options { get; } = new Dictionary<string, IReadOnlyList<ConfigExpectation>>(StringComparer.Ordinal)
    {
        [MinecraftCard.OptionJavaFps] =
        [
            new(MinecraftSettings.JavaId, "enableVsync", "false"),
            new(MinecraftSettings.JavaId, "maxFps", MinecraftSettings.JavaMaxFpsUnlimited),
        ],
        [MinecraftCard.OptionJavaLight] =
        [
            new(MinecraftSettings.JavaId, "graphicsMode", "0"),
            new(MinecraftSettings.JavaId, "renderClouds", "false"),
            new(MinecraftSettings.JavaId, "particles", "2"),
            new(MinecraftSettings.JavaId, "entityShadows", "false"),
        ],
        [MinecraftCard.OptionJavaNear] =
        [
            new(MinecraftSettings.JavaId, "renderDistance", MinecraftSettings.JavaRenderDistanceNear),
            new(MinecraftSettings.JavaId, "simulationDistance", MinecraftSettings.JavaSimulationDistanceNear),
        ],
        [MinecraftCard.OptionBedrockVsync] =
        [
            new(MinecraftSettings.BedrockId, "gfx_vsync", "0"),
        ],
    };
}
