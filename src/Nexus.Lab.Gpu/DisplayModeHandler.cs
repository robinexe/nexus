using Nexus.Core;

namespace Nexus.Lab.Gpu;

/// <summary>Resultado de ChangeDisplaySettingsEx (DISP_CHANGE_*): 0 = sucesso, 1 = exige reiniciar, negativo = falha.</summary>
public enum DisplayChangeResult
{
    Successful = 0,
    Restart = 1,
    Failed = -1,
    BadMode = -2,
    NotUpdated = -3,
    BadFlags = -4,
    BadParam = -5,
    BadDualView = -6,
}

/// <summary>Acesso ao modo de vídeo de um monitor. Windows: user32 (EnumDisplaySettingsEx/ChangeDisplaySettingsEx); testes: fake.</summary>
public interface IDisplayModeBackend
{
    /// <summary>Modo atual do monitor; null se o dispositivo não existe/não está ligado à área de trabalho.</summary>
    DisplayMode? Current(string device);

    /// <summary>Modos que o driver lista para o monitor (já filtrados pelo que o monitor anuncia).</summary>
    IReadOnlyList<DisplayMode> Modes(string device);

    /// <summary>Troca resolução/taxa mantendo a profundidade de cor. <paramref name="test"/> = só pergunta ao driver (CDS_TEST).</summary>
    DisplayChangeResult Change(string device, DisplayMode mode, bool test);
}

/// <summary>
/// Handler "GpuLabDisplayMode": troca o modo de vídeo de UM monitor para um modo que ele mesmo lista. Alvo: device
/// ("\\.\DISPLAYn") e mode ("LxA@Hz"), ambos escolhidos na página e validados pelas regex do catálogo.
/// <para>Detect: "aplicado" quando o modo atual é o alvo; senão o modo atual ("1920x1080@60"); ausente se o monitor não existe.</para>
/// <para>Apply: pergunta ao driver com CDS_TEST e só então grava com CDS_UPDATEREGISTRY (persiste no perfil do usuário).</para>
/// <para>Restore: volta ao modo anterior registrado no diário, também testando antes, e só se o monitor ainda listar esse modo.</para>
/// </summary>
public sealed class DisplayModeHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "GpuLabDisplayMode";
    public const string AppliedValue = "aplicado";
    public const string ValueType = "DisplayMode";

    private readonly IDisplayModeBackend _backend;
    private readonly bool _otherAccount;

    public DisplayModeHandler(IDisplayModeBackend backend, bool otherAccount)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _otherAccount = otherAccount;
    }

    public string HandlerKind => Kind;

    private static string? Device(ActionInstance a) => a.Target.TryGetValue("device", out var d) ? d : null;
    private static DisplayMode? Wanted(ActionInstance a) => a.Target.TryGetValue("mode", out var m) ? DisplayMode.TryParse(m) : null;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_otherAccount)
            return Applicability.Blocked("O modo de vídeo é da sessão de quem usa o PC; o aplicador está em outra conta e não pode mudá-lo.");
        var device = Device(action);
        if (!DisplayModes.IsValidDevice(device)) return Applicability.Blocked("Monitor inválido (esperado \\\\.\\DISPLAYn).");
        if (Wanted(action) is not { } wanted) return Applicability.Blocked("Modo inválido (esperado LarguraxAltura@Hz).");
        if (action.Desired.Exists && (action.Desired.Type != ValueType || action.Desired.Value != AppliedValue))
            return Applicability.Blocked("Valor desejado inválido para modo de vídeo.");
        var current = _backend.Current(device!);
        if (current is null) return Applicability.NotApplicable($"O monitor {device} não está ligado agora.");
        if (!DisplayModes.IsListed(wanted, _backend.Modes(device!)))
            return Applicability.Blocked($"O monitor {device} não lista o modo {wanted.Label}; o NEXUS só usa modos que o próprio monitor anuncia.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var device = Device(action);
        if (!DisplayModes.IsValidDevice(device)) return StateSnapshot.Absent();
        var current = _backend.Current(device!);
        if (current is null) return StateSnapshot.Absent(new Dictionary<string, string> { ["device"] = device! });
        var detail = new Dictionary<string, string> { ["device"] = device!, ["mode"] = current.Key };
        var wanted = Wanted(action);
        return wanted is not null && current.SameResolution(wanted) && current.Hz == wanted.Hz
            ? StateSnapshot.Of(ValueType, AppliedValue, ValueOrigin.Explicit, detail)
            : StateSnapshot.Of(ValueType, current.Key, ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var device = Device(action);
        var wanted = Wanted(action);
        if (!DisplayModes.IsValidDevice(device) || wanted is null) throw new InvalidOperationException("Alvo de modo de vídeo inválido.");
        if (!DisplayModes.IsListed(wanted, _backend.Modes(device!)))
            throw new InvalidOperationException($"O monitor não lista {wanted.Label}.");
        Change(device!, wanted);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (_otherAccount) return "O aplicador está em outra conta e não pode mudar o modo de vídeo desta sessão.";
        var device = Device(action);
        if (!DisplayModes.IsValidDevice(device)) return "Monitor inválido no diário.";
        if (!original.Exists || original.Value == AppliedValue) return null;
        if (original.Type != ValueType || DisplayMode.TryParse(original.Value) is not { } mode) return "Modo original inválido no diário.";
        if (_backend.Current(device!) is null) return $"O monitor {device} não está ligado agora; conecte-o e desfaça de novo.";
        if (!DisplayModes.IsListed(mode, _backend.Modes(device!))) return $"O monitor {device} não lista mais o modo {mode.Label}.";
        return null;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        // Monitor ausente antes, ou já no modo alvo antes: não há o que voltar.
        if (!original.Exists || original.Value == AppliedValue) return;
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        Change(Device(action)!, DisplayMode.TryParse(original.Value)!);
    }

    private void Change(string device, DisplayMode mode)
    {
        var test = _backend.Change(device, mode, test: true);
        if (test != DisplayChangeResult.Successful)
            throw new InvalidOperationException($"O driver recusou {mode.Label} no teste (CDS_TEST: {test}). Nada foi alterado.");
        var real = _backend.Change(device, mode, test: false);
        if (real == DisplayChangeResult.Restart)
            throw new InvalidOperationException("O Windows pediu reinício para aplicar este modo; a verificação vai acusar a diferença.");
        if (real != DisplayChangeResult.Successful)
            throw new InvalidOperationException($"O driver recusou {mode.Label} ({real}).");
    }
}
