using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.ViewModels;
using Nexus.DriverUpdates;
using Nexus.HardwareDiscovery;
using Nexus.Measurement;
using Nexus.Desktop.ViewModels.Simple;

namespace Nexus.Tests.Modules;

/// <summary>
/// "Atraso de drivers" apontando o driver: agregação pura dos eventos DPC/ISR por módulo (eventos sintéticos), o handler da ação
/// efêmera <c>measure.driver-latency.trace</c> com backend falso, o cruzamento arquivo .sys → dispositivo, o achado do
/// Diagnóstico e o painel da tela com um aplicador falso.
/// </summary>
[Trait("Category", "Unit")]
[Collection(LocalizationState.Name)]
public sealed class MeasureDriverTraceTests : IDisposable
{
    private const string Me = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-dpc-trace-").FullName;

    public MeasureDriverTraceTests() => Loc.Use("pt-BR");

    public void Dispose()
    {
        Loc.Use("pt-BR");
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ módulos e eventos sintéticos

    private static readonly KernelModule Kernel = new(0xFFFFF800_0000_0000, 0x0100_0000, @"\SystemRoot\system32\ntoskrnl.exe");
    private static readonly KernelModule Hal = new(0xFFFFF800_0100_0000, 0x0010_0000, @"\SystemRoot\system32\hal.dll");
    private static readonly KernelModule Ndis = new(0xFFFFF800_0200_0000, 0x0020_0000, @"\SystemRoot\system32\drivers\ndis.sys");
    private static readonly KernelModule Nvidia = new(0xFFFFF800_0300_0000, 0x0400_0000, @"\SystemRoot\system32\DriverStore\FileRepository\nv_dispi.inf_amd64_x\nvlddmkm.sys");
    /// <summary>Só a base (EnumDeviceDrivers): sem tamanho.</summary>
    private static readonly KernelModule Audio = new(0xFFFFF800_0800_0000, 0, "RTKVHD64.sys");

    private static KernelRoutineEvent Dpc(KernelModule m, double us, ulong offset = 0x1000) => new(KernelRoutineKind.Dpc, m.Base + offset, us, 0);
    private static KernelRoutineEvent Isr(KernelModule m, double us, ulong offset = 0x1000) => new(KernelRoutineKind.Isr, m.Base + offset, us, 1);

    private static DriverTraceResult Sample() => DriverTraceAnalyzer.Aggregate(
    [
        Dpc(Ndis, 120), Dpc(Ndis, 1800), Dpc(Ndis, 90), Isr(Ndis, 40),
        Dpc(Nvidia, 300), Dpc(Nvidia, 650),
        Dpc(Kernel, 2500), Dpc(Kernel, 50), Isr(Hal, 30),
        Isr(Audio, 700, 0x2000), Dpc(Audio, 200),
        new(KernelRoutineKind.Dpc, 0xFFFFF800_7000_0000, 400, 2), // endereço fora de todo módulo
        Dpc(Ndis, 5_000_000), // > 1 s: lixo, descartado
        new(KernelRoutineKind.Dpc, Ndis.Base, double.NaN, 0),
    ], [Kernel, Hal, Ndis, Nvidia, Audio], 10, new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.FromHours(-3)));

    [Fact]
    public void Aggregates_total_max_and_count_per_module_for_dpc_and_isr()
    {
        var r = Sample();
        Assert.Equal(12, r.EventCount); // 14 eventos − 1 implausível − 1 NaN
        Assert.Equal(1, r.Unresolved);
        var ndis = Assert.Single(r.Modules, m => m.Module == "ndis.sys");
        Assert.Equal(3, ndis.DpcCount);
        Assert.Equal(1800, ndis.DpcMaxUs);
        Assert.Equal(2.01, ndis.DpcTotalMs, 3);
        Assert.Equal(1, ndis.IsrCount);
        Assert.Equal(40, ndis.IsrMaxUs);
        Assert.False(ndis.IsKernelCore);
        var audio = Assert.Single(r.Modules, m => m.Module == "rtkvhd64.sys"); // base sem tamanho ainda resolve
        Assert.Equal(700, audio.IsrMaxUs);
        Assert.Equal(1, audio.DpcCount);
        Assert.True(Assert.Single(r.Modules, m => m.Module == "ntoskrnl.exe").IsKernelCore);
        Assert.True(Assert.Single(r.Modules, m => m.Module == "hal.dll").IsKernelCore);
    }

    [Fact]
    public void Culprit_is_the_worst_non_kernel_driver_and_top5_keeps_the_kernel_marked()
    {
        var r = Sample();
        Assert.Equal("ndis.sys", r.Culprit!.Module); // ntoskrnl tem 2500 µs mas é o Windows, não culpa de driver
        var top = r.Top5;
        Assert.Equal(5, top.Count);
        Assert.Equal("ntoskrnl.exe", top[0].Module);
        Assert.Equal(["ntoskrnl.exe", "ndis.sys", "rtkvhd64.sys", "nvlddmkm.sys", "hal.dll"], top.Select(m => m.Module).ToArray());
        Assert.True(top[0].IsKernelCore);
    }

    [Fact]
    public void No_culprit_when_every_driver_stays_below_half_a_millisecond()
    {
        var r = DriverTraceAnalyzer.Aggregate([Dpc(Ndis, 120), Dpc(Nvidia, 499), Dpc(Kernel, 3000)], [Kernel, Ndis, Nvidia], 10, DateTimeOffset.Now);
        Assert.Null(r.Culprit);
        Assert.Equal(3, r.EventCount);
        Assert.Empty(DriverTraceAnalyzer.Aggregate([], [], 10, DateTimeOffset.Now).Modules);
    }

    [Fact]
    public void Address_resolution_respects_sizes_and_nearest_base_without_size()
    {
        var sorted = DriverTraceAnalyzer.Normalize([Audio, Nvidia, Ndis, new KernelModule(Ndis.Base, 0, "ndis.sys")]); // base repetida: vence a com tamanho
        Assert.Equal(3, sorted.Count);
        Assert.Equal(Ndis, DriverTraceAnalyzer.Resolve(Ndis.Base + Ndis.Size - 1, sorted));
        Assert.Null(DriverTraceAnalyzer.Resolve(Ndis.Base + Ndis.Size, sorted)); // logo depois do fim do ndis: buraco até o nvlddmkm
        Assert.Equal(Audio, DriverTraceAnalyzer.Resolve(Audio.Base + (10UL << 20), sorted)); // sem tamanho: até 64 MB
        Assert.Null(DriverTraceAnalyzer.Resolve(Audio.Base + (65UL << 20), sorted));
        Assert.Null(DriverTraceAnalyzer.Resolve(Ndis.Base - 1, sorted));
        Assert.Equal("ndis.sys", DriverTraceAnalyzer.ModuleName(@"\Device\HarddiskVolume3\Windows\System32\drivers\NDIS.SYS"));
        Assert.True(DriverTraceAnalyzer.IsKernelCore("ntkrnlmp.exe"));
        Assert.False(DriverTraceAnalyzer.IsKernelCore("ndis.sys"));
    }

    [Fact]
    public void Result_file_round_trips_and_missing_or_corrupt_file_is_null()
    {
        var path = DriverTraceFile.PathFor(_dir);
        Assert.Null(DriverTraceFile.Load(path));
        DriverTraceFile.Save(path, Sample());
        var back = DriverTraceFile.Load(path)!;
        Assert.Equal(Sample().Modules.Count, back.Modules.Count);
        Assert.Equal("ndis.sys", back.Culprit!.Module);
        Assert.Equal(1, back.Unresolved);
        File.WriteAllText(path, "{ não é json");
        Assert.Null(DriverTraceFile.Load(path));
    }

    // ------------------------------------------------------------------ handler da ação efêmera

    private sealed class FakeTrace : IKernelTraceBackend
    {
        public TimeSpan? Asked;
        public (IReadOnlyList<KernelRoutineEvent> Events, IReadOnlyList<KernelModule> Modules) Capture(TimeSpan duration)
        {
            Asked = duration;
            return ([Dpc(Ndis, 1800), Dpc(Kernel, 100)], [Kernel, Ndis]);
        }
    }

    private static CatalogEntry Entry() =>
        CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog")).Actions.Single(a => a.Definition.Id == "measure.driver-latency.trace");

    private static ActionInstance Instance(string? sid = Me)
    {
        var a = Entry().TryInstantiate(new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);
        return sid is null ? a : UserProfileScope.Bind(a, sid);
    }

    [Fact]
    public void Catalog_entry_is_admin_ephemeral_user_scoped_and_read_only()
    {
        var e = Entry();
        Assert.Equal(PrivilegeLevel.Admin, e.Definition.Privileges);
        Assert.Equal(PersistenceKind.Ephemeral, e.Definition.Persistence);
        Assert.Equal(RebootRequirement.None, e.Definition.Reboot);
        Assert.False(e.Definition.Irreversible);
        Assert.Equal(DriverTraceHandler.Kind, e.Definition.HandlerKind);
        var a = Instance(null);
        Assert.True(UserProfileScope.IsUserScoped(a));
        Assert.Equal(10, DriverTraceHandler.Seconds(a));
        Assert.Equal(StateSnapshot.Of(DriverTraceHandler.StateType, DriverTraceHandler.Done), a.Desired);
        Assert.Contains("ETW", e.Definition.Mechanism, StringComparison.Ordinal);
        Assert.Contains("Nenhuma configuração muda", e.Definition.Risks, StringComparison.Ordinal);
        Assert.Contains(e.Definition.Evidence, ev => ev.Source.Contains("learn.microsoft.com/windows/win32/etw/perfinfo", StringComparison.Ordinal));
        Assert.Equal(DriverTraceText.TraceActionId, e.Definition.Key);
    }

    [Fact]
    public void Handler_assesses_scope_seconds_and_profile_then_writes_the_result_file()
    {
        var trace = new FakeTrace();
        var handler = new DriverTraceHandler(trace, sid => sid == Me ? _dir : null, otherAccount: true, clock: () => new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var a = Instance();
        var assessed = handler.Assess(a, T.Ctx);
        // No Linux dos testes: tudo validado, mas o rastreamento é só Windows 64 bits.
        Assert.Equal(OperatingSystem.IsWindows() ? ApplicabilityKind.Applicable : ApplicabilityKind.NotApplicable, assessed.Kind);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(Instance(null), T.Ctx).Kind); // outra conta sem SID: recusa
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(a with { Target = new Dictionary<string, string>(a.Target) { ["seconds"] = "120" } }, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(a with { Target = new Dictionary<string, string>(a.Target) { ["scope"] = "Machine" } }, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(a, T.Ctx with { IsElevated = false }).Kind); // exige administrador
        Assert.Equal(StateSnapshot.Of(DriverTraceHandler.StateType, DriverTraceHandler.Ready), handler.Detect(a));

        handler.Apply(a);
        Assert.Equal(TimeSpan.FromSeconds(10), trace.Asked);
        var saved = DriverTraceFile.Load(DriverTraceFile.PathFor(_dir))!;
        Assert.Equal("ndis.sys", saved.Culprit!.Module);
        Assert.Equal(10, saved.DurationSeconds);
        Assert.Equal(2026, saved.TakenAt.Year);
        Assert.Null(handler.CheckRestore(a, handler.Detect(a)));
        handler.Restore(a, handler.Detect(a)); // efêmera: nada acontece
        Assert.Throws<InvalidOperationException>(() => handler.Apply(Instance(null)));
    }

    // ------------------------------------------------------------------ arquivo .sys → dispositivo e categoria

    private static DeviceDriver Device(string desc, string cls, string service) =>
        new("PCI\\X", desc, ["PCI\\VEN_8086"], [], cls, null, "Intel", "22.0", null, "Intel", "oem1.inf", null, 0, null, service);

    [Fact]
    public void Module_lookup_maps_sys_file_to_device_by_service_image_path_or_service_name()
    {
        var lookup = new DriverModuleLookup(
            [Device("Intel(R) Wi-Fi 6 AX201", "Net", "Netwtw10"), Device("Realtek High Definition Audio", "MEDIA", "IntcAzAudAddService"), Device("NVIDIA GeForce RTX 4070", "Display", "nvlddmkm")],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Netwtw10"] = @"\SystemRoot\System32\drivers\Netwtw10.sys",
                ["IntcAzAudAddService"] = @"system32\drivers\RTKVHD64.sys",
                ["ndis"] = @"\SystemRoot\system32\drivers\ndis.sys",
            },
            m => m == "ndis.sys" ? "NDIS 6.x wrapper driver" : null);

        var wifi = lookup.Describe("netwtw10.sys");
        Assert.Equal("Intel(R) Wi-Fi 6 AX201", wifi.Device);
        Assert.Equal(DriverCategory.Network, wifi.Category);
        var audio = lookup.Describe("RTKVHD64.SYS");
        Assert.Equal("Realtek High Definition Audio", audio.Device);
        Assert.Equal(DriverCategory.Audio, audio.Category);
        var nv = lookup.Describe("nvlddmkm.sys"); // sem ImagePath: o Windows usa drivers\<serviço>.sys
        Assert.Equal("NVIDIA GeForce RTX 4070", nv.Device);
        Assert.Equal(DriverCategory.Video, nv.Category);
        var ndis = lookup.Describe("ndis.sys"); // sem dispositivo: descrição do arquivo + categoria pelo nome
        Assert.Null(ndis.Device);
        Assert.Equal("NDIS 6.x wrapper driver", ndis.FileDescription);
        Assert.Equal(DriverCategory.Network, ndis.Category);
        Assert.Equal(DriverCategory.Kernel, lookup.Describe("ntoskrnl.exe").Category);
        Assert.Equal(DriverCategory.Storage, DriverModuleLookup.CategoryOf("stornvme.sys", null));
        Assert.Equal(DriverCategory.Usb, DriverModuleLookup.CategoryOf("USBXHCI.SYS", null));
        Assert.Equal(DriverCategory.Unknown, DriverModuleLookup.CategoryOf("xyzabc.sys", null));
        Assert.Equal(DriverCategory.Storage, DriverModuleLookup.CategoryOf("xyzabc.sys", "SCSIAdapter"));
    }

    // ------------------------------------------------------------------ achado do Diagnóstico / Problemas

    [Fact]
    public void Diagnostic_finding_is_attention_only_when_bad_and_names_the_culprit_when_traced()
    {
        var bad = PcChecks.DriverLatency(new DriverLatencyFacts("Bad", 23.5, Traced: true, "ndis.sys", "Intel(R) Wi-Fi 6 AX201", 1800))!;
        Assert.Equal(PcChecks.DriverLatencyId, bad.Id);
        Assert.Equal(DiagnosticSeverity.Attention, bad.Severity);
        Assert.Contains("ndis.sys", bad.Title, StringComparison.Ordinal);
        Assert.Contains("1800 µs", bad.Evidence, StringComparison.Ordinal);
        Assert.True(PcChecks.IsPcCheckId(bad.Id));
        Assert.Contains(PcChecks.DriverLatencySource.Url, PcChecks.SourceUrls);

        var untraced = PcChecks.DriverLatency(new DriverLatencyFacts("Bad", 23.5, Traced: false))!;
        Assert.Equal(DiagnosticSeverity.Attention, untraced.Severity);
        Assert.Contains("não dá para saber qual driver", untraced.Evidence, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Info, PcChecks.DriverLatency(new DriverLatencyFacts("MayStutter", 6, false))!.Severity);
        Assert.Equal(DiagnosticSeverity.Ok, PcChecks.DriverLatency(new DriverLatencyFacts("Good", 1, false))!.Severity);
        Assert.Null(PcChecks.DriverLatency(new DriverLatencyFacts("Unknown", 0, false)));
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en")]
    [InlineData("es")]
    public void Plain_sentence_in_every_language_names_driver_and_device(string lang)
    {
        Loc.Use(lang);
        var traced = new PcCheckState { DriverLatency = new DriverLatencyFacts("Bad", 23.5, true, "ndis.sys", "Intel(R) Wi-Fi 6 AX201", 1800) };
        var p = PcCheckText.Plain(traced)[PcChecks.DriverLatencyId];
        Assert.Contains("ndis.sys", p.Title, StringComparison.Ordinal);
        Assert.Contains("Intel(R) Wi-Fi 6 AX201", p.Title, StringComparison.Ordinal);
        Assert.Equal("Icon.Drivers", p.IconKey);
        Assert.Equal(Loc.T("Simple.Topic.Drivers"), p.Topic);
        var total = PcCheckText.Plain(new PcCheckState { DriverLatency = new DriverLatencyFacts("Bad", 23.5, false) })[PcChecks.DriverLatencyId];
        Assert.DoesNotContain("{", total.Title + total.WhatToDo, StringComparison.Ordinal);
        Assert.Contains("(10 s)", total.WhatToDo, StringComparison.Ordinal);
        Assert.Empty(PcCheckText.Plain(new PcCheckState { DriverLatency = new DriverLatencyFacts("MayStutter", 6, false) })); // informação, não atenção
        Assert.Equal(68, PlainFindings.ImpactOf(PcChecks.DriverLatency(traced.DriverLatency)!));
    }

    // ------------------------------------------------------------------ frases da tela e resultado do aplicador (lógica pura)

    private static BrokerCallResult Verified() =>
        new(true, new BrokerResponse(Guid.NewGuid(), true, Results: [new ActionResultDto(DriverTraceText.TraceActionId, "Verified", "Executada")], RunId: Guid.NewGuid()), "ok");

    private static DriverModuleLookup Lookup() => new([Device("Intel(R) Wi-Fi 6 AX201", "Net", "ndis")], new Dictionary<string, string> { ["ndis"] = @"\SystemRoot\system32\drivers\ndis.sys" });

    [Fact]
    public void Screen_text_names_culprit_device_top5_kernel_note_and_category_advice()
    {
        var lookup = Lookup();
        var trace = Sample();
        Assert.Equal("Provável culpado: ndis.sys (Intel(R) Wi-Fi 6 AX201) — DPC máximo 1,8 ms", DriverTraceText.Culprit(trace, lookup.Describe));
        Assert.Equal("", DriverTraceText.NoCulprit(trace));
        var top = DriverTraceText.Top(trace, lookup.Describe);
        Assert.Equal(5, top.Count);
        Assert.True(top[0].IsKernelCore);
        Assert.StartsWith("ntoskrnl.exe: pior 2,5 ms; 2 chamadas, 2,6 ms no total", top[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("ndis.sys (Intel(R) Wi-Fi 6 AX201): pior 1,8 ms; 4 chamadas", top[1].Text, StringComparison.Ordinal);
        Assert.Contains("10 s", DriverTraceText.Note(trace, hostIsVm: false), StringComparison.Ordinal);
        Assert.Contains(Loc.T("Dpc.TraceVm"), DriverTraceText.Note(trace, hostIsVm: true), StringComparison.Ordinal);
        Assert.DoesNotContain(Loc.T("Dpc.TraceVm"), DriverTraceText.Note(trace, hostIsVm: false), StringComparison.Ordinal);
        var advice = Assert.Single(DriverTraceText.Advice(null, trace, lookup.Describe));
        Assert.Equal(Loc.T("Dpc.Culprit.Network"), advice.Title);
        Assert.Equal(Loc.T("Dpc.Advice.NetworkTraced"), advice.Advice);

        // Sem culpado: nenhuma frase de culpado, e com veredito ruim voltam os culpados típicos.
        var quiet = DriverTraceAnalyzer.Aggregate([Dpc(Ndis, 100)], [Ndis], 10, DateTimeOffset.Now);
        Assert.Equal("", DriverTraceText.Culprit(quiet, lookup.Describe));
        Assert.Contains("0,5 ms", DriverTraceText.NoCulprit(quiet), StringComparison.Ordinal);
        var bad = DriverLatencyAnalyzer.Analyze(Enumerable.Range(1, 60).Select(s => new DpcSample(s, [new CoreDpcReading("0,0", 25, 1, 500, 900)])).ToList());
        Assert.Equal(4, DriverTraceText.Advice(bad, quiet, lookup.Describe).Count);
        Assert.Empty(DriverTraceText.Advice(null, null, lookup.Describe));

        // Resumo para o Diagnóstico: só com veredito dos contadores; o culpado e o dispositivo entram quando há investigação.
        Assert.Null(DriverTraceText.Facts(null, trace, lookup.Describe));
        var facts = DriverTraceText.Facts(bad, trace, lookup.Describe)!;
        Assert.Equal(("Bad", true, "ndis.sys", "Intel(R) Wi-Fi 6 AX201", 1800.0), (facts.Level, facts.Traced, facts.Culprit, facts.Device, facts.WorstUs));
        Assert.False(DriverTraceText.Facts(bad, null, lookup.Describe)!.Traced);
    }

    [Fact]
    public void Applier_reply_is_read_honestly_cancel_failure_stale_or_missing_file()
    {
        var fresh = Sample() with { TakenAt = DateTimeOffset.Now };
        var now = DateTimeOffset.Now;
        var ok = DriverTraceText.Outcome(Verified(), () => fresh, now);
        Assert.Same(fresh, ok.Trace);
        Assert.Equal("", ok.Status);

        Assert.Equal((null, Loc.T("Dpc.Trace.Cancelled")), DriverTraceText.Outcome(null, () => fresh, now));
        var failed = DriverTraceText.Outcome(new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true,
            Results: [new ActionResultDto(DriverTraceText.TraceActionId, "Failed", "StartTrace falhou (código 5).")]), ""), () => fresh, now);
        Assert.Null(failed.Trace);
        Assert.Contains("StartTrace falhou", failed.Status, StringComparison.Ordinal);
        var blocked = DriverTraceText.Outcome(new BrokerCallResult(true, new BrokerResponse(Guid.NewGuid(), true,
            Results: [new ActionResultDto(DriverTraceText.TraceActionId, "Blocked", "Precisa de administrador.")]), ""), () => fresh, now);
        Assert.Contains("Precisa de administrador", blocked.Status, StringComparison.Ordinal);
        Assert.Equal((null, Loc.T("Dpc.Trace.NoFile")), DriverTraceText.Outcome(Verified(), () => Sample(), now)); // arquivo de 08/11 10:00: velho
        Assert.Equal((null, Loc.T("Dpc.Trace.NoFile")), DriverTraceText.Outcome(Verified(), () => null, now));
        Assert.Equal("measure.driver-latency.trace@1", DriverTraceText.TraceActionId);
    }

    [Fact]
    public void Advice_by_category_never_promises_and_ms_formatting_is_pt()
    {
        foreach (var cat in Enum.GetValues<DriverCategory>())
        {
            var row = DriverTraceText.AdviceFor(cat, "x.sys");
            Assert.False(string.IsNullOrWhiteSpace(row.Advice));
            Assert.DoesNotContain("garantido", row.Advice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FPS", row.Advice, StringComparison.Ordinal);
        }
        Assert.Equal("1,8 ms", DriverTraceText.Ms(1800));
        Assert.Equal("420 µs", DriverTraceText.Ms(420));
    }
}
