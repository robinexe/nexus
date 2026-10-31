namespace Nexus.Core;

/// <summary>
/// Validação estática (sem ler nem escrever no sistema) de uma restauração vinda do diário: forma do alvo, tipos e a
/// política de segurança. O executor a chama antes de toda restauração, porque alvo e valor original são desserializados
/// do diário e não passaram pelo Assess nesta invocação. Retorna null se permitido, ou o motivo da recusa.
/// </summary>
public interface IRestoreValidator
{
    string? CheckRestore(ActionInstance action, StateSnapshot original);
}
