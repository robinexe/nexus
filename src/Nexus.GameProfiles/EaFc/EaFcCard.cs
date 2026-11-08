using Nexus.GameProfiles.Cards;

namespace Nexus.GameProfiles.EaFc;

/// <summary>
/// Cartão do EA SPORTS FC (FC 25 e FC 26): só Medir/Comparar e as opções genéricas. Sem ação de arquivo (pesquisa de
/// 08/11/2026, docs/modules/eafc.md): o fcsetup.ini (%LOCALAPPDATA%\EA SPORTS FC 25 ou FC26\settings) muda de nome de chave a
/// cada edição (WAITFORVSYNC → WAIT_FOR_VSYNC, RENDERINGQUALITY → RENDERING_QUALITY), é regravado pelo jogo (relatos de volta
/// ao padrão a cada abertura) e a EA não documenta o arquivo; o que o NEXUS gravasse poderia ser desfeito pelo jogo sem aviso,
/// e os guias dependem de arquivo somente leitura, o que o NEXUS não faz. Os mitos estão em catalog/eafc.v1.json ("rejected").
/// </summary>
public sealed class EaFcCard : IGameCardProvider
{
    public const string Id = "eafc";

    public static readonly IReadOnlyList<string> Executables = ["FC26.exe", "FC25.exe", "FC24.exe"];

    public int Order => 50;

    public GameCardDefinition Card { get; } = new(
        Id,
        "EA SPORTS FC",
        Executables,
        "#3F8F6B",
        "FC",
        null,
        [],
        "docs/modules/eafc.md");
}
