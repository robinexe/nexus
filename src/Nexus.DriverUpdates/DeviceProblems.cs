using System.Globalization;

namespace Nexus.DriverUpdates;

/// <summary>Como o NEXUS agrupa um código de problema do Gerenciador de Dispositivos.</summary>
public enum DeviceProblemKind
{
    /// <summary>O Windows não instalou driver para o aparelho (códigos 1, 18 e 28): "dispositivo sem driver".</summary>
    MissingDriver,
    /// <summary>Tem driver, mas ele não carregou ou parou (ex.: 10, 31, 37, 39, 43, 52).</summary>
    DriverFailed,
    /// <summary>Outro problema (recursos, reinício pendente, dependência).</summary>
    Other,
    /// <summary>Desativado por você, pelo firmware ou preparado para remoção (22, 29, 47): não é defeito.</summary>
    Disabled,
}

/// <summary>Um dispositivo presente que o Windows marca com problema (o ponto de exclamação amarelo do Gerenciador de Dispositivos).</summary>
public sealed record DeviceProblem(string InstanceId, string Name, string? HardwareId, string? ClassName, int Code, DeviceProblemKind Kind, string Meaning)
{
    /// <summary>"Código 28", como o Gerenciador de Dispositivos mostra.</summary>
    public string CodeText => "Código " + Code.ToString(CultureInfo.InvariantCulture);

    public bool NeedsDriver => Kind is DeviceProblemKind.MissingDriver or DeviceProblemKind.DriverFailed;
}

/// <summary>
/// "Ver se falta algum driver": dispositivos presentes com código de problema (CM_PROB_*). Significados conforme a tabela oficial
/// "Device Manager error messages" (<see cref="Source"/>). O conserto mais comum para "sem driver" é o próprio Windows Update (ele
/// oferece drivers também para aparelhos sem driver) ou o site do fabricante do PC/aparelho. Puro: testável em qualquer SO.
/// </summary>
public static class DeviceProblems
{
    public const string Source = "https://learn.microsoft.com/windows-hardware/drivers/install/device-manager-error-messages";

    private static readonly Dictionary<int, (DeviceProblemKind Kind, string Meaning)> Codes = new()
    {
        [1] = (DeviceProblemKind.MissingDriver, "Não está configurado: o Windows não achou driver para ele."),
        [3] = (DeviceProblemKind.DriverFailed, "O driver pode estar corrompido, ou faltou memória ao carregá-lo."),
        [10] = (DeviceProblemKind.DriverFailed, "O dispositivo não pôde ser iniciado (driver com defeito ou incompatível)."),
        [12] = (DeviceProblemKind.Other, "Recursos insuficientes (conflito com outro dispositivo)."),
        [14] = (DeviceProblemKind.Other, "Só funciona depois de reiniciar o PC."),
        [18] = (DeviceProblemKind.MissingDriver, "Os drivers precisam ser reinstalados."),
        [19] = (DeviceProblemKind.DriverFailed, "A configuração do driver no registro está danificada."),
        [21] = (DeviceProblemKind.Other, "O Windows está removendo este dispositivo."),
        [22] = (DeviceProblemKind.Disabled, "Desativado (no Gerenciador de Dispositivos)."),
        [24] = (DeviceProblemKind.Other, "Não está presente, não funciona direito ou não tem todos os drivers."),
        [28] = (DeviceProblemKind.MissingDriver, "Os drivers não estão instalados."),
        [29] = (DeviceProblemKind.Disabled, "Desativado pelo firmware (BIOS/UEFI)."),
        [31] = (DeviceProblemKind.DriverFailed, "O Windows não conseguiu carregar o driver."),
        [32] = (DeviceProblemKind.DriverFailed, "O serviço do driver está desativado."),
        [33] = (DeviceProblemKind.Other, "O Windows não conseguiu determinar os recursos do dispositivo."),
        [34] = (DeviceProblemKind.Other, "Precisa de configuração manual (veja o manual do aparelho)."),
        [35] = (DeviceProblemKind.Other, "O firmware não informa os recursos deste dispositivo (atualize o BIOS)."),
        [37] = (DeviceProblemKind.DriverFailed, "O driver falhou ao iniciar."),
        [38] = (DeviceProblemKind.DriverFailed, "Uma instância anterior do driver ainda está na memória (reinicie)."),
        [39] = (DeviceProblemKind.DriverFailed, "O driver está corrompido ou ausente."),
        [40] = (DeviceProblemKind.DriverFailed, "A informação do serviço do driver no registro está faltando ou errada."),
        [41] = (DeviceProblemKind.DriverFailed, "O driver carregou, mas não encontrou o aparelho."),
        [42] = (DeviceProblemKind.Other, "Dispositivo duplicado no sistema."),
        [43] = (DeviceProblemKind.DriverFailed, "O Windows parou o dispositivo porque ele relatou problemas."),
        [44] = (DeviceProblemKind.Other, "Um programa ou serviço desligou o dispositivo."),
        [47] = (DeviceProblemKind.Disabled, "Preparado para remoção segura."),
        [48] = (DeviceProblemKind.DriverFailed, "O driver foi bloqueado por ter problemas conhecidos."),
        [49] = (DeviceProblemKind.Other, "O registro do sistema passou do limite de tamanho."),
        [50] = (DeviceProblemKind.Other, "O Windows não aplicou todas as propriedades do dispositivo."),
        [51] = (DeviceProblemKind.Other, "Esperando outro dispositivo iniciar."),
        [52] = (DeviceProblemKind.DriverFailed, "O driver não tem assinatura digital válida."),
        [54] = (DeviceProblemKind.DriverFailed, "O dispositivo falhou e está sendo reiniciado."),
    };

    public static DeviceProblemKind KindOf(int code) => Codes.TryGetValue(code, out var c) ? c.Kind : DeviceProblemKind.Other;

    public static string MeaningOf(int code) =>
        Codes.TryGetValue(code, out var c) ? c.Meaning : $"Problema código {code.ToString(CultureInfo.InvariantCulture)} (veja o Gerenciador de Dispositivos).";

    /// <summary>Dispositivos com código de problema (≠ 0): sem driver primeiro, depois driver com falha, outros e desativados.</summary>
    public static IReadOnlyList<DeviceProblem> From(IEnumerable<DeviceDriver> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices
            .Where(d => d.ProblemCode is > 0)
            .Select(d => new DeviceProblem(d.InstanceId, string.IsNullOrWhiteSpace(d.Description) ? d.InstanceId : d.Description.Trim(),
                d.HardwareIds.Count > 0 ? d.HardwareIds[0] : null, d.ClassName, d.ProblemCode!.Value, KindOf(d.ProblemCode.Value), MeaningOf(d.ProblemCode.Value)))
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Os que precisam de driver (sem driver ou com driver que não carrega): o que o cartão "Dispositivos sem driver" mostra.</summary>
    public static IReadOnlyList<DeviceProblem> NeedingDriver(IEnumerable<DeviceProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return problems.Where(p => p.NeedsDriver).ToList();
    }

    /// <summary>O que fazer, em poucas frases, quando há dispositivo sem driver.</summary>
    public const string Advice =
        "Procure no Windows Update: ele oferece drivers também para aparelhos sem driver (às vezes em \"atualizações opcionais\"). " +
        "Se não aparecer, baixe no site do fabricante do PC ou do aparelho; o hardware ID mostrado aqui ajuda a achar o driver certo.";
}
