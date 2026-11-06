using Nexus.Core;
using Nexus.GameProfiles.Fortnite;

namespace Nexus.GameProfiles.Configs;

/// <summary>
/// Handler "GameConfigValue": uma chave do arquivo de configuração de um jogo da lista fechada <see cref="GameConfigs"/>, no
/// perfil de quem pede.
/// <list type="bullet">
/// <item>Alvo: <c>scope</c>=User (o aplicador como serviço anexa <c>userSid</c>), <c>game</c>, <c>key</c>. O caminho sai do
/// perfil (ProfileList pelo SID) e da lista, nunca do pedido; não sai da pasta do perfil e não segue links.</item>
/// <item>Desejado: tipo <c>ConfigValue</c>, um dos valores da chave na lista (os do menu do jogo).</item>
/// <item>Recusa com o jogo aberto, arquivo somente leitura, codificação desconhecida ou chave repetida. Nunca cria arquivo nem
/// acrescenta chave: só troca o valor de uma linha que o jogo já gravou (sem o arquivo ou sem a chave = não se aplica).</item>
/// <item>Desfazer devolve o valor original exato da linha.</item>
/// </list>
/// </summary>
public sealed class GameConfigHandler : IActionHandler, IRestoreValidator
{
    public const string StateType = "ConfigValue";

    private readonly Func<string?, string?> _profileDir;
    private readonly Func<IReadOnlyList<string>, bool> _anyRunning;
    private readonly bool _otherAccount;
    private readonly Func<string?> _steamDir;

    /// <param name="profileDirForSid">Pasta do perfil (C:\Users\nome) do SID; sem SID, a da conta do aplicador. Null = não identificado.</param>
    /// <param name="anyRunning">Algum destes processos está aberto (na dúvida, responde que sim).</param>
    /// <param name="steamDir">Pasta da Steam (registro da máquina) ou null; só o CS2 usa.</param>
    public GameConfigHandler(Func<string?, string?> profileDirForSid, Func<IReadOnlyList<string>, bool> anyRunning, bool otherAccount, Func<string?>? steamDir = null)
    {
        _steamDir = steamDir ?? (() => null);
        _profileDir = profileDirForSid ?? throw new ArgumentNullException(nameof(profileDirForSid));
        _anyRunning = anyRunning ?? throw new ArgumentNullException(nameof(anyRunning));
        _otherAccount = otherAccount;
    }

    public string HandlerKind => GameConfigs.HandlerKind;

    private string? TargetProblem(ActionInstance action, out GameConfig? game, out GameConfigKey? key, out string? sid)
    {
        game = null; key = null; sid = null;
        var t = action.Target;
        if (!t.TryGetValue("scope", out var scope) || !UserProfileScope.IsUserScopeName(scope)) return "Ajuste de jogo sem escopo de usuário no catálogo.";
        if (t.Keys.Any(k => k is not ("scope" or "game" or "key" or UserProfileScope.TargetKey))) return "Alvo com campos desconhecidos.";
        game = GameConfigs.Find(t.GetValueOrDefault("game"));
        if (game is null) return "Jogo fora da lista permitida.";
        key = game.Find(t.GetValueOrDefault("key"));
        if (key is null) return $"{game.Name}: chave fora da lista permitida.";
        if (t.TryGetValue(UserProfileScope.TargetKey, out var s))
        {
            if (!UserProfileScope.IsValidSid(s)) return "Perfil de usuário inválido no alvo.";
            sid = s;
        }
        else if (_otherAccount)
        {
            return "O aplicador roda com outra conta e o pedido não identificou o seu perfil; nada foi lido.";
        }
        return null;
    }

    private (GameConfigs.Located Where, GameConfig Game, GameConfigKey Key) Locate(ActionInstance action)
    {
        if (TargetProblem(action, out var game, out var key, out var sid) is { } problem) throw new InvalidOperationException(problem);
        var profile = _profileDir(sid) ?? throw new InvalidOperationException("Não foi possível identificar a pasta do seu perfil.");
        return (GameConfigs.Locate(profile, game!, game!.Root == ConfigRoot.Steam ? _steamDir() : null), game!, key!);
    }

    private static string RunningMessage(GameConfig g) => $"Feche o {g.Name} antes: aberto, ele regrava a configuração ao sair e a mudança se perderia.";

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (TargetProblem(action, out var game, out var key, out _) is { } problem) return Applicability.Blocked(problem);
        if (!action.Desired.Exists || !string.Equals(action.Desired.Type, StateType, StringComparison.Ordinal) ||
            !key!.Values.Contains(action.Desired.Value ?? "", StringComparer.Ordinal))
            return Applicability.Blocked($"Valor '{action.Desired.Value}' não é aceito para {key?.Key}.");

        GameConfigs.Located where;
        try { where = Locate(action).Where; }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException) { return Applicability.Blocked(ex.Message); }
        if (where.Ambiguous) return Applicability.Blocked(where.Problem!);
        if (where.Path is null) return Applicability.NotApplicable(where.Problem!);

        IniFiles.IniRead read;
        try { read = IniFiles.Read(where.Path); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException)
        {
            return Applicability.Blocked($"{game!.Name}: codificação do arquivo não reconhecida; edição recusada para não corromper ({ex.Message}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Applicability.Blocked($"{game!.Name}: não deu para ler a configuração: {ex.Message}"); }
        if (read.ReadOnly)
            return Applicability.Blocked($"{game!.Name}: o arquivo de configuração está como somente leitura; desmarque em Propriedades para o jogo (e o NEXUS) poderem salvar.");
        var (state, _) = ConfigLines.Parse(read.Text, game!.Syntax).Get(key!.Key);
        if (state == ConfigKeyState.Ambiguous) return Applicability.Blocked($"{game.Name}: '{key.Key}' aparece mais de uma vez; ajuste pelo menu do jogo.");
        if (state == ConfigKeyState.Absent) return Applicability.NotApplicable($"{game.Name}: esta versão do jogo não grava '{key.Key}'.");
        if (_anyRunning(game.Processes)) return Applicability.Blocked(RunningMessage(game));
        return Applicability.Ok;
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (TargetProblem(action, out _, out _, out _) is { } problem) return problem;
        if (!original.Exists) return null; // a chave não existia: nada a devolver (o editor nunca a criou)
        if (!string.Equals(original.Type, StateType, StringComparison.Ordinal) || !ConfigLines.IsSafeValue(original.Value)) return "Valor original inválido no diário.";
        return null;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var (where, game, key) = Locate(action);
        if (where.Path is null) return StateSnapshot.Absent(new Dictionary<string, string> { ["file"] = "ausente" });
        var read = IniFiles.Read(where.Path);
        var (state, value) = ConfigLines.Parse(read.Text, game.Syntax).Get(key.Key);
        if (state == ConfigKeyState.Ambiguous) throw new InvalidDataException($"'{key.Key}' aparece mais de uma vez em {game.Name}.");
        var detail = new Dictionary<string, string> { ["sha256"] = read.Sha256 };
        return state == ConfigKeyState.Present ? StateSnapshot.Of(StateType, value!, ValueOrigin.Explicit, detail) : StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TargetProblem(action, out _, out var key, out _);
        if (!action.Desired.Exists || key is null || !key.Values.Contains(action.Desired.Value ?? "", StringComparer.Ordinal))
            throw new InvalidOperationException("Valor fora da lista permitida.");
        Edit(action, action.Desired.Value!);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (!original.Exists) return;
        if (!ConfigLines.IsSafeValue(original.Value)) throw new InvalidOperationException("Valor original inválido.");
        Edit(action, original.Value!);
    }

    private void Edit(ActionInstance action, string value)
    {
        var (where, game, key) = Locate(action);
        if (where.Path is null) throw new FileNotFoundException(where.Problem ?? "Arquivo de configuração não encontrado; nada foi criado.");
        if (_anyRunning(game.Processes)) throw new InvalidOperationException(RunningMessage(game));
        var read = IniFiles.Read(where.Path);
        var doc = ConfigLines.Parse(read.Text, game.Syntax);
        if (!doc.Set(key.Key, value)) throw new InvalidOperationException($"{game.Name}: '{key.Key}' não está no arquivo; nada foi acrescentado.");
        IniFiles.WriteAtomic(where.Path, doc.ToString(), read.Encoding, read.Sha256);
    }
}
