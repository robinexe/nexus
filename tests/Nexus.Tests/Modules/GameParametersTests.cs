using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.Lab.Gpu;

namespace Nexus.Tests.Modules;

/// <summary>
/// As ações do catálogo principal que precisavam de parâmetro do usuário: otimizações em janela por jogo ({gameExePath}),
/// fps_max do CS2 ({cs2CfgDir} + valor da lista) e a genérica de inicialização (substituída pelo módulo Inicialização).
/// </summary>
[Trait("Category", "Unit")]
public sealed class GameParametersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-cs2-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CatalogDocument Catalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private static CatalogEntry Entry(string key) => Catalog().Actions.Single(a => a.Definition.Key == key);

    private const string Cs2Cfg = @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg";

    // ------------------------------------------------------------------ otimizações em janela por jogo

    [Fact]
    public void Windowed_per_app_takes_the_game_exe_from_the_page()
    {
        var e = Entry("win.windowed-game-optimizations.per-app@1");
        Assert.Equal(GameLibraries.ExePattern, e.UserParameters["gameExePath"]); // mesma regra da página (IsValidExePath)
        const string exe = @"D:\SteamLibrary\steamapps\common\Rocket League\Binaries\Win64\RocketLeague.exe";
        var args = e.ValidateUserArguments(new Dictionary<string, string> { ["gameExePath"] = exe }, out var why);
        Assert.True(args is not null, why);
        var inst = e.TryInstantiate(new Dictionary<string, string>(args!), out why)!;
        Assert.True(inst is not null, why);
        Assert.Equal(exe, inst.Target["name"]);
        Assert.Equal("SwapEffectUpgradeEnable", inst.Target["token"]);
        Assert.Equal(("Token", "1"), (inst.Desired.Type, inst.Desired.Value));

        foreach (var bad in new[] { "RocketLeague.exe", @"D:\Jogo\jogo.bat", @"\\servidor\jogo.exe", "D:\\Jogo\\jogo.exe\n" })
            Assert.Null(e.ValidateUserArguments(new Dictionary<string, string> { ["gameExePath"] = bad }, out _));
        Assert.Null(e.ValidateUserArguments(null, out var missing));
        Assert.Contains("gameExePath", missing, StringComparison.Ordinal);
        Assert.Equal("1", GameLibraries.ParseToken("GpuPreference=2;SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable")?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(GameLibraries.ParseToken("GpuPreference=2;", "SwapEffectUpgradeEnable"));
    }

    // ------------------------------------------------------------------ CS2 fps_max

    [Fact]
    public void Cs2_fps_max_rules_match_the_catalog()
    {
        var e = Entry(Cs2Install.FpsMaxActionKey);
        Assert.Equal(Cs2Install.CfgDirPattern, e.UserParameters["cs2CfgDir"]);
        Assert.Equal(Cs2Install.FpsMaxValues, e.Definition.AllowedValues);
        Assert.Null(e.DesiredValue); // o valor vem da lista, escolhido na página

        var args = e.ValidateUserArguments(new Dictionary<string, string> { ["cs2CfgDir"] = Cs2Cfg }, out var why);
        Assert.True(args is not null, why);
        var inst = (e with { DesiredValue = "141" }).TryInstantiate(new Dictionary<string, string>(args!), out why)!;
        Assert.True(inst is not null, why);
        Assert.Equal(Cs2Cfg, inst.Target["root"]);
        Assert.Equal("autoexec.cfg", inst.Target["file"]);
        Assert.Equal("fps_max", inst.Target["convar"]);
        Assert.Equal(("Convar", "141"), (inst.Desired.Type, inst.Desired.Value));
        // A restauração reconstrói os argumentos a partir do alvo gravado no diário.
        Assert.Equal(Cs2Cfg, e.UserArgumentsFromTarget(inst.Target, out _)!["cs2CfgDir"]);

        foreach (var bad in new[]
                 {
                     @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo",            // não é a pasta cfg
                     @"D:\..\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg",                 // ".."
                     @"D:\Jogos\Outro\cfg",
                     @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\..",
                 })
        {
            Assert.Null(e.ValidateUserArguments(new Dictionary<string, string> { ["cs2CfgDir"] = bad }, out _));
            Assert.False(Cs2Install.IsValidCfgDir(bad));
        }
    }

    [Fact]
    public void Cs2_install_is_found_from_the_steam_manifest_folder()
    {
        Assert.Equal(Cs2Cfg, Cs2Install.CfgDirFromInstallDir(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"));
        Assert.Equal(Cs2Cfg, Cs2Install.CfgDirFromInstallDir(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\"));
        Assert.Null(Cs2Install.CfgDirFromInstallDir(@"D:\SteamLibrary\steamapps\common\Dota 2"));
        Assert.Null(Cs2Install.CfgDirFromInstallDir(null));
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\csgo\console.log", Cs2Install.ConsoleLogPath(Cs2Cfg));
    }

    private const string CvarListDump =
        "] cvarlist\n" +
        "cvar list\n--------------\n" +
        "fps_max                                  : 400      : cl, a, rel    : Frame rate limiter\n" +
        "sensitivity                              : 1.25     : cl, a         : Mouse sensitivity\n" +
        "--------------\n" +
        "2 total convars/concommands\n";

    [Fact]
    public void Console_log_counts_only_when_it_is_a_cvarlist_dump()
    {
        var dump = Cs2Install.FromConsoleLog(CvarListDump, "teste");
        Assert.NotNull(dump);
        Assert.True(dump!.Contains("fps_max"));
        // Log comum de partida, sem o rodapé do cvarlist: não serve de prova.
        Assert.Null(Cs2Install.FromConsoleLog("Connected to 1.2.3.4\nmap : de_dust2 : loading\n", "teste"));
        Assert.Null(Cs2Install.FromConsoleLog("", "teste"));
    }

    [Fact]
    public void Cs2_handler_validates_against_the_dump_next_to_the_target_cfg()
    {
        var cfg = Directory.CreateDirectory(Path.Combine(_dir, "game", "csgo", "cfg")).FullName;
        var handler = new SourceCfgConvarHandler(a => Cs2Install.ReadDump(a.Target["root"]));
        var action = new ActionInstance(T.Def("cs2.cfg.fps_max", SourceCfgConvarHandler.Kind),
            new Dictionary<string, string> { ["root"] = cfg, ["file"] = "autoexec.cfg", ["convar"] = "fps_max" }, StateSnapshot.Of("Convar", "141"));

        var blocked = handler.Assess(action, T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, blocked.Kind);
        Assert.Contains("-condebug", blocked.Reason, StringComparison.Ordinal); // diz o que fazer

        File.WriteAllText(Path.Combine(_dir, "game", "csgo", "console.log"), CvarListDump);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, T.Ctx).Kind);
        Assert.Null(Cs2Install.ReadFpsMax(cfg));
        var before = handler.Detect(action);
        handler.Apply(action);
        Assert.Equal("141", Cs2Install.ReadFpsMax(cfg));
        handler.Restore(action, before);
        Assert.Null(Cs2Install.ReadFpsMax(cfg));

        // Em produção o aplicador só aceita a pasta cfg do CS2 (regra do catálogo); fora dela, nenhum dump é lido.
        Assert.Null(Cs2Install.CvarsFor(action));
    }

    // ------------------------------------------------------------------ inicialização genérica

    [Fact]
    public void Generic_startup_action_is_superseded_by_the_startup_module()
    {
        var c = Catalog();
        Assert.DoesNotContain(c.Actions, a => a.Definition.Id == "startup.run-entry.disable");
        var r = Assert.Single(c.Rejected, x => x.Id == "startup.run-entry.disable");
        Assert.Contains("boot.startup.user.run", r.Reason, StringComparison.Ordinal);
        Assert.Contains(c.Actions, a => a.Definition.Key == "boot.startup.user.run@1" && a.Definition.HandlerKind == "StartupApproved");
        Assert.Contains(c.Actions, a => a.Definition.Key == "boot.startup.machine.run@1" && a.Definition.HandlerKind == "StartupApproved");
    }
}
