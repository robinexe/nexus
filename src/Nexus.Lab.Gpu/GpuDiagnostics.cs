using System.Globalization;

namespace Nexus.Lab.Gpu;

/// <summary>O que o botão "Corrigir" de um achado faz.</summary>
public enum GpuFixKind { None, DisplayMode, AppGpuPreference, GameMode, Hags, OpenLink }

/// <summary>Achado do diagnóstico: evidência medida, fonte, conselho e (quando houver) uma ação reversível.</summary>
public sealed record GpuFinding(
    string Id,
    string Level,
    string Title,
    string Evidence,
    string Source,
    string Advice,
    GpuFixKind Fix = GpuFixKind.None,
    string? FixLabel = null,
    IReadOnlyDictionary<string, string>? FixArguments = null);

/// <summary>Regras do diagnóstico da placa de vídeo (puras: testáveis com relatórios sintéticos).</summary>
public static class GpuDiagnostics
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Ações do catálogo usadas pelos achados.</summary>
    public const string DisplayModeAction = "gpulab.display.mode@1";
    public const string AppPreferenceAction = "gpulab.app.gpu-preference@1";
    public const string GameModeAction = "win.gamemode.enable@1";
    public const string HagsAction = "gpu.hags.lab@1";

    public static HybridInfo Hybrid(GpuLabReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var hw = r.Hardware.ToList();
        var igpu = hw.FirstOrDefault(a => a.IsIntegrated);
        var dgpu = hw.FirstOrDefault(a => !a.IsIntegrated);
        if (igpu is null || dgpu is null)
            return new(false, null, hw.Count <= 1 ? "Uma GPU só: tudo é desenhado por ela." : "Mais de uma GPU, sem par integrada + dedicada.");
        var panel = r.Monitors.FirstOrDefault(m => m.Internal);
        if (panel is null)
            return new(true, null, $"Híbrido: {igpu.Name} (integrada) + {dgpu.Name} (dedicada). Tela interna desligada ou não identificada.");
        if (panel.AdapterLuid == dgpu.Luid)
            return new(true, dgpu.Name, $"A tela interna está ligada direto na {dgpu.Name}: chave MUX em modo dedicado ou Advanced Optimus ativo. Sem cópia pela integrada.");
        if (panel.AdapterLuid == igpu.Luid)
            return new(true, igpu.Name, $"A tela interna é desenhada pela {igpu.Name} (Optimus/híbrido): nos jogos a {dgpu.Name} renderiza e o quadro é copiado pela integrada.");
        return new(true, null, $"Híbrido: {igpu.Name} + {dgpu.Name}.");
    }

    public static IReadOnlyList<GpuFinding> Evaluate(GpuLabReport r, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(r);
        var list = new List<GpuFinding>();
        var hw = r.Hardware.ToList();
        var primary = r.Primary;
        var hybrid = Hybrid(r);

        if (hw.Count == 0)
        {
            list.Add(new("gpu.none", "warn", "Nenhuma GPU de hardware encontrada",
                "O DXGI só listou o adaptador básico da Microsoft (sem driver do fabricante).", "IDXGIFactory1::EnumAdapters1",
                "Instale o driver da placa pelo site oficial do fabricante (NVIDIA, AMD ou Intel)."));
            return list;
        }

        // Driver
        foreach (var a in hw)
        {
            if (a.DriverDate is not { } date) continue;
            var days = DriverInfo.AgeDays(date, today);
            var (level, text) = DriverInfo.AgeVerdict(days, a.IsIntegrated);
            if (level == "good") continue;
            var v = a.DriverInfoDecoded;
            var url = DriverInfo.OfficialDownloadPage(a.Vendor);
            list.Add(new($"driver.age.{a.VendorId:x4}{a.DeviceId:x4}", level, $"Driver da {a.Name} com {days} dias",
                $"Versão {v.VendorVersion ?? v.WindowsVersion} de {date.ToString("dd/MM/yyyy", Pt)}.",
                "Gerenciador de dispositivos (DEVPKEY_Device_DriverDate)", text,
                url is null ? GpuFixKind.None : GpuFixKind.OpenLink,
                url is null ? null : "Abrir site oficial", url is null ? null : new Dictionary<string, string> { ["url"] = url }));
        }

        // PCIe (repouso)
        foreach (var a in hw.Where(a => !a.IsIntegrated && a.Pcie.HasData))
        {
            var (level, text) = Pcie.IdleVerdict(a.Pcie, false);
            if (level == "good") continue;
            list.Add(new($"pcie.width.{a.Luid}", level, $"{a.Name} em {a.Pcie.Describe()}",
                $"Agora: {a.Pcie.Describe()}. Placa: {a.Pcie.Describe(current: false)}." + (a.Pcie.PortMaxWidth is { } pw ? $" Porta: x{pw}." : ""),
                "DEVPKEY_PciDevice_CurrentLinkWidth/MaxLinkWidth", text));
        }

        // Monitores
        foreach (var m in r.Monitors)
        {
            if (m.Advice.BelowMaxRefresh && m.Advice.Best is { } best)
                list.Add(new($"display.refresh.{m.Device}", "warn", $"{Name(m)} a {m.Current.Hz} Hz, mas aceita {best.Hz} Hz",
                    $"Modo atual {m.Current.Label}; o monitor lista {best.Label} na mesma resolução ({m.ListedModes} modos listados).",
                    "EnumDisplaySettingsEx (modos que o monitor anuncia)",
                    $"Mais Hz deixa a imagem mais fluida e reduz o atraso. Muito comum depois de formatar ou trocar cabo. O NEXUS só troca a taxa, nunca a resolução, e Desfazer volta para {m.Current.Hz} Hz." +
                    (best.Hz >= 120 ? " Se a tela piscar ou ficar preta, use um cabo DisplayPort/HDMI 2.0 ou mais novo." : ""),
                    GpuFixKind.DisplayMode, $"Usar {best.Hz} Hz",
                    new Dictionary<string, string> { ["device"] = m.Device, ["mode"] = best.Key }));
            else if (m.Advice.BelowNativeResolution && m.Advice.Native is { } native)
                list.Add(new($"display.res.{m.Device}", "info", $"{Name(m)} abaixo da resolução máxima",
                    $"Atual {m.Current.Width}×{m.Current.Height}; o monitor lista até {native.Width}×{native.Height}.",
                    "EnumDisplaySettingsEx",
                    "Pode ser escolha sua (escala/desempenho) ou um modo de DSR/VSR. O NEXUS não muda resolução; ajuste em Configurações > Sistema > Tela se quiser."));
        }

        // Híbrido: preferência de GPU por jogo
        if (hybrid.IsHybrid)
        {
            var prefs = r.AppPreferences.ToDictionary(p => p.ExePath, p => p.GpuPreference, StringComparer.OrdinalIgnoreCase);
            foreach (var g in r.Games.Where(g => g.ExePath is not null).Take(40))
            {
                prefs.TryGetValue(g.ExePath!, out var pref);
                if (pref == 2) continue;
                list.Add(new($"app.pref.{g.ExePath}", pref == 1 ? "warn" : "info",
                    pref == 1 ? $"{g.Name} está na GPU integrada" : $"{g.Name} sem preferência de GPU",
                    $"{g.Store}: {g.ExePath} · preferência atual: {GameLibraries.PreferenceLabel(pref)}" + (g.ExePathSure ? "." : " (executável estimado pela pasta)."),
                    @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences",
                    "Em notebook híbrido o Windows às vezes escolhe a integrada. Marcar alto desempenho é o mesmo que Configurações > Sistema > Tela > Elementos gráficos, e Desfazer volta ao que era.",
                    GpuFixKind.AppGpuPreference, "Usar a dedicada",
                    new Dictionary<string, string> { ["exePath"] = g.ExePath! }));
            }
            if (hybrid.InternalPanelGpu is { } panelGpu && primary is not null && panelGpu != primary.Name)
                list.Add(new("hybrid.panel", "info", "Tela interna pela integrada",
                    hybrid.Description, "QueryDisplayConfig (adaptador de cada saída)",
                    "É o modo padrão (economiza bateria) e custa alguns % de FPS pela cópia. Se o software do fabricante tiver modo \"GPU dedicada\"/MUX, ele tira essa cópia (exige reinício). Um monitor externo na porta ligada à dedicada também evita a cópia."));
        }

        // HAGS
        if (primary is not null)
        {
            var effective = primary.HagsEnabled;
            var configured = r.Settings.HwSchMode;
            var on = effective ?? (configured == 2 ? true : configured == 1 ? false : null);
            var needsFg = primary.Vendor == GpuVendor.Nvidia && primary.Architecture.Name is "Ada Lovelace" or "Blackwell";
            if (on == false && needsFg)
                list.Add(new("hags.off.fg", "warn", "HAGS desligado numa RTX 40/50",
                    $"Agendamento acelerado por hardware: desligado ({(effective is null ? "configuração do registro" : "estado efetivo do driver")}).",
                    @"D3DKMTQueryAdapterInfo (WDDM 2.7) / HKLM\...\GraphicsDrivers\HwSchMode",
                    "A geração de quadros do DLSS 3/4 exige HAGS ligado. Ligar exige administrador e reinício; Desfazer volta ao valor anterior.",
                    GpuFixKind.Hags, "Ligar HAGS"));
            else if (on is not null)
                list.Add(new("hags.state", "info", $"HAGS {(on.Value ? "ligado" : "desligado")}",
                    effective is null ? $"HwSchMode = {configured?.ToString(CultureInfo.InvariantCulture) ?? "padrão"} no registro." : "Estado efetivo informado pelo driver (WDDM 2.7).",
                    @"D3DKMTQueryAdapterInfo / HKLM\...\GraphicsDrivers\HwSchMode",
                    "Os testes públicos mostram efeito pequeno e variável por jogo e driver. Não é uma otimização garantida; só é obrigatório para a geração de quadros da NVIDIA (RTX 40/50)."));
            if (primary.HagsSupported == false)
                list.Add(new("hags.unsupported", "info", "Esta GPU/driver não oferece HAGS", "WDDM 2.7 caps: HwSchSupported = 0.", "D3DKMTQueryAdapterInfo", "Nada a fazer."));
        }

        // MPO
        if (r.Settings.OverlayTestMode == 5)
            list.Add(new("mpo.off", "info", "MPO (planos de sobreposição) desligado",
                @"HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5.", "Registro do DWM",
                "Alguém desligou o MPO, em geral para corrigir tela piscando. Isso pode aumentar um pouco a latência em janela sem bordas; se a tela não pisca mais, pode remover o valor no futuro. O NEXUS não altera."));

        // Modo de Jogo
        if (r.Settings.AutoGameModeEnabled == 0)
            list.Add(new("gamemode.off", "info", "Modo de Jogo desligado", @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 0.", "Registro (Configurações > Jogos)",
                "O Modo de Jogo dá prioridade ao jogo em primeiro plano e segura atualizações durante a partida. É do Windows e reversível.",
                GpuFixKind.GameMode, "Ligar Modo de Jogo"));

        // Bateria
        if (r.Settings.OnBattery == true)
            list.Add(new("power.battery", "warn", "Notebook na bateria", "GetSystemPowerStatus: ACLineStatus = 0.", "Windows (energia)",
                "Na bateria a GPU dedicada recebe bem menos potência. Para jogar e para medir, ligue o carregador original."));

        // NVIDIA: limites
        if (primary?.Nvidia is { } nv)
        {
            if (nv.PowerLimitWatts is { } lim && nv.DefaultPowerLimitWatts is { } def && lim < def * 0.95)
                list.Add(new("nv.power.lowered", "warn", "Limite de potência abaixo do padrão",
                    $"Limite atual {lim:0} W; padrão da placa {def:0} W.", "NVML (nvmlDeviceGetPowerManagementLimit)",
                    "Algum programa (MSI Afterburner, modo silencioso do fabricante) baixou o limite. Volte ao padrão no programa que mudou."));
            if (nv.Bar1TotalBytes is { } bar1 && primary.Architecture.ResizableBarCapable)
            {
                var vram = nv.VramTotalBytes ?? primary.DedicatedVideoBytes;
                if (bar1 <= 300UL << 20)
                    list.Add(new("rebar.off", "info", "Resizable BAR desligado",
                        $"Janela BAR1 de {bar1 >> 20} MB (com ReBAR ela cobre a VRAM inteira, {vram >> 20} MB).", "NVML (nvmlDeviceGetBAR1MemoryInfo)",
                        r.IsLaptop
                            ? "Em notebook depende de BIOS do fabricante; muitos modelos não oferecem. Ganho típico 0–10% em jogos que a NVIDIA habilita."
                            : "Ative na BIOS: \"Above 4G Decoding\" e \"Re-Size BAR\" (exige boot UEFI/CSM desligado). Ganho típico 0–10% em jogos que a NVIDIA habilita."));
                else if (bar1 >= vram / 2)
                    list.Add(new("rebar.on", "good", "Resizable BAR ligado", $"Janela BAR1 de {bar1 >> 20} MB para {vram >> 20} MB de VRAM.", "NVML (nvmlDeviceGetBAR1MemoryInfo)", "Nada a fazer."));
            }
        }
        return list;
    }

    private static string Name(MonitorReport m) => string.IsNullOrWhiteSpace(m.FriendlyName) ? (m.Internal ? "Tela do notebook" : m.Device) : m.FriendlyName;

    /// <summary>Frase do topo: "NVIDIA GeForce RTX 3050 Laptop GPU · Ampere · 4 GB · driver 566.36 de 2024-12".</summary>
    public static string HeroLine(GpuAdapter a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var parts = new List<string> { a.Name };
        if (a.Architecture != GpuArchitecture.Unknown) parts.Add(a.Architecture.Name);
        var vram = a.Nvidia?.VramTotalBytes ?? a.DedicatedVideoBytes;
        if (vram > 0 && !a.IsIntegrated) parts.Add(FormatGb(vram));
        var v = a.DriverInfoDecoded;
        var ver = v.VendorVersion ?? (string.IsNullOrEmpty(v.WindowsVersion) ? null : v.WindowsVersion);
        if (ver is not null) parts.Add($"driver {ver}" + (a.DriverDate is { } d ? $" de {d.ToString("yyyy-MM", CultureInfo.InvariantCulture)}" : ""));
        return string.Join(" · ", parts);
    }

    public static string FormatGb(ulong bytes)
    {
        var gb = bytes / 1073741824.0;
        return (gb >= 10 || Math.Abs(gb - Math.Round(gb)) < 0.05 ? Math.Round(gb).ToString("0", Pt) : gb.ToString("0.#", Pt)) + " GB";
    }
}
