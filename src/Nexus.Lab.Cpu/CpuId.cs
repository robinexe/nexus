using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Nexus.Lab.Cpu;

/// <summary>Os quatro registradores devolvidos por uma instrução CPUID.</summary>
public readonly record struct CpuIdRegisters(uint Eax, uint Ebx, uint Ecx, uint Edx)
{
    public static CpuIdRegisters Zero => default;
}

/// <summary>Fonte de CPUID. A real usa a instrução do processador; os testes usam registros de amostra.</summary>
public interface ICpuIdSource
{
    /// <summary>False em processadores sem CPUID (ARM): o decodificador devolve uma identidade "indisponível".</summary>
    bool IsSupported { get; }
    CpuIdRegisters Query(uint leaf, uint subleaf = 0);
}

/// <summary>CPUID de verdade (System.Runtime.Intrinsics.X86.X86Base.CpuId). Só leitura; não exige administrador nem driver.</summary>
public sealed class HardwareCpuIdSource : ICpuIdSource
{
    public bool IsSupported => X86Base.IsSupported;

    public CpuIdRegisters Query(uint leaf, uint subleaf = 0)
    {
        if (!X86Base.IsSupported) return CpuIdRegisters.Zero;
        var (a, b, c, d) = X86Base.CpuId(unchecked((int)leaf), unchecked((int)subleaf));
        return new CpuIdRegisters(unchecked((uint)a), unchecked((uint)b), unchecked((uint)c), unchecked((uint)d));
    }
}

/// <summary>CPUID a partir de uma tabela (folha, subfolha) → registradores. Folhas ausentes devolvem zero, como no hardware.</summary>
public sealed class TableCpuIdSource : ICpuIdSource
{
    private readonly Dictionary<(uint Leaf, uint Sub), CpuIdRegisters> _table;

    public TableCpuIdSource(IReadOnlyDictionary<(uint Leaf, uint Sub), CpuIdRegisters> table, bool supported = true)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = new Dictionary<(uint, uint), CpuIdRegisters>(table);
        IsSupported = supported;
    }

    public bool IsSupported { get; }

    public CpuIdRegisters Query(uint leaf, uint subleaf = 0) =>
        _table.TryGetValue((leaf, subleaf), out var r) ? r : _table.TryGetValue((leaf, uint.MaxValue), out var any) ? any : CpuIdRegisters.Zero;
}

public enum CpuVendor { Unknown, Intel, Amd, Other }

public enum CacheKind { Data, Instruction, Unified }

/// <summary>Um nível de cache como o CPUID o descreve (folha 4 na Intel, 0x8000001D na AMD).</summary>
public sealed record CpuIdCache(int Level, CacheKind Kind, long SizeBytes, int Ways, int LineBytes, int SharedByLogical, bool FullyAssociative)
{
    public string Label => Level switch
    {
        1 => Kind == CacheKind.Instruction ? "L1i" : "L1d",
        _ => $"L{Level}",
    };
}

public sealed record CpuFeature(string Name, bool Present, string Meaning);

/// <summary>Identidade decodificada do processador. Campos null = não informado pelo CPUID.</summary>
public sealed record CpuIdentity
{
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    public CpuVendor Vendor { get; init; }
    public string VendorString { get; init; } = "";
    public string? Brand { get; init; }
    public int Family { get; init; }
    public int Model { get; init; }
    public int Stepping { get; init; }
    public int BaseFamily { get; init; }
    public int ExtendedFamily { get; init; }
    public int BaseModel { get; init; }
    public int ExtendedModel { get; init; }
    public uint Signature { get; init; }
    public Microarchitecture? Microarchitecture { get; init; }
    public IReadOnlyList<CpuFeature> Features { get; init; } = [];
    public IReadOnlyList<CpuIdCache> Caches { get; init; } = [];
    public bool Hybrid { get; init; }
    public bool HypervisorPresent { get; init; }
    public bool? Smt { get; init; }
    /// <summary>Frequências anunciadas na folha 0x16 (só Intel). 0 = não informado.</summary>
    public int BaseMhz { get; init; }
    public int MaxMhz { get; init; }
    public int BusMhz { get; init; }

    public bool Has(string feature) => Features.Any(f => f.Present && f.Name.Equals(feature, StringComparison.OrdinalIgnoreCase));

    public string FamilyText => string.Create(CultureInfo.InvariantCulture, $"família {Family:X}h · modelo {Model:X2}h · stepping {Stepping}");

    public CpuIdCache? Cache(int level) => Caches.FirstOrDefault(c => c.Level == level && c.Kind != CacheKind.Instruction);

    public static CpuIdentity Unavailable(string reason) => new() { Available = false, UnavailableReason = reason };
}

/// <summary>Decodifica os registradores do CPUID. Puro: os testes usam registros de amostra de processadores reais.</summary>
public static class CpuIdDecoder
{
    public static CpuIdentity Decode(ICpuIdSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsSupported)
            return CpuIdentity.Unavailable("Este processador não tem a instrução CPUID (ARM ou outra arquitetura). As medições continuam valendo; a identidade vem do Windows.");

        var l0 = source.Query(0);
        var maxLeaf = l0.Eax;
        var vendorString = Ascii(l0.Ebx, l0.Edx, l0.Ecx);
        var vendor = vendorString switch
        {
            "GenuineIntel" => CpuVendor.Intel,
            "AuthenticAMD" or "HygonGenuine" => CpuVendor.Amd,
            "" => CpuVendor.Unknown,
            _ => CpuVendor.Other,
        };

        var l1 = maxLeaf >= 1 ? source.Query(1) : CpuIdRegisters.Zero;
        var stepping = (int)(l1.Eax & 0xF);
        var baseModel = (int)((l1.Eax >> 4) & 0xF);
        var baseFamily = (int)((l1.Eax >> 8) & 0xF);
        var extModel = (int)((l1.Eax >> 16) & 0xF);
        var extFamily = (int)((l1.Eax >> 20) & 0xFF);
        var family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
        var model = baseFamily is 0x6 or 0xF ? (extModel << 4) | baseModel : baseModel;

        var maxExt = source.Query(0x80000000).Eax;
        string? brand = null;
        if (maxExt >= 0x80000004)
        {
            var sb = new StringBuilder();
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                var r = source.Query(leaf);
                sb.Append(Ascii(r.Eax, r.Ebx, r.Ecx, r.Edx));
            }
            brand = CleanBrand(sb.ToString());
        }

        var l7 = maxLeaf >= 7 ? source.Query(7, 0) : CpuIdRegisters.Zero;
        var l7s1 = maxLeaf >= 7 && l7.Eax >= 1 ? source.Query(7, 1) : CpuIdRegisters.Zero;
        var e1 = maxExt >= 0x80000001 ? source.Query(0x80000001) : CpuIdRegisters.Zero;

        bool B(uint reg, int bit) => (reg & (1u << bit)) != 0;
        var hypervisor = B(l1.Ecx, 31);
        var features = new List<CpuFeature>
        {
            new("SSE4.2", B(l1.Ecx, 20), "Instruções de texto e CRC; exigidas por quase todo jogo moderno."),
            new("AVX", B(l1.Ecx, 28), "Vetores de 256 bits (ponto flutuante)."),
            new("AVX2", B(l7.Ebx, 5), "Vetores inteiros de 256 bits; exigido por vários jogos e emuladores recentes."),
            new("FMA3", B(l1.Ecx, 12), "Multiplica e soma numa instrução só."),
            new("AVX-512", B(l7.Ebx, 16), "Vetores de 512 bits. Desligado de fábrica nos Intel híbridos (12ª geração em diante)."),
            new("AVX-VNNI", B(l7s1.Eax, 4), "Produto escalar de inteiros para IA, em 256 bits."),
            new("AVX-512 VNNI", B(l7.Ecx, 11), "Produto escalar de inteiros para IA, em 512 bits."),
            new("SHA", B(l7.Ebx, 29), "Aceleração de SHA-1/SHA-256 (criptografia, verificação de arquivos)."),
            new("AES-NI", B(l1.Ecx, 25), "Criptografia AES por hardware (BitLocker, HTTPS, VPN)."),
            new("BMI2", B(l7.Ebx, 8), "Manipulação de bits rápida."),
            new("POPCNT", B(l1.Ecx, 23), "Contagem de bits; exigida pelo Windows 11 24H2."),
            new("Virtualização (VT-x)", vendor == CpuVendor.Intel && B(l1.Ecx, 5), "Necessária para máquinas virtuais, WSL2 e a segurança baseada em virtualização."),
            new("Virtualização (AMD-V)", vendor == CpuVendor.Amd && B(e1.Ecx, 2), "Necessária para máquinas virtuais, WSL2 e a segurança baseada em virtualização."),
        };
        if (vendor != CpuVendor.Intel) features.RemoveAll(f => f.Name == "Virtualização (VT-x)");
        if (vendor != CpuVendor.Amd) features.RemoveAll(f => f.Name == "Virtualização (AMD-V)");

        var caches = DecodeCaches(source, vendor, maxLeaf, maxExt);

        bool? smt = null;
        if (vendor == CpuVendor.Amd && maxExt >= 0x8000001E)
        {
            var e1e = source.Query(0x8000001E);
            if (e1e != CpuIdRegisters.Zero) smt = ((e1e.Ebx >> 8) & 0xFF) + 1 > 1;
        }
        else if (vendor == CpuVendor.Intel && maxLeaf >= 0xB)
        {
            var lb = source.Query(0xB, 0);
            if (((lb.Ecx >> 8) & 0xFF) == 1) smt = (lb.Ebx & 0xFFFF) > 1;
        }
        features.Add(new("SMT", smt == true, vendor == CpuVendor.Intel
            ? "Hyper-Threading: duas threads por núcleo."
            : "Multithreading simultâneo: duas threads por núcleo."));

        int baseMhz = 0, maxMhz = 0, busMhz = 0;
        if (vendor == CpuVendor.Intel && maxLeaf >= 0x16)
        {
            var l16 = source.Query(0x16);
            baseMhz = (int)(l16.Eax & 0xFFFF);
            maxMhz = (int)(l16.Ebx & 0xFFFF);
            busMhz = (int)(l16.Ecx & 0xFFFF);
        }

        var signature = l1.Eax & 0x0FFF3FFF;
        return new CpuIdentity
        {
            Available = true,
            Vendor = vendor,
            VendorString = vendorString,
            Brand = brand,
            Family = family,
            Model = model,
            Stepping = stepping,
            BaseFamily = baseFamily,
            ExtendedFamily = extFamily,
            BaseModel = baseModel,
            ExtendedModel = extModel,
            Signature = signature,
            Microarchitecture = MicroarchitectureTable.Find(vendor, family, model, stepping, brand),
            Features = features,
            Caches = caches,
            Hybrid = vendor == CpuVendor.Intel && B(l7.Edx, 15),
            HypervisorPresent = hypervisor,
            Smt = smt,
            BaseMhz = baseMhz,
            MaxMhz = maxMhz,
            BusMhz = busMhz,
        };
    }

    private static List<CpuIdCache> DecodeCaches(ICpuIdSource source, CpuVendor vendor, uint maxLeaf, uint maxExt)
    {
        var list = new List<CpuIdCache>();
        uint leaf;
        if (vendor == CpuVendor.Intel && maxLeaf >= 4) leaf = 4;
        else if (vendor == CpuVendor.Amd && maxExt >= 0x8000001D) leaf = 0x8000001D;
        else return list;

        for (uint sub = 0; sub < 16; sub++)
        {
            var r = source.Query(leaf, sub);
            var type = r.Eax & 0x1F;
            if (type == 0) break;
            var level = (int)((r.Eax >> 5) & 0x7);
            var fully = (r.Eax & (1u << 9)) != 0;
            var sharing = (int)((r.Eax >> 14) & 0xFFF) + 1;
            var line = (int)(r.Ebx & 0xFFF) + 1;
            var partitions = (int)((r.Ebx >> 12) & 0x3FF) + 1;
            var ways = (int)((r.Ebx >> 22) & 0x3FF) + 1;
            var sets = (long)r.Ecx + 1;
            var size = ways * partitions * line * sets;
            var kind = type switch { 1 => CacheKind.Data, 2 => CacheKind.Instruction, _ => CacheKind.Unified };
            list.Add(new CpuIdCache(level, kind, size, ways, line, sharing, fully));
        }
        return list;
    }

    /// <summary>Junta registradores em texto ASCII (4 bytes cada, little-endian), parando no primeiro zero.</summary>
    public static string Ascii(params uint[] registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        var bytes = new byte[registers.Length * 4];
        for (var i = 0; i < registers.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), registers[i]);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static string CleanBrand(string s)
    {
        var cleaned = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return cleaned;
    }
}
