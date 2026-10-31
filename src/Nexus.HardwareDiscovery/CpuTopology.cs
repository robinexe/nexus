using System.Buffers.Binary;
using System.Numerics;

namespace Nexus.HardwareDiscovery;

public sealed record CpuTopologyInfo(int Packages, int PhysicalCores, int LogicalProcessors, bool Smt, int EfficiencyClasses,
    int PerformanceCores, IReadOnlyList<ulong> L3CacheBytes, int NumaNodes);

/// <summary>
/// Decodificador do buffer de GetLogicalProcessorInformationEx(RelationAll) para x64. Puro: testável sem Windows.
/// Layout: Relationship (int32) @0, Size (uint32) @4, união @8.
/// PROCESSOR_RELATIONSHIP: Flags @8, EfficiencyClass @9, GroupCount (uint16) @30, GROUP_AFFINITY[] @32 (16 bytes cada, Mask = 8 bytes).
/// CACHE_RELATIONSHIP: Level @8, CacheSize (uint32) @12.
/// </summary>
public static class CpuTopologyParser
{
    private const int RelationProcessorCore = 0, RelationNumaNode = 1, RelationCache = 2, RelationProcessorPackage = 3;
    private const byte LtpPcSmt = 1;

    public static CpuTopologyInfo Parse(ReadOnlySpan<byte> buffer)
    {
        int packages = 0, cores = 0, logical = 0, numa = 0;
        var smt = false;
        var classes = new Dictionary<byte, int>();
        var l3 = new List<ulong>();
        var pos = 0;
        while (pos + 8 <= buffer.Length)
        {
            var rel = BinaryPrimitives.ReadInt32LittleEndian(buffer[pos..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(pos + 4)..]);
            if (size < 8 || pos + size > buffer.Length) throw new InvalidDataException($"Registro de topologia inválido em {pos} (tamanho {size}).");
            var rec = buffer.Slice(pos, size);
            switch (rel)
            {
                case RelationProcessorCore when size >= 32:
                    cores++;
                    if ((rec[8] & LtpPcSmt) != 0) smt = true;
                    classes[rec[9]] = classes.GetValueOrDefault(rec[9]) + 1;
                    var groups = BinaryPrimitives.ReadUInt16LittleEndian(rec[30..]);
                    for (var g = 0; g < groups && 32 + (g + 1) * 16 <= size; g++)
                        logical += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(rec[(32 + g * 16)..]));
                    break;
                case RelationProcessorPackage:
                    packages++;
                    break;
                case RelationNumaNode:
                    numa++;
                    break;
                case RelationCache when size >= 16 && rec[8] == 3:
                    l3.Add(BinaryPrimitives.ReadUInt32LittleEndian(rec[12..]));
                    break;
            }
            pos += size;
        }
        // Em CPUs híbridas, a maior classe de eficiência é a dos núcleos de desempenho.
        var perf = classes.Count == 0 ? 0 : classes[classes.Keys.Max()];
        return new CpuTopologyInfo(packages, cores, logical, smt, classes.Count, perf, l3, numa);
    }
}
