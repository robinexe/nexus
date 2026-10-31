using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.Lab.Gpu.Windows;

/// <summary>
/// NVML (NVIDIA Management Library) que vem com o driver NVIDIA. Carregada SÓ de caminho fixo (System32 ou DriverStore do
/// próprio driver), nunca do PATH nem da pasta do NEXUS, com <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/>.
/// Somente leitura: nenhuma função "Set" é sequer resolvida.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class NvmlSession : IDisposable
{
    private readonly IntPtr _lib;
    private readonly delegate* unmanaged[Cdecl]<int> _shutdown;
    private readonly IntPtr[] _devices;
    private bool _disposed;

    // Funções resolvidas (null quando o driver é antigo e não tem a função).
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int> _name;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, PciInfo*, int> _pci;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _clock, _maxClock;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _powerLimit, _defaultLimit, _enforcedLimit, _power, _fan, _gen, _maxGen, _width, _maxWidth;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int> _limitRange;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _temp, _tempThreshold;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> _util;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ulong*, int> _reasons;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Memory*, int> _memory;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Memory*, int> _bar1;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int*, int> _pstate;

    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        public fixed byte BusId[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu, Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory { public ulong Total, Free, Used; }

    public string DriverVersion { get; } = "";
    public string LoadedFrom { get; }
    public int Count => _devices.Length;

    private NvmlSession(IntPtr lib, string path)
    {
        _lib = lib;
        LoadedFrom = path;
        IntPtr F(params string[] names)
        {
            foreach (var n in names)
                if (NativeLibrary.TryGetExport(lib, n, out var p)) return p;
            return IntPtr.Zero;
        }
        var init = F("nvmlInit_v2", "nvmlInit");
        if (init == IntPtr.Zero || ((delegate* unmanaged[Cdecl]<int>)init)() != 0)
            throw new InvalidOperationException("nvmlInit falhou (driver NVIDIA sem NVML funcional).");
        _shutdown = (delegate* unmanaged[Cdecl]<int>)F("nvmlShutdown");

        var getCount = (delegate* unmanaged[Cdecl]<uint*, int>)F("nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
        var getHandle = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)F("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
        uint count = 0;
        var devices = new List<IntPtr>();
        if (getCount != null && getHandle != null && getCount(&count) == 0)
            for (uint i = 0; i < Math.Min(count, 16u); i++)
            {
                IntPtr h;
                if (getHandle(i, &h) == 0) devices.Add(h);
            }
        _devices = [.. devices];

        var ver = (delegate* unmanaged[Cdecl]<byte*, uint, int>)F("nvmlSystemGetDriverVersion");
        if (ver != null)
        {
            var b = stackalloc byte[96];
            if (ver(b, 96) == 0) DriverVersion = Encoding.ASCII.GetString(b, Strlen(b, 96));
        }

        _name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)F("nvmlDeviceGetName");
        _pci = (delegate* unmanaged[Cdecl]<IntPtr, PciInfo*, int>)F("nvmlDeviceGetPciInfo_v3", "nvmlDeviceGetPciInfo_v2");
        _clock = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)F("nvmlDeviceGetClockInfo");
        _maxClock = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)F("nvmlDeviceGetMaxClockInfo");
        _powerLimit = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetPowerManagementLimit");
        _defaultLimit = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetPowerManagementDefaultLimit");
        _enforcedLimit = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetEnforcedPowerLimit");
        _limitRange = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)F("nvmlDeviceGetPowerManagementLimitConstraints");
        _power = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetPowerUsage");
        _temp = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)F("nvmlDeviceGetTemperature");
        _tempThreshold = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)F("nvmlDeviceGetTemperatureThreshold");
        _fan = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetFanSpeed");
        _util = (delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int>)F("nvmlDeviceGetUtilizationRates");
        _gen = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetCurrPcieLinkGeneration");
        _maxGen = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetGpuMaxPcieLinkGeneration", "nvmlDeviceGetMaxPcieLinkGeneration");
        _width = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetCurrPcieLinkWidth");
        _maxWidth = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)F("nvmlDeviceGetMaxPcieLinkWidth");
        _reasons = (delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)F("nvmlDeviceGetCurrentClocksEventReasons", "nvmlDeviceGetCurrentClocksThrottleReasons");
        _memory = (delegate* unmanaged[Cdecl]<IntPtr, Memory*, int>)F("nvmlDeviceGetMemoryInfo");
        _bar1 = (delegate* unmanaged[Cdecl]<IntPtr, Memory*, int>)F("nvmlDeviceGetBAR1MemoryInfo");
        _pstate = (delegate* unmanaged[Cdecl]<IntPtr, int*, int>)F("nvmlDeviceGetPerformanceState");
    }

    private static int Strlen(byte* b, int max)
    {
        var n = 0;
        while (n < max && b[n] != 0) n++;
        return n;
    }

    /// <summary>Caminhos fixos onde o driver NVIDIA instala a nvml.dll (nunca o PATH).</summary>
    public static IEnumerable<string> CandidatePaths()
    {
        var system = Environment.SystemDirectory; // C:\Windows\System32
        yield return Path.Combine(system, "nvml.dll");
        var repo = Path.Combine(system, "DriverStore", "FileRepository");
        IEnumerable<string> dirs;
        try { dirs = Directory.Exists(repo) ? Directory.EnumerateDirectories(repo, "nv*").ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dirs = []; }
        foreach (var d in dirs.OrderByDescending(d => SafeWriteTime(d)))
        {
            var p = Path.Combine(d, "nvml.dll");
            if (File.Exists(p)) yield return p;
        }
    }

    private static DateTime SafeWriteTime(string dir)
    {
        try { return Directory.GetLastWriteTimeUtc(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>Abre a NVML; null (com o motivo) quando não há driver NVIDIA ou a biblioteca não inicia.</summary>
    public static NvmlSession? TryOpen(out string status)
    {
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            // Só dentro da pasta de sistema (protegida contra escrita de usuários comuns).
            if (!Path.GetFullPath(path).StartsWith(Environment.SystemDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!NativeLibrary.TryLoad(path, out var lib)) continue;
            try
            {
                var s = new NvmlSession(lib, path);
                status = $"NVML {s.DriverVersion} carregada de {path}";
                return s;
            }
            catch (InvalidOperationException ex)
            {
                NativeLibrary.Free(lib);
                status = ex.Message;
                return null;
            }
        }
        status = "Sem driver NVIDIA (nvml.dll não encontrada no System32/DriverStore).";
        return null;
    }

    private static double? U(delegate* unmanaged[Cdecl]<IntPtr, uint*, int> f, IntPtr d, double scale = 1)
    {
        if (f == null) return null;
        uint v;
        return f(d, &v) == 0 ? v * scale : null;
    }

    private static double? UArg(delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> f, IntPtr d, int arg)
    {
        if (f == null) return null;
        uint v;
        return f(d, arg, &v) == 0 ? v : null;
    }

    /// <summary>Leitura completa de uma placa (≈ 1 ms; pode ser chamada a 1 Hz durante a gravação).</summary>
    public NvidiaReading Read(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var d = _devices[index];
        string? name = null, busId = null;
        if (_name != null)
        {
            var b = stackalloc byte[96];
            if (_name(d, b, 96) == 0) name = Encoding.UTF8.GetString(b, Strlen(b, 96));
        }
        uint? pciId = null, sub = null;
        if (_pci != null)
        {
            PciInfo p;
            if (_pci(d, &p) == 0)
            {
                pciId = p.PciDeviceId;
                sub = p.PciSubSystemId;
                busId = Encoding.ASCII.GetString(p.BusId, Strlen(p.BusId, 32));
            }
        }
        double? minLimit = null, maxLimit = null;
        if (_limitRange != null)
        {
            uint lo, hi;
            if (_limitRange(d, &lo, &hi) == 0) { minLimit = lo / 1000.0; maxLimit = hi / 1000.0; }
        }
        double? util = null, memUtil = null;
        if (_util != null)
        {
            Utilization u;
            if (_util(d, &u) == 0) { util = u.Gpu; memUtil = u.Memory; }
        }
        ulong? reasons = null;
        if (_reasons != null)
        {
            ulong r;
            if (_reasons(d, &r) == 0) reasons = r;
        }
        ulong? total = null, used = null, bar1 = null;
        if (_memory != null)
        {
            Memory m;
            if (_memory(d, &m) == 0) { total = m.Total; used = m.Used; }
        }
        if (_bar1 != null)
        {
            Memory m;
            if (_bar1(d, &m) == 0) bar1 = m.Total;
        }
        int? pstate = null;
        if (_pstate != null)
        {
            int ps;
            if (_pstate(d, &ps) == 0 && ps is >= 0 and <= 15) pstate = ps;
        }
        return new NvidiaReading
        {
            Name = name,
            PciDeviceId = pciId,
            PciSubSystemId = sub,
            BusId = busId,
            GraphicsClockMhz = UArg(_clock, d, 0),
            MemoryClockMhz = UArg(_clock, d, 2),
            MaxGraphicsClockMhz = UArg(_maxClock, d, 0),
            MaxMemoryClockMhz = UArg(_maxClock, d, 2),
            PowerWatts = U(_power, d, 0.001),
            PowerLimitWatts = U(_powerLimit, d, 0.001),
            DefaultPowerLimitWatts = U(_defaultLimit, d, 0.001),
            EnforcedPowerLimitWatts = U(_enforcedLimit, d, 0.001),
            MinPowerLimitWatts = minLimit,
            MaxPowerLimitWatts = maxLimit,
            TemperatureC = UArg(_temp, d, 0),
            SlowdownTemperatureC = UArg(_tempThreshold, d, 1),
            ShutdownTemperatureC = UArg(_tempThreshold, d, 0),
            MaxOperatingTemperatureC = UArg(_tempThreshold, d, 3),
            FanPercent = U(_fan, d),
            UtilizationPercent = util,
            MemoryUtilizationPercent = memUtil,
            PcieGen = (int?)U(_gen, d),
            PcieMaxGen = (int?)U(_maxGen, d),
            PcieWidth = (int?)U(_width, d),
            PcieMaxWidth = (int?)U(_maxWidth, d),
            ReasonsMask = reasons,
            VramTotalBytes = total,
            VramUsedBytes = used,
            Bar1TotalBytes = bar1,
            PerformanceState = pstate,
        };
    }

    public IReadOnlyList<NvidiaReading> ReadAll() => Enumerable.Range(0, Count).Select(Read).ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_shutdown != null) _ = _shutdown();
        NativeLibrary.Free(_lib);
    }
}
