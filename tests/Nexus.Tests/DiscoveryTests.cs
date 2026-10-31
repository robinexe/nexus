using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Nexus.Core;
using Nexus.HardwareDiscovery;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public sealed class SmbiosAndTopologyTests
{
    private static byte[] Structure(byte type, byte[] formattedAfterHeader, params string[] strings)
    {
        var len = (byte)(4 + formattedAfterHeader.Length);
        var ms = new MemoryStream();
        ms.WriteByte(type);
        ms.WriteByte(len);
        ms.Write([0x00, 0x10]); // handle
        ms.Write(formattedAfterHeader);
        if (strings.Length == 0) ms.Write([0, 0]);
        else
        {
            foreach (var s in strings) { ms.Write(Encoding.ASCII.GetBytes(s)); ms.WriteByte(0); }
            ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    /// <summary>Tipo 17 com campos até o offset 0x22 (SMBIOS 2.8). Offsets relativos ao início da estrutura.</summary>
    private static byte[] MemoryDevice(ushort sizeMb, ushort speed, ushort configured, byte memType, string locator, string maker, string part)
    {
        var f = new byte[0x22 - 4];
        void W(int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(off - 4), v);
        W(0x0C, sizeMb);
        f[0x10 - 4] = 1;           // DeviceLocator -> string 1
        f[0x12 - 4] = memType;
        W(0x15, speed);
        f[0x17 - 4] = 2;           // Manufacturer -> string 2
        f[0x1A - 4] = 3;           // PartNumber -> string 3
        W(0x20, configured);
        // SMBIOS não tem string vazia: "sem string" é índice 0 e conjunto de strings vazio (dois zeros).
        if (maker.Length == 0 && part.Length == 0)
        {
            f[0x17 - 4] = 0;
            f[0x1A - 4] = 0;
            return Structure(17, f, locator);
        }
        return Structure(17, f, locator, maker, part);
    }

    private static byte[] Raw(params byte[][] structures)
    {
        var body = structures.SelectMany(s => s).Concat(Structure(127, [])).ToArray();
        var raw = new byte[8 + body.Length];
        raw[1] = 3; raw[2] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)body.Length);
        body.CopyTo(raw, 8);
        return raw;
    }

    [Fact]
    public void Smbios_parser_reads_system_board_bios_and_memory_and_skips_empty_slots()
    {
        var system = Structure(1, [1, 2, 0, 0], "Micro-Star International", "MS-7C02");
        var board = Structure(2, [1, 2, 0, 0], "MSI", "B550 TOMAHAWK");
        var bios = Structure(0, [1, 2, 0, 0, 3, 0], "American Megatrends", "1.A0", "05/10/2024");
        var dimmA = MemoryDevice(16384, 2133, 3600, 0x1A, "DIMM_A2", "G.Skill", "F4-3600C16-16GVKC");
        var empty = MemoryDevice(0, 0, 0, 0x02, "DIMM_A1", "", "");
        var dimmB = MemoryDevice(16384, 2133, 3600, 0x1A, "DIMM_B2", "G.Skill", "F4-3600C16-16GVKC");

        var t = SmbiosParser.Parse(Raw(system, board, bios, dimmA, empty, dimmB));

        Assert.Equal(3, t.Major);
        Assert.Equal(("Micro-Star International", "MS-7C02"), SmbiosParser.SystemInfo(t));
        Assert.Equal(("MSI", "B550 TOMAHAWK"), SmbiosParser.Baseboard(t));
        Assert.Equal(("American Megatrends", "1.A0", "05/10/2024"), SmbiosParser.Bios(t));
        var mods = SmbiosParser.MemoryModules(t);
        Assert.Equal(2, mods.Count);
        Assert.All(mods, m =>
        {
            Assert.Equal(16UL * 1024 * 1024 * 1024, m.SizeBytes);
            Assert.Equal("DDR4", m.Type);
            Assert.Equal(2133u, m.MaxSpeedMts);
            Assert.Equal(3600u, m.ConfiguredSpeedMts);
        });
        Assert.Equal("DIMM_B2", mods[1].Locator);
        Assert.False(SmbiosParser.LooksVirtual("Micro-Star International", "MS-7C02"));
        Assert.True(SmbiosParser.LooksVirtual("Microsoft Corporation", "Virtual Machine"));
        Assert.Null(SmbiosParser.LooksVirtual(null, null));
    }

    [Fact]
    public void Smbios_parser_stops_at_corrupted_structure_without_inventing_data()
    {
        var good = Structure(1, [1, 2, 0, 0], "A", "B");
        var raw = Raw(good);
        raw[8 + good.Length + 1] = 2; // tamanho < 4 na estrutura seguinte
        var t = SmbiosParser.Parse(raw);
        Assert.Single(t.Structures);
    }

    private static byte[] CoreRecord(byte flags, byte effClass, ulong mask)
    {
        var r = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(r, 0);   // RelationProcessorCore
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 48);
        r[8] = flags;
        r[9] = effClass;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), mask);
        return r;
    }

    private static byte[] Simple(int rel, int size)
    {
        var r = new byte[size];
        BinaryPrimitives.WriteInt32LittleEndian(r, rel);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), (uint)size);
        return r;
    }

    private static byte[] L3(uint bytes)
    {
        var r = Simple(2, 48);
        r[8] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), bytes);
        return r;
    }

    [Fact]
    public void Cpu_topology_counts_cores_threads_smt_l3_and_hybrid_classes()
    {
        // 8 núcleos com SMT (2 threads cada) numa única classe, 1 pacote, 1 L3 de 96 MB (ex.: 5800X3D).
        var buf = Enumerable.Range(0, 8).SelectMany(i => CoreRecord(1, 0, 0b11UL << (2 * i)))
            .Concat(Simple(3, 48)).Concat(Simple(1, 48)).Concat(L3(96u * 1024 * 1024)).ToArray();
        var t = CpuTopologyParser.Parse(buf);
        Assert.Equal((1, 8, 16, true, 1), (t.Packages, t.PhysicalCores, t.LogicalProcessors, t.Smt, t.EfficiencyClasses));
        Assert.Equal([96UL * 1024 * 1024], t.L3CacheBytes);

        // Híbrida: 2 P-cores (classe 1, SMT) + 4 E-cores (classe 0).
        var hybrid = CoreRecord(1, 1, 0b11).Concat(CoreRecord(1, 1, 0b1100))
            .Concat(Enumerable.Range(0, 4).SelectMany(i => CoreRecord(0, 0, 1UL << (4 + i)))).ToArray();
        var h = CpuTopologyParser.Parse(hybrid);
        Assert.Equal((6, 8, 2, 2), (h.PhysicalCores, h.LogicalProcessors, h.EfficiencyClasses, h.PerformanceCores));
    }

    [Fact]
    public void Cpu_topology_rejects_truncated_records()
    {
        var r = CoreRecord(0, 0, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 200);
        Assert.Throws<InvalidDataException>(() => CpuTopologyParser.Parse(r));
    }

    [Theory]
    // Nomes reais do PC de validação (log de 30/10/2026).
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "Realtek Gaming 2.5GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("vEthernet (Default Switch)-QoS Packet Scheduler-0000", "Hyper-V Virtual Ethernet Adapter-QoS Packet Scheduler-0000", true)]
    [InlineData("Wi-Fi-Native WiFi Filter Driver-0000", "Intel(R) Wi-Fi 6E AX210 160MHz-Native WiFi Filter Driver-0000", true)]
    [InlineData("Ethernet", "Realtek Gaming 2.5GbE Family Controller", false)]
    [InlineData("Tailscale", "Tailscale Tunnel", false)]
    [InlineData("Conexão Local* 8", "WAN Miniport (IP)", false)]
    public void Ndis_filter_bindings_are_not_counted_as_adapters(string name, string description, bool binding) =>
        Assert.Equal(binding, NetworkClassifier.IsFilterBinding(name, description));

    [Fact]
    public void Apipa_is_not_a_usable_address_and_unknown_types_show_their_number()
    {
        Assert.False(NetworkClassifier.IsUsableIpv4("169.254.172.82"));
        Assert.False(NetworkClassifier.IsUsableIpv4("0.0.0.0"));
        Assert.True(NetworkClassifier.IsUsableIpv4("192.168.1.9"));
        Assert.Equal("Virtual (53, propVirtual)", NetworkClassifier.TypeName(53, null));
        Assert.Equal("Ethernet", NetworkClassifier.TypeName(6, "Ethernet"));
    }

    [Fact]
    public void Disk_model_and_startup_state_helpers()
    {
        Assert.Equal("Samsung SSD 980", WindowsDiscoveryHelpers.Model(@"SCSI\Disk&Ven_NVMe&Prod_Samsung_SSD_980\5&2d6&0&000000"));
        Assert.Equal("SanDisk Ultra", WindowsDiscoveryHelpers.Model(@"USBSTOR\Disk&Ven_SanDisk&Prod_Ultra&Rev_1.00\4C53"));
        Assert.Null(WindowsDiscoveryHelpers.Model("lixo"));
        Assert.True(WindowsDiscoveryHelpers.Approved([0x02, 0, 0]));
        Assert.True(WindowsDiscoveryHelpers.Approved([0x06]));
        Assert.False(WindowsDiscoveryHelpers.Approved([0x03, 1, 2]));
        Assert.Null(WindowsDiscoveryHelpers.Approved(null));
    }
}

/// <summary>Acesso aos auxiliares internos da descoberta (que estão na classe marcada como Windows).</summary>
internal static class WindowsDiscoveryHelpers
{
#pragma warning disable CA1416 // funções puras (texto/bytes), sem chamada a API do Windows
    public static string? Model(string id) => WindowsDiscovery.ModelFromInstanceId(id);
    public static bool? Approved(byte[]? raw) => WindowsDiscovery.ApprovedState(raw);
#pragma warning restore CA1416
}

[Trait("Category", "Unit")]
public sealed class DiagnosticRulesTests
{
    private static SystemFacts Facts(Action<Dictionary<string, string>>? signals = null, IReadOnlyList<MemoryModule>? modules = null,
        IReadOnlyList<DriveFacts>? drives = null)
    {
        var s = new Dictionary<string, string>();
        signals?.Invoke(s);
        return new SystemFacts
        {
            CpuVendor = "AMD",
            ActivePowerScheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
            ActivePowerSchemeName = "Equilibrado",
            RegistrySignals = s,
            ReadSignals = new HashSet<string>([DiscoverySignals.GameMode, DiscoverySignals.AppCapture, DiscoverySignals.GameDvrEnabled,
                DiscoverySignals.Hags, DiscoverySignals.HvciEnabled, DiscoverySignals.VbsEnabled]),
            IsElevated = false,
            MemoryModules = modules ?? [],
            Drives = drives ?? [],
            SystemDrive = @"C:\",
        };
    }

    [Fact]
    public void Explicit_values_are_detected_and_absent_defaults_are_inferred()
    {
        var off = DiagnosticRules.Evaluate(Facts(s => s[DiscoverySignals.GameMode] = "0")).Single(f => f.Id == "win.gamemode");
        Assert.Equal((DiagnosticSeverity.Attention, FactStatus.Detected), (off.Severity, off.Basis));
        Assert.Equal("win.gamemode.enable", off.RelatedActionId);

        var absent = DiagnosticRules.Evaluate(Facts()).Single(f => f.Id == "win.gamemode");
        Assert.Equal(FactStatus.Inferred, absent.Basis);
    }

    [Fact]
    public void With_nothing_read_every_finding_is_unavailable()
    {
        // Retrato vazio (ex.: descoberta falhou ou não é Windows): nenhuma conclusão pode ser "detectada" ou "inferida".
        var findings = DiagnosticRules.Evaluate(new SystemFacts());
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.True(f.Basis == FactStatus.Unavailable, $"{f.Id} concluiu '{f.Title}' sem dados ({f.Basis})"));
    }

    [Fact]
    public void Missing_inputs_yield_unavailable_findings_not_guesses()
    {
        var findings = DiagnosticRules.Evaluate(new SystemFacts());
        Assert.Equal(FactStatus.Unavailable, findings.Single(f => f.Id == "power.scheme").Basis);
        Assert.Equal(FactStatus.Unavailable, findings.Single(f => f.Id == "mem.speed").Basis);
        Assert.Equal(FactStatus.Unavailable, findings.Single(f => f.Id == "disk.system-free").Basis);
    }

    [Fact]
    public void Amd_on_balanced_plan_does_not_presume_a_gain()
    {
        var p = DiagnosticRules.Evaluate(Facts()).Single(f => f.Id == "power.scheme");
        Assert.Equal(DiagnosticSeverity.Info, p.Severity);
        Assert.Contains("não é presumido", p.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_and_disk_rules_use_the_numbers_they_cite()
    {
        var mods = new[] { new MemoryModule("A2", 16UL << 30, "DDR4", 3200, 2133, "x", "y") };
        var f = DiagnosticRules.Evaluate(Facts(modules: mods, drives: [new DriveFacts(@"C:\", "NTFS", 1000, 50)]));
        var speed = f.Single(x => x.Id == "mem.speed");
        Assert.Equal(DiagnosticSeverity.Attention, speed.Severity);
        Assert.Contains("3200", speed.Evidence, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Attention, f.Single(x => x.Id == "mem.channels").Severity);
        Assert.Equal(DiagnosticSeverity.Attention, f.Single(x => x.Id == "disk.system-free").Severity);
    }
}

[Trait("Category", "Unit")]
public sealed class CatalogTests
{
    private static CatalogDocument Real() => CatalogLoader.Load(Path.Combine(AppContext.BaseDirectory, "catalog", "actions.v1.json"));

    private static readonly Dictionary<string, string> Params = new() { ["activeScheme"] = "381b4222-f694-41f0-9685-ff5bb260df2e" };

    [Fact]
    public void Real_catalog_loads_all_actions_and_rejected_items()
    {
        var c = Real();
        Assert.Equal("1.0.0-draft", c.CatalogVersion);
        Assert.Equal(13, c.Actions.Count);
        Assert.Equal(14, c.Rejected.Count);
        Assert.Equal(c.Actions.Count, c.Actions.Select(a => a.Definition.Id).Distinct().Count());
    }

    [Fact]
    public void Instantiation_resolves_values_and_reports_what_is_missing()
    {
        var c = Real();
        ActionInstance? Inst(string id, out string? reason) => c.Actions.Single(a => a.Definition.Id == id).TryInstantiate(Params, out reason);

        var gm = Inst("win.gamemode.enable", out _)!;
        Assert.Equal(("DWord", "1"), (gm.Desired.Type, gm.Desired.Value));
        Assert.Equal("HKCU", gm.Target["hive"]);

        Assert.Null(Inst("win.windowed-game-optimizations.per-app", out var r1));
        Assert.Contains("gameExePath", r1, StringComparison.Ordinal);
        Assert.Null(Inst("sched.win32priorityseparation.lab", out var r2));
        Assert.Contains("2, 38, 40", r2, StringComparison.Ordinal);

        var power = Inst("power.active-scheme.high-performance.session", out _)!;
        Assert.Equal(("Guid", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), (power.Desired.Type, power.Desired.Value));
        var parking = Inst("power.processor.min-cores-parking", out _)!;
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", parking.Target["scheme"]);
        Assert.Equal(("UInt32", "100"), (parking.Desired.Type, parking.Desired.Value));
        Assert.Equal(("StartType", "Manual"), (Inst("svc.sysmain.manual.lab", out _)!.Desired.Type, Inst("svc.sysmain.manual.lab", out _)!.Desired.Value));
    }

    [Fact]
    public void Simulation_with_wrapped_handlers_reads_state_and_can_never_write()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(T.RT(@"Software\Microsoft\GameBar", "AutoGameModeEnabled"), "DWord", "0");
        var handlers = SimulationOnlyHandler.Wrap([new RegistryValueHandler(reg)]);
        var gm = Real().Actions.Single(a => a.Definition.Id == "win.gamemode.enable").TryInstantiate(Params, out _)!;

        var plan = new DesiredStateResolver(handlers).Resolve([gm], T.Ctx);
        var item = Assert.Single(plan.Items);
        Assert.Equal(PlanStatus.Pending, item.Status);
        Assert.Equal("0", item.ObservedAtPlan.Value);

        // Mesmo que alguém construísse um executor com esses handlers, nada seria escrito.
        var journal = new InMemoryJournal();
        var result = new ActionExecutor(handlers, journal).Execute(journal.BeginRun("p", "1"), plan, T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.FailedAndCompensated, result.Outcome);
        Assert.Contains("bloqueada", result.Detail, StringComparison.Ordinal);
        Assert.Equal("0", reg.Read(T.RT(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")).Value);
        Assert.Throws<ExecutionBlockedException>(() => handlers.Get(RegistryValueHandler.Kind).Restore(gm, StateSnapshot.Absent()));
    }
}

/// <summary>
/// Garantia estrutural de SOMENTE LEITURA: inspeciona os metadados do assembly Nexus.HardwareDiscovery e falha se ele
/// referenciar qualquer API de escrita (registro, arquivos, energia, serviços, dispositivos, processos).
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReadOnlyGuaranteeTests
{
    private static readonly string[] ForbiddenNative =
    [
        "RegSetValue", "RegCreateKey", "RegDelete", "RegSetKeySecurity", "RegRestoreKey", "RegReplaceKey",
        "PowerSet", "PowerWrite", "PowerDelete", "PowerCreate", "PowerImport", "PowerRestore",
        "ChangeServiceConfig", "CreateService", "DeleteService", "StartService", "ControlService", "SetServiceObjectSecurity",
        "SetupDiSet", "SetupDiCallClassInstaller", "SetupDiInstall", "SetupDiRemove", "SetupDiChangeState",
        "WriteFile", "DeleteFile", "MoveFile", "CopyFile", "CreateProcess", "ShellExecute", "SetSystem", "SetProcess", "SetThread",
        "NtSet", "ZwSet", "DeviceIoControl", "OpenProcess",
    ];

    private static readonly (string Type, string Member)[] ForbiddenManaged =
    [
        ("RegistryKey", "SetValue"), ("RegistryKey", "CreateSubKey"), ("RegistryKey", "DeleteValue"), ("RegistryKey", "DeleteSubKey"),
        ("RegistryKey", "DeleteSubKeyTree"), ("RegistryKey", "SetAccessControl"), ("Registry", "SetValue"),
        ("File", "WriteAllText"), ("File", "WriteAllBytes"), ("File", "WriteAllLines"), ("File", "AppendAllText"), ("File", "Delete"),
        ("File", "Move"), ("File", "Copy"), ("File", "Create"), ("File", "OpenWrite"), ("File", "Replace"), ("File", "SetAttributes"),
        ("Directory", "CreateDirectory"), ("Directory", "Delete"), ("Directory", "Move"),
        ("FileStream", ".ctor"), ("StreamWriter", ".ctor"), ("Process", "Start"), ("Process", "Kill"),
        ("ServiceController", "Stop"), ("ServiceController", "Start"),
    ];

    [Fact]
    public void Hardware_discovery_assembly_references_no_write_api()
    {
        var path = typeof(Discovery).Assembly.Location;
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var violations = new List<string>();

        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            var import = m.GetImport();
            if (import.Module.IsNil) continue;
            var entry = md.GetString(import.Name);
            if (ForbiddenNative.Any(f => entry.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                violations.Add($"P/Invoke {entry}");
        }

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            var name = md.GetString(mr.Name);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var type = md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
            if (ForbiddenManaged.Any(f => f.Type == type && f.Member == name))
                violations.Add($"{type}.{name}");
            // OpenSubKey só na forma de 1 parâmetro (somente leitura); OpenSubKey(nome, true/permissões) é proibido.
            if (type == "RegistryKey" && name == "OpenSubKey")
            {
                var sig = md.GetBlobReader(mr.Signature);
                sig.ReadSignatureHeader();
                var paramCount = sig.ReadCompressedInteger();
                if (paramCount != 1) violations.Add($"RegistryKey.OpenSubKey com {paramCount} parâmetros (permite escrita)");
            }
        }

        Assert.True(violations.Count == 0, "APIs de escrita encontradas: " + string.Join(", ", violations));
    }

    [Fact]
    public void Guarantee_test_would_detect_a_write_api()
    {
        // Controle do próprio teste: o assembly do Nexus.WindowsActions (que escreve de propósito) precisa ser apanhado.
        var path = typeof(WindowsRegistryBackend).Assembly.Location;
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var found = md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(mr => mr.Parent.Kind == HandleKind.TypeReference)
            .Select(mr => (md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name), md.GetString(mr.Name)))
            .ToList();
        Assert.Contains(("RegistryKey", "SetValue"), found);
        var natives = md.MethodDefinitions.Select(h => md.GetMethodDefinition(h).GetImport())
            .Where(i => !i.Module.IsNil).Select(i => md.GetString(i.Name)).ToList();
        Assert.Contains(natives, n => n.StartsWith("PowerSet", StringComparison.Ordinal) || n.StartsWith("PowerWrite", StringComparison.Ordinal));
    }
}

/// <summary>
/// A interface desktop não pode ter caminho de execução LOCAL: inspeciona o Nexus.Desktop.dll compilado e falha se ele
/// construir ActionExecutor/RecoveryService, adquirir a trava, abrir o diário para escrita ou chamar Execute/Revert.
/// Desde 31/10/2026 a única via de escrita é o serviço NexusBroker, e só por <c>BrokerSession.ForInstalledService</c>
/// (que confere o dono do pipe): abrir o pipe diretamente (<c>BrokerClient.Connect</c>) também é proibido.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DesktopReadOnlyTests
{
    private static string DesktopDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var path = Path.Combine(dir!.FullName, "src", "Nexus.Desktop", "bin", config, "net10.0-windows", "Nexus.Desktop.dll");
        Assert.True(File.Exists(path), $"Nexus.Desktop.dll não encontrado em {path}: compile a solução inteira antes dos testes.");
        return path;
    }

    private static List<(string Type, string Member)> References(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(mr => mr.Parent.Kind == HandleKind.TypeReference)
            .Select(mr => (md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name), md.GetString(mr.Name)))
            .Distinct().ToList();
    }

    [Fact]
    public void Desktop_has_no_execution_path()
    {
        var refs = References(DesktopDll());
        (string, string)[] forbidden =
        [
            ("ActionExecutor", ".ctor"), ("ActionExecutor", "Execute"), ("ActionExecutor", "Revert"), ("ActionExecutor", "RevertRun"),
            ("RecoveryService", ".ctor"), ("RecoveryService", "Recover"), ("FileRunCoordinator", "TryAcquire"), ("IRunCoordinator", "TryAcquire"),
            ("SqliteJournal", ".ctor"), ("IActionHandler", "Apply"), ("IActionHandler", "Restore"),
            ("BrokerClient", "Connect"), ("BrokerSession", ".ctor"), ("BrokerService", ".ctor"),
        ];
        var hits = forbidden.Where(refs.Contains).ToList();
        Assert.True(hits.Count == 0, "Caminho de execução na interface: " + string.Join(", ", hits));

        // E usa os caminhos somente leitura esperados (controle de que a inspeção enxerga as referências).
        Assert.Contains(("SimulationOnlyHandler", "Wrap"), refs);
        Assert.Contains(("SqliteJournal", "OpenReadOnly"), refs);
        Assert.Contains(("DesiredStateResolver", "Resolve"), refs);
        Assert.Contains(("BrokerSession", "ForInstalledService"), refs); // escrita só pelo serviço instalado
    }
}
