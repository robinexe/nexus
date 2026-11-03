using Nexus.Broker;
using Nexus.Core;
using Nexus.Desktop.ViewModels.Simple;
using Nexus.Measurement;

namespace Nexus.Tests.Modules;

/// <summary>Modo jogo automático: máquina de estados, seleção das ações (lista fechada conferida contra o catálogo) e estado gravado.</summary>
[Trait("Category", "Unit")]
public sealed class GameModeAutoTests
{
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    // ================================================================== máquina de estados

    [Fact]
    public void Applies_after_the_game_settles_and_restores_after_the_stop_delay()
    {
        var m = new GameModeAutoMachine();
        Assert.Equal(GameModeAutoPhase.Off, m.Phase);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "cs2.exe", Now));
        Assert.Equal(GameModeAutoPhase.Waiting, m.Phase);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "cs2.exe", Now.AddSeconds(10)));
        Assert.Equal(GameModeAutoStep.Apply, m.Next(true, "cs2.exe", Now.AddSeconds(21)));
        m.Applying(true, "cs2.exe");
        Assert.True(m.Applied);
        Assert.Equal("cs2.exe", m.ActiveGame);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "cs2.exe", Now.AddSeconds(60)));
        Assert.Equal(GameModeAutoPhase.Active, m.Phase);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddSeconds(61))); // fechou agora: folga (pode reabrir)
        Assert.Equal(GameModeAutoPhase.Closing, m.Phase);
        Assert.Equal(15, m.SecondsUntilRestore);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddSeconds(63)));
        Assert.Equal(13, m.SecondsUntilRestore);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "cs2.exe", Now.AddSeconds(65))); // reabriu: continua aplicado
        Assert.Equal(GameModeAutoPhase.Active, m.Phase);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddSeconds(70)));
        Assert.Equal(GameModeAutoStep.Restore, m.Next(true, null, Now.AddSeconds(86)));
        Assert.Equal(GameModeAutoPhase.Restoring, m.Phase);
        m.Restored(true, Now.AddSeconds(87));
        Assert.False(m.Applied);
        Assert.Null(m.ActiveGame);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddSeconds(200)));
        Assert.Equal(GameModeAutoPhase.Waiting, m.Phase);
    }

    [Fact]
    public void Stop_delay_is_configurable_and_clamped()
    {
        var m = new GameModeAutoMachine(stopDelay: TimeSpan.FromSeconds(60));
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "g", Now));
        Assert.Equal(GameModeAutoStep.Apply, m.Next(true, "g", Now.AddSeconds(30)));
        m.Applying(true, "g");
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddSeconds(31)));
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddSeconds(80)));  // 49 s fechado: ainda não
        Assert.Equal(GameModeAutoStep.Restore, m.Next(true, null, Now.AddSeconds(91))); // 60 s: desfaz
        m.StopDelay = TimeSpan.Zero;
        Assert.Equal(GameModeAutoMachine.MinStopDelay, m.StopDelay);
        m.StopDelay = TimeSpan.FromHours(1);
        Assert.Equal(GameModeAutoMachine.MaxStopDelay, m.StopDelay);
    }

    [Fact]
    public void Off_never_applies_and_turning_off_restores_at_once()
    {
        var m = new GameModeAutoMachine();
        Assert.Equal(GameModeAutoStep.None, m.Next(false, "g", Now));
        Assert.Equal(GameModeAutoStep.None, m.Next(false, "g", Now.AddMinutes(5)));
        Assert.Equal(GameModeAutoPhase.Off, m.Phase);
        Assert.Equal(GameModeAutoStep.Apply, m.Next(true, "g", Now.AddMinutes(6)));
        m.Applying(true, "g");
        Assert.Equal(GameModeAutoStep.Restore, m.Next(false, "g", Now.AddMinutes(7))); // desligou com o jogo aberto: desfaz já
        Assert.Equal(GameModeAutoPhase.Restoring, m.Phase);
        m.Restored(false, Now.AddMinutes(7));
        Assert.Equal(GameModeAutoStep.None, m.Next(false, "g", Now.AddMinutes(7).AddSeconds(10))); // falhou: espera 30 s
        Assert.Equal(GameModeAutoStep.Restore, m.Next(false, "g", Now.AddMinutes(8)));
    }

    [Fact]
    public void Refused_apply_waits_for_the_next_game_and_a_new_game_updates_the_name()
    {
        var m = new GameModeAutoMachine();
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "a", Now));
        Assert.Equal(GameModeAutoStep.Apply, m.Next(true, "a", Now.AddSeconds(30)));
        m.Applying(false, "a");
        Assert.False(m.Applied);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "a", Now.AddMinutes(5))); // não insiste na mesma partida
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddMinutes(6)));
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "b", Now.AddMinutes(7)));
        Assert.Equal(GameModeAutoStep.Apply, m.Next(true, "b", Now.AddMinutes(8)));
        m.Applying(true, "b");
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now.AddMinutes(9)));
        Assert.Equal(GameModeAutoStep.None, m.Next(true, "c", Now.AddMinutes(9).AddSeconds(5))); // outro jogo dentro da folga
        Assert.Equal("c", m.ActiveGame);
        Assert.Equal(GameModeAutoPhase.Active, m.Phase);
    }

    [Fact]
    public void Resume_restores_what_was_left_applied_when_no_game_is_open()
    {
        var m = new GameModeAutoMachine();
        m.Resume("fortnite");
        Assert.True(m.Applied);
        Assert.Equal("fortnite", m.ActiveGame);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now));
        Assert.Equal(GameModeAutoPhase.Closing, m.Phase);
        Assert.Equal(GameModeAutoStep.Restore, m.Next(true, null, Now.AddSeconds(20)));
        // Mesmo com o modo desligado desde então (o usuário desligou e fechou o NEXUS), o pendente é desfeito.
        var off = new GameModeAutoMachine();
        off.Resume(null);
        Assert.Equal(GameModeAutoStep.Restore, off.Next(false, null, Now));
    }

    [Fact]
    public void Failed_restore_retries_with_backoff_and_gives_up_after_three_attempts()
    {
        var m = new GameModeAutoMachine();
        m.Resume("g");
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, Now)); // retomado sem jogo: respeita a folga
        var t = Now.AddSeconds(20);
        Assert.Equal(GameModeAutoStep.Restore, m.Next(true, null, t));
        m.Restored(false, t);
        Assert.Equal(1, m.RestoreFailures);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, t.AddSeconds(10)));
        Assert.Equal(GameModeAutoPhase.Restoring, m.Phase);
        Assert.Equal(GameModeAutoStep.Restore, m.Next(true, null, t.AddSeconds(31)));
        m.Restored(false, t.AddSeconds(31));
        Assert.Equal(GameModeAutoStep.Restore, m.Next(true, null, t.AddSeconds(62)));
        m.Restored(false, t.AddSeconds(62));
        Assert.Equal(GameModeAutoPhase.UndoFailed, m.Phase);
        Assert.Equal(GameModeAutoStep.None, m.Next(true, null, t.AddMinutes(10))); // desistiu: o usuário resolve
        Assert.Equal(GameModeAutoPhase.UndoFailed, m.Phase);
        m.RetryUndo();
        Assert.Equal(GameModeAutoStep.Restore, m.Next(true, null, t.AddMinutes(11)));
        m.Restored(true, t.AddMinutes(11));
        Assert.Equal(0, m.RestoreFailures);
        Assert.Equal(GameModeAutoPhase.Waiting, m.Phase);
    }

    // ================================================================== seleção das ações

    [Fact]
    public void Closed_list_matches_the_catalog_and_is_safe_for_a_match()
    {
        Assert.NotEmpty(GameModeActions.All);
        foreach (var action in GameModeActions.All)
        {
            var entry = Assert.Single(Catalog.Actions, a => a.Definition.Key == action.Key);
            var d = entry.Definition;
            Assert.Equal(RebootRequirement.None, d.Reboot);
            Assert.False(d.Irreversible, action.Key);
            Assert.NotEqual(PersistenceKind.Ephemeral, d.Persistence);
            Assert.Equal(action.Admin, d.Privileges == PrivilegeLevel.Admin);
            Assert.Empty(entry.UserParameters);
            Assert.Empty(entry.Placeholders());
            Assert.NotEmpty(d.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(entry.Revert), action.Key);
            Assert.False(string.IsNullOrWhiteSpace(d.Mechanism), action.Key);
        }
        Assert.Equal(GameModeActions.All.Count, GameModeActions.All.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(GameModeActions.PowerPlan, GameModeActions.DefaultKeys);
        Assert.DoesNotContain(GameModeActions.DeliveryCap, GameModeActions.DefaultKeys); // administrador: nunca marcado por padrão
    }

    [Fact]
    public void Normalize_keeps_only_known_keys_in_catalog_order_and_null_means_default()
    {
        Assert.Equal(GameModeActions.DefaultKeys, GameModeActions.Normalize(null));
        Assert.Empty(GameModeActions.Normalize([]));
        var chosen = GameModeActions.Normalize([GameModeActions.Notifications, "cleaning.temp-user@1", GameModeActions.PowerPlan, GameModeActions.PowerPlan, "x"]);
        Assert.Equal([GameModeActions.PowerPlan, GameModeActions.Notifications], chosen);
    }

    [Fact]
    public void Plan_sends_admin_actions_only_when_the_process_is_already_elevated()
    {
        var all = GameModeActions.All.Select(a => a.Key).ToList();
        var user = GameModeActions.Plan(all, processElevated: false);
        Assert.Equal([GameModeActions.PowerPlan, GameModeActions.Notifications, GameModeActions.BackgroundCapture], user.User);
        Assert.Empty(user.Admin);
        Assert.Equal([GameModeActions.DeliveryCap], user.SkippedNeedAdmin);
        var admin = GameModeActions.Plan(all, processElevated: true);
        Assert.Equal([GameModeActions.DeliveryCap], admin.Admin);
        Assert.Empty(admin.SkippedNeedAdmin);
        Assert.True(GameModeActions.Plan([], true).IsEmpty);
        Assert.True(GameModeActions.Plan([GameModeActions.DeliveryCap], false).IsEmpty); // só a de administrador, sem elevação: nada a aplicar
    }

    // ================================================================== estado gravado

    [Fact]
    public void State_file_keeps_choices_delay_and_pending_runs_and_reads_the_old_format()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-gamemode-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "game-session.json");
            var store = new GameSessionStore(path);
            var user = Guid.NewGuid();
            var admin = Guid.NewGuid();
            var state = new GameSessionState(true, null)
            {
                Actions = [GameModeActions.PowerPlan],
                PendingRuns = [new PendingGameRun(user, BrokerLevel.User), new PendingGameRun(admin, BrokerLevel.Admin)],
                PendingGame = "cs2.exe",
                StopDelaySeconds = 30,
            };
            Assert.True(store.Save(state));
            var loaded = new GameSessionStore(path).Load();
            Assert.True(loaded.Enabled);
            Assert.Equal([GameModeActions.PowerPlan], loaded.Actions);
            Assert.Equal(30, loaded.StopDelaySeconds);
            Assert.Equal("cs2.exe", loaded.PendingGame);
            Assert.Equal([new PendingGameRun(user, BrokerLevel.User), new PendingGameRun(admin, BrokerLevel.Admin)], loaded.AllPendingRuns);
            Assert.True(loaded.HasPending);
            var settled = loaded.Settled();
            Assert.False(settled.HasPending);
            Assert.Equal([GameModeActions.PowerPlan], settled.Actions); // a escolha fica

            // Formato antigo (Sessão de jogo): uma execução do aplicador de administrador.
            var legacy = Guid.NewGuid();
            File.WriteAllText(path, $"{{\"Enabled\":true,\"PendingRun\":\"{legacy}\"}}");
            var old = new GameSessionStore(path).Load();
            Assert.Null(old.Actions);
            Assert.Equal([new PendingGameRun(legacy, BrokerLevel.Admin)], old.AllPendingRuns);
            Assert.True(old.HasPending);
            Assert.False(old.Settled().HasPending);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
