using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.Fortnite;

/// <summary>
/// Cartão do Fortnite na tela Jogos (contrato <see cref="IGameCardProvider"/>). As quatro opções são as mesmas de antes da
/// migração (mesmas ações, mesmo estado lido do GameUserSettings.ini): Modo desempenho, FPS máximo e menos atraso, imagem nítida,
/// efeitos leves. O Reflex só entra em placa NVIDIA (a tela decide pelo hardware lido). Fontes: docs/modules/fortnite.md.
/// </summary>
public sealed class FortniteCard : IGameCardProvider
{
    public const string Id = "fortnite";
    public const string PerfOption = "perf";
    public const string LatencyOption = "latency";
    public const string ClearOption = "clear";
    public const string LowFxOption = "lowfx";

    public int Order => 10;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Fortnite",
        [FortniteSettings.GameExe, .. FortniteSettings.BlockingProcesses.Where(p => !p.Equals("FortniteClient-Win64-Shipping", StringComparison.Ordinal)).Select(p => p + ".exe")],
        "#6E8BD9",
        "FN",
        "Card.Fn.Note",
        [
            new(PerfOption, "Card.Fn.Perf", "Card.Fn.Perf.Detail", FortniteSettings.PerformanceModeAction, null,
                GameCardAudience.WeakGpu, [FortniteSettings.RhiDx12Action]),
            new(LatencyOption, "Card.Fn.MaxFps", "Card.Fn.MaxFps.Detail", FortniteSettings.FpsLimitAction, FortniteSettings.FpsUnlimited,
                GameCardAudience.Everyone, [FortniteSettings.VSyncOffAction]),
            new(ClearOption, "Card.Fn.Clear", "Card.Fn.Clear.Detail", FortniteSettings.MotionBlurOffAction, null, GameCardAudience.Competitive),
            new(LowFxOption, "Card.Fn.LowFx", "Card.Fn.LowFx.Detail", FortniteSettings.ShadowsOffAction, null, GameCardAudience.WeakGpu,
                [FortniteSettings.GrassOffAction, FortniteSettings.EffectsLowAction, FortniteSettings.PostProcessLowAction, FortniteSettings.FoliageLowAction]),
        ],
        "docs/modules/fortnite.md");
}
