using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.Services;
using Nexus.Security;
using Nexus.Security.Scanner;
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Um sinal de um arquivo (camada, título, por que é suspeito, pontos).</summary>
public sealed class ScanHitRow(ScanHit h)
{
    public string Layer => h.Layer switch { ScanLayer.Hash => Loc.T("Scan.ListaHashes"), ScanLayer.Pattern => Loc.T("Scan.RegraPadrao"), _ => Loc.T("Scan.Heuristica") };
    public string Title => h.Title;
    public string Why => h.Why;
    public string PointsText => h.Points > 0 ? $"+{h.Points}" : h.Points == 0 ? "0" : h.Points.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Arquivo apontado pela verificação, com as ações (Defender, Explorer, propriedades, ignorar, quarentena).</summary>
public sealed class ScanFindingRow : ObservableObject
{
    private bool _selected;
    private string _status = "";

    public ScanFindingRow(FileVerdict v) => Verdict = v;

    public FileVerdict Verdict { get; }
    public string Path => Verdict.Path;
    public string FileName => System.IO.Path.GetFileName(Verdict.Path);
    public string Folder => System.IO.Path.GetDirectoryName(Verdict.Path) ?? "";
    public string VerdictText => Verdict.VerdictText;
    public string ScoreText => Verdict.Verdict == Nexus.Security.Scanner.Verdict.KnownBad ? Loc.T("Scan.Hash") : $"{Math.Min(Verdict.Score, 100)} pts";
    public string ToneKey => Verdict.IsTestSample ? "Info" : Verdict.Verdict switch
    {
        Nexus.Security.Scanner.Verdict.KnownBad or Nexus.Security.Scanner.Verdict.HighlySuspicious => "Bad",
        Nexus.Security.Scanner.Verdict.Suspicious => "Warn",
        _ => "Unknown",
    };
    /// <summary>Tom do design system: vermelho muito suspeito/na lista, âmbar suspeito, ciano teste, neutro para revisar.</summary>
    public UiTone Tone => ScannerPage.UiToneOf(ToneKey);
    public string IconKey => ScannerPage.IconOf(ToneKey);
    /// <summary>Pontos de 0 a 100 (para a barrinha do cartão).</summary>
    public double ScorePercent => Verdict.Verdict == Nexus.Security.Scanner.Verdict.KnownBad ? 100 : Math.Clamp(Verdict.Score, 0, 100);
    public bool HasStatus => Status.Length > 0;
    public string Meta => $"{SizeText(Verdict.Size)} · " + (Verdict.ValidSignature switch
    {
        true => Loc.T("Scan.AssinaturaDigitalValida"),
        false => Loc.T("Scan.SemAssinaturaValida"),
        _ => Loc.T("Scan.AssinaturaNaoConferida"),
    }) + (Verdict.Sha256 is { } s ? Loc.F("Scan.Sha256N", s[..16]) : "");
    public IReadOnlyList<ScanHitRow> Hits { get; init; } = [];
    public bool CanQuarantine => Verdict.Sha256 is not null;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value && CanQuarantine); }
    public string Status { get => _status; set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }

    public RelayCommand? DefenderCommand { get; init; }
    public RelayCommand? ExplorerCommand { get; init; }
    public RelayCommand? PropertiesCommand { get; init; }
    public RelayCommand? IgnoreCommand { get; init; }
    public RelayCommand? QuarantineCommand { get; init; }

    internal static string SizeText(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };
}

/// <summary>Grupo de resultados por gravidade.</summary>
public sealed class ScanFindingGroup(string key, string title, string toneKey, string note) : ObservableObject
{
    public string Key => key;
    public string Title => title;
    public string ToneKey => toneKey;
    public string Note => note;
    public ObservableCollection<ScanFindingRow> Items { get; } = [];
    public int Count => Items.Count;
    public UiTone Tone => ScannerPage.UiToneOf(toneKey);
    public string IconKey => ScannerPage.IconOf(toneKey);
    internal void Changed() => OnPropertyChanged(nameof(Count));
}

/// <summary>Unidade para a verificação completa.</summary>
public sealed class ScanDriveRow(string root) : ObservableObject
{
    private bool _selected = root.StartsWith(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", StringComparison.OrdinalIgnoreCase);
    public string Root => root;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>
/// Verificar arquivos: motor local SOMENTE LEITURA (lista de hashes opcional, regras de padrão, heurística de PE) com
/// explicação por achado. O Microsoft Defender continua sendo a proteção principal: cada achado tem "Verificar com o
/// Defender". A única escrita (isolar na quarentena do NEXUS, reversível) passa pelo aplicador via Host.ApplyAsync.
/// </summary>
public sealed class ScannerPage : ModulePage, IDisposable
{
    private const int UserSlots = 10;
    private const int MachineSlots = 4;
    private static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "scanner");
    private static readonly string HashesFolder = Path.Combine(DataRoot, "hashes");
    private static readonly string RulesFolder = Path.Combine(DataRoot, "rules");

    private ScanEngine? _engine;
    private IgnoreList _ignore = new();
    private CatalogDocument? _catalog;
    private CancellationTokenSource? _cts;
    private FolderWatcher? _watcher;
    private bool _scanning;
    private bool _liveWatch;
    private bool _hostHooked;
    private bool _loaded;
    private string _scanStatus = "";
    private string _currentFile = "";
    private string _filesText = "";
    private string _rateText = "";
    private string _elapsedText = "";
    private string _databaseText = Loc.T("Scan.CarregandoRegras");
    private string _liveText = "";
    private double _scannedFiles, _filesPerSecond = double.NaN;
    private double _ruleCount = double.NaN, _hashCount = double.NaN, _ignoreCount = double.NaN;
    private bool _hasScanned;
    private Guid? _lastRun;
    private BrokerLevel _lastLevel;

    public ScannerPage()
    {
        QuickScanCommand = new RelayCommand(() => _ = ScanAsync(Loc.T("Sec.VerificacaoRapida"), () => ScanTargets.Quick(), quick: true), CanStart);
        FullScanCommand = new RelayCommand(() => _ = FullScanAsync(), () => CanStart() && Drives.Any(d => d.IsSelected));
        FolderScanCommand = new RelayCommand(() => _ = FolderScanAsync(), CanStart);
        FileScanCommand = new RelayCommand(() => _ = FileScanAsync(), CanStart);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _scanning);
        ImportHashesCommand = new RelayCommand(() => _ = ImportAsync(rules: false), () => !_scanning && !Busy);
        ImportRulesCommand = new RelayCommand(() => _ = ImportAsync(rules: true), () => !_scanning && !Busy);
        QuarantineSelectedCommand = new RelayCommand(() => _ = QuarantineAsync(AllRows().Where(r => r.IsSelected).ToList()),
            () => !Busy && !_scanning && AllRows().Any(r => r.IsSelected));
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => !Busy && _lastRun is not null);
        ClearResultsCommand = new RelayCommand(() => { Groups.Clear(); NotifyResults(); }, () => !_scanning);
    }

    public override string Title => Loc.T("Scan.NavTitle");
    public override string Group => Loc.T("Nav.Proteger");
    public override string Subtitle => Loc.T("Scan.NavSubtitle");

    public ObservableCollection<ScanFindingGroup> Groups { get; } = [];
    public ObservableCollection<ScanDriveRow> Drives { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    public RelayCommand QuickScanCommand { get; }
    public RelayCommand FullScanCommand { get; }
    public RelayCommand FolderScanCommand { get; }
    public RelayCommand FileScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ImportHashesCommand { get; }
    public RelayCommand ImportRulesCommand { get; }
    public RelayCommand QuarantineSelectedCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand ClearResultsCommand { get; }

    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) NotifyResults(); } }
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public string CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value); }
    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    public string DatabaseText { get => _databaseText; private set => Set(ref _databaseText, value); }
    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    public bool CanUndo => _lastRun is not null;

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Arquivos lidos na verificação atual/última (0 antes da primeira): o número do destaque.</summary>
    public double ScannedFiles { get => _scannedFiles; private set => Set(ref _scannedFiles, value); }
    public double FilesPerSecond { get => _filesPerSecond; private set => Set(ref _filesPerSecond, value); }
    public double RuleCount { get => _ruleCount; private set => Set(ref _ruleCount, value); }
    public double HashCount { get => _hashCount; private set => Set(ref _hashCount, value); }
    public double IgnoreCount { get => _ignoreCount; private set => Set(ref _ignoreCount, value); }
    public double FindingNumber => FindingCount;
    /// <summary>Carregando regras e listas (esqueleto nos cartões do motor).</summary>
    public bool EngineLoading => _engine is null;
    public bool HasScanned { get => _hasScanned; private set { if (Set(ref _hasScanned, value)) NotifyResults(); } }
    public bool HasResults => FindingCount > 0;
    /// <summary>Verificou e nada apareceu: estado vazio "nenhum sinal".</summary>
    public bool ShowClean => HasScanned && !Scanning && FindingCount == 0;
    /// <summary>Ainda não verificou nada: estado vazio com o primeiro passo.</summary>
    public bool ShowNeverScanned => !HasScanned && !Scanning && FindingCount == 0;

    private void NotifyResults()
    {
        foreach (var name in new[] { nameof(FindingCount), nameof(ResultSummary), nameof(FindingNumber), nameof(HasResults), nameof(ShowClean), nameof(ShowNeverScanned) })
            OnPropertyChanged(name);
    }

    internal static UiTone UiToneOf(string toneKey) => toneKey switch { "Bad" => UiTone.Bad, "Warn" => UiTone.Warn, "Info" => UiTone.Info, _ => UiTone.Neutral };
    internal static string IconOf(string toneKey) => toneKey switch { "Bad" => "Icon.Error", "Warn" => "Icon.Warning", "Info" => "Icon.Info", _ => "Icon.Search" };

    public int FindingCount => Groups.Sum(g => g.Count);
    public string ResultSummary => FindingCount == 0
        ? Loc.T("Scan.NenhumAchadoAindaAchados")
        : string.Join(" · ", Groups.Where(g => g.Count > 0).Select(g => $"{g.Count} {g.Title.ToLower(CultureInfo.CurrentCulture)}"));

    /// <summary>Vigiar a pasta Downloads enquanto esta página estiver aberta (desliga ao sair da página).</summary>
    public bool LiveWatch
    {
        get => _liveWatch;
        set
        {
            if (!Set(ref _liveWatch, value)) return;
            if (value) StartWatch();
            else StopWatch();
        }
    }

    private bool CanStart() => !_scanning && !Busy && _engine is not null;

    // ------------------------------------------------------------------ abrir a página

    public override async Task OnShownAsync()
    {
        HookHost();
        if (_loaded) return;
        _loaded = true;
        await RunAsync(LoadEngineAsync);
    }

    private void HookHost()
    {
        if (_hostHooked || Host is not INotifyPropertyChanged npc) return;
        _hostHooked = true;
        npc.PropertyChanged += (_, e) =>
        {
            // Saiu da página: a verificação ao vivo para (promessa da interface: "enquanto a página está aberta").
            if (e.PropertyName == nameof(MainViewModel.SelectedPage) && Host is MainViewModel mv && !ReferenceEquals(mv.SelectedPage, this))
                LiveWatch = false;
        };
    }

    private async Task LoadEngineAsync()
    {
        var notes = new List<string>();
        var (engine, ignore, text) = await Task.Run(() =>
        {
            var hashes = HashDatabase.LoadBuiltIn();
            var rules = RuleSet.LoadBuiltIn();
            foreach (var file in SafeFiles(HashesFolder, "*.txt").Concat(SafeFiles(HashesFolder, "*.csv")))
            {
                try { hashes.AddFile(file, out _, out _); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add(Loc.F("Scan.ListaHashesNIlegivel", Path.GetFileName(file), ex.Message)); }
            }
            foreach (var file in SafeFiles(RulesFolder, "*.json"))
            {
                try { rules.AddFile(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { notes.Add(Loc.F("Scan.RegrasNNaoCarregadas", Path.GetFileName(file), ex.Message)); }
            }
            var ign = IgnoreList.Load(IgnoreList.DefaultPath);
            var opts = new ScanOptions { ExcludedDirectories = ScanTargets.DefaultExclusions() };
            var desc = Loc.F("Scan.NRegrasPadraoN", rules.Rules.Count, hashes.RealCount) +
                       (hashes.RealCount == 0 ? Loc.T("Scan.NenhumaListaImportadaSo") : $" ({string.Join(", ", hashes.Sources.Where(s => s != "teste embutido"))})") +
                       Loc.F("Scan.NExcecoes", ign.Count);
            return (new ScanEngine(hashes, rules, new AuthenticodeSignatureChecker(), opts, ign), ign, desc);
        });
        _engine = engine;
        _ignore = ignore;
        DatabaseText = text;
        RuleCount = engine.Rules.Rules.Count;
        HashCount = engine.Hashes.RealCount;
        IgnoreCount = ignore.Count;
        OnPropertyChanged(nameof(EngineLoading));
        Notes.Clear();
        foreach (var n in notes) Notes.Add(n);
        Drives.Clear();
        foreach (var d in await Task.Run(ScanTargets.FixedDrives)) Drives.Add(new ScanDriveRow(d));
    }

    private static string[] SafeFiles(string folder, string pattern)
    {
        try { return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ------------------------------------------------------------------ modos

    private Task FullScanAsync()
    {
        var roots = Drives.Where(d => d.IsSelected).Select(d => d.Root).ToList();
        if (Host is null || roots.Count == 0) return Task.CompletedTask;
        if (!Host.Confirm(Loc.T("Sec.VerificacaoCompleta"), Loc.F("Scan.VerificarNPodeLevar", string.Join(", ", roots))))
            return Task.CompletedTask;
        return ScanAsync(Loc.T("Sec.VerificacaoCompleta"), () => roots, quick: false);
    }

    private Task FolderScanAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Scan.EscolhaPastaVerificar") };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        var path = dlg.FolderName;
        return ScanAsync(Loc.F("Scan.VerificacaoN", path), () => [path], quick: false, custom: true);
    }

    private Task FileScanAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Scan.EscolhaArquivoVerificar"), Multiselect = true };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        var files = dlg.FileNames.ToList();
        return ScanAsync(files.Count == 1 ? Loc.F("Scan.VerificacaoN", Path.GetFileName(files[0])) : Loc.F("Scan.VerificacaoNArquivos", files.Count), () => files, quick: false, custom: true);
    }

    private async Task ScanAsync(string label, Func<IReadOnlyList<string>> roots, bool quick, bool custom = false)
    {
        if (_engine is null) return;
        var baseEngine = _engine;
        var options = new ScanOptions
        {
            ExcludedDirectories = baseEngine.Options.ExcludedDirectories,
            ScanSmallFiles = !quick, // na rápida, só tipos executáveis/documentos (AppData tem milhares de arquivos pequenos de cache)
            SniffUnknownForPe = custom,
        };
        var engine = new ScanEngine(baseEngine.Hashes, baseEngine.Rules, new AuthenticodeSignatureChecker(), options, _ignore);
        Scanning = true;
        Groups.Clear();
        NotifyResults();
        ScannedFiles = 0;
        FilesPerSecond = double.NaN;
        ScanStatus = Loc.F("Scan.NAndamentoSoLeitura", label);
        CurrentFile = "";
        FilesText = Loc.T("Scan.N0Arquivos");
        RateText = "";
        ElapsedText = "00:00";
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        var dispatcher = Application.Current?.Dispatcher;
        var progress = new Progress<ScanProgress>(p =>
        {
            FilesText = Loc.F("Scan.NArquivosNLidos", p.Files, ScanFindingRow.SizeText(p.Bytes)) + (p.Skipped > 0 ? Loc.F("Scan.NPulados", p.Skipped) : "");
            RateText = p.FilesPerSecond > 0 ? $"{p.FilesPerSecond:N0} arquivos/s" : "";
            ElapsedText = p.Elapsed.ToString(p.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
            CurrentFile = p.Current ?? "";
            ScannedFiles = p.Files;
            FilesPerSecond = p.FilesPerSecond > 0 ? p.FilesPerSecond : double.NaN;
        });
        try
        {
            var summary = await Task.Run(() => engine.Scan(roots(), progress, v => dispatcher?.BeginInvoke(() => AddFinding(v)), token));
            CurrentFile = "";
            ScannedFiles = summary.Files;
            var found = summary.Findings.Count;
            ScanStatus = (summary.Cancelled ? Loc.F("Scan.NCancelada", label) : Loc.F("Scan.NConcluida", label)) +
                         Loc.F("Scan.NArquivosNS", summary.Files, summary.Elapsed.TotalSeconds) +
                         (found == 0 ? Loc.T("Scan.NenhumSinalSuspeito2") : Loc.F("Scan.NArquivoSSinais", found)) +
                         (summary.Skipped > 0 ? Loc.F("Scan.NPuladosLinksArquivos", summary.Skipped) : "");
            Host?.Notify(ScanStatus);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ScanStatus = Loc.F("Sec.NNaoFoiPossivel", label, ex.Message);
        }
        finally
        {
            Scanning = false;
            HasScanned = true;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    // ------------------------------------------------------------------ resultados

    private static (string Key, string Title, string Tone, string Note) GroupOf(FileVerdict v) =>
        v.IsTestSample ? ("test", Loc.T("Scan.ArquivosTeste"), "Info", Loc.T("Scan.AmostrasTesteComoEicar"))
        : v.Verdict switch
        {
            Nexus.Security.Scanner.Verdict.KnownBad => ("hash", Loc.T("Scan.SuaListaHashes"), "Bad", Loc.T("Scan.Sha256BateLista")),
            Nexus.Security.Scanner.Verdict.HighlySuspicious => ("high", Loc.T("Scan.MuitoSuspeitos"), "Bad", Loc.T("Scan.VariosSinaisFortesJuntos")),
            Nexus.Security.Scanner.Verdict.Suspicious => ("mid", Loc.T("Scan.Suspeitos"), "Warn", Loc.T("Scan.SinaisMerecemAtencaoProgramas")),
            _ => ("low", Loc.T("Scan.Revisar"), "Unknown", Loc.T("Scan.SinaisFracosMaioriaVezes")),
        };

    private static readonly string[] GroupOrder = ["test", "hash", "high", "mid", "low"];

    private void AddFinding(FileVerdict v)
    {
        var (key, title, tone, note) = GroupOf(v);
        var group = Groups.FirstOrDefault(g => g.Key == key);
        if (group is null)
        {
            group = new ScanFindingGroup(key, title, tone, note);
            var index = 0;
            while (index < Groups.Count && Array.IndexOf(GroupOrder, Groups[index].Key) < Array.IndexOf(GroupOrder, key)) index++;
            Groups.Insert(index, group);
        }
        if (group.Items.Any(r => string.Equals(r.Path, v.Path, StringComparison.OrdinalIgnoreCase) && r.Verdict.Sha256 == v.Sha256)) return;
        ScanFindingRow? row = null;
        row = new ScanFindingRow(v)
        {
            Hits = v.Hits.Where(h => h.Points > 0 || h.RuleId == "pe.signed").OrderByDescending(h => h.Points).Select(h => new ScanHitRow(h)).ToList(),
            DefenderCommand = new RelayCommand(() => _ = DefenderAsync(row!), () => !_scanning && !Busy),
            ExplorerCommand = new RelayCommand(() => { if (!ShellViews.ShowInExplorer(v.Path)) row!.Status = Loc.T("Scan.ArquivoNaoEstaMais"); }),
            PropertiesCommand = new RelayCommand(() => { if (!ShellViews.ShowProperties(v.Path)) row!.Status = Loc.T("Scan.NaoFoiPossivelAbrir"); }),
            IgnoreCommand = new RelayCommand(() => Ignore(row!), () => v.Sha256 is not null),
            QuarantineCommand = new RelayCommand(() => _ = QuarantineAsync([row!]), () => !Busy && !_scanning && v.Sha256 is not null),
        };
        // Mais grave primeiro dentro do grupo.
        var at = 0;
        while (at < group.Items.Count && group.Items[at].Verdict.Score >= v.Score) at++;
        group.Items.Insert(at, row);
        group.Changed();
        NotifyResults();
    }

    private IEnumerable<ScanFindingRow> AllRows() => Groups.SelectMany(g => g.Items);

    private void RemoveRow(ScanFindingRow row)
    {
        foreach (var g in Groups.ToList())
        {
            if (!g.Items.Remove(row)) continue;
            g.Changed();
            if (g.Items.Count == 0) Groups.Remove(g);
        }
        NotifyResults();
    }

    private void Ignore(ScanFindingRow row)
    {
        if (row.Verdict.Sha256 is not { } sha || Host is null) return;
        if (!Host.Confirm(Loc.T("Scan.IgnorarEsteAchado"), Loc.F("Scan.NaoMostrarMaisN", row.FileName, IgnoreList.DefaultPath)))
            return;
        try
        {
            _ignore.Add(row.Path, sha, DateTimeOffset.UtcNow);
            _ignore.Save(IgnoreList.DefaultPath);
            RemoveRow(row);
            Message = Loc.F("Scan.NSeraIgnoradoNas", row.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = Loc.F("Scan.NaoFoiPossivelSalvar", ex.Message); }
    }

    // ------------------------------------------------------------------ Defender (exceção controlada do brief)

    private async Task DefenderAsync(ScanFindingRow row)
    {
        if (Host is null) return;
        if (!Host.Confirm(Loc.T("Sec.VerificarMicrosoftDefender"),
                Loc.F("Scan.PedirMicrosoftDefenderVerificar", row.Path)))
            return;
        Scanning = true;
        row.Status = Loc.T("Scan.DefenderVerificando");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var result = await DefenderScanner.ScanAsync(ScanKind.Custom, row.Path, (_, _) => { }, cts.Token);
            row.Status = Loc.T("Scan.Defender") + result.Summary;
            Host.Notify(row.Status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { row.Status = Loc.F("Scan.DefenderNaoFoiPossivel", ex.Message); }
        finally { Scanning = false; }
    }

    // ------------------------------------------------------------------ quarentena reversível (aplicador)

    private CatalogDocument Catalog() => _catalog ??= CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory).Localized(Loc.Language);

    /// <summary>Vaga de administrador para arquivos de Arquivos de Programas/ProgramData ou do perfil de outra conta.</summary>
    private static bool NeedsAdmin(string path)
    {
        bool Under(string? root) => !string.IsNullOrEmpty(root) && QuarantinePolicy.Under(path, root);
        if (Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) || Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)) ||
            Under(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
            return true;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var users = Path.GetDirectoryName(profile);
        return Under(users) && !Under(profile);
    }

    private async Task QuarantineAsync(IReadOnlyList<ScanFindingRow> rows)
    {
        if (Host is null) return;
        var usable = rows.Where(r => r.Verdict.Sha256 is not null).ToList();
        if (usable.Count == 0) return;
        await RunAsync(async () =>
        {
            foreach (var group in usable.GroupBy(r => NeedsAdmin(r.Path)))
            {
                var machine = group.Key;
                var slots = machine ? MachineSlots : UserSlots;
                var prefix = machine ? "scanner.quarantine.machine." : "scanner.quarantine.user.";
                var items = group.Take(slots).ToList();
                var requests = new List<ActionRequest>();
                var accepted = new List<ScanFindingRow>();
                foreach (var row in items)
                {
                    var id = $"{prefix}{requests.Count + 1:00}";
                    var args = new Dictionary<string, string> { ["path"] = row.Path, ["sha256"] = row.Verdict.Sha256! };
                    var entry = Catalog().Actions.FirstOrDefault(a => a.Definition.Id == id);
                    if (entry?.ValidateUserArguments(args, out var why) is null)
                    {
                        row.Status = Loc.T("Scan.NaoIsolarPeloNexus");
                        continue;
                    }
                    requests.Add(new ActionRequest(id + "@1", null, args));
                    accepted.Add(row);
                }
                if (requests.Count == 0) continue;
                var level = machine ? BrokerLevel.Admin : BrokerLevel.User;
                var text = Loc.F("Scan.IsolarNArquivoS", accepted.Count) +
                           string.Join("\n", accepted.Take(8).Select(r => $"• {r.FileName} ({r.VerdictText})")) + (accepted.Count > 8 ? Loc.F("Sec.MaisN", accepted.Count - 8) : "") +
                           Loc.T("Scan.ArquivoSaiLugarAtalhos") +
                           Loc.T("Scan.SuspeitoNaoMesmoMalware") +
                           (group.Count() > slots ? Loc.F("Scan.LimiteNVezDemais", slots) : "") +
                           (machine ? Loc.T("Sec.VaiPedirPermissaoAdministrador") : "");
                var r = await Host.ApplyAsync(Loc.T("Scan.IsolarQuarentenaNexus"), text, requests, level);
                if (r is null) continue;
                Message = r.Message;
                if (r.Response is { Ok: true, RunId: { } run })
                {
                    _lastRun = run;
                    _lastLevel = level;
                    OnPropertyChanged(nameof(CanUndo));
                }
                var outcomes = r.Response?.Results ?? [];
                for (var i = 0; i < accepted.Count; i++)
                {
                    var key = requests[i].ActionId;
                    var res = outcomes.FirstOrDefault(o => o.ActionKey == key);
                    accepted[i].Status = res is null ? (r.Ok ? Loc.T("Scan.PedidoEnviado") : Loc.T("Scan.NaoIsolado"))
                        : res.Outcome is "Verified" or "AlreadyConfigured" ? Loc.T("Scan.IsoladoQuarentenaNexusDesfazivel")
                        : Loc.F("Scan.NaoIsoladoN", res.Detail ?? res.Outcome);
                    accepted[i].IsSelected = false;
                }
            }
        });
    }

    private async Task UndoAsync()
    {
        if (Host is null || _lastRun is not { } run) return;
        await RunAsync(async () =>
        {
            var r = await Host.RevertAsync(run, _lastLevel);
            if (r is null) return;
            Message = r.Message;
            if (r.Response?.FullyRestored == true)
            {
                _lastRun = null;
                OnPropertyChanged(nameof(CanUndo));
                foreach (var row in AllRows().Where(x => x.Status.StartsWith("Isolado", StringComparison.Ordinal))) row.Status = Loc.T("Scan.DevolvidoLugar");
            }
        });
    }

    // ------------------------------------------------------------------ importar listas/regras (dados do próprio NEXUS)

    private async Task ImportAsync(bool rules)
    {
        if (Host is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = rules ? Loc.T("Scan.ImportarRegrasPadraoNexus") : Loc.T("Scan.ImportarListaHashesSha"),
            Filter = rules ? Loc.T("Scan.RegrasJsonJson") : Loc.T("Scan.ListaHashesTxtCsv"),
        };
        if (dlg.ShowDialog() != true) return;
        var source = dlg.FileName;
        await RunAsync(async () =>
        {
            var info = new FileInfo(source);
            if (info.Length > (rules ? RuleSet.MaxRuleFileBytes : 200L * 1024 * 1024)) { Message = Loc.T("Scan.ArquivoGrandeDemais"); return; }
            string summary;
            try
            {
                summary = await Task.Run(() =>
                {
                    if (rules)
                    {
                        var test = new RuleSet();
                        test.AddFile(source); // valida antes de copiar
                        var clash = test.Rules.Select(x => x.Id).Intersect(_engine!.Rules.Rules.Select(x => x.Id)).FirstOrDefault();
                        if (clash is not null) throw new FormatException($"a regra '{clash}' já existe");
                        return Loc.F("Scan.NRegraS", test.Rules.Count);
                    }
                    var db = new HashDatabase();
                    db.AddFile(source, out var added, out var skipped);
                    if (added == 0) throw new FormatException("nenhum SHA-256 encontrado no arquivo");
                    return Loc.F("Scan.NHashEs", added) + (skipped > 0 ? Loc.F("Scan.NLinhasIgnoradas", skipped) : "");
                });
            }
            catch (FormatException ex) { Message = Loc.F("Scan.NaoImportadoN", ex.Message); return; }
            if (!Host.Confirm(Loc.T("Scan.Importar"), Loc.F("Scan.ImportarNNCopia", summary, Path.GetFileName(source), (rules ? RulesFolder : HashesFolder))))
                return;
            var folder = rules ? RulesFolder : HashesFolder;
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
            _loaded = false;
            await LoadEngineAsync();
            _loaded = true;
            Message = Loc.F("Scan.ImportadoN", summary);
        });
    }

    // ------------------------------------------------------------------ ao vivo (Downloads)

    private void StartWatch()
    {
        var folder = OperatingSystem.IsWindows() ? ScanTargets.DownloadsFolder() : null;
        if (_engine is null || folder is null || !Directory.Exists(folder))
        {
            LiveText = Loc.T("Scan.PastaDownloadsNaoEncontrada");
            _liveWatch = false;
            OnPropertyChanged(nameof(LiveWatch));
            return;
        }
        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            _watcher = new FolderWatcher(folder, _engine, v => dispatcher?.BeginInvoke(() =>
            {
                LiveText = Loc.F("Scan.VivoNVerificouN", DateTime.Now, Path.GetFileName(v.Path), v.VerdictText);
                if (ScanEngine.IsFinding(v) && !_ignore.Contains(v.Path, v.Sha256))
                {
                    AddFinding(v);
                    Host?.Notify(Loc.F("Scan.ArquivoNovoDownloadsSinais", Path.GetFileName(v.Path)));
                }
            }));
            LiveText = Loc.F("Scan.VigiandoNEnquantoEsta", folder);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            LiveText = Loc.F("Scan.NaoFoiPossivelVigiar", ex.Message);
            _liveWatch = false;
            OnPropertyChanged(nameof(LiveWatch));
        }
    }

    public void Dispose()
    {
        StopWatch();
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
    }

    private void StopWatch()
    {
        _watcher?.Dispose();
        _watcher = null;
        LiveText = "";
    }
}
