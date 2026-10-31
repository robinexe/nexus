using System.Buffers.Binary;
using System.Text;
using Nexus.Core;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Memory;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>Montador de tabelas SMBIOS binárias para os testes (formato RawSMBIOSData do GetSystemFirmwareTable).</summary>
internal static class SmbiosBuilder
{
    public static byte[] Table(params byte[][] structures)
    {
        var body = structures.SelectMany(s => s).Concat(End()).ToArray();
        var raw = new byte[8 + body.Length];
        raw[1] = 3; raw[2] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)body.Length);
        body.CopyTo(raw, 8);
        return raw;
    }

    private static byte[] End() => [127, 4, 0xFF, 0xFF, 0, 0];

    private static byte[] Build(byte type, ushort handle, byte[] formatted, string?[] strings)
    {
        formatted[0] = type;
        formatted[1] = (byte)formatted.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(formatted.AsSpan(2), handle);
        var tail = new List<byte>();
        foreach (var s in strings.Where(s => !string.IsNullOrEmpty(s))) { tail.AddRange(Encoding.ASCII.GetBytes(s!)); tail.Add(0); }
        if (tail.Count == 0) tail.Add(0);
        tail.Add(0);
        return formatted.Concat(tail).ToArray();
    }

    /// <summary>Índices de string 1-based só para os valores presentes (vazio = 0).</summary>
    private static byte[] Indices(string?[] strings)
    {
        var idx = new byte[strings.Length];
        byte n = 0;
        for (var i = 0; i < strings.Length; i++) idx[i] = string.IsNullOrEmpty(strings[i]) ? (byte)0 : ++n;
        return idx;
    }

    public static byte[] System(string maker, string product)
    {
        var f = new byte[0x1B];
        string?[] s = [maker, product];
        var i = Indices(s);
        f[0x04] = i[0]; f[0x05] = i[1];
        return Build(1, 0x0001, f, s);
    }

    public static byte[] Board(string maker, string product)
    {
        var f = new byte[0x0F];
        string?[] s = [maker, product];
        var i = Indices(s);
        f[0x04] = i[0]; f[0x05] = i[1];
        return Build(2, 0x0002, f, s);
    }

    public static byte[] Chassis(byte type)
    {
        var f = new byte[0x0D];
        f[0x05] = type;
        return Build(3, 0x0003, f, []);
    }

    public static byte[] Array16(ushort handle, ulong maxBytes, ushort devices, byte use = 0x03)
    {
        var f = new byte[0x17];
        f[0x04] = 0x03; f[0x05] = use; f[0x06] = 0x03;
        var kb = maxBytes / 1024;
        if (kb >= 0x80000000)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x07), 0x80000000);
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(0x0F), maxBytes);
        }
        else BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x07), (uint)kb);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x0B), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x0D), devices);
        return Build(16, handle, f, []);
    }

    public sealed record Dimm
    {
        public ushort Handle { get; init; } = 0x0011;
        public ushort Array { get; init; } = 0x0010;
        public ushort SizeWord { get; init; }
        public uint ExtendedSizeMb { get; init; }
        public byte FormFactor { get; init; } = 0x0D;
        public string? Locator { get; init; }
        public string? Bank { get; init; }
        public byte Type { get; init; } = 0x1A;
        public ushort TypeDetail { get; init; } = 0x4080; // síncrona + sem buffer
        public ushort Speed { get; init; }
        public uint ExtendedSpeed { get; init; }
        public string? Manufacturer { get; init; }
        public string? Serial { get; init; }
        public string? PartNumber { get; init; }
        public byte Rank { get; init; }
        public ushort Configured { get; init; }
        public uint ExtendedConfigured { get; init; }
        public ushort MinMv { get; init; }
        public ushort MaxMv { get; init; }
        public ushort ConfMv { get; init; }
        public ushort ModuleId { get; init; }
        public ushort DataWidth { get; init; } = 64;
    }

    public static byte[] Device(Dimm d)
    {
        var f = new byte[0x5C];
        string?[] s = [d.Locator, d.Bank, d.Manufacturer, d.Serial, d.PartNumber];
        var i = Indices(s);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x04), d.Array);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x06), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x08), d.DataWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x0A), d.DataWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x0C), d.SizeWord);
        f[0x0E] = d.FormFactor;
        f[0x10] = i[0]; f[0x11] = i[1];
        f[0x12] = d.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x13), d.TypeDetail);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x15), d.Speed);
        f[0x17] = i[2]; f[0x18] = i[3]; f[0x1A] = i[4];
        f[0x1B] = d.Rank;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x1C), d.ExtendedSizeMb);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x20), d.Configured);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x22), d.MinMv);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x24), d.MaxMv);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x26), d.ConfMv);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(0x2C), d.ModuleId);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x54), d.ExtendedSpeed);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x58), d.ExtendedConfigured);
        return Build(17, d.Handle, f, s);
    }

    /// <summary>O PC do responsável: Acer Nitro AN515-45, 1 módulo Samsung 8 GB DDR4-3200 SODIMM no canal A, canal B vazio.</summary>
    public static byte[] AcerNitro() => Table(
        System("Acer", "Nitro AN515-45"),
        Board("CZ", "Doc_CZ"),
        Chassis(0x0A),
        Array16(0x0010, 64UL << 30, 2),
        Device(new Dimm
        {
            Handle = 0x0011, SizeWord = 8192, Locator = "DIMM 0", Bank = "P0 CHANNEL A", Speed = 3200, Configured = 3200,
            Manufacturer = "Samsung", Serial = "12345678", PartNumber = "M471A1K43DB1-CWE   ", Rank = 1, MinMv = 1200, MaxMv = 1200, ConfMv = 1200,
        }),
        Device(new Dimm { Handle = 0x0012, SizeWord = 0, Locator = "DIMM 0", Bank = "P0 CHANNEL B", Type = 0x02, FormFactor = 0x02, Manufacturer = "Unknown", PartNumber = "Unknown" }));
}

[Trait("Category", "Unit")]
public sealed class MemoryLabSmbiosTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    [Fact]
    public void Acer_nitro_single_module_is_found_with_all_fields()
    {
        var inv = SmbiosMemory.Parse(SmbiosBuilder.AcerNitro());
        Assert.Equal(2, inv.Devices.Count);
        var m = Assert.Single(inv.Populated);
        Assert.Equal(8 * GiB, m.SizeBytes);
        Assert.Equal("DDR4", m.Type);
        Assert.Equal(MemoryFormFactor.SoDimm, m.FormFactor);
        Assert.Equal(3200u, m.SpeedMts);
        Assert.Equal(3200u, m.ConfiguredSpeedMts);
        Assert.Equal(1, m.Rank);
        Assert.Equal(64, m.DataWidthBits);
        Assert.Equal(1200, m.ConfiguredVoltageMv);
        Assert.Equal("M471A1K43DB1-CWE", m.PartNumber);
        Assert.Equal("DIMM 0", m.Locator);
        Assert.Equal("P0 CHANNEL A", m.BankLocator);
        Assert.True(m.Unbuffered);
        Assert.False(m.LooksSoldered);

        var empty = Assert.Single(inv.Empty);
        Assert.Equal(0UL, empty.SizeBytes);
        Assert.Null(empty.Manufacturer); // "Unknown" vira null
        Assert.Null(empty.PartNumber);
        Assert.Equal(64 * GiB, inv.MaxCapacityBytes);
        Assert.Equal(8 * GiB, inv.InstalledBytes);
    }

    [Fact]
    public void Extended_size_kb_size_unknown_and_extended_speed()
    {
        var raw = SmbiosBuilder.Table(
            SmbiosBuilder.Array16(0x10, 4UL << 40, 4), // 4 TB: usa a capacidade estendida
            SmbiosBuilder.Device(new SmbiosBuilder.Dimm { Handle = 0x20, SizeWord = 0x7FFF, ExtendedSizeMb = 65536, Type = 0x22, FormFactor = 0x09, Speed = 0xFFFF, ExtendedSpeed = 8000, Configured = 0xFFFF, ExtendedConfigured = 7200, Locator = "DIMM_A1" }),
            SmbiosBuilder.Device(new SmbiosBuilder.Dimm { Handle = 0x21, SizeWord = 0x8000 | 512, Locator = "DIMM_A2" }), // 512 KB
            SmbiosBuilder.Device(new SmbiosBuilder.Dimm { Handle = 0x22, SizeWord = 0xFFFF, Locator = "DIMM_B1" }));
        var inv = SmbiosMemory.Parse(raw);
        Assert.Equal(64 * GiB, inv.Devices[0].SizeBytes);
        Assert.Equal("DDR5", inv.Devices[0].Type);
        Assert.Equal(MemoryFormFactor.Dimm, inv.Devices[0].FormFactor);
        Assert.Equal(8000u, inv.Devices[0].SpeedMts);
        Assert.Equal(7200u, inv.Devices[0].ConfiguredSpeedMts);
        Assert.Equal(512UL * 1024, inv.Devices[1].SizeBytes);
        Assert.Null(inv.Devices[2].SizeBytes);
        Assert.True(inv.Devices[2].Populated); // desconhecido não é "vazio"
        Assert.Equal(4UL << 40, inv.MaxCapacityBytes);
    }

    [Fact]
    public void Video_memory_array_devices_are_excluded_and_soldered_is_detected()
    {
        var raw = SmbiosBuilder.Table(
            SmbiosBuilder.Array16(0x10, 16UL << 30, 2),
            SmbiosBuilder.Array16(0x30, 1UL << 30, 1, use: 0x04),
            SmbiosBuilder.Device(new SmbiosBuilder.Dimm { Handle = 0x11, Array = 0x10, SizeWord = 8192, FormFactor = 0x0B, Locator = "ChannelA-DIMM0", Bank = "BANK 0" }),
            SmbiosBuilder.Device(new SmbiosBuilder.Dimm { Handle = 0x12, Array = 0x10, SizeWord = 0, Locator = "ChannelB-DIMM0", Bank = "BANK 2" }),
            SmbiosBuilder.Device(new SmbiosBuilder.Dimm { Handle = 0x31, Array = 0x30, SizeWord = 1024, Locator = "VRAM" }));
        var inv = SmbiosMemory.Parse(raw);
        Assert.Equal(2, inv.Devices.Count);
        Assert.True(inv.Devices[0].LooksSoldered);
        Assert.Equal(MemoryFormFactor.Soldered, inv.Devices[0].FormFactor);
        Assert.DoesNotContain(inv.Devices, d => d.Locator == "VRAM");
        Assert.Equal(16UL << 30, inv.MaxCapacityBytes); // só memória do sistema
    }

    [Fact]
    public void Onboard_locator_marks_soldered()
    {
        var raw = SmbiosBuilder.Table(SmbiosBuilder.Device(new SmbiosBuilder.Dimm { SizeWord = 8192, FormFactor = 0x02, Locator = "Onboard Memory", Bank = "BANK 0" }));
        Assert.True(SmbiosMemory.Parse(raw).Devices[0].LooksSoldered);
    }

    [Fact]
    public void Truncated_or_short_structures_do_not_throw_or_invent()
    {
        var full = SmbiosBuilder.AcerNitro();
        var cut = full[..(full.Length / 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(cut.AsSpan(4), (uint)(cut.Length - 8));
        var inv = SmbiosMemory.Parse(cut);
        Assert.True(inv.Devices.Count <= 2);
    }

    [Theory]
    [InlineData("Unknown", null)]
    [InlineData("  Not Specified ", null)]
    [InlineData("0000", null)]
    [InlineData("FFFFFFFF", null)]
    [InlineData("To Be Filled By O.E.M.", null)]
    [InlineData(" Kingston ", "Kingston")]
    public void Clean_removes_firmware_placeholders(string input, string? expected) => Assert.Equal(expected, SmbiosMemory.Clean(input));

    [Theory]
    [InlineData((byte)0x0A, true)]
    [InlineData((byte)0x09, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x23, false)]
    [InlineData((byte)0x02, null)]
    public void Chassis_type_decides_portable(byte chassis, bool? expected)
    {
        var t = SmbiosParser.Parse(SmbiosBuilder.Table(SmbiosBuilder.Chassis(chassis)));
        Assert.Equal(expected, SmbiosMemory.IsPortable(t));
    }

    [Theory]
    [InlineData("80CE", (ushort)0, "Samsung")]
    [InlineData("80AD000080AD", (ushort)0, "SK hynix")]
    [InlineData("802C", (ushort)0, "Micron")]
    [InlineData("0198", (ushort)0, "Kingston")]
    [InlineData("029E", (ushort)0, "Corsair")]
    [InlineData("04CD", (ushort)0, "G.Skill")]
    [InlineData("859B", (ushort)0, "Crucial")]
    [InlineData("04CB", (ushort)0, "ADATA")]
    [InlineData("04EF", (ushort)0, "TeamGroup")]
    [InlineData("0443", (ushort)0, "Ramaxel")]
    [InlineData("Kingston", (ushort)0, "Kingston")]
    [InlineData("Hynix Semiconductor", (ushort)0, "SK hynix")]
    [InlineData("Unknown", (ushort)0x9B85, "Crucial")] // ID JEDEC do módulo (SMBIOS 3.2): byte baixo 0x85 (5 continuações), alto 0x9B
    [InlineData("Unknown", (ushort)0x9801, "Kingston")]
    public void Jedec_vendor_resolution(string manufacturer, ushort moduleId, string expected) =>
        Assert.Equal(expected, JedecVendors.Resolve(manufacturer, moduleId == 0 ? null : moduleId).Name);

    [Fact]
    public void Jedec_unknown_code_is_not_guessed()
    {
        Assert.Equal("ABCD", JedecVendors.Resolve("ABCD", null).Name); // texto devolvido como está, marcado como não reconhecido
        Assert.Contains("não reconhecido", JedecVendors.Resolve("ABCD", null).How, StringComparison.Ordinal);
        Assert.Null(JedecVendors.Resolve(null, null).Name);
        Assert.Null(JedecVendors.FromModuleId(0x1234));
    }
}

[Trait("Category", "Unit")]
public sealed class MemoryLabPartNumberTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    // número, fabricante, geração, MT/s, CL, GB por módulo, módulos no kit, SODIMM, perfil
    [Theory]
    [InlineData("KF432C16BB/8", "Kingston", "DDR4", 3200, 16, 8, 1, false, "XMP")]
    [InlineData("KF432C16BBK2/16", "Kingston", "DDR4", 3200, 16, 8, 2, false, "XMP")]
    [InlineData("KF560C36BBEK2-32", "Kingston", "DDR5", 6000, 36, 16, 2, false, "EXPO")]
    [InlineData("KF426S15IB/8", "Kingston", "DDR4", 2666, 15, 8, 1, true, "XMP")]
    [InlineData("KVR32S22S8/8", "Kingston", "DDR4", 3200, 22, 8, 1, true, "JEDEC")]
    [InlineData("KVR16N11/8", "Kingston", "DDR3", 1600, 11, 8, 1, false, "JEDEC")]
    [InlineData("KVR48U40BS8-16", "Kingston", "DDR5", 4800, 40, 16, 1, false, "JEDEC")]
    [InlineData("HX432C16FB3/8", "Kingston", "DDR4", 3200, 16, 8, 1, false, "XMP")]
    [InlineData("HX426S15IB2/8", "Kingston", "DDR4", 2666, 15, 8, 1, true, "XMP")]
    [InlineData("KHX3200C16D4/8GX", "Kingston", "DDR4", 3200, 16, 8, 1, false, "XMP")]
    [InlineData("KCP426SS8/8", "Kingston", "DDR4", 2666, 19, 8, 1, true, "JEDEC")]
    [InlineData("ACR26D4S9S8ME-8", "Kingston", "DDR4", 2666, 19, 8, 1, true, "JEDEC")]
    [InlineData("CMK16GX4M2B3200C16", "Corsair", "DDR4", 3200, 16, 8, 2, false, "XMP")]
    [InlineData("CMK32GX5M2B6000Z30", "Corsair", "DDR5", 6000, 30, 16, 2, false, "EXPO")]
    [InlineData("CMSX8GX4M1A2666C18", "Corsair", "DDR4", 2666, 18, 8, 1, true, "XMP")]
    [InlineData("F4-3600C16D-16GVKC", "G.Skill", "DDR4", 3600, 16, 8, 2, false, "XMP")]
    [InlineData("F4-3200C22S-8GRS", "G.Skill", "DDR4", 3200, 22, 8, 1, true, "XMP")]
    [InlineData("F5-6000J3038F16GX2-TZ5RK", "G.Skill", "DDR5", 6000, 30, 16, 2, false, "XMP")]
    [InlineData("F5-6000J3036F16GX2-TZ5NR", "G.Skill", "DDR5", 6000, 30, 16, 2, false, "EXPO")]
    [InlineData("CT8G4SFRA32A", "Crucial", "DDR4", 3200, 22, 8, 1, true, "JEDEC")]
    [InlineData("CT16G4DFD832A.C16FE", "Crucial", "DDR4", 3200, 22, 16, 1, false, "JEDEC")]
    [InlineData("CT16G48C40U5", "Crucial", "DDR5", 4800, 40, 16, 1, false, "JEDEC")]
    [InlineData("BL2K8G32C16U4B", "Crucial", "DDR4", 3200, 16, 8, 2, false, "XMP")]
    [InlineData("M471A1K43DB1-CWE", "Samsung", "DDR4", 3200, 22, 8, 1, true, "JEDEC")]
    [InlineData("M378A2K43EB1-CWE", "Samsung", "DDR4", 3200, 22, 16, 1, false, "JEDEC")]
    [InlineData("M425R1GB4BB0-CQK", "Samsung", "DDR5", 4800, 40, 8, 1, true, "JEDEC")]
    [InlineData("HMA81GS6DJR8N-XN", "SK hynix", "DDR4", 3200, 22, 8, 1, true, "JEDEC")]
    [InlineData("HMA82GU6CJR8N-VK", "SK hynix", "DDR4", 2666, 19, 16, 1, false, "JEDEC")]
    [InlineData("MTA8ATF1G64HZ-3G2R1", "Micron", "DDR4", 3200, 22, 8, 1, true, "JEDEC")]
    [InlineData("MTA16ATF2G64HZ-2G6E1", "Micron", "DDR4", 2666, 19, 16, 1, true, "JEDEC")]
    [InlineData("AD4S320038G22-SGN", "ADATA", "DDR4", 3200, 22, 8, 1, true, "JEDEC")]
    [InlineData("AX4U360038G18-SBG", "ADATA", "DDR4", 3600, 18, 8, 1, false, "XMP")]
    [InlineData("TF3D416G3200HC16CDC01", "TeamGroup", "DDR4", 3200, 16, 8, 2, false, "XMP")]
    [InlineData("TED48G3200C22-S01", "TeamGroup", "DDR4", 3200, 22, 8, 1, true, "JEDEC")]
    public void Decodes_known_part_numbers(string pn, string vendor, string gen, int speed, int cl, int moduleGb, int kit, bool sodimm, string profile)
    {
        var i = PartNumberDecoder.Decode(pn);
        Assert.True(i.Decoded, i.Rule);
        Assert.Equal(vendor, i.Vendor);
        Assert.Equal(gen, i.Generation);
        Assert.Equal(speed, i.SpeedMts);
        Assert.Equal(cl, i.CasLatency);
        Assert.Equal((ulong)moduleGb * GiB, i.ModuleBytes);
        Assert.Equal(kit, i.KitModules);
        Assert.Equal(sodimm, i.FormFactor == MemoryFormFactor.SoDimm);
        Assert.Equal(profile, i.Profile);
    }

    [Fact]
    public void Kingston_fury_beast_reads_like_the_box()
    {
        var i = PartNumberDecoder.Decode("KF432C16BB/8");
        Assert.Equal("Fury Beast", i.Family);
        Assert.Equal(DecodeConfidence.High, i.Confidence);
        Assert.False(i.CasLatencyInferred);
        Assert.Equal("Kingston Fury Beast · DDR4-3200 CL16 · 8 GB · XMP", i.Summary);
    }

    [Fact]
    public void Samsung_and_hynix_reveal_rank_organization()
    {
        Assert.Equal("1Rx8", PartNumberDecoder.Decode("M471A1K43DB1-CWE").Organization);
        Assert.Equal("1Rx8", PartNumberDecoder.Decode("HMA81GS6DJR8N-XN").Organization);
        Assert.Equal("2Rx8", PartNumberDecoder.Decode("HMA82GU6CJR8N-VK").Organization);
        Assert.Equal("1Rx16", PartNumberDecoder.Decode("HMAA1GS6CJR6N-XN").Organization);
        Assert.Equal("1Rx8", PartNumberDecoder.Decode("MTA8ATF1G64HZ-3G2R1").Organization);
        Assert.Equal("2Rx8", PartNumberDecoder.Decode("CT16G4DFD832A").Organization);
        Assert.True(PartNumberDecoder.Decode("M471A1K43DB1-CWE").CasLatencyInferred); // CL22 da tabela JEDEC, marcado
    }

    [Fact]
    public void Oem_and_partial_rules_have_lower_confidence()
    {
        Assert.Equal(DecodeConfidence.Medium, PartNumberDecoder.Decode("ACR26D4S9S8ME-8").Confidence);
        Assert.Contains("Acer", PartNumberDecoder.Decode("ACR26D4S9S8ME-8").Family, StringComparison.Ordinal);
        var ram = PartNumberDecoder.Decode("RMSA3270ME86H9F-2666");
        Assert.Equal("Ramaxel", ram.Vendor);
        Assert.Equal(2666, ram.SpeedMts);
        Assert.Null(ram.ModuleBytes); // tamanho não está no número: fica com o SMBIOS
        var hynix5 = PartNumberDecoder.Decode("HMCG66AGBSA095N");
        Assert.Equal(DecodeConfidence.Low, hynix5.Confidence);
        Assert.Equal("DDR5", hynix5.Generation);
        Assert.Null(hynix5.SpeedMts);
    }

    [Theory]
    [InlineData("ABCDEF123")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("XYZ-3200")]
    [InlineData("CMXYZ")]
    [InlineData("F4-ABC")]
    [InlineData("M471Z9999-ZZZ")]
    public void Unknown_part_numbers_are_never_invented(string? pn)
    {
        var i = PartNumberDecoder.Decode(pn);
        Assert.False(i.Decoded);
        Assert.Null(i.SpeedMts);
        Assert.Null(i.CasLatency);
        Assert.Null(i.ModuleBytes);
        Assert.Contains("desconhecido", i.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Kingston_internal_number_names_vendor_but_does_not_decode()
    {
        var i = PartNumberDecoder.Decode("9905702-017.A00G");
        Assert.False(i.Decoded);
        Assert.Equal("Kingston", i.Vendor);
        Assert.NotEmpty(i.Notes);
    }

    [Fact]
    public void Samsung_unknown_speed_code_keeps_size_but_not_speed()
    {
        var i = PartNumberDecoder.Decode("M471A1K43DB1-CZZ");
        Assert.Equal(DecodeConfidence.Medium, i.Confidence);
        Assert.Null(i.SpeedMts);
        Assert.Equal(8 * GiB, i.ModuleBytes);
    }
}

[Trait("Category", "Unit")]
public sealed class MemoryLabChannelTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    [Theory]
    [InlineData("DIMM 0", "P0 CHANNEL A", "A")]          // Acer / AMD (AGESA)
    [InlineData("DIMM 1", "P0 CHANNEL B", "B")]
    [InlineData("ChannelA-DIMM0", "BANK 0", "A")]        // Intel / Lenovo
    [InlineData("ChannelB-DIMM1", "BANK 3", "B")]
    [InlineData("DIMM_A1", "BANK 0", "A")]               // ASUS
    [InlineData("DIMMB2", null, "B")]                    // MSI
    [InlineData("DDR4_A1", null, "A")]                   // Gigabyte
    [InlineData("A2", "BANK 0", "A")]                    // ASRock
    [InlineData("DIMM A", "Not Specified", "A")]         // Dell
    [InlineData("DIMM B", null, "B")]
    [InlineData("Controller1-ChannelA-DIMM0", "BANK 0", "1A")] // LPDDR com dois controladores
    [InlineData("DIMM 1", "P1 CHANNEL B", "1B")]
    [InlineData("Bottom-Slot 1(left)", "BANK 0", null)]  // HP: sem canal no nome
    [InlineData("DIMM 0", "BANK 0", null)]
    [InlineData(null, null, null)]
    public void Channel_from_locator_and_bank(string? locator, string? bank, string? expected) =>
        Assert.Equal(expected, ChannelInference.ChannelOf(locator, bank));

    private static MemoryDevice D(string? loc, string? bank, ulong gb, string type = "DDR4", int width = 64, MemoryFormFactor ff = MemoryFormFactor.Dimm, bool soldered = false, string? pn = null, uint speed = 3200) =>
        new() { Locator = loc, BankLocator = bank, SizeBytes = gb * GiB, Type = gb == 0 ? null : type, DataWidthBits = width, FormFactor = ff, LooksSoldered = soldered, PartNumber = pn, SpeedMts = gb == 0 ? null : speed, ConfiguredSpeedMts = gb == 0 ? null : speed };

    [Fact]
    public void Acer_one_module_is_single_with_empty_channel_b()
    {
        var l = ChannelInference.Infer([D("DIMM 0", "P0 CHANNEL A", 8), D("DIMM 0", "P0 CHANNEL B", 0)]);
        Assert.Equal(ChannelMode.SingleChannel, l.Mode);
        Assert.Equal(["B"], l.EmptyChannels);
        Assert.Equal(64, l.BusWidthBits);
        Assert.True(l.FromNames);
    }

    [Fact]
    public void Asus_two_modules_in_a2_b2_is_dual()
    {
        var l = ChannelInference.Infer([D("DIMM_A1", "BANK 0", 0), D("DIMM_A2", "BANK 1", 16), D("DIMM_B1", "BANK 2", 0), D("DIMM_B2", "BANK 3", 16)]);
        Assert.Equal(ChannelMode.DualChannel, l.Mode);
        Assert.Equal(128, l.BusWidthBits);
        Assert.False(l.Asymmetric);
        Assert.Empty(l.EmptyChannels);
    }

    [Fact]
    public void Msi_two_modules_in_the_same_channel_is_single()
    {
        var l = ChannelInference.Infer([D("DIMMA1", null, 8), D("DIMMA2", null, 8), D("DIMMB1", null, 0), D("DIMMB2", null, 0)]);
        Assert.Equal(ChannelMode.SingleChannel, l.Mode);
        Assert.Equal(["B"], l.EmptyChannels);
    }

    [Fact]
    public void Lenovo_soldered_plus_free_slot()
    {
        var l = ChannelInference.Infer([D("ChannelA-DIMM0", "BANK 0", 8, ff: MemoryFormFactor.Soldered, soldered: true), D("ChannelB-DIMM0", "BANK 2", 0)]);
        Assert.Equal(ChannelMode.SingleChannel, l.Mode);
        Assert.Equal(["B"], l.EmptyChannels);
    }

    [Fact]
    public void Dell_letters_and_asymmetric_sizes()
    {
        var l = ChannelInference.Infer([D("DIMM A", null, 8), D("DIMM B", null, 16)]);
        Assert.Equal(ChannelMode.DualChannel, l.Mode);
        Assert.True(l.Asymmetric);
        Assert.Contains("assimétrico", l.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Hp_without_channel_names_is_an_estimate()
    {
        var one = ChannelInference.Infer([D("Bottom-Slot 1(left)", "BANK 0", 8), D("Bottom-Slot 2(right)", "BANK 2", 0)]);
        Assert.Equal(ChannelMode.SingleChannel, one.Mode);
        Assert.False(one.FromNames);
        var two = ChannelInference.Infer([D("Bottom-Slot 1(left)", "BANK 0", 8), D("Bottom-Slot 2(right)", "BANK 2", 8)]);
        Assert.Equal(ChannelMode.DualChannel, two.Mode);
        Assert.False(two.FromNames);
        Assert.Contains("medição", two.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Lpddr5_eight_16bit_channels_equals_dual()
    {
        var devs = new List<MemoryDevice>();
        foreach (var c in new[] { "0", "1" })
            foreach (var ch in "ABCD")
                devs.Add(D($"Controller{c}-Channel{ch}-DIMM0", "BANK 0", 2, "LPDDR5", 16, MemoryFormFactor.Soldered, true, speed: 6400));
        var l = ChannelInference.Infer(devs);
        Assert.Equal(128, l.BusWidthBits);
        Assert.Equal(ChannelMode.DualChannel, l.Mode);
        Assert.Equal(8, l.ChannelCount);
    }

    [Fact]
    public void No_modules_is_unknown()
    {
        var l = ChannelInference.Infer([D("DIMM 0", "P0 CHANNEL A", 0)]);
        Assert.Equal(ChannelMode.Unknown, l.Mode);
    }
}

[Trait("Category", "Unit")]
public sealed class MemoryLabTheoryTests
{
    [Fact]
    public void Theoretical_bandwidth()
    {
        Assert.Equal(25.6, BandwidthTheory.TheoreticalGbps(3200, 64), 3);
        Assert.Equal(51.2, BandwidthTheory.TheoreticalGbps(3200, 128), 3);
        Assert.Equal(76.8, BandwidthTheory.TheoreticalGbps(4800, 128), 3);
    }

    [Fact]
    public void Measured_22_on_single_channel_ddr4_3200_is_consistent_with_single()
    {
        var c = BandwidthTheory.Check(22.0, 3200, 64, 64);
        Assert.Equal(ChannelEvidence.ConsistentWithSingle, c.Evidence);
        Assert.Equal(86, c.EfficiencyPercent, 0);
        Assert.Contains("22,0", c.Text, StringComparison.Ordinal);
        Assert.Contains("25,6", c.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Measured_above_one_channel_proves_multiple()
    {
        var c = BandwidthTheory.Check(41.0, 3200, 64, 128);
        Assert.Equal(ChannelEvidence.ProvesMultiple, c.Evidence);
        Assert.Equal(80, c.EfficiencyPercent, 0);
    }

    [Fact]
    public void Very_low_measure_is_inconclusive_and_zero_is_not_measured()
    {
        Assert.Equal(ChannelEvidence.Inconclusive, BandwidthTheory.Check(8, 3200, 64, 64).Evidence);
        Assert.Equal(ChannelEvidence.NotMeasured, BandwidthTheory.Check(0, 3200, 64, 64).Evidence);
    }

    [Fact]
    public void Verdict_combines_slots_and_measure()
    {
        var single = ChannelInference.Infer([new MemoryDevice { Locator = "DIMM 0", BankLocator = "P0 CHANNEL A", SizeBytes = 8UL << 30 }]);
        Assert.Contains("confirmado", BandwidthTheory.Verdict(single, BandwidthTheory.Check(22, 3200, 64, 64)), StringComparison.Ordinal);
        Assert.Contains("passa do limite", BandwidthTheory.Verdict(single, BandwidthTheory.Check(40, 3200, 64, 64)), StringComparison.Ordinal);
        var dual = ChannelInference.Infer([
            new MemoryDevice { Locator = "DIMM_A2", SizeBytes = 8UL << 30 }, new MemoryDevice { Locator = "DIMM_B2", SizeBytes = 8UL << 30 }]);
        Assert.Contains("não passou", BandwidthTheory.Verdict(dual, BandwidthTheory.Check(22, 3200, 64, 128)), StringComparison.Ordinal);
        Assert.Contains("Meça", BandwidthTheory.Verdict(dual, BandwidthTheory.Check(0, 3200, 64, 128)), StringComparison.Ordinal);
    }

    [Fact]
    public void Acer_analysis_headline_and_theory()
    {
        var a = MemoryLabAnalyzer.Analyze(SmbiosBuilder.AcerNitro(), [
            new GpuInfo("AMD Radeon(TM) Graphics", "AMD", null, null, null, 512UL << 20),
            new GpuInfo("NVIDIA GeForce RTX 3050 Laptop GPU", "NVIDIA", null, null, null, 4UL << 30)]);
        Assert.Equal("8 GB DDR4-3200 · 1 módulo · SINGLE CHANNEL", a.Headline);
        Assert.Equal(25.6, a.TheoreticalGbps, 3);
        Assert.Equal(64, a.WidthPerChannelBits);
        Assert.True(a.Machine.Portable);
        Assert.True(a.Machine.HasIntegratedGpu);
        Assert.True(a.Machine.HasDiscreteGpu);
        Assert.Equal("Nitro AN515-45", a.Machine.Model);
        var part = Assert.Single(a.Parts);
        Assert.Equal("Samsung", part.Vendor);
        Assert.Equal(3200, part.SpeedMts);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon RX 6600M", false)]
    [InlineData("Intel(R) UHD Graphics 620", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", false)]
    [InlineData("Radeon Pro W6600", false)]
    public void Integrated_gpu_by_name(string name, bool expected) =>
        Assert.Equal(expected, MemoryLabAnalyzer.IsIntegrated(new GpuInfo(name, null, null, null, null, null)));
}

[Trait("Category", "Unit")]
public sealed class MemoryLabDiagnosisTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    private static MemoryLabFacts Facts(MemoryDevice[] devices, MachineInfo? machine = null, MemoryState? state = null, ChannelCheck? check = null, ulong? maxCapacity = 32 * GiB)
    {
        var inv = new MemoryInventory([new MemoryArray(0x10, "placa-mãe", "memória do sistema", "sem ECC", maxCapacity, devices.Length)], devices, "3.4");
        var layout = ChannelInference.Infer(devices);
        var parts = inv.Populated.Select(d => PartNumberDecoder.Decode(d.PartNumber)).ToList();
        return new MemoryLabFacts(inv, layout, parts, machine ?? Laptop, state, null, check);
    }

    private static readonly MachineInfo Laptop = new("Acer", "Nitro AN515-45", "CZ", true, true, true);
    private static readonly MachineInfo AsusDesktop = new("ASUS", "System Product Name", "ASUSTeK COMPUTER INC.", false, false, true);

    private static MemoryDevice M(string loc, string? bank, ulong gb, uint speed = 3200, uint? conf = null, string? pn = "M471A1K43DB1-CWE", MemoryFormFactor ff = MemoryFormFactor.SoDimm, bool soldered = false) =>
        new() { Locator = loc, BankLocator = bank, SizeBytes = gb * GiB, Type = gb == 0 ? null : "DDR4", SpeedMts = gb == 0 ? null : speed, ConfiguredSpeedMts = gb == 0 ? null : conf ?? speed, PartNumber = gb == 0 ? null : pn, FormFactor = ff, LooksSoldered = soldered, DataWidthBits = 64 };

    private static MemoryDevice[] Acer => [M("DIMM 0", "P0 CHANNEL A", 8), M("DIMM 0", "P0 CHANNEL B", 0)];

    [Fact]
    public void Acer_single_channel_recommends_matching_module_in_free_slot()
    {
        var check = BandwidthTheory.Check(21.5, 3200, 64, 64);
        var f = MemoryDiagnosis.Evaluate(Facts(Acer, check: check));
        var s = f.Single(x => x.Id == "single-channel");
        Assert.Equal("bad", s.Severity);
        Assert.Contains("8 GB DDR4-3200 SODIMM", s.Recommendation, StringComparison.Ordinal);
        Assert.Contains("M471A1K43DB1-CWE", s.Recommendation, StringComparison.Ordinal);
        Assert.Contains("~2×", s.Recommendation, StringComparison.Ordinal);
        Assert.Contains("canal B vazio", s.Evidence, StringComparison.Ordinal);
        Assert.Contains("21,5", s.Evidence, StringComparison.Ordinal); // a medição entra como evidência
        Assert.Contains(s.Steps, st => st.Contains("Notebook", StringComparison.Ordinal));
        Assert.NotEmpty(s.Sources);
        Assert.Contains(f, x => x.Id == "low-capacity");
        Assert.DoesNotContain(f, x => x.Id is "xmp-off" or "speed-limited" or "mixed-modules");
    }

    [Fact]
    public void Single_channel_finding_is_dropped_when_measure_proves_two_channels()
    {
        var f = MemoryDiagnosis.Evaluate(Facts(Acer, check: BandwidthTheory.Check(42, 3200, 64, 64)));
        Assert.DoesNotContain(f, x => x.Id == "single-channel");
    }

    [Fact]
    public void Soldered_memory_with_free_slot()
    {
        var devs = new[] { M("ChannelA-DIMM0", "BANK 0", 8, ff: MemoryFormFactor.Soldered, soldered: true), M("ChannelB-DIMM0", "BANK 2", 0) };
        var s = MemoryDiagnosis.Evaluate(Facts(devs)).Single(x => x.Id == "single-channel");
        Assert.Contains("soldada", s.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void No_free_slot_is_a_warning_with_manual_advice()
    {
        var s = MemoryDiagnosis.Evaluate(Facts([M("DIMM 0", "P0 CHANNEL A", 8)], maxCapacity: null)).Single(x => x.Id == "single-channel");
        Assert.Equal("warn", s.Severity);
        Assert.Contains("não lista slot livre", s.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_xmp_kit_running_at_jedec_speed_gets_asus_steps()
    {
        var devs = new[] { M("DIMM_A2", "BANK 1", 8, speed: 2133, conf: 2133, pn: "CMK16GX4M2B3200C16", ff: MemoryFormFactor.Dimm), M("DIMM_B2", "BANK 3", 8, speed: 2133, conf: 2133, pn: "CMK16GX4M2B3200C16", ff: MemoryFormFactor.Dimm) };
        var x = MemoryDiagnosis.Evaluate(Facts(devs, AsusDesktop)).Single(f => f.Id == "xmp-off");
        Assert.Contains("3200", x.Evidence, StringComparison.Ordinal);
        Assert.Contains("2133", x.Evidence, StringComparison.Ordinal);
        Assert.Contains(x.Steps, s => s.Contains("Ai Overclock Tuner", StringComparison.Ordinal));
        Assert.Contains("XMP", x.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Micro-Star International Co., Ltd.", "A-XMP")]
    [InlineData("Gigabyte Technology Co., Ltd.", "Tweaker")]
    [InlineData("ASRock", "OC Tweaker")]
    [InlineData("Contoso", "Memory Profile")]
    public void Xmp_steps_per_board_maker(string maker, string expected)
    {
        var devs = new[] { M("DIMM_A2", null, 8, speed: 3200, conf: 2400, pn: "F4-3200C16D-16GVKB", ff: MemoryFormFactor.Dimm), M("DIMM_B2", null, 8, speed: 3200, conf: 2400, pn: "F4-3200C16D-16GVKB", ff: MemoryFormFactor.Dimm) };
        var x = MemoryDiagnosis.Evaluate(Facts(devs, new MachineInfo(maker, "x", maker, false, false, true))).Single(f => f.Id == "xmp-off");
        Assert.Contains(x.Steps, s => s.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Laptop_slower_than_module_is_platform_limit_not_xmp()
    {
        var f = MemoryDiagnosis.Evaluate(Facts([M("DIMM 0", "P0 CHANNEL A", 8, speed: 3200, conf: 2666), M("DIMM 0", "P0 CHANNEL B", 0)]));
        Assert.DoesNotContain(f, x => x.Id == "xmp-off");
        var s = f.Single(x => x.Id == "speed-limited");
        Assert.Contains("notebook", s.Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mixed_modules_and_split_kit()
    {
        var devs = new[] { M("DIMM_A2", null, 8, pn: "CMK16GX4M2B3200C16", ff: MemoryFormFactor.Dimm), M("DIMM_B2", null, 16, speed: 2666, pn: "KVR26N19D8/16", ff: MemoryFormFactor.Dimm) };
        var f = MemoryDiagnosis.Evaluate(Facts(devs, AsusDesktop));
        var mixed = f.Single(x => x.Id == "mixed-modules");
        Assert.Equal("warn", mixed.Severity);
        Assert.Contains("tamanhos diferentes", mixed.Evidence, StringComparison.Ordinal);
        Assert.Contains("velocidades diferentes", mixed.Evidence, StringComparison.Ordinal);
        Assert.Contains(f, x => x.Id == "kit-split");
    }

    [Fact]
    public void Dual_channel_confirmed_is_good()
    {
        var devs = new[] { M("DIMM_A2", null, 8, ff: MemoryFormFactor.Dimm), M("DIMM_B2", null, 8, ff: MemoryFormFactor.Dimm) };
        var f = MemoryDiagnosis.Evaluate(Facts(devs, AsusDesktop, check: BandwidthTheory.Check(41, 3200, 64, 128)));
        Assert.Equal("good", f.Single(x => x.Id == "dual-channel").Severity);
        Assert.DoesNotContain(f, x => x.Id == "single-channel");
    }

    [Fact]
    public void Pagefile_off_with_little_ram_offers_the_reversible_action()
    {
        var state = new MemoryState { TotalPhysicalBytes = 8 * GiB, CommitLimitBytes = 8 * GiB, PageFile = PageFileConfig.Parse([]) };
        var p = MemoryDiagnosis.Evaluate(Facts(Acer, state: state)).Single(x => x.Id == "pagefile-off");
        Assert.Equal("bad", p.Severity);
        Assert.Equal(MemoryDiagnosis.PageFileActionId, p.ActionId);
        Assert.Contains(p.Sources, s => s.Url.StartsWith("https://learn.microsoft.com", StringComparison.Ordinal));
    }

    [Fact]
    public void Small_fixed_pagefile_and_automatic_is_fine()
    {
        var small = new MemoryState { TotalPhysicalBytes = 8 * GiB, PageFile = PageFileConfig.Parse([@"C:\pagefile.sys 512 1024"]) };
        Assert.Contains(MemoryDiagnosis.Evaluate(Facts(Acer, state: small)), x => x.Id == "pagefile-small");
        var auto = new MemoryState { TotalPhysicalBytes = 8 * GiB, PageFile = PageFileConfig.Parse([@"?:\pagefile.sys"]) };
        Assert.DoesNotContain(MemoryDiagnosis.Evaluate(Facts(Acer, state: auto)), x => x.Id.StartsWith("pagefile", StringComparison.Ordinal));
    }

    [Fact]
    public void Commit_peak_and_hard_faults_are_evidence()
    {
        var state = new MemoryState { TotalPhysicalBytes = 8 * GiB, CommitPeakBytes = 11 * GiB, PagesInputPerSec = 320, PageFile = PageFileConfig.Parse([@"?:\pagefile.sys"]) };
        var low = MemoryDiagnosis.Evaluate(Facts(Acer, state: state)).Single(x => x.Id == "low-capacity");
        Assert.Equal("bad", low.Severity);
        Assert.Contains("11 GB", low.Evidence, StringComparison.Ordinal);
        Assert.Contains("320", low.Evidence, StringComparison.Ordinal);

        var big = new[] { M("DIMM_A2", null, 8, ff: MemoryFormFactor.Dimm), M("DIMM_B2", null, 8, ff: MemoryFormFactor.Dimm) };
        var s16 = new MemoryState { TotalPhysicalBytes = 16 * GiB, CommitPeakBytes = 15 * GiB, PageFile = PageFileConfig.Parse([@"?:\pagefile.sys"]) };
        var f16 = MemoryDiagnosis.Evaluate(Facts(big, AsusDesktop, s16));
        Assert.Contains(f16, x => x.Id == "commit-pressure");
        Assert.DoesNotContain(f16, x => x.Id == "low-capacity");
    }

    [Fact]
    public void Compression_off_is_informative_only()
    {
        var state = new MemoryState { CompressionEnabled = false, CompressionSource = "Get-MMAgent", PageFile = PageFileConfig.Parse([@"?:\pagefile.sys"]) };
        var c = MemoryDiagnosis.Evaluate(Facts(Acer, state: state)).Single(x => x.Id == "compression-off");
        Assert.Equal("info", c.Severity);
        Assert.Null(c.ActionId);
    }

    [Fact]
    public void Gaming_impact_is_a_range_with_sources_and_igpu_note()
    {
        var single = ChannelInference.Infer(Acer);
        var g = MemoryDiagnosis.Gaming(single, Laptop);
        Assert.Contains("%", g.Headline, StringComparison.Ordinal);
        Assert.Contains("GPU integrada", g.Detail, StringComparison.Ordinal);
        Assert.True(g.Sources.Count >= 3);
        Assert.All(g.Sources, s => Assert.StartsWith("https://", s.Url, StringComparison.Ordinal));
        var dual = ChannelInference.Infer([M("DIMM_A2", null, 8), M("DIMM_B2", null, 8)]);
        Assert.Empty(MemoryDiagnosis.Gaming(dual, Laptop).Sources);
    }
}

[Trait("Category", "Unit")]
public sealed class MemoryLabWindowsStateTests
{
    [Fact]
    public void Pagefile_config_parsing()
    {
        Assert.Equal(PageFileMode.Unknown, PageFileConfig.Parse(null).Mode);
        Assert.Equal(PageFileMode.None, PageFileConfig.Parse([]).Mode);
        Assert.Equal(PageFileMode.None, PageFileConfig.Parse(["", "  "]).Mode);
        Assert.Equal(PageFileMode.AutomaticAllDrives, PageFileConfig.Parse([@"?:\pagefile.sys"]).Mode);
        Assert.Equal(PageFileMode.SystemManaged, PageFileConfig.Parse([@"C:\pagefile.sys 0 0"]).Mode);
        Assert.Equal(PageFileMode.SystemManaged, PageFileConfig.Parse([@"C:\pagefile.sys"]).Mode);
        var custom = PageFileConfig.Parse([@"C:\pagefile.sys 1024 4096", @"D:\pagefile.sys 0 0"]);
        Assert.Equal(PageFileMode.Custom, custom.Mode);
        Assert.Equal(1024, custom.Entries[0].InitialMb);
        Assert.Equal(4096, custom.Entries[0].MaximumMb);
        Assert.Contains("1.024–4.096 MB", custom.Describe(), StringComparison.Ordinal);
        Assert.Contains("Automático", PageFileConfig.Parse([@"?:\pagefile.sys"]).Describe(), StringComparison.Ordinal);
    }
}

[Trait("Category", "Unit")]
public sealed class MemoryLabHandlerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-mem-").FullName;
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static ActionInstance Instance()
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == MemoryDiagnosis.PageFileActionId);
        var inst = entry.TryInstantiate(new Dictionary<string, string>(), out var reason);
        Assert.True(inst is not null, reason);
        return inst!;
    }

    [Fact]
    public void Catalog_loads_and_every_kind_is_registered()
    {
        var mine = Catalog.Actions.Where(a => a.Definition.Id.StartsWith("memorylab.", StringComparison.Ordinal)).ToList();
        Assert.Contains(mine, a => a.Definition.Id == MemoryDiagnosis.PageFileActionId);
        Assert.Equal(Catalog.Actions.Count, Catalog.Actions.Select(a => a.Definition.Key).Distinct().Count());
        var kinds = MemoryLabHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), _dir, false, true)).Select(h => h.HandlerKind).ToHashSet();
        Assert.All(mine, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.Equal(kinds.Count, mine.Select(a => a.Definition.HandlerKind).Distinct().Count());
        Assert.All(kinds, k => Assert.StartsWith("MemoryLab", k, StringComparison.Ordinal));
        var d = mine.Single(a => a.Definition.Id == MemoryDiagnosis.PageFileActionId).Definition;
        Assert.Equal(PrivilegeLevel.Admin, d.Privileges);
        Assert.Equal(RebootRequirement.Required, d.Reboot);
        Assert.NotEmpty(d.Evidence);
    }

    [Fact]
    public void Apply_then_restore_absent_removes_the_value()
    {
        var reg = new InMemoryRegistryBackend();
        reg.SeedKey(RegHive.HKLM, PageFileHandler.MemoryManagementKey);
        var h = new PageFileHandler(reg);
        var a = Instance();
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var before = h.Detect(a);
        Assert.False(before.Exists);
        h.Apply(a);
        var after = h.Detect(a);
        Assert.True(after.SameValueAs(a.Desired));
        Assert.Equal("MultiString", after.Type);
        h.Restore(a, before);
        Assert.False(h.Detect(a).Exists);
        Assert.Equal(0, reg.ValueCount(RegHive.HKLM, PageFileHandler.MemoryManagementKey)); // chave mantida, valor removido
    }

    [Fact]
    public void Restore_brings_back_the_previous_custom_value_exactly()
    {
        var reg = new InMemoryRegistryBackend();
        var target = new RegistryTarget(RegHive.HKLM, PageFileHandler.MemoryManagementKey, "PagingFiles", RegView.Default);
        reg.Seed(target, "MultiString", PageFileConfig.Canonical([@"C:\pagefile.sys 512 1024", @"D:\pagefile.sys 0 0"]));
        var h = new PageFileHandler(reg);
        var a = Instance();
        var before = h.Detect(a);
        h.Apply(a);
        Assert.Equal(@"?:\pagefile.sys", reg.Read(target).Value);
        Assert.Null(h.CheckRestore(a, before));
        h.Restore(a, before);
        Assert.Equal(before.Value, reg.Read(target).Value);
    }

    [Fact]
    public void Assess_blocks_tampered_instances()
    {
        var h = new PageFileHandler(new InMemoryRegistryBackend());
        var a = Instance();
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(a with { Desired = StateSnapshot.Of("MultiString", "C:\\pagefile.sys 16 16") }, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(a with { Target = new Dictionary<string, string> { ["setting"] = "Other" } }, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(a, T.Ctx with { IsElevated = false }).Kind);
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of("DWord", "1")));
        Assert.NotNull(h.CheckRestore(a with { Target = new Dictionary<string, string> { ["setting"] = "PagingFiles", ["path"] = "x" } }, StateSnapshot.Absent()));
    }
}

[Trait("Category", "Unit")]
public sealed class MemoryLabBenchmarkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-memrun-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly BenchmarkOptions Tiny = new()
    {
        BufferBytes = 8L << 20, Threads = 2, Passes = 3, LatencyBytes = 4L << 20, LatencyDuration = TimeSpan.FromMilliseconds(40), TryLargePages = false,
    };

    [Fact]
    public void Reduced_bandwidth_measure_runs_and_reports_medians()
    {
        var progress = new List<string>();
        var r = MemoryBenchmark.MeasureBandwidth(Tiny, new SyncProgress(progress));
        Assert.True(r.ReadGbps > 0);
        Assert.True(r.WriteGbps > 0);
        Assert.True(r.CopyGbps > 0);
        Assert.Equal(3, r.ReadSamples.Count);
        Assert.Equal(2, r.Threads);
        Assert.Equal(8L << 20, r.BufferBytes);
        Assert.Contains(progress, p => p.Contains("leitura", StringComparison.Ordinal));
    }

    [Fact]
    public void Reduced_latency_measure_runs()
    {
        var r = MemoryBenchmark.MeasureLatency(Tiny);
        Assert.True(r.RandomNs > 0);
        Assert.True(r.PageLocalNs > 0);
        Assert.False(r.LargePages);
        Assert.Contains("4 KB", r.PageNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Measures_are_cancelable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MemoryBenchmark.MeasureBandwidth(Tiny, null, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => MemoryBenchmark.MeasureLatency(Tiny, null, null, cts.Token));
    }

    [Fact]
    public void Sizes_follow_free_memory()
    {
        var big = BenchmarkOptions.ForAvailableMemory(3UL << 30, 12);
        Assert.Equal(256L << 20, big.BufferBytes);
        Assert.Equal(512L << 20, big.LatencyBytes);
        Assert.Equal(12, big.Threads);
        var small = BenchmarkOptions.ForAvailableMemory(512UL << 20, 4);
        Assert.Equal(64L << 20, small.BufferBytes);
        Assert.Equal(128L << 20, small.LatencyBytes);
    }

    [Fact]
    public void Median_of_samples()
    {
        Assert.Equal(2, MemoryBenchmark.Median([3, 1, 2]));
        Assert.Equal(2.5, MemoryBenchmark.Median([4, 1, 2, 3]));
        Assert.Equal(0, MemoryBenchmark.Median([]));
    }

    [Fact]
    public void Store_saves_and_loads_runs_newest_first_and_skips_corrupt_files()
    {
        var store = new MemoryLabStore(_dir);
        var older = new MemoryRun { TakenAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), Configuration = "8 GB", TheoreticalGbps = 25.6, Bandwidth = new BandwidthResult(21, 15, 14, [21], [15], [14], 12, 1, 1, "AVX") };
        var newer = older with { TakenAt = older.TakenAt.AddDays(10), Configuration = "16 GB" };
        store.Save(older);
        store.Save(newer);
        File.WriteAllText(Path.Combine(_dir, "99999999-999999.json"), "{ corrompido");
        var all = store.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("16 GB", all[0].Configuration);
        Assert.Equal(21, all[1].Bandwidth!.ReadGbps);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) { lock (sink) sink.Add(value); }
    }
}
