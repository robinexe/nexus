using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.LeagueOfLegends;

/// <summary>
/// Cartão do League of Legends (Riot): só "Medir FPS" e "Comparar com e sem". O game.cfg fica na pasta do jogo
/// (C:\Riot Games\League of Legends\Config), fora do perfil do usuário (o handler de configuração só escreve dentro do perfil),
/// as preferências da conta (PersistedSettings.json) sincronizam com o servidor e o jogo roda sob o Vanguard. Sem posição da Riot
/// autorizando edição por programa, nada é escrito. Decisão e fontes: docs/modules/lol.md.
/// </summary>
public sealed class LeagueCard : IGameCardProvider
{
    public const string Id = "lol";
    public const string GameExe = "League of Legends.exe";

    public int Order => 30;

    public GameCardDefinition Card { get; } = new(
        Id,
        "League of Legends",
        [GameExe, "LeagueClient.exe", "LeagueClientUx.exe"],
        "#5EB8A6",
        "LoL",
        null,
        [],
        "docs/modules/lol.md");
}
