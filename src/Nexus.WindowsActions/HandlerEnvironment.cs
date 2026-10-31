using Nexus.WindowsActions.Registry;

namespace Nexus.WindowsActions;

/// <summary>
/// O que o aplicador (broker) oferece aos handlers dos módulos ao registrá-los.
/// </summary>
/// <param name="Registry">Backend de registro do aplicador (já recusa HKCU sem perfil quando roda com outra conta).</param>
/// <param name="DataDirectory">Pasta de dados do aplicador (a do diário): %LocalAppData%\NEXUS (usuário) ou %ProgramData%\NEXUS
/// (administrador/serviço). Quarentenas e backups dos módulos ficam aqui, em subpastas próprias.</param>
/// <param name="OtherAccount">O aplicador roda com outra conta que não a de quem pede (serviço ou elevação com outra credencial):
/// caminhos de perfil (%LocalAppData%, %TEMP% do usuário) não podem ser deduzidos do próprio processo.</param>
/// <param name="Elevated">O aplicador está elevado (administrador).</param>
public sealed record HandlerEnvironment(IRegistryBackend Registry, string DataDirectory, bool OtherAccount, bool Elevated);
