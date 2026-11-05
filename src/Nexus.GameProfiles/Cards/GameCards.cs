namespace Nexus.GameProfiles.Cards;

/// <summary>
/// Contrato comum dos cartões de jogo (tela Jogos e Placa de vídeo › Ajustes por jogo). Cada jogo com ajustes próprios
/// registra um <see cref="GameCardDefinition"/> em <see cref="GameCardRegistry"/>; a interface monta o cartão a partir dele.
/// Toda mudança continua sendo uma ação de catálogo (<see cref="GameCardOption.ActionKey"/>) aplicada pelo aplicador, com
/// diário e desfazer. Textos: chaves de recurso do Desktop (Strings.resx/.en/.es), nunca texto fixo aqui.
/// </summary>
/// <param name="Id">Identificador estável em minúsculas (ex.: "fortnite", "valorant"). Também é o prefixo das ações no catálogo.</param>
/// <param name="DisplayName">Nome oficial do jogo (marca: não traduzido).</param>
/// <param name="Executables">Executáveis do jogo (o que desenha primeiro; lançadores/antitrapaça depois). Usado para detectar instalado/aberto.</param>
/// <param name="AccentHex">Cor de destaque do cartão (#RRGGBB), discreta, combinando com o tema escuro do NEXUS.</param>
/// <param name="Monogram">1–3 letras para o selo do cartão (sem logotipo/arte do jogo: marcas de terceiros não são desenhadas).</param>
/// <param name="CloseFirstNoteKey">Chave do aviso "feche o jogo antes" quando o jogo regrava o arquivo ao sair (null se não precisa).</param>
/// <param name="Options">Opções em linguagem simples, na ordem de importância.</param>
/// <param name="SourcesDocPath">Documento com as fontes (ex.: "docs/modules/fortnite.md").</param>
public sealed record GameCardDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Executables,
    string AccentHex,
    string Monogram,
    string? CloseFirstNoteKey,
    IReadOnlyList<GameCardOption> Options,
    string SourcesDocPath);

/// <summary>Uma opção do cartão: uma frase simples, um clique para aplicar, estado lido do jogo (Detect da ação).</summary>
/// <param name="Id">Identificador da opção dentro do jogo (ex.: "perf-mode").</param>
/// <param name="TitleKey">Chave de recurso do título curto ("Mais FPS em PC fraco").</param>
/// <param name="DetailKey">Chave de recurso de uma frase explicando o efeito e o custo.</param>
/// <param name="ActionKey">Ação do catálogo (id@versão). Várias ações juntas: use <see cref="ExtraActionKeys"/>.</param>
/// <param name="Value">Valor escolhido quando a ação tem allowedValues (ex.: limite de FPS); null quando o catálogo fixa o valor.</param>
/// <param name="Audience">Para quem a opção é indicada (a recomendação automática por hardware usa isto).</param>
/// <param name="ExtraActionKeys">Outras ações aplicadas no mesmo clique (mesmo desfazer).</param>
public sealed record GameCardOption(
    string Id,
    string TitleKey,
    string DetailKey,
    string ActionKey,
    string? Value = null,
    GameCardAudience Audience = GameCardAudience.Everyone,
    IReadOnlyList<string>? ExtraActionKeys = null);

/// <summary>Para quem a opção é indicada (usado pela recomendação automática por hardware).</summary>
[Flags]
public enum GameCardAudience
{
    Everyone = 0,
    WeakGpu = 1,          // vídeo integrado / placa de entrada / pouca VRAM
    StrongGpu = 2,        // placa intermediária ou melhor
    LowRam = 4,           // 8 GB ou menos
    Laptop = 8,
    Competitive = 16,     // prioriza menor atraso sobre imagem
    Nvidia = 32,
    Amd = 64,
    Intel = 128,
}

/// <summary>
/// Um jogo com cartão próprio: crie src/Nexus.GameProfiles/&lt;Jogo&gt;/&lt;Jogo&gt;Card.cs com uma classe sem parâmetros que
/// implemente esta interface. O registro acha todas sozinho (sem lista central para editar: vários jogos em paralelo sem conflito).
/// </summary>
public interface IGameCardProvider
{
    GameCardDefinition Card { get; }
    /// <summary>Ordem de exibição (menor primeiro; popularidade aproximada).</summary>
    int Order { get; }
}

/// <summary>Registro dos cartões: todas as classes <see cref="IGameCardProvider"/> deste assembly, por <see cref="IGameCardProvider.Order"/>.</summary>
public static class GameCardRegistry
{
    private static readonly Lazy<IReadOnlyList<GameCardDefinition>> Cards = new(() =>
        typeof(GameCardRegistry).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IGameCardProvider).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IGameCardProvider)Activator.CreateInstance(t)!)
            .OrderBy(p => p.Order).ThenBy(p => p.Card.DisplayName, StringComparer.Ordinal)
            .Select(p => p.Card)
            .ToList());

    public static IReadOnlyList<GameCardDefinition> All => Cards.Value;

    public static GameCardDefinition? Find(string id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
}
