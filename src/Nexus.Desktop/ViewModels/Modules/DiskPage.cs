using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.DiskTools.Analysis;
using Nexus.DiskTools.SecureDelete;
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
                case DiskTile { File: { } file }: Message = $"{file.Name}: {Format.Bytes(file.Size)} em {file.Folder}."; break;
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

    public override string Title => "Espaço em disco";
    public override string Group => "Limpar";
    public override string Subtitle => "Veja o que ocupa o seu disco, pasta por pasta. Só lê: nada é apagado por esta análise.";

    public ObservableCollection<DiskVolumeRow> Volumes { get; } = [];

    public DiskVolumeRow? SelectedVolume
    {
        get => _selectedVolume;
        set { if (Set(ref _selectedVolume, value)) OnPropertyChanged(nameof(HasVolume)); }
    }

    public bool HasVolume => SelectedVolume is not null;

    public bool Scanning
    {
        get => _scanning;
        private set { if (Set(ref _scanning, value)) CommandManager.InvalidateRequerySuggested(); }
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
            Message = $"Não foi possível listar as unidades: {ex.Message}";
        }
    }

    private Task PickFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta para analisar", Multiselect = false };
        return dialog.ShowDialog() == true ? AnalyzeAsync(dialog.FolderName) : Task.CompletedTask;
    }

    private async Task AnalyzeAsync(string? root)
    {
        if (root is null || Scanning) return;
        _scan?.Dispose();
        _scan = new CancellationTokenSource();
        Scanning = true;
        Message = "";
        ProgressText = "Começando…";
        var progress = new Progress<ScanProgress>(p => ProgressText =
            $"{p.Files.ToString("N0", Pt)} arquivos · {Format.Bytes(p.Bytes)} · {p.Directories.ToString("N0", Pt)} pastas" +
            (p.AccessDenied > 0 ? $" · {p.AccessDenied} sem acesso" : "") + $"\n{p.CurrentPath}");
        try
        {
            var result = await DiskScanner.ScanAsync(root, new DiskScanOptions(), progress, _scan.Token);
            var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            var insights = await Task.Run(() => KnownFolders.Analyze(result, new KnownFolderContext { EpicNames = GameLibraries.ReadEpicNames(epic) }));
            _result = result;
            OnPropertyChanged(nameof(HasResult));
            Summary = $"{Format.Bytes(result.Root.Size)} em {result.Files.ToString("N0", Pt)} arquivos e {result.Directories.ToString("N0", Pt)} pastas, " +
                      $"lidos em {result.Elapsed.TotalSeconds.ToString("N1", Pt)} s." +
                      (result.AccessDenied > 0 ? $" {result.AccessDenied} pasta(s) sem permissão de leitura foram puladas (o tamanho delas não entra)." : "") +
                      (result.ReparsePointsSkipped > 0 ? $" {result.ReparsePointsSkipped} link(s)/junção(ões) não seguidos (o conteúdo é contado onde realmente está)." : "") +
                      (result.CloudOnlyFiles > 0 ? $" {Format.Bytes(result.CloudOnlyBytes)} só na nuvem (OneDrive) não ocupam este disco." : "");
            var biggest = Math.Max(1, result.TopFiles.Count > 0 ? result.TopFiles[0].Size : 1);
            TopFiles = result.TopFiles.Select(f => new DiskFileRow(f, (double)f.Size / biggest)).ToList();
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
            Message = "Análise cancelada. Nada foi alterado.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ProgressText = "";
            Message = $"Não foi possível analisar: {ex.Message}";
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
        FolderTitle = $"{folder.FullPath} · {Format.Bytes(folder.Size)}" + (folder.Incomplete ? " (mínimo: parte sem acesso)" : "");
        var tiles = folder.Tiles();
        if (tiles.Count > MaxTiles)
        {
            var rest = tiles.Skip(MaxTiles - 1).ToList();
            tiles = [.. tiles.Take(MaxTiles - 1), new DiskTile($"Mais {rest.Count} itens pequenos", rest.Sum(t => t.Size), DiskTileKind.OtherFiles, null, null)];
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
            Message = "O item não existe mais.";
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message = $"Não foi possível abrir o Explorer: {ex.Message}";
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
            MediaKind.Hdd => $"Disco: {media.Describe()}. Em HD, uma passada de dados aleatórios sobrescreve o conteúdo no lugar.",
            MediaKind.Ssd => $"Disco: {media.Describe()}. ATENÇÃO: em SSD/NVMe a sobrescrita NÃO é garantida. O controlador grava em outras células " +
                             "(nivelamento de desgaste) e o TRIM pode já ter liberado as antigas; cópias físicas podem continuar no chip. O arquivo some do " +
                             $"Windows, mas para proteção real use BitLocker no disco (os restos ficam ilegíveis) e, depois, \"{cipher}\" (Prompt) para sobrescrever o espaço livre.",
            _ => $"Tipo de disco desconhecido. Se for SSD/NVMe, a sobrescrita não é garantida (nivelamento de desgaste e TRIM); use BitLocker e \"{cipher}\" para proteção real.",
        };
    }

    private async Task SecureDeleteAsync()
    {
        if (Host is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Exclusão segura: escolha UM arquivo para apagar para sempre",
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
                SecureDeleteNote = "Recusado: " + refused;
                return;
            }
            var (info, media) = await Task.Run(() =>
            {
                var fi = new FileInfo(path);
                var m = OperatingSystem.IsWindows() ? StorageMedia.Query(path[0]) : StorageMediaInfo.Unknown;
                return (fi, m);
            });
            if (!info.Exists) { SecureDeleteNote = "O arquivo não existe mais."; return; }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                SecureDeleteNote = "Recusado: o item é um link ou arquivo de nuvem; sobrescrevê-lo não apagaria o conteúdo real.";
                return;
            }
            var warning = MediaWarning(media, path[0]);
            SecureDeleteNote = warning;

            // Confirmação 1: o que é e por que é diferente de apagar.
            if (!Host.Confirm("Exclusão segura: IRREVERSÍVEL",
                    "A exclusão segura sobrescreve o conteúdo com dados aleatórios e apaga o arquivo.\n\n" +
                    "• NÃO vai para a Lixeira nem para a quarentena do NEXUS.\n" +
                    "• O botão Desfazer NÃO funciona para isto: não existe cópia.\n" +
                    "• Cópias em backup, nuvem, Histórico de Arquivos ou pontos de restauração continuam existindo.\n\n" +
                    warning + "\n\nContinuar?"))
            {
                Host.Notify("Cancelado. Nada foi alterado.");
                return;
            }

            // Confirmação 2 (pelo aplicador), com o nome do arquivo. O pedido leva a confirmação explícita da ação irreversível.
            var request = new ActionRequest(SecureDeleteActionId, null, new Dictionary<string, string>(StringComparer.Ordinal) { ["filePath"] = path },
                ConfirmIrreversible: true);
            var r = await Host.ApplyAsync("Apagar para sempre",
                $"Apagar PARA SEMPRE \"{info.Name}\" ({Format.Bytes(info.Length)})?\n\n{path}\n\nNão há como desfazer.", [request], BrokerLevel.User);
            if (r is null) return; // cancelado na segunda confirmação
            var item = r.Response?.Results is { Count: > 0 } results ? results[0] : null;
            SecureDeleteNote = (r.Response, item) switch
            {
                ({ Ok: true }, { Outcome: "Verified" }) => $"\"{info.Name}\" foi sobrescrito e apagado. {warning}",
                ({ Ok: true }, { } x) => $"Não apagado ({x.Outcome}): {x.Detail}",
                ({ Ok: false, Error: { } e }, _) => $"Recusado pelo aplicador ({e.Code}): {e.Detail}",
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
    public string FreeText => $"{Format.Bytes(Volume.FreeBytes)} livres de {Format.Bytes(Volume.TotalBytes)}";
    public bool AlmostFull => Volume.TotalBytes > 0 && Volume.FreeBytes < Volume.TotalBytes / 10;
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
    public string Detail => $"{FileCategories.Label(File.Category)} · alterado em {File.LastWriteUtc.ToLocalTime():dd/MM/yyyy}";
    public double Fraction { get; } = fraction;
}

public sealed class DiskCategoryRow(CategoryTotal total, double fraction)
{
    public string Label => total.Label;
    public string SizeText => Format.Bytes(total.Bytes);
    public string FilesText => $"{total.Files.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} arquivos";
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
}
