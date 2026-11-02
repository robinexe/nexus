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

/// <summary>Cartão "O que dá para melhorar": uma frase e um botão (navega ou, em "Ver como", abre a explicação aqui mesmo).</summary>
public sealed class ImproveCardRow : ObservableObject
{
    private bool _showHowTo;

    public ImproveCardRow(ImproveCard card)
    {
        Card = card;
        ToggleHowToCommand = new RelayCommand(() => ShowHowTo = !ShowHowTo);
    }

    public ImproveCard Card { get; }
    public string Text => Card.Text;
    public string ActionText => Card.ActionText;
    public string IconKey => Card.IconKey;
    public string Target => Card.Target;
    public string HowTo => Card.HowTo ?? "";
    /// <summary>O botão abre a explicação no próprio cartão (memória: o que comprar) em vez de levar a outra tela.</summary>
    public bool IsHowTo => Card.Kind == ImproveKind.SingleChannel;
    public bool Navigates => !IsHowTo;
    public bool ShowHowTo { get => _showHowTo; private set => Set(ref _showHowTo, value); }
    public RelayCommand ToggleHowToCommand { get; }
}

/// <summary>
/// Início (modo simples): o anel com a palavra grande ("Seu PC está ótimo / pode melhorar / precisa de atenção"), um botão
/// "Otimizar meu PC com segurança" e no máximo 3 cartões. A nota vem de leituras que já existem (a janela junta e chama
/// <see cref="Update"/>); nada aqui escreve.
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

    /// <summary>Chamado pela janela sempre que uma leitura muda (diagnóstico, limpeza medida, catálogo relido).</summary>
    public void Update(HealthInputs inputs)
    {
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        _result = inputs.HasData ? SimpleHealth.Compute(inputs) : new HealthResult(double.NaN, HealthLevel.Great);
        Cards = inputs.HasData ? ImproveCards.Top(inputs).Select(c => new ImproveCardRow(c)).ToList() : [];
        foreach (var n in new[] { nameof(IsLoading), nameof(HasData), nameof(Score), nameof(Level), nameof(Headline), nameof(Word), nameof(Tone),
                     nameof(HasCards), nameof(NoCards) })
            OnPropertyChanged(n);
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

/// <summary>Um jogo na tela Jogos: nome, inicial (no lugar do ícone), opções em palavras simples que valem para ele e "Medir FPS".</summary>
public sealed class SimpleGameRow : ObservableObject
{
    private bool _lowLatency, _maxPower, _fpsCap, _windowed;

    public SimpleGameRow(string name, string exe, string? exePath, bool nvidia, bool windowedAvailable, bool windowedOn, string? fps)
    {
        Name = name;
        Exe = exe;
        ExePath = exePath;
        CanNvidia = nvidia;
        CanWindowed = windowedAvailable && !windowedOn && exePath is not null;
        WindowedOn = windowedOn;
        Fps = fps;
    }

    public string Name { get; }
    /// <summary>Nome do executável (ex.: cs2.exe).</summary>
    public string Exe { get; }
    /// <summary>Caminho completo (só quando a biblioteca da loja informou): necessário para "jogos em janela".</summary>
    public string? ExePath { get; }
    public string Initial => Name.Length > 0 ? char.ToUpper(Name[0], Loc.Culture).ToString() : "?";
    /// <summary>Placa e driver NVIDIA ativos: as três opções do driver por jogo valem.</summary>
    public bool CanNvidia { get; }
    public bool CanWindowed { get; }
    public bool WindowedOn { get; }
    public string? Fps { get; }
    public bool CanFps => CanNvidia && Fps is not null;
    public string FpsText => Loc.F("Simple.Games.FpsCap", Fps ?? "60");
    public bool HasOptions => CanNvidia || CanWindowed;
    public bool LowLatency { get => _lowLatency; set { if (Set(ref _lowLatency, value)) OnPropertyChanged(nameof(AnyChosen)); } }
    public bool MaxPower { get => _maxPower; set { if (Set(ref _maxPower, value)) OnPropertyChanged(nameof(AnyChosen)); } }
    public bool FpsCap { get => _fpsCap; set { if (Set(ref _fpsCap, value)) OnPropertyChanged(nameof(AnyChosen)); } }
    public bool Windowed { get => _windowed; set { if (Set(ref _windowed, value)) OnPropertyChanged(nameof(AnyChosen)); } }
    public bool AnyChosen => _lowLatency || _maxPower || _fpsCap || _windowed;
    public RelayCommand? ApplyCommand { get; internal set; }
    public RelayCommand? MeasureCommand { get; internal set; }
}

/// <summary>
/// Jogos (modo simples): os jogos achados nas lojas (Steam, Epic…) em cartões; para cada um, "Medir FPS" e só as opções que
/// valem para ele, em palavras simples (as mesmas ações do driver NVIDIA por jogo e de "jogos em janela" da página Placa de
/// vídeo), e um resumo do Modo de jogo do Windows. Aplica pelo aplicador, com confirmação, e dá para desfazer em Histórico.
/// </summary>
public sealed class SimpleGamesPage : ModulePage
{
    private bool _loading = true;
    private bool? _gameModeOn;

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
    public bool GameModeOff => _gameModeOn == false;

    /// <summary>A janela informa o Modo de jogo a cada leitura (achado win.gamemode do diagnóstico).</summary>
    public void SetGameMode(bool? on)
    {
        _gameModeOn = on;
        OnPropertyChanged(nameof(GameModeText));
        OnPropertyChanged(nameof(GameModeTone));
        OnPropertyChanged(nameof(GameModeOff));
    }

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
            Games.Add(Wire(new SimpleGameRow(w.Name, exe, w.ExePath, nv.ContainsKey(exe), true, w.IsOn, Gpu.SelectedNvFps)));
        }
        foreach (var g in nv.Values)
            if (seen.Add(g.Exe)) Games.Add(Wire(new SimpleGameRow(g.Name, g.Exe, null, true, false, false, Gpu.SelectedNvFps)));
        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(NoGames));
    }

    private SimpleGameRow Wire(SimpleGameRow row)
    {
        row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => ApplyAsync(row)), () => !Busy && row.AnyChosen && Host is not null);
        row.MeasureCommand = new RelayCommand(() => MeasureGame(row));
        return row;
    }

    private void MeasureGame(SimpleGameRow row)
    {
        Measure.ManualExe = row.Exe;
        Host?.Navigate(Measure);
    }

    /// <summary>As opções marcadas viram as mesmas ações da página Placa de vídeo: driver NVIDIA (administrador) e jogos em janela (sua conta).</summary>
    private async Task ApplyAsync(SimpleGameRow row)
    {
        if (Host is null) return;
        var done = new List<string>();
        var nv = new List<ActionRequest>();
        var lines = new List<string>();
        var exe = new Dictionary<string, string> { ["exe"] = row.Exe };
        if (row.CanNvidia && row.LowLatency) { nv.Add(new ActionRequest(NvidiaDrsActions.LowLatency, null, exe)); lines.Add(Loc.T("Simple.Games.LowLatency")); }
        if (row.CanNvidia && row.MaxPower) { nv.Add(new ActionRequest(NvidiaDrsActions.MaxPower, null, exe)); lines.Add(Loc.T("Simple.Games.MaxPower")); }
        if (row.CanFps && row.FpsCap && row.Fps is { } fps && NvidiaDrsActions.FpsCaps.Contains(fps))
        {
            nv.Add(new ActionRequest(NvidiaDrsActions.FpsCap, fps, exe));
            lines.Add(row.FpsText);
        }
        if (nv.Count > 0)
        {
            var text = Loc.F("Simple.Games.ConfirmNv", row.Name, string.Join("\n", lines.Select(l => "• " + l)));
            var r = await Host.ApplyAsync(Loc.F("Simple.Games.ApplyTitle", row.Name), text, nv, BrokerLevel.Admin);
            if (r is null) { Message = Loc.T("Shell.CanceladoNadaFoiAlterado"); return; }
            done.Add(r.Ok ? Loc.T("Simple.Games.DoneNv") : Loc.T("Simple.Games.Failed"));
        }
        if (row.CanWindowed && row.Windowed && row.ExePath is { } path)
        {
            var text = Loc.F("Simple.Games.ConfirmWindowed", row.Name);
            var r = await Host.ApplyAsync(Loc.F("Simple.Games.ApplyTitle", row.Name),
                text, [new ActionRequest(GpuLabPage.WindowedPerAppAction, null, new Dictionary<string, string> { ["gameExePath"] = path })], BrokerLevel.User);
            if (r is null) { Message = Loc.T("Shell.CanceladoNadaFoiAlterado"); return; }
            done.Add(r.Ok ? Loc.T("Simple.Games.DoneWindowed") : Loc.T("Simple.Games.Failed"));
        }
        row.LowLatency = row.MaxPower = row.FpsCap = row.Windowed = false;
        Message = string.Join(" ", done) + " " + Loc.T("Simple.Games.UndoHint");
    }
}

/// <summary>Um problema que precisa do usuário: uma frase, o que fazer (1–2 frases) e, quando dá, um botão.</summary>
public sealed record ProblemRow(string Title, string WhatToDo, string IconKey, int Impact, string Target = "", string ActionText = "")
{
    public bool HasTarget => Target.Length > 0;
    public RelayCommand? OpenCommand { get; init; }
    public bool HasLink => OpenCommand is not null;
}

/// <summary>
/// Problemas (modo simples): o que precisa de você (diagnóstico + "só você pode fazer" + o que o Assistente anotou), cada um
/// com "O que fazer", do maior impacto para o menor; o que está bem fica recolhido em "Está tudo certo com: …".
/// </summary>
public sealed class SimpleProblemsPage : ModulePage
{
    private IReadOnlyList<ProblemRow> _items = [];
    private IReadOnlyList<string> _fine = [];
    private bool _hasData;

    public override string Title => Loc.T("Simple.Nav.Problems");
    public override string Group => Loc.T("Simple.Nav.Group");
    public override string Subtitle => Loc.T("Simple.Problems.Subtitle");

    public IReadOnlyList<ProblemRow> Items { get => _items; private set => Set(ref _items, value); }
    public bool IsLoading => !_hasData;
    public bool HasItems => _items.Count > 0;
    public bool NoItems => _hasData && _items.Count == 0;
    public IReadOnlyList<string> FineTopics { get => _fine; private set => Set(ref _fine, value); }
    public bool HasFine => _fine.Count > 0;
    public string FineHeader => Loc.F("Simple.Problems.FineHeader", string.Join(", ", _fine));
    public string CountText => _items.Count == 1 ? Loc.T("Simple.Problems.Count1") : Loc.F("Simple.Problems.CountN", _items.Count);

    /// <summary>A janela chama a cada leitura (achados do diagnóstico e do "só você pode fazer"; ações anotadas pelo Assistente).</summary>
    public void Update(IReadOnlyList<DiagnosticFinding> findings, IReadOnlyList<UserAction> userActions)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(userActions);
        var rows = new List<ProblemRow>();
        foreach (var f in findings.Where(f => f.Severity == DiagnosticSeverity.Attention))
        {
            var p = PlainFindings.For(f);
            var (target, action) = TargetFor(f.Id);
            rows.Add(new ProblemRow(p.Title, p.WhatToDo, p.IconKey, PlainFindings.ImpactOf(f), target, action));
        }
        foreach (var u in userActions.Where(u => rows.All(r => r.Title != u.Title)))
            rows.Add(new ProblemRow(u.Title, u.Why, "Icon.User", 30)
            {
                OpenCommand = u.Link is { } link && link.StartsWith("https://", StringComparison.Ordinal) ? new RelayCommand(() => OpenLink(link)) : null,
            });
        Items = [.. rows.OrderByDescending(r => r.Impact)];
        FineTopics = PlainFindings.FineTopics(findings);
        _hasData = findings.Count > 0;
        foreach (var n in new[] { nameof(IsLoading), nameof(HasItems), nameof(NoItems), nameof(HasFine), nameof(FineHeader), nameof(CountText) }) OnPropertyChanged(n);
    }

    /// <summary>Onde resolver: o que o NEXUS faz sozinho vai para Otimizar/Limpar; hardware e BIOS não têm botão (é com você); o resto abre a página completa.</summary>
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
public sealed record SimpleHistoryRow(Guid RunId, BrokerLevel Level, DateTimeOffset Started, string When, string What, bool Active)
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
                list.Add(new SimpleHistoryRow(run.RunId, level, run.StartedAt, "", SimpleHistoryText.Describe(keys), active));
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
/// Boas-vindas da primeira abertura (uma vez): 3 telas curtas — o que o NEXUS faz, nada muda sem você e tudo se desfaz, e a
/// escolha Simples (recomendado) / Completo. Pular = Simples. A janela grava a escolha (settings.json).
/// </summary>
public sealed class WelcomeFlow : ObservableObject
{
    public const int Steps = 3;
    private int _step;
    private bool _isVisible;
    private readonly Action<UiMode> _choose;

    public WelcomeFlow(Action<UiMode> choose)
    {
        _choose = choose ?? throw new ArgumentNullException(nameof(choose));
        NextCommand = new RelayCommand(() => Step++, () => _step < Steps - 1);
        BackCommand = new RelayCommand(() => Step--, () => _step > 0);
        SkipCommand = new RelayCommand(() => Choose(UiMode.Simple));
        ChooseSimpleCommand = new RelayCommand(() => Choose(UiMode.Simple));
        ChooseFullCommand = new RelayCommand(() => Choose(UiMode.Full));
    }

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    public int Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, Math.Clamp(value, 0, Steps - 1))) return;
            foreach (var n in new[] { nameof(IsFirst), nameof(IsSecond), nameof(IsChoice), nameof(NotChoice), nameof(Dots) }) OnPropertyChanged(n);
        }
    }
    public bool IsFirst => _step == 0;
    public bool IsSecond => _step == 1;
    public bool IsChoice => _step == Steps - 1;
    public bool NotChoice => !IsChoice;
    /// <summary>Os três pontos de "onde estou" (true = a tela atual).</summary>
    public IReadOnlyList<bool> Dots => Enumerable.Range(0, Steps).Select(i => i == _step).ToList();
    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand ChooseSimpleCommand { get; }
    public RelayCommand ChooseFullCommand { get; }

    /// <summary>Volta à primeira tela (o autoteste fotografa as três).</summary>
    internal void Show(int step = 0)
    {
        _step = -1;
        Step = step;
        IsVisible = true;
    }

    private void Choose(UiMode mode)
    {
        IsVisible = false;
        _choose(mode);
    }
}
