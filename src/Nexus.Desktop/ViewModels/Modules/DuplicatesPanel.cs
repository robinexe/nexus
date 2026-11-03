using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Nexus.Broker;
using Nexus.DiskTools.Analysis;

namespace Nexus.Desktop.ViewModels;

/// <summary>Uma cópia de um grupo de duplicados (caixa de marcar = mover para a quarentena).</summary>
public sealed class DupFileRow : ObservableObject
{
    private readonly DuplicatesPanel _panel;
    private bool _selected;

    public DupFileRow(DupFile file, DupGroupRow group, DuplicatesPanel panel)
    {
        File = file;
        Group = group;
        _panel = panel;
    }

    public DupFile File { get; }
    public DupGroupRow Group { get; }
    public string Name => Path.GetFileName(File.Path);
    public string Folder => Path.GetDirectoryName(File.Path) ?? "";
    public string When => File.LastWriteUtc.ToLocalTime().ToString("g", Loc.Culture);
    public string Tooltip => File.Path;

    /// <summary>Marcada para ir à quarentena. Nunca todas as cópias do grupo: a última que sobrar não deixa marcar.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (value && !_selected && Group.Files.Count(f => f._selected) >= Group.Files.Count - 1) { OnPropertyChanged(); _panel.Note(Loc.T("Dup.KeepOne")); return; }
            if (Set(ref _selected, value)) _panel.SelectionChanged();
        }
    }

    internal void SetSelected(bool value) { if (Set(ref _selected, value, nameof(Selected))) _panel.SelectionChanged(); }
}

/// <summary>Um grupo de cópias idênticas: "3 cópias · 1,2 GB cada · sobra 2,4 GB".</summary>
public sealed class DupGroupRow
{
    public DupGroupRow(DuplicateGroup group, DuplicatesPanel panel)
    {
        Group = group;
        Files = [.. group.Files.Select(f => new DupFileRow(f, this, panel))];
    }

    public DuplicateGroup Group { get; }
    public IReadOnlyList<DupFileRow> Files { get; }
    public string Header => Loc.F("Dup.GroupHeader", Path.GetFileName(Group.Files[0].Path), Group.Files.Count, Format.Bytes(Group.Size), Format.Bytes(Group.Wasted));
}

/// <summary>
/// Arquivos duplicados (dentro do analisador de disco): procura na pasta que está aberta (ou na unidade escolhida) por tamanho e
/// depois por SHA-256 — só leitura, cancelável, fora da thread da interface. O que o usuário marcar vai para a quarentena do NEXUS
/// pelo aplicador (<see cref="DuplicateAction"/>, sua conta, com Desfazer); nada é apagado de verdade e sempre fica uma cópia.
/// Pastas do Windows, de programas, de jogos e de dados de aplicativos ficam fora da busca.
/// </summary>
public sealed class DuplicatesPanel : ObservableObject, IDisposable
{
    public const string DuplicateAction = "files.duplicate.quarantine@1";
    public const int MaxShownGroups = 150;
    public const int MaxSelection = 500;

    private readonly Func<IModuleHost?> _host;
    private readonly Func<string?> _root;
    private CancellationTokenSource? _cancel;
    private bool _scanning;
    private bool _working;
    private string _progress = "";
    private string _summary = "";
    private string _message = "";
    private IReadOnlyList<(Guid Run, BrokerLevel Level)> _lastRuns = [];

    public DuplicatesPanel(Func<IModuleHost?> host, Func<string?> root)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !_scanning && !_working);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => _scanning);
        SelectExtrasCommand = new RelayCommand(SelectExtras, () => !_scanning && Groups.Count > 0);
        ClearCommand = new RelayCommand(() => { foreach (var f in Groups.SelectMany(g => g.Files)) f.SetSelected(false); }, () => SelectedCount > 0);
        QuarantineCommand = new RelayCommand(() => _ = QuarantineAsync(), () => !_scanning && !_working && SelectedCount > 0);
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => !_working && _lastRuns.Count > 0);
    }

    public void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
    }

    public ObservableCollection<DupGroupRow> Groups { get; } = [];
    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectExtrasCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand QuarantineCommand { get; }
    public RelayCommand UndoCommand { get; }
    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Summary { get => _summary; private set { if (Set(ref _summary, value)) OnPropertyChanged(nameof(HasSummary)); } }
    public bool HasSummary => _summary.Length > 0;
    public string Message { get => _message; private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;
    public bool HasGroups => Groups.Count > 0;
    public bool CanUndo => _lastRuns.Count > 0;
    public string RootText => Loc.F("Dup.Where", _root() ?? "—");
    public int SelectedCount => Groups.Sum(g => g.Files.Count(f => f.Selected));
    public string SelectionText => Loc.F("Dup.Selection", SelectedCount, Format.Bytes(Groups.Sum(g => g.Files.Where(f => f.Selected).Sum(f => f.File.Size))));

    /// <summary>A pasta aberta no analisador mudou: atualiza o "Procurar em".</summary>
    public void RootChanged() => OnPropertyChanged(nameof(RootText));

    internal void Note(string text) => Message = text;

    internal void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectExtras()
    {
        foreach (var g in Groups)
        {
            var extra = DuplicateFinder.ExtraCopies(g.Group).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in g.Files) f.SetSelected(extra.Contains(f.File.Path));
        }
    }

    /// <summary>Pastas que nunca entram, além das do <see cref="DuplicateFinder"/>: os dados do próprio NEXUS.</summary>
    internal static IReadOnlyList<string> ExcludedRoots() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS"),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    ];

    private async Task ScanAsync()
    {
        var root = _root();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { Message = Loc.T("Dup.NoFolder"); return; }
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        Scanning = true;
        Message = "";
        Summary = "";
        Groups.Clear();
        OnPropertyChanged(nameof(HasGroups));
        SelectionChanged();
        var progress = new Progress<DuplicateProgress>(p => Progress = p.Stage == "list"
            ? Loc.F("Dup.Listing", p.FilesSeen)
            : Loc.F("Dup.Hashing", p.BytesToHash <= 0 ? 0 : Math.Min(100, 100.0 * p.HashedBytes / p.BytesToHash)));
        try
        {
            var result = await Task.Run(() => DuplicateFinder.Find(new FileSystemDuplicateSource(), [root], DuplicateFinder.DefaultMinSize, progress, ExcludedRoots(), token), token);
            foreach (var g in result.Groups.Take(MaxShownGroups)) Groups.Add(new DupGroupRow(g, this));
            OnPropertyChanged(nameof(HasGroups));
            Summary = result.Cancelled ? Loc.T("Dup.Cancelled")
                : result.Groups.Count == 0 ? Loc.F("Dup.None", result.FilesSeen)
                : Loc.F("Dup.Found", result.Groups.Count, Format.Bytes(result.Wasted), result.FilesSeen) +
                  (result.Groups.Count > MaxShownGroups ? Loc.F("Dup.Shown", MaxShownGroups) : "");
        }
        catch (OperationCanceledException) { Summary = Loc.T("Dup.Cancelled"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("Dup.Failed", ex.Message); }
        finally
        {
            Progress = "";
            Scanning = false;
            SelectionChanged();
        }
    }

    private async Task QuarantineAsync()
    {
        if (_host() is not { } host) return;
        var chosen = Groups.SelectMany(g => g.Files).Where(f => f.Selected).ToList();
        if (chosen.Count == 0) return;
        if (chosen.Count > MaxSelection) { Message = Loc.F("Dup.TooMany", MaxSelection); return; }
        if (Groups.Any(g => !DuplicateFinder.KeepsOne(g.Group, g.Files.Where(f => f.Selected).Select(f => f.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase))))
        { Message = Loc.T("Dup.KeepOne"); return; }
        var requests = chosen.Select(f => new ActionRequest(DuplicateAction, null, new Dictionary<string, string> { ["path"] = f.File.Path, ["sha256"] = f.Group.Group.Sha256 })).ToList();
        var bytes = chosen.Sum(f => f.File.Size);
        var list = string.Join("\n", chosen.Take(12).Select(f => "• " + f.File.Path)) + (chosen.Count > 12 ? "\n" + Loc.F("Dup.AndMore", chosen.Count - 12) : "");
        _working = true;
        try
        {
            var r = await host.ApplyAsync(Loc.T("Dup.QuarantineTitle"), Loc.F("Dup.QuarantineConfirm", chosen.Count, Format.Bytes(bytes), list), requests, BrokerLevel.User);
            if (r is null) return; // cancelado
            if (r.AllRunIds.Count > 0)
            {
                _lastRuns = [.. r.AllRunIds.Select(id => (id, BrokerLevel.User))];
                OnPropertyChanged(nameof(CanUndo));
            }
            var moved = chosen.Count(f => !File.Exists(f.File.Path));
            Message = moved == chosen.Count ? Loc.F("Dup.Moved", moved, Format.Bytes(chosen.Where(f => !File.Exists(f.File.Path)).Sum(f => f.File.Size)))
                : Loc.F("Dup.MovedSome", moved, chosen.Count, r.Message);
            RemoveGone();
        }
        finally
        {
            _working = false;
            SelectionChanged();
        }
    }

    private async Task UndoAsync()
    {
        if (_host() is not { } host || _lastRuns.Count == 0) return;
        _working = true;
        try
        {
            var ok = true;
            foreach (var (run, level) in _lastRuns.Reverse())
            {
                var r = await host.RevertAsync(run, level);
                if (r is null) return; // cancelado
                ok &= r.Ok;
            }
            _lastRuns = ok ? [] : _lastRuns;
            OnPropertyChanged(nameof(CanUndo));
            Message = ok ? Loc.T("Dup.Undone") : Loc.T("Dup.UndoFailed");
        }
        finally
        {
            _working = false;
            SelectionChanged();
        }
    }

    /// <summary>Tira da lista o que saiu do lugar; grupo com uma cópia só deixa de ser duplicado.</summary>
    private void RemoveGone()
    {
        foreach (var g in Groups.ToList())
        {
            var left = g.Group.Files.Where(f => File.Exists(f.Path)).ToList();
            var index = Groups.IndexOf(g);
            Groups.RemoveAt(index);
            if (left.Count > 1) Groups.Insert(index, new DupGroupRow(g.Group with { Files = left }, this));
        }
        OnPropertyChanged(nameof(HasGroups));
    }
}
