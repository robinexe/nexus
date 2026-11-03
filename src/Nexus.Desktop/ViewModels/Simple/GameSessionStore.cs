using System.IO;
using System.Text.Json;
using Nexus.Broker;
using Nexus.Measurement;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>Uma execução do diário aplicada pelo modo jogo automático e ainda não desfeita (nível do aplicador que a gravou).</summary>
public sealed record PendingGameRun(Guid RunId, BrokerLevel Level);

/// <summary>
/// O que o modo jogo automático lembra entre aberturas: ligado pelo usuário (com confirmação), as ações escolhidas, a folga antes
/// de desfazer e as execuções aplicadas que ainda faltam desfazer (se o NEXUS fechar com o jogo aberto, desfaz na próxima abertura).
/// <paramref name="Enabled"/>/<paramref name="PendingRun"/> são o formato antigo da "Sessão de jogo" (uma execução de administrador);
/// continuam lidos para desfazer o que uma versão anterior deixou aplicado.
/// </summary>
public sealed record GameSessionState(bool Enabled, Guid? PendingRun)
{
    public static readonly GameSessionState Off = new(false, null);

    /// <summary>Chaves das ações escolhidas (lista fechada <see cref="GameModeActions"/>). Null = o usuário nunca escolheu: vale o padrão.</summary>
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>Execuções aplicadas na partida atual (ou na última, se o NEXUS fechou antes de desfazer).</summary>
    public IReadOnlyList<PendingGameRun>? PendingRuns { get; init; }

    /// <summary>Jogo pelo qual as execuções pendentes foram aplicadas (só para a tela).</summary>
    public string? PendingGame { get; init; }

    /// <summary>Folga, em segundos, entre o jogo fechar e o desfazer. Null = padrão (15 s).</summary>
    public int? StopDelaySeconds { get; init; }

    /// <summary>Tudo que falta desfazer: as execuções novas e, se houver, a da versão antiga (sempre do aplicador de administrador).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<PendingGameRun> AllPendingRuns
    {
        get
        {
            var runs = new List<PendingGameRun>(PendingRuns ?? []);
            if (PendingRun is { } legacy && runs.All(r => r.RunId != legacy)) runs.Add(new PendingGameRun(legacy, BrokerLevel.Admin));
            return runs;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPending => PendingRun is not null || PendingRuns is { Count: > 0 };

    /// <summary>Este estado sem nenhuma execução pendente (tudo desfeito).</summary>
    public GameSessionState Settled() => this with { PendingRun = null, PendingRuns = null, PendingGame = null };
}

/// <summary>
/// %LocalAppData%\NEXUS\game-session.json (só preferência da interface; a mudança no Windows passa pelo aplicador). Arquivo
/// ausente/ilegível = desligado. Lógica pura (testada): a tela Jogos lê daqui a cada abertura, então "Ligado" sobrevive ao reinício.
/// </summary>
public sealed class GameSessionStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "game-session.json");

    public GameSessionState Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<GameSessionState>(File.ReadAllText(Path), Options) ?? GameSessionState.Off : GameSessionState.Off;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return GameSessionState.Off; }
    }

    /// <summary>Falso = não deu para gravar (só perde a lembrança entre aberturas).</summary>
    public bool Save(GameSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(state, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
