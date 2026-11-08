using Nexus.GameProfiles.Cards;
using Nexus.GameProfiles.Configs;

namespace Nexus.GameProfiles.Apex;

/// <summary>
/// Cartão do Apex Legends (Steam ou EA app). Arquivo: Jogos Salvos\Respawn\Apex\local\videoconfig.txt (KeyValues), editado pelo
/// handler GameConfigValue (lista fechada em <see cref="GameConfigs"/>, ações em catalog/games.v1.json). Opções de
/// lançamento da Steam/EA (+fps_max, -novid…) ficam de fora: são da loja, não do jogo, e o NEXUS não edita a biblioteca da loja
/// (explicação em docs/modules/apex.md).
/// </summary>
public sealed class ApexCard : IGameCardProvider
{
    public const string Id = "apex";
    public const string GameExe = "r5apex.exe";

    public const string VSyncOption = "vsync";
    public const string LowFxOption = "lowfx";

    public const string VSyncOffAction = "apex.cfg.vsync.off@1";
    public const string SsaoOffAction = "apex.cfg.ssao.off@1";
    public const string VolumetricOffAction = "apex.cfg.volumetric-lighting.off@1";
    public const string DepthFeatherOffAction = "apex.cfg.depth-feather.off@1";
    public const string SunShadowsOffAction = "apex.cfg.sun-shadows.off@1";

    public int Order => 50;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Apex Legends",
        [GameExe, "r5apex_dx12.exe", "r5apex_launcher.exe"],
        "#D09A5B",
        "AP",
        "Card.Apex.CloseFirst",
        [
            new(VSyncOption, "Card.Apex.VSync", "Card.Apex.VSync.Detail", VSyncOffAction, null, GameCardAudience.Competitive),
            new(LowFxOption, "Card.Apex.LowFx", "Card.Apex.LowFx.Detail", SsaoOffAction, null, GameCardAudience.WeakGpu,
                [VolumetricOffAction, DepthFeatherOffAction, SunShadowsOffAction]),
        ],
        "docs/modules/apex.md");
}

/// <summary>Estado real das opções do Apex Legends: as mesmas chaves e valores que as ações gravam (lista fechada <see cref="GameConfigs"/>, id "apex").</summary>
public sealed class ApexStateReader : GameConfigCardReader
{
    public override string GameId => ApexCard.Id;

    protected override IReadOnlyDictionary<string, IReadOnlyList<ConfigExpectation>> Options { get; } = new Dictionary<string, IReadOnlyList<ConfigExpectation>>(StringComparer.Ordinal)
    {
        [ApexCard.VSyncOption] = [Expect("setting.mat_vsync_mode")],
        [ApexCard.LowFxOption] = [Expect("setting.ssao_enabled"), Expect("setting.volumetric_lighting"), Expect("setting.mat_depthfeather_enable"), Expect("setting.csm_enabled")],
    };

    public static ConfigExpectation Expect(string key)
    {
        var k = GameConfigs.Find(ApexCard.Id)!.Find(key) ?? throw new InvalidOperationException($"Apex Legends: '{key}' fora da lista.");
        return new ConfigExpectation(ApexCard.Id, k.Key, k.Values.Single());
    }
}
