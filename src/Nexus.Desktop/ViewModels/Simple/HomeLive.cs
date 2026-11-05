using System.IO;
using System.Windows;
using System.Windows.Threading;
using Nexus.HardwareDiscovery;
using Nexus.Monitoring;
using Nexus.Monitoring.Windows;

namespace Nexus.Desktop.ViewModels.Simple;

/// <summary>
/// Painel "Seu PC agora" do Início: processador, placa de vídeo, memória e disco ao vivo, em frases simples (TelemetryText).
/// Só leitura, os mesmos contadores da página Monitor (Nexus.Monitoring), sem lista de processos (mais barato). Coleta a 1 Hz
/// só enquanto o Início está aberto e a janela à vista; minimizada ou na bandeja, para. Temperatura: só a da placa NVIDIA
/// (nvidia-smi do driver); a do processador o Windows não informa com segurança, e o painel diz isso em vez de inventar.
/// </summary>
public sealed class HomeLive : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private MonitorSession? _session;
    private Task? _opening;
    private bool _ticking, _ready, _unavailable;
    private int _diskEvery;

    private double _cpuPercent = double.NaN, _gpuPercent = double.NaN, _ramFraction = double.NaN, _diskUsedFraction = double.NaN;
    private string _cpuText = "—", _cpuDetail = "", _cpuSentence = "", _gpuText = "—", _gpuDetail = "", _gpuSentence = "";
    private string _ramUsed = "—", _ramTotal = "", _ramSentence = "", _diskFree = "—", _diskSentence = "";
    private bool _ramWarn, _diskWarn;
    private string _cpuName = "", _gpuName = "";
    private bool _gpuIsNvidia;

    public HomeLive()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    /// <summary>Liga (Início aberto) ou desliga (outra página) a coleta.</summary>
    public void SetActive(bool on)
    {
        if (!on) { _timer.Stop(); return; }
        if (!OperatingSystem.IsWindows() || _unavailable) return;
        _timer.Start();
        if (_session is null && _opening is null) _opening = OpenAsync();
    }

    /// <summary>Nomes das peças e o disco, a cada leitura do PC.</summary>
    public void SetHardware(SystemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        CpuName = Shorten(facts.CpuModel ?? "");
        var gpu = SimpleHealth.MainGpu(facts.Gpus);
        GpuName = Shorten(gpu?.Name ?? "");
        _gpuIsNvidia = (gpu?.Name ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                       (gpu?.Vendor ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        if (gpu?.MemoryBytes is { } vram && vram > 0 && _gpuIsNvidia) GpuMemory = DesignMathBytes(vram);
        ReadDisk();
    }

    private static string DesignMathBytes(ulong bytes) => Nexus.Desktop.Views.Controls.DesignMath.Bytes(bytes);

    /// <summary>"AMD Ryzen 5 5600H with Radeon Graphics" → "AMD Ryzen 5 5600H"; "NVIDIA GeForce GTX 1650" → "GeForce GTX 1650".</summary>
    private static string Shorten(string name)
    {
        var n = name.Trim();
        var cut = n.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) n = n[..cut];
        foreach (var junk in new[] { "(R)", "(TM)", "(tm)", "(r)", " CPU", " Processor" })
            n = n.Replace(junk, "", StringComparison.Ordinal);
        if (n.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) && n.Length > 7) n = n[7..];
        return string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task OpenAsync()
    {
        try
        {
            _session = await Task.Run(() => new MonitorSession(new WindowsPerformanceSource(), null, new WindowsNvidiaSource()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _unavailable = true;
            _timer.Stop();
            Nexus.Desktop.Services.Shell.UiWatchdog.Note("Início ao vivo: " + ex.Message);
            OnPropertyChanged(nameof(IsUnavailable));
        }
        finally { _opening = null; }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        var session = _session;
        if (_ticking || session is null) return;
        var window = Application.Current?.MainWindow;
        if (window is null || !window.IsVisible || window.WindowState == WindowState.Minimized) return;
        _ticking = true;
        try
        {
            var tick = await Task.Run(session.Tick);
            Apply(tick);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Nexus.Desktop.Services.Shell.UiWatchdog.Note("Início ao vivo (leitura): " + ex.Message);
        }
        finally { _ticking = false; }
    }

    private void Apply(MonitorTick tick)
    {
        var s = tick.Snapshot;
        CpuPercent = s.Cpu.TotalPercent;
        CpuText = Math.Round(s.Cpu.TotalPercent).ToString("0", Loc.Culture);
        CpuDetail = TelemetryText.Ghz(s.Cpu.EffectiveMhz);
        CpuSentence = TelemetryText.Cpu(s.Cpu.TotalPercent);

        double? temp = _gpuIsNvidia && tick.Nvidia.Count > 0 ? tick.Nvidia[0].TemperatureC : null;
        var gpu = s.Gpu?.OverallPercent ?? double.NaN;
        GpuPercent = gpu;
        GpuText = double.IsFinite(gpu) ? Math.Round(gpu).ToString("0", Loc.Culture) : "—";
        GpuDetail = TelemetryText.Celsius(temp) is { Length: > 0 } c ? c : GpuMemory;
        GpuSentence = TelemetryText.Gpu(gpu, temp);

        if (s.Memory is { TotalBytes: > 0 } m)
        {
            RamFraction = (double)m.UsedBytes / m.TotalBytes;
            RamUsed = TelemetryText.Gigabytes(m.UsedBytes);
            RamTotal = Loc.F("Live.Ram.Of", TelemetryText.Gigabytes(m.TotalBytes));
            (RamSentence, RamWarn) = TelemetryText.Ram(RamFraction);
        }

        if (++_diskEvery >= 30) { _diskEvery = 0; ReadDisk(); }
        if (!_ready) { _ready = true; OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(IsWarmingUp)); }
    }

    /// <summary>Espaço do disco do Windows (DriveInfo, só leitura). A cada 30 s e a cada leitura do PC.</summary>
    private void ReadDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return;
            var d = new DriveInfo(root);
            if (!d.IsReady || d.TotalSize <= 0) return;
            DiskFree = TelemetryText.Gigabytes(d.AvailableFreeSpace);
            DiskTotal = Loc.F("Live.Disk.Of", DesignMathBytes((ulong)d.TotalSize));
            DiskUsedFraction = 1 - (double)d.AvailableFreeSpace / d.TotalSize;
            (DiskSentence, DiskWarn) = TelemetryText.Disk(d.AvailableFreeSpace, d.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* sem leitura: fica "—" */ }
    }

    public bool IsReady => _ready;
    public bool IsWarmingUp => !_ready && !_unavailable;
    public bool IsUnavailable => _unavailable;

    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }
    public string GpuName { get => _gpuName; private set { if (Set(ref _gpuName, value)) OnPropertyChanged(nameof(HasGpu)); } }
    public bool HasGpu => _gpuName.Length > 0;
    public string GpuMemory { get; private set; } = "";

    public double CpuPercent { get => _cpuPercent; private set => Set(ref _cpuPercent, value); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string CpuDetail { get => _cpuDetail; private set => Set(ref _cpuDetail, value); }
    public string CpuSentence { get => _cpuSentence; private set => Set(ref _cpuSentence, value); }

    public double GpuPercent { get => _gpuPercent; private set => Set(ref _gpuPercent, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string GpuDetail { get => _gpuDetail; private set => Set(ref _gpuDetail, value); }
    public string GpuSentence { get => _gpuSentence; private set => Set(ref _gpuSentence, value); }

    public double RamFraction { get => _ramFraction; private set => Set(ref _ramFraction, value); }
    public string RamUsed { get => _ramUsed; private set => Set(ref _ramUsed, value); }
    public string RamTotal { get => _ramTotal; private set => Set(ref _ramTotal, value); }
    public string RamSentence { get => _ramSentence; private set => Set(ref _ramSentence, value); }
    public bool RamWarn { get => _ramWarn; private set => Set(ref _ramWarn, value); }

    public double DiskUsedFraction { get => _diskUsedFraction; private set => Set(ref _diskUsedFraction, value); }
    public string DiskFree { get => _diskFree; private set => Set(ref _diskFree, value); }
    public string DiskTotal { get => _diskTotal; private set => Set(ref _diskTotal, value); }
    private string _diskTotal = "";
    public string DiskSentence { get => _diskSentence; private set => Set(ref _diskSentence, value); }
    public bool DiskWarn { get => _diskWarn; private set => Set(ref _diskWarn, value); }
}
