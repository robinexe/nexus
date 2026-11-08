using Nexus.GameProfiles.Cards;
using Nexus.GameProfiles.Configs;

namespace Nexus.GameProfiles.Overwatch2;

/// <summary>
/// Cartão do Overwatch 2 (Battle.net). Arquivo: Documentos\Overwatch\Settings\Settings_v0.ini, editado pelo handler GameConfigValue
/// (lista fechada em <see cref="GameConfigs"/>, ações em catalog/games.v1.json). O jogo regrava o arquivo ao sair e só
/// grava as chaves que já saíram do padrão: chave ausente = "não se aplica" (nada é acrescentado). Fontes: docs/modules/overwatch2.md.
/// </summary>
public sealed class Overwatch2Card : IGameCardProvider
{
    public const string Id = "ow2";
    public const string GameExe = "Overwatch.exe";

    public const string FpsOption = "fps";
    public const string ReflexOption = "reflex";
    public const string SharpOption = "sharp";

    public const string CustomFrameRatesAction = "ow2.cfg.custom-framerate.on@1";
    public const string FrameRateCapAction = "ow2.cfg.framerate-cap.max@1";
    public const string LimitToRefreshOffAction = "ow2.cfg.limit-to-refresh.off@1";
    public const string ReflexBoostAction = "ow2.cfg.reflex.boost@1";
    public const string DynamicScaleOffAction = "ow2.cfg.dynamic-render-scale.off@1";

    public int Order => 40;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Overwatch 2",
        [GameExe, "Overwatch Launcher.exe"],
        "#7FB0D6",
        "OW",
        "Card.Ow2.CloseFirst",
        [
            new(FpsOption, "Card.Ow2.Fps", "Card.Ow2.Fps.Detail", FrameRateCapAction, null, GameCardAudience.Everyone,
                [CustomFrameRatesAction, LimitToRefreshOffAction]),
            new(ReflexOption, "Card.Ow2.Reflex", "Card.Ow2.Reflex.Detail", ReflexBoostAction, null, GameCardAudience.Nvidia),
            new(SharpOption, "Card.Ow2.Sharp", "Card.Ow2.Sharp.Detail", DynamicScaleOffAction, null, GameCardAudience.StrongGpu),
        ],
        "docs/modules/overwatch2.md");
}

/// <summary>Estado real das opções do Overwatch 2: as mesmas chaves e valores que as ações gravam (lista fechada <see cref="GameConfigs"/>, id "ow2").</summary>
public sealed class Overwatch2StateReader : GameConfigCardReader
{
    public override string GameId => Overwatch2Card.Id;

    protected override IReadOnlyDictionary<string, IReadOnlyList<ConfigExpectation>> Options { get; } = new Dictionary<string, IReadOnlyList<ConfigExpectation>>(StringComparer.Ordinal)
    {
        [Overwatch2Card.FpsOption] = [Expect("FrameRateCap"), Expect("UseCustomFrameRates"), Expect("LimitToRefresh")],
        [Overwatch2Card.ReflexOption] = [Expect("ReflexMode")],
        [Overwatch2Card.SharpOption] = [Expect("DynamicRenderScale")],
    };

    /// <summary>A expectativa sai da lista fechada (um valor só por chave): o que a tela compara é o que o aplicador grava.</summary>
    public static ConfigExpectation Expect(string key)
    {
        var k = GameConfigs.Find(Overwatch2Card.Id)!.Find(key) ?? throw new InvalidOperationException($"Overwatch 2: '{key}' fora da lista.");
        return new ConfigExpectation(Overwatch2Card.Id, k.Key, k.Values.Single());
    }
}
