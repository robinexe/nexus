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
/// Handler "PowerSettingValue" (valor AC). Alvo: scheme, subgroup, setting.
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
        if (action.Desired.Type != "UInt32" || !uint.TryParse(action.Desired.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var u) ||
            action.Desired.Value != u.ToString(CultureInfo.InvariantCulture))
            return Applicability.Blocked("Desejado deve ser Type 'UInt32' em decimal canônico.");
        return Applicability.Ok;
    }

    private static Guid SchemeFor(ActionInstance a) => Guid.Parse(a.Target["scheme"]);

    public StateSnapshot Detect(ActionInstance action)
    {
        var v = _power.ReadAcValue(SchemeFor(action), Guid.Parse(action.Target["subgroup"]), Guid.Parse(action.Target["setting"]));
        return StateSnapshot.Of("UInt32", v.ToString(CultureInfo.InvariantCulture));
    }

    public void Apply(ActionInstance action) => Write(SchemeFor(action), action, uint.Parse(action.Desired.Value!, CultureInfo.InvariantCulture));

    public void Restore(ActionInstance action, StateSnapshot original) =>
        Write(SchemeFor(action), action, uint.Parse(original.Value!, CultureInfo.InvariantCulture));

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        foreach (var k in new[] { "scheme", "subgroup", "setting" })
            if (!action.Target.TryGetValue(k, out var v) || !Guid.TryParse(v, out _)) return $"Alvo inválido: '{k}'.";
        return original.Exists && original.Type == "UInt32" && uint.TryParse(original.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? null : "Valor original inválido.";
    }

    private void Write(Guid scheme, ActionInstance a, uint value)
    {
        _power.WriteAcValue(scheme, Guid.Parse(a.Target["subgroup"]), Guid.Parse(a.Target["setting"]), value);
        if (scheme == _power.GetActiveScheme()) _power.SetActiveScheme(scheme); // reaplica o esquema ativo para efetivar
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsPowerBackend : IPowerBackend
{
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

    private static void Check(uint rc, string api)
    {
        if (rc != 0) throw new InvalidOperationException($"{api} falhou com código Win32 {rc}.");
    }

    [LibraryImport("powrprof.dll")] private static partial uint PowerGetActiveScheme(IntPtr root, out IntPtr activePolicyGuid);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerSetActiveScheme(IntPtr root, Guid* scheme);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerReadACValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint* value);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerWriteACValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint value);
    [LibraryImport("powrprof.dll")] private static partial uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, [Out] byte[] buffer, ref uint bufferSize);
    [LibraryImport("kernel32.dll")] private static partial IntPtr LocalFree(IntPtr mem);
}
