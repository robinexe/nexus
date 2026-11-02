using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nexus.Desktop.ViewModels;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.SelfTest;

namespace Nexus.Desktop.SelfTest;

/// <summary>
/// Autoteste da interface no PC real (<c>Nexus.Desktop.exe --selftest-ui relatorio.json</c>): abre a janela, espera a
/// leitura do PC, abre CADA página da navegação dos dois modos (simples, com a boas-vindas e a tela Otimizar de demonstração,
/// e depois completo: <see cref="MainViewModel.NavPages"/>, inclusive as que vierem depois), espera ela
/// ficar ociosa e registra exceções, erros de binding, interface parada, tempo de carga e uma imagem da tela.
/// Nada é aplicado: o <see cref="MainViewModel.SelfTestMode"/> recusa os pedidos ao aplicador e responde "não" às perguntas.
/// Grava só o JSON pedido e as imagens na pasta "telas" ao lado dele.
/// </summary>
internal sealed class UiSelfTestRunner
{
    private static readonly TimeSpan InitialTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(20);
    /// <summary>Propriedades "ocupado" que as páginas usam (Busy da ModulePage e variantes próprias).</summary>
    private static readonly string[] BusyNames = ["Busy", "Scanning", "Loading", "IsLoading", "Inspecting", "Refreshing", "Measuring", "Checking", "Searching", "Reading"];

    private readonly MainWindow _window;
    private readonly string _jsonPath;
    private readonly bool _screenshots;
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private Bucket _bucket = new();
    private long _lastPong = Environment.TickCount64;
    private bool _finished;

    /// <summary>O que acontece enquanto uma etapa (abertura ou uma página) está em andamento.</summary>
    private sealed class Bucket
    {
        public List<string> Exceptions { get; } = [];
        public int BindingErrors { get; set; }
        public List<string> BindingSamples { get; } = [];
        public double LongestFreeze { get; set; }
    }

    public static UiSelfTestRunner? Current { get; private set; }

    private UiSelfTestRunner(MainWindow window, string jsonPath, bool screenshots)
    {
        _window = window;
        _jsonPath = jsonPath;
        _screenshots = screenshots;
    }

    /// <summary>"--selftest-ui ARQUIVO [--no-screenshots]" nos argumentos; null se não for autoteste. "--lang en|es" (lido pelo
    /// construtor de App, ver LanguageOption) fotografa a interface em outro idioma.</summary>
    public static (string Path, bool Screenshots)? Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "--selftest-ui", StringComparison.OrdinalIgnoreCase)) continue;
            var path = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "autoteste", "ui.json");
            return (Path.GetFullPath(path), !args.Contains("--no-screenshots", StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    public static UiSelfTestRunner Start(MainWindow window, string jsonPath, bool screenshots)
    {
        var runner = new UiSelfTestRunner(window, jsonPath, screenshots);
        Current = runner;
        runner.Hook();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _ = runner.RunAsync()));
        return runner;
    }

    // ------------------------------------------------------------------ coleta

    private void Hook()
    {
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener(this));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) RecordException(ex, "não tratada (AppDomain)"); };
        TaskScheduler.UnobservedTaskException += (_, e) => { RecordException(e.Exception, "tarefa não observada"); e.SetObserved(); };
        var timer = new Timer(_ =>
        {
            var gap = (Environment.TickCount64 - Interlocked.Read(ref _lastPong)) / 1000.0;
            lock (_gate) { if (gap > _bucket.LongestFreeze) _bucket.LongestFreeze = gap; }
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Interlocked.Exchange(ref _lastPong, Environment.TickCount64)));
        }, null, 250, 250);
        _timer = timer;
    }

    private Timer? _timer;

    /// <summary>Chamado por App.OnUnhandled no modo autoteste (sem janela de erro).</summary>
    public void RecordException(Exception ex, string where = "não tratada (interface)")
    {
        var text = Sanitizer.Clip(Sanitizer.Clean($"[{where}] {ex}"), 6000);
        lock (_gate) { if (_bucket.Exceptions.Count < 10) _bucket.Exceptions.Add(text); }
    }

    private void RecordBinding(string message)
    {
        lock (_gate)
        {
            _bucket.BindingErrors++;
            if (_bucket.BindingSamples.Count < 8) _bucket.BindingSamples.Add(Sanitizer.Clip(Sanitizer.Clean(message), 600));
        }
    }

    private Bucket Swap()
    {
        lock (_gate)
        {
            var old = _bucket;
            _bucket = new Bucket();
            Interlocked.Exchange(ref _lastPong, Environment.TickCount64);
            return old;
        }
    }

    private sealed class BindingListener(UiSelfTestRunner owner) : TraceListener
    {
        private readonly StringBuilder _pending = new();

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (eventType <= TraceEventType.Warning) owner.RecordBinding(message ?? "");
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (eventType > TraceEventType.Warning) return;
            string text;
            try { text = args is { Length: > 0 } && format is not null ? string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args) : format ?? ""; }
            catch (FormatException) { text = format ?? ""; }
            owner.RecordBinding(text);
        }

        // Caminho alternativo (alguns emissores escrevem direto): uma linha = um erro.
        public override void Write(string? message) { lock (_pending) _pending.Append(message); }

        public override void WriteLine(string? message)
        {
            string line;
            lock (_pending) { _pending.Append(message); line = _pending.ToString(); _pending.Clear(); }
            if (line.Length > 0) owner.RecordBinding(line);
        }
    }

    // ------------------------------------------------------------------ execução

    private async Task RunAsync()
    {
        var watchdog = new CancellationTokenSource(TotalTimeout);
        var pages = new List<UiPageResult>();
        string? abort = null;
        double initialSeconds = 0;
        var initialDone = false;
        string? initialStatus = null;
        Bucket? startup = null;
        var vm = (MainViewModel)_window.DataContext;
        try
        {
            // 1) A leitura do PC (a janela começa sozinha no Loaded).
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < InitialTimeout && (vm.Facts is null || vm.Busy)) await Task.Delay(250, CancellationToken.None);
            initialDone = vm.Facts is not null && !vm.Busy;
            initialSeconds = sw.Elapsed.TotalSeconds;
            initialStatus = Sanitizer.Clean(vm.Status);
            await IdleAsync();
            startup = Swap();

            // 2) Modo simples: as telas da navegação dele, as 3 telas da boas-vindas e a tela Otimizar trabalhando e no fim
            //    (dados de demonstração). Depois o modo completo: cada página da navegação dele (qualquer página nova entra sozinha).
            var index = 0;
            vm.SetMode(UiMode.Simple, persist: false);
            await IdleAsync();
            foreach (var page in vm.NavPages.ToList())
            {
                if (watchdog.IsCancellationRequested) { abort = $"tempo total de {TotalTimeout.TotalMinutes:0} min excedido"; break; }
                pages.Add(await VisitAsync(vm, page, ++index, Loc.F("Simple.SelfTest.Label", page.Title)));
            }
            for (var step = 0; step < WelcomeFlow.Steps && !watchdog.IsCancellationRequested; step++)
            {
                var s = step;
                pages.Add(await VisitAsync(vm, vm.Home, ++index, Loc.F("Simple.SelfTest.Welcome", s + 1, WelcomeFlow.Steps),
                    () => vm.Welcome.Show(s), () => vm.Welcome.IsVisible = false));
            }
            if (!watchdog.IsCancellationRequested)
                pages.Add(await VisitAsync(vm, vm.SimpleOptimize, ++index, Loc.T("Simple.SelfTest.OptimizeRunning"), vm.Assistant.ShowAutoDemo, vm.Assistant.HideAutoDemo));
            if (!watchdog.IsCancellationRequested)
                pages.Add(await VisitAsync(vm, vm.SimpleOptimize, ++index, Loc.T("Simple.SelfTest.OptimizeDone"), vm.Assistant.ShowAutoDoneDemo, vm.Assistant.HideAutoDemo));

            vm.SetMode(UiMode.Full, persist: false);
            await IdleAsync();
            foreach (var page in vm.NavPages.ToList())
            {
                if (watchdog.IsCancellationRequested) { abort = $"tempo total de {TotalTimeout.TotalMinutes:0} min excedido"; break; }
                index++;
                pages.Add(await VisitAsync(vm, page, index));
            }

            // 3) Telas que só aparecem durante uma ação: a tela de progresso do Assistente, com dados de demonstração.
            if (!watchdog.IsCancellationRequested)
                pages.Add(await VisitAsync(vm, vm.Assistant, ++index, "Assistente completo (progresso, demonstração)",
                    vm.Assistant.ShowProcessDemo, vm.Assistant.HideProcessDemo));
            // 4) A tela de progresso da janela (qualquer aplicar/desfazer fora do Assistente), sobre a página Aplicar.
            if (!watchdog.IsCancellationRequested)
                pages.Add(await VisitAsync(vm, vm.Broker, ++index, "Aplicar (progresso, demonstração)", vm.ShowApplyDemo, vm.HideApplyDemo));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            abort = Sanitizer.Clip(Sanitizer.Clean($"o próprio autoteste falhou: {ex}"), 3000);
            startup ??= Swap();
        }

        var report = new UiSelfTestReport
        {
            StartedAt = _started,
            Seconds = Math.Round(_clock.Elapsed.TotalSeconds, 1),
            Environment = SelfTestEnvironment.Current(),
            InitialLoadSeconds = Math.Round(initialSeconds, 1),
            InitialLoadCompleted = initialDone,
            InitialStatus = initialStatus,
            StartupExceptions = startup?.Exceptions ?? [],
            StartupBindingErrors = startup?.BindingErrors ?? 0,
            RefusedWrites = vm.SelfTestRefusals,
            Aborted = abort is not null,
            AbortReason = abort,
            Pages = pages,
        };
        Finish(report);
    }

    /// <param name="label">Nome da tela no relatório (padrão: o título da página).</param>
    /// <param name="stage">Monta um estado especial da página depois de ela ficar ociosa (ex.: a tela de progresso de demonstração).</param>
    /// <param name="cleanup">Desfaz o estado especial depois da imagem.</param>
    private async Task<UiPageResult> VisitAsync(MainViewModel vm, PageViewModel page, int index, string? label = null, Action? stage = null, Action? cleanup = null)
    {
        Swap();
        var notes = new List<string>();
        var sw = Stopwatch.StartNew();
        Task? shown;
        if (ReferenceEquals(vm.SelectedPage, page))
            shown = page is ModulePage m ? m.OnShownAsync() : null; // já aberta (a primeira): dispara de novo, como ao clicar
        else
        {
            vm.SelectedPage = page; // o setter dispara OnShownAsync
            shown = vm.LastShownTask;
        }

        var idle = false;
        var deadline = sw.Elapsed + PageTimeout;
        if (shown is not null)
        {
            var done = await Task.WhenAny(shown, Task.Delay(PageTimeout));
            if (done == shown && shown.IsFaulted && shown.Exception is { } agg)
                foreach (var e in agg.InnerExceptions) RecordException(e, "OnShownAsync");
            if (done != shown) notes.Add("OnShownAsync não terminou no tempo limite.");
        }
        while (sw.Elapsed < deadline)
        {
            if (!vm.Busy && !IsBusy(page)) { idle = true; break; }
            await Task.Delay(200);
        }
        if (!idle) notes.Add($"Continuava ocupada depois de {PageTimeout.TotalSeconds:0} s.");
        if (stage is not null)
        {
            try { stage(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { RecordException(ex, "estado de demonstração"); }
            await IdleAsync();
            await Task.Delay(1200); // anel, números e revelação chegam ao valor
        }
        await IdleAsync();
        _window.UpdateLayout();
        await Task.Delay(450); // fim da animação de entrada da página
        await IdleAsync();
        var load = sw.Elapsed.TotalSeconds;

        var hasTemplate = HasTemplate(page);
        string? shot = null;
        if (_screenshots)
        {
            try { shot = Capture(index, label ?? page.Title); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            { notes.Add("Imagem da tela não gravada: " + Sanitizer.Clean(ex.Message)); }
        }
        if (cleanup is not null)
        {
            try { cleanup(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { RecordException(ex, "estado de demonstração"); }
        }
        var b = Swap();
        return new UiPageResult
        {
            Index = index,
            Title = label ?? page.Title,
            Group = page.Group,
            Type = page.GetType().Name,
            LoadSeconds = Math.Round(load, 2),
            ReachedIdle = idle,
            LongestFreezeSeconds = Math.Round(b.LongestFreeze, 1),
            HasTemplate = hasTemplate,
            Exceptions = b.Exceptions,
            BindingErrors = b.BindingErrors,
            BindingSamples = b.BindingSamples,
            Message = page is ModulePage mp && mp.Message.Length > 0 ? Sanitizer.Clip(Sanitizer.Clean(mp.Message), 300) : null,
            AppStatus = Sanitizer.Clip(Sanitizer.Clean(vm.Status), 300),
            Screenshot = shot,
            Notes = notes,
            Status = UiSelfTestReport.Classify(b.Exceptions.Count, hasTemplate, b.LongestFreeze, b.BindingErrors, idle),
        };
    }

    private Task IdleAsync() => _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private static bool IsBusy(PageViewModel page)
    {
        foreach (var name in BusyNames)
        {
            var p = page.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p?.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0 && p.GetValue(page) is true) return true;
        }
        return false;
    }

    /// <summary>Sem DataTemplate para o tipo, o ContentControl mostraria o ToString da página num TextBlock.</summary>
    private bool HasTemplate(PageViewModel page)
    {
        if (_window.FindName("PageHost") is not ContentControl host) return true;
        if (VisualTreeHelper.GetChildrenCount(host) == 0) return false;
        var presenter = VisualTreeHelper.GetChild(host, 0);
        if (presenter is ContentPresenter cp && VisualTreeHelper.GetChildrenCount(cp) == 1 && VisualTreeHelper.GetChild(cp, 0) is TextBlock tb)
            return !string.Equals(tb.Text, page.ToString(), StringComparison.Ordinal);
        return true;
    }

    private string Capture(int index, string title)
    {
        if (_window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1) throw new InvalidOperationException("janela sem conteúdo visível");
        var dpi = VisualTreeHelper.GetDpi(_window);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // O conteúdo é transparente sobre o fundo da janela; Render(root) não inclui Window.Background → pintamos o fundo antes.
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(_window.Background ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(backdrop);
        bmp.Render(root);
        var folder = Path.Combine(Path.GetDirectoryName(_jsonPath)!, "telas");
        Directory.CreateDirectory(folder);
        var name = $"{index:00}-{Slug(title)}.png";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(Path.Combine(folder, name))) encoder.Save(fs);
        return "telas/" + name;
    }

    internal static string Slug(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "pagina" : s.Length > 40 ? s[..40].TrimEnd('-') : s;
    }

    private void Finish(UiSelfTestReport report)
    {
        if (_finished) return;
        _finished = true;
        _timer?.Dispose();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_jsonPath)!);
            File.WriteAllText(_jsonPath, report.ToJson(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"autoteste: não foi possível gravar {_jsonPath}: {ex.Message}");
        }
        var code = report.ExitCode;
        // Fecha de vez (sem bandeja). Se algo segurar o encerramento, o processo sai mesmo assim.
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Environment.Exit(code), TaskScheduler.Default);
        Application.Current.Shutdown(code);
    }
}
