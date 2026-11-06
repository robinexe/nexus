using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Core;

namespace Nexus.WindowsActions.Power;

public interface IPowerBackend
{
    Guid GetActiveScheme();
    void SetActiveScheme(Guid scheme);
    IReadOnlyList<Guid> EnumerateSchemes();
    uint ReadAcValue(Guid scheme, Guid subgroup, Guid setting);
    void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value);
    uint ReadDcValue(Guid scheme, Guid subgroup, Guid setting);
    void WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value);
}

/// <summary>Modo de energia do Windows (o controle "Modo de energia" das Configurações): overlay sobre o plano Equilibrado.</summary>
public interface IPowerOverlayBackend
{
    /// <summary>Overlay em vigor; null = recurso inexistente nesta versão do Windows.</summary>
    Guid? GetEffectiveOverlay();
    void SetActiveOverlay(Guid overlay);
    Guid GetActiveScheme();
}

/// <summary>
/// Handler "PowerOverlayScheme": o "Modo de energia" do Windows 10/11 (Melhor eficiência … Melhor desempenho), a forma que a
/// Intel recomenda para Core Ultra e que funciona nos notebooks Modern Standby que escondem o plano Alto desempenho. Alvo:
/// <c>setting</c>=<c>powerMode</c>. Desejado: Type "Guid", um dos overlays conhecidos. Só vale com o plano Equilibrado ativo
/// (em outro plano o Windows ignora o modo): fora disso, "não se aplica". Desfazer devolve o modo anterior.
/// </summary>
public sealed class PowerOverlaySchemeHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "PowerOverlayScheme";
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>Overlays documentados (Microsoft Learn, "Customize the Windows performance power slider").</summary>
    public static readonly IReadOnlySet<Guid> Known = new HashSet<Guid>
    {
        Guid.Empty,                                            // Equilibrado / "Recomendado"
        BestPerformance,                                       // Melhor desempenho
        new("961cc777-2547-4f9d-8174-7d86181b8a7a"),           // Melhor eficiência de energia
        new("3af9b8d9-7c97-431d-ad78-34a8bfea439f"),           // Melhor bateria / economia
    };

    private readonly IPowerOverlayBackend _power;
    public PowerOverlaySchemeHandler(IPowerOverlayBackend power) => _power = power ?? throw new ArgumentNullException(nameof(power));
    public string HandlerKind => Kind;

    private static string? TargetProblem(ActionInstance a) =>
        a.Target.Count == 1 && a.Target.TryGetValue("setting", out var s) && s == "powerMode" ? null : "Alvo do modo de energia inválido.";

    private static Guid? Parse(StateSnapshot s) =>
        s.Exists && s.Type == "Guid" && Guid.TryParse(s.Value, out var g) && s.Value == g.ToString("D") && Known.Contains(g) ? g : null;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (TargetProblem(action) is { } p) return Applicability.Blocked(p);
        if (Parse(action.Desired) is null) return Applicability.Blocked("Desejado deve ser um modo de energia conhecido (Guid 'D' minúsculo).");
        if (_power.GetEffectiveOverlay() is null) return Applicability.NotApplicable("Esta versão do Windows não tem o "Modo de energia".");
        if (_power.GetActiveScheme() != Balanced)
            return Applicability.NotApplicable("O "Modo de energia" só vale com o plano Equilibrado; este PC usa outro plano de energia.");
        return Applicability.Ok;
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return TargetProblem(action) ?? (Parse(original) is null ? "Modo de energia original inválido no diário." : null);
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var g = _power.GetEffectiveOverlay() ?? throw new InvalidOperationException("Modo de energia indisponível.");
        return StateSnapshot.Of("Guid", g.ToString("D"), ValueOrigin.Explicit);
    }

    public void Apply(ActionInstance action)
    {
        if (TargetProblem(action) is { } p) throw new InvalidOperationException(p);
        _power.SetActiveOverlay(Parse(action.Desired) ?? throw new InvalidOperationException("Modo de energia desconhecido."));
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        _power.SetActiveOverlay(Parse(original) ?? throw new InvalidOperationException("Modo de energia original inválido."));
    }
}

/// <summary>Handler "PowerActiveScheme". Alvo: schemeGuid. Desejado: Type "Guid".</summary>
public sealed class PowerActiveSchemeHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "PowerActiveScheme";
    private readonly IPowerBackend _power;
    public PowerActiveSchemeHandler(IPowerBackend power) => _power = power;
    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (action.Desired.Type != "Guid" || !Guid.TryParse(action.Desired.Value, out var g) || action.Desired.Value != g.ToString("D"))
            return Applicability.Blocked("Desejado deve ser Type 'Guid' em formato canônico minúsculo 'D'.");
        return _power.EnumerateSchemes().Contains(g)
            ? Applicability.Ok
            : Applicability.NotApplicable("Esquema não existe neste sistema (comum em Modern Standby). Não será criado automaticamente.");
    }

    public StateSnapshot Detect(ActionInstance action) => StateSnapshot.Of("Guid", _power.GetActiveScheme().ToString("D"));

    public void Apply(ActionInstance action) => _power.SetActiveScheme(Guid.Parse(action.Desired.Value!));

    public void Restore(ActionInstance action, StateSnapshot original) => _power.SetActiveScheme(Guid.Parse(original.Value!));

    public string? CheckRestore(ActionInstance action, StateSnapshot original) =>
        original.Exists && original.Type == "Guid" && Guid.TryParse(original.Value, out _) ? null : "Esquema original inválido.";
}

/// <summary>
/// Handler "PowerSettingValue". Alvo: scheme, subgroup, setting e acdc ("AC" = na tomada, padrão quando ausente; "DC" = na bateria).
/// O acdc fica no alvo gravado no diário, então Detect/Apply/Restore usam sempre o mesmo índice (AC ou DC).
/// O esquema é obrigatório e fixado pelo planejador (normalmente o ativo no momento do plano),
/// para que leitura, verificação e restauração apontem sempre para o mesmo esquema,
/// mesmo que o usuário troque de plano depois.
/// </summary>
public sealed class PowerSettingValueHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "PowerSettingValue";
    private readonly IPowerBackend _power;
    public PowerSettingValueHandler(IPowerBackend power) => _power = power;
    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        if (!action.Target.TryGetValue("scheme", out var sc) || !Guid.TryParse(sc, out var scheme)) return Applicability.Blocked("Esquema não fixado no alvo.");
        if (!_power.EnumerateSchemes().Contains(scheme)) return Applicability.NotApplicable("Esquema não existe neste sistema.");
        if (!action.Target.TryGetValue("subgroup", out var sg) || !Guid.TryParse(sg, out _)) return Applicability.Blocked("Subgrupo inválido.");
        if (!action.Target.TryGetValue("setting", out var st) || !Guid.TryParse(st, out _)) return Applicability.Blocked("Configuração inválida.");
        if (!TryPowerSource(action, out var dc)) return Applicability.Blocked("acdc deve ser 'AC' ou 'DC'.");
        if (action.Desired.Type != "UInt32" || !uint.TryParse(action.Desired.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var u) ||
            action.Desired.Value != u.ToString(CultureInfo.InvariantCulture))
            return Applicability.Blocked("Desejado deve ser Type 'UInt32' em decimal canônico.");
        try { _ = Read(action); }
        catch (InvalidOperationException) { return Applicability.NotApplicable("Configuração de energia não existe neste sistema."); }
        return Applicability.Ok;
    }

    private static Guid SchemeFor(ActionInstance a) => Guid.Parse(a.Target["scheme"]);

    /// <summary>acdc ausente = AC (compatível com entradas antigas do diário); só "AC" e "DC" exatos são aceitos.</summary>
    private static bool TryPowerSource(ActionInstance a, out bool dc)
    {
        dc = false;
        if (!a.Target.TryGetValue("acdc", out var v)) return true;
        if (v == "AC") return true;
        if (v == "DC") { dc = true; return true; }
        return false;
    }

    private static bool IsDc(ActionInstance a) =>
        TryPowerSource(a, out var dc) ? dc : throw new InvalidOperationException("acdc deve ser 'AC' ou 'DC'.");

    private uint Read(ActionInstance a)
    {
        var (scheme, sub, set) = (SchemeFor(a), Guid.Parse(a.Target["subgroup"]), Guid.Parse(a.Target["setting"]));
        return IsDc(a) ? _power.ReadDcValue(scheme, sub, set) : _power.ReadAcValue(scheme, sub, set);
    }

    public StateSnapshot Detect(ActionInstance action) => StateSnapshot.Of("UInt32", Read(action).ToString(CultureInfo.InvariantCulture));

    public void Apply(ActionInstance action) => Write(SchemeFor(action), action, uint.Parse(action.Desired.Value!, CultureInfo.InvariantCulture));

    public void Restore(ActionInstance action, StateSnapshot original) =>
        Write(SchemeFor(action), action, uint.Parse(original.Value!, CultureInfo.InvariantCulture));

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        foreach (var k in new[] { "scheme", "subgroup", "setting" })
            if (!action.Target.TryGetValue(k, out var v) || !Guid.TryParse(v, out _)) return $"Alvo inválido: '{k}'.";
        if (!TryPowerSource(action, out _)) return "Alvo inválido: 'acdc'.";
        return original.Exists && original.Type == "UInt32" && uint.TryParse(original.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? null : "Valor original inválido.";
    }

    private void Write(Guid scheme, ActionInstance a, uint value)
    {
        var (sub, set) = (Guid.Parse(a.Target["subgroup"]), Guid.Parse(a.Target["setting"]));
        if (IsDc(a)) _power.WriteDcValue(scheme, sub, set, value);
        else _power.WriteAcValue(scheme, sub, set, value);
        if (scheme == _power.GetActiveScheme()) _power.SetActiveScheme(scheme); // reaplica o esquema ativo para efetivar
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsPowerBackend : IPowerBackend, IPowerOverlayBackend
{
    // PowerGetEffectiveOverlayScheme / PowerSetActiveOverlayScheme (powrprof, Windows 10 1709+): procurados em tempo de execução;
    // ausentes = recurso inexistente (o handler responde "não se aplica").
    private static readonly Lazy<(IntPtr Get, IntPtr Set)> Overlay = new(() =>
    {
        if (!NativeLibrary.TryLoad("powrprof.dll", out var lib)) return (IntPtr.Zero, IntPtr.Zero);
        NativeLibrary.TryGetExport(lib, "PowerGetEffectiveOverlayScheme", out var get);
        NativeLibrary.TryGetExport(lib, "PowerSetActiveOverlayScheme", out var set);
        return (get, set);
    });

    public unsafe Guid? GetEffectiveOverlay()
    {
        var get = Overlay.Value.Get;
        if (get == IntPtr.Zero) return null;
        Guid g;
        var rc = ((delegate* unmanaged[Stdcall]<Guid*, uint>)get)(&g);
        return rc == 0 ? g : null;
    }

    public unsafe void SetActiveOverlay(Guid overlay)
    {
        var set = Overlay.Value.Set;
        if (set == IntPtr.Zero) throw new InvalidOperationException("PowerSetActiveOverlayScheme indisponível nesta versão do Windows.");
        Check(((delegate* unmanaged[Stdcall]<Guid, uint>)set)(overlay), "PowerSetActiveOverlayScheme");
    }

    private const uint AccessScheme = 16;

    public Guid GetActiveScheme()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var ptr), nameof(PowerGetActiveScheme));
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    // Ponteiros explícitos: o gerador do LibraryImport não trata Guid (outro assembly) como blittable sem DisableRuntimeMarshalling.
    public unsafe void SetActiveScheme(Guid scheme) => Check(PowerSetActiveScheme(IntPtr.Zero, &scheme), nameof(PowerSetActiveScheme));

    public IReadOnlyList<Guid> EnumerateSchemes()
    {
        var list = new List<Guid>();
        var buffer = new byte[16];
        for (uint i = 0; ; i++)
        {
            var size = (uint)buffer.Length;
            var rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buffer, ref size);
            if (rc == 259) break; // ERROR_NO_MORE_ITEMS
            Check(rc, nameof(PowerEnumerate));
            list.Add(new Guid(buffer));
        }
        return list;
    }

    public unsafe uint ReadAcValue(Guid scheme, Guid subgroup, Guid setting)
    {
        uint v;
        Check(PowerReadACValueIndex(IntPtr.Zero, &scheme, &subgroup, &setting, &v), nameof(PowerReadACValueIndex));
        return v;
    }

    public unsafe void WriteAcValue(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        Check(PowerWriteACValueIndex(IntPtr.Zero, &scheme, &subgroup, &setting, value), nameof(PowerWriteACValueIndex));

    public unsafe uint ReadDcValue(Guid scheme, Guid subgroup, Guid setting)
    {
        uint v;
        Check(PowerReadDCValueIndex(IntPtr.Zero, &scheme, &subgroup, &setting, &v), nameof(PowerReadDCValueIndex));
        return v;
    }

    public unsafe void WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        Check(PowerWriteDCValueIndex(IntPtr.Zero, &scheme, &subgroup, &setting, value), nameof(PowerWriteDCValueIndex));

    private static void Check(uint rc, string api)
    {
        if (rc != 0) throw new InvalidOperationException($"{api} falhou com código Win32 {rc}.");
    }

    [LibraryImport("powrprof.dll")] private static partial uint PowerGetActiveScheme(IntPtr root, out IntPtr activePolicyGuid);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerSetActiveScheme(IntPtr root, Guid* scheme);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerReadACValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint* value);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerWriteACValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint value);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerReadDCValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint* value);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerWriteDCValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint value);
    [LibraryImport("powrprof.dll")] private static partial uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, [Out] byte[] buffer, ref uint bufferSize);
    [LibraryImport("kernel32.dll")] private static partial IntPtr LocalFree(IntPtr mem);
}
