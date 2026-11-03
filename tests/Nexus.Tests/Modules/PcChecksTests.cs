using Nexus.HardwareDiscovery;
using Nexus.SystemTools;
using Nexus.SystemTools.Checks;

namespace Nexus.Tests.Modules;

/// <summary>
/// Checagens do PC (somente leitura): rede para jogos (ping/jitter/perda, Wi-Fi x cabo, bufferbloat), saúde dos discos (e jogos
/// em HD/SATA), monitor abaixo da taxa máxima e HDR, limitação do processador pelo firmware (evento 37 + % Performance Limit) e o
/// aviso de notebook na bateria. Cada regra com dados falsos; leitores com JSON/saídas sintéticas e backends falsos.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PcChecksTests
{
    private static DiagnosticFinding One(IEnumerable<DiagnosticFinding> list, string id) => Assert.Single(list, f => f.Id == id);

    // ------------------------------------------------------------------ ping: estatística

    [Fact]
    public void Ping_stats_compute_loss_average_jitter_and_p95()
    {
        var s = PingStats.From("1.1.1.1", [10, 12, null, 14, 10]);
        Assert.Equal(5, s.Sent);
        Assert.Equal(4, s.Received);
        Assert.Equal(20, s.LossPercent, 3);
        Assert.Equal(11.5, s.AverageMs!.Value, 3);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(14, s.MaxMs);
        Assert.Equal((2 + 2 + 4) / 3.0, s.JitterMs!.Value, 3); // |12-10|, |14-12|, |10-14|
        Assert.Equal(14, s.P95Ms);
    }

    [Fact]
    public void Ping_stats_with_no_reply_are_all_lost_and_have_no_numbers()
    {
        var s = PingStats.From("x", [null, null]);
        Assert.True(s.AllLost);
        Assert.Equal(100, s.LossPercent);
        Assert.Null(s.AverageMs);
        Assert.Null(s.JitterMs);
        Assert.False(PingStats.From("x", []).AllLost);
    }

    // ------------------------------------------------------------------ rede: regras

    private static PingStats Stable(string t, double ms = 20) => PingStats.From(t, Enumerable.Repeat<double?>(ms, 20).ToList());

    [Fact]
    public void Wired_and_stable_network_has_no_attention()
    {
        var list = PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, "Realtek", null, 1000, Stable("192.168.0.1", 1), Stable("1.1.1.1", 15)));
        Assert.All(list, f => Assert.Equal(DiagnosticSeverity.Ok, f.Severity));
        Assert.Contains(list, f => f.Id == PcChecks.LinkId);
        Assert.Contains(list, f => f.Id == PcChecks.LocalId);
        Assert.Contains(list, f => f.Id == PcChecks.InternetId);
    }

    [Theory]
    [InlineData(80, DiagnosticSeverity.Info)]
    [InlineData(30, DiagnosticSeverity.Attention)]
    [InlineData(null, DiagnosticSeverity.Info)]
    public void Wifi_is_info_and_weak_signal_is_attention(int? signal, DiagnosticSeverity expected)
    {
        var f = One(PcChecks.Network(new NetworkCheckFacts(LinkKind.WiFi, "Intel Wi-Fi 6", signal)), PcChecks.LinkId);
        Assert.Equal(expected, f.Severity);
        if (signal is { } s) Assert.Contains($"{s}%", f.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Packet_loss_to_the_internet_is_attention()
    {
        var lossy = PingStats.From("1.1.1.1", [.. Enumerable.Repeat<double?>(20, 18), null, null]); // 10%
        var f = One(PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, Internet: lossy)), PcChecks.InternetId);
        Assert.Equal(DiagnosticSeverity.Attention, f.Severity);
        Assert.Contains("Perda", f.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Jitter_to_the_internet_is_attention_and_high_stable_ping_is_only_info()
    {
        var jittery = PingStats.From("srv", [20, 60, 20, 60, 20, 60]);
        Assert.Equal(DiagnosticSeverity.Attention, One(PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, Internet: jittery)), PcChecks.InternetId).Severity);
        var far = Stable("srv", 180);
        var f = One(PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, Internet: far)), PcChecks.InternetId);
        Assert.Equal(DiagnosticSeverity.Info, f.Severity);
        Assert.Contains("distância", f.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Unstable_gateway_points_to_a_problem_inside_the_house()
    {
        var wobbly = PingStats.From("192.168.0.1", [2, 40, 3, 45, 2, 50]);
        var f = One(PcChecks.Network(new NetworkCheckFacts(LinkKind.WiFi, Gateway: wobbly)), PcChecks.LocalId);
        Assert.Equal(DiagnosticSeverity.Attention, f.Severity);
    }

    [Fact]
    public void Router_that_ignores_ping_is_not_an_alarm()
    {
        var f = One(PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, Gateway: PingStats.From("192.168.0.1", [null, null, null]))), PcChecks.LocalId);
        Assert.Equal(DiagnosticSeverity.Info, f.Severity);
        var g = One(PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, Internet: PingStats.From("srv", [null, null]))), PcChecks.InternetId);
        Assert.Equal(DiagnosticSeverity.Info, g.Severity);
    }

    [Theory]
    [InlineData(20, 22, 21, "A+", DiagnosticSeverity.Ok)]
    [InlineData(20, 60, 30, "B", DiagnosticSeverity.Info)]
    [InlineData(20, 150, 40, "C", DiagnosticSeverity.Attention)]
    [InlineData(20, 30, 500, "F", DiagnosticSeverity.Attention)]
    public void Bufferbloat_grade_follows_the_latency_increase(double idle, double down, double up, string grade, DiagnosticSeverity sev)
    {
        var b = new BufferbloatResult(idle, down, up, 300, 40, 50_000_000);
        Assert.Equal(grade, b.Grade);
        var f = One(PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, Bufferbloat: b)), PcChecks.BufferbloatId);
        Assert.Equal(sev, f.Severity);
        if (sev == DiagnosticSeverity.Attention)
        {
            Assert.NotNull(f.Owner);
            Assert.Contains("SQM", f.Owner!.Explanation, StringComparison.Ordinal);
            Assert.Equal(PcChecks.BufferbloatSource.Url, f.Owner.SourceUrl);
        }
    }

    [Fact]
    public void Bufferbloat_without_loaded_replies_has_no_grade_and_no_finding()
    {
        var b = new BufferbloatResult(20, null, null, null, null, 0);
        Assert.Null(b.WorstIncreaseMs);
        Assert.Equal("?", b.Grade);
        Assert.DoesNotContain(PcChecks.Network(new NetworkCheckFacts(LinkKind.Wired, Bufferbloat: b)), f => f.Id == PcChecks.BufferbloatId);
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("br.valorant.example.com", true)]
    [InlineData("game-server01", true)]
    [InlineData("", false)]
    [InlineData("1.1.1.1; rm -rf", false)]
    [InlineData("http://site", false)]
    [InlineData("a b", false)]
    [InlineData("-bad.com", false)]
    public void Typed_host_is_validated_strictly(string host, bool ok) => Assert.Equal(ok, GameNetworkTest.IsValidHost(host));

    private sealed class FakePing(Func<string, double?> reply) : IPingClient
    {
        public int Calls;
        public Task<double?> PingAsync(string host, int timeoutMs, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(reply(host));
        }
    }

    private sealed class FakeInfo(ActiveLink? link, int? signal) : INetworkInfo
    {
        public ActiveLink? Active() => link;
        public int? WifiSignalPercent() => signal;
    }

    [Fact]
    public async Task Quick_test_pings_router_and_typed_server_and_reads_wifi_signal()
    {
        var ping = new FakePing(h => h == "192.168.0.1" ? 2 : 30);
        var facts = await GameNetworkTest.QuickAsync(new FakeInfo(new ActiveLink(LinkKind.WiFi, "Wi-Fi", 866, "192.168.0.1"), 72), ping, "1.1.1.1", 5, TimeSpan.Zero);
        Assert.Equal(LinkKind.WiFi, facts.Link);
        Assert.Equal(72, facts.WifiSignalPercent);
        Assert.Equal(2, facts.Gateway!.AverageMs);
        Assert.Equal(30, facts.Internet!.AverageMs);
        Assert.Equal(10, ping.Calls);
    }

    [Fact]
    public async Task Quick_test_skips_an_invalid_host_and_a_missing_router()
    {
        var facts = await GameNetworkTest.QuickAsync(new FakeInfo(null, null), new FakePing(_ => 5), "bad host;", 3, TimeSpan.Zero);
        Assert.Equal(LinkKind.Unknown, facts.Link);
        Assert.Null(facts.Gateway);
        Assert.Null(facts.Internet);
    }

    private sealed class FakeLoad(long bytes) : ILoadSource
    {
        public int Downloads, Uploads;
        public async Task<long> DownloadAsync(long maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Downloads);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); } catch (OperationCanceledException) { }
            return Math.Min(bytes, maxBytes);
        }
        public async Task<long> UploadAsync(long maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Uploads);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); } catch (OperationCanceledException) { }
            return Math.Min(bytes, maxBytes);
        }
    }

    [Fact]
    public async Task Bufferbloat_test_measures_latency_under_download_and_upload_and_stops_the_load()
    {
        var loaded = 0;
        var load = new FakeLoad(1_000_000);
        // Latência sobe enquanto há carga: o falso responde 20 ms parado e 120 ms quando alguma transferência começou.
        var ping = new FakePing(_ => Volatile.Read(ref loaded) == 1 ? 120 : 20);
        var plan = new BufferbloatPlan
        {
            Idle = TimeSpan.FromMilliseconds(60), Phase = TimeSpan.FromMilliseconds(300), Warmup = TimeSpan.Zero, PingInterval = TimeSpan.FromMilliseconds(20),
            DownloadStreams = 2, UploadStreams = 1,
        };
        var phases = new List<string>();
        var r = await BufferbloatTest.RunAsync(ping, load, "1.1.1.1", plan, new SyncProgress(p => { phases.Add(p); Volatile.Write(ref loaded, p == "idle" ? 0 : 1); }));
        Assert.Equal(20, r.IdleMs);
        Assert.Equal(120, r.DownloadLoadedMs);
        Assert.Equal(120, r.UploadLoadedMs);
        Assert.Equal(100, r.WorstIncreaseMs);
        Assert.Equal(2, load.Downloads);
        Assert.Equal(1, load.Uploads);
        Assert.Equal(3_000_000, r.BytesUsed);
        Assert.Equal(["idle", "download", "upload"], phases);
    }

    private sealed class SyncProgress(Action<string> a) : IProgress<string>
    {
        public void Report(string value) => a(value);
    }

    [Fact]
    public void Bufferbloat_plan_states_the_maximum_data_shown_in_the_confirmation()
    {
        var plan = new BufferbloatPlan();
        Assert.Equal(4 * 25_000_000L + 2 * 10_000_000L, plan.MaxBytes);
        Assert.Equal(120, plan.MaxMegabytes);
    }

    [Theory]
    [InlineData("    Nome                   : Wi-Fi\r\n    Sinal                  : 86%\r\n    Perfil                 : Casa\r\n", 86)]
    [InlineData("Name : Wi-Fi\nSignal : 100%\n", 100)]
    [InlineData("Nombre: Wi-Fi\n    Señal  :  7 %\n", 7)]
    [InlineData("There is no wireless interface on the system.", null)]
    [InlineData("", null)]
    public void Wifi_signal_is_read_from_netsh_in_any_language(string text, int? expected) => Assert.Equal(expected, WifiSignalParser.Parse(text));

    // ------------------------------------------------------------------ discos

    private static DiskHealthFacts Disk(int n, DiskMedia media, string bus, string health = "Healthy", int? temp = 35, int? wear = 3, long? errors = 0,
        bool rel = true, params string[] op) =>
        new(n, "Disco " + n, media, bus, health, op.Length == 0 ? ["OK"] : op, temp, null, wear, errors, errors is null ? null : 0, 1200, rel);

    [Fact]
    public void Healthy_disk_is_ok_and_shows_not_informed_fields()
    {
        var f = PcChecks.Disk(new DiskHealthFacts(0, "Samsung 980", DiskMedia.Ssd, "NVMe", "Healthy", ["OK"]));
        Assert.Equal(DiagnosticSeverity.Ok, f.Severity);
        Assert.Equal("diskhealth.0", f.Id);
        Assert.Contains("não informado", f.Evidence, StringComparison.Ordinal);
        Assert.Contains("administrador", f.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unhealthy", 0, null)]
    [InlineData("Healthy", 3, null)]
    [InlineData("Healthy", 0, "Falha prevista")]
    public void Failing_disk_asks_for_backup(string health, long errors, string? op)
    {
        var f = PcChecks.Disk(op is null ? Disk(1, DiskMedia.Hdd, "SATA", health, errors: errors) : Disk(1, DiskMedia.Hdd, "SATA", health, errors: errors, op: op));
        Assert.Equal(DiagnosticSeverity.Attention, f.Severity);
        Assert.Contains("defeito", f.Title, StringComparison.Ordinal);
        Assert.Contains("backup", f.Owner!.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Worn_or_warning_disk_is_near_end_of_life()
    {
        Assert.Contains("fim da vida", PcChecks.Disk(Disk(0, DiskMedia.Ssd, "NVMe", wear: 95)).Title, StringComparison.Ordinal);
        Assert.Contains("fim da vida", PcChecks.Disk(Disk(0, DiskMedia.Ssd, "NVMe", health: "Warning")).Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DiskMedia.Ssd, "NVMe", 72, DiagnosticSeverity.Attention)]
    [InlineData(DiskMedia.Ssd, "NVMe", 60, DiagnosticSeverity.Ok)]
    [InlineData(DiskMedia.Hdd, "SATA", 56, DiagnosticSeverity.Attention)]
    public void Hot_disk_depends_on_the_type(DiskMedia media, string bus, int temp, DiagnosticSeverity sev) =>
        Assert.Equal(sev, PcChecks.Disk(Disk(0, media, bus, temp: temp)).Severity);

    [Fact]
    public void Games_on_hdd_are_attention_with_load_time_evidence()
    {
        var disks = new[] { Disk(0, DiskMedia.Ssd, "NVMe"), Disk(1, DiskMedia.Hdd, "SATA") };
        var games = new[] { new GameOnDisk("Cyberpunk 2077", @"D:\Games\Cyberpunk", 1), new GameOnDisk("CS2", @"C:\Steam\cs2", 0) };
        var f = One(PcChecks.Disks(disks, games), PcChecks.GamesOnSlowDiskId);
        Assert.Equal(DiagnosticSeverity.Attention, f.Severity);
        Assert.Contains("Cyberpunk", f.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("CS2", f.Evidence, StringComparison.Ordinal);
        Assert.Equal(PcChecks.LoadTimeSource.Url, f.Owner!.SourceUrl);
        Assert.Contains("TechSpot", f.Owner.Gain, StringComparison.Ordinal);
    }

    [Fact]
    public void Games_on_sata_ssd_are_info_only_when_an_nvme_exists()
    {
        var game = new[] { new GameOnDisk("Starfield", @"E:\Starfield", 1) };
        var withNvme = PcChecks.Disks([Disk(0, DiskMedia.Ssd, "NVMe"), Disk(1, DiskMedia.Ssd, "SATA")], game);
        Assert.Equal(DiagnosticSeverity.Info, One(withNvme, PcChecks.GamesOnSlowDiskId).Severity);
        var onlySata = PcChecks.Disks([Disk(1, DiskMedia.Ssd, "SATA")], game);
        Assert.DoesNotContain(onlySata, f => f.Id == PcChecks.GamesOnSlowDiskId);
    }

    [Fact]
    public void Games_are_placed_on_disks_by_drive_letter()
    {
        var disks = new[]
        {
            new PhysicalDiskFacts(0, "NVMe", DiskMedia.Ssd, "NVMe", null, "", [@"C:\"]),
            new PhysicalDiskFacts(1, "HD", DiskMedia.Hdd, "SATA", null, "", [@"D:\", @"E:\"]),
        };
        var placed = PcChecks.PlaceGames([("A", @"d:\games\a"), ("B", @"C:\b"), ("C", null), ("D", @"\\server\share"), ("E", @"Z:\e")], disks);
        Assert.Equal([("A", 1), ("B", 0)], placed.Select(p => (p.Name, p.DiskNumber)));
    }

    [Fact]
    public void Disk_health_json_is_parsed_with_admin_only_fields_as_not_informed()
    {
        const string json = """
            [{"DeviceId":"0","FriendlyName":"Samsung SSD 980","MediaType":4,"BusType":17,"HealthStatus":0,"OperationalStatus":[2],"Size":1000204886016,"Rel":true,
              "Temperature":41,"TemperatureMax":70,"Wear":2,"ReadErrorsUncorrected":0,"WriteErrorsUncorrected":0,"PowerOnHours":3120},
             {"DeviceId":"1","FriendlyName":"WDC WD10EZEX","MediaType":3,"BusType":11,"HealthStatus":1,"OperationalStatus":[5],"Size":1000204886016,"Rel":false,"RelError":"Acesso negado"}]
            """;
        var disks = DiskHealthReader.Parse(json);
        Assert.Equal(2, disks.Count);
        var nvme = disks[0];
        Assert.Equal((0, DiskMedia.Ssd, "NVMe", "Healthy", 41, 2, 3120L), (nvme.Number!.Value, nvme.Media, nvme.Bus!, nvme.Health!, nvme.TemperatureC!.Value, nvme.WearPercent!.Value, nvme.PowerOnHours!.Value));
        var hdd = disks[1];
        Assert.Equal(DiskMedia.Hdd, hdd.Media);
        Assert.Equal("SATA", hdd.Bus);
        Assert.Equal("Warning", hdd.Health);
        Assert.Equal(["Falha prevista"], hdd.OperationalStatus);
        Assert.False(hdd.ReliabilityRead);
        Assert.Null(hdd.TemperatureC);
        Assert.Null(hdd.WearPercent);
        Assert.Null(hdd.PowerOnHours);
        Assert.Equal(DiagnosticSeverity.Attention, PcChecks.Disk(hdd).Severity);
    }

    [Theory]
    [InlineData("{\"DeviceId\":\"2\",\"MediaType\":0,\"BusType\":7,\"HealthStatus\":5,\"Rel\":true,\"Temperature\":0,\"Wear\":0}")]
    public void Single_object_and_zero_temperature_are_handled(string json)
    {
        var d = Assert.Single(DiskHealthReader.Parse(json));
        Assert.Equal(DiskMedia.Unknown, d.Media);
        Assert.Equal("USB", d.Bus);
        Assert.Equal("Unknown", d.Health);
        Assert.Null(d.TemperatureC); // 0 = o disco não informa
        Assert.Equal(0, d.WearPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("null")]
    public void Invalid_disk_json_is_an_empty_list(string json) => Assert.Empty(DiskHealthReader.Parse(json));

    private sealed class FakeRunner(string output, int exit = 0, string error = "") : IProcessRunner
    {
        public string? Script;
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, bool utf8 = false)
        {
            Script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1]));
            return new ProcessResult(exit, output, error);
        }
    }

    [Fact]
    public void Readers_only_use_read_cmdlets()
    {
        var runner = new FakeRunner("[]");
        Assert.Empty(DiskHealthReader.Read(runner));
        var disk = runner.Script!;
        _ = ThrottleReader.Read(runner, null);
        foreach (var script in new[] { disk, runner.Script! })
            foreach (var verb in new[] { "Set-", "Remove-", "New-", "Invoke-", "Start-Process", "Clear-", "Reset-", "Repair-", "Optimize-", "Initialize-", "Format-" })
                Assert.DoesNotContain(verb, script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MSFT_StorageReliabilityCounter", disk, StringComparison.Ordinal);
        Assert.Contains("Kernel-Processor-Power", runner.Script!, StringComparison.Ordinal);
        Assert.Contains("PercentPerformanceLimit", runner.Script!, StringComparison.Ordinal);
    }

    [Fact]
    public void Disk_reader_failure_without_output_throws_the_first_error_line()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DiskHealthReader.Read(new FakeRunner("", 1, "Get-CimInstance : Classe inválida\r\nmais")));
        Assert.Equal("Get-CimInstance : Classe inválida", ex.Message);
    }

    // ------------------------------------------------------------------ monitor

    [Fact]
    public void Monitor_below_max_refresh_is_attention_and_ok_otherwise()
    {
        var low = new DisplayCheckFacts(@"\\.\DISPLAY1", "AOC 24G2", 1920, 1080, 60, 144, "1920x1080@144", false, null);
        var list = PcChecks.Displays([low]);
        var f = One(list, PcChecks.DisplayRefreshPrefix + @"\\.\DISPLAY1");
        Assert.Equal(DiagnosticSeverity.Attention, f.Severity);
        Assert.Contains("144", f.Title, StringComparison.Ordinal);
        Assert.Contains("15 s", f.Owner!.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain(list, x => x.Id == PcChecks.DisplayOkId);

        var ok = PcChecks.Displays([low with { CurrentHz = 144, MaxHz = 144, BestModeKey = null }]);
        Assert.Equal(DiagnosticSeverity.Ok, One(ok, PcChecks.DisplayOkId).Severity);
        Assert.Empty(PcChecks.Displays([]));
    }

    [Fact]
    public void Hdr_available_and_off_is_only_info()
    {
        var m = new DisplayCheckFacts(@"\\.\DISPLAY2", "LG", 3840, 2160, 120, 120, null, true, false);
        var list = PcChecks.Displays([m]);
        Assert.Equal(DiagnosticSeverity.Info, One(list, PcChecks.DisplayHdrPrefix + @"\\.\DISPLAY2").Severity);
        Assert.Contains(list, x => x.Id == PcChecks.DisplayOkId);
        Assert.DoesNotContain(PcChecks.Displays([m with { HdrEnabled = true }]), x => x.Id.StartsWith(PcChecks.DisplayHdrPrefix, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ limitação pelo firmware

    [Fact]
    public void Throttle_events_explain_neutrally_without_promising_anything()
    {
        var f = PcChecks.Throttle(new ThrottleFacts(5, DateTimeOffset.Now.AddHours(-2), 100))!;
        Assert.Equal(DiagnosticSeverity.Attention, f.Severity);
        Assert.Equal(PcChecks.ThrottleId, f.Id);
        Assert.Contains("temperatura, energia ou limite do fabricante", f.Owner!.Explanation, StringComparison.Ordinal);
        Assert.Contains("5 evento", f.Evidence, StringComparison.Ordinal);
        Assert.Equal(PcChecks.ThrottleSource.Url, f.Owner.SourceUrl);
    }

    [Theory]
    [InlineData(0, 100.0, DiagnosticSeverity.Ok)]
    [InlineData(1, 100.0, DiagnosticSeverity.Info)]
    [InlineData(0, 70.0, DiagnosticSeverity.Attention)]
    [InlineData(0, 85.0, DiagnosticSeverity.Info)]
    public void Throttle_severity_depends_on_events_and_current_limit(int events, double limit, DiagnosticSeverity sev) =>
        Assert.Equal(sev, PcChecks.Throttle(new ThrottleFacts(events, null, limit))!.Severity);

    [Fact]
    public void Throttle_on_battery_is_expected_so_only_info_and_unread_is_nothing()
    {
        Assert.Equal(DiagnosticSeverity.Info, PcChecks.Throttle(new ThrottleFacts(10, null, 60, OnBattery: true))!.Severity);
        Assert.Null(PcChecks.Throttle(new ThrottleFacts(null, null, null)));
    }

    [Fact]
    public void Throttle_json_is_parsed()
    {
        var t = ThrottleReader.Parse("{\"Count\":3,\"Last\":\"2026-11-01T22:10:00.0000000Z\",\"Limit\":87}", false);
        Assert.Equal(3, t.Events37);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 22, 10, 0, TimeSpan.Zero), t.LastEvent);
        Assert.Equal(87, t.PerformanceLimitPercent);
        Assert.False(t.OnBattery);
        var noEvents = ThrottleReader.Parse("{\"Count\":0,\"Limit\":100}", null);
        Assert.Equal(DiagnosticSeverity.Ok, PcChecks.Throttle(noEvents)!.Severity);
        var failed = ThrottleReader.Parse("{\"EventError\":\"x\"}", null);
        Assert.Null(failed.Events37);
        Assert.Null(ThrottleReader.Parse("lixo", null).Events37);
    }

    // ------------------------------------------------------------------ notebook na bateria

    [Fact]
    public void Battery_notice_only_when_enabled_on_battery_and_not_repeated_too_soon()
    {
        var now = DateTimeOffset.Now;
        Assert.True(PcChecks.ShouldNotifyBattery(true, true, true, null, now));
        Assert.True(PcChecks.ShouldNotifyBattery(true, null, true, now - TimeSpan.FromHours(1), now));
        Assert.False(PcChecks.ShouldNotifyBattery(false, true, true, null, now));       // desligado em Ajustes
        Assert.False(PcChecks.ShouldNotifyBattery(true, true, false, null, now));       // na tomada
        Assert.False(PcChecks.ShouldNotifyBattery(true, true, null, null, now));        // não deu para ler
        Assert.False(PcChecks.ShouldNotifyBattery(true, false, true, null, now));       // PC sem bateria
        Assert.False(PcChecks.ShouldNotifyBattery(true, true, true, now - TimeSpan.FromMinutes(5), now));
    }

    // ------------------------------------------------------------------ geral

    [Fact]
    public void Pc_check_ids_are_recognized_and_sources_are_openable()
    {
        foreach (var id in new[] { PcChecks.LinkId, PcChecks.BufferbloatId, "diskhealth.0", PcChecks.GamesOnSlowDiskId, @"display.refresh.\\.\DISPLAY1", PcChecks.ThrottleId })
            Assert.True(PcChecks.IsPcCheckId(id), id);
        foreach (var id in new[] { "disk.system-free", "cpu.power-plan", "owner.xmp", "mem.channels" })
            Assert.False(PcChecks.IsPcCheckId(id), id);
        foreach (var url in PcChecks.SourceUrls)
        {
            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.Contains(url, OwnerChecks.SourceUrls);
        }
    }
}
