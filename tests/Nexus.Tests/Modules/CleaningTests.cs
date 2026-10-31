using Nexus.Cleaning;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>Pasta temporária real com a estrutura de um perfil e da máquina (funciona em Linux e Windows).</summary>
internal sealed class CleaningSandbox : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("nexus-limpeza-").FullName;
    public string Data => Path.Combine(Root, "data");
    public CleaningFolders Folders { get; }
    public DateTime Now { get; } = DateTime.UtcNow;

    public CleaningSandbox()
    {
        var profile = Path.Combine(Root, "Users", "ana");
        Folders = new CleaningFolders(profile, Path.Combine(profile, "AppData", "Local", "Temp"), Path.Combine(profile, "AppData", "Local"),
            Path.Combine(profile, "AppData", "LocalLow"), Path.Combine(profile, "AppData", "Roaming"), Path.Combine(Root, "ProgramData"),
            Path.Combine(Root, "Windows"), Path.Combine(Root, "Steam"));
        Directory.CreateDirectory(Folders.UserTemp!);
        Directory.CreateDirectory(Data);
    }

    public string File(string path, int bytes = 100, double ageHours = 48)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', bytes).ToArray());
        System.IO.File.SetLastWriteTimeUtc(path, Now.AddHours(-ageHours));
        return path;
    }

    public string Temp(string relative, int bytes = 100, double ageHours = 48) => File(Path.Combine(Folders.UserTemp!, relative), bytes, ageHours);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FakeProcesses(params string[] running) : IProcessProbe
{
    public HashSet<string> Running { get; } = new(running, StringComparer.OrdinalIgnoreCase);
    public bool IsRunning(string processName) => Running.Contains(processName);
}

/// <summary>Operações reais de arquivo, mas com "arquivo em uso" simulado (Linux não tem travas obrigatórias).</summary>
internal sealed class InUseOps : IQuarantineFileOps
{
    public HashSet<string> InUse { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsInUse(string path) => InUse.Contains(path);
    public void MoveFile(string source, string destination) => RealQuarantineFileOps.Instance.MoveFile(source, destination);
}

[Trait("Category", "Unit")]
public sealed class CleaningRuleTests
{
    private static CleaningRule Rule(string id) => CleaningRuleSet.BuiltIn.Find(id)!;

    [Fact]
    public void Built_in_rules_load_and_cover_the_required_areas()
    {
        var set = CleaningRuleSet.BuiltIn;
        foreach (var id in new[] { "temp-user", "temp-windows", "thumbnails", "wer-user", "wer-system", "logs-cbs", "delivery-optimization", "windows-update-download",
                     "recycle-bin", "chrome-cache", "edge-cache", "brave-cache", "opera-cache", "firefox-cache", "chrome-history", "chrome-cookies", "discord-cache",
                     "spotify-cache", "teams-cache", "slack-cache", "shader-nvidia", "shader-amd", "shader-intel", "shader-directx", "steam-htmlcache", "steam-logs",
                     "steam-shadercache", "epic-webcache", "epic-logs", "battlenet-cache", "crashdumps-user", "crashdumps-system" })
            Assert.NotNull(set.Find(id));
        Assert.All(set.Rules.Where(r => r.Sensitive), r => Assert.False(r.DefaultSelected));
        Assert.All(set.Rules.Where(r => r.Id.StartsWith("shader-", StringComparison.Ordinal)), r => Assert.False(r.DefaultSelected));
        // Irreversíveis ou de serviço: só medir.
        Assert.Equal(CleaningMode.MeasureOnly, set.Find("recycle-bin")!.Mode);
        Assert.Equal(CleaningMode.MeasureOnly, set.Find("windows-update-download")!.Mode);
        Assert.Equal(CleaningMode.MeasureOnly, set.Find("delivery-optimization")!.Mode);
        // Caches de navegador nunca incluem cookies/histórico/senhas/sessões.
        foreach (var r in set.Rules.Where(r => r.Id.EndsWith("-cache", StringComparison.Ordinal) && r.Category == CleaningCategory.Navegadores))
            Assert.All(r.Roots, root => Assert.DoesNotMatch("(?i)cookies|history|login data|sessions|local storage", root.Template));
    }

    [Theory]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Sistema","scope":"User","mode":"Quarantine","roots":["%LocalAppData%\\..\\Documents"],"patterns":["*"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Sistema","scope":"Machine","admin":true,"mode":"Quarantine","roots":["%LocalAppData%\\Temp"],"patterns":["*"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Sistema","scope":"User","mode":"Quarantine","roots":["C:\\Temp"],"patterns":["*"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Sistema","scope":"User","mode":"Quarantine","sensitive":true,"defaultSelected":true,"warning":"w","roots":["%LocalAppData%\\A"],"patterns":["*"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Sistema","scope":"Machine","admin":true,"mode":"Quarantine","roots":["%WinDir%"],"patterns":["*"],"recursive":true,"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Sistema","scope":"User","mode":"Quarantine","roots":["%LocalAppData%\\A"],"patterns":["*"],"closeApps":["c:\\evil.exe"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    public void Malformed_or_unsafe_rules_are_rejected(string json) =>
        Assert.Throws<InvalidDataException>(() => CleaningRuleSet.Parse(json));

    [Fact]
    public void Eligibility_respects_age_and_pattern()
    {
        using var s = new CleaningSandbox();
        var old = s.Temp("velho.tmp", ageHours: 48);
        var fresh = s.Temp("novo.tmp", ageHours: 1);
        var nested = s.Temp(Path.Combine("sub", "dentro.log"), ageHours: 30);
        var rule = Rule("temp-user");
        var found = CleaningEnumerator.Enumerate(rule, CleaningPaths.Expand(rule, s.Folders), s.Now).Select(c => c.Path).ToList();
        Assert.Contains(old, found);
        Assert.Contains(nested, found);
        Assert.DoesNotContain(fresh, found);

        var explorer = Path.Combine(s.Folders.LocalAppData!, "Microsoft", "Windows", "Explorer");
        var thumb = s.File(Path.Combine(explorer, "thumbcache_256.db"));
        var other = s.File(Path.Combine(explorer, "explorer-config.db"));
        var thumbs = Rule("thumbnails");
        var t = CleaningEnumerator.Enumerate(thumbs, CleaningPaths.Expand(thumbs, s.Folders), s.Now).Select(c => c.Path).ToList();
        Assert.Equal([thumb], t);
        Assert.DoesNotContain(other, t);
    }

    [Fact]
    public void Exclusions_and_non_recursive_roots_are_honored()
    {
        using var s = new CleaningSandbox();
        var set = CleaningRuleSet.Parse("""
        {"rules":[{"id":"teste","title":"t","category":"Apps","scope":"User","mode":"Quarantine","roots":["%LocalAppData%\\App\\Cache"],
          "patterns":["*"],"exclude":["*.keep"],"excludeDirs":["guardar"],"recursive":true,"explanation":"e","expectedGain":"g","evidence":"v"},
         {"id":"raso","title":"t","category":"Apps","scope":"User","mode":"Quarantine","roots":["%LocalAppData%\\App\\Cache"],
          "patterns":["*.bin"],"recursive":false,"explanation":"e","expectedGain":"g","evidence":"v"}]}
        """);
        var cache = Path.Combine(s.Folders.LocalAppData!, "App", "Cache");
        var a = s.File(Path.Combine(cache, "a.bin"));
        var keep = s.File(Path.Combine(cache, "b.keep"));
        var guarded = s.File(Path.Combine(cache, "guardar", "c.bin"));
        var deep = s.File(Path.Combine(cache, "sub", "d.bin"));
        var rule = set.Find("teste")!;
        var found = CleaningEnumerator.Enumerate(rule, CleaningPaths.Expand(rule, s.Folders), s.Now).Select(c => c.Path).ToList();
        Assert.Contains(a, found);
        Assert.Contains(deep, found);
        Assert.DoesNotContain(keep, found);
        Assert.DoesNotContain(guarded, found);

        var shallow = set.Find("raso")!;
        Assert.Equal([a], CleaningEnumerator.Enumerate(shallow, CleaningPaths.Expand(shallow, s.Folders), s.Now).Select(c => c.Path).ToList());
    }

    [Fact]
    public void Wildcard_profiles_expand_only_to_real_directories()
    {
        using var s = new CleaningSandbox();
        var userData = Path.Combine(s.Folders.LocalAppData!, "Google", "Chrome", "User Data");
        var c1 = s.File(Path.Combine(userData, "Default", "Cache", "Cache_Data", "f_000001"), ageHours: 0);
        var c2 = s.File(Path.Combine(userData, "Profile 1", "Code Cache", "js", "abc"), ageHours: 0);
        var cookies = s.File(Path.Combine(userData, "Default", "Network", "Cookies"), ageHours: 0);
        var history = s.File(Path.Combine(userData, "Default", "History"), ageHours: 0);
        var rule = Rule("chrome-cache");
        var found = CleaningEnumerator.Enumerate(rule, CleaningPaths.Expand(rule, s.Folders), s.Now).Select(c => c.Path).ToList();
        Assert.Contains(c1, found);
        Assert.Contains(c2, found);
        Assert.DoesNotContain(cookies, found);
        Assert.DoesNotContain(history, found);

        var hist = Rule("chrome-history");
        Assert.Equal([history], CleaningEnumerator.Enumerate(hist, CleaningPaths.Expand(hist, s.Folders), s.Now).Select(c => c.Path).ToList());
    }

    [Fact]
    public void Reparse_points_are_never_followed()
    {
        using var s = new CleaningSandbox();
        var outside = Path.Combine(s.Root, "fora");
        var victim = s.File(Path.Combine(outside, "importante.docx"));
        var normal = s.Temp("lixo.tmp");
        Directory.CreateSymbolicLink(Path.Combine(s.Folders.UserTemp!, "junção"), outside);
        File.CreateSymbolicLink(Path.Combine(s.Folders.UserTemp!, "link.tmp"), victim);
        var rule = Rule("temp-user");
        var stats = new CleaningEnumerationStats();
        var found = CleaningEnumerator.Enumerate(rule, CleaningPaths.Expand(rule, s.Folders), s.Now, stats).Select(c => c.Path).ToList();
        Assert.Equal([normal], found);
        Assert.Equal(2, stats.ReparseSkipped);

        // Raiz inteira que é um link: não expande.
        var userData = Path.Combine(s.Folders.LocalAppData!, "Google", "Chrome", "User Data");
        Directory.CreateDirectory(userData);
        Directory.CreateSymbolicLink(Path.Combine(userData, "Default"), outside);
        Assert.Empty(CleaningPaths.Expand(Rule("chrome-cache"), s.Folders));
    }

    [Fact]
    public void Targets_outside_the_rule_or_protected_are_refused()
    {
        using var s = new CleaningSandbox();
        var rule = Rule("temp-user");
        Assert.True(CleaningPaths.IsWithinRule(rule, s.Folders, Path.Combine(s.Folders.UserTemp!, "a", "b.tmp")));
        Assert.False(CleaningPaths.IsWithinRule(rule, s.Folders, Path.Combine(s.Folders.UserProfile!, "Documents", "tese.docx")));
        Assert.False(CleaningPaths.IsWithinRule(rule, s.Folders, Path.Combine(s.Folders.UserTemp!, "..", "x.tmp")));
        Assert.False(CleaningPaths.IsWithinRule(Rule("thumbnails"), s.Folders, Path.Combine(s.Folders.LocalAppData!, "Microsoft", "Windows", "Explorer", "outro.db")));
        Assert.False(CleaningPaths.IsWithinRule(Rule("crashdumps-user"), s.Folders, Path.Combine(s.Folders.LocalAppData!, "CrashDumps", "sub", "a.dmp")));

        Assert.NotNull(CleaningPaths.ForbiddenReason(s.Folders.UserProfile!, s.Folders));
        Assert.NotNull(CleaningPaths.ForbiddenReason(Path.Combine(s.Folders.UserProfile!, "Desktop", "x"), s.Folders));
        Assert.NotNull(CleaningPaths.ForbiddenReason(Path.Combine(s.Folders.WinDir!, "System32", "drivers"), s.Folders));
        Assert.NotNull(CleaningPaths.ForbiddenReason(Path.Combine(s.Folders.WinDir!, "WinSxS"), s.Folders));
        Assert.NotNull(CleaningPaths.ForbiddenReason(s.Folders.WinDir!, s.Folders));
        Assert.NotNull(CleaningPaths.ForbiddenReason(s.Folders.LocalAppData!, s.Folders));
        Assert.Null(CleaningPaths.ForbiddenReason(s.Folders.UserTemp!, s.Folders));
        // MEMORY.DMP: só o nome exato, direto em %WinDir%.
        var dumps = Rule("crashdumps-system");
        Assert.True(CleaningPaths.IsWithinRule(dumps, s.Folders, Path.Combine(s.Folders.WinDir!, "MEMORY.DMP")));
        Assert.False(CleaningPaths.IsWithinRule(dumps, s.Folders, Path.Combine(s.Folders.WinDir!, "explorer.exe")));
        Assert.False(CleaningPaths.IsWithinRule(dumps, s.Folders, Path.Combine(s.Folders.WinDir!, "System32", "MEMORY.DMP")));
    }

    [Fact]
    public async Task Scanner_measures_bytes_and_reports_open_apps()
    {
        using var s = new CleaningSandbox();
        s.Temp("a.tmp", bytes: 1000);
        s.Temp("b.tmp", bytes: 500);
        s.Temp("c.tmp", bytes: 7, ageHours: 0);
        s.File(Path.Combine(s.Folders.LocalAppData!, "Google", "Chrome", "User Data", "Default", "Cache", "x"), bytes: 42, ageHours: 0);
        var rules = new[] { Rule("temp-user"), Rule("chrome-cache"), Rule("recycle-bin"), Rule("steam-logs") };
        var results = await CleaningScanner.ScanAsync(rules, s.Folders, new FakeProcesses("chrome"), s.Now, () => (3, 999));
        Assert.Equal((2L, 1500L), (results[0].Files, results[0].Bytes));
        Assert.Equal(42, results[1].Bytes);
        Assert.Equal(["chrome"], results[1].RunningApps);
        Assert.Equal(999, results[2].Bytes);
        Assert.Equal(0, results[3].Bytes);

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CleaningScanner.ScanAsync(rules, s.Folders, new FakeProcesses(), s.Now, cancel: cancel.Token));
    }
}

[Trait("Category", "Unit")]
public sealed class CleaningHandlerTests
{
    private static CleaningRuleSet Set => CleaningRuleSet.BuiltIn;
    private static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    private static ActionInstance Action(string ruleId, string batch, string? sid = null)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == "cleaning." + ruleId);
        var args = entry.ValidateUserArguments(new Dictionary<string, string> { ["batch"] = batch }, out var reason);
        Assert.True(args is not null, reason);
        var a = entry.TryInstantiate(args!, out reason);
        Assert.True(a is not null, reason);
        return sid is null ? a! : UserProfileScope.Bind(a!, sid);
    }

    private static (CleaningRuleHandler Handler, InUseOps Ops, FakeProcesses Procs) Handler(CleaningSandbox s, bool otherAccount = false, CleaningFolders? folders = null)
    {
        var ops = new InUseOps();
        var procs = new FakeProcesses();
        return (new CleaningRuleHandler(Set, s.Data, _ => folders ?? s.Folders, procs, otherAccount, ops), ops, procs);
    }

    private static string NewBatch() => CleaningBatchId.New(DateTime.UtcNow);

    [Fact]
    public void Catalog_has_one_action_per_quarantine_rule_with_matching_privilege()
    {
        var actions = Catalog.Actions.Where(a => a.Definition.HandlerKind == CleaningRuleHandler.Kind).ToList();
        var quarantine = Set.Rules.Where(r => r.Mode == CleaningMode.Quarantine).ToList();
        Assert.Equal(quarantine.Select(r => r.ActionId).Order(StringComparer.Ordinal), actions.Select(a => a.Definition.Id).Order(StringComparer.Ordinal));
        foreach (var a in actions)
        {
            var rule = quarantine.Single(r => r.ActionId == a.Definition.Id);
            Assert.Equal(rule.Admin ? PrivilegeLevel.Admin : PrivilegeLevel.User, a.Definition.Privileges);
            Assert.Equal(rule.Id, a.TargetTemplate["rule"]);
            Assert.Equal(rule.Scope == CleaningScope.User, a.TargetTemplate.TryGetValue("scope", out var sc) && sc == "HKCU");
            Assert.Equal(CleaningBatchId.Pattern, a.UserParameters["batch"]);
            Assert.StartsWith("cleaning.", a.Definition.Id, StringComparison.Ordinal);
        }
        Assert.True(actions.Count(a => a.Definition.Privileges == PrivilegeLevel.User) <= 32);
        Assert.True(actions.Count(a => a.Definition.Privileges == PrivilegeLevel.Admin) <= 32);
        // Todo handlerKind do catálogo do módulo é registrado pelo módulo.
        var kinds = CleaningHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, false)).Select(h => h.HandlerKind).ToList();
        Assert.All(actions, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.Null(Catalog.Actions.Single(a => a.Definition.Id == "cleaning.temp-user").ValidateUserArguments(new Dictionary<string, string> { ["batch"] = "..\\x" }, out _));
    }

    [Fact]
    public void Apply_moves_eligible_files_writes_manifest_and_verifies()
    {
        using var s = new CleaningSandbox();
        var a = s.Temp("a.tmp", bytes: 10);
        var b = s.Temp(Path.Combine("pasta", "b.tmp"), bytes: 20);
        var fresh = s.Temp("novo.tmp", ageHours: 1);
        var (h, _, _) = Handler(s);
        var batch = NewBatch();
        var act = Action("temp-user", batch);

        var before = h.Detect(act);
        Assert.Equal(CleaningRuleHandler.NotApplied, before.Value);
        Assert.Equal("2", before.Detail!["eligibleFiles"]);
        Assert.Equal("30", before.Detail!["eligibleBytes"]);

        h.Apply(act);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(fresh));
        Assert.True(Directory.Exists(Path.Combine(s.Folders.UserTemp!, "pasta"))); // pastas ficam
        var after = h.Detect(act);
        Assert.True(after.SameValueAs(act.Desired));

        var ruleDir = QuarantineStore.RuleDirectory(s.Data, batch, "temp-user");
        var manifest = QuarantineStore.ReadManifest(ruleDir)!;
        Assert.Equal(QuarantineManifest.Applied, manifest.Status);
        Assert.Equal(2, manifest.MovedFiles);
        Assert.Equal(30, manifest.MovedBytes);
        Assert.All(manifest.Entries, e => Assert.True(File.Exists(QuarantineStore.QuarantinedFile(ruleDir, e.Index))));
        var batches = QuarantineStore.ReadBatches(s.Data);
        Assert.Equal(30, Assert.Single(batches).BytesInQuarantine);

        // Lote reaproveitado: recusado.
        Assert.Throws<InvalidOperationException>(() => h.Apply(act));
    }

    [Fact]
    public void Restore_returns_everything_and_never_overwrites()
    {
        using var s = new CleaningSandbox();
        var a = s.Temp("a.tmp", bytes: 10);
        var b = s.Temp(Path.Combine("pasta", "b.tmp"), bytes: 20);
        var (h, _, _) = Handler(s);
        var act = Action("temp-user", NewBatch());
        var original = h.Detect(act);
        h.Apply(act);

        File.WriteAllText(a, "novo conteúdo"); // um programa recriou o arquivo no mesmo lugar
        Directory.Delete(Path.Combine(s.Folders.UserTemp!, "pasta")); // e apagou uma pasta
        Assert.Null(h.CheckRestore(act, original));
        h.Restore(act, original);

        Assert.Equal("novo conteúdo", File.ReadAllText(a));
        Assert.Equal(10, new FileInfo(Path.Combine(s.Folders.UserTemp!, "a (restaurado).tmp")).Length);
        Assert.Equal(20, new FileInfo(b).Length);
        var now = h.Detect(act);
        Assert.Equal(CleaningRuleHandler.NotApplied, now.Value);
        Assert.Equal(QuarantineManifest.Restored, QuarantineStore.ReadManifest(QuarantineStore.RuleDirectory(s.Data, act.Target["batch"], "temp-user"))!.Status);
        Assert.NotNull(h.CheckRestore(act, StateSnapshot.Of(CleaningRuleHandler.StateType, CleaningRuleHandler.Applied)));
    }

    [Fact]
    public void Files_in_use_are_skipped_and_counted()
    {
        using var s = new CleaningSandbox();
        var busy = s.Temp("aberto.tmp");
        var free = s.Temp("livre.tmp");
        var (h, ops, _) = Handler(s);
        ops.InUse.Add(busy);
        var act = Action("temp-user", NewBatch());
        h.Apply(act);
        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(free));
        var m = QuarantineStore.ReadManifest(QuarantineStore.RuleDirectory(s.Data, act.Target["batch"], "temp-user"))!;
        Assert.Equal(1, m.SkippedInUse);
        Assert.Equal(1, m.MovedFiles);
        Assert.True(h.Detect(act).SameValueAs(act.Desired));
    }

    [Fact]
    public void Tampered_manifest_cannot_restore_outside_the_rule()
    {
        using var s = new CleaningSandbox();
        s.Temp("a.tmp");
        var (h, _, _) = Handler(s);
        var act = Action("temp-user", NewBatch());
        var original = h.Detect(act);
        h.Apply(act);
        var ruleDir = QuarantineStore.RuleDirectory(s.Data, act.Target["batch"], "temp-user");
        var path = Path.Combine(ruleDir, QuarantineStore.ManifestFile);
        var evil = Path.Combine(s.Folders.UserProfile!, "Documents", "virus.exe");
        File.WriteAllText(path, File.ReadAllText(path).Replace(System.Text.Json.JsonEncodedText.Encode(Path.Combine(s.Folders.UserTemp!, "a.tmp")).ToString(),
            System.Text.Json.JsonEncodedText.Encode(evil).ToString(), StringComparison.Ordinal));
        h.Restore(act, original);
        Assert.False(File.Exists(evil));
        Assert.True(File.Exists(QuarantineStore.QuarantinedFile(ruleDir, 0)));
        Assert.Equal(CleaningRuleHandler.Applied, h.Detect(act).Value); // não confirmou: o executor não declara restaurado
    }

    [Fact]
    public void Open_app_blocks_its_rule_and_profile_rules_need_a_sid_under_another_account()
    {
        using var s = new CleaningSandbox();
        var (h, _, procs) = Handler(s);
        procs.Running.Add("chrome");
        var chrome = Action("chrome-cache", NewBatch());
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(chrome, T.Ctx).Kind);
        Assert.Contains("chrome", h.Assess(chrome, T.Ctx).Reason, StringComparison.Ordinal);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(Action("edge-cache", NewBatch()), T.Ctx).Kind);

        var (svc, _, _) = Handler(s, otherAccount: true);
        Assert.Equal(ApplicabilityKind.Blocked, svc.Assess(Action("temp-user", NewBatch()), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Applicable, svc.Assess(Action("temp-user", NewBatch(), "S-1-5-21-1-2-3-1001"), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Applicable, svc.Assess(Action("temp-windows", NewBatch()), T.Ctx).Kind);
        // SID num alvo de máquina é recusado.
        var bad = Action("temp-windows", NewBatch()) is var w ? w with { Target = new Dictionary<string, string>(w.Target) { ["userSid"] = "S-1-5-21-1-2-3-1001" } } : null;
        Assert.Equal(ApplicabilityKind.Blocked, svc.Assess(bad!, T.Ctx).Kind);
    }

    [Fact]
    public void Executor_applies_verifies_and_reverts_a_cleaning_run()
    {
        using var s = new CleaningSandbox();
        var a = s.Temp("a.tmp");
        var dump = s.File(Path.Combine(s.Folders.LocalAppData!, "CrashDumps", "jogo.exe.123.dmp"), bytes: 5000);
        var (h, _, _) = Handler(s);
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var batch = NewBatch();
        var plan = new DesiredStateResolver(handlers).Resolve([Action("temp-user", batch), Action("crashdumps-user", batch)], T.Ctx);
        var run = exec.BeginRun("teste", "1");
        var results = exec.Execute(run, plan, T.Ctx);
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(dump));
        Assert.Equal(2, Assert.Single(QuarantineStore.ReadBatches(s.Data)).Rules.Count);

        var reverted = exec.RevertRun(run);
        Assert.All(reverted, r => Assert.Equal(RevertOutcome.Reverted, r.Outcome));
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(dump));
        Assert.True(Assert.Single(QuarantineStore.ReadBatches(s.Data)).FullyRestored);
    }

    [Fact]
    public void Crash_after_apply_is_recovered_from_the_journal()
    {
        using var s = new CleaningSandbox();
        var a = s.Temp("a.tmp");
        var (h, _, _) = Handler(s);
        var handlers = new HandlerRegistry().Add(h);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal) { FaultInjector = (point, _) => { if (point == "after-apply") throw new InvalidOperationException("queda simulada"); } };
        var plan = new DesiredStateResolver(handlers).Resolve([Action("temp-user", NewBatch())], T.Ctx);
        var run = exec.BeginRun("teste", "1");
        Assert.Throws<InvalidOperationException>(() => exec.Execute(run, plan, T.Ctx));
        Assert.False(File.Exists(a));
        Assert.Equal(JournalState.Applying, journal.Entries(run).Single().State);

        // Novo processo: só o diário e o handler.
        var report = new RecoveryService(journal, new ActionExecutor(handlers, journal)).Recover();
        Assert.True(report.FullyRestored);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Crash_before_any_file_moved_counts_as_original()
    {
        using var s = new CleaningSandbox();
        var a = s.Temp("a.tmp");
        var (h, ops, _) = Handler(s);
        ops.InUse.Add(a); // nada sai
        var act = Action("temp-user", NewBatch());
        h.Apply(act);
        Assert.True(File.Exists(a));
        Assert.Equal(CleaningRuleHandler.Applied, h.Detect(act).Value); // lote vazio concluído = aplicado (nada a desfazer)
        h.Restore(act, StateSnapshot.Of(CleaningRuleHandler.StateType, CleaningRuleHandler.NotApplied));
        Assert.Equal(CleaningRuleHandler.NotApplied, h.Detect(act).Value);
    }

    [Fact]
    public void Batch_ids_are_closed_format()
    {
        var id = CleaningBatchId.New(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc));
        Assert.Matches("^20261031T120000Z-[0-9a-f]{8}$", id);
        Assert.Equal(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc), CleaningBatchId.TimeOf(id));
        Assert.False(CleaningBatchId.IsValid("20261031T120000Z-1234567"));
        Assert.False(CleaningBatchId.IsValid("../../Windows"));
        Assert.Throws<ArgumentException>(() => QuarantineStore.RuleDirectory("/x", "..", "temp-user"));
    }
}
