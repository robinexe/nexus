using System.Globalization;
using Nexus.Core;

namespace Nexus.Measurement;

/// <summary>Captura do rastreamento do kernel (Windows em Windows/WindowsKernelTrace.cs; falso nos testes). Só leitura.</summary>
public interface IKernelTraceBackend
{
    /// <summary>
    /// Inicia uma sessão ETW de sistema com DPC + interrupção + carga de imagens, espera <paramref name="duration"/>, para a sessão e
    /// devolve os eventos e os módulos vistos. A sessão é SEMPRE parada antes de voltar (também em erro).
    /// </summary>
    (IReadOnlyList<KernelRoutineEvent> Events, IReadOnlyList<KernelModule> Modules) Capture(TimeSpan duration);
}

/// <summary>
/// Ação efêmera <c>measure.driver-latency.trace</c>: rastreamento do kernel de ~10 s (DPC/ISR com o endereço da rotina → módulo),
/// como o LatencyMon/xperf fazem. Exige administrador (sessão ETW do kernel) e NÃO muda nada no PC: a sessão é temporária e é parada
/// ao fim. O resultado vai para <see cref="DriverTraceFile"/> no perfil de quem pediu, que a interface lê.
/// <list type="bullet">
/// <item>Alvo fixo: {"scope": "User", "seconds": "10"}; o aplicador acrescenta <c>userSid</c> (B10). Segundos de 5 a 30.</item>
/// <item>Detect: "pronto"; Apply: captura + grava; Restore: nada (efêmera).</item>
/// <item>Sem perfil resolvido (aplicador com outra conta e sem SID): recusa antes de iniciar qualquer sessão.</item>
/// </list>
/// </summary>
public sealed class DriverTraceHandler(IKernelTraceBackend backend, Func<string?, string?> localAppDataForSid, bool otherAccount,
    Func<bool?>? virtualMachine = null, Func<DateTimeOffset>? clock = null) : IActionHandler, IRestoreValidator
{
    public const string Kind = "MeasureDriverLatencyTrace";
    public const string StateType = "Ephemeral";
    public const string Ready = "pronto";
    public const string Done = "executado";
    public const string SecondsKey = "seconds";
    public const int MinSeconds = 5, MaxSeconds = 30, DefaultSeconds = 10;

    private readonly IKernelTraceBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    private readonly Func<string?, string?> _localAppData = localAppDataForSid ?? throw new ArgumentNullException(nameof(localAppDataForSid));
    private readonly Func<bool?> _vm = virtualMachine ?? (() => null);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public string HandlerKind => Kind;

    /// <summary>Caminho do arquivo de resultado para este pedido, ou null quando o perfil não pode ser deduzido.</summary>
    public string? ResultPath(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        string? sid = null;
        if (action.Target.TryGetValue(UserProfileScope.TargetKey, out var s))
        {
            if (!UserProfileScope.IsValidSid(s)) return null;
            sid = s;
        }
        else if (otherAccount) return null;
        var local = _localAppData(sid);
        return string.IsNullOrWhiteSpace(local) ? null : DriverTraceFile.PathFor(local);
    }

    public static int? Seconds(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!action.Target.TryGetValue(SecondsKey, out var text)) return DefaultSeconds;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= MinSeconds and <= MaxSeconds ? n : null;
    }

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
        if (!action.Target.TryGetValue("scope", out var scope) || !UserProfileScope.IsUserScopeName(scope))
            return Applicability.Blocked("Alvo inválido: o resultado vai para o perfil de quem pediu (scope User).");
        if (Seconds(action) is null) return Applicability.Blocked($"Duração inválida: entre {MinSeconds} e {MaxSeconds} segundos.");
        if (ResultPath(action) is null)
            return Applicability.Blocked("Não deu para saber em qual perfil gravar o resultado (o aplicador roda com outra conta e o pedido não trouxe a sua).");
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            return Applicability.NotApplicable("O rastreamento do kernel do NEXUS só roda em Windows 64 bits.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action) => StateSnapshot.Of(StateType, Ready);

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var path = ResultPath(action) ?? throw new InvalidOperationException("Perfil de destino desconhecido; nenhuma sessão foi iniciada.");
        var seconds = Seconds(action) ?? throw new InvalidOperationException("Duração inválida.");
        var started = _clock();
        var (events, modules) = _backend.Capture(TimeSpan.FromSeconds(seconds));
        var result = DriverTraceAnalyzer.Aggregate(events, modules, seconds, started, _vm());
        DriverTraceFile.Save(path, result);
    }

    public void Restore(ActionInstance action, StateSnapshot original) { }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original.Type == StateType && original.Value == Ready ? null : "Original inválido para ação efêmera.";
    }
}
