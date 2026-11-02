using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.Core;
using Nexus.HardwareDiscovery;
using Nexus.Privacy;
using Nexus.WindowsActions.Registry;
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Privacidade: lê o estado de cada item do catálogo (somente leitura), mostra o placar e aplica perfis pelo aplicador.
/// A página nunca escreve: tudo passa por <see cref="IModuleHost.ApplyAsync"/> / <see cref="IModuleHost.RevertAsync"/>.
/// </summary>
public sealed class PrivacyPage : ModulePage
{
    public override string Title => Loc.T("Settings.Privacidade");
    public override string Group => Loc.T("Nav.Proteger");
    public override string Subtitle => Loc.T("Priv.ControleWindowsColetaMostra");

    private PrivacyCatalog? _catalog;
    private IReadOnlyList<PrivacyItemState> _states = [];
    private int _build;
    private bool _scanning;
    private bool _hasRead;
    private readonly Stack<List<(Guid Run, BrokerLevel Level)>> _applied = new();

    private string _scoreText = "–";
    private double _scoreFraction;
    private string _scoreCaption = Loc.T("Priv.LendoEstadoWindows");
    private int _recommendedPending;
    private int _strictPending;
    private int _selectedPending;
    private string _buildNote = "";
    private string _lastResult = "";
    private double _scorePercent = double.NaN;
    private double _protectedNumber = double.NaN;
    private double _applicableNumber = double.NaN;
    private double _exposedNumber = double.NaN;

    public PrivacyPage()
    {
        ApplyRecommendedCommand = new RelayCommand(() => _ = ApplyProfileAsync(PrivacyProfile.Recommended), () => !Busy && RecommendedPending > 0);
        ApplyStrictCommand = new RelayCommand(() => _ = ApplyProfileAsync(PrivacyProfile.Strict), () => !Busy && StrictPending > 0);
        ApplySelectedCommand = new RelayCommand(() => _ = ApplySelectedAsync(), () => !Busy && SelectedPending > 0);
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => !Busy && CanUndo);
        RescanCommand = new RelayCommand(() => _ = ScanAsync(), () => !Busy && !_scanning);
        SelectRecommendedCommand = new RelayCommand(() => Select(i => i.Item.Profile == PrivacyProfile.Recommended), () => !Busy);
        SelectStrictCommand = new RelayCommand(() => Select(i => i.Item.Profile is PrivacyProfile.Recommended or PrivacyProfile.Strict), () => !Busy);
        SelectNoneCommand = new RelayCommand(() => Select(_ => false), () => !Busy);
    }

    public ObservableCollection<PrivacyThemeGroup> Groups { get; } = [];
    public ObservableCollection<PrivacyInfoRow> InfoRows { get; } = [];

    public ICommand ApplyRecommendedCommand { get; }
    public ICommand ApplyStrictCommand { get; }
    public ICommand ApplySelectedCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand SelectRecommendedCommand { get; }
    public ICommand SelectStrictCommand { get; }
    public ICommand SelectNoneCommand { get; }

    public string ScoreText { get => _scoreText; private set => Set(ref _scoreText, value); }
    /// <summary>0 a 1, para a barra do placar.</summary>
    public double ScoreFraction { get => _scoreFraction; private set => Set(ref _scoreFraction, value); }
    public string ScoreCaption { get => _scoreCaption; private set => Set(ref _scoreCaption, value); }
    public int RecommendedPending { get => _recommendedPending; private set { if (Set(ref _recommendedPending, value)) { OnPropertyChanged(nameof(RecommendedText)); OnPropertyChanged(nameof(RecommendedNumber)); } } }
    public int StrictPending { get => _strictPending; private set { if (Set(ref _strictPending, value)) { OnPropertyChanged(nameof(StrictText)); OnPropertyChanged(nameof(StrictNumber)); } } }
    public int SelectedPending { get => _selectedPending; private set { if (Set(ref _selectedPending, value)) { OnPropertyChanged(nameof(SelectedText)); OnPropertyChanged(nameof(SelectedNumber)); } } }
    public string RecommendedText => RecommendedPending == 0 ? Loc.T("Priv.RecomendadoTudoAplicado") : Loc.F("Priv.RecomendadoN", Plural(RecommendedPending, Loc.T("Priv.ItemFalta"), Loc.T("Priv.ItensFaltam")));
    public string StrictText => StrictPending == 0 ? Loc.T("Priv.RigidoTudoAplicado") : Loc.F("Priv.RigidoN", Plural(StrictPending, Loc.T("Priv.ItemFalta"), Loc.T("Priv.ItensFaltam")));
    public string SelectedText => SelectedPending == 0 ? Loc.T("Priv.NenhumItemMarcado") : $"{Plural(SelectedPending, Loc.T("Priv.ItemMarcado"), Loc.T("Priv.ItensMarcados"))}";
    public string BuildNote { get => _buildNote; private set => Set(ref _buildNote, value); }
    public string LastResult { get => _lastResult; private set { if (Set(ref _lastResult, value)) OnPropertyChanged(nameof(HasLastResult)); } }
    public bool HasLastResult => LastResult.Length > 0;
    public bool HasRead { get => _hasRead; private set { if (Set(ref _hasRead, value)) OnPropertyChanged(nameof(IsLoading)); } }

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Esqueleto no destaque e na lista: lendo pela primeira vez.</summary>
    public bool IsLoading => _scanning && !HasRead;
    /// <summary>Protegidos / que se aplicam, em % (NaN antes da primeira leitura): o anel do destaque.</summary>
    public double ScorePercent { get => _scorePercent; private set => Set(ref _scorePercent, value); }
    public double ProtectedNumber { get => _protectedNumber; private set => Set(ref _protectedNumber, value); }
    public double ApplicableNumber { get => _applicableNumber; private set => Set(ref _applicableNumber, value); }
    /// <summary>Itens expostos ou parciais (o que ainda dá para proteger).</summary>
    public double ExposedNumber { get => _exposedNumber; private set => Set(ref _exposedNumber, value); }
    public double RecommendedNumber => RecommendedPending;
    public double StrictNumber => StrictPending;
    public double SelectedNumber => SelectedPending;
    public string ProtectedCaption => double.IsNaN(ApplicableNumber) ? "" : Loc.F("Priv.NValemEsteWindows", ApplicableNumber.ToString("0", CultureInfo.CurrentCulture));
    public bool CanUndo => _applied.Count > 0;

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString(CultureInfo.CurrentCulture)} {many}";

    public override Task OnFactsReadyAsync(SystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        _build = facts.WindowsBuild ?? 0;
        return ScanAsync();
    }

    public override Task OnShownAsync() => HasRead ? Task.CompletedTask : ScanAsync();

    // ------------------------------------------------------------------ leitura

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        OnPropertyChanged(nameof(IsLoading));
        try
        {
            if (_build == 0 && Host?.Facts?.WindowsBuild is { } b) _build = b;
            var build = _build;
            var (catalog, states) = await Task.Run(() =>
            {
                var c = _catalog ?? PrivacyCatalog.LoadFromDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "catalog"));
                var scanner = new PrivacyScanner(new WindowsRegistryBackend());
                var s = scanner.Scan(c, build);
                var consent = c.Info.ToDictionary(i => i.Id, i => i.ConsentStore is null ? null : scanner.ReadConsent(i.ConsentStore), StringComparer.Ordinal);
                return (c, (States: s, Consent: consent));
            });
            _catalog = catalog;
            _states = states.States;
            Rebuild(states.Consent);
            HasRead = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ScoreCaption = Loc.T("Priv.NaoFoiPossivelLer");
            Message = Loc.F("Priv.LeituraFalhouN", ex.Message);
        }
        finally { _scanning = false; OnPropertyChanged(nameof(IsLoading)); }
    }

    private void Rebuild(Dictionary<string, string?> consent)
    {
        // Preserva as marcações do usuário entre leituras; na primeira leitura, marca o Recomendado que falta.
        var previous = Groups.SelectMany(g => g.Items).ToDictionary(i => i.Id, i => i.IsSelected, StringComparer.Ordinal);
        Groups.Clear();
        foreach (var theme in _states.GroupBy(s => s.Item.Theme))
        {
            var rows = theme.Select(s => new PrivacyItemRow(s, previous.TryGetValue(s.Item.Id, out var sel) ? sel : s.Item.Profile == PrivacyProfile.Recommended, Recount)).ToList();
            Groups.Add(new PrivacyThemeGroup(theme.Key, rows));
        }
        InfoRows.Clear();
        foreach (var info in _catalog!.Info)
            InfoRows.Add(new PrivacyInfoRow(info, consent.TryGetValue(info.Id, out var v) ? v : null, OnSettingsOpened));

        var score = PrivacyPlanner.Score(_states);
        ScoreText = score.ToString();
        ScoreFraction = score.Fraction;
        ScorePercent = score.Applicable == 0 ? double.NaN : score.Fraction * 100;
        ProtectedNumber = score.Protected;
        ApplicableNumber = score.Applicable;
        ExposedNumber = score.Applicable - score.Protected;
        OnPropertyChanged(nameof(ProtectedCaption));
        ScoreCaption = score.Applicable == 0 ? Loc.T("Priv.NenhumItemAplicaEste")
            : Loc.F("Priv.NNAplicamEste", Plural(score.Protected, Loc.T("Priv.ProtecaoAtiva"), Loc.T("Priv.ProtecoesAtivas")), score.Applicable) +
              (score.Partial > 0 ? $" ({Plural(score.Partial, Loc.T("Priv.ItemParcial"), Loc.T("Priv.ItensParciais"))})." : ".");
        var na = _states.Count(s => s.Status == PrivacyItemStatus.NotApplicable);
        BuildNote = (_build > 0 ? Loc.F("Priv.WindowsBuildN", _build.ToString(CultureInfo.InvariantCulture)) : Loc.T("Priv.BuildWindowsDesconhecido")) +
                    (na > 0 ? Loc.F("Priv.NEstaVersao", Plural(na, Loc.T("Priv.ItemNaoAplica"), Loc.T("Priv.ItensNaoAplicam"))) : "");
        RecommendedPending = PrivacyPlanner.MissingFor(_states, PrivacyProfile.Recommended).Count;
        StrictPending = PrivacyPlanner.MissingFor(_states, PrivacyProfile.Strict).Count;
        Recount();
    }

    private void Recount() => SelectedPending = Groups.SelectMany(g => g.Items).Count(i => i.IsSelected && i.CanSelect);

    private void Select(Func<PrivacyItemState, bool> rule)
    {
        foreach (var row in Groups.SelectMany(g => g.Items)) row.IsSelected = row.CanSelect && rule(row.State);
        Recount();
    }

    private void OnSettingsOpened(bool ok, string title) =>
        Message = ok ? Loc.F("Priv.ConfiguracoesAbertasNVoce", title) : Loc.T("Priv.WindowsNaoAbriuConfiguracoes");

    // ------------------------------------------------------------------ escrita (só pelo aplicador)

    private Task ApplyProfileAsync(PrivacyProfile profile) =>
        ApplyAsync(PrivacyPlanner.MissingFor(_states, profile), profile == PrivacyProfile.Recommended ? Loc.T("Priv.PerfilRecomendado") : Loc.T("Priv.PerfilRigido"));

    private Task ApplySelectedAsync()
    {
        var chosen = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected && i.CanSelect).Select(i => i.State).ToList();
        return ApplyAsync(chosen, Loc.T("Priv.SelecaoPersonalizada"));
    }

    private Task ApplyAsync(IReadOnlyList<PrivacyItemState> items, string label) => RunAsync(async () =>
    {
        if (Host is null) return;
        var batches = PrivacyPlanner.Batches(items);
        if (batches.Count == 0) { Message = Loc.T("Priv.NadaAplicarEssesItens"); return; }

        var runs = new List<(Guid Run, BrokerLevel Level)>();
        int verified = 0, already = 0, other = 0;
        var problems = new List<string>();
        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var level = PrivacyPlanner.NeedsAdmin(batch) ? BrokerLevel.Admin : BrokerLevel.User;
            var requests = PrivacyPlanner.ActionKeys(batch).Select(k => new ActionRequest(k)).ToList();
            var r = await Host.ApplyAsync(Loc.F("Priv.PrivacidadeN", label), PrivacyPlanner.ConfirmationText(batch, i + 1, batches.Count), requests, level);
            if (r is null)
            {
                Message = i == 0 ? Loc.T("Shell.CanceladoNadaFoiAlterado") : Loc.F("Priv.ParteNCanceladaPartes", i + 1);
                break;
            }
            if (r.Response?.RunId is { } run) runs.Add((run, level));
            foreach (var res in r.Response?.Results ?? [])
            {
                if (res.Outcome == nameof(ExecutionOutcome.Verified)) verified++;
                else if (res.Outcome == nameof(ExecutionOutcome.AlreadyConfigured)) already++;
                else { other++; if (problems.Count < 4) problems.Add($"{res.ActionKey}: {res.Outcome}{(res.Detail is null ? "" : " — " + res.Detail)}"); }
            }
            if (!r.Ok) { Message = r.Message; break; }
        }
        if (runs.Count > 0) { _applied.Push(runs); OnPropertyChanged(nameof(CanUndo)); }
        if (verified + already + other > 0)
            LastResult = $"{Plural(verified, Loc.T("Priv.AjusteAplicadoConferido"), Loc.T("Priv.AjustesAplicadosConferidos"))}" +
                         (already > 0 ? Loc.F("Priv.NJaEstavamAssim", already) : "") +
                         (other > 0 ? $", {Plural(other, Loc.T("Shell.NaoAplicado"), Loc.T("Priv.NaoAplicados"))}: {string.Join("; ", problems)}" : ".") +
                         Loc.T("Priv.AlgunsItensSoAparecem");
        await ScanAsync();
    });

    private Task UndoAsync() => RunAsync(async () =>
    {
        if (Host is null || _applied.Count == 0) return;
        var runs = _applied.Peek();
        var allOk = true;
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            var r = await Host.RevertAsync(runs[i].Run, runs[i].Level);
            if (r is null) { allOk = false; Message = Loc.T("Auto.DesfazerCancelado"); break; }
            if (r.Response?.FullyRestored != true) { allOk = false; Message = Loc.F("Priv.DesfazerIncompletoNVeja", r.Message); break; }
            runs.RemoveAt(i);
        }
        if (allOk || runs.Count == 0) { _applied.Pop(); LastResult = Loc.T("Priv.DesfeitoValoresVoltaramEram"); }
        OnPropertyChanged(nameof(CanUndo));
        await ScanAsync();
    });
}

/// <summary>Um tema da lista (ex.: "Anúncios e sugestões").</summary>
public sealed class PrivacyThemeGroup(string theme, IReadOnlyList<PrivacyItemRow> items)
{
    public string Theme { get; } = theme;
    public IReadOnlyList<PrivacyItemRow> Items { get; } = items;
    public string Count => $"{Items.Count(i => i.State.Status == PrivacyItemStatus.Protected)}/{Items.Count(i => i.State.Status != PrivacyItemStatus.NotApplicable)}";

    public int ProtectedCount => Items.Count(i => i.State.Status == PrivacyItemStatus.Protected);
    public int ApplicableCount => Items.Count(i => i.State.Status != PrivacyItemStatus.NotApplicable);
    /// <summary>0 a 100, para a barra do cartão do tema.</summary>
    public double Percent => ApplicableCount == 0 ? 0 : 100.0 * ProtectedCount / ApplicableCount;
    public string CountText => ApplicableCount == 0 ? Loc.T("Priv.NaoAplicaEsteWindows") : Loc.F("Priv.NNProtegidos", ProtectedCount, ApplicableCount);
    public UiTone Tone => ApplicableCount == 0 ? UiTone.Neutral : ProtectedCount == ApplicableCount ? UiTone.Good : ProtectedCount == 0 ? UiTone.Bad : UiTone.Warn;
    /// <summary>Ícone do tema (Icons.xaml), escolhido pelo nome do tema do catálogo.</summary>
    public string IconKey => IconFor(Theme);

    internal static string IconFor(string theme) => theme switch
    {
        _ when theme.StartsWith("Anúncios", StringComparison.Ordinal) => "Icon.Sparkle",
        _ when theme.StartsWith("Atividade", StringComparison.Ordinal) => "Icon.History",
        _ when theme.StartsWith("Localização", StringComparison.Ordinal) => "Icon.Network",
        _ when theme.Contains("Edge", StringComparison.Ordinal) => "Icon.Browser",
        _ when theme.Contains("Office", StringComparison.Ordinal) => "Icon.Apps",
        _ when theme.StartsWith("Pesquisa", StringComparison.Ordinal) => "Icon.Search",
        _ when theme.StartsWith("Telemetria", StringComparison.Ordinal) => "Icon.Diagnostics",
        _ when theme.StartsWith("Voz", StringComparison.Ordinal) => "Icon.User",
        _ when theme.StartsWith("Permiss", StringComparison.Ordinal) => "Icon.Lock",
        _ when theme.StartsWith("Serviços", StringComparison.Ordinal) => "Icon.Services",
        _ => "Icon.Privacy",
    };
}

/// <summary>Um item com interruptor: marcado = entra na próxima aplicação.</summary>
public sealed class PrivacyItemRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;
    private bool _showDetails;

    public PrivacyItemRow(PrivacyItemState state, bool selected, Action changed)
    {
        State = state;
        _changed = changed;
        _isSelected = selected && CanSelect;
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
        Details = state.Actions.Select(a => new PrivacyActionRow(a)).ToList();
    }

    public PrivacyItemState State { get; }
    public string Id => State.Item.Id;
    public string Title => State.Item.Title;
    public string Summary => State.Item.Summary;
    public string Loses => State.Item.Loses;
    public string? Warning => State.Item.Warning;
    public bool HasWarning => Warning is not null && CanSelect;
    public IReadOnlyList<PrivacyActionRow> Details { get; }
    public ICommand ToggleDetailsCommand { get; }
    public bool ShowDetails { get => _showDetails; set => Set(ref _showDetails, value); }

    /// <summary>Só dá para marcar o que ainda não está protegido e se aplica a este Windows.</summary>
    public bool CanSelect => State.Status is PrivacyItemStatus.Exposed or PrivacyItemStatus.Partial;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value && CanSelect)) _changed(); }
    }

    public string StatusKey => State.Status.ToString();
    /// <summary>Tom do selo: verde protegido, âmbar parcial, vermelho exposto.</summary>
    public UiTone StatusTone => State.Status switch
    {
        PrivacyItemStatus.Protected => UiTone.Good,
        PrivacyItemStatus.Partial => UiTone.Warn,
        PrivacyItemStatus.Exposed => UiTone.Bad,
        _ => UiTone.Neutral,
    };
    public string StatusText => State.Status switch
    {
        PrivacyItemStatus.Protected => Loc.T("Priv.Protegido2"),
        PrivacyItemStatus.Partial => Loc.T("Priv.Parcial"),
        PrivacyItemStatus.Exposed => Loc.T("Priv.Exposto"),
        _ => Loc.T("Pg.NaoAplica"),
    };

    public string ProfileText => State.Item.Profile switch
    {
        PrivacyProfile.Recommended => Loc.T("Priv.Recomendado"),
        PrivacyProfile.Strict => Loc.T("Priv.Rigido"),
        _ => Loc.T("Priv.Opcional"),
    };

    public string Meta =>
        (State.Item.NeedsAdmin ? Loc.T("Priv.ValePcPedeAdministrador") : Loc.T("Priv.SoSuaContaSem")) +
        (State.Item.FullyDocumented ? Loc.T("Priv.DocumentadoPelaMicrosoft") : Loc.T("Priv.ParteFonteComunidade")) +
        (State.Status == PrivacyItemStatus.Protected ? Loc.T("Priv.VoltarDesfazerOuConfiguracoes") : "");
}

/// <summary>Detalhe técnico de uma ação: onde, atual, desejado, fonte.</summary>
public sealed class PrivacyActionRow(PrivacyActionState state)
{
    public string Title => state.Entry.Definition.Title;
    public string Where => $@"{state.Entry.TargetTemplate["hive"]}\{state.Entry.TargetTemplate["path"]}\{state.Entry.TargetTemplate["name"]}";
    public string Values => !state.Applicable ? Loc.T("Priv.NaoAplicaEstaVersao")
        : Loc.F("Priv.AgoraNProtegidoN", state.Current, state.Desired) + (state.Error is null ? "" : Loc.F("Priv.ErroLeituraN", state.Error));
    public string Mechanism => state.Entry.Definition.Mechanism;
    public string Evidence => string.Join("\n", state.Entry.Definition.Evidence.Select(e => $"{LevelText(e.Level)}: {e.Source}"));

    private static string LevelText(EvidenceLevel l) => l switch
    {
        EvidenceLevel.VendorDocumented => "Microsoft",
        EvidenceLevel.Community => Loc.T("Priv.Comunidade"),
        EvidenceLevel.Observed => Loc.T("Priv.Observado"),
        EvidenceLevel.Mechanistic => Loc.T("Priv.Mecanismo"),
        _ => Loc.T("Auto.Hipotese"),
    };
}

/// <summary>Item só de leitura: permissão de app ou referência a outra página.</summary>
public sealed class PrivacyInfoRow
{
    public PrivacyInfoRow(PrivacyInfo info, string? consent, Action<bool, string> opened)
    {
        Info = info;
        State = info.ConsentStore is null ? Loc.T("Priv.VejaPaginaServicos")
            : consent switch
            {
                "Allow" => Loc.T("Priv.PermitidoGeralCadaApp"),
                "Deny" => Loc.T("Priv.BloqueadoTodosApps"),
                _ => Loc.T("Priv.NaoLido"),
            };
        OpenCommand = new RelayCommand(() => opened(info.SettingsUri is not null && SettingsLauncher.Open(info.SettingsUri), info.Title), () => info.SettingsUri is not null);
    }

    public PrivacyInfo Info { get; }
    public string Title => Info.Title;
    public string Text => Info.Text;
    public string IconKey => PrivacyThemeGroup.IconFor(Info.Theme);
    public string State { get; }
    public bool CanOpen => Info.SettingsUri is not null;
    public ICommand OpenCommand { get; }
}
