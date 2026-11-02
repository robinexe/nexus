namespace Nexus.Lab.Gpu;

/// <summary>
/// Estado do Resizable BAR (Smart Access Memory na AMD) de uma GPU dedicada, lido sem driver de kernel.
/// <para>Enabled: true = ligado (provado), false = desligado (provado), null = não dá para afirmar.</para>
/// </summary>
public sealed record ReBarReading(bool? Enabled, string Evidence, string Source)
{
    public static readonly ReBarReading NotApplicable = new(null, "GPU integrada: usa a memória do sistema; Resizable BAR não se aplica.", "");
}

/// <summary>
/// Capacidades de hardware que o Windows não entrega prontas: Resizable BAR e o teto PCIe da própria GPU.
/// Puras (testáveis com adaptadores sintéticos); a leitura de Windows fica em <c>Windows/D3D12Probe</c> e no NVML.
/// </summary>
public static class HardwareCaps
{
    /// <summary>
    /// Duas fontes, só afirmações que a fonte prova:
    /// 1) NVIDIA: tamanho da janela BAR1 pelo NVML (o mesmo número de "BAR1 Memory Usage" do nvidia-smi). Sem ReBAR ela tem
    ///    256 MB; com ReBAR cobre a VRAM inteira.
    /// 2) Qualquer fabricante: D3D12 "GPU upload heaps" (D3D12_FEATURE_D3D12_OPTIONS16.GPUUploadHeapSupported). A Microsoft
    ///    exige a memória de vídeo inteira visível pela CPU para expor o recurso: numa GPU dedicada, true = ReBAR ligado.
    ///    false NÃO prova desligado (o runtime/driver pode não oferecer o recurso), então vira "não dá para afirmar".
    /// </summary>
    public static ReBarReading ResizableBar(GpuAdapter a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.IsIntegrated) return ReBarReading.NotApplicable;
        var vram = a.Nvidia?.VramTotalBytes ?? a.DedicatedVideoBytes;
        if (a.Nvidia?.Bar1TotalBytes is { } bar1 && vram > 0)
        {
            const string src = "NVML (nvmlDeviceGetBAR1MemoryInfo, o mesmo do nvidia-smi)";
            if (bar1 <= 300UL << 20)
                return new(false, $"Janela BAR1 de {bar1 >> 20} MB (com Resizable BAR ela cobre a VRAM inteira, {vram >> 20} MB).", src);
            if (bar1 >= vram / 2)
                return new(true, $"Janela BAR1 de {bar1 >> 20} MB para {vram >> 20} MB de VRAM.", src);
        }
        if (a.GpuUploadHeapSupported == true)
            return new(true, "O Direct3D 12 oferece \"GPU upload heaps\", que exigem a memória de vídeo inteira visível pelo processador.",
                "D3D12 CheckFeatureSupport (D3D12_OPTIONS16.GPUUploadHeapSupported)");
        return new(null, a.GpuUploadHeapSupported == false
                ? "O Direct3D 12 não ofereceu \"GPU upload heaps\". Isso não prova que está desligado: driver ou Windows antigos também não oferecem."
                : "O Windows não expõe esse estado para esta placa sem ferramentas do fabricante.",
            "D3D12 / NVML");
    }

    /// <summary>Geração PCIe máxima da arquitetura (especificação do fabricante). Null = não tabelada (confia no que a placa informa).</summary>
    public static int? ArchitectureMaxPcieGen(GpuArchitecture arch) => arch?.Name switch
    {
        "Kepler ou anterior" or "Maxwell" or "Pascal" or "Volta" or "Turing" => 3,
        "Ampere" or "Ada Lovelace" => 4,
        "Hopper" or "Blackwell" => 5,
        "GCN (1ª–3ª geração)" or "GCN 4 (Polaris)" => 3,
        "RDNA 1" or "RDNA 2" or "RDNA 3" => 4,
        "RDNA 4" => 5,
        "Xe-HPG (Alchemist)" or "Xe2-HPG (Battlemage)" or "Xe-LP (DG1)" => 4,
        _ => null, // Vega (3.0 ou 4.0 conforme o chip) e o resto: sem tabela
    };

    /// <summary>
    /// Link PCIe com o teto da PRÓPRIA GPU corrigido: se o Windows informa uma geração máxima acima do que a arquitetura existe
    /// (ex.: uma GTX 1650, que é PCIe 3.0, aparecendo como 4.0), usa o teto da arquitetura e devolve a nota explicando.
    /// </summary>
    public static (PcieLinkState Link, string? Note) PcieWithGpuCeiling(GpuAdapter a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var s = a.Pcie;
        if (ArchitectureMaxPcieGen(a.Architecture) is { } archMax && s.MaxGen is { } reported && reported > archMax)
            return (s with { MaxGen = archMax },
                $"O Windows informou PCIe {Pcie.GenLabel(reported)} como máximo, mas a arquitetura {a.Architecture.Name} vai só até {Pcie.GenLabel(archMax)}; usei {Pcie.GenLabel(archMax)}.");
        return (s, null);
    }

    public static bool IsIntelArcDiscrete(GpuAdapter a) =>
        a is not null && a.Vendor == GpuVendor.Intel && !a.IsIntegrated && a.Architecture.Name is "Xe-HPG (Alchemist)" or "Xe2-HPG (Battlemage)";
}
