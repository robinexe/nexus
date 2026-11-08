using Nexus.Core;
using Nexus.WindowsActions;

namespace Nexus.Measurement;

/// <summary>
/// Grupo local do Windows (por SID) e a marca "foi o NEXUS que adicionou" (HKLM\SOFTWARE\NEXUS\PerfLogGroup, um valor por
/// conta). O desinstalador usa a marca para tirar do grupo só quem o NEXUS (ou o próprio instalador) colocou lá.
/// </summary>
public interface ILocalGroupBackend
{
    /// <summary>A conta é membro DIRETO do grupo (o que o NEXUS acrescenta e remove).</summary>
    bool IsMember(string groupSid, string userSid);
    void AddMember(string groupSid, string userSid);
    void RemoveMember(string groupSid, string userSid);
    /// <summary>Grava a marca de que esta conta foi adicionada pelo NEXUS (<paramref name="by"/>: "app" ou "installer").</summary>
    void SetMarker(string userSid, string by);
    void ClearMarker(string userSid);
    bool HasMarker(string userSid);
}

/// <summary>
/// Ação <c>measure.perflog-group.join</c>: coloca a conta de quem pede no grupo local "Usuários do log de desempenho"
/// (Performance Log Users, S-1-5-32-559), para o PresentMon medir sem pedir permissão ao Windows a cada partida.
/// <list type="bullet">
/// <item>Detect: a conta é membro direto? (Membership:Member) ou ausente.</item>
/// <item>Apply: adiciona (NetLocalGroupAddMembers) se ainda não é membro, e grava a marca do NEXUS.</item>
/// <item>Restore: se antes era membro, garante que continua; se antes NÃO era (o NEXUS adicionou), remove e apaga a marca.
/// Nunca remove alguém que já estava no grupo antes do NEXUS.</item>
/// </list>
/// A conta vem do SID do pedido (<c>userSid</c>, acrescentado pelo aplicador quando roda como serviço ou com outra conta — B10);
/// sem ele, só vale a conta do próprio processo, e só quando o aplicador roda com a mesma conta de quem pede.
/// </summary>
public sealed class PerfLogGroupHandler(ILocalGroupBackend backend, Func<string?> processUserSid, bool otherAccount) : IActionHandler
{
    public const string Kind = "MeasureLocalGroupMember";
    /// <summary>BUILTIN\Performance Log Users ("Usuários do log de desempenho" no Windows em português).</summary>
    public const string PerformanceLogUsersSid = "S-1-5-32-559";
    public const string MembershipType = "Membership";
    public const string MemberValue = "Member";
    public const string UserDetailKey = "user";

    private readonly ILocalGroupBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    private readonly Func<string?> _processUserSid = processUserSid ?? throw new ArgumentNullException(nameof(processUserSid));

    public string HandlerKind => Kind;

    /// <summary>Só este grupo: a ação não serve para colocar ninguém em Administradores nem em outro grupo.</summary>
    private static string? Group(ActionInstance action) =>
        action.Target.TryGetValue("group", out var g) && string.Equals(g, PerformanceLogUsersSid, StringComparison.Ordinal) ? g : null;

    /// <summary>Conta alvo: o SID do pedido; sem ele, a conta do processo (só se o aplicador é a própria conta de quem pede).</summary>
    public string? ResolveUser(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Target.TryGetValue(UserProfileScope.TargetKey, out var sid))
            return UserProfileScope.IsValidSid(sid) ? sid : null;
        if (otherAccount) return null;
        var me = _processUserSid();
        return UserProfileScope.IsValidSid(me) ? me : null;
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var common = CommonAssessment.Check(action, context);
        if (common.Kind != ApplicabilityKind.Applicable) return common;
        if (Group(action) is null) return Applicability.Blocked("Só o grupo \"Usuários do log de desempenho\" (S-1-5-32-559) é aceito.");
        if (ResolveUser(action) is null)
            return Applicability.Blocked("Não deu para saber qual conta adicionar (o aplicador roda com outra conta e o pedido não trouxe a sua).");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var (group, user) = Require(action);
        var detail = new Dictionary<string, string>(StringComparer.Ordinal) { [UserDetailKey] = user };
        return _backend.IsMember(group, user) ? StateSnapshot.Of(MembershipType, MemberValue, ValueOrigin.Explicit, detail) : StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action)
    {
        var (group, user) = Require(action);
        if (_backend.IsMember(group, user)) return; // já estava: nada a fazer, e sem marca (não foi o NEXUS)
        _backend.AddMember(group, user);
        _backend.SetMarker(user, "app");
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var (group, user) = Require(action);
        if (original.Detail is { } d && d.TryGetValue(UserDetailKey, out var recorded) && !string.Equals(recorded, user, StringComparison.Ordinal))
            throw new InvalidOperationException("Esta mudança foi feita para outra conta. Desfaça a partir da conta que pediu.");
        if (original.Exists)
        {
            if (!_backend.IsMember(group, user)) _backend.AddMember(group, user);
            return;
        }
        // Antes NÃO era membro: foi o NEXUS que adicionou. Remove e apaga a marca.
        if (_backend.IsMember(group, user)) _backend.RemoveMember(group, user);
        _backend.ClearMarker(user);
    }

    private (string Group, string User) Require(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var group = Group(action) ?? throw new InvalidOperationException("Grupo não permitido para esta ação.");
        var user = ResolveUser(action) ?? throw new InvalidOperationException("Conta de usuário desconhecida; nada foi alterado.");
        return (group, user);
    }
}

/// <summary>Handlers do módulo Medir (registrados pelo aplicador em ModuleHandlers).</summary>
public static class MeasureHandlers
{
    public static IEnumerable<IActionHandler> Create(HandlerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsWindows()) return [];
        return
        [
            new PerfLogGroupHandler(new Windows.WindowsLocalGroupBackend(), Windows.WindowsLocalGroupBackend.CurrentUserSid, environment.OtherAccount),
            new DriverTraceHandler(new Windows.WindowsKernelTrace(), sid => LocalAppDataForSid(environment, sid), environment.OtherAccount),
        ];
    }

    /// <summary>
    /// %LocalAppData% de quem pediu, pelo ProfileList (HKLM) e pelo SID — nunca por variáveis de ambiente do aplicador (que pode ser
    /// um serviço). Sem SID: a conta deste processo, só quando o aplicador é a própria conta de quem pede. Mesma regra do Fortnite/Limpeza.
    /// </summary>
    public static string? LocalAppDataForSid(HandlerEnvironment environment, string? sid)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsWindows()) return null;
        var who = sid ?? (environment.OtherAccount ? null : Windows.WindowsLocalGroupBackend.CurrentUserSid());
        if (!UserProfileScope.IsValidSid(who)) return null;
        string? profile;
        try
        {
            var r = environment.Registry.Read(new Nexus.WindowsActions.Registry.RegistryTarget(Nexus.WindowsActions.Registry.RegHive.HKLM,
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{who}", "ProfileImagePath", Nexus.WindowsActions.Registry.RegView.Registry64));
            if (!r.ValueExists || r.Value is null) return null;
            profile = r.Value.Replace("%SystemDrive%", Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { return null; }
        if (profile.Contains('%', StringComparison.Ordinal) || profile.Contains("..", StringComparison.Ordinal) ||
            profile.Length < 3 || !char.IsAsciiLetter(profile[0]) || profile[1] != ':' || profile[2] is not ('\\' or '/')) return null;
        return Path.Combine(profile.TrimEnd('\\', '/'), "AppData", "Local");
    }
}
