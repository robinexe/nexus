using System.Collections.ObjectModel;
using System.Globalization;
using Nexus.Broker;
using Nexus.Desktop.Views.Modules.CpuLab;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Cpu;
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Laboratório do processador: identidade por CPUID e pela topologia do Windows, medições de verdade (frequência por núcleo,
/// latência de cache/RAM, núcleo-a-núcleo, carga sustentada) e o diagnóstico do que segura o processador. Medir não muda nada no
/// PC; as duas correções do plano de energia vão pelo aplicador (catálogo cpulab), com confirmação, diário e desfazer.
/// As rodadas ficam em %LocalAppData%\NEXUS\lab\cpu\*.json (só dados do NEXUS) para comparar antes/depois.
/// </summary>
public sealed class CpuLabPage : ModulePage
{
    private static CultureInfo Pt => Loc.Culture; // idioma da interface (docs/12-idiomas.md)

    private readonly CpuLabStore _store = new(CpuLabStore.DefaultDirectory);
    private CpuIdentity? _identity;
    private CpuTopology? _topology;
    private CpuPowerState _power = new();
    private SystemFacts? _facts;
    private CpuLabRun? _quick, _prevQuick, _sustained, _prevSustained;
    private Action? _cancel;
    private Guid? _lastRun;
    private BrokerLevel _lastLevel;
    private bool _loaded, _measuring;
    private string _heroName = Loc.T("Cpu.LendoProcessador"), _heroLine = "", _progressText = "", _powerNotice = "", _frequencyText = "";
    private string _topologyText = "", _coreSummary = "", _clusterText = "", _latencySummary = "", _sustainedText = "", _scalingText = "";
    private string _identityNote = "", _historyText = "";
    private double _progress, _baseGhz;
    private double _coreCount = double.NaN, _threadCount = double.NaN, _l3Bytes = double.NaN;
    private string _coreThreadText = "", _coreDetail = "";
    private int _sustainedSeconds = 45;
    private IReadOnlyList<LatencyPoint>? _latency, _previousLatency;
    private IReadOnlyList<CacheStep>? _steps;
    private IReadOnlyList<CacheMarker>? _declared;
    private CoreToCoreMatrix? _matrix;
    private IReadOnlyList<FrequencySample>? _series, _previousSeries;

    public CpuLabPage()
    {
        MeasureCommand = new RelayCommand(() => _ = MeasureAsync(sustained: false), () => !Measuring && _loaded);
        SustainedCommand = new RelayCommand(() => _ = MeasureAsync(sustained: true), () => !Measuring && _loaded);
        CancelCommand = new RelayCommand(() => _cancel?.Invoke(), () => Measuring);
        UndoCommand = new RelayCommand(() => _ = UndoAsync(), () => CanUndo && !Busy);
        RefreshCommand = new RelayCommand(() => _ = RunAsync(LoadAsync), () => !Busy && !Measuring);
    }

    public override string Title => Loc.T("Cpu.NavTitle");
    public override string Group => Loc.T("Nav.Laboratorio");
    public override string Subtitle => Loc.T("Cpu.NavSubtitle");

    public RelayCommand MeasureCommand { get; }
    public RelayCommand SustainedCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public ObservableCollection<CpuFactRow> IdentityRows { get; } = [];
    public ObservableCollection<CpuFeatureChip> Features { get; } = [];
    public ObservableCollection<CpuCacheRow> Caches { get; } = [];
    public ObservableCollection<CpuCoreBar> CoreBars { get; } = [];
    public ObservableCollection<CpuFindingRow> Findings { get; } = [];
    public ObservableCollection<string> CacheComparisons { get; } = [];
    public ObservableCollection<string> Comparison { get; } = [];
    public IReadOnlyList<int> SustainedChoices { get; } = [30, 45, 60];

    public string HeroName { get => _heroName; private set => Set(ref _heroName, value); }
    public string HeroLine { get => _heroLine; private set => Set(ref _heroLine, value); }
    public string FrequencyText { get => _frequencyText; private set => Set(ref _frequencyText, value); }
    public string TopologyText { get => _topologyText; private set => Set(ref _topologyText, value); }
    public string IdentityNote { get => _identityNote; private set => Set(ref _identityNote, value); }
    public string PowerNotice { get => _powerNotice; private set => Set(ref _powerNotice, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string CoreSummary { get => _coreSummary; private set => Set(ref _coreSummary, value); }
    public string ClusterText { get => _clusterText; private set => Set(ref _clusterText, value); }
    public string LatencySummary { get => _latencySummary; private set => Set(ref _latencySummary, value); }
    public string SustainedText { get => _sustainedText; private set => Set(ref _sustainedText, value); }
    public string ScalingText { get => _scalingText; private set => Set(ref _scalingText, value); }
    public string HistoryText { get => _historyText; private set => Set(ref _historyText, value); }
    public double BaseGhz { get => _baseGhz; private set => Set(ref _baseGhz, value); }
    public int SustainedSeconds { get => _sustainedSeconds; set => Set(ref _sustainedSeconds, Math.Clamp(value, 30, 60)); }

    public IReadOnlyList<LatencyPoint>? Latency { get => _latency; private set => Set(ref _latency, value); }
    public IReadOnlyList<LatencyPoint>? PreviousLatency { get => _previousLatency; private set => Set(ref _previousLatency, value); }
    public IReadOnlyList<CacheStep>? Steps { get => _steps; private set => Set(ref _steps, value); }
    public IReadOnlyList<CacheMarker>? Declared { get => _declared; private set => Set(ref _declared, value); }
    public CoreToCoreMatrix? Matrix { get => _matrix; private set => Set(ref _matrix, value); }
    public IReadOnlyList<FrequencySample>? Series { get => _series; private set => Set(ref _series, value); }
    public IReadOnlyList<FrequencySample>? PreviousSeries { get => _previousSeries; private set => Set(ref _previousSeries, value); }

    public bool Measuring
    {
        get => _measuring;
        private set { if (Set(ref _measuring, value)) OnPropertyChanged(nameof(Idle)); }
    }
    public bool Idle => !Measuring;
    public bool CanUndo => _lastRun is not null;
    public bool HasQuick => _quick is not null;
    public bool HasSustained => _sustained is not null;
    public bool HasComparison => Comparison.Count > 0;

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Verdadeiro até a primeira leitura do processador terminar (esqueleto no destaque).</summary>
    public bool IsLoading => !_loaded;
    /// <summary>Destaque: o GHz do núcleo mais rápido depois de medir; antes disso, quantos núcleos.</summary>
    public string HeroTitle => double.IsFinite(BestGhz) ? Loc.T("Cpu.NucleoMaisRapido") : Loc.T("Cpu.SeuProcessador");
    public double HeroNumber => double.IsFinite(BestGhz) ? BestGhz : CoreCount;
    public int HeroDecimals => double.IsFinite(BestGhz) ? 2 : 0;
    public string HeroSuffix => double.IsFinite(BestGhz) ? " GHz" : Loc.T("Pg.Nucleos");
    public double CoreCount { get => _coreCount; private set => Set(ref _coreCount, value); }
    public double ThreadCount { get => _threadCount; private set => Set(ref _threadCount, value); }
    public double L3Bytes { get => _l3Bytes; private set => Set(ref _l3Bytes, value); }
    /// <summary>"6 / 12" (núcleos / threads).</summary>
    public string CoreThreadText { get => _coreThreadText; private set => Set(ref _coreThreadText, value); }
    public string CoreDetail { get => _coreDetail; private set => Set(ref _coreDetail, value); }
    /// <summary>GHz do núcleo mais rápido na última medição rápida (NaN = ainda não medido).</summary>
    public double BestGhz => _quick?.BestCore?.Ghz is { } g && g > 0 ? g : double.NaN;
    /// <summary>GHz médio com todos os núcleos carregados (NaN = ainda não medido).</summary>
    public double AllCoreGhz => (_sustained ?? _quick)?.AllCoreGhz is { } g && g > 0 ? g : double.NaN;
    /// <summary>Latência da RAM medida pela curva (ns; NaN = ainda não medido).</summary>
    public double RamLatencyNs => _quick?.CacheSteps.FirstOrDefault(s => s.Level == "RAM")?.Ns is { } ns && ns > 0 ? ns : double.NaN;
    public string MostFastCoreText => _quick?.BestCore is { } b ? Loc.F("Cpu.NucleoN", b.Core + 1) : "";
    /// <summary>Base anunciada × turbo medido (o BeforeAfter do destaque).</summary>
    public bool HasTurboCompare => BaseGhz > 0 && double.IsFinite(BestGhz);
    public bool SingleChannel => _facts?.MemoryModules.Count == 1;
    public bool HasEfficiencyCores => _topology?.Hybrid == true;

    private void NotifyNumbers()
    {
        foreach (var name in new[]
                 {
                     nameof(IsLoading), nameof(HeroTitle), nameof(HeroNumber), nameof(HeroDecimals), nameof(HeroSuffix), nameof(BestGhz),
                     nameof(AllCoreGhz), nameof(RamLatencyNs), nameof(MostFastCoreText), nameof(HasTurboCompare), nameof(SingleChannel),
                     nameof(HasEfficiencyCores),
                 })
            OnPropertyChanged(name);
    }

    public static string MethodText { get; } =
        Loc.T("Cpu.FrequenciaCadeiaSomasXor") +
        Loc.T("Cpu.OperacoesSegundoCiclosSegundo") +
        Loc.T("Cpu.WindowsValeMelhorVarias") +
        Loc.T("Cpu.LatenciaListaLigadaEmbaralhada") +
        Loc.T("Cpu.N512MbPreBuscador") +
        Loc.T("Cpu.DegrausSaoAchadosCurva") +
        Loc.T("Cpu.TraducaoPaginasTlbComo") +
        Loc.T("Cpu.NucleoNucleoDuasThreads") +
        Loc.T("Cpu.InterlockedCompareexchangeMetadeTemp") +
        Loc.T("Cpu.DividemMesmaL3Ccx") +
        Loc.T("Cpu.SustentadoMesmaCadeiaTodos") +
        Loc.T("Cpu.SegundoQuedaDepoisPrimeiros") +
        Loc.T("Cpu.TodosJuntosNucleoSozinho") +
        Loc.T("Cpu.ReaisEscalamMenos") +
        Loc.T("Cpu.NadaDissoUsaDriver") +
        Loc.T("Cpu.TopologiaWindowsCronometroAlta");

    // ------------------------------------------------------------------ ciclo de vida

    public override async Task OnShownAsync()
    {
        if (!_loaded) await RunAsync(LoadAsync);
    }

    public override Task OnFactsReadyAsync(SystemFacts facts)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        if (_loaded)
        {
            BuildIdentity();
            Diagnose();
        }
        return Task.CompletedTask;
    }

    private async Task LoadAsync()
    {
        var (identity, topology, power, runs) = await Task.Run(() =>
        {
            var id = CpuIdDecoder.Decode(new HardwareCpuIdSource());
            var topo = TopologyReader.Read();
            var pw = CpuPowerReader.Create().Read();
            IReadOnlyList<CpuLabRun> all;
            try { all = _store.LoadAll(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { all = []; }
            return (id, topo, pw, all);
        });
        _identity = identity;
        _topology = topology;
        _power = power;
        var quicks = runs.Where(r => r.Kind == CpuRunKind.Quick).ToList();
        var sustained = runs.Where(r => r.Kind == CpuRunKind.Sustained).ToList();
        _quick = quicks.ElementAtOrDefault(0);
        _prevQuick = quicks.ElementAtOrDefault(1);
        _sustained = sustained.ElementAtOrDefault(0);
        _prevSustained = sustained.ElementAtOrDefault(1);
        HistoryText = runs.Count == 0
            ? Loc.T("Cpu.NenhumaMedicaoGuardadaAinda")
            : Loc.F("Cpu.NMedicoesGuardadasN", runs.Count, _store.Directory, runs[0].TakenAt.ToLocalTime());
        _loaded = true;
        BuildIdentity();
        ShowResults();
        Diagnose();
    }

    // ------------------------------------------------------------------ identidade

    private void BuildIdentity()
    {
        var id = _identity;
        var topo = _topology;
        if (id is null || topo is null) return;
        var realTopology = topo.Source.StartsWith("GetLogical", StringComparison.Ordinal);

        HeroName = id.Brand ?? _facts?.CpuModel ?? Loc.T("Cpu.NavTitle");
        var parts = new List<string>();
        if (id.Microarchitecture is { } m) parts.Add($"{m.Core} {m.Codename}");
        var cores = realTopology ? topo.CoreCount : _facts?.PhysicalCores ?? topo.CoreCount;
        var threads = realTopology ? topo.LogicalCount : _facts?.LogicalProcessors ?? topo.LogicalCount;
        parts.Add(topo.Hybrid
            ? Loc.F("Cpu.NPNNucleos", topo.PerformanceCores, topo.EfficiencyCores, threads)
            : Loc.F("Pg.NNucleosNThreads", cores, threads));
        var l3 = topo.TotalL3Bytes > 0 ? topo.TotalL3Bytes : id.Cache(3)?.SizeBytes ?? 0;
        if (l3 > 0) parts.Add($"{CacheStepDetector.Bytes(l3)} L3");
        HeroLine = string.Join(" · ", parts);
        CoreCount = topo.Hybrid ? topo.PerformanceCores + topo.EfficiencyCores : cores;
        ThreadCount = threads;
        L3Bytes = l3 > 0 ? l3 : double.NaN;
        CoreThreadText = string.Create(Pt, $"{CoreCount:0} / {threads}");
        CoreDetail = topo.Hybrid
            ? Loc.F("Cpu.NDesempenhoNEconomia", topo.PerformanceCores, topo.EfficiencyCores)
            : topo.Smt || threads > cores ? Loc.T("Cpu.N2ThreadsNucleoSmt") : Loc.T("Cpu.N1ThreadNucleo");
        IdentityNote = id.Available ? "" : id.UnavailableReason ?? "";

        IdentityRows.Clear();
        void Row(string label, string? value, string source) { if (!string.IsNullOrWhiteSpace(value)) IdentityRows.Add(new CpuFactRow(label, value, source)); }
        if (id.Available)
        {
            Row(Loc.T("Cpu.Fabricante"), id.Vendor switch { CpuVendor.Intel => "Intel", CpuVendor.Amd => "AMD", _ => id.VendorString }, Loc.T("Cpu.CpuidFolha0"));
            Row(Loc.T("Cpu.Assinatura"), $"{id.FamilyText} (0x{id.Signature:X8})", Loc.T("Cpu.CpuidFolha1"));
            Row(Loc.T("Cpu.Microarquitetura"), id.Microarchitecture is { } ma ? Loc.F("Cpu.NCodinomeNN", ma.Core, ma.Codename, ma.Year) : Loc.T("Cpu.ModeloForaTabelaNao"), Loc.T("Cpu.TabelaNexusSdmIntel"));
            if (id.Microarchitecture?.Note is { Length: > 0 } note) Row(Loc.T("Cpu.SobreChip"), note, Loc.T("Cpu.TabelaNexus"));
            Row(Loc.T("Cpu.Hipervisor"), id.HypervisorPresent ? Loc.T("Cpu.SimWindowsRodaSobre") : Loc.T("Cpu.Nao"), Loc.T("Cpu.CpuidFolha1Ecx"));
        }
        Row(Loc.T("Cpu.Topologia"), TopologySummary(topo), topo.Source);
        Row(Loc.T("Cpu.GruposWindows"), topo.Groups.ToString(Pt), topo.Source);

        BaseGhz = (id.BaseMhz > 0 ? id.BaseMhz : _power.RegistryMhz ?? _power.NtMaxMhz ?? 0) / 1000.0;
        var freq = new List<string>();
        if (_power.RegistryMhz is { } reg) freq.Add(Loc.F("Cpu.BaseNGhzRegistro", reg / 1000.0));
        if (_power.NtMaxMhz is { } nmax) freq.Add(Loc.F("Cpu.MaximaInformadaWindowsN", nmax / 1000.0));
        if (id.MaxMhz > 0) freq.Add(Loc.F("Cpu.TurboAnunciadoNGhz", id.MaxMhz / 1000.0));
        else if (id.Vendor == CpuVendor.Amd) freq.Add(Loc.T("Cpu.AmdNaoPublicaTurbo"));
        FrequencyText = freq.Count == 0 ? Loc.T("Cpu.FrequenciasAnunciadasIndisponiveis") : char.ToUpper(string.Join("; ", freq)[0], Pt) + string.Join("; ", freq)[1..] + ".";
        TopologyText = TopologySummary(topo);
        NotifyNumbers();

        Features.Clear();
        foreach (var f in id.Features) Features.Add(new CpuFeatureChip(f.Name, f.Present, f.Meaning));

        Caches.Clear();
        foreach (var c in id.Caches)
            Caches.Add(new CpuCacheRow(c.Label, CacheStepDetector.Bytes(c.SizeBytes), c.FullyAssociative ? Loc.T("Cpu.Total") : Loc.F("Cpu.NVias", c.Ways),
                Loc.F("Cpu.NBytes", c.LineBytes), c.SharedByLogical <= 1 ? Loc.T("Cpu.N1Thread") : Loc.F("Pg.NThreads", c.SharedByLogical)));

        var declared = new List<CacheMarker>();
        foreach (var lv in new[] { 1, 2, 3 })
            if (id.Cache(lv) is { } c) declared.Add(new CacheMarker(Loc.F("Cpu.CpuidLNN", lv, CacheStepDetector.Bytes(c.SizeBytes)), c.SizeBytes));
        if (declared.Count == 0)
            foreach (var g in topo.Caches.Where(c => c.Kind != CacheKind.Instruction).GroupBy(c => c.Level).OrderBy(g => g.Key))
                declared.Add(new CacheMarker(Loc.F("Cpu.WindowsLNN", g.Key, CacheStepDetector.Bytes(g.First().SizeBytes)), g.First().SizeBytes));
        Declared = declared;
    }

    private static string TopologySummary(CpuTopology t)
    {
        var s = t.Hybrid
            ? Loc.F("Cpu.NNucleosPN", t.PerformanceCores, t.EfficiencyCores, t.LogicalCount)
            : Loc.F("Cpu.NNucleosNProcessadores", t.CoreCount, t.LogicalCount, (t.Smt ? Loc.T("Cpu.N2ThreadsNucleo") : ""));
        var clusters = t.L3Clusters();
        if (t.Caches.Any(c => c.Level == 3))
            s += clusters.Count > 1
                ? Loc.F("Cpu.NBlocosL3N", clusters.Count, string.Join(" | ", clusters.Select(c => Loc.F("Cpu.NucleosNN", c[0] + 1, c[^1] + 1))))
                : Loc.T("Cpu.L3CompartilhadaTodos");
        return s;
    }

    // ------------------------------------------------------------------ medir

    private async Task MeasureAsync(bool sustained)
    {
        if (Measuring || _topology is null) return;
        _power = await Task.Run(() => CpuPowerReader.Create().Read());
        if (_power.OnBattery == true && Host is not null &&
            !Host.Confirm(Loc.T("Cpu.NotebookBateria"), Loc.T("Cpu.CarregadorEstaDesligadoBateria")))
            return;
        if (sustained && Host is not null &&
            !Host.Confirm(Loc.T("Cpu.TesteSustentado"), Loc.F("Cpu.TodosNucleosVaoTrabalhar", SustainedSeconds)))
            return;

        Measuring = true;
        Message = "";
        Progress = 0;
        ProgressText = Loc.T("Shell.Preparando");
        using var cts = new CancellationTokenSource();
        _cancel = cts.Cancel;
        var ct = cts.Token;
        var progress = new Progress<CpuLabProgress>(p => { ProgressText = p.Stage; Progress = p.Fraction; });
        var template = new CpuLabRun
        {
            Brand = _identity?.Brand ?? _facts?.CpuModel,
            OnBattery = _power.OnBattery,
            PowerMode = _power.PowerModeOverlay is { } o ? ProcessorPowerSettings.OverlayName(o) : null,
        };
        try
        {
            var runner = new CpuLabRunner(ThreadPinners.ForThisOs(), _topology, CpuLabOptions.Quick);
            var preferred = _quick?.BestCore?.Core;
            var run = await Task.Run(() => sustained
                ? runner.RunSustainedAsync(template, TimeSpan.FromSeconds(SustainedSeconds), preferred, progress, ct)
                : runner.RunQuickAsync(template, progress, ct), ct);
            try { await Task.Run(() => _store.Save(run), CancellationToken.None); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Message = Loc.F("Cpu.MedidoMasNaoFoi", ex.Message); }
            if (sustained) { _prevSustained = _sustained; _sustained = run; }
            else { _prevQuick = _quick; _quick = run; }
            HistoryText = Loc.F("Cpu.GuardadoNMedicaoN", _store.Directory, run.DurationSeconds);
            _power = await Task.Run(() => CpuPowerReader.Create().Read());
            ShowResults();
            Diagnose();
            Host?.Notify(sustained ? Loc.T("Cpu.TesteSustentadoConcluido") : Loc.T("Cpu.MedicaoConcluida"));
        }
        catch (OperationCanceledException) { Message = Loc.T("Cpu.MedicaoCanceladaNadaFoi"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = Loc.F("Cpu.MedicaoFalhouN", ex.Message); }
        finally
        {
            _cancel = null;
            Measuring = false;
            ProgressText = "";
        }
    }

    // ------------------------------------------------------------------ resultados

    private void ShowResults()
    {
        var q = _quick;
        CoreBars.Clear();
        if (q is not null && q.CoreFrequencies.Count > 0)
        {
            var max = q.CoreFrequencies.Max(c => c.Ghz);
            var best = q.BestCore;
            foreach (var c in q.CoreFrequencies)
                CoreBars.Add(new CpuCoreBar(Loc.F("Cpu.NucleoNN", c.Core + 1, (c.PerformanceCore ? "" : " (E)")), c.Ghz.ToString("0.00", Pt) + " GHz",
                    max <= 0 ? 0 : c.Ghz / max, ReferenceEquals(c, best), !c.PerformanceCore,
                    Loc.F("Cpu.ProcessadorLogicoNN", c.Logical, (c.Pinned ? Loc.T("Cpu.ThreadFixadaNucleo") : Loc.T("Cpu.SemFixacaoNucleo")))));
            var parts = new List<string>();
            if (best is not null) parts.Add(Loc.F("Cpu.MaisRapidoNucleoN", best.Core + 1, best.Ghz));
            if (q.SpreadPercent is { } spread) parts.Add(Loc.F("Cpu.DispersaoNEntreNucleos", spread));
            if (q.AllCoreGhz is { } all) parts.Add(Loc.F("Cpu.TodosCarregadosNGhz", all));
            if (BaseGhz > 0 && best is not null) parts.Add(Loc.F("Cpu.NSobreBase", (best.Ghz / BaseGhz - 1) * 100));
            CoreSummary = string.Join("; ", parts) + ".";
        }
        else CoreSummary = Loc.T("Cpu.CliqueMedirCronometrarCada");

        Latency = q?.Latency.Count > 1 ? q.Latency : null;
        PreviousLatency = _prevQuick?.Latency.Count > 1 ? _prevQuick.Latency : null;
        Steps = q?.CacheSteps;
        CacheComparisons.Clear();
        if (q is not null && q.CacheSteps.Count > 0)
        {
            LatencySummary = string.Join(" · ", q.CacheSteps.Select(s => s.CapacityBytes is { } cap
                ? Loc.F("Cpu.NNNsN", s.Level, s.Ns, s.Cycles, CacheStepDetector.Bytes(cap))
                : Loc.F("Cpu.NNNsN2", s.Level, s.Ns, s.Cycles)));
            var declared = new Dictionary<int, long>();
            foreach (var lv in new[] { 1, 2, 3 })
                if (_identity?.Cache(lv) is { } c) declared[lv] = c.SizeBytes;
            foreach (var c in CacheStepDetector.Compare(q.CacheSteps, declared)) CacheComparisons.Add(c.Text);
            var ram = q.CacheSteps.FirstOrDefault(s => s.Level == "RAM");
            if (ram is not null && _facts?.MemoryModules.Count == 1)
                CacheComparisons.Add(Loc.T("Cpu.So1PenteMemoria2"));
        }
        else LatencySummary = q is null ? "" : Loc.T("Cpu.CurvaNaoMostrouDegraus");

        Matrix = q?.Matrix;
        ClusterText = q?.Matrix is { } m
            ? ClusterInferrer.Infer(m).Explanation + (_topology is { } t && t.L3Clusters().Count > 1 ? Loc.F("Cpu.WindowsDeclaraNBlocos", t.L3Clusters().Count) : "")
            : "";

        var s2 = _sustained;
        Series = s2?.AllCoreSeries.Count > 1 ? s2.AllCoreSeries : q?.AllCoreSeries.Count > 1 ? q.AllCoreSeries : null;
        PreviousSeries = s2 is not null && _prevSustained?.AllCoreSeries.Count > 1 ? _prevSustained.AllCoreSeries : null;
        SustainedText = s2?.Throttle?.Text ?? (q is not null ? Loc.T("Cpu.CurvaCurtaBateriaRapida") : "");
        var run = s2 ?? q;
        ScalingText = run?.Scaling is { } sc
            ? Loc.F("Cpu.EscalaMultiThreadN",
                sc, run.Threads, sc / Math.Max(1, run.Threads) * 100)
            : "";

        Comparison.Clear();
        if (_prevQuick is not null && q is not null)
            foreach (var l in RunComparer.Compare(_prevQuick, q)) Comparison.Add(Loc.F("Cpu.RapidaNNN", _prevQuick.TakenAt.ToLocalTime(), q.TakenAt.ToLocalTime(), l));
        if (_prevSustained is not null && s2 is not null)
            foreach (var l in RunComparer.Compare(_prevSustained, s2)) Comparison.Add(Loc.F("Cpu.SustentadaN", l));
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(HasQuick));
        OnPropertyChanged(nameof(HasSustained));
        NotifyNumbers();
    }

    // ------------------------------------------------------------------ diagnóstico e correções

    private void Diagnose()
    {
        PowerNotice = _power.OnBattery == true
            ? Loc.T("Cpu.NotebookEstaBateriaLigue")
            : _power.HasBattery == true ? Loc.T("Cpu.NotebookMecaSempreTomada") : "";

        var input = new CpuDiagnosisInput
        {
            Power = _power,
            Identity = _identity,
            Topology = _topology,
            Quick = _quick,
            Sustained = _sustained,
            GameModeRead = _facts?.ReadSignals.Contains(DiscoverySignals.GameMode),
            GameModeEnabled = Flag(DiscoverySignals.GameMode),
            VbsConfigured = Flag(DiscoverySignals.VbsEnabled),
            HvciConfigured = Flag(DiscoverySignals.HvciEnabled),
        };
        Findings.Clear();
        foreach (var f in CpuDiagnosis.Evaluate(input))
            Findings.Add(new CpuFindingRow(f, f.ActionId is null ? null : new RelayCommand(() => _ = ApplyAsync(f), () => !Busy && !Measuring)));
    }

    private bool? Flag(string key)
    {
        if (_facts is null || !_facts.ReadSignals.Contains(key)) return null;
        return _facts.RegistrySignals.TryGetValue(key, out var v) ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n != 0 : null : null;
    }

    private async Task ApplyAsync(CpuFinding f)
    {
        if (Host is null || f.ActionId is null) return;
        var level = f.ActionNeedsAdmin ? BrokerLevel.Admin : BrokerLevel.User;
        var text = Loc.F("Cpu.NFoiVistoN", f.Title, f.Evidence, f.ActionLabel ?? f.Title) +
                   Loc.T("Cpu.DepoisCliqueMedirNovo") +
                   (level == BrokerLevel.Admin ? Loc.T("Sec.VaiPedirPermissaoAdministrador") : "");
        await RunAsync(async () =>
        {
            var r = await Host.ApplyAsync(f.ActionLabel ?? f.Title, text, [new ActionRequest(f.ActionId, f.ActionValue)], level);
            if (r is null) return;
            Message = r.Message;
            if (r.Response is { Ok: true, RunId: { } run })
            {
                _lastRun = run;
                _lastLevel = level;
                OnPropertyChanged(nameof(CanUndo));
            }
            _power = await Task.Run(() => CpuPowerReader.Create().Read());
            Diagnose();
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
            if (r.Response?.FullyRestored == true) { _lastRun = null; OnPropertyChanged(nameof(CanUndo)); }
            _power = await Task.Run(() => CpuPowerReader.Create().Read());
            Diagnose();
        });
    }
}

public sealed record CpuFactRow(string Label, string Value, string Source);

public sealed record CpuFeatureChip(string Name, bool Present, string Meaning)
{
    public string Tip => (Present ? Loc.T("Cpu.Presente") : Loc.T("Cpu.Ausente")) + Meaning;
}

public sealed record CpuCacheRow(string Level, string Size, string Ways, string Line, string SharedBy);

public sealed record CpuCoreBar(string Label, string ValueText, double Fraction, bool Best, bool Efficiency, string Tip);

public sealed class CpuFindingRow(CpuFinding finding, RelayCommand? apply)
{
    public CpuFinding Finding { get; } = finding;
    public RelayCommand? ApplyCommand { get; } = apply;
    public bool HasAction => ApplyCommand is not null;
    public bool HasInstruction => !string.IsNullOrEmpty(Finding.Instruction);
    public string Title => Finding.Title;
    public string Evidence => Finding.Evidence;
    public string Explanation => Finding.Explanation;
    public string Source => Loc.T("Gpu.Fonte") + Finding.Source;
    public string Instruction => Finding.Instruction ?? "";
    public string ActionLabel => Finding.ActionLabel ?? Loc.T("Gpu.Corrigir");
    /// <summary>Tom do design system (selo e distintivo).</summary>
    public UiTone ToneKind => Finding.Severity switch
    {
        FindingSeverity.Good => UiTone.Good,
        FindingSeverity.Warn => UiTone.Warn,
        FindingSeverity.Bad => UiTone.Bad,
        _ => UiTone.Info,
    };
    public string IconKey => Finding.Severity switch
    {
        FindingSeverity.Good => "Icon.Check",
        FindingSeverity.Warn => "Icon.Warning",
        FindingSeverity.Bad => "Icon.Error",
        _ => "Icon.Info",
    };
    /// <summary>"good", "info", "warn" ou "bad" (cores do tema).</summary>
    public string Tone => Finding.Severity switch
    {
        FindingSeverity.Good => "good",
        FindingSeverity.Warn => "warn",
        FindingSeverity.Bad => "bad",
        _ => "info",
    };
    public string SeverityText => Finding.Severity switch
    {
        FindingSeverity.Good => Loc.T("Gpu.TudoCerto"),
        FindingSeverity.Warn => Loc.T("Gpu.Atencao"),
        FindingSeverity.Bad => Loc.T("Cpu.SegurandoProcessador"),
        _ => Loc.T("Pg.Saber"),
    };
}
