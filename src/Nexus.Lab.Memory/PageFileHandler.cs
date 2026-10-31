using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Lab.Memory;

/// <summary>
/// Handler "MemoryLabPageFile": volta o arquivo de paginação para "automático em todas as unidades" (o padrão do Windows),
/// escrevendo o valor <c>PagingFiles</c> = "?:\pagefile.sys" (REG_MULTI_SZ) em HKLM\...\Memory Management.
/// O alvo é FIXO no código (o catálogo não escolhe chave nem valor); Restore regrava o valor anterior completo (tipo + dados)
/// ou remove o valor se ele não existia. Vale no próximo reinício.
/// </summary>
public sealed class PageFileHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "MemoryLabPageFile";
    public const string AutomaticValue = @"?:\pagefile.sys";
    public const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    internal static readonly RegistryTarget Target = new(RegHive.HKLM, MemoryManagementKey, "PagingFiles", RegView.Default);
    private static readonly string[] RestorableTypes = ["MultiString", "String", "ExpandString"];
    private readonly IRegistryBackend _reg;

    public PageFileHandler(IRegistryBackend registry) => _reg = registry;

    public string HandlerKind => Kind;

    private static string? CheckTarget(ActionInstance action) =>
        action.Target.Count == 1 && action.Target.TryGetValue("setting", out var s) && s == "PagingFiles" ? null : "Alvo inválido para o arquivo de paginação.";

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (CheckTarget(action) is { } problem) return Applicability.Blocked(problem);
        if (!action.Desired.Exists || action.Desired.Type != "MultiString" || action.Desired.Value != AutomaticValue)
            return Applicability.Blocked("Só é permitido o modo automático do Windows (?:\\pagefile.sys).");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var r = _reg.Read(Target);
        return r.ValueExists && r.Kind is not null && r.Value is not null ? StateSnapshot.Of(r.Kind, r.Value) : StateSnapshot.Absent();
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckTarget(action) is { } problem) throw new InvalidOperationException(problem);
        _reg.Write(Target, "MultiString", AutomaticValue);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        if (original.Exists) _reg.Write(Target, original.Type!, original.Value!);
        else _reg.DeleteValue(Target);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckTarget(action) is { } problem) return problem;
        if (original.Exists && (original.Type is null || original.Value is null || !RestorableTypes.Contains(original.Type, StringComparer.OrdinalIgnoreCase)))
            return $"Tipo original '{original.Type}' não restaurável.";
        return null;
    }
}
