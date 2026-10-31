using System.Globalization;
using System.Text;
using Nexus.Broker;
using Nexus.Core;

namespace Nexus.Automation;

public sealed record OneClickItem(Recommendation Recommendation, ActionRequest Request, BrokerLevel Level, string State);

public sealed record OneClickSkip(Recommendation Recommendation, string Reason);

/// <summary>
/// Plano do "Otimizar agora": o que vai pelo aplicador do usuário (sem aviso) e o que vai pelo de administrador (aviso do
/// Windows + ponto de restauração), e o que ficou de fora com o motivo.
/// </summary>
public sealed record OneClickPlan(IReadOnlyList<OneClickItem> User, IReadOnlyList<OneClickItem> Admin, IReadOnlyList<OneClickSkip> Skipped)
{
    public static readonly OneClickPlan Empty = new([], [], []);
    public int Count => User.Count + Admin.Count;
    public bool IsEmpty => Count == 0;

    public IReadOnlyList<ActionRequest> Requests(BrokerLevel level) => (level == BrokerLevel.User ? User : Admin).Select(i => i.Request).ToList();

    public IReadOnlyList<string> Gains => User.Concat(Admin).Select(i => i.Recommendation.Gain).Where(g => !string.IsNullOrWhiteSpace(g))
        .Select(g => g!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Resumo de uma linha: "3 ações · 1 pede administrador · risco baixo".</summary>
    public string Summary => IsEmpty
        ? "Nada a fazer agora: o que é seguro aplicar em um clique já está assim neste PC."
        : string.Create(CultureInfo.InvariantCulture,
            $"{Count} {(Count == 1 ? "ação" : "ações")}{(Admin.Count > 0 ? $" · {Admin.Count} pede{(Admin.Count == 1 ? "" : "m")} administrador" : "")} · todas de risco baixo e reversíveis");

    /// <summary>Texto da confirmação de um nível (o Host/terminal mostra antes de aplicar).</summary>
    public string Confirmation(BrokerLevel level)
    {
        var items = level == BrokerLevel.User ? User : Admin;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Aplicar {items.Count} {(items.Count == 1 ? "ação" : "ações")} recomendada(s) para este PC?");
        sb.AppendLine();
        foreach (var i in items)
            sb.AppendLine(CultureInfo.InvariantCulture, $"• {i.Recommendation.Title}: {i.Recommendation.Reason}");
        sb.AppendLine();
        if (level == BrokerLevel.Admin)
        {
            sb.AppendLine("Estas mudanças são do sistema: o Windows vai pedir sua permissão (aviso do Controle de Conta de Usuário),");
            sb.AppendLine("e o NEXUS cria um ponto de restauração antes de alterar qualquer coisa.");
        }
        sb.Append("Cada alteração é registrada antes de ser feita e pode ser desfeita depois em \"Desfazer tudo\".");
        return sb.ToString();
    }
}

public static class OneClickPlanner
{
    /// <summary>Motivo que o aplicador do usuário dá para ações de administrador (CommonAssessment): não é recusa, é nível.</summary>
    public const string NeedsElevation = "Requer o broker elevado.";

    /// <summary>Pedidos candidatos (antes de perguntar ao aplicador): as recomendações marcadas para um clique.</summary>
    public static IReadOnlyList<(Recommendation Recommendation, ActionRequest Request)> Candidates(RecommendationSet set, CatalogDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(catalog);
        var list = new List<(Recommendation, ActionRequest)>();
        foreach (var r in set.OneClick)
        {
            var entry = catalog.Actions.FirstOrDefault(a => a.Definition.Key == r.ActionKey);
            if (entry is null) continue;
            list.Add((r, new ActionRequest(entry.Definition.Key, entry.DesiredValue is null ? r.Value : null)));
        }
        return list;
    }

    /// <summary>
    /// Monta o plano com o resultado da simulação do aplicador do USUÁRIO (sem aviso do Windows): "Pending" entra; uma ação de
    /// administrador recusada só por falta de elevação entra no nível administrador (o aplicador elevado repete a simulação
    /// antes de escrever); "já está assim", "não se aplica" e bloqueios ficam de fora com o motivo.
    /// </summary>
    public static OneClickPlan Build(IReadOnlyList<(Recommendation Recommendation, ActionRequest Request)> candidates, IReadOnlyList<ActionResultDto> planResults)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(planResults);
        var user = new List<OneClickItem>();
        var admin = new List<OneClickItem>();
        var skipped = new List<OneClickSkip>();
        foreach (var (rec, req) in candidates)
        {
            var result = planResults.FirstOrDefault(p => string.Equals(p.ActionKey, req.ActionId, StringComparison.Ordinal));
            var isAdmin = rec.Privileges == PrivilegeLevel.Admin;
            switch (result)
            {
                case null:
                    skipped.Add(new(rec, "O aplicador não respondeu sobre esta ação."));
                    break;
                case { Outcome: "Pending" }:
                    (isAdmin ? admin : user).Add(new(rec, req, isAdmin ? BrokerLevel.Admin : BrokerLevel.User, $"{result.Observed} → {result.Desired}"));
                    break;
                case { Outcome: "Blocked" } when isAdmin && result.Detail == NeedsElevation:
                    admin.Add(new(rec, req, BrokerLevel.Admin, "confirmado pelo aplicador de administrador antes de escrever"));
                    break;
                case { Outcome: "AlreadyConfigured" }:
                    skipped.Add(new(rec, "Já está assim neste PC."));
                    break;
                case { Outcome: "NotApplicable" }:
                    skipped.Add(new(rec, $"Não se aplica: {result.Detail}"));
                    break;
                default:
                    skipped.Add(new(rec, $"{result.Outcome}: {result.Detail}"));
                    break;
            }
        }
        return new OneClickPlan(user, admin, skipped);
    }

    /// <summary>
    /// Simula no aplicador. Uma ação inválida derruba o pedido inteiro (o broker recusa o lote); nesse caso, simula uma a uma
    /// para que as demais continuem valendo. Sem conexão: devolve a mensagem e nenhum resultado.
    /// </summary>
    public static async Task<(IReadOnlyList<ActionResultDto> Results, string? Problem)> SimulateAsync(
        IReadOnlyList<ActionRequest> requests, Func<IReadOnlyList<ActionRequest>, Task<BrokerCallResult>> plan)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(plan);
        if (requests.Count == 0) return ([], null);
        var all = await plan(requests).ConfigureAwait(false);
        if (all.Ok) return (all.Response!.Results ?? [], null);
        if (!all.Connected || all.Response is null) return ([], all.Message);
        var results = new List<ActionResultDto>();
        foreach (var r in requests)
        {
            var one = await plan([r]).ConfigureAwait(false);
            if (one.Ok) results.AddRange(one.Response!.Results ?? []);
            else if (!one.Connected) return (results, one.Message);
            else results.Add(new ActionResultDto(r.ActionId, "Refused", one.Response?.Error?.Detail ?? one.Message));
        }
        return (results, null);
    }
}
