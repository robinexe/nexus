using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.GtaV;

/// <summary>
/// Cartão do GTA V (Legacy e Enhanced). Cada opção aplica a mesma chave nos dois settings.xml: a edição que não está
/// instalada responde "não se aplica" e não muda nada, então um clique serve para quem tem uma ou as duas.
/// </summary>
public sealed class GtaVCard : IGameCardProvider
{
    public const string Id = GtaVSettings.LegacyId;
    public const string OptionVsyncOff = "vsync-off";
    public const string OptionKeepRunning = "keep-running";

    public int Order => 30;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Grand Theft Auto V",
        GtaVSettings.Executables,
        "#7A9A6D",
        "GTA",
        "Card.gtav.CloseFirst",
        [
            new(OptionVsyncOff, "Card.gtav.VsyncOff.Title", "Card.gtav.VsyncOff.Detail",
                GtaVSettings.LegacyVsyncOffAction, ExtraActionKeys: [GtaVSettings.EnhancedVsyncOffAction]),
            new(OptionKeepRunning, "Card.gtav.KeepRunning.Title", "Card.gtav.KeepRunning.Detail",
                GtaVSettings.LegacyKeepRunningAction, ExtraActionKeys: [GtaVSettings.EnhancedKeepRunningAction]),
        ],
        "docs/modules/gtav.md");
}

/// <summary>Estado: ligada quando a edição instalada (uma ou as duas) está no valor; a edição ausente não conta.</summary>
public sealed class GtaVCardState : IGameCardStateReader
{
    public string GameId => GtaVCard.Id;

    private static readonly Dictionary<string, string> KeyByOption = new(StringComparer.Ordinal)
    {
        [GtaVCard.OptionVsyncOff] = GtaVSettings.VSyncKey,
        [GtaVCard.OptionKeepRunning] = GtaVSettings.PauseOnFocusLossKey,
    };

    public GameCardOptionState Read(string optionId, GameCardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(optionId);
        if (!KeyByOption.TryGetValue(optionId, out var key)) return GameCardOptionState.Unknown;
        var values = new[] { GtaVSettings.LegacyId, GtaVSettings.EnhancedId }
            .Select(id => GameConfigCardReader.CurrentValue(id, key, context)).Where(v => v is not null).ToList();
        if (values.Count == 0) return GameCardOptionState.Unknown;
        return values.All(v => v == "0") ? GameCardOptionState.On : GameCardOptionState.Off;
    }
}
