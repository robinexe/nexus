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
/// </summary>
public sealed class DesiredStateResolver
{
    private readonly HandlerRegistry _handlers;

    public DesiredStateResolver(HandlerRegistry handlers) => _handlers = handlers;

    public Plan Resolve(IReadOnlyList<ActionInstance> desired, SystemContext context)
    {
        var duplicates = desired.GroupBy(a => a.Definition.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ArgumentException($"Perfil contém ações repetidas: {string.Join(", ", duplicates)}");

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

            var batch = action.Definition.Reboot == RebootRequirement.Required ? 1 : 0;
            items.Add(new PlanItem(action, PlanStatus.Pending, current, null, batch));
        }

        // Conflitos primeiro; depois propaga dependências até ponto fixo (um item bloqueado por conflito
        // também bloqueia quem depende dele).
        BlockConflicts(items);
        BlockUnsatisfiedDependencies(items);
        PromoteDependentsToLaterBatch(items);
        return new Plan(items);
    }

    private static void BlockUnsatisfiedDependencies(List<PlanItem> items)
    {
        bool changed;
        do
        {
            changed = false;
            var ok = items.Where(i => i.Status is PlanStatus.Pending or PlanStatus.AlreadyConfigured)
                          .Select(i => i.Action.Definition.Id).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Status != PlanStatus.Pending) continue;
                var missing = (item.Action.Definition.DependsOn ?? []).Where(d => !ok.Contains(d)).ToList();
                if (missing.Count == 0) continue;
                items[i] = item with { Status = PlanStatus.Blocked, Reason = $"Dependência não satisfeita: {string.Join(", ", missing)}" };
                changed = true;
            }
        } while (changed);
    }

    /// <summary>Quem depende de um item do lote de reinício vai para o mesmo lote (a ordem topológica é preservada dentro do lote).</summary>
    private static void PromoteDependentsToLaterBatch(List<PlanItem> items)
    {
        var batchOf = items.Where(i => i.Status == PlanStatus.Pending).ToDictionary(i => i.Action.Definition.Id, i => i.Batch, StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++) // items já está em ordem topológica
        {
            var item = items[i];
            if (item.Status != PlanStatus.Pending) continue;
            var required = (item.Action.Definition.DependsOn ?? []).Select(d => batchOf.TryGetValue(d, out var b) ? b : 0).DefaultIfEmpty(0).Max();
            if (required > item.Batch)
            {
                items[i] = item with { Batch = required };
                batchOf[item.Action.Definition.Id] = required;
            }
        }
    }

    private static void BlockConflicts(List<PlanItem> items)
    {
        var active = items.Where(i => i.Status is PlanStatus.Pending or PlanStatus.AlreadyConfigured)
                          .ToDictionary(i => i.Action.Definition.Id, StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Status != PlanStatus.Pending) continue;
            var hits = (item.Action.Definition.ConflictsWith ?? []).Where(active.ContainsKey).ToList();
            if (hits.Count > 0)
                items[i] = item with { Status = PlanStatus.Blocked, Reason = $"Conflito com: {string.Join(", ", hits)}" };
        }
    }

    /// <summary>Ordenação de Kahn estável (preserva a ordem do perfil quando não há dependência).</summary>
    private static List<ActionInstance> TopologicalOrder(IReadOnlyList<ActionInstance> actions)
    {
        var byId = actions.ToDictionary(a => a.Definition.Id, StringComparer.Ordinal);
        var indegree = actions.ToDictionary(a => a.Definition.Id, _ => 0, StringComparer.Ordinal);
        var dependents = actions.ToDictionary(a => a.Definition.Id, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var a in actions)
            foreach (var dep in a.Definition.DependsOn ?? [])
                if (byId.ContainsKey(dep))
                {
                    indegree[a.Definition.Id]++;
                    dependents[dep].Add(a.Definition.Id);
                }

        var position = actions.Select((a, i) => (a.Definition.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var ready = new SortedSet<(int Pos, string Id)>(actions.Where(a => indegree[a.Definition.Id] == 0)
                                                              .Select(a => (position[a.Definition.Id], a.Definition.Id)));
        var result = new List<ActionInstance>(actions.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            result.Add(byId[next.Id]);
            foreach (var d in dependents[next.Id])
                if (--indegree[d] == 0) ready.Add((position[d], d));
        }

        if (result.Count != actions.Count)
        {
            var cycle = indegree.Where(kv => kv.Value > 0).Select(kv => kv.Key);
            throw new InvalidOperationException($"Ciclo de dependências no perfil: {string.Join(", ", cycle)}");
        }
        return result;
    }
}
