using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexus.Broker;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.Desktop.Views.Controls;
using Nexus.GameProfiles.Apex;
using Nexus.GameProfiles.CallOfDuty;
using Nexus.GameProfiles.Cards;
using Nexus.GameProfiles.Configs;
using Nexus.GameProfiles.Fortnite;
using Nexus.GameProfiles.LeagueOfLegends;
using Nexus.GameProfiles.Overwatch2;
using Nexus.GameProfiles.Valorant;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;

namespace Nexus.Tests.Modules;

/// <summary>
/// Cartões de jogo (GameCardRegistry): contrato de cada cartão (ids, selo sem marca, chaves de texto nos três idiomas, doc com
/// fontes), leituras de estado coerentes com a lista fechada e o catálogo, planos da tela Jogos, detecção pelo executável e
/// pelos "Programas instalados", e os arquivos de exemplo reais do Overwatch 2 e do Apex Legends.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameCardTests
{
    private static readonly string[] Expected = [FortniteCard.Id, ValorantCard.Id, CallOfDutyCard.Id, LeagueCard.Id, Overwatch2Card.Id, ApexCard.Id];

    private static HashSet<string> ResxKeys(string file) =>
        XDocument.Load(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "Localization", file)).Root!.Elements("data")
            .Select(d => d.Attribute("name")!.Value).ToHashSet(StringComparer.Ordinal);

    // ------------------------------------------------------------------ contrato

    [Fact]
    public void Registry_finds_the_six_cards_in_order_with_unique_ids_and_executables()
    {
        var all = GameCardRegistry.All;
        foreach (var id in Expected) Assert.NotNull(GameCardRegistry.Find(id));
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        var exes = all.SelectMany(c => c.Executables).ToList();
        Assert.Equal(exes.Count, exes.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // um executável pertence a um cartão só
        // Ordem: Fortnite antes dos da Riot, Overwatch e Apex depois (popularidade aproximada).
        Assert.True(all.ToList().FindIndex(c => c.Id == FortniteCard.Id) < all.ToList().FindIndex(c => c.Id == ValorantCard.Id));
        Assert.True(all.ToList().FindIndex(c => c.Id == ValorantCard.Id) < all.ToList().FindIndex(c => c.Id == ApexCard.Id));
    }

    [Fact]
    public void Every_card_has_a_brand_free_badge_translated_texts_and_a_sources_document()
    {
        var pt = ResxKeys("Strings.resx");
        var en = ResxKeys("Strings.en.resx");
        var es = ResxKeys("Strings.es.resx");
        foreach (var card in GameCardRegistry.All)
        {
            Assert.Matches("^[a-z0-9]+$", card.Id);
            Assert.InRange(card.Monogram.Length, 1, 3);
            Assert.NotNull(DesignMath.ParseHexColor(card.AccentHex)); // #RRGGBB válido
            Assert.NotEmpty(card.Executables);
            Assert.All(card.Executables, e => Assert.EndsWith(".exe", e, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(Wiring.Root, card.SourcesDocPath)), $"{card.Id}: falta {card.SourcesDocPath}");
            var doc = File.ReadAllText(Path.Combine(Wiring.Root, card.SourcesDocPath));
            Assert.Contains("http", doc, StringComparison.Ordinal); // fonte citada
            var keys = card.Options.SelectMany(o => new[] { o.TitleKey, o.DetailKey }).ToList();
            if (card.CloseFirstNoteKey is { } note) keys.Add(note);
            foreach (var key in keys)
            {
                Assert.True(pt.Contains(key), $"{card.Id}: '{key}' não existe em Strings.resx");
                Assert.True(en.Contains(key) && es.Contains(key), $"{card.Id}: '{key}' sem tradução en/es");
            }
            Assert.Equal(card.Options.Count, card.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count());
            // Cartão com opções de arquivo avisa "feche o jogo antes"; cartão só com Medir/Comparar não precisa.
            if (card.Options.Count > 0) Assert.NotNull(card.CloseFirstNoteKey);
        }
    }

    [Fact]
    public void Riot_and_activision_games_have_no_file_options_only_measure_and_compare()
    {
        foreach (var id in new[] { ValorantCard.Id, LeagueCard.Id, CallOfDutyCard.Id })
        {
            var card = GameCardRegistry.Find(id)!;
            Assert.Empty(card.Options);
            Assert.Null(card.CloseFirstNoteKey);
            // A decisão (anticheat de kernel / arquivo sincronizado na conta / arquivo que muda a cada título) está escrita na doc.
            var doc = File.ReadAllText(Path.Combine(Wiring.Root, card.SourcesDocPath));
            Assert.Contains("Medir", doc, StringComparison.Ordinal);
        }
        // E a lista fechada do handler continua sem eles (mesma decisão de GameConfigTests).
        Assert.DoesNotContain(GameConfigs.All, g => g.Id is "valorant" or "lol" or "cod");
    }

    // ------------------------------------------------------------------ leituras e catálogo

    [Fact]
    public void State_readers_expect_exactly_what_the_catalog_actions_write()
    {
        foreach (var (id, reader) in new (string, GameConfigCardReader)[] { (Overwatch2Card.Id, new Overwatch2StateReader()), (ApexCard.Id, new ApexStateReader()) })
        {
            Assert.Same(GameCardStateRegistry.Find(id)!.GetType(), reader.GetType());
            var card = GameCardRegistry.Find(id)!;
            foreach (var option in card.Options)
            {
                // Cada ação da opção grava (game, key, valor) no catálogo; o leitor espera exatamente isso.
                var expected = new[] { option.ActionKey }.Concat(option.ExtraActionKeys ?? []).Select(k =>
                {
                    var e = Wiring.Entry(k)!;
                    Assert.Equal(GameConfigs.HandlerKind, e.Definition.HandlerKind);
                    return new ConfigExpectation(e.TargetTemplate["game"], e.TargetTemplate["key"], e.DesiredValue!);
                }).ToHashSet();
                var declared = Expectations(reader, option.Id);
                Assert.Equal(expected, declared.ToHashSet());
            }
        }
        Assert.Null(GameCardStateRegistry.Find(ValorantCard.Id)); // sem opções, sem leitor
        Assert.Throws<InvalidOperationException>(() => Overwatch2StateReader.Expect("NaoExiste"));
    }

    private static IReadOnlyList<ConfigExpectation> Expectations(GameConfigCardReader reader, string optionId)
    {
        var options = (IReadOnlyDictionary<string, IReadOnlyList<ConfigExpectation>>)reader.GetType()
            .GetProperty("Options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(reader)!;
        return options[optionId];
    }

    [Fact]
    public void Fortnite_card_keeps_the_same_actions_as_the_screen_plans()
    {
        var card = GameCardRegistry.Find(FortniteCard.Id)!;
        Assert.Equal([FortniteCard.PerfOption, FortniteCard.LatencyOption, FortniteCard.ClearOption, FortniteCard.LowFxOption], card.Options.Select(o => o.Id));
        static HashSet<string> Keys(GameCardOption o) => [o.ActionKey, .. o.ExtraActionKeys ?? []];
        Assert.Equal(GameOptionPlans.FortnitePerformance().Requests.Select(r => r.Request.ActionId).ToHashSet(), Keys(card.Options[0]));
        Assert.Equal(GameOptionPlans.FortniteLatency(false).Requests.Select(r => r.Request.ActionId).ToHashSet(), Keys(card.Options[1]));
        Assert.Equal(FortniteSettings.FpsUnlimited, card.Options[1].Value); // nunca limita
        Assert.Equal(GameOptionPlans.FortniteClear().Requests.Select(r => r.Request.ActionId).ToHashSet(), Keys(card.Options[2]));
        Assert.Equal(GameOptionPlans.FortniteLowFx().Requests.Select(r => r.Request.ActionId).ToHashSet(), Keys(card.Options[3]));
        Assert.Equal("Card.Fn.Note", card.CloseFirstNoteKey);
        Assert.Contains(FortniteSettings.GameExe, card.Executables);
    }

    // ------------------------------------------------------------------ planos e estado

    [Fact]
    public void Card_option_plan_sends_every_action_as_user_and_reads_the_real_file()
    {
        var card = GameCardRegistry.Find(Overwatch2Card.Id)!;
        var fps = card.Options.Single(o => o.Id == Overwatch2Card.FpsOption);
        var profile = Directory.CreateTempSubdirectory("nexus-ow2-").FullName;
        try
        {
            var plan = GameCardPlans.For(card, fps, () => new GameCardStateContext(profile));
            Assert.Equal("ow2|fps", plan.Id);
            Assert.Equal([Overwatch2Card.FrameRateCapAction, Overwatch2Card.CustomFrameRatesAction, Overwatch2Card.LimitToRefreshOffAction], plan.Requests.Select(r => r.Request.ActionId));
            Assert.All(plan.Requests, r => Assert.Equal(BrokerLevel.User, r.Level));
            Assert.Equal(3, plan.Parts.Count);
            var source = new FakeSource();
            Assert.Equal(OptionState.Unknown, plan.Read(source)); // arquivo ausente: sem leitura, nunca "desligado"

            var path = Path.Combine(profile, "Documents", "Overwatch", "Settings", "Settings_v0.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Ow2Ini);
            Assert.Equal(OptionState.Off, plan.Read(source)); // amostra de fábrica
            var doc = ConfigLines.Parse(Ow2Ini, ConfigSyntax.Equals);
            doc.Set("FrameRateCap", "600"); doc.Set("UseCustomFrameRates", "1"); doc.Set("LimitToRefresh", "0");
            File.WriteAllText(path, doc.ToString());
            Assert.Equal(OptionState.On, plan.Read(source));
            // Reflex (placa NVIDIA) continua desligado no mesmo arquivo; "sharp" idem.
            Assert.Equal(OptionState.Off, GameCardPlans.For(card, card.Options.Single(o => o.Id == Overwatch2Card.ReflexOption), () => new GameCardStateContext(profile)).Read(source));
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    private sealed class FakeSource : IGameStateSource
    {
        public INvidiaDrsReader Nvidia => throw new NotSupportedException();
        public int? WindowedToken(string exePath) => null;
        public FortniteState Fortnite() => FortniteState.NotFound;
    }

    [Fact]
    public void Screen_finds_the_card_by_the_library_executable_or_by_what_it_launches()
    {
        Assert.Equal(ValorantCard.Id, GameCardLookup.ForExe("VALORANT.exe")?.Id);
        Assert.Equal(ValorantCard.Id, GameCardLookup.ForExe("VALORANT-Win64-Shipping.exe")?.Id);
        Assert.Equal(LeagueCard.Id, GameCardLookup.ForExe("LeagueClient.exe")?.Id);
        Assert.Equal(CallOfDutyCard.Id, GameCardLookup.ForExe("cod.exe")?.Id);
        Assert.Equal(Overwatch2Card.Id, GameCardLookup.ForExe("Overwatch Launcher.exe")?.Id);
        Assert.Equal(ApexCard.Id, GameCardLookup.ForExe("r5apex_launcher.exe")?.Id);
        Assert.Equal(FortniteCard.Id, GameCardLookup.ForExe("FortniteClient-Win64-Shipping_EAC_EOS.exe")?.Id);
        Assert.Null(GameCardLookup.ForExe("cs2.exe"));
        Assert.Null(GameCardLookup.ForExe(""));
        Assert.Equal(["Overwatch", "Overwatch Launcher"], GameCardLookup.ProcessNames(GameCardRegistry.Find(Overwatch2Card.Id)!));
    }

    [Fact]
    public void Audience_rules_drive_the_recommendation_of_card_options()
    {
        var ow2 = GameCardRegistry.Find(Overwatch2Card.Id)!;
        Assert.Equal(GameCardAudience.Nvidia, ow2.Options.Single(o => o.Id == "reflex").Audience);
        Assert.Equal(GameCardAudience.StrongGpu, ow2.Options.Single(o => o.Id == "sharp").Audience);
        var apex = GameCardRegistry.Find(ApexCard.Id)!;
        Assert.Equal(GameCardAudience.WeakGpu, apex.Options.Single(o => o.Id == "lowfx").Audience);
        var amdWeak = new Nexus.Assistant.HardwareProfile(Nexus.Assistant.GpuClass.Entry, Nexus.Assistant.GpuMaker.Amd, "RX 6400", 4, "teste", 16, false, null, true, false);
        Assert.True(Nexus.Assistant.HardwareRules.ForAudience(amdWeak, GameCardAudience.Nvidia).IsNotRecommended);
        Assert.True(Nexus.Assistant.HardwareRules.ForAudience(amdWeak, GameCardAudience.WeakGpu).IsRecommended);
    }

    // ------------------------------------------------------------------ detecção (Battle.net, EA app)

    [Fact]
    public void Installed_programs_give_overwatch_cod_and_apex_only_when_the_executable_exists()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe",
            @"D:\Jogos\Call of Duty\_retail_\cod.exe",
            @"C:\Program Files\EA Games\Apex\r5apex.exe",
        };
        var programs = new (string, string?)[]
        {
            ("Overwatch", @"C:\Program Files (x86)\Overwatch"),
            ("Call of Duty", @"D:\Jogos\Call of Duty\"),
            ("Apex Legends", @"C:\Program Files\EA Games\Apex"),
            ("Apex Legends", @"C:\Program Files\EA Games\Apex"), // repetido (64 bits + WOW6432Node): um só
            ("Overwatch", null),                                 // sem pasta: ignorado
            ("Battle.net", @"C:\Program Files (x86)\Battle.net"), // não é jogo da lista
            ("Hearthstone", @"C:\Program Files (x86)\Hearthstone"),
            ("Overwatch", @"\\servidor\jogos\Overwatch"),         // caminho de rede: recusado
        };
        var games = GameLibraries.FromInstalledPrograms(programs, files.Contains);
        Assert.Equal(["Overwatch 2", "Call of Duty", "Apex Legends"], games.Select(g => g.Name));
        Assert.Equal(["Battle.net", "Battle.net", "EA app"], games.Select(g => g.Store));
        Assert.All(games, g => Assert.True(g.ExePathSure));
        Assert.Equal(@"D:\Jogos\Call of Duty\_retail_\cod.exe", games[1].ExePath);
        // Executável que não existe na pasta: nada é adivinhado.
        Assert.Empty(GameLibraries.FromInstalledPrograms([("Overwatch", @"C:\Outra")], _ => false));
        // Cada jogo detectado tem cartão pelo executável.
        foreach (var g in games) Assert.NotNull(GameCardLookup.ForExe(g.ExePath!.Split('\\')[^1])); // caminho do Windows, teste roda no Linux
    }

    // ------------------------------------------------------------------ arquivos de exemplo reais

    /// <summary>Settings_v0.ini real (Overwatch; estrutura publicada em gist.github.com/sdaugherty) com as chaves que o Overwatch 2 grava ao sair do padrão.</summary>
    private const string Ow2Ini = "[Cinematics.1]\r\nShowIntro = \"0\"\r\n\r\n[GPU.6]\r\nGPUDeviceID = \"7297\"\r\nGPUName = \"NVIDIA GeForce GTX 1050\"\r\n\r\n[Render.13]\r\n" +
        "AADetail = \"0\"\r\nDynamicRenderScale = \"1\"\r\nFrameRateCap = \"300\"\r\nFullScreenRefresh = \"144\"\r\nGFXPresetLevel = \"1\"\r\nLimitToRefresh = \"1\"\r\n" +
        "ReflexMode = \"1\"\r\nRenderScale = \"-2\"\r\nShowFPSCounter = \"1\"\r\nUseCustomFrameRates = \"0\"\r\nWindowMode = \"2\"\r\n\r\n[Sound.3]\r\nMusicVolume = \"10.000000\"\r\n";

    /// <summary>videoconfig.txt real do Apex Legends (KeyValues da Respawn, tabulações e chaves "setting.*").</summary>
    private const string ApexCfg = "\"VideoConfig\"\n{\n\t\"setting.cl_gib_allow\"\t\t\"0\"\n\t\"setting.cl_particle_fallback_base\"\t\t\"3\"\n\t\"setting.mat_vsync_mode\"\t\t\"1\"\n" +
        "\t\"setting.ssao_enabled\"\t\t\"1\"\n\t\"setting.volumetric_lighting\"\t\t\"1\"\n\t\"setting.mat_depthfeather_enable\"\t\t\"1\"\n\t\"setting.csm_enabled\"\t\t\"1\"\n" +
        "\t\"setting.defaultres\"\t\t\"1920\"\n\t\"setting.defaultresheight\"\t\t\"1080\"\n\t\"setting.fullscreen\"\t\t\"1\"\n}\n";

    [Fact]
    public void Overwatch_sample_reads_every_card_key_and_round_trips_byte_for_byte()
    {
        var doc = ConfigLines.Parse(Ow2Ini, ConfigSyntax.Equals);
        Assert.Equal(Ow2Ini, doc.ToString());
        foreach (var read in Expectations(new Overwatch2StateReader(), Overwatch2Card.FpsOption).Concat(Expectations(new Overwatch2StateReader(), Overwatch2Card.ReflexOption)).Concat(Expectations(new Overwatch2StateReader(), Overwatch2Card.SharpOption)))
        {
            var (state, value) = doc.Get(read.Key);
            Assert.Equal(ConfigKeyState.Present, state);
            Assert.NotEqual(read.Desired, value); // amostra "de fábrica": tudo ainda desligado
            Assert.True(doc.Set(read.Key, read.Desired));
            Assert.Equal((ConfigKeyState.Present, read.Desired), doc.Get(read.Key));
        }
        var text = doc.ToString();
        Assert.Contains("FrameRateCap = \"600\"\r\n", text, StringComparison.Ordinal); // aspas e espaços como o jogo grava
        Assert.Contains("ReflexMode = \"2\"\r\n", text, StringComparison.Ordinal);
        Assert.Equal(Ow2Ini.Split("\r\n").Length, text.Split("\r\n").Length);
        // Cabeçalhos de seção e chaves de outras seções intactos.
        Assert.Contains("[Render.13]\r\n", text, StringComparison.Ordinal);
        Assert.Contains("MusicVolume = \"10.000000\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Apex_sample_reads_every_card_key_and_round_trips_byte_for_byte()
    {
        var doc = ConfigLines.Parse(ApexCfg, ConfigSyntax.KeyValues);
        Assert.Equal(ApexCfg, doc.ToString());
        foreach (var read in Expectations(new ApexStateReader(), ApexCard.VSyncOption).Concat(Expectations(new ApexStateReader(), ApexCard.LowFxOption)))
        {
            Assert.Equal((ConfigKeyState.Present, "1"), doc.Get(read.Key));
            Assert.True(doc.Set(read.Key, read.Desired));
        }
        var text = doc.ToString();
        Assert.Contains("\t\"setting.mat_vsync_mode\"\t\t\"0\"\n", text, StringComparison.Ordinal);
        Assert.Contains("\t\"setting.csm_enabled\"\t\t\"0\"\n", text, StringComparison.Ordinal);
        Assert.Contains("\t\"setting.defaultres\"\t\t\"1920\"\n", text, StringComparison.Ordinal); // o resto fica
        Assert.Equal(ApexCfg.Split('\n').Length, text.Split('\n').Length);
    }

    [Fact]
    public void Badge_parses_only_full_hex_colors()
    {
        Assert.Equal(((byte)0x6E, (byte)0x8B, (byte)0xD9), DesignMath.ParseHexColor("#6E8BD9"));
        Assert.Null(DesignMath.ParseHexColor(null));
        Assert.Null(DesignMath.ParseHexColor("6E8BD9"));
        Assert.Null(DesignMath.ParseHexColor("#6E8"));
        Assert.Null(DesignMath.ParseHexColor("#GG8BD9"));
    }

    [Fact]
    public void Design_system_manual_and_games_doc_mention_the_cards()
    {
        var manual = File.ReadAllText(Path.Combine(Wiring.Root, "docs", "10-design-system.md"));
        Assert.Contains("GameBadge", manual, StringComparison.Ordinal);
        var xaml = File.ReadAllText(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "Views", "Pages", "Simple.xaml"));
        Assert.Contains("c:GameBadge", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"Illo\.(Fortnite|Valorant|Apex|Overwatch)"), xaml); // nenhuma arte de marca
    }
}
