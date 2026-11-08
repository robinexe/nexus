using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexus.Core;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.GameProfiles.Cards;
using Nexus.GameProfiles.Configs;
using Nexus.GameProfiles.EaFc;
using Nexus.GameProfiles.GtaV;
using Nexus.GameProfiles.Minecraft;
using Nexus.GameProfiles.Roblox;
using Nexus.GameProfiles.RocketLeague;

namespace Nexus.Tests.Modules;

/// <summary>
/// Cartões Minecraft (Java/Bedrock), Roblox, GTA V (Legacy/Enhanced), Rocket League e EA FC: parsers XML sem perdas (settings.xml
/// do GTA V, GlobalBasicSettings_13.xml do Roblox), lista fechada por reflexão, catálogo batendo com as constantes, textos nos três
/// idiomas, leitura de estado real e ponte para a tela Jogos. Jogos sem mecanismo seguro (Rocket League, EA FC) só com mitos.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class GameCardsSetBTests : IDisposable
{
    private readonly string _profile = Directory.CreateTempSubdirectory("nexus-cards-b-").FullName;
    private bool _running;

    public void Dispose()
    {
        Directory.Delete(_profile, recursive: true);
        GC.SuppressFinalize(this);
    }

    // Trechos reais (estrutura) dos arquivos de cada jogo.
    private const string GtaSettings = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Settings>\r\n<version value=\"27\" />\r\n<configSource>SMC_AUTO</configSource>\r\n<graphics>\r\n<Tessellation value=\"3\" />\r\n<LodScale value=\"1.000000\" />\r\n</graphics>\r\n<video>\r\n<AdapterIndex value=\"0\" />\r\n<ScreenWidth value=\"1920\" />\r\n<RefreshRate value=\"60\" />\r\n<Windowed value=\"0\" />\r\n<VSync value=\"1\" />\r\n<PauseOnFocusLoss value=\"1\" />\r\n<AspectRatio value=\"0\" />\r\n</video>\r\n</Settings>\r\n";
    private const string RobloxSettingsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<roblox xmlns:xmime=\"http://www.w3.org/2005/05/xmlmime\" version=\"4\">\r\n\t<Item class=\"UserGameSettings\" referent=\"RBX0\">\r\n\t\t<Properties>\r\n\t\t\t<bool name=\"AllTutorialsDisabled\">false</bool>\r\n\t\t\t<int name=\"FramerateCap\">60</int>\r\n\t\t\t<float name=\"GamepadCameraSensitivity\">1</float>\r\n\t\t\t<int name=\"GraphicsQualityLevel\">10</int>\r\n\t\t\t<string name=\"VRComfortSetting\">Comfort</string>\r\n\t\t</Properties>\r\n\t</Item>\r\n</roblox>\r\n";
    private const string Bedrock = "ctrl_sensitivity:0.5000\ngfx_vsync:1\ngfx_viewdistance:256.0000\ngfx_fancygraphics:1\n";
    private const string Java = "version:3955\r\nenableVsync:true\r\nmaxFps:120\r\ngraphicsMode:1\r\nrenderDistance:12\r\nsimulationDistance:12\r\nrenderClouds:\"true\"\r\nparticles:0\r\nentityShadows:true\r\n";

    private string Write(string relative, string text)
    {
        var path = Path.Combine([_profile, .. relative.Split('\\')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        return path;
    }

    private GameConfigHandler Handler() => new(_ => _profile, _ => _running, otherAccount: false);

    private static ActionInstance Instance(string key)
    {
        var e = Wiring.Entry(key) ?? throw new InvalidOperationException(key + " não está no catálogo");
        return e.TryInstantiate(new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);
    }

    private GameCardStateContext Ctx => new(_profile);

    // ------------------------------------------------------------------ parsers XML

    [Fact]
    public void Xml_attribute_syntax_changes_only_the_value_and_keeps_every_other_byte()
    {
        var doc = ConfigLines.Parse(GtaSettings, ConfigSyntax.XmlAttribute);
        Assert.Equal(GtaSettings, doc.ToString());
        Assert.Equal((ConfigKeyState.Present, "1"), doc.Get("VSync"));
        Assert.Equal((ConfigKeyState.Present, "1"), doc.Get("pauseonfocusloss"));
        Assert.Equal(ConfigKeyState.Absent, doc.Get("configSource").State); // elemento sem atributo value não é chave
        Assert.Equal(ConfigKeyState.Absent, doc.Get("Settings").State);
        Assert.Equal(ConfigKeyState.Absent, doc.Get("video").State);
        Assert.True(doc.Set("VSync", "0"));
        Assert.Equal(GtaSettings.Replace("<VSync value=\"1\" />", "<VSync value=\"0\" />", StringComparison.Ordinal), doc.ToString());
        Assert.False(doc.Set("FrameLimit", "0")); // o GTA V não tem limitador; nada é acrescentado
        // O resultado continua XML válido.
        XDocument.Parse(doc.ToString());
    }

    [Fact]
    public void Xml_named_syntax_reads_name_attribute_and_keeps_indentation()
    {
        var doc = ConfigLines.Parse(RobloxSettingsXml, ConfigSyntax.XmlNamed);
        Assert.Equal(RobloxSettingsXml, doc.ToString());
        Assert.Equal((ConfigKeyState.Present, "60"), doc.Get("FramerateCap"));
        Assert.Equal((ConfigKeyState.Present, "Comfort"), doc.Get("VRComfortSetting"));
        Assert.Equal(ConfigKeyState.Absent, doc.Get("Item").State);       // bloco com filhos em outras linhas
        Assert.Equal(ConfigKeyState.Absent, doc.Get("Properties").State);
        Assert.Equal(ConfigKeyState.Absent, doc.Get("int").State);        // o nome da tag não é chave
        Assert.True(doc.Set("FramerateCap", "240"));
        Assert.Equal(RobloxSettingsXml.Replace("<int name=\"FramerateCap\">60</int>", "<int name=\"FramerateCap\">240</int>", StringComparison.Ordinal), doc.ToString());
        XDocument.Parse(doc.ToString());
        // Tag de fechamento diferente, vários elementos na linha ou entidade no valor: não é chave (o editor não mexe).
        Assert.Equal(ConfigKeyState.Absent, ConfigLines.Parse("<int name=\"A\">1</bool>\n", ConfigSyntax.XmlNamed).Get("A").State);
        Assert.Equal(ConfigKeyState.Absent, ConfigLines.Parse("<int name=\"A\">1</int><int name=\"B\">2</int>\n", ConfigSyntax.XmlNamed).Get("A").State);
        Assert.Equal(ConfigKeyState.Absent, ConfigLines.Parse("<string name=\"A\">a &amp; b</string>\n", ConfigSyntax.XmlNamed).Get("A").State);
    }

    [Fact]
    public void Values_that_would_break_xml_are_refused()
    {
        var doc = ConfigLines.Parse(GtaSettings, ConfigSyntax.XmlAttribute);
        Assert.Throws<ArgumentException>(() => doc.Set("VSync", "0\" /><Hack value=\"1"));
        Assert.Throws<ArgumentException>(() => doc.Set("VSync", "<0>"));
        Assert.Throws<ArgumentException>(() => doc.Set("VSync", "a&b"));
        Assert.False(ConfigLines.IsSafeValue("1<2"));
        Assert.True(ConfigLines.IsSafeValue("240"));
    }

    // ------------------------------------------------------------------ lista fechada e catálogo

    [Fact]
    public void Providers_register_the_new_games_without_editing_the_central_list()
    {
        foreach (var id in new[] { MinecraftSettings.BedrockId, RobloxSettings.Id, GtaVSettings.LegacyId, GtaVSettings.EnhancedId })
            Assert.NotNull(GameConfigs.Find(id));
        Assert.Equal(ConfigRoot.LocalAppData, GameConfigs.Find(RobloxSettings.Id)!.Root);
        Assert.Equal(ConfigSyntax.XmlNamed, GameConfigs.Find(RobloxSettings.Id)!.Syntax);
        Assert.Equal(ConfigSyntax.XmlAttribute, GameConfigs.Find(GtaVSettings.EnhancedId)!.Syntax);
        Assert.Equal(GameConfigs.All.Count, GameConfigs.All.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count());
        // Rocket League e EA FC não têm arquivo na lista: sem mecanismo seguro, sem ação.
        Assert.DoesNotContain(GameConfigs.All, g => g.Id.Contains("rocket", StringComparison.OrdinalIgnoreCase) || g.Id.Contains("fc", StringComparison.OrdinalIgnoreCase));
        // Diretriz: nunca limitar FPS. O Roblox só grava o máximo do menu; o Minecraft Java só "Ilimitado".
        Assert.Equal(["240"], GameConfigs.Find(RobloxSettings.Id)!.Find("FramerateCap")!.Values);
        Assert.Equal(["260"], GameConfigs.Find(MinecraftSettings.JavaId)!.Find("maxFps")!.Values);
        Assert.Single(Directory.GetFiles(Path.Combine(_profile), "*", SearchOption.AllDirectories).DefaultIfEmpty("x")); // pasta temporária vazia
    }

    [Fact]
    public void Every_card_action_exists_is_user_level_and_matches_the_closed_list()
    {
        var mine = new[] { MinecraftCard.Id, RobloxCard.Id, GtaVCard.Id, RocketLeagueCard.Id, EaFcCard.Id };
        foreach (var id in mine) Assert.NotNull(GameCardRegistry.Find(id));
        foreach (var card in mine.Select(GameCardRegistry.Find!))
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", card!.AccentHex);
            Assert.InRange(card.Monogram.Length, 1, 3);
            Assert.True(File.Exists(Path.Combine(Wiring.Root, card.SourcesDocPath)), card.SourcesDocPath + " não existe");
            Assert.All(card.Executables, e => Assert.EndsWith(".exe", e, StringComparison.Ordinal));
            Assert.Equal(card.Options.Count, card.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count());
            foreach (var option in card.Options)
            {
                foreach (var key in new[] { option.ActionKey }.Concat(option.ExtraActionKeys ?? []))
                {
                    var e = Wiring.Entry(key);
                    Assert.True(e is not null, $"{card.Id}/{option.Id}: {key} não está no catálogo");
                    Assert.Equal(PrivilegeLevel.User, e!.Definition.Privileges);
                    Assert.Equal(GameConfigs.HandlerKind, e.Definition.HandlerKind);
                    var t = e.TargetTemplate;
                    var k = GameConfigs.Find(t["game"])?.Find(t["key"]);
                    Assert.True(k is not null, $"{key}: {t["game"]}/{t["key"]} fora da lista fechada");
                    Assert.Contains(e.DesiredValue!, k!.Values);
                    Assert.NotEmpty(e.Definition.Evidence);
                    Assert.False(string.IsNullOrWhiteSpace(e.Definition.Risks));
                }
                if (option.Value is not null) Assert.Contains(option.Value, Wiring.Entry(option.ActionKey)!.Definition.AllowedValues ?? []);
            }
        }
        // Os jogos sem mecanismo seguro ficam só com Medir/Comparar e explicam o porquê em "rejected".
        Assert.Empty(GameCardRegistry.Find(RocketLeagueCard.Id)!.Options);
        Assert.Empty(GameCardRegistry.Find(EaFcCard.Id)!.Options);
        Assert.Contains(Wiring.Catalog.Rejected, r => r.Id == "myth.rocketleague.tasystemsettings-read-only" && r.Source!.Contains("epicgames.com", StringComparison.Ordinal));
        Assert.Contains(Wiring.Catalog.Rejected, r => r.Id == "myth.eafc.fcsetup-ini-tweaks");
        Assert.Contains(Wiring.Catalog.Rejected, r => r.Id == "myth.roblox.fastflags-clientappsettings" && r.Source!.Contains("devforum.roblox.com", StringComparison.Ordinal));
        Assert.Contains(Wiring.Catalog.Rejected, r => r.Id == "myth.minecraft.xmx-more-ram");
    }

    [Fact]
    public void Card_texts_exist_in_the_three_languages()
    {
        var loc = Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "Localization");
        var files = ResxFiles
            .Select(f => XDocument.Load(Path.Combine(loc, f)).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet(StringComparer.Ordinal)).ToList();
        foreach (var id in new[] { MinecraftCard.Id, RobloxCard.Id, GtaVCard.Id })
        {
            var card = GameCardRegistry.Find(id)!;
            var keys = card.Options.SelectMany(o => new[] { o.TitleKey, o.DetailKey }).Append(card.CloseFirstNoteKey!).ToList();
            foreach (var key in keys)
            {
                Assert.StartsWith("Card." + id + ".", key, StringComparison.Ordinal);
                foreach (var set in files) Assert.True(set.Contains(key), key + " falta em um resx");
            }
        }
    }

    // ------------------------------------------------------------------ aplicar/desfazer pelo handler genérico

    [Fact]
    public void Roblox_framerate_cap_applies_and_restores_byte_for_byte()
    {
        var path = Write(@"AppData\Local\Roblox\GlobalBasicSettings_13.xml", RobloxSettingsXml);
        var handler = Handler();
        var action = Instance(RobloxSettings.FpsMaxAction);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, T.Ctx).Kind);
        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(RobloxCard.Id, RobloxCard.OptionFpsMax, Ctx));
        var original = handler.Detect(action);
        Assert.Equal("60", original.Value);
        handler.Apply(action);
        Assert.Contains("<int name=\"FramerateCap\">240</int>", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(GameCardOptionState.On, GameCardStateRegistry.Read(RobloxCard.Id, RobloxCard.OptionFpsMax, Ctx));
        handler.Restore(action, original);
        Assert.Equal(RobloxSettingsXml, File.ReadAllText(path));
        // Roblox aberto: recusa.
        _running = true;
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(action, T.Ctx).Kind);
    }

    [Fact]
    public void Gta_legacy_and_enhanced_are_independent_files()
    {
        var legacy = Write(@"Documents\Rockstar Games\GTA V\settings.xml", GtaSettings);
        var handler = Handler();
        var vsync = Instance(GtaVSettings.LegacyVsyncOffAction);
        var enhanced = Instance(GtaVSettings.EnhancedVsyncOffAction);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(vsync, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.NotApplicable, handler.Assess(enhanced, T.Ctx).Kind); // Enhanced não instalado: nada muda
        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(GtaVCard.Id, GtaVCard.OptionVsyncOff, Ctx));
        var original = handler.Detect(vsync);
        handler.Apply(vsync);
        Assert.Contains("<VSync value=\"0\" />", File.ReadAllText(legacy), StringComparison.Ordinal);
        Assert.Equal(GameCardOptionState.On, GameCardStateRegistry.Read(GtaVCard.Id, GtaVCard.OptionVsyncOff, Ctx)); // só a edição instalada conta
        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(GtaVCard.Id, GtaVCard.OptionKeepRunning, Ctx));
        // Enhanced aparece com V-Sync ligado: o cartão deixa de estar "ligado" até as duas edições estarem iguais.
        Write(@"Documents\Rockstar Games\GTAV Enhanced\settings.xml", GtaSettings);
        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(GtaVCard.Id, GtaVCard.OptionVsyncOff, Ctx));
        handler.Apply(enhanced);
        Assert.Equal(GameCardOptionState.On, GameCardStateRegistry.Read(GtaVCard.Id, GtaVCard.OptionVsyncOff, Ctx));
        handler.Restore(vsync, original);
        Assert.Equal(GtaSettings, File.ReadAllText(legacy));
        Assert.False(Directory.Exists(Path.Combine(_profile, "OneDrive")));
    }

    [Fact]
    public void Minecraft_bedrock_lives_in_the_store_package_and_java_distances_are_menu_values()
    {
        var bedrock = Write(@"AppData\Local\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\LocalState\games\com.mojang\minecraftpe\options.txt", Bedrock);
        Write(@"AppData\Roaming\.minecraft\options.txt", Java);
        var handler = Handler();
        var vsync = Instance(MinecraftSettings.BedrockVsyncOffAction);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(vsync, T.Ctx).Kind);
        handler.Apply(vsync);
        Assert.Equal(Bedrock.Replace("gfx_vsync:1", "gfx_vsync:0", StringComparison.Ordinal), File.ReadAllText(bedrock));
        Assert.Equal(GameCardOptionState.On, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionBedrockVsync, Ctx));

        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionJavaNear, Ctx));
        handler.Apply(Instance(MinecraftSettings.JavaRenderDistanceNearAction));
        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionJavaNear, Ctx)); // metade feita = ainda não
        handler.Apply(Instance(MinecraftSettings.JavaSimulationDistanceNearAction));
        Assert.Equal(GameCardOptionState.On, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionJavaNear, Ctx));
        // "Gráficos leves" exige as quatro chaves; "FPS sem limite", as duas.
        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionJavaLight, Ctx));
        Assert.Equal(GameCardOptionState.Off, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionJavaFps, Ctx));
        // Nunca um limite: o valor 60 em maxFps é recusado.
        var fps = Instance(MinecraftSettings.JavaMaxFpsUnlimitedAction);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(fps with { Desired = fps.Desired with { Value = "60" } }, T.Ctx).Kind);
    }

    [Fact]
    public void Missing_files_mean_no_reading_never_off_and_unknown_games_have_no_reader()
    {
        Assert.Equal(GameCardOptionState.Unknown, GameCardStateRegistry.Read(RobloxCard.Id, RobloxCard.OptionFpsMax, Ctx));
        Assert.Equal(GameCardOptionState.Unknown, GameCardStateRegistry.Read(GtaVCard.Id, GtaVCard.OptionVsyncOff, Ctx));
        Assert.Equal(GameCardOptionState.Unknown, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionJavaFps, new GameCardStateContext(null)));
        Assert.Equal(GameCardOptionState.Unknown, GameCardStateRegistry.Read(RocketLeagueCard.Id, "qualquer", Ctx));
        Assert.Equal(GameCardOptionState.Unknown, GameCardStateRegistry.Read(MinecraftCard.Id, "opcao-inexistente", Ctx));
        Assert.Null(GameCardStateRegistry.Find(EaFcCard.Id));
        // Versão do jogo sem a chave: sem leitura, e o handler responde "não se aplica" sem acrescentar nada.
        var path = Write(@"AppData\Roaming\.minecraft\options.txt", "version:3955\r\nenableVsync:true\r\n");
        Assert.Equal(GameCardOptionState.Unknown, GameCardStateRegistry.Read(MinecraftCard.Id, MinecraftCard.OptionJavaNear, Ctx));
        Assert.Equal(ApplicabilityKind.NotApplicable, Handler().Assess(Instance(MinecraftSettings.JavaRenderDistanceNearAction), T.Ctx).Kind);
        Assert.Equal("version:3955\r\nenableVsync:true\r\n", File.ReadAllText(path));
    }

    // ------------------------------------------------------------------ ponte para a tela Jogos

    [Fact]
    public void Card_plans_ask_the_broker_as_user_and_read_state_through_the_registry()
    {
        var card = GameCardRegistry.Find(MinecraftCard.Id)!;
        var plans = GameCardPlans.ForCard(card, () => Ctx);
        Assert.Equal(card.Options.Count, plans.Count);
        var fps = plans.Single(p => p.Id == "minecraft|" + MinecraftCard.OptionJavaFps);
        Assert.Equal([MinecraftSettings.JavaVsyncOffAction, MinecraftSettings.JavaMaxFpsUnlimitedAction], fps.Requests.Select(r => r.Request.ActionId));
        Assert.All(fps.Requests, r => Assert.Equal(Nexus.Broker.BrokerLevel.User, r.Level));
        Assert.Equal(fps.Requests.Count, fps.Parts.Count);
        Assert.Equal(OptionState.Unknown, fps.Read(null!)); // sem arquivo: sem leitura (nunca "desligado")
        Write(@"AppData\Roaming\.minecraft\options.txt", Java.Replace("enableVsync:true", "enableVsync:false", StringComparison.Ordinal).Replace("maxFps:120", "maxFps:260", StringComparison.Ordinal));
        Assert.Equal(OptionState.On, fps.Read(null!));
        // O diário reconhece a execução que ligou a opção pelas duas ações.
        var run = new JournalRun(Guid.NewGuid(), Nexus.Broker.BrokerLevel.User, DateTimeOffset.UtcNow,
            [new JournalItem(MinecraftSettings.JavaVsyncOffAction, new Dictionary<string, string>(), true), new JournalItem(MinecraftSettings.JavaMaxFpsUnlimitedAction, new Dictionary<string, string>(), true)]);
        Assert.Equal(UndoKind.Runs, OptionJournal.Find([run], fps.Parts).Kind);
        // Cartões sem opção produzem lista vazia (só Medir/Comparar na tela).
        Assert.Empty(GameCardPlans.ForCard(GameCardRegistry.Find(EaFcCard.Id)!, () => Ctx));
    }

    [Fact]
    public void Cards_come_in_order_and_show_no_brand_art()
    {
        var ids = GameCardRegistry.All.Select(c => c.Id).ToList();
        Assert.True(ids.IndexOf(MinecraftCard.Id) < ids.IndexOf(RobloxCard.Id) && ids.IndexOf(RobloxCard.Id) < ids.IndexOf(GtaVCard.Id)
                    && ids.IndexOf(GtaVCard.Id) < ids.IndexOf(RocketLeagueCard.Id) && ids.IndexOf(RocketLeagueCard.Id) < ids.IndexOf(EaFcCard.Id));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        // Nenhum cartão aponta para imagem/logotipo: só monograma e cor.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Wiring.Root, "src", "Nexus.GameProfiles"), "*Card.cs", SearchOption.AllDirectories))
            Assert.DoesNotMatch(ImageRegex(), File.ReadAllText(file));
    }

    private static readonly string[] ResxFiles = ["Strings.resx", "Strings.en.resx", "Strings.es.resx"];

    [GeneratedRegex(@"\.(png|jpg|jpeg|svg|ico)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRegex();
}
