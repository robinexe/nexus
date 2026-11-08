using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.Valorant;

/// <summary>
/// Cartão do VALORANT (Riot): só "Medir FPS" e "Comparar com e sem". O NEXUS não edita o GameUserSettings.ini
/// (%LOCALAPPDATA%\VALORANT\Saved\Config\&lt;conta&gt;\Windows): a Riot guarda as configurações no servidor da conta
/// (mensagem do próprio jogo "changes to settings will not be saved on the server"), o jogo roda sob o Vanguard (anticheat de
/// kernel) e não há posição da Riot autorizando edição por programa. Decisão e fontes: docs/modules/valorant.md.
/// </summary>
public sealed class ValorantCard : IGameCardProvider
{
    public const string Id = "valorant";
    public const string GameExe = "VALORANT-Win64-Shipping.exe";

    public int Order => 20;

    public GameCardDefinition Card { get; } = new(
        Id,
        "VALORANT",
        [GameExe, "VALORANT.exe"],
        "#B07CC6",
        "VA",
        null,
        [],
        "docs/modules/valorant.md");
}
