using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.Boot.Inventory;
using Nexus.Boot.Windows;
using Nexus.Broker;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Memory;
using Nexus.Lab.Memory.Problems;
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>Barra do quadro "Memória agora".</summary>
public sealed record DoctorBar(string Label, string Value, double Fraction, string Tone, string Detail);

/// <summary>Uma linha da lista "Maiores consumidores". <paramref name="History"/> = memória privada nas leituras (sparkline).</summary>
public sealed record DoctorConsumerRow(string Name, string Private, string WorkingSet, string Processes, string Trend, string TrendTone, string Kind,
    IReadOnlyList<double>? History = null)
{
    public UiTone TrendToneKind => TrendTone switch { "bad" => UiTone.Bad, "good" => UiTone.Good, _ => UiTone.Info };
    public string IconKey =>
        Kind == Loc.T("MemDoc.Navegador") ? "Icon.Browser"
        : Kind == Loc.T("MemDoc.Servico") ? "Icon.Services"
        : Kind == Loc.T("MemDoc.MaquinaVirtual") ? "Icon.Pc"
        : Kind == "Windows" ? "Icon.Windows"
        : "Icon.Apps";
}

/// <summary>Evento da linha do tempo.</summary>
public sealed record DoctorEvent(string When, string Text, string Tone);

/// <summary>Uma solução (botão) de um suspeito.</summary>
public sealed class DoctorFixRow : ObservableObject
{
    private string _status = "";
    private Guid? _runId;

    public required FixOption Fix { get; init; }
    public string Title => Fix.Title;
    public string WhatItDoes => Fix.WhatItDoes;
    public string HowLong => Loc.T("MemDoc.QuantoDura") + Fix.HowLong;
    public string Cost => Loc.T("MemDoc.Custo") + Fix.Cost;
    public bool IsTemporary => Fix.Term == FixTerm.Temporary;
    public bool HasButton => Fix.Kind != FixKind.Info;
    public string ButtonText => Fix.Kind switch
    {
        FixKind.Navigate => Loc.T("Shell.Abrir"),
        FixKind.DisableStartup => Loc.T("MemDoc.DesligarInicializacao"),
        _ => Fix.Term == FixTerm.Temporary ? Loc.T("MemDoc.ResolverAgora") : Loc.T("MemDoc.ResolverVez"),
    };
    public string Badge => (Fix.Admin ? Loc.T("MemDoc.Administrador") : "") + (Fix.Kind == FixKind.Info ? Loc.T("MemDoc.Orientacao") : Fix.Reversible || Fix.Kind == FixKind.DisableStartup ? Loc.T("MemDoc.Reversivel") : Fix.Kind == FixKind.Navigate ? Loc.T("MemDoc.OutraPagina") : Loc.T("MemDoc.SemDesfazer"));
    public ICommand? ApplyCommand { get; set; }
    public ICommand? RevertCommand { get; set; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public Guid? RunId { get => _runId; set { if (Set(ref _runId, value)) OnPropertyChanged(nameof(CanRevert)); } }
    public bool CanRevert => RunId is not null;
    public bool Admin => Fix.Admin;
    /// <summary>
    /// Nível em que a execução <see cref="RunId"/> foi feita: Desfazer precisa ir ao MESMO aplicador (cada nível tem o seu diário).
    /// "Desligar da inicialização" usa o nível da entrada (máquina = administrador), que não é o <see cref="Admin"/> da solução.
    /// </summary>
    public BrokerLevel RunLevel { get; set; }
    /// <summary>Selo: vermelho quando não há desfazer (risco à vista), ciano nos demais.</summary>
    public UiTone BadgeTone => Fix.Kind != FixKind.Info && !Fix.Reversible && Fix.Kind is not (FixKind.DisableStartup or FixKind.Navigate) ? UiTone.Bad : UiTone.Info;
}

/// <summary>Cartão de suspeito.</summary>
public sealed class DoctorSuspectRow
{
    public required Suspect Suspect { get; init; }
    public string Title => Suspect.Title;
    public string Severity => Suspect.Severity;
    public string SeverityText => Suspect.Severity switch { "bad" => Loc.T("Gpu.Problema"), "warn" => Loc.T("Gpu.Atencao"), _ => Loc.T("Mem.Informacao") };
    public string Evidence => Suspect.Evidence;
    public string Explanation => Suspect.Explanation;
    public IReadOnlyList<DoctorFixRow> Temporary { get; init; } = [];
    public IReadOnlyList<DoctorFixRow> Permanent { get; init; } = [];
    public IReadOnlyList<MemSourceRow> Sources { get; init; } = [];
    public bool HasTemporary => Temporary.Count > 0;
    public bool HasPermanent => Permanent.Count > 0;
    public bool HasSources => Sources.Count > 0;
    public UiTone ToneKind => Suspect.Severity switch { "bad" => UiTone.Bad, "warn" => UiTone.Warn, _ => UiTone.Info };
    public string IconKey => Suspect.Severity switch { "bad" => "Icon.Error", "warn" => "Icon.Warning", _ => "Icon.Info" };
    /// <summary>Tendência do suspeito (memória privada do app, ou memória comprometida do PC) nas leituras desta sessão.</summary>
    public IReadOnlyList<double> Trend { get; init; } = [];
    public string TrendLabel { get; init; } = "";
    public bool HasTrend => Trend.Count > 1;
}

/// <summary>
/// "Memória: diagnóstico": quem está comendo a memória agora e ao longo do tempo (processos, serviços, kernel), com soluções
/// temporárias e permanentes. Leitura direta; toda escrita vai pelo aplicador (Host.ApplyAsync) com confirmação.
/// </summary>
public sealed class MemoryDoctorPage : ModulePage
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly MemoryHistory _history = new(480);
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, DoctorFixRow> _applied = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenSuspects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scannedTags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _tagDrivers = new(StringComparer.Ordinal);
    private DoctorContext _context = new();
    private bool _subscribed, _sampling, _contextLoaded, _keepRecording, _gameGuard, _visible;
    private int _intervalMinutes = 1;
    private DateTimeOffset? _lastPurge;
    private string _headline = Loc.T("Mem.LendoMemoria"), _headlineTone = "info", _subline = "", _kernelText = "", _gpuText = "", _historyText = "";
    private string _guardText = "", _poolNote = "", _lastSample = "";
    private Geometry? _usedGeometry, _commitGeometry, _poolGeometry;
    private bool _guardSuggest;
    private double _inUseBytes = double.NaN, _totalBytes = double.NaN, _commitPercent = double.NaN, _usedPercent = double.NaN;
    private System.Windows.GridLength _stackInUse = new(1, System.Windows.GridUnitType.Star), _stackModified = new(0, System.Windows.GridUnitType.Star);
    private System.Windows.GridLength _stackStandby = new(0, System.Windows.GridUnitType.Star), _stackFree = new(0, System.Windows.GridUnitType.Star);
    private string _inUseText = "", _modifiedText = "", _standbyText = "", _freeText = "", _commitText = "";

    public MemoryDoctorPage()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => _ = SampleAsync();
        SampleNowCommand = new RelayCommand(() => _ = SampleAsync(), () => !_sampling);
        ClearCommand = new RelayCommand(ClearHistory, () => !_sampling);
        PurgeNowCommand = new RelayCommand(() => _ = RunAsync(PurgeFromGuardAsync), () => !Busy);
    }

    public override string Title => Loc.T("MemDoc.NavTitle");
    public override string Group => Loc.T("Nav.Laboratorio");
    public override string Subtitle => Loc.T("MemDoc.NavSubtitle");

    public ICommand SampleNowCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand PurgeNowCommand { get; }

    public ObservableCollection<DoctorBar> Bars { get; } = [];
    public ObservableCollection<DoctorSuspectRow> Suspects { get; } = [];
    public ObservableCollection<DoctorConsumerRow> Consumers { get; } = [];
    public ObservableCollection<DoctorEvent> Timeline { get; } = [];
    public IReadOnlyList<int> IntervalOptions { get; } = [1, 2, 5];

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string HeadlineTone { get => _headlineTone; private set => Set(ref _headlineTone, value); }
    public string Subline { get => _subline; private set => Set(ref _subline, value); }
    public string KernelText { get => _kernelText; private set => Set(ref _kernelText, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string HistoryText { get => _historyText; private set => Set(ref _historyText, value); }
    public string PoolNote { get => _poolNote; private set => Set(ref _poolNote, value); }
    public string LastSample { get => _lastSample; private set => Set(ref _lastSample, value); }
    public string GuardText { get => _guardText; private set => Set(ref _guardText, value); }
    public bool GuardSuggest { get => _guardSuggest; private set => Set(ref _guardSuggest, value); }
    public Geometry? UsedGeometry { get => _usedGeometry; private set => Set(ref _usedGeometry, value); }
    public Geometry? CommitGeometry { get => _commitGeometry; private set => Set(ref _commitGeometry, value); }
    public Geometry? PoolGeometry { get => _poolGeometry; private set => Set(ref _poolGeometry, value); }
    public bool HasSuspects => Suspects.Count > 0;
    public bool NoSuspects => Suspects.Count == 0 && _history.Count > 0;
    public bool HasTimeline => Timeline.Count > 0;

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Ainda sem nenhuma leitura (esqueleto no destaque e nas listas).</summary>
    public bool IsLoading => _history.Count == 0;
    public bool HasSample => _history.Count > 0;
    public double InUseBytes { get => _inUseBytes; private set => Set(ref _inUseBytes, value); }
    public double TotalBytes { get => _totalBytes; private set => Set(ref _totalBytes, value); }
    /// <summary>Pressão de memória: % do limite de memória comprometida (RAM + arquivo de paginação).</summary>
    public double CommitPercent { get => _commitPercent; private set => Set(ref _commitPercent, value); }
    public double UsedPercent { get => _usedPercent; private set => Set(ref _usedPercent, value); }
    /// <summary>Barra empilhada da RAM: em uso | modificada | cache | livre (larguras proporcionais).</summary>
    public System.Windows.GridLength StackInUse { get => _stackInUse; private set => Set(ref _stackInUse, value); }
    public System.Windows.GridLength StackModified { get => _stackModified; private set => Set(ref _stackModified, value); }
    public System.Windows.GridLength StackStandby { get => _stackStandby; private set => Set(ref _stackStandby, value); }
    public System.Windows.GridLength StackFree { get => _stackFree; private set => Set(ref _stackFree, value); }
    public string InUseText { get => _inUseText; private set => Set(ref _inUseText, value); }
    public string ModifiedText { get => _modifiedText; private set => Set(ref _modifiedText, value); }
    public string StandbyText { get => _standbyText; private set => Set(ref _standbyText, value); }
    public string FreeText { get => _freeText; private set => Set(ref _freeText, value); }
    /// <summary>"6,1 GB de 13,2 GB".</summary>
    public string CommitText { get => _commitText; private set => Set(ref _commitText, value); }
    public bool HasChart => _history.Count > 1;

    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set
        {
            if (!IntervalOptions.Contains(value) || !Set(ref _intervalMinutes, value)) return;
            _timer.Interval = TimeSpan.FromMinutes(value);
            AddEvent(Loc.F("MemDoc.IntervaloLeituraNMin", value), "info");
        }
    }

    /// <summary>Continua lendo com a página fechada (enquanto o NEXUS estiver aberto). Padrão: desligado.</summary>
    public bool KeepRecording
    {
        get => _keepRecording;
        set { if (Set(ref _keepRecording, value) && !value && !_visible) _timer.Stop(); }
    }

    /// <summary>Modo "vigiar enquanto jogo" (padrão desligado): só AVISA e oferece o botão; nada roda sozinho.</summary>
    public bool GameGuard
    {
        get => _gameGuard;
        set
        {
            if (!Set(ref _gameGuard, value)) return;
            GuardText = value ? Loc.T("MemDoc.VigiandoAvisoQuandoJogo") : "";
            if (!value) GuardSuggest = false;
        }
    }

    public static string HonestText =>
        Loc.T("MemDoc.NadaAquiLimpadorRam2") +
        Loc.T("MemDoc.TravaPcMemoriaComprometida") +
        Loc.T("MemDoc.CadaSolucaoDizFaz");

    // ------------------------------------------------------------------ ciclo de vida

    public override async Task OnShownAsync()
    {
        _visible = true;
        if (!_subscribed && Host is INotifyPropertyChanged host)
        {
            host.PropertyChanged += OnHostChanged;
            _subscribed = true;
        }
        if (!OperatingSystem.IsWindows()) { Headline = Loc.T("Mem.DisponivelSoWindows"); return; }
        _timer.Start();
        if (!_contextLoaded) _ = LoadContextAsync();
        if (_history.Latest is null || DateTimeOffset.Now - _history.Latest.At > TimeSpan.FromSeconds(30)) await SampleAsync();
    }

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedPage) || sender is not MainViewModel main) return;
        _visible = ReferenceEquals(main.SelectedPage, this);
        if (!_visible && !_keepRecording) _timer.Stop();
    }

    private void ClearHistory()
    {
        _history.Clear();
        _seenSuspects.Clear();
        Timeline.Clear();
        OnPropertyChanged(nameof(HasTimeline));
        AddEvent(Loc.T("MemDoc.HistoricoApagadoRecomecandoObservar"), "info");
        _ = SampleAsync();
    }

    // ------------------------------------------------------------------ leitura

    private async Task LoadContextAsync()
    {
        _contextLoaded = true;
        try
        {
            var gpus = Host?.Facts?.Gpus ?? [];
            var sysMain = Host?.Facts?.Services.FirstOrDefault(s => string.Equals(s.Name, "SysMain", StringComparison.OrdinalIgnoreCase));
            var (state, analysis, localTags) = await Task.Run(() =>
            {
                MemoryAnalysis? a = null;
                try { a = MemoryLabAnalyzer.Analyze(WindowsMemoryReader.ReadSmbios(), gpus); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                return (WindowsMemoryReader.ReadState(sysMain?.State, sysMain?.StartType), a, WindowsDoctorReader.ReadLocalPoolTagTxt());
            });
            _context = _context with
            {
                CompressionEnabled = state.CompressionEnabled,
                PageFileMode = state.PageFile.Mode,
                InstalledRamBytes = analysis is null ? 0 : (long)analysis.Inventory.InstalledBytes,
                SingleChannel = analysis is null ? null : analysis.Layout.Mode == ChannelMode.SingleChannel,
                FreeSlot = analysis?.Inventory.Empty.Any(d => !d.LooksSoldered),
                LocalPoolTags = localTags,
            };
            Rebuild();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("MemDoc.ConfiguracaoMemoriaNaoLida", ex.Message); }
    }

    private async Task SampleAsync()
    {
        if (_sampling || !OperatingSystem.IsWindows()) return;
        _sampling = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var snap = await Task.Run(() => WindowsDoctorReader.Read());
            _history.Add(snap);
            LastSample = Loc.F("MemDoc.UltimaLeituraNCada", snap.At.ToLocalTime(), _intervalMinutes, _history.Count);
            PoolNote = snap.Problems.TryGetValue("tags do pool", out var why) ? why : "";
            ShowNow(snap);
            ShowConsumers();
            ShowTimelineChart();
            await IdentifyTagsAsync();
            Rebuild();
            CheckGuard(snap);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("Priv.LeituraFalhouN", ex.Message); }
        finally
        {
            _sampling = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ShowNow(MemorySnapshot snap)
    {
        var s = snap.System;
        if (s.Total <= 0) return;
        var used = (double)s.InUse / s.Total;
        Headline = Loc.F("MemDoc.NUsoNComprometida", Gb(s.InUse), Gb(s.Total), s.CommitRatio * 100);
        HeadlineTone = s.CommitRatio >= 0.9 || used >= 0.92 ? "bad" : s.CommitRatio >= 0.8 || used >= 0.85 ? "warn" : "good";
        Subline = s.CommitRatio >= 0.9
            ? Loc.T("MemDoc.PertoLimiteProgramasPodem")
            : Loc.T("MemDoc.CacheMemoriaLivreSao");
        InUseBytes = s.InUse;
        TotalBytes = s.Total;
        UsedPercent = used * 100;
        CommitPercent = s.CommitRatio * 100;
        CommitText = Loc.F("MemDoc.NDeN", Gb(s.CommitTotal), Gb(s.CommitLimit));
        var modified = Math.Max(0, s.Modified ?? 0);
        var standby = Math.Max(0, s.Standby ?? 0);
        var free = Math.Max(0, s.Total - s.InUse - modified - standby);
        static System.Windows.GridLength Star(long v) => new(Math.Max(0, v), System.Windows.GridUnitType.Star);
        StackInUse = Star(s.InUse);
        StackModified = Star(modified);
        StackStandby = Star(standby);
        StackFree = Star(free);
        InUseText = Gb(s.InUse);
        ModifiedText = Gb(modified);
        StandbyText = Gb(standby);
        FreeText = Gb(free);
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasSample));
        OnPropertyChanged(nameof(HasChart));
        Bars.Clear();
        Bars.Add(new DoctorBar(Loc.T("MemDoc.Uso"), Gb(s.InUse), used, used >= 0.92 ? "bad" : used >= 0.85 ? "warn" : "good", Loc.T("MemDoc.TotalDisponivelModificadaComo")));
        if (s.Modified is { } mod) Bars.Add(new DoctorBar(Loc.T("MemDoc.Modificada"), Gb(mod), (double)mod / s.Total, "info", Loc.T("MemDoc.EsperandoSerGravadaDisco")));
        if (s.Standby is { } sb) Bars.Add(new DoctorBar(Loc.T("Mem.CacheListaEspera"), Gb(sb), (double)sb / s.Total, "info", Loc.T("MemDoc.DisponivelDevolvidaHoraQuem")));
        if (s.FreeZero is { } fz) Bars.Add(new DoctorBar(Loc.T("MemDoc.Livre2"), Gb(fz), (double)fz / s.Total, "info", Loc.T("MemDoc.SemUsoNenhumPouca")));
        Bars.Add(new DoctorBar(Loc.T("MemDoc.ComprometidaLimite"), Loc.F("MemDoc.NDeN", Gb(s.CommitTotal), Gb(s.CommitLimit)), s.CommitRatio, s.CommitRatio >= 0.9 ? "bad" : s.CommitRatio >= 0.8 ? "warn" : "good",
            Loc.T("MemDoc.LimiteRamArquivoPaginacao")));
        KernelText = Loc.F("MemDoc.KernelPoolNaoPaginado", Gb(s.NonPagedPool), Gb(s.PagedPool)) +
                     (snap.PoolTags is { Count: > 0 } tags ? Loc.F("MemDoc.NTagsLidas", tags.Count) : "");
        GpuText = s.GpuShared is { } gs ? Loc.F("MemDoc.GpuNRamUso", Gb(gs)) + (s.GpuDedicated is { } gd ? Loc.F("MemDoc.VramDedicadaUsoN", Gb(gd)) : "") : "";
    }

    private void ShowConsumers()
    {
        Consumers.Clear();
        foreach (var g in _history.LatestGroups.Where(g => g.Kind != AppKind.System || g.Key == "app:memcompression").Take(10))
        {
            var v = LeakDetector.Evaluate(_history.Group(g.Key, x => x.PrivateBytes), LeakThresholds.PrivateBytes);
            var (trend, tone) = v.Kind switch
            {
                TrendKind.Insufficient => (Loc.T("MemDoc.Observando"), "info"),
                TrendKind.Leak => (Loc.F("MemDoc.NHSemParar", MemoryDoctorEngine.Bytes(v.SlopePerHour)), "bad"),
                TrendKind.Noisy => (Loc.T("MemDoc.SobeDesce"), "info"),
                _ => (v.SlopePerHour > 0 ? $"+{MemoryDoctorEngine.Bytes(v.SlopePerHour)}/h" : Loc.T("MemDoc.Estavel"), "good"),
            };
            var kind = g.Kind switch
            {
                AppKind.Browser => Loc.T("MemDoc.Navegador"), AppKind.ServiceHost or AppKind.Service => Loc.T("MemDoc.Servico"), AppKind.VirtualMachine => Loc.T("MemDoc.MaquinaVirtual"),
                AppKind.System => "Windows", _ => Loc.T("MemDoc.Programa"),
            };
            var history = _history.Group(g.Key, x => x.PrivateBytes).Select(t => t.Value).ToList();
            Consumers.Add(new DoctorConsumerRow(g.DisplayName, Gb(g.PrivateBytes), Gb(g.WorkingSet), g.ProcessCount == 1 ? Loc.T("MemDoc.N1Processo") : Loc.F("MemDoc.NProcessos", g.ProcessCount), trend, tone, kind, history));
        }
        var snaps = _history.Snapshots;
        var first = snaps.Count == 0 ? null : snaps[0];
        HistoryText = first is null ? "" : Loc.F("MemDoc.ObservandoDesdeNN", first.At.ToLocalTime(), LeakDetector.Minutes(DateTimeOffset.Now - first.At)) +
            Loc.T("MemDoc.VazamentoSoApontadoDepois");
    }

    private void ShowTimelineChart()
    {
        var snaps = _history.Snapshots;
        if (snaps.Count < 2) { UsedGeometry = CommitGeometry = PoolGeometry = null; return; }
        var max = snaps.Max(s => (double)Math.Max(s.System.CommitLimit, s.System.Total));
        if (max <= 0) return;
        UsedGeometry = Line(snaps, s => s.System.InUse / max);
        CommitGeometry = Line(snaps, s => s.System.CommitTotal / max);
        var poolMax = Math.Max(1, snaps.Max(s => (double)s.System.NonPagedPool) * 1.2);
        PoolGeometry = Line(snaps, s => s.System.NonPagedPool / poolMax);
    }

    /// <summary>Linha 0..1 num quadro 1000 × 100 (Path com Stretch=Fill); o primeiro trecho invisível fixa a escala.</summary>
    private static StreamGeometry Line(IReadOnlyList<MemorySnapshot> snaps, Func<MemorySnapshot, double> y)
    {
        var t0 = snaps[0].At;
        var span = Math.Max(1, (snaps[^1].At - t0).TotalSeconds);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(0, 0), false, false);
            ctx.LineTo(new System.Windows.Point(1000, 100), false, false);
            for (var i = 0; i < snaps.Count; i++)
            {
                var p = new System.Windows.Point((snaps[i].At - t0).TotalSeconds / span * 1000, 100 - Math.Clamp(y(snaps[i]), 0, 1) * 100);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>Tags do pool que crescem sem dono conhecido: procura o texto da tag nos drivers (uma vez por tag, até 3).</summary>
    private async Task IdentifyTagsAsync()
    {
        var unknown = MemoryDoctorEngine.GrowingTags(_history, _context)
            .Where(t => t.Owner is null && !_scannedTags.Contains(t.Tag)).Take(3).Select(t => t.Tag).ToList();
        if (unknown.Count == 0) return;
        foreach (var tag in unknown) _scannedTags.Add(tag);
        var found = await Task.Run(() => unknown.ToDictionary(t => t, t => WindowsDoctorReader.FindDriversWithTag(t)));
        foreach (var (tag, drivers) in found)
        {
            _tagDrivers[tag] = drivers;
            AddEvent(drivers.Count == 0 ? Loc.F("MemDoc.TagNNenhumDriver", tag) : Loc.F("MemDoc.TagNEncontradaN", tag, string.Join(", ", drivers)), "info");
        }
        _context = _context with { TagDrivers = new Dictionary<string, IReadOnlyList<string>>(_tagDrivers) };
    }

    private void Rebuild()
    {
        if (_history.Latest is null) return;
        var suspects = MemoryDoctorEngine.Evaluate(_history, _context);
        Suspects.Clear();
        foreach (var s in suspects)
        {
            if (_seenSuspects.Add(s.Id)) AddEvent(s.Title, s.Severity);
            var appTrend = s.GroupKey is { } key ? _history.Group(key, x => x.PrivateBytes).Select(t => t.Value).ToList() : [];
            var trend = appTrend.Count > 1 ? appTrend : _history.System(x => x.CommitTotal).Select(t => t.Value).ToList();
            Suspects.Add(new DoctorSuspectRow
            {
                Suspect = s,
                Trend = trend,
                TrendLabel = appTrend.Count > 1 ? Loc.T("MemDoc.MemoriaAppNasUltimas") : Loc.T("MemDoc.MemoriaComprometidaPcNas"),
                Temporary = s.Temporary.Select(f => FixRow(s, f)).ToList(),
                Permanent = s.Permanent.Select(f => FixRow(s, f)).ToList(),
                Sources = s.Sources.Select(Source).ToList(),
            });
        }
        OnPropertyChanged(nameof(HasSuspects));
        OnPropertyChanged(nameof(NoSuspects));
    }

    private DoctorFixRow FixRow(Suspect s, FixOption f)
    {
        var key = s.Id + "|" + f.Id;
        var row = new DoctorFixRow { Fix = f };
        if (_applied.TryGetValue(key, out var previous)) { row.RunId = previous.RunId; row.RunLevel = previous.RunLevel; row.Status = previous.Status; }
        row.ApplyCommand = new RelayCommand(() => _ = RunAsync(() => ApplyFixAsync(key, s, row)), () => !Busy && !row.CanRevert);
        row.RevertCommand = new RelayCommand(() => _ = RunAsync(() => RevertFixAsync(key, row)), () => !Busy && row.CanRevert);
        return row;
    }

    // ------------------------------------------------------------------ soluções (escrita só pelo aplicador)

    private async Task ApplyFixAsync(string key, Suspect suspect, DoctorFixRow row)
    {
        if (Host is null) return;
        var f = row.Fix;
        switch (f.Kind)
        {
            case FixKind.Navigate:
                var type = f.Target switch
                {
                    "DriverUpdates" => typeof(DriverUpdatesPage), "MemoryLab" => typeof(MemoryLabPage), "Services" => typeof(ServicesPage),
                    "Startup" => typeof(StartupPage), _ => null,
                };
                if (type is not null && Host.FindPage(type) is { } page) Host.Navigate(page);
                return;
            case FixKind.DisableStartup:
                await DisableStartupAsync(key, row);
                return;
            case FixKind.Info:
                return;
        }
        if (f.ActionKey is null) return;
        if (f.Guard == ProcessGuard.Forbidden) { row.Status = f.GuardReason; return; }
        if (f.Guard == ProcessGuard.Sensitive &&
            !Host.Confirm(Loc.T("MemDoc.Confirmacao12"), Loc.F("MemDoc.NNVoceVai", f.GuardReason, f.Title)))
        {
            row.Status = Loc.T("Clean.Cancelado");
            return;
        }
        var text = Loc.F("MemDoc.NNNQuanto", suspect.Title, f.Title, f.WhatItDoes, f.HowLong, f.Cost) +
                   (f.Reversible ? Loc.T("MemDoc.DesfazerBotaoDesfazerAqui") : Loc.T("MemDoc.NaoHaDesfazerAcao")) +
                   (f.Admin ? Loc.T("MemDoc.ExigeAdministradorWindowsVai") : "");
        var r = await Host.ApplyAsync(f.Title, text, [new ActionRequest(f.ActionKey, f.Value, f.Arguments)], f.Admin ? BrokerLevel.Admin : BrokerLevel.User);
        if (r is null) { row.Status = Loc.T("Shell.CanceladoNadaFoiAlterado"); return; }
        var item = r.Response?.Results is { Count: > 0 } items ? items[0] : null;
        var outcome = item?.Outcome ?? "";
        if (outcome is "Verified" or "AlreadyConfigured")
        {
            row.RunId = outcome == "Verified" && f.Reversible ? r.Response?.RunId : null;
            row.RunLevel = f.Admin ? BrokerLevel.Admin : BrokerLevel.User;
            row.Status = outcome == "AlreadyConfigured" ? Loc.T("MemDoc.JaEstavaAssim") : f.Term == FixTerm.Temporary ? Loc.F("MemDoc.FeitoNLembreIsso", DateTime.Now) : Loc.T("MemDoc.Feito");
            _applied[key] = row;
            if (f.ActionKey == DoctorActions.StandbyPurge) _lastPurge = DateTimeOffset.Now;
            AddEvent($"{f.Title}: {row.Status}", "good");
            if (f.Term == FixTerm.Temporary) _ = SampleAsync();
        }
        else row.Status = item?.Detail is { Length: > 0 } d ? Loc.F("MemDoc.NaoFeitoN", d) : Loc.F("MemDoc.NaoFeitoN", r.Message);
        Message = row.Status;
    }

    private async Task RevertFixAsync(string key, DoctorFixRow row)
    {
        if (Host is null || row.RunId is not { } run) return;
        var r = await Host.RevertAsync(run, row.RunLevel);
        if (r is null) return;
        if (r.Response?.FullyRestored == true)
        {
            row.RunId = null;
            row.Status = Loc.T("Svc.Desfeito");
            _applied.Remove(key);
            AddEvent(Loc.F("MemDoc.NDesfeito", row.Title), "info");
        }
        else row.Status = Loc.F("Mem.NaoDesfeitoN", r.Message);
        Message = row.Status;
    }

    /// <summary>Procura o app no inventário do módulo Inicialização e desliga pelo mesmo pedido da página Inicialização.</summary>
    private async Task DisableStartupAsync(string key, DoctorFixRow row)
    {
        if (Host is null || row.Fix.Target is not { } exe) return;
        var items = await Task.Run(() =>
            new StartupInventory(new WindowsBootRegistryReader(), new WindowsFileInspector(), Environment.GetEnvironmentVariable,
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                .Read(WindowsStartupFolders.Current(), new WindowsTaskScheduler(), new WindowsServiceReader(), null).Items);
        var match = items.FirstOrDefault(i => i.Enabled && !i.IsMicrosoft && i.ExecutablePath is { } p &&
                                              string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), exe, StringComparison.OrdinalIgnoreCase) &&
                                              StartupPage.ToggleRequest(i) is not null);
        if (match is null) { row.Status = Loc.F("MemDoc.NNaoAbreWindows", exe); return; }
        var req = StartupPage.ToggleRequest(match)!.Value;
        var r = await Host.ApplyAsync(Loc.T("MemDoc.DesligarInicializacao"),
            Loc.F("MemDoc.NDeixaAbrirWindows", match.Name, match.SourceLabel, row.Fix.Cost),
            [new ActionRequest(req.Key, "disabled", req.Args)], req.Level);
        if (r is null) { row.Status = Loc.T("Shell.CanceladoNadaFoiAlterado"); return; }
        var outcome = r.Response?.Results is { Count: > 0 } items2 ? items2[0].Outcome : "";
        if (outcome is "Verified" or "AlreadyConfigured")
        {
            row.RunId = outcome == "Verified" ? r.Response?.RunId : null;
            row.RunLevel = req.Level;
            row.Status = outcome == "Verified" ? Loc.T("MemDoc.DesligadoInicializacaoValeProximo") : Loc.T("MemDoc.JaEstavaDesligado");
            _applied[key] = row;
            AddEvent($"{match.Name}: {row.Status}", "good");
        }
        else row.Status = Loc.F("MemDoc.NaoFeitoN", r.Message);
        Message = row.Status;
    }

    // ------------------------------------------------------------------ modo "vigiar enquanto jogo"

    private void CheckGuard(MemorySnapshot snap)
    {
        if (!_gameGuard) return;
        var game = WindowsDoctorReader.RunningGame(snap.Processes);
        var d = Nexus.Lab.Memory.Problems.GameGuard.Decide(new GameGuardSettings(true), snap.System, game, DateTimeOffset.Now, _lastPurge);
        GuardSuggest = d.Suggest;
        GuardText = d.Suggest ? d.Reason + Loc.T("MemDoc.EsvaziarListaEsperaPode") : Loc.T("MemDoc.Vigiando") + d.Reason;
        if (d.Suggest)
        {
            Host?.Notify(Loc.T("MemDoc.MemoriaLivreMuitoBaixa"));
            AddEvent(d.Reason, "warn");
        }
    }

    private async Task PurgeFromGuardAsync()
    {
        if (Host is null) return;
        var r = await Host.ApplyAsync(Loc.T("MemDoc.EsvaziarListaEspera"),
            Loc.T("MemDoc.JogaForaCacheArquivos") +
            Loc.T("MemDoc.CustoJogoProgramasReleem"),
            [new ActionRequest(DoctorActions.StandbyPurge)], BrokerLevel.Admin);
        if (r?.Response?.Results is { Count: > 0 } items && items[0].Outcome == "Verified")
        {
            _lastPurge = DateTimeOffset.Now;
            GuardSuggest = false;
            AddEvent(Loc.T("MemDoc.ListaEsperaEsvaziadaModo"), "good");
            _ = SampleAsync();
        }
    }

    // ------------------------------------------------------------------ utilidades

    private void AddEvent(string text, string tone)
    {
        Timeline.Insert(0, new DoctorEvent(DateTime.Now.ToString("HH:mm", Pt), text, tone));
        while (Timeline.Count > 60) Timeline.RemoveAt(Timeline.Count - 1);
        OnPropertyChanged(nameof(HasTimeline));
    }

    private static MemSourceRow Source(MemorySource s) => new() { Title = s.Title, Url = s.Url, OpenCommand = new RelayCommand(() => OpenUrl(s.Url)) };

    /// <summary>Abre só as fontes fixas do diagnóstico (https), nunca texto vindo de fora.</summary>
    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private static string Gb(long bytes) => MemoryDoctorEngine.Bytes(bytes);
}
