using System.Globalization;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>Relógio fixo para os handlers (lotes "antigos" sem mexer no relógio do sistema).</summary>
internal sealed class FixedClock(DateTime utc) : TimeProvider
{
    public DateTime Utc { get; set; } = utc;
    public override DateTimeOffset GetUtcNow() => new(Utc, TimeSpan.Zero);
}

/// <summary>Lixeira falsa.</summary>
internal sealed class FakeRecycleBin : IRecycleBinBackend
{
    public long Items { get; set; } = 3;
    public long Bytes { get; set; } = 4096;
    public int EmptyCalls { get; private set; }
    public string? LastRoot { get; private set; }
    public bool Ignore { get; set; }
    public RecycleBinSize? Query(string? rootPath) => new RecycleBinSize(Items, Bytes);
    public int Empty(string? rootPath)
    {
        EmptyCalls++;
        LastRoot = rootPath;
        if (Ignore) return unchecked((int)0x80004005);
        Items = 0;
        Bytes = 0;
        return 0;
    }
    public bool DriveReady(string rootPath) => rootPath is @"C:\" or @"D:\";
}

internal static class CleaningV2Support
{
    public static readonly CatalogDocument Catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));

    public static ActionInstance Action(string actionId, IReadOnlyDictionary<string, string> args)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == actionId);
        var valid = entry.ValidateUserArguments(args, out var reason);
        Assert.True(valid is not null, reason);
        var a = entry.TryInstantiate(valid!, out reason);
        Assert.True(a is not null, reason);
        return a!;
    }

    /// <summary>Lote sintético na quarentena: manifesto + arquivos em files\, com data de criação escolhida.</summary>
    public static string MakeBatch(string dataDir, DateTime createdUtc, string ruleKey, params int[] sizes)
    {
        var batch = CleaningBatchId.New(createdUtc);
        var ruleDir = QuarantineStore.RuleDirectory(dataDir, batch, ruleKey);
        Directory.CreateDirectory(Path.Combine(ruleDir, QuarantineStore.FilesDirectory));
        var m = new QuarantineManifest
        {
            Batch = batch, Rule = ruleKey.StartsWith("u_", StringComparison.Ordinal) ? ruleKey[2..] : ruleKey, RuleTitle = "Regra " + ruleKey, Category = "Sistema",
            CreatedUtc = createdUtc, FinishedUtc = createdUtc, Status = QuarantineManifest.Applied, UserRule = ruleKey.StartsWith("u_", StringComparison.Ordinal),
        };
        for (var i = 0; i < sizes.Length; i++)
        {
            File.WriteAllBytes(QuarantineStore.QuarantinedFile(ruleDir, i), new byte[sizes[i]]);
            m.Entries.Add(new QuarantineEntry { Index = i, Path = $"/origem/{i}.tmp", Size = sizes[i], State = QuarantineEntryState.Moved });
        }
        m.MovedFiles = sizes.Length;
        m.MovedBytes = sizes.Sum();
        QuarantineMover.WriteManifest(ruleDir, m);
        return batch;
    }
}

[Trait("Category", "Unit")]
public sealed class CleaningV2CatalogTests
{
    private static CatalogDocument Catalog => CleaningV2Support.Catalog;

    [Fact]
    public void New_actions_exist_with_the_irreversible_contract_and_registered_handlers()
    {
        string[] irreversible = ["cleaning.quarantine.purge", "cleaning.quarantine.purge.machine", "cleaning.quarantine.purge-older", "cleaning.quarantine.purge-older.machine", "cleaning.recycle-bin.empty"];
        foreach (var id in irreversible)
        {
            var a = Catalog.Actions.Single(x => x.Definition.Id == id).Definition;
            Assert.True(a.Irreversible, id);
            Assert.Equal(PersistenceKind.Persistent, a.Persistence);
            Assert.Equal(id.EndsWith(".machine", StringComparison.Ordinal) ? PrivilegeLevel.Admin : PrivilegeLevel.User, a.Privileges);
        }
        var userRule = Catalog.Actions.Single(x => x.Definition.Id == CleaningUserRuleHandler.ActionId);
        Assert.False(userRule.Definition.Irreversible);
        Assert.Equal(PrivilegeLevel.User, userRule.Definition.Privileges);
        Assert.Equal("HKCU", userRule.TargetTemplate["scope"]);

        var kinds = CleaningHandlers.Create(new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, false)).Select(h => h.HandlerKind).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct(StringComparer.Ordinal).Count());
        foreach (var a in Catalog.Actions.Where(a => a.Definition.Id.StartsWith("cleaning.", StringComparison.Ordinal)))
            Assert.Contains(a.Definition.HandlerKind, kinds);
        Assert.All(Catalog.Actions.Where(a => a.Definition.Id.StartsWith("cleaning.", StringComparison.Ordinal)), a => Assert.StartsWith("Cleaning", a.Definition.HandlerKind, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cleaning.quarantine.purge-older", "days", "30", true)]
    [InlineData("cleaning.quarantine.purge-older", "days", "999", true)]
    [InlineData("cleaning.quarantine.purge-older", "days", "0", false)]
    [InlineData("cleaning.quarantine.purge-older", "days", "1000", false)]
    [InlineData("cleaning.quarantine.purge-older", "days", "07", false)]
    [InlineData("cleaning.quarantine.purge-older", "days", "7\n", false)]
    [InlineData("cleaning.quarantine.purge-older", "days", "-1", false)]
    [InlineData("cleaning.recycle-bin.empty", "drive", "C", true)]
    [InlineData("cleaning.recycle-bin.empty", "drive", "all", true)]
    [InlineData("cleaning.recycle-bin.empty", "drive", "C:", false)]
    [InlineData("cleaning.recycle-bin.empty", "drive", "c", false)]
    [InlineData("cleaning.recycle-bin.empty", "drive", "..\\", false)]
    public void User_parameters_are_closed(string actionId, string key, string value, bool ok)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == actionId);
        Assert.Equal(ok, entry.ValidateUserArguments(new Dictionary<string, string> { [key] = value }, out _) is not null);
    }

    [Theory]
    [InlineData("meu-app", true)]
    [InlineData("a1-b2-c3", true)]
    [InlineData("Meu-App", false)]
    [InlineData("../x", false)]
    [InlineData("u_x", false)]
    [InlineData("x-", false)]
    public void User_rule_id_parameter_is_closed(string rule, bool ok)
    {
        var entry = Catalog.Actions.Single(a => a.Definition.Id == CleaningUserRuleHandler.ActionId);
        var args = new Dictionary<string, string> { ["rule"] = rule, ["batch"] = CleaningBatchId.New(DateTime.UtcNow) };
        Assert.Equal(ok, entry.ValidateUserArguments(args, out _) is not null);
    }
}

[Trait("Category", "Unit")]
public sealed class CleaningV2RuleTests
{
    private static CleaningRule Rule(string id) => CleaningRuleSet.BuiltIn.Find(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void New_built_in_rules_are_present_honest_and_have_catalog_parity()
    {
        string[] ids = ["windows-logs-old", "store-cache", "nvidia-nvcache", "nvidia-installers", "amd-installers", "riot-logs", "minecraft-logs", "ea-logs",
            "ubisoft-logs", "obs-logs", "dev-vscode-cache", "dev-npm-cache", "dev-pip-cache", "dev-nuget-cache", "dev-gradle-cache", "dev-java-cache"];
        foreach (var id in ids)
        {
            var r = Rule(id);
            Assert.False(string.IsNullOrWhiteSpace(r.Evidence), id);
            Assert.False(string.IsNullOrWhiteSpace(r.Explanation), id);
            Assert.Single(CleaningV2Support.Catalog.Actions, a => a.Definition.Id == r.ActionId);
        }
        // Desenvolvedor: sempre desmarcado e rotulado.
        var dev = CleaningRuleSet.BuiltIn.Rules.Where(r => r.Category == CleaningCategory.Desenvolvedor).ToList();
        Assert.True(dev.Count >= 6);
        Assert.All(dev, r => Assert.False(r.DefaultSelected));
        Assert.All(dev, r => Assert.Contains("desenvolvedor", r.Title, StringComparison.Ordinal));
        // Mitos e coisas que não fazemos: Prefetch, histórico do Defender, wsreset.
        Assert.DoesNotContain(CleaningRuleSet.BuiltIn.Rules, r => r.Roots.Any(x => x.Template.Contains("Prefetch", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(CleaningRuleSet.BuiltIn.Rules, r => r.Roots.Any(x => x.Template.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase)));
        // Instaladores de driver e logs antigos do Windows: só com idade; caches de shader: desmarcados com aviso.
        Assert.True(Rule("nvidia-installers").MinAgeHours >= 720);
        Assert.True(Rule("amd-installers").MinAgeHours >= 720);
        Assert.True(Rule("windows-logs-old").MinAgeHours >= 720);
        Assert.False(Rule("nvidia-nvcache").DefaultSelected);
        Assert.NotNull(Rule("nvidia-nvcache").Warning);
        // Nenhuma regra embutida de usuário vai ao NuGet global (só o http-cache).
        Assert.DoesNotContain(Rule("dev-nuget-cache").Roots, r => r.Template.Contains(".nuget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void System_drive_and_profile_placeholders_expand_and_honor_age()
    {
        using var s = new CleaningSandbox();
        var folders = s.Folders with { SystemDrive = s.Root };
        var oldDriver = s.File(Path.Combine(s.Root, "NVIDIA", "DisplayDriver", "566.36", "setup.exe"), ageHours: 24 * 40);
        var newDriver = s.File(Path.Combine(s.Root, "NVIDIA", "DisplayDriver", "572.16", "setup.exe"), ageHours: 24 * 3);
        var rule = Rule("nvidia-installers");
        var found = CleaningEnumerator.Enumerate(rule, CleaningPaths.Expand(rule, folders), s.Now).Select(c => c.Path).ToList();
        Assert.Equal([oldDriver], found);
        Assert.True(CleaningPaths.IsWithinRule(rule, folders, oldDriver));
        Assert.False(CleaningPaths.IsWithinRule(rule, folders, Path.Combine(s.Root, "NVIDIA", "outro.exe")));
        Assert.True(File.Exists(newDriver));

        var gradle = s.File(Path.Combine(folders.UserProfile!, ".gradle", "caches", "modules-2", "x.jar"), ageHours: 24 * 10);
        var g = Rule("dev-gradle-cache");
        Assert.Equal([gradle], CleaningEnumerator.Enumerate(g, CleaningPaths.Expand(g, folders), s.Now).Select(c => c.Path).ToList());
    }

    [Theory]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Jogos","scope":"Machine","admin":true,"mode":"Quarantine","roots":["%SystemDrive%\\Windows"],"patterns":["*"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Jogos","scope":"Machine","admin":true,"mode":"Quarantine","roots":["%SystemDrive%"],"patterns":["x"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Apps","scope":"User","mode":"Quarantine","roots":["%UserProfile%"],"patterns":["x"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Apps","scope":"User","mode":"Quarantine","roots":["%UserProfile%\\*\\cache"],"patterns":["*"],"explanation":"e","expectedGain":"g","evidence":"v"}]}""")]
    [InlineData("""{"rules":[{"id":"x","title":"t","category":"Apps","scope":"User","mode":"Quarantine","roots":["%LocalAppData%\\A"],"patterns":["*"],"explanation":"e","expectedGain":"g","evidence":"v","category2":1,"scope":"5"}]}""")]
    public void New_placeholders_are_strict(string json) => Assert.Throws<InvalidDataException>(() => CleaningRuleSet.Parse(json));
}

[Trait("Category", "Unit")]
public sealed class CleaningUserRuleTests : IDisposable
{
    private readonly CleaningSandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static string One(string roots, string extra = "", string scope = "User") =>
        $$"""{"rules":[{"id":"meu-app","title":"Meu app","category":"Apps","scope":"{{scope}}","mode":"Quarantine","roots":[{{roots}}],"patterns":["*"],"recursive":true,"explanation":"e","expectedGain":"g","evidence":"v"{{extra}}}]}""";

    [Theory]
    [InlineData("\"%AppData%\\\\Microsoft\\\\Credentials\"")]
    [InlineData("\"%AppData%\\\\Microsoft\\\\Protect\\\\S-1-5\"")]
    [InlineData("\"%AppData%\\\\Microsoft\\\\*\"")]
    [InlineData("\"%AppData%\\\\Microsoft\"")]
    [InlineData("\"%LocalAppData%\\\\NEXUS\\\\quarantine\"")]
    [InlineData("\"%UserProfile%\\\\Documents\\\\Jogos\"")]
    [InlineData("\"%UserProfile%\\\\Saved Games\"")]
    [InlineData("\"%UserProfile%\\\\OneDrive - Empresa\"")]
    [InlineData("\"%UserProfile%\\\\.ssh\"")]
    [InlineData("\"%LocalAppData%\\\\*\\\\Cache\"")]
    [InlineData("\"%LocalAppData%\"")]
    [InlineData("\"%WinDir%\\\\Temp\"")]
    [InlineData("\"%ProgramData%\\\\App\"")]
    [InlineData("\"%SteamPath%\\\\logs\"")]
    [InlineData("\"%SystemDrive%\\\\NVIDIA\"")]
    [InlineData("\"C:\\\\Temp\"")]
    [InlineData("\"%LocalAppData%\\\\..\\\\..\\\\Documents\"")]
    public void Forbidden_targets_and_machine_placeholders_are_refused(string root) =>
        Assert.Throws<InvalidDataException>(() => CleaningRuleSet.ParseUserRules(One(root), out _));

    [Theory]
    [InlineData(",\"admin\":true")]
    [InlineData(",\"special\":\"RecycleBin\"")]
    [InlineData(",\"nativeTool\":\"ms-settings:storagesense\"")]
    public void User_rules_cannot_escalate(string extra) =>
        Assert.Throws<InvalidDataException>(() => CleaningRuleSet.ParseUserRules(One("\"%LocalAppData%\\\\MeuApp\\\\Cache\"", extra), out _));

    [Fact]
    public void Machine_scope_built_in_id_and_measure_only_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => CleaningRuleSet.ParseUserRules(One("\"%ProgramData%\\\\MeuApp\"", ",\"admin\":true", scope: "Machine"), out _));
        Assert.Throws<InvalidDataException>(() => CleaningRuleSet.ParseUserRules(One("\"%LocalAppData%\\\\MeuApp\\\\Cache\"").Replace("\"meu-app\"", "\"temp-user\"", StringComparison.Ordinal), out _));
        Assert.Throws<InvalidDataException>(() => CleaningRuleSet.ParseUserRules(One("\"%LocalAppData%\\\\MeuApp\\\\Cache\"").Replace("\"Quarantine\"", "\"MeasureOnly\"", StringComparison.Ordinal), out _));
        var ok = CleaningRuleSet.ParseUserRules(One("\"%LocalAppData%\\\\MeuApp\\\\Cache\""), out var enabled);
        Assert.True(enabled);
        Assert.True(Assert.Single(ok.Rules).IsUserRule);
        Assert.Equal("u_meu-app", ok.Rules[0].QuarantineKey);
    }

    [Fact]
    public void Folder_load_reports_errors_per_file_and_keeps_valid_files()
    {
        var dir = Path.Combine(_s.Folders.AppData!, "NEXUS", "rules");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a-bom.json"), One("\"%LocalAppData%\\\\MeuApp\\\\Cache\""));
        File.WriteAllText(Path.Combine(dir, "b-quebrado.json"), "{ \"rules\": [ { \"id\": \"x\", } ");
        File.WriteAllText(Path.Combine(dir, "c-duplicado.json"), One("\"%AppData%\\\\Outro\\\\Cache\""));
        File.WriteAllText(Path.Combine(dir, "d-perigoso.json"), One("\"%AppData%\\\\Microsoft\\\\Credentials\"").Replace("meu-app", "outro", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(dir, UserCleaningRules.ExampleFileName), UserCleaningRules.ExampleJson);
        File.WriteAllText(Path.Combine(dir, "nao-json.txt"), "ignorado");

        Assert.Equal(dir, UserCleaningRules.FolderFor(_s.Folders));
        var load = UserCleaningRules.Load(dir);
        Assert.Null(load.FolderProblem);
        Assert.Equal(5, load.Files.Count);
        Assert.Equal("meu-app", Assert.Single(load.Rules.Rules).Id);
        Assert.Null(load.Files.Single(f => f.Name == "a-bom.json").Error);
        Assert.Contains("JSON inválido", load.Files.Single(f => f.Name == "b-quebrado.json").Error, StringComparison.Ordinal);
        Assert.Contains("outro arquivo", load.Files.Single(f => f.Name == "c-duplicado.json").Error, StringComparison.Ordinal);
        Assert.Contains("protegida", load.Files.Single(f => f.Name == "d-perigoso.json").Error, StringComparison.Ordinal);
        var example = load.Files.Single(f => f.Name == UserCleaningRules.ExampleFileName);
        Assert.Null(example.Error); // o exemplo é válido, mas vem desativado
        Assert.False(example.Enabled);
        Assert.Empty(example.RuleIds);

        Assert.Empty(UserCleaningRules.Load(Path.Combine(_s.Root, "nao-existe")).Rules.Rules);
    }

    [Fact]
    public void Linked_rules_folder_is_refused()
    {
        var real = Path.Combine(_s.Root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.json"), One("\"%LocalAppData%\\\\MeuApp\\\\Cache\""));
        var link = Path.Combine(_s.Root, "link");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; } // sem permissão para criar link
        var load = UserCleaningRules.Load(link);
        Assert.NotNull(load.FolderProblem);
        Assert.Empty(load.Rules.Rules);
    }

    private (CleaningUserRuleHandler Handler, string RulesDir) Handler(bool elevated = false, bool otherAccount = false)
    {
        var dir = Path.Combine(_s.Folders.AppData!, "NEXUS", "rules");
        Directory.CreateDirectory(dir);
        return (new CleaningUserRuleHandler(() => UserCleaningRules.Load(dir), _s.Data, _ => _s.Folders, new FakeProcesses(), otherAccount, elevated, new InUseOps()), dir);
    }

    private static ActionInstance UserRuleAction(string rule, string batch) =>
        CleaningV2Support.Action(CleaningUserRuleHandler.ActionId, new Dictionary<string, string> { ["rule"] = rule, ["batch"] = batch });

    [Fact]
    public void User_rule_applies_and_restores_even_after_the_file_is_deleted()
    {
        var (h, dir) = Handler();
        File.WriteAllText(Path.Combine(dir, "meu.json"), One("\"%LocalAppData%\\\\MeuApp\\\\Cache\""));
        var f = _s.File(Path.Combine(_s.Folders.LocalAppData!, "MeuApp", "Cache", "x.bin"), bytes: 50);
        var keep = _s.File(Path.Combine(_s.Folders.LocalAppData!, "MeuApp", "config.json"), bytes: 5);
        var act = UserRuleAction("meu-app", CleaningBatchId.New(DateTime.UtcNow));
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(act, T.Ctx).Kind);
        var before = h.Detect(act);

        h.Apply(act);
        Assert.False(File.Exists(f));
        Assert.True(File.Exists(keep));
        Assert.True(h.Detect(act).SameValueAs(act.Desired));
        var manifest = QuarantineStore.ReadManifest(QuarantineStore.RuleDirectory(_s.Data, act.Target["batch"], "u_meu-app"))!;
        Assert.True(manifest.UserRule);
        Assert.NotNull(manifest.RuleSnapshot);

        // O usuário apagou o arquivo de regras: o desfazer usa a cópia revalidada do manifesto.
        File.Delete(Path.Combine(dir, "meu.json"));
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(UserRuleAction("meu-app", CleaningBatchId.New(DateTime.UtcNow)), T.Ctx).Kind);
        Assert.Null(h.CheckRestore(act, before));
        h.Restore(act, before);
        Assert.True(File.Exists(f));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void User_rules_are_refused_by_the_elevated_or_service_applier(bool elevated, bool otherAccount)
    {
        var (h, dir) = Handler(elevated, otherAccount);
        File.WriteAllText(Path.Combine(dir, "meu.json"), One("\"%LocalAppData%\\\\MeuApp\\\\Cache\""));
        var f = _s.File(Path.Combine(_s.Folders.LocalAppData!, "MeuApp", "Cache", "x.bin"));
        var act = UserRuleAction("meu-app", CleaningBatchId.New(DateTime.UtcNow));
        var assess = h.Assess(act, T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, assess.Kind);
        Assert.Contains("nunca no aplicador elevado", assess.Reason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => h.Apply(act));
        Assert.True(File.Exists(f));
        // Também pelo alvo com SID (serviço): recusado.
        var (h2, _) = Handler();
        Assert.Equal(ApplicabilityKind.Blocked, h2.Assess(UserProfileScope.Bind(act, "S-1-5-21-1-2-3-1001"), T.Ctx).Kind);
        // E a regra embutida não aceita uma regra do usuário com o mesmo handler.
        Assert.Equal(ApplicabilityKind.Blocked, h2.Assess(UserRuleAction("nao-carregada", CleaningBatchId.New(DateTime.UtcNow)), T.Ctx).Kind);
    }
}

[Trait("Category", "Unit")]
public sealed class CleaningPurgeTests : IDisposable
{
    private readonly CleaningSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private static ActionInstance Purge(string batch, bool machine = false) =>
        CleaningV2Support.Action(machine ? "cleaning.quarantine.purge.machine" : "cleaning.quarantine.purge", new Dictionary<string, string> { ["batch"] = batch });

    private static ActionInstance PurgeOlder(int days) =>
        CleaningV2Support.Action("cleaning.quarantine.purge-older", new Dictionary<string, string> { ["days"] = days.ToString(CultureInfo.InvariantCulture) });

    private sealed class OpenGate : IExecutionGate
    {
        public GateDecision Evaluate() => new(true, "teste: liberado");
    }

    [Fact]
    public void Broker_refuses_purge_without_explicit_confirmation_and_executes_with_it()
    {
        var batch = CleaningV2Support.MakeBatch(_s.Data, DateTime.UtcNow.AddDays(-1), "temp-user", 100, 200);
        var journal = new InMemoryJournal();
        var coordDir = Directory.CreateTempSubdirectory("nexus-purge-coord-").FullName;
        try
        {
            using var svc = new BrokerService(CleaningV2Support.Catalog, new HandlerRegistry().Add(new CleaningQuarantinePurgeHandler(_s.Data, false, false)), journal,
                new FileRunCoordinator(coordDir), new OpenGate(), () => T.Ctx, () => new Dictionary<string, string>());
            BrokerRequest Req(BrokerOperation op, bool? confirm, Guid? run = null) => new(1, Guid.NewGuid(), op,
                op == BrokerOperation.RevertRun ? null : [new ActionRequest("cleaning.quarantine.purge@1", null, new Dictionary<string, string> { ["batch"] = batch }, confirm)], run);

            foreach (var confirm in new bool?[] { null, false })
            {
                var r = svc.Handle(Req(BrokerOperation.Execute, confirm));
                Assert.False(r.Ok);
                Assert.Equal("irreversible-unconfirmed", r.Error!.Code);
            }
            Assert.Empty(journal.Runs());
            Assert.Equal((2L, 300L), QuarantineStore.MeasureOnDisk(_s.Data, batch));

            var plan = svc.Handle(Req(BrokerOperation.Plan, null));
            Assert.True(plan.Ok, plan.Error?.Detail);
            Assert.Contains("IRREVERSÍVEL", plan.Message, StringComparison.Ordinal);

            var exec = svc.Handle(Req(BrokerOperation.Execute, true));
            Assert.True(exec.Ok, exec.Error?.Detail);
            Assert.Equal("Verified", Assert.Single(exec.Results!).Outcome);
            Assert.Equal((0L, 0L), QuarantineStore.MeasureOnDisk(_s.Data, batch));
            var b = Assert.Single(QuarantineStore.ReadBatches(_s.Data));
            Assert.True(b.FullyPurged);
            Assert.Equal(300, b.BytesPurged);
            Assert.Equal(0, b.BytesInQuarantine);

            var undo = svc.Handle(Req(BrokerOperation.RevertRun, null, exec.RunId));
            Assert.True(undo.Ok);
            Assert.False(undo.FullyRestored);
            Assert.Equal("Irreversible", Assert.Single(undo.Results!).Outcome);
        }
        finally { Directory.Delete(coordDir, recursive: true); }
    }

    [Fact]
    public void Executor_blocks_unconfirmed_purge_and_recycle_bin_without_journal()
    {
        var batch = CleaningV2Support.MakeBatch(_s.Data, DateTime.UtcNow, "temp-user", 10);
        var bin = new FakeRecycleBin();
        var handlers = new HandlerRegistry().Add(new CleaningQuarantinePurgeHandler(_s.Data, false, false)).Add(new CleaningRecycleBinHandler(bin, false));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var recycle = CleaningV2Support.Action("cleaning.recycle-bin.empty", new Dictionary<string, string> { ["drive"] = "all" });
        var plan = new DesiredStateResolver(handlers).Resolve([Purge(batch), recycle], T.Ctx);
        var run = exec.BeginRun("teste", "1");
        var results = exec.Execute(run, plan, T.Ctx);
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Blocked, r.Outcome));
        Assert.Empty(journal.Entries(run));
        Assert.Equal(0, bin.EmptyCalls);
        Assert.Equal(1, QuarantineStore.MeasureOnDisk(_s.Data, batch).Files);

        var run2 = exec.BeginRun("teste", "1");
        var ok = exec.Execute(run2, plan, T.Ctx, [recycle.Definition.Key, Purge(batch).Definition.Key]);
        Assert.All(ok, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));
        Assert.Equal(1, bin.EmptyCalls);
        Assert.Null(bin.LastRoot);
        Assert.All(exec.RevertRun(run2), r => Assert.Equal(RevertOutcome.Irreversible, r.Outcome));
    }

    [Fact]
    public void Purged_batch_cannot_be_restored_by_the_cleaning_run()
    {
        var a = _s.Temp("a.tmp", bytes: 10);
        var cleaner = new CleaningRuleHandler(CleaningRuleSet.BuiltIn, _s.Data, _ => _s.Folders, new FakeProcesses(), false, new InUseOps());
        var handlers = new HandlerRegistry().Add(cleaner).Add(new CleaningQuarantinePurgeHandler(_s.Data, false, false));
        var exec = new ActionExecutor(handlers, new InMemoryJournal());
        var batch = CleaningBatchId.New(DateTime.UtcNow);
        var clean = CleaningV2Support.Action("cleaning.temp-user", new Dictionary<string, string> { ["batch"] = batch });
        var run = exec.BeginRun("limpar", "1");
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(exec.Execute(run, new DesiredStateResolver(handlers).Resolve([clean], T.Ctx), T.Ctx)).Outcome);
        Assert.False(File.Exists(a));

        var purge = Purge(batch);
        var run2 = exec.BeginRun("esvaziar", "1");
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(exec.Execute(run2, new DesiredStateResolver(handlers).Resolve([purge], T.Ctx), T.Ctx, [purge.Definition.Key])).Outcome);
        Assert.Equal(CleaningBatchHandlerBase.Purged, cleaner.Detect(clean).Value);

        var undo = Assert.Single(exec.RevertRun(run));
        Assert.NotEqual(RevertOutcome.Reverted, undo.Outcome);
        Assert.False(File.Exists(a));
        Assert.Contains("esvaziada", cleaner.CheckRestore(clean, StateSnapshot.Of(CleaningBatchHandlerBase.StateType, CleaningBatchHandlerBase.NotApplied)), StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_never_follows_links_inside_the_quarantine()
    {
        var batch = CleaningV2Support.MakeBatch(_s.Data, DateTime.UtcNow, "temp-user", 10);
        var outside = Path.Combine(_s.Root, "fora");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precioso.txt");
        File.WriteAllText(precious, "não apague");
        var link = Path.Combine(QuarantineStore.RuleDirectory(_s.Data, batch, "temp-user"), QuarantineStore.FilesDirectory, "link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        var h = new CleaningQuarantinePurgeHandler(_s.Data, false, false);
        h.Apply(Purge(batch));
        Assert.True(File.Exists(precious));
        Assert.False(Directory.Exists(link));
        Assert.Equal(CleaningQuarantinePurgeHandler.Purged, h.Detect(Purge(batch)).Value);
    }

    [Fact]
    public void Expiration_by_date_selects_only_old_batches()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var old = CleaningV2Support.MakeBatch(_s.Data, now.AddDays(-45), "temp-user", 1000);
        var mid = CleaningV2Support.MakeBatch(_s.Data, now.AddDays(-20), "thumbnails", 500);
        var fresh = CleaningV2Support.MakeBatch(_s.Data, now.AddDays(-1), "temp-user", 100);
        Assert.Equal([old], QuarantineStore.ExpiredBatches(_s.Data, 30, now).Select(b => b.BatchId));
        Assert.Equal([mid, old], QuarantineStore.ExpiredBatches(_s.Data, 14, now).Select(b => b.BatchId));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuarantineStore.ExpiredBatches(_s.Data, 0, now));

        var h = new CleaningQuarantinePurgeHandler(_s.Data, false, false, new FixedClock(now));
        var act = PurgeOlder(30);
        var before = h.Detect(act);
        Assert.Equal(CleaningQuarantinePurgeHandler.Present, before.Value);
        Assert.Equal("1000", before.Detail!["bytes"]);
        h.Apply(act);
        Assert.Equal(CleaningQuarantinePurgeHandler.Purged, h.Detect(act).Value);
        Assert.Equal((0L, 0L), QuarantineStore.MeasureOnDisk(_s.Data, old));
        Assert.Equal(500, QuarantineStore.MeasureOnDisk(_s.Data, mid).Bytes);
        Assert.Equal(100, QuarantineStore.MeasureOnDisk(_s.Data, fresh).Bytes);
        Assert.Empty(QuarantineStore.ExpiredBatches(_s.Data, 30, now));
    }

    [Fact]
    public void Purge_levels_and_catalog_flags_are_enforced()
    {
        var batch = CleaningV2Support.MakeBatch(_s.Data, DateTime.UtcNow, "temp-windows", 10);
        // Quarentena do usuário pelo serviço: recusada. Do administrador sem elevação: recusada.
        Assert.Equal(ApplicabilityKind.Blocked, new CleaningQuarantinePurgeHandler(_s.Data, otherAccount: true, elevated: true).Assess(Purge(batch), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, new CleaningQuarantinePurgeHandler(_s.Data, false, false).Assess(Purge(batch, machine: true), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Applicable, new CleaningQuarantinePurgeHandler(_s.Data, false, true).Assess(Purge(batch, machine: true), T.Ctx).Kind);
        // Lote inexistente: não se aplica.
        Assert.Equal(ApplicabilityKind.NotApplicable, new CleaningQuarantinePurgeHandler(_s.Data, false, false).Assess(Purge(CleaningBatchId.New(DateTime.UtcNow)), T.Ctx).Kind);
        // Catálogo adulterado (sem "irreversible"): recusado; Restore sempre recusa.
        var tampered = Purge(batch) is var p ? p with { Definition = p.Definition with { Irreversible = false } } : null;
        var h = new CleaningQuarantinePurgeHandler(_s.Data, false, false);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(tampered!, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(tampered!));
        Assert.NotNull(h.CheckRestore(Purge(batch), StateSnapshot.Of("x", "y")));
        Assert.Throws<InvalidOperationException>(() => h.Restore(Purge(batch), StateSnapshot.Of("x", "y")));
        Assert.Equal(1, QuarantineStore.MeasureOnDisk(_s.Data, batch).Files);
    }

    [Fact]
    public void Recycle_bin_is_user_only_and_verified_by_size()
    {
        var bin = new FakeRecycleBin();
        var c = CleaningV2Support.Action("cleaning.recycle-bin.empty", new Dictionary<string, string> { ["drive"] = "C" });
        Assert.Equal(ApplicabilityKind.Blocked, new CleaningRecycleBinHandler(bin, otherAccount: true).Assess(c, T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, new CleaningRecycleBinHandler(null, false).Assess(c, T.Ctx).Kind);
        var z = CleaningV2Support.Action("cleaning.recycle-bin.empty", new Dictionary<string, string> { ["drive"] = "Z" });
        Assert.Equal(ApplicabilityKind.Blocked, new CleaningRecycleBinHandler(bin, false).Assess(z, T.Ctx).Kind);

        var h = new CleaningRecycleBinHandler(bin, false);
        var before = h.Detect(c);
        Assert.Equal(CleaningRecycleBinHandler.NotEmpty, before.Value);
        Assert.Equal("4096", before.Detail!["bytes"]);
        h.Apply(c);
        Assert.Equal(@"C:\", bin.LastRoot);
        Assert.Equal(CleaningRecycleBinHandler.Empty, h.Detect(c).Value);

        // O Windows não esvaziou: Apply falha (e a releitura do executor mostraria "not-empty").
        var stubborn = new FakeRecycleBin { Ignore = true };
        Assert.Throws<IOException>(() => new CleaningRecycleBinHandler(stubborn, false).Apply(c));
        Assert.Throws<InvalidOperationException>(() => h.Restore(c, before));
    }
}

[Trait("Category", "Unit")]
public sealed class CleaningHistoryTests : IDisposable
{
    private readonly CleaningSandbox _s = new();
    public void Dispose() => _s.Dispose();

    [Fact]
    public void History_aggregates_manifests_and_journal()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var user = Path.Combine(_s.Root, "user");
        var admin = Path.Combine(_s.Root, "admin");
        var septBatch = CleaningV2Support.MakeBatch(user, new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), "temp-user", 1000, 1000);
        var octBatch = CleaningV2Support.MakeBatch(user, new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), "chrome-cache", 300);
        var adminBatch = CleaningV2Support.MakeBatch(admin, new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc), "temp-windows", 700);

        // Setembro foi esvaziado em outubro (recuperado de verdade = 2000).
        new CleaningQuarantinePurgeHandler(user, false, false, new FixedClock(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc)))
            .Apply(CleaningV2Support.Action("cleaning.quarantine.purge", new Dictionary<string, string> { ["batch"] = septBatch }));

        // Diário: a execução que limpou o lote de outubro e um esvaziamento da Lixeira.
        var journal = new InMemoryJournal(new FixedClock(now.AddDays(-2)));
        var run = journal.BeginRun("limpar", "1");
        var clean = CleaningV2Support.Action("cleaning.chrome-cache", new Dictionary<string, string> { ["batch"] = octBatch });
        journal.AddPlanned(run, 0, clean);
        var run2 = journal.BeginRun("lixeira", "1");
        var bin = CleaningV2Support.Action("cleaning.recycle-bin.empty", new Dictionary<string, string> { ["drive"] = "all" });
        var id = journal.AddPlanned(run2, 0, bin);
        journal.Transition(id, JournalState.BackedUp, original: StateSnapshot.Of(CleaningRecycleBinHandler.StateType, CleaningRecycleBinHandler.NotEmpty, ValueOrigin.Explicit,
            new Dictionary<string, string> { ["bytes"] = "5000", ["items"] = "4" }));
        journal.Transition(id, JournalState.Applying);
        journal.Transition(id, JournalState.Applied);
        journal.Transition(id, JournalState.Verified);

        var report = CleaningHistory.Build(
            [.. QuarantineStore.ReadBatches(user).Select(b => (b, CleaningLevel.User)), .. QuarantineStore.ReadBatches(admin).Select(b => (b, CleaningLevel.Admin))],
            [(journal, CleaningLevel.User)], now);

        Assert.Equal(3, report.Sessions.Count);
        Assert.Equal(adminBatch, report.Sessions[0].BatchId); // mais recente primeiro
        Assert.Equal(CleaningLevel.Admin, report.Sessions[0].Level);
        var sept = report.Sessions.Single(s => s.BatchId == septBatch);
        Assert.Equal("esvaziada", sept.Status);
        Assert.Equal(2000, sept.BytesPurged);
        var oct = report.Sessions.Single(s => s.BatchId == octBatch);
        Assert.Equal(run, oct.RunId);
        Assert.Equal("na quarentena", oct.Status);

        Assert.Equal(2000 + 5000, report.RecoveredTotal);
        Assert.Equal(2000 + 5000, report.RecoveredThisMonth);
        Assert.Equal(300 + 700, report.Recoverable);
        Assert.Equal(3000, report.CleanedTotal);
        Assert.Equal(1000, report.CleanedThisMonth);
        Assert.Equal(12, report.Months.Count);
        Assert.Equal(2000, report.Months.Single(m => m.Month == 9).Cleaned);
        Assert.Equal(7000, report.Months.Single(m => m.Month == 10).Recovered);
        Assert.Equal(5000, Assert.Single(report.RecycleBin).Bytes);

        var csv = CleaningHistory.ToCsv(report);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 3 + 1, lines.Length);
        Assert.StartsWith("tipo;data_utc;lote", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("lixeira;", StringComparison.Ordinal) && l.Contains(";5000;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("diz \"oi\"", "\"diz \"\"oi\"\"\"")]
    [InlineData("x,\"y\"", "\"x,\"\"y\"\"\"")]
    [InlineData("linha\nnova", "linha nova")]
    public void Csv_fields_are_escaped_and_formulas_neutralized(string value, string expected) =>
        Assert.Equal(expected, CleaningHistory.Field(value));
}
