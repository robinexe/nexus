using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Nexus.Core;
using Nexus.Lab.Memory;
using Nexus.Lab.Memory.Problems;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

[Trait("Category", "Unit")]
public sealed class MemoryDoctorTests
{
    private const double Mb = 1024 * 1024, Gb = 1024 * Mb;
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private static List<TrendSample> Series(int n, int minutesStep, Func<int, double> value) =>
        Enumerable.Range(0, n).Select(i => new TrendSample(T0.AddMinutes(i * minutesStep), value(i))).ToList();

    // ------------------------------------------------------------------ detector de vazamento

    [Fact]
    public void Linear_growth_is_a_leak()
    {
        // 500 MB → 1,7 GB em 40 min, com ruído pequeno.
        var s = Series(21, 2, i => 500 * Mb + i * 60 * Mb + (i % 3 - 1) * 5 * Mb);
        var v = LeakDetector.Evaluate(s, LeakThresholds.PrivateBytes);
        Assert.Equal(TrendKind.Leak, v.Kind);
        Assert.InRange(v.FittedGrowth / Gb, 1.1, 1.3);
        Assert.True(v.R2 > 0.95);
        Assert.InRange(v.SlopePerHour / Gb, 1.6, 2.0);
    }

    [Fact]
    public void Sawtooth_garbage_collection_is_not_a_leak()
    {
        // Sobe 100 MB por minuto por 8 min e cai de volta, repetido por 48 min.
        var s = Series(49, 1, i => 800 * Mb + (i % 8) * 100 * Mb);
        var v = LeakDetector.Evaluate(s, LeakThresholds.PrivateBytes);
        Assert.NotEqual(TrendKind.Leak, v.Kind);
    }

    [Fact]
    public void Isolated_spike_is_not_a_leak()
    {
        var s = Series(30, 1, i => i == 27 ? 3 * Gb : 900 * Mb);
        var v = LeakDetector.Evaluate(s, LeakThresholds.PrivateBytes);
        Assert.NotEqual(TrendKind.Leak, v.Kind);
        Assert.Equal(0, v.Growth, 3);
    }

    [Fact]
    public void Step_that_came_back_down_is_not_a_leak()
    {
        var s = Series(30, 1, i => i is >= 10 and < 28 ? 1.8 * Gb : 600 * Mb);
        Assert.NotEqual(TrendKind.Leak, LeakDetector.Evaluate(s, LeakThresholds.PrivateBytes).Kind);
    }

    [Fact]
    public void Too_short_or_small_growth_is_not_reported()
    {
        Assert.Equal(TrendKind.Insufficient, LeakDetector.Evaluate(Series(5, 1, i => i * 200 * Mb), LeakThresholds.PrivateBytes).Kind);
        Assert.Equal(TrendKind.Insufficient, LeakDetector.Evaluate(Series(20, 0, i => i * 200 * Mb), LeakThresholds.PrivateBytes).Kind);
        // 4 GB → 4,1 GB: abaixo de 150 MB e de 20%.
        Assert.Equal(TrendKind.Stable, LeakDetector.Evaluate(Series(20, 2, i => 4 * Gb + i * 5 * Mb), LeakThresholds.PrivateBytes).Kind);
        Assert.Equal(TrendKind.Stable, LeakDetector.Evaluate(Series(20, 2, i => 2 * Gb - i * 10 * Mb), LeakThresholds.PrivateBytes).Kind);
        Assert.Equal(TrendKind.Insufficient, LeakDetector.Evaluate([], LeakThresholds.PrivateBytes).Kind);
    }

    [Fact]
    public void Handle_leak_uses_its_own_thresholds()
    {
        var leak = LeakDetector.Evaluate(Series(16, 2, i => 2000 + i * 300), LeakThresholds.Handles);
        Assert.True(leak.IsLeak);
        var normal = LeakDetector.Evaluate(Series(16, 2, i => 2000 + i % 4 * 50), LeakThresholds.Handles);
        Assert.False(normal.IsLeak);
    }

    // ------------------------------------------------------------------ agrupamento

    private static ProcSample P(int pid, string name, double privMb, int session = 1, string? path = null, int handles = 300, int? gdi = 100) =>
        new(pid, name, (long)(privMb * Mb * 0.8), (long)(privMb * Mb), handles, 20, session, 133_000_000_000_000_000 + pid, gdi, 50, path);

    [Fact]
    public void Browser_processes_are_summed_and_svchost_is_grouped_by_service()
    {
        var procs = new List<ProcSample>
        {
            P(10, "msedge", 300), P(11, "msedge", 200), P(12, "msedge.exe", 100), P(20, "svchost", 50, 0), P(21, "svchost", 80, 0),
            P(22, "svchost", 10, 0), P(30, "Discord", 400), P(31, "DiscordPTB", 50), P(40, "notepad", 20), P(4, "System", 1, 0),
        };
        var services = new Dictionary<int, IReadOnlyList<ServiceInfo>>
        {
            [20] = [new ServiceInfo("wuauserv", "Windows Update")],
            [21] = [new ServiceInfo("Dnscache", "Cliente DNS"), new ServiceInfo("BITS", "Transferência inteligente")],
        };
        var groups = AppGrouping.Group(procs, services);
        var edge = groups.Single(g => g.Key == "app:msedge");
        Assert.Equal(AppKind.Browser, edge.Kind);
        Assert.Equal(3, edge.ProcessCount);
        Assert.Equal((long)(600 * Mb), edge.PrivateBytes);
        Assert.Equal("app:msedge", groups[0].Key); // maior primeiro
        var wu = groups.Single(g => g.Key == "svc:wuauserv");
        Assert.Equal(AppKind.ServiceHost, wu.Kind);
        Assert.Contains("Windows Update", wu.DisplayName, StringComparison.Ordinal);
        Assert.Single(groups, g => g.Key == "svc:bits,dnscache");
        Assert.Single(groups, g => g.Key == "svc:pid:22");
        Assert.Equal(2, groups.Single(g => g.Key == "app:discord").ProcessCount);
        Assert.Equal(AppKind.System, groups.Single(g => g.Key == "app:system").Kind);
        Assert.Equal(100, edge.MaxGdi);
        Assert.True(AppGrouping.IsBrowser("chrome.exe"));
        Assert.False(AppGrouping.IsBrowser("notepad"));
    }

    // ------------------------------------------------------------------ proteção

    [Theory]
    [InlineData("csrss", 600, 1, ProcessGuard.Forbidden)]
    [InlineData("svchost.exe", 900, 0, ProcessGuard.Forbidden)]
    [InlineData("lsass", 700, 0, ProcessGuard.Forbidden)]
    [InlineData("MsMpEng", 800, 0, ProcessGuard.Forbidden)]
    [InlineData("vgc", 1200, 0, ProcessGuard.Forbidden)]
    [InlineData("EasyAntiCheat_EOS", 1300, 1, ProcessGuard.Forbidden)]
    [InlineData("BEService", 1301, 1, ProcessGuard.Forbidden)]
    [InlineData("Nexus.Desktop", 1400, 1, ProcessGuard.Forbidden)]
    [InlineData("System", 4, 0, ProcessGuard.Forbidden)]
    [InlineData("someservice", 1500, 0, ProcessGuard.Forbidden)]
    [InlineData("dwm", 1600, 1, ProcessGuard.Forbidden)]
    [InlineData("explorer", 1700, 1, ProcessGuard.Sensitive)]
    [InlineData("Discord", 1800, 1, ProcessGuard.Normal)]
    public void Protection_rules(string name, int pid, int session, ProcessGuard expected) =>
        Assert.Equal(expected, ProcessProtection.Check(name, pid, session).Level);

    [Fact]
    public void Games_and_system32_programs_need_double_confirmation()
    {
        Assert.Equal(ProcessGuard.Sensitive, ProcessProtection.Check("cs2", 2000, 1, @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe").Level);
        Assert.Equal(ProcessGuard.Sensitive, ProcessProtection.Check("FortniteClient-Win64-Shipping", 2001, 1, @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\x.exe").Level);
        Assert.Equal(ProcessGuard.Sensitive, ProcessProtection.Check("mmc", 2002, 1, @"C:\Windows\System32\mmc.exe").Level);
        Assert.True(ProcessProtection.LooksLikeGame(@"C:\XboxGames\Forza\Content\forza.exe"));
        Assert.False(ProcessProtection.LooksLikeGame(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_1\calc.exe"));
        Assert.False(ProcessProtection.LooksLikeGame(null));
        Assert.Equal(ProcessGuard.Normal, ProcessProtection.Check("notepad++", 2003, 1, @"C:\Program Files\Notepad++\notepad++.exe").Level);
    }

    // ------------------------------------------------------------------ tags do pool

    private static byte[] PoolBuffer(params (string Tag, ulong Paged, ulong NonPaged)[] tags)
    {
        var buf = new byte[8 + 40 * tags.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)tags.Length);
        for (var i = 0; i < tags.Length; i++)
        {
            var e = buf.AsSpan(8 + i * 40, 40);
            Encoding.ASCII.GetBytes(tags[i].Tag, e);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 10);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], 4);
            BinaryPrimitives.WriteUInt64LittleEndian(e[16..], tags[i].Paged);
            BinaryPrimitives.WriteUInt32LittleEndian(e[24..], 7);
            BinaryPrimitives.WriteUInt32LittleEndian(e[28..], 2);
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], tags[i].NonPaged);
        }
        return buf;
    }

    [Fact]
    public void Pool_tag_buffer_is_parsed_like_poolmon()
    {
        var tags = PoolTagParser.Parse(PoolBuffer(("Ntfx", 1000, 0), ("Nr22", 0, 900_000_000), ("Irp ", 0, 5000)));
        Assert.Equal(3, tags.Count);
        var nr = tags.Single(t => t.Tag == "Nr22");
        Assert.Equal(900_000_000, nr.NonPagedBytes);
        Assert.Equal(7, nr.NonPagedAllocs);
        Assert.Equal(2, nr.NonPagedFrees);
        Assert.Equal(10, nr.PagedAllocs);
        Assert.Equal("Irp ", tags[2].Tag);
        // Contagem maior que o buffer: não lê além do fim.
        var truncated = PoolBuffer(("Ntfx", 1, 1), ("Proc", 2, 2));
        BinaryPrimitives.WriteUInt32LittleEndian(truncated, 50);
        Assert.Equal(2, PoolTagParser.Parse(truncated).Count);
        Assert.Empty(PoolTagParser.Parse([1, 2]));
        Assert.Equal("A?b?", PoolTagParser.TagText([0x41, 0x01, 0x62, 0xFF]));
    }

    [Fact]
    public void Pool_tags_map_to_owners_with_source()
    {
        var exact = KnownPoolTags.Lookup("Ntfx")!;
        Assert.True(exact.Exact);
        Assert.Equal("ntfs.sys", exact.Owner);
        var family = KnownPoolTags.Lookup("TcpE")!;
        Assert.False(family.Exact);
        Assert.Contains("tcpip.sys", family.Owner, StringComparison.Ordinal);
        Assert.Contains("ndis.sys", KnownPoolTags.Lookup("NDnd")!.Owner, StringComparison.Ordinal);
        Assert.Null(KnownPoolTags.Lookup("Nr22")); // terceiro: só pela busca nos drivers
        var local = KnownPoolTags.ParsePoolTagTxt("rem comentário\r\nNr22 - rtwlane.sys - Realtek WLAN\r\nAfdB - afd.sys  - Afd data buffer\r\nIrp  - <unknown>  - Io, IRP packets\r\nlixo\r\n");
        Assert.Equal("rtwlane.sys", local["Nr22"].Owner);
        Assert.Equal("Io, IRP packets", local["Irp "].Description);
        var viaLocal = KnownPoolTags.Lookup("Nr22", local)!;
        Assert.True(viaLocal.Exact);
        Assert.Contains("instalada neste PC", viaLocal.Source, StringComparison.Ordinal);
        var driver = Encoding.ASCII.GetBytes("MZ....abcNr22xyz....");
        Assert.True(KnownPoolTags.BinaryContainsTag(driver, "Nr22"));
        Assert.False(KnownPoolTags.BinaryContainsTag(driver, "Nr23"));
        Assert.False(KnownPoolTags.BinaryContainsTag(driver, "Nr2"));
    }

    // ------------------------------------------------------------------ handlers com backends falsos

    private sealed class FakeStandby : IStandbyListBackend
    {
        public int Calls;
        public void PurgeStandby() => Calls++;
    }

    private sealed class FakeProcesses : IProcessControlBackend
    {
        public Dictionary<int, ProcessIdentity> Live { get; } = [];
        public List<string> Log { get; } = [];
        public bool Closes = true;
        public ProcessIdentity? Find(int pid) => Live.GetValueOrDefault(pid);
        public void Trim(int pid) => Log.Add($"trim {pid}");
        public bool CloseGracefully(int pid, TimeSpan wait) { Log.Add($"close {pid}"); return Closes; }
        public void Terminate(int pid) => Log.Add($"end {pid}");
    }

    private sealed class FakeServices : IServiceControlBackend
    {
        public Dictionary<string, ServiceRunInfo> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Log { get; } = [];
        public int StartFailures;
        public ServiceRunInfo? Query(string service) => Map.GetValueOrDefault(service);
        public void StopService(string service, TimeSpan timeout) { Log.Add("stop " + service); Map[service] = Map[service] with { State = "Stopped" }; }
        public void StartService(string service, TimeSpan timeout)
        {
            Log.Add("start " + service);
            if (StartFailures-- > 0) throw new InvalidOperationException("erro 1053");
            Map[service] = Map[service] with { State = "Running" };
        }
    }

    private sealed class FakeAgent : IMemoryAgentBackend
    {
        public bool? Value;
        public bool? GetCompression() => Value;
        public void SetCompression(bool enabled) => Value = enabled;
    }

    private static ActionInstance Instance(string id, Dictionary<string, string>? args = null)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == id);
        var validated = entry.ValidateUserArguments(args, out var why);
        Assert.True(validated is not null, why);
        var inst = entry.TryInstantiate(new Dictionary<string, string>(validated!), out var reason);
        Assert.True(inst is not null, reason);
        return inst!;
    }

    private static Dictionary<string, string> ProcArgs(int pid, string name, long started) => new()
    {
        ["pid"] = pid.ToString(CultureInfo.InvariantCulture), ["name"] = name, ["started"] = started.ToString(CultureInfo.InvariantCulture),
    };

    [Fact]
    public void Standby_purge_is_ephemeral_and_fixed_target()
    {
        var fake = new FakeStandby();
        var h = new StandbyPurgeHandler(fake);
        var a = Instance("memorylab.standby.purge");
        Assert.Equal(PersistenceKind.Ephemeral, a.Definition.Persistence);
        Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(a, T.Ctx with { IsElevated = false }).Kind);
        h.Apply(a);
        Assert.Equal(1, fake.Calls);
        var bad = a with { Target = new Dictionary<string, string> { ["list"] = "modified" } };
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(bad, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(bad));
        Assert.Equal(1, fake.Calls);
        Assert.Null(h.CheckRestore(a, h.Detect(a)));
    }

    [Fact]
    public void Ephemeral_purge_runs_through_the_executor_with_nothing_to_undo()
    {
        var fake = new FakeStandby();
        var handlers = new HandlerRegistry().Add(new StandbyPurgeHandler(fake));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var plan = new DesiredStateResolver(handlers).Resolve([Instance("memorylab.standby.purge")], T.Ctx);
        var run = Guid.NewGuid();
        var results = exec.Execute(run, plan, T.Ctx);
        Assert.Equal(1, fake.Calls);
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));
    }

    [Fact]
    public void Trim_and_close_check_identity_and_protection()
    {
        var fake = new FakeProcesses();
        fake.Live[500] = new ProcessIdentity(500, "Discord", 111, 1, @"C:\Users\u\AppData\Local\Discord\app-1\Discord.exe");
        fake.Live[600] = new ProcessIdentity(600, "csrss", 222, 1, @"C:\Windows\System32\csrss.exe");
        var trim = new ProcessTrimHandler(fake);
        var close = new ProcessCloseHandler(fake);

        var ok = Instance("memorylab.process.trim", ProcArgs(500, "Discord.exe", 111));
        Assert.Equal(ApplicabilityKind.Applicable, trim.Assess(ok, T.Ctx with { IsElevated = false }).Kind);
        trim.Apply(ok);
        Assert.Contains("trim 500", fake.Log);

        // PID reaproveitado (outro horário de início) → recusado, nada feito.
        var reused = Instance("memorylab.process.trim", ProcArgs(500, "Discord", 999));
        Assert.Equal(ApplicabilityKind.NotApplicable, trim.Assess(reused, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => trim.Apply(reused));
        // Processo do Windows → recusado mesmo com identidade certa.
        var core = Instance("memorylab.process.close", ProcArgs(600, "csrss", 222));
        Assert.Equal(ApplicabilityKind.NotApplicable, close.Assess(core, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => close.Apply(core));
        // Já fechou.
        Assert.Throws<InvalidOperationException>(() => trim.Apply(Instance("memorylab.process.trim", ProcArgs(777, "x", 1))));
        Assert.Equal(["trim 500"], fake.Log);

        var gentle = Instance("memorylab.process.close", ProcArgs(500, "Discord", 111));
        close.Apply(gentle);
        fake.Closes = false;
        var ex = Assert.Throws<InvalidOperationException>(() => close.Apply(gentle));
        Assert.Contains("Nada foi forçado", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("end 500", fake.Log);
        close.Apply(Instance("memorylab.process.end", ProcArgs(500, "Discord", 111)));
        Assert.Contains("end 500", fake.Log);

        var mode = gentle with { Target = new Dictionary<string, string>(gentle.Target) { ["mode"] = "kill-tree" } };
        Assert.Equal(ApplicabilityKind.Blocked, close.Assess(mode, T.Ctx).Kind);
    }

    [Theory]
    [InlineData("pid", "0")]
    [InlineData("pid", "12a")]
    [InlineData("name", "..\\x")]
    [InlineData("name", "a\nb")]
    [InlineData("started", "-1")]
    public void Process_arguments_are_validated_by_the_catalog(string key, string value)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "memorylab.process.trim");
        var args = ProcArgs(500, "Discord", 111);
        args[key] = value;
        Assert.Null(entry.ValidateUserArguments(args, out _));
    }

    [Fact]
    public void Service_restart_returns_to_running_and_refuses_protected_services()
    {
        var fake = new FakeServices();
        fake.Map["DiagTrack"] = new ServiceRunInfo("DiagTrack", "Running", 900, false, 0, true);
        var h = new ServiceRestartHandler(fake);
        var a = Instance("memorylab.service.restart", new() { ["service"] = "DiagTrack" });
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        h.Apply(a);
        Assert.Equal(["stop DiagTrack", "start DiagTrack"], fake.Log);
        Assert.Equal("Running", fake.Map["DiagTrack"].State);

        // Primeira partida falha: tenta de novo e volta a rodar.
        fake.Log.Clear();
        fake.StartFailures = 1;
        h.Apply(a);
        Assert.Equal(["stop DiagTrack", "start DiagTrack", "start DiagTrack"], fake.Log);
        Assert.Equal("Running", fake.Map["DiagTrack"].State);

        // Falha duas vezes: erro claro dizendo que ficou parado.
        fake.StartFailures = 2;
        var ex = Assert.Throws<InvalidOperationException>(() => h.Apply(a));
        Assert.Contains("não voltou a iniciar", ex.Message, StringComparison.Ordinal);

        fake.Map["DiagTrack"] = fake.Map["DiagTrack"] with { State = "Stopped" };
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(a, T.Ctx).Kind);
        fake.Map["DiagTrack"] = fake.Map["DiagTrack"] with { State = "Running", RunningDependents = 2 };
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(a, T.Ctx).Kind);

        foreach (var protectedName in new[] { "AudioSrv", "WinDefend", "RpcSs", "vgc", "Dnscache" })
        {
            fake.Map[protectedName] = new ServiceRunInfo(protectedName, "Running", 1, true, 0, true);
            var p = Instance("memorylab.service.restart", new() { ["service"] = protectedName });
            Assert.Equal(ApplicabilityKind.Blocked, h.Assess(p, T.Ctx).Kind);
            Assert.Throws<InvalidOperationException>(() => h.Apply(p));
        }
        Assert.DoesNotContain(fake.Log, l => l.Contains("AudioSrv", StringComparison.Ordinal));
    }

    [Fact]
    public void Compression_applies_and_restores_previous_value()
    {
        var agent = new FakeAgent { Value = false };
        var h = new CompressionHandler(agent);
        var a = Instance("memorylab.compression.enable");
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var before = h.Detect(a);
        Assert.Equal("False", before.Value);
        h.Apply(a);
        Assert.True(h.Detect(a).SameValueAs(a.Desired));
        Assert.Null(h.CheckRestore(a, before));
        h.Restore(a, before);
        Assert.False(agent.Value);
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Absent()));
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of("MMAgent", "talvez")));
        agent.Value = null;
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(a, T.Ctx).Kind);
        var off = a with { Desired = StateSnapshot.Of("MMAgent", "False") };
        agent.Value = true;
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(off, T.Ctx).Kind);
    }

    [Fact]
    public void Browser_policies_are_a_closed_list_and_restore_absent_removes_key()
    {
        var reg = new InMemoryRegistryBackend();
        var h = new BrowserPolicyHandler(reg);
        var a = Instance("memorylab.browser.edge-startupboost-off");
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        var before = h.Detect(a);
        Assert.False(before.Exists);
        h.Apply(a);
        var t = new RegistryTarget(RegHive.HKLM, BrowserPolicyHandler.EdgeKey, "StartupBoostEnabled", RegView.Default);
        Assert.Equal("0", reg.Read(t).Value);
        Assert.True(h.Detect(a).SameValueAs(a.Desired));
        h.Restore(a, before);
        Assert.False(reg.Read(t).KeyExists);

        // Valor anterior existente volta exatamente.
        reg.Write(t, "DWord", "1");
        var prev = h.Detect(a);
        h.Apply(a);
        h.Restore(a, prev);
        Assert.Equal("1", reg.Read(t).Value);

        var foreign = a with { Target = new Dictionary<string, string> { ["policy"] = "defender.off" } };
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(foreign, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(foreign));
        Assert.NotNull(h.CheckRestore(foreign, prev));
        var wrongValue = a with { Desired = StateSnapshot.Of("DWord", "1") };
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(wrongValue, T.Ctx).Kind);
    }

    [Fact]
    public void Catalog_actions_are_registered_ephemeral_where_expected_and_documented()
    {
        var mine = Catalog.Actions.Where(a => a.Definition.Id.StartsWith("memorylab.", StringComparison.Ordinal)).ToList();
        Assert.Equal(11, mine.Count);
        var kinds = MemoryLabHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, true),
            new FakeStandby(), new FakeProcesses(), new FakeServices(), new FakeAgent()).Select(h => h.HandlerKind).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
        Assert.All(mine, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.All(mine, a =>
        {
            Assert.NotEmpty(a.Definition.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(a.Revert));
            Assert.False(string.IsNullOrWhiteSpace(a.Definition.Mechanism));
            Assert.False(a.Definition.Irreversible);
        });
        string[] ephemeral = ["memorylab.standby.purge", "memorylab.process.trim", "memorylab.process.close", "memorylab.process.end", "memorylab.service.restart"];
        foreach (var id in ephemeral) Assert.Equal(PersistenceKind.Ephemeral, mine.Single(a => a.Definition.Id == id).Definition.Persistence);
        Assert.All(mine.Where(a => a.Definition.HandlerKind == BrowserPolicyHandler.Kind), a => Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges));
        // Toda chave usada pela página existe no catálogo (inclusive as reaproveitadas de outros módulos).
        foreach (var key in typeof(DoctorActions).GetFields().Select(f => (string)f.GetValue(null)!))
            Assert.Contains(Catalog.Actions, a => a.Definition.Key == key);
    }

    // ------------------------------------------------------------------ motor de suspeitos

    private static SystemMemorySample Sys(double totalGb = 8, double availGb = 3, double commitGb = 7, double limitGb = 12, double npMb = 300, double ppMb = 600,
        double? freeMb = 1000, double? standbyMb = 1500) => new()
    {
        Total = (long)(totalGb * Gb), Available = (long)(availGb * Gb), CommitTotal = (long)(commitGb * Gb), CommitLimit = (long)(limitGb * Gb),
        CommitPeak = (long)(commitGb * Gb), NonPagedPool = (long)(npMb * Mb), PagedPool = (long)(ppMb * Mb), Modified = (long)(100 * Mb),
        FreeZero = freeMb is null ? null : (long)(freeMb.Value * Mb), Standby = standbyMb is null ? null : (long)(standbyMb.Value * Mb),
    };

    private static MemoryHistory History(int frames, Func<int, MemorySnapshot> build)
    {
        var h = new MemoryHistory();
        for (var i = 0; i < frames; i++) h.Add(build(i));
        return h;
    }

    private static List<ProcSample> Background(int extra = 0) =>
        Enumerable.Range(0, 12 + extra).Select(i => P(1000 + i, "proc" + i, 100)).ToList();

    [Fact]
    public void Leaking_app_becomes_a_suspect_with_temporary_and_permanent_fixes()
    {
        var h = History(21, i => new MemorySnapshot
        {
            At = T0.AddMinutes(i * 2), System = Sys(),
            Processes = [.. Background(), P(500, "Discord", 400 + i * 60, path: @"C:\Users\u\AppData\Local\Discord\Discord.exe")],
        });
        var s = MemoryDoctorEngine.Evaluate(h).Single(x => x.Id == "leak:app:discord");
        Assert.Contains("cresceu 1,2 GB em 40 min", s.Title, StringComparison.Ordinal);
        Assert.Contains(s.Temporary, f => f.ActionKey == DoctorActions.ProcessClose && f.Arguments!["pid"] == "500" && f.Arguments["name"] == "Discord");
        Assert.Contains(s.Temporary, f => f.ActionKey == DoctorActions.ProcessTrim && f.Cost.Contains("PIORAR", StringComparison.Ordinal));
        Assert.Contains(s.Permanent, f => f.Kind == FixKind.DisableStartup && f.Target == "Discord");
        Assert.All(s.Temporary.Concat(s.Permanent), f => Assert.False(string.IsNullOrWhiteSpace(f.HowLong)));
    }

    [Fact]
    public void Spiky_app_is_not_a_suspect()
    {
        var h = History(30, i => new MemorySnapshot
        {
            At = T0.AddMinutes(i), System = Sys(),
            Processes = [.. Background(), P(500, "Discord", i == 25 ? 3000 : 500)],
        });
        Assert.DoesNotContain(MemoryDoctorEngine.Evaluate(h), s => s.GroupKey == "app:discord");
    }

    [Fact]
    public void Leaking_service_offers_restart_and_manual_and_protected_services_only_info()
    {
        var h = History(21, i => new MemorySnapshot
        {
            At = T0.AddMinutes(i * 2), System = Sys(),
            Processes = [.. Background(), P(900, "svchost", 100 + i * 40, session: 0), P(901, "svchost", 100 + i * 40, session: 0)],
            ServicesByPid = new Dictionary<int, IReadOnlyList<ServiceInfo>>
            {
                [900] = [new ServiceInfo("DiagTrack", "Experiências do Usuário Conectado e Telemetria")],
                [901] = [new ServiceInfo("AudioSrv", "Áudio do Windows")],
            },
        });
        var all = MemoryDoctorEngine.Evaluate(h);
        var diag = all.Single(x => x.GroupKey == "svc:diagtrack");
        Assert.Contains(diag.Temporary, f => f.ActionKey == DoctorActions.ServiceRestart && f.Arguments!["service"] == "DiagTrack");
        Assert.Contains(diag.Permanent, f => f.ActionKey == DoctorActions.ServiceStartType && f.Value == "Manual");
        var audio = all.Single(x => x.GroupKey == "svc:audiosrv");
        Assert.DoesNotContain(audio.Temporary, f => f.Kind == FixKind.Apply);
        Assert.Empty(audio.Permanent);
    }

    [Fact]
    public void Kernel_pool_growth_points_to_the_driver_and_driver_updates()
    {
        var h = History(21, i => new MemorySnapshot
        {
            At = T0.AddMinutes(i * 2), System = Sys(npMb: 300 + i * 40), Processes = Background(),
            PoolTags = [new PoolTagUsage("Nr22", 0, (long)((50 + i * 38) * Mb)), new PoolTagUsage("Ntfx", (long)(80 * Mb), 0)],
        });
        var ctx = new DoctorContext { TagDrivers = new Dictionary<string, IReadOnlyList<string>> { ["Nr22"] = ["rtwlane.sys"] } };
        var pool = MemoryDoctorEngine.Evaluate(h, ctx).Single(x => x.Id == "pool");
        Assert.Equal("bad", pool.Severity);
        Assert.Contains("rtwlane.sys", pool.Title, StringComparison.Ordinal);
        Assert.Contains("'Nr22'", pool.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("'Ntfx'", pool.Evidence, StringComparison.Ordinal);
        Assert.Contains(pool.Permanent, f => f.Kind == FixKind.Navigate && f.Target == "DriverUpdates");
        Assert.Contains(pool.Temporary, f => f.Title.Contains("Reiniciar o PC", StringComparison.Ordinal));
    }

    [Fact]
    public void Pool_without_tags_asks_for_admin()
    {
        var h = History(21, i => new MemorySnapshot { At = T0.AddMinutes(i * 2), System = Sys(npMb: 300 + i * 40), Processes = Background() });
        var pool = MemoryDoctorEngine.Evaluate(h).Single(x => x.Id == "pool");
        Assert.Contains("administrador", pool.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void System_rules_commit_standby_compression_ram_and_browser()
    {
        var procs = Background().Concat(Enumerable.Range(0, 20).Select(i => P(3000 + i, "msedge", 150))).ToList();
        var h = History(3, i => new MemorySnapshot
        {
            At = T0.AddMinutes(i), System = Sys(availGb: 0.8, commitGb: 11.2, limitGb: 12, freeMb: 60, standbyMb: 2500), Processes = procs,
        });
        var ctx = new DoctorContext { CompressionEnabled = false, PageFileMode = PageFileMode.Custom, InstalledRamBytes = (long)(8 * Gb), SingleChannel = true, FreeSlot = true };
        var all = MemoryDoctorEngine.Evaluate(h, ctx);
        var commit = all.Single(s => s.Id == "commit");
        Assert.Equal("bad", commit.Severity);
        Assert.Contains(commit.Permanent, f => f.ActionKey == DoctorActions.PageFileAutomatic);
        var standby = all.Single(s => s.Id == "standby");
        Assert.Contains(standby.Temporary, f => f.ActionKey == DoctorActions.StandbyPurge && f.Admin);
        Assert.Contains("NORMAL", standby.Explanation, StringComparison.Ordinal);
        Assert.Contains(all.Single(s => s.Id == "compression").Permanent, f => f.ActionKey == DoctorActions.CompressionOn && f.Reversible);
        var ram = all.Single(s => s.Id == "ram");
        Assert.Contains("16 GB", ram.Title, StringComparison.Ordinal);
        Assert.Contains("slot livre", ram.Explanation, StringComparison.Ordinal);
        var browser = all.Single(s => s.Id == "browser:app:msedge");
        Assert.Equal(3, browser.Permanent.Count(f => f.ActionKey?.StartsWith("memorylab.browser.edge", StringComparison.Ordinal) == true));
        Assert.Equal("bad", all[0].Severity);
    }

    [Fact]
    public void Healthy_pc_has_no_suspects()
    {
        var h = History(20, i => new MemorySnapshot { At = T0.AddMinutes(i * 2), System = Sys(totalGb: 16, availGb: 14, commitGb: 8, limitGb: 20), Processes = Background() });
        Assert.Empty(MemoryDoctorEngine.Evaluate(h, new DoctorContext { CompressionEnabled = true, PageFileMode = PageFileMode.AutomaticAllDrives }));
    }

    [Fact]
    public void History_keeps_capacity_and_cuts_series_when_the_app_restarts()
    {
        var h = new MemoryHistory(5);
        for (var i = 0; i < 8; i++)
            h.Add(new MemorySnapshot { At = T0.AddMinutes(i), System = Sys(), Processes = i == 5 ? Background() : [.. Background(), P(500, "Discord", 100 + i)] });
        Assert.Equal(5, h.Count);
        Assert.Equal(2, h.Group("app:discord", g => g.PrivateBytes).Count); // só depois da ausência (frame 5)
    }

    // ------------------------------------------------------------------ modo "vigiar enquanto jogo"

    [Fact]
    public void Game_guard_only_suggests_when_all_conditions_hold()
    {
        var on = new GameGuardSettings(Enabled: true, FreeThreshold: 0.05, CooldownMinutes: 10);
        var low = Sys(freeMb: 100, standbyMb: 3000);
        Assert.True(GameGuard.Decide(on, low, "cs2", T0, null).Suggest);
        Assert.False(GameGuard.Decide(on with { Enabled = false }, low, "cs2", T0, null).Suggest);
        Assert.False(GameGuard.Decide(on, low, null, T0, null).Suggest);
        Assert.False(GameGuard.Decide(on, Sys(freeMb: 2000, standbyMb: 3000), "cs2", T0, null).Suggest);
        Assert.False(GameGuard.Decide(on, Sys(freeMb: 100, standbyMb: 500), "cs2", T0, null).Suggest);
        Assert.False(GameGuard.Decide(on, low, "cs2", T0, T0.AddMinutes(-3)).Suggest);
        Assert.True(GameGuard.Decide(on, low, "cs2", T0, T0.AddMinutes(-11)).Suggest);
        Assert.False(GameGuard.Decide(on, Sys(freeMb: null), "cs2", T0, null).Suggest);
        Assert.False(new GameGuardSettings().Enabled); // padrão desligado
    }
}
