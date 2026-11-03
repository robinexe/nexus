using Nexus.GameProfiles.Cards;

namespace Nexus.Assistant;

/// <summary>Veredito de um ajuste para o PC lido.</summary>
public enum FitVerdict { Neutral, Recommended, NotRecommended }

/// <summary>
/// Motivo do veredito, em chave estável. A interface traduz cada um (Strings.resx: "Fit.*"); as bibliotecas nunca carregam texto
/// de tela. Cada motivo corresponde a uma regra com mecanismo e fonte em docs/modules/recommendation.md.
/// </summary>
public enum FitReason
{
    None,
    /// <summary>Notebook: mais calor e ruído; o processador reduz a frequência e o ganho some.</summary>
    LaptopHeat,
    /// <summary>Notebook na bateria: o Windows limita a potência; o ajuste só gasta bateria.</summary>
    OnBattery,
    /// <summary>Vídeo integrado: o ajuste depende de placa dedicada.</summary>
    IntegratedGpu,
    /// <summary>Vídeo integrado ou placa de entrada: o ajuste alivia a placa.</summary>
    WeakGpuRelief,
    /// <summary>Placa forte: o ajuste tira qualidade sem necessidade.</summary>
    StrongGpuNoNeed,
    /// <summary>Placa forte: o ajuste usa o que a placa oferece.</summary>
    StrongGpuUse,
    /// <summary>8 GB de RAM ou menos: o ajuste libera memória.</summary>
    LowRamRelief,
    /// <summary>HDD ou pouca memória: o SysMain ajuda justamente neste PC.</summary>
    SysMainHelpsHere,
    /// <summary>Disco rígido: menos escritas de metadados.</summary>
    HddFewerWrites,
    /// <summary>Só vale em placas NVIDIA.</summary>
    NotNvidia,
    /// <summary>Só vale em placas AMD.</summary>
    NotAmd,
    /// <summary>Só vale em placas Intel.</summary>
    NotIntel,
    /// <summary>Placa NVIDIA: o ajuste é do driver dela.</summary>
    NvidiaGpu,
    /// <summary>Placa AMD.</summary>
    AmdGpu,
    /// <summary>Placa Intel.</summary>
    IntelGpu,
    /// <summary>Preferência "menos atraso a imagem bonita".</summary>
    Competitive,
    /// <summary>Notebook: economia de energia do Wi-Fi na bateria faz o ping oscilar.</summary>
    LaptopWifi,
    /// <summary>Notebook: limitar o FPS reduz calor e ruído.</summary>
    LaptopCooler,
    /// <summary>Notebook ou placa fraca: gravação contínua em segundo plano ocupa o codificador de vídeo.</summary>
    BackgroundCapture,
}

/// <summary>Veredito + motivo. <see cref="Neutral"/> quando não há regra com mecanismo claro para este PC.</summary>
public sealed record Fit(FitVerdict Verdict, FitReason Reason)
{
    public static readonly Fit Neutral = new(FitVerdict.Neutral, FitReason.None);
    public static Fit Yes(FitReason r) => new(FitVerdict.Recommended, r);
    public static Fit No(FitReason r) => new(FitVerdict.NotRecommended, r);
    public bool IsRecommended => Verdict == FitVerdict.Recommended;
    public bool IsNotRecommended => Verdict == FitVerdict.NotRecommended;
}

/// <summary>
/// Regras de recomendação por hardware (puras, testadas): para cada ação do catálogo e para cada público de opção de jogo
/// (<see cref="GameCardAudience"/>), "recomendado / neutro / não recomendado para este PC". Só entram regras com mecanismo claro e
/// fonte (docs/modules/recommendation.md); sem base, a resposta é neutra. Sem leitura (null no perfil), nenhuma regra dispara.
/// </summary>
public static class HardwareRules
{
    /// <summary>Ações de energia "na tomada" que mantêm o processador/periféricos acordados: ganho pequeno, calor e consumo em notebook.</summary>
    private static readonly HashSet<string> AlwaysOnPowerActions = new(StringComparer.Ordinal)
    {
        "power.active-scheme.high-performance.session", "power.processor.min-cores-parking", "fps.power.throttling.off",
        "fps.power.min-processor-state.100.ac", "fps.power.epp.performance.ac", "fps.power.usb-selective-suspend.off.ac",
        "fps.power.pcie-aspm.off.ac", "hw.nvme.latency-tolerance.ac", "cpulab.power.max-state.ac", "cpulab.power.boost-mode.ac",
    };

    /// <summary>Ajustes que liberam RAM (navegador e compressão), com fonte do fabricante.</summary>
    private static readonly HashSet<string> MemoryReliefActions = new(StringComparer.Ordinal)
    {
        "memorylab.compression.enable", "memorylab.browser.edge-sleepingtabs-on", "memorylab.browser.edge-background-off",
        "memorylab.browser.chrome-background-off", "memorylab.browser.edge-startupboost-off",
    };

    /// <summary>Veredito para uma ação do catálogo (aceita "id" ou "id@versão").</summary>
    public static Fit ForAction(HardwareProfile profile, string actionIdOrKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(actionIdOrKey);
        var at = actionIdOrKey.IndexOf('@', StringComparison.Ordinal);
        var id = at < 0 ? actionIdOrKey : actionIdOrKey[..at];

        // ---- energia: notebook (na bateria pesa mais que o formato)
        if (AlwaysOnPowerActions.Contains(id))
        {
            if (profile.OnBattery == true) return Fit.No(FitReason.OnBattery);
            if (profile.IsLaptop == true) return Fit.No(FitReason.LaptopHeat);
            return Fit.Neutral; // desktop: sem medição que prove FPS a mais
        }
        if (id == "net.wifi.power-saving.dc-max-perf")
            return profile.IsLaptop == true ? Fit.Yes(FitReason.LaptopWifi) : Fit.Neutral;

        // ---- disco e memória
        if (id is "svc.sysmain.manual.lab" or "boot.service.sysmain.manual")
            return profile.Hdd || profile.LowRam ? Fit.No(FitReason.SysMainHelpsHere) : Fit.Neutral;
        if (id == "sys.ntfs.last-access.disable")
            return profile.Hdd ? Fit.Yes(FitReason.HddFewerWrites) : Fit.Neutral;
        if (MemoryReliefActions.Contains(id))
            return profile.LowRam ? Fit.Yes(FitReason.LowRamRelief) : Fit.Neutral;

        // ---- placa de vídeo
        if (id == "gpu.hags.lab")
            return profile.GpuClass == GpuClass.Integrated ? Fit.No(FitReason.IntegratedGpu) : Fit.Neutral;
        if (id == "sys.ui.transparency.off")
            return profile.GpuClass == GpuClass.Integrated ? Fit.Yes(FitReason.WeakGpuRelief) : profile.IsLaptop == true ? Fit.Yes(FitReason.LaptopHeat) : Fit.Neutral;
        if (id is "win.gamedvr.background-capture.disable" or "fps.gamedvr.policy.off")
            return profile.WeakGpu || profile.IsLaptop == true ? Fit.Yes(FitReason.BackgroundCapture) : Fit.Neutral;

        // ---- driver NVIDIA (DRS): só em placas NVIDIA
        if (id.StartsWith("gpu.nv.", StringComparison.Ordinal))
        {
            if (profile.GpuMaker is not GpuMaker.Unknown and not GpuMaker.Nvidia) return Fit.No(FitReason.NotNvidia);
            if (profile.GpuMaker == GpuMaker.Unknown) return Fit.Neutral;
            return id switch
            {
                "gpu.nv.power.max.per-app" => profile.OnBattery == true ? Fit.No(FitReason.OnBattery) : profile.IsLaptop == true ? Fit.No(FitReason.LaptopHeat)
                    : profile.Competitive ? Fit.Yes(FitReason.Competitive) : Fit.Neutral,
                "gpu.nv.lowlatency.on.per-app" => profile.Competitive ? Fit.Yes(FitReason.Competitive) : Fit.Neutral,
                _ => Fit.Neutral,
            };
        }

        // ---- Fortnite (GameUserSettings.ini)
        switch (id)
        {
            case "fortnite.render.performance-mode":
                return profile.WeakGpu ? Fit.Yes(FitReason.WeakGpuRelief) : profile.GpuClass == GpuClass.Strong ? Fit.No(FitReason.StrongGpuNoNeed) : Fit.Neutral;
            case "fortnite.render.dx12-full":
                return profile.GpuClass == GpuClass.Strong ? Fit.Yes(FitReason.StrongGpuUse) : profile.WeakGpu ? Fit.No(FitReason.WeakGpuRelief) : Fit.Neutral;
            case "fortnite.latency.reflex":
                if (profile.GpuMaker is not GpuMaker.Unknown and not GpuMaker.Nvidia) return Fit.No(FitReason.NotNvidia);
                return profile.GpuMaker == GpuMaker.Nvidia && profile.Competitive ? Fit.Yes(FitReason.Competitive) : Fit.Neutral;
            case "fortnite.visual.shadows.off":
                return profile.WeakGpu ? Fit.Yes(FitReason.WeakGpuRelief) : profile.Competitive ? Fit.Yes(FitReason.Competitive) : Fit.Neutral;
            case "fortnite.fps.limit":
                return profile.IsLaptop == true ? Fit.Yes(FitReason.LaptopCooler) : Fit.Neutral;
            case "fortnite.display.vsync.off" or "fortnite.visual.motion-blur.off":
                return profile.Competitive ? Fit.Yes(FitReason.Competitive) : Fit.Neutral;
        }
        return Fit.Neutral;
    }

    /// <summary>
    /// Veredito para o público de uma opção de jogo (<see cref="GameCardOption.Audience"/>). Fornecedor diferente do lido = não
    /// recomendado; placa fraca/forte contrária = não recomendado; o resto que casa = recomendado; sem leitura = neutro.
    /// </summary>
    public static Fit ForAudience(HardwareProfile profile, GameCardAudience audience)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (audience == GameCardAudience.Everyone) return Fit.Neutral;
        Fit? yes = null;
        Fit? no = null;
        void Consider(bool flag, Fit? fit) { if (!flag || fit is null) return; if (fit.IsNotRecommended) no ??= fit; else if (fit.IsRecommended) yes ??= fit; }

        Consider(audience.HasFlag(GameCardAudience.WeakGpu), profile.WeakGpu ? Fit.Yes(FitReason.WeakGpuRelief) : profile.GpuClass == GpuClass.Strong ? Fit.No(FitReason.StrongGpuNoNeed) : null);
        Consider(audience.HasFlag(GameCardAudience.StrongGpu), profile.GpuClass == GpuClass.Strong ? Fit.Yes(FitReason.StrongGpuUse) : profile.WeakGpu ? Fit.No(FitReason.WeakGpuRelief) : null);
        Consider(audience.HasFlag(GameCardAudience.LowRam), profile.LowRam ? Fit.Yes(FitReason.LowRamRelief) : null);
        Consider(audience.HasFlag(GameCardAudience.Laptop), profile.IsLaptop == true ? Fit.Yes(FitReason.LaptopCooler) : null);
        Consider(audience.HasFlag(GameCardAudience.Competitive), profile.Competitive ? Fit.Yes(FitReason.Competitive) : null);
        Consider(audience.HasFlag(GameCardAudience.Nvidia), Maker(profile, GpuMaker.Nvidia, FitReason.NvidiaGpu, FitReason.NotNvidia));
        Consider(audience.HasFlag(GameCardAudience.Amd), Maker(profile, GpuMaker.Amd, FitReason.AmdGpu, FitReason.NotAmd));
        Consider(audience.HasFlag(GameCardAudience.Intel), Maker(profile, GpuMaker.Intel, FitReason.IntelGpu, FitReason.NotIntel));
        return no ?? yes ?? Fit.Neutral;
    }

    private static Fit? Maker(HardwareProfile p, GpuMaker wanted, FitReason yes, FitReason no) =>
        p.GpuMaker == GpuMaker.Unknown ? null : p.GpuMaker == wanted ? Fit.Yes(yes) : Fit.No(no);
}
