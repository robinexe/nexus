using System.Reflection;
using Nexus.Core;

namespace Nexus.Broker;

/// <summary>
/// Processa pedidos do broker. O cliente nunca envia alvos: só ids do catálogo que o PRÓPRIO broker carregou.
/// O broker roda o motor inteiro (plano, diário, trava, verificação); o cliente não orquestra passos.
/// <list type="bullet">
/// <item>Leitura (ReadState, Plan, Status): sempre permitida.</item>
/// <item>Escrita (Execute, EndSession, RevertRun, Recover): exige <see cref="IExecutionGate"/> liberado e a trava de execução.</item>
/// <item>Execute recusa enquanto houver operações interrompidas no diário: a recuperação vem antes.</item>
/// <item>Ajustes de sessão: o broker segura a trava até EndSession, que reverte os ajustes da sessão.</item>
/// <item>Com <see cref="BindCallerProfile"/> (serviço como LocalSystem): toda ação de HKCU recebe no alvo o SID de quem pediu
/// (<see cref="UserProfileScope"/>); sem SID válido do cliente, ações de HKCU são recusadas.</item>
/// </list>
/// </summary>
public sealed class BrokerService : IDisposable
{
    public const int MaxActionsPerRequest = 32;
    public const int MaxProfileLength = 128;
    /// <summary>Limites de resposta: textos e listas nunca podem fazer o quadro passar de <see cref="BrokerWire.MaxFrameBytes"/>.</summary>
    public const int MaxTextLength = 1024;
    public const int MaxResults = 40;

    private readonly CatalogDocument _catalog;
    private readonly HandlerRegistry _handlers;
    private readonly IJournal _journal;
    private readonly IRunCoordinator _coordinator;
    private readonly IExecutionGate _gate;
    private readonly Func<SystemContext> _context;
    private readonly Func<IReadOnlyDictionary<string, string>> _parameters;
    private readonly Lock _gateLock = new();
    private readonly bool _bindCallerProfile;

    /// <summary>
    /// Chamado antes de executar um lote com ação persistente de administrador (mudança de sistema). No Windows, o host cria um
    /// ponto de restauração. Retorna uma frase para a resposta (ex.: "Ponto de restauração criado."), ou null.
    /// </summary>
    public Func<string?>? BeforeSystemChange { get; init; }

    /// <summary>Há ajustes de sessão aplicados (o broker não deve encerrar enquanto isso for verdade).</summary>
    public bool HasActiveSession { get { lock (_gateLock) return _sessionLease is not null; } }
    private IRunLease? _sessionLease;
    private Guid? _sessionRun;

    /// <param name="bindCallerProfile">true quando o broker roda como serviço (LocalSystem): HKCU passa a ser o perfil de quem
    /// pediu, identificado pelo transporte (achado B10).</param>
    public BrokerService(CatalogDocument catalog, HandlerRegistry handlers, IJournal journal, IRunCoordinator coordinator,
        IExecutionGate gate, Func<SystemContext> context, Func<IReadOnlyDictionary<string, string>> parameters, bool bindCallerProfile = false)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(handlers);
        _bindCallerProfile = bindCallerProfile;
        // Toda restauração passa a exigir que a entrada do diário corresponda ao catálogo do broker.
        _handlers = CatalogBoundHandler.Wrap(handlers, catalog?.Actions.Select(a => a.Definition.HandlerKind) ?? [], catalog!, parameters!, bindCallerProfile);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public static string Version => typeof(BrokerService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    public bool BindCallerProfile => _bindCallerProfile;

    /// <summary>Processa um pedido. Nunca lança: erros viram resposta com código.</summary>
    /// <param name="callerSid">SID de quem pediu, obtido pelo transporte (nunca pelo conteúdo do pedido).</param>
    public BrokerResponse Handle(BrokerRequest request, string? callerSid = null)
    {
        if (request is null) return BrokerResponse.Fail(Guid.Empty, "invalid-request", "Pedido vazio.");
        lock (_gateLock) // um pedido por vez: o broker é um único executor
        {
            try
            {
                if (request.V != BrokerWire.ProtocolVersion)
                    return BrokerResponse.Fail(request.Id, "unsupported-version", $"Versão {request.V}; este broker fala {BrokerWire.ProtocolVersion}.");
                if (request.Profile is { Length: > MaxProfileLength })
                    return BrokerResponse.Fail(request.Id, "invalid-request", "Perfil longo demais.");
                return request.Op switch
                {
                    BrokerOperation.Ping => new BrokerResponse(request.Id, true, Message: "pong"),
                    BrokerOperation.Status => new BrokerResponse(request.Id, true, Status: Status()),
                    BrokerOperation.ReadState => ReadState(request, callerSid),
                    BrokerOperation.Plan => PlanOnly(request, callerSid),
                    BrokerOperation.Execute => Execute(request, callerSid),
                    BrokerOperation.EndSession => EndSession(request),
                    BrokerOperation.RevertRun => RevertRun(request),
                    BrokerOperation.Recover => Recover(request),
                    _ => BrokerResponse.Fail(request.Id, "unknown-operation", request.Op.ToString()),
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return BrokerResponse.Fail(request.Id, "internal-error", Clip($"{ex.GetType().Name}: {ex.Message}")!);
            }
        }
    }

    private BrokerStatus Status()
    {
        var gate = _gate.Evaluate();
        var probe = _coordinator.Probe();
        string Clip(string? t) => BrokerService.Clip(t) ?? "";
        var coordination = probe.State switch
        {
            CoordinationState.Active when _sessionLease is not null => "sessão do broker ativa",
            CoordinationState.Active => "outro executor ativo: " + (probe.Holder?.Describe() ?? "dono não identificado"),
            CoordinationState.Idle => "livre",
            _ => "indeterminado: " + probe.Detail,
        };
        return new BrokerStatus(Version, _catalog.CatalogVersion, gate.Allowed, Clip(gate.Reason), Clip(coordination), _sessionRun);
    }

    // ------------------------------------------------------------------ validação de pedidos

    /// <summary>Converte pedidos em ações concretas usando SÓ o catálogo do broker. Erro = nenhuma ação.</summary>
    private (List<ActionInstance>? Actions, BrokerResponse? Error) Resolve(BrokerRequest r, string? callerSid)
    {
        if (r.Actions is null || r.Actions.Count == 0)
            return (null, BrokerResponse.Fail(r.Id, "invalid-request", "Nenhuma ação pedida."));
        if (r.Actions.Count > MaxActionsPerRequest)
            return (null, BrokerResponse.Fail(r.Id, "invalid-request", $"Máximo de {MaxActionsPerRequest} ações por pedido."));
        if (r.Actions.Any(a => a is null || string.IsNullOrWhiteSpace(a.ActionId) || a.ActionId.Length > 128 || a.Value is { Length: > 64 } ||
                               a.Arguments is { Count: > CatalogEntry.MaxUserArguments } ||
                               (a.Arguments?.Any(kv => kv.Key is null || kv.Key.Length > 64 || kv.Value is null || kv.Value.Length > CatalogEntry.MaxUserArgumentLength) ?? false)))
            return (null, BrokerResponse.Fail(r.Id, "invalid-request", "Ação nula, vazia ou com campos longos demais."));
        if (r.Actions.Select(a => a.ActionId).Distinct(StringComparer.Ordinal).Count() != r.Actions.Count)
            return (null, BrokerResponse.Fail(r.Id, "invalid-request", "Ação repetida no pedido."));

        var parameters = _parameters();
        var list = new List<ActionInstance>();
        foreach (var a in r.Actions)
        {
            var entry = _catalog.Actions.FirstOrDefault(e => string.Equals(e.Definition.Key, a.ActionId, StringComparison.Ordinal));
            if (entry is null)
                return (null, BrokerResponse.Fail(r.Id, "unknown-action", $"'{Clip(a.ActionId, 128)}' não existe no catálogo do broker ({_catalog.CatalogVersion})."));

            var chosen = entry;
            if (entry.DesiredValue is not null)
            {
                if (a.Value is not null && a.Value != entry.DesiredValue)
                    return (null, BrokerResponse.Fail(r.Id, "invalid-value", $"'{a.ActionId}' tem valor fixo no catálogo ({entry.DesiredValue})."));
            }
            else if (entry.Definition.AllowedValues is { Count: > 0 } allowed)
            {
                if (a.Value is null || !allowed.Contains(a.Value, StringComparer.Ordinal))
                    return (null, BrokerResponse.Fail(r.Id, "invalid-value", $"'{a.ActionId}' exige um valor entre: {string.Join(", ", allowed)}."));
                chosen = entry with { DesiredValue = a.Value };
            }
            else if (a.Value is not null)
            {
                return (null, BrokerResponse.Fail(r.Id, "invalid-value", $"'{a.ActionId}' não aceita valor."));
            }

            var effective = parameters;
            if (chosen.UserParameters.Count > 0 || a.Arguments is { Count: > 0 })
            {
                var args = chosen.ValidateUserArguments(a.Arguments, out var argError);
                if (args is null)
                    return (null, BrokerResponse.Fail(r.Id, "invalid-arguments", $"'{Clip(a.ActionId, 128)}': {argError}"));
                var merged = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
                foreach (var (k, v) in args) merged[k] = v;
                effective = merged;
            }
            var instance = chosen.TryInstantiate(effective, out var reason);
            if (instance is null)
                return (null, BrokerResponse.Fail(r.Id, "not-instantiable", $"'{a.ActionId}': {reason}"));
            if (!_handlers.Contains(instance.Definition.HandlerKind))
                return (null, BrokerResponse.Fail(r.Id, "handler-unavailable", $"'{a.ActionId}': handler '{instance.Definition.HandlerKind}' pendente."));
            if (_bindCallerProfile && UserProfileScope.IsUserScoped(instance))
            {
                // Como serviço, o HKCU do processo é o do SYSTEM: a ação vai para o perfil de quem pediu, ou não vai.
                if (!UserProfileScope.IsValidSid(callerSid))
                    return (null, BrokerResponse.Fail(r.Id, "caller-unknown", $"'{a.ActionId}' altera o perfil do usuário, e o cliente não foi identificado como conta de usuário."));
                instance = UserProfileScope.Bind(instance, callerSid!);
            }
            list.Add(instance);
        }
        return (list, null);
    }

    // ------------------------------------------------------------------ leitura

    private BrokerResponse ReadState(BrokerRequest r, string? callerSid)
    {
        var (actions, error) = Resolve(r, callerSid);
        if (error is not null) return error;
        var results = actions!.Select(a =>
        {
            try
            {
                var s = _handlers.Get(a.Definition.HandlerKind).Detect(a);
                return new ActionResultDto(a.Definition.Key, "Read", s.Origin.ToString(), s.Describe(), a.Desired.Describe());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new ActionResultDto(a.Definition.Key, "ReadFailed", Clip($"{ex.GetType().Name}: {ex.Message}"));
            }
        }).ToList();
        return new BrokerResponse(r.Id, true, Results: results);
    }

    private BrokerResponse PlanOnly(BrokerRequest r, string? callerSid)
    {
        var (actions, error) = Resolve(r, callerSid);
        if (error is not null) return error;
        var plan = new DesiredStateResolver(_handlers).Resolve(actions!, _context());
        return new BrokerResponse(r.Id, true, Results: plan.Items.Select(i => new ActionResultDto(i.Action.Definition.Key, i.Status.ToString(), i.Reason,
            i.Status is PlanStatus.Pending or PlanStatus.AlreadyConfigured ? i.ObservedAtPlan.Describe() : null, i.Action.Desired.Describe())).ToList(),
            Message: "Simulação: nada foi aplicado." + (actions!.Any(a => a.Definition.Irreversible)
                ? " Atenção: há ação IRREVERSÍVEL no pedido; aplicar exige confirmação explícita e não poderá ser desfeito."
                : ""));
    }

    // ------------------------------------------------------------------ escrita (portão + trava)

    private BrokerResponse? GateRefusal(BrokerRequest r)
    {
        var gate = _gate.Evaluate();
        return gate.Allowed ? null : BrokerResponse.Fail(r.Id, "execution-blocked", Clip(gate.Reason)!);
    }

    private (IRunLease? Lease, BrokerResponse? Error) AcquireLease(BrokerRequest r, string purpose)
    {
        var acquired = _coordinator.TryAcquire(purpose, TimeSpan.FromSeconds(2));
        if (acquired.Acquired) return (acquired.Lease, null);
        return (null, acquired.Status == LeaseAcquireStatus.HeldByOther
            ? BrokerResponse.Fail(r.Id, "busy", Clip("Outro executor ativo: " + (acquired.Holder?.Describe() ?? "dono não identificado"))!)
            : BrokerResponse.Fail(r.Id, "coordination-unavailable", Clip($"{acquired.Status}: {acquired.Detail}")!));
    }

    private BrokerResponse Execute(BrokerRequest r, string? callerSid)
    {
        if (GateRefusal(r) is { } blocked) return blocked; // antes de qualquer coisa: nada lido, nada travado, nada gravado
        if (_sessionLease is not null)
            return BrokerResponse.Fail(r.Id, "session-active", $"Encerre a sessão {_sessionRun} antes de outra execução.");
        var (actions, error) = Resolve(r, callerSid);
        if (error is not null) return error;
        // Ações irreversíveis: só com confirmação explícita, ação por ação. Recusa o pedido inteiro antes de travar ou gravar.
        var confirmedIrreversible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (instance, request) in actions!.Zip(r.Actions!))
        {
            if (!instance.Definition.Irreversible) continue;
            if (request.ConfirmIrreversible != true)
                return BrokerResponse.Fail(r.Id, "irreversible-unconfirmed",
                    $"'{Clip(request.ActionId, 128)}' é irreversível e o pedido não trouxe a confirmação explícita do usuário (confirmIrreversible). Nada foi feito.");
            confirmedIrreversible.Add(instance.Definition.Key);
        }

        var (lease, leaseError) = AcquireLease(r, "broker-execute");
        if (leaseError is not null) return leaseError;
        var keepLease = false;
        try
        {
            // Só operações realmente interrompidas (estado do sistema incerto) bloqueiam. Conflitos e entradas superadas são
            // decisões pendentes do usuário: não impedem novas execuções (a recuperação trata entradas superadas).
            var interrupted = RecoveryService.FindInterrupted(_journal)
                .Where(f => f.Entry.State is JournalState.BackedUp or JournalState.Applying or JournalState.Applied or JournalState.Restoring or JournalState.Verified)
                .ToList();
            if (interrupted.Count > 0)
                return BrokerResponse.Fail(r.Id, "recovery-required",
                    $"{interrupted.Count} operação(ões) interrompida(s) no diário. Execute Recover antes de aplicar.");

            var exec = new ActionExecutor(_handlers, _journal, lease!);
            var ctx = _context();
            var plan = new DesiredStateResolver(_handlers).Resolve(actions!, ctx); // antes de abrir a execução: falha aqui não deixa execução órfã
            string? safetyNote = null;
            if (BeforeSystemChange is not null && plan.Items.Any(i => i.Status == PlanStatus.Pending &&
                    i.Action.Definition.Privileges == PrivilegeLevel.Admin && i.Action.Definition.Persistence == PersistenceKind.Persistent))
            {
                try { safetyNote = BeforeSystemChange(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { safetyNote = $"Ponto de restauração não criado ({ex.Message}); o diário continua permitindo desfazer."; }
            }
            var runId = exec.BeginRun(string.IsNullOrWhiteSpace(r.Profile) ? "broker" : r.Profile, _catalog.CatalogVersion);
            var results = exec.Execute(runId, plan, ctx, confirmedIrreversible);

            var sessionApplied = _journal.Entries(runId).Any(e => e.State == JournalState.Verified &&
                                                                  e.Action.Definition.Persistence == PersistenceKind.Session);
            if (sessionApplied)
            {
                _sessionLease = lease;
                _sessionRun = runId;
                keepLease = true;
            }
            else
            {
                exec.EndRun(runId);
            }
            return new BrokerResponse(r.Id, true, Results: Cap(results.Select(x => new ActionResultDto(x.ActionKey, x.Outcome.ToString(), Clip(x.Detail)))),
                RunId: runId, SessionActive: sessionApplied,
                Message: (sessionApplied ? "Ajustes de sessão aplicados; o broker mantém a trava até EndSession." : "Execução encerrada.") +
                         (safetyNote is null ? "" : " " + safetyNote));
        }
        finally
        {
            if (!keepLease) lease!.Dispose();
        }
    }

    private BrokerResponse EndSession(BrokerRequest r)
    {
        if (_sessionLease is null || r.RunId is null || r.RunId != _sessionRun)
            return BrokerResponse.Fail(r.Id, "no-such-session", "Não há sessão ativa com esse runId.");
        if (GateRefusal(r) is { } blocked) return blocked;

        var exec = new ActionExecutor(_handlers, _journal, _sessionLease);
        var runId = _sessionRun.Value;
        var sessionEntries = _journal.Entries(runId)
            .Where(e => e.Action.Definition.Persistence == PersistenceKind.Session)
            .OrderByDescending(e => e.Sequence)
            .Select(e => exec.Revert(e.Id, r.Force))
            .ToList();
        var ok = sessionEntries.All(x => x.Outcome is RevertOutcome.Reverted or RevertOutcome.AlreadyOriginal or RevertOutcome.NothingToRevert);
        if (ok)
        {
            exec.EndRun(runId);
            _sessionLease.Dispose();
            _sessionLease = null;
            _sessionRun = null;
        }
        // Se algo ficou pendente, a trava continua com o broker: a sessão não é declarada encerrada.
        return new BrokerResponse(r.Id, ok, ok ? null : new BrokerError("session-revert-incomplete", "Alguns ajustes de sessão não foram revertidos; a sessão continua aberta."),
            Results: Cap(sessionEntries.Select(Map)), RunId: runId, SessionActive: !ok, FullyRestored: ok);
    }

    private BrokerResponse RevertRun(BrokerRequest r)
    {
        if (r.RunId is null) return BrokerResponse.Fail(r.Id, "invalid-request", "runId obrigatório.");
        if (GateRefusal(r) is { } blocked) return blocked;
        if (_sessionLease is not null && r.RunId != _sessionRun)
            return BrokerResponse.Fail(r.Id, "session-active", $"Encerre a sessão {_sessionRun} antes.");

        var ownSession = _sessionLease is not null;
        var lease = _sessionLease;
        if (!ownSession)
        {
            var (l, err) = AcquireLease(r, "broker-revert");
            if (err is not null) return err;
            lease = l;
        }
        try
        {
            var exec = new ActionExecutor(_handlers, _journal, lease!);
            var results = exec.RevertRun(r.RunId.Value, r.Force);
            var ok = results.All(x => x.Outcome is RevertOutcome.Reverted or RevertOutcome.AlreadyOriginal or RevertOutcome.NothingToRevert);
            if (ok && ownSession)
            {
                exec.EndRun(r.RunId.Value);
                _sessionLease = null;
                _sessionRun = null;
                lease!.Dispose();
                lease = null;
            }
            return new BrokerResponse(r.Id, true, Results: Cap(results.Select(Map)), RunId: r.RunId, FullyRestored: ok);
        }
        finally
        {
            if (!ownSession) lease?.Dispose();
        }
    }

    private BrokerResponse Recover(BrokerRequest r)
    {
        if (GateRefusal(r) is { } blocked) return blocked;
        if (_sessionLease is not null)
            return BrokerResponse.Fail(r.Id, "session-active", $"Encerre a sessão {_sessionRun} antes de recuperar.");
        var (lease, err) = AcquireLease(r, "broker-recover");
        if (err is not null) return err;
        using (lease)
        {
            var report = new RecoveryService(_journal, new ActionExecutor(_handlers, _journal, lease!)).Recover(r.Force);
            var capped = Cap(report.Results.Select(Map));
            return new BrokerResponse(r.Id, true, Results: capped, FullyRestored: report.FullyRestored,
                Message: (report.FullyRestored ? "Recuperação completa." : "Recuperação parcial: veja os itens.") +
                         (report.Results.Count > capped.Count ? $" Mostrando {capped.Count} de {report.Results.Count} itens; o diário tem todos." : ""));
        }
    }

    private static ActionResultDto Map(RevertResult x) => new(x.ActionKey, x.Outcome.ToString(), Clip(x.Detail), Inferred: x.Inferred);

    internal static string? Clip(string? text, int max = MaxTextLength) =>
        text is null || text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "… [truncado]");

    private static List<ActionResultDto> Cap(IEnumerable<ActionResultDto> items) => items.Take(MaxResults).ToList();

    public void Dispose()
    {
        lock (_gateLock)
        {
            // Ao encerrar o broker com sessão aberta, a trava é liberada e a sessão vira "abandonada": a próxima
            // recuperação a reverte. Isso é o mesmo que acontece se o processo morrer.
            _sessionLease?.Dispose();
            _sessionLease = null;
        }
    }
}
