using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Nexus.HardwareDiscovery;
using Nexus.Monitoring;
using Nexus.Monitoring.Windows;
using Tone = Nexus.Desktop.Views.Controls.Tone;

namespace Nexus.Desktop.ViewModels
{
    /// <summary>
    /// Monitor ao vivo (somente leitura): CPU por núcleo, GPU por motor, memória, disco, rede, processos, saúde dos discos e
    /// temperaturas que o Windows de fato expõe. Coleta a 1 Hz só enquanto a página está à vista e a janela não está minimizada.
    /// Nada aqui escreve no sistema: não há ação de catálogo nem chamada ao aplicador.
    /// </summary>
    public sealed class MonitorPage : ModulePage
    {
        private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
        private const int VisibleProcesses = 8;

        private readonly DispatcherTimer _timer;
        private MonitorSession? _session;
        private Task? _starting;
        private bool _ticking, _subscribed, _userPaused, _healthLoaded;
        private string _liveStatus = Loc.T("Mon.Parado");
        private bool _running;
        private string _bottleneckTitle = Loc.T("Mon.Coletando"), _bottleneckText = Loc.T("Mon.AbrindoContadoresWindows"), _bottleneckTone = "info";
        private string _temperatureNote = "";
        private string _diskHealthNote = Loc.T("Mon.LendoSaudeDiscos");
        private string _unavailableNote = "";
        private string _cpuName = "";
        private bool _sortByMemory, _warmingUp = true, _healthLoading;
        private double _gpuDedicatedTotal;

        public MonitorPage()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            ToggleCommand = new RelayCommand(() =>
            {
                if (Running) { _userPaused = true; Stop(Loc.T("Mon.PausadoVoce")); }
                else { _userPaused = false; Start(); }
            });
            RefreshHealthCommand = new RelayCommand(() => _ = LoadHealthAsync(), () => !Busy);
            SortByCpuCommand = new RelayCommand(() => SortByMemory = false);
            SortByMemoryCommand = new RelayCommand(() => SortByMemory = true);
        }

        public override string Title => Loc.T("Mon.NavTitle");
        public override string Group => Loc.T("Nav.Medir");
        public override string Subtitle => Loc.T("Mon.NavSubtitle");

        public MonitorGauge Cpu { get; } = new("CPU", "%", 100);
        public MonitorGauge Gpu { get; } = new("GPU", "%", 100);
        public MonitorGauge Ram { get; } = new(Loc.T("Pg.Memoria"), "%", 100);
        public MonitorGauge Disk { get; } = new(Loc.T("Pg.Disco"), "%", 100);
        public MonitorGauge Net { get; } = new(Loc.T("Mon.Rede"), "", 0);

        public ObservableCollection<CoreBar> Cores { get; } = [];
        public ObservableCollection<MeterRow> GpuEngines { get; } = [];
        public ObservableCollection<ProcessRow> Processes { get; } = [];
        public ObservableCollection<ActivityRow> Disks { get; } = [];
        public ObservableCollection<ActivityRow> Networks { get; } = [];
        public ObservableCollection<DiskHealthRow> DiskHealth { get; } = [];
        public ObservableCollection<string> Temperatures { get; } = [];

        public RelayCommand ToggleCommand { get; }
        public RelayCommand RefreshHealthCommand { get; }
        public RelayCommand SortByCpuCommand { get; }
        public RelayCommand SortByMemoryCommand { get; }

        public bool Running { get => _running; private set { if (Set(ref _running, value)) OnPropertyChanged(nameof(ToggleText)); } }
        public string ToggleText => Running ? Loc.T("Mon.Pausar") : Loc.T("Mon.Retomar");
        public string LiveStatus { get => _liveStatus; private set => Set(ref _liveStatus, value); }
        public string BottleneckTitle { get => _bottleneckTitle; private set => Set(ref _bottleneckTitle, value); }
        public string BottleneckText { get => _bottleneckText; private set => Set(ref _bottleneckText, value); }
        /// <summary>"good", "warn" ou "info".</summary>
        public string BottleneckTone
        {
            get => _bottleneckTone;
            private set { if (Set(ref _bottleneckTone, value)) { OnPropertyChanged(nameof(BottleneckToneValue)); OnPropertyChanged(nameof(BottleneckIconKey)); } }
        }
        /// <summary>Tom do cartão-veredito (verde folgado, âmbar limitando, ciano informação).</summary>
        public Tone BottleneckToneValue => BottleneckTone switch { "good" => Tone.Good, "warn" => Tone.Warn, "bad" => Tone.Bad, _ => Tone.Info };
        public string BottleneckIconKey => BottleneckTone switch { "good" => "Icon.Check", "warn" => "Icon.Warning", "bad" => "Icon.Error", _ => "Icon.Info" };
        /// <summary>Até a primeira leitura chegar (esqueleto no lugar dos números).</summary>
        public bool IsWarmingUp { get => _warmingUp; private set => Set(ref _warmingUp, value); }
        /// <summary>Lendo a saúde dos discos (esqueleto no lugar da lista).</summary>
        public bool HealthLoading { get => _healthLoading; private set => Set(ref _healthLoading, value); }
        public string TemperatureNote { get => _temperatureNote; private set => Set(ref _temperatureNote, value); }
        public string DiskHealthNote { get => _diskHealthNote; private set => Set(ref _diskHealthNote, value); }
        public string UnavailableNote { get => _unavailableNote; private set => Set(ref _unavailableNote, value); }
        public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }

        public bool SortByMemory
        {
            get => _sortByMemory;
            set { if (Set(ref _sortByMemory, value)) { OnPropertyChanged(nameof(SortByCpu)); RefreshProcesses(); } }
        }
        public bool SortByCpu => !_sortByMemory;

        private IReadOnlyList<ProcessUsage> _lastProcesses = [];

        public override Task OnFactsReadyAsync(SystemFacts facts)
        {
            ArgumentNullException.ThrowIfNull(facts);
            CpuName = facts.CpuModel ?? "";
            _gpuDedicatedTotal = facts.Gpus.Select(g => (double)(g.MemoryBytes ?? 0)).DefaultIfEmpty(0).Max();
            return Task.CompletedTask;
        }

        public override async Task OnShownAsync()
        {
            if (!_subscribed && Host is INotifyPropertyChanged host)
            {
                // Detecta a saída da página sem precisar de gancho no MainViewModel: ao trocar de página, a coleta para.
                host.PropertyChanged += OnHostChanged;
                _subscribed = true;
            }
            if (!_userPaused) Start();
            if (!_healthLoaded) await LoadHealthAsync();
        }

        private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedPage) || sender is not MainViewModel main) return;
            if (!ReferenceEquals(main.SelectedPage, this)) Stop(Loc.T("Mon.ParadoColetaSoRoda"));
        }

        /// <summary>Começa (ou retoma) a coleta a 1 Hz. A primeira vez abre os contadores em segundo plano.</summary>
        public void Start()
        {
            if (Running) return;
            Running = true;
            LiveStatus = Loc.T("Mon.Vivo1LeituraSegundo");
            _timer.Start();
            if (_session is null && _starting is null) _starting = OpenSessionAsync();
        }

        /// <summary>Para a coleta (contadores ficam abertos, sem custo enquanto ninguém coleta).</summary>
        public void Stop(string? reason = null)
        {
            reason ??= Loc.T("Mon.Parado");
            _timer.Stop();
            Running = false;
            LiveStatus = reason;
        }

        private async Task OpenSessionAsync()
        {
            try
            {
                _session = await Task.Run(() => new MonitorSession(new WindowsPerformanceSource(), new WindowsProcessSource(), new WindowsNvidiaSource()));
                UnavailableNote = _session.Unavailable.Count == 0 ? "" : string.Join(Environment.NewLine, _session.Unavailable);
                if (!_session.NvidiaAvailable) BuildTemperatures(null, []);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Stop(Loc.T("Mon.Indisponivel"));
                Message = Loc.F("Mon.NaoFoiPossivelAbrir", ex.Message);
            }
            finally { _starting = null; }
        }

        private async void OnTick(object? sender, EventArgs e)
        {
            var session = _session;
            if (_ticking || session is null) return;
            if (Application.Current?.MainWindow?.WindowState == WindowState.Minimized) return; // janela minimizada: não coleta
            _ticking = true;
            try
            {
                var tick = await Task.Run(session.Tick);
                Apply(tick, session.History);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Message = Loc.F("Mon.FalhaNumaLeituraN", ex.Message);
            }
            finally { _ticking = false; }
        }

        // ------------------------------------------------------------------ aplicar uma leitura na tela

        private void Apply(MonitorTick tick, MonitorHistory history)
        {
            var s = tick.Snapshot;

            // CPU
            IsWarmingUp = false;
            Cpu.Value = N0(s.Cpu.TotalPercent);
            Cpu.Number = s.Cpu.TotalPercent;
            Cpu.Detail = s.Cpu.EffectiveMhz is { } mhz
                ? Loc.F("Mon.NEfetivoNN", Ghz(mhz), (s.Cpu.BaseMhz is { } b ? Loc.F("Mon.BaseN", Ghz(b)) : ""), s.Cpu.Cores.Count)
                : Loc.F("Mon.NProcessadoresLogicos", s.Cpu.Cores.Count);
            Cpu.Series.Load(history.Cpu, 100);
            SyncCores(s.Cpu.Cores);

            // GPU
            if (s.Gpu is { } g)
            {
                Gpu.Value = N0(g.OverallPercent);
                Gpu.Number = g.OverallPercent;
                var mem = g.DedicatedBytes is { } d
                    ? _gpuDedicatedTotal > d ? Loc.F("Mon.VramNN", Bytes(d), Bytes(_gpuDedicatedTotal)) : Loc.F("Mon.VramUsoN", Bytes(d))
                    : Loc.T("Mon.VramSemLeitura");
                Gpu.Detail = Loc.F("Mon.NNMaisOcupado", mem, (g.Engines.Count > 0 ? g.Engines[0].Label : "3D"));
                SyncRows(GpuEngines, g.Engines.Where(x => x.Percent >= 0.5 || x.Type is "3D" or "Copy" or "VideoEncode" or "VideoDecode").Take(6)
                    .Select(x => (x.Type, x.Label, x.Percent)).ToList());
            }
            else
            {
                Gpu.Value = "—";
                Gpu.Number = double.NaN;
                Gpu.Detail = Loc.T("Mon.WindowsNaoExposContadores");
                GpuEngines.Clear();
            }
            Gpu.Series.Load(history.Gpu, 100);

            // Memória
            if (s.Memory is { } m)
            {
                Ram.Value = N0(m.UsedPercent);
                Ram.Number = m.UsedPercent;
                Ram.Detail = Loc.F("Mon.NNDisponivelN", Bytes(m.UsedBytes), Bytes(m.TotalBytes), Bytes(m.AvailableBytes), Bytes(m.CacheBytes)) +
                             Loc.F("Mon.ComprometidaNN", Bytes(m.CommitBytes), Bytes(m.CommitLimitBytes));
            }
            Ram.Series.Load(history.Memory, 100);

            // Disco
            Disk.Value = N0(s.DiskActiveMax);
            Disk.Number = s.DiskActiveMax;
            var read = s.Disks.Sum(x => x.ReadBytesPerSec);
            var write = s.Disks.Sum(x => x.WriteBytesPerSec);
            Disk.Detail = s.Disks.Count == 0 ? Loc.T("Mon.SemContadoresDisco") : Loc.F("Mon.AtivoMaisOcupadoLeitura", Rate(read), Rate(write));
            Disk.Series.Load(history.Disk, 100);
            SyncActivity(Disks, s.Disks.Select(x => (x.Instance, x.Label, x.ActivePercent,
                Loc.F("Mon.NAtivoLerN", N0(x.ActivePercent), Rate(x.ReadBytesPerSec), Rate(x.WriteBytesPerSec), x.Queue.ToString("0.#", Pt)))).ToList());

            // Rede
            var net = s.NetworkTotalBytesPerSec;
            Net.Value = Rate(net, withUnit: false);
            Net.Unit = RateUnit(net);
            var sent = s.Networks.Sum(x => x.SentBytesPerSec);
            var recv = s.Networks.Sum(x => x.ReceivedBytesPerSec);
            Net.Detail = $"↓ {Rate(recv)} · ↑ {Rate(sent)}";
            Net.Series.Load(history.Network, 0);
            SyncActivity(Networks, s.Networks.Where(x => x.TotalBytesPerSec > 0 || x.LinkBitsPerSec is not null).Take(4)
                .Select(x => (x.Name, x.Name, x.LinkBitsPerSec is { } link and > 0 ? Math.Min(100, x.TotalBytesPerSec * 8 / link * 100) : 0,
                    $"↓ {Rate(x.ReceivedBytesPerSec)} · ↑ {Rate(x.SentBytesPerSec)}{(x.LinkBitsPerSec is { } l ? Loc.F("Mon.LinkN", LinkSpeed(l)) : "")}")).ToList());

            // Processos, gargalo, temperaturas
            _lastProcesses = s.Processes;
            RefreshProcesses();
            BottleneckTitle = tick.Verdict.Title;
            BottleneckText = tick.Verdict.Explanation;
            BottleneckTone = tick.Verdict.Tone;
            BuildTemperatures(s, tick.Nvidia);
        }

        private void SyncCores(IReadOnlyList<CoreUsage> cores)
        {
            while (Cores.Count > cores.Count) Cores.RemoveAt(Cores.Count - 1);
            while (Cores.Count < cores.Count) Cores.Add(new CoreBar());
            for (var i = 0; i < cores.Count; i++)
            {
                var c = cores[i];
                Cores[i].Update(c.Percent, Loc.F("Mon.NucleoLogicoNN", c.Index, N0(c.Percent), (c.Mhz is { } m ? $" · {Ghz(m)}" : "")));
            }
        }

        private static void SyncRows(ObservableCollection<MeterRow> rows, IReadOnlyList<(string Key, string Label, double Percent)> data)
        {
            for (var i = rows.Count - 1; i >= 0; i--)
                if (!data.Any(d => d.Key == rows[i].Key)) rows.RemoveAt(i);
            foreach (var d in data)
            {
                var row = rows.FirstOrDefault(r => r.Key == d.Key);
                if (row is null) rows.Add(row = new MeterRow(d.Key, d.Label));
                row.Update(d.Percent, $"{N0(d.Percent)}%");
            }
        }

        private static void SyncActivity(ObservableCollection<ActivityRow> rows, IReadOnlyList<(string Key, string Label, double Percent, string Detail)> data)
        {
            for (var i = rows.Count - 1; i >= 0; i--)
                if (!data.Any(d => d.Key == rows[i].Key)) rows.RemoveAt(i);
            foreach (var d in data)
            {
                var row = rows.FirstOrDefault(r => r.Key == d.Key);
                if (row is null) rows.Add(row = new ActivityRow(d.Key, d.Label));
                row.SetActivity(d.Percent, d.Detail);
            }
        }

        private void RefreshProcesses()
        {
            var ordered = (SortByMemory
                    ? _lastProcesses.OrderByDescending(p => p.WorkingSetBytes)
                    : _lastProcesses.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.WorkingSetBytes))
                .Take(VisibleProcesses).ToList();
            while (Processes.Count > ordered.Count) Processes.RemoveAt(Processes.Count - 1);
            while (Processes.Count < ordered.Count) Processes.Add(new ProcessRow());
            for (var i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                Processes[i].Update(p.Name, p.Pid, $"{p.CpuPercent.ToString("0.0", Pt)}%", Bytes(p.WorkingSetBytes),
                    p.GpuPercent is { } gp ? $"{N0(gp)}%" : "—", p.CpuPercent / 100);
            }
        }

        private void BuildTemperatures(MonitorSnapshot? s, IReadOnlyList<NvidiaGpuReading> nvidia)
        {
            var lines = new List<string>();
            foreach (var g in nvidia)
            {
                var parts = new List<string>();
                if (g.TemperatureC is { } t) parts.Add($"{N0(t)} °C");
                if (g.UtilizationPercent is { } u) parts.Add(Loc.F("Mon.UsoN", N0(u)));
                if (g.GraphicsClockMhz is { } c) parts.Add($"{N0(c)} MHz");
                if (g.PowerWatts is { } w) parts.Add($"{w.ToString("0", Pt)} W");
                lines.Add(Loc.F("Mon.GpuNvidiaNN", g.Index, string.Join(" · ", parts)));
            }
            foreach (var z in s?.ThermalZones ?? [])
                lines.Add(Loc.F("Mon.ZonaTermicaAcpiN", z.Zone.TrimStart('\\'), z.Celsius.ToString("0.0", Pt)));
            if (!lines.SequenceEqual(Temperatures))
            {
                Temperatures.Clear();
                foreach (var l in lines) Temperatures.Add(l);
            }

            var note = Loc.T("Mon.CpuWindowsNaoTem");
            note += _session?.NvidiaAvailable == true
                ? Loc.T("Mon.GpuLidaPeloNvidia")
                : Loc.T("Mon.GpuTemperaturaSoAparece");
            if (s?.ThermalZones.Count > 0)
                note += Loc.T("Mon.ZonasAcpiSaoSensores");
            TemperatureNote = note;
        }

        // ------------------------------------------------------------------ saúde dos discos

        private Task LoadHealthAsync() => RunAsync(async () =>
        {
            _healthLoaded = true;
            DiskHealthNote = Loc.T("Mon.LendoSaudeDiscosPode");
            HealthLoading = true;
            DiskHealthReport report;
            try { report = await new WindowsDiskHealthSource().ReadAsync(CancellationToken.None); }
            finally { HealthLoading = false; }
            DiskHealth.Clear();
            foreach (var e in report.Disks) DiskHealth.Add(DiskHealthRow.From(e));
            var notes = new List<string>();
            if (report.Error is not null) notes.Add(report.Error);
            if (report.Disks.Any(d => !d.Disk.ReliabilityAvailable))
                notes.Add(report.Elevated
                    ? Loc.T("Mon.AlgunsDiscosNaoInformam")
                    : Loc.T("Mon.TemperaturaDesgasteErrosVem"));
            notes.Add(Loc.T("Mon.CriteriosCriticoWindowsDiz"));
            DiskHealthNote = string.Join(Environment.NewLine, notes);
        });

        // ------------------------------------------------------------------ formatação

        internal static string N0(double v) => Math.Round(v).ToString("0", Pt);

        internal static string Ghz(double mhz) => $"{(mhz / 1000).ToString("0.00", Pt)} GHz";

        internal static string Bytes(double b)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return $"{b.ToString(b >= 100 || i == 0 ? "0" : "0.0", Pt)} {units[i]}";
        }

        internal static string Rate(double bytesPerSec, bool withUnit = true)
        {
            var (v, unit) = Scale(bytesPerSec);
            var text = v.ToString(v >= 100 ? "0" : "0.0", Pt);
            return withUnit ? $"{text} {unit}" : text;
        }

        internal static string RateUnit(double bytesPerSec) => Scale(bytesPerSec).Unit;

        private static (double Value, string Unit) Scale(double b)
        {
            string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
            var i = 0;
            while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
            return (b, units[i]);
        }

        internal static string LinkSpeed(double bits) =>
            bits >= 1e9 ? $"{(bits / 1e9).ToString("0.#", Pt)} Gb/s" : $"{(bits / 1e6).ToString("0", Pt)} Mb/s";
    }

    // ------------------------------------------------------------------ itens da tela

    /// <summary>
    /// Série de um mini-gráfico: um buffer fixo de 300 pontos (5 min) reaproveitado a cada segundo (sem alocar), e um evento
    /// para o desenho se refazer. <see cref="Maximum"/> 0 = escala automática (rede).
    /// </summary>
    public sealed class SparkSeries
    {
        private readonly double[] _values = new double[MonitorHistory.DefaultCapacity];

        public event EventHandler? Changed;

        public int Count { get; private set; }
        public int Capacity => _values.Length;
        public double Maximum { get; private set; }
        public ReadOnlySpan<double> Values => _values.AsSpan(0, Count);

        public void Load(RingBuffer<double> source, double maximum)
        {
            ArgumentNullException.ThrowIfNull(source);
            Count = source.CopyTo(_values);
            Maximum = maximum;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class MonitorGauge : ObservableObject
    {
        private string _value = "—", _detail = "", _unit;
        private double _number = double.NaN;

        public MonitorGauge(string label, string unit, double maximum)
        {
            Label = label;
            _unit = unit;
            Maximum = maximum;
        }

        public string Label { get; }
        public double Maximum { get; }
        public SparkSeries Series { get; } = new();
        public string Value { get => _value; set => Set(ref _value, value); }
        /// <summary>O mesmo valor como número (anel do medidor); NaN = sem leitura.</summary>
        public double Number { get => _number; set => Set(ref _number, value); }
        public string Unit { get => _unit; set => Set(ref _unit, value); }
        public string Detail { get => _detail; set => Set(ref _detail, value); }
    }

    public sealed class CoreBar : ObservableObject
    {
        private double _fraction;
        private string _tip = "";
        private bool _hot;

        public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
        public string Tip { get => _tip; private set => Set(ref _tip, value); }
        /// <summary>Núcleo a 90% ou mais (desenhado em âmbar forte).</summary>
        public bool Hot { get => _hot; private set => Set(ref _hot, value); }

        public void Update(double percent, string tip)
        {
            Fraction = Math.Clamp(percent / 100, 0.02, 1); // um fio mínimo para o núcleo parado continuar visível
            Hot = percent >= 90;
            Tip = tip;
        }
    }

    public class MeterRow : ObservableObject
    {
        private double _fraction;
        private string _valueText = "";

        public MeterRow(string key, string label)
        {
            Key = key;
            Label = label;
        }

        public string Key { get; }
        public string Label { get; }
        public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
        public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }

        public void Update(double percent, string valueText)
        {
            Fraction = Math.Clamp(percent / 100, 0, 1);
            ValueText = valueText;
        }
    }

    public sealed class ActivityRow(string key, string label) : MeterRow(key, label)
    {
        private string _detail = "";
        public string Detail { get => _detail; private set => Set(ref _detail, value); }

        public void SetActivity(double percent, string detail)
        {
            Update(percent, $"{MonitorPage.N0(percent)}%");
            Detail = detail;
        }
    }

    public sealed class ProcessRow : ObservableObject
    {
        private string _name = "", _cpu = "", _memory = "", _gpu = "";
        private int _pid;
        private double _cpuFraction;

        public string Name { get => _name; private set => Set(ref _name, value); }
        public int Pid { get => _pid; private set => Set(ref _pid, value); }
        public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }
        public string Memory { get => _memory; private set => Set(ref _memory, value); }
        public string Gpu { get => _gpu; private set => Set(ref _gpu, value); }
        public double CpuFraction { get => _cpuFraction; private set => Set(ref _cpuFraction, value); }

        public void Update(string name, int pid, string cpu, string memory, string gpu, double cpuFraction)
        {
            Name = name;
            Pid = pid;
            Cpu = cpu;
            Memory = memory;
            Gpu = gpu;
            CpuFraction = Math.Clamp(cpuFraction, 0, 1);
        }
    }

    public sealed record DiskHealthRow(string Name, string Kind, string LevelText, string Tone, string Readings, string Reasons)
    {
        private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

        /// <summary>Tom do selo (verde ok, âmbar atenção, vermelho crítico, ciano sem leitura).</summary>
        public Views.Controls.Tone ToneValue => Tone switch
        {
            "good" => Views.Controls.Tone.Good,
            "warn" => Views.Controls.Tone.Warn,
            "bad" => Views.Controls.Tone.Bad,
            _ => Views.Controls.Tone.Info,
        };

        public static DiskHealthRow From(DiskHealthEntry e)
        {
            ArgumentNullException.ThrowIfNull(e);
            var d = e.Disk;
            var kind = string.Join(" · ", new[] { d.BusType, d.MediaType is "Unspecified" ? "" : d.MediaType, d.SizeBytes is { } s ? MonitorPage.Bytes(s) : "" }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            var readings = new List<string>();
            if (d.TemperatureC is { } t and > 0) readings.Add($"{t:0} °C{(d.TemperatureMaxC is { } mx and > 0 ? Loc.F("Mon.MaxRegistradaNC", mx) : "")}");
            if (d.WearPercent is { } w) readings.Add(Loc.F("Mon.DesgasteN", w));
            if (d.PowerOnHours is { } h) readings.Add(Loc.F("Mon.NHLigado", h.ToString("N0", Pt)));
            if (d.ReadErrorsTotal is { } re) readings.Add(Loc.F("Mon.NErrosLeituraCorrigidos", re.ToString("N0", Pt)));
            var unc = (d.ReadErrorsUncorrected ?? 0) + (d.WriteErrorsUncorrected ?? 0);
            if (d.ReadErrorsUncorrected is not null || d.WriteErrorsUncorrected is not null) readings.Add(Loc.F("Mon.NNaoCorrigidos", unc.ToString("N0", Pt)));
            if (readings.Count == 0) readings.Add(Loc.T("Mon.SemContadoresConfiabilidade"));
            var tone = e.Verdict.Level switch
            {
                DiskHealthLevel.Ok => "good",
                DiskHealthLevel.Attention => "warn",
                DiskHealthLevel.Critical => "bad",
                _ => "info",
            };
            return new DiskHealthRow(string.IsNullOrWhiteSpace(d.FriendlyName) ? Loc.F("Auto.DiscoN", d.DeviceId) : d.FriendlyName, kind, e.Verdict.LevelText, tone,
                string.Join(" · ", readings), string.Join(Environment.NewLine, e.Verdict.Reasons));
        }
    }
}

namespace Nexus.Desktop.Views.Monitor
{
    using System.Windows.Media;
    using Nexus.Desktop.ViewModels;

    /// <summary>
    /// Mini-gráfico de linha leve: uma StreamGeometry congelada por segundo (área + traço), sem elemento por ponto e sem
    /// animação (a linha só muda quando chega a leitura nova, então respeita as animações desligadas do Windows). Os pontos
    /// entram pela direita; o eixo X sempre representa 5 minutos.
    /// </summary>
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(SparkSeries), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender, OnBrushChanged));

        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private Pen? _pen;
        private Brush? _fill;
        private bool _attached;

        public Sparkline()
        {
            SnapsToDevicePixels = true;
            Loaded += (_, _) => Attach(Series);
            Unloaded += (_, _) => Detach(Series);
        }

        public SparkSeries? Series { get => (SparkSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
        public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

        private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var s = (Sparkline)d;
            s.Detach(e.OldValue as SparkSeries);
            if (s.IsLoaded) s.Attach(e.NewValue as SparkSeries);
        }

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var s = (Sparkline)d;
            s._pen = null;
            s._fill = null;
        }

        // Inscrição só enquanto carregado: a série vive no ViewModel; sem isso, cada visita à página deixaria um controle preso.
        private void Attach(SparkSeries? series)
        {
            if (series is null || _attached) return;
            series.Changed += OnData;
            _attached = true;
            InvalidateVisual();
        }

        private void Detach(SparkSeries? series)
        {
            if (series is null || !_attached) return;
            series.Changed -= OnData;
            _attached = false;
        }

        private void OnData(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext drawingContext)
        {
            ArgumentNullException.ThrowIfNull(drawingContext);
            var dc = drawingContext;
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // área de clique/tooltip

            if (GridBrush is { } grid)
            {
                var gp = new Pen(grid, 1);
                gp.Freeze();
                for (var i = 1; i <= 3; i++)
                {
                    var y = Math.Round(h * i / 4) + 0.5;
                    dc.DrawLine(gp, new Point(0, y), new Point(w, y));
                }
            }

            var series = Series;
            if (series is null || series.Count < 2) return;
            var values = series.Values;
            var max = series.Maximum;
            if (max <= 0)
            {
                foreach (var v in values) max = Math.Max(max, v);
                max = max <= 0 ? 1 : max * 1.15;
            }

            var step = w / (series.Capacity - 1);
            var x0 = w - (values.Length - 1) * step;
            const double pad = 1.5;
            double Y(double v) => h - pad - Math.Clamp(v / max, 0, 1) * (h - 2 * pad);

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var c = line.Open())
            using (var a = area.Open())
            {
                c.BeginFigure(new Point(x0, Y(values[0])), false, false);
                a.BeginFigure(new Point(x0, h), true, true);
                a.LineTo(new Point(x0, Y(values[0])), false, false);
                for (var i = 1; i < values.Length; i++)
                {
                    var p = new Point(x0 + i * step, Y(values[i]));
                    c.LineTo(p, true, true);
                    a.LineTo(p, false, false);
                }
                a.LineTo(new Point(w, h), false, false);
            }
            line.Freeze();
            area.Freeze();

            if (_pen is null && Stroke is { } stroke)
            {
                _pen = new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round };
                _pen.Freeze();
                if (stroke is SolidColorBrush solid)
                {
                    var color = solid.Color;
                    _fill = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
                    _fill.Freeze();
                }
            }
            if (_fill is not null) dc.DrawGeometry(_fill, null, area);
            if (_pen is not null) dc.DrawGeometry(null, _pen, line);
        }
    }
}
