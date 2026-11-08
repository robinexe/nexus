using Nexus.GameProfiles.Configs;
using Nexus.GameProfiles.Fortnite;

namespace Nexus.GameProfiles.Cards;

/// <summary>Estado real de uma opção do cartão: ligada (como a ação deixa), desligada (outro valor), ou sem leitura (arquivo/chave ausente).</summary>
public enum GameCardOptionState { Unknown, Off, On }

/// <summary>O que a leitura de estado precisa saber da máquina (nos testes: pastas temporárias).</summary>
/// <param name="ProfileDir">Pasta do perfil (C:\Users\nome) ou null quando não identificada.</param>
/// <param name="SteamDir">Pasta da Steam ou null.</param>
public sealed record GameCardStateContext(string? ProfileDir, string? SteamDir = null);

/// <summary>
/// Lê o estado real das opções de UM cartão, do mesmo arquivo que a ação escreve (só leitura, nunca grava). Uma classe sem
/// parâmetros por jogo, achada por reflexão como os cartões (<see cref="GameCardStateRegistry"/>). Falha de leitura = sem leitura.
/// </summary>
public interface IGameCardStateReader
{
    /// <summary>Id do cartão (<see cref="GameCardDefinition.Id"/>).</summary>
    string GameId { get; }
    GameCardOptionState Read(string optionId, GameCardStateContext context);
}

/// <summary>Registro dos leitores de estado: todas as classes <see cref="IGameCardStateReader"/> deste assembly, por id do jogo.</summary>
public static class GameCardStateRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, IGameCardStateReader>> Readers = new(() =>
        typeof(GameCardStateRegistry).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IGameCardStateReader).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IGameCardStateReader)Activator.CreateInstance(t)!)
            .ToDictionary(r => r.GameId, StringComparer.Ordinal));

    public static IGameCardStateReader? Find(string gameId) => Readers.Value.GetValueOrDefault(gameId);

    /// <summary>Estado de uma opção: sem leitor para o jogo = sem leitura (a tela mostra o botão sem estado, nunca "desligado").</summary>
    public static GameCardOptionState Read(string gameId, string optionId, GameCardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reader = Find(gameId);
        if (reader is null) return GameCardOptionState.Unknown;
        try { return reader.Read(optionId, context); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return GameCardOptionState.Unknown; }
    }
}

/// <summary>Uma chave de um jogo da lista fechada e o valor que a opção grava (para comparar com o que está no arquivo).</summary>
public sealed record ConfigExpectation(string GameId, string Key, string Desired);

/// <summary>
/// Leitor de estado para cartões cujas opções são só ações do handler GameConfigValue: cada opção é uma lista de
/// <see cref="ConfigExpectation"/>. Ligada quando TODAS as chaves estão no valor da ação; desligada quando alguma está com
/// outro valor; sem leitura quando o arquivo não existe (jogo não instalado), várias contas, ou chave ausente nesta versão.
/// Mesma localização e mesmo parser do handler (<see cref="GameConfigs.Locate"/>, <see cref="ConfigLines"/>): o que a tela mostra é o
/// que o aplicador leria.
/// </summary>
public abstract class GameConfigCardReader : IGameCardStateReader
{
    public abstract string GameId { get; }

    /// <summary>Opção → chaves esperadas. Opção fora do mapa (ex.: só Medir/Comparar) = sem leitura.</summary>
    protected abstract IReadOnlyDictionary<string, IReadOnlyList<ConfigExpectation>> Options { get; }

    public GameCardOptionState Read(string optionId, GameCardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(optionId);
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.TryGetValue(optionId, out var expectations) || expectations.Count == 0) return GameCardOptionState.Unknown;
        var states = expectations.Select(e => ReadKey(e, context)).ToList();
        if (states.All(s => s == GameCardOptionState.On)) return GameCardOptionState.On;
        return states.Contains(GameCardOptionState.Off) ? GameCardOptionState.Off : GameCardOptionState.Unknown;
    }

    /// <summary>Valor atual de uma chave (null = arquivo ou chave ausente, várias contas, erro de leitura).</summary>
    public static string? CurrentValue(string gameId, string key, GameCardStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var game = GameConfigs.Find(gameId);
        if (game is null || context.ProfileDir is null) return null;
        try
        {
            var where = GameConfigs.Locate(context.ProfileDir, game, game.Root == ConfigRoot.Steam ? context.SteamDir : null);
            if (where.Path is null) return null;
            var read = IniFiles.Read(where.Path);
            var (state, value) = ConfigLines.Parse(read.Text, game.Syntax).Get(key);
            return state == ConfigKeyState.Present ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static GameCardOptionState ReadKey(ConfigExpectation e, GameCardStateContext context)
    {
        var value = CurrentValue(e.GameId, e.Key, context);
        if (value is null) return GameCardOptionState.Unknown;
        return string.Equals(value, e.Desired, StringComparison.Ordinal) ? GameCardOptionState.On : GameCardOptionState.Off;
    }
}
