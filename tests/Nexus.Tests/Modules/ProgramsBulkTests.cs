using Nexus.Programs.Inventory;
using Nexus.Programs.Uninstall;

namespace Nexus.Tests.Modules;

internal static class BulkFixtures
{
    public const string Sys = @"C:\Windows\System32";
    public const string Code = "{A1B2C3D4-1111-2222-3333-444455556666}";

    public static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows\System32\msiexec.exe",
        @"C:\Windows\System32\cmd.exe",
        @"C:\Program Files\7-Zip\Uninstall.exe",
        @"C:\Program Files\Foo\unins000.exe",
        @"C:\Program Files\Foo\helper.exe",
        @"C:\Program Files\Other\evil.exe",
        @"C:\Users\a\AppData\Local\Discord\Update.exe",
        @"C:\Program Files\NEXUS\uninstall.exe",
        @"C:\Program Files\NVIDIA Corporation\Installer2\setup.exe",
    };

    public static readonly UninstallEnvironment Env = new(Files.Contains, s => s, Sys, @"C:\Program Files\NEXUS");

    public static InstalledProgram P(string name, string? uninstall, string? quiet = null, long? size = null, string? publisher = "Editor",
        InstallScope scope = InstallScope.Machine64, bool msi = false, bool noRemove = false, string? location = null) =>
        new($"{scope}|{name}", name, publisher, "1.0", size, null, location, uninstall, quiet, null, scope, name, msi, noRemove);
}

// ====================================================================== seleção (bloqueios por categoria)

[Trait("Category", "Unit")]
public class ProgramsBulkPolicyTests
{
    private static BulkEligibility E(InstalledProgram p) => BulkUninstallPolicy.Evaluate(p, BulkFixtures.Env);

    [Fact]
    public void Ordinary_program_with_valid_uninstaller_is_allowed()
    {
        var e = E(BulkFixtures.P("7-Zip 24.08 (x64)", "\"C:\\Program Files\\7-Zip\\Uninstall.exe\""));
        Assert.Equal(BulkEligibilityKind.Allowed, e.Kind);
        Assert.True(e.Selectable);
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Runtime do Visual C++")]
    [InlineData("Microsoft Visual C++ 2010  x86 Redistributable - 10.0.40219", "Runtime do Visual C++")]
    [InlineData("Microsoft .NET Runtime - 8.0.10 (x64)", ".NET")]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.10 (x64)", ".NET")]
    [InlineData("Microsoft ASP.NET Core 8.0.10 - Shared Framework (x64)", ".NET")]
    [InlineData("DirectX for Managed Code Update (Summer 2004)", "Runtime de jogos")]
    [InlineData("Microsoft Edge WebView2 Runtime", "Edge / WebView2")]
    [InlineData("NVIDIA Graphics Driver 566.03", "Driver de vídeo")]
    [InlineData("NVIDIA PhysX System Software 9.23.1019", "Driver de vídeo")]
    [InlineData("AMD Software", "Driver de vídeo")]
    [InlineData("AMD Chipset Software", "Driver de chipset/placa")]
    [InlineData("Intel(R) Management Engine Components", "Driver de chipset/placa")]
    [InlineData("Realtek High Definition Audio Driver", "Driver de dispositivo")]
    public void Critical_runtimes_and_drivers_need_extra_confirmation(string name, string category)
    {
        var e = E(BulkFixtures.P(name, $"MsiExec.exe /X{BulkFixtures.Code}", msi: true));
        Assert.Equal(BulkEligibilityKind.NeedsExtraConfirmation, e.Kind);
        Assert.Equal(category, e.Category);
        Assert.False(string.IsNullOrWhiteSpace(e.Reason));
        Assert.True(e.Selectable);
        Assert.True(e.Critical);
    }

    [Theory]
    [InlineData("NVIDIA App")]
    [InlineData("NVIDIA GeForce Experience")]
    [InlineData("Steam")]
    [InlineData("Microsoft Teams")]
    public void Lookalike_names_are_not_critical(string name) =>
        Assert.Equal(BulkEligibilityKind.Allowed, E(BulkFixtures.P(name, "\"C:\\Program Files\\Foo\\unins000.exe\"")).Kind);

    [Fact]
    public void Store_apps_are_blocked_and_point_to_settings()
    {
        var e = E(BulkFixtures.P("Spotify", "x", scope: InstallScope.Store));
        Assert.Equal(BulkEligibilityKind.Blocked, e.Kind);
        Assert.Contains("Configurações", e.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRemove_is_blocked() =>
        Assert.Equal(BulkEligibilityKind.Blocked, E(BulkFixtures.P("Foo", "\"C:\\Program Files\\Foo\\unins000.exe\"", noRemove: true)).Kind);

    [Theory]
    [InlineData("NEXUS", null, null)]
    [InlineData("NEXUS 0.9.0", "robinexe", null)]
    [InlineData("Otimizador", "Outro", @"C:\Program Files\NEXUS")] // pela pasta, mesmo com outro nome
    public void Nexus_itself_is_blocked(string name, string? publisher, string? location)
    {
        var e = E(BulkFixtures.P(name, "\"C:\\Program Files\\NEXUS\\uninstall.exe\"", publisher: publisher, location: location));
        Assert.Equal(BulkEligibilityKind.Blocked, e.Kind);
        Assert.Equal("O próprio NEXUS", e.Category);
    }

    [Fact]
    public void Nexus_lookalike_from_other_publisher_elsewhere_is_allowed() =>
        Assert.Equal(BulkEligibilityKind.Allowed, E(BulkFixtures.P("Nexus Mod Manager", "\"C:\\Program Files\\Foo\\unins000.exe\"", publisher: "Black Tree Gaming")).Kind);

    [Theory]
    [InlineData("Microsoft Update Health Tools")]
    [InlineData("Windows PC Health Check")]
    public void Windows_components_are_blocked(string name) =>
        Assert.Equal(BulkEligibilityKind.Blocked, E(BulkFixtures.P(name, $"MsiExec.exe /X{BulkFixtures.Code}", msi: true)).Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("cmd.exe /c del everything")]
    [InlineData("\"C:\\Program Files\\Gone\\uninst.exe\"")]
    [InlineData("uninst.exe")]
    public void Invalid_uninstaller_is_blocked_with_parser_reason(string? raw)
    {
        var e = E(BulkFixtures.P("Foo", raw));
        Assert.Equal(BulkEligibilityKind.Blocked, e.Kind);
        Assert.Equal("Desinstalador inválido", e.Category);
        Assert.False(string.IsNullOrWhiteSpace(e.Reason));
    }

    [Fact]
    public void Critical_with_invalid_uninstaller_is_blocked_not_just_warned() =>
        Assert.Equal(BulkEligibilityKind.Blocked, E(BulkFixtures.P("NVIDIA Graphics Driver 566.03", "\"C:\\nope\\x.exe\"")).Kind);
}

// ====================================================================== montagem da fila

[Trait("Category", "Unit")]
public class ProgramsBulkQueueBuilderTests
{
    [Fact]
    public void Msi_becomes_basic_ui_with_norestart()
    {
        var plan = BulkQueueBuilder.Build([BulkFixtures.P("App MSI", $"MsiExec.exe /I{BulkFixtures.Code}", msi: true)], BulkFixtures.Env, preferQuiet: true);
        var item = Assert.Single(plan.Items);
        Assert.Equal(UninstallMode.MsiBasic, item.Mode);
        Assert.Equal(@"C:\Windows\System32\msiexec.exe", item.Command.FileName);
        Assert.Equal($"/x {BulkFixtures.Code} /qb- /norestart", item.Command.Arguments);
        Assert.Equal("Windows Installer (MSI)", item.KindText);
    }

    [Fact]
    public void Without_quiet_preference_everything_is_interactive_and_msi_stays_plain_remove()
    {
        var plan = BulkQueueBuilder.Build([
            BulkFixtures.P("App MSI", $"MsiExec.exe /X{BulkFixtures.Code}", msi: true),
            BulkFixtures.P("Discord", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall -s"),
        ], BulkFixtures.Env, preferQuiet: false);
        Assert.All(plan.Items, i => Assert.Equal(UninstallMode.Interactive, i.Mode));
        Assert.Equal($"/x {BulkFixtures.Code}", plan.Items.Single(i => i.Program.Name == "App MSI").Command.Arguments);
    }

    [Fact]
    public void Vendor_quiet_string_is_parsed_and_used()
    {
        var p = BulkFixtures.P("Discord", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall -s");
        var item = Assert.Single(BulkQueueBuilder.Build([p], BulkFixtures.Env, true).Items);
        Assert.Equal(UninstallMode.VendorQuiet, item.Mode);
        Assert.Equal(@"C:\Users\a\AppData\Local\Discord\Update.exe", item.Command.FileName);
        Assert.Equal("--uninstall -s", item.Command.Arguments);
    }

    [Fact]
    public void Unquoted_quiet_string_with_spaces_uses_existing_exe_prefix()
    {
        var p = BulkFixtures.P("Foo", "\"C:\\Program Files\\Foo\\unins000.exe\"", @"C:\Program Files\Foo\unins000.exe /VERYSILENT /SUPPRESSMSGBOXES");
        var item = Assert.Single(BulkQueueBuilder.Build([p], BulkFixtures.Env, true).Items);
        Assert.Equal(UninstallMode.VendorQuiet, item.Mode);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES", item.Command.Arguments);
    }

    [Fact]
    public void Quiet_string_in_same_folder_is_accepted()
    {
        var p = BulkFixtures.P("Foo", "\"C:\\Program Files\\Foo\\unins000.exe\"", "\"C:\\Program Files\\Foo\\helper.exe\" /uninstall /quiet");
        Assert.Equal(UninstallMode.VendorQuiet, Assert.Single(BulkQueueBuilder.Build([p], BulkFixtures.Env, true).Items).Mode);
    }

    [Fact]
    public void Quiet_string_pointing_elsewhere_falls_back_to_interactive_with_note()
    {
        var p = BulkFixtures.P("Foo", "\"C:\\Program Files\\Foo\\unins000.exe\"", "\"C:\\Program Files\\Other\\evil.exe\" /S");
        var item = Assert.Single(BulkQueueBuilder.Build([p], BulkFixtures.Env, true).Items);
        Assert.Equal(UninstallMode.Interactive, item.Mode);
        Assert.Equal(@"C:\Program Files\Foo\unins000.exe", item.Command.FileName);
        Assert.Contains("outro executável", item.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cmd.exe /c \"C:\\Program Files\\Foo\\unins000.exe\" /S")]
    [InlineData("\"C:\\Program Files\\Foo\\missing.exe\" /S")]
    public void Refused_quiet_string_falls_back_to_interactive(string quiet)
    {
        var p = BulkFixtures.P("Foo", "\"C:\\Program Files\\Foo\\unins000.exe\"", quiet);
        var item = Assert.Single(BulkQueueBuilder.Build([p], BulkFixtures.Env, true).Items);
        Assert.Equal(UninstallMode.Interactive, item.Mode);
        Assert.Contains("recusado", item.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Msi_quiet_string_is_normalized_to_our_msi_form()
    {
        var p = BulkFixtures.P("Foo", "\"C:\\Program Files\\Foo\\unins000.exe\"", $"MsiExec.exe /X{BulkFixtures.Code} /qn REBOOT=Force");
        var item = Assert.Single(BulkQueueBuilder.Build([p], BulkFixtures.Env, true).Items);
        Assert.Equal(UninstallMode.MsiBasic, item.Mode);
        Assert.Equal($"/x {BulkFixtures.Code} /qb- /norestart", item.Command.Arguments); // opções do registro descartadas
    }

    [Fact]
    public void No_guessed_silent_switches_when_vendor_declared_none()
    {
        var item = Assert.Single(BulkQueueBuilder.Build([BulkFixtures.P("Foo", "\"C:\\Program Files\\Foo\\unins000.exe\"")], BulkFixtures.Env, true).Items);
        Assert.Equal(UninstallMode.Interactive, item.Mode);
        Assert.Equal("", item.Command.Arguments);
    }

    [Fact]
    public void Order_is_interactive_then_quiet_then_critical_each_by_name()
    {
        var plan = BulkQueueBuilder.Build([
            BulkFixtures.P("Microsoft Visual C++ 2015-2022 Redistributable (x64)", $"MsiExec.exe /X{{B1B2C3D4-1111-2222-3333-444455556666}}", msi: true),
            BulkFixtures.P("Zeta MSI", $"MsiExec.exe /X{BulkFixtures.Code}", msi: true),
            BulkFixtures.P("Beta", "\"C:\\Program Files\\Foo\\unins000.exe\""),
            BulkFixtures.P("Alpha", "\"C:\\Program Files\\7-Zip\\Uninstall.exe\""),
            BulkFixtures.P("Discord", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall", "\"C:\\Users\\a\\AppData\\Local\\Discord\\Update.exe\" --uninstall -s"),
        ], BulkFixtures.Env, true);
        Assert.Equal(["Alpha", "Beta", "Discord", "Zeta MSI", "Microsoft Visual C++ 2015-2022 Redistributable (x64)"], plan.Items.Select(i => i.Program.Name));
        Assert.Single(plan.CriticalItems);
    }

    [Fact]
    public void Blocked_and_invalid_go_to_refused_with_reason_and_duplicates_collapse()
    {
        var ok = BulkFixtures.P("Alpha", "\"C:\\Program Files\\7-Zip\\Uninstall.exe\"", size: 1024);
        var plan = BulkQueueBuilder.Build([ok, ok,
            BulkFixtures.P("NEXUS", "\"C:\\Program Files\\NEXUS\\uninstall.exe\""),
            BulkFixtures.P("Broken", "\"C:\\gone\\x.exe\""),
            BulkFixtures.P("NoSize", "\"C:\\Program Files\\Foo\\unins000.exe\"")], BulkFixtures.Env, true);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(2, plan.Refused.Count);
        Assert.All(plan.Refused, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        Assert.Equal(1024, plan.KnownBytes);
        Assert.Equal(1, plan.UnknownSize);
    }
}

// ====================================================================== executor da fila (máquina de estados)

[Trait("Category", "Unit")]
public class ProgramsBulkQueueTests
{
    /// <summary>Roteiro de um desinstalador falso: código, se tira o programa da lista, e se trava até cancelar.</summary>
    private sealed record Script(int? Code = 0, bool Removes = true, bool Hangs = false, int RemovesAfterPolls = 0, bool Refuses = false, bool UserCancelsUac = false);

    private sealed class FakeWorld
    {
        public readonly HashSet<string> Installed = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Queue<Script>> Scripts = new(StringComparer.Ordinal);
        public readonly List<string> Started = [];
        public readonly Dictionary<string, int> PendingRemovals = new(StringComparer.Ordinal);
        public int Running;
        public int MaxConcurrent;
        public DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        public string RebootMark = "a";
        public Action<string>? OnStart;

        public bool StillInstalled(InstalledProgram p)
        {
            if (PendingRemovals.TryGetValue(p.Name, out var n))
            {
                if (n <= 0) { Installed.Remove(p.Name); PendingRemovals.Remove(p.Name); }
                else PendingRemovals[p.Name] = n - 1;
            }
            return Installed.Contains(p.Name);
        }

        public Task Delay(TimeSpan t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Now += t;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner(FakeWorld world) : IUninstallRunner
    {
        public IRunningUninstaller Start(UninstallCommand command)
        {
            var name = command.Arguments; // nos testes, o argumento carrega o nome do programa
            world.Started.Add(name);
            world.OnStart?.Invoke(name);
            var s = world.Scripts.TryGetValue(name, out var q) && q.Count > 0 ? q.Dequeue() : new Script();
            if (s.Refuses || s.UserCancelsUac) throw new UninstallStartException("falhou", s.UserCancelsUac);
            world.Running++;
            world.MaxConcurrent = Math.Max(world.MaxConcurrent, world.Running);
            return new Proc(world, name, s);
        }
    }

    private sealed class Proc(FakeWorld world, string name, Script s) : IRunningUninstaller
    {
        public int? ExitCode => s.Code;

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (s.Hangs) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (s.Removes)
            {
                if (s.RemovesAfterPolls > 0) world.PendingRemovals[name] = s.RemovesAfterPolls;
                else world.Installed.Remove(name);
            }
        }

        public void Dispose() => world.Running--;
    }

    private sealed class Probe(FakeWorld world) : IRebootPendingProbe
    {
        public string? Fingerprint() => world.RebootMark;
    }

    private static BulkQueueItem Item(string name, UninstallMode mode = UninstallMode.Interactive, long? size = 1_048_576) =>
        new(BulkFixtures.P(name, "x", size: size), new UninstallCommand(@"C:\x\" + name + ".exe", name, false, null), mode, BulkEligibility.Allowed);

    private static readonly BulkUninstallOptions Fast = new()
    {
        InteractiveTimeout = TimeSpan.FromMinutes(60), QuietTimeout = TimeSpan.FromMinutes(30), ChildWait = TimeSpan.FromMinutes(10),
        PollInterval = TimeSpan.FromSeconds(3), SettleDelay = TimeSpan.FromSeconds(2), BusyRetryDelay = TimeSpan.FromSeconds(45), MaxBusyRetries = 2,
    };

    private static (BulkUninstaller Queue, FakeWorld World, List<BulkQueueSnapshot> Snaps) Make(BulkUninstallOptions? options, params BulkQueueItem[] items)
    {
        var world = new FakeWorld();
        foreach (var i in items) world.Installed.Add(i.Program.Name);
        var snaps = new List<BulkQueueSnapshot>();
        var q = new BulkUninstaller(items, new FakeRunner(world), world.StillInstalled, new Probe(world), options ?? Fast,
            new SyncProgress(snaps), world.Delay, () => world.Now);
        return (q, world, snaps);
    }

    private sealed class SyncProgress(List<BulkQueueSnapshot> list) : IProgress<BulkQueueSnapshot>
    {
        public void Report(BulkQueueSnapshot value) => list.Add(value);
    }

    [Fact]
    public async Task All_succeed_in_order_one_at_a_time()
    {
        var (q, world, snaps) = Make(null, Item("A"), Item("B"), Item("C"));
        var s = await q.RunAsync();
        Assert.Equal(["A", "B", "C"], world.Started);
        Assert.Equal(1, world.MaxConcurrent);
        Assert.Equal(3, s.Removed.Count);
        Assert.Empty(s.Failed);
        Assert.Empty(s.Skipped);
        Assert.Equal(3L * 1_048_576, s.FreedBytes);
        Assert.False(s.RebootRequired);
        // progresso: em algum momento B estava rodando com A concluído e C na fila
        Assert.Contains(snaps, x => x.Current?.Item.Program.Name == "B" && x.Current.State == BulkItemState.Running && x.Removed == 1 && x.Pending == 1);
        Assert.Null(q.Snapshot().CurrentIndex);
    }

    [Fact]
    public async Task Failure_with_code_is_reported_and_queue_continues()
    {
        var (q, world, _) = Make(null, Item("A"), Item("B"));
        world.Scripts["A"] = new([new Script(Code: 1603, Removes: false)]);
        var s = await q.RunAsync();
        var f = Assert.Single(s.Failed);
        Assert.Equal(1603, f.ExitCode);
        Assert.Contains("1603", f.Detail, StringComparison.Ordinal);
        Assert.Single(s.Removed);
        Assert.Contains("A", s.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nonzero_code_but_entry_gone_counts_as_removed()
    {
        var (q, world, _) = Make(null, Item("A"));
        world.Scripts["A"] = new([new Script(Code: 2, Removes: true)]);
        Assert.Single((await q.RunAsync()).Removed);
    }

    [Fact]
    public async Task Busy_1618_goes_to_end_and_retries_after_delay()
    {
        var (q, world, snaps) = Make(null, Item("A"), Item("B"));
        world.Scripts["A"] = new([new Script(Code: 1618, Removes: false), new Script(Code: 0)]);
        var start = world.Now;
        var s = await q.RunAsync();
        Assert.Equal(["A", "B", "A"], world.Started);
        Assert.Equal(2, s.Removed.Count);
        Assert.Contains(snaps, x => x.Items[0].State == BulkItemState.RetryLater);
        Assert.True(world.Now - start >= Fast.BusyRetryDelay);
        Assert.Equal(2, s.Removed.Single(r => r.Item.Program.Name == "A").Attempts);
    }

    [Fact]
    public async Task Busy_1618_gives_up_after_max_retries()
    {
        var (q, world, _) = Make(null, Item("A"));
        world.Scripts["A"] = new([new Script(1618, false), new Script(1618, false), new Script(1618, false), new Script(0)]);
        var s = await q.RunAsync();
        Assert.Equal(3, world.Started.Count);
        var f = Assert.Single(s.Failed);
        Assert.Equal(1618, f.ExitCode);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public async Task Reboot_codes_mark_reboot_required(int code)
    {
        var (q, world, _) = Make(null, Item("A"), Item("B"));
        world.Scripts["A"] = new([new Script(Code: code)]);
        var s = await q.RunAsync();
        Assert.Equal(2, s.Removed.Count);
        Assert.True(s.RebootRequired);
        Assert.True(s.RebootFromCodes);
        Assert.True(s.Removed.Single(r => r.Item.Program.Name == "A").RebootRequired);
        Assert.Contains("Reinicie", s.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_pending_rename_appearing_during_batch_marks_reboot()
    {
        var (q, world, _) = Make(null, Item("A"));
        world.OnStart = _ => world.RebootMark = "b";
        var s = await q.RunAsync();
        Assert.True(s.RebootRequired);
        Assert.True(s.RebootFromWindows);
        Assert.False(s.RebootFromCodes);
    }

    [Fact]
    public async Task Pending_reboot_that_existed_before_is_not_attributed()
    {
        var (q, _, _) = Make(null, Item("A"));
        Assert.False((await q.RunAsync()).RebootRequired);
    }

    [Fact]
    public async Task Process_timeout_skips_without_killing_and_continues()
    {
        var opts = Fast with { InteractiveTimeout = TimeSpan.FromMilliseconds(50) };
        var (q, world, _) = Make(opts, Item("A"), Item("B"));
        world.Scripts["A"] = new([new Script(Hangs: true)]);
        var s = await q.RunAsync();
        var sk = Assert.Single(s.Skipped);
        Assert.Equal("A", sk.Item.Program.Name);
        Assert.Contains("Tempo esgotado", sk.Detail, StringComparison.Ordinal);
        Assert.Contains("não foi fechado", sk.Detail, StringComparison.Ordinal);
        Assert.Single(s.Removed);
    }

    [Fact]
    public async Task Quiet_items_use_quiet_timeout()
    {
        var opts = Fast with { InteractiveTimeout = TimeSpan.FromHours(5), QuietTimeout = TimeSpan.FromMilliseconds(50) };
        var (q, world, _) = Make(opts, Item("A", UninstallMode.VendorQuiet));
        world.Scripts["A"] = new([new Script(Hangs: true)]);
        Assert.Single((await q.RunAsync()).Skipped);
    }

    [Fact]
    public async Task Child_process_is_awaited_until_entry_disappears()
    {
        var (q, world, snaps) = Make(null, Item("A"));
        world.Scripts["A"] = new([new Script(Code: 0, Removes: true, RemovesAfterPolls: 4)]);
        var s = await q.RunAsync();
        Assert.Single(s.Removed);
        Assert.Contains(snaps, x => x.Items[0].State == BulkItemState.WaitingForChild);
    }

    [Fact]
    public async Task Child_wait_timeout_skips()
    {
        var (q, world, _) = Make(null, Item("A"), Item("B"));
        world.Scripts["A"] = new([new Script(Code: 0, Removes: false)]);
        var start = world.Now;
        var s = await q.RunAsync();
        var sk = Assert.Single(s.Skipped);
        Assert.Contains("Continua instalado", sk.Detail, StringComparison.Ordinal);
        Assert.True(world.Now - start >= Fast.ChildWait);
        Assert.Single(s.Removed);
    }

    [Fact]
    public async Task Null_exit_code_waits_on_registry()
    {
        var (q, world, _) = Make(null, Item("A"));
        world.Scripts["A"] = new([new Script(Code: null, Removes: true, RemovesAfterPolls: 2)]);
        Assert.Single((await q.RunAsync()).Removed);
    }

    [Theory]
    [InlineData(1602)]
    [InlineData(1223)]
    public async Task Cancel_in_vendor_window_is_skipped(int code)
    {
        var (q, world, _) = Make(null, Item("A"));
        world.Scripts["A"] = new([new Script(Code: code, Removes: false)]);
        var s = await q.RunAsync();
        Assert.Contains("Cancelado", Assert.Single(s.Skipped).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uac_refused_is_skipped_and_start_failure_is_failed()
    {
        var (q, world, _) = Make(null, Item("A"), Item("B"), Item("C"));
        world.Scripts["A"] = new([new Script(UserCancelsUac: true)]);
        world.Scripts["B"] = new([new Script(Refuses: true)]);
        var s = await q.RunAsync();
        Assert.Contains("aviso de administrador", Assert.Single(s.Skipped).Detail, StringComparison.Ordinal);
        Assert.Contains("Não abriu", Assert.Single(s.Failed).Detail, StringComparison.Ordinal);
        Assert.Single(s.Removed);
    }

    [Fact]
    public async Task Stop_after_current_finishes_current_and_skips_rest()
    {
        BulkUninstaller? queue = null;
        var (q, world, _) = Make(null, Item("A"), Item("B"), Item("C"));
        queue = q;
        world.OnStart = n => { if (n == "A") queue.RequestStopAfterCurrent(); };
        var s = await q.RunAsync();
        Assert.Equal(["A"], world.Started);
        Assert.Single(s.Removed);
        Assert.Equal(2, s.Skipped.Count);
        Assert.All(s.Skipped, x => Assert.Contains("parou a fila", x.Detail, StringComparison.Ordinal));
        Assert.True(q.Snapshot().StopRequested);
    }

    [Fact]
    public async Task Skip_current_stops_waiting_but_continues_queue()
    {
        BulkUninstaller? queue = null;
        var (q, world, _) = Make(null, Item("A"), Item("B"));
        queue = q;
        world.Scripts["A"] = new([new Script(Hangs: true)]);
        world.OnStart = n => { if (n == "A") _ = Task.Run(async () => { await Task.Delay(30); queue.SkipCurrent(); }); };
        var s = await q.RunAsync();
        Assert.Contains("Pulado por você", Assert.Single(s.Skipped).Detail, StringComparison.Ordinal);
        Assert.Single(s.Removed);
    }

    [Fact]
    public async Task Registry_read_error_never_claims_removed()
    {
        var world = new FakeWorld();
        var item = Item("A");
        world.Installed.Add("A");
        var q = new BulkUninstaller([item], new FakeRunner(world), _ => throw new IOException("x"), null, Fast with { ChildWait = TimeSpan.FromSeconds(9) },
            null, world.Delay, () => world.Now);
        var s = await q.RunAsync();
        Assert.Empty(s.Removed);
        Assert.Single(s.Skipped);
    }
}

// ====================================================================== resumo

[Trait("Category", "Unit")]
public class ProgramsBulkSummaryTests
{
    private static BulkItemStatus S(string name, BulkItemState state, long? size, string detail = "", int? code = null, bool reboot = false) =>
        new(new BulkQueueItem(BulkFixtures.P(name, "x", size: size), new UninstallCommand(@"C:\x.exe", "", false, null), UninstallMode.Interactive, BulkEligibility.Allowed),
            state, detail, code, reboot);

    [Fact]
    public void Summary_counts_space_and_lists_failures_with_reason()
    {
        var s = BulkSummary.From([
            S("A", BulkItemState.Removed, 2L * 1024 * 1024 * 1024),
            S("B", BulkItemState.Removed, null),
            S("C", BulkItemState.Failed, 10, UninstallExitCodes.Explain(1603), 1603),
            S("D", BulkItemState.Skipped, 10, "Pulado por você."),
        ], windowsPendingChanged: false);
        Assert.Equal(2, s.Removed.Count);
        Assert.Equal(2L * 1024 * 1024 * 1024, s.FreedBytes);
        Assert.Equal(1, s.RemovedWithoutSize);
        Assert.Equal("2 removido(s) · 1 com falha · 1 pulado(s)", s.Headline);
        Assert.Contains("2,0 GB", s.FreedText, StringComparison.Ordinal);
        Assert.Contains("1 sem tamanho", s.FreedText, StringComparison.Ordinal);
        var text = s.Text();
        Assert.Contains("C: O Windows Installer falhou", text, StringComparison.Ordinal);
        Assert.Contains("D (pulado)", text, StringComparison.Ordinal);
        Assert.Equal(["A", "B"], s.RemovedPrograms.Select(p => p.Name));
        Assert.False(s.RebootRequired);
    }

    [Fact]
    public void Nothing_removed_says_so() =>
        Assert.Equal("Nada foi removido.", BulkSummary.From([S("A", BulkItemState.Failed, 1)], false).FreedText);

    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(3010, true, true, false)]
    [InlineData(1641, true, true, false)]
    [InlineData(1618, false, false, false)]
    [InlineData(1602, false, false, true)]
    [InlineData(null, true, false, false)]
    public void Exit_code_classification(int? code, bool success, bool reboot, bool cancelled)
    {
        Assert.Equal(success, UninstallExitCodes.IsSuccess(code));
        Assert.Equal(reboot, UninstallExitCodes.IsRebootRequired(code));
        Assert.Equal(cancelled, UninstallExitCodes.IsCancelled(code));
        Assert.False(string.IsNullOrWhiteSpace(UninstallExitCodes.Explain(code)));
    }

    [Fact]
    public void Inventory_contains_matches_by_key_or_name_and_version()
    {
        var a = BulkFixtures.P("A", "x");
        var other = a with { Id = "Machine32|A", Scope = InstallScope.Machine32 };
        Assert.True(ProgramInventory.Contains([other], a));
        Assert.False(ProgramInventory.Contains([a with { Id = "z", Version = "2.0" }], a));
    }
}
