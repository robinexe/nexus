using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Lab.Cpu;

/// <summary>Processador lógico: grupo do Windows (até 64 por grupo) e número dentro do grupo. Index é sequencial no PC.</summary>
public readonly record struct LogicalCpu(int Index, ushort Group, byte Number);

/// <summary>Núcleo físico com suas threads. EfficiencyClass maior = núcleo de desempenho (P) em CPUs híbridas.</summary>
public sealed record CoreInfo(int Index, IReadOnlyList<LogicalCpu> Threads, byte EfficiencyClass);

/// <summary>Cache como o Windows a descreve, com os processadores lógicos que a compartilham.</summary>
public sealed record CacheDomain(int Level, CacheKind Kind, long SizeBytes, int Ways, int LineBytes, IReadOnlyList<int> LogicalIndices);

public sealed record CpuTopology(IReadOnlyList<CoreInfo> Cores, IReadOnlyList<CacheDomain> Caches, int Groups, int Packages, int NumaNodes, string Source)
{
    public int LogicalCount => Cores.Sum(c => c.Threads.Count);
    public int CoreCount => Cores.Count;
    public bool Smt => Cores.Any(c => c.Threads.Count > 1);
    public bool Hybrid => Cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
    public byte TopEfficiencyClass => Cores.Count == 0 ? (byte)0 : Cores.Max(c => c.EfficiencyClass);
    public int PerformanceCores => Hybrid ? Cores.Count(c => c.EfficiencyClass == TopEfficiencyClass) : Cores.Count;
    public int EfficiencyCores => Cores.Count - PerformanceCores;

    /// <summary>Grupos de núcleos que compartilham a mesma L3 (CCX na AMD; o anel inteiro na Intel).</summary>
    public IReadOnlyList<IReadOnlyList<int>> L3Clusters()
    {
        var l3 = Caches.Where(c => c.Level == 3).ToList();
        if (l3.Count == 0) return [Cores.Select(c => c.Index).ToList()];
        return l3.Select(c => (IReadOnlyList<int>)Cores.Where(core => core.Threads.Any(t => c.LogicalIndices.Contains(t.Index))).Select(core => core.Index).ToList())
                 .Where(g => g.Count > 0).ToList();
    }

    public long TotalL3Bytes => Caches.Where(c => c.Level == 3).Sum(c => c.SizeBytes);

    public bool IsPerformanceCore(CoreInfo core)
    {
        ArgumentNullException.ThrowIfNull(core);
        return !Hybrid || core.EfficiencyClass == TopEfficiencyClass;
    }

    /// <summary>Sem a API do Windows: cada processador lógico vira um "núcleo" (grupo 0). Usado no Linux e como último recurso.</summary>
    public static CpuTopology Fallback(int logical)
    {
        logical = Math.Max(1, logical);
        var cores = Enumerable.Range(0, logical).Select(i => new CoreInfo(i, [new LogicalCpu(i, (ushort)(i / 64), (byte)(i % 64))], 0)).ToList();
        return new CpuTopology(cores, [], (logical + 63) / 64, 1, 1, "Environment.ProcessorCount (topologia real indisponível)");
    }
}

/// <summary>
/// Decodifica o buffer de GetLogicalProcessorInformationEx(RelationAll) em x64, preservando o mapa de núcleos e caches.
/// Layout: Relationship @0, Size @4. PROCESSOR_RELATIONSHIP: Flags @8, EfficiencyClass @9, GroupCount @30, GROUP_AFFINITY[] @32.
/// CACHE_RELATIONSHIP: Level @8, Associativity @9, LineSize @10, CacheSize @12, Type @16, GroupCount @38 (0 em Windows antigos =
/// 1), GROUP_AFFINITY[] @40. GROUP_AFFINITY: Mask (8 bytes) @0, Group (2 bytes) @8, total 16 bytes.
/// </summary>
public static class TopologyParser
{
    private const int RelationProcessorCore = 0, RelationNumaNode = 1, RelationCache = 2, RelationProcessorPackage = 3, RelationGroup = 4;

    public static CpuTopology Parse(ReadOnlySpan<byte> buffer)
    {
        var rawCores = new List<(List<(ushort G, byte N)> Threads, byte Eff)>();
        var rawCaches = new List<(int Level, CacheKind Kind, long Size, int Ways, int Line, List<(ushort G, byte N)> Lps)>();
        int packages = 0, numa = 0, groups = 0;
        var pos = 0;
        while (pos + 8 <= buffer.Length)
        {
            var rel = BinaryPrimitives.ReadInt32LittleEndian(buffer[pos..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(pos + 4)..]);
            if (size < 8 || pos + size > buffer.Length) throw new InvalidDataException($"Registro de topologia inválido em {pos} (tamanho {size}).");
            var rec = buffer.Slice(pos, size);
            switch (rel)
            {
                case RelationProcessorCore when size >= 48:
                {
                    var count = BinaryPrimitives.ReadUInt16LittleEndian(rec[30..]);
                    rawCores.Add((Masks(rec, 32, count), rec[9]));
                    break;
                }
                case RelationCache when size >= 56:
                {
                    var type = BinaryPrimitives.ReadInt32LittleEndian(rec[16..]);
                    var kind = type switch { 1 => CacheKind.Instruction, 2 => CacheKind.Data, _ => CacheKind.Unified };
                    var count = BinaryPrimitives.ReadUInt16LittleEndian(rec[38..]);
                    rawCaches.Add((rec[8], kind, BinaryPrimitives.ReadUInt32LittleEndian(rec[12..]), rec[9],
                        BinaryPrimitives.ReadUInt16LittleEndian(rec[10..]), Masks(rec, 40, Math.Max((ushort)1, count))));
                    break;
                }
                case RelationProcessorPackage: packages++; break;
                case RelationNumaNode: numa++; break;
                case RelationGroup when size >= 10: groups = BinaryPrimitives.ReadUInt16LittleEndian(rec[10..]); break;
            }
            pos += size;
        }

        var all = rawCores.SelectMany(c => c.Threads).Distinct().OrderBy(t => t.G).ThenBy(t => t.N).ToList();
        var index = all.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var cores = rawCores
            .OrderBy(c => c.Threads.Min(t => index[t]))
            .Select((c, i) => new CoreInfo(i, c.Threads.Select(t => new LogicalCpu(index[t], t.G, t.N)).OrderBy(t => t.Index).ToList(), c.Eff))
            .ToList();
        var caches = rawCaches
            .Select(c => new CacheDomain(c.Level, c.Kind, c.Size, c.Ways, c.Line, c.Lps.Where(index.ContainsKey).Select(t => index[t]).Order().ToList()))
            .ToList();
        return new CpuTopology(cores, caches, Math.Max(1, groups), Math.Max(1, packages), Math.Max(1, numa), "GetLogicalProcessorInformationEx");
    }

    private static List<(ushort, byte)> Masks(ReadOnlySpan<byte> rec, int offset, int count)
    {
        var list = new List<(ushort, byte)>();
        for (var g = 0; g < count && offset + (g + 1) * 16 <= rec.Length; g++)
        {
            var at = offset + g * 16;
            var mask = BinaryPrimitives.ReadUInt64LittleEndian(rec[at..]);
            var group = BinaryPrimitives.ReadUInt16LittleEndian(rec[(at + 8)..]);
            for (byte b = 0; b < 64; b++)
                if ((mask & (1UL << b)) != 0) list.Add((group, b));
        }
        return list;
    }
}

/// <summary>Lê a topologia do Windows (somente leitura).</summary>
public static partial class TopologyReader
{
    public static CpuTopology Read()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return CpuTopology.Fallback(Environment.ProcessorCount);
        try { return ReadWindows(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return CpuTopology.Fallback(Environment.ProcessorCount) with { Source = $"Environment.ProcessorCount (GetLogicalProcessorInformationEx falhou: {ex.Message})" };
        }
    }

    [SupportedOSPlatform("windows")]
    private static CpuTopology ReadWindows()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF, null, ref len);
        if (len == 0) throw new InvalidOperationException("tamanho zero");
        var buffer = new byte[len];
        if (!GetLogicalProcessorInformationEx(0xFFFF, buffer, ref len))
            throw new InvalidOperationException($"código Win32 {Marshal.GetLastPInvokeError()}");
        return TopologyParser.Parse(buffer.AsSpan(0, (int)len));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, [Out] byte[]? buffer, ref uint length);
}
