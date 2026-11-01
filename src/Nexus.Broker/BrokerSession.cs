using System.Text;
using Nexus.Core;

namespace Nexus.Broker;

/// <summary>Resultado de uma chamada ao broker: se houve conexão, a resposta e uma mensagem pronta para mostrar.</summary>
public sealed record BrokerCallResult(bool Connected, BrokerResponse? Response, string Message)
{
    public bool Ok => Connected && Response is { Ok: true };
}

/// <summary>
/// Conversa da interface com o broker, sem WPF (testável). Cada chamada abre uma conexão, envia um pedido e fecha.
/// A interface só usa <see cref="ForInstalledService"/>: o cliente confere que o dono do pipe é SYSTEM/Administradores (B3)
/// e o serviço decide tudo (catálogo, portão de VM, trava, diário). A interface não tem caminho de execução local.
/// </summary>
public sealed class BrokerSession
{
    private readonly Func<BrokerClient> _connect;

    /// <param name="connect">Abre a conexão. Testes usam um pipe local; a interface usa <see cref="ForInstalledService"/>.</param>
    public BrokerSession(Func<BrokerClient> connect) => _connect = connect ?? throw new ArgumentNullException(nameof(connect));

    /// <summary>O broker instalado como serviço do Windows (NexusBroker), com verificação do dono do pipe.</summary>
    public static BrokerSession ForInstalledService(string pipeName = BrokerPipeServer.DefaultPipeName, int timeoutMs = 3000) =>
        new(() => BrokerClient.Connect(pipeName, timeoutMs, BrokerServerKind.Service));

    /// <summary>
    /// Aplicar neste PC: broker sob demanda do nível pedido (usuário sem aviso; administrador com o aviso do Windows ou pelo
    /// serviço instalado). Ver <see cref="LocalBroker"/>.
    /// </summary>
    /// <param name="connected">Chamado depois de conectar (antes de enviar o pedido): a tela de progresso marca "aplicando".</param>
    public static BrokerSession ForThisPc(BrokerLevel level, string brokerDirectory, string userSid, Action<string>? progress = null, Action? connected = null) =>
        new(() =>
        {
            var client = LocalBroker.Connect(level, brokerDirectory, userSid, progress);
            connected?.Invoke();
            return client;
        });

    public BrokerCallResult Status() => Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.Status));
    public BrokerCallResult Plan(IReadOnlyList<ActionRequest> actions) => Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.Plan, actions));
    public BrokerCallResult Execute(IReadOnlyList<ActionRequest> actions, string profile = "interface") =>
        Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.Execute, actions, Profile: profile));
    public BrokerCallResult RevertRun(Guid runId) => Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: runId));
    public BrokerCallResult EndSession(Guid runId) => Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.EndSession, RunId: runId));
    public BrokerCallResult Recover() => Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.Recover));

    private BrokerCallResult Call(BrokerRequest request)
    {
        BrokerClient client;
        try
        {
            client = _connect();
        }
        catch (TimeoutException)
        {
            return new(false, null, "O aplicador do NEXUS não respondeu (não instalado, parado ou não iniciou). Nada foi enviado.");
        }
        catch (OperationCanceledException ex)
        {
            return new(false, null, ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new(false, null, $"Não foi possível iniciar o aplicador do NEXUS: {ex.Message}. Nada foi alterado.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(false, null, $"Conexão recusada: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return new(false, null, $"Não foi possível conectar ao broker: {ex.Message}");
        }

        using (client)
        {
            try
            {
                var response = client.Send(request);
                return new(true, response, Describe(request.Op, response));
            }
            catch (Exception ex) when (ex is IOException or BrokerProtocolException or TimeoutException)
            {
                // Pedido pode ter chegado: para escrita, o diário do broker é a fonte da verdade (Histórico / Recover).
                return new(true, null, $"A conexão caiu durante {request.Op}: {ex.Message}. Confira o Histórico antes de repetir.");
            }
        }
    }

    private static string Describe(BrokerOperation op, BrokerResponse r)
    {
        if (!r.Ok) return $"{op} recusado pelo broker ({r.Error?.Code}): {r.Error?.Detail}";
        return op switch
        {
            BrokerOperation.Status when r.Status is { } s =>
                $"Broker {s.BrokerVersion} · catálogo {s.CatalogVersion} · escrita {(s.ExecutionAllowed ? "LIBERADA" : "BLOQUEADA")}: {s.ExecutionGateReason}",
            BrokerOperation.Execute => $"Execução {r.RunId}: {Count(r)}{(r.SessionActive == true ? " · sessão aberta (encerre para reverter os ajustes de sessão)" : "")}." + Extra(r.Message),
            BrokerOperation.RevertRun or BrokerOperation.EndSession or BrokerOperation.Recover =>
                $"{op}: {(r.FullyRestored == true ? "tudo restaurado" : "RESTAURAÇÃO PARCIAL, veja os itens")}. {r.Message}".TrimEnd(),
            _ => r.Message ?? "ok",
        };
    }

    /// <summary>O que o aplicador acrescentou além das frases padrão (ex.: ponto de restauração criado ou o motivo de não ter sido).</summary>
    private static string Extra(string? message) => ApplySummary.Extra(message) is { Length: > 0 } rest ? " " + rest : "";

    private static string Count(BrokerResponse r) =>
        r.Results is null ? "sem itens" : string.Join(", ", r.Results.GroupBy(x => x.Outcome).Select(g => $"{g.Count()} {g.Key}"));

    /// <summary>Pedido do broker a partir de ações do catálogo: só o id@versão e, quando o catálogo deixa escolher, o valor.</summary>
    public static IReadOnlyList<ActionRequest> RequestsFor(IEnumerable<(CatalogEntry Entry, ActionInstance Instance)> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        return chosen.Select(c => new ActionRequest(c.Entry.Definition.Key, c.Entry.DesiredValue is null ? c.Instance.Desired.Value : null)).ToList();
    }

    /// <summary>Texto da confirmação antes de aplicar: o que muda, onde e o que exige.</summary>
    public static string ConfirmationText(IReadOnlyList<(CatalogEntry Entry, ActionInstance Instance)> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var sb = new StringBuilder();
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Aplicar {chosen.Count} ação(ões) pelo broker?");
        sb.AppendLine();
        foreach (var (entry, instance) in chosen)
        {
            var d = entry.Definition;
            var flags = new List<string>();
            if (d.Privileges == PrivilegeLevel.Admin) flags.Add("administrador");
            if (d.Persistence == PersistenceKind.Session) flags.Add("sessão: revertida ao encerrar");
            else flags.Add("persistente");
            if (d.Reboot != RebootRequirement.None) flags.Add($"reinício: {d.Reboot}");
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"• {d.Title} → {instance.Desired.Describe()}  [{string.Join("; ", flags)}]");
        }
        sb.AppendLine();
        sb.AppendLine("Cada alteração é registrada no diário antes de ser feita e pode ser revertida em \"Reverter esta execução\".");
        sb.Append("O broker só escreve se estiver numa VM de teste liberada; fora dela, recusa sem alterar nada.");
        return sb.ToString();
    }
}
