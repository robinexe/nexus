namespace Nexus.Boot;

/// <summary>
/// Lista proibida de serviços do módulo (além de <see cref="Nexus.WindowsActions.SafetyPolicy.CheckService"/>). Vale para
/// qualquer pedido, com qualquer catálogo: nenhum destes serviços tem o tipo de início alterado pelo NEXUS.
/// Critério: sem eles o Windows não inicia, perde rede/som/atualizações/segurança, ou jogos com anticheat deixam de abrir.
/// </summary>
public static class ServicePolicy
{
    /// <summary>Nunca alterados (motivo resumido por grupo).</summary>
    public static readonly IReadOnlyDictionary<string, string> Forbidden = Build(
        ("núcleo do Windows (RPC/COM, sessões, perfis, contas)",
            ["RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "ProfSvc", "UserManager", "Power", "PlugPlay", "BrokerInfrastructure",
             "CoreMessagingRegistrar", "StateRepository", "SystemEventsBroker", "TimeBrokerSvc", "SENS", "gpsvc", "Schedule", "KeyIso",
             "VaultSvc", "Appinfo", "TrustedInstaller", "EventLog", "EventSystem", "Winmgmt", "CryptSvc", "DeviceInstall", "camsvc"]),
        ("segurança (firewall, Defender, Central de Segurança)",
            ["BFE", "MpsSvc", "WinDefend", "WdNisSvc", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc", "SgrmBroker",
             "WaaSMedicSvc", "mpssvc"]),
        ("rede básica (endereço IP, nomes, conexões)",
            ["Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "Wcmsvc", "LanmanWorkstation", "WinHttpAutoProxySvc", "BITS"]),
        ("som", ["AudioSrv", "AudioEndpointBuilder"]),
        ("anticheat de jogos (o jogo deixa de abrir)",
            ["vgc", "vgk", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceit", "PnkBstrA", "PnkBstrB", "mhyprot2", "ACE-BASE"]));

    /// <summary>Podem ir para Manual (ou voltar a Automático), mas nunca para Desativado: o Windows Update deixaria de funcionar.</summary>
    public static readonly IReadOnlySet<string> NeverDisabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wuauserv", "UsoSvc", "DoSvc", "InstallService", "msiserver",
    };

    private static Dictionary<string, string> Build(params (string Reason, string[] Names)[] groups)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reason, names) in groups)
            foreach (var n in names) d.TryAdd(n, reason);
        return d;
    }

    /// <summary>Motivo da recusa, ou null se o serviço pode ir para <paramref name="desiredStartType"/>.</summary>
    public static string? Check(string serviceName, string? desiredStartType)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return "Serviço não informado.";
        if (Nexus.WindowsActions.SafetyPolicy.CheckService(serviceName) is { } core) return core;
        if (Forbidden.TryGetValue(serviceName, out var why)) return $"Serviço '{serviceName}' é protegido pelo NEXUS: {why}.";
        if (desiredStartType == "Disabled" && NeverDisabled.Contains(serviceName))
            return $"'{serviceName}' pode ficar em Manual, mas nunca Desativado (o Windows Update deixaria de funcionar).";
        return null;
    }

    public static bool IsProtected(string serviceName) => Check(serviceName, null) is not null;

    /// <summary>
    /// Serviço por usuário (instância "Nome_1a2b3c" ou modelo): o Windows recria a instância a cada logon, então alterar o tipo de
    /// início da instância não persiste e o do modelo afeta todos os usuários. Não alteramos nenhum dos dois.
    /// </summary>
    public static bool IsPerUserService(int serviceType) => (serviceType & 0x40) != 0 || (serviceType & 0x80) != 0;

    /// <summary>Somente processos de serviço Win32 (0x10 próprio, 0x20 compartilhado). Drivers (0x1, 0x2, 0x8) nunca.</summary>
    public static bool IsWin32Service(int serviceType) => (serviceType & 0x30) != 0 && (serviceType & 0x0B) == 0;
}
