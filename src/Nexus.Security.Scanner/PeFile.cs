using System.Buffers.Binary;
using System.Text;

namespace Nexus.Security.Scanner;

/// <summary>Seção de um PE. <see cref="Entropy"/> é NaN quando os bytes da seção não estavam no trecho lido.</summary>
public sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize, uint Characteristics, double Entropy)
{
    public const uint CodeFlag = 0x00000020;
    public const uint ExecuteFlag = 0x20000000;
    public const uint WriteFlag = 0x80000000;

    public bool IsExecutable => (Characteristics & (ExecuteFlag | CodeFlag)) != 0;
    public bool IsWritable => (Characteristics & WriteFlag) != 0;
}

public sealed record PeImport(string Dll, IReadOnlyList<string> Functions);

/// <summary>
/// Leitor de PE (formato documentado em https://learn.microsoft.com/windows/win32/debug/pe-format) próprio, defensivo e
/// somente leitura: toda leitura confere limites, nada é alocado com tamanho vindo do arquivo sem teto, e um trecho parcial
/// (arquivo maior que o limite de leitura) ainda rende cabeçalho e seções. Nunca executa nem carrega o arquivo.
/// </summary>
public sealed class PeFile
{
    public const int MaxSections = 96;
    public const int MaxImportDlls = 256;
    public const int MaxImportFunctions = 8192;
    private const int MaxFunctionsPerDll = 2048;
    private const int DebugTypeRepro = 16;

    public ushort Machine { get; private init; }
    public bool Is64 { get; private init; }
    public uint TimeDateStamp { get; private init; }
    public ushort Characteristics { get; private init; }
    public bool IsDll => (Characteristics & 0x2000) != 0;
    public ushort Subsystem { get; private init; }
    public uint EntryPoint { get; private init; }
    public uint SizeOfHeaders { get; private init; }
    public IReadOnlyList<PeSection> Sections { get; private init; } = [];
    public IReadOnlyList<PeImport> Imports { get; private set; } = [];
    /// <summary>Tabela de certificados (assinatura Authenticode embutida): deslocamento no ARQUIVO e tamanho.</summary>
    public uint SecurityOffset { get; private init; }
    public uint SecuritySize { get; private init; }
    public bool HasEmbeddedSignature => SecurityOffset != 0 && SecuritySize >= 8;
    /// <summary>Executável .NET (diretório CLR presente): importa só mscoree!_CorExeMain por natureza.</summary>
    public bool IsDotNet { get; private init; }
    /// <summary>Build reproduzível (entrada de depuração REPRO): o carimbo de data é um hash, não uma data.</summary>
    public bool HasReproDebugEntry { get; private set; }
    /// <summary>Fim dos dados das seções no arquivo (onde começa o "overlay").</summary>
    public long EndOfImage { get; private init; }
    /// <summary>Bytes depois das seções, sem contar a assinatura digital (que fica legitimamente no fim).</summary>
    public long OverlaySize { get; private init; }
    public long FileLength { get; private init; }

    public IEnumerable<string> ImportedFunctions => Imports.SelectMany(i => i.Functions);

    public PeSection? EntrySection => Sections.FirstOrDefault(s => EntryPoint >= s.VirtualAddress && EntryPoint < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));

    /// <summary>Lê o PE de <paramref name="data"/> (o arquivo inteiro ou o começo dele). null = não é PE válido.</summary>
    /// <param name="fileLength">Tamanho real do arquivo (para calcular o overlay quando só o começo foi lido).</param>
    public static PeFile? TryParse(ReadOnlySpan<byte> data, long fileLength)
    {
        try { return Parse(data, Math.Max(fileLength, data.Length)); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    public static bool LooksLikePe(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == (byte)'M' && data[1] == (byte)'Z';

    private static PeFile? Parse(ReadOnlySpan<byte> d, long fileLength)
    {
        if (d.Length < 0x40 || !LooksLikePe(d)) return null;
        var lfanew = U32(d, 0x3C);
        if (lfanew < 0x40 || lfanew > 0x10000000 || lfanew + 24 > (uint)d.Length) return null;
        var pe = (int)lfanew;
        if (U32(d, pe) != 0x00004550) return null; // "PE\0\0"
        var coff = pe + 4;
        var machine = U16(d, coff);
        var nSections = U16(d, coff + 2);
        var timestamp = U32(d, coff + 4);
        var optSize = U16(d, coff + 16);
        var characteristics = U16(d, coff + 18);
        var opt = coff + 20;
        if (opt + 2 > d.Length) return null;
        var magic = U16(d, opt);
        bool is64;
        if (magic == 0x10b) is64 = false;
        else if (magic == 0x20b) is64 = true;
        else return null;
        if (opt + optSize > d.Length || optSize < (is64 ? 112 : 96)) return null;
        var entry = U32(d, opt + 16);
        var sizeOfHeaders = U32(d, opt + 60);
        var subsystem = U16(d, opt + 68);
        var nDirs = U32(d, opt + (is64 ? 108 : 92));
        var dirs = opt + (is64 ? 112 : 96);
        var optEnd = opt + optSize;
        static (uint Rva, uint Size) DirOf(ReadOnlySpan<byte> d, int index, uint nDirs, int dirs, int optEnd) =>
            index < nDirs && dirs + (index * 8) + 8 <= optEnd ? (U32(d, dirs + (index * 8)), U32(d, dirs + (index * 8) + 4)) : (0u, 0u);

        // Seções
        if (nSections == 0 || nSections > MaxSections) return null;
        var table = opt + optSize;
        var sections = new List<PeSection>(nSections);
        long end = sizeOfHeaders;
        for (var i = 0; i < nSections; i++)
        {
            var s = table + (i * 40);
            if (s + 40 > d.Length) break;
            var name = Encoding.ASCII.GetString(d.Slice(s, 8)).TrimEnd('\0');
            var vsize = U32(d, s + 8);
            var va = U32(d, s + 12);
            var rawSize = U32(d, s + 16);
            var rawPtr = U32(d, s + 20);
            var flags = U32(d, s + 36);
            var entropy = double.NaN;
            if (rawSize > 0 && rawPtr < d.Length)
            {
                var available = (int)Math.Min(rawSize, (uint)d.Length - rawPtr);
                if (available == rawSize) entropy = ShannonEntropy.Of(d.Slice((int)rawPtr, available));
            }
            if (rawSize > 0) end = Math.Max(end, (long)rawPtr + rawSize);
            sections.Add(new PeSection(Sanitize(name), va, vsize, rawPtr, rawSize, flags, entropy));
        }
        if (sections.Count == 0) return null;

        var (secOff, secSize) = DirOf(d, 4, nDirs, dirs, optEnd);
        var (clrRva, clrSize) = DirOf(d, 14, nDirs, dirs, optEnd);
        var overlay = Math.Max(0, fileLength - end);
        if (secOff != 0 && secSize > 0 && secOff >= end && (long)secOff + secSize <= fileLength)
            overlay = Math.Max(0, overlay - secSize);

        var parsed = new PeFile
        {
            Machine = machine,
            Is64 = is64,
            TimeDateStamp = timestamp,
            Characteristics = characteristics,
            Subsystem = subsystem,
            EntryPoint = entry,
            SizeOfHeaders = sizeOfHeaders,
            Sections = sections,
            SecurityOffset = secOff,
            SecuritySize = secSize,
            IsDotNet = clrRva != 0 && clrSize != 0,
            EndOfImage = end,
            OverlaySize = overlay,
            FileLength = fileLength,
        };
        var (impRva, impSize) = DirOf(d, 1, nDirs, dirs, optEnd);
        var (dbgRva, dbgSize) = DirOf(d, 6, nDirs, dirs, optEnd);
        if (impRva != 0 && impSize != 0) parsed.Imports = parsed.ReadImports(d, impRva);
        parsed.HasReproDebugEntry = dbgRva != 0 && dbgSize != 0 && parsed.HasRepro(d, dbgRva, dbgSize);
        return parsed;
    }

    /// <summary>Converte RVA em deslocamento no arquivo. -1 = fora de qualquer seção/trecho lido.</summary>
    public long RvaToOffset(uint rva, int dataLength)
    {
        if (rva < SizeOfHeaders) return rva < dataLength ? rva : -1;
        foreach (var s in Sections)
        {
            var span = Math.Max(s.VirtualSize, s.RawSize);
            if (rva >= s.VirtualAddress && rva < (long)s.VirtualAddress + span)
            {
                var delta = rva - s.VirtualAddress;
                if (delta >= s.RawSize) return -1; // parte só virtual (zerada na memória), sem bytes no arquivo
                var off = (long)s.RawPointer + delta;
                return off < dataLength ? off : -1;
            }
        }
        return -1;
    }

    private List<PeImport> ReadImports(ReadOnlySpan<byte> d, uint rva)
    {
        var list = new List<PeImport>();
        var total = 0;
        var desc = RvaToOffset(rva, d.Length);
        if (desc < 0) return list;
        for (var i = 0; i < MaxImportDlls; i++)
        {
            var o = desc + (i * 20);
            if (o + 20 > d.Length) break;
            var oft = U32(d, (int)o);
            var nameRva = U32(d, (int)o + 12);
            var ft = U32(d, (int)o + 16);
            if (oft == 0 && nameRva == 0 && ft == 0) break;
            var nameOff = RvaToOffset(nameRva, d.Length);
            if (nameOff < 0) continue;
            var dll = Sanitize(CString(d, nameOff, 256)).ToLowerInvariant();
            var functions = new List<string>();
            var thunk = RvaToOffset(oft != 0 ? oft : ft, d.Length);
            var step = Is64 ? 8 : 4;
            for (var k = 0; thunk >= 0 && k < MaxFunctionsPerDll && total < MaxImportFunctions; k++)
            {
                var t = thunk + (k * step);
                if (t + step > d.Length) break;
                var value = Is64 ? BinaryPrimitives.ReadUInt64LittleEndian(d.Slice((int)t, 8)) : U32(d, (int)t);
                if (value == 0) break;
                var ordinal = Is64 ? (value & 0x8000000000000000UL) != 0 : (value & 0x80000000UL) != 0;
                if (ordinal) { functions.Add("#" + (value & 0xFFFF).ToString(System.Globalization.CultureInfo.InvariantCulture)); total++; continue; }
                var byName = RvaToOffset((uint)(value & 0x7FFFFFFF), d.Length);
                if (byName < 0 || byName + 2 >= d.Length) continue;
                var fn = Sanitize(CString(d, byName + 2, 256));
                if (fn.Length > 0) { functions.Add(fn); total++; }
            }
            list.Add(new PeImport(dll, functions));
            if (total >= MaxImportFunctions) break;
        }
        return list;
    }

    private bool HasRepro(ReadOnlySpan<byte> d, uint rva, uint size)
    {
        var off = RvaToOffset(rva, d.Length);
        if (off < 0) return false;
        var count = Math.Min(size / 28, 32);
        for (var i = 0; i < count; i++)
        {
            var e = off + (i * 28);
            if (e + 28 > d.Length) break;
            if (U32(d, (int)e + 12) == DebugTypeRepro) return true;
        }
        return false;
    }

    private static string CString(ReadOnlySpan<byte> d, long offset, int max)
    {
        if (offset < 0 || offset >= d.Length) return "";
        var slice = d.Slice((int)offset, (int)Math.Min(max, d.Length - offset));
        var zero = slice.IndexOf((byte)0);
        return Encoding.ASCII.GetString(zero >= 0 ? slice[..zero] : slice);
    }

    /// <summary>Só caracteres imprimíveis (nomes vêm do arquivo e vão para a interface).</summary>
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (c >= 0x20 && c < 0x7F) sb.Append(c);
        return sb.ToString();
    }

    private static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o, 2));
    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o, 4));
}

/// <summary>Entropia de Shannon em bits por byte (0 = constante, 8 = aleatório; compressão/criptografia ficam acima de ~7,2).</summary>
public static class ShannonEntropy
{
    public static double Of(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double h = 0, n = data.Length;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            var p = c / n;
            h -= p * Math.Log2(p);
        }
        return h;
    }
}
