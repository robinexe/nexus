using System.Globalization;
using Nexus.Broker;
using Nexus.DriverUpdates;
using Nexus.HardwareDiscovery;
using Nexus.Measurement;
using Nexus.Measurement.Windows;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// "Atraso de drivers": (1) 60–180 s lendo os contadores documentados de DPC/interrupção por núcleo (nível de usuário, sem
/// administrador, sem mudar nada — nem a resolução do timer): pior momento, média, duração média de cada chamada e um veredito
/// (bom / pode causar engasgos / ruim). (2) "Investigar como administrador (10 s)": ação efêmera do catálogo
/// <c>measure.driver-latency.trace@1</c> pelo aplicador (rastreamento do kernel, como o LatencyMon), cujo resumo volta pelo arquivo
/// <see cref="DriverTraceFile"/> no perfil: provável culpado (driver .sys + dispositivo pelo inventário de drivers), top 5 e o que
/// fazer por categoria. Sem administrador a tela diz com honestidade que só o total é conhecido. Frases em <see cref="DriverTraceText"/>.
/// </summary>
public sealed class DriverLatencyPanel : ObservableObject
{
    public const string TraceActionId = DriverTraceText.TraceActionId;
    private static CultureInfo Pt => Loc.Culture;
    private readonly Func<IDpcCounterSource?> _factory;
    private readonly Func<IModuleHost?> _host;
    private readonly Func<DriverTraceResult?> _loadTrace;
    private readonly Func<DriverModuleLookup?> _lookupFactory;
    private DriverModuleLookup? _lookup;
    private int _seconds = 60;
    private bool _running, _tracing;
    private double _progress;
    private string _status = "", _traceStatus = "";
    private DriverLatencyResult? _result;
    private DriverTraceResult? _trace;
    private CancellationTokenSource? _cancel;

    public DriverLatencyPanel(Func<IDpcCounterSource?>? factory = null, Func<IModuleHost?>? host = null, Func<DriverTraceResult?>? loadTrace = null,
        Func<DriverModuleLookup?>? lookupFactory = null)
    {
        _factory = factory ?? DefaultSource;
        _host = host ?? (() => null);
        _loadTrace = loadTrace ?? DefaultLoadTrace;
        _lookupFactory = lookupFactory ?? DefaultLookup;
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !_running && !_tracing);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => _running);
        InvestigateCommand = new RelayCommand(() => _ = InvestigateAsync(), () => !_running && !_tracing && _host() is not null);
    }

    private static WindowsDpcCounters? DefaultSource() => OperatingSystem.IsWindows() ? new WindowsDpcCounters() : null;

    private static DriverTraceResult? DefaultLoadTrace() =>
        DriverTraceFile.Load(DriverTraceFile.PathFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    private static DriverModuleLookup? DefaultLookup() => OperatingSystem.IsWindows() ? WindowsDriverModuleSource.Build() : null;

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand InvestigateCommand { get; }

    /// <summary>Resumo para o Diagnóstico/Problemas mudou (novo veredito ou nova investigação).</summary>
    public event EventHandler<DriverLatencyFacts>? FactsChanged;

    /// <summary>Duração escolhida: 60, 120 ou 180 s.</summary>
    public int Seconds
    {
        get => _seconds;
        set { if (value is 60 or 120 or 180 && Set(ref _seconds, value)) OnPropertyChanged(nameof(ProgressText)); }
    }

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (!Set(ref _running, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>A investigação como administrador está em andamento (aviso do Windows + 10 s de rastreamento).</summary>
    public bool IsTracing
    {
        get => _tracing;
        private set
        {
            if (!Set(ref _tracing, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIdle => !_running && !_tracing;
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressText)); } }
    public string ProgressText => Loc.F("Meas.NSNS", Math.Clamp(_progress, 0, 1) * _seconds, _seconds);
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string TraceStatus { get => _traceStatus; private set => Set(ref _traceStatus, value); }

    public DriverLatencyResult? Result
    {
        get => _result;
        private set
        {
            if (!Set(ref _result, value)) return;
            foreach (var p in new[] { nameof(HasResult), nameof(VerdictTitle), nameof(VerdictDetail), nameof(VerdictTone), nameof(PeakText),
                         nameof(AverageText), nameof(CallText), nameof(CoreLines), nameof(Advice), nameof(HasAdvice), nameof(ShowNoAdmin), nameof(CulpritsHeader) }) OnPropertyChanged(p);
        }
    }

    /// <summary>Último resumo da investigação como administrador (arquivo no perfil), ou null.</summary>
    public DriverTraceResult? Trace
    {
        get => _trace;
        private set
        {
            if (!Set(ref _trace, value)) return;
            foreach (var p in new[] { nameof(HasTrace), nameof(HasCulprit), nameof(CulpritText), nameof(NoCulpritText), nameof(TopRows), nameof(TraceNote),
                         nameof(ShowNoAdmin), nameof(Advice), nameof(HasAdvice), nameof(CulpritsHeader), nameof(HasKernelInTop) }) OnPropertyChanged(p);
        }
    }

    public bool HasResult => _result is { Level: not DriverLatencyLevel.Unknown };
    public bool HasTrace => _trace is not null;
    public bool HasCulprit => _trace?.Culprit is not null;

    /// <summary>Com resultado dos contadores e sem investigação: frase honesta de que o total não diz qual driver.</summary>
    public bool ShowNoAdmin => HasResult && !HasTrace;

    public string VerdictTitle => _result?.Level switch
    {
        DriverLatencyLevel.Good => Loc.T("Dpc.Verdict.Good"),
        DriverLatencyLevel.MayStutter => Loc.T("Dpc.Verdict.May"),
        DriverLatencyLevel.Bad => Loc.T("Dpc.Verdict.Bad"),
        _ => "",
    };

    public string VerdictDetail => _result?.Level switch
    {
        DriverLatencyLevel.Good => Loc.T("Dpc.Detail.Good"),
        DriverLatencyLevel.MayStutter => Loc.T("Dpc.Detail.May"),
        DriverLatencyLevel.Bad => Loc.T("Dpc.Detail.Bad"),
        _ => "",
    };

    public Tone VerdictTone => _result?.Level switch
    {
        DriverLatencyLevel.Good => Tone.Good,
        DriverLatencyLevel.MayStutter => Tone.Warn,
        DriverLatencyLevel.Bad => Tone.Bad,
        _ => Tone.Neutral,
    };

    /// <summary>"Pior momento: núcleo 0,3 com 23% do tempo em drivers, aos 41 s".</summary>
    public string PeakText => _result is { PeakCore: { } core } r ? Loc.F("Dpc.Peak", CoreName(core), r.PeakPercent, r.PeakAtSeconds) : "";

    public string AverageText => _result is { } r ? Loc.F("Dpc.Average", r.AverageDpcPercent, r.AverageInterruptPercent, r.SecondsAboveWarn, r.Samples) : "";

    /// <summary>Duração média de cada chamada (DPC/ISR), quando deu para calcular.</summary>
    public string CallText => _result is { } r && (r.WorstAverageDpcMicroseconds is not null || r.WorstAverageIsrMicroseconds is not null)
        ? Loc.F("Dpc.CallAverage", Us(r.WorstAverageDpcMicroseconds), Us(r.WorstAverageIsrMicroseconds))
        : "";

    public IReadOnlyList<string> CoreLines => _result is null ? [] :
        _result.Cores.Take(4).Select(c => Loc.F("Dpc.CoreLine", CoreName(c.Core), c.PeakPercent, c.AveragePercent)).ToList();

    // ------------------------------------------------------------------ investigação (rastreamento do kernel)

    public string CulpritText => DriverTraceText.Culprit(_trace, Describe);
    public string NoCulpritText => DriverTraceText.NoCulprit(_trace);
    public IReadOnlyList<DriverTraceRow> TopRows => DriverTraceText.Top(_trace, Describe);
    public bool HasKernelInTop => _trace?.Top5.Any(m => m.IsKernelCore) == true;
    public string TraceNote => DriverTraceText.Note(_trace, _host()?.Facts?.IsVirtualMachine == true);

    public bool HasAdvice => _result is { Level: DriverLatencyLevel.MayStutter or DriverLatencyLevel.Bad } || HasCulprit;
    public string CulpritsHeader => HasCulprit ? Loc.T("Dpc.CulpritsTitleTraced") : Loc.T("Dpc.CulpritsTitle");
    public IReadOnlyList<DriverAdviceRow> Advice => DriverTraceText.Advice(_result, _trace, Describe);

    private DriverModuleInfo? Describe(string module)
    {
        try
        {
            _lookup ??= _lookupFactory();
            return _lookup?.Describe(module);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static string Us(double? v) => v is { } d ? d.ToString("0", Pt) + " µs" : "—";

    /// <summary>"0,3" (grupo, núcleo) vira "núcleo 3"; outro formato fica como veio.</summary>
    internal static string CoreName(string instance)
    {
        var comma = instance.LastIndexOf(',');
        var n = comma >= 0 ? instance[(comma + 1)..] : instance;
        return int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? Loc.F("Dpc.Core", i) : instance;
    }

    /// <summary>Resumo para o Diagnóstico (null enquanto não há veredito).</summary>
    public DriverLatencyFacts? Facts() => DriverTraceText.Facts(_result, _trace, Describe);

    private void RaiseFacts()
    {
        if (Facts() is { } f) FactsChanged?.Invoke(this, f);
    }

    private async Task RunAsync()
    {
        if (_running || _tracing) return;
        IDpcCounterSource? source;
        try { source = _factory(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.F("Dpc.Failed", ex.Message); return; }
        if (source is null) { Status = Loc.T("Dpc.WindowsOnly"); return; }
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsRunning = true;
        Progress = 0;
        Result = null;
        Status = Loc.T("Dpc.Running");
        try
        {
            if (source.Unavailable.Count > 0 && source.Read() is null)
            {
                Status = Loc.F("Dpc.Unavailable", string.Join("; ", source.Unavailable));
                return;
            }
            var progress = new Progress<double>(p => Progress = p);
            var (result, _, cancelled) = await DriverLatencyTest.RunAsync(source, TimeSpan.FromSeconds(_seconds), progress, cancel: cancel.Token);
            Result = result;
            Status = result.Level == DriverLatencyLevel.Unknown ? Loc.T("Dpc.NoData")
                : cancelled ? Loc.F("Dpc.Cancelled", result.Samples) : "";
            RaiseFacts();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = Loc.F("Dpc.Failed", ex.Message);
        }
        finally
        {
            source.Dispose();
            _cancel = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// "Investigar como administrador (10 s)": a ação efêmera do catálogo pelo aplicador (confirmação + aviso do Windows), depois
    /// o resumo que o aplicador gravou no perfil. Nada é escrito pela interface.
    /// </summary>
    public async Task InvestigateAsync()
    {
        if (_running || _tracing) return;
        var host = _host();
        if (host is null) return;
        IsTracing = true;
        TraceStatus = Loc.T("Dpc.Investigating");
        var started = DateTimeOffset.Now;
        try
        {
            var reply = await host.ApplyAsync(Loc.T("Dpc.Trace.Title"), Loc.T("Dpc.Trace.Confirm"), [new ActionRequest(TraceActionId)], BrokerLevel.Admin);
            var load = _loadTrace;
            var (trace, status) = await Task.Run(() => DriverTraceText.Outcome(reply, load, started));
            TraceStatus = status;
            if (trace is null) return;
            _lookup = null; // inventário fresco para este resultado
            Trace = trace;
            RaiseFacts();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceStatus = Loc.F("Dpc.Trace.Failed", ex.Message);
        }
        finally
        {
            IsTracing = false;
        }
    }
}
