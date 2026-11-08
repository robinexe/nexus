using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.CallOfDuty;

/// <summary>
/// Cartão do Call of Duty (Warzone / Black Ops / Modern Warfare, Battle.net ou Steam): só "Medir FPS" e "Comparar com e sem".
/// O arquivo de opções muda de nome a cada título (options.3.cod22.cst, options.4.cod23.cst, s.1.0.cod24.txt…), tem sintaxe
/// própria com comentário na mesma linha, os guias exigem marcá-lo como somente leitura para o jogo não repor o valor, e o jogo roda
/// sob o Ricochet (anticheat de kernel) sem posição da Activision sobre edição por programa. Na dúvida, nada é escrito.
/// Decisão e fontes: docs/modules/cod.md.
/// </summary>
public sealed class CallOfDutyCard : IGameCardProvider
{
    public const string Id = "cod";
    public const string GameExe = "cod.exe";

    public int Order => 25;

    public GameCardDefinition Card { get; } = new(
        Id,
        "Call of Duty",
        [GameExe],
        "#9AA37A",
        "CoD",
        null,
        [],
        "docs/modules/cod.md");
}
