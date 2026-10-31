using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nexus.Broker;

/// <summary>
/// Servidor de named pipe: uma conexão por vez, quadros com tamanho máximo, JSON estrito. Mensagem malformada recebe
/// resposta de erro e a conexão é encerrada. A criação do pipe (com ACL no Windows) é injetada pelo host.
/// </summary>
public sealed class BrokerPipeServer
{
    public const string DefaultPipeName = "NexusBroker.v1";
    public const int MaxInstances = 4;
    /// <summary>Prazo para um quadro completo chegar depois que a conexão está aberta (cliente parado não monopoliza).</summary>
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(15);

    private readonly BrokerService _service;
    private readonly Func<bool, NamedPipeServerStream> _createPipe;

    /// <param name="createPipe">Cria uma instância do pipe; o argumento indica se é a primeira (deve falhar se o nome já
    /// existir, para não aceitar um pipe criado antes por outro processo).</param>
    public BrokerPipeServer(BrokerService service, Func<bool, NamedPipeServerStream> createPipe)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _createPipe = createPipe ?? throw new ArgumentNullException(nameof(createPipe));
    }

    /// <summary>Pipe para testes e plataformas sem ACL: só o usuário atual.</summary>
    public static NamedPipeServerStream CurrentUserPipe(string name, bool first) =>
        new(name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first && OperatingSystem.IsWindows() ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    public Action<string>? Log { get; set; }

    /// <summary>
    /// Identifica o cliente pelo próprio pipe (no Windows: SID do token do cliente via impersonação). Chamado depois do
    /// primeiro quadro, porque o Windows só permite impersonar depois de ler dados do pipe. Null = não identificado.
    /// </summary>
    public Func<NamedPipeServerStream, string?>? IdentifyClient { get; init; }

    /// <summary>
    /// Mantém SEMPRE uma instância escutando: a próxima é criada antes de atender a conexão atual, então o nome nunca fica
    /// livre para outro processo ocupar. Erros de uma conexão não derrubam o servidor.
    /// </summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        var connections = new List<Task>();
        NamedPipeServerStream? listening = null;
        var first = true;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                if (listening is null)
                {
                    try
                    {
                        listening = _createPipe(first);
                        first = false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (first) throw; // nome já ocupado na partida: recusar subir em vez de servir ao lado de um impostor
                        Log?.Invoke($"não foi possível criar instância do pipe: {ex.Message}; nova tentativa em 1 s");
                        try { await Task.Delay(1000, cancel).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                }

                try
                {
                    await listening.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    Log?.Invoke($"conexão falhou: {ex.Message}");
                    await listening.DisposeAsync().ConfigureAwait(false);
                    listening = null;
                    continue;
                }

                var connected = listening;
                listening = null;
                try { listening = _createPipe(false); } // próxima instância antes de atender
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log?.Invoke($"instância seguinte adiada: {ex.Message}"); }

                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(ServeAsync(connected, cancel));
            }
        }
        finally
        {
            if (listening is not null) await listening.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancel)
    {
        await using var _ = pipe;
        string? callerSid = null;
        var identified = false;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                byte[]? frame;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    timeout.CancelAfter(FrameTimeout);
                    try
                    {
                        frame = await BrokerWire.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Log?.Invoke("conexão encerrada: cliente inativo ou servidor parando");
                        return;
                    }
                }
                if (frame is null) return;

                if (!identified)
                {
                    identified = true;
                    try { callerSid = IdentifyClient?.Invoke(pipe); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { Log?.Invoke($"cliente não identificado: {ex.GetType().Name}: {ex.Message}"); }
                }

                BrokerResponse response;
                BrokerRequest? request = null;
                try
                {
                    request = BrokerWire.Decode<BrokerRequest>(frame);
                    response = _service.Handle(request, callerSid);
                }
                catch (BrokerProtocolException ex)
                {
                    Log?.Invoke($"pedido recusado: {ex.Message}");
                    await TryWriteAsync(pipe, BrokerResponse.Fail(Guid.Empty, "protocol-error", BrokerService.Clip(ex.Message)!), cancel).ConfigureAwait(false);
                    return; // conteúdo inválido: encerra a conexão
                }

                Log?.Invoke($"{request.Op} {request.Id} -> {(response.Ok ? "ok" : response.Error?.Code)}");
                if (!await TryWriteAsync(pipe, response, cancel).ConfigureAwait(false)) return;
            }
        }
        catch (BrokerProtocolException ex)
        {
            Log?.Invoke($"quadro recusado: {ex.Message}");
            await TryWriteAsync(pipe, BrokerResponse.Fail(Guid.Empty, "protocol-error", BrokerService.Clip(ex.Message)!), cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Qualquer falha fica restrita a esta conexão: o servidor continua atendendo.
            Log?.Invoke($"erro na conexão: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<bool> TryWriteAsync(Stream pipe, BrokerResponse response, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(FrameTimeout);
            await BrokerWire.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (BrokerProtocolException)
        {
            // Resposta grande demais (não deveria acontecer: textos e listas são limitados). Envia um erro curto.
            try { await BrokerWire.WriteAsync(pipe, BrokerResponse.Fail(response.Id, "response-too-large", "Resposta excedeu o limite."), cancel).ConfigureAwait(false); return true; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return false; }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }
}

/// <summary>Quem o cliente aceita como servidor do pipe (achado B3).</summary>
public enum BrokerServerKind
{
    /// <summary>Broker de console do próprio usuário (desenvolvimento e testes): o dono do pipe deve ser o usuário atual.</summary>
    SameUser,
    /// <summary>Broker instalado como serviço: o dono do pipe deve ser SYSTEM ou Administradores. Um pipe com o mesmo nome criado
    /// por um usuário comum (impostor) é recusado antes de qualquer pedido.</summary>
    Service,
}

public sealed class BrokerClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private BrokerClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static BrokerClient Connect(string pipeName = BrokerPipeServer.DefaultPipeName, int timeoutMs = 5000, BrokerServerKind server = BrokerServerKind.SameUser)
    {
        // SameUser: CurrentUserOnly já faz o .NET conferir que o dono do pipe é o usuário atual.
        // Service: o dono é SYSTEM/Administradores, então a conferência é feita aqui. Identification permite ao serviço saber
        // QUEM pediu (SID para o HKCU, B10) sem poder agir em nome do cliente.
        var pipe = server == BrokerServerKind.SameUser
            ? new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly)
            : new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeoutMs);
            if (server == BrokerServerKind.Service)
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Broker como serviço só existe no Windows.");
                if (ServiceOwnerProblem(pipe) is { } problem)
                    throw new UnauthorizedAccessException(problem);
            }
            return new BrokerClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>Null se o dono do pipe é SYSTEM ou Administradores (só contas privilegiadas podem criar um pipe com esse dono).</summary>
    [SupportedOSPlatform("windows")]
    internal static string? ServiceOwnerProblem(PipeStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return IsServiceOwner(owner)
            ? null
            : $"O pipe do broker pertence a {owner?.Value ?? "dono desconhecido"}, não a SYSTEM/Administradores: possível impostor. Nada foi enviado.";
    }

    [SupportedOSPlatform("windows")]
    public static bool IsServiceOwner(SecurityIdentifier? owner) =>
        owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    public BrokerResponse Send(BrokerRequest request)
    {
        BrokerWire.Write(_pipe, request);
        var frame = BrokerWire.ReadFrame(_pipe) ?? throw new BrokerProtocolException("Broker fechou a conexão.");
        return BrokerWire.Decode<BrokerResponse>(frame);
    }

    /// <summary>Envia bytes crus (para testes de robustez do servidor).</summary>
    public BrokerResponse? SendRaw(byte[] bytes)
    {
        _pipe.Write(bytes);
        _pipe.Flush();
        var frame = BrokerWire.ReadFrame(_pipe);
        return frame is null ? null : BrokerWire.Decode<BrokerResponse>(frame);
    }

    public void Dispose() => _pipe.Dispose();
}
