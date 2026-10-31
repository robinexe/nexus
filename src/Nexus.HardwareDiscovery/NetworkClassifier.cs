using System.Text.RegularExpressions;

namespace Nexus.HardwareDiscovery;

/// <summary>Regras puras (testáveis sem Windows) para não confundir vínculos de filtro NDIS com adaptadores.</summary>
public static partial class NetworkClassifier
{
    [GeneratedRegex(@"-\d{4}$")]
    private static partial Regex BindingSuffix();

    /// <summary>
    /// O Windows expõe cada filtro leve ligado a um adaptador (WFP, QoS Packet Scheduler, monitores de rede, filtros Wi-Fi)
    /// como uma "interface" extra cujo nome e descrição terminam em "-0000". Não são adaptadores.
    /// </summary>
    public static bool IsFilterBinding(string name, string description) =>
        BindingSuffix().IsMatch(name ?? "") && BindingSuffix().IsMatch(description ?? "");

    /// <summary>IPv4 que serve para comunicação: exclui APIPA (169.254/16, sem DHCP) e 0.0.0.0.</summary>
    public static bool IsUsableIpv4(string address) =>
        !string.IsNullOrEmpty(address) && address != "0.0.0.0" && !address.StartsWith("169.254.", StringComparison.Ordinal);

    /// <summary>Nome do tipo; valores fora do enum do .NET (ex.: 53 = propVirtual da IANA) aparecem com o número.</summary>
    public static string TypeName(int value, string? enumName) =>
        enumName is not null && !int.TryParse(enumName, out _) ? enumName
        : value == 53 ? "Virtual (53, propVirtual)"
        : $"Tipo {value}";
}
