using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using Nexus.Core;

namespace Nexus.Automation;

/// <summary>Acesso ao Agendador do Windows, só para a tarefa do NEXUS (nome fixo). Testes usam um falso.</summary>
public interface ITaskSchedulerBackend
{
    /// <summary>XML da tarefa, ou null se ela não existe. Lança exceção se não deu para saber.</summary>
    string? QueryXml(string taskName);
    void CreateOrReplace(string taskName, string xml);
    void Delete(string taskName);
}

/// <summary>
/// Agendador via <c>%SystemRoot%\System32\schtasks.exe</c>, com argumentos em lista (sem shell) e o XML num arquivo próprio
/// (UTF-16, como o schtasks espera). Roda na conta de quem chama: a tarefa é do usuário, sem elevação.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SchtasksBackend : ITaskSchedulerBackend
{
    private readonly string _workDirectory;
    private static string Exe => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <param name="workDirectory">Pasta para o XML temporário (ex.: <c>DataDirectory\automation</c>).</param>
    public SchtasksBackend(string workDirectory) => _workDirectory = workDirectory;

    public string? QueryXml(string taskName)
    {
        // Lista (sem cabeçalho, CSV) para distinguir "não existe" de "erro": a mensagem de erro do schtasks é traduzida.
        var (code, list, err) = Run("/query", "/fo", "csv", "/nh");
        if (code != 0) throw new InvalidOperationException($"Não foi possível listar as tarefas agendadas: {err.Trim()}");
        if (!list.Contains($"\"\\{taskName}\"", StringComparison.OrdinalIgnoreCase)) return null;
        var (code2, xml, err2) = Run("/query", "/tn", taskName, "/xml", "ONE");
        if (code2 != 0) throw new InvalidOperationException($"Não foi possível ler a tarefa {taskName}: {err2.Trim()}");
        return xml;
    }

    public void CreateOrReplace(string taskName, string xml)
    {
        Directory.CreateDirectory(_workDirectory);
        var file = Path.Combine(_workDirectory, $"task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            var (code, _, err) = Run("/create", "/tn", taskName, "/xml", file, "/f");
            if (code != 0) throw new InvalidOperationException($"O Agendador recusou a tarefa: {err.Trim()}");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Delete(string taskName)
    {
        var (code, _, err) = Run("/delete", "/tn", taskName, "/f");
        if (code != 0) throw new InvalidOperationException($"Não foi possível remover a tarefa: {err.Trim()}");
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks.exe não iniciou.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            throw new TimeoutException("schtasks.exe não respondeu em 30 s.");
        }
        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}

/// <summary>
/// Handler da verificação semanal agendada (ação <c>automation.scheduled-scan.weekly</c>).
/// Estado: Type "ScanSchedule", Value "SUN 19:00" (tarefa do NEXUS em dia, apontando para este nexus.exe), "OFF" (não existe),
/// "STALE" (do NEXUS, mas com outro horário de programa/caminho) ou "FOREIGN" (outra tarefa com o mesmo nome: nunca é tocada).
/// Detail["xml"] guarda o XML de uma tarefa do NEXUS para a restauração recriá-la exatamente.
/// </summary>
public sealed class AutomationScheduledScanHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "AutomationScheduledScan";
    public const string StateType = "ScanSchedule";
    public const string Stale = "STALE";
    public const string Foreign = "FOREIGN";

    private readonly ITaskSchedulerBackend _backend;
    private readonly Func<string?> _executable;
    private readonly bool _otherAccount;
    private readonly TimeProvider _clock;

    /// <param name="executable">Caminho do nexus.exe que a tarefa vai rodar (null = não encontrado).</param>
    /// <param name="otherAccount">O aplicador roda com outra conta: a tarefa seria criada para a conta errada, então recusa.</param>
    public AutomationScheduledScanHandler(ITaskSchedulerBackend backend, Func<string?> executable, bool otherAccount, TimeProvider? clock = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        _otherAccount = otherAccount;
        _clock = clock ?? TimeProvider.System;
    }

    public string HandlerKind => Kind;

    /// <summary>nexus.exe ao lado do aplicador (o broker é copiado junto da interface e da linha de comando).</summary>
    public static string? DefaultExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ScheduledScanTask.ExecutableName);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string? TargetProblem(ActionInstance a) =>
        a.Target.TryGetValue("taskName", out var n) && n == ScheduledScanTask.TaskName ? null : $"Só a tarefa {ScheduledScanTask.TaskName} pode ser gerenciada.";

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action) is { } t) return Applicability.Blocked(t);
        if (action.Desired.Type != StateType || (action.Desired.Value != ScanSchedule.Off && ScanSchedule.TryParse(action.Desired.Value) is null))
            return Applicability.Blocked("Valor desejado deve ser OFF ou \"DIA HH:MM\".");
        if (_otherAccount)
            return Applicability.Blocked("A tarefa é da sua conta: use o aplicador do usuário (não o serviço nem outra conta de administrador).");
        if (action.Desired.Value != ScanSchedule.Off)
        {
            var exe = _executable();
            if (exe is null) return Applicability.NotApplicable("nexus.exe não está instalado ao lado do aplicador; a verificação agendada precisa dele.");
            if (ScheduledScanTask.ValidateExecutable(exe) is { } problem) return Applicability.Blocked(problem);
        }
        var current = Detect(action);
        if (current.Value == Foreign)
            return Applicability.Blocked($"Já existe uma tarefa chamada {ScheduledScanTask.TaskName} que não foi criada pelo NEXUS; ela não será tocada.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var xml = _backend.QueryXml(ScheduledScanTask.TaskName);
        if (xml is null)
            return StateSnapshot.Of(StateType, ScanSchedule.Off, ValueOrigin.Explicit, new Dictionary<string, string> { ["present"] = "0" });
        ScheduledTaskInfo info;
        try { info = ScheduledScanTask.Inspect(xml); }
        catch (XmlException) { return StateSnapshot.Of(StateType, Foreign, ValueOrigin.ThirdPartyManaged); }
        if (!info.Ours) return StateSnapshot.Of(StateType, Foreign, ValueOrigin.ThirdPartyManaged);

        var detail = new Dictionary<string, string> { ["present"] = "1" };
        if (xml.Length <= ScheduledScanTask.MaxXmlChars) detail["xml"] = xml;
        var exe = _executable();
        var current = info.Schedule is not null && exe is not null
            && string.Equals(info.Command, exe, StringComparison.OrdinalIgnoreCase)
            && info.Arguments == ScheduledScanTask.Arguments;
        return StateSnapshot.Of(StateType, current ? info.Schedule!.ToString() : Stale, ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (TargetProblem(action) is { } t) throw new InvalidOperationException(t);
        var now = Detect(action);
        if (now.Value == Foreign) throw new InvalidOperationException("Tarefa com o mesmo nome não é do NEXUS; nada foi alterado.");
        if (action.Desired.Value == ScanSchedule.Off)
        {
            if (now.Value != ScanSchedule.Off) _backend.Delete(ScheduledScanTask.TaskName);
            return;
        }
        var schedule = ScanSchedule.TryParse(action.Desired.Value) ?? throw new InvalidOperationException("Horário inválido.");
        var exe = _executable() ?? throw new InvalidOperationException("nexus.exe não encontrado.");
        _backend.CreateOrReplace(ScheduledScanTask.TaskName, ScheduledScanTask.BuildXml(exe, schedule, _clock.GetLocalNow().DateTime));
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        var now = Detect(action);
        if (now.Value == Foreign) throw new InvalidOperationException("Tarefa com o mesmo nome não é do NEXUS; nada foi alterado.");
        if (original.Value == ScanSchedule.Off)
        {
            if (now.Value != ScanSchedule.Off) _backend.Delete(ScheduledScanTask.TaskName);
            return;
        }
        _backend.CreateOrReplace(ScheduledScanTask.TaskName, original.Detail!["xml"]);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (TargetProblem(action) is { } t) return t;
        if (!original.Exists || original.Type != StateType) return "Estado original com formato desconhecido.";
        if (original.Value == ScanSchedule.Off) return null;
        if (original.Value == Foreign) return "O estado original era uma tarefa de terceiros; o NEXUS não a recria.";
        if (original.Value != Stale && ScanSchedule.TryParse(original.Value) is null) return "Horário original inválido.";
        if (original.Detail is null || !original.Detail.TryGetValue("xml", out var xml)) return "O diário não guardou o XML da tarefa anterior.";
        try
        {
            var info = ScheduledScanTask.Inspect(xml);
            if (!info.Ours) return "O XML guardado não é de uma tarefa do NEXUS.";
            if (ScheduledScanTask.ValidateExecutable(info.Command) is { } bad) return bad;
            if (info.Arguments != ScheduledScanTask.Arguments) return "O XML guardado roda outros argumentos.";
        }
        catch (XmlException ex) { return $"XML guardado inválido: {ex.Message}"; }
        return null;
    }
}
