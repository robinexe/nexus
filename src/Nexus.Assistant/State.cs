using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Broker;

namespace Nexus.Assistant;

public enum StepStatus { Pending, Done, Skipped }

/// <summary>Resultado de uma mudança pedida pelo assistente.</summary>
public enum ChangeOutcome { Applied, AlreadyConfigured, Refused, Canceled }

/// <summary>Uma execução do aplicador feita pelo assistente (para "Desfazer tudo", em ordem inversa).</summary>
public sealed record RunRecord(AssistantStepId Step, Guid RunId, BrokerLevel Level, DateTimeOffset At, int Actions, bool Reverted = false);

/// <summary>Uma mudança e o que aconteceu com ela (vai para o relatório).</summary>
public sealed record ChangeRecord(
    AssistantStepId Step,
    string Title,
    string? Before,
    string? After,
    BrokerLevel Level,
    string Risk,
    bool Undoable,
    ChangeOutcome Outcome,
    string? Detail,
    Guid? RunId);

/// <summary>Algo que ficou de fora e por quê (etapa pulada, item desmarcado, recusa do aplicador).</summary>
public sealed record SkipRecord(AssistantStepId Step, string What, string Why);

/// <summary>Um número antes/depois (ex.: espaço livre, placar de privacidade). Textos já formatados para o usuário.</summary>
public sealed record MetricRecord(string Label, string? Before, string? After, string? Note = null);

public sealed record StepRecord(StepStatus Status, string? Summary = null, IReadOnlyList<StepFact>? Facts = null);

/// <summary>Estado do assistente, salvo em %LocalAppData%\NEXUS\assistente\estado.json para parar e retomar.</summary>
public sealed class AssistantState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public AssistantStepId Current { get; set; } = AssistantSteps.First;
    public bool Finished { get; set; }
    public Dictionary<AssistantStepId, StepRecord> Steps { get; set; } = [];
    public List<RunRecord> Runs { get; set; } = [];
    public List<ChangeRecord> Changes { get; set; } = [];
    public List<SkipRecord> Skipped { get; set; } = [];
    public List<UserAction> UserActions { get; set; } = [];
    public Dictionary<string, MetricRecord> Metrics { get; set; } = [];
    /// <summary>Preferências do próprio NEXUS mudadas pelo assistente (ex.: medição automática), para desfazer: nome → valor anterior.</summary>
    public Dictionary<string, string> AppPreferences { get; set; } = [];
    public string? ReportPath { get; set; }

    public StepStatus StatusOf(AssistantStepId step) => Steps.TryGetValue(step, out var r) ? r.Status : StepStatus.Pending;

    public void SetMetric(string key, string label, string? before = null, string? after = null, string? note = null)
    {
        Metrics.TryGetValue(key, out var old);
        Metrics[key] = new MetricRecord(label, before ?? old?.Before, after ?? old?.After, note ?? old?.Note);
    }

    public void AddUserAction(UserAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (UserActions.All(a => a.Title != action.Title)) UserActions.Add(action);
    }

    public bool HasUndoableRuns => Runs.Any(r => !r.Reverted);
}

public enum LoadStatus { New, Resumed, Corrupt }

/// <summary>Lê e grava o estado. Arquivo ilegível ou de versão desconhecida: começa do zero e guarda a cópia ruim ao lado.</summary>
public sealed class AssistantStateStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public AssistantStateStore(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

    public string Path { get; }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "assistente", "estado.json");

    public bool Exists => File.Exists(Path);

    public (AssistantState State, LoadStatus Status) Load()
    {
        if (!File.Exists(Path)) return (new AssistantState(), LoadStatus.New);
        try
        {
            var state = JsonSerializer.Deserialize<AssistantState>(File.ReadAllText(Path), Json);
            if (state is null || state.Version != AssistantState.CurrentVersion || !Enum.IsDefined(state.Current)) throw new JsonException("versão ou etapa desconhecida");
            state.Steps ??= [];
            state.Runs ??= [];
            state.Changes ??= [];
            state.Skipped ??= [];
            state.UserActions ??= [];
            state.Metrics ??= [];
            state.AppPreferences ??= [];
            return (state, LoadStatus.Resumed);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            try { File.Move(Path, Path + $".corrompido-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException) { }
            return (new AssistantState(), LoadStatus.Corrupt);
        }
    }

    /// <summary>Grava de forma atômica (arquivo temporário + troca).</summary>
    public void Save(AssistantState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.UpdatedAt = DateTimeOffset.Now;
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, Path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

/// <summary>Máquina de etapas: avançar (concluída), pular (com motivo), voltar e ir a uma etapa já visitada.</summary>
public sealed class AssistantFlow
{
    public AssistantFlow(AssistantState state) => State = state ?? throw new ArgumentNullException(nameof(state));

    public AssistantState State { get; }
    public AssistantStepId Current => State.Current;
    public StepInfo CurrentInfo => AssistantSteps.Info(State.Current);
    public bool CanGoBack => AssistantSteps.Previous(State.Current) is not null;
    public bool IsLast => State.Current == AssistantSteps.Last;

    /// <summary>Etapas concluídas ou puladas (0–13).</summary>
    public int Completed => State.Steps.Values.Count(s => s.Status != StepStatus.Pending);

    /// <summary>Marca a etapa atual como concluída e vai para a próxima. Na última, encerra.</summary>
    public AssistantStepId Advance(string? summary = null, IReadOnlyList<StepFact>? facts = null)
    {
        State.Steps[State.Current] = new StepRecord(StepStatus.Done, summary, facts);
        return MoveNext();
    }

    /// <summary>Pula a etapa atual (registra o motivo) e vai para a próxima.</summary>
    public AssistantStepId Skip(string reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "Pulada por você." : reason;
        State.Steps[State.Current] = new StepRecord(StepStatus.Skipped, why);
        State.Skipped.RemoveAll(s => s.Step == State.Current && s.What == "Etapa inteira");
        State.Skipped.Add(new SkipRecord(State.Current, "Etapa inteira", why));
        return MoveNext();
    }

    /// <summary>Volta uma etapa (não desfaz nada; dá para refazer a etapa).</summary>
    public AssistantStepId Back()
    {
        if (AssistantSteps.Previous(State.Current) is { } prev) State.Current = prev;
        State.Finished = false;
        return State.Current;
    }

    /// <summary>Vai direto a uma etapa já concluída/pulada ou à primeira pendente (a barra de etapas usa isto).</summary>
    public bool GoTo(AssistantStepId step)
    {
        if (!Enum.IsDefined(step)) return false;
        var firstPending = AssistantSteps.All.Select(s => s.Id).FirstOrDefault(s => State.StatusOf(s) == StepStatus.Pending, AssistantSteps.Last);
        if (State.StatusOf(step) == StepStatus.Pending && step > firstPending) return false;
        State.Current = step;
        State.Finished = false;
        return true;
    }

    private AssistantStepId MoveNext()
    {
        if (AssistantSteps.Next(State.Current) is { } next) State.Current = next;
        else State.Finished = true;
        return State.Current;
    }
}
