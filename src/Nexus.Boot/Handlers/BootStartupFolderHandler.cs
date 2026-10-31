using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Startup;

namespace Nexus.Boot.Handlers;

/// <summary>
/// "BootStartupFolder": habilita/desabilita um atalho da pasta Inicializar pelo mesmo método do Gerenciador de Tarefas
/// (Explorer\StartupApproved\StartupFolder, valor com o nome do arquivo). O arquivo não é movido nem apagado.
/// Detect/Apply/Restore são os do <see cref="StartupApprovedHandler"/> (mesmo formato binário); aqui só a validação do alvo,
/// que o handler original limita a Run/Run32.
/// </summary>
public sealed partial class BootStartupFolderHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "BootStartupFolder";
    private readonly StartupApprovedHandler _inner;
    private readonly Func<RegHive, string?, string?> _folderOf;
    private readonly Func<string, bool> _exists;

    /// <param name="folderOf">Pasta Inicializar para (escopo, SID) — null quando não dá para saber com certeza (aí a existência
    /// do arquivo não é conferida; o valor em StartupApproved é inofensivo sem o arquivo).</param>
    public BootStartupFolderHandler(IRegistryBackend registry, Func<RegHive, string?, string?> folderOf, Func<string, bool> exists, TimeProvider? clock = null)
    {
        _inner = new StartupApprovedHandler(registry, clock);
        _folderOf = folderOf;
        _exists = exists;
    }

    public string HandlerKind => Kind;

    [GeneratedRegex(@"^[^\\/:*?""<>|\x00-\x1f]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex FilePattern();

    public static bool IsValidFileName(string? name) => name is not null && FilePattern().IsMatch(name) && name is not ("." or "..") && !name.EndsWith(' ');

    private static string? TargetProblem(ActionInstance action, out RegHive scope)
    {
        scope = RegHive.HKCU;
        if (!action.Target.TryGetValue("scope", out var s) || !Enum.TryParse(s, true, out scope)) return "Escopo inválido.";
        if (!action.Target.TryGetValue("fileName", out var f) || !IsValidFileName(f)) return "Nome de arquivo inválido.";
        return UserHive.Check(action, scope);
    }

    /// <summary>A mesma ação, no formato do StartupApprovedHandler (kind StartupFolder).</summary>
    private static ActionInstance Inner(ActionInstance action)
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scope"] = action.Target["scope"],
            ["kind"] = "StartupFolder",
            ["entryName"] = action.Target["fileName"],
        };
        if (action.Target.TryGetValue(UserHive.TargetKey, out var sid)) t[UserHive.TargetKey] = sid;
        return action with { Target = t };
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (TargetProblem(action, out var scope) is { } p) return Applicability.Blocked(p);
        if (action.Desired.Type != "StartupState" || action.Desired.Value is not ("enabled" or "disabled"))
            return Applicability.Blocked("Estado desejado deve ser enabled/disabled.");
        var folder = _folderOf(scope, UserHive.Of(action));
        if (folder is not null && !_exists(folder.TrimEnd('\\') + "\\" + action.Target["fileName"]))
            return Applicability.NotApplicable("Arquivo não encontrado na pasta Inicializar.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action) => _inner.Detect(Inner(action));

    public void Apply(ActionInstance action)
    {
        if (TargetProblem(action, out _) is { } p) throw new InvalidOperationException(p);
        _inner.Apply(Inner(action));
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        if (TargetProblem(action, out _) is { } p) return p;
        if (original.Exists && (original.Type != "StartupState" || original.Value is not ("enabled" or "disabled"))) return "Estado original inválido.";
        return null;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        if (CheckRestore(action, original) is { } p) throw new InvalidOperationException(p);
        _inner.Restore(Inner(action), original);
    }
}
