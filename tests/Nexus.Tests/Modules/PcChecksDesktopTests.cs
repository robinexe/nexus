using Nexus.Desktop.ViewModels.Simple;
using Nexus.Desktop.Views.Shell;
using Nexus.HardwareDiscovery;
using Nexus.SystemTools.Checks;

namespace Nexus.Tests.Modules;

/// <summary>
/// Checagens do PC na interface simples: frases com os números da leitura (nos três idiomas), assunto, impacto, cartões da tela
/// Início, a página de cada achado e o coordenador com leitores falsos (leitura automática, teste de rede do usuário, aviso de bateria).
/// </summary>
[Trait("Category", "Unit")]
[Collection(LocalizationState.Name)]
public sealed class PcChecksDesktopTests
{
    public PcChecksDesktopTests() => Loc.Use("pt-BR");

    private static PcCheckState Bad() => new()
    {
        Network = new NetworkCheckFacts(LinkKind.WiFi, "Wi-Fi", 35, 866,
            PingStats.From("192.168.0.1", [2, 40, 3, 45, 2, 50]),
            PingStats.From("1.1.1.1", [20, 21, null, 22, 20, null, 21, 20, 20, 21]),
            new BufferbloatResult(20, 180, 60, 300, 30, 80_000_000)),
        Disks =
        [
            new DiskHealthFacts(0, "Samsung 980", DiskMedia.Ssd, "NVMe", "Healthy", ["OK"], 75, null, 3, 0, 0, 900, true),
            new DiskHealthFacts(1, "WDC WD10EZEX", DiskMedia.Hdd, "SATA", "Unhealthy", ["OK"], 40, null, null, 5, 0, 30000, true),
            new DiskHealthFacts(2, "Crucial MX500", DiskMedia.Ssd, "SATA", "Healthy", ["OK"], 35, null, 95, 0, 0, 100, true),
        ],
        Games = [new GameOnDisk("Cyberpunk 2077", @"D:\Games\Cyberpunk", 1), new GameOnDisk("Starfield", @"D:\Games\Starfield", 1)],
        Displays = [new DisplayCheckFacts(@"\\.\DISPLAY1", "AOC 24G2", 1920, 1080, 60, 144, "1920x1080@144", false, null)],
        Throttle = new ThrottleFacts(6, DateTimeOffset.Now, 70),
    };

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en")]
    [InlineData("es")]
    public void Every_attention_finding_gets_a_plain_sentence_in_every_language(string lang)
    {
        Loc.Use(lang);
        try
        {
            var state = Bad();
            var attention = state.Findings().Where(f => f.Severity == DiagnosticSeverity.Attention).ToList();
            Assert.True(attention.Count >= 9, string.Join(", ", attention.Select(f => f.Id)));
            var plain = PcCheckText.Plain(state);
            foreach (var f in attention)
            {
                Assert.True(plain.TryGetValue(f.Id, out var p), f.Id);
                Assert.False(string.IsNullOrWhiteSpace(p!.Title));
                Assert.False(string.IsNullOrWhiteSpace(p.WhatToDo));
                Assert.DoesNotContain("{", p.Title, StringComparison.Ordinal);
                Assert.DoesNotContain("MSFT_", p.Title + p.WhatToDo, StringComparison.Ordinal); // nada técnico na frase simples
            }
        }
        finally { Loc.Use("pt-BR"); }
    }

    [Fact]
    public void Plain_sentences_carry_the_numbers_of_the_reading()
    {
        var plain = PcCheckText.Plain(Bad());
        Assert.Equal("O monitor está a 60 Hz, mas aceita 144 Hz", plain[PcChecks.DisplayRefreshPrefix + @"\\.\DISPLAY1"].Title);
        Assert.Contains("Usar 144 Hz", plain[PcChecks.DisplayRefreshPrefix + @"\\.\DISPLAY1"].WhatToDo, StringComparison.Ordinal);
        Assert.Equal("Disco 1 (WDC WD10EZEX) pode estar falhando", plain["diskhealth.1"].Title);
        Assert.Equal("Disco 0 (Samsung 980) está esquentando (75 °C)", plain["diskhealth.0"].Title);
        Assert.Equal("Disco 2 (Crucial MX500) está perto do fim da vida útil", plain["diskhealth.2"].Title);
        Assert.Equal("2 jogos estão num HD e carregam bem mais devagar", plain[PcChecks.GamesOnSlowDiskId].Title);
        Assert.Equal("Parte dos pacotes se perde até a internet (20%)", plain[PcChecks.InternetId].Title);
        Assert.Equal("O ping sobe 160 ms quando a internet está cheia", plain[PcChecks.BufferbloatId].Title);
        Assert.Contains("SQM", plain[PcChecks.BufferbloatId].WhatToDo, StringComparison.Ordinal);
        Assert.Equal("Wi-Fi com sinal fraco (35%)", plain[PcChecks.LinkId].Title);
        Assert.Contains("temperatura, energia ou limite do fabricante", plain[PcChecks.ThrottleId].WhatToDo, StringComparison.Ordinal);
        Assert.Equal("Usar 144 Hz", PcCheckText.UseHz(144));
    }

    [Fact]
    public void Jitter_sentence_when_there_is_no_loss()
    {
        var s = new PcCheckState { Network = new NetworkCheckFacts(LinkKind.Wired, Internet: PingStats.From("srv", [20, 60, 20, 60, 20, 60])) };
        Assert.Equal("O ping varia muito até a internet (±40 ms)", PcCheckText.Plain(s)[PcChecks.InternetId].Title);
    }

    [Fact]
    public void Not_informed_values_are_never_zero()
    {
        Assert.Equal("não informado", PcCheckText.OrNotInformed<int>(null, "{0} °C"));
        Assert.Equal("41 °C", PcCheckText.OrNotInformed<int>(41, "{0} °C"));
        Assert.Equal("Disco 3", PcCheckText.DiskName(new DiskHealthFacts(3, "", DiskMedia.Unknown, null, null, [])));
    }

    [Fact]
    public void Plain_map_wins_and_topics_and_impact_follow_the_check()
    {
        var state = Bad();
        var plain = PcCheckText.Plain(state);
        var display = state.Findings().First(f => f.Id.StartsWith(PcChecks.DisplayRefreshPrefix, StringComparison.Ordinal));
        Assert.Equal(plain[display.Id], PlainFindings.For(display, plain));
        Assert.Equal(display.Title, PlainFindings.For(display).Title); // sem o mapa: o título da biblioteca, como antes
        Assert.Equal("rede", PlainFindings.TopicOf(PcChecks.BufferbloatId));
        Assert.Equal("monitor", PlainFindings.TopicOf(display.Id));
        Assert.Equal("disco", PlainFindings.TopicOf("diskhealth.1"));
        Assert.Equal("energia", PlainFindings.TopicOf(PcChecks.ThrottleId));
        var failing = state.Findings().First(f => f.Id == "diskhealth.1");
        Assert.True(PlainFindings.ImpactOf(failing) > PlainFindings.ImpactOf(display));
        Assert.True(PlainFindings.ImpactOf(display) > PlainFindings.ImpactOf(state.Findings().First(f => f.Id == PcChecks.BufferbloatId)));
    }

    [Fact]
    public void Home_cards_put_a_failing_disk_first_and_offer_the_monitor()
    {
        var state = Bad();
        var inputs = new HealthInputs { Findings = state.Findings(), Plain = PcCheckText.Plain(state) };
        var top = ImproveCards.Top(inputs);
        Assert.Equal(ImproveKind.DiskHealth, top[0].Kind);
        Assert.Contains("pode estar falhando", top[0].Text, StringComparison.Ordinal);
        Assert.Equal(ImproveKind.Monitor, top[1].Kind);
        Assert.StartsWith(PcChecks.DisplayRefreshPrefix, top[1].FindingId, StringComparison.Ordinal);
        Assert.Equal(@"\\.\DISPLAY1", state.DisplayFor(top[1].FindingId!)!.Device);
        Assert.Null(state.DisplayFor("diskhealth.1"));
    }

    [Theory]
    [InlineData("net.bufferbloat", "SystemToolsPage")]
    [InlineData("net.link", "SystemToolsPage")]
    [InlineData("diskhealth.1", "DiskPage")]
    [InlineData("diskhealth.games", "DiskPage")]
    [InlineData(@"display.refresh.\\.\DISPLAY1", "GpuLabPage")]
    [InlineData("cpu.firmware-limit", "DiagnosticsPage")]
    [InlineData("disk.system-free", "CleaningPage")]
    public void Each_check_opens_its_advanced_page(string id, string page) => Assert.Equal(page, ShellMap.PageForFinding(id));

    // ------------------------------------------------------------------ coordenador com leitores falsos

    private sealed class FakeReaders : IPcCheckReaders
    {
        public bool FailDisks;
        public bool? Battery = false;
        public IReadOnlyList<DiskHealthFacts> Disks() => FailDisks ? throw new InvalidOperationException("sem CIM") :
            [new DiskHealthFacts(1, "HD", DiskMedia.Hdd, "SATA", "Healthy", ["OK"], 35)];
        public IReadOnlyList<(string Name, string? InstallDir)> Games() => [("Jogo", @"D:\Jogo")];
        public IReadOnlyList<DisplayCheckFacts> Displays() => [new DisplayCheckFacts(@"\\.\DISPLAY1", "M", 1920, 1080, 60, 165, "1920x1080@165", null, null)];
        public ThrottleFacts Throttle(bool? onBattery) => new(0, null, 100, onBattery);
        public bool? OnBattery() => Battery;
        public INetworkInfo Network { get; } = new Net();
        public IPingClient Ping { get; } = new Pinger();
        public ILoadSource NewLoad() => throw new NotSupportedException();

        private sealed class Net : INetworkInfo
        {
            public ActiveLink? Active() => new(LinkKind.Wired, "Eth", 1000, "192.168.0.1");
            public int? WifiSignalPercent() => null;
        }

        private sealed class Pinger : IPingClient
        {
            public Task<double?> PingAsync(string host, int timeoutMs, CancellationToken cancellationToken) => Task.FromResult<double?>(1);
        }
    }

    private static readonly PhysicalDiskFacts[] Physical =
        [new(0, "NVMe", DiskMedia.Ssd, "NVMe", null, "", [@"C:\"]), new(1, "HD", DiskMedia.Hdd, "SATA", null, "", [@"D:\"])];

    [Fact]
    public async Task Automatic_reading_fills_every_check_and_places_games()
    {
        var c = new PcChecksCoordinator(new FakeReaders());
        var changed = 0;
        c.Changed += (_, _) => changed++;
        await c.RunAutomaticAsync(Physical);
        Assert.Equal(1, changed);
        var s = c.State;
        Assert.Single(s.Disks!);
        Assert.Equal([("Jogo", 1)], s.Games.Select(g => (g.Name, g.DiskNumber)));
        Assert.Single(s.Displays!);
        Assert.Equal(0, s.Throttle!.Events37);
        Assert.Equal(LinkKind.Wired, s.Network!.Link);
        Assert.NotNull(s.Network.Gateway);
        Assert.Null(s.Network.Internet); // a leitura automática não sai de casa
        Assert.Contains(c.Findings, f => f.Id == PcChecks.GamesOnSlowDiskId && f.Severity == DiagnosticSeverity.Attention);
        Assert.Contains(c.Findings, f => f.Id.StartsWith(PcChecks.DisplayRefreshPrefix, StringComparison.Ordinal));
        Assert.True(c.Plain.ContainsKey(PcChecks.GamesOnSlowDiskId));
    }

    [Fact]
    public async Task A_failing_reader_is_skipped_and_the_users_network_test_is_kept()
    {
        var c = new PcChecksCoordinator(new FakeReaders { FailDisks = true });
        var mine = new NetworkCheckFacts(LinkKind.WiFi, "Wi-Fi", 80, Internet: PingStats.From("srv", [30, 31]));
        c.Update(s => s with { Network = mine });
        await c.RunAutomaticAsync(Physical);
        Assert.Null(c.State.Disks);
        Assert.Empty(c.State.Games);
        Assert.Same(mine, c.State.Network);
        Assert.NotNull(c.State.Displays);
    }

    [Fact]
    public void Battery_notice_fires_once_per_interval_and_respects_the_setting()
    {
        var readers = new FakeReaders { Battery = true };
        var c = new PcChecksCoordinator(readers);
        var notices = new List<(string Title, string Text)>();
        c.Notice += (_, n) => notices.Add(n);
        var now = DateTimeOffset.Now;
        Assert.False(c.OnGameStarted("CS2", enabled: false, now));
        Assert.True(c.OnGameStarted("CS2", enabled: true, now));
        Assert.False(c.OnGameStarted("CS2", enabled: true, now.AddMinutes(10)));
        Assert.True(c.OnGameStarted("Valorant", enabled: true, now.AddMinutes(40)));
        Assert.Equal(2, notices.Count);
        Assert.Equal("Notebook na bateria", notices[0].Title);
        Assert.Equal("CS2 — Na bateria o FPS cai bastante; ligue na tomada.", notices[0].Text);
        readers.Battery = false;
        Assert.False(c.OnGameStarted("X", true, now.AddHours(5)));
    }
}
