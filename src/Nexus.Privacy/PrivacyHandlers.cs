using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Privacy;

/// <summary>Handlers deste módulo registrados no aplicador (broker). Cada handler precisa de ação correspondente no catálogo.</summary>
/// <remarks>
/// As ações fora de <c>SOFTWARE\Policies</c> usam o <c>RegistryValue</c> do núcleo (com a política de segurança geral). Só os
/// valores de política de privacidade, que a política geral proíbe em bloco, passam pelo <see cref="PrivacyPolicyValueHandler"/>,
/// restrito à lista fechada <see cref="PrivacyPolicyAllowlist"/>.
/// </remarks>
public static class PrivacyHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return [new PrivacyPolicyValueHandler(environment.Registry)];
    }
}
