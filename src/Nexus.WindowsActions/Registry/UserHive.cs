using Nexus.Core;

namespace Nexus.WindowsActions.Registry;

/// <summary>
/// Perfil de usuário de um alvo HKCU (achado B10). Quando o broker roda como serviço (LocalSystem), "HKCU" do processo seria o
/// perfil do SYSTEM. O broker então grava no ALVO da ação o SID de quem pediu (<see cref="TargetKey"/>), e os backends
/// escrevem em <c>HKU\&lt;SID&gt;</c>. Como o SID fica no diário, a restauração sempre volta para o mesmo perfil, não importa
/// quem a peça. Só contas locais/de domínio (<c>S-1-5-21-…</c>) são aceitas: nada de SYSTEM, .DEFAULT ou serviços.
/// </summary>
public static class UserHive
{
    public const string TargetKey = UserProfileScope.TargetKey;

    public static bool IsValidSid(string? sid) => UserProfileScope.IsValidSid(sid);

    /// <summary>SID do alvo, ou null quando a ação usa o HKCU do próprio processo.</summary>
    public static string? Of(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Target.TryGetValue(TargetKey, out var sid) ? sid : null;
    }

    /// <summary>Erro se o alvo tem <see cref="TargetKey"/> inválido ou aplicado a uma hive que não é HKCU.</summary>
    public static string? Check(ActionInstance action, RegHive hive)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!action.Target.TryGetValue(TargetKey, out var sid)) return null;
        if (hive != RegHive.HKCU) return "Perfil de usuário só se aplica a HKCU.";
        return IsValidSid(sid) ? null : "SID de usuário inválido no alvo (só contas S-1-5-21-…).";
    }
}
