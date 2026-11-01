using System.Text;
using Nexus.Core;

namespace Nexus.Broker;

/// <summary>Resultado de uma chamada ao broker: se houve conexão, a resposta e uma mensagem pronta para mostrar.</summary>
public sealed record BrokerCallResult(bool Connected, BrokerResponse? Response, string Message, IReadOnlyList<Guid>? RunIds = null)
{
    /// <summary>Todas as execuções gravadas (um pedido grande vira vários lotes de até 64 ações; desfazer volta todas).</summary>
    public IReadOnlyList<Guid> AllRunIds => RunIds ?? (Response?.RunId is { } r ? [r] : []);

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
    public BrokerCallResult Plan(IReadOnlyList<ActionRequest> actions) =>
        Batched(actions, chunk => Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.Plan, chunk)));
    public BrokerCallResult Execute(IReadOnlyList<ActionRequest> actions, string profile = "interface") =>
        Batched(actions, chunk => Call(new BrokerRequest(BrokerWire.ProtocolVersion, Guid.NewGuid(), BrokerOperation.Execute, chunk, Profile: profile)));

    private static readonly HashSet<string> PerActionRefusals = new(StringComparer.Ordinal)
        { "invalid-value", "invalid-arguments", "not-instantiable", "handler-unavailable", "unknown-action", "caller-unknown" };

    /// <summary>
    /// O aplicador recusa o pedido INTEIRO quando uma ação não passa na validação. Para uma lista grande (página Aplicar,
    /// Otimizar agora), uma ação inválida não deve impedir as outras: tira só a ação citada, registra o motivo como
    /// "Refused" e reenvia (no máximo 16 vezes). Nada foi escrito nas tentativas recusadas (validação vem antes de tudo).
    /// </summary>
    private static Func<IReadOnlyList<ActionRequest>, BrokerCallResult> Resilient(Func<IReadOnlyList<ActionRequest>, BrokerCallResult> send) => actions =>
    {
        var pending = actions.ToList();
        var refused = new List<ActionResultDto>();
        for (var attempt = 0; ; attempt++)
        {
            var r = send(pending);
            var error = r.Response is { Ok: false, Error: { } e } ? e : null;
            var culprit = error is not null && PerActionRefusals.Contains(error.Code) && pending.Count > 1 && attempt < 16
                ? pending.FirstOrDefault(p => error.Detail?.Contains("'" + p.ActionId + "'", StringComparison.Ordinal) == true)
                : null;
            if (culprit is null)
            {
                if (refused.Count == 0) return r;
                var results = refused.Concat(r.Response?.Results ?? []).ToList();
                var note = $" {refused.Count} ação(ões) recusada(s) pelo aplicador e puladas: {string.Join("; ", refused.Select(x => x.ActionKey + " (" + x.Detail + ")"))}.";
                return r with { Response = r.Response is null ? null : r.Response with { Results = results }, Message = r.Message + note };
            }
            refused.Add(new ActionResultDto(culprit.ActionId, "Refused", error!.Detail));
            pending.Remove(culprit);
        }
    };

    /// <summary>
    /// O aplicador aceita até <see cref="BrokerService.MaxActionsPerRequest"/> ações por pedido. Mais que isso vira lotes em
    /// sequência (mesma ordem); para no primeiro lote que falhar. Os resultados e as execuções (para desfazer) são somados.
    /// </summary>
    public static BrokerCallResult Batched(IReadOnlyList<ActionRequest> actions, Func<IReadOnlyList<ActionRequest>, BrokerCallResult> send)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(send);
        send = Resilient(send);
        if (actions.Count <= BrokerService.MaxActionsPerRequest) return send(actions);
        var results = new List<ActionResultDto>();
        var runs = new List<Guid>();
        var messages = new List<string>();
        BrokerCallResult last = null!;
        var batches = actions.Chunk(BrokerService.MaxActionsPerRequest).ToList();
        for (var i = 0; i < batches.Count; i++)
        {
            last = send(batches[i]);
            messages.Add($"Lote {i + 1}/{batches.Count}: {last.Message}");
            if (last.Response?.Results is { } rs) results.AddRange(rs);
            if (last.Response?.RunId is { } run) runs.Add(run);
            if (!last.Ok)
            {
                if (i + 1 < batches.Count) messages.Add($"Lotes {i + 2} a {batches.Count} não foram enviados.");
                break;
            }
        }
        var response = last.Response is null ? null : last.Response with { Results = results };
        return new BrokerCallResult(last.Connected, response, string.Join(" ", messages), runs);
    }
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
        // Mesma regra do aplicador (BrokerService.Validate): só manda valor quando o catálogo oferece uma escolha
        // (allowedValues). Valor fixo no catálogo ou alvo que já define o desejado (ex.: plano de energia pelo GUID): sem valor.
        // Ajustes de sessão por último: em lotes, só o último abre a sessão (os outros não esperam por ela).
        return chosen.OrderBy(c => c.Entry.Definition.Persistence == PersistenceKind.Session ? 1 : 0).Select(c => new ActionRequest(c.Entry.Definition.Key,
            c.Entry.DesiredValue is null && c.Entry.Definition.AllowedValues is { Count: > 0 } allowed && allowed.Contains(c.Instance.Desired.Value, StringComparer.Ordinal)
                ? c.Instance.Desired.Value : null)).ToList();
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
