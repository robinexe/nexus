using Nexus.HardwareDiscovery;

namespace Nexus.Tests;

/// <summary>SSD/HDD pelo próprio disco (IOCTL): regras puras com consultas falsas, diagnóstico e conselhos que dependem do disco.</summary>
[Trait("Category", "Unit")]
public sealed class StorageTests
{
    private sealed class FakeStorage : IStorageQuery
    {
        public Dictionary<int, RawDiskInfo?> Disks { get; } = [];
        public Dictionary<string, IReadOnlyList<int>> Volumes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ThrowOnQuery { get; init; }
        public IReadOnlyList<int> DiskNumbers() => Disks.Keys.ToList();
        public RawDiskInfo? Query(int diskNumber) => ThrowOnQuery ? throw new IOException("falhou") : Disks.GetValueOrDefault(diskNumber);
        public IReadOnlyList<int> DisksOfVolume(string root) => Volumes.TryGetValue(root, out var d) ? d : [];
    }

    [Theory]
    [InlineData(false, 17, DiskMedia.Ssd)] // NVMe sem penalidade
    [InlineData(false, 11, DiskMedia.Ssd)] // SSD SATA
    [InlineData(true, 11, DiskMedia.Hdd)]  // HD SATA
    [InlineData(true, 7, DiskMedia.Hdd)]   // HD externo USB
    [InlineData(null, 17, DiskMedia.Ssd)]  // NVMe que não responde à penalidade de busca
    [InlineData(null, 11, DiskMedia.Unknown)] // SATA sem resposta: não chuta
    [InlineData(null, null, DiskMedia.Unknown)]
    [InlineData(false, 18, DiskMedia.Scm)]
    public void Classify_uses_seek_penalty_then_nvme_bus(bool? seek, int? bus, DiskMedia expected) =>
        Assert.Equal(expected, StorageClassifier.Classify(new RawDiskInfo(seek, bus, null, null, null)).Media);

    [Fact]
    public void Seek_penalty_wins_over_nvme_bus()
    {
        // Disco que se diz NVMe mas informa penalidade de busca (ex.: ponte/controladora estranha): vale o que o disco informa.
        Assert.Equal(DiskMedia.Hdd, StorageClassifier.Classify(new RawDiskInfo(true, 17, null, null, null)).Media);
    }

    [Theory]
    [InlineData(DiskMedia.Ssd, "NVMe", "SSD NVMe")]
    [InlineData(DiskMedia.Ssd, "SATA", "SSD (SATA)")]
    [InlineData(DiskMedia.Ssd, null, "SSD")]
    [InlineData(DiskMedia.Hdd, "SATA", "HD (SATA)")]
    [InlineData(DiskMedia.Unknown, "SATA", "desconhecido")]
    public void Describe_is_plain_portuguese(DiskMedia m, string? bus, string expected) => Assert.Equal(expected, StorageClassifier.Describe(m, bus));

    [Theory]
    [InlineData("NVMe", "Samsung SSD 980", "Samsung SSD 980")]
    [InlineData("ATA", "ST1000LM035-1RK172", "ST1000LM035-1RK172")]
    [InlineData("WDC", "WDC WD10EZEX-08WN4A0", "WDC WD10EZEX-08WN4A0")]
    [InlineData("Kingston", "SA400S37240G", "Kingston SA400S37240G")]
    [InlineData(null, null, null)]
    [InlineData("  ", "", null)]
    public void Model_drops_generic_vendor_and_repetition(string? vendor, string? product, string? expected) =>
        Assert.Equal(expected, StorageClassifier.Model(vendor, product));

    [Theory]
    [InlineData(17, "NVMe")]
    [InlineData(11, "SATA")]
    [InlineData(7, "USB")]
    [InlineData(99, null)]
    [InlineData(null, null)]
    public void Bus_names(int? bus, string? expected) => Assert.Equal(expected, StorageClassifier.BusName(bus));

    [Fact]
    public void Read_links_volumes_to_disks_like_the_owner_pc()
    {
        // Notebook com NVMe (C:) e um HD SATA de dados (D:).
        var q = new FakeStorage();
        q.Disks[0] = new RawDiskInfo(false, 17, "NVMe", "KINGSTON SNVS512G", 512_110_190_592);
        q.Disks[1] = new RawDiskInfo(true, 11, null, "ST1000LM035-1RK172", 1_000_204_886_016);
        q.Volumes[@"C:\"] = [0];
        q.Volumes[@"D:\"] = [1];
        var (disks, media) = StorageClassifier.Read(q, [@"C:\", @"D:\"]);
        Assert.Equal(2, disks.Count);
        Assert.Equal(DiskMedia.Ssd, disks[0].Media);
        Assert.Equal("NVMe", disks[0].Bus);
        Assert.Equal("KINGSTON SNVS512G", disks[0].Model);
        Assert.Equal([@"C:\"], disks[0].Volumes);
        Assert.Equal("SSD NVMe", disks[0].MediaText);
        Assert.Equal(DiskMedia.Hdd, disks[1].Media);
        Assert.Equal(DiskMedia.Ssd, media[@"C:\"]);
        Assert.Equal(DiskMedia.Hdd, media[@"D:\"]);
    }

    [Fact]
    public void Volume_spanning_disks_of_different_types_is_unknown()
    {
        var q = new FakeStorage();
        q.Disks[0] = new RawDiskInfo(false, 17, null, "A", null);
        q.Disks[1] = new RawDiskInfo(true, 11, null, "B", null);
        q.Volumes[@"E:\"] = [0, 1];
        var (_, media) = StorageClassifier.Read(q, [@"E:\"]);
        Assert.Equal(DiskMedia.Unknown, media[@"E:\"]);
    }

    [Fact]
    public void Disk_that_does_not_answer_is_unknown_not_guessed()
    {
        var q = new FakeStorage();
        q.Disks[0] = null;
        q.Volumes[@"C:\"] = [0];
        var (disks, media) = StorageClassifier.Read(q, [@"C:\"]);
        Assert.Equal(DiskMedia.Unknown, Assert.Single(disks).Media);
        Assert.Equal(DiskMedia.Unknown, media[@"C:\"]);
    }

    [Fact]
    public void Query_exception_does_not_break_the_read()
    {
        var q = new FakeStorage { ThrowOnQuery = true };
        q.Disks[0] = new RawDiskInfo(false, 17, null, null, null);
        q.Volumes[@"C:\"] = [0];
        var (disks, media) = StorageClassifier.Read(q, [@"C:\"]);
        Assert.Equal(DiskMedia.Unknown, Assert.Single(disks).Media);
        Assert.Equal(DiskMedia.Unknown, media[@"C:\"]);
    }

    [Fact]
    public void Volume_disk_not_in_enumeration_is_still_queried()
    {
        var q = new FakeStorage();
        q.Volumes[@"C:\"] = [3];
        var (disks, _) = StorageClassifier.Read(q, [@"C:\"]);
        Assert.Equal(3, Assert.Single(disks).Number);
    }

    // ------------------------------------------------------------------ diagnóstico e conselhos

    [Fact]
    public void Hdd_system_disk_is_attention_and_honest_about_fps()
    {
        var f = DiagnosticRules.SystemDiskFinding(new SystemFacts { SystemDiskMedia = DiskMedia.Hdd, SystemDiskBus = "SATA" });
        Assert.Equal("disk.system-media", f.Id);
        Assert.Equal(DiagnosticSeverity.Attention, f.Severity);
        Assert.Contains("HD (SATA)", f.Evidence, StringComparison.Ordinal);
        Assert.Contains("FPS", f.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Ssd_system_disk_is_ok_and_unknown_is_unavailable()
    {
        var ssd = DiagnosticRules.SystemDiskFinding(new SystemFacts { SystemDiskMedia = DiskMedia.Ssd, SystemDiskBus = "NVMe" });
        Assert.Equal(DiagnosticSeverity.Ok, ssd.Severity);
        Assert.Contains("SSD NVMe", ssd.Title, StringComparison.Ordinal);
        var unknown = DiagnosticRules.SystemDiskFinding(new SystemFacts());
        Assert.Equal(FactStatus.Unavailable, unknown.Basis);
    }

    [Fact]
    public void Evaluate_includes_the_disk_finding_and_sysmain_advice_by_media()
    {
        var facts = new SystemFacts
        {
            SystemDiskMedia = DiskMedia.Hdd,
            Services = [new ServiceFacts("SysMain", "SysMain", "Running", "Automatic")],
        };
        var all = DiagnosticRules.Evaluate(facts);
        Assert.Contains(all, x => x.Id == "disk.system-media");
        var sysmain = Assert.Single(all, x => x.Id == "svc.sysmain");
        Assert.Contains("HD", sysmain.Evidence, StringComparison.Ordinal);
        Assert.Contains("Não desligue", sysmain.Evidence, StringComparison.Ordinal);
        Assert.Contains("SSD", DiagnosticRules.SysMainAdvice(DiskMedia.Ssd), StringComparison.Ordinal);
    }

    [Fact]
    public void Ssd_only_preset_warns_on_hdd_and_unknown_but_not_on_ssd()
    {
        Assert.Contains("HD", StorageAdvice.PresetWarning("jogos-ssd", DiskMedia.Hdd), StringComparison.Ordinal);
        Assert.NotNull(StorageAdvice.PresetWarning("jogos-ssd", DiskMedia.Unknown));
        Assert.Null(StorageAdvice.PresetWarning("jogos-ssd", DiskMedia.Ssd));
        Assert.Null(StorageAdvice.PresetWarning("sem-xbox", DiskMedia.Hdd));
    }

    [Fact]
    public void Ssd_preset_id_exists_in_the_services_catalog()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "catalog", "services.v1.json"));
        Assert.Contains($"\"id\": \"{StorageAdvice.SsdServicesPreset}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Drive_facts_default_to_unknown_media()
    {
        Assert.Equal(DiskMedia.Unknown, new DriveFacts(@"C:\", "NTFS", 1, 1).Media);
    }
}
