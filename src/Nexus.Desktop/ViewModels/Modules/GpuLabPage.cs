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
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public const int RecordSeconds = 60;
    public const int RecordDelaySeconds = 10;

    private GpuLabReport? _report;
    private NvmlSession? _nvml;
    private string _nvmlStatus = "";
    private bool _nvmlTried;
    private CancellationTokenSource? _recording;

    private string _heroTitle = "Lendo a placa de vídeo…", _heroDetail = "", _hybridText = "", _vendorNote = "";
    private string _recordStatus = "Abra um jogo, volte aqui e clique em Gravar. Você tem 10 s para voltar ao jogo; o NEXUS observa 60 s.";
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
        ShaderCacheOptions.Add(new NvChoice(NvidiaDrsActions.ShaderCacheValues[0], "100 GB"));
        ShaderCacheOptions.Add(new NvChoice(NvidiaDrsActions.ShaderCacheValues[1], "Ilimitado"));
        _selectedShaderCache = ShaderCacheOptions[0];
    }

    public override string Title => "Placa de vídeo";
    public override string Group => Loc.T("Nav.Laboratorio");
    public override string Subtitle => "Engenharia reversa da sua placa de vídeo: barramento, clocks, limites e monitores.";

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
    public string RecordButtonText => IsRecording ? "Parar e analisar" : "Gravar enquanto jogo (60 s)";
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
                catch (Exception ex) when (ex is not OutOfMemoryException) { _nvmlStatus = $"NVML indisponível: {ex.Message}"; }
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
        HeroTitle = primary is null ? "Nenhuma GPU de hardware encontrada" : GpuDiagnostics.HeroLine(primary);
        HeroDetail = primary is null ? "O Windows só mostra o adaptador básico (sem driver do fabricante)." : PrimaryDetail(primary);
        HybridText = hybrid.Description;
        HeroCaption = primary is null ? "Sem driver do fabricante" : primary.Name + (primary.IsIntegrated ? " · integrada" : " · dedicada");
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
            { Nvidia: not null } => $"Fonte dos clocks, limites e motivos: {r.NvmlStatus}.",
            { Vendor: GpuVendor.Nvidia } => $"Placa NVIDIA, mas a NVML não abriu ({r.NvmlStatus}). Sem clocks, limites e motivos de limitação.",
            { Vendor: GpuVendor.Amd } => "AMD: os clocks, a potência e os motivos de limitação só saem pela biblioteca ADLX, que exige o SDK da AMD. Sem ela o NEXUS mostra o que o Windows expõe (uso, VRAM, PCIe, driver) e diz o que falta, em vez de inventar.",
            { Vendor: GpuVendor.Intel } => "Intel: clocks e potência só pela biblioteca do driver (IGCL), que exige SDK. O NEXUS mostra o que o Windows expõe (uso, VRAM, PCIe, driver).",
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
        parts.Add(a.IsIntegrated ? "integrada ao processador" : "dedicada");
        if (a.IsHighPerformance) parts.Add("a GPU de alto desempenho do Windows");
        parts.Add($"PCI {a.VendorId:X4}:{a.DeviceId:X4}");
        var v = a.DriverInfoDecoded;
        if (!string.IsNullOrEmpty(v.WindowsVersion)) parts.Add($"driver {v.WindowsVersion} no Windows");
        return string.Join(" · ", parts);
    }

    private static GpuStatCard PcieCard(GpuAdapter a, bool laptop)
    {
        if (a.IsIntegrated) return new("Barramento", "Integrada", "Fica dentro do processador: não usa slot PCIe.", "info", "Icon.Hardware");
        var (link, note) = HardwareCaps.PcieWithGpuCeiling(a);
        var (level, text) = Pcie.IdleVerdict(link, false, laptop);
        if (!link.HasData) return new("Barramento PCIe", "—", text, "info", "Icon.Hardware");
        var detail = $"GPU: {link.Describe(current: false)}" + (link.PortMaxGen is { } pg ? $" · porta: PCIe {Pcie.GenLabel(pg)} x{link.PortMaxWidth?.ToString(CultureInfo.InvariantCulture) ?? "?"}" : "") + ". " + text +
                     (note is null ? "" : " " + note);
        return new("Barramento PCIe (agora)", link.Describe(), detail, level, "Icon.Hardware");
    }

    private static GpuStatCard LimitsCard(GpuAdapter a)
    {
        if (a.Nvidia is not { } nv) return new("Limites", "sem leitura", "O driver desta placa não expõe limites de potência/temperatura sem SDK.", "info", "Icon.Bolt");
        var value = nv.EnforcedPowerLimitWatts ?? nv.PowerLimitWatts;
        var parts = new List<string>();
        if (nv.DefaultPowerLimitWatts is { } d) parts.Add($"padrão {d:0} W");
        if (nv.MinPowerLimitWatts is { } lo && nv.MaxPowerLimitWatts is { } hi && hi > 0) parts.Add($"faixa {lo:0}–{hi:0} W");
        if (nv.SlowdownTemperatureC is { } t) parts.Add($"reduz clock a {t:0} °C");
        if (nv.MaxGraphicsClockMhz is { } c) parts.Add($"clock máx. {c:0} MHz");
        return new("Limite de potência", value is { } v ? $"{v:0} W" : "—", string.Join(" · ", parts), "info", "Icon.Bolt");
    }

    private static GpuStatCard MemoryCard(GpuAdapter a)
    {
        var total = a.Nvidia?.VramTotalBytes ?? a.DedicatedVideoBytes;
        var parts = new List<string>();
        if (a.LocalBudgetBytes is { } b) parts.Add($"orçamento do Windows {GpuDiagnostics.FormatGb(b)}");
        if ((a.Nvidia?.VramUsedBytes ?? a.LocalUsageBytes) is { } u) parts.Add($"em uso {GpuDiagnostics.FormatGb(u)}");
        if (a.SharedSystemBytes > 0) parts.Add($"pode emprestar até {GpuDiagnostics.FormatGb(a.SharedSystemBytes)} da RAM");
        return new(a.IsIntegrated ? "Memória (reservada da RAM)" : "Memória de vídeo", total > 0 ? GpuDiagnostics.FormatGb(total) : "—", string.Join(" · ", parts), "info", "Icon.Ram");
    }

    private static GpuStatCard MonitorCard(GpuLabReport r)
    {
        var m = r.Monitors.FirstOrDefault(x => x.Primary) ?? (r.Monitors.Count > 0 ? r.Monitors[0] : null);
        if (m is null) return new("Monitor", "—", "Nenhum monitor lido.", "info", "Icon.Monitor");
        var best = m.Advice.BelowMaxRefresh ? m.Advice.Best : null;
        return new("Monitor principal", $"{m.Current.Hz} Hz",
            best is not null ? $"Aceita {best.Hz} Hz nesta resolução." : "Já na maior taxa desta resolução.", best is not null ? "warn" : "good", "Icon.Monitor",
            best is not null ? $"pode {best.Hz} Hz" : "no máximo");
    }

    /// <summary>Recursos da placa como selos (o mesmo conteúdo do antigo cartão "Recursos": HAGS, Resizable BAR, MPO, Modo de Jogo, energia).</summary>
    private static IEnumerable<GpuCheckRow> FeatureChecks(GpuLabReport r, GpuAdapter a)
    {
        var hags = a.HagsEnabled ?? (r.Settings.HwSchMode == 2 ? true : r.Settings.HwSchMode == 1 ? false : null);
        yield return new("Agendamento por hardware (HAGS)", hags is null ? "não informado" : hags.Value ? "ligado" : "desligado",
            hags == true ? "good" : "info", a.HagsEnabled is null ? "Lido do registro do Windows." : "Informado pelo driver.");
        if (!a.IsIntegrated)
        {
            var rb = HardwareCaps.ResizableBar(a);
            yield return rb.Enabled is { } on
                ? new("Resizable BAR", on ? "ligado" : "desligado", on ? "good" : HardwareCaps.IsIntelArcDiscrete(a) ? "warn" : "info", $"{rb.Evidence} Fonte: {rb.Source}.")
                : new("Resizable BAR", "não dá para afirmar", HardwareCaps.IsIntelArcDiscrete(a) ? "warn" : "info", rb.Evidence);
        }
        yield return new("MPO (planos de sobreposição)", r.Settings.OverlayTestMode == 5 ? "desligado no registro" : "padrão do Windows",
            r.Settings.OverlayTestMode == 5 ? "warn" : "good", "Desligar o MPO à mão só se recomenda para tela piscando.");
        yield return new("Modo de Jogo", r.Settings.AutoGameModeEnabled == 0 ? "desligado" : "ligado", r.Settings.AutoGameModeEnabled == 0 ? "warn" : "good",
            "Dá prioridade ao jogo em primeiro plano.");
        if (r.Settings.OnBattery == true) yield return new("Energia", "na bateria", "warn", "Na bateria a GPU é limitada pelo notebook.");
    }

    private static IEnumerable<GpuKeyValue> NvidiaRows(NvidiaReading n)
    {
        static string F(double? v, string unit, string fmt = "0") => v is { } x ? x.ToString(fmt, Pt) + unit : "—";
        yield return new("Clock gráfico", $"{F(n.GraphicsClockMhz, " MHz")} agora", $"máximo {F(n.MaxGraphicsClockMhz, " MHz")}");
        yield return new("Clock da memória", $"{F(n.MemoryClockMhz, " MHz")} agora", $"máximo {F(n.MaxMemoryClockMhz, " MHz")}");
        yield return new("Potência", $"{F(n.PowerWatts, " W", "0.0")} agora",
            $"limite {F(n.EnforcedPowerLimitWatts ?? n.PowerLimitWatts, " W")} · padrão {F(n.DefaultPowerLimitWatts, " W")} · faixa {F(n.MinPowerLimitWatts, "")}–{F(n.MaxPowerLimitWatts, " W")}");
        yield return new("Temperatura", $"{F(n.TemperatureC, " °C")} agora",
            $"reduz clock a {F(n.SlowdownTemperatureC, " °C")} · desliga a {F(n.ShutdownTemperatureC, " °C")}" + (n.MaxOperatingTemperatureC is { } mo ? $" · alvo máx. {mo:0} °C" : ""));
        yield return new("Ventoinha", n.FanPercent is { } fan ? $"{fan:0}%" : "—", n.FanPercent is null ? "Em notebook a ventoinha é do fabricante do notebook, não da placa." : "");
        yield return new("Uso", $"{F(n.UtilizationPercent, "%")} GPU · {F(n.MemoryUtilizationPercent, "%")} memória", n.PerformanceState is { } ps ? $"estado P{ps} (P0 = máximo, P8 = repouso)" : "");
        yield return new("PCIe (pela placa)", n.PcieGen is { } g ? $"Gen {g} x{n.PcieWidth}" : "—", n.PcieMaxGen is { } mg ? $"máximo Gen {mg} x{n.PcieMaxWidth}" : "");
        var reasons = n.ReasonsMask is { } m ? ThrottleReasons.Describe(m) : [];
        yield return new("Limitando agora", reasons.Count == 0 ? (n.ReasonsMask is null ? "—" : "nada") : reasons[0], reasons.Count > 1 ? string.Join("; ", reasons.Skip(1)) : "Em repouso é normal aparecer \"ociosa\". O que importa é o que aparece com um jogo aberto: use a gravação.");
        yield return new("VRAM", n.VramTotalBytes is { } t ? $"{GpuDiagnostics.FormatGb(n.VramUsedBytes ?? 0)} de {GpuDiagnostics.FormatGb(t)}" : "—",
            n.Bar1TotalBytes is { } b ? $"janela BAR1 {b >> 20} MB" : "");
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
                var text = $"Trocar {args["device"]} para {mode?.Label}?\n\nÉ a mesma troca de Configurações > Tela > Taxa de atualização, só com um modo que o próprio monitor anuncia. " +
                           "O driver é consultado antes (teste) e a resolução não muda.\n\n" +
                           $"Depois de trocar, o NEXUS pergunta se você quer manter. Sem resposta em {KeepChangeCountdown.DefaultSeconds} s (tela preta, cabo antigo), " +
                           "volta sozinho ao modo de antes, como faz o próprio Windows.";
                var label = $"{args["device"]} a {mode?.Hz} Hz";
                await ApplyAsync(f.Title, text, new ActionRequest(GpuDiagnostics.DisplayModeAction, null, Copy(args)), BrokerLevel.User, label,
                    change => StartKeepCountdown(change, mode is null ? "esta taxa" : $"{mode.Hz} Hz"));
                break;
            }
            case GpuFixKind.AppGpuPreference when args is not null:
            {
                var text = $"Marcar \"alto desempenho\" para:\n{args["exePath"]}\n\nO jogo passa a usar a GPU dedicada (mais FPS, mais consumo de bateria). " +
                           "É o mesmo ajuste de Configurações > Tela > Elementos gráficos e vale a partir da próxima vez que o jogo abrir.";
                await ApplyAsync("Usar a GPU dedicada", text, new ActionRequest(GpuDiagnostics.AppPreferenceAction, null, Copy(args)), BrokerLevel.User,
                    $"Alto desempenho: {System.IO.Path.GetFileName(args["exePath"])}");
                break;
            }
            case GpuFixKind.GameMode:
                await ApplyAsync("Ligar Modo de Jogo", "Ligar o Modo de Jogo do Windows?\n\nEle dá prioridade ao jogo em primeiro plano e segura atualizações durante a partida.",
                    new ActionRequest(GpuDiagnostics.GameModeAction), BrokerLevel.User, "Modo de Jogo ligado");
                break;
            case GpuFixKind.Hags:
                await ApplyAsync("Ligar HAGS", "Ligar o agendamento de GPU acelerado por hardware?\n\nNecessário para a geração de quadros do DLSS (RTX 40/50). " +
                    "Exige administrador (o Windows vai pedir permissão e um ponto de restauração é criado) e só vale depois de reiniciar.",
                    new ActionRequest(GpuDiagnostics.HagsAction, "2"), BrokerLevel.Admin, "HAGS ligado (reinicie para valer)");
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
        if (!Host.Confirm("Abrir site oficial", $"Abrir no navegador a página oficial de drivers?\n\n{url}\n\nO NEXUS não baixa nem instala nada: escolha o driver lá, no site do fabricante.")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Message = $"Não foi possível abrir o navegador: {ex.Message}"; }
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
    public string KeepModeDetail => _keepChange is null ? "" : $"Agora: {_keepChange.Text}. Se a tela ficou preta ou piscando, não faça nada: o modo anterior volta sozinho.";
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
        Host?.Notify($"Monitor trocado para {what}. Volta sozinho em {KeepChangeCountdown.DefaultSeconds} s se você não clicar em Manter.");
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
        Message = $"Mantido: {_keepChange?.Text}. Para voltar depois, use Desfazer.";
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
            Message = (timedOut ? $"Sem resposta em {KeepChangeCountdown.DefaultSeconds} s: o monitor voltou ao modo anterior. " : "O monitor voltou ao modo anterior. ") + r.Message;
            var report = await Task.Run(() => GpuLabScanner.Scan(_nvml, _nvmlStatus, probeD3D12: true));
            _report = report;
            Build(report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = $"Não deu para voltar o modo do monitor sozinho: {ex.Message}. Use Desfazer aqui ou na página Aplicar.";
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
    private string _cs2Status = "Procurando o Counter-Strike 2 na Steam…";
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
            Cs2Status = "Counter-Strike 2 não encontrado nas bibliotecas da Steam.";
            return;
        }
        var (current, dump) = await Task.Run(() => (Cs2Install.ReadFpsMax(cfg), Cs2Install.ReadDump(cfg)));
        Cs2Status = $"CS2 em {cfg}. fps_max no autoexec.cfg: {current ?? "não definido (o jogo usa o padrão, 400)"}. " +
                    (dump is null
                        ? "Falta a lista de comandos desta versão: abra o CS2 com -condebug nas opções de inicialização da Steam, digite cvarlist no console e feche o jogo. Sem ela o NEXUS não grava (é a prova de que fps_max existe na sua versão)."
                        : $"Lista de comandos desta versão encontrada ({dump.Count} itens, {dump.SourceDescription}).");
        if (current is not null && Cs2FpsOptions.Contains(current)) SelectedCs2Fps = current;
    }

    private Task ApplyWindowedAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedWindowedGame is not { } g) return;
        var exe = g.ExePath;
        var text = $"Ligar \"Otimizações para jogos em janela\" só para:\n{exe}\n\n" +
                   "O Windows passa a apresentar este jogo no modo flip (menos atraso em janela e sem bordas, e permite G-SYNC/FreeSync em janela). " +
                   "Só vale para jogos DirectX 10/11 em janela ou sem bordas: em DirectX 12, Vulkan ou tela cheia exclusiva não muda nada. " +
                   "É a mesma opção de Configurações > Sistema > Tela > Elementos gráficos, por jogo, e vale na próxima vez que o jogo abrir. Desfazer devolve o que era.";
        await ApplyAsync("Otimizações para jogos em janela neste jogo", text,
            new ActionRequest(WindowedPerAppAction, null, new Dictionary<string, string> { ["gameExePath"] = exe }), BrokerLevel.User,
            $"Janela otimizada: {System.IO.Path.GetFileName(exe)}");
    });

    private Task ApplyCs2FpsAsync() => RunAsync(async () =>
    {
        if (Host is null || _cs2CfgDir is not { } cfg || SelectedCs2Fps is not { } fps) return;
        var text = $"Gravar fps_max {fps} no autoexec.cfg do CS2?\n\n{cfg}\n\n" +
                   (fps == "0" ? "0 tira o limite do jogo. " : "O jogo não passa desse FPS. Com G-SYNC/FreeSync, um limite um pouco abaixo da taxa do monitor (ex.: 141 em 144 Hz) mantém o VRR ativo. ") +
                   "Só a linha do fps_max muda; o resto do arquivo fica igual. Vale na próxima vez que o jogo abrir (se o CS2 não ler o autoexec sozinho, adicione +exec autoexec nas opções de inicialização). " +
                   "A Steam Cloud pode sobrescrever o arquivo. Desfazer devolve a linha que havia.";
        await ApplyAsync("CS2: limite de FPS", text,
            new ActionRequest(Cs2Install.FpsMaxActionKey, fps, new Dictionary<string, string> { ["cs2CfgDir"] = cfg }), BrokerLevel.User, $"CS2 fps_max {fps}");
    });

    // ------------------------------------------------------------------ driver NVIDIA por jogo (perfis DRS)

    // Só leitura aqui (INvidiaDrsReader); a gravação é do handler NvidiaDrsSetting no aplicador.
    private readonly INvidiaDrsReader _drs = NvidiaDrsFactory.Reader();
    private bool _nvAvailable, _nvLowLatency, _nvMaxPower, _nvFpsCap, _nvVsync;
    private string _nvStatus = "Lendo o driver NVIDIA…", _shaderCacheText = "", _nvFpsHint = "";
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
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ((string?)$"Sem placa/driver NVIDIA utilizável: {ex.Message}", ""); }
        });
        NvAvailable = unavailable is null;
        NvStatus = unavailable ?? "Driver NVIDIA encontrado. Os ajustes vão para o perfil do jogo no próprio driver, como no Painel de Controle NVIDIA.";
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
            NvFpsHint = $"Monitor principal a {monitor.Current.Hz} Hz: sugestão {cap} FPS (pelo menos 3 abaixo da taxa, para ficar dentro do G-SYNC).";
        }
        else
        {
            SelectedNvFps ??= "60";
            NvFpsHint = "Com G-SYNC, use pelo menos 3 FPS abaixo da taxa do monitor.";
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
            if (!r.ProfileFound) return "padrão do driver (o jogo não tem perfil no driver)";
            if (!r.InProfile || r.Value is not { } v) return "padrão do driver";
            return spec.Describe(v) + (r.IsPredefined ? " (definido pela NVIDIA)" : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "sem leitura: " + ex.Message; }
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
                list.Add(new("Perfil no driver", profile.ProfileFound ? profile.ProfileName ?? "?" : "nenhum (o NEXUS cria \"NEXUS - " + game.Exe + "\")", ""));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { list.Add(new("Perfil no driver", "sem leitura", ex.Message)); return list; }
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
            lines.Add("• Modo de baixa latência: ligado (1 quadro na fila; vale em DirectX 9/11 sem Reflex)");
        }
        if (NvMaxPower)
        {
            requests.Add(new ActionRequest(NvidiaDrsActions.MaxPower, null, new Dictionary<string, string> { ["exe"] = exe }));
            lines.Add("• Energia: preferir desempenho máximo (só com este jogo aberto; mais calor e consumo)");
        }
        var fps = SelectedNvFps;
        if (NvFpsCap)
        {
            if (fps is null || !NvidiaDrsActions.FpsCaps.Contains(fps)) { Message = "Escolha um limite de FPS da lista."; return; }
            requests.Add(new ActionRequest(NvidiaDrsActions.FpsCap, fps, new Dictionary<string, string> { ["exe"] = exe }));
            lines.Add($"• Taxa máxima de quadros: {fps} FPS");
        }
        if (NvVsync)
        {
            var capInProfile = NvCurrent.Any(r => r.Label == NvidiaDrsSettings.All[NvidiaDrsSettings.FrameRateLimit].Name && r.Value.EndsWith(" FPS", StringComparison.Ordinal));
            if (!NvFpsCap && !capInProfile)
            {
                Message = "V-Sync no driver só faz sentido com G-SYNC E limite de FPS abaixo da taxa do monitor. Marque também o limite de FPS.";
                return;
            }
            requests.Add(new ActionRequest(NvidiaDrsActions.VsyncOn, null, new Dictionary<string, string> { ["exe"] = exe }));
            lines.Add("• V-Sync: ligado no driver (para G-SYNC; desligue o V-Sync dentro do jogo)");
        }
        if (requests.Count == 0) return;
        var text = $"Gravar no perfil do driver NVIDIA de {game.Name} ({exe}):\n\n{string.Join("\n", lines)}\n\n" +
                   "É o mesmo banco de perfis do Painel de Controle NVIDIA. Vale na próxima vez que o jogo abrir. " +
                   "Exige administrador (o Windows pede permissão e um ponto de restauração é criado). Desfazer devolve o que o perfil tinha.";
        var r = await Host.ApplyAsync("Aplicar no driver NVIDIA", text, requests, BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run }) Changes.Insert(0, new GpuAppliedChange($"driver NVIDIA em {exe} ({requests.Count} ajuste(s))", run, BrokerLevel.Admin, this));
        Message = StartupPage.Describe(r);
        await ReadNvGameAsync();
    });

    private Task ApplyShaderCacheAsync() => RunAsync(async () =>
    {
        if (Host is null || SelectedShaderCache is not { } choice) return;
        var text = $"Mudar o tamanho do cache de sombreador do driver NVIDIA para {choice.Label} (perfil global)?\n\n" +
                   "O driver guarda os sombreadores já compilados dos jogos; quando o cache enche, apaga os antigos e o jogo volta a compilar durante a partida (travadinhas). " +
                   "Não aumenta FPS. A pasta do cache (%LOCALAPPDATA%\\NVIDIA\\DXCache) pode crescer muitos GB. Exige administrador; Desfazer devolve o tamanho anterior.";
        var r = await Host.ApplyAsync("Cache de sombreador NVIDIA", text, [new ActionRequest(NvidiaDrsActions.ShaderCache, choice.Value)], BrokerLevel.Admin);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run }) Changes.Insert(0, new GpuAppliedChange($"cache de sombreador NVIDIA: {choice.Label}", run, BrokerLevel.Admin, this));
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
                RecordStatus = $"Começa em {i} s: volte para o jogo e jogue normalmente.";
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
                RecordStatus = $"Gravando {t:0}/{RecordSeconds} s" + (sample?.GraphicsClockMhz is { } c ? $" · {c:0} MHz" : "") +
                               (sample?.UtilizationPercent is { } u ? $" · uso {u:0}%" : "") + (sample?.TemperatureC is { } tc ? $" · {tc:0} °C" : "");
                nextTick += 1000;
                var wait = nextTick - clock.ElapsedMilliseconds;
                if (wait > 0) await Delay((int)wait, cts.Token);
            }
            Analyze(gpu, samples);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordStatus = $"A gravação parou: {ex.Message}";
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
            RecordFindings.Add(new GpuFindingRow(new GpuFinding("rec." + f.Title, f.Level, f.Title, f.Evidence, "NVML durante a gravação", f.Advice ?? ""), this));
        var summary = new List<string> { $"{a.Samples} s gravados, {a.LoadSamples} com a GPU trabalhando (uso ≥ {RecordingAnalyzer.LoadThresholdPercent:0}%)" };
        if (a.AvgClockUnderLoad is { } c) summary.Add($"clock médio {c:0} MHz");
        if (a.AvgPowerUnderLoad is { } p) summary.Add($"potência média {p:0} W");
        if (a.MaxTemperature is { } t) summary.Add($"máxima {t:0} °C");
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
        var role = a.IsHighPerformance ? "alto desempenho" : a.IsMinimumPower ? "economia de energia" : "";
        Kind = (a.IsIntegrated ? "integrada" : "dedicada") + (role.Length > 0 ? " · " + role : "");
        Architecture = a.Architecture == GpuArchitecture.Unknown ? "arquitetura não identificada" : $"{a.Architecture.Name} · {a.Architecture.Family}";
        var v = a.DriverInfoDecoded;
        Driver = (v.VendorVersion is { } vv ? $"driver {vv} ({v.WindowsVersion})" : string.IsNullOrEmpty(v.WindowsVersion) ? "driver não informado" : $"driver {v.WindowsVersion}") +
                 (a.DriverDate is { } d ? $" de {d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}" : "");
        Memory = a.DedicatedVideoBytes > 0 ? $"{GpuDiagnostics.FormatGb(a.DedicatedVideoBytes)} dedicada" : "sem memória dedicada";
        Ids = $"PCI {a.VendorId:X4}:{a.DeviceId:X4} · subsistema {a.SubSysId:X8} · LUID {a.Luid:X}";
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
        Name = !string.IsNullOrWhiteSpace(m.FriendlyName) ? m.FriendlyName : m.Internal ? "Tela do notebook" : m.Device;
        Mode = m.Current.Label;
        Max = m.Advice.Best is { } b ? $"máximo nesta resolução: {b.Hz} Hz" : "";
        Detail = string.Join(" · ", new[]
        {
            m.Device + (m.Primary ? " (principal)" : ""),
            m.Connection,
            string.IsNullOrEmpty(m.AdapterName) ? "" : "desenhado pela " + m.AdapterName,
            m.HdrSupported == true ? (m.HdrEnabled == true ? "HDR ligado" : "HDR disponível (desligado)") : m.HdrSupported == false ? "sem HDR" : "",
            m.BitsPerColor is { } bpc ? $"{bpc} bits por cor" : "",
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
    public string StatusText => IsBelowMax ? "Abaixo do que o monitor aguenta" : "Na taxa máxima";
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
    public string Source => "Fonte: " + Finding.Source;
    public string Tone => Finding.Level;
    public UiTone ToneKind => GpuTones.Of(Finding.Level);
    public string IconKey => GpuTones.IconOf(Finding.Level);
    public string LevelText => Finding.Level switch { "bad" => "Problema", "warn" => "Atenção", "good" => "Tudo certo", _ => "Para saber" };
    public bool HasAdvice => !string.IsNullOrWhiteSpace(Finding.Advice);
    public bool HasFix => Finding.Fix != GpuFixKind.None;
    public string FixLabel => Finding.FixLabel ?? "Corrigir";
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
        Exe = g.ExePath is null ? "executável não encontrado" : g.ExePath + (g.ExePathSure ? "" : " (provável)");
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
    public string Label => Name + (ExeSure ? "" : " (executável provável)");
    public string StateText => SwapEffectUpgrade switch
    {
        1 => "Já ligado para este jogo.",
        0 => "Desligado para este jogo (alguém marcou \"não\").",
        _ => "Segue a opção geral do Windows (sem escolha própria).",
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
    public string Label => ExeSure ? $"{Name} ({Exe})" : $"{Name} ({Exe}, provável)";
}

/// <summary>Opção com valor do catálogo e rótulo.</summary>
public sealed record NvChoice(string Value, string Label);
