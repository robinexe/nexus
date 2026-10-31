using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Nexus.Broker;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;
using Nexus.Lab.Gpu.Windows;
using Nexus.Monitoring;
using Nexus.Monitoring.Windows;

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

    public GpuLabPage()
    {
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !Busy && !IsRecording);
        RecordCommand = new RelayCommand(() => _ = ToggleRecordingAsync(), () => _hasReport && (!Busy || IsRecording));
    }

    public override string Title => "Placa de vídeo";
    public override string Group => "Laboratório";
    public override string Subtitle => "Engenharia reversa da sua placa de vídeo: barramento, clocks, limites e monitores.";

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RecordCommand { get; }

    public string HeroTitle { get => _heroTitle; private set => Set(ref _heroTitle, value); }
    public string HeroDetail { get => _heroDetail; private set => Set(ref _heroDetail, value); }
    public string HybridText { get => _hybridText; private set => Set(ref _hybridText, value); }
    public string VendorNote { get => _vendorNote; private set => Set(ref _vendorNote, value); }
    public bool HasReport { get => _hasReport; private set => Set(ref _hasReport, value); }
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
    public string VerdictTone { get => _verdictTone; private set => Set(ref _verdictTone, value); }

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
            return GpuLabScanner.Scan(_nvml, _nvmlStatus);
        });
        _report = report;
        Build(report);
        HasReport = true;
    });

    private void Build(GpuLabReport r)
    {
        var primary = r.Primary;
        var hybrid = r.Hybrid;
        IsHybrid = hybrid.IsHybrid;
        HeroTitle = primary is null ? "Nenhuma GPU de hardware encontrada" : GpuDiagnostics.HeroLine(primary);
        HeroDetail = primary is null ? "O Windows só mostra o adaptador básico (sem driver do fabricante)." : PrimaryDetail(primary);
        HybridText = hybrid.Description;

        Adapters.Clear();
        foreach (var a in r.Hardware) Adapters.Add(new GpuAdapterRow(a));

        Cards.Clear();
        if (primary is not null)
        {
            Cards.Add(PcieCard(primary));
            Cards.Add(LimitsCard(primary));
            Cards.Add(MemoryCard(primary));
            Cards.Add(FeaturesCard(r, primary));
        }

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

    private static GpuStatCard PcieCard(GpuAdapter a)
    {
        if (a.IsIntegrated) return new("Barramento", "Integrada", "Fica dentro do processador: não usa slot PCIe.", "info");
        var (level, text) = Pcie.IdleVerdict(a.Pcie, false);
        if (!a.Pcie.HasData) return new("Barramento PCIe", "—", text, "info");
        var detail = $"Placa: {a.Pcie.Describe(current: false)}" + (a.Pcie.PortMaxGen is { } pg ? $" · porta: PCIe {Pcie.GenLabel(pg)} x{a.Pcie.PortMaxWidth?.ToString(CultureInfo.InvariantCulture) ?? "?"}" : "") + ". " + text;
        return new("Barramento PCIe (agora)", a.Pcie.Describe(), detail, level);
    }

    private static GpuStatCard LimitsCard(GpuAdapter a)
    {
        if (a.Nvidia is not { } nv) return new("Limites", "sem leitura", "O driver desta placa não expõe limites de potência/temperatura sem SDK.", "info");
        var value = nv.EnforcedPowerLimitWatts ?? nv.PowerLimitWatts;
        var parts = new List<string>();
        if (nv.DefaultPowerLimitWatts is { } d) parts.Add($"padrão {d:0} W");
        if (nv.MinPowerLimitWatts is { } lo && nv.MaxPowerLimitWatts is { } hi && hi > 0) parts.Add($"faixa {lo:0}–{hi:0} W");
        if (nv.SlowdownTemperatureC is { } t) parts.Add($"reduz clock a {t:0} °C");
        if (nv.MaxGraphicsClockMhz is { } c) parts.Add($"clock máx. {c:0} MHz");
        return new("Limite de potência", value is { } v ? $"{v:0} W" : "—", string.Join(" · ", parts), "info");
    }

    private static GpuStatCard MemoryCard(GpuAdapter a)
    {
        var total = a.Nvidia?.VramTotalBytes ?? a.DedicatedVideoBytes;
        var parts = new List<string>();
        if (a.LocalBudgetBytes is { } b) parts.Add($"orçamento do Windows {GpuDiagnostics.FormatGb(b)}");
        if ((a.Nvidia?.VramUsedBytes ?? a.LocalUsageBytes) is { } u) parts.Add($"em uso {GpuDiagnostics.FormatGb(u)}");
        if (a.SharedSystemBytes > 0) parts.Add($"pode emprestar até {GpuDiagnostics.FormatGb(a.SharedSystemBytes)} da RAM");
        return new(a.IsIntegrated ? "Memória (reservada da RAM)" : "Memória de vídeo", total > 0 ? GpuDiagnostics.FormatGb(total) : "—", string.Join(" · ", parts), "info");
    }

    private static GpuStatCard FeaturesCard(GpuLabReport r, GpuAdapter a)
    {
        var lines = new List<string>();
        var hags = a.HagsEnabled ?? (r.Settings.HwSchMode == 2 ? true : r.Settings.HwSchMode == 1 ? false : null);
        lines.Add($"HAGS: {(hags is null ? "não informado" : hags.Value ? "ligado" : "desligado")}{(a.HagsEnabled is null ? "" : " (driver)")}");
        if (a.Nvidia?.Bar1TotalBytes is { } bar1)
            lines.Add($"Resizable BAR: {(bar1 > 300UL << 20 ? "ligado" : "desligado")} (BAR1 {bar1 >> 20} MB)");
        else lines.Add("Resizable BAR: sem leitura (só NVIDIA via NVML)");
        lines.Add($"MPO: {(r.Settings.OverlayTestMode == 5 ? "desligado no registro" : "padrão do Windows")}");
        lines.Add($"Modo de Jogo: {(r.Settings.AutoGameModeEnabled == 0 ? "desligado" : "ligado")}");
        if (r.Settings.OnBattery == true) lines.Add("Energia: na bateria (GPU limitada)");
        return new("Recursos", "", string.Join(Environment.NewLine, lines), "info");
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
                           "O driver é consultado antes (teste) e a resolução não muda.\n\nSe a tela piscar ou ficar preta (cabo antigo), aguarde alguns segundos; depois use Desfazer aqui ou na página Aplicar.";
                await ApplyAsync(f.Title, text, new ActionRequest(GpuDiagnostics.DisplayModeAction, null, Copy(args)), BrokerLevel.User, $"{args["device"]} a {mode?.Hz} Hz");
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

    private async Task ApplyAsync(string title, string text, ActionRequest request, BrokerLevel level, string change)
    {
        var r = await Host!.ApplyAsync(title, text, [request], level);
        if (r is null) return;
        if (r.Response is { Ok: true, RunId: { } run }) Changes.Insert(0, new GpuAppliedChange(change, run, level, this));
        Message = StartupPage.Describe(r);
        _report = null;
        var report = await Task.Run(() => GpuLabScanner.Scan(_nvml, _nvmlStatus));
        _report = report;
        Build(report);
    }

    internal Task UndoAsync(GpuAppliedChange c) => RunAsync(async () =>
    {
        if (Host is null) return;
        var r = await Host.RevertAsync(c.RunId, c.Level);
        if (r is null) return;
        if (r.Response?.FullyRestored == true) Changes.Remove(c);
        Message = r.Message;
        var report = await Task.Run(() => GpuLabScanner.Scan(_nvml, _nvmlStatus));
        _report = report;
        Build(report);
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
        var a = RecordingAnalyzer.Analyze(samples, gpu.Nvidia?.MaxGraphicsClockMhz, gpu.Pcie, _report?.IsLaptop ?? false);
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

/// <summary>Cartão de número (barramento, limites, memória, recursos).</summary>
public sealed record GpuStatCard(string Title, string Value, string Detail, string Tone);

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
        Fix = fix;
        FixLabel = fix?.FixLabel ?? "";
        FixCommand = new RelayCommand(() => { if (Fix is not null) _ = page.FixAsync(Fix); }, () => Fix is not null && !page.Busy);
    }

    public string Name { get; }
    public string Mode { get; }
    public string Max { get; }
    public string Detail { get; }
    public string Tone { get; }
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
    public GpuFinding? Fix { get; }
    public bool HasFix => Fix is not null;
    public RelayCommand FixCommand { get; }
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
