namespace Nexus.Lab.Cpu;

/// <summary>Microarquitetura (núcleo) e codinome (o chip). Ex.: Zen 3 + "Cezanne"; Golden Cove/Gracemont + "Alder Lake-S".</summary>
public sealed record Microarchitecture(string Core, string Codename, int Year, string Note = "")
{
    public string Display => $"{Core} \"{Codename}\"";
}

/// <summary>
/// Tabela de família/modelo → microarquitetura, de ~2015 até hoje. Fontes: manuais de programação da Intel (SDM vol. 4, tabela
/// de assinaturas "DisplayFamily_DisplayModel") e os "Revision Guides"/PPRs da AMD por família (17h, 19h, 1Ah), conferidos com
/// a lista de modelos do kernel Linux (arch/x86/include/asm/intel-family.h). Modelos desconhecidos ficam sem nome (null): nunca
/// se chuta.
/// </summary>
public static class MicroarchitectureTable
{
    private sealed record Row(CpuVendor Vendor, int Family, int ModelFrom, int ModelTo, Func<int, string?, Microarchitecture> Make);

    private static Row I(int model, string core, string codename, int year, string note = "") =>
        new(CpuVendor.Intel, 6, model, model, (_, _) => new Microarchitecture(core, codename, year, note));

    private static Row A(int family, int from, int to, string core, string codename, int year, string note = "") =>
        new(CpuVendor.Amd, family, from, to, (_, _) => new Microarchitecture(core, codename, year, note));

    private static readonly Row[] Rows =
    [
        // ===== Intel (família 6) =====
        I(0x4E, "Skylake", "Skylake-U/Y", 2015),
        I(0x5E, "Skylake", "Skylake-S/H", 2015),
        I(0x55, "Skylake-SP", "Skylake-SP / Cascade Lake", 2017, "Servidor/HEDT; o stepping separa Skylake-SP (≤4) de Cascade Lake (≥5)."),
        new(CpuVendor.Intel, 6, 0x8E, 0x8E, (s, _) => s switch
        {
            9 => new("Skylake (Kaby Lake)", "Kaby Lake-U/Y / Amber Lake", 2016),
            10 => new("Skylake (Coffee Lake)", "Coffee Lake-U / Kaby Lake-R", 2017),
            11 => new("Skylake (Whiskey Lake)", "Whiskey Lake-U", 2018),
            _ => new("Skylake (Comet Lake)", "Comet Lake-U / Amber Lake", 2019),
        }),
        new(CpuVendor.Intel, 6, 0x9E, 0x9E, (s, _) => s switch
        {
            9 => new("Skylake (Kaby Lake)", "Kaby Lake-S/H", 2017),
            >= 13 => new("Skylake (Coffee Lake)", "Coffee Lake Refresh", 2018),
            _ => new("Skylake (Coffee Lake)", "Coffee Lake-S/H", 2017),
        }),
        I(0xA5, "Skylake (Comet Lake)", "Comet Lake-S/H", 2020),
        I(0xA6, "Skylake (Comet Lake)", "Comet Lake-U", 2020),
        I(0x66, "Palm Cove", "Cannon Lake", 2018),
        I(0x7D, "Sunny Cove", "Ice Lake", 2019),
        I(0x7E, "Sunny Cove", "Ice Lake-U/Y", 2019),
        I(0x6A, "Sunny Cove", "Ice Lake-SP", 2021),
        I(0x6C, "Sunny Cove", "Ice Lake-D", 2021),
        I(0x8C, "Willow Cove", "Tiger Lake-U", 2020),
        I(0x8D, "Willow Cove", "Tiger Lake-H", 2021),
        I(0xA7, "Cypress Cove", "Rocket Lake-S", 2021),
        I(0x97, "Golden Cove + Gracemont", "Alder Lake-S", 2021, "Híbrido: núcleos P (Golden Cove) e E (Gracemont)."),
        I(0x9A, "Golden Cove + Gracemont", "Alder Lake-P", 2022, "Híbrido: núcleos P (Golden Cove) e E (Gracemont)."),
        I(0xBE, "Gracemont", "Alder Lake-N", 2023, "Só núcleos E."),
        I(0xB7, "Raptor Cove + Gracemont", "Raptor Lake-S", 2022, "Híbrido. 13ª/14ª geração de desktop."),
        I(0xBF, "Golden Cove + Gracemont", "Raptor Lake-S (pastilha Alder Lake)", 2023, "Híbrido. Chips de entrada da 13ª/14ª geração."),
        I(0xBA, "Raptor Cove + Gracemont", "Raptor Lake-P/H", 2023, "Híbrido. 13ª geração móvel."),
        I(0xAA, "Redwood Cove + Crestmont", "Meteor Lake-H/U", 2023, "Híbrido com núcleos LP-E no SoC (Core Ultra série 1)."),
        I(0xAC, "Redwood Cove + Crestmont", "Meteor Lake-S", 2024),
        I(0xC6, "Lion Cove + Skymont", "Arrow Lake-S/HX", 2024, "Híbrido, sem Hyper-Threading (Core Ultra 200S)."),
        I(0xC5, "Lion Cove + Skymont", "Arrow Lake-H", 2025, "Híbrido, sem Hyper-Threading."),
        I(0xB5, "Redwood Cove + Crestmont", "Arrow Lake-U", 2025),
        I(0xBD, "Lion Cove + Skymont", "Lunar Lake", 2024, "Híbrido, sem Hyper-Threading, memória no encapsulamento."),
        I(0xCC, "Cougar Cove + Darkmont", "Panther Lake", 2025, "Híbrido."),
        I(0x8F, "Golden Cove", "Sapphire Rapids", 2023, "Servidor/workstation."),
        I(0xCF, "Raptor Cove", "Emerald Rapids", 2023, "Servidor."),
        I(0x5C, "Goldmont", "Apollo Lake", 2016),
        I(0x7A, "Goldmont Plus", "Gemini Lake", 2017),
        I(0x96, "Tremont", "Elkhart Lake", 2021),
        I(0x9C, "Tremont", "Jasper Lake", 2021),
        I(0x8A, "Sunny Cove + Tremont", "Lakefield", 2020),

        // ===== AMD família 17h (Zen, Zen+, Zen 2) =====
        A(0x17, 0x01, 0x01, "Zen", "Summit Ridge / Naples", 2017, "Ryzen 1000 de desktop, Threadripper 1000, EPYC 7001."),
        A(0x17, 0x08, 0x08, "Zen+", "Pinnacle Ridge / Colfax", 2018, "Ryzen 2000 de desktop."),
        A(0x17, 0x11, 0x11, "Zen", "Raven Ridge", 2018, "Ryzen 2000 com gráfico integrado."),
        A(0x17, 0x18, 0x18, "Zen+", "Picasso", 2019, "Ryzen 3000 móvel / com gráfico integrado."),
        A(0x17, 0x20, 0x20, "Zen", "Dali / Pollock", 2020),
        A(0x17, 0x31, 0x31, "Zen 2", "Rome / Castle Peak", 2019, "EPYC 7002, Threadripper 3000."),
        A(0x17, 0x60, 0x60, "Zen 2", "Renoir", 2020, "Ryzen 4000 móvel / 4000G."),
        A(0x17, 0x68, 0x68, "Zen 2", "Lucienne", 2021, "Ryzen 5x00U com Zen 2."),
        A(0x17, 0x71, 0x71, "Zen 2", "Matisse", 2019, "Ryzen 3000 de desktop."),
        A(0x17, 0x90, 0x91, "Zen 2", "Van Gogh", 2022, "Steam Deck."),
        A(0x17, 0xA0, 0xAF, "Zen 2", "Mendocino", 2022, "Ryzen 7020."),

        // ===== AMD família 19h (Zen 3, Zen 3+, Zen 4) =====
        A(0x19, 0x00, 0x0F, "Zen 3", "Milan / Chagall", 2021, "EPYC 7003, Threadripper 5000."),
        A(0x19, 0x10, 0x1F, "Zen 4", "Genoa / Storm Peak", 2022, "EPYC 9004, Threadripper 7000."),
        A(0x19, 0x20, 0x2F, "Zen 3", "Vermeer", 2020, "Ryzen 5000 de desktop (inclui 5800X3D)."),
        A(0x19, 0x40, 0x4F, "Zen 3+", "Rembrandt", 2022, "Ryzen 6000 móvel."),
        A(0x19, 0x50, 0x5F, "Zen 3", "Cezanne", 2021, "Ryzen 5000 móvel (H/U) e 5000G; Barceló é a revisão de 2022 do mesmo chip."),
        A(0x19, 0x60, 0x6F, "Zen 4", "Raphael", 2022, "Ryzen 7000 de desktop (inclui X3D)."),
        A(0x19, 0x70, 0x7F, "Zen 4", "Phoenix / Hawk Point", 2023, "Ryzen 7040/8040 móvel e 8000G."),
        A(0x19, 0xA0, 0xAF, "Zen 4c", "Bergamo / Siena", 2023, "Servidor."),

        // ===== AMD família 1Ah (Zen 5) =====
        A(0x1A, 0x00, 0x1F, "Zen 5", "Turin", 2024, "EPYC 9005."),
        A(0x1A, 0x20, 0x2F, "Zen 5", "Strix Point", 2024, "Ryzen AI 300 (núcleos Zen 5 e Zen 5c)."),
        A(0x1A, 0x40, 0x4F, "Zen 5", "Granite Ridge", 2024, "Ryzen 9000 de desktop (inclui X3D)."),
        A(0x1A, 0x60, 0x6F, "Zen 5", "Krackan Point", 2025, "Ryzen AI 300 de entrada."),
        A(0x1A, 0x70, 0x77, "Zen 5", "Strix Halo", 2025, "Ryzen AI Max."),
    ];

    /// <summary>Microarquitetura da assinatura, ou null se o modelo não está na tabela.</summary>
    public static Microarchitecture? Find(CpuVendor vendor, int family, int model, int stepping, string? brand = null)
    {
        foreach (var r in Rows)
            if (r.Vendor == vendor && r.Family == family && model >= r.ModelFrom && model <= r.ModelTo)
                return r.Make(stepping, brand);
        return null;
    }

    public static int Count => Rows.Length;
}
