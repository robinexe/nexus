using Nexus.Core;

namespace Nexus.GameProfiles.Fortnite;

/// <summary>
/// Handler "FortniteIniValue": uma chave do GameUserSettings.ini do Fortnite no perfil de quem pede.
/// <list type="bullet">
/// <item>Alvo: <c>scope</c>=User (o broker-serviço anexa <c>userSid</c>), <c>section</c>, <c>key</c>. Só as chaves de
/// <see cref="FortniteSettings.Allowed"/>; o caminho do arquivo é fixo e sai do perfil (ProfileList pelo SID), nunca do pedido.</item>
/// <item>Desejado: tipo <c>IniValue</c>, valor aceito pela regra da chave. Ausente = a chave não está no arquivo.</item>
/// <item>Recusa com o jogo aberto (ele regrava o arquivo ao sair e a mudança se perderia), com o arquivo somente leitura,
/// com codificação desconhecida e com a chave escrita de forma ambígua. Nunca cria o arquivo: sem ele, "abra o jogo uma vez".</item>
/// <item>Escrita sem perdas (<see cref="IniDocument"/>): só a linha da chave muda; codificação, quebras de linha e o resto ficam.
/// Desfazer devolve o valor original exato, ou remove a linha se ela não existia.</item>
/// </list>
/// </summary>
public sealed class FortniteIniHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "FortniteIniValue";
    public const string StateType = "IniValue";

    private readonly Func<string?, string?> _localAppData;
    private readonly Func<bool> _gameRunning;
    private readonly bool _otherAccount;

    /// <param name="localAppDataForSid">AppData\Local do perfil: com SID (aplicador como serviço), o daquele usuário; sem SID, o da
    /// conta do próprio aplicador. Null = não deu para identificar com segurança (nada é lido).</param>
    /// <param name="gameRunning">Algum processo de <see cref="FortniteSettings.BlockingProcesses"/> está aberto.</param>
    /// <param name="otherAccount">O aplicador roda com outra conta: sem SID no alvo, recusa (não adivinha o perfil).</param>
    public FortniteIniHandler(Func<string?, string?> localAppDataForSid, Func<bool> gameRunning, bool otherAccount)
    {
        _localAppData = localAppDataForSid ?? throw new ArgumentNullException(nameof(localAppDataForSid));
        _gameRunning = gameRunning ?? throw new ArgumentNullException(nameof(gameRunning));
        _otherAccount = otherAccount;
    }

    public string HandlerKind => Kind;

    public const string RunningMessage = "Feche o Fortnite antes: com o jogo aberto ele regrava o GameUserSettings.ini ao sair e a mudança se perderia.";

    private string? TargetProblem(ActionInstance action, out FortniteKey? key, out string? sid)
    {
        key = null; sid = null;
        var t = action.Target;
        if (!t.TryGetValue("scope", out var scope) || !UserProfileScope.IsUserScopeName(scope)) return "Ação do Fortnite sem escopo de usuário no catálogo.";
        t.TryGetValue("section", out var section);
        t.TryGetValue("key", out var name);
        key = FortniteSettings.Find(section, name);
        if (key is null) return $"Chave fora da lista permitida do Fortnite: [{section}] {name}.";
        if (t.Keys.Any(k => k is not ("scope" or "section" or "key" or UserProfileScope.TargetKey))) return "Alvo com campos desconhecidos.";
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

    private string PathOf(ActionInstance action)
    {
        if (TargetProblem(action, out _, out var sid) is { } problem) throw new InvalidOperationException(problem);
        var local = _localAppData(sid) ?? throw new InvalidOperationException("Não foi possível identificar a pasta AppData\\Local do seu perfil.");
        return SafeFiles.EnsureUnderRoot(local, FortniteSettings.FilePath(local));
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (TargetProblem(action, out var key, out _) is { } problem) return Applicability.Blocked(problem);
        if (action.Desired.Exists && (!string.Equals(action.Desired.Type, StateType, StringComparison.Ordinal) || !key!.Accepts(action.Desired.Value ?? "")))
            return Applicability.Blocked($"Valor '{action.Desired.Value}' não é aceito para {key!.Key}.");
        if (!action.Desired.Exists) return Applicability.Blocked("Esta ação só escreve um valor; remover a chave é só para o desfazer.");

        string path;
        try { path = PathOf(action); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { return Applicability.Blocked(ex.Message); }

        IniFiles.IniRead read;
        try { read = IniFiles.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException)
        {
            return Applicability.Blocked($"Codificação do GameUserSettings.ini não reconhecida; edição recusada para não corromper o arquivo ({ex.Message}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Applicability.Blocked($"Não deu para ler o GameUserSettings.ini: {ex.Message}"); }
        if (!read.Exists)
            return Applicability.NotApplicable("O Fortnite ainda não criou o GameUserSettings.ini neste perfil: abra o jogo uma vez, feche e tente de novo.");
        if (read.ReadOnly)
            return Applicability.Blocked("O GameUserSettings.ini está marcado como somente leitura (alguns guias mandam fazer isso). Desmarque em Propriedades do arquivo; " +
                                         "assim o próprio jogo também consegue salvar o que você muda no menu.");
        if (_gameRunning()) return Applicability.Blocked(RunningMessage);
        if (IniDocument.Parse(read.Text).Get(key!.Section, key.Key).State == IniKeyState.Ambiguous)
            return Applicability.Blocked($"'{key.Key}' aparece repetida ou como lista em [{key.Section}]; edite pelo menu do jogo para não mudar outra preferência.");
        return Applicability.Ok;
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (TargetProblem(action, out _, out _) is { } problem) return problem;
        if (original.Exists && (!string.Equals(original.Type, StateType, StringComparison.Ordinal) || !IniDocument.IsSafeValue(original.Value)))
            return "Valor original inválido no diário.";
        try { _ = PathOf(action); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { return ex.Message; }
        return null;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TargetProblem(action, out var key, out _);
        var read = IniFiles.Read(PathOf(action));
        var detail = new Dictionary<string, string> { ["sha256"] = read.Sha256, ["fileExisted"] = read.Exists ? "true" : "false" };
        var (state, value) = IniDocument.Parse(read.Text).Get(key!.Section, key.Key);
        if (state == IniKeyState.Ambiguous) throw new InvalidDataException($"'{key.Key}' aparece de forma ambígua em [{key.Section}].");
        return state == IniKeyState.Present ? StateSnapshot.Of(StateType, value!, ValueOrigin.Explicit, detail) : StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TargetProblem(action, out var key, out _);
        if (!action.Desired.Exists || !key!.Accepts(action.Desired.Value ?? "")) throw new InvalidOperationException("Valor fora da lista permitida.");
        Edit(action, (doc, k) => doc.Set(k.Section, k.Key, action.Desired.Value!));
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Exists && !IniDocument.IsSafeValue(original.Value)) throw new InvalidOperationException("Valor original inválido.");
        Edit(action, (doc, k) =>
        {
            if (original.Exists) doc.Set(k.Section, k.Key, original.Value!);
            else doc.Remove(k.Section, k.Key);
        });
    }

    private void Edit(ActionInstance action, Action<IniDocument, FortniteKey> change)
    {
        if (TargetProblem(action, out var key, out _) is { } problem) throw new InvalidOperationException(problem);
        if (_gameRunning()) throw new InvalidOperationException(RunningMessage);
        var path = PathOf(action);
        var read = IniFiles.Read(path);
        if (!read.Exists) throw new FileNotFoundException("GameUserSettings.ini não existe neste perfil; nada foi criado.", path);
        var doc = IniDocument.Parse(read.Text);
        change(doc, key!);
        IniFiles.WriteAtomic(path, doc.ToString(), read.Encoding, read.Sha256);
    }
}
