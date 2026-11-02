using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Nexus.Broker;
using Nexus.GameProfiles.SourceCfg;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;
using Nexus.Lab.Gpu.Windows;
using Nexus.Monitoring;
using Nexus.Monitoring.Windows;
using UiTone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels;

/// <summary>
/// Laboratório da placa de vídeo: descobre o que a GPU é (DXGI + gerenciador de dispositivos + NVML) e por que ela não
/// entrega tudo (PCIe, limites de potência/temperatura, monitor abaixo da taxa, app na integrada, HAGS). Leitura direto
/// daqui; toda correção vai pelo aplicador (Host.ApplyAsync) com diário e Desfazer. A gravação de 60 s só lê sensores.
/// </summary>
public sealed class GpuLabPage : ModulePage
{
    private static CultureInfo Pt => Loc.Culture; // idioma da interface (docs/12-idiomas.md)
    public const int RecordSeconds = 60;
    public const int RecordDelaySeconds = 10;

    private GpuLabReport? _report;
    private NvmlSession? _nvml;
    private string _nvmlStatus = "";
    private bool _nvmlTried;
    private CancellationTokenSource? _recording;

    private string _heroTitle = Loc.T("Gpu.LendoPlacaVideo"), _heroDetail = "", _hybridText = "", _vendorNote = "";
    private string _recordStatus = Loc.T("Gpu.AbraJogoVolteAqui");
    private string _verdict = "", _verdictTone = "info";
    private bool _isRecording, _hasReport, _isHybrid;
    private double _recordProgress;
    private double _chartMaxClock = 2000;
    private IReadOnlyList<GpuTelemetrySample> _samples = [];
    private IReadOnlyList<LimitBand> _bands = [];
    private double _vramBytes = double.NaN, _vramUsedPercent = double.NaN;
    private string _heroCaption = "";

    public GpuLabPage()
    {
        Changes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChanges));
        Findings.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasFindings)); OnPropertyChanged(nameof(ShowNoFindings)); };
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !Busy && !IsRecording);
        RecordCommand = new RelayCommand(() => _ = ToggleRecordingAsync(), () => _hasReport && (!Busy || IsRecording));
        ApplyNvCommand = new RelayCommand(() => _ = ApplyNvAsync(), () => !Busy && NvAvailable && SelectedNvGame is not null && (NvLowLatency || NvMaxPower || NvFpsCap || NvVsync));
        ApplyShaderCacheCommand = new RelayCommand(() => _ = ApplyShaderCacheAsync(), () => !Busy && NvAvailable && SelectedShaderCache is not null);
        ApplyWindowedCommand = new RelayCommand(() => _ = ApplyWindowedAsync(), () => !Busy && SelectedWindowedGame is { IsOn: false });
        ApplyCs2FpsCommand = new RelayCommand(() => _ = ApplyCs2FpsAsync(), () => !Busy && _cs2CfgDir is not null && SelectedCs2Fps is not null);
        foreach (var v in Cs2Install.FpsMaxValues) Cs2FpsOptions.Add(v);
        _selectedCs2Fps = "400";
        foreach (var f in NvidiaDrsActions.FpsCaps) NvFpsOptions.Add(f);
        ShaderCacheOptions.Add(new NvChoice(NvidiaDrsActions.ShaderCacheValues[0], Loc.T("Gpu.N100Gb")));
        ShaderCacheOptions.Add(new NvChoice(NvidiaDrsActions.ShaderCacheValues[1], Loc.T("Gpu.Ilimitado")));
        _selectedShaderCache = ShaderCacheOptions[0];
    }

    public override string Title => Loc.T("Pg.PlacaVideo");
    public override string Group => Loc.T("Nav.Laboratorio");
    public override string Subtitle => Loc.T("Gpu.EngenhariaReversaSuaPlaca");

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RecordCommand { get; }

    public string HeroTitle { get => _heroTitle; private set => Set(ref _heroTitle, value); }
    public string HeroDetail { get => _heroDetail; private set => Set(ref _heroDetail, value); }
    public string HybridText { get => _hybridText; private set => Set(ref _hybridText, value); }
    public string VendorNote { get => _vendorNote; private set => Set(ref _vendorNote, value); }
    public bool HasReport { get => _hasReport; private set { if (Set(ref _hasReport, value)) { OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(ShowNoFindings)); OnPropertyChanged(nameof(HeroFallback)); } } }

    // ------------------------------------------------------------------ números para a apresentação (design system v2)

    /// <summary>Verdadeiro até a primeira leitura terminar (esqueleto no destaque e nos cartões).</summary>
    public bool IsLoading => !HasReport;
    /// <summary>Memória de vídeo da GPU principal (bytes; NaN = sem leitura).</summary>
    public double VramBytes { get => _vramBytes; private set { if (Set(ref _vramBytes, value)) OnPropertyChanged(nameof(HeroFallback)); } }
    /// <summary>% da VRAM em uso agora (NaN = o driver não informa).</summary>
    public double VramUsedPercent { get => _vramUsedPercent; private set { if (Set(ref _vramUsedPercent, value)) OnPropertyChanged(nameof(HasVramUse)); } }
    public bool HasVramUse => double.IsFinite(VramUsedPercent);
    /// <summary>"—" no destaque quando a leitura terminou sem memória de vídeo para mostrar.</summary>
    public string HeroFallback => HasReport && !double.IsFinite(VramBytes) ? "—" : "";
    /// <summary>Uma linha: nome da placa e o papel dela.</summary>
    public string HeroCaption { get => _heroCaption; private set => Set(ref _heroCaption, value); }
    /// <summary>Recursos (HAGS, Resizable BAR, MPO, Modo de Jogo, energia) como selos.</summary>
    public ObservableCollection<GpuCheckRow> Checks { get; } = [];
    public bool HasChanges => Changes.Count > 0;
    public bool HasFindings => Findings.Count > 0;
    /// <summary>Leitura feita e nenhum achado: mostra o selo "Nada a apontar".</summary>
    public bool ShowNoFindings => HasReport && Findings.Count == 0;
    public bool IsHybrid { get => _isHybrid; private set => Set(ref _isHybrid, value); }

    public ObservableCollection<GpuStatCard> Cards { get; } = [];
    public ObservableCollection<GpuAdapterRow> Adapters { get; } = [];
    public ObservableCollection<GpuMonitorRow> Monitors { get; } = [];
    public ObservableCollection<GpuFindingRow> Findings { get; } = [];
    public ObservableCollection<GpuGameRow> Games { get; } = [];
    public ObservableCollection<GpuKeyValue> Readings { get; } = [];
    public ObservableCollection<GpuFindingRow> RecordFindings { get; } = [];
    public ObservableCollection<GpuAppliedChange> Changes { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    // Gravação
    public bool IsRecording { get => _isRecording; private set { if (Set(ref _isRecording, value)) OnPropertyChanged(nameof(RecordButtonText)); } }
    public string RecordButtonText => IsRecording ? Loc.T("Gpu.PararAnalisar") : Loc.T("Gpu.GravarEnquantoJogo60");
    public string RecordStatus { get => _recordStatus; private set => Set(ref _recordStatus, value); }
    public double RecordProgress { get => _recordProgress; private set => Set(ref _recordProgress, value); }
    public IReadOnlyList<GpuTelemetrySample> Samples { get => _samples; private set => Set(ref _samples, value); }
    public IReadOnlyList<LimitBand> Bands { get => _bands; private set => Set(ref _bands, value); }
    public double ChartMaxClock { get => _chartMaxClock; private set => Set(ref _chartMaxClock, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    /// <summary>"good", "warn", "bad" ou "info".</summary>
    public string VerdictTone { get => _verdictTone; private set { if (Set(ref _verdictTone, value)) OnPropertyChanged(nameof(VerdictToneKind)); } }
    public UiTone VerdictToneKind => GpuTones.Of(VerdictTone);

    public override Task OnShownAsync() => _report is null ? LoadAsync() : Task.CompletedTask;

    public override Task OnFactsReadyAsync(SystemFacts facts) => Task.CompletedTask;

    // ------------------------------------------------------------------ leitura

    public Task LoadAsync() => RunAsync(async () =>
    {
        Message = "";
        var report = await Task.Run(() =>
        {
            if (!_nvmlTried)
            {
                _nvmlTried = true;
                try { _nvml = NvmlSession.TryOpen(out _nvmlStatus); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _nvmlStatus = Loc.F("Gpu.NvmlIndisponivelN", ex.Message); }
            }
            return GpuLabScanner.Scan(_nvml, _nvmlStatus, probeD3D12: true);
        });
        _report = report;
        Build(report);
        HasReport = true;
        await ReadCs2Async(report);
        await ReadNvidiaAsync();
    });

    private void Build(GpuLabReport r)
    {
        var primary = r.Primary;
        var hybrid = r.Hybrid;
        IsHybrid = hybrid.IsHybrid;
        HeroTitle = primary is null ? Loc.T("Gpu.NenhumaGpuHardwareEncontrada") : GpuDiagnostics.HeroLine(primary);
        HeroDetail = primary is null ? Loc.T("Gpu.WindowsSoMostraAdaptador") : PrimaryDetail(primary);
        HybridText = hybrid.Description;
        HeroCaption = primary is null ? Loc.T("Gpu.SemDriverFabricante") : primary.Name + (primary.IsIntegrated ? Loc.T("Gpu.Integrada") : Loc.T("Gpu.Dedicada"));
        var vram = primary is null ? 0UL : primary.Nvidia?.VramTotalBytes ?? primary.DedicatedVideoBytes;
        VramBytes = vram > 0 ? vram : double.NaN;
        var vramUsed = primary?.Nvidia?.VramUsedBytes ?? primary?.LocalUsageBytes;
        VramUsedPercent = vram > 0 && vramUsed is { } vu ? Math.Clamp(vu * 100.0 / vram, 0, 100) : double.NaN;

        Adapters.Clear();
        foreach (var a in r.Hardware) Adapters.Add(new GpuAdapterRow(a));

        Cards.Clear();
        if (primary is not null)
        {
            Cards.Add(PcieCard(primary, r.IsLaptop));
            Cards.Add(LimitsCard(primary));
            Cards.Add(MemoryCard(primary));
            Cards.Add(MonitorCard(r));
        }
        Checks.Clear();
        if (primary is not null) foreach (var c in FeatureChecks(r, primary)) Checks.Add(c);

        Readings.Clear();
        if (primary?.Nvidia is { } nv) foreach (var kv in NvidiaRows(nv)) Readings.Add(kv);
        VendorNote = primary switch
        {
            null => "",
            { Nvidia: not null } => Loc.F("Gpu.FonteClocksLimitesMotivos", r.NvmlStatus),
            { Vendor: GpuVendor.Nvidia } => Loc.F("Gpu.PlacaNvidiaMasNvml", r.NvmlStatus),
            { Vendor: GpuVendor.Amd } => Loc.T("Gpu.AmdClocksPotenciaMotivos"),
            { Vendor: GpuVendor.Intel } => Loc.T("Gpu.IntelClocksPotenciaSo"),
            _ => "",
        };

        Monitors.Clear();
        var findings = GpuDiagnostics.Evaluate(r, DateOnly.FromDateTime(DateTime.Now));
        foreach (var m in r.Monitors)
            Monitors.Add(new GpuMonitorRow(m, findings.FirstOrDefault(f => f.Fix == GpuFixKind.DisplayMode && f.FixArguments?["device"] == m.Device), this));

        Findings.Clear();
        foreach (var f in findings.Where(f => f.Fix != GpuFixKind.AppGpuPreference).OrderBy(f => Rank(f.Level)))
            Findings.Add(new GpuFindingRow(f, this));

        Games.Clear();
        var prefs = r.AppPreferences.ToDictionary(p => p.ExePath, p => p.GpuPreference, StringComparer.OrdinalIgnoreCase);
        foreach (var g in r.Games)
        {
            int? pref = g.ExePath is not null && prefs.TryGetValue(g.ExePath, out var p) ? p : null;
            var fix = findings.FirstOrDefault(f => f.Fix == GpuFixKind.AppGpuPreference && f.FixArguments?["exePath"] == g.ExePath);
            Games.Add(new GpuGameRow(g, pref, fix, this));
        }

        var selectedExe = SelectedWindowedGame?.ExePath;
        WindowedGames.Clear();
        foreach (var g in r.Games.Where(g => GameLibraries.IsValidExePath(g.ExePath)))
        {
            var raw = r.AppPreferences.FirstOrDefault(p => string.Equals(p.ExePath, g.ExePath, StringComparison.OrdinalIgnoreCase))?.RawValue;
            WindowedGames.Add(new WindowedGameOption(g.Name, g.ExePath!, GameLibraries.ParseToken(raw, "SwapEffectUpgradeEnable"), g.ExePathSure));
        }
        OnPropertyChanged(nameof(HasWindowedGames));
        SelectedWindowedGame = WindowedGames.FirstOrDefault(w => string.Equals(w.ExePath, selectedExe, StringComparison.OrdinalIgnoreCase))
                               ?? WindowedGames.FirstOrDefault(w => !w.IsOn) ?? WindowedGames.FirstOrDefault();

        Notes.Clear();
        foreach (var n in r.Notes) Notes.Add(n);
        if (primary?.Nvidia?.MaxGraphicsClockMhz is { } mx) ChartMaxClock = mx;
    }

    private static int Rank(string level) => level switch { "bad" => 0, "warn" => 1, "info" => 2, _ => 3 };

    private static string PrimaryDetail(GpuAdapter a)
    {
        var parts = new List<string>();
        if (a.Architecture != GpuArchitecture.Unknown) parts.Add($"{a.Architecture.Family} ({a.Architecture.Year})");
        parts.Add(a.IsIntegrated ? Loc.T("Gpu.IntegradaProcessador") : Loc.T("Drv.Dedicada"));
        if (a.IsHighPerformance) parts.Add(Loc.T("Gpu.GpuAltoDesempenhoWindows"));
        parts.Add(Loc.F("Gpu.PciNN", a.VendorId, a.DeviceId));
        var v = a.DriverInfoDecoded;
        if (!string.IsNullOrEmpty(v.WindowsVersion)) parts.Add(Loc.F("Gpu.DriverNWindows", v.WindowsVersion));
        return string.Join(" · ", parts);
    }

    private static GpuStatCard PcieCard(GpuAdapter a, bool laptop)
    {
        if (a.IsIntegrated) return new(Loc.T("Gpu.Barramento"), Loc.T("Gpu.Integrada2"), Loc.T("Gpu.FicaDentroProcessadorNao"), "info", "Icon.Hardware");
        var (link, note) = HardwareCaps.PcieWithGpuCeiling(a);
        var (level, text) = Pcie.IdleVerdict(link, false, laptop);
        if (!link.HasData) return new(Loc.T("Gpu.BarramentoPcie"), "—", text, "info", "Icon.Hardware");
        var detail = $"GPU: {link.Describe(current: false)}" + (link.PortMaxGen is { } pg ? Loc.F("Gpu.PortaPcieNX", Pcie.GenLabel(pg), link.PortMaxWidth?.ToString(CultureInfo.InvariantCulture) ?? "?") : "") + ". " + text +
                     (note is null ? "" : " " + note);
        return new(Loc.T("Gpu.BarramentoPcieAgora"), link.Describe(), detail, level, "Icon.Hardware");
    }

    private static GpuStatCard LimitsCard(GpuAdapter a)
    {
        if (a.Nvidia is not { } nv) return new(Loc.T("Gpu.Limites"), Loc.T("Gpu.SemLeitura"), Loc.T("Gpu.DriverDestaPlacaNao"), "info", "Icon.Bolt");
        var value = nv.EnforcedPowerLimitWatts ?? nv.PowerLimitWatts;
        var parts = new List<string>();
        if (nv.DefaultPowerLimitWatts is { } d) parts.Add(Loc.F("Gpu.PadraoNW", d));
        if (nv.MinPowerLimitWatts is { } lo && nv.MaxPowerLimitWatts is { } hi && hi > 0) parts.Add(Loc.F("Gpu.FaixaNNW", lo, hi));
        if (nv.SlowdownTemperatureC is { } t) parts.Add(Loc.F("Gpu.ReduzClockNC", t));
        if (nv.MaxGraphicsClockMhz is { } c) parts.Add(Loc.F("Gpu.ClockMaxNMhz", c));
        return new(Loc.T("Gpu.LimitePotencia"), value is { } v ? $"{v:0} W" : "—", string.Join(" · ", parts), "info", "Icon.Bolt");
    }

    private static GpuStatCard MemoryCard(GpuAdapter a)
    {
        var total = a.Nvidia?.VramTotalBytes ?? a.DedicatedVideoBytes;
        var parts = new List<string>();
        if (a.LocalBudgetBytes is { } b) parts.Add(Loc.F("Gpu.OrcamentoWindowsN", GpuDiagnostics.FormatGb(b)));
        if ((a.Nvidia?.VramUsedBytes ?? a.LocalUsageBytes) is { } u) parts.Add(Loc.F("Gpu.UsoN", GpuDiagnostics.FormatGb(u)));
        if (a.SharedSystemBytes > 0) parts.Add(Loc.F("Gpu.PodeEmprestarAteN", GpuDiagnostics.FormatGb(a.SharedSystemBytes)));
        return new(a.IsIntegrated ? Loc.T("Gpu.MemoriaReservadaRam") : Loc.T("Gpu.MemoriaVideo"), total > 0 ? GpuDiagnostics.FormatGb(total) : "—", string.Join(" · ", parts), "info", "Icon.Ram");
    }

    private static GpuStatCard MonitorCard(GpuLabReport r)
    {
        var m = r.Monitors.FirstOrDefault(x => x.Primary) ?? (r.Monitors.Count > 0 ? r.Monitors[0] : null);
        if (m is null) return new("Monitor", "—", Loc.T("Gpu.NenhumMonitorLido"), "info", "Icon.Monitor");
        var best = m.Advice.BelowMaxRefresh ? m.Advice.Best : null;
        return new(Loc.T("Gpu.MonitorPrincipal"), $"{m.Current.Hz} Hz",
            best is not null ? Loc.F("Gpu.AceitaNHzNesta", best.Hz) : Loc.T("Gpu.JaMaiorTaxaDesta"), best is not null ? "warn" : "good", "Icon.Monitor",
            best is not null ? Loc.F("Gpu.PodeNHz", best.Hz) : Loc.T("Gpu.Maximo"));
    }

    /// <summary>Recursos da placa como selos (o mesmo conteúdo do antigo cartão "Recursos": HAGS, Resizable BAR, MPO, Modo de Jogo, energia).</summary>
    private static IEnumerable<GpuCheckRow> FeatureChecks(GpuLabReport r, GpuAdapter a)
    {
        var hags = a.HagsEnabled ?? (r.Settings.HwSchMode == 2 ? true : r.Settings.HwSchMode == 1 ? false : null);
        yield return new(Loc.T("Gpu.AgendamentoHardwareHags"), hags is null ? Loc.T("Gpu.NaoInformado") : hags.Value ? Loc.T("Gpu.Ligado") : Loc.T("Gpu.Desligado"),
            hags == true ? "good" : "info", a.HagsEnabled is null ? Loc.T("Gpu.LidoRegistroWindows") : Loc.T("Gpu.InformadoPeloDriver"));
        if (!a.IsIntegrated)
        {
            var rb = HardwareCaps.ResizableBar(a);
            yield return rb.Enabled is { } on
                ? new(Loc.T("Gpu.ResizableBar"), on ? Loc.T("Gpu.Ligado") : Loc.T("Gpu.Desligado"), on ? "good" : HardwareCaps.IsIntelArcDiscrete(a) ? "warn" : "info", Loc.F("Gpu.NFonteN", rb.Evidence, rb.Source))
                : new(Loc.T("Gpu.ResizableBar"), Loc.T("Gpu.NaoAfirmar"), HardwareCaps.IsIntelArcDiscrete(a) ? "warn" : "info", rb.Evidence);
        }
        yield return new(Loc.T("Gpu.MpoPlanosSobreposicao"), r.Settings.OverlayTestMode == 5 ? Loc.T("Gpu.DesligadoRegistro") : Loc.T("Gpu.PadraoWindows"),
            r.Settings.OverlayTestMode == 5 ? "warn" : "good", Loc.T("Gpu.DesligarMpoMaoSo"));
        yield return new(Loc.T("Gpu.ModoJogo"), r.Settings.AutoGameModeEnabled == 0 ? Loc.T("Gpu.Desligado") : Loc.T("Gpu.Ligado"), r.Settings.AutoGameModeEnabled == 0 ? "warn" : "good",
            Loc.T("Gpu.PrioridadeJogoPrimeiroPlano"));
        if (r.Settings.OnBattery == true) yield return new(Loc.T("Gpu.Energia"), Loc.T("Gpu.Bateria"), "warn", Loc.T("Gpu.BateriaGpuLimitadaPelo"));
    }

    private static IEnumerable<GpuKeyValue> NvidiaRows(NvidiaReading n)
    {
        static string F(double? v, string unit, string fmt = "0") => v is { } x ? x.ToString(fmt, Pt) + unit : "—";
        yield return new(Loc.T("Gpu.ClockGrafico"), Loc.F("Gpu.NAgora", F(n.GraphicsClockMhz, " MHz")), Loc.F("Gpu.MaximoN", F(n.MaxGraphicsClockMhz, " MHz")));
        yield return new(Loc.T("Gpu.ClockMemoria"), Loc.F("Gpu.NAgora", F(n.MemoryClockMhz, " MHz")), Loc.F("Gpu.MaximoN", F(n.MaxMemoryClockMhz, " MHz")));
        yield return new(Loc.T("Gpu.Potencia2"), Loc.F("Gpu.NAgora", F(n.PowerWatts, " W", "0.0")),
            Loc.F("Gpu.LimiteNPadraoN", F(n.EnforcedPowerLimitWatts ?? n.PowerLimitWatts, " W"), F(n.DefaultPowerLimitWatts, " W"), F(n.MinPowerLimitWatts, ""), F(n.MaxPowerLimitWatts, " W")));
        yield return new(Loc.T("Gpu.Temperatura2"), Loc.F("Gpu.NAgora", F(n.TemperatureC, " °C")),
            Loc.F("Gpu.ReduzClockNDesliga", F(n.SlowdownTemperatureC, " °C"), F(n.ShutdownTemperatureC, " °C")) + (n.MaxOperatingTemperatureC is { } mo ? Loc.F("Gpu.AlvoMaxNC", mo) : ""));
        yield return new(Loc.T("Gpu.Ventoinha"), n.FanPercent is { } fan ? $"{fan:0}%" : "—", n.FanPercent is null ? Loc.T("Gpu.NotebookVentoinhaFabricanteNotebook") : "");
        yield return new(Loc.T("Gpu.Uso2"), Loc.F("Gpu.NGpuNMemoria", F(n.UtilizationPercent, "%"), F(n.MemoryUtilizationPercent, "%")), n.PerformanceState is { } ps ? Loc.F("Gpu.EstadoPNP0", ps) : "");
        yield return new(Loc.T("Gpu.PciePelaPlaca"), n.PcieGen is { } g ? Loc.F("Gpu.GenNXN", g, n.PcieWidth) : "—", n.PcieMaxGen is { } mg ? Loc.F("Gpu.MaximoGenNX", mg, n.PcieMaxWidth) : "");
        var reasons = n.ReasonsMask is { } m ? ThrottleReasons.Describe(m) : [];
        yield return new(Loc.T("Gpu.LimitandoAgora"), reasons.Count == 0 ? (n.ReasonsMask is null ? "—" : Loc.T("Gpu.Nada")) : reasons[0], reasons.Count > 1 ? string.Join("; ", reasons.Skip(1)) : Loc.T("Gpu.RepousoNormalAparecerOciosa"));
        yield return new("VRAM", n.VramTotalBytes is { } t ? $"{GpuDiagnostics.FormatGb(n.VramUsedBytes ?? 0)} de {GpuDiagnostics.FormatGb(t)}" : "—",
            n.Bar1TotalBytes is { } b ? Loc.F("Gpu.JanelaBar1NMb", b >> 20) : "");
    }

    // ------------------------------------------------------------------ correções (sempre pelo aplicador)

    internal Task FixAsync(GpuFinding f) => RunAsync(async () =>
    {
        if (Host is null) return;
        var args = f.FixArguments;
        switch (f.Fix)
        {
            case GpuFixKind.DisplayMode when args is not null:
            {
                var mode = DisplayMode.TryParse(args["mode"]);
                var text = Loc.F("Gpu.TrocarNNMesma", args["device"], mode?.Label) +
                           Loc.T("Gpu.DriverConsultadoAntesTeste") +
                           Loc.F("Gpu.DepoisTrocarNexusPergunta", KeepChangeCountdown.DefaultSeconds) +
                           Loc.T("Gpu.VoltaSozinhoModoAntes");
                var label = Loc.F("Gpu.NNHz", args["device"], mode?.Hz);
                await ApplyAsync(f.Title, text, new ActionRequest(GpuDiagnostics.DisplayModeAction, null, Copy(args)), BrokerLevel.User, label,
                    change => StartKeepCountdown(change, mode is null ? Loc.T("Gpu.EstaTaxa") : $"{mode.Hz} Hz"));
                break;
            }
            case GpuFixKind.AppGpuPreference when args is not null:
            {
                var text = Loc.F("Gpu.MarcarAltoDesempenhoN", args["exePath"]) +
                           Loc.T("Gpu.MesmoAjusteConfiguracoesTela");
                await ApplyAsync(Loc.T("Gpu.UsarGpuDedicada"), text, new ActionRequest(GpuDiagnostics.AppPreferenceAction, null, Copy(args)), BrokerLevel.User,
                    Loc.F("Gpu.AltoDesempenhoN", System.IO.Path.GetFileName(args["exePath"])));
                break;
            }
            case GpuFixKind.GameMode:
                await ApplyAsync(Loc.T("Gpu.LigarModoJogo"), Loc.T("Gpu.LigarModoJogoWindows"),
                    new ActionRequest(GpuDiagnostics.GameModeAction), BrokerLevel.User, Loc.T("Shell.ModoJogoLigado"));
                break;
            case GpuFixKind.Hags:
                await ApplyAsync(Loc.T("Gpu.LigarHags"), Loc.T("Gpu.LigarAgendamentoGpuAcelerado") +
                    Loc.T("Gpu.ExigeAdministradorWindowsVai"),
                    new ActionRequest(GpuDiagnostics.HagsAction, "2"), BrokerLevel.Admin, Loc.T("Gpu.HagsLigadoReinicieValer"));
                break;
            case GpuFixKind.OpenLink when args is not null && args.TryGetValue("url", out var url):
                OpenOfficialPage(url);
                break;
        }
    });

    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> a) => a.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private void OpenOfficialPage(string url)
    {
        // Só as páginas oficiais fixas da biblioteca; o NEXUS nunca baixa nada.
        var allowed = new[] { GpuVendor.Nvidia, GpuVendor.Amd, GpuVendor.Intel }.Select(DriverInfo.OfficialDownloadPage).ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains(url) || Host is null) return;
        if (!Host.Confirm(Loc.T("Gpu.AbrirSiteOficial"), Loc.F("Gpu.AbrirNavegadorPaginaOficial", url))) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = Loc.F("Asst.NaoFoiPossivelAbrir", ex.Message); }
    }

    private async Task ApplyAsync(string title, string text, ActionRequest request, BrokerLevel level, string change, Action<GpuAppliedChange>? applied = null)
    {
        var r = await Host!.ApplyAsync(title, text, [request], level);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run })
        {
            var c = new GpuAppliedChange(change, run, level, this);
            Changes.Insert(0, c);
            applied?.Invoke(c);
        }
        Message = StartupPage.Describe(r);
        _report = null;
        var report = await Task.Run(() => GpuLabScanner.Scan(_nvml, _nvmlStatus, probeD3D12: true));
        _report = report;
        Build(report);
        await ReadCs2Async(report);
    }

    internal Task UndoAsync(GpuAppliedChange c) => RunAsync(async () =>
    {
        if (Host is null) return;
        if (ReferenceEquals(c, _keepChange) && _keep is { Finished: false } && _keep.Keep()) { _keepTimer?.Stop(); RaiseKeep(); } // Desfazer manual vence a contagem
        var r = await Host.RevertAsync(c.RunId, c.Level);
        if (r is null) return;
        if (r.Response?.FullyRestored == true) Changes.Remove(c);
        Message = r.Message;
        var report = await Task.Run(() => GpuLabScanner.Scan(_nvml, _nvmlStatus, probeD3D12: true));
        _report = report;
        Build(report);
        await ReadCs2Async(report);
        await ReadNvidiaAsync();
    });

    // ------------------------------------------------------------------ "Manter esta taxa?" (volta sozinho, como no Windows)

    private KeepChangeCountdown? _keep;
    private GpuAppliedChange? _keepChange;
    private string _keepWhat = "";
    private long _keepLastTick;
    private System.Windows.Threading.DispatcherTimer? _keepTimer;
    private RelayCommand? _keepModeCommand, _revertModeNowCommand;

    /// <summary>A pergunta está na tela (contando).</summary>
    public bool KeepModeAsking => _keep is { Finished: false };
    /// <summary>"Manter esta taxa? Volta sozinho em 15 s."</summary>
    public string KeepModePrompt => _keep?.Prompt(_keepWhat) ?? "";
    /// <summary>Linha de apoio: o que mudou e o que acontece sem resposta.</summary>
    public string KeepModeDetail => _keepChange is null ? "" : Loc.F("Gpu.AgoraNTelaFicou", _keepChange.Text);
    /// <summary>Fração do tempo que resta (1 → 0), para a barra.</summary>
    public double KeepModeFraction => _keep?.Fraction ?? 0;
    public RelayCommand KeepModeCommand => _keepModeCommand ??= new RelayCommand(KeepMode, () => KeepModeAsking);
    public RelayCommand RevertModeNowCommand => _revertModeNowCommand ??= new RelayCommand(() => { if (_keep?.RevertNow() == true) FinishKeep(); }, () => KeepModeAsking);

    private void StartKeepCountdown(GpuAppliedChange change, string what)
    {
        _keep = new KeepChangeCountdown();
        _keepChange = change;
        _keepWhat = what;
        _keepLastTick = Stopwatch.GetTimestamp();
        if (_keepTimer is null)
        {
            _keepTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
            _keepTimer.Tick += (_, _) => OnKeepTick();
        }
        _keepTimer.Start();
        RaiseKeep();
        Host?.Notify(Loc.F("Gpu.MonitorTrocadoNVolta", what, KeepChangeCountdown.DefaultSeconds));
    }

    private void OnKeepTick()
    {
        if (_keep is null) { _keepTimer?.Stop(); return; }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_keepLastTick, now);
        _keepLastTick = now;
        if (_keep.Tick(elapsed)) { FinishKeep(); return; }
        RaiseKeep();
    }

    private void KeepMode()
    {
        if (_keep?.Keep() != true) return;
        _keepTimer?.Stop();
        Message = Loc.F("Gpu.MantidoNVoltarDepois", _keepChange?.Text);
        RaiseKeep();
    }

    /// <summary>"Voltar agora" ou tempo esgotado: desfaz pelo aplicador sem outra pergunta (o usuário já aceitou ao trocar).</summary>
    private void FinishKeep()
    {
        _keepTimer?.Stop();
        RaiseKeep();
        if (_keep is not { ShouldRevert: true } || _keepChange is not { } change) return;
        var timedOut = _keep.State == KeepChangeState.TimedOut;
        _ = AutoRevertAsync(change, timedOut);
    }

    private async Task AutoRevertAsync(GpuAppliedChange change, bool timedOut)
    {
        if (Host is null) return;
        try
        {
            var r = await Host.RevertWithoutConfirmationAsync(change.RunId, change.Level);
            if (r is null) return;
            if (r.Response?.FullyRestored == true) Changes.Remove(change);
            Message = (timedOut ? Loc.F("Gpu.SemRespostaNS", KeepChangeCountdown.DefaultSeconds) : Loc.T("Gpu.MonitorVoltouModoAnterior")) + r.Message;
            var report = await Task.Run(() => GpuLabScanner.Scan(_nvml, _nvmlStatus, probeD3D12: true));
            _report = report;
            Build(report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = Loc.F("Gpu.NaoDeuVoltarModo", ex.Message);
        }
    }

    private void RaiseKeep()
    {
        OnPropertyChanged(nameof(KeepModeAsking));
        OnPropertyChanged(nameof(KeepModePrompt));
        OnPropertyChanged(nameof(KeepModeDetail));
        OnPropertyChanged(nameof(KeepModeFraction));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ por jogo, sem driver: janela (DX10/11) e fps_max do CS2

    public const string WindowedPerAppAction = "win.windowed-game-optimizations.per-app@1";
    private WindowedGameOption? _selectedWindowedGame;
    private string? _cs2CfgDir, _selectedCs2Fps;
    private string _cs2Status = Loc.T("Gpu.ProcurandoCounterStrike2");
    private bool _cs2Found;

    public RelayCommand ApplyWindowedCommand { get; }
    public RelayCommand ApplyCs2FpsCommand { get; }
    /// <summary>Jogos com executável conhecido (Steam/Epic) e o estado de "Otimizações para jogos em janela" de cada um.</summary>
    public ObservableCollection<WindowedGameOption> WindowedGames { get; } = [];
    public bool HasWindowedGames => WindowedGames.Count > 0;
    public WindowedGameOption? SelectedWindowedGame
    {
        get => _selectedWindowedGame;
        set { if (Set(ref _selectedWindowedGame, value)) OnPropertyChanged(nameof(WindowedState)); }
    }
    public string WindowedState => SelectedWindowedGame?.StateText ?? "";
    public bool Cs2Found { get => _cs2Found; private set => Set(ref _cs2Found, value); }
    /// <summary>Onde está o CS2, o fps_max atual e se há o dump do cvarlist que o aplicador exige.</summary>
    public string Cs2Status { get => _cs2Status; private set => Set(ref _cs2Status, value); }
    public ObservableCollection<string> Cs2FpsOptions { get; } = [];
    public string? SelectedCs2Fps { get => _selectedCs2Fps; set => Set(ref _selectedCs2Fps, value); }

    /// <summary>CS2 instalado (manifesto Steam): pasta cfg, fps_max atual e dump do cvarlist. Só leitura.</summary>
    private async Task ReadCs2Async(GpuLabReport r)
    {
        var cfg = r.Games.Where(g => g.Store == "Steam").Select(g => Cs2Install.CfgDirFromInstallDir(g.InstallDir)).FirstOrDefault(c => c is not null);
        _cs2CfgDir = cfg;
        Cs2Found = cfg is not null;
        if (cfg is null)
        {
            Cs2Status = Loc.T("Gpu.CounterStrike2Nao");
            return;
        }
        var (current, dump) = await Task.Run(() => (Cs2Install.ReadFpsMax(cfg), Cs2Install.ReadDump(cfg)));
        Cs2Status = Loc.F("Gpu.Cs2NFpsMax", cfg, current ?? Loc.T("Gpu.NaoDefinidoJogoUsa")) +
                    (dump is null
                        ? Loc.T("Gpu.FaltaListaComandosDesta")
                        : Loc.F("Gpu.ListaComandosDestaVersao", dump.Count, dump.SourceDescription));
        if (current is not null && Cs2FpsOptions.Contains(current)) SelectedCs2Fps = current;
    }

    private Task ApplyWindowedAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedWindowedGame is not { } g) return;
        var exe = g.ExePath;
        var text = Loc.F("Gpu.LigarOtimizacoesJogosJanela", exe) +
                   Loc.T("Gpu.WindowsPassaApresentarEste") +
                   Loc.T("Gpu.SoValeJogosDirectx") +
                   Loc.T("Gpu.MesmaOpcaoConfiguracoesSistema");
        await ApplyAsync(Loc.T("Gpu.OtimizacoesJogosJanelaNeste"), text,
            new ActionRequest(WindowedPerAppAction, null, new Dictionary<string, string> { ["gameExePath"] = exe }), BrokerLevel.User,
            Loc.F("Gpu.JanelaOtimizadaN", System.IO.Path.GetFileName(exe)));
    });

    private Task ApplyCs2FpsAsync() => RunAsync(async () =>
    {
        if (Host is null || _cs2CfgDir is not { } cfg || SelectedCs2Fps is not { } fps) return;
        var text = Loc.F("Gpu.GravarFpsMaxN", fps, cfg) +
                   (fps == "0" ? Loc.T("Gpu.N0TiraLimiteJogo") : Loc.T("Gpu.JogoNaoPassaDesse")) +
                   Loc.T("Gpu.SoLinhaFpsMax") +
                   Loc.T("Gpu.SteamCloudPodeSobrescrever");
        await ApplyAsync(Loc.T("Gpu.Cs2LimiteFps"), text,
            new ActionRequest(Cs2Install.FpsMaxActionKey, fps, new Dictionary<string, string> { ["cs2CfgDir"] = cfg }), BrokerLevel.User, Loc.F("Gpu.Cs2FpsMaxN", fps));
    });

    // ------------------------------------------------------------------ driver NVIDIA por jogo (perfis DRS)

    // Só leitura aqui (INvidiaDrsReader); a gravação é do handler NvidiaDrsSetting no aplicador.
    private readonly INvidiaDrsReader _drs = NvidiaDrsFactory.Reader();
    private bool _nvAvailable, _nvLowLatency, _nvMaxPower, _nvFpsCap, _nvVsync;
    private string _nvStatus = Loc.T("Gpu.LendoDriverNvidia"), _shaderCacheText = "", _nvFpsHint = "";
    private NvGameOption? _selectedNvGame;
    private string? _selectedNvFps;
    private NvChoice? _selectedShaderCache;

    public RelayCommand ApplyNvCommand { get; }
    public RelayCommand ApplyShaderCacheCommand { get; }
    /// <summary>NVAPI abriu (placa e driver NVIDIA ativos).</summary>
    public bool NvAvailable { get => _nvAvailable; private set => Set(ref _nvAvailable, value); }
    /// <summary>"Sem placa/driver NVIDIA…" ou o resumo do que foi lido.</summary>
    public string NvStatus { get => _nvStatus; private set => Set(ref _nvStatus, value); }
    public ObservableCollection<NvGameOption> NvGames { get; } = [];
    public bool HasNvGames => NvGames.Count > 0;
    public NvGameOption? SelectedNvGame
    {
        get => _selectedNvGame;
        set { if (Set(ref _selectedNvGame, value)) _ = ReadNvGameAsync(); }
    }
    public bool NvLowLatency { get => _nvLowLatency; set => Set(ref _nvLowLatency, value); }
    public bool NvMaxPower { get => _nvMaxPower; set => Set(ref _nvMaxPower, value); }
    public bool NvFpsCap { get => _nvFpsCap; set => Set(ref _nvFpsCap, value); }
    public bool NvVsync { get => _nvVsync; set => Set(ref _nvVsync, value); }
    public ObservableCollection<string> NvFpsOptions { get; } = [];
    public string? SelectedNvFps { get => _selectedNvFps; set => Set(ref _selectedNvFps, value); }
    /// <summary>"Monitor a 144 Hz: sugestão 141 FPS (3 abaixo, para o G-SYNC)".</summary>
    public string NvFpsHint { get => _nvFpsHint; private set => Set(ref _nvFpsHint, value); }
    /// <summary>Valores atuais do perfil do jogo escolhido.</summary>
    public ObservableCollection<GpuKeyValue> NvCurrent { get; } = [];
    public string ShaderCacheText { get => _shaderCacheText; private set => Set(ref _shaderCacheText, value); }
    public ObservableCollection<NvChoice> ShaderCacheOptions { get; } = [];
    public NvChoice? SelectedShaderCache { get => _selectedShaderCache; set => Set(ref _selectedShaderCache, value); }

    /// <summary>Disponibilidade, cache de sombreador (global), jogos com executável e a sugestão de limite pelo monitor.</summary>
    private async Task ReadNvidiaAsync()
    {
        var (unavailable, cache) = await Task.Run(() =>
        {
            try
            {
                var u = _drs.Unavailable();
                return (u, u is null ? DescribeSetting(DrsTarget.Base, NvidiaDrsSettings.ShaderCacheMaxSize) : "");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ((string?)Loc.F("Gpu.SemPlacaDriverNvidia", ex.Message), ""); }
        });
        NvAvailable = unavailable is null;
        NvStatus = unavailable ?? Loc.T("Gpu.DriverNvidiaEncontradoAjustes");
        ShaderCacheText = cache;

        var previous = SelectedNvGame?.Exe;
        NvGames.Clear();
        foreach (var g in _report?.Games ?? [])
        {
            var exe = g.ExePath is null ? null : System.IO.Path.GetFileName(g.ExePath);
            if (NvidiaDrsSettings.IsValidExe(exe) && NvGames.All(x => !string.Equals(x.Exe, exe, StringComparison.OrdinalIgnoreCase)))
                NvGames.Add(new NvGameOption(g.Name, exe!, g.ExePathSure));
        }
        OnPropertyChanged(nameof(HasNvGames));
        var monitors = _report?.Monitors ?? [];
        var monitor = monitors.FirstOrDefault(m => m.Primary) ?? (monitors.Count > 0 ? monitors[0] : null);
        if (monitor is not null && NvidiaDrsSettings.NearestAllowedCap(monitor.Current.Hz, NvidiaDrsActions.FpsCaps) is { } cap)
        {
            SelectedNvFps ??= cap;
            NvFpsHint = Loc.F("Gpu.MonitorPrincipalNHz", monitor.Current.Hz, cap);
        }
        else
        {
            SelectedNvFps ??= "60";
            NvFpsHint = Loc.T("Gpu.GSyncUsePelo");
        }
        if (NvAvailable && NvGames.Count > 0)
            SelectedNvGame = NvGames.FirstOrDefault(x => x.Exe == previous) ?? NvGames[0];
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private string DescribeSetting(DrsTarget target, uint id)
    {
        var spec = NvidiaDrsSettings.All[id];
        try
        {
            var r = _drs.Read(target, id);
            if (!r.ProfileFound) return Loc.T("Gpu.PadraoDriverJogoNao");
            if (!r.InProfile || r.Value is not { } v) return Loc.T("Gpu.PadraoDriver");
            return spec.Describe(v) + (r.IsPredefined ? Loc.T("Gpu.DefinidoPelaNvidia") : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Loc.T("Gpu.SemLeitura2") + ex.Message; }
    }

    private async Task ReadNvGameAsync()
    {
        var game = SelectedNvGame;
        NvCurrent.Clear();
        if (game is null || !NvAvailable) return;
        var rows = await Task.Run(() =>
        {
            var target = DrsTarget.App(game.Exe);
            var list = new List<GpuKeyValue>();
            try
            {
                var profile = _drs.Read(target, NvidiaDrsSettings.PrerenderLimit);
                list.Add(new(Loc.T("Gpu.PerfilDriver"), profile.ProfileFound ? profile.ProfileName ?? "?" : Loc.T("Gpu.NenhumNexusCriaNexus") + game.Exe + "\")", ""));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { list.Add(new(Loc.T("Gpu.PerfilDriver"), Loc.T("Gpu.SemLeitura"), ex.Message)); return list; }
            foreach (var id in new[] { NvidiaDrsSettings.PrerenderLimit, NvidiaDrsSettings.PreferredPstate, NvidiaDrsSettings.FrameRateLimit, NvidiaDrsSettings.VsyncMode })
                list.Add(new(NvidiaDrsSettings.All[id].Name, DescribeSetting(target, id), ""));
            return list;
        });
        if (!ReferenceEquals(game, SelectedNvGame)) return;
        foreach (var r in rows) NvCurrent.Add(r);
    }

    private Task ApplyNvAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedNvGame is not { } game) return;
        var exe = game.Exe;
        var requests = new List<ActionRequest>();
        var lines = new List<string>();
        if (NvLowLatency)
        {
            requests.Add(new ActionRequest(NvidiaDrsActions.LowLatency, null, new Dictionary<string, string> { ["exe"] = exe }));
            lines.Add(Loc.T("Gpu.ModoBaixaLatenciaLigado2"));
        }
        if (NvMaxPower)
        {
            requests.Add(new ActionRequest(NvidiaDrsActions.MaxPower, null, new Dictionary<string, string> { ["exe"] = exe }));
            lines.Add(Loc.T("Gpu.EnergiaPreferirDesempenhoMaximo"));
        }
        var fps = SelectedNvFps;
        if (NvFpsCap)
        {
            if (fps is null || !NvidiaDrsActions.FpsCaps.Contains(fps)) { Message = Loc.T("Gpu.EscolhaLimiteFpsLista"); return; }
            requests.Add(new ActionRequest(NvidiaDrsActions.FpsCap, fps, new Dictionary<string, string> { ["exe"] = exe }));
            lines.Add(Loc.F("Gpu.TaxaMaximaQuadrosN", fps));
        }
        if (NvVsync)
        {
            var capInProfile = NvCurrent.Any(r => r.Label == NvidiaDrsSettings.All[NvidiaDrsSettings.FrameRateLimit].Name && r.Value.EndsWith(" FPS", StringComparison.Ordinal));
            if (!NvFpsCap && !capInProfile)
            {
                Message = Loc.T("Gpu.VSyncDriverSo");
                return;
            }
            requests.Add(new ActionRequest(NvidiaDrsActions.VsyncOn, null, new Dictionary<string, string> { ["exe"] = exe }));
            lines.Add(Loc.T("Gpu.VSyncLigadoDriver2"));
        }
        if (requests.Count == 0) return;
        var text = Loc.F("Gpu.GravarPerfilDriverNvidia", game.Name, exe, string.Join("\n", lines)) +
                   Loc.T("Gpu.MesmoBancoPerfisPainel") +
                   Loc.T("Gpu.ExigeAdministradorWindowsPede");
        var r = await Host.ApplyAsync(Loc.T("Gpu.AplicarDriverNvidia"), text, requests, BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run }) Changes.Insert(0, new GpuAppliedChange(Loc.F("Gpu.DriverNvidiaNN", exe, requests.Count), run, BrokerLevel.Admin, this));
        Message = StartupPage.Describe(r);
        await ReadNvGameAsync();
    });

    private Task ApplyShaderCacheAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedShaderCache is not { } choice) return;
        var text = Loc.F("Gpu.MudarTamanhoCacheSombreador", choice.Label) +
                   Loc.T("Gpu.DriverGuardaSombreadoresJa") +
                   Loc.T("Gpu.NaoAumentaFpsPasta");
        var r = await Host.ApplyAsync(Loc.T("Gpu.CacheSombreadorNvidia"), text, [new ActionRequest(NvidiaDrsActions.ShaderCache, choice.Value)], BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run }) Changes.Insert(0, new GpuAppliedChange(Loc.F("Gpu.CacheSombreadorNvidiaN", choice.Label), run, BrokerLevel.Admin, this));
        Message = StartupPage.Describe(r);
        ShaderCacheText = await Task.Run(() => DescribeSetting(DrsTarget.Base, NvidiaDrsSettings.ShaderCacheMaxSize));
    });

    // ------------------------------------------------------------------ gravação

    private async Task ToggleRecordingAsync()
    {
        if (IsRecording) { _recording?.Cancel(); return; }
        if (_report?.Primary is not { } gpu) return;
        IsRecording = true;
        RecordFindings.Clear();
        Verdict = "";
        Samples = [];
        Bands = [];
        using var cts = new CancellationTokenSource();
        _recording = cts;
        var samples = new List<GpuTelemetrySample>();
        IPerformanceSource? pdh = null;
        try
        {
            for (var i = RecordDelaySeconds; i > 0 && !cts.IsCancellationRequested; i--)
            {
                RecordStatus = Loc.F("Gpu.ComecaNSVolte", i);
                await Delay(1000, cts.Token);
            }
            var nvIndex = await Task.Run(() => NvidiaIndex(gpu));
            if (nvIndex < 0) pdh = await Task.Run(() => (IPerformanceSource)new WindowsPerformanceSource());
            var key = GpuIdentity.PdhAdapterKey(gpu.Luid);
            var clock = Stopwatch.StartNew();
            var nextTick = 0L;
            while (!cts.IsCancellationRequested && clock.Elapsed.TotalSeconds < RecordSeconds)
            {
                var t = clock.Elapsed.TotalSeconds;
                var sample = await Task.Run(() => ReadSample(gpu, nvIndex, pdh, key, t));
                if (sample is not null) samples.Add(sample);
                Samples = samples.ToList();
                Bands = RecordingAnalyzer.Bands(samples);
                RecordProgress = Math.Min(1, t / RecordSeconds);
                RecordStatus = Loc.F("Gpu.GravandoNNS", t, RecordSeconds) + (sample?.GraphicsClockMhz is { } c ? Loc.F("Gpu.NMhz", c) : "") +
                               (sample?.UtilizationPercent is { } u ? Loc.F("Gpu.UsoN2", u) : "") + (sample?.TemperatureC is { } tc ? $" · {tc:0} °C" : "");
                nextTick += 1000;
                var wait = nextTick - clock.ElapsedMilliseconds;
                if (wait > 0) await Delay((int)wait, cts.Token);
            }
            Analyze(gpu, samples);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordStatus = Loc.F("Gpu.GravacaoParouN", ex.Message);
        }
        finally
        {
            pdh?.Dispose();
            _recording = null;
            IsRecording = false;
            RecordProgress = samples.Count > 0 ? 1 : 0;
        }
    }

    private static async Task Delay(int ms, CancellationToken token)
    {
        try { await Task.Delay(ms, token); }
        catch (TaskCanceledException) { }
    }

    private int NvidiaIndex(GpuAdapter gpu)
    {
        if (_nvml is null || gpu.Vendor != GpuVendor.Nvidia) return -1;
        for (var i = 0; i < _nvml.Count; i++)
        {
            var r = _nvml.Read(i);
            if (r.DeviceId == gpu.DeviceId && (r.PciSubSystemId is null || gpu.SubSysId == 0 || r.PciSubSystemId == gpu.SubSysId)) return i;
        }
        return _nvml.Count == 1 ? 0 : -1;
    }

    private GpuTelemetrySample? ReadSample(GpuAdapter gpu, int nvIndex, IPerformanceSource? pdh, string key, double seconds)
    {
        if (nvIndex >= 0 && _nvml is not null) return _nvml.Read(nvIndex).ToSample(seconds);
        if (pdh is null) return null;
        var raw = pdh.Read();
        var usage = MonitorAggregator.Gpu(raw.GpuEngine, raw.GpuDedicated).FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (usage is null) return null;
        return new GpuTelemetrySample(seconds, usage.OverallPercent, null, null, null, null, null, null, null, null, usage.DedicatedBytes,
            gpu.DedicatedVideoBytes > 0 ? gpu.DedicatedVideoBytes : null);
    }

    private void Analyze(GpuAdapter gpu, List<GpuTelemetrySample> samples)
    {
        var a = RecordingAnalyzer.Analyze(samples, gpu.Nvidia?.MaxGraphicsClockMhz, HardwareCaps.PcieWithGpuCeiling(gpu).Link, _report?.IsLaptop ?? false);
        Verdict = a.Verdict;
        VerdictTone = a.VerdictLevel;
        RecordFindings.Clear();
        foreach (var f in a.Findings)
            RecordFindings.Add(new GpuFindingRow(new GpuFinding("rec." + f.Title, f.Level, f.Title, f.Evidence, Loc.T("Gpu.NvmlDuranteGravacao"), f.Advice ?? ""), this));
        var summary = new List<string> { Loc.F("Gpu.NSGravadosN", a.Samples, a.LoadSamples, RecordingAnalyzer.LoadThresholdPercent) };
        if (a.AvgClockUnderLoad is { } c) summary.Add(Loc.F("Gpu.ClockMedioNMhz", c));
        if (a.AvgPowerUnderLoad is { } p) summary.Add(Loc.F("Gpu.PotenciaMediaNW", p));
        if (a.MaxTemperature is { } t) summary.Add(Loc.F("Gpu.MaximaNC", t));
        RecordStatus = string.Join(" · ", summary) + ".";
    }
}

/// <summary>Cartão de número (barramento, limites, memória, monitor). IconKey = ícone do design system; Trend = selo curto.</summary>
public sealed record GpuStatCard(string Title, string Value, string Detail, string Tone, string IconKey = "Icon.Gpu", string Trend = "")
{
    public UiTone ToneKind => GpuTones.Of(Tone);
}

/// <summary>Um recurso da placa (HAGS, Resizable BAR…) como selo.</summary>
public sealed record GpuCheckRow(string Name, string State, string Tone, string Note)
{
    public UiTone ToneKind => GpuTones.Of(Tone);
}

/// <summary>"good"/"warn"/"bad"/"info" → tom e ícone do design system.</summary>
public static class GpuTones
{
    public static UiTone Of(string? tone) => tone switch
    {
        "good" => UiTone.Good,
        "warn" => UiTone.Warn,
        "bad" => UiTone.Bad,
        _ => UiTone.Info,
    };

    public static string IconOf(string? tone) => tone switch
    {
        "good" => "Icon.Check",
        "warn" => "Icon.Warning",
        "bad" => "Icon.Error",
        _ => "Icon.Info",
    };
}

public sealed record GpuKeyValue(string Label, string Value, string Note);

/// <summary>Uma GPU na lista "o que tem neste PC".</summary>
public sealed class GpuAdapterRow
{
    public GpuAdapterRow(GpuAdapter a)
    {
        ArgumentNullException.ThrowIfNull(a);
        Name = a.Name;
        var role = a.IsHighPerformance ? Loc.T("Gpu.AltoDesempenho") : a.IsMinimumPower ? Loc.T("Gpu.EconomiaEnergia") : "";
        Kind = (a.IsIntegrated ? Loc.T("Drv.Integrada") : Loc.T("Drv.Dedicada")) + (role.Length > 0 ? " · " + role : "");
        Architecture = a.Architecture == GpuArchitecture.Unknown ? Loc.T("Gpu.ArquiteturaNaoIdentificada") : $"{a.Architecture.Name} · {a.Architecture.Family}";
        var v = a.DriverInfoDecoded;
        Driver = (v.VendorVersion is { } vv ? Loc.F("Gpu.DriverNN", vv, v.WindowsVersion) : string.IsNullOrEmpty(v.WindowsVersion) ? Loc.T("Gpu.DriverNaoInformado") : Loc.F("Gpu.DriverN", v.WindowsVersion)) +
                 (a.DriverDate is { } d ? $" de {d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}" : "");
        Memory = a.DedicatedVideoBytes > 0 ? Loc.F("Gpu.NDedicada", GpuDiagnostics.FormatGb(a.DedicatedVideoBytes)) : Loc.T("Gpu.SemMemoriaDedicada");
        Ids = Loc.F("Gpu.PciNNSubsistema", a.VendorId, a.DeviceId, a.SubSysId, a.Luid);
    }

    public string Name { get; }
    public string Kind { get; }
    public string Architecture { get; }
    public string Driver { get; }
    public string Memory { get; }
    public string Ids { get; }
}

/// <summary>Monitor: modo atual, melhor modo e quem desenha.</summary>
public sealed class GpuMonitorRow
{
    public GpuMonitorRow(MonitorReport m, GpuFinding? fix, GpuLabPage page)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(page);
        Name = !string.IsNullOrWhiteSpace(m.FriendlyName) ? m.FriendlyName : m.Internal ? Loc.T("Gpu.TelaNotebook") : m.Device;
        Mode = m.Current.Label;
        Max = m.Advice.Best is { } b ? Loc.F("Gpu.MaximoNestaResolucaoN", b.Hz) : "";
        Detail = string.Join(" · ", new[]
        {
            m.Device + (m.Primary ? " (principal)" : ""),
            m.Connection,
            string.IsNullOrEmpty(m.AdapterName) ? "" : Loc.T("Gpu.DesenhadoPela") + m.AdapterName,
            m.HdrSupported == true ? (m.HdrEnabled == true ? Loc.T("Gpu.HdrLigado") : Loc.T("Gpu.HdrDisponivelDesligado")) : m.HdrSupported == false ? Loc.T("Gpu.SemHdr") : "",
            m.BitsPerColor is { } bpc ? Loc.F("Gpu.NBitsCor", bpc) : "",
        }.Where(s => !string.IsNullOrEmpty(s)));
        Tone = m.Advice.BelowMaxRefresh ? "warn" : "good";
        CurrentHz = m.Current.Hz;
        MaxHz = m.Advice.Best?.Hz ?? m.Current.Hz;
        IsBelowMax = m.Advice.BelowMaxRefresh;
        Fix = fix;
        FixLabel = fix?.FixLabel ?? "";
        FixCommand = new RelayCommand(() => { if (Fix is not null) _ = page.FixAsync(Fix); }, () => Fix is not null && !page.Busy);
    }

    public string Name { get; }
    public string Mode { get; }
    public string Max { get; }
    public string Detail { get; }
    public string Tone { get; }
    public UiTone ToneKind => GpuTones.Of(Tone);
    /// <summary>Taxa atual e a maior anunciada na mesma resolução (o antes → depois do cartão).</summary>
    public double CurrentHz { get; }
    public double MaxHz { get; }
    public bool IsBelowMax { get; }
    public string StatusText => IsBelowMax ? Loc.T("Gpu.AbaixoMonitorAguenta") : Loc.T("Gpu.TaxaMaxima");
    public GpuFinding? Fix { get; }
    public bool HasFix => Fix is not null;
    public string FixLabel { get; }
    public RelayCommand FixCommand { get; }
}

/// <summary>Achado com evidência, fonte e botão Corrigir (quando há ação reversível).</summary>
public sealed class GpuFindingRow
{
    public GpuFindingRow(GpuFinding f, GpuLabPage page)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(page);
        Finding = f;
        FixCommand = new RelayCommand(() => _ = page.FixAsync(f), () => f.Fix != GpuFixKind.None && !page.Busy);
    }

    public GpuFinding Finding { get; }
    public string Title => Finding.Title;
    public string Evidence => Finding.Evidence;
    public string Advice => Finding.Advice;
    public string Source => Loc.T("Gpu.Fonte") + Finding.Source;
    public string Tone => Finding.Level;
    public UiTone ToneKind => GpuTones.Of(Finding.Level);
    public string IconKey => GpuTones.IconOf(Finding.Level);
    public string LevelText => Finding.Level switch { "bad" => Loc.T("Gpu.Problema"), "warn" => Loc.T("Gpu.Atencao"), "good" => Loc.T("Gpu.TudoCerto"), _ => Loc.T("Pg.Saber") };
    public bool HasAdvice => !string.IsNullOrWhiteSpace(Finding.Advice);
    public bool HasFix => Finding.Fix != GpuFixKind.None;
    public string FixLabel => Finding.FixLabel ?? Loc.T("Gpu.Corrigir");
    public RelayCommand FixCommand { get; }
}

/// <summary>Jogo instalado e a GPU que o Windows dá para ele.</summary>
public sealed class GpuGameRow
{
    public GpuGameRow(GameEntry g, int? preference, GpuFinding? fix, GpuLabPage page)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(page);
        Name = g.Name;
        Store = g.Store;
        Exe = g.ExePath is null ? Loc.T("Gpu.ExecutavelNaoEncontrado") : g.ExePath + (g.ExePathSure ? "" : Loc.T("Gpu.Provavel"));
        Preference = GameLibraries.PreferenceLabel(preference);
        IsHigh = preference == 2;
        Fix = fix;
        FixCommand = new RelayCommand(() => { if (Fix is not null) _ = page.FixAsync(Fix); }, () => Fix is not null && !page.Busy);
    }

    public string Name { get; }
    public string Store { get; }
    public string Exe { get; }
    public string Preference { get; }
    public bool IsHigh { get; }
    public UiTone ToneKind => IsHigh ? UiTone.Good : Fix is not null ? UiTone.Warn : UiTone.Info;
    public GpuFinding? Fix { get; }
    public bool HasFix => Fix is not null;
    public RelayCommand FixCommand { get; }
}

/// <summary>Jogo para "Otimizações para jogos em janela": executável e o token SwapEffectUpgradeEnable atual (1 = ligado, 0 = desligado, null = padrão).</summary>
public sealed record WindowedGameOption(string Name, string ExePath, int? SwapEffectUpgrade, bool ExeSure)
{
    public bool IsOn => SwapEffectUpgrade == 1;
    public string Label => Name + (ExeSure ? "" : Loc.T("Gpu.ExecutavelProvavel"));
    public string StateText => SwapEffectUpgrade switch
    {
        1 => Loc.T("Gpu.JaLigadoEsteJogo"),
        0 => Loc.T("Gpu.DesligadoEsteJogoAlguem"),
        _ => Loc.T("Gpu.SegueOpcaoGeralWindows"),
    } + " " + ExePath;
}

/// <summary>Mudança aplicada nesta sessão, com Desfazer.</summary>
public sealed class GpuAppliedChange
{
    public GpuAppliedChange(string text, Guid runId, BrokerLevel level, GpuLabPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Text = text;
        RunId = runId;
        Level = level;
        UndoCommand = new RelayCommand(() => _ = page.UndoAsync(this), () => !page.Busy);
    }

    public string Text { get; }
    public Guid RunId { get; }
    public BrokerLevel Level { get; }
    public RelayCommand UndoCommand { get; }
}

/// <summary>Jogo com executável conhecido, para os ajustes do driver NVIDIA (só o nome do arquivo vai para o perfil).</summary>
public sealed record NvGameOption(string Name, string Exe, bool ExeSure)
{
    public string Label => ExeSure ? $"{Name} ({Exe})" : Loc.F("Gpu.NNProvavel", Name, Exe);
}

/// <summary>Opção com valor do catálogo e rótulo.</summary>
public sealed record NvChoice(string Value, string Label);
