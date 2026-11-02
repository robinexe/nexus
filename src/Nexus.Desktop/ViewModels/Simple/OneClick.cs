using Nexus.Broker;
using Nexus.Core;

namespace Nexus.Desktop.ViewModels.Simple;

// =====================================================================================================================
// "Fácil de aplicar as modificações" (pedido do responsável): toda recomendação da interface simples se aplica com UM clique
// onde ela aparece. Clique → uma frase de confirmação → aplica pelo aplicador (diário, releitura, aviso do Windows para o que é
// de sistema) → resultado ali mesmo: "Feito ✓ — Desfazer". Desfazer volta pelo mesmo aplicador.
// =====================================================================================================================

/// <summary>O que a janela oferece às telas simples para montar os pedidos (só a partir do catálogo e das leituras; nada escreve).</summary>
public interface ISimpleActions
{
    /// <summary>"Limpar agora": as regras seguras da Limpeza (marcadas por padrão, sem dados pessoais), com o lote novo.</summary>
    IReadOnlyList<(ActionRequest Request, BrokerLevel Level)> SafeCleanupRequests();

    /// <summary>Os ajustes rápidos recomendados (risco baixo) que ainda mudariam algo neste PC.</summary>
    IReadOnlyList<(ActionRequest Request, BrokerLevel Level)> PendingTweakRequests();

    /// <summary>Pedido para uma ação do catálogo de risco baixo, sem parâmetros do usuário, reversível; null se não der.</summary>
    (ActionRequest Request, BrokerLevel Level)? LowRiskRequest(string actionIdOrKey);

    /// <summary>Uma limpeza foi aplicada ou desfeita: mede de novo.</summary>
    void CleanupChanged();
}

/// <summary>Resultado de um clique: deu certo, já estava assim, e as execuções que desfazem (com o nível do aplicador).</summary>
public sealed record OneClickResult(bool Ok, bool Already, IReadOnlyList<(Guid Run, BrokerLevel Level)> Runs);

/// <summary>Aplica uma lista de pedidos com UMA confirmação (frase simples) e desfaz pelas execuções devolvidas.</summary>
public sealed class QuickApplier(IModuleHost host)
{
    /// <summary>Null = o usuário disse não (nada foi alterado).</summary>
    public async Task<OneClickResult?> ApplyAsync(string title, string sentence, IReadOnlyList<(ActionRequest Request, BrokerLevel Level)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return new OneClickResult(true, true, []);
        if (!host.Confirm(title, sentence)) return null;
        var runs = new List<(Guid, BrokerLevel)>();
        var ok = true;
        var changed = false;
        foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin }) // sua conta primeiro; sistema depois (um aviso do Windows)
        {
            var requests = items.Where(i => i.Level == level).Select(i => i.Request).ToList();
            if (requests.Count == 0) continue;
            var r = await host.ApplyConfirmedAsync(title, requests, level, null);
            if (r is null) { ok = false; continue; }
            var results = r.Response?.Results ?? [];
            if (!r.Ok || results.Any(x => x.Outcome is not ("Verified" or "AlreadyConfigured" or "NotApplicable"))) ok = false;
            if (results.Any(x => x.Outcome == "Verified"))
            {
                changed = true;
                runs.AddRange(r.AllRunIds.Select(id => (id, level)));
            }
        }
        return new OneClickResult(ok, ok && !changed, runs);
    }

    /// <summary>Desfaz as execuções (a mais nova primeiro), sem outra pergunta: o clique em "Desfazer" é a escolha.</summary>
    public async Task<bool> UndoAsync(IReadOnlyList<(Guid Run, BrokerLevel Level)> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var ok = true;
        foreach (var (run, level) in runs.Reverse())
        {
            var r = await host.RevertWithoutConfirmationAsync(run, level);
            if (r is null || !r.Ok) ok = false;
        }
        return ok;
    }
}

/// <summary>
/// Um botão de um clique com o resultado no lugar: pronto → trabalhando → "Feito ✓ — Desfazer" (ou "Já estava assim ✓", ou
/// "Não deu certo"). Desfazer volta ao estado pronto ("Desfeito").
/// </summary>
public sealed class OneClickAction : ObservableObject
{
    private enum Phase { Ready, Working, Done, Failed }

    private readonly Func<QuickApplier?> _applier;
    private readonly Func<QuickApplier, Task<OneClickResult?>> _apply;
    private readonly Action? _after;
    private Phase _phase;
    private string _note = "";
    private IReadOnlyList<(Guid Run, BrokerLevel Level)> _runs = [];

    public OneClickAction(string actionText, Func<QuickApplier?> applier, Func<QuickApplier, Task<OneClickResult?>> apply, Action? after = null)
    {
        ActionText = actionText;
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _after = after;
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => _phase is Phase.Ready or Phase.Failed);
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => _phase == Phase.Done && _runs.Count > 0);
    }

    public string ActionText { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand UndoCommand { get; }
    public bool IsReady => _phase is Phase.Ready or Phase.Failed;
    public bool IsWorking => _phase == Phase.Working;
    public bool IsDone => _phase == Phase.Done;
    public bool CanUndo => _phase == Phase.Done && _runs.Count > 0;
    /// <summary>"Feito", "Já estava assim", "Não deu certo", "Desfeito".</summary>
    public string Note { get => _note; private set { if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;

    private void Go(Phase p)
    {
        _phase = p;
        foreach (var n in new[] { nameof(IsReady), nameof(IsWorking), nameof(IsDone), nameof(CanUndo) }) OnPropertyChanged(n);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task ApplyAsync()
    {
        if (_applier() is not { } applier) return;
        Go(Phase.Working);
        try
        {
            var r = await _apply(applier);
            if (r is null) { Note = ""; Go(Phase.Ready); return; }
            _runs = r.Runs;
            Note = !r.Ok ? Loc.T("Simple.One.Failed") : r.Already ? Loc.T("Simple.One.Already") : Loc.T("Simple.One.Done");
            Go(r.Ok ? Phase.Done : Phase.Failed);
            if (r.Runs.Count > 0) _after?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Note = Loc.T("Simple.One.Failed");
            Go(Phase.Failed);
        }
    }

    private async Task UndoAsync()
    {
        if (_applier() is not { } applier) return;
        var runs = _runs;
        Go(Phase.Working);
        try
        {
            var ok = await applier.UndoAsync(runs);
            _runs = ok ? [] : runs;
            Note = ok ? Loc.T("Simple.One.Undone") : Loc.T("Simple.One.UndoFailed");
            Go(ok ? Phase.Ready : Phase.Done);
            _after?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Note = Loc.T("Simple.One.UndoFailed");
            Go(Phase.Done);
        }
    }
}

// ================================================================== Ajustes rápidos

/// <summary>Um ajuste rápido lido do catálogo + recomendações deste PC. Ligado = o Windows já está como o ajuste deixa (Detect).</summary>
public sealed record QuickTweakData(string Key, string Title, string Sentence, QuickCategory Category, bool IsOn, bool Recommended, string RiskWord,
    ActionRequest Request, BrokerLevel Level);

/// <summary>Uma linha dos ajustes rápidos: caixa de ligar/desligar, uma frase e (em "Mostrar mais opções") a palavra de risco.</summary>
public sealed class QuickTweakRow : ObservableObject
{
    private readonly QuickTweaksPanel _panel;
    private bool _busy;
    private string _note = "";

    public QuickTweakRow(QuickTweakData data, QuickTweaksPanel panel)
    {
        Data = data;
        _panel = panel;
        ToggleCommand = new RelayCommand(() => _ = _panel.ToggleAsync(this), () => !_busy);
    }

    public QuickTweakData Data { get; }
    public string Title => Data.Title;
    public string Sentence => Data.Sentence;
    public bool HasSentence => Data.Sentence.Length > 0;
    public bool IsOn => Data.IsOn;
    public bool ShowRisk => !Data.Recommended;
    public string RiskWord => Data.RiskWord;
    public bool Busy { get => _busy; internal set => Set(ref _busy, value); }
    public string Note { get => _note; internal set { if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;
    public RelayCommand ToggleCommand { get; }

    /// <summary>Clique cancelado: a caixa volta a mostrar o estado real.</summary>
    internal void Refresh() => OnPropertyChanged(nameof(IsOn));
}

public sealed record QuickTweakGroup(string Title, string IconKey, IReadOnlyList<QuickTweakRow> Items);

/// <summary>
/// "Ajustes rápidos" (dentro de Otimizar): os ajustes que o NEXUS recomenda para este PC em 4 grupos (Velocidade, Jogos,
/// Privacidade, Energia). Ligar aplica; desligar desfaz a execução que ligou (desta sessão ou do diário, quando ela só mudou esse
/// ajuste). Por padrão só os de risco baixo recomendados; "Mostrar mais opções" mostra o resto com a palavra de risco.
/// </summary>
public sealed class QuickTweaksPanel : ObservableObject
{
    private readonly Dictionary<string, (Guid Run, BrokerLevel Level)> _runs = new(StringComparer.Ordinal);
    // O resultado ("Feito ✓", "Desfeito") sobrevive à releitura que remonta as linhas depois de aplicar.
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    private IReadOnlyList<QuickTweakData> _data = [];
    private IReadOnlyList<QuickTweakGroup> _groups = [];
    private bool _showMore;
    private string _message = "";

    public QuickTweaksPanel() => ToggleMoreCommand = new RelayCommand(() => { _showMore = !_showMore; Rebuild(); });

    /// <summary>Quem aplica (a janela) e quem acha a execução de um ajuste feito antes (o Histórico simples).</summary>
    internal IModuleHost? Host { get; set; }
    internal Func<string, (Guid Run, BrokerLevel Level)?>? FindRun { get; set; }

    public IReadOnlyList<QuickTweakGroup> Groups { get => _groups; private set => Set(ref _groups, value); }
    public bool HasAny => _groups.Count > 0;
    public bool HasMore => _data.Any(d => !d.Recommended);
    public string MoreText => _showMore ? Loc.T("Simple.Quick.Less") : Loc.T("Simple.Quick.More");
    public RelayCommand ToggleMoreCommand { get; }
    public string Message { get => _message; private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;

    /// <summary>Recomendados que ainda mudariam algo (o cartão "N ajustes seguros" da tela Início).</summary>
    public IReadOnlyList<QuickTweakData> Pending => [.. _data.Where(d => d.Recommended && !d.IsOn)];

    public void Update(IReadOnlyList<QuickTweakData> data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        Rebuild();
    }

    private void Rebuild()
    {
        Groups = [.. _data.Where(d => d.Recommended || _showMore)
            .GroupBy(d => d.Category).OrderBy(g => g.Key)
            .Select(g => new QuickTweakGroup(QuickTweakText.CategoryTitle(g.Key), QuickTweakText.CategoryIcon(g.Key),
                [.. g.OrderBy(d => d.Recommended ? 0 : 1).Select(d => new QuickTweakRow(d, this) { Note = _notes.GetValueOrDefault(d.Key, "") })]))];
        foreach (var n in new[] { nameof(HasAny), nameof(HasMore), nameof(MoreText) }) OnPropertyChanged(n);
    }

    internal async Task ToggleAsync(QuickTweakRow row)
    {
        if (Host is null) return;
        var applier = new QuickApplier(Host);
        row.Busy = true;
        Message = "";
        try
        {
            if (!row.IsOn)
            {
                var r = await applier.ApplyAsync(row.Title, Loc.F("Simple.Quick.ConfirmOn", row.Title), [(row.Data.Request, row.Data.Level)]);
                if (r is null) { row.Refresh(); return; }
                if (r.Runs.Count > 0) _runs[row.Data.Key] = r.Runs[^1];
                row.Note = r.Ok ? Loc.T("Simple.One.Done") : Loc.T("Simple.One.Failed");
            }
            else
            {
                (Guid Run, BrokerLevel Level)? run = _runs.TryGetValue(row.Data.Key, out var mine) ? mine : FindRun?.Invoke(row.Data.Key);
                if (run is not { } found)
                {
                    row.Refresh();
                    row.Note = Loc.T("Simple.Quick.NothingToUndo");
                    return;
                }
                if (!Host.Confirm(row.Title, Loc.F("Simple.Quick.ConfirmOff", row.Title))) { row.Refresh(); return; }
                var ok = await applier.UndoAsync([found]);
                if (ok) _runs.Remove(row.Data.Key);
                row.Note = ok ? Loc.T("Simple.One.Undone") : Loc.T("Simple.One.UndoFailed");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { row.Note = Loc.T("Simple.One.Failed"); }
        finally
        {
            row.Busy = false;
            row.Refresh();
            _notes[row.Data.Key] = row.Note;
        }
    }
}
