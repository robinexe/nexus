using System.Buffers.Binary;
using System.Text;
using Nexus.Core;
using Nexus.Lab.Cpu;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Power;

namespace Nexus.Tests.Modules;

[Trait("Category", "Unit")]
public sealed class CpuLabTests
{
    // ================================================================== CPUID de amostra

    /// <summary>Monta uma tabela de CPUID como a de um processador real (assinatura, marca, recursos, caches).</summary>
    private sealed class CpuIdSample
    {
        public readonly Dictionary<(uint, uint), CpuIdRegisters> Table = [];

        public CpuIdSample(string vendor, uint signature, string brand, uint maxLeaf, uint maxExt)
        {
            var v = Regs(vendor.PadRight(12, '\0'));
            Table[(0, 0)] = new(maxLeaf, v[0], v[2], v[1]); // EBX, EDX, ECX = "Genu" "ineI" "ntel"
            Table[(0x80000000, 0)] = new(maxExt, 0, 0, 0);
            var b = Regs(brand.PadRight(48, '\0'));
            for (var i = 0; i < 3; i++) Table[(0x80000002u + (uint)i, 0)] = new(b[i * 4], b[i * 4 + 1], b[i * 4 + 2], b[i * 4 + 3]);
            Leaf1 = new(signature, 0, 0, 0);
        }

        public CpuIdRegisters Leaf1 { get => Table[(1, 0)]; set => Table[(1, 0)] = value; }

        public CpuIdSample Features(uint l1Ecx, uint l7Ebx, uint l7Ecx = 0, uint l7Edx = 0, uint l7s1Eax = 0, uint e1Ecx = 0)
        {
            Leaf1 = Leaf1 with { Ecx = l1Ecx, Edx = 1u << 28 };
            Table[(7, 0)] = new(l7s1Eax != 0 ? 1u : 0u, l7Ebx, l7Ecx, l7Edx);
            Table[(7, 1)] = new(l7s1Eax, 0, 0, 0);
            Table[(0x80000001, 0)] = new(0, 0, e1Ecx, 0);
            return this;
        }

        /// <summary>Uma entrada de cache no formato da folha 4 / 0x8000001D.</summary>
        public CpuIdSample Cache(uint leaf, uint sub, int type, int level, int sharing, int line, int ways, long bytes)
        {
            var sets = bytes / (ways * line);
            var eax = (uint)type | (uint)level << 5 | (uint)(sharing - 1) << 14;
            var ebx = (uint)(line - 1) | 0u << 12 | (uint)(ways - 1) << 22;
            Table[(leaf, sub)] = new(eax, ebx, (uint)(sets - 1), 0);
            return this;
        }

        public CpuIdSample Leaf(uint leaf, uint sub, CpuIdRegisters r) { Table[(leaf, sub)] = r; return this; }

        public CpuIdentity Decode() => CpuIdDecoder.Decode(new TableCpuIdSource(Table));

        private static uint[] Regs(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            return Enumerable.Range(0, bytes.Length / 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4))).ToArray();
        }
    }

    private const uint Sse42 = 1u << 20, Avx = 1u << 28, Fma = 1u << 12, Aes = 1u << 25, Popcnt = 1u << 23, Vmx = 1u << 5, Hv = 1u << 31;
    private const uint Avx2 = 1u << 5, Bmi2 = 1u << 8, Avx512F = 1u << 16, Sha = 1u << 29;
    private const long K = 1024, M = 1024 * 1024;

    private static CpuIdSample Amd(uint signature, string brand, long l2, long l3, int l3Ways, bool avx512)
    {
        var s = new CpuIdSample("AuthenticAMD", signature, brand, 0x10, 0x80000021)
            .Features(Sse42 | Avx | Fma | Aes | Popcnt, Avx2 | Bmi2 | Sha | (avx512 ? Avx512F : 0), avx512 ? 1u << 11 : 0, 0, 0, e1Ecx: 1u << 2)
            .Cache(0x8000001D, 0, 1, 1, 2, 64, 8, 32 * K)
            .Cache(0x8000001D, 1, 2, 1, 2, 64, 8, 32 * K)
            .Cache(0x8000001D, 2, 3, 2, 2, 64, 8, l2)
            .Cache(0x8000001D, 3, 3, 3, 16, 64, l3Ways, l3)
            .Leaf(0x8000001E, 0, new(0, 1u << 8, 0, 0)); // 2 threads por núcleo
        return s;
    }

    private static CpuIdSample Intel(uint signature, string brand, long l3, int l3Ways, int baseMhz, int maxMhz)
    {
        var s = new CpuIdSample("GenuineIntel", signature, brand, 0x20, 0x80000008)
            .Features(Sse42 | Avx | Fma | Aes | Popcnt | Vmx, Avx2 | Bmi2 | Sha, 0, l7Edx: 1u << 15, l7s1Eax: 1u << 4)
            .Cache(4, 0, 1, 1, 2, 64, 12, 48 * K)
            .Cache(4, 1, 2, 1, 2, 64, 8, 32 * K)
            .Cache(4, 2, 3, 2, 2, 64, 10, 1280 * K)
            .Cache(4, 3, 3, 3, 32, 64, l3Ways, l3)
            .Leaf(0xB, 0, new(1, 2, 1u << 8, 0))
            .Leaf(0x16, 0, new((uint)baseMhz, (uint)maxMhz, 100, 0));
        return s;
    }

    [Fact]
    public void Ryzen_5_5600H_decodes_as_Zen3_Cezanne()
    {
        var id = Amd(0x00A50F00, "AMD Ryzen 5 5600H with Radeon Graphics         ", 512 * K, 16 * M, 16, avx512: false).Decode();

        Assert.True(id.Available);
        Assert.Equal(CpuVendor.Amd, id.Vendor);
        Assert.Equal(0x19, id.Family);
        Assert.Equal(0x50, id.Model);
        Assert.Equal(0, id.Stepping);
        Assert.Equal("AMD Ryzen 5 5600H with Radeon Graphics", id.Brand);
        Assert.Equal("Zen 3", id.Microarchitecture!.Core);
        Assert.Equal("Cezanne", id.Microarchitecture.Codename);
        Assert.Equal(16 * M, id.Cache(3)!.SizeBytes);
        Assert.Equal(16, id.Cache(3)!.Ways);
        Assert.Equal(16, id.Cache(3)!.SharedByLogical);
        Assert.Equal(512 * K, id.Cache(2)!.SizeBytes);
        Assert.Equal(32 * K, id.Cache(1)!.SizeBytes);
        Assert.Contains(id.Caches, c => c.Label == "L1i");
        Assert.True(id.Has("AVX2"));
        Assert.True(id.Has("SHA"));
        Assert.True(id.Has("Virtualização (AMD-V)"));
        Assert.False(id.Has("AVX-512"));
        Assert.False(id.Hybrid);
        Assert.True(id.Smt);
        Assert.Equal(0, id.MaxMhz); // AMD não publica a folha 0x16
    }

    [Fact]
    public void Core_i9_12900K_decodes_as_hybrid_Alder_Lake_without_AVX512()
    {
        var id = Intel(0x00090672, "12th Gen Intel(R) Core(TM) i9-12900K", 30 * M, 12, 3200, 5200).Decode();

        Assert.Equal(CpuVendor.Intel, id.Vendor);
        Assert.Equal(6, id.Family);
        Assert.Equal(0x97, id.Model);
        Assert.Equal(2, id.Stepping);
        Assert.Equal("Alder Lake-S", id.Microarchitecture!.Codename);
        Assert.Contains("Golden Cove", id.Microarchitecture.Core, StringComparison.Ordinal);
        Assert.True(id.Hybrid);
        Assert.True(id.Has("AVX-VNNI"));
        Assert.False(id.Has("AVX-512"));
        Assert.True(id.Has("Virtualização (VT-x)"));
        Assert.Equal(30 * M, id.Cache(3)!.SizeBytes);
        Assert.Equal(1280 * K, id.Cache(2)!.SizeBytes);
        Assert.Equal(48 * K, id.Cache(1)!.SizeBytes);
        Assert.Equal(3200, id.BaseMhz);
        Assert.Equal(5200, id.MaxMhz);
        Assert.True(id.Smt);
    }

    [Fact]
    public void Ryzen_7_7800X3D_decodes_as_Zen4_Raphael_with_96MB_L3_and_AVX512()
    {
        var id = Amd(0x00A60F12, "AMD Ryzen 7 7800X3D 8-Core Processor", 1 * M, 96 * M, 16, avx512: true).Decode();
        Assert.Equal(0x19, id.Family);
        Assert.Equal(0x61, id.Model);
        Assert.Equal(2, id.Stepping);
        Assert.Equal("Zen 4", id.Microarchitecture!.Core);
        Assert.Equal("Raphael", id.Microarchitecture.Codename);
        Assert.Equal(96 * M, id.Cache(3)!.SizeBytes);
        Assert.True(id.Has("AVX-512"));
        Assert.True(id.Has("AVX-512 VNNI"));
    }

    [Fact]
    public void Core_i7_13700H_decodes_as_Raptor_Lake_P()
    {
        var id = Intel(0x000B06A2, "13th Gen Intel(R) Core(TM) i7-13700H", 24 * M, 12, 2400, 5000).Decode();
        Assert.Equal(0xBA, id.Model);
        Assert.Equal("Raptor Lake-P/H", id.Microarchitecture!.Codename);
        Assert.Equal(24 * M, id.Cache(3)!.SizeBytes);
        Assert.Equal(2400, id.BaseMhz);
    }

    [Fact]
    public void Ryzen_9_9700X_signature_decodes_as_Zen5_Granite_Ridge()
    {
        var id = Amd(0x00B40F40, "AMD Ryzen 7 9700X 8-Core Processor", 1 * M, 32 * M, 16, avx512: true).Decode();
        Assert.Equal(0x1A, id.Family);
        Assert.Equal(0x44, id.Model);
        Assert.Equal("Granite Ridge", id.Microarchitecture!.Codename);
    }

    [Fact]
    public void Hypervisor_bit_is_reported()
    {
        var s = Amd(0x00A50F00, "AMD Ryzen 5 5600H with Radeon Graphics", 512 * K, 16 * M, 16, false);
        s.Leaf1 = s.Leaf1 with { Ecx = s.Leaf1.Ecx | Hv };
        Assert.True(s.Decode().HypervisorPresent);
    }

    [Fact]
    public void No_cpuid_degrades_to_unavailable_identity()
    {
        var id = CpuIdDecoder.Decode(new TableCpuIdSource(new Dictionary<(uint, uint), CpuIdRegisters>(), supported: false));
        Assert.False(id.Available);
        Assert.NotNull(id.UnavailableReason);
        Assert.Null(id.Microarchitecture);
    }

    [Fact]
    public void Hardware_cpuid_never_throws_on_this_machine()
    {
        var id = CpuIdDecoder.Decode(new HardwareCpuIdSource());
        if (id.Available) Assert.False(string.IsNullOrEmpty(id.VendorString));
        else Assert.NotNull(id.UnavailableReason);
    }

    // ================================================================== Tabela de microarquiteturas

    [Theory]
    [InlineData(CpuVendor.Intel, 6, 0x5E, 3, "Skylake", "Skylake-S/H")]
    [InlineData(CpuVendor.Intel, 6, 0x9E, 9, "Skylake (Kaby Lake)", "Kaby Lake-S/H")]
    [InlineData(CpuVendor.Intel, 6, 0x9E, 10, "Skylake (Coffee Lake)", "Coffee Lake-S/H")]
    [InlineData(CpuVendor.Intel, 6, 0x9E, 13, "Skylake (Coffee Lake)", "Coffee Lake Refresh")]
    [InlineData(CpuVendor.Intel, 6, 0xA5, 5, "Skylake (Comet Lake)", "Comet Lake-S/H")]
    [InlineData(CpuVendor.Intel, 6, 0x8C, 1, "Willow Cove", "Tiger Lake-U")]
    [InlineData(CpuVendor.Intel, 6, 0xA7, 1, "Cypress Cove", "Rocket Lake-S")]
    [InlineData(CpuVendor.Intel, 6, 0xB7, 1, "Raptor Cove + Gracemont", "Raptor Lake-S")]
    [InlineData(CpuVendor.Intel, 6, 0xAA, 4, "Redwood Cove + Crestmont", "Meteor Lake-H/U")]
    [InlineData(CpuVendor.Intel, 6, 0xC6, 2, "Lion Cove + Skymont", "Arrow Lake-S/HX")]
    [InlineData(CpuVendor.Intel, 6, 0xBD, 1, "Lion Cove + Skymont", "Lunar Lake")]
    [InlineData(CpuVendor.Amd, 0x17, 0x01, 1, "Zen", "Summit Ridge / Naples")]
    [InlineData(CpuVendor.Amd, 0x17, 0x08, 2, "Zen+", "Pinnacle Ridge / Colfax")]
    [InlineData(CpuVendor.Amd, 0x17, 0x71, 0, "Zen 2", "Matisse")]
    [InlineData(CpuVendor.Amd, 0x17, 0x60, 1, "Zen 2", "Renoir")]
    [InlineData(CpuVendor.Amd, 0x19, 0x21, 0, "Zen 3", "Vermeer")]
    [InlineData(CpuVendor.Amd, 0x19, 0x50, 0, "Zen 3", "Cezanne")]
    [InlineData(CpuVendor.Amd, 0x19, 0x44, 1, "Zen 3+", "Rembrandt")]
    [InlineData(CpuVendor.Amd, 0x19, 0x61, 2, "Zen 4", "Raphael")]
    [InlineData(CpuVendor.Amd, 0x19, 0x74, 1, "Zen 4", "Phoenix / Hawk Point")]
    [InlineData(CpuVendor.Amd, 0x1A, 0x24, 0, "Zen 5", "Strix Point")]
    [InlineData(CpuVendor.Amd, 0x1A, 0x44, 0, "Zen 5", "Granite Ridge")]
    public void Microarchitecture_table(CpuVendor vendor, int family, int model, int stepping, string core, string codename)
    {
        var m = MicroarchitectureTable.Find(vendor, family, model, stepping);
        Assert.NotNull(m);
        Assert.Equal(core, m!.Core);
        Assert.Equal(codename, m.Codename);
    }

    [Fact]
    public void Unknown_models_are_not_guessed()
    {
        Assert.Null(MicroarchitectureTable.Find(CpuVendor.Intel, 6, 0x01, 0));
        Assert.Null(MicroarchitectureTable.Find(CpuVendor.Amd, 0x15, 0x02, 0)); // Bulldozer: fora da tabela
    }

    // ================================================================== Topologia (GetLogicalProcessorInformationEx)

    private static byte[] CoreRecord(byte eff, bool smt, params int[] lps)
    {
        var r = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(r, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 48);
        r[8] = smt ? (byte)1 : (byte)0;
        r[9] = eff;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(30), 1);
        ulong mask = 0;
        foreach (var lp in lps) mask |= 1UL << lp;
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), mask);
        return r;
    }

    private static byte[] CacheRecord(byte level, int type, uint size, params int[] lps)
    {
        var r = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(r, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 56);
        r[8] = level; r[9] = 16;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(10), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), size);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(38), 1);
        ulong mask = 0;
        foreach (var lp in lps) mask |= 1UL << lp;
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(40), mask);
        return r;
    }

    [Fact]
    public void Topology_6_cores_12_threads_with_one_shared_L3()
    {
        var buf = new List<byte>();
        for (var c = 0; c < 6; c++) buf.AddRange(CoreRecord(0, true, c * 2, c * 2 + 1));
        buf.AddRange(CacheRecord(3, 0, 16 << 20, Enumerable.Range(0, 12).ToArray()));
        for (var c = 0; c < 6; c++) buf.AddRange(CacheRecord(2, 0, 512 << 10, c * 2, c * 2 + 1));

        var t = TopologyParser.Parse(buf.ToArray());

        Assert.Equal(6, t.CoreCount);
        Assert.Equal(12, t.LogicalCount);
        Assert.True(t.Smt);
        Assert.False(t.Hybrid);
        Assert.Equal(6, t.PerformanceCores);
        Assert.Single(t.L3Clusters());
        Assert.Equal(16L << 20, t.TotalL3Bytes);
        Assert.Equal([4, 5], t.Cores[2].Threads.Select(x => x.Index));
    }

    [Fact]
    public void Topology_hybrid_P_and_E_cores_and_two_L3_domains()
    {
        var buf = new List<byte>();
        buf.AddRange(CoreRecord(1, true, 0, 1));
        buf.AddRange(CoreRecord(1, true, 2, 3));
        for (var e = 4; e < 8; e++) buf.AddRange(CoreRecord(0, false, e));
        buf.AddRange(CacheRecord(3, 0, 12 << 20, 0, 1, 2, 3));
        buf.AddRange(CacheRecord(3, 0, 12 << 20, 4, 5, 6, 7));

        var t = TopologyParser.Parse(buf.ToArray());

        Assert.True(t.Hybrid);
        Assert.Equal(2, t.PerformanceCores);
        Assert.Equal(4, t.EfficiencyCores);
        Assert.True(t.IsPerformanceCore(t.Cores[0]));
        Assert.False(t.IsPerformanceCore(t.Cores[5]));
        var clusters = t.L3Clusters();
        Assert.Equal(2, clusters.Count);
        Assert.Equal([0, 1], clusters[0]);
        Assert.Equal([2, 3, 4, 5], clusters[1]);
    }

    [Fact]
    public void Topology_rejects_truncated_buffer()
    {
        var r = CoreRecord(0, false, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 200);
        Assert.Throws<InvalidDataException>(() => TopologyParser.Parse(r));
    }

    // ================================================================== Degraus de cache numa curva sintética

    private static List<LatencyPoint> SyntheticCurve(double noise, int seed = 7)
    {
        var rnd = new Random(seed);
        return LatencyProbe.Sizes(512L << 20).Select(b =>
        {
            double ns = b <= 32 * K ? 1.0 : b <= 512 * K ? 3.2 : b <= 16 * M ? 11 : b <= 32 * M ? 40 : b <= 64 * M ? 75 : 88;
            ns *= 1 + (rnd.NextDouble() * 2 - 1) * noise;
            return new LatencyPoint(b, ns, ns * 4.2);
        }).ToList();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.03)]
    public void Steps_are_found_in_a_synthetic_curve(double noise)
    {
        var steps = CacheStepDetector.Detect(SyntheticCurve(noise));

        Assert.Equal(["L1", "L2", "L3", "RAM"], steps.Select(s => s.Level));
        Assert.Equal(32 * K, steps[0].CapacityBytes);
        Assert.Equal(512 * K, steps[1].CapacityBytes);
        Assert.Equal(16 * M, steps[2].CapacityBytes);
        Assert.Null(steps[3].CapacityBytes);
        Assert.InRange(steps[3].Ns, 80, 95);
        Assert.InRange(steps[0].Cycles, 3.5, 5);
    }

    [Fact]
    public void Steps_compare_with_cpuid_sizes()
    {
        var steps = CacheStepDetector.Detect(SyntheticCurve(0.02));
        var cmp = CacheStepDetector.Compare(steps, new Dictionary<int, long> { [1] = 32 * K, [2] = 512 * K, [3] = 16 * M });
        Assert.All(cmp, c => Assert.True(c.Agrees, c.Text));
        Assert.Contains("O CPUID diz L3 16 MB", cmp[2].Text, StringComparison.Ordinal);

        var wrong = CacheStepDetector.Compare(steps, new Dictionary<int, long> { [3] = 96 * M });
        Assert.False(wrong.Single().Agrees);
    }

    [Fact]
    public void Flat_curve_has_a_single_level_and_no_ram_label()
    {
        var flat = LatencyProbe.Sizes(1 << 20).Select(b => new LatencyPoint(b, 1.1, 4)).ToList();
        var steps = CacheStepDetector.Detect(flat);
        Assert.Single(steps);
        Assert.Equal("L1", steps[0].Level);
    }

    [Fact]
    public void Sizes_go_from_4KB_to_the_limit_two_per_octave()
    {
        var s = LatencyProbe.Sizes(512L << 20);
        Assert.Equal(4096, s[0]);
        Assert.Equal(512L << 20, s[^1]);
        Assert.Contains(5824L, s); // 4 KB × √2, arredondado à linha de 64 bytes
        Assert.All(s, b => Assert.Equal(0, b % 64));
        Assert.True(s.SequenceEqual(s.Order()));
    }

    // ================================================================== Clusters numa matriz sintética

    private static CoreToCoreMatrix Matrix(int n, Func<int, int, double> f)
    {
        var ns = Enumerable.Range(0, n).Select(i => Enumerable.Range(0, n).Select(j => i == j ? (double?)null : f(i, j)).ToArray()).ToArray();
        return new CoreToCoreMatrix(Enumerable.Range(0, n).ToList(), ns);
    }

    [Fact]
    public void Two_ccx_are_inferred_from_the_matrix()
    {
        var m = Matrix(8, (i, j) => i / 4 == j / 4 ? 20 + (i + j) % 3 : 80 + (i * j) % 5);
        var r = ClusterInferrer.Infer(m);
        Assert.Equal(2, r.Clusters.Count);
        Assert.Equal([0, 1, 2, 3], r.Clusters[0]);
        Assert.Equal([4, 5, 6, 7], r.Clusters[1]);
        Assert.InRange(r.NearNs!.Value, 19, 23);
        Assert.InRange(r.FarNs!.Value, 79, 85);
    }

    [Fact]
    public void Uniform_matrix_is_one_cluster()
    {
        var r = ClusterInferrer.Infer(Matrix(6, (i, j) => 18 + (i + j) % 4));
        Assert.Single(r.Clusters);
        Assert.Equal(6, r.Clusters[0].Count);
    }

    [Fact]
    public void Hybrid_e_core_block_is_separated()
    {
        // 4 núcleos P (30 ns entre si), bloco de 4 E (45 ns entre si), P↔E 70 ns: o maior salto separa P de E.
        var r = ClusterInferrer.Infer(Matrix(8, (i, j) => (i < 4, j < 4) switch { (true, true) => 30, (false, false) => 45, _ => 70 }));
        Assert.Equal(2, r.Clusters.Count);
    }

    // ================================================================== Queda de frequência numa série sintética

    [Fact]
    public void Throttling_is_detected_with_size_and_time()
    {
        var series = Enumerable.Range(1, 45).Select(s => new FrequencySample(s, s <= 8 ? 4.2 : s <= 12 ? 3.8 : 3.5)).ToList();
        var r = ThrottleDetector.Analyze(series)!;
        Assert.True(r.Throttles);
        Assert.InRange(r.DropPercent, 15, 18);
        Assert.InRange(r.DropAfterSeconds!.Value, 8, 13);
        Assert.Equal(4.2, r.InitialGhz, 2);
        Assert.Equal(3.5, r.SustainedGhz, 2);
        Assert.Contains("caiu", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Flat_series_does_not_throttle()
    {
        var series = Enumerable.Range(1, 30).Select(s => new FrequencySample(s, 4.0 + (s % 3) * 0.02)).ToList();
        var r = ThrottleDetector.Analyze(series)!;
        Assert.False(r.Throttles);
        Assert.False(r.Unstable);
        Assert.Null(r.DropAfterSeconds);
    }

    [Fact]
    public void Oscillating_series_is_unstable()
    {
        var series = Enumerable.Range(1, 30).Select(s => new FrequencySample(s, s % 2 == 0 ? 4.2 : 3.2)).ToList();
        Assert.True(ThrottleDetector.Analyze(series)!.Unstable);
        Assert.Null(ThrottleDetector.Analyze(series.Take(3).ToList()));
    }

    // ================================================================== Diagnóstico com fatos falsos

    private static CpuLabRun QuickRun(double best, params double[] others) => new()
    {
        Kind = CpuRunKind.Quick,
        CoreFrequencies = new[] { best }.Concat(others).Select((g, i) => new CoreFrequency(i, i * 2, g, true, true)).ToList(),
    };

    [Fact]
    public void Limited_plan_produces_fixes_with_catalog_actions()
    {
        var f = CpuDiagnosis.Evaluate(new CpuDiagnosisInput
        {
            Power = new CpuPowerState { MaxStateAc = 80, BoostModeAc = 0, RegistryMhz = 3301 },
            Quick = QuickRun(2.6, 2.6, 2.5),
        });

        var max = f.Single(x => x.Id == "max-state-ac");
        Assert.Equal(FindingSeverity.Bad, max.Severity);
        Assert.Equal(CpuDiagnosis.MaxStateActionId, max.ActionId);
        Assert.True(max.ActionNeedsAdmin);
        Assert.Contains("2,60 GHz", max.Evidence, StringComparison.Ordinal);

        var boost = f.Single(x => x.Id == "boost-off-ac");
        Assert.Equal(CpuDiagnosis.BoostModeActionId, boost.ActionId);
        Assert.Equal("2", boost.ActionValue);
        Assert.DoesNotContain(f, x => x.Id == "nothing-holding");
        Assert.DoesNotContain(f, x => x.Id == "no-turbo-measured"); // a causa já está explicada pelo plano
        Assert.Equal(FindingSeverity.Bad, f[0].Severity);           // mais graves primeiro
    }

    [Fact]
    public void Battery_saver_efficiency_mode_and_mhz_limit_are_warnings_with_instructions()
    {
        var f = CpuDiagnosis.Evaluate(new CpuDiagnosisInput
        {
            Power = new CpuPowerState
            {
                MaxStateAc = 100, BoostModeAc = 2, OnBattery = true, BatterySaver = true,
                PowerModeOverlay = ProcessorPowerSettings.OverlayBestEfficiency, NtMaxMhz = 3301, NtMhzLimit = 1800,
            },
        });
        foreach (var id in new[] { "on-battery", "battery-saver", "power-mode-efficiency", "mhz-limit" })
        {
            var x = f.Single(y => y.Id == id);
            Assert.Equal(FindingSeverity.Warn, x.Severity);
            Assert.False(string.IsNullOrEmpty(x.Instruction));
            Assert.Null(x.ActionId);
            Assert.Contains("https://", x.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Missing_turbo_without_plan_cause_is_flagged()
    {
        var f = CpuDiagnosis.Evaluate(new CpuDiagnosisInput
        {
            Power = new CpuPowerState { MaxStateAc = 100, BoostModeAc = 2, RegistryMhz = 3301 },
            Quick = QuickRun(3.30, 3.29),
        });
        Assert.Contains(f, x => x.Id == "no-turbo-measured" && x.Severity == FindingSeverity.Warn);
    }

    [Fact]
    public void Sustained_drop_and_hvci_and_game_mode()
    {
        var series = Enumerable.Range(1, 40).Select(s => new FrequencySample(s, s < 10 ? 4.0 : 3.2)).ToList();
        var f = CpuDiagnosis.Evaluate(new CpuDiagnosisInput
        {
            Power = new CpuPowerState { MaxStateAc = 100, BoostModeAc = 2, HvciRunning = true, CoreParkingMinAc = 10 },
            Sustained = new CpuLabRun { Kind = CpuRunKind.Sustained, AllCoreSeries = series },
            GameModeRead = true,
            GameModeEnabled = false,
        });

        Assert.Equal(FindingSeverity.Bad, f.Single(x => x.Id == "sustained-drop").Severity); // 20% de queda
        var hvci = f.Single(x => x.Id == "hvci");
        Assert.Contains("Memory Integrity", hvci.Source, StringComparison.Ordinal);
        Assert.Null(hvci.ActionId); // segurança: só instrução, nunca automático
        Assert.Equal(CpuDiagnosis.GameModeActionId, f.Single(x => x.Id == "game-mode-off").ActionId);
        Assert.Equal(CpuDiagnosis.CoreParkingActionId, f.Single(x => x.Id == "core-parking").ActionId);
    }

    [Fact]
    public void Healthy_pc_gets_a_single_good_summary()
    {
        var series = Enumerable.Range(1, 30).Select(s => new FrequencySample(s, 4.1)).ToList();
        var f = CpuDiagnosis.Evaluate(new CpuDiagnosisInput
        {
            Power = new CpuPowerState { MaxStateAc = 100, BoostModeAc = 2, RegistryMhz = 3301, OnBattery = false, BatterySaver = false, HvciRunning = false },
            Quick = QuickRun(4.2, 4.1, 4.15),
            Sustained = new CpuLabRun { Kind = CpuRunKind.Sustained, AllCoreSeries = series },
            GameModeRead = true,
        });
        Assert.Equal("nothing-holding", f[0].Id);
        Assert.DoesNotContain(f, x => x.Severity is FindingSeverity.Bad or FindingSeverity.Warn);
        Assert.Contains(f, x => x.Id == "game-mode-on");
        Assert.Contains(f, x => x.Id == "sustained-ok");
    }

    [Fact]
    public void Nothing_read_means_no_findings()
    {
        Assert.Empty(CpuDiagnosis.Evaluate(new CpuDiagnosisInput()));
    }

    // ================================================================== Catálogo e aplicador

    private static CatalogDocument Catalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    [Fact]
    public void Catalog_loads_and_every_action_the_page_offers_exists()
    {
        var cat = Catalog();
        var mine = cat.Actions.Where(a => a.Definition.Id.StartsWith("cpulab.", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, a => Assert.Equal(PowerSettingValueHandler.Kind, a.Definition.HandlerKind));
        Assert.All(mine, a => Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges));
        foreach (var key in new[] { CpuDiagnosis.MaxStateActionId, CpuDiagnosis.BoostModeActionId, CpuDiagnosis.GameModeActionId, CpuDiagnosis.CoreParkingActionId })
            Assert.Contains(cat.Actions, a => a.Definition.Key == key);
        Assert.Contains(CpuDiagnosis.BoostModeValue, cat.Actions.Single(a => a.Definition.Key == CpuDiagnosis.BoostModeActionId).Definition.AllowedValues!);
        Assert.Empty(CpuLabHandlers.Create(new HandlerEnvironment(new Nexus.WindowsActions.Registry.InMemoryRegistryBackend(), Path.GetTempPath(), false, false)));
    }

    private sealed class FakePower : IPowerBackend
    {
        public Guid Active;
        public readonly Dictionary<(Guid, Guid, Guid), uint> Values = [];
        public Guid GetActiveScheme() => Active;
        public void SetActiveScheme(Guid scheme) => Active = scheme;
        public IReadOnlyList<Guid> EnumerateSchemes() => [Active];
        public uint ReadAcValue(Guid s, Guid g, Guid k) => Values.TryGetValue((s, g, k), out var v) ? v : 0;
        public void WriteAcValue(Guid s, Guid g, Guid k, uint v) => Values[(s, g, k)] = v;
    }

    [Theory]
    [InlineData(CpuDiagnosis.MaxStateActionId, null, 80u, 100u)]
    [InlineData(CpuDiagnosis.BoostModeActionId, "2", 0u, 2u)]
    public void Power_fix_applies_and_restores_through_the_executor(string key, string? value, uint before, uint expected)
    {
        var scheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var entry = Catalog().Actions.Single(a => a.Definition.Key == key);
        if (value is not null) entry = entry with { DesiredValue = value };
        var action = entry.TryInstantiate(new Dictionary<string, string> { ["activeScheme"] = scheme.ToString("D") }, out var reason);
        Assert.True(action is not null, reason);
        var setting = Guid.Parse(action!.Target["setting"]);

        var power = new FakePower { Active = scheme };
        power.Values[(scheme, ProcessorPowerSettings.Subgroup, setting)] = before;
        var handlers = new HandlerRegistry().Add(new PowerSettingValueHandler(power));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("cpulab", "1");

        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, new DesiredStateResolver(handlers).Resolve([action], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal(expected, power.Values[(scheme, ProcessorPowerSettings.Subgroup, setting)]);

        exec.RevertRun(run);
        Assert.Equal(before, power.Values[(scheme, ProcessorPowerSettings.Subgroup, setting)]);
    }

    [Fact]
    public void Boost_mode_requires_an_allowed_value()
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == CpuDiagnosis.BoostModeActionId);
        Assert.Null(entry.TryInstantiate(new Dictionary<string, string> { ["activeScheme"] = Guid.NewGuid().ToString("D") }, out var reason));
        Assert.Contains("1, 2", reason!, StringComparison.Ordinal);
    }

    // ================================================================== Armazenamento e comparação

    [Fact]
    public void Store_round_trips_and_compares_runs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-cpulab-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CpuLabStore(dir);
            var before = QuickRun(3.9, 3.8) with
            {
                TakenAt = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), AllCoreGhz = 3.6, MultiThreadGops = 40,
                CacheSteps = [new CacheStep("L1", 32 * K, 48 * K, 1, 4), new CacheStep("RAM", null, null, 95, 400)],
                CoreToCoreCores = [0, 1], CoreToCoreNs = [[null, 20.5], [20.5, null]],
            };
            var after = before with { TakenAt = before.TakenAt.AddHours(1), AllCoreGhz = 4.0, CoreFrequencies = QuickRun(4.4, 4.3).CoreFrequencies };
            store.Save(before);
            var path = store.Save(after);
            Assert.EndsWith("-rapida.json", path, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "cpu-lixo.json"), "{ quebrado");

            var all = store.LoadAll();
            Assert.Equal(2, all.Count);
            Assert.Equal(after.TakenAt, all[0].TakenAt);
            Assert.Equal(20.5, all[0].Matrix!.At(0, 1));
            Assert.Null(all[0].Matrix!.At(0, 0));
            Assert.Equal(2, all[0].CacheSteps.Count);

            var lines = RunComparer.Compare(all[1], all[0]);
            Assert.Contains(lines, l => l.StartsWith("Núcleo mais rápido: 3,90 → 4,40 GHz", StringComparison.Ordinal) && l.Contains("melhor", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Latência da RAM", StringComparison.Ordinal) && l.Contains("igual", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Non_windows_power_reader_reports_why()
    {
        if (OperatingSystem.IsWindows()) return;
        var s = CpuPowerReader.Create().Read();
        Assert.Null(s.MaxStateAc);
        Assert.NotEmpty(s.Notes);
        Assert.Equal(Environment.ProcessorCount, TopologyReader.Read().LogicalCount);
    }
}

/// <summary>Sem paralelismo: estes testes ocupam todos os processadores e atrapalhariam testes com tempo limite (ex.: regex).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CpuLabSerialGroup
{
    public const string Name = "Laboratório do processador (medições reais)";
}

[Trait("Category", "Unit")]
[Collection(CpuLabSerialGroup.Name)]
public sealed class CpuLabMeasurementTests
{
    private const long K = 1024, M = 1024 * 1024;

    // ================================================================== Medições reais, reduzidas (rodam no Linux, sem P/Invoke)

    [Fact]
    public void Frequency_probe_measures_something_plausible()
    {
        var ghz = FrequencyProbe.MeasureCurrentThread(TimeSpan.FromMilliseconds(20), 3, 10, CancellationToken.None);
        Assert.InRange(ghz, 0.01, 15);
    }

    [Fact]
    public void Latency_probe_measures_small_and_larger_sets()
    {
        var rnd = new Random(1);
        var small = LatencyProbe.Measure(16 * K, rnd, CancellationToken.None, minSteps: 1 << 16, targetMs: 5);
        var large = LatencyProbe.Measure(8 * M, rnd, CancellationToken.None, minSteps: 1 << 16, targetMs: 5);
        Assert.InRange(small, 0.05, 100_000); // só sanidade: a máquina de teste pode estar muito carregada
        Assert.InRange(large, 0.05, 100_000);
    }

    [Fact]
    public async Task Quick_battery_runs_reduced_on_any_os()
    {
        var topo = CpuTopology.Fallback(Math.Min(2, Environment.ProcessorCount));
        var runner = new CpuLabRunner(NoPinning.Instance, topo, CpuLabOptions.Tiny);
        var stages = new List<string>();
        var run = await runner.RunQuickAsync(new CpuLabRun { Brand = "teste" }, new SyncProgress<CpuLabProgress>(p => stages.Add(p.Stage)), CancellationToken.None);

        Assert.Equal(CpuRunKind.Quick, run.Kind);
        Assert.Equal("teste", run.Brand);
        Assert.Equal(topo.CoreCount, run.CoreFrequencies.Count);
        Assert.All(run.CoreFrequencies, c => Assert.True(c.Ghz > 0));
        Assert.NotEmpty(run.Latency);
        Assert.Equal(256 * K, run.Latency[^1].Bytes);
        Assert.NotEmpty(run.AllCoreSeries);
        Assert.True(run.MultiThreadGops > 0);
        if (topo.CoreCount >= 2) Assert.Equal(2, run.CoreToCoreCores.Count);
        Assert.Contains("Pronto", stages);
    }

    [Fact]
    public async Task Sustained_test_runs_reduced_and_can_be_cancelled()
    {
        var topo = CpuTopology.Fallback(Math.Min(2, Environment.ProcessorCount));
        var runner = new CpuLabRunner(NoPinning.Instance, topo, CpuLabOptions.Tiny);
        var run = await runner.RunSustainedAsync(new CpuLabRun(), TimeSpan.FromSeconds(1), 0, null, CancellationToken.None);
        Assert.Equal(CpuRunKind.Sustained, run.Kind);
        // Janelas de 0,1 s; numa máquina de teste carregada algumas podem ficar sem fatia (a thread não rodou nelas).
        Assert.InRange(run.AllCoreSeries.Count, 1, 12);
        Assert.Equal(run.AllCoreSeries.Count >= 4, run.Throttle is not null);
        Assert.True(run.SingleThreadGops > 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunSustainedAsync(new CpuLabRun(), TimeSpan.FromSeconds(30), 0, null, cts.Token));
    }

    [Fact]
    public async Task Core_to_core_pair_gives_a_latency()
    {
        if (Environment.ProcessorCount < 2) return;
        var ns = await CoreToCoreProbe.MeasurePairAsync(NoPinning.Instance, new LogicalCpu(0, 0, 0), new LogicalCpu(1, 0, 1), 2000, CancellationToken.None);
        Assert.NotNull(ns);
        Assert.InRange(ns!.Value, 1, 1_000_000);
    }

    private sealed class SyncProgress<TValue>(Action<TValue> report) : IProgress<TValue>
    {
        public void Report(TValue value) { lock (this) report(value); }
    }
}
