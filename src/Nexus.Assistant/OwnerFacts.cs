using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;
using Nexus.Lab.Memory;

namespace Nexus.Assistant;

/// <summary>
/// Monta os fatos das regras "só você pode fazer" (<see cref="OwnerChecks"/>) a partir do que os laboratórios leram.
/// Puro: recebe relatórios prontos (ou null quando a leitura falhou) e nunca lê o sistema.
/// </summary>
public static class OwnerFactsBuilder
{
    public static OwnerCheckFacts From(GpuLabReport? gpu, MemoryAnalysis? memory, SystemFacts? facts)
    {
        var hardware = gpu?.Hardware.ToList() ?? [];
        var discrete = gpu?.Primary is { IsIntegrated: false } p ? p : hardware.FirstOrDefault(a => !a.IsIntegrated);
        var integrated = hardware.FirstOrDefault(a => a.IsIntegrated);
        bool? laptop = gpu is not null && (gpu.Settings.HasBattery is not null || gpu.Monitors.Count > 0) ? gpu.IsLaptop : memory?.Machine.Portable;

        var panel = gpu?.Monitors.FirstOrDefault(m => m.Internal);
        var owner = panel is null || panel.AdapterLuid == 0 ? DisplayOwner.Unknown
            : discrete is not null && panel.AdapterLuid == discrete.Luid ? DisplayOwner.Discrete
            : integrated is not null && panel.AdapterLuid == integrated.Luid ? DisplayOwner.Integrated
            : DisplayOwner.Unknown;

        return new OwnerCheckFacts(laptop, discrete is null ? null : Gpu(discrete), integrated is not null, owner, Memory(memory, facts));
    }

    public static OwnerGpu Gpu(GpuAdapter a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var rb = HardwareCaps.ResizableBar(a);
        var (link, _) = HardwareCaps.PcieWithGpuCeiling(a);
        return new OwnerGpu(a.Name, GpuIdentity.VendorName(a.Vendor), HardwareCaps.IsIntelArcDiscrete(a), a.Architecture.ResizableBarCapable,
            rb.Enabled, rb.Enabled is null ? rb.Evidence : $"{rb.Evidence} Fonte: {rb.Source}.",
            link.CurrentGen, link.CurrentWidth, link.MaxGen, link.MaxWidth, link.PortMaxWidth);
    }

    /// <summary>SMBIOS do laboratório de memória quando houver; senão os módulos da descoberta (WMI) com o número de peça decodificado.</summary>
    public static OwnerMemory? Memory(MemoryAnalysis? memory, SystemFacts? facts)
    {
        if (memory is not null && memory.Inventory.Populated.Count > 0)
        {
            var pop = memory.Inventory.Populated;
            var part = memory.Parts.Where(x => x.Decoded && x.SpeedMts is > 0).OrderBy(x => x.SpeedMts).FirstOrDefault();
            return new OwnerMemory(Min(pop.Select(d => d.ConfiguredSpeedMts)), Min(pop.Select(d => d.SpeedMts)),
                part?.SpeedMts is { } ps ? (uint)ps : null, part?.Profile, part?.PartNumber);
        }
        var modules = facts?.MemoryModules ?? [];
        if (modules.Count == 0) return null;
        var decoded = modules.Select(m => m.PartNumber).Where(s => !string.IsNullOrWhiteSpace(s)).Select(PartNumberDecoder.Decode)
            .Where(x => x.Decoded && x.SpeedMts is > 0).OrderBy(x => x.SpeedMts).FirstOrDefault();
        return new OwnerMemory(Min(modules.Select(m => m.ConfiguredSpeedMts)), Min(modules.Select(m => m.MaxSpeedMts)),
            decoded?.SpeedMts is { } ds ? (uint)ds : null, decoded?.Profile, decoded?.PartNumber);
    }

    private static uint? Min(IEnumerable<uint?> values) => values.Where(v => v is > 0).DefaultIfEmpty(null).Min();
}
