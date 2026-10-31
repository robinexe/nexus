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

namespace Nexus.Desktop.ViewModels;

/// <summary>Um sinal de um arquivo (camada, título, por que é suspeito, pontos).</summary>
public sealed class ScanHitRow(ScanHit h)
{
    public string Layer => h.Layer switch { ScanLayer.Hash => "Lista de hashes", ScanLayer.Pattern => "Regra de padrão", _ => "Heurística" };
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
    public string ScoreText => Verdict.Verdict == Nexus.Security.Scanner.Verdict.KnownBad ? "hash" : $"{Math.Min(Verdict.Score, 100)} pts";
    public string ToneKey => Verdict.IsTestSample ? "Info" : Verdict.Verdict switch
    {
        Nexus.Security.Scanner.Verdict.KnownBad or Nexus.Security.Scanner.Verdict.HighlySuspicious => "Bad",
        Nexus.Security.Scanner.Verdict.Suspicious => "Warn",
        _ => "Unknown",
    };
    public string Meta => $"{SizeText(Verdict.Size)} · " + (Verdict.ValidSignature switch
    {
        true => "assinatura digital válida",
        false => "sem assinatura válida",
        _ => "assinatura não conferida",
    }) + (Verdict.Sha256 is { } s ? $" · SHA-256 {s[..16]}…" : "");
    public IReadOnlyList<ScanHitRow> Hits { get; init; } = [];
    public bool CanQuarantine => Verdict.Sha256 is not null;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value && CanQuarantine); }
    public string Status { get => _status; set => Set(ref _status, value); }

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
    private string _databaseText = "Carregando regras…";
    private string _liveText = "";
    private Guid? _lastRun;
    private BrokerLevel _lastLevel;

    public ScannerPage()
    {
        QuickScanCommand = new RelayCommand(() => _ = ScanAsync("Verificação rápida", () => ScanTargets.Quick(), quick: true), CanStart);
        FullScanCommand = new RelayCommand(() => _ = FullScanAsync(), () => CanStart() && Drives.Any(d => d.IsSelected));
        FolderScanCommand = new RelayCommand(() => _ = FolderScanAsync(), CanStart);
        FileScanCommand = new RelayCommand(() => _ = FileScanAsync(), CanStart);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _scanning);
        ImportHashesCommand = new RelayCommand(() => _ = ImportAsync(rules: false), () => !_scanning && !Busy);
        ImportRulesCommand = new RelayCommand(() => _ = ImportAsync(rules: true), () => !_scanning && !Busy);
        QuarantineSelectedCommand = new RelayCommand(() => _ = QuarantineAsync(AllRows().Where(r => r.IsSelected).ToList()),
            () => !Busy && !_scanning && AllRows().Any(r => r.IsSelected));
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => !Busy && _lastRun is not null);
        ClearResultsCommand = new RelayCommand(() => { Groups.Clear(); OnPropertyChanged(nameof(FindingCount)); OnPropertyChanged(nameof(ResultSummary)); }, () => !_scanning);
    }

    public override string Title => "Verificar arquivos";
    public override string Group => "Proteger";
    public override string Subtitle => "Procure sinais suspeitos em arquivos e peça a opinião do Microsoft Defender.";

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

    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public string CurrentFile { get => _currentFile; private set => Set(ref _currentFile, value); }
    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    public string DatabaseText { get => _databaseText; private set => Set(ref _databaseText, value); }
    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    public bool CanUndo => _lastRun is not null;
    public int FindingCount => Groups.Sum(g => g.Count);
    public string ResultSummary => FindingCount == 0
        ? "Nenhum achado ainda. Achados aparecem aqui agrupados por gravidade, com o porquê de cada um."
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"Lista de hashes \"{Path.GetFileName(file)}\" ilegível: {ex.Message}"); }
            }
            foreach (var file in SafeFiles(RulesFolder, "*.json"))
            {
                try { rules.AddFile(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { notes.Add($"Regras \"{Path.GetFileName(file)}\" não carregadas: {ex.Message}"); }
            }
            var ign = IgnoreList.Load(IgnoreList.DefaultPath);
            var opts = new ScanOptions { ExcludedDirectories = ScanTargets.DefaultExclusions() };
            var desc = $"{rules.Rules.Count} regras de padrão · {hashes.RealCount:N0} hashes importados" +
                       (hashes.RealCount == 0 ? " (nenhuma lista importada; só o conjunto de teste)" : $" ({string.Join(", ", hashes.Sources.Where(s => s != "teste embutido"))})") +
                       $" · {ign.Count} exceções";
            return (new ScanEngine(hashes, rules, new AuthenticodeSignatureChecker(), opts, ign), ign, desc);
        });
        _engine = engine;
        _ignore = ignore;
        DatabaseText = text;
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
        if (!Host.Confirm("Verificação completa", $"Verificar {string.Join(", ", roots)}? Pode levar de minutos a mais de uma hora e usa o disco enquanto roda. Só lê: nada é alterado. Você pode cancelar a qualquer momento."))
            return Task.CompletedTask;
        return ScanAsync("Verificação completa", () => roots, quick: false);
    }

    private Task FolderScanAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta para verificar" };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        var path = dlg.FolderName;
        return ScanAsync($"Verificação de \"{path}\"", () => [path], quick: false, custom: true);
    }

    private Task FileScanAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Escolha o arquivo para verificar", Multiselect = true };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        var files = dlg.FileNames.ToList();
        return ScanAsync(files.Count == 1 ? $"Verificação de \"{Path.GetFileName(files[0])}\"" : $"Verificação de {files.Count} arquivos", () => files, quick: false, custom: true);
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
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(ResultSummary));
        ScanStatus = $"{label} em andamento… Só leitura: nada é alterado.";
        CurrentFile = "";
        FilesText = "0 arquivos";
        RateText = "";
        ElapsedText = "00:00";
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        var dispatcher = Application.Current?.Dispatcher;
        var progress = new Progress<ScanProgress>(p =>
        {
            FilesText = $"{p.Files:N0} arquivos · {ScanFindingRow.SizeText(p.Bytes)} lidos" + (p.Skipped > 0 ? $" · {p.Skipped:N0} pulados" : "");
            RateText = p.FilesPerSecond > 0 ? $"{p.FilesPerSecond:N0} arquivos/s" : "";
            ElapsedText = p.Elapsed.ToString(p.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
            CurrentFile = p.Current ?? "";
        });
        try
        {
            var summary = await Task.Run(() => engine.Scan(roots(), progress, v => dispatcher?.BeginInvoke(() => AddFinding(v)), token));
            CurrentFile = "";
            var found = summary.Findings.Count;
            ScanStatus = (summary.Cancelled ? $"{label} cancelada. " : $"{label} concluída. ") +
                         $"{summary.Files:N0} arquivos em {summary.Elapsed.TotalSeconds:N0} s; " +
                         (found == 0 ? "nenhum sinal suspeito." : $"{found} arquivo(s) com sinais para revisar.") +
                         (summary.Skipped > 0 ? $" {summary.Skipped:N0} pulados (links, arquivos só na nuvem, em uso ou sem permissão)." : "");
            Host?.Notify(ScanStatus);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ScanStatus = $"{label}: não foi possível concluir ({ex.Message}).";
        }
        finally
        {
            Scanning = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    // ------------------------------------------------------------------ resultados

    private static (string Key, string Title, string Tone, string Note) GroupOf(FileVerdict v) =>
        v.IsTestSample ? ("test", "Arquivos de teste", "Info", "Amostras de teste (como o EICAR). Não são ameaça; servem para conferir que a verificação funciona.")
        : v.Verdict switch
        {
            Nexus.Security.Scanner.Verdict.KnownBad => ("hash", "Na sua lista de hashes", "Bad", "O SHA-256 bate com uma lista que você importou. Peça a opinião do Defender e, se ele confirmar, trate pela Segurança do Windows."),
            Nexus.Security.Scanner.Verdict.HighlySuspicious => ("high", "Muito suspeitos", "Bad", "Vários sinais fortes juntos. Suspeito não é o mesmo que malware: verifique com o Defender antes de isolar."),
            Nexus.Security.Scanner.Verdict.Suspicious => ("mid", "Suspeitos", "Warn", "Sinais que merecem atenção. Programas legítimos às vezes têm alguns deles."),
            _ => ("low", "Para revisar", "Unknown", "Sinais fracos. Na maioria das vezes é um programa comum; confira se você reconhece o arquivo."),
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
            ExplorerCommand = new RelayCommand(() => { if (!ShellViews.ShowInExplorer(v.Path)) row!.Status = "O arquivo não está mais nesse lugar."; }),
            PropertiesCommand = new RelayCommand(() => { if (!ShellViews.ShowProperties(v.Path)) row!.Status = "Não foi possível abrir as propriedades."; }),
            IgnoreCommand = new RelayCommand(() => Ignore(row!), () => v.Sha256 is not null),
            QuarantineCommand = new RelayCommand(() => _ = QuarantineAsync([row!]), () => !Busy && !_scanning && v.Sha256 is not null),
        };
        // Mais grave primeiro dentro do grupo.
        var at = 0;
        while (at < group.Items.Count && group.Items[at].Verdict.Score >= v.Score) at++;
        group.Items.Insert(at, row);
        group.Changed();
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(ResultSummary));
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
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void Ignore(ScanFindingRow row)
    {
        if (row.Verdict.Sha256 is not { } sha || Host is null) return;
        if (!Host.Confirm("Ignorar este achado", $"Não mostrar mais \"{row.FileName}\" nas verificações?\n\nVale só para este arquivo com este conteúdo exato (SHA-256): se ele mudar, volta a ser verificado. A lista fica em {IgnoreList.DefaultPath}."))
            return;
        try
        {
            _ignore.Add(row.Path, sha, DateTimeOffset.UtcNow);
            _ignore.Save(IgnoreList.DefaultPath);
            RemoveRow(row);
            Message = $"\"{row.FileName}\" será ignorado nas próximas verificações.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = $"Não foi possível salvar a exceção: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ Defender (exceção controlada do brief)

    private async Task DefenderAsync(ScanFindingRow row)
    {
        if (Host is null) return;
        if (!Host.Confirm("Verificar com o Microsoft Defender",
                $"Pedir ao Microsoft Defender para verificar \"{row.Path}\"?\n\nUsa a ferramenta oficial MpCmdRun (-Scan -ScanType 3 -File). Se ele encontrar ameaça, a remoção ou quarentena é feita pelo próprio Defender (Segurança do Windows)."))
            return;
        Scanning = true;
        row.Status = "Defender verificando…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var result = await DefenderScanner.ScanAsync(ScanKind.Custom, row.Path, (_, _) => { }, cts.Token);
            row.Status = "Defender: " + result.Summary;
            Host.Notify(row.Status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { row.Status = $"Defender: não foi possível verificar ({ex.Message})."; }
        finally { Scanning = false; }
    }

    // ------------------------------------------------------------------ quarentena reversível (aplicador)

    private CatalogDocument Catalog() => _catalog ??= CatalogLoader.LoadDirectory(NexusServices.CatalogDirectory);

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
                        row.Status = "Não dá para isolar pelo NEXUS (caminho fora do formato aceito: muito longo ou fundo demais). Use o Defender.";
                        continue;
                    }
                    requests.Add(new ActionRequest(id + "@1", null, args));
                    accepted.Add(row);
                }
                if (requests.Count == 0) continue;
                var level = machine ? BrokerLevel.Admin : BrokerLevel.User;
                var text = $"Isolar {accepted.Count} arquivo(s) na quarentena do NEXUS?\n\n" +
                           string.Join("\n", accepted.Take(8).Select(r => $"• {r.FileName} ({r.VerdictText})")) + (accepted.Count > 8 ? $"\n• … e mais {accepted.Count - 8}" : "") +
                           "\n\nO arquivo sai do lugar (atalhos e inicialização param de achá-lo), mas NÃO é apagado: fica guardado com manifesto e SHA-256 e volta com Desfazer." +
                           "\n\nSuspeito não é o mesmo que malware. Se ainda não pediu, considere verificar com o Microsoft Defender antes." +
                           (group.Count() > slots ? $"\n\nLimite de {slots} por vez: os demais ficam para a próxima." : "") +
                           (machine ? "\n\nVai pedir permissão de administrador (UAC) e criar um ponto de restauração." : "");
                var r = await Host.ApplyAsync("Isolar na quarentena do NEXUS", text, requests, level);
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
                    accepted[i].Status = res is null ? (r.Ok ? "Pedido enviado." : "Não isolado.")
                        : res.Outcome is "Verified" or "AlreadyConfigured" ? "Isolado na quarentena do NEXUS (desfazível)."
                        : $"Não isolado: {res.Detail ?? res.Outcome}";
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
                foreach (var row in AllRows().Where(x => x.Status.StartsWith("Isolado", StringComparison.Ordinal))) row.Status = "Devolvido ao lugar.";
            }
        });
    }

    // ------------------------------------------------------------------ importar listas/regras (dados do próprio NEXUS)

    private async Task ImportAsync(bool rules)
    {
        if (Host is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = rules ? "Importar regras de padrão (nexus.scanrules/1)" : "Importar lista de hashes SHA-256",
            Filter = rules ? "Regras (*.json)|*.json" : "Lista de hashes (*.txt;*.csv)|*.txt;*.csv",
        };
        if (dlg.ShowDialog() != true) return;
        var source = dlg.FileName;
        await RunAsync(async () =>
        {
            var info = new FileInfo(source);
            if (info.Length > (rules ? RuleSet.MaxRuleFileBytes : 200L * 1024 * 1024)) { Message = "Arquivo grande demais."; return; }
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
                        return $"{test.Rules.Count} regra(s)";
                    }
                    var db = new HashDatabase();
                    db.AddFile(source, out var added, out var skipped);
                    if (added == 0) throw new FormatException("nenhum SHA-256 encontrado no arquivo");
                    return $"{added:N0} hash(es)" + (skipped > 0 ? $" ({skipped:N0} linhas ignoradas)" : "");
                });
            }
            catch (FormatException ex) { Message = $"Não importado: {ex.Message}"; return; }
            if (!Host.Confirm("Importar", $"Importar {summary} de \"{Path.GetFileName(source)}\"?\n\nUma cópia vai para {(rules ? RulesFolder : HashesFolder)}. Confira a origem da lista: um hash na lista faz o arquivo aparecer como \"na sua lista\". Nada é baixado da internet."))
                return;
            var folder = rules ? RulesFolder : HashesFolder;
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
            _loaded = false;
            await LoadEngineAsync();
            _loaded = true;
            Message = $"Importado: {summary}.";
        });
    }

    // ------------------------------------------------------------------ ao vivo (Downloads)

    private void StartWatch()
    {
        var folder = OperatingSystem.IsWindows() ? ScanTargets.DownloadsFolder() : null;
        if (_engine is null || folder is null || !Directory.Exists(folder))
        {
            LiveText = "Pasta Downloads não encontrada.";
            _liveWatch = false;
            OnPropertyChanged(nameof(LiveWatch));
            return;
        }
        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            _watcher = new FolderWatcher(folder, _engine, v => dispatcher?.BeginInvoke(() =>
            {
                LiveText = $"Ao vivo: {DateTime.Now:HH:mm:ss} verificou \"{Path.GetFileName(v.Path)}\" — {v.VerdictText}.";
                if (ScanEngine.IsFinding(v) && !_ignore.Contains(v.Path, v.Sha256))
                {
                    AddFinding(v);
                    Host?.Notify($"Arquivo novo em Downloads com sinais para revisar: {Path.GetFileName(v.Path)}");
                }
            }));
            LiveText = $"Vigiando {folder} enquanto esta página estiver aberta. Arquivos novos são verificados alguns segundos depois de terminarem de baixar.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            LiveText = $"Não foi possível vigiar Downloads: {ex.Message}";
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
