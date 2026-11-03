using System.Globalization;
using Nexus.Experimentation;
using Nexus.Measurement;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Uma sessão na lista, com os números já formatados para a tela.</summary>
public sealed class SessionRow : ObservableObject
{
    private static CultureInfo Pt => Loc.Culture; // idioma da interface (docs/12-idiomas.md)
    private MeasuredSession _session;

    public SessionRow(MeasuredSession session) => _session = session;

    public MeasuredSession Session
    {
        get => _session;
        set
        {
            if (!Set(ref _session, value)) return;
            foreach (var p in new[] { nameof(Name), nameof(Tag), nameof(TagText), nameof(IsBefore), nameof(IsAfter), nameof(Excluded), nameof(ExcludedText) }) OnPropertyChanged(p);
        }
    }

    public string Name => Session.Name;
    public string Game => Session.DisplayName;
    public string Application => Session.Application;
    public string When => Session.CapturedAt.ToLocalTime().ToString("dd/MM HH:mm", Pt);
    public string Source => Session.Source;
    public SessionTag Tag => Session.Tag;
    public bool IsBefore => Session.Tag == SessionTag.Before;
    public bool IsAfter => Session.Tag == SessionTag.After;
    public string TagText => Session.Tag switch { SessionTag.Before => Loc.T("Shell.Antes"), SessionTag.After => Loc.T("Shell.Depois"), _ => "" };
    public bool Excluded => Session.Excluded;
    public string ExcludedText => Session.Excluded ? Loc.T("Meas.ForaComparacoes") : "";
    public bool Automatic => Session.Automatic;
    public double AverageFps => Session.Summary.AverageFps;
    public double Low1Fps => Session.Summary.OnePercentLowAverageFps;
    public double P99Ms => Session.Summary.P99FrametimeMs;
    public double StuttersPerMinute => Session.Summary.StuttersPerMinute;
    public string AverageFpsText => Session.Summary.AverageFps.ToString("0", Pt);
    public string Low1Text => Session.Summary.OnePercentLowAverageFps.ToString("0", Pt);
    public string P99Text => Session.Summary.P99FrametimeMs.ToString("0.0", Pt) + " ms";
    public string StutterText => Session.Summary.StuttersPerMinute.ToString("0.0", Pt) + "/min";
    public string DurationText => TimeSpan.FromMilliseconds(Session.Summary.DurationMs).ToString(Session.Summary.DurationMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss", Pt);
    public string FramesText => Session.Summary.Frames.ToString("N0", Pt) + " quadros";
    // "Por que travou?" (null em partidas gravadas antes desta função)
    public bool HasStutters => Session.Stutters is not null;
    public string StutterVerdict => StutterWords.Verdict(Session.Stutters);
    public Tone StutterTone => StutterWords.ToneOf(Session.Stutters);
    public IReadOnlyList<StutterCauseRow> StutterCauses => StutterWords.Rows(Session.Stutters);
    public IReadOnlyList<string> StutterWorst => StutterWords.Worst(Session.Stutters);
    public bool HasStutterWorst => Session.Stutters is { Worst.Count: > 0 };
    public string StutterNotes => StutterWords.Notes(Session.Stutters);
    public IReadOnlyList<TracePoint> Trace => Session.Trace;
    public IReadOnlyList<HistogramBin> Histogram => Session.Histogram;

    /// <summary>"1920x1080 · na tomada · tela cheia otimizada" (o que foi lido).</summary>
    public string ConditionsText
    {
        get
        {
            var c = Session.Conditions;
            if (c is null) return "";
            var parts = new List<string>();
            if (c.Resolution is { } r) parts.Add(r);
            if (c.OnBattery is { } b) parts.Add(b ? Loc.T("Gpu.Bateria") : Loc.T("Meas.Tomada"));
            if (c.PresentMode is { } m) parts.Add(AutoCompare.ModeText(m));
            if (c.GpuDriver is { } d) parts.Add("driver " + d);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Mudanças do NEXUS ativas quando a partida foi gravada.</summary>
    public string AppliedText
    {
        get
        {
            var active = Session.Applied.Where(a => a.Active).ToList();
            return active.Count == 0 ? Loc.T("Meas.NenhumaMudancaNexusAtiva") :
                Loc.F("Meas.AtivasNestaPartidaN", string.Join(", ", active.Take(6).Select(a => a.Title)), (active.Count > 6 ? Loc.F("Meas.MaisN", active.Count - 6) : ""));
        }
    }
}

/// <summary>Uma métrica comparada, pronta para a tela.</summary>
public sealed class ComparisonRow(MetricComparison m)
{
    private static CultureInfo Pt => Loc.Culture; // idioma da interface (docs/12-idiomas.md)
    public MetricComparison Model { get; } = m;
    public string Name => Model.Metric.Name;
    public string Explanation => Model.Metric.Explanation;
    public string Unit => Model.Metric.Unit;
    public double Before => Model.Before;
    public double After => Model.After;
    public string BeforeText => Format(Model.Before);
    public string AfterText => Format(Model.After);
    public string DeltaText => double.IsFinite(Model.DeltaPercent) ? Model.DeltaPercent.ToString("+0.0;-0.0;0.0", Pt) + "%" + AutoCompare.PlusMinus(Model.CiLowPercent, Model.CiHighPercent) : "—";
    /// <summary>true = melhor, false = pior, null = sem diferença relevante ou sem base.</summary>
    public bool? Better => Model.Better;
    public bool Proven => Model.Verdict is Verdict.Improved or Verdict.Regressed;
    public string Sentence => Model.Sentence;
    /// <summary>Rótulos das barras do antes/depois com a unidade ("Antes · ms").</summary>
    public string BeforeLabel => Model.Metric.Unit == "FPS" ? Loc.T("Shell.Antes") : Loc.F("Meas.AntesN", Model.Metric.Unit.TrimStart('/'));
    public string AfterLabel => Model.Metric.Unit == "FPS" ? Loc.T("Shell.Depois") : Loc.F("Meas.DepoisN", Model.Metric.Unit.TrimStart('/'));
    /// <summary>Selo honesto: só "Melhorou"/"Piorou" quando o teste comprova; o resto fica neutro.</summary>
    public string VerdictText => Model.Verdict switch
    {
        Verdict.Improved => Loc.T("Meas.Melhorou"),
        Verdict.Regressed => Loc.T("Meas.Piorou"),
        Verdict.NoMeaningfulChange => Loc.T("Meas.IgualPratica"),
        Verdict.Inconclusive => Loc.T("Meas.DentroVariacao"),
        _ => Model.Better switch { true => Loc.T("Meas.FavorDepoisSemProva"), false => Loc.T("Meas.FavorAntesSemProva"), _ => Loc.T("Meas.DiferencaPequena") },
    };
    public Tone VerdictTone => Model.Verdict switch
    {
        Verdict.Improved => Tone.Good,
        Verdict.Regressed => Tone.Bad,
        _ => Model.Better == false ? Tone.Warn : Tone.Info,
    };
    public bool HigherIsBetter => Model.Metric.Direction == Direction.HigherIsBetter;
    /// <summary>Largura relativa das barras (0..1): o maior dos dois lados ocupa a barra inteira.</summary>
    public double BeforeRatio => Ratio(Model.Before);
    public double AfterRatio => Ratio(Model.After);
    private string Format(double v) => !double.IsFinite(v) ? "—" : Model.Metric.Unit == "FPS" ? v.ToString("0", Pt) : v.ToString("0.0", Pt) + " " + Model.Metric.Unit;

    private double Ratio(double v)
    {
        var max = Math.Max(Math.Abs(Model.Before), Math.Abs(Model.After));
        return double.IsFinite(v) && double.IsFinite(max) && max > 0 ? Math.Clamp(Math.Abs(v) / max, 0, 1) : 0;
    }
}

/// <summary>Um jogo na lista, com a tendência das sessões.</summary>
public sealed class GameRow(GameHistory h)
{
    private static CultureInfo Pt => Loc.Culture; // idioma da interface (docs/12-idiomas.md)
    public GameHistory Model { get; } = h;
    public string Key => Model.Key;
    public string Name => Model.Name;
    public string SessionsText => Model.Sessions == 1 ? Loc.T("Meas.N1Partida") : Loc.F("Meas.NPartidas", Model.Sessions);
    public string LastPlayedText => Loc.T("Meas.Ultima") + Model.LastPlayed.ToLocalTime().ToString("dd/MM HH:mm", Pt);
    public IReadOnlyList<double> FpsTrend => Model.AverageFpsTrend;
    public IReadOnlyList<double> Low1Trend => Model.Low1Trend;
    public string LastFpsText => Model.LastAverageFps.ToString("0", Pt);
    public double LastFps => Model.LastAverageFps;
    /// <summary>Tendência do 1% low: verde subindo, âmbar caindo (tendência, não prova), neutro com uma partida só.</summary>
    public Tone TrendTone => Model.Low1ChangePercent switch { >= 3 => Tone.Good, <= -3 => Tone.Warn, null => Tone.Neutral, _ => Tone.Info };
    public string LastLow1Text => Model.LastLow1Fps.ToString("0", Pt);
    /// <summary>"1% low +8% na tendência" (descritivo: tendência, não prova).</summary>
    public string TrendText => Model.Low1ChangePercent is { } c ? Loc.F("Meas.N1LowNTendencia", c.ToString("+0;-0;0", Pt)) : Loc.T("Meas.PartidaSo");
}

/// <summary>Comando com parâmetro de texto (ex.: remover um item da lista de jogos pelo nome do executável).</summary>
public sealed class TextCommand(Action<string> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is string { Length: > 0 };
    public void Execute(object? parameter) { if (parameter is string { Length: > 0 } s) execute(s); }
}
