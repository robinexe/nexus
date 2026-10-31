namespace Nexus.Broker;

public sealed record GateDecision(bool Allowed, string Reason);

/// <summary>Decide se o broker pode escrever no sistema. Consultado antes de toda operação que escreve.</summary>
public interface IExecutionGate
{
    GateDecision Evaluate();
}

/// <summary>Padrão seguro: nunca permite.</summary>
public sealed class AlwaysBlockedGate : IExecutionGate
{
    public GateDecision Evaluate() => new(false, "Execução real desativada nesta instalação.");
}

/// <summary>Bloqueio com motivo próprio (ex.: a pasta de dados do aplicador elevado não pôde ser protegida).</summary>
public sealed class BlockedGate(string reason) : IExecutionGate
{
    public GateDecision Evaluate() => new(false, reason);
}

/// <summary>
/// Libera escrita somente em máquina virtual de teste, com dupla confirmação:
/// <list type="number">
/// <item>o SMBIOS indica máquina virtual (inferência da descoberta: VMware, VirtualBox, QEMU, Hyper-V...);</item>
/// <item>existe o arquivo de liberação (padrão <c>%ProgramData%\NEXUS\vm-test-host.allow</c>) cuja primeira linha é o nome
/// desta máquina. Copiar o arquivo para outro PC não o libera.</item>
/// </list>
/// No PC físico de jogos, a condição 1 já falha: nada é escrito, mesmo que o arquivo exista.
/// </summary>
public sealed class VmOnlyExecutionGate : IExecutionGate
{
    public const string MarkerFileName = "vm-test-host.allow";
    private readonly Func<bool?> _isVirtualMachine;
    private readonly string _markerPath;
    private readonly string _machineName;
    private readonly Func<string, string?>? _markerTrust;

    /// <param name="markerTrust">Checagem de confiança do arquivo (no Windows: dono administrador e sem escrita para usuários
    /// comuns). Retorna null se confiável ou o motivo. Null = sem checagem (apenas testes fora do Windows).</param>
    public VmOnlyExecutionGate(Func<bool?> isVirtualMachine, string markerPath, string machineName, Func<string, string?>? markerTrust = null)
    {
        _markerTrust = markerTrust;
        _isVirtualMachine = isVirtualMachine ?? throw new ArgumentNullException(nameof(isVirtualMachine));
        _markerPath = markerPath ?? throw new ArgumentNullException(nameof(markerPath));
        _machineName = machineName ?? throw new ArgumentNullException(nameof(machineName));
    }

    public static string DefaultMarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", MarkerFileName);

    public GateDecision Evaluate()
    {
        bool? vm;
        try { vm = _isVirtualMachine(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { vm = null; }

        if (vm != true)
            return new(false, vm == false
                ? "Hardware físico detectado (SMBIOS): execução real bloqueada. Testes de alteração só em VM Windows isolada."
                : "Não foi possível confirmar que esta é uma máquina virtual: execução real bloqueada.");

        string? firstLine;
        try
        {
            if (!File.Exists(_markerPath))
                return new(false, $"VM detectada, mas falta o arquivo de liberação {_markerPath} com o nome desta máquina ({_machineName}).");
            firstLine = File.ReadLines(_markerPath).FirstOrDefault()?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, $"Arquivo de liberação ilegível: {ex.Message}");
        }

        if (!string.Equals(firstLine, _machineName, StringComparison.OrdinalIgnoreCase))
            return new(false, $"O arquivo de liberação não corresponde a esta máquina (contém \"{firstLine}\", esperado \"{_machineName}\").");

        string? distrust;
        try { distrust = _markerTrust?.Invoke(_markerPath); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { distrust = $"Não foi possível verificar o dono do arquivo de liberação: {ex.Message}"; }
        return distrust is null
            ? new(true, $"VM de teste liberada pelo arquivo {_markerPath}.")
            : new(false, distrust);
    }
}

/// <summary>
/// Execução neste PC autorizada pelo usuário (decisão do responsável em 31/10/2026: o NEXUS aplica de verdade em qualquer PC).
/// O broker só recebe <c>--allow-host</c> quando é iniciado pela interface (depois da confirmação do usuário e, para ações de
/// administrador, do aviso do Windows) ou pelo instalador do serviço com <c>-AllowHost</c>. As proteções continuam: catálogo
/// fechado, política de alvos proibidos, diário antes de escrever, verificação, reversão e ponto de restauração.
/// </summary>
public sealed class HostConsentGate : IExecutionGate
{
    public GateDecision Evaluate() => new(true, "Aplicação neste PC autorizada pelo usuário. Tudo é registrado no diário e pode ser desfeito.");
}

/// <summary>Libera se qualquer um dos portões liberar; o motivo é o do primeiro que liberou (ou o do último, se nenhum).</summary>
public sealed class AnyGate(params IExecutionGate[] gates) : IExecutionGate
{
    public GateDecision Evaluate()
    {
        GateDecision? last = null;
        foreach (var g in gates)
        {
            last = g.Evaluate();
            if (last.Allowed) return last;
        }
        return last ?? new(false, "Nenhum portão configurado.");
    }
}
