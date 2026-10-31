namespace Nexus.Core;

public enum PlanStatus { Pending, AlreadyConfigured, NotApplicable, Blocked }

public sealed record PlanItem(
    ActionInstance Action,
    PlanStatus Status,
    StateSnapshot ObservedAtPlan,
    string? Reason,
    int Batch);

public sealed record Plan(IReadOnlyList<PlanItem> Items)
{
    public IEnumerable<PlanItem> Pending => Items.Where(i => i.Status == PlanStatus.Pending).OrderBy(i => i.Batch);
    public bool RequiresReboot => Pending.Any(i => i.Action.Definition.Reboot == RebootRequirement.Required);
}

/// <summary>
/// Calcula apenas as diferenças entre estado atual e estado desejado do perfil.
/// Ordena por dependências (topológica estável), bloqueia conflitos e agrupa as mudanças
/// que exigem reinicialização no último lote.
/// <para>
/// Cada item é uma INSTÂNCIA (<see cref="ActionInstance.InstanceKey"/>: ação + alvo resolvido). A mesma ação pode aparecer
/// várias vezes com alvos diferentes (remoção em lote); a mesma ação com o mesmo alvo é recusada. Dependências e conflitos
/// continuam declarados por id de ação: depender de X exige que TODAS as instâncias de X no plano estejam pendentes ou já
/// configuradas (e que exista ao menos uma); conflitar com X bloqueia se qualquer instância de X estiver ativa.
/// </para>
/// <para>
/// Ações <see cref="PersistenceKind.Ephemeral"/> não têm estado: o handler não é lido (nem Detect) e o item fica sempre pendente
/// quando aplicável.
/// </para>
/// </summary>
public sealed class DesiredStateResolver
{
    private readonly HandlerRegistry _handlers;

    public DesiredStateResolver(HandlerRegistry handlers) => _handlers = handlers;

    /// <summary>Estado registrado no plano para ações efêmeras (não há o que ler).</summary>
    public static StateSnapshot EphemeralObserved { get; } = StateSnapshot.Of("Ephemeral", "sem-estado");

    public Plan Resolve(IReadOnlyList<ActionInstance> desired, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var duplicates = desired.GroupBy(a => a.InstanceKey, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => $"{g.First().Definition.Id} ({g.First().TargetLabel})").ToList();
        if (duplicates.Count > 0)
            throw new ArgumentException($"Perfil contém ações repetidas (mesma ação e mesmo alvo): {string.Join(", ", duplicates)}");

        var ordered = TopologicalOrder(desired);
        var items = new List<PlanItem>(ordered.Count);

        foreach (var action in ordered)
        {
            if (!_handlers.Contains(action.Definition.HandlerKind))
            {
                items.Add(new PlanItem(action, PlanStatus.Blocked, StateSnapshot.Absent(),
                    $"HandlerKind '{action.Definition.HandlerKind}' não implementado.", 0));
                continue;
            }

            var handler = _handlers.Get(action.Definition.HandlerKind);
            var common = CommonAssessment.Check(action, context);
            var specific = common.Kind == ApplicabilityKind.Applicable ? handler.Assess(action, context) : common;

            if (specific.Kind != ApplicabilityKind.Applicable)
            {
                var status = specific.Kind == ApplicabilityKind.NotApplicable ? PlanStatus.NotApplicable : PlanStatus.Blocked;
                items.Add(new PlanItem(action, status, StateSnapshot.Absent(), specific.Reason, 0));
                continue;
            }

            var batch = action.Definition.Reboot == RebootRequirement.Required ? 1 : 0;
            if (action.Definition.Persistence == PersistenceKind.Ephemeral)
            {
                items.Add(new PlanItem(action, PlanStatus.Pending, EphemeralObserved, null, batch));
                continue;
            }

            var current = handler.Detect(action);
            if (current.Origin is ValueOrigin.PolicyEnforced or ValueOrigin.ThirdPartyManaged)
            {
                items.Add(new PlanItem(action, PlanStatus.Blocked, current,
                    $"Valor gerenciado ({current.Origin}); o NEXUS não disputa com políticas ou outros gerenciadores.", 0));
                continue;
            }

            if (current.SameValueAs(action.Desired))
            {
                items.Add(new PlanItem(action, PlanStatus.AlreadyConfigured, current, null, 0));
                continue;
            }

            items.Add(new PlanItem(action, PlanStatus.Pending, current, null, batch));
        }

        // Conflitos primeiro; depois propaga dependências até ponto fixo (um item bloqueado por conflito
        // também bloqueia quem depende dele).
        BlockConflicts(items);
        BlockUnsatisfiedDependencies(items);
        PromoteDependentsToLaterBatch(items);
        return new Plan(items);
    }

    private static bool IsActive(PlanItem i) => i.Status is PlanStatus.Pending or PlanStatus.AlreadyConfigured;

    private static void BlockUnsatisfiedDependencies(List<PlanItem> items)
    {
        bool changed;
        do
        {
            changed = false;
            // Satisfeita = há instâncias da dependência no plano e todas estão ativas.
            var byId = items.GroupBy(i => i.Action.Definition.Id, StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.All(IsActive), StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Status != PlanStatus.Pending) continue;
                var missing = (item.Action.Definition.DependsOn ?? []).Where(d => !byId.TryGetValue(d, out var allOk) || !allOk).ToList();
                if (missing.Count == 0) continue;
                items[i] = item with { Status = PlanStatus.Blocked, Reason = $"Dependência não satisfeita: {string.Join(", ", missing)}" };
                changed = true;
            }
        } while (changed);
    }

    /// <summary>Quem depende de um item do lote de reinício vai para o mesmo lote (a ordem topológica é preservada dentro do lote).</summary>
    private static void PromoteDependentsToLaterBatch(List<PlanItem> items)
    {
        var batchOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in items.Where(i => i.Status == PlanStatus.Pending))
            batchOf[p.Action.Definition.Id] = Math.Max(batchOf.GetValueOrDefault(p.Action.Definition.Id), p.Batch);
        for (var i = 0; i < items.Count; i++) // items já está em ordem topológica
        {
            var item = items[i];
            if (item.Status != PlanStatus.Pending) continue;
            var required = (item.Action.Definition.DependsOn ?? []).Select(d => batchOf.TryGetValue(d, out var b) ? b : 0).DefaultIfEmpty(0).Max();
            if (required > item.Batch)
            {
                items[i] = item with { Batch = required };
                batchOf[item.Action.Definition.Id] = Math.Max(batchOf.GetValueOrDefault(item.Action.Definition.Id), required);
            }
        }
    }

    private static void BlockConflicts(List<PlanItem> items)
    {
        var active = items.Where(IsActive).Select(i => i.Action.Definition.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Status != PlanStatus.Pending) continue;
            var hits = (item.Action.Definition.ConflictsWith ?? []).Where(active.Contains).ToList();
            if (hits.Count > 0)
                items[i] = item with { Status = PlanStatus.Blocked, Reason = $"Conflito com: {string.Join(", ", hits)}" };
        }
    }

    /// <summary>
    /// Ordenação de Kahn estável sobre instâncias (preserva a ordem do perfil quando não há dependência). Cada instância que
    /// depende de X espera todas as instâncias de X.
    /// </summary>
    private static List<ActionInstance> TopologicalOrder(IReadOnlyList<ActionInstance> actions)
    {
        var n = actions.Count;
        var indexesById = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var id = actions[i].Definition.Id;
            if (!indexesById.TryGetValue(id, out var list)) indexesById[id] = list = [];
            list.Add(i);
        }

        var indegree = new int[n];
        var dependents = new List<int>[n];
        for (var i = 0; i < n; i++) dependents[i] = [];
        for (var i = 0; i < n; i++)
            foreach (var dep in (actions[i].Definition.DependsOn ?? []).Distinct(StringComparer.Ordinal))
                if (indexesById.TryGetValue(dep, out var sources))
                    foreach (var s in sources)
                    {
                        indegree[i]++; // depender de si mesma vira ciclo, como antes
                        dependents[s].Add(i);
                    }

        var ready = new SortedSet<int>(Enumerable.Range(0, n).Where(i => indegree[i] == 0));
        var result = new List<ActionInstance>(n);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            result.Add(actions[next]);
            foreach (var d in dependents[next])
                if (--indegree[d] == 0) ready.Add(d);
        }

        if (result.Count != n)
        {
            var cycle = Enumerable.Range(0, n).Where(i => indegree[i] > 0).Select(i => actions[i].Definition.Id).Distinct(StringComparer.Ordinal);
            throw new InvalidOperationException($"Ciclo de dependências no perfil: {string.Join(", ", cycle)}");
        }
        return result;
    }
}
