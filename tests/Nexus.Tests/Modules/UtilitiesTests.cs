using System.IO.Compression;
using System.Text;
using Nexus.Boot.Impact;
using Nexus.Core;
using Nexus.DiskTools.Analysis;
using Nexus.Measurement;
using Nexus.Privacy;
using Nexus.SystemTools;
using Nexus.SystemTools.Crash;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>Utilidades de 02/11: travamentos, tempo de boot, sessão de jogo, teste do mouse, duplicados e mitos.</summary>
[Trait("Category", "Unit")]
public sealed class UtilitiesTests
{
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
    private static readonly string[] Languages = ["en", "es"];
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static bool IsGame(string app, string? path) => app.Equals("cs2.exe", StringComparison.OrdinalIgnoreCase) || app.Equals("eldenring.exe", StringComparison.OrdinalIgnoreCase);

    private static string Evt(int id, DateTimeOffset t, string provider, params (string? Name, string Value)[] data)
    {
        var d = string.Concat(data.Select(x => x.Name is null ? $"<Data>{x.Value}</Data>" : $"<Data Name='{x.Name}'>{x.Value}</Data>"));
        return $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{provider}'/><EventID>{id}</EventID>" +
               $"<TimeCreated SystemTime='{t.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}'/></System><EventData>{d}</EventData></Event>";
    }

    private static string Crash1000Positional(DateTimeOffset t, string app, string module, string code) =>
        Evt(1000, t, "Application Error", (null, app), (null, "1.0.0.0"), (null, "0"), (null, module), (null, "31.0.15.3623"), (null, "0"), (null, code),
            (null, "0x1"), (null, "0x2"), (null, "0x3"), (null, @"C:\Users\ana\Games\" + app), (null, @"C:\Windows\System32\" + module));

    // ================================================================== travamentos: leitura

    [Fact]
    public void Event_1000_is_read_by_position_and_by_name()
    {
        var pos = CrashEvents.AsAppCrash(CrashEvents.Parse(Crash1000Positional(Now, "cs2.exe", "nvwgf2umx.dll", "c0000005"))!)!;
        Assert.Equal("cs2.exe", pos.AppName);
        Assert.Equal("nvwgf2umx.dll", pos.ModuleName);
        Assert.Equal("c0000005", pos.ExceptionCode);
        Assert.EndsWith("cs2.exe", pos.AppPath, StringComparison.Ordinal);

        var named = CrashEvents.AsAppCrash(CrashEvents.Parse(Evt(1000, Now, "Application Error", ("AppName", "eldenring.exe"), ("AppVersion", "1.2"),
            ("ModuleName", "ntdll.dll"), ("ExceptionCode", "0xC0000374"), ("AppPath", @"D:\Jogos\eldenring.exe")))!)!;
        Assert.Equal("eldenring.exe", named.AppName);
        Assert.Equal("c0000374", named.ExceptionCode);
        Assert.Null(CrashEvents.Parse("<nada/>"));
        Assert.Null(CrashEvents.Parse("<!DOCTYPE x [<!ENTITY a 'b'>]><Event/>")); // DTD proibida
    }

    [Fact]
    public void Event_1001_and_4101_are_recognized()
    {
        var lke = CrashEvents.AsWerEvent(CrashEvents.Parse(Evt(1001, Now, "Windows Error Reporting", (null, "0"), (null, "5"), (null, "LiveKernelEvent"),
            (null, "Não disponível"), (null, "0"), (null, "141"), (null, "ffffc000")))!)!;
        Assert.True(CrashEvents.IsVideoLiveKernel(lke));
        var hang = CrashEvents.AsWerEvent(CrashEvents.Parse(Evt(1001, Now, "Windows Error Reporting", (null, "0"), (null, "5"), (null, "AppHangB1"),
            (null, ""), (null, "0"), (null, "cs2.exe")))!)!;
        Assert.True(CrashEvents.IsHang(hang));
        Assert.False(CrashEvents.IsVideoLiveKernel(hang));
        Assert.NotNull(CrashEvents.AsDisplayReset(CrashEvents.Parse(Evt(4101, Now, "Display", (null, "nvlddmkm")))!));
        Assert.Null(CrashEvents.AsDisplayReset(CrashEvents.Parse(Evt(4101, Now, "OutroProvedor", (null, "x")))!));
    }

    [Fact]
    public void Report_wer_is_parsed_and_the_account_name_never_leaves()
    {
        var text = "Version=1\r\nEventType=APPCRASH\r\nEventTime=134064000000000000\r\nSig[0].Name=Application Name\r\nSig[0].Value=cs2.exe\r\n" +
                   "AppPath=C:\\Users\\Matheus Anjos\\Steam\\cs2.exe\r\nMachineName=PC-DO-MATHEUS\r\nOsInfo[0].Key=vermaj\r\nLoadedModule[0]=C:\\Users\\Matheus Anjos\\x.dll\r\n";
        var r = CrashEvents.ParseReport("AppCrash_cs2.exe_abc", text)!;
        Assert.Equal("APPCRASH", r.EventType);
        Assert.Equal("cs2.exe", r.AppName);
        Assert.NotNull(r.TimeUtc);
        Assert.DoesNotContain("Matheus", r.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("PC-DO-MATHEUS", r.RedactedText, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%\\Steam\\cs2.exe", r.RedactedText, StringComparison.Ordinal);
        Assert.True(CrashEvents.IsRelevantReport(r, IsGame));
        Assert.False(CrashEvents.IsRelevantReport(r with { AppName = "notepad.exe" }, IsGame));
        Assert.True(CrashEvents.IsRelevantReport(new WerReportFile("Kernel_141_x", "LiveKernelEvent", null, null, ""), IsGame));
        Assert.Null(CrashEvents.ParseReport("x", "sem nada útil"));
    }

    [Fact]
    public void Utf16_report_is_decoded()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("EventType=APPCRASH\r\n")).ToArray();
        Assert.StartsWith("EventType=APPCRASH", WindowsCrashReaderDecode(bytes), StringComparison.Ordinal);
        Assert.StartsWith("EventType=APPCRASH", WindowsCrashReaderDecode(Encoding.Unicode.GetBytes(new string('a', 60) + "EventType=APPCRASH")[120..]), StringComparison.Ordinal);
    }

    // WindowsCrashReader é [SupportedOSPlatform("windows")], mas a decodificação é pura.
#pragma warning disable CA1416
    private static string WindowsCrashReaderDecode(byte[] b) => WindowsCrashReader.DecodeReport(b);
#pragma warning restore CA1416

    // ================================================================== travamentos: resumo

    [Fact]
    public void Digest_counts_game_crashes_this_week_and_merges_tdr_events()
    {
        var crashes = new[]
        {
            new AppCrash(Now.AddDays(-1), "cs2.exe", "1", "nvwgf2umx.dll", null, "c0000005", null),
            new AppCrash(Now.AddDays(-2), "cs2.exe", "1", "nvwgf2umx.dll", null, "c0000005", null),
            new AppCrash(Now.AddDays(-3), "cs2.exe", "1", "ntdll.dll", null, "c0000005", null),
            new AppCrash(Now.AddDays(-20), "cs2.exe", "1", "x.dll", null, "c0000005", null), // fora da semana
            new AppCrash(Now.AddDays(-1), "notepad.exe", "1", "x.dll", null, "c0000005", null), // não é jogo
        };
        var display = new[] { new DisplayReset(Now.AddDays(-1), "nvlddmkm"), new DisplayReset(Now.AddDays(-4), "nvlddmkm") };
        var wer = new[] { new WerEvent(Now.AddDays(-1).AddMinutes(1), "LiveKernelEvent", "141", null) }; // mesmo reinício do 4101
        var d = CrashDigestBuilder.Build(crashes, wer, display, IsGame, Now);
        var g = Assert.Single(d.Games);
        Assert.Equal(3, g.Count);
        Assert.Equal("nvwgf2umx.dll", g.TopModule);
        Assert.True(g.InGpuDriver);
        Assert.Equal(2, d.VideoResets);
        Assert.Equal([CrashAdvice.UpdateGpuDriver], d.Advice);
        Assert.False(d.Nothing);
    }

    [Fact]
    public void Advice_follows_the_pattern_of_the_crashes()
    {
        Assert.Empty(CrashDigestBuilder.Advise([], 0));
        Assert.Contains(CrashAdvice.CheckOverclockAndTemps, CrashDigestBuilder.Advise([], 3));
        var oneGame = new GameCrashCount("eldenring.exe", 3, "eldenring.exe", "c0000005", false, Now);
        Assert.Equal([CrashAdvice.VerifyGameFiles], CrashDigestBuilder.Advise([oneGame], 0));
        var two = CrashDigestBuilder.Advise([oneGame, new GameCrashCount("cs2.exe", 1, "ntdll.dll", "c0000005", false, Now)], 0);
        Assert.Contains(CrashAdvice.TestMemory, two);
        Assert.Contains(CrashAdvice.CheckOverclockAndTemps, two);
        Assert.DoesNotContain(CrashAdvice.VerifyGameFiles, two);
        Assert.Contains(CrashAdvice.TestMemory, CrashDigestBuilder.Advise([oneGame with { TopException = "c0000374" }], 0));
        Assert.True(CrashDigestBuilder.IsGpuDriverModule("atidxx64.dll"));
        Assert.False(CrashDigestBuilder.IsGpuDriverModule("game.dll"));
        Assert.Equal(1, CrashDigestBuilder.CountVideoResets([Now, Now.AddMinutes(1)], [Now.AddMinutes(2)]));
        Assert.Equal(2, CrashDigestBuilder.CountVideoResets([Now], [Now.AddMinutes(10)]));
    }

    [Fact]
    public void Bundle_has_only_the_fixed_entries_and_no_personal_data()
    {
        var data = CrashData.FromXml(
            [Crash1000Positional(Now.AddDays(-1), "cs2.exe", "nvwgf2umx.dll", "c0000005"), Crash1000Positional(Now.AddDays(-1), "notepad.exe", "x.dll", "c0000005")],
            [Evt(4101, Now.AddDays(-1), "Display", (null, "nvlddmkm"))],
            [CrashEvents.ParseReport("../../AppCrash_cs2", "EventType=APPCRASH\nSig[0].Name=Application Name\nSig[0].Value=cs2.exe\nAppPath=C:\\Users\\ana\\cs2.exe\n")!,
             CrashEvents.ParseReport("AppCrash_notepad", "EventType=APPCRASH\nSig[0].Name=Application Name\nSig[0].Value=notepad.exe\n")!],
            ["O log X exige administrador."]);
        using var ms = new MemoryStream();
        CrashBundle.Write(ms, data, IsGame, "resumo", @"Windows 11 — C:\Users\ana\x", Now);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.All(CrashBundle.FixedEntries, n => Assert.Contains(n, names));
        Assert.Equal(CrashBundle.FixedEntries.Count + 1, names.Count); // + 1 relatório do jogo (o do bloco de notas fica fora)
        Assert.All(names, n => Assert.DoesNotContain("..", n, StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open());
            var text = r.ReadToEnd();
            Assert.DoesNotContain(@"Users\ana", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("notepad", text, StringComparison.OrdinalIgnoreCase);
        }
        var csv = CrashBundle.EventsCsv(data, IsGame);
        Assert.Contains("cs2.exe", csv, StringComparison.Ordinal);
        Assert.Contains("4101", csv, StringComparison.Ordinal);
        Assert.Equal("relatorio", CrashBundle.SafeName("..."));
    }

    // ================================================================== minidespejo por jogo (handler)

    private static ActionInstance DumpAction(string exe = "cs2.exe")
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "system.crash.localdumps");
        return entry.TryInstantiate(new Dictionary<string, string> { ["gameExe"] = exe }, out var why) ?? throw new InvalidOperationException(why);
    }

    private static (IReadOnlyList<ExecutionResult> Results, ActionExecutor Exec, Guid Run) Run(IActionHandler h, ActionInstance a)
    {
        var handlers = new HandlerRegistry().Add(h);
        var exec = new ActionExecutor(handlers, new InMemoryJournal());
        var run = exec.BeginRun("teste", "1");
        return (exec.Execute(run, new DesiredStateResolver(handlers).Resolve([a], T.Ctx), T.Ctx), exec, run);
    }

    [Fact]
    public void Local_dumps_apply_and_restore_absent_removes_what_nexus_created()
    {
        var reg = new InMemoryRegistryBackend();
        var h = new LocalDumpsHandler(reg);
        var a = DumpAction();
        Assert.Equal(LocalDumpsHandler.Off, h.Detect(a).Value);
        var (results, exec, run) = Run(h, a);
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results).Outcome);
        var key = LocalDumpsHandler.KeyPath("cs2.exe");
        Assert.Equal("1", reg.Read(new RegistryTarget(RegHive.HKLM, key, "DumpType", RegView.Default)).Value);
        Assert.Equal("3", reg.Read(new RegistryTarget(RegHive.HKLM, key, "DumpCount", RegView.Default)).Value);
        Assert.False(reg.Read(new RegistryTarget(RegHive.HKLM, key, "DumpFolder", RegView.Default)).ValueExists); // pasta padrão
        Assert.Equal(RevertOutcome.Reverted, Assert.Single(exec.RevertRun(run)).Outcome);
        Assert.Equal(-1, reg.ValueCount(RegHive.HKLM, key));
        Assert.Equal(-1, reg.ValueCount(RegHive.HKLM, LocalDumpsHandler.ParentPath));
    }

    [Fact]
    public void Local_dumps_restore_brings_back_previous_values()
    {
        var reg = new InMemoryRegistryBackend();
        var key = LocalDumpsHandler.KeyPath("cs2.exe");
        reg.Seed(new RegistryTarget(RegHive.HKLM, key, "DumpType", RegView.Default), "DWord", "2");
        reg.Seed(new RegistryTarget(RegHive.HKLM, key, "DumpFolder", RegView.Default), "ExpandString", @"D:\dumps");
        var h = new LocalDumpsHandler(reg);
        var (results, exec, run) = Run(h, DumpAction());
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results).Outcome);
        Assert.Equal(RevertOutcome.Reverted, Assert.Single(exec.RevertRun(run)).Outcome);
        Assert.Equal("2", reg.Read(new RegistryTarget(RegHive.HKLM, key, "DumpType", RegView.Default)).Value);
        Assert.False(reg.Read(new RegistryTarget(RegHive.HKLM, key, "DumpCount", RegView.Default)).ValueExists);
        Assert.Equal(@"D:\dumps", reg.Read(new RegistryTarget(RegHive.HKLM, key, "DumpFolder", RegView.Default)).Value);
    }

    [Theory]
    [InlineData(@"..\x.exe")]
    [InlineData(@"C:\jogo\x.exe")]
    [InlineData("x.bat")]
    [InlineData("a..exe")]
    public void Local_dumps_reject_bad_executable_names(string exe)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "system.crash.localdumps");
        var bad = new ActionInstance(entry.Definition, new Dictionary<string, string> { ["exe"] = exe }, LocalDumpsHandler.DesiredState);
        Assert.Equal(ApplicabilityKind.Blocked, new LocalDumpsHandler(new InMemoryRegistryBackend()).Assess(bad, T.Ctx).Kind);
        Assert.Null(SafetyPolicy.CheckRegistry("HKLM", LocalDumpsHandler.KeyPath("cs2.exe"), "DumpType")); // a política geral permite (diagnóstico)
    }

    [Fact]
    public void Local_dumps_refuse_restore_with_tampered_original()
    {
        var h = new LocalDumpsHandler(new InMemoryRegistryBackend());
        var a = DumpAction();
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of(LocalDumpsHandler.StateType, LocalDumpsHandler.Off)));
        var bad = new Dictionary<string, string> { ["keyExisted"] = "false", ["parentExisted"] = "false", ["dumpType"] = "String:x", ["dumpCount"] = "" };
        Assert.NotNull(h.CheckRestore(a, StateSnapshot.Of(LocalDumpsHandler.StateType, LocalDumpsHandler.Off, ValueOrigin.Explicit, bad)));
    }

    // ================================================================== catálogo

    [Fact]
    public void Catalog_actions_are_registered_and_myths_have_sources()
    {
        var mine = Catalog.Actions.Where(a => a.Definition.Id is "system.crash.localdumps" or "files.duplicate.quarantine" or "game.session.do-foreground-cap").ToList();
        Assert.Equal(3, mine.Count);
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), OtherAccount: false, Elevated: true);
        Assert.Contains(SystemToolsHandlers.Create(env), h => h.HandlerKind == LocalDumpsHandler.Kind);
        foreach (var a in mine)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Definition.Mechanism) || string.IsNullOrWhiteSpace(a.Revert) || a.Definition.Evidence.Count == 0, a.Definition.Id);
            Assert.All(Languages, l => Assert.False(string.IsNullOrWhiteSpace(a.Translations[l].Title)));
        }
        Assert.Equal(PrivilegeLevel.Admin, mine.Single(a => a.Definition.Id == "system.crash.localdumps").Definition.Privileges);
        Assert.Equal(PrivilegeLevel.User, mine.Single(a => a.Definition.Id == "files.duplicate.quarantine").Definition.Privileges);
        var cap = mine.Single(a => a.Definition.Id == "game.session.do-foreground-cap");
        Assert.Null(PrivacyPolicyAllowlist.Check(RegHive.HKLM, cap.TargetTemplate["path"], cap.TargetTemplate["name"]));
        Assert.Equal(PrivilegeLevel.Admin, cap.Definition.Privileges);

        var myths = Catalog.Rejected.Where(r => r.Id.StartsWith("myth.util.", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, myths.Count);
        Assert.All(myths, m => Assert.StartsWith("https://", m.Source, StringComparison.Ordinal));
        Assert.All(myths, m => Assert.True(m.Reason.Length > 80, m.Id));
    }

    [Fact]
    public void Catalog_parameters_reject_bad_input()
    {
        var dump = Catalog.Actions.Single(a => a.Definition.Id == "system.crash.localdumps");
        Assert.NotNull(dump.ValidateUserArguments(new Dictionary<string, string> { ["gameExe"] = "VALORANT-Win64-Shipping.exe" }, out _));
        Assert.Null(dump.ValidateUserArguments(new Dictionary<string, string> { ["gameExe"] = @"..\cmd.exe" }, out _));
        Assert.Null(dump.ValidateUserArguments(new Dictionary<string, string> { ["gameExe"] = "x.exe\n" }, out _));
        var dup = Catalog.Actions.Single(a => a.Definition.Id == "files.duplicate.quarantine");
        Assert.NotNull(dup.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"C:\Users\ana\Downloads\a (1).zip", ["sha256"] = new string('a', 64) }, out _));
        Assert.Null(dup.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"C:\Users\ana\..\x.zip", ["sha256"] = new string('a', 64) }, out _));
        Assert.Null(dup.ValidateUserArguments(new Dictionary<string, string> { ["path"] = @"C:\x.zip", ["sha256"] = new string('A', 64) }, out _));
    }

    // ================================================================== tempo de boot

    private static BootRecord Boot(int daysAgo, double seconds) => new(Now.AddDays(-daysAgo), (long)(seconds * 1000), null, null, false, null);

    [Fact]
    public void Boot_trend_compares_recent_with_previous_and_ignores_noise()
    {
        var slowThenFast = new[] { Boot(10, 40), Boot(9, 42), Boot(8, 41), Boot(4, 25), Boot(3, 24), Boot(2, 26) };
        var t = BootTrends.Trend(slowThenFast, 3);
        Assert.Equal(BootTrendDirection.Faster, t.Direction);
        Assert.Equal(-16000, t.DeltaMs, 1);
        Assert.Equal(BootTrendDirection.Stable, BootTrends.Trend([Boot(4, 30), Boot(3, 31), Boot(2, 29), Boot(1, 30.5)], 2).Direction);
        Assert.Equal(BootTrendDirection.Slower, BootTrends.Trend([Boot(4, 20), Boot(3, 20), Boot(2, 30), Boot(1, 30)], 2).Direction);
        Assert.Equal(BootTrendDirection.Unknown, BootTrends.Trend([Boot(1, 30), Boot(2, 30)], 2).Direction);
    }

    [Fact]
    public void Boot_before_after_uses_the_latest_startup_change()
    {
        var boots = new[] { Boot(10, 40), Boot(8, 44), Boot(6, 42), Boot(3, 30), Boot(1, 28) };
        var change = Now.AddDays(-5);
        var c = BootTrends.LatestComparison(boots, [Now.AddDays(-30), change])!;
        Assert.Equal(change, c.ChangeUtc);
        Assert.Equal(3, c.BeforeCount);
        Assert.Equal(2, c.AfterCount);
        Assert.Equal(42000, c.BeforeAverageMs, 1);
        Assert.Equal(29000, c.AfterAverageMs, 1);
        Assert.True(BootTrends.IsMeaningful(c));
        var waiting = BootTrends.BeforeAfter(boots, Now.AddHours(-1))!;
        Assert.True(waiting.WaitingForBoot);
        Assert.True(double.IsNaN(waiting.DeltaMs));
        Assert.Null(BootTrends.BeforeAfter(boots, Now.AddDays(-60))); // nada antes para comparar
        Assert.False(BootTrends.IsMeaningful(new BootBeforeAfter(change, 30000, 3, 29000, 3)));
    }

    // ================================================================== sessão de jogo

    [Fact]
    public void Game_session_applies_after_the_game_settles_and_restores_after_it_closes()
    {
        var s = new GameSessionCap();
        var t = Now;
        Assert.Equal(GameSessionCommand.None, s.Next(true, true, t));
        Assert.Equal(GameSessionCommand.None, s.Next(true, true, t.AddSeconds(10)));
        Assert.Equal(GameSessionCommand.Apply, s.Next(true, true, t.AddSeconds(21)));
        s.Applying(ok: true);
        Assert.Equal(GameSessionCommand.None, s.Next(true, true, t.AddSeconds(60)));
        Assert.Equal(GameSessionCommand.None, s.Next(true, false, t.AddSeconds(61))); // fechou agora: espera (pode reabrir)
        Assert.Equal(GameSessionCommand.None, s.Next(true, true, t.AddSeconds(65)));  // reabriu: continua aplicado
        Assert.Equal(GameSessionCommand.None, s.Next(true, false, t.AddSeconds(70)));
        Assert.Equal(GameSessionCommand.Restore, s.Next(true, false, t.AddSeconds(86)));
        s.Restored(ok: true);
        Assert.False(s.Applied);
        Assert.Equal(GameSessionCommand.None, s.Next(true, false, t.AddSeconds(200)));
    }

    [Fact]
    public void Game_session_off_never_applies_and_turning_off_restores()
    {
        var s = new GameSessionCap();
        Assert.Equal(GameSessionCommand.None, s.Next(false, true, Now));
        Assert.Equal(GameSessionCommand.None, s.Next(false, true, Now.AddMinutes(5)));
        Assert.Equal(GameSessionCommand.Apply, s.Next(true, true, Now.AddMinutes(6)));
        s.Applying(true);
        Assert.Equal(GameSessionCommand.Restore, s.Next(false, true, Now.AddMinutes(7)));
        s.Restored(false); // falhou: tenta de novo
        Assert.Equal(GameSessionCommand.Restore, s.Next(false, true, Now.AddMinutes(8)));
    }

    [Fact]
    public void Game_session_refused_once_waits_for_the_next_game_and_resumes_after_restart()
    {
        var s = new GameSessionCap();
        s.Next(true, true, Now);
        Assert.Equal(GameSessionCommand.Apply, s.Next(true, true, Now.AddSeconds(30)));
        s.Applying(ok: false); // aviso do Windows negado
        Assert.Equal(GameSessionCommand.None, s.Next(true, true, Now.AddMinutes(5)));
        s.Next(true, false, Now.AddMinutes(6));
        s.Next(true, true, Now.AddMinutes(7));
        Assert.Equal(GameSessionCommand.Apply, s.Next(true, true, Now.AddMinutes(8)));

        var resumed = new GameSessionCap();
        resumed.Resume();
        resumed.Next(true, false, Now);
        Assert.Equal(GameSessionCommand.Restore, resumed.Next(true, false, Now.AddSeconds(20)));
    }

    [Fact]
    public void Background_activity_is_reported_only_above_one_megabyte_per_second()
    {
        var before = new[] { new ProcessIo(1, "steam.exe", 0), new ProcessIo(2, "OneDrive.exe", 1000), new ProcessIo(3, "chrome.exe", 0), new ProcessIo(4, "upc.exe", 0) };
        var after = new[] { new ProcessIo(1, "steam.exe", 20 * 1_048_576UL), new ProcessIo(2, "OneDrive.exe", 2000), new ProcessIo(3, "chrome.exe", 99 * 1_048_576UL),
            new ProcessIo(5, "upc.exe", 50 * 1_048_576UL) }; // PID novo: sem base
        var busy = BackgroundActivityRules.Busy(before, after, 2);
        var only = Assert.Single(busy);
        Assert.Equal(BackgroundApp.Steam, only.App);
        Assert.Equal(10, only.MegabytesPerSecond, 3);
        Assert.Empty(BackgroundActivityRules.Busy(before, after, 0));
    }

    // ================================================================== teste do mouse

    private static long[] Stamps(double intervalMs, int count, double jitterMs = 0, int seed = 1)
    {
        var f = 10_000_000L;
        var rnd = new Random(seed);
        var list = new long[count];
        double t = 0;
        for (var i = 0; i < count; i++)
        {
            t += intervalMs + (jitterMs == 0 ? 0 : (rnd.NextDouble() * 2 - 1) * jitterMs);
            list[i] = (long)(t * f / 1000.0);
        }
        return list;
    }

    [Fact]
    public void Mouse_1000hz_stable_is_recognized()
    {
        var r = MousePollingAnalyzer.Analyze(Stamps(1.0, 3000, 0.1), 10_000_000);
        Assert.Equal(MousePollingVerdict.Stable, r.Verdict);
        Assert.Equal(1000, r.NominalHz);
        Assert.InRange(r.RateHz, 950, 1050);
        Assert.True(r.JitterMs < 0.2);
    }

    [Fact]
    public void Mouse_pauses_are_ignored_and_irregular_delivery_is_flagged()
    {
        var stamps = Stamps(8, 600).ToList(); // 125 Hz
        var pause = stamps[^1] + 10_000_000; // 1 s parado
        stamps.AddRange(Stamps(8, 600).Select(x => x + pause));
        var r = MousePollingAnalyzer.Analyze(stamps, 10_000_000);
        Assert.Equal(125, r.NominalHz);
        Assert.Equal(MousePollingVerdict.Stable, r.Verdict);

        var irregular = new List<long>();
        long t = 0;
        for (var i = 0; i < 2000; i++) { t += i % 10 == 0 ? 120_000 : 10_000; irregular.Add(t); } // 1 ms com soluços de 12 ms
        var bad = MousePollingAnalyzer.Analyze(irregular, 10_000_000);
        Assert.Equal(MousePollingVerdict.Irregular, bad.Verdict);
        Assert.Equal(MousePollingVerdict.NotEnoughData, MousePollingAnalyzer.Analyze(Stamps(1, 50), 10_000_000).Verdict);
        Assert.Equal(0, MousePollingAnalyzer.Analyze(Stamps(1.5, 1000), 10_000_000).NominalHz); // 667 Hz: nenhuma taxa padrão perto
    }

    // ================================================================== duplicados

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-dup-" + Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(Root);
        public string File(string rel, byte[] content)
        {
            var p = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            System.IO.File.WriteAllBytes(p, content);
            return p;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private static byte[] Bytes(int size, byte seed, int changeAt = -1)
    {
        var b = new byte[size];
        for (var i = 0; i < size; i++) b[i] = (byte)(i * 31 + seed);
        if (changeAt >= 0) b[changeAt] ^= 0xFF;
        return b;
    }

    [Fact]
    public void Duplicates_are_found_by_size_then_full_hash_skipping_system_and_game_folders()
    {
        using var tree = new TempTree();
        var big = Bytes(200_000, 7);
        var a = tree.File(@"Downloads/video.mp4".Replace('/', Path.DirectorySeparatorChar), big);
        var b = tree.File(@"Documentos/copia/video (1).mp4".Replace('/', Path.DirectorySeparatorChar), big);
        tree.File(@"Documentos/quase.mp4".Replace('/', Path.DirectorySeparatorChar), Bytes(200_000, 7, changeAt: 150_000)); // mesmo tamanho e começo, fim diferente
        tree.File(@"steamapps/common/jogo/video.mp4".Replace('/', Path.DirectorySeparatorChar), big); // pasta de jogo: fora
        tree.File(@"AppData/Local/x/video.mp4".Replace('/', Path.DirectorySeparatorChar), big); // dados de app: fora
        var small1 = tree.File("p1.bin", Bytes(5000, 3));
        var small2 = tree.File("p2.bin", Bytes(5000, 3));
        var r = DuplicateFinder.Find(new FileSystemDuplicateSource(), [tree.Root], minSize: 1000);
        Assert.False(r.Cancelled);
        Assert.Equal(2, r.Groups.Count);
        var g = r.Groups[0];
        Assert.Equal(200_000, g.Size);
        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), g.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(DuplicateFinder.Hash(new FileSystemDuplicateSource(), a, long.MaxValue), g.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", g.Sha256);
        Assert.Equal(200_000, g.Wasted);
        Assert.Equal(new[] { small1, small2 }.Order(StringComparer.Ordinal), r.Groups[1].Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.True(r.SkippedFolders >= 2);
        Assert.Empty(DuplicateFinder.Find(new FileSystemDuplicateSource(), [tree.Root]).Groups); // padrão: só a partir de 1 MB
    }

    [Fact]
    public void Duplicate_selection_always_keeps_one_copy_and_cancel_stops()
    {
        var g = new DuplicateGroup(10, new string('a', 64), [new DupFile("A", 10, new DateTime(2020, 1, 1)), new DupFile("B", 10, new DateTime(2024, 1, 1)), new DupFile("C", 10, new DateTime(2025, 1, 1))]);
        var extra = DuplicateFinder.ExtraCopies(g).Select(f => f.Path).ToHashSet();
        Assert.Equal(["B", "C"], extra.Order());
        Assert.True(DuplicateFinder.KeepsOne(g, extra));
        Assert.False(DuplicateFinder.KeepsOne(g, new HashSet<string> { "A", "B", "C" }));
        Assert.True(DuplicateFinder.IsSkippedFolder(Path.Combine("C:", "Program Files")));
        Assert.False(DuplicateFinder.IsSkippedFolder(Path.Combine("C:", "Fotos")));
        using var tree = new TempTree();
        tree.File("a.bin", Bytes(2000, 1));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(DuplicateFinder.Find(new FileSystemDuplicateSource(), [tree.Root], 1, cancel: cts.Token).Cancelled);
    }

    [Fact]
    public void Excluded_roots_are_not_scanned()
    {
        using var tree = new TempTree();
        var content = Bytes(3000, 9);
        tree.File(Path.Combine("a", "x.bin"), content);
        tree.File(Path.Combine("nexus", "x.bin"), content);
        Assert.Empty(DuplicateFinder.Find(new FileSystemDuplicateSource(), [tree.Root], 1, excludedRoots: [Path.Combine(tree.Root, "nexus")]).Groups);
        Assert.Single(DuplicateFinder.Find(new FileSystemDuplicateSource(), [tree.Root], 1).Groups);
    }
}
