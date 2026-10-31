using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Monitoring;

/// <summary>
/// Transforma uma leitura crua (<see cref="RawSample"/>) e a lista de processos em um <see cref="MonitorSnapshot"/>.
/// Lógica pura: testada com amostras falsas. As regras seguem o Gerenciador de Tarefas sempre que ele tem uma regra pública.
/// </summary>
public static partial class MonitorAggregator
{
    public static MonitorSnapshot Aggregate(RawSample raw, IReadOnlyList<ProcessUsage>? processes = null, IReadOnlyDictionary<int, string>? processNames = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var adapters = Gpu(raw.GpuEngine, raw.GpuDedicated, processNames);
        var primary = adapters
            .OrderByDescending(a => a.DedicatedBytes ?? 0)
            .ThenByDescending(a => a.OverallPercent)
            .FirstOrDefault();

        var procs = processes ?? [];
        if (primary is not null && procs.Count > 0)
        {
            var byPid = primary.Processes.ToDictionary(p => p.Pid, p => p.Percent);
            procs = [.. procs.Select(p => byPid.TryGetValue(p.Pid, out var g) ? p with { GpuPercent = g } : p)];
        }

        return new MonitorSnapshot
        {
            At = raw.At,
            Cpu = Cpu(raw.ProcessorUtility, raw.ProcessorPerformance, raw.ProcessorFrequency),
            Memory = Memory(raw.Memory),
            Disks = Disks(raw.DiskIdle, raw.DiskReadBytes, raw.DiskWriteBytes, raw.DiskQueue),
            Networks = Networks(raw.NetSentBytes, raw.NetReceivedBytes, raw.NetBandwidth),
            GpuAdapters = adapters,
            Gpu = primary,
            Processes = procs,
            ThermalZones = Thermal(raw.ThermalZones),
        };
    }

    // ------------------------------------------------------------------ CPU

    /// <summary>
    /// Instâncias de \Processor Information são "grupo,núcleo" (ex.: "0,3"), mais "0,_Total" por grupo e "_Total" geral.
    /// Total = "_Total" (ou a média dos núcleos, se faltar). Frequência efetiva = % Processor Performance × frequência nominal.
    /// % Processor Utility passa de 100 quando o turbo está ativo; como o Gerenciador de Tarefas, limitamos a 100.
    /// </summary>
    public static CpuUsage Cpu(CounterSet utility, CounterSet performance, CounterSet frequency)
    {
        ArgumentNullException.ThrowIfNull(utility);
        ArgumentNullException.ThrowIfNull(performance);
        ArgumentNullException.ThrowIfNull(frequency);

        var cores = new List<(int Group, int Number, string Instance, double Value)>();
        foreach (var (instance, value) in utility.Values)
        {
            if (TryParseCore(instance, out var g, out var n)) cores.Add((g, n, instance, value));
        }
        cores.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Number.CompareTo(b.Number));

        var list = new List<CoreUsage>(cores.Count);
        for (var i = 0; i < cores.Count; i++)
        {
            var c = cores[i];
            list.Add(new CoreUsage(i, c.Instance, Clamp(c.Value), Mhz(performance.Get(c.Instance), frequency.Get(c.Instance))));
        }

        var total = utility.Get("_Total") ?? (list.Count > 0 ? list.Average(c => c.Percent) : 0);
        var baseMhz = frequency.Get("_Total") ?? (list.Count > 0 && frequency.Get(cores[0].Instance) is { } f0 ? f0 : null);
        var effective = Mhz(performance.Get("_Total"), baseMhz);
        return new CpuUsage(Clamp(total), effective, baseMhz is > 0 ? baseMhz : null, list);
    }

    internal static bool TryParseCore(string instance, out int group, out int number)
    {
        group = number = 0;
        var comma = instance.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0) return false;
        return int.TryParse(instance.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out group)
            && int.TryParse(instance.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static double? Mhz(double? performancePercent, double? baseMhz) =>
        performancePercent is > 0 && baseMhz is > 0 ? Math.Round(performancePercent.Value / 100.0 * baseMhz.Value) : null;

    // ------------------------------------------------------------------ memória

    /// <summary>Em uso = total − disponível (a mesma conta do Gerenciador de Tarefas). Cache é parte do "disponível": não é memória perdida.</summary>
    public static MemoryUsage? Memory(MemoryReading? m) =>
        m is null || m.TotalBytes == 0 ? null
            : new MemoryUsage(m.TotalBytes, m.TotalBytes - Math.Min(m.AvailableBytes, m.TotalBytes), m.AvailableBytes, m.CacheBytes, m.CommitBytes, m.CommitLimitBytes);

    // ------------------------------------------------------------------ disco

    /// <summary>Instâncias de \PhysicalDisk: "0 C: D:" (número do disco + volumes). Ativo = 100 − % Idle Time.</summary>
    public static IReadOnlyList<DiskActivity> Disks(CounterSet idle, CounterSet read, CounterSet write, CounterSet queue)
    {
        ArgumentNullException.ThrowIfNull(idle);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(queue);
        var names = idle.Values.Keys.Concat(read.Values.Keys).Concat(write.Values.Keys)
            .Where(n => !n.Contains("_Total", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        var list = new List<DiskActivity>();
        foreach (var n in names)
        {
            var active = idle.Get(n) is { } i ? Clamp(100 - i) : 0;
            list.Add(new DiskActivity(n, DiskLabel(n), active, Math.Max(0, read.Get(n) ?? 0), Math.Max(0, write.Get(n) ?? 0), Math.Max(0, queue.Get(n) ?? 0)));
        }
        return list;
    }

    public static string DiskLabel(string instance)
    {
        var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return instance;
        var volumes = parts.Skip(1).ToArray();
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? volumes.Length == 0 ? $"Disco {parts[0]}" : $"Disco {parts[0]} · {string.Join(' ', volumes)}"
            : instance;
    }

    // ------------------------------------------------------------------ rede

    public static IReadOnlyList<NetworkActivity> Networks(CounterSet sent, CounterSet received, CounterSet bandwidth)
    {
        ArgumentNullException.ThrowIfNull(sent);
        ArgumentNullException.ThrowIfNull(received);
        ArgumentNullException.ThrowIfNull(bandwidth);
        return [.. sent.Values.Keys.Concat(received.Values.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new NetworkActivity(n, Math.Max(0, sent.Get(n) ?? 0), Math.Max(0, received.Get(n) ?? 0), bandwidth.Get(n) is > 0 and var b ? b : null))
            .OrderByDescending(n => n.TotalBytesPerSec)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // ------------------------------------------------------------------ GPU

    [GeneratedRegex(@"^pid_(?<pid>\d+)_luid_(?<luid>0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(?<phys>\d+)_eng_(?<eng>\d+)_engtype_(?<type>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EngineInstance();

    [GeneratedRegex(@"^luid_(?<luid>0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(?<phys>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterInstance();

    public readonly record struct GpuEngineKey(int Pid, string Adapter, int Engine, string Type);

    public static bool TryParseEngine(string instance, out GpuEngineKey key)
    {
        var m = EngineInstance().Match(instance);
        if (!m.Success)
        {
            key = default;
            return false;
        }
        key = new GpuEngineKey(int.Parse(m.Groups["pid"].ValueSpan, CultureInfo.InvariantCulture),
            $"{m.Groups["luid"].Value.ToLowerInvariant()}_phys_{m.Groups["phys"].Value}",
            int.Parse(m.Groups["eng"].ValueSpan, CultureInfo.InvariantCulture),
            m.Groups["type"].Value);
        return true;
    }

    /// <summary>
    /// Regra do Gerenciador de Tarefas: cada motor físico (adaptador + eng_N) soma o uso de todos os processos; o uso de um
    /// tipo (3D, Copy, VideoEncode…) é o do motor mais ocupado daquele tipo; o uso da placa é o do tipo mais ocupado.
    /// Por processo: o motor em que ele mais usa.
    /// </summary>
    public static IReadOnlyList<GpuAdapterUsage> Gpu(CounterSet engines, CounterSet dedicated, IReadOnlyDictionary<int, string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(dedicated);

        var perEngine = new Dictionary<(string Adapter, int Engine), (string Type, double Sum)>();
        var perProcess = new Dictionary<(string Adapter, int Pid, int Engine), double>();
        foreach (var (instance, value) in engines.Values)
        {
            if (!TryParseEngine(instance, out var k) || value <= 0) continue;
            var ek = (k.Adapter, k.Engine);
            perEngine[ek] = perEngine.TryGetValue(ek, out var cur) ? (cur.Type, cur.Sum + value) : (k.Type, value);
            var pk = (k.Adapter, k.Pid, k.Engine);
            perProcess[pk] = perProcess.GetValueOrDefault(pk) + value;
        }
        // Motores parados também contam (para a placa aparecer mesmo ociosa).
        foreach (var instance in engines.Values.Keys)
        {
            if (TryParseEngine(instance, out var k) && !perEngine.ContainsKey((k.Adapter, k.Engine))) perEngine[(k.Adapter, k.Engine)] = (k.Type, 0);
        }

        var memory = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in dedicated.Values)
        {
            var m = AdapterInstance().Match(instance);
            if (!m.Success) continue;
            var key = $"{m.Groups["luid"].Value.ToLowerInvariant()}_phys_{m.Groups["phys"].Value}";
            memory[key] = memory.GetValueOrDefault(key) + (ulong)Math.Max(0, value);
        }

        var adapters = perEngine.Keys.Select(k => k.Adapter).Concat(memory.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<GpuAdapterUsage>();
        foreach (var adapter in adapters)
        {
            var types = perEngine.Where(e => e.Key.Adapter == adapter)
                .GroupBy(e => e.Value.Type, StringComparer.Ordinal)
                .Select(g => new GpuEngineUsage(g.Key, Clamp(g.Max(e => e.Value.Sum))))
                .OrderByDescending(e => e.Percent).ThenBy(e => e.Type, StringComparer.Ordinal)
                .ToList();
            var procs = perProcess.Where(p => p.Key.Adapter == adapter)
                .GroupBy(p => p.Key.Pid)
                .Select(g => new GpuProcessUsage(g.Key, names is not null && names.TryGetValue(g.Key, out var n) ? n : null, Clamp(g.Max(p => p.Value))))
                .Where(p => p.Percent >= 0.05)
                .OrderByDescending(p => p.Percent)
                .ToList();
            result.Add(new GpuAdapterUsage(adapter, types.Count == 0 ? 0 : types.Max(t => t.Percent), types, procs,
                memory.TryGetValue(adapter, out var mem) ? mem : null));
        }
        return result;
    }

    // ------------------------------------------------------------------ temperatura

    /// <summary>Kelvin → °C; descarta valores impossíveis (zonas ACPI sem sensor costumam devolver 0 K ou valores fixos absurdos).</summary>
    public static IReadOnlyList<ThermalZoneReading> Thermal(CounterSet kelvin)
    {
        ArgumentNullException.ThrowIfNull(kelvin);
        return [.. kelvin.Values
            .Select(kv => new ThermalZoneReading(kv.Key, Math.Round(kv.Value - 273.15, 1)))
            .Where(t => t.Celsius is > 1 and < 130)
            .OrderBy(t => t.Zone, StringComparer.OrdinalIgnoreCase)];
    }

    internal static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 100);
}

/// <summary>
/// CPU por processo pela API de processos: diferença de TotalProcessorTime entre duas leituras, dividida pelo tempo passado
/// e pelo número de processadores lógicos (0–100% da máquina inteira, como o Gerenciador de Tarefas).
/// </summary>
public sealed class ProcessCpuTracker
{
    private readonly int _logicalProcessors;
    private Dictionary<int, (TimeSpan Cpu, DateTime? Start)> _last = [];
    private DateTimeOffset? _lastAt;

    public ProcessCpuTracker(int logicalProcessors) => _logicalProcessors = Math.Max(1, logicalProcessors);

    public IReadOnlyList<ProcessUsage> Update(IReadOnlyList<ProcessReading> processes, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var elapsed = _lastAt is { } prev ? (at - prev).TotalMilliseconds : 0;
        var next = new Dictionary<int, (TimeSpan Cpu, DateTime? Start)>(processes.Count);
        var result = new List<ProcessUsage>(processes.Count);
        foreach (var p in processes)
        {
            double cpu = 0;
            if (p.CpuTime is { } now)
            {
                next[p.Pid] = (now, p.StartTime);
                // Mesmo PID com outro início (ou CPU que "voltou") = outro processo: sem base, 0% nesta leitura.
                if (elapsed > 0 && _last.TryGetValue(p.Pid, out var before) && before.Start == p.StartTime && now >= before.Cpu)
                    cpu = MonitorAggregator.Clamp((now - before.Cpu).TotalMilliseconds / elapsed / _logicalProcessors * 100);
            }
            if (p.Pid != 0) result.Add(new ProcessUsage(p.Pid, p.Name, Math.Round(cpu, 1), p.WorkingSetBytes));
        }
        _last = next;
        _lastAt = at;
        return result;
    }
}
