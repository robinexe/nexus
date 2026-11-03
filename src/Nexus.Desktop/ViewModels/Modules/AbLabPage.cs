using System.Collections.ObjectModel;
using System.IO;
using Nexus.Broker;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.HardwareDiscovery;
using Nexus.Measurement;
using Nexus.Measurement.AbLab;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Laboratório A/B ("Comparar com e sem"): prova, NESTE PC, se um ajuste ajuda num jogo. O usuário escolhe o jogo e o ajuste;
/// joga uma partida sem (A), o NEXUS aplica pelo aplicador, joga com (B) e, se pediu mais certeza, mais uma sem (A de novo).
/// As partidas são as mesmas que o Medir grava sozinho (PresentMon); a lógica (regras, veredito, máquina de estados, fila e
/// histórico) é <see cref="AbLabMachine"/>/<see cref="AbLabJudge"/>/<see cref="AbLabFlow"/> em Nexus.Measurement, sem WPF.
/// Esta página só executa os comandos da máquina (aplicar/desfazer por <see cref="IModuleHost.ApplyConfirmedAsync"/> e
/// <see cref="IModuleHost.RevertWithoutConfirmationAsync"/>, com o consentimento dado no começo) e mostra tudo em palavras
/// simples. O estado é gravado a cada passo (<see cref="AbLabStore"/>) para sobreviver a fechar o NEXUS.
/// </summary>
public sealed class AbLabPage : ModulePage
{
    private readonly AbLabStore _store;
    private AbLabData _data = AbLabData.Empty;
    private IReadOnlyList<AbGameChoice> _games = [];
    private AbGameChoice? _selectedGame;
    private bool _aba, _askAtEnd, _running, _loadingGames;
    private string _lastNote = "";
    private AbResultView? _result;
    private IReadOnlyList<AbHistoryRow> _history = [];
    private bool _subscribed;

    public AbLabPage(MeasurePage measure, SimpleGamesPage games, QuickTweaksPanel tweaks) : this(measure, games, tweaks, new AbLabStore(AbLabStore.DefaultPath)) { }

    internal AbLabPage(MeasurePage measure, SimpleGamesPage games, QuickTweaksPanel tweaks, AbLabStore store)
    {
        Measure = measure ?? throw new ArgumentNullException(nameof(measure));
        Games = games ?? throw new ArgumentNullException(nameof(games));
        Tweaks = tweaks ?? throw new ArgumentNullException(nameof(tweaks));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        StartCommand = new RelayCommand(() => _ = RunAsync(StartAsync), () => !Busy && !_running && !HasActive && SelectedGame is not null && SelectedGame.Candidates.Any(c => c.IsChecked));
        CancelCommand = new RelayCommand(() => _ = RunAsync(CancelAsync), () => !_running && Active is { } a && AbLabMachine.CanCancel(a));
        KeepCommand = new RelayCommand(() => _ = RunAsync(() => AnswerAsync(true)), () => !_running && Active is { Step: AbStep.AskUser });
        UndoCommand = new RelayCommand(() => _ = RunAsync(() => AnswerAsync(false)), () => !_running && Active is { Step: AbStep.AskUser });
        DismissResultCommand = new RelayCommand(() => { Result = null; OnPropertyChanged(nameof(ShowSetup)); });
        RefreshGamesCommand = new RelayCommand(() => _ = LoadGamesAsync(), () => !_loadingGames);
    }

    public override string Title => Loc.T("Ab.Title");
    public override string Group => Loc.T("Nav.Medir");
    public override string Subtitle => Loc.T("Ab.Subtitle");

    public MeasurePage Measure { get; }
    public SimpleGamesPage Games { get; }
    public QuickTweaksPanel Tweaks { get; }

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand KeepCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand DismissResultCommand { get; }
    public RelayCommand RefreshGamesCommand { get; }

    // ------------------------------------------------------------------ escolha (jogo + ajuste + desenho)

    public IReadOnlyList<AbGameChoice> GameChoices { get => _games; private set { if (Set(ref _games, value)) { OnPropertyChanged(nameof(HasGames)); OnPropertyChanged(nameof(NoGames)); } } }
    public AbGameChoice? SelectedGame { get => _selectedGame; set { if (Set(ref _selectedGame, value)) { OnPropertyChanged(nameof(HasCandidates)); OnPropertyChanged(nameof(NoCandidates)); } } }
    public bool IsLoadingGames { get => _loadingGames; private set { if (Set(ref _loadingGames, value)) { OnPropertyChanged(nameof(HasGames)); OnPropertyChanged(nameof(NoGames)); } } }
    public bool HasGames => !_loadingGames && _games.Count > 0;
    public bool NoGames => !_loadingGames && _games.Count == 0;
    public bool HasCandidates => _selectedGame is { Candidates.Count: > 0 };
    public bool NoCandidates => _selectedGame is { Candidates.Count: 0 };
    /// <summary>A/B/A: três partidas (sem, com, sem) — controla a variação natural (aquecimento, cena, horário).</summary>
    public bool UseAba { get => _aba; set => Set(ref _aba, value); }
    /// <summary>Sem desfazer automático: no fim, pergunta "manter mesmo assim?".</summary>
    public bool AskAtEnd { get => _askAtEnd; set => Set(ref _askAtEnd, value); }
    public string PlaysText => Loc.F("Ab.Setup.Plays", AbLabMachine.PlaysNeeded(UseAba ? AbDesign.ABA : AbDesign.AB), AbLabText.MinMinutes());
    public bool ShowSetup => !HasActive && Result is null;
    /// <summary>Sem PresentMon o Medir não grava partidas: o teste não tem com o que trabalhar.</summary>
    public bool MissingPresentMon => !Measure.HasPresentMon;

    // ------------------------------------------------------------------ teste em andamento

    public AbTestState? Active => _data.Active is { Step: not AbStep.Done } a ? a : null;
    public bool HasActive => Active is not null;
    public string StepTitle => Active is { } a ? AbLabText.StepTitle(a) : "";
    public string StepDetail => Active is { } a ? AbLabText.StepDetail(a) : "";
    public string ActiveHeadline => Active is { } a ? Loc.F("Ab.Active.Headline", a.GameName, a.Candidate.Title) : "";
    public bool IsWaitingForPlay => Active is { } a && AbLabMachine.IsWaitingForPlay(a);
    public bool IsAsking => Active is { Step: AbStep.AskUser };
    public bool IsWorking => _running || Active is { Step: AbStep.Applying or AbStep.Reapplying or AbStep.Reverting or AbStep.Undoing or AbStep.Deciding };
    public int PlayIndex => Active is { } a ? AbLabMachine.PlayIndex(a) : 0;
    public int PlaysNeeded => Active is { } a ? AbLabMachine.PlaysNeeded(a.Design) : 0;
    public double PlayProgress => Active is { } a ? 100.0 * (AbLabMachine.PlayIndex(a) - 1) / AbLabMachine.PlaysNeeded(a.Design) : 0;
    /// <summary>"Medindo agora", "Abra o jogo"… vem da detecção do Medir.</summary>
    public string MeasureHint => Measure.IsCapturing ? Loc.T("Ab.Hint.Measuring") : Measure.IsPlaying ? Loc.T("Ab.Hint.Open") : Loc.T("Ab.Hint.OpenGame");
    public bool AutoMeasureOff => !Measure.AutoMeasure;
    /// <summary>Última observação ("A partida de 2 min foi curta demais…").</summary>
    public string LastNote { get => _lastNote; private set { if (Set(ref _lastNote, value)) OnPropertyChanged(nameof(HasLastNote)); } }
    public bool HasLastNote => _lastNote.Length > 0;
    public string QueueText => _data.Queue.Count == 0 ? "" : Loc.F("Ab.Queue", _data.Queue.Count);
    public bool HasQueue => _data.Queue.Count > 0;
    /// <summary>A/B/A que pergunta: o ajuste já está desfeito; A/B que pergunta: ainda está aplicado.</summary>
    public string AskText => Active is { } a ? (a.Design == AbDesign.ABA ? Loc.T("Ab.Ask.Aba") : Loc.T("Ab.Ask.Ab")) : "";

    // ------------------------------------------------------------------ resultado e histórico

    public AbResultView? Result { get => _result; private set { if (Set(ref _result, value)) { OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(ShowSetup)); } } }
    public bool HasResult => _result is not null;
    public IReadOnlyList<AbHistoryRow> History { get => _history; private set { if (Set(ref _history, value)) OnPropertyChanged(nameof(HasHistory)); } }
    public bool HasHistory => _history.Count > 0;

    // ------------------------------------------------------------------ ciclo de vida

    public override Task OnFactsReadyAsync(SystemFacts facts)
    {
        Subscribe();
        Load();
        // Um teste que ficou no meio ao fechar o NEXUS: o que dá para retomar, retoma; aplicar interrompido vira "parou".
        if (_data.Active is { } a)
        {
            var resumed = AbLabMachine.Resume(a, DateTimeOffset.Now);
            if (!ReferenceEquals(resumed, a)) Save(_data with { Active = resumed });
            _ = RunAsync(DriveAsync);
        }
        return Task.CompletedTask;
    }

    public override async Task OnShownAsync()
    {
        Subscribe();
        Load();
        await LoadGamesAsync();
        RefreshActive();
        if (_waitingSessions && Measure.HasSessions) { _waitingSessions = false; await DriveAsync(); }
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        Measure.SessionSaved += (_, s) => _ = SafeAsync(() => OfferAsync(s));
        Measure.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MeasurePage.IsCapturing) or nameof(MeasurePage.HeroKicker) or nameof(MeasurePage.AutoMeasure) or nameof(MeasurePage.HasPresentMon))
                foreach (var n in new[] { nameof(MeasureHint), nameof(AutoMeasureOff), nameof(MissingPresentMon) }) OnPropertyChanged(n);
        };
    }

    /// <summary>Como RunAsync, mas sem depender de Busy: a partida chega por evento, mesmo enquanto um comando do usuário roda.</summary>
    private async Task SafeAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("Shell.NaoFoiPossivelConcluir", ex.Message); }
    }

    private void Load()
    {
        _data = _store.Load();
        History = [.. _data.History.OrderByDescending(h => h.At).Take(30).Select(h => new AbHistoryRow(h))];
    }

    private void Save(AbLabData data)
    {
        _data = data;
        try { _store.Save(data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = Loc.F("Ab.SaveFailed", ex.Message); }
        RefreshActive();
    }

    private void RefreshActive()
    {
        foreach (var n in new[] { nameof(Active), nameof(HasActive), nameof(StepTitle), nameof(StepDetail), nameof(ActiveHeadline), nameof(IsWaitingForPlay),
                     nameof(IsAsking), nameof(IsWorking), nameof(PlayIndex), nameof(PlaysNeeded), nameof(PlayProgress), nameof(ShowSetup), nameof(QueueText),
                     nameof(HasQueue), nameof(AskText), nameof(MeasureHint), nameof(AutoMeasureOff), nameof(MissingPresentMon), nameof(PlaysText) })
            OnPropertyChanged(n);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Os jogos da tela Jogos (biblioteca) com as opções deles + os ajustes rápidos do catálogo que ainda não estão ligados.</summary>
    private async Task LoadGamesAsync()
    {
        if (_loadingGames) return;
        IsLoadingGames = _games.Count == 0;
        try
        {
            await Games.OnShownAsync();
            var tweaks = Tweaks.All.Where(t => !t.IsOn).Select(t => AbLabCandidates.FromCatalog(t.Key, t.Title, t.Request, t.Level)).ToList();
            var keep = _pendingGame ?? _selectedGame?.Exe;
            _pendingGame = null;
            var list = new List<AbGameChoice>();
            foreach (var (name, exe, options) in Games.DescribeForLab())
            {
                var candidates = options.Select(o => AbLabCandidates.FromPlan(o.Text, o.Plan)).Concat(tweaks).ToList();
                list.Add(new AbGameChoice(name, exe, candidates, AbLabFlow.LatestFor(_data.History, exe)));
            }
            // Jogos já medidos que não estão na biblioteca (adicionados à mão no Medir) também entram, só com os ajustes do catálogo.
            foreach (var g in Measure.Games.Where(g => list.All(l => !string.Equals(l.Key, g.Key, StringComparison.Ordinal))))
                list.Add(new AbGameChoice(g.Name, g.Key, tweaks, AbLabFlow.LatestFor(_data.History, g.Key)));
            GameChoices = list;
            SelectedGame = list.FirstOrDefault(g => string.Equals(g.Exe, keep, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("Shell.NaoFoiPossivelConcluir", ex.Message); }
        finally { IsLoadingGames = false; }
    }

    private string? _pendingGame;
    private bool _waitingSessions;

    /// <summary>Pré-seleciona um jogo (atalho "Comparar com e sem" da tela Jogos); se a lista ainda não foi montada, vale na próxima montagem.</summary>
    public void PreselectGame(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return;
        var key = GameKey.Of(exe);
        var found = _games.FirstOrDefault(g => g.Key == key);
        if (found is not null) SelectedGame = found;
        else _pendingGame = exe;
    }

    // ------------------------------------------------------------------ comandos do usuário

    private async Task StartAsync()
    {
        if (SelectedGame is not { } game || Host is null) return;
        var chosen = game.Candidates.Where(c => c.IsChecked).Select(c => c.Candidate).ToList();
        if (chosen.Count == 0) return;
        var design = UseAba ? AbDesign.ABA : AbDesign.AB;
        var autoUndo = !AskAtEnd;
        if (!Host.Confirm(Loc.T("Ab.Title"), AbLabText.StartConfirmation(game.Name, chosen, design, autoUndo))) return;
        try
        {
            Save(AbLabFlow.Begin(_data, game.Exe, game.Name, chosen, design, autoUndo, DateTimeOffset.Now, NewId));
        }
        catch (InvalidOperationException ex) { Message = ex.Message; return; }
        Result = null;
        LastNote = "";
        if (!Measure.AutoMeasure) Measure.AutoMeasure = true; // sem medição automática não há partida para o teste
        Measure.StartWatching();
        await Task.CompletedTask;
    }

    private async Task CancelAsync()
    {
        if (Active is not { } a) return;
        if (Host is { } h && !h.Confirm(Loc.T("Ab.Cancel"), AbLabMachine.TweakApplied(a) ? Loc.T("Ab.Cancel.ConfirmApplied") : Loc.T("Ab.Cancel.Confirm"))) return;
        Save(_data with { Active = AbLabMachine.Cancel(a, DateTimeOffset.Now), Queue = [] });
        await DriveAsync();
    }

    private async Task AnswerAsync(bool keep)
    {
        if (Active is not { Step: AbStep.AskUser } a) return;
        Save(_data with { Active = AbLabMachine.Answer(a, keep, DateTimeOffset.Now) });
        await DriveAsync();
    }

    // ------------------------------------------------------------------ partidas medidas

    private async Task OfferAsync(MeasuredSession session)
    {
        if (Active is not { } a) return;
        var (next, rejections) = AbLabMachine.Offer(a, session, DateTimeOffset.Now);
        if (rejections.Count > 0)
        {
            var why = AbLabText.Rejection(rejections);
            if (why.Length > 0) LastNote = why;
            return;
        }
        LastNote = Loc.F("Ab.Accepted", AbLabMachine.PlayIndex(a), session.Summary.AverageFps.ToString("0", Loc.Culture), session.Summary.OnePercentLowAverageFps.ToString("0", Loc.Culture));
        Save(_data with { Active = next });
        await DriveAsync();
    }

    // ------------------------------------------------------------------ a máquina: executa o comando pendente até parar numa espera

    private async Task DriveAsync()
    {
        if (_running) return;
        _running = true;
        RefreshActive();
        try
        {
            for (var guard = 0; guard < 8 && _data.Active is { } s; guard++)
            {
                var now = DateTimeOffset.Now;
                switch (AbLabMachine.CommandFor(s))
                {
                    case AbCommand.Apply:
                        {
                            var (ok, already, runs) = await ApplyAsync(s);
                            Save(_data with { Active = AbLabMachine.Applied(s, ok, already, runs, DateTimeOffset.Now) });
                            if (!ok) LastNote = Loc.T("Ab.ApplyFailedNote");
                            else if (already) LastNote = Loc.T("Ab.AlreadyOnNote");
                            continue;
                        }
                    case AbCommand.Revert:
                        {
                            var ok = await RevertAsync(s);
                            Save(_data with { Active = AbLabMachine.Reverted(s, ok, DateTimeOffset.Now) });
                            continue;
                        }
                    case AbCommand.Decide:
                        {
                            var baseline = Measure.FindSession(s.Baseline);
                            var treatment = Measure.FindSession(s.Treatment);
                            var baseline2 = Measure.FindSession(s.Baseline2);
                            if (baseline is null || treatment is null || (s.Design == AbDesign.ABA && baseline2 is null))
                            {
                                // O Medir ainda não leu as partidas do disco (abertura do NEXUS): espera; a página tenta de novo ao ser aberta.
                                if (!Measure.HasSessions) { _waitingSessions = true; break; }
                                // Partida apagada no meio: o teste não tem como decidir; termina desfazendo o que aplicou.
                                LastNote = Loc.T("Ab.SessionMissing");
                                Save(_data with { Active = s with { Step = s.Runs.Count > 0 ? AbStep.Undoing : AbStep.Done, Outcome = s.Runs.Count > 0 ? AbOutcome.None : AbOutcome.Failed, Failure = AbFailure.Interrupted, StepSince = now } });
                                continue;
                            }
                            var judgement = AbLabJudge.Judge(baseline, treatment, baseline2);
                            Save(_data with { Active = AbLabMachine.Decided(s, judgement, DateTimeOffset.Now) });
                            continue;
                        }
                    case AbCommand.Ask:
                    case AbCommand.None:
                    default:
                        break;
                }
                if (s.Step == AbStep.Done)
                {
                    Result = new AbResultView(s);
                    Save(AbLabFlow.Advance(_data, DateTimeOffset.Now, NewId));
                    History = [.. _data.History.OrderByDescending(h => h.At).Take(30).Select(h => new AbHistoryRow(h))];
                    Host?.Notify(Loc.F("Ab.Finished", s.Candidate.Title, Result.Title));
                    if (_data.Active is not null) { LastNote = Loc.F("Ab.NextInQueue", _data.Active.Candidate.Title); continue; }
                }
                break;
            }
        }
        finally
        {
            _running = false;
            RefreshActive();
        }
    }

    /// <summary>Aplica o ajuste pelo aplicador, sem outra confirmação (o consentimento foi dado ao começar o teste).</summary>
    private async Task<(bool Ok, bool Already, IReadOnlyList<AbRunRef> Runs)> ApplyAsync(AbTestState s)
    {
        if (Host is null) return (false, false, []);
        var runs = new List<AbRunRef>();
        var ok = true;
        var changed = false;
        var items = AbLabCandidates.Requests(s.Candidate);
        foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
        {
            var requests = items.Where(i => i.Level == level).Select(i => i.Request).ToList();
            if (requests.Count == 0) continue;
            BrokerCallResult? r;
            try { r = await Host.ApplyConfirmedAsync(Loc.F("Ab.ApplyTitle", s.Candidate.Title), requests, level, null); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Message = ex.Message; r = null; }
            if (r is null) { ok = false; continue; }
            var results = r.Response?.Results ?? [];
            if (!r.Ok || results.Any(x => x.Outcome is not ("Verified" or "AlreadyConfigured" or "NotApplicable"))) ok = false;
            if (results.Any(x => x.Outcome == "Verified"))
            {
                changed = true;
                runs.AddRange(r.AllRunIds.Select(id => new AbRunRef(id, level == BrokerLevel.Admin)));
            }
        }
        return (ok, ok && !changed, runs);
    }

    /// <summary>Desfaz as execuções do teste (a mais nova primeiro), sem perguntar: combinado no começo.</summary>
    private async Task<bool> RevertAsync(AbTestState s)
    {
        if (Host is null) return false;
        var ok = true;
        foreach (var run in s.Runs.Reverse())
        {
            BrokerCallResult? r;
            try { r = await Host.RevertWithoutConfirmationAsync(run.Run, run.Admin ? BrokerLevel.Admin : BrokerLevel.User); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Message = ex.Message; r = null; }
            if (r is null || !r.Ok) ok = false;
        }
        return ok;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>Um jogo para escolher: nome, executável, os ajustes que dá para testar e o que já foi testado nele.</summary>
public sealed class AbGameChoice(string name, string exe, IReadOnlyList<AbCandidate> candidates, IReadOnlyList<AbResult> tested)
{
    public string Name { get; } = name;
    public string Exe { get; } = exe;
    public string Key { get; } = GameKey.Of(exe);
    public string Initial => Name.Length > 0 ? char.ToUpper(Name[0], Loc.Culture).ToString() : "?";
    public ObservableCollection<AbCandidateRow> Candidates { get; } = [.. candidates.Select(c => new AbCandidateRow(c, tested.FirstOrDefault(t => t.CandidateId == c.Id)))];
    public override string ToString() => Name;
}

/// <summary>Um ajuste candidato com a caixa de marcar e o que o último teste neste jogo disse dele.</summary>
public sealed class AbCandidateRow(AbCandidate candidate, AbResult? last) : ObservableObject
{
    private bool _checked;
    public AbCandidate Candidate { get; } = candidate;
    public string Title => Candidate.Title;
    public bool IsChecked { get => _checked; set { if (Set(ref _checked, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    /// <summary>"Testado: ajudou (+18% no 1% low) — mantido" ou vazio.</summary>
    public string LastText { get; } = last is null ? "" : last.Judgement is { } j
        ? Loc.F("Ab.Candidate.Tested", AbLabText.Verdict(j).Title, AbLabText.Highlight(j) is { Length: > 0 } h ? h : AbLabText.Outcome(last.Outcome, last.Failure).Text)
        : Loc.F("Ab.Candidate.TestedPlain", AbLabText.Outcome(last.Outcome, last.Failure).Text);
    public bool HasLast => LastText.Length > 0;
    public Tone LastTone { get; } = last?.Judgement is { } jj ? AbLabText.Verdict(jj).Tone : last is null ? Tone.Neutral : AbLabText.Outcome(last.Outcome, last.Failure).Tone;
}

/// <summary>Uma métrica do resultado: nome, antes → depois, diferença, selo honesto.</summary>
public sealed class AbMetricRow(AbMetric m)
{
    public string Name { get; } = AbLabText.MetricName(m.Key);
    public double Before { get; } = m.Before;
    public double After { get; } = m.After;
    public bool HigherIsBetter { get; } = AbLabText.HigherIsBetter(m.Key);
    public string BeforeText { get; } = AbLabText.Value(m.Key, m.Before);
    public string AfterText { get; } = AbLabText.Value(m.Key, m.After);
    public string DeltaText { get; } = AbLabText.Delta(m);
    public string VerdictText { get; } = AbLabText.MetricVerdict(m).Text;
    public Tone VerdictTone { get; } = AbLabText.MetricVerdict(m).Tone;
    public bool Decisive { get; } = m.Decisive;
}

/// <summary>O resultado de um teste terminado, em palavras simples.</summary>
public sealed class AbResultView
{
    public AbResultView(AbTestState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        GameName = s.GameName;
        CandidateTitle = s.Candidate.Title;
        var outcome = AbLabText.Outcome(s.Outcome, s.Failure);
        OutcomeText = outcome.Text;
        OutcomeTone = outcome.Tone;
        if (s.Judgement is { } j)
        {
            var v = AbLabText.Verdict(j);
            Title = v.Title;
            Detail = v.Detail;
            Tone = v.Tone;
            Metrics = [.. j.Metrics.Select(m => new AbMetricRow(m))];
            Warnings = AbLabText.Warnings(j);
            Method = j.Bracketed ? Loc.F("Ab.Method.Aba", j.Pairs) : Loc.F("Ab.Method.Ab", j.Pairs);
        }
        else
        {
            Title = outcome.Text;
            Detail = s.Outcome == AbOutcome.AlreadyOn ? Loc.T("Ab.Outcome.AlreadyOnDetail") : "";
            Tone = outcome.Tone;
            Metrics = [];
            Warnings = [];
            Method = "";
        }
    }

    public string GameName { get; }
    public string CandidateTitle { get; }
    public string Headline => Loc.F("Ab.Result.Headline", CandidateTitle, GameName);
    public string Title { get; }
    public string Detail { get; }
    public Tone Tone { get; }
    public string OutcomeText { get; }
    public Tone OutcomeTone { get; }
    public IReadOnlyList<AbMetricRow> Metrics { get; }
    public bool HasMetrics => Metrics.Count > 0;
    public IReadOnlyList<string> Warnings { get; }
    public bool HasWarnings => Warnings.Count > 0;
    public string Method { get; }
    public bool HasMethod => Method.Length > 0;
}

/// <summary>Uma linha do histórico.</summary>
public sealed class AbHistoryRow(AbResult r)
{
    public string Text { get; } = AbLabText.HistoryLine(r);
    public string OutcomeText { get; } = AbLabText.Outcome(r.Outcome, r.Failure).Text;
    public Tone Tone { get; } = r.Judgement is { } j ? AbLabText.Verdict(j).Tone : AbLabText.Outcome(r.Outcome, r.Failure).Tone;
}
