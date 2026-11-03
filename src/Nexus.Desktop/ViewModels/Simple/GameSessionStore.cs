using System.IO;
using System.Text.Json;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>O que a Sessão de jogo lembra entre aberturas: ligada pelo usuário e a execução aplicada que ainda falta desfazer.</summary>
public sealed record GameSessionState(bool Enabled, Guid? PendingRun)
{
    public static readonly GameSessionState Off = new(false, null);
}

/// <summary>
/// %LocalAppData%\NEXUS\game-session.json (só preferência da interface; a mudança no Windows passa pelo aplicador). Arquivo
/// ausente/ilegível = desligada. Lógica pura (testada): a tela Jogos lê daqui a cada abertura, então "Ligada" sobrevive ao reinício.
/// </summary>
public sealed class GameSessionStore(string path)
{
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "game-session.json");

    public GameSessionState Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<GameSessionState>(File.ReadAllText(Path)) ?? GameSessionState.Off : GameSessionState.Off;
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
            File.WriteAllText(Path, JsonSerializer.Serialize(state));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
