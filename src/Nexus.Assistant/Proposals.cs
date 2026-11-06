using System.Globalization;
using System.Text;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;

namespace Nexus.Assistant;

/// <summary>Um número achado numa etapa ("9,6 GB", "3 de 5"). Tom: good, warn, bad, info.</summary>
public sealed record StepFact(string Label, string Value, string Tone = "info", string? Detail = null);

public sealed record MetricValue(string Label, string Value);

/// <summary>Algo que só o usuário pode fazer (comprar um módulo de RAM, ligar o XMP no BIOS, baixar o driver no site oficial).</summary>
public sealed record UserAction(string Title, string Why, string? Link = null);

/// <summary>
/// Uma mudança proposta: um pedido ao aplicador (ação do catálogo) com o nível, o risco e o que o usuário vê.
/// O risco, "experimental", "irreversível" e o nível vêm SEMPRE do catálogo (<see cref="AssistantCatalog.Change"/>), nunca do chamador.
/// </summary>
public sealed record ProposedChange(
    string Id,
    AssistantStepId Step,
    string Title,
    string Detail,
    ActionRequest Request,
    BrokerLevel Level,
    ActionRisk Risk,
    bool Experimental,
    bool Irreversible,
    bool Undoable,
    string? Before = null,
    string? After = null,
    bool OptIn = false)
{
    /// <summary>Marcada por padrão? Só risco baixo, documentada, reversível e que não seja opt-in.</summary>
    public bool SelectedByDefault => ChangeSelection.IsSelectedByDefault(this);

    /// <summary>Classificação do catálogo (o "Experimental" acima junta Experimental com Rejeitado/Obsoleto/Inútil; o modo
    /// "Desempenho máximo" precisa separar: experimental entra, mito nunca).</summary>
    public Nexus.Core.Classification Classification { get; init; } = Nexus.Core.Classification.SupportedDocumented;

    public string LevelText => Level == BrokerLevel.Admin ? "administrador" : "sua conta";
    public string RiskText => Experimental ? "experimental" : RiskModel.Text(Risk);
    public string BeforeAfter => Before is null && After is null ? "" : $"{Before ?? "?"} → {After ?? "?"}";
}

/// <summary>O que uma etapa leu e propõe.</summary>
public sealed record StepScan(
    IReadOnlyList<StepFact> Facts,
    IReadOnlyList<ProposedChange> Changes,
    IReadOnlyList<UserAction> UserActions,
    IReadOnlyList<string> Notes,
    string Recommendation)
{
    /// <summary>Itens só para o usuário revisar (programas grandes, maiores consumidores, pontos suspeitos): nada é aplicado.</summary>
    public IReadOnlyList<ReviewItem> Review { get; init; } = [];

    /// <summary>Números que vão para o relatório (chave estável → rótulo e valor): lidos no começo (antes) e de novo no fim (depois).</summary>
    public IReadOnlyDictionary<string, MetricValue> Metrics { get; init; } = new Dictionary<string, MetricValue>();

    public static StepScan Empty(string recommendation) => new([], [], [], [], recommendation);

    /// <summary>Etapa que não pôde ler: um fato "indisponível" com o motivo, nada proposto.</summary>
    public static StepScan Unavailable(string what, string reason) =>
        new([new StepFact(what, "indisponível", "warn", reason)], [], [], [], "Não consegui ler esta parte agora. Dá para pular e seguir.");
}

/// <summary>Regras de seleção: o que é marcado por padrão e o que nunca entra no assistente.</summary>
public static class ChangeSelection
{
    /// <summary>Nunca entra: ação irreversível (exclusão segura, esvaziar quarentena/Lixeira, desinstalar) ou pedido que confirme irreversível.</summary>
    public static bool IsAllowed(ProposedChange c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return !c.Irreversible && c.Request.ConfirmIrreversible != true;
    }

    public static bool IsSelectedByDefault(ProposedChange c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return IsAllowed(c) && !c.Experimental && !c.OptIn && c.Risk == ActionRisk.Low;
    }

    /// <summary>Tira o que não é permitido e duplicatas (mesma ação + valor + argumentos), mantendo a primeira ocorrência.</summary>
    public static IReadOnlyList<ProposedChange> Sanitize(IEnumerable<ProposedChange> changes, ISet<string>? alreadyProposed = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var seen = alreadyProposed ?? new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ProposedChange>();
        foreach (var c in changes)
        {
            if (!IsAllowed(c)) continue;
            if (!seen.Add(Signature(c.Request))) continue;
            list.Add(c);
        }
        return list;
    }

    public static string Signature(ActionRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var args = r.Arguments is null ? "" : string.Join(";", r.Arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        return $"{r.ActionId}|{r.Value}|{args}";
    }
}

/// <summary>Catálogo carregado + riscos declarados: fábrica das mudanças (nível, risco e reversibilidade vêm daqui).</summary>
public sealed class AssistantCatalog
{
    private readonly Dictionary<string, CatalogEntry> _byKey;
    private readonly IReadOnlyDictionary<string, ActionRisk> _declared;

    public AssistantCatalog(CatalogDocument document, IReadOnlyDictionary<string, ActionRisk>? declaredRisk = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _declared = declaredRisk ?? new Dictionary<string, ActionRisk>();
        _byKey = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var e in document.Actions) _byKey[e.Definition.Key] = e;
    }

    public CatalogDocument Document { get; }
    public IReadOnlyDictionary<string, ActionRisk> DeclaredRisk => _declared;

    public static AssistantCatalog Load(string directory) =>
        new(CatalogLoader.LoadDirectory(directory), RiskModel.ReadDeclared(directory));

    public CatalogEntry? Entry(string actionKey) => _byKey.GetValueOrDefault(actionKey);

    /// <summary>
    /// Monta a mudança a partir do catálogo. Null quando a ação não existe no catálogo (nunca se inventa nível ou risco) ou
    /// quando é irreversível (o assistente nunca propõe).
    /// </summary>
    public ProposedChange? Change(AssistantStepId step, ActionRequest request, string title, string detail, string? before = null, string? after = null, bool optIn = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Entry(request.ActionId) is not { } e) return null;
        var d = e.Definition;
        // Ajuste "de sessão" segura a trava do aplicador até alguém encerrar a sessão: as etapas seguintes seriam recusadas
        // ("session-active") e o assistente não tem onde encerrar. Fica para as páginas que controlam a sessão.
        if (d.Persistence == PersistenceKind.Session) return null;
        var risk = RiskModel.Of(e, _declared.TryGetValue(d.Key, out var r) ? r : null);
        var experimental = d.Classification is Classification.Experimental or Classification.Rejected or Classification.Obsolete
            or Classification.Incompatible or Classification.Ineffective;
        var change = new ProposedChange(
            Id: $"{step}:{ChangeSelection.Signature(request)}",
            Step: step,
            Title: title,
            Detail: detail,
            Request: request,
            Level: d.Privileges == PrivilegeLevel.Admin ? BrokerLevel.Admin : BrokerLevel.User,
            Risk: risk,
            Experimental: experimental,
            Irreversible: d.Irreversible,
            Undoable: !d.Irreversible && d.Persistence != PersistenceKind.Ephemeral,
            Before: before,
            After: after,
            OptIn: optIn) { Classification = d.Classification };
        return ChangeSelection.IsAllowed(change) ? change : null;
    }
}

/// <summary>Um pedido ao aplicador: uma etapa, um nível, no máximo 64 ações, com o texto da confirmação.</summary>
public sealed record ApplyBatch(AssistantStepId Step, BrokerLevel Level, int Part, int Parts, string Title, string Confirmation, IReadOnlyList<ProposedChange> Changes)
{
    public IReadOnlyList<ActionRequest> Requests => Changes.Select(c => c.Request).ToList();
}

/// <summary>Agrupa as mudanças escolhidas de uma etapa: primeiro tudo da conta do usuário, depois o administrador (um aviso do Windows).</summary>
public static class ApplyGrouping
{
    public const int MaxActionsPerRequest = 64;

    public static IReadOnlyList<ApplyBatch> Group(AssistantStepId step, IEnumerable<ProposedChange> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var allowed = ChangeSelection.Sanitize(chosen);
        var chunks = new List<(BrokerLevel Level, IReadOnlyList<ProposedChange> Items)>();
        foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
            foreach (var chunk in allowed.Where(c => c.Level == level).Chunk(MaxActionsPerRequest))
                chunks.Add((level, chunk));
        var info = AssistantSteps.Info(step);
        var batches = new List<ApplyBatch>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var (level, items) = chunks[i];
            var title = $"Assistente · etapa {info.Number}: {info.Title}" + (level == BrokerLevel.Admin ? " (administrador)" : "");
            batches.Add(new ApplyBatch(step, level, i + 1, chunks.Count, title, Confirmation(info, level, items, i + 1, chunks.Count), items));
        }
        return batches;
    }

    public static string Confirmation(StepInfo info, BrokerLevel level, IReadOnlyList<ProposedChange> items, int part, int parts)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        if (parts > 1) sb.AppendLine(CultureInfo.InvariantCulture, $"Parte {part} de {parts}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{info.Title}: {items.Count} {(items.Count == 1 ? "mudança" : "mudanças")} {(level == BrokerLevel.Admin ? "de sistema" : "na sua conta")}.");
        sb.AppendLine();
        foreach (var c in items.Take(20))
            sb.AppendLine(CultureInfo.InvariantCulture, $"• {c.Title}{(c.BeforeAfter.Length > 0 ? $" ({c.BeforeAfter})" : "")}");
        if (items.Count > 20) sb.AppendLine(CultureInfo.InvariantCulture, $"• e mais {items.Count - 20}.");
        sb.AppendLine();
        sb.Append(items.All(c => c.Undoable)
            ? "Tudo fica no diário do NEXUS e volta em \"Desfazer tudo que o assistente fez\"."
            : "Fica no diário do NEXUS. O que é de efeito imediato (ponto de restauração) não tem o que desfazer; o resto volta em \"Desfazer tudo que o assistente fez\".");
        if (level == BrokerLevel.Admin)
            sb.Append(" O Windows vai pedir permissão de administrador uma vez e o NEXUS cria um ponto de restauração antes.");
        return sb.ToString();
    }
}
