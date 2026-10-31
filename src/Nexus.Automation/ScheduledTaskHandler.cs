using System.Xml;
using Nexus.Core;

namespace Nexus.Automation;

/// <summary>
/// Handler genérico das tarefas agendadas do NEXUS (ações <c>automation.schedule.scan</c> e <c>automation.schedule.clean</c>).
/// Alvo: <c>{ task: "scan"|"clean", taskName, schedule: "{schedule}", conditions: "{conditions}" }</c> (o horário e as condições
/// são parâmetros do usuário validados pela regex do catálogo e de novo aqui). Valor desejado: "ON" ou "OFF".
/// Estado: "OFF" (não existe), "ON" (tarefa do NEXUS exatamente como o alvo pede, apontando para este nexus.exe), "DIFFERENT"
/// (do NEXUS, mas com outro horário, condições ou pasta) ou "FOREIGN" (tarefa de outro programa com o mesmo nome: nunca tocada).
/// Detail["xml"] guarda o XML de uma tarefa do NEXUS para a restauração recriá-la exatamente.
/// </summary>
public sealed class AutomationScheduledTaskHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "AutomationScheduledTask";
    public const string StateType = "TaskSchedule";
    public const string On = "ON";
    public const string Off = "OFF";
    public const string Different = "DIFFERENT";
    public const string Foreign = "FOREIGN";

    private readonly ITaskSchedulerBackend _backend;
    private readonly Func<string?> _executable;
    private readonly bool _otherAccount;
    private readonly TimeProvider _clock;

    public AutomationScheduledTaskHandler(ITaskSchedulerBackend backend, Func<string?> executable, bool otherAccount, TimeProvider? clock = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        _otherAccount = otherAccount;
        _clock = clock ?? TimeProvider.System;
    }

    public string HandlerKind => Kind;

    private sealed record Parsed(NexusTaskKind Task, TaskSchedule Schedule, TaskConditions Conditions);

    /// <summary>Lê e valida o alvo. Nada do alvo vira texto livre no XML: só o tipo (lista fechada), o horário e as condições.</summary>
    private static Parsed? ParseTarget(ActionInstance a, out string? problem)
    {
        problem = null;
        var t = a.Target;
        var kind = NexusTaskKind.Find(t.GetValueOrDefault("task"));
        if (kind is null) { problem = "Tipo de tarefa desconhecido."; return null; }
        if (t.GetValueOrDefault("taskName") != kind.TaskName) { problem = $"Só a tarefa {kind.TaskName} pode ser gerenciada."; return null; }
        var schedule = TaskSchedule.TryParse(t.GetValueOrDefault("schedule"));
        if (schedule is null) { problem = "Horário fora do formato (ex.: \"WEEKLY SUN 19:00\")."; return null; }
        var conditions = TaskConditions.TryParse(t.GetValueOrDefault("conditions"));
        if (conditions is null) { problem = "Condições fora do formato."; return null; }
        return new Parsed(kind, schedule, conditions);
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var p = ParseTarget(action, out var problem);
        if (p is null) return Applicability.Blocked(problem!);
        if (action.Desired.Type != StateType || action.Desired.Value is not (On or Off))
            return Applicability.Blocked("Valor desejado deve ser ON ou OFF.");
        if (_otherAccount)
            return Applicability.Blocked("A tarefa é da sua conta: use o aplicador do usuário (não o serviço nem outra conta de administrador).");
        if (action.Desired.Value == On)
        {
            var exe = _executable();
            if (exe is null) return Applicability.NotApplicable("nexus.exe não está ao lado do aplicador; a tarefa agendada precisa dele.");
            if (ScheduledScanTask.ValidateExecutable(exe) is { } bad) return Applicability.Blocked(bad);
        }
        if (Detect(action).Value == Foreign)
            return Applicability.Blocked($"Já existe uma tarefa chamada {p.Task.TaskName} que não foi criada pelo NEXUS; ela não será tocada.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var p = ParseTarget(action, out var problem) ?? throw new InvalidOperationException(problem);
        var xml = _backend.QueryXml(p.Task.TaskName);
        if (xml is null) return StateSnapshot.Of(StateType, Off, ValueOrigin.Explicit, new Dictionary<string, string> { ["present"] = "0" });
        NexusTaskInfo info;
        try { info = NexusTaskXml.Inspect(xml, p.Task); }
        catch (XmlException) { return StateSnapshot.Of(StateType, Foreign, ValueOrigin.ThirdPartyManaged); }
        if (!info.Ours) return StateSnapshot.Of(StateType, Foreign, ValueOrigin.ThirdPartyManaged);

        var detail = new Dictionary<string, string> { ["present"] = "1" };
        if (xml.Length <= ScheduledScanTask.MaxXmlChars) detail["xml"] = xml;
        if (info.Schedule is not null) detail["schedule"] = info.Schedule.ToString();
        if (info.Conditions is not null) detail["conditions"] = info.Conditions.ToString();
        var exe = _executable();
        var same = exe is not null
                   && string.Equals(info.Command, exe, StringComparison.OrdinalIgnoreCase)
                   && info.Schedule == p.Schedule
                   && info.Conditions == p.Conditions
                   && info.DisallowOnBatteries == p.Conditions.SkipOnBattery;
        return StateSnapshot.Of(StateType, same ? On : Different, ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var p = ParseTarget(action, out var problem) ?? throw new InvalidOperationException(problem);
        var now = Detect(action);
        if (now.Value == Foreign) throw new InvalidOperationException("Tarefa com o mesmo nome não é do NEXUS; nada foi alterado.");
        if (action.Desired.Value == Off)
        {
            if (now.Value != Off) _backend.Delete(p.Task.TaskName);
            return;
        }
        if (action.Desired.Value != On) throw new InvalidOperationException("Valor desejado deve ser ON ou OFF.");
        var exe = _executable() ?? throw new InvalidOperationException("nexus.exe não encontrado.");
        _backend.CreateOrReplace(p.Task.TaskName, NexusTaskXml.Build(p.Task, exe, p.Schedule, p.Conditions, _clock.GetLocalNow().DateTime));
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        var p = ParseTarget(action, out _)!;
        var now = Detect(action);
        if (now.Value == Foreign) throw new InvalidOperationException("Tarefa com o mesmo nome não é do NEXUS; nada foi alterado.");
        if (original.Value == Off)
        {
            if (now.Value != Off) _backend.Delete(p.Task.TaskName);
            return;
        }
        _backend.CreateOrReplace(p.Task.TaskName, original.Detail!["xml"]);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        var p = ParseTarget(action, out var problem);
        if (p is null) return problem;
        if (!original.Exists || original.Type != StateType) return "Estado original com formato desconhecido.";
        if (original.Value == Off) return null;
        if (original.Value == Foreign) return "O estado original era uma tarefa de terceiros; o NEXUS não a recria.";
        if (original.Value is not (On or Different)) return "Estado original inválido.";
        if (original.Detail is null || !original.Detail.TryGetValue("xml", out var xml)) return "O diário não guardou o XML da tarefa anterior.";
        try
        {
            var info = NexusTaskXml.Inspect(xml, p.Task);
            if (!info.Ours) return "O XML guardado não é de uma tarefa do NEXUS deste tipo.";
            if (ScheduledScanTask.ValidateExecutable(info.Command) is { } bad) return bad;
            if (info.Conditions is null) return "O XML guardado roda outros argumentos.";
            if (info.Schedule is null) return "O XML guardado tem um horário que o NEXUS não gera.";
        }
        catch (XmlException ex) { return $"XML guardado inválido: {ex.Message}"; }
        return null;
    }
}

/// <summary>Estado de uma tarefa do NEXUS lido do Agendador (para a interface e a linha de comando; somente leitura).</summary>
public sealed record NexusTaskStatus(NexusTaskKind Kind, string State, TaskSchedule? Schedule, TaskConditions? Conditions, bool PointsHere, string? Error)
{
    public bool IsOn => State is AutomationScheduledTaskHandler.On or AutomationScheduledTaskHandler.Different;

    public string Describe() => Error is not null ? $"Não foi possível ler o Agendador: {Error}" : State switch
    {
        AutomationScheduledTaskHandler.Off => "Desligada.",
        AutomationScheduledTaskHandler.Foreign => $"Existe uma tarefa \"{Kind.TaskName}\" que não é do NEXUS; ela não será tocada.",
        _ => $"Ligada: {Schedule?.Describe() ?? "horário que o NEXUS não reconhece"}"
             + (Conditions is null ? "" : $" ({Conditions.Describe()})")
             + (PointsHere ? "." : ". Aponta para outra pasta do NEXUS: salve de novo para atualizar."),
    };
}
