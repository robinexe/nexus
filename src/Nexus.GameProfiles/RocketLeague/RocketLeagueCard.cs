using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.RocketLeague;

/// <summary>
/// Cartão do Rocket League: só Medir/Comparar e as opções genéricas (driver, placa de alto desempenho). Sem ação de arquivo
/// (pesquisa de 08/11/2026, docs/modules/rocketleague.md): o jogo regrava TASystemSettings.ini ao abrir, por isso os
/// "tweaks" exigem marcar o arquivo como somente leitura, e a própria Epic diz que o arquivo somente leitura é o que impede o
/// jogo de salvar as configurações. O limite de FPS e o V-Sync ficam no menu do jogo (Vídeo), não num arquivo que o NEXUS
/// possa editar com segurança. Os mitos estão em catalog/rocketleague.v1.json ("rejected").
/// </summary>
public sealed class RocketLeagueCard : IGameCardProvider
{
    public const string Id = "rocketleague";

    public static readonly IReadOnlyList<string> Executables = ["RocketLeague.exe", "RocketLeague_BE.exe"];

    public int Order => 40;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Rocket League",
        Executables,
        "#4F7FB5",
        "RL",
        null,
        [],
        "docs/modules/rocketleague.md");
}
