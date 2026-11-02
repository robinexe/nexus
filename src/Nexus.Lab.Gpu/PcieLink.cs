using System.Globalization;

namespace Nexus.Lab.Gpu;

/// <summary>
/// Estado do link PCI Express. Fontes: propriedades DEVPKEY_PciDevice_* do Windows (pciprop.h), lidas do registrador
/// Link Status/Capabilities da própria placa, e do pai (porta raiz/ponte) para saber o que a plataforma oferece.
/// </summary>
/// <param name="CurrentGen">Geração negociada agora (1 = 2,5 GT/s … 5 = 32 GT/s). Null = não exposta (ex.: iGPU).</param>
/// <param name="MaxGen">Máximo suportado pela placa.</param>
/// <param name="CurrentWidth">Largura negociada (x1…x16).</param>
/// <param name="MaxWidth">Largura máxima da placa.</param>
/// <param name="PortMaxGen">Máximo da porta à qual ela está ligada (CPU/chipset); null se não legível.</param>
/// <param name="PortMaxWidth">Largura máxima da porta.</param>
public sealed record PcieLinkState(int? CurrentGen, int? MaxGen, int? CurrentWidth, int? MaxWidth, int? PortMaxGen = null, int? PortMaxWidth = null)
{
    public bool HasData => CurrentGen is not null || CurrentWidth is not null;

    /// <summary>O melhor que a combinação placa + porta pode negociar.</summary>
    public int? AttainableGen => MaxGen is { } g ? (PortMaxGen is { } p ? Math.Min(g, p) : g) : PortMaxGen;
    public int? AttainableWidth => MaxWidth is { } w ? (PortMaxWidth is { } p ? Math.Min(w, p) : w) : PortMaxWidth;

    public string Describe(bool current = true)
    {
        var (gen, width) = current ? (CurrentGen, CurrentWidth) : (MaxGen, MaxWidth);
        if (gen is null && width is null) return "não informado";
        return $"PCIe {(gen is { } g ? Pcie.GenLabel(g) : "?")} x{(width?.ToString(CultureInfo.InvariantCulture) ?? "?")}";
    }
}

public static class Pcie
{
    /// <summary>Valor de DEVPKEY_PciDevice_*LinkSpeed (1..6) → geração. 0 ou fora da faixa = desconhecido.</summary>
    public static int? GenFromSpeedCode(uint? code) => code is >= 1 and <= 6 ? (int)code : null;

    /// <summary>Largura válida (1, 2, 4, 8, 12, 16, 32); 0 = link desligado/não informado.</summary>
    public static int? Width(uint? w) => w is 1 or 2 or 4 or 8 or 12 or 16 or 32 ? (int)w : null;

    public static string GenLabel(int gen) => gen switch
    {
        1 => "1.0", 2 => "2.0", 3 => "3.0", 4 => "4.0", 5 => "5.0", 6 => "6.0",
        _ => gen.ToString(CultureInfo.InvariantCulture),
    };

    public static double TransferRateGts(int gen) => gen switch { 1 => 2.5, 2 => 5, 3 => 8, 4 => 16, 5 => 32, 6 => 64, _ => 0 };

    /// <summary>Banda útil por sentido (GB/s), já descontada a codificação (8b/10b até 2.0; 128b/130b do 3.0 ao 5.0; FLIT no 6.0).</summary>
    public static double BandwidthGBs(int gen, int width)
    {
        var perLane = gen switch { 1 => 0.25, 2 => 0.5, 3 => 0.985, 4 => 1.969, 5 => 3.938, 6 => 7.563, _ => 0 };
        return perLane * width;
    }

    /// <summary>
    /// Leitura honesta do link EM REPOUSO. A geração cai sozinha quando a GPU está ociosa (ASPM / gerência de energia
    /// do link): isso é normal e não indica defeito. Só a largura e a geração SOB CARGA dizem algo.
    /// </summary>
    /// <param name="laptop">Notebook: a largura do link é fiação da placa-mãe (decisão de fábrica), então largura menor vira
    /// informação, não alerta.</param>
    public static (string Level, string Text) IdleVerdict(PcieLinkState s, bool integrated, bool laptop = false)
    {
        if (integrated) return ("info", "Integrada ao processador: não usa um slot PCIe externo.");
        if (!s.HasData) return ("info", "O Windows não expôs o estado do link para esta placa.");
        var notes = new List<string>();
        var level = "good";
        if (s.CurrentWidth is { } cw && s.AttainableWidth is { } aw && cw < aw)
        {
            if (laptop)
            {
                level = "info";
                notes.Add($"Rodando em x{cw}; a GPU suporta x{aw}. Em notebook isso é quase sempre projeto do fabricante (muitos ligam a GPU em x8) e não muda com ajuste nenhum. " +
                          "O custo costuma ser de poucos %: os jogos raramente usam a banda inteira do barramento.");
            }
            else
            {
                level = "warn";
                notes.Add($"Rodando em x{cw}, mas placa e porta suportam x{aw}. Em desktop: placa no slot errado, encaixe ou sujeira no conector.");
            }
        }
        if (s.CurrentGen is { } cg && s.AttainableGen is { } ag && cg < ag)
            notes.Add($"Geração {GenLabel(cg)} agora (máx. {GenLabel(ag)}). Em repouso a GPU baixa a geração para economizar energia; meça com um jogo aberto.");
        if (s.MaxGen is { } mg && s.PortMaxGen is { } pg && pg < mg)
            notes.Add($"A placa suporta PCIe {GenLabel(mg)}, mas a porta do processador/chipset vai só até {GenLabel(pg)}: esse é o teto da plataforma.");
        if (notes.Count == 0) notes.Add($"Link no máximo possível: {s.Describe()} (~{BandwidthGBs(s.CurrentGen ?? 0, s.CurrentWidth ?? 0).ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"))} GB/s por sentido).");
        return (level, string.Join(" ", notes));
    }
}

/// <summary>Motivos de redução de clock informados pelo driver NVIDIA (nvmlClocksEventReasons / nvmlClocksThrottleReasons).</summary>
[Flags]
public enum ClockEventReasons : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationsClocksSetting = 0x2,
    SwPowerCap = 0x4,
    HwSlowdown = 0x8,
    SyncBoost = 0x10,
    SwThermalSlowdown = 0x20,
    HwThermalSlowdown = 0x40,
    HwPowerBrakeSlowdown = 0x80,
    DisplayClockSetting = 0x100,
}

/// <summary>Categoria de limitação usada na análise da gravação e nas faixas do gráfico.</summary>
public enum LimitCategory { None, Idle, Power, Thermal, Hardware, Other }

public static class ThrottleReasons
{
    private static readonly (ClockEventReasons Flag, string Text)[] Texts =
    [
        (ClockEventReasons.GpuIdle, "GPU ociosa (normal: sem trabalho, o clock baixa)"),
        (ClockEventReasons.ApplicationsClocksSetting, "clock fixado por configuração de aplicativo"),
        (ClockEventReasons.SwPowerCap, "limite de potência (o driver segura o clock para não passar dos watts permitidos)"),
        (ClockEventReasons.HwSlowdown, "redução por hardware (temperatura alta ou fonte/carregador sinalizando)"),
        (ClockEventReasons.SyncBoost, "sincronia de boost com outra GPU"),
        (ClockEventReasons.SwThermalSlowdown, "limite térmico do driver (GPU ou memória quente)"),
        (ClockEventReasons.HwThermalSlowdown, "proteção térmica por hardware (temperatura crítica)"),
        (ClockEventReasons.HwPowerBrakeSlowdown, "freio de potência externo (fonte/carregador pediu para reduzir)"),
        (ClockEventReasons.DisplayClockSetting, "clock limitado pela configuração de vídeo"),
    ];

    /// <summary>Cada bit ligado vira uma frase em português; bits desconhecidos aparecem em hexadecimal.</summary>
    public static IReadOnlyList<string> Describe(ulong mask)
    {
        var list = new List<string>();
        var known = 0UL;
        foreach (var (flag, text) in Texts)
        {
            known |= (ulong)flag;
            if ((mask & (ulong)flag) != 0) list.Add(text);
        }
        var unknown = mask & ~known;
        if (unknown != 0) list.Add($"outros motivos (0x{unknown.ToString("X", CultureInfo.InvariantCulture)})");
        return list;
    }

    /// <summary>Motivo dominante de uma amostra (térmico &gt; potência &gt; hardware &gt; outro &gt; ocioso).</summary>
    public static LimitCategory Categorize(ulong mask)
    {
        var m = (ClockEventReasons)mask;
        if ((m & (ClockEventReasons.SwThermalSlowdown | ClockEventReasons.HwThermalSlowdown)) != 0) return LimitCategory.Thermal;
        if ((m & (ClockEventReasons.SwPowerCap | ClockEventReasons.HwPowerBrakeSlowdown)) != 0) return LimitCategory.Power;
        if ((m & ClockEventReasons.HwSlowdown) != 0) return LimitCategory.Hardware;
        if ((m & (ClockEventReasons.ApplicationsClocksSetting | ClockEventReasons.SyncBoost | ClockEventReasons.DisplayClockSetting)) != 0) return LimitCategory.Other;
        if ((m & ClockEventReasons.GpuIdle) != 0) return LimitCategory.Idle;
        return LimitCategory.None;
    }

    public static string CategoryLabel(LimitCategory c) => c switch
    {
        LimitCategory.Power => "potência",
        LimitCategory.Thermal => "temperatura",
        LimitCategory.Hardware => "hardware",
        LimitCategory.Other => "configuração",
        LimitCategory.Idle => "ociosa",
        _ => "livre",
    };
}
