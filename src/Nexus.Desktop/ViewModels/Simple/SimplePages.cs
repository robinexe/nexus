using System.Collections.ObjectModel;
using System.IO;
using Nexus.Assistant;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.Views.Controls;
using Nexus.Desktop.Views.Shell;
using Nexus.GameProfiles;
using Nexus.GameProfiles.Fortnite;
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
    /// <summary>Checagens do PC (monitor abaixo da taxa: o cartão leva ao "Usar 144 Hz" da página Placa de vídeo).</summary>
    internal PcChecksCoordinator? Checks { get; set; }

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
            case ImproveKind.Monitor when c.FindingId is { } fid && Checks?.State.DisplayFor(fid) is { MaxHz: { } max } m:
                return new ImproveCardRow(c with { ActionText = PcCheckText.UseHz(max) }, null, new RelayCommand(() => _ = MonitorFix.RunAsync(Host, m.Device)));
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

/// <summary>Uma opção de um jogo, em palavras simples, com o botão de um clique ("Ligar" → "Feito ✓ — Desfazer"; já ligada: "✓ Ligado — Desfazer").</summary>
/// <param name="Fit">Veredito do hardware (selo discreto "Recomendado para o seu PC" / "Não indicado…"); neutro = sem selo.</param>
public sealed record GameOption(string Text, OneClickAction Action, Fit Fit)
{
    public string FitNote => FitText.Sentence(Fit);
    public bool HasFitNote => FitNote.Length > 0;
    public Views.Controls.Tone FitTone => Fit.IsRecommended ? Views.Controls.Tone.Good : Views.Controls.Tone.Warn;
}

/// <summary>Um jogo na tela Jogos: nome, inicial (no lugar do ícone), as opções que valem para ele e "Medir FPS".</summary>
public sealed class SimpleGameRow(string name, string exe, IReadOnlyList<GameOption> options, RelayCommand measure) : ObservableObject
{
    private string _status = "";

    public string Name { get; } = name;
    /// <summary>Nome do executável (ex.: cs2.exe), só para Medir.</summary>
    public string Exe { get; } = exe;
    public string Initial => Name.Length > 0 ? char.ToUpper(Name[0], Loc.Culture).ToString() : "?";
    public IReadOnlyList<GameOption> Options { get; } = options;
    public bool HasOptions => Options.Count > 0;
    public RelayCommand MeasureCommand { get; } = measure;
    /// <summary>"Aplicar o recomendado para este PC": as opções recomendadas pelo hardware que ainda não estão ligadas (um clique, confirmação, desfazer). Null = nada a aplicar.</summary>
    public OneClickAction? ApplyRecommended { get; init; }
    public bool HasApplyRecommended => ApplyRecommended is not null;
    /// <summary>"Por que travou?" da última partida medida, em uma linha ("12 travadas: a maioria provavelmente por…"), ou vazio.</summary>
    public string StutterVerdict { get; init; } = "";
    public bool HasStutterVerdict => StutterVerdict.Length > 0;
    /// <summary>Estado atual lido do jogo (Fortnite: "Agora: Modo desempenho · limite 141 FPS · Reflex ligado"), ou vazio. Relido depois de aplicar.</summary>
    public string Status { get => _status; set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    /// <summary>Aviso fixo do jogo (Fortnite: "feche o Fortnite antes"), ou vazio.</summary>
    public string Note { get; init; } = "";
    public bool HasNote => Note.Length > 0;
    /// <summary>O jogo está aberto agora e as opções dele não podem ser aplicadas.</summary>
    public bool IsRunning { get; init; }
}

/// <summary>Leitura real no Windows (só leitura): banco de perfis NVIDIA, UserGpuPreferences do DirectX e o arquivo do Fortnite.</summary>
internal sealed class WindowsGameStateSource : IGameStateSource
{
    public INvidiaDrsReader Nvidia { get; } = NvidiaDrsFactory.Reader();

    public int? WindowedToken(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            return GameLibraries.ParseToken(key?.GetValue(exePath) as string, GameOptionStates.WindowedToken);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public FortniteState Fortnite()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? FortniteState.NotFound : FortniteState.Read(local);
    }
}

/// <summary>
/// Jogos: os jogos achados nas lojas (Steam, Epic…) em cartões; para cada um, "Medir FPS" e só as opções que valem para ele, em
/// palavras simples, cada uma com um clique (as mesmas ações do driver NVIDIA por jogo e de "jogos em janela" da página Placa de
/// vídeo), e um resumo do Modo de jogo do Windows com "Ligar" de um clique. Tudo pelo aplicador, com uma frase de confirmação.
/// Cada opção mostra o estado REAL (lido do driver, do registro e do arquivo do jogo a cada abertura e depois de aplicar):
/// ligada → "✓ Ligado — Desfazer" (desfaz pela execução do diário que só mexeu nela; feita junto com outras → Histórico).
/// </summary>
public sealed class SimpleGamesPage : ModulePage
{
    private bool _loading = true;
    private bool? _gameModeOn;
    private OneClickAction? _gameModeAction;
    // O botão de cada opção é o mesmo entre aberturas da tela (o "Feito ✓ — Desfazer" desta sessão não se perde ao navegar).
    private readonly Dictionary<string, OneClickAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OptionStatus> _states = new(StringComparer.OrdinalIgnoreCase);

    public SimpleGamesPage(GpuLabPage gpu, MeasurePage measure)
    {
        Gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        Measure = measure ?? throw new ArgumentNullException(nameof(measure));
        Session = new GameSessionPanel(() => Host, () => Measure);
        TestMouseCommand = new RelayCommand(OpenMouseTest);
    }

    /// <summary>Sessão de jogo (desligada por padrão): limite temporário dos downloads da Microsoft enquanto um jogo está aberto.</summary>
    public GameSessionPanel Session { get; }
    /// <summary>Teste do mouse (taxa real e irregularidade; só diagnóstico).</summary>
    public RelayCommand TestMouseCommand { get; }

    private static void OpenMouseTest()
    {
        var window = new Views.Modules.Mouse.MouseTestWindow();
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } main) window.Owner = main;
        window.ShowDialog();
    }

    /// <summary>A cada leitura do PC: retoma a sessão de jogo (ligada, ou com um limite para desfazer depois de reabrir o NEXUS).</summary>
    public override Task OnFactsReadyAsync(SystemFacts facts)
    {
        Session.Start();
        return Task.CompletedTask;
    }

    public override string Title => Loc.T("Simple.Nav.Games");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Games.Subtitle");

    public GpuLabPage Gpu { get; }
    public MeasurePage Measure { get; }
    internal ISimpleActions? Actions { get; set; }
    /// <summary>Execuções dos diários (a janela passa as do Histórico, relidas a cada escrita); por padrão lê do disco.</summary>
    internal Func<IReadOnlyList<JournalRun>> Journal { get; set; } = SimpleHistoryPage.ReadJournal;
    /// <summary>De onde vem o estado real das opções (só leitura).</summary>
    internal IGameStateSource StateSource { get; set; } = new WindowsGameStateSource();
    public ObservableCollection<SimpleGameRow> Games { get; } = [];
    public bool IsLoading { get => _loading; private set { if (Set(ref _loading, value)) { OnPropertyChanged(nameof(NoGames)); OnPropertyChanged(nameof(HasGames)); } } }
    public bool HasGames => !_loading && Games.Count > 0;
    public bool NoGames => !_loading && Games.Count == 0;
    /// <summary>"Modo de jogo do Windows: ligado" (do registro; null = não deu para ler).</summary>
    public string GameModeText => _gameModeOn switch
    {
        true => Loc.T("Simple.Games.GameModeOn"),
        false => Loc.T("Simple.Games.GameModeOff"),
        _ => Loc.T("Simple.Games.GameModeUnknown"),
    };
    public Tone GameModeTone => _gameModeOn switch { true => Tone.Good, false => Tone.Warn, _ => Tone.Neutral };
    /// <summary>Ligar o Modo de jogo com um clique; ligado: "✓ Ligado — Desfazer" quando foi o NEXUS que ligou.</summary>
    public OneClickAction? GameModeAction { get => _gameModeAction; private set { if (Set(ref _gameModeAction, value)) OnPropertyChanged(nameof(HasGameModeAction)); } }
    public bool HasGameModeAction => _gameModeAction is not null;

    /// <summary>
    /// A janela informa o Modo de jogo a cada leitura: o achado win.gamemode do diagnóstico e a releitura do catálogo (que é refeita
    /// depois de cada escrita, então vale mais).
    /// </summary>
    public void SetGameMode(bool? fromDiagnosis, string? fixActionId, string? catalogReadState)
    {
        var state = GameOptionStates.GameMode(fromDiagnosis, catalogReadState);
        _gameModeOn = state switch { OptionState.On => true, OptionState.Off => false, _ => null };
        if (_gameModeAction is null && Actions is { } a && fixActionId is not null && a.LowRiskRequest(fixActionId) is { } req)
            GameModeAction = new OneClickAction(Loc.T("Simple.Games.TurnOn"), Applier,
                q => q.ApplyAsync(Loc.T("Simple.Games.GameModeOff"), Loc.T("Simple.Games.ConfirmGameMode"), [req]));
        if (_gameModeAction is { } action && state != OptionState.Unknown)
        {
            var key = Actions?.LowRiskRequest(fixActionId ?? "") is { } r ? r.Request.ActionId : GameOptionStates.GameModeAction;
            action.ShowState(new OptionStatus(state, state == OptionState.On ? OptionJournal.Find(SafeJournal(), [new OptionPart(key)]) : OptionUndo.None));
        }
        OnPropertyChanged(nameof(GameModeText));
        OnPropertyChanged(nameof(GameModeTone));
    }

    private IReadOnlyList<JournalRun> SafeJournal()
    {
        try { return Journal(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
    }

    private QuickApplier? Applier() => Host is null ? null : new QuickApplier(Host);

    public override async Task OnShownAsync()
    {
        IsLoading = Games.Count == 0;
        try
        {
            await Gpu.OnShownAsync(); // leitura da placa e da biblioteca de jogos (só leitura)
            var games = Describe();
            var source = StateSource;
            var (fortnite, states) = await Task.Run(() => (ReadFortnite(), GameStateReader.Read(games.SelectMany(g => g.Plans.Select(p => p.Plan)), source, SafeJournal())));
            _fortnite = fortnite;
            foreach (var (id, status) in states) _states[id] = status;
            Build(games);
        }
        finally { IsLoading = false; }
    }

    /// <summary>Depois de aplicar/desfazer uma opção: relê só aquele jogo (não o PC inteiro) e atualiza os botões dele.</summary>
    private async Task RefreshGameAsync(string exe)
    {
        try
        {
            var game = Describe().FirstOrDefault(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase));
            if (game is null) return;
            var source = StateSource;
            var fortnite = game.IsFortnite;
            var (fn, states) = await Task.Run(() => (fortnite ? ReadFortnite() : _fortnite, GameStateReader.Read(game.Plans.Select(p => p.Plan), source, SafeJournal())));
            foreach (var (id, status) in states)
            {
                _states[id] = status;
                if (_actions.TryGetValue(id, out var action)) action.ShowState(status);
            }
            if (fortnite)
            {
                _fortnite = fn;
                if (Games.FirstOrDefault(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase)) is { } row) row.Status = FortniteStatus(fn.State);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = ex.Message; } // a releitura falhou: o botão fica como estava
    }

    /// <summary>Fortnite: configurações atuais (GameUserSettings.ini do seu perfil) e se o jogo está aberto. Só leitura.</summary>
    private (FortniteState State, bool Running) _fortnite = (FortniteState.NotFound, false);

    private (FortniteState State, bool Running) ReadFortnite() => (StateSource.Fortnite(), FortniteProcesses.AnyRunning());

    /// <summary>Um jogo e as opções que valem para ele (texto + plano: o que pede, como ler, como achar no diário).</summary>
    private sealed record GameDescription(string Name, string Exe, bool IsFortnite, IReadOnlyList<(string Text, GameOptionPlan Plan)> Plans);

    /// <summary>Veredito do hardware para um plano: o pior entre as ações que ele pede (uma "não recomendada" pesa mais que uma "recomendada").</summary>
    private Fit FitOf(GameOptionPlan plan)
    {
        if (Host?.HardwareProfile is not { } hw) return Fit.Neutral;
        var fits = plan.Requests.Select(r => HardwareRules.ForAction(hw, r.Request.ActionId)).ToList();
        return fits.FirstOrDefault(f => f.IsNotRecommended) ?? fits.FirstOrDefault(f => f.IsRecommended) ?? Fit.Neutral;
    }

    /// <summary>Os jogos da biblioteca (jogos em janela + driver NVIDIA) com as opções de cada um. Só a partir das leituras já feitas.</summary>
    private List<GameDescription> Describe()
    {
        var list = new List<GameDescription>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nv = Gpu.NvAvailable ? Gpu.NvGames.ToDictionary(g => g.Exe, StringComparer.OrdinalIgnoreCase) : [];
        foreach (var w in Gpu.WindowedGames)
        {
            var exe = Path.GetFileName(w.ExePath);
            if (string.IsNullOrEmpty(exe) || !seen.Add(exe)) continue;
            list.Add(Describe(w.Name, exe, nv.ContainsKey(exe), w.ExePath));
        }
        foreach (var g in nv.Values)
            if (seen.Add(g.Exe)) list.Add(Describe(g.Name, g.Exe, true, null));
        return list;
    }

    /// <summary>As opções que valem para o jogo: Fortnite (arquivo do jogo), driver NVIDIA (administrador) e jogos em janela (sua conta).</summary>
    private GameDescription Describe(string name, string exe, bool nvidia, string? windowedPath)
    {
        var options = new List<(string, GameOptionPlan)>();
        var fortnite = string.Equals(exe, FortniteSettings.GameExe, StringComparison.OrdinalIgnoreCase);
        if (fortnite) options.AddRange(FortniteOptions());
        if (nvidia)
        {
            options.Add((Loc.T("Simple.Games.LowLatency"), GameOptionPlans.NvLowLatency(exe)));
            options.Add((Loc.T("Simple.Games.MaxPower"), GameOptionPlans.NvMaxPower(exe)));
            if (Gpu.SelectedNvFps is { } fps && NvidiaDrsActions.FpsCaps.Contains(fps))
                options.Add((Loc.F("Simple.Games.FpsCap", fps), GameOptionPlans.NvFpsCap(exe, fps)));
        }
        if (windowedPath is not null) options.Add((Loc.T("Simple.Games.Windowed"), GameOptionPlans.Windowed(exe, windowedPath)));
        return new GameDescription(name, exe, fortnite, options);
    }

    /// <summary>
    /// Fortnite (GameUserSettings.ini, sua conta, com o jogo fechado): Modo desempenho; menos atraso (limite 3 abaixo do monitor +
    /// Reflex nas placas NVIDIA); imagem nítida (sem desfoque). A "mira sem aceleração" não entra: no Windows o jogo já lê o mouse
    /// direto, e a chave bDisableMouseAcceleration não faz nada (mito no catálogo).
    /// </summary>
    private List<(string, GameOptionPlan)> FortniteOptions()
    {
        var list = new List<(string, GameOptionPlan)> { (Loc.T("Simple.Games.Fn.Perf"), GameOptionPlans.FortnitePerformance()) };
        var report = Gpu.Report;
        var monitors = report?.Monitors ?? [];
        var monitor = monitors.FirstOrDefault(m => m.Primary) ?? (monitors.Count > 0 ? monitors[0] : null);
        var cap = monitor is null ? null : FortniteSettings.SuggestedFpsLimit(monitor.Current.Hz);
        var nvidia = report?.Primary?.Vendor == GpuVendor.Nvidia;
        if (GameOptionPlans.FortniteLatency(cap, nvidia) is { } latency)
        {
            var text = (cap, nvidia) switch
            {
                ({ } c, true) => Loc.F("Simple.Games.Fn.LatencyReflex", FortniteSettings.FpsLabel(c)),
                ({ } c, false) => Loc.F("Simple.Games.Fn.LatencyCap", FortniteSettings.FpsLabel(c)),
                _ => Loc.T("Simple.Games.Fn.LatencyReflexOnly"),
            };
            list.Add((text, latency));
        }
        list.Add((Loc.T("Simple.Games.Fn.Clear"), GameOptionPlans.FortniteClear()));
        return list;
    }

    private void Build(IReadOnlyList<GameDescription> games)
    {
        Games.Clear();
        foreach (var g in games)
        {
            var options = g.Plans.Select(p => new GameOption(p.Text, ActionFor(g, p.Text, p.Plan), FitOf(p.Plan))).ToList();
            Games.Add(new SimpleGameRow(g.Name, g.Exe, options, new RelayCommand(() => { Measure.ManualExe = g.Exe; Host?.Navigate(Measure); }))
            {
                ApplyRecommended = ApplyRecommendedFor(g),
                StutterVerdict = Measure.LatestStutterVerdict(g.Exe),
                Status = g.IsFortnite ? FortniteStatus(_fortnite.State) : "",
                Note = g.IsFortnite ? (_fortnite.Running ? Loc.T("Simple.Games.Fn.Running") : Loc.T("Simple.Games.Fn.Note")) : "",
                IsRunning = g.IsFortnite && _fortnite.Running,
            });
        }
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(NoGames));
    }

    /// <summary>
    /// "Aplicar o recomendado para este PC": junta os pedidos das opções recomendadas pelo hardware que ainda não estão ligadas
    /// (estado lido) num clique só, com uma confirmação; o desfazer volta todas. Null quando não há nada recomendado pendente.
    /// O botão é o mesmo entre aberturas (chave "<exe>|recommended").
    /// </summary>
    private OneClickAction? ApplyRecommendedFor(GameDescription game)
    {
        var pending = game.Plans.Where(p => FitOf(p.Plan).IsRecommended && _states.GetValueOrDefault(p.Plan.Id, OptionStatus.Unread).State != OptionState.On).ToList();
        var id = game.Exe + "|recommended";
        if (pending.Count == 0) { _actions.Remove(id); return null; }
        var requests = pending.SelectMany(p => p.Plan.Requests).ToList();
        var names = string.Join(", ", pending.Select(p => p.Text));
        if (!_actions.TryGetValue(id, out var action))
        {
            var exe = game.Exe;
            var title = game.IsFortnite ? "Fortnite" : game.Name;
            var admin = requests.Any(r => r.Level == BrokerLevel.Admin);
            action = new OneClickAction(Loc.T("Simple.Games.ApplyRecommended"), Applier,
                q => q.ApplyAsync(title, admin ? Loc.F("Simple.Games.ConfirmRecommendedAdmin", title, names) : Loc.F("Simple.Games.ConfirmRecommended", title, names), requests),
                () => _ = RefreshGameAsync(exe));
            _actions[id] = action;
        }
        return action;
    }

    /// <summary>O botão da opção (o mesmo da última abertura, se houver), já mostrando o estado lido.</summary>
    private OneClickAction ActionFor(GameDescription game, string text, GameOptionPlan plan)
    {
        if (!_actions.TryGetValue(plan.Id, out var action))
        {
            var title = game.IsFortnite ? "Fortnite" : game.Name;
            var admin = plan.Requests.Any(r => r.Level == BrokerLevel.Admin);
            var sentence = game.IsFortnite && !admin ? Loc.F("Simple.Games.Fn.Confirm", text)
                : admin ? Loc.F("Simple.Games.ConfirmAdmin", game.Name, text) : Loc.F("Simple.Games.Confirm", game.Name, text);
            var exe = game.Exe;
            action = new OneClickAction(Loc.T("Simple.Games.TurnOn"), Applier, q => q.ApplyAsync(title, sentence, plan.Requests), () => _ = RefreshGameAsync(exe));
            _actions[plan.Id] = action;
        }
        action.ShowState(_states.GetValueOrDefault(plan.Id, OptionStatus.Unread));
        return action;
    }

    /// <summary>"Agora: Modo desempenho · limite 141 FPS · Reflex ligado" (ou "abra o jogo uma vez").</summary>
    private static string FortniteStatus(FortniteState s)
    {
        if (!s.FileFound) return Loc.T("Simple.Games.Fn.NotFound");
        var parts = new List<string> { s.IsPerformanceMode ? Loc.T("Simple.Games.Fn.ModePerf") : Loc.T("Simple.Games.Fn.ModeDx12") };
        parts.Add(s.FrameRateLimit is { } f && FortniteSettings.FpsLabel(f) is var label && label != "0" ? Loc.F("Simple.Games.Fn.Cap", label) : Loc.T("Simple.Games.Fn.NoCap"));
        if (s.Reflex is { } r) parts.Add(r == "0" ? Loc.T("Simple.Games.Fn.ReflexOff") : Loc.T("Simple.Games.Fn.ReflexOn"));
        return Loc.F("Simple.Games.Fn.Now", string.Join(" · ", parts));
    }
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
    /// <summary>Texto do botão de <see cref="OpenCommand"/> ("Abrir página"; no monitor, "Usar 144 Hz").</summary>
    public string LinkText { get; init; } = Loc.T("Simple.Problems.OpenPage");
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

    public SimpleProblemsPage() => Crash = new CrashReportPanel(() => Host);

    /// <summary>"O jogo fechou sozinho?": juntar informações do travamento (só leitura + ZIP onde o usuário escolher).</summary>
    public CrashReportPanel Crash { get; }

    public override string Title => Loc.T("Simple.Nav.Problems");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Problems.Subtitle");

    internal ISimpleActions? Actions { get; set; }
    /// <summary>Checagens do PC: frases com os números da leitura e o botão "Usar 144 Hz" do monitor.</summary>
    internal PcChecksCoordinator? Checks { get; set; }
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
        var plain = Checks?.Plain;
        foreach (var f in findings.Where(f => f.Severity == DiagnosticSeverity.Attention && kept.All(k => k.Key != f.Id)))
        {
            var p = PlainFindings.For(f, plain);
            if (Checks?.State.DisplayFor(f.Id) is { MaxHz: { } max } monitor)
            {
                rows.Add(new ProblemRow(f.Id, p.Title, p.WhatToDo, p.IconKey, PlainFindings.ImpactOf(f))
                {
                    OpenCommand = new RelayCommand(() => _ = MonitorFix.RunAsync(Host, monitor.Device)),
                    LinkText = PcCheckText.UseHz(max),
                });
                continue;
            }
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
        // Disco falhando/quente e limitação do processador: é com você (backup, ventilação); a explicação já está na linha.
        _ when id.StartsWith(PcChecks.DiskPrefix, StringComparison.Ordinal) && id != PcChecks.GamesOnSlowDiskId => ("DiskPage", Loc.T("Simple.Problems.Details")),
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

    /// <summary>As execuções dos dois diários na última leitura (para as telas acharem o que desfaz uma opção já ligada).</summary>
    public IReadOnlyList<JournalRun> JournalRuns { get; private set; } = [];

    /// <summary>
    /// A execução que ligou só este ajuste e ainda vale (a mais nova), para desligar nos Ajustes rápidos sem desfazer outras coisas
    /// junto. Null se ele foi feito junto com outros (aí, Desfazer aqui no Histórico).
    /// </summary>
    public (Guid Run, BrokerLevel Level)? FindSingleActionRun(string actionKey) =>
        OptionJournal.Find(JournalRuns, [new OptionPart(actionKey)]) is { Kind: UndoKind.Runs, Runs: [.., var newest] } ? newest : null;

    public override Task OnShownAsync() => LoadAsync();

    /// <summary>Relê os dois diários (fora da thread da interface).</summary>
    public async Task LoadAsync()
    {
        var runs = await Task.Run(ReadJournal);
        JournalRuns = runs;
        var now = DateTime.Now;
        Rows = runs.Where(r => r.Active || r.Items.Any(i => i.Reverted))
            .OrderByDescending(r => r.Started)
            .Select(r =>
            {
                var row = new SimpleHistoryRow(r.RunId, r.Level, r.Started, SimpleHistoryText.When(r.Started.LocalDateTime, now),
                    SimpleHistoryText.Describe([.. r.Items.Select(i => i.Key)]), r.Active, [.. r.Items.Select(i => i.Key)]);
                return row with { UndoCommand = r.Active ? new RelayCommand(() => _ = RunAsync(() => UndoAsync(row)), () => !Busy && Host is not null) : null };
            }).ToList();
        _loaded = true;
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(NoRows));
    }

    /// <summary>Lê os dois diários do disco (sua conta e sistema), só leitura. Diário ilegível: fica sem as execuções dele.</summary>
    public static IReadOnlyList<JournalRun> ReadJournal() => [.. Read(BrokerLevel.User), .. Read(BrokerLevel.Admin)];

    private static List<JournalRun> Read(BrokerLevel level)
    {
        var path = LocalBroker.JournalPath(level);
        var list = new List<JournalRun>();
        if (!File.Exists(path)) return list;
        try
        {
            using var journal = SqliteJournal.OpenReadOnly(path);
            foreach (var run in journal.Runs())
            {
                var entries = journal.Entries(run.RunId);
                if (entries.Count == 0) continue;
                list.Add(new JournalRun(run.RunId, level, run.StartedAt, [.. entries.Select(e => new JournalItem(e.Action.Definition.Key, e.Action.Target,
                    e.State is JournalState.Applied or JournalState.Verified) { Reverted = e.State == JournalState.Reverted })]));
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
