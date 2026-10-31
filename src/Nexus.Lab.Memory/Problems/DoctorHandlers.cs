using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Lab.Memory.Problems;

// ============================================================================ backends (interfaces; Windows em WindowsDoctorBackends.cs)

/// <summary>Esvaziar a lista de espera (cache) da memória. Lança em caso de falha.</summary>
public interface IStandbyListBackend
{
    void PurgeStandby();
}

/// <summary>Identidade de um processo: PID + nome + horário de início (o PID sozinho pode ser reaproveitado).</summary>
public sealed record ProcessIdentity(int Pid, string Name, long StartFileTimeUtc, int SessionId, string? Path);

public interface IProcessControlBackend
{
    /// <summary>Null se o processo não existe (ou não pode ser lido).</summary>
    ProcessIdentity? Find(int pid);
    /// <summary>EmptyWorkingSet: tira da RAM as páginas do processo (elas continuam comprometidas; voltam quando usadas).</summary>
    void Trim(int pid);
    /// <summary>Pede para a janela principal fechar (como clicar no X). True se o processo terminou dentro do prazo.</summary>
    bool CloseGracefully(int pid, TimeSpan wait);
    /// <summary>Encerra à força (como "Finalizar tarefa").</summary>
    void Terminate(int pid);
}

/// <summary>Estado de execução de um serviço.</summary>
public sealed record ServiceRunInfo(string Name, string State, int Pid, bool SharesProcess, int RunningDependents, bool CanStop);

public interface IServiceControlBackend
{
    /// <summary>Null se o serviço não existe.</summary>
    ServiceRunInfo? Query(string service);
    void StopService(string service, TimeSpan timeout);
    void StartService(string service, TimeSpan timeout);
}

/// <summary>Configuração do Agente de Gerenciamento de Memória (Get-/Enable-/Disable-MMAgent).</summary>
public interface IMemoryAgentBackend
{
    /// <summary>Null se não foi possível ler.</summary>
    bool? GetCompression();
    void SetCompression(bool enabled);
}

// ============================================================================ base efêmera

/// <summary>
/// Ação sem estado anterior a guardar (catálogo <c>"persistence": "Ephemeral"</c>): o executor registra no diário, executa e
/// considera feito se <see cref="Run"/> não lançar; desfazer responde "nada a desfazer". Mesmo contrato das ações efêmeras do
/// módulo Ferramentas do sistema (docs/02-architecture.md, "Ações efêmeras").
/// </summary>
public abstract class MemoryEphemeralHandler : IActionHandler, IRestoreValidator
{
    public const string StateType = "Ephemeral";
    public const string Ready = "pronto";
    public const string Done = "executado";

    public abstract string HandlerKind { get; }

    protected abstract void Run(ActionInstance action);

    protected virtual Applicability AssessCore(ActionInstance action, SystemContext context) => Applicability.Ok;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (action.Desired.Type != StateType || action.Desired.Value != Done)
            return Applicability.Blocked("Valor desejado inválido para uma ação efêmera.");
        if (action.Definition.Persistence != PersistenceKind.Ephemeral)
            return Applicability.Blocked("Ação sem estado precisa ser declarada \"persistence\": \"Ephemeral\" no catálogo.");
        return AssessCore(action, context);
    }

    public StateSnapshot Detect(ActionInstance action) => StateSnapshot.Of(StateType, Ready);

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Run(action);
    }

    public void Restore(ActionInstance action, StateSnapshot original) { }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original.Type == StateType && original.Value == Ready ? null : "Original inválido para ação efêmera.";
    }
}

// ============================================================================ esvaziar a lista de espera

/// <summary>
/// "MemoryLabStandbyPurge": esvazia a lista de espera (NtSetSystemInformation, SystemMemoryListInformation =
/// MemoryPurgeStandbyList; exige administrador e o privilégio SeProfileSingleProcessPrivilege, que o backend ativa no próprio
/// processo). Efêmera: o cache se refaz sozinho conforme os arquivos são lidos de novo. Alvo fixo: {"list": "standby"}.
/// </summary>
public sealed class StandbyPurgeHandler(IStandbyListBackend backend) : MemoryEphemeralHandler
{
    public const string Kind = "MemoryLabStandbyPurge";
    public override string HandlerKind => Kind;

    private static bool ValidTarget(ActionInstance action) =>
        action.Target.Count == 1 && action.Target.TryGetValue("list", out var l) && l == "standby";

    protected override Applicability AssessCore(ActionInstance action, SystemContext context) =>
        ValidTarget(action) ? Applicability.Ok : Applicability.Blocked("Alvo inválido: só a lista de espera pode ser esvaziada.");

    protected override void Run(ActionInstance action)
    {
        if (!ValidTarget(action)) throw new InvalidOperationException("Alvo inválido.");
        backend.PurgeStandby();
    }
}

// ============================================================================ processos (aparar / fechar / encerrar)

/// <summary>Validação comum dos alvos de processo: {pid, name, started} vindos do usuário (regex no catálogo) e revalidados aqui.</summary>
public static partial class ProcessTarget
{
    [GeneratedRegex(@"\A[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PidRx();

    [GeneratedRegex(@"\A[0-9]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StartedRx();

    [GeneratedRegex(@"\A[^\x00-\x1f\\/:*?""<>|]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRx();

    public static string? Parse(ActionInstance action, out int pid, out string name, out long started)
    {
        ArgumentNullException.ThrowIfNull(action);
        pid = 0; name = ""; started = 0;
        if (!action.Target.TryGetValue("pid", out var p) || !PidRx().IsMatch(p) || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out pid))
            return "PID inválido.";
        if (!action.Target.TryGetValue("name", out var n) || !NameRx().IsMatch(n)) return "Nome de processo inválido.";
        name = AppGrouping.NormalizeName(n);
        if (!action.Target.TryGetValue("started", out var s) || !StartedRx().IsMatch(s) || !long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out started))
            return "Horário de início inválido.";
        return null;
    }

    /// <summary>Confere que o processo é o mesmo que o usuário escolheu e que não é protegido. Null = pode.</summary>
    public static string? Verify(IProcessControlBackend backend, ActionInstance action, out int pid)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (Parse(action, out pid, out var name, out var started) is { } bad) return bad;
        var found = backend.Find(pid);
        if (found is null) return "O processo já não existe.";
        if (!AppGrouping.NormalizeName(found.Name).Equals(name, StringComparison.OrdinalIgnoreCase) || found.StartFileTimeUtc != started)
            return "O PID agora pertence a outro processo (o escolhido já fechou). Nada foi feito.";
        var guard = ProcessProtection.Check(found.Name, found.Pid, found.SessionId, found.Path);
        return guard.Level == ProcessGuard.Forbidden ? guard.Reason : null;
    }
}

/// <summary>"MemoryLabProcessTrim": EmptyWorkingSet num processo escolhido (efêmero: o processo recarrega o que usar).</summary>
public sealed class ProcessTrimHandler(IProcessControlBackend backend) : MemoryEphemeralHandler
{
    public const string Kind = "MemoryLabProcessTrim";
    public override string HandlerKind => Kind;

    protected override Applicability AssessCore(ActionInstance action, SystemContext context)
    {
        if (action.Target.Count != 3) return Applicability.Blocked("Alvo inválido.");
        if (ProcessTarget.Parse(action, out _, out _, out _) is { } bad) return Applicability.Blocked(bad);
        return ProcessTarget.Verify(backend, action, out _) is { } problem ? Applicability.NotApplicable(problem) : Applicability.Ok;
    }

    protected override void Run(ActionInstance action)
    {
        if (action.Target.Count != 3) throw new InvalidOperationException("Alvo inválido.");
        if (ProcessTarget.Verify(backend, action, out var pid) is { } problem) throw new InvalidOperationException(problem);
        backend.Trim(pid);
    }
}

/// <summary>
/// "MemoryLabProcessClose": fecha um processo escolhido. Alvo {mode, pid, name, started}. mode "close" = pede para a janela fechar
/// (o programa pode perguntar "salvar?"; nada é forçado); mode "end" = encerra à força. Efêmero: não há como "desfazer"
/// um fechamento; o usuário abre o programa de novo.
/// </summary>
public sealed class ProcessCloseHandler(IProcessControlBackend backend) : MemoryEphemeralHandler
{
    public const string Kind = "MemoryLabProcessClose";
    public static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(10);
    public override string HandlerKind => Kind;

    private static string? Mode(ActionInstance a) =>
        a.Target.Count == 4 && a.Target.TryGetValue("mode", out var m) && m is "close" or "end" ? m : null;

    protected override Applicability AssessCore(ActionInstance action, SystemContext context)
    {
        if (Mode(action) is null) return Applicability.Blocked("Modo inválido (só close/end).");
        if (ProcessTarget.Parse(action, out _, out _, out _) is { } bad) return Applicability.Blocked(bad);
        return ProcessTarget.Verify(backend, action, out _) is { } problem ? Applicability.NotApplicable(problem) : Applicability.Ok;
    }

    protected override void Run(ActionInstance action)
    {
        var mode = Mode(action) ?? throw new InvalidOperationException("Modo inválido.");
        if (ProcessTarget.Verify(backend, action, out var pid) is { } problem) throw new InvalidOperationException(problem);
        if (mode == "close")
        {
            if (!backend.CloseGracefully(pid, GracefulWait))
                throw new InvalidOperationException("O programa não fechou em 10 s (pode estar perguntando se quer salvar, ou não tem janela). Nada foi forçado.");
            return;
        }
        backend.Terminate(pid);
    }
}

// ============================================================================ reiniciar serviço

/// <summary>
/// "MemoryLabServiceRestart": para e inicia de novo um serviço que está rodando. Efêmero, mas devolve o estado anterior: se
/// parou e não voltou, tenta iniciar de novo e relata a falha (o serviço nunca fica parado por causa do NEXUS sem aviso).
/// Recusa: serviços protegidos (<see cref="SafetyPolicy.CheckService"/>), serviço parado, serviço com dependentes rodando, serviço
/// que não aceita parar.
/// </summary>
public sealed partial class ServiceRestartHandler(IServiceControlBackend backend) : MemoryEphemeralHandler
{
    public const string Kind = "MemoryLabServiceRestart";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public override string HandlerKind => Kind;

    [GeneratedRegex(@"\A[A-Za-z0-9_.\-]([A-Za-z0-9_.\- ]{0,78}[A-Za-z0-9_.\-])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRx();

    public static string? CheckName(string? name) =>
        name is null || !NameRx().IsMatch(name) ? "Nome de serviço inválido." : SafetyPolicy.CheckService(name);

    private static string? Name(ActionInstance a) => a.Target.Count == 1 && a.Target.TryGetValue("service", out var s) ? s : null;

    protected override Applicability AssessCore(ActionInstance action, SystemContext context)
    {
        var name = Name(action);
        if (CheckName(name) is { } bad) return Applicability.Blocked(bad);
        var info = backend.Query(name!);
        if (info is null) return Applicability.NotApplicable($"Serviço '{name}' não existe.");
        return CheckState(info) is { } why ? Applicability.NotApplicable(why) : Applicability.Ok;
    }

    private static string? CheckState(ServiceRunInfo info)
    {
        if (!info.State.Equals("Running", StringComparison.OrdinalIgnoreCase)) return "O serviço não está rodando: nada a reiniciar.";
        if (!info.CanStop) return "O serviço não aceita ser parado agora.";
        if (info.RunningDependents > 0) return $"{info.RunningDependents} serviço(s) dependem dele e estão rodando: reiniciar derrubaria esses também.";
        return null;
    }

    protected override void Run(ActionInstance action)
    {
        var name = Name(action);
        if (CheckName(name) is { } bad) throw new InvalidOperationException(bad);
        var info = backend.Query(name!) ?? throw new InvalidOperationException("Serviço não existe.");
        if (CheckState(info) is { } why) throw new InvalidOperationException(why);
        backend.StopService(name!, Timeout);
        try { backend.StartService(name!, Timeout); }
        catch (Exception first) when (first is InvalidOperationException or TimeoutException)
        {
            // Volta ao estado anterior (rodando): uma segunda tentativa; se falhar, o erro diz claramente que o serviço ficou parado.
            try { backend.StartService(name!, Timeout); }
            catch (Exception second) when (second is InvalidOperationException or TimeoutException)
            {
                throw new InvalidOperationException($"O serviço parou e não voltou a iniciar ({second.Message}). Inicie-o pela página Serviços ou reinicie o PC.", second);
            }
        }
    }
}

// ============================================================================ compressão de memória (persistente, com Restore)

/// <summary>
/// "MemoryLabCompression": liga a compressão de memória (Enable-MMAgent -MemoryCompression). Detect lê Get-MMAgent
/// (MemoryCompression True/False); Restore volta ao valor anterior (Disable-MMAgent -MemoryCompression se estava desligada).
/// Alvo fixo {"setting": "MemoryCompression"}; o valor desejado só pode ser "True".
/// </summary>
public sealed class CompressionHandler(IMemoryAgentBackend backend) : IActionHandler, IRestoreValidator
{
    public const string Kind = "MemoryLabCompression";
    public const string StateType = "MMAgent";
    public string HandlerKind => Kind;

    private static string? CheckTarget(ActionInstance a) =>
        a.Target.Count == 1 && a.Target.TryGetValue("setting", out var s) && s == "MemoryCompression" ? null : "Alvo inválido para a compressão de memória.";

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (CheckTarget(action) is { } p) return Applicability.Blocked(p);
        if (action.Desired.Type != StateType || action.Desired.Value != "True") return Applicability.Blocked("Só é permitido LIGAR a compressão.");
        return backend.GetCompression() is null ? Applicability.NotApplicable("Não foi possível ler a configuração (Get-MMAgent).") : Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action) =>
        backend.GetCompression() is { } on ? StateSnapshot.Of(StateType, on ? "True" : "False") : StateSnapshot.Absent();

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckTarget(action) is { } p) throw new InvalidOperationException(p);
        backend.SetCompression(true);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } p) throw new InvalidOperationException(p);
        backend.SetCompression(original.Value == "True");
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckTarget(action) is { } p) return p;
        if (!original.Exists) return "Estado original não foi lido: nada a restaurar.";
        return original.Type == StateType && original.Value is "True" or "False" ? null : $"Valor original '{original.Value}' não restaurável.";
    }
}

// ============================================================================ políticas de navegador (lista fechada)

/// <summary>
/// "MemoryLabBrowserPolicy": lista FECHADA de políticas documentadas que reduzem processos de navegador rodando à toa.
/// O catálogo só escolhe o id ({"policy": "edge.startupboost"}); chave, nome, tipo e valor ficam no código.
/// Restore regrava o valor anterior ou remove o valor (e a chave, se o NEXUS a criou e ela ficou vazia).
/// </summary>
public sealed class BrowserPolicyHandler(IRegistryBackend registry) : IActionHandler, IRestoreValidator
{
    public const string Kind = "MemoryLabBrowserPolicy";
    private const string KeyExistedDetail = "keyExisted";
    public const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";
    public const string ChromeKey = @"SOFTWARE\Policies\Google\Chrome";

    /// <summary>id → (chave em HKLM, nome do valor, valor DWORD).</summary>
    public static readonly IReadOnlyDictionary<string, (string Path, string Name, string Value)> Policies =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["edge.startupboost"] = (EdgeKey, "StartupBoostEnabled", "0"),
            ["edge.backgroundmode"] = (EdgeKey, "BackgroundModeEnabled", "0"),
            ["edge.sleepingtabs"] = (EdgeKey, "SleepingTabsEnabled", "1"),
            ["chrome.backgroundmode"] = (ChromeKey, "BackgroundModeEnabled", "0"),
        };

    public string HandlerKind => Kind;

    private static (RegistryTarget Target, string Value)? Resolve(ActionInstance a) =>
        a.Target.Count == 1 && a.Target.TryGetValue("policy", out var id) && Policies.TryGetValue(id, out var p)
            ? (new RegistryTarget(RegHive.HKLM, p.Path, p.Name, RegView.Default), p.Value)
            : null;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (Resolve(action) is not { } r) return Applicability.Blocked("Política fora da lista permitida.");
        if (action.Desired.Type != "DWord" || action.Desired.Value != r.Value) return Applicability.Blocked("Valor diferente do fixado para esta política.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var r = Resolve(action) ?? throw new InvalidOperationException("Política fora da lista permitida.");
        var read = registry.Read(r.Target);
        var detail = new Dictionary<string, string> { [KeyExistedDetail] = read.KeyExists ? "true" : "false" };
        return read.ValueExists && read.Kind is not null && read.Value is not null ? StateSnapshot.Of(read.Kind, read.Value, ValueOrigin.Explicit, detail) : StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action)
    {
        var r = Resolve(action) ?? throw new InvalidOperationException("Política fora da lista permitida.");
        registry.Write(r.Target, "DWord", r.Value);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } p) throw new InvalidOperationException(p);
        var t = Resolve(action)!.Value.Target;
        if (original.Exists) { registry.Write(t, original.Type!, original.Value!); return; }
        registry.DeleteValue(t);
        if (original.Detail is not null && original.Detail.TryGetValue(KeyExistedDetail, out var existed) && existed == "false")
            registry.DeleteKeyIfEmpty(t.Hive, t.Path, t.View);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (Resolve(action) is null) return "Política fora da lista permitida.";
        if (original.Exists && (original.Value is null || original.Type is not ("DWord" or "String" or "QWord")))
            return $"Tipo original '{original.Type}' não restaurável.";
        return null;
    }
}
