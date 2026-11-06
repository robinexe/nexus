using System.Text;
using Nexus.Core;
using Nexus.GameProfiles.Configs;

namespace Nexus.Tests.Modules;

/// <summary>
/// Configuração de jogos (handler GameConfigValue): edição sem perdas das 3 sintaxes, lista fechada de jogos/chaves/valores,
/// nunca cria arquivo nem chave, recusa com o jogo aberto, somente leitura e várias contas; desfazer devolve o valor exato.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameConfigTests : IDisposable
{
    private readonly string _profile = Directory.CreateTempSubdirectory("nexus-gc-").FullName;
    private bool _running;

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_profile, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_profile, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Minecraft = "version:3955\r\nenableVsync:true\r\nmaxFps:120\r\nrenderClouds:\"true\"\r\nlang:pt_br\r\n";
    private const string Ow2 = "[Render.13]\nFrameRateCap = \"141\"\nLimitToRefresh = \"1\"\nReflexMode = \"0\"\n\n[Sound.3]\nVolume = \"80\"\n";
    private const string Apex = "\"VideoConfig\"\n{\n\t\"setting.mat_vsync_mode\"\t\t\"1\"\n\t\"setting.ssao_enabled\"\t\t\"1\"\n}\n";
    private const string R6 = "[DISPLAY]\r\n; FPSLimit: below 30 means no limit\r\nFPSLimit=141\r\nVSync=1\r\n[CUSTOM_QUALITY]\r\nShadow=2\r\n";

    private string Write(string relative, string text, Encoding? enc = null)
    {
        var path = Path.Combine([_profile, .. relative.Split('\\')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        enc ??= new UTF8Encoding(false);
        File.WriteAllBytes(path, [.. enc.GetPreamble(), .. enc.GetBytes(text)]);
        return path;
    }

    private GameConfigHandler Handler() => new(_ => _profile, _ => _running, otherAccount: false);

    private static ActionInstance Instance(string id)
    {
        var e = Wiring.Entry(id + "@1") ?? throw new InvalidOperationException(id + " não está no catálogo");
        return e.TryInstantiate(new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);
    }

    // ------------------------------------------------------------------ ConfigLines

    [Fact]
    public void Each_syntax_round_trips_byte_identical_and_changes_only_the_value()
    {
        foreach (var (text, syntax, key, before, after) in new[]
        {
            (Minecraft, ConfigSyntax.Colon, "maxFps", "120", "260"),
            (Minecraft, ConfigSyntax.Colon, "renderClouds", "true", "false"),
            (Ow2, ConfigSyntax.Equals, "FrameRateCap", "141", "600"),
            (Apex, ConfigSyntax.KeyValues, "setting.mat_vsync_mode", "1", "0"),
            (R6, ConfigSyntax.Equals, "fpslimit", "141", "0"),
        })
        {
            var doc = ConfigLines.Parse(text, syntax);
            Assert.Equal(text, doc.ToString());
            Assert.Equal((ConfigKeyState.Present, before), doc.Get(key));
            Assert.True(doc.Set(key, after));
            Assert.Equal((ConfigKeyState.Present, after), doc.Get(key));
            var a = text.Split('\n');
            var b = doc.ToString().Split('\n');
            Assert.Equal(a.Length, b.Length);
            var changed = Assert.Single(Enumerable.Range(0, a.Length), i => a[i] != b[i]);
            Assert.Equal(a[changed].Replace(before, after, StringComparison.Ordinal), b[changed]); // aspas, espaços e tabs ficam
        }
    }

    [Fact]
    public void Never_adds_keys_refuses_repeated_ones_and_rejects_unsafe_values()
    {
        var doc = ConfigLines.Parse(Minecraft, ConfigSyntax.Colon);
        Assert.False(doc.Set("graphicsMode", "0")); // ausente: nada acrescentado
        Assert.Equal(Minecraft, doc.ToString());
        var twice = ConfigLines.Parse("maxFps:120\nmaxFps:60\n", ConfigSyntax.Colon);
        Assert.Equal(ConfigKeyState.Ambiguous, twice.Get("maxFps").State);
        Assert.Throws<InvalidOperationException>(() => twice.Set("maxFps", "260"));
        Assert.Throws<ArgumentException>(() => doc.Set("maxFps", "260\nenableVsync:true"));
        Assert.Throws<ArgumentException>(() => doc.Set("maxFps", "2\"60"));
        // Comentário e cabeçalho de seção nunca são chave.
        Assert.Equal(ConfigKeyState.Absent, ConfigLines.Parse(R6, ConfigSyntax.Equals).Get("; FPSLimit").State);
    }

    // ------------------------------------------------------------------ handler

    [Fact]
    public void Apply_and_restore_minecraft_through_the_executor()
    {
        var path = Write(@"AppData\Roaming\.minecraft\options.txt", Minecraft);
        var handler = Handler();
        var action = Instance("minecraft.cfg.vsync.off");
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, T.Ctx).Kind);
        var original = handler.Detect(action);
        Assert.Equal("true", original.Value);
        handler.Apply(action);
        Assert.Contains("enableVsync:false\r\n", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("false", handler.Detect(action).Value);
        Assert.Null(handler.CheckRestore(action, original));
        handler.Restore(action, original);
        Assert.Equal(Minecraft, File.ReadAllText(path));
    }

    [Fact]
    public void Missing_game_or_key_is_not_applicable_and_nothing_is_created()
    {
        var handler = Handler();
        Assert.Equal(ApplicabilityKind.NotApplicable, handler.Assess(Instance("ow2.cfg.framerate-cap.max"), T.Ctx).Kind);
        Assert.False(Directory.Exists(Path.Combine(_profile, "Documents", "Overwatch")));
        Write(@"AppData\Roaming\.minecraft\options.txt", Minecraft);
        Assert.Equal(ApplicabilityKind.NotApplicable, handler.Assess(Instance("minecraft.cfg.graphics.fast"), T.Ctx).Kind); // versão sem a chave
    }

    [Fact]
    public void Open_game_read_only_file_and_several_accounts_are_refused()
    {
        var handler = Handler();
        var apex = Write(@"Saved Games\Respawn\Apex\local\videoconfig.txt", Apex);
        _running = true;
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(Instance("apex.cfg.vsync.off"), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => handler.Apply(Instance("apex.cfg.vsync.off")));
        _running = false;
        File.SetAttributes(apex, FileAttributes.ReadOnly);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(Instance("apex.cfg.vsync.off"), T.Ctx).Kind);

        Write(@"Documents\My Games\Rainbow Six - Siege\conta-a\GameSettings.ini", R6);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(Instance("r6.cfg.fps-limit.off"), T.Ctx).Kind);
        Write(@"Documents\My Games\Rainbow Six - Siege\conta-b\GameSettings.ini", R6);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(Instance("r6.cfg.fps-limit.off"), T.Ctx).Kind); // não escolhe a conta
    }

    [Fact]
    public void Onedrive_documents_are_found_too()
    {
        Write(@"OneDrive\Documents\Overwatch\Settings\Settings_v0.ini", Ow2);
        var handler = Handler();
        var action = Instance("ow2.cfg.limit-to-refresh.off");
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, T.Ctx).Kind);
        handler.Apply(action);
        Assert.Equal("0", handler.Detect(action).Value);
    }

    [Fact]
    public void Only_listed_games_keys_and_values_are_accepted()
    {
        Write(@"AppData\Roaming\.minecraft\options.txt", Minecraft);
        var handler = Handler();
        var good = Instance("minecraft.cfg.maxfps.unlimited");
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(good with { Desired = good.Desired with { Value = "60" } }, T.Ctx).Kind); // nunca limita
        var target = new Dictionary<string, string>(good.Target) { ["key"] = "lang" };
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(good with { Target = target }, T.Ctx).Kind);
        var game = new Dictionary<string, string>(good.Target) { ["game"] = "valorant" };
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(good with { Target = game }, T.Ctx).Kind);
    }

    [Fact]
    public void Catalog_never_caps_fps_and_every_value_is_in_the_handler_list()
    {
        var mine = Wiring.Catalog.Actions.Where(e => e.Definition.HandlerKind == GameConfigs.HandlerKind).ToList();
        Assert.Equal(20, mine.Count);
        foreach (var e in mine)
        {
            var t = e.TargetTemplate;
            var key = GameConfigs.Find(t["game"])?.Find(t["key"]);
            Assert.NotNull(key);
            Assert.Contains(e.DesiredValue!, key!.Values);
            Assert.Equal(PrivilegeLevel.User, e.Definition.Privileges);
        }
        // Nenhum jogo de anticheat de kernel ou arquivo sincronizado na conta (decisão da pesquisa de 06/11/2026).
        Assert.DoesNotContain(GameConfigs.All, g => g.Id is "valorant" or "lol" or "cod" or "rivals");
    }
}
