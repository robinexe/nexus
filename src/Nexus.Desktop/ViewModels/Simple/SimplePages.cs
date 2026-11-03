using System.Collections.ObjectModel;
using System.IO;
using Nexus.Assistant;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.Views.Controls;
using Nexus.Desktop.Views.Shell;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;
using Nexus.Persistence;

namespace Nexus.Desktop.ViewModels.Simple;

// =====================================================================================================================
// Modo simples (pedido: "facilitar a vida do usuário leigo"). Seis telas, uma palavra cada. Nenhuma tem lógica de escrita
// própria: cada uma é uma vitrine simples sobre uma página completa que já existe (Assistente, Limpeza, Placa de vídeo,
// Medir, diagnóstico, diário) e escreve só pelos mesmos caminhos (IModuleHost.ApplyAsync/RevertAsync, com confirmação).
// A lógica que decide o que mostrar está em SimpleLogic.cs (pura, testada).
// =====================================================================================================================

/// <summary>
/// Cartão "O que dá para melhorar": uma frase e UM botão. O botão aplica com um clique quando o NEXUS sabe resolver (limpar,
/// ajustes seguros, achado com ação de risco baixo) e mostra "Feito ✓ — Desfazer" no próprio cartão; quando a solução é com
/// você (comprar memória, BIOS), "Ver como" abre a explicação ali mesmo; o driver de vídeo abre Atualizar drivers já procurando.
/// </summary>
public sealed class ImproveCardRow : ObservableObject
{
    private bool _showHowTo;

    public ImproveCardRow(ImproveCard card, OneClickAction? oneClick, RelayCommand? open)
    {
        Card = card;
        OneClick = oneClick;
        OpenCommand = open;
        ToggleHowToCommand = new RelayCommand(() => ShowHowTo = !ShowHowTo);
    }

    public ImproveCard Card { get; }
    public string Text => Card.Text;
    public string IconKey => Card.IconKey;
    public string HowTo => Card.HowTo ?? "";
    /// <summary>Aplica com um clique (null quando a solução não é uma ação do NEXUS).</summary>
    public OneClickAction? OneClick { get; }
    public bool IsOneClick => OneClick is not null;
    /// <summary>Abre outra tela já fazendo a primeira parte (driver de vídeo: procurar a versão nova).</summary>
    public RelayCommand? OpenCommand { get; }
    public bool IsOpen => OneClick is null && OpenCommand is not null;
    /// <summary>A solução é com você: o botão abre a explicação no próprio cartão.</summary>
    public bool IsHowTo => OneClick is null && OpenCommand is null;
    public string ActionText => IsHowTo ? Loc.T("Simple.Card.SeeHow") : Card.ActionText;
    public bool ShowHowTo { get => _showHowTo; private set => Set(ref _showHowTo, value); }
    public RelayCommand ToggleHowToCommand { get; }
    /// <summary>Clique em andamento ou feito: o cartão fica até a próxima vez (o resultado e o Desfazer continuam à vista).</summary>
    internal bool Pinned => OneClick is { IsReady: false } || OneClick is { HasNote: true };
}

/// <summary>
/// Início: o anel com a palavra grande ("Seu PC está ótimo / pode melhorar / precisa de atenção"), um botão
/// "Otimizar meu PC com segurança" e no máximo 3 cartões, cada um resolvido com um clique onde está.
/// </summary>
public sealed class HomePage : ModulePage
{
    private HealthInputs _inputs = new();
    private HealthResult _result = new(double.NaN, HealthLevel.Great);
    private IReadOnlyList<ImproveCardRow> _cards = [];

    public HomePage() => OptimizeCommand = new RelayCommand(StartOptimize, () => Host?.Facts is not null);

    public override string Title => Loc.T("Simple.Nav.Home");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Home.Subtitle");

    /// <summary>De onde vêm os pedidos dos cartões (a janela).</summary>
    internal ISimpleActions? Actions { get; set; }

    /// <summary>Chamado pela janela sempre que uma leitura muda (diagnóstico, limpeza medida, catálogo relido).</summary>
    public void Update(HealthInputs inputs)
    {
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        _result = inputs.HasData ? SimpleHealth.Compute(inputs) : new HealthResult(double.NaN, HealthLevel.Great);
        if (!inputs.HasData) Cards = [];
        else
        {
            // Cartão em que o usuário acabou de clicar continua (com "Feito ✓ — Desfazer"); os outros lugares vêm da leitura nova.
            var kept = _cards.Where(c => c.Pinned).ToList();
            var fresh = ImproveCards.Top(inputs, ImproveCards.Max + kept.Count).Where(c => kept.All(k => k.Card.Kind != c.Kind))
                .Take(Math.Max(0, ImproveCards.Max - kept.Count)).Select(Row);
            Cards = [.. kept, .. fresh];
        }
        foreach (var n in new[] { nameof(IsLoading), nameof(HasData), nameof(Score), nameof(Level), nameof(Headline), nameof(Word), nameof(Tone),
                     nameof(HasCards), nameof(NoCards) })
            OnPropertyChanged(n);
    }

    private QuickApplier? Applier() => Host is null ? null : new QuickApplier(Host);

    private ImproveCardRow Row(ImproveCard c)
    {
        var a = Actions;
        switch (c.Kind)
        {
            case ImproveKind.Cleanup or ImproveKind.LowDisk when a is not null:
                var bytes = ImproveCards.Bytes(_inputs.RecoverableBytes);
                return new ImproveCardRow(c, new OneClickAction(Loc.T("Simple.Card.CleanupAction"), Applier,
                    q => q.ApplyAsync(Loc.T("Simple.Clean.Now"), Loc.F("Simple.One.ConfirmClean", bytes), a.SafeCleanupRequests()), a.CleanupChanged), null);
            case ImproveKind.PendingTweaks when a is not null:
                return new ImproveCardRow(c, new OneClickAction(Loc.T("Simple.Card.TweaksAction"), Applier, q =>
                {
                    var items = a.PendingTweakRequests();
                    return q.ApplyAsync(Loc.T("Simple.Quick.Title"), Loc.F("Simple.One.ConfirmTweaks", items.Count), items);
                }), null);
            case ImproveKind.Finding when a is not null && c.FixActionId is { } fix && a.LowRiskRequest(fix) is { } req:
                return new ImproveCardRow(c, new OneClickAction(Loc.T("Simple.One.Fix"), Applier,
                    q => q.ApplyAsync(Loc.T("Simple.One.Fix"), Loc.F("Simple.One.ConfirmFix", c.Text), [req])), null);
            case ImproveKind.GpuDriver:
                return new ImproveCardRow(c, null, new RelayCommand(() => _ = OpenDriversAsync()));
            default:
                return new ImproveCardRow(c, null, null);
        }
    }

    /// <summary>"Atualizar" o driver de vídeo: abre Atualizar drivers e já procura a versão nova da placa dedicada (só leitura).</summary>
    private async Task OpenDriversAsync()
    {
        if (Host?.FindPage(typeof(DriverUpdatesPage)) is not DriverUpdatesPage page) return;
        Host.Navigate(page);
        await page.OnShownAsync();
        var card = page.Gpus.FirstOrDefault(g => !g.Status.Integrated && g.CanUpdateInApp) ?? page.Gpus.FirstOrDefault(g => g.CanUpdateInApp);
        if (card?.CheckCommand is { } check && check.CanExecute(null)) check.Execute(null);
    }

    public HealthInputs Inputs => _inputs;
    public bool IsLoading => !_inputs.HasData;
    public bool HasData => _inputs.HasData;
    /// <summary>0–100 (NaN antes da primeira leitura): enche o anel.</summary>
    public double Score => _result.Score;
    public HealthLevel Level => _result.Level;
    public string Headline => HasData ? SimpleHealth.Headline(_result.Level) : Loc.T("Simple.Home.Reading");
    public string Word => HasData ? SimpleHealth.Word(_result.Level) : "";
    public Tone Tone => !HasData ? Tone.Info : _result.Level switch { HealthLevel.Great => Tone.Good, HealthLevel.CouldImprove => Tone.Warn, _ => Tone.Bad };
    public IReadOnlyList<ImproveCardRow> Cards { get => _cards; private set => Set(ref _cards, value); }
    public bool HasCards => _cards.Count > 0;
    public bool NoCards => HasData && _cards.Count == 0;
    public RelayCommand OptimizeCommand { get; }

    private void StartOptimize()
    {
        if (Host?.FindPage(typeof(SimpleOptimizePage)) is not SimpleOptimizePage page) return;
        Host.Navigate(page);
        if (page.Assistant.StartAutoCommand.CanExecute(null)) page.Assistant.StartAutoCommand.Execute(null);
    }
}

/// <summary>Um dos 4 itens da tela Otimizar ("Limpar arquivos temporários"…), com o estado da rodada automática.</summary>
public sealed record OptimizePhaseRow(string Title, string IconKey, bool IsDone, bool IsCurrent)
{
    public Tone Tone => IsDone ? Tone.Good : IsCurrent ? Tone.Warn : Tone.Neutral;
    public string StateIcon => IsDone ? "Icon.Check" : IconKey;
}

/// <summary>
/// Otimizar (modo simples): o Assistente completo no modo automático. Antes: os 4 itens e "Começar". Durante: anel com o
/// andamento da rodada inteira e o item atual. Depois: "Pronto! Liberamos X e ajustamos Y coisas.", a palavra da saúde,
/// "Ver detalhes" (o relatório completo) e "Desfazer tudo". O passo a passo continua no modo completo.
/// </summary>
public sealed class SimpleOptimizePage : ModulePage
{
    private static string PhaseTitle(AutoPilot.Phase p) => p switch
    {
        AutoPilot.Phase.Clean => Loc.T("Simple.Opt.PhaseClean"),
        AutoPilot.Phase.Tune => Loc.T("Simple.Opt.PhaseTune"),
        AutoPilot.Phase.Startup => Loc.T("Simple.Opt.PhaseStartup"),
        _ => Loc.T("Simple.Opt.PhaseCheck"),
    };

    private static string PhaseIcon(AutoPilot.Phase p) => p switch
    {
        AutoPilot.Phase.Clean => "Icon.Clean",
        AutoPilot.Phase.Tune => "Icon.Games",
        AutoPilot.Phase.Startup => "Icon.Startup",
        _ => "Icon.Security",
    };

    private IReadOnlyList<OptimizePhaseRow> _phases = [];

    public SimpleOptimizePage(AssistantPage assistant, HomePage home)
    {
        Assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        Home = home ?? throw new ArgumentNullException(nameof(home));
        Assistant.AutoProgressChanged += (_, _) => Refresh();
        Assistant.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AssistantPage.AutoPercent)) OnPropertyChanged(nameof(Percent)); };
        Refresh();
    }

    public override string Title => Loc.T("Simple.Nav.Optimize");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Opt.Subtitle");

    /// <summary>A página completa que faz o trabalho (comandos: StartAutoCommand, OpenReportCommand, UndoAllCommand).</summary>
    public AssistantPage Assistant { get; }
    /// <summary>A palavra da saúde depois da rodada ("Seu PC está ótimo").</summary>
    public HomePage Home { get; }
    /// <summary>"Ajustes rápidos": os ajustes recomendados para este PC, para ligar e desligar um a um.</summary>
    public QuickTweaksPanel QuickTweaks { get; } = new();
    public IReadOnlyList<OptimizePhaseRow> Phases { get => _phases; private set => Set(ref _phases, value); }
    public double Percent => Assistant.AutoPercent;
    /// <summary>Ocupada enquanto a rodada roda (o autoteste espera).</summary>
    public bool Running => Assistant.AutoRunning;
    public string CurrentText => Assistant.AutoPhase is { } p ? PhaseTitle(p) : Loc.T("Simple.Opt.Finishing");

    private void Refresh()
    {
        Phases = Enum.GetValues<AutoPilot.Phase>().Select(p => new OptimizePhaseRow(PhaseTitle(p), PhaseIcon(p), Assistant.IsPhaseDone(p), Assistant.AutoPhase == p)).ToList();
        foreach (var n in new[] { nameof(Percent), nameof(Running), nameof(CurrentText) }) OnPropertyChanged(n);
    }
}

/// <summary>
/// Limpar (modo simples): um número ("X GB podem ser liberados"), "Limpar agora" (só regras de risco baixo, para a quarentena
/// como sempre) e "Esvaziar lixeira do NEXUS". A lista fica escondida até "Ver o que será limpo".
/// </summary>
public sealed class SimpleCleanPage : ModulePage
{
    private bool _showList;

    public SimpleCleanPage(CleaningPage cleaning)
    {
        Cleaning = cleaning ?? throw new ArgumentNullException(nameof(cleaning));
        ToggleListCommand = new RelayCommand(() => ShowList = !ShowList);
        Cleaning.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CleaningPage.Scanning)) { OnPropertyChanged(nameof(Scanning)); OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(ScanFailed)); }
            if (e.PropertyName is nameof(CleaningPage.SafeBytes)) { OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(NothingToClean)); OnPropertyChanged(nameof(HasSomething)); }
        };
    }

    public override string Title => Loc.T("Simple.Nav.Clean");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Clean.Subtitle");

    /// <summary>A página completa (SafeBytes, SafeItems, CleanSafeCommand, PurgeAllCommand, UndoLastCommand).</summary>
    public CleaningPage Cleaning { get; }
    public bool Scanning => Cleaning.Scanning;
    /// <summary>Medindo agora (o número mostra "—" se a medição não deu certo; a mensagem da Limpeza explica).</summary>
    public bool IsLoading => Cleaning.Scanning;
    public bool HasSomething => Cleaning.SafeBytes > 0;
    /// <summary>A medição terminou sem número (erro de leitura): mostra o motivo que a Limpeza escreveu.</summary>
    public bool ScanFailed => !Cleaning.Scanning && double.IsNaN(Cleaning.SafeBytes) && Cleaning.RecoverableCaption.Length > 0 && !IsFirstScanPending;
    private bool IsFirstScanPending => Cleaning.RecoverableCaption == Loc.T("Clean.MedindoPodeSerLiberado");
    public bool NothingToClean => !Cleaning.Scanning && Cleaning.SafeBytes is 0;
    public bool ShowList { get => _showList; private set => Set(ref _showList, value); }
    public RelayCommand ToggleListCommand { get; }

    public override Task OnShownAsync() => Cleaning.OnShownAsync();
}

/// <summary>Uma opção de um jogo, em palavras simples, com o botão de um clique ("Ligar" → "Feito ✓ — Desfazer").</summary>
public sealed record GameOption(string Text, OneClickAction Action);

/// <summary>Um jogo na tela Jogos: nome, inicial (no lugar do ícone), as opções que valem para ele e "Medir FPS".</summary>
public sealed class SimpleGameRow(string name, string exe, IReadOnlyList<GameOption> options, bool windowedOn, RelayCommand measure)
{
    public string Name { get; } = name;
    /// <summary>Nome do executável (ex.: cs2.exe), só para Medir.</summary>
    public string Exe { get; } = exe;
    public string Initial => Name.Length > 0 ? char.ToUpper(Name[0], Loc.Culture).ToString() : "?";
    public IReadOnlyList<GameOption> Options { get; } = options;
    public bool HasOptions => Options.Count > 0;
    public bool WindowedOn { get; } = windowedOn;
    public RelayCommand MeasureCommand { get; } = measure;
    /// <summary>"Por que travou?" da última partida medida, em uma linha ("12 travadas: a maioria provavelmente por…"), ou vazio.</summary>
    public string StutterVerdict { get; init; } = "";
    public bool HasStutterVerdict => StutterVerdict.Length > 0;
}

/// <summary>
/// Jogos: os jogos achados nas lojas (Steam, Epic…) em cartões; para cada um, "Medir FPS" e só as opções que valem para ele, em
/// palavras simples, cada uma com um clique (as mesmas ações do driver NVIDIA por jogo e de "jogos em janela" da página Placa de
/// vídeo), e um resumo do Modo de jogo do Windows com "Ligar" de um clique. Tudo pelo aplicador, com uma frase de confirmação.
/// </summary>
public sealed class SimpleGamesPage : ModulePage
{
    private bool _loading = true;
    private bool? _gameModeOn;
    private OneClickAction? _gameModeAction;

    public SimpleGamesPage(GpuLabPage gpu, MeasurePage measure)
    {
        Gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        Measure = measure ?? throw new ArgumentNullException(nameof(measure));
    }

    public override string Title => Loc.T("Simple.Nav.Games");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Games.Subtitle");

    public GpuLabPage Gpu { get; }
    public MeasurePage Measure { get; }
    internal ISimpleActions? Actions { get; set; }
    public ObservableCollection<SimpleGameRow> Games { get; } = [];
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) { OnPropertyChanged(nameof(NoGames)); OnPropertyChanged(nameof(HasGames)); } } }
    public bool HasGames => !_loading && Games.Count > 0;
    public bool NoGames => !_loading && Games.Count == 0;
    /// <summary>"Modo de jogo do Windows: ligado" (do diagnóstico; null = não deu para ler).</summary>
    public string GameModeText => _gameModeOn switch
    {
        true => Loc.T("Simple.Games.GameModeOn"),
        false => Loc.T("Simple.Games.GameModeOff"),
        _ => Loc.T("Simple.Games.GameModeUnknown"),
    };
    public Tone GameModeTone => _gameModeOn switch { true => Tone.Good, false => Tone.Warn, _ => Tone.Neutral };
    /// <summary>Ligar o Modo de jogo com um clique (só quando está desligado e o catálogo tem a ação).</summary>
    public OneClickAction? GameModeAction { get => _gameModeAction; private set { if (Set(ref _gameModeAction, value)) OnPropertyChanged(nameof(HasGameModeAction)); } }
    public bool HasGameModeAction => _gameModeAction is not null;

    /// <summary>A janela informa o Modo de jogo a cada leitura (achado win.gamemode do diagnóstico).</summary>
    public void SetGameMode(bool? on, string? fixActionId)
    {
        _gameModeOn = on;
        if (on == false && Actions is { } a && fixActionId is not null && a.LowRiskRequest(fixActionId) is { } req)
            GameModeAction ??= new OneClickAction(Loc.T("Simple.Games.TurnOn"), Applier,
                q => q.ApplyAsync(Loc.T("Simple.Games.GameModeOff"), Loc.T("Simple.Games.ConfirmGameMode"), [req]));
        else if (_gameModeAction is not { HasNote: true }) GameModeAction = null; // depois de ligar, o "Feito ✓ — Desfazer" fica
        OnPropertyChanged(nameof(GameModeText));
        OnPropertyChanged(nameof(GameModeTone));
    }

    private QuickApplier? Applier() => Host is null ? null : new QuickApplier(Host);

    public override async Task OnShownAsync()
    {
        IsLoading = true;
        try
        {
            await Gpu.OnShownAsync(); // leitura da placa e da biblioteca de jogos (só leitura)
            Build();
        }
        finally { IsLoading = false; }
    }

    private void Build()
    {
        Games.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nv = Gpu.NvAvailable ? Gpu.NvGames.ToDictionary(g => g.Exe, StringComparer.OrdinalIgnoreCase) : [];
        foreach (var w in Gpu.WindowedGames)
        {
            var exe = Path.GetFileName(w.ExePath);
            if (string.IsNullOrEmpty(exe) || !seen.Add(exe)) continue;
            Games.Add(Make(w.Name, exe, nv.ContainsKey(exe), w.IsOn ? null : w.ExePath, w.IsOn));
        }
        foreach (var g in nv.Values)
            if (seen.Add(g.Exe)) Games.Add(Make(g.Name, g.Exe, true, null, false));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(NoGames));
    }

    /// <summary>As opções que valem para o jogo: driver NVIDIA (administrador) e jogos em janela (sua conta).</summary>
    private SimpleGameRow Make(string name, string exe, bool nvidia, string? windowedPath, bool windowedOn)
    {
        var options = new List<GameOption>();
        if (nvidia)
        {
            var args = new Dictionary<string, string> { ["exe"] = exe };
            options.Add(Option(name, Loc.T("Simple.Games.LowLatency"), new ActionRequest(NvidiaDrsActions.LowLatency, null, args), BrokerLevel.Admin));
            options.Add(Option(name, Loc.T("Simple.Games.MaxPower"), new ActionRequest(NvidiaDrsActions.MaxPower, null, args), BrokerLevel.Admin));
            if (Gpu.SelectedNvFps is { } fps && NvidiaDrsActions.FpsCaps.Contains(fps))
                options.Add(Option(name, Loc.F("Simple.Games.FpsCap", fps), new ActionRequest(NvidiaDrsActions.FpsCap, fps, args), BrokerLevel.Admin));
        }
        if (windowedPath is not null)
            options.Add(Option(name, Loc.T("Simple.Games.Windowed"),
                new ActionRequest(GpuLabPage.WindowedPerAppAction, null, new Dictionary<string, string> { ["gameExePath"] = windowedPath }), BrokerLevel.User));
        return new SimpleGameRow(name, exe, options, windowedOn, new RelayCommand(() => { Measure.ManualExe = exe; Host?.Navigate(Measure); }))
        {
            StutterVerdict = Measure.LatestStutterVerdict(exe),
        };
    }

    private GameOption Option(string game, string text, ActionRequest request, BrokerLevel level) =>
        new(text, new OneClickAction(Loc.T("Simple.Games.TurnOn"), Applier, q => q.ApplyAsync(game,
            level == BrokerLevel.Admin ? Loc.F("Simple.Games.ConfirmAdmin", game, text) : Loc.F("Simple.Games.Confirm", game, text), [(request, level)])));
}

/// <summary>Um problema que precisa do usuário: uma frase, o que fazer (1–2 frases) e, quando o NEXUS resolve, um botão de um clique.</summary>
public sealed record ProblemRow(string Key, string Title, string WhatToDo, string IconKey, int Impact, string Target = "", string ActionText = "")
{
    /// <summary>Resolve com um clique ("Resolver" → "Feito ✓ — Desfazer").</summary>
    public OneClickAction? Fix { get; init; }
    public bool HasFix => Fix is not null;
    /// <summary>Sem ação do NEXUS: abre a tela que explica/resolve.</summary>
    public bool HasTarget => Fix is null && Target.Length > 0;
    public RelayCommand? OpenCommand { get; init; }
    public bool HasLink => OpenCommand is not null;
}

/// <summary>
/// Problemas: o que precisa de você (diagnóstico + "só você pode fazer" + o que o Assistente anotou), cada um com "O que fazer",
/// do maior impacto para o menor, e "Resolver" de um clique quando o NEXUS sabe resolver; o que está bem fica recolhido.
/// </summary>
public sealed class SimpleProblemsPage : ModulePage
{
    private IReadOnlyList<ProblemRow> _items = [];
    private IReadOnlyList<string> _fine = [];
    private bool _hasData;

    public override string Title => Loc.T("Simple.Nav.Problems");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Problems.Subtitle");

    internal ISimpleActions? Actions { get; set; }
    public IReadOnlyList<ProblemRow> Items { get => _items; private set => Set(ref _items, value); }
    public bool IsLoading => !_hasData;
    public bool HasItems => _items.Count > 0;
    public bool NoItems => _hasData && _items.Count == 0;
    public IReadOnlyList<string> FineTopics { get => _fine; private set => Set(ref _fine, value); }
    public bool HasFine => _fine.Count > 0;
    public string FineHeader => Loc.F("Simple.Problems.FineHeader", string.Join(", ", _fine));
    public string CountText => _items.Count == 1 ? Loc.T("Simple.Problems.Count1") : Loc.F("Simple.Problems.CountN", _items.Count);

    private QuickApplier? Applier() => Host is null ? null : new QuickApplier(Host);

    /// <summary>A janela chama a cada leitura (achados do diagnóstico e do "só você pode fazer"; ações anotadas pelo Assistente).</summary>
    public void Update(IReadOnlyList<DiagnosticFinding> findings, IReadOnlyList<UserAction> userActions)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(userActions);
        // Linha em que o usuário acabou de clicar fica (com "Feito ✓ — Desfazer") mesmo que o problema tenha sumido da leitura.
        var kept = _items.Where(r => r.Fix is { IsReady: false } || r.Fix is { HasNote: true }).ToList();
        var rows = new List<ProblemRow>(kept);
        foreach (var f in findings.Where(f => f.Severity == DiagnosticSeverity.Attention && kept.All(k => k.Key != f.Id)))
        {
            var p = PlainFindings.For(f);
            var (target, action) = TargetFor(f.Id);
            var fix = Actions is { } a && f.RelatedActionId is { } id && a.LowRiskRequest(id) is { } req
                ? new OneClickAction(Loc.T("Simple.One.Fix"), Applier, q => q.ApplyAsync(Loc.T("Simple.One.Fix"), Loc.F("Simple.One.ConfirmFix", p.Title), [req]))
                : null;
            rows.Add(new ProblemRow(f.Id, p.Title, p.WhatToDo, p.IconKey, PlainFindings.ImpactOf(f), target, action) { Fix = fix });
        }
        foreach (var u in userActions.Where(u => rows.All(r => r.Title != u.Title)))
            rows.Add(new ProblemRow("user:" + u.Title, u.Title, u.Why, "Icon.User", 30)
            {
                OpenCommand = u.Link is { } link && link.StartsWith("https://", StringComparison.Ordinal) ? new RelayCommand(() => OpenLink(link)) : null,
            });
        Items = [.. rows.OrderByDescending(r => r.Impact)];
        FineTopics = PlainFindings.FineTopics(findings);
        _hasData = findings.Count > 0;
        foreach (var n in new[] { nameof(IsLoading), nameof(HasItems), nameof(NoItems), nameof(HasFine), nameof(FineHeader), nameof(CountText) }) OnPropertyChanged(n);
    }

    /// <summary>Onde resolver quando não há ação de um clique: limpeza e otimizar ficam nas telas simples; hardware e BIOS não têm botão (é com você); o resto abre a página completa.</summary>
    private static (string Target, string Action) TargetFor(string id) => id switch
    {
        "win.gamemode" or "win.gamedvr.capture" => ("SimpleOptimizePage", Loc.T("Simple.Card.TweaksAction")),
        _ when id.StartsWith("startup.", StringComparison.Ordinal) => ("SimpleOptimizePage", Loc.T("Simple.Card.TweaksAction")),
        "disk.system-free" => ("SimpleCleanPage", Loc.T("Simple.Card.CleanupAction")),
        _ when id.StartsWith("mem.", StringComparison.Ordinal) || id.StartsWith("owner.", StringComparison.Ordinal) || id == "disk.system-media" => ("", ""),
        _ => (ShellMap.PageForFinding(id), Loc.T("Simple.Problems.Details")),
    };

    private void OpenLink(string url)
    {
        if (Host is null || !Host.Confirm(Loc.T("Asst.AbrirNavegador"), Loc.F("Asst.AbrirEstaPaginaNavegador", url))) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = ex.Message; }
    }
}

/// <summary>Uma linha do Histórico simples: "Hoje 14:30 — Limpeza: 12 itens limpos — Desfazer".</summary>
public sealed record SimpleHistoryRow(Guid RunId, BrokerLevel Level, DateTimeOffset Started, string When, string What, bool Active, IReadOnlyList<string> Keys)
{
    public bool Undone => !Active;
    public RelayCommand? UndoCommand { get; init; }
}

/// <summary>
/// Histórico (modo simples): "O que o NEXUS mudou", das duas contas do aplicador (sua conta e sistema), em palavras simples,
/// com "Desfazer" em cada linha e "Desfazer tudo". Leitura do diário só leitura; desfazer só pelo aplicador (IModuleHost).
/// </summary>
public sealed class SimpleHistoryPage : ModulePage
{
    private IReadOnlyList<SimpleHistoryRow> _rows = [];
    private bool _loaded;

    public SimpleHistoryPage() => UndoAllCommand = new RelayCommand(() => _ = RunAsync(UndoAllAsync), () => !Busy && Host is not null && _rows.Any(r => r.Active));

    public override string Title => Loc.T("Simple.Nav.History");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Hist.Subtitle");

    public IReadOnlyList<SimpleHistoryRow> Rows { get => _rows; private set => Set(ref _rows, value); }
    public bool HasRows => _rows.Count > 0;
    public bool NoRows => _loaded && _rows.Count == 0;
    public RelayCommand UndoAllCommand { get; }

    /// <summary>
    /// A execução que ligou só este ajuste e ainda vale (a mais nova), para desligar nos Ajustes rápidos sem desfazer outras coisas
    /// junto. Null se ele foi feito junto com outros (aí, Desfazer aqui no Histórico).
    /// </summary>
    public (Guid Run, BrokerLevel Level)? FindSingleActionRun(string actionKey) =>
        _rows.Where(r => r.Active && r.Keys.Count == 1 && r.Keys[0] == actionKey).OrderByDescending(r => r.Started)
            .Select(r => ((Guid, BrokerLevel)?)(r.RunId, r.Level)).FirstOrDefault();

    public override Task OnShownAsync() => LoadAsync();

    /// <summary>Relê os dois diários (fora da thread da interface).</summary>
    public async Task LoadAsync()
    {
        var read = await Task.Run(() => Read(BrokerLevel.User).Concat(Read(BrokerLevel.Admin)).ToList());
        var now = DateTime.Now;
        Rows = read.OrderByDescending(r => r.Started)
            .Select(r => r with
            {
                When = SimpleHistoryText.When(r.Started.LocalDateTime, now),
                UndoCommand = r.Active ? new RelayCommand(() => _ = RunAsync(() => UndoAsync(r)), () => !Busy && Host is not null) : null,
            }).ToList();
        _loaded = true;
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(NoRows));
    }

    private static List<SimpleHistoryRow> Read(BrokerLevel level)
    {
        var path = LocalBroker.JournalPath(level);
        var list = new List<SimpleHistoryRow>();
        if (!File.Exists(path)) return list;
        try
        {
            using var journal = SqliteJournal.OpenReadOnly(path);
            foreach (var run in journal.Runs())
            {
                var entries = journal.Entries(run.RunId);
                if (entries.Count == 0) continue;
                var keys = entries.Select(e => e.Action.Definition.Key).ToList();
                var active = entries.Any(e => e.State is JournalState.Applied or JournalState.Verified);
                // Só o que mudou algo de verdade (execução recusada ou já igual não é "o que o NEXUS mudou").
                if (!active && !entries.Any(e => e.State == JournalState.Reverted)) continue;
                list.Add(new SimpleHistoryRow(run.RunId, level, run.StartedAt, "", SimpleHistoryText.Describe(keys), active, keys));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { } // diário ilegível: a tela só fica sem as linhas dele
        return list;
    }

    private async Task UndoAsync(SimpleHistoryRow row)
    {
        if (Host is null) return;
        var r = await Host.RevertAsync(row.RunId, row.Level);
        if (r is not null) Message = r.Ok ? Loc.T("Simple.Hist.Undone") : Loc.T("Simple.Hist.UndoFailed");
        await LoadAsync();
    }

    private async Task UndoAllAsync()
    {
        if (Host is null) return;
        var active = _rows.Where(r => r.Active).ToList();
        if (active.Count == 0) return;
        if (!Host.Confirm(Loc.T("Simple.Hist.UndoAll"), Loc.F("Simple.Hist.UndoAllConfirm", active.Count))) return;
        var failed = 0;
        foreach (var row in active) // mais nova primeiro
        {
            var r = await Host.RevertWithoutConfirmationAsync(row.RunId, row.Level);
            if (r is null || !r.Ok) failed++;
        }
        Message = failed == 0 ? Loc.T("Simple.Hist.UndoneAll") : Loc.F("Simple.Hist.UndoneSome", active.Count - failed, active.Count);
        await LoadAsync();
    }
}

/// <summary>
/// Boas-vindas da primeira abertura (uma vez, só para quem nunca usou o NEXUS): 2 telas curtas — o que o NEXUS faz, e que é
/// seguro (nada muda sem você) e tudo se desfaz. Dá para pular. Ao fechar, a janela grava que já passou por aqui.
/// </summary>
public sealed class WelcomeFlow : ObservableObject
{
    public const int Steps = 2;
    private int _step;
    private bool _isVisible;
    private readonly Action _done;

    public WelcomeFlow(Action done)
    {
        _done = done ?? throw new ArgumentNullException(nameof(done));
        NextCommand = new RelayCommand(() => Step++, () => _step < Steps - 1);
        SkipCommand = new RelayCommand(Finish);
        StartCommand = new RelayCommand(Finish);
    }

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    public int Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, Math.Clamp(value, 0, Steps - 1))) return;
            foreach (var n in new[] { nameof(IsFirst), nameof(IsLast), nameof(NotLast), nameof(Dots) }) OnPropertyChanged(n);
        }
    }
    public bool IsFirst => _step == 0;
    public bool IsLast => _step == Steps - 1;
    public bool NotLast => !IsLast;
    /// <summary>Os pontos de "onde estou" (true = a tela atual).</summary>
    public IReadOnlyList<bool> Dots => Enumerable.Range(0, Steps).Select(i => i == _step).ToList();
    public RelayCommand NextCommand { get; }
    public RelayCommand SkipCommand { get; }
    /// <summary>"Começar" na última tela.</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>Mostra a partir de uma tela (o autoteste fotografa as duas).</summary>
    internal void Show(int step = 0)
    {
        _step = -1;
        Step = step;
        IsVisible = true;
    }

    private void Finish()
    {
        IsVisible = false;
        _done();
    }
}

/// <summary>Um ladrilho de "Ferramentas avançadas": ícone, uma palavra (o nome da página) e uma linha do que ela faz.</summary>
public sealed record AdvancedTile(string Title, string Description, string IconKey, string Target);

/// <summary>
/// Ferramentas avançadas: todas as páginas completas de antes (Meu PC, Hardware, Diagnóstico, laboratórios, Programas, Serviços,
/// Ajustes…) como ladrilhos grandes. É o único caminho para elas (um link discreto no pé da navegação), para a lista lateral
/// continuar com 6 itens. Também guarda as ações de leitura que saíram do cabeçalho (exportar a leitura, ver tudo como administrador).
/// </summary>
public sealed class AdvancedToolsPage : ModulePage
{
    private IReadOnlyList<AdvancedTile> _tiles = [];

    public override string Title => Loc.T("Simple.Adv.Title");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Adv.Subtitle");

    public IReadOnlyList<AdvancedTile> Tiles { get => _tiles; private set => Set(ref _tiles, value); }
    public RelayCommand? ExportCommand { get; private set; }
    public RelayCommand? RelaunchAsAdminCommand { get; private set; }
    public bool CanRelaunchAsAdmin { get; private set; }

    /// <summary>A janela informa as páginas e os comandos dela (uma vez, ao montar).</summary>
    internal void Setup(IReadOnlyList<PageViewModel> pages, RelayCommand export, RelayCommand relaunch, bool canRelaunch)
    {
        ArgumentNullException.ThrowIfNull(pages);
        // Ajustes primeiro (é o que mais se procura aqui); depois a ordem de antes (Meu PC, laboratórios, otimizar, limpar…).
        Tiles = [.. pages.OrderBy(p => p is SettingsPage ? -1 : ShellMap.GroupRank(p.Group))
            .Select(p => new AdvancedTile(p.Title, p.Subtitle, ShellMap.IconFor(p.GetType().Name), p.GetType().Name))];
        ExportCommand = export;
        RelaunchAsAdminCommand = relaunch;
        CanRelaunchAsAdmin = canRelaunch;
    }
}
