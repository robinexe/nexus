using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.Roblox;

/// <summary>Cartão do Roblox: uma opção (FPS máximo do menu, 240) pelo arquivo do próprio menu; o resto é Medir/Comparar.</summary>
public sealed class RobloxCard : IGameCardProvider
{
    public const string Id = RobloxSettings.Id;
    public const string OptionFpsMax = "fps-max";

    public int Order => 20;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Roblox",
        RobloxSettings.Executables,
        "#8A8F9C",
        "RB",
        "Card.roblox.CloseFirst",
        [
            new(OptionFpsMax, "Card.roblox.FpsMax.Title", "Card.roblox.FpsMax.Detail", RobloxSettings.FpsMaxAction),
        ],
        "docs/modules/roblox.md");
}

public sealed class RobloxCardState : GameConfigCardReader
{
    public override string GameId => RobloxCard.Id;

    protected override IReadOnlyDictionary<string, IReadOnlyList<ConfigExpectation>> Options { get; } = new Dictionary<string, IReadOnlyList<ConfigExpectation>>(StringComparer.Ordinal)
    {
        [RobloxCard.OptionFpsMax] = [new(RobloxSettings.Id, RobloxSettings.FramerateCapKey, RobloxSettings.FramerateCapMax)],
    };
}
