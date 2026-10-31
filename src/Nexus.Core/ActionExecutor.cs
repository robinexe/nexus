namespace Nexus.Core;

public enum ExecutionOutcome
{
    Verified,
    AlreadyConfigured,
    SkippedExternalChange,
    NotApplicable,
    Blocked,
    Failed,
    FailedAndCompensated,
    /// <summary>
    /// Erro inesperado (ex.: o diário parou de gravar) com o alvo em estado indeterminado. A execução inteira foi
    /// interrompida; a recuperação encontra a entrada pelo diário (caso A5).
    /// </summary>
    Interrupted,
}

public sealed record ExecutionResult(string ActionKey, ExecutionOutcome Outcome, long? EntryId, string? Detail);

/// <summary>Resultado de desfazer uma entrada.</summary>
/// <remarks><see cref="Irreversible"/>: a ação foi declarada irreversível no catálogo (<see cref="ActionDefinition.Irreversible"/>);
/// nada foi escrito e não há como desfazer. Não conta como "restaurado".</remarks>
public enum RevertOutcome { Reverted, AlreadyOriginal, Conflict, NothingToRevert, Failed, Irreversible }

/// <param name="Inferred">A decisão de restaurar se apoiou numa inferência que a releitura não prova
/// (interrompido durante o Apply com o valor atual igual ao desejado). Ver docs/07-recovery-cases.md, caso A1.</param>
public sealed record RevertResult(long EntryId, string ActionKey, RevertOutcome Outcome, string Detail, bool Inferred = false);

/// <summary>
/// Executa um plano: reavalia pré-condições e o estado imediatamente antes de aplicar,
/// grava backup no diário ANTES de escrever no sistema, relê após aplicar e só então marca Verified.
/// Em falha de verificação, tenta ação compensatória (restaurar o original) e registra o resultado real.
/// Toda escrita no sistema exige a trava de execução (<see cref="IRunLease"/>) e é precedida de um estado no diário:
/// Applying antes de aplicar, Restoring antes de restaurar.
/// </summary>
public sealed class ActionExecutor
{
    private readonly HandlerRegistry _handlers;
    private readonly IJournal _journal;
    private readonly IRunLease? _lease;

    /// <summary>Ponto de injeção de falhas para os testes de recuperação (seção 14). Nulo em produção.</summary>
    public Action<string, ActionInstance>? FaultInjector { get; set; }

    /// <summary>Executor de produção: só existe com a trava de execução em mãos.</summary>
    public ActionExecutor(HandlerRegistry handlers, IJournal journal, IRunLease lease)
        : this(handlers, journal)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureHeld();
        _lease = lease;
    }

    /// <summary>Sem trava: apenas para testes do motor com diário e backends em memória.</summary>
    internal ActionExecutor(HandlerRegistry handlers, IJournal journal)
    {
        _handlers = handlers;
        _journal = journal;
    }

    public IRunLease? Lease => _lease;

    /// <summary>Abre uma execução no diário e a vincula à trava (a recuperação nunca toca a execução vinculada).</summary>
    public Guid BeginRun(string profile, string catalogVersion)
    {
        _lease?.EnsureHeld();
        var id = _journal.BeginRun(profile, catalogVersion);
        _lease?.BindRun(id);
        return id;
    }

    /// <summary>Encerra a execução: ajustes de sessão deixam de ser considerados interrompidos.</summary>
    public void EndRun(Guid runId)
    {
        _lease?.EnsureHeld();
        _journal.EndRun(runId);
        _lease?.UnbindRun(runId);
    }

    /// <summary>Executa sem confirmar nenhuma ação irreversível: toda ação irreversível pendente sai como Blocked.</summary>
    public IReadOnlyList<ExecutionResult> Execute(Guid runId, Plan plan, SystemContext context) => Execute(runId, plan, context, []);

    /// <param name="confirmedIrreversible">Chaves (id@versão) de ações irreversíveis que o usuário confirmou explicitamente para
    /// ESTA execução. Uma ação irreversível fora da lista não é aplicada (Blocked), nem chega ao diário.</param>
    public IReadOnlyList<ExecutionResult> Execute(Guid runId, Plan plan, SystemContext context, IReadOnlyCollection<string> confirmedIrreversible)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmedIrreversible);
        if (_lease is not null)
        {
            _lease.EnsureHeld();
            if (_lease.ActiveRunId != runId)
                throw new InvalidOperationException($"A execução {runId} não está vinculada à trava deste executor. Use BeginRun.");
        }

        var results = new List<ExecutionResult>();
        foreach (var item in plan.Items.Where(i => i.Status != PlanStatus.Pending))
        {
            var key = item.Action.Definition.Key;
            results.Add(item.Status switch
            {
                PlanStatus.AlreadyConfigured => new(key, ExecutionOutcome.AlreadyConfigured, null, item.ObservedAtPlan.Describe()),
                PlanStatus.NotApplicable => new(key, ExecutionOutcome.NotApplicable, null, item.Reason),
                _ => new(key, ExecutionOutcome.Blocked, null, item.Reason),
            });
        }

        // A sequência continua a partir da última entrada da execução: uma execução pode ter vários Execute
        // (ex.: lote pós-reinício) e o diário exige (run, seq) único. RevertRun depende dessa ordem.
        var seq = _journal.Entries(runId).Select(e => e.Sequence).DefaultIfEmpty(-1).Max() + 1;

        // Ordem: lote (reinício por último) e, dentro do lote, ordem topológica do plano.
        var notSucceeded = new HashSet<string>(StringComparer.Ordinal);
        string? interruptedBy = null;
        foreach (var item in plan.Pending)
        {
            var key = item.Action.Definition.Key;
            if (interruptedBy is not null)
            {
                results.Add(new(key, ExecutionOutcome.Blocked, null, $"Execução interrompida após erro inesperado em {interruptedBy}; nada mais foi aplicado."));
                continue;
            }

            var failedDeps = (item.Action.Definition.DependsOn ?? []).Where(notSucceeded.Contains).ToList();
            if (failedDeps.Count > 0)
            {
                notSucceeded.Add(item.Action.Definition.Id);
                results.Add(new(key, ExecutionOutcome.Blocked, null, $"Dependência não concluída nesta execução: {string.Join(", ", failedDeps)}"));
                continue;
            }

            ExecutionResult r;
            try
            {
                if (item.Action.Definition.Irreversible && !confirmedIrreversible.Contains(key, StringComparer.Ordinal))
                    r = new(key, ExecutionOutcome.Blocked, null, IrreversibleNotConfirmed);
                else
                    r = ExecuteOne(runId, seq++, item, context);
            }
            // Com FaultInjector ativo (testes de recuperação), a exceção simula a morte do processo e precisa propagar.
            catch (Exception ex) when (ex is not OperationCanceledException && FaultInjector is null)
            {
                // Erro inesperado (ex.: diário sem espaço). O alvo pode ter sido escrito e o diário ficou onde parou; a
                // recuperação o encontrará. Continuar aplicando com o diário em falha não é seguro: interrompe tudo (A5).
                r = new(key, ExecutionOutcome.Interrupted, null, $"Erro inesperado: {ex.Message}. Estado do alvo indeterminado; execute a recuperação.");
                interruptedBy = key;
            }
            results.Add(r);
            if (r.Outcome != ExecutionOutcome.Verified) notSucceeded.Add(item.Action.Definition.Id);
        }
        return results;
    }

    public const string IrreversibleNotConfirmed =
        "Ação irreversível: exige confirmação explícita do usuário para esta execução. Nada foi feito.";

    private ExecutionResult ExecuteOne(Guid runId, int seq, PlanItem item, SystemContext context)
    {
        var action = item.Action;
        var key = action.Definition.Key;
        var handler = _handlers.Get(action.Definition.HandlerKind);

        // Pré-condições verificadas novamente imediatamente antes da aplicação.
        var common = CommonAssessment.Check(action, context);
        var assess = common.Kind == ApplicabilityKind.Applicable ? handler.Assess(action, context) : common;
        if (assess.Kind != ApplicabilityKind.Applicable)
            return new(key, assess.Kind == ApplicabilityKind.Blocked ? ExecutionOutcome.Blocked : ExecutionOutcome.NotApplicable, null, assess.Reason);

        var entry = _journal.AddPlanned(runId, seq, action);

        StateSnapshot original;
        try
        {
            original = handler.Detect(action);
        }
        catch (Exception ex)
        {
            _journal.Transition(entry, JournalState.Failed, note: $"Falha ao ler estado: {ex.Message}");
            return new(key, ExecutionOutcome.Failed, entry, ex.Message);
        }

        // Mudança externa entre planejamento e execução: não aplicar sobre um estado que o usuário não revisou.
        if (!original.SameValueAs(item.ObservedAtPlan))
        {
            _journal.Transition(entry, JournalState.Failed, observed: original,
                note: $"Estado mudou após o planejamento ({item.ObservedAtPlan.Describe()} → {original.Describe()}).");
            return new(key, ExecutionOutcome.SkippedExternalChange, entry, original.Describe());
        }

        _journal.Transition(entry, JournalState.BackedUp, original: original);
        FaultInjector?.Invoke("after-backup", action);

        _lease?.EnsureHeld();
        _journal.Transition(entry, JournalState.Applying);
        try
        {
            handler.Apply(action);
        }
        catch (Exception ex)
        {
            return Compensate(entry, key, handler, action, original, $"Apply lançou exceção: {ex.Message}");
        }
        FaultInjector?.Invoke("after-apply", action);

        _journal.Transition(entry, JournalState.Applied);

        StateSnapshot after;
        try
        {
            after = handler.Detect(action);
        }
        catch (Exception ex)
        {
            return Compensate(entry, key, handler, action, original, $"Releitura falhou: {ex.Message}");
        }

        if (!after.SameValueAs(action.Desired))
            return Compensate(entry, key, handler, action, original,
                $"Verificação falhou: esperado {action.Desired.Describe()}, lido {after.Describe()}.", observedAfterApply: after);

        _journal.Transition(entry, JournalState.Verified, observed: after);
        return new(key, ExecutionOutcome.Verified, entry, after.Describe());
    }

    /// <param name="observedAfterApply">Valor relido logo após o Apply (produzido pela escrita do NEXUS). É gravado no diário
    /// com o estado Failed para que uma recuperação posterior também saiba que foi o NEXUS que o produziu (caso A2).
    /// Só esse valor, o desejado ou o original autorizam escrever; qualquer outro é tratado como alteração externa.</param>
    private ExecutionResult Compensate(long entry, string key, IActionHandler handler, ActionInstance action, StateSnapshot original, string why,
        StateSnapshot? observedAfterApply = null)
    {
        _journal.Transition(entry, JournalState.Failed, observed: observedAfterApply, note: why);
        StateSnapshot current;
        try
        {
            current = handler.Detect(action);
        }
        catch (Exception ex)
        {
            return new(key, ExecutionOutcome.Failed, entry, why + $" Releitura para compensação falhou: {ex.Message}");
        }

        if (current.SameValueAs(original))
        {
            _journal.Transition(entry, JournalState.Reverted, observed: current, note: why + " Nada foi escrito: estado original intacto.");
            return new(key, ExecutionOutcome.FailedAndCompensated, entry, why);
        }
        if (action.Definition.Irreversible)
        {
            // Não há original para devolver: o alvo pode ter sido alterado em parte (ex.: arquivo já sobrescrito). Encerrar com o
            // registro honesto em vez de deixar uma "compensação pendente" que nenhuma recuperação conseguiria cumprir.
            _journal.Transition(entry, JournalState.Accepted, observed: current,
                note: why + $" Ação irreversível interrompida: estado atual {current.Describe()} mantido; não há como desfazer.");
            return new(key, ExecutionOutcome.Failed, entry, why + " Ação irreversível: o alvo pode ter sido alterado em parte e não há como desfazer.");
        }
        var producedByNexus = current.SameValueAs(action.Desired) || (observedAfterApply is not null && current.SameValueAs(observedAfterApply));
        if (!producedByNexus)
        {
            _journal.Transition(entry, JournalState.RevertConflict, observed: current,
                note: why + $" Estado atual {current.Describe()} não é o original nem o desejado; nada foi sobrescrito.");
            return new(key, ExecutionOutcome.Failed, entry, why + " Conflito: alteração externa preservada.");
        }

        var outcome = RestoreJournaled(entry, handler, action, original, current, why);
        return outcome.Outcome == RevertOutcome.Reverted
            ? new(key, ExecutionOutcome.FailedAndCompensated, entry, why)
            : new(key, ExecutionOutcome.Failed, entry, why + " " + outcome.Detail);
    }

    /// <summary>
    /// Grava Restoring (com o valor que está autorizado a sobrescrever) ANTES de escrever, restaura e relê.
    /// Se não confirmar, grava Restoring de novo com o valor que a tentativa deixou, produzido pelo NEXUS (caso A3).
    /// </summary>
    private RevertResult RestoreJournaled(long entry, IActionHandler handler, ActionInstance action, StateSnapshot original,
        StateSnapshot authorizedCurrent, string context, bool inferred = false, string? successNote = null)
    {
        var key = action.Definition.Key;
        // Alvo e original podem vir do diário (desserializados): revalida forma e política de segurança antes de escrever.
        if (handler is IRestoreValidator validator && validator.CheckRestore(action, original) is { } refused)
            return new(entry, key, RevertOutcome.Failed, $"Restauração recusada pela validação do alvo: {refused} Nada foi escrito.", inferred);
        _lease?.EnsureHeld();
        _journal.Transition(entry, JournalState.Restoring, observed: authorizedCurrent,
            note: $"{context} Restaurando {original.Describe()} sobre {authorizedCurrent.Describe()}.".Trim());
        FaultInjector?.Invoke("before-restore", action);

        StateSnapshot now;
        try
        {
            handler.Restore(action, original);
            now = handler.Detect(action);
        }
        catch (Exception ex)
        {
            return new(entry, key, RevertOutcome.Failed, $"Restauração lançou exceção: {ex.Message}. Entrada permanece em Restoring.", inferred);
        }

        if (now.SameValueAs(original))
        {
            _journal.Transition(entry, JournalState.Reverted, observed: now,
                note: successNote ?? $"{context} Estado original restaurado e verificado.".Trim());
            return new(entry, key, RevertOutcome.Reverted, now.Describe(), inferred);
        }

        _journal.Transition(entry, JournalState.Restoring, observed: now,
            note: $"Restauração não confirmada: lido {now.Describe()} (valor deixado pelo NEXUS).");
        return new(entry, key, RevertOutcome.Failed, $"Restauração não confirmada: lido {now.Describe()}.", inferred);
    }

    /// <summary>
    /// Reverte uma entrada. Nunca sobrescreve silenciosamente uma alteração posterior: só escreve se o estado atual
    /// for o desejado ou um valor que o diário prova ter sido produzido pelo NEXUS; caso contrário o resultado é
    /// Conflict, a menos que <paramref name="force"/> tenha sido confirmado pelo usuário.
    /// </summary>
    public RevertResult Revert(long entryId, bool force = false)
    {
        _lease?.EnsureHeld();
        var e = _journal.Get(entryId);
        var key = e.Action.Definition.Key;
        if (e.State is JournalState.Reverted or JournalState.Planned)
            return new(entryId, key, RevertOutcome.NothingToRevert, $"Estado {e.State}.");
        if (e.State == JournalState.Accepted)
            return new(entryId, key, RevertOutcome.NothingToRevert, "Estado Accepted: o usuário manteve o estado atual; o backup não é mais usado.");
        if (e.Original is null)
            return new(entryId, key, RevertOutcome.NothingToRevert, "Sem backup: nada foi escrito.");
        if (e.Action.Definition.Irreversible)
            return RefuseIrreversible(_journal, e);

        if (!_handlers.Contains(e.Action.Definition.HandlerKind))
            return new(entryId, key, RevertOutcome.Failed,
                $"Handler '{e.Action.Definition.HandlerKind}' indisponível neste ambiente; restauração não executada.");
        var handler = _handlers.Get(e.Action.Definition.HandlerKind);
        StateSnapshot current;
        try { current = handler.Detect(e.Action); }
        catch (Exception ex) { return new(entryId, key, RevertOutcome.Failed, $"Falha ao ler estado: {ex.Message}"); }

        if (current.SameValueAs(e.Original))
        {
            _journal.Transition(entryId, JournalState.Reverted, observed: current, note: "Já estava no estado original.");
            return new(entryId, key, RevertOutcome.AlreadyOriginal, current.Describe());
        }

        if (e.State == JournalState.BackedUp)
        {
            // Apply nunca foi chamado: qualquer diferença veio de fora do NEXUS. Nunca escrever.
            _journal.Transition(entryId, JournalState.Reverted, observed: current,
                note: $"Interrompido antes de aplicar; valor atual {current.Describe()} não foi escrito pelo NEXUS e foi preservado.");
            return new(entryId, key, RevertOutcome.NothingToRevert, current.Describe());
        }

        // Valores que o diário prova terem sido produzidos (ou autorizados) pelo NEXUS:
        //  - o desejado;
        //  - em Failed: o valor relido logo após o Apply, gravado pela compensação (caso A2);
        //  - em Restoring: o valor que a restauração estava autorizada a sobrescrever ou o que ela deixou (caso A3).
        var matchesDesired = current.SameValueAs(e.Action.Desired);
        var matchesNexusRecord = e.State is JournalState.Failed or JournalState.Restoring &&
                                 e.LastObserved is not null && current.SameValueAs(e.LastObserved);
        var authorized = matchesDesired || matchesNexusRecord;
        if (!authorized && !force)
        {
            if (e.State != JournalState.RevertConflict)
                _journal.Transition(entryId, JournalState.RevertConflict, observed: current,
                    note: $"Alterado externamente para {current.Describe()}; original {e.Original.Describe()}.");
            return new(entryId, key, RevertOutcome.Conflict,
                $"Atual {current.Describe()} ≠ aplicado {e.Action.Desired.Describe()}. Confirmação necessária.");
        }

        // Caso A1: interrompido entre Applying e Applied com o valor desejado presente. A releitura não prova quem o escreveu;
        // a janela é a própria chamada ao Apply, então presume-se que foi o NEXUS. O resultado declara a inferência.
        var inferred = matchesDesired && e.State == JournalState.Applying;
        var context = authorized ? "" : "Conflito confirmado pelo usuário (--force).";
        if (inferred) context = "Inferido: interrompido durante o Apply com o valor desejado presente; presumido escrito pelo NEXUS.";
        return RestoreJournaled(entryId, handler, e.Action, e.Original, current, context, inferred,
            successNote: authorized ? null : "Revertido com confirmação de conflito.");
    }

    /// <summary>
    /// Desfazer de uma ação irreversível: nunca chama o handler. Se o Apply nem começou (BackedUp), nada foi feito e a entrada
    /// é encerrada como Reverted. Senão devolve <see cref="RevertOutcome.Irreversible"/>; entradas que a recuperação veria como
    /// pendentes (interrompidas, falhas) são encerradas como Accepted, com a nota de que não há como desfazer.
    /// </summary>
    internal static RevertResult RefuseIrreversible(IJournal journal, JournalEntry e)
    {
        var key = e.Action.Definition.Key;
        if (e.State == JournalState.BackedUp)
        {
            journal.Transition(e.Id, JournalState.Reverted, note: "Ação irreversível interrompida antes de aplicar: nada foi feito.");
            return new(e.Id, key, RevertOutcome.NothingToRevert, "Interrompida antes de aplicar: nada foi feito.");
        }
        const string why = "Ação irreversível: não há como desfazer (não existe cópia do original). Nada foi escrito.";
        if (e.State is not (JournalState.Verified or JournalState.Reverted or JournalState.Accepted or JournalState.Planned))
            journal.Transition(e.Id, JournalState.Accepted, note: $"{why} Estava em {e.State}; entrada encerrada sem restaurar.");
        return new(e.Id, key, RevertOutcome.Irreversible, why);
    }

    /// <summary>Reverte uma execução inteira em ordem inversa. Relata cada item; nunca declara sucesso total com falhas parciais.</summary>
    public IReadOnlyList<RevertResult> RevertRun(Guid runId, bool force = false) =>
        _journal.Entries(runId).OrderByDescending(e => e.Sequence).Select(e => Revert(e.Id, force)).ToList();
}
