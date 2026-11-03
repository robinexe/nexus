using System.Globalization;

namespace Nexus.HardwareDiscovery;

/// <summary>
/// Explicação de um ponto que só o dono do PC resolve (BIOS, hardware, software do fabricante). O NEXUS só informa: nada
/// disso é ação do catálogo e nada é gravado.
/// </summary>
/// <param name="Explanation">O que é e o que fazer, em português simples.</param>
/// <param name="Gain">Ganho medido por terceiros, com a ressalva de que varia.</param>
public sealed record OwnerAdvice(string Explanation, string Gain, string SourceTitle, string SourceUrl);

/// <summary>Quem desenha a tela interna do notebook (saída do QueryDisplayConfig).</summary>
public enum DisplayOwner { Unknown, Integrated, Discrete }

/// <summary>GPU dedicada como os laboratórios a leram. Null em cada campo = não lido.</summary>
/// <param name="Vendor">"NVIDIA", "AMD", "Intel" ou outro.</param>
/// <param name="ReBarEnabled">true/false só quando a fonte prova (NVML BAR1, D3D12); null = não dá para afirmar.</param>
/// <param name="GpuMaxGen">Teto PCIe da PRÓPRIA GPU (já corrigido pela arquitetura).</param>
/// <param name="PortMaxWidth">Largura máxima da porta (slot/fiação) onde a GPU está ligada.</param>
public sealed record OwnerGpu(
    string Name,
    string Vendor,
    bool IsIntelArc = false,
    bool ReBarCapable = false,
    bool? ReBarEnabled = null,
    string? ReBarEvidence = null,
    int? PcieCurrentGen = null,
    int? PcieCurrentWidth = null,
    int? GpuMaxGen = null,
    int? GpuMaxWidth = null,
    int? PortMaxWidth = null);

/// <param name="ConfiguredMts">Velocidade em uso (Win32_PhysicalMemory.ConfiguredClockSpeed / SMBIOS tipo 17).</param>
/// <param name="RatedMts">Velocidade que o módulo anuncia ao firmware (Speed). Quase sempre o perfil JEDEC, não o XMP/EXPO.</param>
/// <param name="PartNumberMts">Velocidade decodificada do número de peça (o que está na caixa, normalmente o perfil XMP/EXPO).</param>
/// <param name="Profile">"XMP", "EXPO" ou "XMP/EXPO" quando o número de peça indica.</param>
public sealed record OwnerMemory(uint? ConfiguredMts, uint? RatedMts, uint? PartNumberMts = null, string? Profile = null, string? PartNumber = null);

/// <summary>Tudo o que as regras "só você pode fazer" usam. Montado pelos leitores (Assistente/interface) a partir dos laboratórios.</summary>
public sealed record OwnerCheckFacts(
    bool? IsLaptop,
    OwnerGpu? DiscreteGpu = null,
    bool HasIntegratedGpu = false,
    DisplayOwner PanelOwner = DisplayOwner.Unknown,
    OwnerMemory? Memory = null);

/// <summary>
/// Pontos que só o dono resolve, cada um com explicação curta, ganho medido e fonte. Puras e honestas: sem leitura que prove,
/// a regra não acusa nada (devolve nada ou diz "confira"), e nenhuma promete FPS.
/// </summary>
public static class OwnerChecks
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public const string ReBarId = "owner.rebar";
    public const string XmpId = "owner.xmp";
    public const string MuxId = "owner.mux";
    public const string PcieId = "owner.pcie";

    public static readonly (string Title, string Url) ArcSource =
        ("Tom's Hardware: Arc A770 perde até 24% sem Resizable BAR", "https://www.tomshardware.com/news/arc-a770-loses-25-percent-performance-without-resizable-bar");
    public static readonly (string Title, string Url) ReBarSource =
        ("TechSpot: Resizable BAR na NVIDIA (RTX 3080) vs. Smart Access Memory na AMD", "https://www.techspot.com/review/2234-nvidia-resizable-bar/");
    public static readonly (string Title, string Url) ExpoSource =
        ("Custom PC: testes de memória com AMD EXPO", "https://www.custompc.com/amd-expo-memory-benchmarks");
    public static readonly (string Title, string Url) MuxSource =
        ("ASUS ROG: desempenho com a chave MUX", "https://rog.asus.com/us/articles/rog-gaming-laptops/maximize-your-rog-laptops-performance-with-a-mux-switch/");
    public static readonly (string Title, string Url) MuxSkeptic =
        ("Laptop Mag: a chave MUX é superestimada", "https://www.laptopmag.com/features/the-mux-switch-is-overhyped-heres-why");
    public static readonly (string Title, string Url) PcieSource =
        ("TechPowerUp: escalonamento PCI-Express da RTX 4090", "https://www.techpowerup.com/review/nvidia-geforce-rtx-4090-pci-express-scaling/");

    /// <summary>As únicas páginas que a interface aceita abrir a partir destes achados e das checagens do PC (fontes fixas, nunca texto de fora).</summary>
    public static IReadOnlySet<string> SourceUrls { get; } = new HashSet<string>(
        new[] { ArcSource, ReBarSource, ExpoSource, MuxSource, MuxSkeptic, PcieSource }.Select(x => x.Url).Concat(PcChecks.SourceUrls), StringComparer.Ordinal);

    public static IReadOnlyList<DiagnosticFinding> Evaluate(OwnerCheckFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var list = new List<DiagnosticFinding>();
        void Add(DiagnosticFinding? x) { if (x is not null) list.Add(x); }
        Add(ResizableBar(f));
        Add(MemoryProfile(f));
        Add(Mux(f));
        Add(PcieLink(f));
        return list;
    }

    private static DiagnosticFinding Finding(string id, string title, DiagnosticSeverity sev, FactStatus basis, string evidence,
        string explanation, string gain, (string Title, string Url) source) =>
        new(id, title, sev, basis, evidence, null, new OwnerAdvice(explanation, gain, source.Title, source.Url));

    // ------------------------------------------------------------------ Resizable BAR / Smart Access Memory

    public static DiagnosticFinding? ResizableBar(OwnerCheckFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.DiscreteGpu is not { } g) return null;
        var laptop = f.IsLaptop == true;
        var bios = laptop
            ? "Em notebook a opção depende da BIOS do fabricante; muitos modelos não a oferecem."
            : "Na BIOS/UEFI ligue \"Above 4G Decoding\" e \"Re-Size BAR Support\" (na AMD pode aparecer como Smart Access Memory). Exige boot UEFI com CSM desligado.";
        if (g.IsIntelArc)
        {
            const string gain = "Sem ele a Arc A770 rendeu 76–80% do normal (perda de ~20–24%) nos testes da TechPowerUp.";
            return g.ReBarEnabled switch
            {
                true => Finding(ReBarId, "Resizable BAR ligado (Intel Arc)", DiagnosticSeverity.Ok, FactStatus.Detected, g.ReBarEvidence ?? "Leitura do driver.",
                    "A Intel Arc depende do Resizable BAR, e ele está ligado. Nada a fazer.", gain, ArcSource),
                false => Finding(ReBarId, "Resizable BAR desligado: a Intel Arc perde muito sem ele", DiagnosticSeverity.Attention, FactStatus.Detected,
                    g.ReBarEvidence ?? "Leitura do driver.", "A Intel exige Resizable BAR para as placas Arc. " + bios, gain, ArcSource),
                null => Finding(ReBarId, "Intel Arc: confira o Resizable BAR na BIOS", DiagnosticSeverity.Attention, FactStatus.Unavailable,
                    (g.ReBarEvidence ?? "O Windows não informa o estado.") + " O NEXUS não afirma se está ligado ou desligado.",
                    "A Intel exige Resizable BAR para as placas Arc. Entre na BIOS e confira. " + bios, gain, ArcSource),
            };
        }
        if (!g.ReBarCapable || g.ReBarEnabled is null) return null; // sem leitura, NVIDIA/AMD não justificam alarme
        var amd = g.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase);
        var name = amd ? "Smart Access Memory (Resizable BAR)" : "Resizable BAR";
        var gain2 = amd
            ? "Na AMD o ganho médio medido foi ~7% (TechSpot, 18 jogos), variando por jogo."
            : "Na NVIDIA o ganho médio medido foi de 0 a 2% (TechSpot, RTX 3080 em 20 jogos); o driver só liga em jogos da lista dele.";
        return g.ReBarEnabled == true
            ? Finding(ReBarId, $"{name} ligado", DiagnosticSeverity.Ok, FactStatus.Detected, g.ReBarEvidence ?? "Leitura do driver.", "Nada a fazer.", gain2, ReBarSource)
            : Finding(ReBarId, $"{name} desligado", DiagnosticSeverity.Info, FactStatus.Detected, g.ReBarEvidence ?? "Leitura do driver.",
                "Ganho pequeno, mas de graça se a placa-mãe oferece. " + bios, gain2, ReBarSource);
    }

    // ------------------------------------------------------------------ XMP / EXPO

    public static DiagnosticFinding? MemoryProfile(OwnerCheckFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.Memory is not { ConfiguredMts: { } conf } m || conf == 0) return null;
        var target = Math.Max(m.RatedMts ?? 0, m.PartNumberMts ?? 0);
        // "Claramente abaixo": 3% e pelo menos 200 MT/s, para não acusar arredondamento do firmware (ex.: 3192 vs 3200).
        if (target == 0 || conf >= target * 0.97 || target - conf < 200) return null;
        var fromPart = m.PartNumberMts is { } pm && pm >= target;
        var evidence = string.Create(Pt, $"Em uso: {conf} MT/s. ") +
                       (fromPart ? string.Create(Pt, $"O número de peça {m.PartNumber} indica {m.PartNumberMts} MT/s{(m.Profile is null ? "" : $" ({m.Profile})")}. ") : "") +
                       (m.RatedMts is { } r ? string.Create(Pt, $"O módulo anuncia {r} MT/s ao firmware.") : "");
        const string limit = "O Windows só vê o que o firmware informa (em geral o padrão JEDEC); o perfil XMP/EXPO aparece pelo número de peça, quando ele é conhecido.";
        const string gain = "Depende do jogo: aparece mais nos limitados pelo processador. No teste da Custom PC, a memória no perfil EXPO deu FPS maior que a mesma memória mais lenta.";
        var profile = m.Profile ?? "XMP/EXPO";
        if (f.IsLaptop == true)
            return Finding(XmpId, "Memória abaixo da velocidade do módulo (notebook)", DiagnosticSeverity.Info, FactStatus.Inferred, evidence,
                "Em notebook isso quase sempre é limite do processador ou da BIOS do fabricante, que raramente oferece XMP/EXPO. Não é defeito. " + limit,
                gain, ExpoSource);
        return Finding(XmpId, $"Perfil {profile} provavelmente desligado na BIOS", DiagnosticSeverity.Attention, FactStatus.Inferred, evidence,
            $"Na BIOS/UEFI, ligue o perfil {profile} (pode se chamar XMP, EXPO, D.O.C.P. ou A-XMP) e salve. É o perfil testado pelo fabricante do módulo. " +
            "Se o PC não ligar, ele volta ao padrão sozinho depois de algumas tentativas (ou limpe o CMOS). " + limit, gain, ExpoSource);
    }

    // ------------------------------------------------------------------ MUX / Advanced Optimus

    public static DiagnosticFinding? Mux(OwnerCheckFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.IsLaptop != true || !f.HasIntegratedGpu || f.DiscreteGpu is not { } g) return null;
        if (!(g.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Vendor.Equals("AMD", StringComparison.OrdinalIgnoreCase))) return null;
        const string gain = "A ASUS mediu +9% em média com a chave MUX no modo dedicado; a Laptop Mag mostra que o ganho varia muito por jogo e cai em resoluções altas.";
        return f.PanelOwner switch
        {
            DisplayOwner.Discrete => Finding(MuxId, "Tela do notebook ligada direto na GPU dedicada", DiagnosticSeverity.Ok, FactStatus.Detected,
                $"A tela interna é desenhada pela {g.Name} (modo dedicado da chave MUX ou Advanced Optimus).",
                "Sem a cópia de quadros pela integrada. Nada a fazer; para economizar bateria fora dos jogos, volte ao modo híbrido no software do fabricante.", gain, MuxSource),
            DisplayOwner.Integrated => Finding(MuxId, "Tela do notebook pela GPU integrada (modo híbrido)", DiagnosticSeverity.Info, FactStatus.Detected,
                $"A tela interna é desenhada pela integrada; nos jogos a {g.Name} renderiza e o quadro é copiado pela integrada.",
                "Se o notebook tiver chave MUX ou Advanced Optimus, escolha o modo \"GPU dedicada\"/\"Ultimate\" no software do fabricante (ex.: Armoury Crate, NitroSense, Vantage) e reinicie. " +
                "Sem MUX, um monitor externo na porta ligada à dedicada (em geral HDMI/DisplayPort) também evita a cópia. Custa bateria.", gain, MuxSource),
            _ => Finding(MuxId, "Notebook híbrido: veja se há chave MUX", DiagnosticSeverity.Info, FactStatus.Inferred,
                $"Notebook com GPU integrada e {g.Name}; o Windows não informou qual GPU desenha a tela interna.",
                "Se o software do fabricante tiver modo \"GPU dedicada\"/MUX/Advanced Optimus, ele tira a cópia de quadros pela integrada (exige reinício e custa bateria).",
                gain, MuxSkeptic),
        };
    }

    // ------------------------------------------------------------------ link PCIe

    public static DiagnosticFinding? PcieLink(OwnerCheckFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.DiscreteGpu is not { PcieCurrentWidth: { } cw, GpuMaxWidth: { } gw } g || cw >= gw) return null;
        var laptop = f.IsLaptop == true;
        var gen = g.PcieCurrentGen is { } cg ? $"PCIe {GenLabel(cg)} " : "PCIe ";
        var evidence = $"Agora: {gen}x{cw}. A própria GPU suporta x{gw}" + (g.GpuMaxGen is { } mg ? $" (PCIe {GenLabel(mg)})" : "") + "." +
                       (g.PortMaxWidth is { } pw ? $" A porta onde ela está ligada vai até x{pw}." : "");
        const string gain = "Metade das pistas raramente pesa: a TechPowerUp mediu cerca de 2% de diferença numa RTX 4090 em PCIe 4.0 x8 contra x16. Pesa mais quando a VRAM acaba e o jogo usa a RAM pelo barramento.";
        var wiredNarrow = g.PortMaxWidth is { } port && port <= cw;
        if (laptop)
            return Finding(PcieId, $"GPU ligada em x{cw} (projeto do notebook)", DiagnosticSeverity.Info, FactStatus.Detected, evidence,
                "Em notebook a largura do link é fiação da placa-mãe, decidida pelo fabricante (x8 é comum). Não é defeito e não muda com ajuste nenhum.", gain, PcieSource);
        if (wiredNarrow)
            return Finding(PcieId, $"Slot da placa de vídeo em x{cw}", DiagnosticSeverity.Info, FactStatus.Detected, evidence,
                "O slot onde a placa está só oferece essas pistas. Se a placa-mãe tiver outro slot x16 (em geral o mais perto do processador), mude a placa para ele com o PC desligado.",
                gain, PcieSource);
        return Finding(PcieId, $"Placa de vídeo em x{cw}, mas a placa e o slot suportam x{gw}", DiagnosticSeverity.Attention, FactStatus.Detected, evidence,
            "Em PC de mesa isso costuma ser encaixe ou sujeira no conector, um riser/cabo de extensão ruim, ou pistas divididas com um SSD M.2 (veja o manual da placa-mãe). " +
            "Desligue, tire da tomada e reencaixe a placa até travar.", gain, PcieSource);
    }

    private static string GenLabel(int gen) => gen.ToString(CultureInfo.InvariantCulture) + ".0";
}
