using Nexus.Core;
using Nexus.Monitoring;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

[Trait("Category", "Unit")]
public sealed class MonitoringTests
{
    // ------------------------------------------------------------------ CPU

    [Fact]
    public void Cpu_aggregates_cores_in_group_order_and_caps_turbo_at_100()
    {
        var util = CounterSet.Of(("_Total", 55), ("0,_Total", 55), ("0,1", 130), ("0,0", 20), ("0,10", 5), ("0,2", 65));
        var perf = CounterSet.Of(("_Total", 120), ("0,0", 100), ("0,1", 150));
        var freq = CounterSet.Of(("_Total", 3600), ("0,0", 3600), ("0,1", 3600));

        var cpu = MonitorAggregator.Cpu(util, perf, freq);

        Assert.Equal(55, cpu.TotalPercent);
        Assert.Equal(["0,0", "0,1", "0,2", "0,10"], cpu.Cores.Select(c => c.Instance));
        Assert.Equal([0, 1, 2, 3], cpu.Cores.Select(c => c.Index));
        Assert.Equal(100, cpu.Cores[1].Percent);          // 130% de utilidade (turbo) vira 100%
        Assert.Equal(5400, cpu.Cores[1].Mhz);              // 150% × 3600
        Assert.Null(cpu.Cores[2].Mhz);                     // sem % Processor Performance
        Assert.Equal(4320, cpu.EffectiveMhz);              // 120% × 3600
        Assert.Equal(3600, cpu.BaseMhz);
        Assert.Equal(100, cpu.MaxCorePercent);
    }

    [Fact]
    public void Cpu_without_total_averages_cores_and_handles_multiple_groups()
    {
        var util = CounterSet.Of(("1,0", 80), ("0,1", 40), ("0,0", 0), ("1,_Total", 80));
        var cpu = MonitorAggregator.Cpu(util, CounterSet.Empty, CounterSet.Empty);
        Assert.Equal(["0,0", "0,1", "1,0"], cpu.Cores.Select(c => c.Instance));
        Assert.Equal(40, cpu.TotalPercent, 3);
        Assert.Null(cpu.EffectiveMhz);
        Assert.Null(cpu.BaseMhz);
    }

    [Fact]
    public void Cpu_empty_sample_is_zero_not_exception()
    {
        var cpu = MonitorAggregator.Cpu(CounterSet.Empty, CounterSet.Empty, CounterSet.Empty);
        Assert.Equal(0, cpu.TotalPercent);
        Assert.Empty(cpu.Cores);
    }

    // ------------------------------------------------------------------ GPU

    private const string A = "luid_0x00000000_0x0000D1B4_phys_0";
    private const string B = "luid_0x00000000_0x0000AAAA_phys_0";

    [Fact]
    public void Gpu_sums_processes_per_engine_and_takes_busiest_engine_per_type()
    {
        var engines = CounterSet.Of(
            ($"pid_100_{A}_eng_0_engtype_3D", 60),
            ($"pid_200_{A}_eng_0_engtype_3D", 30),         // motor 0: 90%
            ($"pid_100_{A}_eng_1_engtype_3D", 10),         // outro motor 3D: 10% (não soma com o motor 0)
            ($"pid_300_{A}_eng_4_engtype_VideoEncode", 25),
            ($"pid_100_{A}_eng_2_engtype_Copy", 5),
            ($"pid_100_{A}_eng_3_engtype_VideoDecode", 0));
        var memory = CounterSet.Of((A, 2_000_000_000));
        var names = new Dictionary<int, string> { [100] = "jogo", [200] = "dwm", [300] = "obs64" };

        var adapters = MonitorAggregator.Gpu(engines, memory, names);

        var gpu = Assert.Single(adapters);
        Assert.Equal(90, gpu.OverallPercent);
        Assert.Equal(90, gpu.Engines.Single(e => e.Type == "3D").Percent);
        Assert.Equal(25, gpu.Engines.Single(e => e.Type == "VideoEncode").Percent);
        Assert.Equal(5, gpu.Engines.Single(e => e.Type == "Copy").Percent);
        Assert.Equal(0, gpu.Engines.Single(e => e.Type == "VideoDecode").Percent); // motor parado continua listado
        Assert.Equal("Codificação de vídeo", gpu.Engines.Single(e => e.Type == "VideoEncode").Label);
        Assert.Equal(2_000_000_000UL, gpu.DedicatedBytes);
        Assert.Equal([100, 200, 300], gpu.Processes.Select(p => p.Pid));
        Assert.Equal(60, gpu.Processes[0].Percent); // por processo: o motor em que ele mais usa
        Assert.Equal("jogo", gpu.Processes[0].Name);
    }

    [Fact]
    public void Gpu_engine_sum_is_capped_and_primary_adapter_is_the_one_with_more_dedicated_memory()
    {
        var engines = CounterSet.Of(
            ($"pid_1_{A}_eng_0_engtype_3D", 70), ($"pid_2_{A}_eng_0_engtype_3D", 50), // 120% → 100%
            ($"pid_3_{B}_eng_0_engtype_3D", 15));
        var memory = CounterSet.Of((A, 100), (B, 6_000_000_000));
        var snap = MonitorAggregator.Aggregate(new RawSample { GpuEngine = engines, GpuDedicated = memory });

        Assert.Equal(2, snap.GpuAdapters.Count);
        Assert.Equal(100, snap.GpuAdapters.Single(a => a.Key.Contains("d1b4", StringComparison.Ordinal)).OverallPercent);
        Assert.Contains("aaaa", snap.Gpu!.Key, StringComparison.Ordinal);
        Assert.Equal(15, snap.Gpu.OverallPercent);
    }

    [Theory]
    [InlineData("pid_4321_luid_0x00000000_0x0000D1B4_phys_0_eng_12_engtype_VideoProcessing", 4321, 12, "VideoProcessing")]
    [InlineData("pid_8_luid_0x00000001_0x0000ABCD_phys_1_eng_0_engtype_Compute_1", 8, 0, "Compute_1")]
    public void Gpu_engine_instance_names_parse(string instance, int pid, int engine, string type)
    {
        Assert.True(MonitorAggregator.TryParseEngine(instance, out var k));
        Assert.Equal(pid, k.Pid);
        Assert.Equal(engine, k.Engine);
        Assert.Equal(type, k.Type);
    }

    [Fact]
    public void Gpu_ignores_foreign_instance_names()
    {
        Assert.False(MonitorAggregator.TryParseEngine("_Total", out _));
        Assert.Empty(MonitorAggregator.Gpu(CounterSet.Of(("_Total", 50)), CounterSet.Of(("_Total", 1))));
    }

    // ------------------------------------------------------------------ memória, disco, rede, térmica, processos

    [Fact]
    public void Memory_used_is_total_minus_available()
    {
        var m = MonitorAggregator.Memory(new MemoryReading(16_000, 4_000, 3_000, 12_000, 24_000))!;
        Assert.Equal(12_000UL, m.UsedBytes);
        Assert.Equal(75, m.UsedPercent, 3);
        Assert.Equal(50, m.CommitPercent, 3);
        Assert.Null(MonitorAggregator.Memory(new MemoryReading(0, 0, 0, 0, 0)));
    }

    [Fact]
    public void Disks_use_idle_time_and_skip_total()
    {
        var disks = MonitorAggregator.Disks(CounterSet.Of(("_Total", 50), ("0 C:", 10), ("1 D: E:", 104)),
            CounterSet.Of(("0 C:", 1000)), CounterSet.Of(("0 C:", 500), ("1 D: E:", -3)), CounterSet.Of(("0 C:", 3)));
        Assert.Equal(2, disks.Count);
        Assert.Equal("Disco 0 · C:", disks[0].Label);
        Assert.Equal(90, disks[0].ActivePercent);
        Assert.Equal(1000, disks[0].ReadBytesPerSec);
        Assert.Equal(3, disks[0].Queue);
        Assert.Equal("Disco 1 · D: E:", disks[1].Label);
        Assert.Equal(0, disks[1].ActivePercent); // % Idle Time às vezes passa de 100
        Assert.Equal(0, disks[1].WriteBytesPerSec);
    }

    [Fact]
    public void Networks_sorted_by_traffic()
    {
        var nets = MonitorAggregator.Networks(CounterSet.Of(("Wi-Fi", 10), ("Ethernet", 1000)), CounterSet.Of(("Ethernet", 5000)),
            CounterSet.Of(("Ethernet", 1e9), ("Wi-Fi", 0)));
        Assert.Equal(["Ethernet", "Wi-Fi"], nets.Select(n => n.Name));
        Assert.Equal(6000, nets[0].TotalBytesPerSec);
        Assert.Equal(1e9, nets[0].LinkBitsPerSec);
        Assert.Null(nets[1].LinkBitsPerSec);
    }

    [Fact]
    public void Thermal_converts_kelvin_and_drops_impossible_values()
    {
        var t = MonitorAggregator.Thermal(CounterSet.Of((@"\_TZ.TZ00", 300.15), (@"\_TZ.TZ01", 0), (@"\_TZ.TZ02", 3000)));
        var z = Assert.Single(t);
        Assert.Equal(27, z.Celsius, 1);
    }

    [Fact]
    public void Process_cpu_is_time_delta_over_elapsed_and_logical_processors()
    {
        var tracker = new ProcessCpuTracker(4);
        var t0 = DateTimeOffset.UnixEpoch;
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.All(tracker.Update([new(10, "jogo", TimeSpan.FromSeconds(5), 100, start), new(0, "Idle", TimeSpan.Zero, 0)], t0),
            p => Assert.Equal(0, p.CpuPercent)); // primeira leitura não tem base

        var r = tracker.Update([new(10, "jogo", TimeSpan.FromSeconds(7), 100, start), new(11, "protegido", null, 50)], t0.AddSeconds(1));
        Assert.Equal(50, r.Single(p => p.Pid == 10).CpuPercent); // 2 s de CPU em 1 s com 4 núcleos = 50%
        Assert.Equal(0, r.Single(p => p.Pid == 11).CpuPercent);
        Assert.DoesNotContain(r, p => p.Pid == 0);

        // PID reaproveitado por outro processo: sem base, 0%
        r = tracker.Update([new(10, "outro", TimeSpan.FromSeconds(9), 100, start.AddMinutes(1))], t0.AddSeconds(2));
        Assert.Equal(0, r.Single().CpuPercent);
    }

    // ------------------------------------------------------------------ buffer circular

    [Fact]
    public void Ring_buffer_keeps_last_values_in_order()
    {
        var ring = new RingBuffer<int>(3);
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.Latest);
        for (var i = 1; i <= 5; i++) ring.Add(i);
        Assert.Equal(3, ring.Count);
        Assert.Equal([3, 4, 5], Enumerable.Range(0, ring.Count).Select(i => ring[i]));
        Assert.Equal(5, ring.Latest);
        Assert.Equal([4, 5], ring.Last(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[3]);

        Span<int> big = stackalloc int[5];
        Assert.Equal(3, ring.CopyTo(big));
        Assert.Equal([3, 4, 5], big[..3].ToArray());
        Span<int> small = stackalloc int[2];
        Assert.Equal(2, ring.CopyTo(small));
        Assert.Equal([4, 5], small.ToArray()); // destino menor: ficam os mais recentes

        ring.Clear();
        Assert.Equal(0, ring.Count);
        ring.Add(9);
        Assert.Equal([9], ring.Last(10));
    }

    [Fact]
    public void Ring_buffer_partial_fill_and_history_capacity()
    {
        var ring = new RingBuffer<double>(300);
        ring.Add(1);
        ring.Add(2);
        Assert.Equal([1d, 2d], ring.Last(300));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));

        var h = new MonitorHistory();
        for (var i = 0; i < 400; i++) h.Add(new MonitorSnapshot { Cpu = new CpuUsage(i % 100, null, null, []) });
        Assert.Equal(300, h.Cpu.Count);
        Assert.Equal(300, h.Snapshots.Count);
        Assert.Equal(99, h.Cpu.Latest);
    }

    // ------------------------------------------------------------------ gargalo

    private static BottleneckInput In(double cpu, double? gpu, double maxCore = -1, double mem = 50, double commit = 50, double disk = 0, double queue = 0, int n = 10) =>
        new(n, cpu, maxCore < 0 ? cpu : maxCore, gpu, mem, commit, disk, queue);

    [Fact]
    public void Bottleneck_gpu_bound_when_gpu_pegged_and_cpu_moderate()
    {
        var v = BottleneckDetector.Evaluate(In(40, 99));
        Assert.Equal(BottleneckKind.Gpu, v.Kind);
        Assert.Contains("GPU a 99%", v.Explanation, StringComparison.Ordinal);
        Assert.Contains("CPU a 40%", v.Explanation, StringComparison.Ordinal);
        Assert.Equal("warn", v.Tone);
    }

    [Theory]
    [InlineData(95, 70, -1, BottleneckKind.Cpu)]
    [InlineData(35, 60, 99, BottleneckKind.SingleThread)]
    [InlineData(35, 90, 99, BottleneckKind.None)]          // GPU alta: não culpa o núcleo
    [InlineData(5, 3, -1, BottleneckKind.Idle)]
    [InlineData(50, 60, -1, BottleneckKind.None)]
    [InlineData(90, 99, -1, BottleneckKind.Cpu)]           // GPU 99% com CPU 90%: não é "só GPU"
    public void Bottleneck_rules(double cpu, double gpu, double maxCore, BottleneckKind expected) =>
        Assert.Equal(expected, BottleneckDetector.Evaluate(In(cpu, gpu, maxCore)).Kind);

    [Fact]
    public void Bottleneck_memory_disk_collecting_and_no_gpu()
    {
        Assert.Equal(BottleneckKind.Memory, BottleneckDetector.Evaluate(In(40, 99, commit: 95)).Kind); // memória vem antes
        Assert.Equal(BottleneckKind.Disk, BottleneckDetector.Evaluate(In(30, 20, disk: 98, queue: 4)).Kind);
        Assert.Equal(BottleneckKind.None, BottleneckDetector.Evaluate(In(30, 20, disk: 98, queue: 0.5)).Kind);
        Assert.Equal(BottleneckKind.Collecting, BottleneckDetector.Evaluate(In(40, 99, n: 2)).Kind);
        var noGpu = BottleneckDetector.Evaluate(In(30, null, maxCore: 100));
        Assert.Equal(BottleneckKind.SingleThread, noGpu.Kind);
        Assert.Contains("sem leitura", noGpu.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Bottleneck_input_averages_window()
    {
        var snaps = new[]
        {
            new MonitorSnapshot { Cpu = new CpuUsage(40, null, null, [new(0, "0,0", 100, null), new(1, "0,1", 0, null)]),
                Gpu = new GpuAdapterUsage("a", 100, [], [], null) },
            new MonitorSnapshot { Cpu = new CpuUsage(20, null, null, [new(0, "0,0", 50, null)]), Gpu = new GpuAdapterUsage("a", 90, [], [], null),
                Memory = new MemoryUsage(100, 80, 20, 0, 50, 100) },
        };
        var i = BottleneckInput.From(snaps);
        Assert.Equal(2, i.Samples);
        Assert.Equal(30, i.CpuPercent);
        Assert.Equal(75, i.MaxCorePercent);
        Assert.Equal(95, i.GpuPercent);
        Assert.Equal(80, i.MemoryUsedPercent);
        Assert.Equal(50, i.CommitPercent);
        Assert.Equal(0, BottleneckInput.From([]).Samples);
    }

    // ------------------------------------------------------------------ nvidia-smi

    [Fact]
    public void NvidiaSmi_parses_lines_and_not_available_fields()
    {
        var r = NvidiaSmi.Parse("45, 12, 1500, 35.20\r\n71, 99, 2610, [N/A]\r\n\r\n[Not Supported], [Not Supported], [Not Supported], [Not Supported]\nlixo\n");
        Assert.Equal(2, r.Count);
        Assert.Equal(new NvidiaGpuReading(0, 45, 12, 1500, 35.2), r[0]);
        Assert.Equal(1, r[1].Index);
        Assert.Equal(71, r[1].TemperatureC);
        Assert.Null(r[1].PowerWatts);
        Assert.Empty(NvidiaSmi.Parse(null));
        Assert.Empty(NvidiaSmi.Parse("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."));
        Assert.DoesNotContain("--set", NvidiaSmi.Arguments, StringComparison.Ordinal); // só consulta
        Assert.StartsWith("--query-gpu=", NvidiaSmi.Arguments, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ saúde de disco

    private static PhysicalDiskReading Disk(string health = "Healthy", string media = "SSD", double? temp = 40, double? wear = 3,
        ulong? readUnc = 0, ulong? writeUnc = 0, ulong? hours = 1000, bool reliability = true, string op = "OK") => new()
    {
        FriendlyName = "Disco teste", MediaType = media, HealthStatus = health, OperationalStatus = op, ReliabilityAvailable = reliability,
        TemperatureC = temp, WearPercent = wear, ReadErrorsUncorrected = readUnc, WriteErrorsUncorrected = writeUnc, PowerOnHours = hours,
    };

    [Fact]
    public void Disk_health_ok_attention_critical()
    {
        Assert.Equal(DiskHealthLevel.Ok, DiskHealthClassifier.Classify(Disk()).Level);
        Assert.Equal(DiskHealthLevel.Attention, DiskHealthClassifier.Classify(Disk(wear: 75)).Level);
        Assert.Equal(DiskHealthLevel.Critical, DiskHealthClassifier.Classify(Disk(wear: 95)).Level);
        Assert.Equal(DiskHealthLevel.Critical, DiskHealthClassifier.Classify(Disk(readUnc: 2)).Level);
        Assert.Equal(DiskHealthLevel.Critical, DiskHealthClassifier.Classify(Disk(health: "Unhealthy")).Level);
        Assert.Equal(DiskHealthLevel.Attention, DiskHealthClassifier.Classify(Disk(health: "Warning")).Level);
        Assert.Equal(DiskHealthLevel.Attention, DiskHealthClassifier.Classify(Disk(op: "Degraded")).Level);
        Assert.Equal(DiskHealthLevel.Attention, DiskHealthClassifier.Classify(Disk(temp: 68)).Level);
        Assert.Equal(DiskHealthLevel.Critical, DiskHealthClassifier.Classify(Disk(temp: 80)).Level);
        // HD aguenta menos calor e envelhece com horas ligado
        Assert.Equal(DiskHealthLevel.Critical, DiskHealthClassifier.Classify(Disk(media: "HDD", temp: 62)).Level);
        Assert.Equal(DiskHealthLevel.Attention, DiskHealthClassifier.Classify(Disk(media: "HDD", temp: 52)).Level);
        Assert.Equal(DiskHealthLevel.Attention, DiskHealthClassifier.Classify(Disk(media: "HDD", hours: 45_000)).Level);
        Assert.Equal(DiskHealthLevel.Ok, DiskHealthClassifier.Classify(Disk(media: "SSD", hours: 45_000)).Level);
        Assert.Equal("Crítico", DiskHealthClassifier.Classify(Disk(wear: 95)).LevelText);
    }

    [Fact]
    public void Disk_health_without_reliability_counters_degrades_with_explanation()
    {
        var v = DiskHealthClassifier.Classify(Disk(temp: null, wear: null, readUnc: null, writeUnc: null, hours: null, reliability: false));
        Assert.Equal(DiskHealthLevel.Ok, v.Level);
        Assert.Contains(v.Reasons, r => r.Contains("administrador", StringComparison.Ordinal));

        var unknown = DiskHealthClassifier.Classify(Disk(health: "", temp: null, wear: null, reliability: false, op: ""));
        Assert.Equal(DiskHealthLevel.Unknown, unknown.Level);
        Assert.Equal("Sem dados", unknown.LevelText);
    }

    [Fact]
    public void Disk_health_critical_lists_all_reasons()
    {
        var v = DiskHealthClassifier.Classify(Disk(wear: 92, health: "Warning"));
        Assert.Equal(DiskHealthLevel.Critical, v.Level);
        Assert.Equal(2, v.Reasons.Count);
    }

    [Fact]
    public void Disk_health_parser_accepts_object_array_numbers_and_nulls()
    {
        const string array = """
            [{"DeviceId":"0","FriendlyName":"Samsung SSD 980 PRO 1TB","MediaType":"SSD","BusType":"NVMe","HealthStatus":"Healthy",
              "OperationalStatus":"OK","Size":1000204886016,"ReliabilityAvailable":true,"Temperature":41,"TemperatureMax":70,"Wear":2,
              "ReadErrorsTotal":0,"ReadErrorsUncorrected":0,"WriteErrorsUncorrected":null,"PowerOnHours":3120},
             {"DeviceId":"1","FriendlyName":"WDC WD20","MediaType":"3","BusType":"11","HealthStatus":"1","OperationalStatus":"OK",
              "Size":2000398934016,"ReliabilityAvailable":false,"Temperature":null}]
            """;
        var disks = DiskHealthParser.Parse("﻿" + array);
        Assert.Equal(2, disks.Count);
        Assert.Equal("NVMe", disks[0].BusType);
        Assert.Equal(41, disks[0].TemperatureC);
        Assert.Equal(3120UL, disks[0].PowerOnHours);
        Assert.Null(disks[0].WriteErrorsUncorrected);
        Assert.True(disks[0].ReliabilityAvailable);
        Assert.Equal("HDD", disks[1].MediaType);   // valores numéricos do CIM viram nomes
        Assert.Equal("SATA", disks[1].BusType);
        Assert.Equal("Warning", disks[1].HealthStatus);
        Assert.False(disks[1].ReliabilityAvailable);

        var single = DiskHealthParser.Parse("""{"FriendlyName":"X","HealthStatus":"Healthy"}""");
        Assert.Equal("X", Assert.Single(single).FriendlyName);
        Assert.Empty(DiskHealthParser.Parse(""));
        Assert.Empty(DiskHealthParser.Parse("[]"));

        var report = DiskHealthReport.From(disks, elevated: false);
        Assert.Equal(DiskHealthLevel.Ok, report.Disks[0].Verdict.Level);
        Assert.Equal(DiskHealthLevel.Attention, report.Disks[1].Verdict.Level);
    }

    [Fact]
    public void Disk_health_script_only_reads()
    {
        var s = DiskHealthScript.Text;
        Assert.Contains("Get-PhysicalDisk", s, StringComparison.Ordinal);
        Assert.Contains("Get-StorageReliabilityCounter", s, StringComparison.Ordinal);
        foreach (var verb in new[] { "Set-", "Reset-", "Remove-", "Clear-", "Initialize-", "Format-Volume", "Repair-", "Optimize-" })
            Assert.DoesNotContain(verb, s, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ sessão (fontes falsas)

    private sealed class FakePerformance(Func<int, RawSample> next) : IPerformanceSource
    {
        private int _n;
        public bool Disposed { get; private set; }
        public IReadOnlyList<string> Unavailable { get; } = ["GPU: teste"];
        public RawSample Read() => next(_n++);
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProcesses : IProcessSource
    {
        public int Reads { get; private set; }
        public int LogicalProcessors => 2;
        public IReadOnlyList<ProcessReading> Read()
        {
            Reads++;
            return [.. Enumerable.Range(1, 40).Select(i => new ProcessReading(i, $"p{i}", TimeSpan.FromSeconds(Reads * i * 0.01), i * 1000L))];
        }
    }

    [Fact]
    public void Session_ticks_fill_history_trim_processes_and_detect_gpu_bound()
    {
        var clock = DateTimeOffset.UnixEpoch;
        var perf = new FakePerformance(_ => new RawSample
        {
            ProcessorUtility = CounterSet.Of(("_Total", 40), ("0,0", 40), ("0,1", 40)),
            GpuEngine = CounterSet.Of(($"pid_7_{A}_eng_0_engtype_3D", 99)),
            Memory = new MemoryReading(16, 8, 1, 4, 32),
        });
        var procs = new FakeProcesses();
        using var session = new MonitorSession(perf, procs, nvidia: null, clock: () => clock = clock.AddSeconds(1));

        MonitorTick? last = null;
        for (var i = 0; i < 12; i++) last = session.Tick();

        Assert.Equal(12, session.History.Cpu.Count);
        Assert.Equal(6, procs.Reads);                                  // processos a cada 2 tiques
        Assert.True(last!.Snapshot.Processes.Count <= MonitorSession.TopProcesses * 2 + 5);
        Assert.Equal("p40", last.Snapshot.Processes.OrderByDescending(p => p.CpuPercent).First().Name);
        Assert.Equal("p7", last.Snapshot.Gpu!.Processes.Single().Name);
        Assert.Equal(99, last.Snapshot.Processes.Single(p => p.Pid == 7).GpuPercent ?? -1);
        Assert.Equal(BottleneckKind.Gpu, last.Verdict.Kind);
        Assert.Equal(["GPU: teste"], session.Unavailable);
        Assert.False(session.NvidiaAvailable);
        session.Dispose();
        Assert.True(perf.Disposed);
    }

    // ------------------------------------------------------------------ módulo somente leitura

    [Fact]
    public void Module_registers_no_write_handlers_and_has_no_catalog()
    {
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), OtherAccount: false, Elevated: false);
        Assert.Empty(MonitoringHandlers.Create(env));
        var catalog = Path.Combine(AppContext.BaseDirectory, "catalog");
        Assert.False(File.Exists(Path.Combine(catalog, "monitoring.v1.json")));
        Assert.False(File.Exists(Path.Combine(catalog, "monitor.v1.json")));
        Assert.NotEmpty(CatalogLoader.LoadDirectory(catalog).Actions);
        Assert.DoesNotContain(CatalogLoader.LoadDirectory(catalog).Actions, a => a.Definition.Id.StartsWith("monitor", StringComparison.OrdinalIgnoreCase));
    }
}
