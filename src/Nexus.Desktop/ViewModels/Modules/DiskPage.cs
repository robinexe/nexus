using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.DiskTools.Analysis;
using Nexus.DiskTools.SecureDelete;
using Nexus.Desktop.Views.Controls;
using Nexus.Desktop.Views.Modules.Disk;
using Nexus.DiskTools.Volumes;
using Nexus.HardwareDiscovery;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Espaço em disco. A análise é SOMENTE LEITURA e roda aqui mesmo (nada é apagado por esta tela). A única escrita é a exclusão
/// segura de um arquivo, que vai pelo aplicador (<see cref="IModuleHost.ApplyAsync"/>) como ação irreversível do catálogo,
/// com duas confirmações e a confirmação explícita no pedido (<see cref="ActionRequest.ConfirmIrreversible"/>).
/// </summary>
public sealed class DiskPage : ModulePage, IDisposable
{
    public const string SecureDeleteActionId = "disk.secure-delete@1";
    private const int MaxTiles = 250;
    /// <summary>Quantos dos maiores arquivos aparecem como cartões (o resto fica recolhido).</summary>
    public const int TopCardCount = 9;

    private CancellationTokenSource? _scan;
    private DiskScanResult? _result;
    private DirNode? _folder;
    private DiskVolumeRow? _selectedVolume;
    private bool _scanning;
    private string _progressText = "";
    private string _summary = "";
    private string _secureDeleteNote = "";
    private IReadOnlyList<DiskTile> _tiles = [];
    private IReadOnlyList<DiskCrumb> _crumbs = [];
    private IReadOnlyList<DiskFolderRow> _children = [];
    private IReadOnlyList<DiskFileRow> _topFiles = [];
    private IReadOnlyList<DiskCategoryRow> _categories = [];
    private IReadOnlyList<DiskInsightRow> _insights = [];
    private string _folderTitle = "";
    private bool _volumesLoading = true;
    private double _scannedBytes = double.NaN, _scannedFiles = double.NaN;
    private IReadOnlyList<DiskFileRow> _topCards = [];
    private IReadOnlyList<DiskFileRow> _moreFiles = [];

    public DiskPage()
    {
        AnalyzeVolumeCommand = new RelayCommand(() => _ = AnalyzeAsync(SelectedVolume?.Volume.RootPath), () => !Scanning && SelectedVolume is not null);
        AnalyzeFolderCommand = new RelayCommand(() => _ = PickFolderAsync(), () => !Scanning);
        CancelCommand = new RelayCommand(() => _scan?.Cancel(), () => Scanning);
        RefreshVolumesCommand = new RelayCommand(() => _ = LoadVolumesAsync(), () => !Scanning);
        UpCommand = new RelayCommand(() => { if (_folder?.Parent is { } p) Show(p); }, () => _folder?.Parent is not null);
        OpenTileCommand = new DiskCommand(p =>
        {
            switch (p)
            {
                case DiskTile { Folder: { } f }: Show(f); break;
                case DiskTile { File: { } file }: Message = Loc.F("Disk.NNN", file.Name, Format.Bytes(file.Size), file.Folder); break;
                case DiskFolderRow row: Show(row.Node); break;
                case DiskCrumb crumb: Show(crumb.Node); break;
            }
        });
        ShowInExplorerCommand = new DiskCommand(p => ShowInExplorer(p as string));
        SecureDeleteCommand = new RelayCommand(() => _ = SecureDeleteAsync(), () => !Busy && !Scanning && Host is not null);
    }

    public void Dispose()
    {
        _scan?.Cancel();
        _scan?.Dispose();
        _scan = null;
    }

    public override string Title => Loc.T("Disk.NavTitle");
    public override string Group => Loc.T("Nav.Limpar");
    public override string Subtitle => Loc.T("Disk.NavSubtitle");

    public ObservableCollection<DiskVolumeRow> Volumes { get; } = [];

    public DiskVolumeRow? SelectedVolume
    {
        get => _selectedVolume;
        set
        {
            if (!Set(ref _selectedVolume, value)) return;
            OnPropertyChanged(nameof(HasVolume));
            OnPropertyChanged(nameof(FreeBytes));
            OnPropertyChanged(nameof(UsedPercentValue));
            OnPropertyChanged(nameof(HeroTitle));
        }
    }

    public bool HasVolume => SelectedVolume is not null;

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Espaço livre da unidade escolhida (NaN = ainda sem unidade).</summary>
    public double FreeBytes => SelectedVolume is { } v ? v.Volume.FreeBytes : double.NaN;
    /// <summary>% usado da unidade escolhida (0..100; NaN = sem unidade).</summary>
    public double UsedPercentValue => SelectedVolume is { } v ? Math.Round(v.UsedFraction * 100, 1) : double.NaN;
    public string HeroTitle => SelectedVolume is { } v ? Loc.F("Disk.LivreN", v.Volume.RootPath.TrimEnd('\\')) : Loc.T("Disk.EspacoLivre");
    /// <summary>Lendo a lista de unidades (esqueleto no destaque).</summary>
    public bool VolumesLoading { get => _volumesLoading; private set => Set(ref _volumesLoading, value); }
    /// <summary>Bytes e arquivos lidos até agora na varredura (contam ao vivo).</summary>
    public double ScannedBytes { get => _scannedBytes; private set => Set(ref _scannedBytes, value); }
    public double ScannedFiles { get => _scannedFiles; private set => Set(ref _scannedFiles, value); }
    public double ResultBytes => _result is { } r ? r.Root.Size : double.NaN;
    public double ResultFiles => _result is { } r ? r.Files : double.NaN;
    public double ResultFolders => _result is { } r ? r.Directories : double.NaN;
    public double ResultSkipped => _result is { } r ? r.AccessDenied : double.NaN;
    public string ResultSkippedDetail => _result is { } r && r.CloudOnlyFiles > 0
        ? Loc.F("Disk.NSoNuvemNao", Format.Bytes(r.CloudOnlyBytes))
        : Loc.T("Disk.SemPermissaoLeituraTamanho");
    public string ResultTimeText => _result is { } r ? Loc.F("Disk.LidoNS", r.Elapsed.TotalSeconds.ToString("N1", Pt)) : "";
    /// <summary>Sem resultado e sem varredura em andamento: mostra o estado vazio.</summary>
    public bool ShowEmpty => _result is null && !Scanning;
    /// <summary>Os maiores arquivos que viram cartões (os primeiros); o resto fica na lista recolhida.</summary>
    public IReadOnlyList<DiskFileRow> TopCards { get => _topCards; private set => Set(ref _topCards, value); }
    public IReadOnlyList<DiskFileRow> MoreFiles { get => _moreFiles; private set { if (Set(ref _moreFiles, value)) OnPropertyChanged(nameof(HasMoreFiles)); } }
    public bool HasMoreFiles => MoreFiles.Count > 0;
    public string MoreFilesHeader => Loc.F("Disk.VerOutrosNMaiores", MoreFiles.Count);
    public bool HasTopFiles => TopFiles.Count > 0;

    public bool Scanning
    {
        get => _scanning;
        private set { if (Set(ref _scanning, value)) { OnPropertyChanged(nameof(ShowEmpty)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public bool HasResult => _result is not null;
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string FolderTitle { get => _folderTitle; private set => Set(ref _folderTitle, value); }
    public IReadOnlyList<DiskTile> Tiles { get => _tiles; private set => Set(ref _tiles, value); }
    public IReadOnlyList<DiskCrumb> Crumbs { get => _crumbs; private set => Set(ref _crumbs, value); }
    public IReadOnlyList<DiskFolderRow> Children { get => _children; private set => Set(ref _children, value); }
    public IReadOnlyList<DiskFileRow> TopFiles { get => _topFiles; private set => Set(ref _topFiles, value); }
    public IReadOnlyList<DiskCategoryRow> Categories { get => _categories; private set => Set(ref _categories, value); }
    public IReadOnlyList<DiskInsightRow> Insights { get => _insights; private set => Set(ref _insights, value); }
    public bool HasInsights => Insights.Count > 0;
    /// <summary>Aviso da última escolha de exclusão segura (SSD, resultado).</summary>
    public string SecureDeleteNote { get => _secureDeleteNote; private set => Set(ref _secureDeleteNote, value); }

    public ICommand AnalyzeVolumeCommand { get; }
    public ICommand AnalyzeFolderCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshVolumesCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand OpenTileCommand { get; }
    public ICommand ShowInExplorerCommand { get; }
    public ICommand SecureDeleteCommand { get; }

    public override Task OnFactsReadyAsync(SystemFacts facts) => Volumes.Count == 0 ? LoadVolumesAsync() : Task.CompletedTask;

    public override Task OnShownAsync() => Volumes.Count == 0 ? LoadVolumesAsync() : Task.CompletedTask;

    private async Task LoadVolumesAsync()
    {
        VolumesLoading = true;
        try
        {
            var list = await Task.Run(VolumeCatalog.List);
            var previous = SelectedVolume?.Volume.RootPath;
            Volumes.Clear();
            foreach (var v in list) Volumes.Add(new DiskVolumeRow(v));
            var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            SelectedVolume = Volumes.FirstOrDefault(v => string.Equals(v.Volume.RootPath, previous, StringComparison.OrdinalIgnoreCase))
                             ?? Volumes.FirstOrDefault(v => string.Equals(v.Volume.RootPath, system, StringComparison.OrdinalIgnoreCase))
                             ?? Volumes.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = Loc.F("Disk.NaoFoiPossivelListar", ex.Message);
        }
        finally
        {
            VolumesLoading = false;
        }
    }

    private Task PickFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Disk.EscolhaPastaAnalisar"), Multiselect = false };
        return dialog.ShowDialog() == true ? AnalyzeAsync(dialog.FolderName) : Task.CompletedTask;
    }

    private async Task AnalyzeAsync(string? root)
    {
        if (root is null || Scanning) return;
        _scan?.Dispose();
        _scan = new CancellationTokenSource();
        Scanning = true;
        Message = "";
        ProgressText = Loc.T("Disk.Comecando");
        ScannedBytes = 0;
        ScannedFiles = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            ScannedBytes = p.Bytes;
            ScannedFiles = p.Files;
            ProgressText = Loc.F("Disk.NArquivosNN", p.Files.ToString("N0", Pt), Format.Bytes(p.Bytes), p.Directories.ToString("N0", Pt)) +
                           (p.AccessDenied > 0 ? Loc.F("Disk.NSemAcesso", p.AccessDenied) : "") + $"\n{p.CurrentPath}";
        });
        try
        {
            var result = await DiskScanner.ScanAsync(root, new DiskScanOptions(), progress, _scan.Token);
            var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            var insights = await Task.Run(() => KnownFolders.Analyze(result, new KnownFolderContext { EpicNames = GameLibraries.ReadEpicNames(epic) }));
            _result = result;
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ResultBytes));
            OnPropertyChanged(nameof(ResultFiles));
            OnPropertyChanged(nameof(ResultFolders));
            OnPropertyChanged(nameof(ResultSkipped));
            OnPropertyChanged(nameof(ResultSkippedDetail));
            OnPropertyChanged(nameof(ResultTimeText));
            Summary = Loc.F("Disk.NNArquivosN", Format.Bytes(result.Root.Size), result.Files.ToString("N0", Pt), result.Directories.ToString("N0", Pt)) +
                      Loc.F("Disk.LidosNS", result.Elapsed.TotalSeconds.ToString("N1", Pt)) +
                      (result.AccessDenied > 0 ? Loc.F("Disk.NPastaSSem", result.AccessDenied) : "") +
                      (result.ReparsePointsSkipped > 0 ? Loc.F("Disk.NLinkSJuncao", result.ReparsePointsSkipped) : "") +
                      (result.CloudOnlyFiles > 0 ? Loc.F("Disk.NSoNuvemOnedrive", Format.Bytes(result.CloudOnlyBytes)) : "");
            var biggest = Math.Max(1, result.TopFiles.Count > 0 ? result.TopFiles[0].Size : 1);
            TopFiles = result.TopFiles.Select(f => new DiskFileRow(f, (double)f.Size / biggest)).ToList();
            TopCards = TopFiles.Take(TopCardCount).ToList();
            MoreFiles = TopFiles.Skip(TopCardCount).ToList();
            OnPropertyChanged(nameof(MoreFilesHeader));
            OnPropertyChanged(nameof(HasTopFiles));
            var totalCat = Math.Max(1, result.Categories.Sum(c => c.Bytes));
            Categories = result.Categories.Select(c => new DiskCategoryRow(c, (double)c.Bytes / totalCat)).ToList();
            Insights = insights.Take(40).Select(i => new DiskInsightRow(i)).ToList();
            OnPropertyChanged(nameof(HasInsights));
            Show(result.Root);
            ProgressText = "";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "";
            Message = Loc.T("Disk.AnaliseCanceladaNadaFoi");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ProgressText = "";
            Message = Loc.F("Disk.NaoFoiPossivelAnalisar", ex.Message);
        }
        finally
        {
            Scanning = false;
        }
    }

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private void Show(DirNode folder)
    {
        _folder = folder;
        FolderTitle = $"{folder.FullPath} · {Format.Bytes(folder.Size)}" + (folder.Incomplete ? Loc.T("Disk.MinimoParteSemAcesso") : "");
        var tiles = folder.Tiles();
        if (tiles.Count > MaxTiles)
        {
            var rest = tiles.Skip(MaxTiles - 1).ToList();
            tiles = [.. tiles.Take(MaxTiles - 1), new DiskTile(Loc.F("Disk.MaisNItensPequenos", rest.Count), rest.Sum(t => t.Size), DiskTileKind.OtherFiles, null, null)];
        }
        Tiles = tiles;
        Crumbs = folder.Lineage().Select(n => new DiskCrumb(n)).ToList();
        var max = Math.Max(1, folder.Size);
        Children = folder.Children.Take(60).Select(c => new DiskFolderRow(c, (double)c.Size / max)).ToList();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Mostra o item no Explorer (só abre a janela; nada é alterado).</summary>
    private void ShowInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('"', StringComparison.Ordinal) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            Message = Loc.T("Disk.ItemNaoExisteMais");
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = Loc.F("Disk.NaoFoiPossivelAbrir", ex.Message);
        }
    }

    // ------------------------------------------------------------------ exclusão segura (irreversível)

    private static SecureDeletePolicy Policy()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new SecureDeletePolicy(string.IsNullOrEmpty(windows) ? @"C:\Windows" : windows,
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS"),
        ]);
    }

    /// <summary>Texto honesto sobre o tipo de mídia (SSD/NVMe: sobrescrita não garantida).</summary>
    public static string MediaWarning(StorageMediaInfo media, char drive)
    {
        ArgumentNullException.ThrowIfNull(media);
        var cipher = $"cipher /w:{char.ToUpperInvariant(drive)}:\\";
        return media.Kind switch
        {
            MediaKind.Hdd => Loc.F("Disk.DiscoNHdPassada", media.Describe()),
            MediaKind.Ssd => Loc.F("Disk.DiscoNAtencaoSsd", media.Describe()) +
                             Loc.T("Disk.NivelamentoDesgasteTrimPode") +
                             Loc.F("Disk.WindowsMasProtecaoReal", cipher),
            _ => Loc.F("Disk.TipoDiscoDesconhecidoFor", cipher),
        };
    }

    private async Task SecureDeleteAsync()
    {
        if (Host is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Disk.ExclusaoSeguraEscolhaArquivo"),
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = false, // um atalho .lnk é o próprio arquivo escolhido, nunca o destino dele
            ValidateNames = true,
        };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.FileName;

        await RunAsync(async () =>
        {
            // Pré-verificação somente leitura, com a MESMA política do aplicador, para explicar a recusa antes de pedir confirmação.
            if (Policy().Check(path) is { } refused)
            {
                SecureDeleteNote = Loc.T("Disk.Recusado") + refused;
                return;
            }
            var (info, media) = await Task.Run(() =>
            {
                var fi = new FileInfo(path);
                var m = OperatingSystem.IsWindows() ? StorageMedia.Query(path[0]) : StorageMediaInfo.Unknown;
                return (fi, m);
            });
            if (!info.Exists) { SecureDeleteNote = Loc.T("Disk.ArquivoNaoExisteMais"); return; }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                SecureDeleteNote = Loc.T("Disk.RecusadoItemLinkOu");
                return;
            }
            var warning = MediaWarning(media, path[0]);
            SecureDeleteNote = warning;

            // Confirmação 1: o que é e por que é diferente de apagar.
            if (!Host.Confirm(Loc.T("Disk.ExclusaoSeguraIrreversivel2"),
                    Loc.T("Disk.ExclusaoSeguraSobrescreveConteudo") +
                    Loc.T("Disk.NaoVaiLixeiraNem") +
                    Loc.T("Disk.BotaoDesfazerNaoFunciona") +
                    Loc.T("Disk.CopiasBackupNuvemHistorico") +
                    warning + Loc.T("Clean.Continuar")))
            {
                Host.Notify(Loc.T("Shell.CanceladoNadaFoiAlterado"));
                return;
            }

            // Confirmação 2 (pelo aplicador), com o nome do arquivo. O pedido leva a confirmação explícita da ação irreversível.
            var request = new ActionRequest(SecureDeleteActionId, null, new Dictionary<string, string>(StringComparer.Ordinal) { ["filePath"] = path },
                ConfirmIrreversible: true);
            var r = await Host.ApplyAsync(Loc.T("Disk.ApagarSempre"),
                Loc.F("Disk.ApagarSempreNN", info.Name, Format.Bytes(info.Length), path), [request], BrokerLevel.User);
            if (r is null) return; // cancelado na segunda confirmação
            var item = r.Response?.Results is { Count: > 0 } results ? results[0] : null;
            SecureDeleteNote = (r.Response, item) switch
            {
                ({ Ok: true }, { Outcome: "Verified" }) => Loc.F("Disk.NFoiSobrescritoApagado", info.Name, warning),
                ({ Ok: true }, { } x) => Loc.F("Disk.NaoApagadoNN", x.Outcome, x.Detail),
                ({ Ok: false, Error: { } e }, _) => Loc.F("Disk.RecusadoPeloAplicadorN", e.Code, e.Detail),
                _ => r.Message,
            };
        });
    }
}

/// <summary>Comando com parâmetro (as linhas e os blocos do treemap passam o próprio item).</summary>
public sealed class DiskCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed class DiskVolumeRow(VolumeInfo volume)
{
    public VolumeInfo Volume { get; } = volume;
    public string Name => Volume.DisplayName;
    public string MediaText => $"{Volume.Media.Describe()} · {Volume.FileSystem}";
    public double UsedFraction => Volume.UsedFraction;
    public string UsedPercent => Volume.UsedFraction.ToString("P0", CultureInfo.GetCultureInfo("pt-BR"));
    public string UsedText => Format.Bytes(Volume.UsedBytes);
    public string TotalText => Format.Bytes(Volume.TotalBytes);
    public string FreeText => Loc.F("Disk.NLivresN", Format.Bytes(Volume.FreeBytes), Format.Bytes(Volume.TotalBytes));
    public bool AlmostFull => Volume.TotalBytes > 0 && Volume.FreeBytes < Volume.TotalBytes / 10;
    public double UsedPercentValue => Math.Round(Volume.UsedFraction * 100, 1);
    public string FreeShort => Loc.F("Disk.NLivres", Format.Bytes(Volume.FreeBytes));
}

public sealed class DiskCrumb(DirNode node)
{
    public DirNode Node { get; } = node;
    public string Label => Node.Name;
}

public sealed class DiskFolderRow(DirNode node, double fraction)
{
    public DirNode Node { get; } = node;
    public string Name => Node.Name;
    public string SizeText => Format.Bytes(Node.Size) + (Node.Incomplete ? "+" : "");
    public double Fraction { get; } = fraction;
    public string Path => Node.FullPath;
}

public sealed class DiskFileRow(FileItem file, double fraction)
{
    public FileItem File { get; } = file;
    public string Name => File.Name;
    public string SizeText => Format.Bytes(File.Size);
    public string Folder => File.Folder;
    public string Path => File.FullPath;
    public string Detail => Loc.F("Disk.NAlteradoN", FileCategories.Label(File.Category), File.LastWriteUtc.ToLocalTime());
    public double Fraction { get; } = fraction;
    public double SizeBytes => File.Size;
    public string KindText => FileCategories.Label(File.Category);
    public string DateText => File.LastWriteUtc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
    public string IconKey => DiskPalette.IconKey(File.Category);
    public string ColorHex => DiskPalette.Hex(File.Category);
}

public sealed class DiskCategoryRow(CategoryTotal total, double fraction)
{
    public string Label => total.Label;
    public string IconKey => DiskPalette.IconKey(total.Category);
    public string ColorHex => DiskPalette.Hex(total.Category);
    public string PercentText => Fraction.ToString("P0", CultureInfo.GetCultureInfo("pt-BR"));
    public string SizeText => Format.Bytes(total.Bytes);
    public string FilesText => Loc.F("Disk.NArquivos", total.Files.ToString("N0", CultureInfo.GetCultureInfo("pt-BR")));
    public double Fraction { get; } = fraction;
}

public sealed class DiskInsightRow(FolderInsight insight)
{
    public FolderInsight Insight { get; } = insight;
    public string Title => Insight.Title;
    public string SizeText => Format.Bytes(Insight.Size);
    public string KindLabel => Insight.KindLabel;
    public string What => Insight.What;
    public string Advice => Insight.Advice;
    public string? Command => Insight.Command;
    public bool HasCommand => !string.IsNullOrEmpty(Insight.Command);
    public string Path => Insight.Path;
    public bool DoNotTouch => Insight.Kind == InsightKind.DoNotTouch;
    public bool IsGame => Insight.Kind == InsightKind.Game;
    /// <summary>Selo: vermelho = não apague à mão, verde = jogo, âmbar = ferramenta oficial, ciano = decisão sua.</summary>
    public Tone Tone => Insight.Kind switch
    {
        InsightKind.DoNotTouch => Tone.Bad,
        InsightKind.Game => Tone.Good,
        InsightKind.OfficialTool => Tone.Warn,
        _ => Tone.Info,
    };
    public string IconKey => Insight.Kind switch
    {
        InsightKind.DoNotTouch => "Icon.Lock",
        InsightKind.Game => "Icon.Games",
        InsightKind.OfficialTool => "Icon.Windows",
        _ => "Icon.User",
    };
}
