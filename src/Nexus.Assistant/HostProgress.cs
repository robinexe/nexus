using System.Globalization;
using Nexus.Broker;

namespace Nexus.Assistant;

/// <summary>
/// Andamento de UM pedido ao aplicador feito por qualquer página (aplicar ou desfazer), para a tela de progresso da janela.
/// Linhas: sua confirmação → aviso do Windows (administrador) → ponto de restauração (administrador) → aplicando N ações
/// (com os nomes; o "provável agora" é estimativa pelo tempo típico) → conferindo. Os resultados de cada ação entram de
/// verdade quando o aplicador responde. Sem WPF; testável.
/// </summary>
public sealed class HostApplyProgress
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly ProcessTracker _tracker;
    private readonly IReadOnlyList<(string Key, string Name)> _actions;
    private readonly Func<string, string> _name;
    private readonly ProcessTask _confirm;
    private readonly ProcessTask? _windows;
    private readonly ProcessTask? _restore;
    private readonly ProcessTask _work;
    private readonly ProcessTask _check;
    private bool _running;
    private TimeSpan _runningSince;
    private BrokerCallResult? _result;

    /// <param name="actions">Pedido na ordem: chave da ação (id@versão) e nome para pessoas (título do catálogo).</param>
    /// <param name="name">Nome de uma chave que vier nos resultados (desfazer não sabe as ações de antemão).</param>
    public HostApplyProgress(ProcessTracker tracker, IReadOnlyList<(string Key, string Name)> actions, BrokerLevel level, bool revert = false, Func<string, string>? name = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        var names = actions.GroupBy(a => a.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        _name = k => names.TryGetValue(k, out var n) ? n : name?.Invoke(k) ?? k;
        Level = level;
        IsRevert = revert;
        _confirm = tracker.Add("confirm", "Sua confirmação", 0.3);
        if (level == BrokerLevel.Admin) _windows = tracker.Add("windows", "Aviso do Windows (administrador)", 0.3);
        if (level == BrokerLevel.Admin && !revert) _restore = tracker.Add("restore", "Ponto de restauração do Windows", 0.3);
        _work = tracker.Add("work", WorkTitle(actions.Count, revert), WorkSeconds(actions, level, revert));
        if (!revert) tracker.SetItems(_work, actions.Select(a => new ProcessItem(a.Name, ProcessTaskStatus.Pending)));
        _check = tracker.Add("check", "Conferindo e relendo o PC", ProcessTimeModel.Refresh);
    }

    public BrokerLevel Level { get; }
    public bool IsRevert { get; }
    public ProcessTracker Tracker => _tracker;

    public static string WorkTitle(int count, bool revert) =>
        revert ? "Desfazendo a execução" : count == 1 ? "Aplicando 1 ação" : string.Create(Pt, $"Aplicando {count} ações");

    /// <summary>Peso da parte do aplicador: partida + ponto automático (administrador, ao aplicar) + cada ação.</summary>
    public static double WorkSeconds(IReadOnlyList<(string Key, string Name)> actions, BrokerLevel level, bool revert)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return ProcessTimeModel.StartApplier + (level == BrokerLevel.Admin && !revert ? ProcessTimeModel.AdminRestorePoint : 0) +
               (revert ? 4 : actions.Sum(a => ActionSeconds(a.Key)));
    }

    public static double ActionSeconds(string key) =>
        key == StepPlanners.DriverInstallAction ? 45 : key == StepPlanners.RestorePointAction ? 6 : 0.7;

    public void Stage(ApplyStage stage)
    {
        switch (stage)
        {
            case ApplyStage.WaitingConfirmation:
                _tracker.Wait(_confirm, "Aguardando a sua confirmação…");
                break;
            case ApplyStage.Confirmed:
                _tracker.Finish(_confirm, ProcessTaskStatus.Done, "confirmado");
                _tracker.Start(_work, "Iniciando o aplicador do NEXUS…");
                break;
            case ApplyStage.StartingApplier:
                _tracker.SetText(_work, "Iniciando o aplicador do NEXUS…");
                break;
            case ApplyStage.WaitingWindows when _windows is not null:
                _tracker.Wait(_windows, "Confirme o aviso do Windows para aplicar como administrador…");
                break;
            case ApplyStage.Running:
                if (_windows is { } w)
                {
                    if (w.Status == ProcessTaskStatus.Running) _tracker.Finish(w, ProcessTaskStatus.Done, "permitido");
                    else if (w.Status == ProcessTaskStatus.Pending) _tracker.Finish(w, ProcessTaskStatus.Skipped, "não precisou: o aplicador já estava aberto");
                }
                if (_work.Status == ProcessTaskStatus.Pending) _tracker.Start(_work);
                if (_work.Waiting) _tracker.Resume(_work);
                _running = true;
                _runningSince = _tracker.ActiveElapsed;
                _tracker.SetText(_work, EstimateText(0), estimate: true);
                break;
            case ApplyStage.Refreshing:
                _running = false;
                if (!_work.IsFinished) _tracker.Finish(_work, ProcessTaskStatus.Done);
                _tracker.Start(_check, "Conferindo o resultado e relendo o PC…");
                break;
        }
    }

    /// <summary>A cada quadro: o que provavelmente está acontecendo dentro do aplicador (estimativa pela ordem e pelo tempo típico).</summary>
    public void Tick()
    {
        if (!_running || _work.Waiting || _work.IsFinished) return;
        _tracker.SetText(_work, EstimateText((_tracker.ActiveElapsed - _runningSince).TotalSeconds), estimate: true);
    }

    public string EstimateText(double runningSeconds)
    {
        var t = Math.Max(0, runningSeconds);
        if (IsRevert) return "Desfazendo pelo diário: cada item volta ao valor registrado antes da mudança…";
        if (_restore is not null)
        {
            if (t < ProcessTimeModel.AdminRestorePoint) return "Criando um ponto de restauração do Windows antes (costuma levar de 20 a 60 s)…";
            t -= ProcessTimeModel.AdminRestorePoint;
        }
        foreach (var a in _actions)
        {
            var s = ActionSeconds(a.Key);
            if (t < s) return $"Aplicando: {a.Name}…";
            t -= s;
        }
        return _actions.Count == 0 ? "Aplicando…" : "Conferindo cada item com uma releitura…";
    }

    /// <summary>O aplicador respondeu: entra o resultado de cada ação (e do ponto de restauração, pela mensagem dele).</summary>
    public void Finished(BrokerCallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        _running = false;
        if (_windows is { Status: ProcessTaskStatus.Running }) _tracker.Finish(_windows, ProcessTaskStatus.Done, "permitido");
        if (_confirm is { Status: ProcessTaskStatus.Running }) _tracker.Finish(_confirm, ProcessTaskStatus.Done, "confirmado");
        if (result.Response is not { Ok: true } r)
        {
            if (_windows is { Status: ProcessTaskStatus.Pending }) _tracker.Finish(_windows, ProcessTaskStatus.Skipped, "não chegou a ser pedido");
            if (_restore is not null) _tracker.Finish(_restore, ProcessTaskStatus.Skipped, "nada foi aplicado");
            _tracker.Finish(_work, ProcessTaskStatus.Failed, IsRevert ? ApplySummary.DescribeRevert(result) : ApplySummary.Describe(result));
            return;
        }
        if (_restore is not null)
        {
            var msg = r.Message ?? "";
            var (status, text) = msg.Contains("Ponto de restauração do Windows criado", StringComparison.Ordinal) ? (ProcessTaskStatus.Done, "criado antes de aplicar")
                : msg.Contains("Ponto de restauração não criado", StringComparison.Ordinal) ? (ProcessTaskStatus.Skipped, Clip(msg[msg.IndexOf("Ponto de restauração não criado", StringComparison.Ordinal)..]))
                : (ProcessTaskStatus.Skipped, "não precisou: já criado antes nesta sessão do aplicador");
            _tracker.Finish(_restore, status, text);
        }
        var items = (r.Results ?? []).Select(x => Item(x)).ToList();
        _tracker.SetItems(_work, items);
        var parts = (IsRevert ? RevertCounts(r.Results ?? []) : ApplySummary.Counts(r.Results ?? [])).ToList();
        var bad = items.Count(i => i.Status == ProcessTaskStatus.Failed);
        var good = items.Count - bad;
        _tracker.Finish(_work, bad > 0 && good == 0 ? ProcessTaskStatus.Failed : ProcessTaskStatus.Done, parts.Count == 0 ? "nada a fazer" : string.Join(" · ", parts));
    }

    /// <summary>O usuário disse não na confirmação: nada foi enviado.</summary>
    public void Canceled()
    {
        _tracker.Finish(_confirm, ProcessTaskStatus.Skipped, "cancelado por você");
        _tracker.Complete();
    }

    /// <summary>Releitura do PC terminou: fecha o andamento.</summary>
    public void Done()
    {
        if (_check.Status == ProcessTaskStatus.Running) _tracker.Finish(_check, ProcessTaskStatus.Done, "pronto");
        _tracker.Complete();
    }

    public ProcessSummary Summary()
    {
        var took = ProcessText.Duration(_tracker.Elapsed);
        if (_result is not { } r) return new ProcessSummary($"Nada foi enviado • {took}", ProcessTaskStatus.Skipped);
        if (r.Response is not { Ok: true } resp)
            return new ProcessSummary((IsRevert ? ApplySummary.DescribeRevert(r) : ApplySummary.Describe(r)).TrimEnd('.'), ProcessTaskStatus.Failed);
        var results = resp.Results ?? [];
        var parts = (IsRevert ? RevertCounts(results) : ApplySummary.Counts(results)).Select(p => p.Replace(" (motivo na página Aplicar)", "", StringComparison.Ordinal)).ToList();
        if (parts.Count == 0) parts.Add("Nada a fazer");
        parts.Add(took);
        var items = results.Select(Item).ToList();
        var bad = items.Count(i => i.Status == ProcessTaskStatus.Failed);
        var status = bad > 0 && bad == items.Count ? ProcessTaskStatus.Failed : IsRevert && resp.FullyRestored == false ? ProcessTaskStatus.Failed : ProcessTaskStatus.Done;
        return new ProcessSummary(string.Join(" • ", parts), status);
    }

    private ProcessItem Item(ActionResultDto x)
    {
        var name = _name(x.ActionKey);
        return x.Outcome switch
        {
            "Verified" => new(name, ProcessTaskStatus.Done, "feito e conferido"),
            "Reverted" => new(name, ProcessTaskStatus.Done, "voltou ao que era"),
            "AlreadyConfigured" => new(name, ProcessTaskStatus.Skipped, "já estava assim"),
            "AlreadyOriginal" => new(name, ProcessTaskStatus.Skipped, "já estava como antes"),
            "NothingToRevert" => new(name, ProcessTaskStatus.Skipped, "nada a desfazer"),
            "NotApplicable" => new(name, ProcessTaskStatus.Skipped, "não se aplica a este PC"),
            _ when ApplySummary.IsRecentRestorePointRefusal(x.Detail) => new(name, ProcessTaskStatus.Skipped, "não precisou: já existe um ponto recente"),
            _ => new(name, ProcessTaskStatus.Failed, AssistantRunner.OutcomeText(x.Outcome) + (string.IsNullOrWhiteSpace(x.Detail) ? "" : ": " + x.Detail)),
        };
    }

    private static List<string> RevertCounts(IReadOnlyList<ActionResultDto> results)
    {
        int back = results.Count(x => x.Outcome == "Reverted");
        int same = results.Count(x => x.Outcome is "AlreadyOriginal" or "NothingToRevert");
        int bad = results.Count - back - same;
        var list = new List<string>();
        if (back > 0) list.Add(back == 1 ? "1 desfeita" : string.Create(Pt, $"{back} desfeitas"));
        if (same > 0) list.Add(same == 1 ? "1 já estava como antes" : string.Create(Pt, $"{same} já estavam como antes"));
        if (bad > 0) list.Add(bad == 1 ? "1 não voltou" : string.Create(Pt, $"{bad} não voltaram"));
        return list;
    }

    private static string Clip(string s) => s.Length <= 140 ? s : s[..139] + "…";
}
