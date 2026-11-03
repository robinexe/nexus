using System.Globalization;
using Nexus.Measurement;
using Nexus.Measurement.Windows;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Um culpado típico de atraso de drivers e o que fazer, em palavras simples.</summary>
public sealed record DriverAdviceRow(string Title, string Advice);

/// <summary>
/// "Atraso de drivers" (estilo LatencyMon, aproximado): 60–180 s lendo os contadores documentados de DPC/interrupção por núcleo
/// (nível de usuário, sem administrador, sem mudar nada — nem a resolução do timer). Mostra o pior momento, a média, a duração
/// média de cada chamada e um veredito (bom / pode causar engasgos / ruim), com conselhos para os culpados mais comuns.
/// Não diz QUAL driver: isso exige rastreamento do kernel (administrador), que o NEXUS não faz; a tela explica.
/// </summary>
public sealed class DriverLatencyPanel : ObservableObject
{
    private static CultureInfo Pt => Loc.Culture;
    private readonly Func<IDpcCounterSource?> _factory;
    private int _seconds = 60;
    private bool _running;
    private double _progress;
    private string _status = "";
    private DriverLatencyResult? _result;
    private CancellationTokenSource? _cancel;

    public DriverLatencyPanel(Func<IDpcCounterSource?>? factory = null)
    {
        _factory = factory ?? DefaultSource;
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !_running);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => _running);
    }

    private static WindowsDpcCounters? DefaultSource() => OperatingSystem.IsWindows() ? new WindowsDpcCounters() : null;

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

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

    public bool IsIdle => !_running;
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressText)); } }
    public string ProgressText => Loc.F("Meas.NSNS", Math.Clamp(_progress, 0, 1) * _seconds, _seconds);
    public string Status { get => _status; private set => Set(ref _status, value); }

    public DriverLatencyResult? Result
    {
        get => _result;
        private set
        {
            if (!Set(ref _result, value)) return;
            foreach (var p in new[] { nameof(HasResult), nameof(VerdictTitle), nameof(VerdictDetail), nameof(VerdictTone), nameof(PeakText),
                         nameof(AverageText), nameof(CallText), nameof(CoreLines), nameof(Advice), nameof(HasAdvice) }) OnPropertyChanged(p);
        }
    }

    public bool HasResult => _result is { Level: not DriverLatencyLevel.Unknown };

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

    public bool HasAdvice => _result is { Level: DriverLatencyLevel.MayStutter or DriverLatencyLevel.Bad };

    /// <summary>Culpados mais comuns (o teste não aponta qual; ver Dpc.Approx) e o que fazer.</summary>
    public IReadOnlyList<DriverAdviceRow> Advice => !HasAdvice ? [] :
    [
        new(Loc.T("Dpc.Culprit.Wifi"), Loc.T("Dpc.Advice.Wifi")),
        new(Loc.T("Dpc.Culprit.Audio"), Loc.T("Dpc.Advice.Audio")),
        new(Loc.T("Dpc.Culprit.Network"), Loc.T("Dpc.Advice.Network")),
        new(Loc.T("Dpc.Culprit.Video"), Loc.T("Dpc.Advice.Video")),
    ];

    private static string Us(double? v) => v is { } d ? d.ToString("0", Pt) + " µs" : "—";

    /// <summary>"0,3" (grupo, núcleo) vira "núcleo 3"; outro formato fica como veio.</summary>
    internal static string CoreName(string instance)
    {
        var comma = instance.LastIndexOf(',');
        var n = comma >= 0 ? instance[(comma + 1)..] : instance;
        return int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? Loc.F("Dpc.Core", i) : instance;
    }

    private async Task RunAsync()
    {
        if (_running) return;
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
}
