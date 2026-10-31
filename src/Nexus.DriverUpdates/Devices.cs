using System.Text.RegularExpressions;
using Nexus.SystemTools.Drivers;

namespace Nexus.DriverUpdates;

/// <summary>
/// Um dispositivo presente e o driver que ele usa agora (SetupAPI + chave do driver em Control\Class).
/// </summary>
/// <param name="InfName">INF do driver atual como o Windows registra: "oemNN.inf" (terceiros, no repositório) ou o nome de um INF
/// do próprio Windows (ex.: "display.inf").</param>
/// <param name="MatchingDeviceId">Hardware/compatible ID que o INF atual casou (é o que um reinstalar forçado precisa usar).</param>
public sealed record DeviceDriver(
    string InstanceId,
    string Description,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? ClassName,
    string? ClassGuid,
    string? Manufacturer,
    string? DriverVersion,
    DateOnly? DriverDate,
    string? DriverProvider,
    string? InfName,
    string? MatchingDeviceId);

/// <summary>Dispositivos presentes com o driver atual (leitura; não precisa de administrador).</summary>
public interface IDeviceInventory
{
    IReadOnlyList<DeviceDriver> List();
}

/// <summary>Cruzamento das ofertas do Windows Update com o inventário de dispositivos.</summary>
public static class DeviceMatching
{
    /// <summary>
    /// Dispositivos a que a atualização se aplica: o DriverHardwareID do WUA casa (sem diferenciar caixa) um hardware ID ou
    /// compatible ID do dispositivo. Só sem hardware ID na oferta cai para o modelo (DriverModel = descrição do dispositivo).
    /// </summary>
    public static IReadOnlyList<DeviceDriver> Match(WuDriverUpdate update, IReadOnlyList<DeviceDriver> devices)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(devices);
        // Com hardware ID, só ele decide (o modelo é texto livre e poderia casar outro aparelho com o mesmo nome).
        if (update.HardwareId is { } hw)
            return devices.Where(d => d.HardwareIds.Concat(d.CompatibleIds).Any(i => string.Equals(i, hw, StringComparison.OrdinalIgnoreCase))).ToList();
        if (update.Model is { } model)
            return devices.Where(d => string.Equals(d.Description, model, StringComparison.OrdinalIgnoreCase)).ToList();
        return [];
    }

    /// <summary>Versões iguais (a.b.c.d com zeros à esquerda ou campos faltando tratados como o Windows: 1.2 == 1.2.0.0).</summary>
    public static bool SameVersion(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (Version.TryParse(a.Trim(), out var va) && Version.TryParse(b.Trim(), out var vb)) return Normalize(va) == Normalize(vb);
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>-1/0/1 quando as duas versões são numéricas; null se não der para comparar.</summary>
    public static int? CompareVersions(string? a, string? b) =>
        a is not null && b is not null && Version.TryParse(a.Trim(), out var va) && Version.TryParse(b.Trim(), out var vb)
            ? Math.Sign(Normalize(va).CompareTo(Normalize(vb))) : null;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}

/// <summary>De onde virá o driver anterior se o usuário pedir para desfazer.</summary>
public enum RollbackSource
{
    /// <summary>Pacote de terceiros (oemNN.inf): exportado com pnputil ANTES de instalar o novo.</summary>
    Backup,
    /// <summary>INF do próprio Windows (%SystemRoot%\INF\x.inf): fica sempre no PC; reinstala de lá.</summary>
    Inbox,
    /// <summary>Não há como voltar (dispositivo sem driver, ou sem INF/ID que case).</summary>
    None,
}

public sealed record RollbackPlan(RollbackSource Source, string? InfName, string? MatchingDeviceId, string? PreviousVersion, string Explanation)
{
    public bool Possible => Source != RollbackSource.None;
}

/// <summary>Seleção do "o que reverter": decide, ANTES de instalar, se e como o driver atual poderá voltar.</summary>
public static partial class RollbackRules
{
    [GeneratedRegex(@"\A[A-Za-z0-9_\-]{1,64}\.inf\z", RegexOptions.CultureInvariant)]
    private static partial Regex InboxInfName();

    public static bool IsInboxInfName(string? name) => name is not null && InboxInfName().IsMatch(name) && !DriverParsers.IsOemInf(name);

    public static RollbackPlan Plan(DeviceDriver? device)
    {
        if (device is null)
            return new(RollbackSource.None, null, null, null, "Nenhum dispositivo presente para esta atualização: não há driver anterior a guardar.");
        if (string.IsNullOrWhiteSpace(device.InfName) || string.IsNullOrWhiteSpace(device.DriverVersion))
            return new(RollbackSource.None, null, null, null, "O dispositivo está sem driver agora: não há driver anterior para voltar.");
        if (string.IsNullOrWhiteSpace(device.MatchingDeviceId) || device.MatchingDeviceId.Length > 200 || device.MatchingDeviceId.Any(char.IsControl))
            return new(RollbackSource.None, device.InfName, null, device.DriverVersion,
                "O Windows não registrou qual ID o driver atual usa: o NEXUS não consegue reinstalá-lo com segurança.");
        var inf = device.InfName.Trim().ToLowerInvariant();
        if (DriverParsers.IsOemInf(inf))
            return new(RollbackSource.Backup, inf, device.MatchingDeviceId, device.DriverVersion,
                $"Antes de instalar, o NEXUS exporta o driver atual ({inf}, versão {device.DriverVersion}) com pnputil; Desfazer reinstala esse pacote.");
        if (IsInboxInfName(inf))
            return new(RollbackSource.Inbox, inf, device.MatchingDeviceId, device.DriverVersion,
                $"O driver atual ({inf}, versão {device.DriverVersion}) é do próprio Windows e fica no PC; Desfazer reinstala ele.");
        return new(RollbackSource.None, device.InfName, device.MatchingDeviceId, device.DriverVersion, "INF do driver atual com nome inesperado: sem desfazer automático.");
    }
}
