using System.Text.RegularExpressions;
using Nexus.Boot.Inventory;
using Nexus.Core;

namespace Nexus.Boot.Handlers;

/// <summary>Leitura e mudança do estado (habilitada/desabilitada) de uma tarefa agendada.</summary>
public interface IScheduledTaskBackend
{
    /// <summary>Null se a tarefa não existe (ou não é visível para o aplicador).</summary>
    ScheduledTaskInfo? Find(string taskPath);
    void SetEnabled(string taskPath, bool enabled);
}

/// <summary>
/// "BootScheduledTask": habilita/desabilita uma tarefa agendada que roda no logon ou no boot (mesmo efeito do botão
/// "Desabilitar" do Agendador de Tarefas: a tarefa continua lá, só não dispara). Tarefas da Microsoft (\Microsoft\...) e
/// tarefas sem gatilho de logon/boot são recusadas. Snapshot: Type "TaskState", Value "enabled"/"disabled".
/// </summary>
public sealed partial class BootScheduledTaskHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "BootScheduledTask";
    private readonly IScheduledTaskBackend _tasks;

    public BootScheduledTaskHandler(IScheduledTaskBackend tasks) => _tasks = tasks;

    public string HandlerKind => Kind;

    // \pasta\...\nome: até 8 níveis; sem caracteres proibidos em nomes de tarefa nem ".." como nível.
    [GeneratedRegex(@"^(\\[^\\/:*?""<>|\x00-\x1f]{1,120}){1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    public static bool IsValidPath(string? path) =>
        path is not null && PathPattern().IsMatch(path) && !path.Split('\\').Any(p => p is "." or "..") && !path.EndsWith(' ');

    private static string? TargetProblem(ActionInstance action, out string path)
    {
        path = action.Target.TryGetValue("taskPath", out var p) ? p : "";
        if (!IsValidPath(path)) return "Caminho de tarefa inválido.";
        if (BootTaskRules.IsMicrosoftPath(path)) return "Tarefas da Microsoft fazem parte do Windows: o NEXUS não as desliga.";
        return null;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (TargetProblem(action, out var path) is { } problem) return Applicability.Blocked(problem);
        if (action.Desired.Type != "TaskState" || action.Desired.Value is not ("enabled" or "disabled"))
            return Applicability.Blocked("Estado desejado deve ser enabled/disabled.");
        var task = _tasks.Find(path);
        if (task is null) return Applicability.NotApplicable("Tarefa não encontrada.");
        if (!task.IsStartupTask) return Applicability.Blocked("A tarefa não roda no logon nem no boot: fora do escopo da inicialização.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var task = _tasks.Find(action.Target["taskPath"]);
        return task is null ? StateSnapshot.Absent() : StateSnapshot.Of("TaskState", task.Enabled ? "enabled" : "disabled");
    }

    public void Apply(ActionInstance action)
    {
        if (TargetProblem(action, out var path) is { } problem) throw new InvalidOperationException(problem);
        _tasks.SetEnabled(path, action.Desired.Value == "enabled");
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        if (TargetProblem(action, out _) is { } problem) return problem;
        if (!original.Exists) return "A tarefa não existia no backup; nada a restaurar.";
        if (original.Type != "TaskState" || original.Value is not ("enabled" or "disabled")) return "Estado original inválido.";
        return null;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        _tasks.SetEnabled(action.Target["taskPath"], original.Value == "enabled");
    }
}
