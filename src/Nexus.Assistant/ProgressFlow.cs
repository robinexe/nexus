using System.Globalization;
using Nexus.Broker;

namespace Nexus.Assistant;

/// <summary>Resumo de uma etapa terminada para a tela de progresso e o aviso da etapa seguinte ("9,3 GB movidos • 12 s").</summary>
public sealed record ProcessSummary(string Text, ProcessTaskStatus Status)
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// Bom (Done) quando nada foi recusado e algo foi feito ou não precisou; Failed quando tudo foi recusado; Skipped quando
    /// foi cancelado ou não havia nada.
    /// </summary>
    public static ProcessSummary ForApply(StepApplyResult result, double movedBytes, TimeSpan took)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parts = new List<string>();
        if (movedBytes >= 1) parts.Add($"{StepPlanners.Bytes((long)movedBytes)} {(result.Applied == 1 ? "movido" : "movidos")} para a quarentena");
        else if (result.Applied > 0) parts.Add(result.Applied == 1 ? "1 feita" : string.Create(Pt, $"{result.Applied} feitas"));
        if (result.Already > 0) parts.Add(result.Already == 1 ? "1 já estava assim" : string.Create(Pt, $"{result.Already} já estavam assim"));
        if (result.NotNeeded > 0) parts.Add(result.NotNeeded == 1 ? "1 não precisou" : string.Create(Pt, $"{result.NotNeeded} não precisaram"));
        if (result.Refused > 0) parts.Add(result.Refused == 1 ? "1 não aplicada" : string.Create(Pt, $"{result.Refused} não aplicadas"));
        if (result.Canceled > 0) parts.Add(result.Canceled == 1 ? "1 cancelada" : string.Create(Pt, $"{result.Canceled} canceladas"));
        if (parts.Count == 0) parts.Add("Nada aplicado");
        parts.Add(ProcessText.Duration(took));
        var good = result.Applied + result.Already + result.NotNeeded;
        var status = result.Refused > 0 && good == 0 ? ProcessTaskStatus.Failed
            : good == 0 ? ProcessTaskStatus.Skipped
            : ProcessTaskStatus.Done;
        return new ProcessSummary(string.Join(" • ", parts), status);
    }

    public static ProcessSummary ForScan(int facts, int changes, TimeSpan took) =>
        new(string.Join(" • ",
            facts == 1 ? "1 item lido" : string.Create(Pt, $"{facts} itens lidos"),
            changes == 0 ? "nada a mudar" : changes == 1 ? "1 sugestão" : string.Create(Pt, $"{changes} sugestões"),
            ProcessText.Duration(took)), ProcessTaskStatus.Done);
}

/// <summary>
/// Liga a aplicação de uma etapa (<see cref="AssistantRunner.ApplyAsync"/>) à tela de progresso: cada lote vira uma linha com
/// as mudanças dentro; os marcos reais (confirmação, aviso do Windows, aplicando, relendo) mudam o texto; dentro de "aplicando"
/// o texto diz o que provavelmente está acontecendo pelo tempo típico (marcado como estimativa); no fim de cada lote entram
/// os resultados de verdade, item a item.
/// </summary>
public sealed class ApplyProgressObserver : IApplyObserver
{
    private readonly ProcessTracker _tracker;
    private readonly Dictionary<string, ApplyBatch> _batches = [];
    private ApplyBatch? _running;
    private TimeSpan _runningSince;

    public ApplyProgressObserver(ProcessTracker tracker, AssistantStepId step)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        Step = step;
    }

    public AssistantStepId Step { get; }

    /// <summary>Bytes das limpezas aplicadas de verdade (o tamanho medido antes de cada item).</summary>
    public double MovedBytes { get; private set; }

    public void NotNeeded(ProposedChange change, string why)
    {
        ArgumentNullException.ThrowIfNull(change);
        var t = _tracker.Add("not-needed:" + change.Id, change.Title, 0.3);
        _tracker.Finish(t, ProcessTaskStatus.Skipped, why);
    }

    public void Planned(IReadOnlyList<ApplyBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        // Replanejado (ex.: o ponto de restauração saiu porque já havia um recente): o que não existe mais sai do feed.
        var keys = batches.Select(ProcessTimeModel.BatchKey).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _batches.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            if (_tracker.Find(stale) is { Status: ProcessTaskStatus.Pending } old) _tracker.Remove(old);
            _batches.Remove(stale);
        }
        foreach (var b in batches)
        {
            var key = ProcessTimeModel.BatchKey(b);
            _batches[key] = b;
            var t = _tracker.Find(key) ?? _tracker.Add(key, ProcessTimeModel.BatchTitle(b), ProcessTimeModel.BatchSeconds(b));
            if (t.Status != ProcessTaskStatus.Pending) continue;
            t.Title = ProcessTimeModel.BatchTitle(b);
            _tracker.SetExpected(t, ProcessTimeModel.BatchSeconds(b));
            _tracker.SetItems(t, b.Changes.Select(c => new ProcessItem(c.Title, ProcessTaskStatus.Pending)));
        }
    }

    public void Stage(ApplyBatch batch, ApplyStage stage)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_tracker.Find(ProcessTimeModel.BatchKey(batch)) is not { } t) return;
        switch (stage)
        {
            case ApplyStage.WaitingConfirmation:
                _tracker.Wait(t, batch.Level == BrokerLevel.Admin ? "Aguardando a sua confirmação (mudanças de sistema)…" : "Aguardando a sua confirmação…");
                break;
            case ApplyStage.Confirmed:
                _tracker.Resume(t, "Iniciando o aplicador do NEXUS…");
                break;
            case ApplyStage.StartingApplier:
                if (t.Waiting) _tracker.Resume(t);
                _tracker.SetText(t, "Iniciando o aplicador do NEXUS…");
                break;
            case ApplyStage.WaitingWindows:
                _tracker.Wait(t, "Confirme o aviso do Windows para aplicar como administrador…");
                break;
            case ApplyStage.Running:
                if (t.Waiting) _tracker.Resume(t);
                _running = batch;
                _runningSince = _tracker.ActiveElapsed;
                _tracker.SetText(t, ProcessTimeModel.BatchText(batch, 0), estimate: true);
                break;
            case ApplyStage.Refreshing:
                _running = null;
                _tracker.SetText(t, "Conferindo o resultado e relendo o PC…");
                break;
        }
    }

    /// <summary>A cada quadro: atualiza o "provavelmente agora" do lote que está no aplicador.</summary>
    public void Tick()
    {
        if (_running is not { } b || _tracker.Find(ProcessTimeModel.BatchKey(b)) is not { Waiting: false } t) return;
        _tracker.SetText(t, ProcessTimeModel.BatchText(b, (_tracker.ActiveElapsed - _runningSince).TotalSeconds), estimate: true);
    }

    public void Finished(ApplyBatch batch, IReadOnlyList<ChangeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(records);
        if (ReferenceEquals(_running, batch)) _running = null;
        if (_tracker.Find(ProcessTimeModel.BatchKey(batch)) is not { } t) return;
        int ok = 0, neutral = 0, bad = 0, canceled = 0;
        var items = new List<ProcessItem>();
        foreach (var r in records)
        {
            switch (r.Outcome)
            {
                case ChangeOutcome.Applied:
                    ok++;
                    items.Add(new ProcessItem(r.Title, ProcessTaskStatus.Done, r.After is { Length: > 0 } a ? a : "feito"));
                    if (r.Step == AssistantStepId.Cleaning) MovedBytes += ProcessTimeModel.BytesOf(r.Before);
                    break;
                case ChangeOutcome.AlreadyConfigured:
                    neutral++;
                    items.Add(new ProcessItem(r.Title, ProcessTaskStatus.Skipped, NeutralText(r)));
                    break;
                case ChangeOutcome.Canceled:
                    canceled++;
                    items.Add(new ProcessItem(r.Title, ProcessTaskStatus.Skipped, "cancelada"));
                    break;
                default:
                    bad++;
                    items.Add(new ProcessItem(r.Title, ProcessTaskStatus.Failed, r.Detail));
                    break;
            }
        }
        _tracker.SetItems(t, items);
        var parts = new List<string>();
        if (ok > 0) parts.Add(ok == 1 ? "1 feita" : string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{ok} feitas"));
        if (neutral > 0) parts.Add(neutral == 1 ? "1 não precisou" : string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{neutral} não precisaram"));
        if (bad > 0) parts.Add(bad == 1 ? "1 não aplicada" : string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{bad} não aplicadas"));
        if (canceled > 0) parts.Add(canceled == 1 ? "1 cancelada" : string.Create(CultureInfo.GetCultureInfo("pt-BR"), $"{canceled} canceladas"));
        var status = ok + neutral > 0 ? ProcessTaskStatus.Done : canceled > 0 && bad == 0 ? ProcessTaskStatus.Skipped : ProcessTaskStatus.Failed;
        parts.Add(ProcessText.Duration(_tracker.Elapsed - t.WallStart));
        _tracker.Finish(t, status, string.Join(" · ", parts));
    }

    /// <summary>"Já existe um ponto de restauração recente…" quando o motivo é esse; senão "já estava assim".</summary>
    private static string NeutralText(ChangeRecord r) =>
        r.Detail is { } d && d.StartsWith("Já", StringComparison.Ordinal) ? d : "já estava assim";
}
