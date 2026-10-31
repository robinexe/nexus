using System.Text.RegularExpressions;

namespace Nexus.Core;

/// <summary>
/// Ações que escrevem no perfil do usuário (HKCU). Quando o executor roda como serviço, o perfil precisa ser o de quem pediu,
/// gravado no alvo como <see cref="TargetKey"/> (achado B10). Regras independentes de plataforma; a escrita em
/// <c>HKU\&lt;SID&gt;</c> fica nos backends do Windows.
/// </summary>
public static partial class UserProfileScope
{
    public const string TargetKey = "userSid";

    [GeneratedRegex(@"^S-1-5-21(-[0-9]{1,10}){4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    /// <summary>Só contas locais/de domínio (<c>S-1-5-21-…</c>); nunca SYSTEM, .DEFAULT ou contas de serviço.</summary>
    public static bool IsValidSid(string? sid) => sid is not null && SidPattern().IsMatch(sid);

    /// <summary>A ação escreve no HKCU (hive/escopo HKCU, ou handler que só existe em HKCU).</summary>
    public static bool IsUserScoped(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var t = action.Target;
        return (t.TryGetValue("hive", out var hive) && string.Equals(hive, "HKCU", StringComparison.OrdinalIgnoreCase))
            || (t.TryGetValue("scope", out var scope) && string.Equals(scope, "HKCU", StringComparison.OrdinalIgnoreCase))
            || action.Definition.HandlerKind == "DirectXUserGpuPreference";
    }

    /// <summary>Cópia da ação com o perfil do usuário no alvo.</summary>
    public static ActionInstance Bind(ActionInstance action, string sid)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!IsValidSid(sid)) throw new ArgumentException("SID de usuário inválido.", nameof(sid));
        var target = new Dictionary<string, string>(action.Target, StringComparer.Ordinal) { [TargetKey] = sid };
        return action with { Target = target };
    }
}
