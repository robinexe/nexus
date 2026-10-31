using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Nexus.Experimentation;
using Nexus.Measurement;

namespace Nexus.Desktop.ViewModels;

/// <summary>Uma sessão na lista, com os números já formatados para a tela.</summary>
public sealed class SessionRow : ObservableObject
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private MeasuredSession _session;

    public SessionRow(MeasuredSession session) => _session = session;

    public MeasuredSession Session
    {
        get => _session;
        set
        {
            if (!Set(ref _session, value)) return;
            foreach (var p in new[] { nameof(Name), nameof(Tag), nameof(TagText), nameof(IsBefore), nameof(IsAfter) }) OnPropertyChanged(p);
        }
    }

    public string Name => Session.Name;
    public string Application => Session.Application;
    public string When => Session.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt);
    public string Source => Session.Source;
    public SessionTag Tag => Session.Tag;
    public bool IsBefore => Session.Tag == SessionTag.Before;
    public bool IsAfter => Session.Tag == SessionTag.After;
    public string TagText => Session.Tag switch { SessionTag.Before => "Antes", SessionTag.After => "Depois", _ => "" };
    public double AverageFps => Session.Summary.AverageFps;
    public double Low1Fps => Session.Summary.OnePercentLowAverageFps;
    public double P99Ms => Session.Summary.P99FrametimeMs;
    public double StuttersPerMinute => Session.Summary.StuttersPerMinute;
    public string AverageFpsText => Session.Summary.AverageFps.ToString("0", Pt);
    public string Low1Text => Session.Summary.OnePercentLowAverageFps.ToString("0", Pt);
    public string P99Text => Session.Summary.P99FrametimeMs.ToString("0.0", Pt) + " ms";
    public string StutterText => Session.Summary.StuttersPerMinute.ToString("0.0", Pt) + "/min";
    public string DurationText => TimeSpan.FromMilliseconds(Session.Summary.DurationMs).ToString(@"m\:ss", Pt);
    public string FramesText => Session.Summary.Frames.ToString("N0", Pt) + " quadros";
    public IReadOnlyList<TracePoint> Trace => Session.Trace;
    public IReadOnlyList<HistogramBin> Histogram => Session.Histogram;
}

/// <summary>Preferências locais da interface (só o caminho do PresentMon e o último jogo), em %LocalAppData%\NEXUS.</summary>
public sealed record DesktopSettings(string? PresentMonPath, string? LastProcess, int LastSeconds)
{
    private static string PathOf => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "desktop-settings.json");

    public static DesktopSettings Load()
    {
        try { return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(PathOf)) ?? new(null, null, 60); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { return new(null, null, 60); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Medir: capturar com o PresentMon (só leitura de frames) ou importar um CSV, e marcar sessões como antes/depois.
/// O NEXUS não baixa o PresentMon: o usuário indica o executável oficial (github.com/GameTechDev/PresentMon).
/// </summary>
public sealed class MeasurePage : PageViewModel, IDisposable
{
    private readonly SessionStore _store = new(SessionStore.DefaultFolder);
    private CancellationTokenSource? _cancel;
    private string? _presentMonPath;
    private string _processName = "cs2.exe";
    private int _seconds = 60;
    private bool _requestElevation = true;
    private bool _busy;
    private double _progress;
    private string _status = "";
    private string _log = "";
    private SessionRow? _selected;

    public MeasurePage()
    {
        var s = DesktopSettings.Load();
        _presentMonPath = PresentMonRunner.Locate(s.PresentMonPath, AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(s.LastProcess)) _processName = s.LastProcess;
        if (s.LastSeconds is >= PresentMonRunner.MinSeconds and <= PresentMonRunner.MaxSeconds) _seconds = s.LastSeconds;
        BrowseCommand = new RelayCommand(Browse, () => !Busy);
        CaptureCommand = new RelayCommand(() => _ = CaptureAsync(), () => !Busy && HasPresentMon && !string.IsNullOrWhiteSpace(ProcessName));
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => Busy);
        ImportCommand = new RelayCommand(Import, () => !Busy);
        DeleteCommand = new RelayCommand(Delete, () => !Busy && Selected is not null);
        TagBeforeCommand = new RelayCommand(() => Retag(SessionTag.Before), () => Selected is not null);
        TagAfterCommand = new RelayCommand(() => Retag(SessionTag.After), () => Selected is not null);
        ClearTagCommand = new RelayCommand(() => Retag(SessionTag.None), () => Selected is not null && Selected.Tag != SessionTag.None);
        Reload();
    }

    public override string Title => "Medir";
    public override string Group => "Medir";
    public override string Subtitle => "Grave o desempenho de uma partida e veja cada quadro. Medir não altera nada no PC.";

    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public Action? SessionsChanged { get; set; }

    public string? PresentMonPath
    {
        get => _presentMonPath;
        set { if (Set(ref _presentMonPath, value)) { OnPropertyChanged(nameof(HasPresentMon)); OnPropertyChanged(nameof(PresentMonText)); } }
    }
    public bool HasPresentMon => PresentMonPath is not null && File.Exists(PresentMonPath);
    public string PresentMonText => HasPresentMon ? System.IO.Path.GetFileName(PresentMonPath!) : "PresentMon não encontrado";
    public string PresentMonHelp { get; } =
        "O NEXUS usa o PresentMon, ferramenta gratuita da Intel, para ler o tempo de cada quadro. Baixe o executável em " +
        "github.com/GameTechDev/PresentMon (Releases) e indique onde salvou. Ou importe um CSV do PresentMon ou do CapFrameX.";
    public string ProcessName { get => _processName; set => Set(ref _processName, value); }
    public int Seconds { get => _seconds; set => Set(ref _seconds, Math.Clamp(value, PresentMonRunner.MinSeconds, PresentMonRunner.MaxSeconds)); }
    public bool RequestElevation { get => _requestElevation; set => Set(ref _requestElevation, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    /// <summary>0 a 1 durante a captura (tempo decorrido / duração).</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Log { get => _log; set => Set(ref _log, value); }
    public SessionRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool HasSessions => Sessions.Count > 0;
    public string HowTo { get; } =
        "Para uma comparação justa: mesmo mapa ou cena, mesma duração, jogo já aquecido. Grave 3 sessões antes da mudança, " +
        "aplique, grave 3 depois. Alternar (antes, depois, antes...) é ainda melhor.";

    public RelayCommand BrowseCommand { get; }
    public RelayCommand CaptureCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand TagBeforeCommand { get; }
    public RelayCommand TagAfterCommand { get; }
    public RelayCommand ClearTagCommand { get; }

    public void Dispose() => _cancel?.Dispose();

    public void Reload()
    {
        var keep = Selected?.Session.Id;
        var (list, bad) = _store.List();
        Sessions.Clear();
        foreach (var s in list) Sessions.Add(new SessionRow(s));
        Selected = Sessions.FirstOrDefault(r => r.Session.Id == keep) ?? Sessions.FirstOrDefault();
        OnPropertyChanged(nameof(HasSessions));
        if (bad > 0) Status = $"{bad} arquivo(s) de sessão ilegível(is) em {_store.Folder} foram ignorados.";
        SessionsChanged?.Invoke();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Onde está o PresentMon?", Filter = "PresentMon (PresentMon*.exe)|PresentMon*.exe|Executável (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;
        PresentMonPath = dialog.FileName;
        SaveSettings();
    }

    private void SaveSettings() => new DesktopSettings(PresentMonPath, ProcessName, Seconds).Save();

    private async Task CaptureAsync()
    {
        if (!HasPresentMon) return;
        Busy = true;
        Progress = 0;
        Log = "";
        SaveSettings();
        _cancel = new CancellationTokenSource();
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-captura-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var started = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Progress = Math.Min(1, (DateTime.Now - started).TotalSeconds / Seconds);
        timer.Start();
        try
        {
            Status = RequestElevation
                ? $"Abra o jogo. O Windows vai pedir permissão para o PresentMon; depois, {Seconds} s de captura de {ProcessName}."
                : $"Capturando {ProcessName} por {Seconds} s…";
            var progress = new Progress<string>(line => Log += line + Environment.NewLine);
            var result = await PresentMonRunner.RunAsync(new(PresentMonPath!, ProcessName.Trim(), Seconds, output, RequestElevation), _cancel.Token, progress);
            if (!result.Ok) { Status = result.Message; return; }
            var session = await Task.Run(() => _store.Import(output, $"{ProcessName} {DateTime.Now:dd/MM HH:mm}", ProcessName.Trim(), "capturado", started));
            Status = $"Sessão gravada: {session.Summary.AverageFps:0} FPS médio, 1% low {session.Summary.OnePercentLowAverageFps:0}.";
            Reload();
            Selected = Sessions.FirstOrDefault(r => r.Session.Id == session.Id);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            Status = $"A captura terminou, mas não deu para usar o resultado: {ex.Message}";
        }
        finally
        {
            timer.Stop();
            Progress = 0;
            Busy = false;
            _cancel.Dispose();
            _cancel = null;
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
        }
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Importar captura (CSV do PresentMon ou CapFrameX)", Filter = "CSV (*.csv)|*.csv", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var ok = 0;
        var errors = new List<string>();
        foreach (var f in dialog.FileNames)
        {
            try { _store.Import(f); ok++; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException) { errors.Add($"{System.IO.Path.GetFileName(f)}: {ex.Message}"); }
        }
        Reload();
        Status = $"{ok} sessão(ões) importada(s)." + (errors.Count > 0 ? " Não importadas: " + string.Join(" · ", errors) : "");
    }

    private void Delete()
    {
        if (Selected is not { } row) return;
        var answer = System.Windows.MessageBox.Show($"Apagar a sessão \"{row.Name}\"? O arquivo dela na pasta de sessões do NEXUS será removido.",
            "Apagar sessão", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _store.Delete(row.Session);
        Reload();
        Status = $"Sessão \"{row.Name}\" apagada.";
    }

    private void Retag(SessionTag tag)
    {
        if (Selected is not { } row) return;
        row.Session = _store.Retag(row.Session, tag);
        SessionsChanged?.Invoke();
    }
}

/// <summary>Uma métrica comparada, pronta para a tela.</summary>
public sealed class ComparisonRow(MetricComparison m)
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public MetricComparison Model { get; } = m;
    public string Name => Model.Metric.Name;
    public string Explanation => Model.Metric.Explanation;
    public string Unit => Model.Metric.Unit;
    public double Before => Model.Before;
    public double After => Model.After;
    public string BeforeText => Format(Model.Before);
    public string AfterText => Format(Model.After);
    public string DeltaText => double.IsFinite(Model.DeltaPercent) ? Model.DeltaPercent.ToString("+0.0;-0.0;0.0", Pt) + "%" : "—";
    /// <summary>true = melhor, false = pior, null = sem diferença relevante ou sem base.</summary>
    public bool? Better => Model.Better;
    public bool Proven => Model.Verdict is Verdict.Improved or Verdict.Regressed;
    public string Sentence => Model.Sentence;
    public bool HigherIsBetter => Model.Metric.Direction == Direction.HigherIsBetter;
    private string Format(double v) => Model.Metric.Unit == "FPS" ? v.ToString("0", Pt) : v.ToString("0.0", Pt) + " " + Model.Metric.Unit;
}

/// <summary>Comparar: sessões marcadas "antes" contra "depois", com veredito honesto.</summary>
public sealed class ComparePage : PageViewModel
{
    private readonly MeasurePage _measure;
    private SessionComparisonResult? _result;
    private IReadOnlyList<ComparisonRow> _rows = [];

    public ComparePage(MeasurePage measure)
    {
        _measure = measure;
        _measure.SessionsChanged += Recompute;
        Recompute();
    }

    public override string Title => "Comparar";
    public override string Group => "Medir";
    public override string Subtitle => "Antes contra depois, com a resposta que importa: melhorou de verdade ou é variação normal?";

    public SessionComparisonResult? Result { get => _result; private set => Set(ref _result, value); }
    public IReadOnlyList<ComparisonRow> Rows { get => _rows; private set => Set(ref _rows, value); }
    public IReadOnlyList<SessionRow> Before => _measure.Sessions.Where(s => s.IsBefore).ToList();
    public IReadOnlyList<SessionRow> After => _measure.Sessions.Where(s => s.IsAfter).ToList();
    /// <summary>Curvas para sobrepor no gráfico: a primeira sessão de cada lado (mais antiga).</summary>
    public IReadOnlyList<TracePoint> BeforeTrace => Before.OrderBy(s => s.Session.CapturedAt).FirstOrDefault()?.Trace ?? [];
    public IReadOnlyList<TracePoint> AfterTrace => After.OrderBy(s => s.Session.CapturedAt).FirstOrDefault()?.Trace ?? [];
    public bool Ready => Result is { Metrics.Count: > 0 };
    public string Headline => Result?.Headline ?? "";
    public string Method => Result?.Method ?? "";

    public void Recompute()
    {
        Result = SessionComparison.Compare(Before.Select(s => s.Session).ToList(), After.Select(s => s.Session).ToList());
        Rows = Result.Metrics.Select(m => new ComparisonRow(m)).ToList();
        foreach (var p in new[] { nameof(Before), nameof(After), nameof(BeforeTrace), nameof(AfterTrace), nameof(Ready), nameof(Headline), nameof(Method) })
            OnPropertyChanged(p);
    }
}
