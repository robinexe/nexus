using Nexus.GameProfiles.Configs;

namespace Nexus.GameProfiles.Roblox;

/// <summary>
/// Regras fixas do Roblox no PC (pesquisa de 08/11/2026, docs/modules/roblox.md). O único arquivo editado é o das
/// configurações do próprio menu, <c>%LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml</c> (um elemento por linha,
/// <c>&lt;int name="FramerateCap"&gt;60&lt;/int&gt;</c>), com o Roblox fechado. <b>Nada de Fast Flags</b>: desde 29/09/2025 a
/// Roblox só aceita uma lista fechada de flags em ClientAppSettings.json e ignora as outras (o limite de FPS por flag,
/// DFIntTaskSchedulerTargetFps, ficou de fora); a recomendação oficial é a opção "Maximum Frame Rate" do menu, que é o que
/// este arquivo guarda. A versão da Microsoft Store guarda o arquivo em outra pasta (Packages\ROBLOXCORPORATION...) e fica de fora.
/// </summary>
public static class RobloxSettings
{
    public const string Id = "roblox";

    public static readonly IReadOnlyList<string> Executables = ["RobloxPlayerBeta.exe"];

    public const string FramerateCapKey = "FramerateCap";

    /// <summary>Valores do menu "Maximum Frame Rate" (anúncio da Roblox de 30/05/2024): 60, 120, 144 e 240. O NEXUS só grava o máximo.</summary>
    public const string FramerateCapMax = "240";

    public const string FpsMaxAction = "roblox.cfg.framerate-cap.max@1";

    public static GameConfig Game { get; } = new(Id, "Roblox", ConfigRoot.LocalAppData, @"Roblox\GlobalBasicSettings_13.xml", ConfigSyntax.XmlNamed,
        ["RobloxPlayerBeta", "RobloxPlayerLauncher", "RobloxCrashHandler"],
        [
            new(FramerateCapKey, [FramerateCapMax]),
        ]);
}

public sealed class RobloxConfigProvider : IGameConfigProvider
{
    public IReadOnlyList<GameConfig> Games { get; } = [RobloxSettings.Game];
}
