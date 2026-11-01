using Nexus.Broker;

namespace Nexus.Assistant;

/// <summary>Resposta do aplicador, sem WPF (a interface converte de BrokerCallResult).</summary>
public sealed record WriteOutcome(bool Ok, Guid? RunId, string Message, IReadOnlyList<ActionResultDto> Results, bool? FullyRestored = null)
{
    public static WriteOutcome From(BrokerCallResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Ok, r.Response?.RunId, r.Message, r.Response?.Results ?? [], r.Response?.FullyRestored);
    }
}

/// <summary>
/// A única via de escrita do assistente: a interface implementa com IModuleHost.ApplyAsync/RevertAsync (confirmação, aplicador,
/// diário, aviso do Windows para administrador). Null = o usuário cancelou.
/// </summary>
public interface IAssistantWriter
{
    Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level);
    Task<WriteOutcome?> RevertAsync(Guid runId, BrokerLevel level);

    /// <summary>Igual a ApplyAsync, avisando os marcos reais do pedido (confirmação, aplicador, releitura) para a tela de progresso.</summary>
    Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level, IProgress<ApplyStage>? stages) =>
        ApplyAsync(title, confirmation, requests, level);
}

/// <summary>Quem acompanha a aplicação de uma etapa (a tela de progresso). Tudo é chamado na thread de quem aplica.</summary>
public interface IApplyObserver
{
    /// <summary>Uma mudança não precisou ir ao aplicador (ex.: já há ponto de restauração recente).</summary>
    void NotNeeded(ProposedChange change, string why);

    /// <summary>Os lotes que serão pedidos, na ordem.</summary>
    void Planned(IReadOnlyList<ApplyBatch> batches);

    /// <summary>Marco real de um lote (esperando confirmação, aplicador, aplicando, relendo).</summary>
    void Stage(ApplyBatch batch, ApplyStage stage);

    /// <summary>O aplicador respondeu ao lote (ou ele foi cancelado): o que aconteceu com cada mudança, na ordem do lote.</summary>
    void Finished(ApplyBatch batch, IReadOnlyList<ChangeRecord> records);
}

/// <summary>Ponto de restauração recente vale como proteção (o Windows cria no máximo um a cada 24 h).</summary>
public static class RestorePointRule
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static bool IsRecent(DateTime? created, DateTime now) => created is { } c && c <= now.AddMinutes(5) && now - c < Window;

    public static string AlreadyProtected(DateTime created) =>
        string.Create(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"),
            $"Já existe um ponto de restauração recente ({created:dd/MM HH:mm}): vale como proteção.");

    public const string AlreadyProtectedByWindows =
        "Já protegido: o Windows já tem um ponto de restauração recente (ele cria no máximo um a cada 24 h) e ele vale como proteção.";
}

/// <summary>O que aconteceu ao aplicar uma etapa.</summary>
/// <param name="NotNeeded">Neutras que não foram ao aplicador ou que ele não precisou fazer (ex.: ponto de restauração recente).</param>
public sealed record StepApplyResult(int Applied, int Already, int Refused, int Canceled, IReadOnlyList<string> Lines, int NotNeeded = 0)
{
    public bool AnyWritten => Applied > 0;
    public string Summary =>
        Applied + Already + Refused + Canceled + NotNeeded == 0 ? "Nada a aplicar." :
        string.Join(" · ", new[]
        {
            Applied > 0 ? $"{Applied} {(Applied == 1 ? "aplicada" : "aplicadas")}" : null,
            Already > 0 ? $"{Already} já {(Already == 1 ? "estava" : "estavam")} assim" : null,
            NotNeeded > 0 ? $"{NotNeeded} não {(NotNeeded == 1 ? "precisou" : "precisaram")}" : null,
            Refused > 0 ? $"{Refused} {(Refused == 1 ? "recusada" : "recusadas")} (motivo no relatório)" : null,
            Canceled > 0 ? $"{Canceled} {(Canceled == 1 ? "cancelada" : "canceladas")} por você" : null,
        }.Where(s => s is not null));
}

public static class AssistantRunner
{
    private static bool IsOk(string outcome) => outcome is "Verified" or "AlreadyConfigured";

    /// <summary>
    /// Aplica as mudanças escolhidas de uma etapa, agrupadas (usuário primeiro, administrador depois). Recusa do aplicador
    /// não interrompe: é registrada com o motivo e o resto continua. Cancelar uma confirmação encerra a etapa (o que já foi
    /// aplicado fica, e pode ser desfeito).
    /// </summary>
    /// <param name="observer">Tela de progresso (opcional).</param>
    /// <param name="recentRestorePoint">Ponto de restauração mais novo que a interface conseguiu ler (null = nenhum ou não deu
    /// para ler). Se tiver menos de 24 h, o pedido de criar outro não vai ao aplicador: é registrado como "não precisou".</param>
    /// <param name="now">Relógio (testes).</param>
    public static async Task<StepApplyResult> ApplyAsync(AssistantState state, AssistantStepId step, IEnumerable<ProposedChange> chosen, IAssistantWriter writer,
        IApplyObserver? observer = null, DateTime? recentRestorePoint = null, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chosen);
        int applied = 0, already = 0, refused = 0, canceled = 0, notNeeded = 0;
        var list = chosen.ToList();
        if (recentRestorePoint is { } point && RestorePointRule.IsRecent(point, now ?? DateTime.Now))
        {
            var why = RestorePointRule.AlreadyProtected(point);
            foreach (var c in list.Where(c => c.Request.ActionId == StepPlanners.RestorePointAction).ToList())
            {
                Record(state, c, ChangeOutcome.AlreadyConfigured, why, null);
                observer?.NotNeeded(c, why);
                list.Remove(c);
                notNeeded++;
            }
        }
        var batches = ApplyGrouping.Group(step, list);
        observer?.Planned(batches);
        var lines = new List<string>();
        var stop = false;
        foreach (var batch in batches)
        {
            var mark = state.Changes.Count;
            if (stop)
            {
                foreach (var c in batch.Changes) Record(state, c, ChangeOutcome.Canceled, "Você cancelou uma confirmação anterior desta etapa.", null);
                canceled += batch.Changes.Count;
                observer?.Finished(batch, state.Changes.Skip(mark).ToList());
                continue;
            }
            WriteOutcome? r;
            observer?.Stage(batch, ApplyStage.WaitingConfirmation);
            var stages = observer is null ? null : new Relay(s => observer.Stage(batch, s));
            try { r = await writer.ApplyAsync(batch.Title, batch.Confirmation, batch.Requests, batch.Level, stages).ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                r = new WriteOutcome(false, null, $"O aplicador não respondeu: {ex.Message}", []);
            }
            if (r is null)
            {
                foreach (var c in batch.Changes) Record(state, c, ChangeOutcome.Canceled, "Você cancelou a confirmação.", null);
                canceled += batch.Changes.Count;
                lines.Add(batch.Level == BrokerLevel.Admin ? "Mudanças de sistema canceladas." : "Cancelado.");
                stop = true;
                observer?.Finished(batch, state.Changes.Skip(mark).ToList());
                continue;
            }
            lines.Add(r.Message);
            // Só entra em "Desfazer tudo" a execução que mudou algo desfazível (ponto de restauração não tem o que desfazer).
            if (r.RunId is { } run && r.Results.Any(x => x.Outcome == "Verified") && batch.Changes.Any(c => c.Undoable))
                state.Runs.Add(new RunRecord(step, run, batch.Level, DateTimeOffset.Now, batch.Changes.Count));
            var matched = Match(batch.Changes, r.Results);
            for (var i = 0; i < batch.Changes.Count; i++)
            {
                var c = batch.Changes[i];
                var res = matched[i];
                if (!r.Ok && res is null)
                {
                    Record(state, c, ChangeOutcome.Refused, r.Message, null);
                    refused++;
                }
                else if (res is null)
                {
                    Record(state, c, ChangeOutcome.Refused, "O aplicador não devolveu resultado para esta ação.", r.RunId);
                    refused++;
                }
                else if (res.Outcome == "AlreadyConfigured")
                {
                    Record(state, c, ChangeOutcome.AlreadyConfigured, res.Detail, r.RunId);
                    already++;
                }
                else if (IsOk(res.Outcome))
                {
                    Record(state, c, ChangeOutcome.Applied, res.Detail, r.RunId, res.Observed, res.Desired);
                    applied++;
                }
                else if (c.Request.ActionId == StepPlanners.RestorePointAction && ApplySummary.IsRecentRestorePointRefusal(res.Detail))
                {
                    // O Windows recusou por já haver um ponto nas últimas 24 h: o PC já está protegido. Neutro, não é falha.
                    Record(state, c, ChangeOutcome.AlreadyConfigured, RestorePointRule.AlreadyProtectedByWindows, r.RunId);
                    notNeeded++;
                }
                else
                {
                    Record(state, c, ChangeOutcome.Refused, $"{OutcomeText(res.Outcome)}{(res.Detail is null ? "" : ": " + res.Detail)}", r.RunId);
                    refused++;
                }
            }
            observer?.Finished(batch, state.Changes.Skip(mark).ToList());
        }
        return new StepApplyResult(applied, already, refused, canceled, lines, notNeeded);
    }

    /// <summary>Resultado do aplicador em palavras (nunca o nome do enum para o usuário).</summary>
    public static string OutcomeText(string outcome) => outcome switch
    {
        "Verified" => "Aplicada e conferida",
        "AlreadyConfigured" => "Já estava assim",
        "NotApplicable" => "Não se aplica a este PC",
        "SkippedExternalChange" => "Pulada: mudou por fora desde a leitura",
        "Blocked" => "Bloqueada",
        "FailedAndCompensated" => "Não aplicada (voltou ao que era)",
        "Interrupted" => "Interrompida",
        "Conflict" => "Não voltou: mudou por fora depois",
        "Irreversible" => "Não tem desfazer",
        _ => "Não aplicada",
    };

    /// <summary>IProgress que chama na hora, na mesma thread (o host já entrega os marcos na thread da interface).</summary>
    private sealed class Relay(Action<ApplyStage> action) : IProgress<ApplyStage>
    {
        public void Report(ApplyStage value) => action(value);
    }

    /// <summary>Casa cada mudança com o resultado: pelo índice do pedido quando o aplicador informa, senão pela chave (na ordem).</summary>
    internal static ActionResultDto?[] Match(IReadOnlyList<ProposedChange> changes, IReadOnlyList<ActionResultDto> results)
    {
        var matched = new ActionResultDto?[changes.Count];
        var used = new bool[results.Count];
        for (var j = 0; j < results.Count; j++)
        {
            if (results[j].Index is { } idx && idx >= 0 && idx < changes.Count && matched[idx] is null && results[j].ActionKey == changes[idx].Request.ActionId)
            {
                matched[idx] = results[j];
                used[j] = true;
            }
        }
        for (var i = 0; i < changes.Count; i++)
        {
            if (matched[i] is not null) continue;
            for (var j = 0; j < results.Count; j++)
            {
                if (used[j] || results[j].ActionKey != changes[i].Request.ActionId) continue;
                matched[i] = results[j];
                used[j] = true;
                break;
            }
        }
        return matched;
    }

    private static void Record(AssistantState state, ProposedChange c, ChangeOutcome outcome, string? detail, Guid? run, string? observed = null, string? desired = null)
    {
        state.Changes.Add(new ChangeRecord(c.Step, c.Title, c.Before ?? observed, c.After ?? desired, c.Level, c.RiskText,
            c.Undoable && outcome == ChangeOutcome.Applied, outcome, detail, run));
        if (outcome is ChangeOutcome.Refused or ChangeOutcome.Canceled)
            state.Skipped.Add(new SkipRecord(c.Step, c.Title, outcome == ChangeOutcome.Refused ? $"Recusada pelo aplicador: {detail}" : detail ?? "Cancelada."));
    }

    /// <summary>Registra o que o usuário deixou desmarcado (vai para "pulado e por quê").</summary>
    public static void RecordUnselected(AssistantState state, IEnumerable<ProposedChange> unselected)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(unselected);
        foreach (var c in unselected)
            state.Skipped.Add(new SkipRecord(c.Step, c.Title,
                c.Experimental ? "Experimental: fica desmarcado; só com medição antes e depois." :
                c.OptIn ? "Opcional: só liga se você quiser." :
                c.Risk != Nexus.Automation.ActionRisk.Low ? $"Risco {c.RiskText}: deixado para você decidir." : "Desmarcado por você."));
    }

    /// <summary>
    /// Desfaz tudo o que o assistente aplicou, da última execução para a primeira. Cancelar interrompe (o resto continua
    /// aplicado e pode ser desfeito depois). Devolve as linhas para mostrar.
    /// </summary>
    public static async Task<IReadOnlyList<string>> UndoAllAsync(AssistantState state, IAssistantWriter writer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(writer);
        var lines = new List<string>();
        for (var i = state.Runs.Count - 1; i >= 0; i--)
        {
            var run = state.Runs[i];
            if (run.Reverted) continue;
            WriteOutcome? r;
            try { r = await writer.RevertAsync(run.RunId, run.Level).ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { r = new WriteOutcome(false, run.RunId, $"O aplicador não respondeu: {ex.Message}", []); }
            if (r is null) { lines.Add("Desfazer cancelado. O que falta continua aplicado e pode ser desfeito depois."); break; }
            lines.Add($"Etapa {(int)run.Step} ({AssistantSteps.Info(run.Step).ShortName}): {r.Message}");
            if (r.FullyRestored == true || (r.Ok && r.FullyRestored is null)) state.Runs[i] = run with { Reverted = true };
        }
        return lines;
    }
}
