using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Nexus.Core;
using Nexus.Lab.Gpu;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>
/// Banco de perfis do driver NVIDIA (DRS) falso, com a semântica que o handler espera da NVAPI: perfil base, perfis
/// predefinidos da NVIDIA (com valores predefinidos), perfil "NEXUS - exe" criado na primeira gravação e apagado quando
/// fica vazio, DeleteProfileSetting voltando ao predefinido.
/// </summary>
internal sealed class FakeDrs : INvidiaDrs
{
    public sealed class Profile
    {
        public required string Name { get; init; }
        public HashSet<string> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<uint, (uint Value, bool Predefined)> Settings { get; } = [];
        public Dictionary<uint, uint> Predefined { get; } = [];
    }

    public string? UnavailableReason { get; set; }
    public Profile Base { get; } = new() { Name = "Base Profile" };
    public List<Profile> Profiles { get; } = [];
    public int Writes { get; private set; }

    public Profile AddNvidiaProfile(string name, string exe, uint? settingId = null, uint value = 0)
    {
        var p = new Profile { Name = name };
        p.Apps.Add(exe);
        if (settingId is { } id) { p.Predefined[id] = value; p.Settings[id] = (value, true); }
        Profiles.Add(p);
        return p;
    }

    public Profile? Find(string exe) => Profiles.FirstOrDefault(p => p.Apps.Contains(exe));

    private Profile? Resolve(DrsTarget t) => t.Kind == DrsProfileKind.Base ? Base : Find(t.Exe!);

    public string? Unavailable() => UnavailableReason;

    public DrsReading Read(DrsTarget target, uint settingId)
    {
        if (UnavailableReason is not null) throw new NvidiaDrsException(UnavailableReason);
        if (Resolve(target) is not { } p) return DrsReading.NoProfile;
        return p.Settings.TryGetValue(settingId, out var s)
            ? new DrsReading(true, p.Name, true, s.Value, s.Predefined, s.Value)
            : new DrsReading(true, p.Name, false, null, false, null);
    }

    public void SetUserValue(DrsTarget target, uint settingId, uint value)
    {
        if (UnavailableReason is not null) throw new NvidiaDrsException(UnavailableReason);
        Writes++;
        var p = Resolve(target);
        if (p is null)
        {
            p = new Profile { Name = "NEXUS - " + target.Exe };
            p.Apps.Add(target.Exe!);
            Profiles.Add(p);
        }
        p.Settings[settingId] = (value, false);
    }

    public void ClearUserValue(DrsTarget target, uint settingId)
    {
        if (UnavailableReason is not null) throw new NvidiaDrsException(UnavailableReason);
        Writes++;
        if (Resolve(target) is not { } p) return;
        if (p.Predefined.TryGetValue(settingId, out var pre)) p.Settings[settingId] = (pre, true);
        else p.Settings.Remove(settingId);
        if (target.Kind == DrsProfileKind.App && p.Name == "NEXUS - " + target.Exe && p.Settings.Count == 0 && p.Apps.Count <= 1) Profiles.Remove(p);
    }
}

[Trait("Category", "Unit")]
public sealed class NvidiaDrsTests
{
    private static CatalogDocument Catalog() => CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private static ActionInstance FromCatalog(string key, string? exe, string? value = null)
    {
        var entry = Catalog().Actions.Single(a => a.Definition.Key == key);
        if (value is not null)
        {
            Assert.Contains(value, entry.Definition.AllowedValues!);
            entry = entry with { DesiredValue = value };
        }
        var args = entry.ValidateUserArguments(exe is null ? null : new Dictionary<string, string> { ["exe"] = exe }, out var why);
        Assert.True(args is not null, why);
        var instance = entry.TryInstantiate(args!, out why);
        Assert.True(instance is not null, why);
        return instance!;
    }

    private static ActionInstance Raw(Dictionary<string, string> target, uint value) => new(
        new ActionDefinition("gpu.nv.teste", 1, "t", Classification.Conditional, NvidiaDrsHandler.Kind, "", [], "", PrivilegeLevel.Admin,
            PersistenceKind.Persistent, RebootRequirement.GameRestart),
        target, StateSnapshot.Of("UInt32", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static (FakeDrs Drs, ActionExecutor Exec, DesiredStateResolver Resolver, InMemoryJournal Journal) Engine()
    {
        var drs = new FakeDrs();
        var handlers = new HandlerRegistry().Add(new NvidiaDrsHandler(drs));
        var journal = new InMemoryJournal();
        return (drs, new ActionExecutor(handlers, journal), new DesiredStateResolver(handlers), journal);
    }

    // ------------------------------------------------------------------ Detect/Apply/Restore

    [Fact]
    public void Game_without_profile_gets_a_nexus_profile_and_revert_removes_it()
    {
        var (drs, exec, resolver, journal) = Engine();
        var run = journal.BeginRun("t", "1");
        var action = FromCatalog(NvidiaDrsActions.LowLatency, "cs2.exe");
        var r = exec.Execute(run, resolver.Resolve([action], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        var p = Assert.Single(drs.Profiles);
        Assert.Equal("NEXUS - cs2.exe", p.Name);
        Assert.Equal((1u, false), p.Settings[NvidiaDrsSettings.PrerenderLimit]);
        Assert.False(journal.Entries(run).Single().Original!.Exists); // "ausente" antes

        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Empty(drs.Profiles);
    }

    [Fact]
    public void Two_settings_share_the_nexus_profile_and_it_goes_away_only_with_the_last()
    {
        var (drs, exec, resolver, journal) = Engine();
        var run1 = journal.BeginRun("t", "1");
        exec.Execute(run1, resolver.Resolve([FromCatalog(NvidiaDrsActions.LowLatency, "game.exe")], T.Ctx), T.Ctx);
        var run2 = journal.BeginRun("t", "1");
        exec.Execute(run2, resolver.Resolve([FromCatalog(NvidiaDrsActions.FpsCap, "game.exe", "141")], T.Ctx), T.Ctx);
        Assert.Equal(141u, Assert.Single(drs.Profiles).Settings[NvidiaDrsSettings.FrameRateLimit].Value);

        exec.RevertRun(run1);
        Assert.Single(drs.Profiles); // ainda tem o limite de FPS
        exec.RevertRun(run2);
        Assert.Empty(drs.Profiles);
    }

    [Fact]
    public void Predefined_value_in_an_nvidia_profile_comes_back_on_revert_and_the_profile_stays()
    {
        var (drs, exec, resolver, journal) = Engine();
        var nv = drs.AddNvidiaProfile("Counter-Strike 2", "cs2.exe", NvidiaDrsSettings.VsyncMode, NvidiaDrsSettings.VsyncPassive);
        var action = FromCatalog(NvidiaDrsActions.VsyncOn, "cs2.exe");
        var handler = new NvidiaDrsHandler(drs);
        var before = handler.Detect(action);
        Assert.True(before.Exists);
        Assert.Equal("1", before.Detail![NvidiaDrsHandler.PredefinedDetail]);
        Assert.Equal(ValueOrigin.Inherited, before.Origin);

        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([action], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal((NvidiaDrsSettings.VsyncForceOn, false), nv.Settings[NvidiaDrsSettings.VsyncMode]);

        Assert.Equal(RevertOutcome.Reverted, exec.RevertRun(run).Single().Outcome);
        Assert.Equal((NvidiaDrsSettings.VsyncPassive, true), nv.Settings[NvidiaDrsSettings.VsyncMode]);
        Assert.Contains(nv, drs.Profiles);
    }

    [Fact]
    public void Users_own_value_is_written_back_exactly()
    {
        var (drs, exec, resolver, journal) = Engine();
        var nv = drs.AddNvidiaProfile("Meu jogo", "jogo.exe");
        nv.Settings[NvidiaDrsSettings.PreferredPstate] = (3, false); // "desempenho consistente", escolhido no painel
        var run = journal.BeginRun("t", "1");
        exec.Execute(run, resolver.Resolve([FromCatalog(NvidiaDrsActions.MaxPower, "jogo.exe")], T.Ctx), T.Ctx);
        Assert.Equal(1u, nv.Settings[NvidiaDrsSettings.PreferredPstate].Value);
        exec.RevertRun(run);
        Assert.Equal((3u, false), nv.Settings[NvidiaDrsSettings.PreferredPstate]);
    }

    [Fact]
    public void Shader_cache_goes_to_the_base_profile_and_back_to_driver_default()
    {
        var (drs, exec, resolver, journal) = Engine();
        var run = journal.BeginRun("t", "1");
        var r = exec.Execute(run, resolver.Resolve([FromCatalog(NvidiaDrsActions.ShaderCache, null, "4294967295")], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.Equal(0xFFFFFFFFu, drs.Base.Settings[NvidiaDrsSettings.ShaderCacheMaxSize].Value);
        exec.RevertRun(run);
        Assert.False(drs.Base.Settings.ContainsKey(NvidiaDrsSettings.ShaderCacheMaxSize));
    }

    [Fact]
    public void Already_configured_value_writes_nothing()
    {
        var (drs, exec, resolver, journal) = Engine();
        drs.AddNvidiaProfile("Jogo", "jogo.exe").Settings[NvidiaDrsSettings.PrerenderLimit] = (1, false);
        var run = journal.BeginRun("t", "1");
        var r = exec.Execute(run, resolver.Resolve([FromCatalog(NvidiaDrsActions.LowLatency, "jogo.exe")], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.AlreadyConfigured, r.Outcome);
        Assert.Equal(0, drs.Writes);
    }

    [Fact]
    public void Without_nvidia_driver_the_action_is_not_applicable_and_nothing_is_called()
    {
        var (drs, exec, resolver, journal) = Engine();
        drs.UnavailableReason = "Sem placa/driver NVIDIA (nvapi64.dll não está no System32).";
        var action = FromCatalog(NvidiaDrsActions.LowLatency, "cs2.exe");
        var a = new NvidiaDrsHandler(drs).Assess(action, T.Ctx);
        Assert.Equal(ApplicabilityKind.NotApplicable, a.Kind);
        Assert.StartsWith("Sem placa/driver NVIDIA", a.Reason, StringComparison.Ordinal);
        var run = journal.BeginRun("t", "1");
        Assert.Equal(ExecutionOutcome.NotApplicable, exec.Execute(run, resolver.Resolve([action], T.Ctx), T.Ctx).Single().Outcome);
        Assert.Equal(0, drs.Writes);
        Assert.False(new NvidiaDrsHandler(drs).Detect(action).Exists);
        Assert.NotNull(new NvidiaDrsHandler(drs).CheckRestore(action, StateSnapshot.Absent()));
    }

    [Fact]
    public void No_nvidia_backend_on_linux_reports_unavailable()
    {
        var handler = GpuLabHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, true))
            .OfType<NvidiaDrsHandler>().Single();
        var a = handler.Assess(FromCatalog(NvidiaDrsActions.MaxPower, "cs2.exe"), T.Ctx);
        if (!OperatingSystem.IsWindows()) Assert.Equal(ApplicabilityKind.NotApplicable, a.Kind);
        Assert.NotNull(new NoNvidiaDrs().Unavailable());
        Assert.Throws<NvidiaDrsException>(() => new NoNvidiaDrs().Read(DrsTarget.Base, NvidiaDrsSettings.ShaderCacheMaxSize));
    }

    [Fact]
    public void Ambiguous_executable_is_blocked_with_the_reason()
    {
        var drs = new AmbiguousDrs();
        var a = new NvidiaDrsHandler(drs).Assess(FromCatalog(NvidiaDrsActions.LowLatency, "game.exe"), T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, a.Kind);
        Assert.Contains("-182", a.Reason, StringComparison.Ordinal);
    }

    private sealed class AmbiguousDrs : INvidiaDrs
    {
        public string? Unavailable() => null;
        public DrsReading Read(DrsTarget target, uint settingId) => throw new NvidiaDrsException("NvAPI_DRS_FindApplicationByName", NvApiStatus.ExecutablePathIsAmbiguous);
        public void SetUserValue(DrsTarget target, uint settingId, uint value) => throw new InvalidOperationException();
        public void ClearUserValue(DrsTarget target, uint settingId) => throw new InvalidOperationException();
    }

    // ------------------------------------------------------------------ lista fechada

    [Fact]
    public void Uncapping_values_are_accepted_fps_limit_off_and_vsync_forced_off()
    {
        // Diretriz do responsável (06/11/2026): o NEXUS nunca limita FPS; precisa conseguir TIRAR limites do driver.
        foreach (var (setting, value) in new[] { ("0x10835002", NvidiaDrsSettings.FpsUnlimited), ("0x00A879CF", NvidiaDrsSettings.VsyncForceOff) })
        {
            var drs = new FakeDrs();
            var handler = new NvidiaDrsHandler(drs);
            var action = Raw(new Dictionary<string, string> { ["profile"] = "app", ["exe"] = "cs2.exe", ["setting"] = setting }, value);
            Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(action, T.Ctx).Kind);
            handler.Apply(action);
            Assert.Equal(1, drs.Writes);
        }
        Assert.Equal(138504007u, NvidiaDrsSettings.VsyncForceOff);
        // No perfil global também: tirar limite e desligar V-Sync (o que o NVIDIA App deixou ligado para todos os jogos).
        foreach (var (setting, value) in new[] { ("0x10835002", NvidiaDrsSettings.FpsUnlimited), ("0x00A879CF", NvidiaDrsSettings.VsyncForceOff) })
        {
            var drs = new FakeDrs();
            var action = Raw(new Dictionary<string, string> { ["profile"] = "base", ["setting"] = setting }, value);
            Assert.Equal(ApplicabilityKind.Applicable, new NvidiaDrsHandler(drs).Assess(action, T.Ctx).Kind);
        }
    }

    [Theory]
    [InlineData("0x12345678", "app", 1u)]       // id fora da lista
    [InlineData("0x1057eb7", "app", 1u)]        // id malformado
    [InlineData("1057EB71", "app", 1u)]         // sem 0x
    [InlineData("0x1057EB71", "base", 0u)]      // global: só "desempenho máximo" (1), nenhum outro modo
    [InlineData("0x20D690F8", "app", 1u)]       // Vulkan/OpenGL: só no global
    [InlineData("0x20D690F8", "base", 0u)]      // Vulkan/OpenGL: só a camada DXGI (1)
    [InlineData("0x1057EB71", "app", 4u)]       // valor que o NEXUS não aplica
    [InlineData("0x007BA09E", "app", 3u)]       // só "ligado" (1)
    [InlineData("0x10835002", "app", 1024u)]    // acima de FRL_FPS_MAX
    [InlineData("0x10835002", "app", 5u)]       // abaixo do mínimo útil
    [InlineData("0x00A879CF", "app", 0x18888888u)] // só FORCEON ou FORCEOFF (Fast Sync, 1/2 da taxa etc. não)
    [InlineData("0x00A879CF", "app", 0x32610244u)] // 1/2 da taxa: prende o FPS, nunca
    [InlineData("0x10835002", "base", 60u)]     // no global só TIRAR limite (0), nunca pôr
    [InlineData("0x00A879CF", "base", 0x47814940u)] // no global só desligar o V-Sync, nunca forçar
    [InlineData("0x00AC8497", "app", 102400u)]  // cache só no global
    [InlineData("0x00AC8497", "base", 1024u)]   // tamanho fora das opções
    public void Settings_or_values_outside_the_allowlist_are_blocked(string setting, string profile, uint value)
    {
        var target = new Dictionary<string, string> { ["profile"] = profile, ["setting"] = setting };
        if (profile == "app") target["exe"] = "cs2.exe";
        var drs = new FakeDrs();
        var handler = new NvidiaDrsHandler(drs);
        var action = Raw(target, value);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(action, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => handler.Apply(action));
        Assert.Equal(0, drs.Writes);
    }

    [Theory]
    [InlineData("0x1057EB71", 1u)]
    [InlineData("0x007BA09E", 1u)]
    [InlineData("0x10835002", 141u)]
    [InlineData("0x10835002", 20u)]
    [InlineData("0x10835002", 1023u)]
    [InlineData("0x00A879CF", 0x47814940u)]
    public void Allowed_per_game_values_pass(string setting, uint value)
    {
        var action = Raw(new Dictionary<string, string> { ["profile"] = "app", ["exe"] = "cs2.exe", ["setting"] = setting }, value);
        Assert.Equal(ApplicabilityKind.Applicable, new NvidiaDrsHandler(new FakeDrs()).Assess(action, T.Ctx).Kind);
    }

    [Fact]
    public void Wrong_value_type_and_base_with_exe_are_blocked()
    {
        var handler = new NvidiaDrsHandler(new FakeDrs());
        var t = new Dictionary<string, string> { ["profile"] = "app", ["exe"] = "cs2.exe", ["setting"] = "0x007BA09E" };
        var wrongType = Raw(t, 1) with { Desired = StateSnapshot.Of("DWord", "1") };
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(wrongType, T.Ctx).Kind);
        var hexValue = Raw(t, 1) with { Desired = StateSnapshot.Of("UInt32", "0x1") };
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(hexValue, T.Ctx).Kind);
        var baseWithExe = Raw(new Dictionary<string, string> { ["profile"] = "base", ["exe"] = "cs2.exe", ["setting"] = "0x00AC8497" }, 102400);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(baseWithExe, T.Ctx).Kind);
        var noProfile = Raw(new Dictionary<string, string> { ["setting"] = "0x00AC8497" }, 102400);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(noProfile, T.Ctx).Kind);
    }

    [Fact]
    public void Restore_validates_the_original_against_the_header_ranges()
    {
        var handler = new NvidiaDrsHandler(new FakeDrs());
        var vsync = FromCatalog(NvidiaDrsActions.VsyncOn, "cs2.exe");
        Assert.Null(handler.CheckRestore(vsync, StateSnapshot.Absent()));
        Assert.Null(handler.CheckRestore(vsync, StateSnapshot.Of("UInt32", NvidiaDrsSettings.VsyncPassive.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.NotNull(handler.CheckRestore(vsync, StateSnapshot.Of("UInt32", "12345")));     // não é valor de VSYNCMODE
        Assert.NotNull(handler.CheckRestore(vsync, StateSnapshot.Of("Token", "1")));
        var pstate = FromCatalog(NvidiaDrsActions.MaxPower, "cs2.exe");
        Assert.Null(handler.CheckRestore(pstate, StateSnapshot.Of("UInt32", "4")));
        Assert.NotNull(handler.CheckRestore(pstate, StateSnapshot.Of("UInt32", "6")));
        var fps = FromCatalog(NvidiaDrsActions.FpsCap, "cs2.exe", "141");
        Assert.Null(handler.CheckRestore(fps, StateSnapshot.Of("UInt32", "0")));
        Assert.NotNull(handler.CheckRestore(fps, StateSnapshot.Of("UInt32", "5000")));
        // Alvo do diário adulterado (id fora da lista) é recusado antes de qualquer escrita.
        var tampered = vsync with { Target = new Dictionary<string, string>(vsync.Target) { ["setting"] = "0x20AB1234" } };
        Assert.NotNull(handler.CheckRestore(tampered, StateSnapshot.Absent()));
        Assert.Throws<InvalidOperationException>(() => handler.Restore(tampered, StateSnapshot.Absent()));
    }

    [Theory]
    [InlineData("cs2.exe", true)]
    [InlineData("Cyberpunk2077.exe", true)]
    [InlineData("r5apex (dx12).exe", true)]
    [InlineData("eldenring.EXE", true)]
    [InlineData("Game-Win64-Shipping.exe", true)]
    [InlineData(@"C:\Games\cs2.exe", false)]
    [InlineData(@"..\cs2.exe", false)]
    [InlineData("a..exe", false)]
    [InlineData("cs2.exe\n", false)]
    [InlineData(" cs2.exe", false)]
    [InlineData("cs2.dll", false)]
    [InlineData("cs2.exe.txt", false)]
    [InlineData(".exe", false)]
    [InlineData("jogo/sub.exe", false)]
    [InlineData("jogo*.exe", false)]
    [InlineData("", false)]
    public void Executable_must_be_a_bare_file_name(string exe, bool ok)
    {
        Assert.Equal(ok, NvidiaDrsSettings.IsValidExe(exe));
        var entry = Catalog().Actions.Single(a => a.Definition.Key == NvidiaDrsActions.LowLatency);
        if (exe.Length > 0)
        {
            var catalogOk = entry.ValidateUserArguments(new Dictionary<string, string> { ["exe"] = exe }, out _) is not null;
            // A regra do catálogo é a mesma (o handler ainda recusa "a..exe" e espaço nas pontas).
            if (catalogOk && !ok) Assert.Throws<InvalidOperationException>(() => new NvidiaDrsHandler(new FakeDrs()).Apply(FromCatalogUnchecked(entry, exe)));
            if (ok) Assert.True(catalogOk);
        }
    }

    private static ActionInstance FromCatalogUnchecked(CatalogEntry entry, string exe) =>
        entry.TryInstantiate(new Dictionary<string, string> { ["exe"] = exe }, out _)!;

    [Fact]
    public void Executable_longer_than_64_characters_is_refused()
    {
        Assert.True(NvidiaDrsSettings.IsValidExe(new string('a', 64) + ".exe"));
        Assert.False(NvidiaDrsSettings.IsValidExe(new string('a', 65) + ".exe"));
    }

    [Fact]
    public void Fps_suggestion_is_three_below_refresh_and_fits_the_catalog_list()
    {
        Assert.Equal(141, NvidiaDrsSettings.SuggestedFpsCap(144));
        Assert.Equal("141", NvidiaDrsSettings.NearestAllowedCap(144, NvidiaDrsActions.FpsCaps));
        Assert.Equal("57", NvidiaDrsSettings.NearestAllowedCap(60, NvidiaDrsActions.FpsCaps));
        Assert.Equal("162", NvidiaDrsSettings.NearestAllowedCap(165, NvidiaDrsActions.FpsCaps));
        Assert.Equal("237", NvidiaDrsSettings.NearestAllowedCap(240, NvidiaDrsActions.FpsCaps));
        Assert.Equal("120", NvidiaDrsSettings.NearestAllowedCap(125, NvidiaDrsActions.FpsCaps));
        Assert.Equal("240", NvidiaDrsSettings.NearestAllowedCap(360, NvidiaDrsActions.FpsCaps));
        Assert.Null(NvidiaDrsSettings.NearestAllowedCap(24, NvidiaDrsActions.FpsCaps));
    }

    [Fact]
    public void Setting_descriptions_cover_known_values()
    {
        Assert.Equal("ilimitado", NvidiaDrsSettings.All[NvidiaDrsSettings.ShaderCacheMaxSize].Describe(0xFFFFFFFF));
        Assert.Equal("100 GB", NvidiaDrsSettings.All[NvidiaDrsSettings.ShaderCacheMaxSize].Describe(102400));
        Assert.Equal("16 GB", NvidiaDrsSettings.All[NvidiaDrsSettings.ShaderCacheMaxSize].Describe(NvidiaDrsSettings.ShaderCacheDefaultMb));
        Assert.Equal("ligada", NvidiaDrsSettings.All[NvidiaDrsSettings.VsyncMode].Describe(NvidiaDrsSettings.VsyncForceOn));
        Assert.Equal("141 FPS", NvidiaDrsSettings.All[NvidiaDrsSettings.FrameRateLimit].Describe(141));
        Assert.Contains("-182", NvApiStatus.Describe(NvApiStatus.ExecutablePathIsAmbiguous), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ catálogo

    [Fact]
    public void Catalog_entries_load_instantiate_and_use_only_allowed_values()
    {
        var all = Catalog();
        var mine = all.Actions.Where(a => a.Definition.Id.StartsWith("gpu.nv.", StringComparison.Ordinal)).ToList();
        Assert.Equal(11, mine.Count); // 5 + 4 que TIRAM limite de FPS + desempenho máximo e Vulkan/OpenGL no global (06/11/2026)
        var handlerKinds = GpuLabHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, false))
            .Select(h => h.HandlerKind).ToHashSet(StringComparer.Ordinal);
        var handler = new NvidiaDrsHandler(new FakeDrs());
        foreach (var e in mine)
        {
            var d = e.Definition;
            Assert.Equal(NvidiaDrsHandler.Kind, d.HandlerKind);
            Assert.Contains(d.HandlerKind, handlerKinds);
            Assert.Equal(PrivilegeLevel.Admin, d.Privileges);
            Assert.Equal(PersistenceKind.Persistent, d.Persistence);
            Assert.Equal("UInt32", e.DesiredType);
            Assert.NotEmpty(d.Evidence);
            Assert.Contains(d.Evidence, x => x.Source.Contains("NvApiDriverSettings.h", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(d.Mechanism) || string.IsNullOrWhiteSpace(d.Risks) || string.IsNullOrWhiteSpace(e.Revert) || string.IsNullOrWhiteSpace(e.Condition));
            var parsed = NvidiaDrsSettings.ParseTarget(e.TargetTemplate.ToDictionary(kv => kv.Key, kv => kv.Value == "{exe}" ? "x.exe" : kv.Value), out var why);
            Assert.True(parsed is not null, why);
            var values = e.DesiredValue is not null ? [e.DesiredValue] : d.AllowedValues!;
            foreach (var v in values)
            {
                var exe = e.UserParameters.ContainsKey("exe") ? "cs2.exe" : null;
                var instance = FromCatalog(d.Key, exe, e.DesiredValue is null ? v : null);
                Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(instance, T.Ctx).Kind);
            }
        }
        var fps = mine.Single(e => e.Definition.Key == NvidiaDrsActions.FpsCap);
        Assert.Equal(NvidiaDrsActions.FpsCaps, fps.Definition.AllowedValues);
        Assert.Equal(NvidiaDrsActions.ShaderCacheValues, mine.Single(e => e.Definition.Key == NvidiaDrsActions.ShaderCache).Definition.AllowedValues);
        foreach (var key in new[] { NvidiaDrsActions.LowLatency, NvidiaDrsActions.MaxPower, NvidiaDrsActions.VsyncOn,
                                    NvidiaDrsActions.FpsCapOff, NvidiaDrsActions.VsyncOff, NvidiaDrsActions.FpsCapOffGlobal, NvidiaDrsActions.VsyncOffGlobal })
            Assert.Contains(mine, e => e.Definition.Key == key);
        Assert.Contains(all.Rejected, r => r.Id == "gpu.nv.lowlatency.ultra");
        // Decisão do responsável (06/11/2026): desempenho acima de consumo; o global "desempenho máximo" virou ação.
        Assert.DoesNotContain(all.Rejected, r => r.Id == "gpu.nv.power.max.global");
        Assert.Contains(mine, e => e.Definition.Key == NvidiaDrsActions.MaxPowerGlobal);
        Assert.Contains(mine, e => e.Definition.Key == NvidiaDrsActions.VkGlPresentGlobal);
    }

    [Fact]
    public void Catalog_ids_match_the_nvidia_header_constants()
    {
        // NvApiDriverSettings.h (NVIDIA/nvapi): conferido à mão; este teste trava a correspondência catálogo ↔ constantes.
        Assert.Equal(0x1057EB71u, NvidiaDrsSettings.PreferredPstate);
        Assert.Equal(0x007BA09Eu, NvidiaDrsSettings.PrerenderLimit);
        Assert.Equal(0x10835002u, NvidiaDrsSettings.FrameRateLimit);
        Assert.Equal(0x00A879CFu, NvidiaDrsSettings.VsyncMode);
        Assert.Equal(0x00AC8497u, NvidiaDrsSettings.ShaderCacheMaxSize);
        Assert.Equal(0x47814940u, NvidiaDrsSettings.VsyncForceOn);
        Assert.Equal(1199655232u, NvidiaDrsSettings.VsyncForceOn);
        var byKey = Catalog().Actions.ToDictionary(a => a.Definition.Key, StringComparer.Ordinal);
        Assert.Equal("0x007BA09E", byKey[NvidiaDrsActions.LowLatency].TargetTemplate["setting"]);
        Assert.Equal("0x1057EB71", byKey[NvidiaDrsActions.MaxPower].TargetTemplate["setting"]);
        Assert.Equal("0x10835002", byKey[NvidiaDrsActions.FpsCap].TargetTemplate["setting"]);
        Assert.Equal("0x00A879CF", byKey[NvidiaDrsActions.VsyncOn].TargetTemplate["setting"]);
        Assert.Equal("0x00AC8497", byKey[NvidiaDrsActions.ShaderCache].TargetTemplate["setting"]);
    }

    // ------------------------------------------------------------------ interface só lê

    [Fact]
    public void Desktop_never_references_the_drs_writes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var path = Path.Combine(dir!.FullName, "src", "Nexus.Desktop", "bin", config, "net10.0-windows", "Nexus.Desktop.dll");
        Assert.True(File.Exists(path), $"Compile a solução inteira antes dos testes ({path}).");
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var refs = md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(mr => mr.Parent.Kind == HandleKind.TypeReference)
            .Select(mr => (md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name), md.GetString(mr.Name)))
            .ToHashSet();
        foreach (var type in new[] { "INvidiaDrs", "WindowsNvidiaDrs", "NoNvidiaDrs", "NvidiaDrsHandler" })
            foreach (var member in new[] { "SetUserValue", "ClearUserValue", "Apply", "Restore", ".ctor" })
                Assert.DoesNotContain((type, member), refs);
        // Controle: a página lê pelo leitor.
        Assert.Contains(("NvidiaDrsFactory", "Reader"), refs);
        Assert.Contains(("INvidiaDrsReader", "Read"), refs);
    }
}
