namespace Nexus.Measurement.AbLab;

/// <summary>
/// Máquina de estados do teste A/B (funções puras: estado → novo estado). A interface pergunta <see cref="CommandFor"/>,
/// executa (aplicar/desfazer pelo aplicador, comparar, perguntar) e devolve o resultado pela função correspondente. Partidas
/// medidas entram por <see cref="Offer"/>. Ordem: A → aplicar → B → [A/B/A: desfazer → A2] → comparar → manter ou desfazer.
/// </summary>
public static class AbLabMachine
{
    public static AbTestState Start(string id, string gameExe, string gameName, AbCandidate candidate, AbDesign design, bool autoUndo, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameExe);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Actions.Count == 0) throw new ArgumentException("O ajuste não tem ações do catálogo.", nameof(candidate));
        return new AbTestState
        {
            Id = id,
            GameKey = Measurement.GameKey.Of(gameExe),
            GameName = string.IsNullOrWhiteSpace(gameName) ? gameExe : gameName,
            Candidate = candidate,
            Design = design,
            AutoUndo = autoUndo,
            Step = AbStep.WaitBaseline,
            StepSince = now,
            StartedAt = now,
        };
    }

    public static AbCommand CommandFor(AbTestState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Step switch
        {
            AbStep.Applying or AbStep.Reapplying => AbCommand.Apply,
            AbStep.Reverting or AbStep.Undoing => AbCommand.Revert,
            AbStep.Deciding => AbCommand.Decide,
            AbStep.AskUser => AbCommand.Ask,
            _ => AbCommand.None,
        };
    }

    /// <summary>Esperando o usuário jogar.</summary>
    public static bool IsWaitingForPlay(AbTestState s) => s.Step is AbStep.WaitBaseline or AbStep.WaitTreatment or AbStep.WaitBaseline2;

    /// <summary>O ajuste está aplicado agora (por este teste).</summary>
    public static bool TweakApplied(AbTestState s) =>
        s.Runs.Count > 0 && s.Step is AbStep.WaitTreatment or AbStep.Reverting or AbStep.Deciding or AbStep.AskUser or AbStep.Undoing
        || s.Step == AbStep.Done && s.Outcome is AbOutcome.Kept or AbOutcome.KeptByUser;

    /// <summary>Cancelar só fora dos passos em que o aplicador está trabalhando.</summary>
    public static bool CanCancel(AbTestState s) => s.Step is AbStep.WaitBaseline or AbStep.WaitTreatment or AbStep.WaitBaseline2 or AbStep.AskUser;

    /// <summary>Quantas partidas o desenho pede.</summary>
    public static int PlaysNeeded(AbDesign d) => d == AbDesign.ABA ? 3 : 2;

    /// <summary>Qual partida está sendo esperada (1, 2 ou 3), ou a última já jogada.</summary>
    public static int PlayIndex(AbTestState s) => s.Step switch
    {
        AbStep.WaitBaseline or AbStep.Applying => 1,
        AbStep.WaitTreatment or AbStep.Reverting => 2,
        AbStep.WaitBaseline2 => 3,
        _ => PlaysNeeded(s.Design),
    };

    /// <summary>
    /// Por que a partida não serve para o passo atual (vazio = serve). Mesmo jogo, começou depois do passo, não excluída,
    /// duração e quadros mínimos, e as condições que mudam o FPS sozinhas iguais às da primeira partida aceita.
    /// </summary>
    public static IReadOnlyList<AbRejection> Check(AbTestState s, MeasuredSession session, AbRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(session);
        rules ??= AbRules.Default;
        if (session.Game != s.GameKey) return [AbRejection.OtherGame];
        if (!IsWaitingForPlay(s)) return [AbRejection.NotWaiting];
        if (session.Id == s.Baseline || session.Id == s.Treatment || session.Id == s.Baseline2) return [AbRejection.Duplicate];
        var r = new List<AbRejection>();
        if (session.CapturedAt < s.StepSince) r.Add(AbRejection.StartedBeforeStep);
        if (session.Excluded) r.Add(AbRejection.Excluded);
        if (session.DurationSeconds < rules.MinSeconds) r.Add(AbRejection.TooShort);
        if (session.Summary.Frames < rules.MinFrames) r.Add(AbRejection.TooFewFrames);
        var a = s.Reference;
        var b = session.Conditions;
        if (Differs(a?.Resolution, b?.Resolution)) r.Add(AbRejection.ResolutionChanged);
        if (a?.OnBattery is { } x && b?.OnBattery is { } y && x != y) r.Add(AbRejection.BatteryChanged);
        if (Differs(a?.GpuDriver, b?.GpuDriver)) r.Add(AbRejection.DriverChanged);
        return r;
    }

    private static bool Differs(string? x, string? y) =>
        !string.IsNullOrWhiteSpace(x) && !string.IsNullOrWhiteSpace(y) && !x.Equals(y, StringComparison.OrdinalIgnoreCase);

    /// <summary>Uma partida medida terminou. Aceita (avança) ou devolve os motivos para não usar (estado igual).</summary>
    public static (AbTestState State, IReadOnlyList<AbRejection> Rejections) Offer(AbTestState s, MeasuredSession session, DateTimeOffset now, AbRules? rules = null)
    {
        var reasons = Check(s, session, rules);
        if (reasons.Count > 0) return (s, reasons);
        var reference = s.Reference ?? session.Conditions;
        var next = s.Step switch
        {
            AbStep.WaitBaseline => s with { Baseline = session.Id, Step = AbStep.Applying, StepSince = now },
            AbStep.WaitTreatment => s with
            {
                Treatment = session.Id,
                Step = s.Design == AbDesign.ABA ? AbStep.Reverting : AbStep.Deciding,
                StepSince = now,
            },
            _ => s with { Baseline2 = session.Id, Step = AbStep.Deciding, StepSince = now },
        };
        return (next with { Reference = reference }, []);
    }

    /// <summary>Resultado de aplicar (passo Applying ou Reapplying).</summary>
    /// <param name="already">Já estava assim: nada mudou (o teste não tem o que comparar).</param>
    public static AbTestState Applied(AbTestState s, bool ok, bool already, IReadOnlyList<AbRunRef> runs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(runs);
        if (s.Step == AbStep.Reapplying)
        {
            return ok
                ? s with { Step = AbStep.Done, Outcome = AbOutcome.Kept, Runs = runs, StepSince = now }
                : s with { Step = runs.Count > 0 ? AbStep.Undoing : AbStep.Done, Outcome = runs.Count > 0 ? AbOutcome.None : AbOutcome.Failed,
                    Failure = AbFailure.ReapplyFailed, Runs = runs, StepSince = now };
        }
        if (s.Step != AbStep.Applying) return s;
        if (!ok)
            return runs.Count > 0
                ? s with { Step = AbStep.Undoing, Failure = AbFailure.ApplyFailed, Runs = runs, StepSince = now }
                : s with { Step = AbStep.Done, Outcome = AbOutcome.Failed, Failure = AbFailure.ApplyFailed, StepSince = now };
        if (already || runs.Count == 0)
            return s with { Step = AbStep.Done, Outcome = AbOutcome.AlreadyOn, StepSince = now };
        return s with { Step = AbStep.WaitTreatment, Runs = runs, StepSince = now };
    }

    /// <summary>Resultado de desfazer (passo Reverting ou Undoing).</summary>
    public static AbTestState Reverted(AbTestState s, bool ok, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!ok && s.Step is AbStep.Reverting or AbStep.Undoing)
            return s with { Step = AbStep.Done, Outcome = AbOutcome.Failed, Failure = AbFailure.RevertFailed, StepSince = now };
        return s.Step switch
        {
            AbStep.Reverting => s with { Step = AbStep.WaitBaseline2, Runs = [], StepSince = now },
            AbStep.Undoing => s with
            {
                Step = AbStep.Done,
                Runs = [],
                StepSince = now,
                Outcome = s.CancelRequested ? AbOutcome.Cancelled : s.Failure != AbFailure.None ? AbOutcome.Failed : AbOutcome.Undone,
            },
            _ => s,
        };
    }

    /// <summary>A comparação terminou: manter (A/B: nada a fazer; A/B/A: aplicar de novo) ou desfazer (sozinho ou perguntando).</summary>
    public static AbTestState Decided(AbTestState s, AbJudgement judgement, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(judgement);
        if (s.Step != AbStep.Deciding) return s;
        var d = s with { Judgement = judgement, StepSince = now };
        if (judgement.Keep)
            return s.Design == AbDesign.ABA ? d with { Step = AbStep.Reapplying } : d with { Step = AbStep.Done, Outcome = AbOutcome.Kept };
        if (s.Design == AbDesign.ABA) // a última partida já foi sem o ajuste: ele já está desfeito
        {
            if (s.AutoUndo) return d with { Step = AbStep.Done, Outcome = AbOutcome.Undone };
            return d with { Step = AbStep.AskUser };
        }
        return s.AutoUndo ? d with { Step = AbStep.Undoing } : d with { Step = AbStep.AskUser };
    }

    /// <summary>
    /// Resposta à pergunta "manter mesmo assim?". A/B: sim = fica como está; não = desfazer. A/B/A: o ajuste já está desfeito,
    /// então sim = aplicar de novo; não = termina desfeito.
    /// </summary>
    public static AbTestState Answer(AbTestState s, bool keep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Step != AbStep.AskUser) return s;
        if (s.Design == AbDesign.ABA)
            return keep ? s with { Step = AbStep.Reapplying, StepSince = now, CancelRequested = false }
                        : s with { Step = AbStep.Done, Outcome = AbOutcome.Undone, StepSince = now };
        return keep ? s with { Step = AbStep.Done, Outcome = AbOutcome.KeptByUser, StepSince = now }
                    : s with { Step = AbStep.Undoing, StepSince = now };
    }

    /// <summary>Cancelar: se o ajuste está aplicado por este teste, ele é desfeito (combinado no começo); senão, termina.</summary>
    public static AbTestState Cancel(AbTestState s, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!CanCancel(s)) return s;
        if (s.Runs.Count > 0 && s.Step is AbStep.WaitTreatment or AbStep.AskUser)
            return s with { Step = AbStep.Undoing, CancelRequested = true, StepSince = now };
        return s with { Step = AbStep.Done, Outcome = AbOutcome.Cancelled, StepSince = now };
    }

    /// <summary>
    /// Ao reabrir o NEXUS: um passo de aplicar que ficou no meio não tem resultado conhecido (o aplicador pode ou não ter gravado)
    /// → para, e o usuário confere no Histórico. Desfazer pode ser repetido com segurança (desfazer o já desfeito não muda nada).
    /// </summary>
    public static AbTestState Resume(AbTestState s, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Step is AbStep.Applying or AbStep.Reapplying
            ? s with { Step = AbStep.Done, Outcome = AbOutcome.Failed, Failure = AbFailure.Interrupted, StepSince = now }
            : s;
    }
}

/// <summary>Fila e histórico: ao terminar um teste, ele vai para o histórico e o próximo ajuste da fila começa (mesmo jogo e opções).</summary>
public static class AbLabFlow
{
    public const int MaxHistory = 200;

    public static AbLabData Begin(AbLabData data, string gameExe, string gameName, IReadOnlyList<AbCandidate> candidates, AbDesign design, bool autoUndo,
        DateTimeOffset now, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(newId);
        if (data.Active is { Step: not AbStep.Done }) throw new InvalidOperationException("Já há um teste em andamento.");
        if (candidates.Count == 0) throw new ArgumentException("Escolha pelo menos um ajuste.", nameof(candidates));
        var first = AbLabMachine.Start(newId(), gameExe, gameName, candidates[0], design, autoUndo, now);
        return data with { Active = first, Queue = candidates.Skip(1).ToList() };
    }

    /// <summary>Teste terminado → histórico; próximo da fila (se houver e se o teste não foi cancelado nem falhou) começa agora.</summary>
    public static AbLabData Advance(AbLabData data, DateTimeOffset now, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(newId);
        if (data.Active is not { Step: AbStep.Done } done) return data;
        var history = data.History.Append(AbResult.From(done, now)).TakeLast(MaxHistory).ToList();
        var stop = done.Outcome is AbOutcome.Cancelled or AbOutcome.Failed;
        if (stop || data.Queue.Count == 0) return data with { Active = null, Queue = [], History = history };
        var next = AbLabMachine.Start(newId(), done.GameKey, done.GameName, data.Queue[0], done.Design, done.AutoUndo, now);
        return data with { Active = next, Queue = data.Queue.Skip(1).ToList(), History = history };
    }

    /// <summary>Último resultado de cada ajuste testado neste jogo (mais recente primeiro).</summary>
    public static IReadOnlyList<AbResult> LatestFor(IEnumerable<AbResult> history, string gameExe)
    {
        ArgumentNullException.ThrowIfNull(history);
        var key = GameKey.Of(gameExe);
        return history.Where(h => h.GameKey == key && h.Outcome is not (AbOutcome.Cancelled or AbOutcome.None))
            .GroupBy(h => h.CandidateId, StringComparer.Ordinal).Select(g => g.MaxBy(h => h.At)!)
            .OrderByDescending(h => h.At).ToList();
    }
}
