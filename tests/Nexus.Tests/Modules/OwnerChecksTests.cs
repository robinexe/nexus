using Nexus.Assistant;
using Nexus.Automation;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;

namespace Nexus.Tests.Modules;

/// <summary>
/// "Só você pode fazer": Resizable BAR, XMP/EXPO, MUX e link PCIe. Cada regra com fatos falsos nos três casos (acusa, não acusa,
/// não dá para saber), mais a leitura pura (HardwareCaps) e a montagem a partir dos laboratórios.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OwnerChecksTests
{
    private static OwnerGpu Nv(bool? rebar = null, bool capable = true, int? cw = 16, int? gw = 16, int? port = 16, int? cg = 4, int? gg = 4) =>
        new("NVIDIA GeForce RTX 3060", "NVIDIA", false, capable, rebar, "evidência", cg, cw, gg, gw, port);

    private static OwnerGpu Arc(bool? rebar) => new("Intel Arc A770", "Intel", true, true, rebar, "evidência");

    private static OwnerGpu Amd(bool? rebar) => new("AMD Radeon RX 6700 XT", "AMD", false, true, rebar, "evidência");

    // ------------------------------------------------------------------ Resizable BAR

    [Fact]
    public void ReBar_strong_warning_only_for_intel_arc()
    {
        var off = OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Arc(false)))!;
        Assert.Equal(DiagnosticSeverity.Attention, off.Severity);
        Assert.Equal(FactStatus.Detected, off.Basis);
        Assert.Contains("24%", off.Owner!.Gain, StringComparison.Ordinal);
        Assert.Equal(OwnerChecks.ArcSource.Url, off.Owner.SourceUrl);

        var on = OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Arc(true)))!;
        Assert.Equal(DiagnosticSeverity.Ok, on.Severity);

        // Desconhecido: "confira na BIOS", sem afirmar estado.
        var unknown = OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Arc(null)))!;
        Assert.Equal(DiagnosticSeverity.Attention, unknown.Severity);
        Assert.Equal(FactStatus.Unavailable, unknown.Basis);
        Assert.Contains("não afirma", unknown.Evidence, StringComparison.Ordinal);
        Assert.Contains("confira", unknown.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReBar_nvidia_and_amd_are_informational_and_silent_without_reading()
    {
        var nvOff = OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Nv(false)))!;
        Assert.Equal(DiagnosticSeverity.Info, nvOff.Severity);
        Assert.Contains("0 a 2%", nvOff.Owner!.Gain, StringComparison.Ordinal);
        Assert.Equal(OwnerChecks.ReBarSource.Url, nvOff.Owner.SourceUrl);
        Assert.Equal(DiagnosticSeverity.Ok, OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Nv(true)))!.Severity);
        Assert.Null(OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Nv(null))));          // não lido: não acusa
        Assert.Null(OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Nv(false, capable: false)))); // GTX 16/Turing: não suporta

        var amdOff = OwnerChecks.ResizableBar(new OwnerCheckFacts(false, Amd(false)))!;
        Assert.Equal(DiagnosticSeverity.Info, amdOff.Severity);
        Assert.Contains("7%", amdOff.Owner!.Gain, StringComparison.Ordinal);
        Assert.Contains("Smart Access Memory", amdOff.Title, StringComparison.Ordinal);
        Assert.Null(OwnerChecks.ResizableBar(new OwnerCheckFacts(false)));                     // sem GPU dedicada
        Assert.Contains("BIOS do fabricante", OwnerChecks.ResizableBar(new OwnerCheckFacts(true, Nv(false)))!.Owner!.Explanation, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ XMP / EXPO

    [Fact]
    public void Xmp_flags_desktop_only_when_clearly_below()
    {
        var below = OwnerChecks.MemoryProfile(new OwnerCheckFacts(false, Memory: new OwnerMemory(2133, 2133, 3200, "XMP", "CMK16GX4M2B3200C16")))!;
        Assert.Equal(DiagnosticSeverity.Attention, below.Severity);
        Assert.Equal(FactStatus.Inferred, below.Basis);
        Assert.Contains("XMP", below.Title, StringComparison.Ordinal);
        Assert.Contains("3200", below.Evidence, StringComparison.Ordinal);
        Assert.Contains("JEDEC", below.Owner!.Explanation, StringComparison.Ordinal); // a limitação é dita
        Assert.Equal(OwnerChecks.ExpoSource.Url, below.Owner.SourceUrl);

        // Firmware anuncia mais que o configurado (sem número de peça) também conta.
        Assert.NotNull(OwnerChecks.MemoryProfile(new OwnerCheckFacts(false, Memory: new OwnerMemory(4800, 6000))));

        // Na velocidade, ou diferença de arredondamento: nada.
        Assert.Null(OwnerChecks.MemoryProfile(new OwnerCheckFacts(false, Memory: new OwnerMemory(3200, 3200, 3200, "XMP"))));
        Assert.Null(OwnerChecks.MemoryProfile(new OwnerCheckFacts(false, Memory: new OwnerMemory(3192, 3200))));
        // Só JEDEC conhecido e igual ao configurado: não dá para saber se há perfil, então não acusa.
        Assert.Null(OwnerChecks.MemoryProfile(new OwnerCheckFacts(false, Memory: new OwnerMemory(2666, 2666))));
    }

    [Fact]
    public void Xmp_unknown_and_laptop_cases()
    {
        Assert.Null(OwnerChecks.MemoryProfile(new OwnerCheckFacts(false)));
        Assert.Null(OwnerChecks.MemoryProfile(new OwnerCheckFacts(false, Memory: new OwnerMemory(null, 3200, 3600))));
        Assert.Null(OwnerChecks.MemoryProfile(new OwnerCheckFacts(false, Memory: new OwnerMemory(3200, null))));
        var laptop = OwnerChecks.MemoryProfile(new OwnerCheckFacts(true, Memory: new OwnerMemory(2666, 3200)))!;
        Assert.Equal(DiagnosticSeverity.Info, laptop.Severity);
        Assert.Contains("notebook", laptop.Owner!.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ MUX

    [Fact]
    public void Mux_depends_on_who_drives_the_internal_panel()
    {
        var hybrid = OwnerChecks.Mux(new OwnerCheckFacts(true, Nv(), true, DisplayOwner.Integrated))!;
        Assert.Equal(DiagnosticSeverity.Info, hybrid.Severity);
        Assert.Equal(FactStatus.Detected, hybrid.Basis);
        Assert.Contains("+9%", hybrid.Owner!.Gain, StringComparison.Ordinal);
        Assert.Equal(OwnerChecks.MuxSource.Url, hybrid.Owner.SourceUrl);

        Assert.Equal(DiagnosticSeverity.Ok, OwnerChecks.Mux(new OwnerCheckFacts(true, Nv(), true, DisplayOwner.Discrete))!.Severity);

        var unknown = OwnerChecks.Mux(new OwnerCheckFacts(true, Nv(), true, DisplayOwner.Unknown))!;
        Assert.Equal(FactStatus.Inferred, unknown.Basis);
        Assert.Contains("Se o software", unknown.Owner!.Explanation, StringComparison.Ordinal); // condicional, não afirma MUX

        Assert.Null(OwnerChecks.Mux(new OwnerCheckFacts(false, Nv(), true, DisplayOwner.Integrated)));  // desktop
        Assert.Null(OwnerChecks.Mux(new OwnerCheckFacts(null, Nv(), true, DisplayOwner.Integrated)));   // não sabe se é notebook
        Assert.Null(OwnerChecks.Mux(new OwnerCheckFacts(true, Nv(), false, DisplayOwner.Unknown)));     // sem integrada
        Assert.Null(OwnerChecks.Mux(new OwnerCheckFacts(true, Arc(null), true, DisplayOwner.Integrated))); // só NVIDIA/AMD
    }

    // ------------------------------------------------------------------ PCIe

    [Fact]
    public void Pcie_flags_only_below_what_the_gpu_supports_and_is_honest_on_laptops()
    {
        // GTX 1650 do responsável: PCIe 3.0 x8 num notebook, GPU x16. Informação, "projeto do notebook".
        var laptop = OwnerChecks.PcieLink(new OwnerCheckFacts(true, Nv(cw: 8, gw: 16, port: null, cg: 3, gg: 3)))!;
        Assert.Equal(DiagnosticSeverity.Info, laptop.Severity);
        Assert.Contains("projeto do notebook", laptop.Title, StringComparison.Ordinal);
        Assert.Contains("não é defeito", laptop.Owner!.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2%", laptop.Owner.Gain, StringComparison.Ordinal);

        var desktop = OwnerChecks.PcieLink(new OwnerCheckFacts(false, Nv(cw: 8, gw: 16, port: 16)))!;
        Assert.Equal(DiagnosticSeverity.Attention, desktop.Severity);
        Assert.Contains("reencaixe", desktop.Owner!.Explanation, StringComparison.Ordinal);

        var slot = OwnerChecks.PcieLink(new OwnerCheckFacts(false, Nv(cw: 4, gw: 16, port: 4)))!;
        Assert.Equal(DiagnosticSeverity.Info, slot.Severity);
        Assert.Contains("slot", slot.Owner!.Explanation, StringComparison.Ordinal);

        // A GPU é x8 de fábrica (ex.: RTX 4060): x8 é o máximo dela, nada a apontar. Nem a geração em repouso conta.
        Assert.Null(OwnerChecks.PcieLink(new OwnerCheckFacts(false, Nv(cw: 8, gw: 8, cg: 1, gg: 4))));
        // Sem leitura: nada.
        Assert.Null(OwnerChecks.PcieLink(new OwnerCheckFacts(false, Nv(cw: null, gw: 16))));
        Assert.Null(OwnerChecks.PcieLink(new OwnerCheckFacts(false)));
    }

    [Fact]
    public void Every_owner_finding_has_explanation_gain_and_a_listed_https_source()
    {
        var all = new[]
        {
            new OwnerCheckFacts(false, Arc(false), Memory: new OwnerMemory(2133, 2133, 3200, "XMP")),
            new OwnerCheckFacts(true, Nv(false, cw: 8, gw: 16), true, DisplayOwner.Integrated, new OwnerMemory(2666, 3200)),
            new OwnerCheckFacts(true, Amd(true), true, DisplayOwner.Unknown),
        }.SelectMany(OwnerChecks.Evaluate).ToList();
        Assert.True(all.Count >= 8);
        foreach (var f in all)
        {
            Assert.StartsWith("owner.", f.Id, StringComparison.Ordinal);
            Assert.NotNull(f.Owner);
            Assert.False(string.IsNullOrWhiteSpace(f.Owner!.Explanation));
            Assert.False(string.IsNullOrWhiteSpace(f.Owner.Gain));
            Assert.StartsWith("https://", f.Owner.SourceUrl, StringComparison.Ordinal);
            Assert.Contains(f.Owner.SourceUrl, OwnerChecks.SourceUrls);
            Assert.Null(f.RelatedActionId); // só informa: nenhuma ação do catálogo
        }
    }

    // ------------------------------------------------------------------ leitura pura (HardwareCaps)

    private static GpuAdapter Dgpu(uint vendor, uint device, ulong vram = 8UL << 30, NvidiaReading? nv = null, bool? uploadHeap = null, PcieLinkState? pcie = null) =>
        new() { Name = "GPU", VendorId = vendor, DeviceId = device, DedicatedVideoBytes = vram, Nvidia = nv, GpuUploadHeapSupported = uploadHeap, Pcie = pcie ?? new(null, null, null, null) };

    [Fact]
    public void ResizableBar_reading_only_claims_what_the_source_proves()
    {
        // NVIDIA: BAR1 de 256 MB = desligado; cobrindo a VRAM = ligado.
        Assert.False(HardwareCaps.ResizableBar(Dgpu(0x10DE, 0x2503, nv: new NvidiaReading { VramTotalBytes = 12UL << 30, Bar1TotalBytes = 256UL << 20 })).Enabled);
        Assert.True(HardwareCaps.ResizableBar(Dgpu(0x10DE, 0x2503, nv: new NvidiaReading { VramTotalBytes = 12UL << 30, Bar1TotalBytes = 16UL << 30 })).Enabled);
        // D3D12 upload heaps: true prova ligado; false NÃO prova desligado.
        var arcOn = HardwareCaps.ResizableBar(Dgpu(0x8086, 0x56A0, uploadHeap: true));
        Assert.True(arcOn.Enabled);
        Assert.Contains("D3D12", arcOn.Source, StringComparison.Ordinal);
        var arcFalse = HardwareCaps.ResizableBar(Dgpu(0x8086, 0x56A0, uploadHeap: false));
        Assert.Null(arcFalse.Enabled);
        Assert.Contains("não prova", arcFalse.Evidence, StringComparison.Ordinal);
        Assert.Null(HardwareCaps.ResizableBar(Dgpu(0x1002, 0x73DF)).Enabled);
        // Integrada: não se aplica.
        Assert.Same(ReBarReading.NotApplicable, HardwareCaps.ResizableBar(Dgpu(0x1002, 0x1638, vram: 512UL << 20)));
        Assert.True(HardwareCaps.IsIntelArcDiscrete(Dgpu(0x8086, 0x56A0)));
        Assert.False(HardwareCaps.IsIntelArcDiscrete(Dgpu(0x10DE, 0x2503)));
    }

    [Fact]
    public void Pcie_ceiling_uses_the_gpu_architecture_when_windows_overstates_it()
    {
        // GTX 1650 (Turing, PCIe 3.0) informada como 4.0 x16: o teto vira 3.0 e a nota explica.
        var gtx1650 = Dgpu(0x10DE, 0x1F99, 4UL << 30, pcie: new PcieLinkState(3, 4, 8, 16));
        var (link, note) = HardwareCaps.PcieWithGpuCeiling(gtx1650);
        Assert.Equal(3, link.MaxGen);
        Assert.Equal(16, link.MaxWidth);
        Assert.Contains("Turing", note, StringComparison.Ordinal);
        // Dentro do teto: nada muda.
        var rtx = Dgpu(0x10DE, 0x2503, pcie: new PcieLinkState(4, 4, 16, 16));
        Assert.Null(HardwareCaps.PcieWithGpuCeiling(rtx).Note);
        Assert.Equal(3, HardwareCaps.ArchitectureMaxPcieGen(GpuIdentity.Architecture(0x10DE, 0x1F99)));
        Assert.Null(HardwareCaps.ArchitectureMaxPcieGen(GpuArchitecture.Unknown));

        // Verdito em repouso: no notebook, largura menor é informação; no desktop, alerta.
        Assert.Equal("info", Pcie.IdleVerdict(link, false, laptop: true).Level);
        Assert.Contains("projeto do fabricante", Pcie.IdleVerdict(link, false, laptop: true).Text, StringComparison.Ordinal);
        Assert.Equal("warn", Pcie.IdleVerdict(link, false, laptop: false).Level);
    }

    [Fact]
    public void Gpu_lab_findings_follow_the_same_honesty()
    {
        var gtx = Dgpu(0x10DE, 0x1F99, 4UL << 30, pcie: new PcieLinkState(3, 4, 8, 16)) with { IsHighPerformance = true };
        var report = new GpuLabReport { Adapters = [gtx], Settings = new GraphicsSettings { HasBattery = true } };
        var f = GpuDiagnostics.Evaluate(report, new DateOnly(2026, 11, 1));
        var pcie = Assert.Single(f, x => x.Id.StartsWith("pcie.width.", StringComparison.Ordinal));
        Assert.Equal("info", pcie.Level);
        Assert.Contains("PCIe 3.0 x16", pcie.Evidence, StringComparison.Ordinal); // não "4.0 x16"
        Assert.DoesNotContain(f, x => x.Id.StartsWith("rebar", StringComparison.Ordinal)); // Turing: sem ReBAR

        var arc = Dgpu(0x8086, 0x56A0, 16UL << 30) with { IsHighPerformance = true };
        var arcFindings = GpuDiagnostics.Evaluate(new GpuLabReport { Adapters = [arc] }, new DateOnly(2026, 11, 1));
        Assert.Equal("warn", Assert.Single(arcFindings, x => x.Id == "rebar.check").Level);
        var arcOn = GpuDiagnostics.Evaluate(new GpuLabReport { Adapters = [arc with { GpuUploadHeapSupported = true }] }, new DateOnly(2026, 11, 1));
        Assert.Contains(arcOn, x => x.Id == "rebar.on");
    }

    // ------------------------------------------------------------------ montagem a partir dos laboratórios

    [Fact]
    public void Builder_maps_the_owners_laptop()
    {
        var igpu = new GpuAdapter { Name = "AMD Radeon(TM) Graphics", VendorId = 0x1002, DeviceId = 0x1638, Luid = 0xA1, DedicatedVideoBytes = 512UL << 20 };
        var dgpu = new GpuAdapter { Name = "NVIDIA GeForce GTX 1650", VendorId = 0x10DE, DeviceId = 0x1F99, Luid = 0xB2, DedicatedVideoBytes = 4UL << 30,
            IsHighPerformance = true, Pcie = new PcieLinkState(3, 4, 8, 16) };
        var panel = new MonitorReport { Device = @"\\.\DISPLAY1", Internal = true, AdapterLuid = 0xA1, Current = new DisplayMode(1920, 1080, 144),
            Advice = DisplayModes.Advise(new DisplayMode(1920, 1080, 144), [new(1920, 1080, 144)]) };
        var report = new GpuLabReport { Adapters = [igpu, dgpu], Monitors = [panel], Settings = new GraphicsSettings { HasBattery = true } };
        var facts = new SystemFacts { MemoryModules = [new MemoryModule("DIMM 0", 8UL << 30, "DDR4", 3200, 3200, "Kingston", "KF432S20IB/8")] };

        var of = OwnerFactsBuilder.From(report, null, facts);
        Assert.True(of.IsLaptop);
        Assert.True(of.HasIntegratedGpu);
        Assert.Equal(DisplayOwner.Integrated, of.PanelOwner);
        Assert.Equal("NVIDIA", of.DiscreteGpu!.Vendor);
        Assert.Equal((8, 16, 3), (of.DiscreteGpu.PcieCurrentWidth!.Value, of.DiscreteGpu.GpuMaxWidth!.Value, of.DiscreteGpu.GpuMaxGen!.Value));
        Assert.False(of.DiscreteGpu.ReBarCapable);
        Assert.Equal((3200u, 3200u), (of.Memory!.ConfiguredMts!.Value, of.Memory.RatedMts!.Value));

        var findings = OwnerChecks.Evaluate(of);
        Assert.Contains(findings, x => x.Id == OwnerChecks.MuxId && x.Severity == DiagnosticSeverity.Info);
        Assert.Contains(findings, x => x.Id == OwnerChecks.PcieId && x.Severity == DiagnosticSeverity.Info);
        Assert.DoesNotContain(findings, x => x.Id == OwnerChecks.ReBarId);
        Assert.DoesNotContain(findings, x => x.Id == OwnerChecks.XmpId);
        Assert.DoesNotContain(findings, x => x.Severity == DiagnosticSeverity.Attention);

        var empty = OwnerFactsBuilder.From(null, null, null);
        Assert.Null(empty.IsLaptop);
        Assert.Null(empty.DiscreteGpu);
        Assert.Null(empty.Memory);
        Assert.Empty(OwnerChecks.Evaluate(empty));
    }

    [Fact]
    public void Assistant_lists_owner_findings_in_only_you_can_do()
    {
        var facts = new SystemFacts { CpuModel = "AMD Ryzen 5 5600H" };
        var owner = OwnerChecks.Evaluate(new OwnerCheckFacts(false, Arc(false), Memory: new OwnerMemory(2133, 2133, 3200, "XMP")));
        var scan = StepPlanners.MyPc(AssistantFakes.Cat(), facts, PcProfiler.Classify(facts), null, [], [], null, owner);
        Assert.Contains(scan.UserActions, u => u.Title.Contains("Resizable BAR", StringComparison.Ordinal) && u.Link == OwnerChecks.ArcSource.Url);
        var xmp = Assert.Single(scan.UserActions, u => u.Title.Contains("XMP", StringComparison.Ordinal));
        Assert.Contains("Ganho medido", xmp.Why, StringComparison.Ordinal);
        Assert.Contains(scan.Facts, x => x.Label == "Resizable BAR" && x.Tone == "warn");
        // Achados "em ordem" não viram tarefa do usuário.
        var ok = OwnerChecks.Evaluate(new OwnerCheckFacts(false, Arc(true)));
        Assert.Empty(StepPlanners.MyPc(AssistantFakes.Cat(), facts, PcProfiler.Classify(facts), null, [], [], null, ok).UserActions);
    }
}
