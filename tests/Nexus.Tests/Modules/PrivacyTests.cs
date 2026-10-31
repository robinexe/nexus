using Nexus.Core;
using Nexus.Privacy;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

[Trait("Category", "Unit")]
public sealed class PrivacyTests
{
    private static string CatalogDir => Path.Combine(AppContext.BaseDirectory, "catalog");
    private static PrivacyCatalog Catalog() => PrivacyCatalog.LoadFromDirectory(CatalogDir);
    private static readonly Dictionary<string, string> NoParams = new();

    private static ActionInstance Instance(CatalogEntry e) => e.TryInstantiate(NoParams, out var why) ?? throw new InvalidOperationException(why);

    private static HandlerRegistry Handlers(InMemoryRegistryBackend reg)
    {
        var h = new HandlerRegistry().Add(new RegistryValueHandler(reg));
        foreach (var x in PrivacyHandlers.Create(new HandlerEnvironment(reg, Path.GetTempPath(), OtherAccount: false, Elevated: true))) h.Add(x);
        return h;
    }

    // ------------------------------------------------------------------ catálogo

    [Fact]
    public void Catalog_loads_with_the_whole_directory_and_has_35_plus_actions()
    {
        var all = CatalogLoader.LoadDirectory(CatalogDir);
        var c = Catalog();
        Assert.True(c.Document.Actions.Count >= 35, $"Só {c.Document.Actions.Count} ações.");
        Assert.True(c.Items.Count >= 30, $"Só {c.Items.Count} itens.");
        Assert.All(c.Document.Actions, a => Assert.Contains(all.Actions, x => x.Definition.Key == a.Definition.Key));
        Assert.Equal("nexus.catalog/1", c.Document.Schema);
    }

    [Fact]
    public void Ids_are_unique_and_prefixed()
    {
        var c = Catalog();
        var ids = c.Document.Actions.Select(a => a.Definition.Id).Concat(c.Items.Select(i => i.Id)).Concat(c.Info.Select(i => i.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("privacy.", id, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_action_has_url_evidence_mechanism_risks_and_revert()
    {
        foreach (var a in Catalog().Document.Actions)
        {
            var d = a.Definition;
            Assert.NotEmpty(d.Evidence);
            Assert.All(d.Evidence, e => Assert.True(Uri.TryCreate(e.Source, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps, $"{d.Id}: evidência sem URL https ('{e.Source}')."));
            Assert.False(string.IsNullOrWhiteSpace(d.Mechanism), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.Risks), d.Id);
            Assert.False(string.IsNullOrWhiteSpace(a.Revert), d.Id);
            Assert.NotNull(a.DesiredValue);
            // Fonte só da comunidade nunca vira "SupportedDocumented".
            if (d.Classification == Classification.SupportedDocumented)
                Assert.Contains(d.Evidence, e => e.Level == EvidenceLevel.VendorDocumented);
        }
    }

    [Fact]
    public void Every_item_explains_what_you_lose()
    {
        var c = Catalog();
        Assert.All(c.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Loses), i.Id));
        Assert.Contains(c.Items, i => i.Profile == PrivacyProfile.Recommended);
        Assert.Contains(c.Items, i => i.Profile == PrivacyProfile.Strict);
        Assert.Contains(c.Items, i => i.Profile == PrivacyProfile.Optional);
        // Localização e Encontrar meu dispositivo nunca entram num perfil automático.
        Assert.Equal(PrivacyProfile.Optional, c.Items.Single(i => i.Id == "privacy.item.location").Profile);
        Assert.Equal(PrivacyProfile.Optional, c.Items.Single(i => i.Id == "privacy.item.findmydevice").Profile);
    }

    [Fact]
    public void Every_handler_kind_is_registered_by_the_core_or_this_module()
    {
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), false, true);
        var kinds = PrivacyHandlers.Create(env).Select(h => h.HandlerKind).Append(RegistryValueHandler.Kind).ToHashSet(StringComparer.Ordinal);
        Assert.All(Catalog().Document.Actions, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.Equal([PrivacyPolicyValueHandler.Kind], PrivacyHandlers.Create(env).Select(h => h.HandlerKind));
    }

    [Fact]
    public void Telemetry_targets_required_level_not_security()
    {
        var a = Catalog().Document.Actions.Single(x => x.Definition.Id == "privacy.telemetry.required");
        Assert.Equal("1", a.DesiredValue); // 0 (Security) só existe no Enterprise/Education/Server
        Assert.Equal("AllowTelemetry", a.TargetTemplate["name"]);
        Assert.Contains("Home/Pro", a.Definition.Mechanism, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ alvos permitidos

    private static readonly string[] ForbiddenFragments =
        ["Windows Defender", "SmartScreen", "WindowsUpdate", "DeviceGuard", "BitLocker", "FVE", "Lsa", "SecureBoot", "\\CI", "Services\\", "Control\\"];

    [Fact]
    public void Registry_targets_stay_in_allowed_hives_and_never_touch_critical_keys()
    {
        foreach (var a in Catalog().Document.Actions)
        {
            var t = a.TargetTemplate;
            Assert.True(t["hive"] is "HKCU" or "HKLM", a.Definition.Id);
            Assert.DoesNotContain("SYSTEM\\CurrentControlSet", t["path"], StringComparison.OrdinalIgnoreCase);
            Assert.False(t["path"].StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase), a.Definition.Id);
            foreach (var f in ForbiddenFragments)
                Assert.DoesNotContain(f, t["path"], StringComparison.OrdinalIgnoreCase);
            Assert.True(t["path"].StartsWith("Software\\", StringComparison.OrdinalIgnoreCase) || t["path"].StartsWith("Control Panel\\", StringComparison.Ordinal),
                $"{a.Definition.Id}: caminho fora de Software/Control Panel.");
            if (a.Definition.HandlerKind == RegistryValueHandler.Kind)
                Assert.Null(SafetyPolicy.CheckRegistry(t["hive"], t["path"], t["name"])); // RegistryValue nunca em SOFTWARE\Policies
            else
                Assert.Null(PrivacyPolicyAllowlist.Check(Enum.Parse<RegHive>(t["hive"]), t["path"], t["name"]));
            // HKLM exige administrador; política em HKCU também (a chave Policies só é gravável por administradores).
            if (t["hive"] == "HKLM" || t["path"].StartsWith("SOFTWARE\\Policies", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(PrivilegeLevel.Admin, a.Definition.Privileges);
        }
    }

    [Fact]
    public void Policy_allowlist_is_exact_and_excludes_security_policies()
    {
        Assert.All(PrivacyPolicyAllowlist.All, e =>
        {
            Assert.StartsWith(@"SOFTWARE\Policies\Microsoft\", e.Path, StringComparison.Ordinal);
            foreach (var f in ForbiddenFragments) Assert.DoesNotContain(f, e.Path, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("EnableSmartScreen", e.Name);
        });
        // Mesma chave, outro valor: recusado (System também guarda EnableSmartScreen).
        Assert.NotNull(PrivacyPolicyAllowlist.Check(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen"));
        Assert.NotNull(PrivacyPolicyAllowlist.Check(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware"));
        Assert.NotNull(PrivacyPolicyAllowlist.Check(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection\..\System", "EnableActivityFeed"));
        Assert.NotNull(PrivacyPolicyAllowlist.Check(RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry")); // hive errada
        Assert.NotNull(PrivacyPolicyAllowlist.Check(RegHive.HKLM, @"SOFTWARE\WOW6432Node\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"));
        Assert.Null(PrivacyPolicyAllowlist.Check(RegHive.HKLM, @"software\policies\microsoft\windows\datacollection", "allowtelemetry"));
        // Toda entrada da lista é usada pelo catálogo (nada sobrando aberto sem motivo).
        var used = Catalog().Document.Actions.Where(a => a.Definition.HandlerKind == PrivacyPolicyValueHandler.Kind)
            .Select(a => $"{a.TargetTemplate["hive"]}\\{a.TargetTemplate["path"]}|{a.TargetTemplate["name"]}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(PrivacyPolicyAllowlist.All, e => Assert.Contains($"{e.Hive}\\{e.Path}|{e.Name}", used));
    }

    [Fact]
    public void Every_catalog_action_passes_assess_on_a_new_windows_11()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = Handlers(reg);
        var ctx = new SystemContext(26100, "cpu", null, IsElevated: true);
        foreach (var a in Catalog().Document.Actions)
        {
            var inst = Instance(a);
            var h = handlers.Get(a.Definition.HandlerKind);
            var r = h.Assess(inst, ctx);
            Assert.True(r.Kind == ApplicabilityKind.Applicable, $"{a.Definition.Id}: {r.Reason}");
        }
    }

    // ------------------------------------------------------------------ handler de política

    private static ActionInstance PolicyAction(string hive, string path, string name, string value, string type = "DWord", string? sid = null)
    {
        var target = new Dictionary<string, string> { ["hive"] = hive, ["path"] = path, ["name"] = name, ["type"] = type };
        if (sid is not null) target[UserHive.TargetKey] = sid;
        return new ActionInstance(T.Def("privacy.test", PrivacyPolicyValueHandler.Kind, PrivilegeLevel.Admin), target, StateSnapshot.Of(type, value));
    }

    private const string DataCollection = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";

    [Fact]
    public void Policy_handler_blocks_everything_outside_the_allowlist()
    {
        var h = new PrivacyPolicyValueHandler(new InMemoryRegistryBackend());
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(PolicyAction("HKLM", DataCollection, "AllowTelemetry", "1"), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(PolicyAction("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", "1"), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(PolicyAction("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", "0"), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(PolicyAction("HKLM", DataCollection, "AllowTelemetry", "01"), T.Ctx).Kind); // não canônico
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(PolicyAction("HKLM", DataCollection, "AllowTelemetry", "1", type: "Binary"), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(PolicyAction("HKLM", DataCollection, "AllowTelemetry", "1", sid: "S-1-5-21-1-2-3-1001"), T.Ctx).Kind); // SID só em HKCU
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(PolicyAction("HKCU", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "1", sid: "S-1-5-18"), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(PolicyAction("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "X", "1")));
        Assert.NotNull(h.CheckRestore(PolicyAction("HKLM", @"SOFTWARE\Policies\Microsoft\WindowsUpdate", "X", "1"), StateSnapshot.Absent()));
    }

    [Fact]
    public void Policy_handler_applies_and_restores_absent_value_and_created_key()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = new HandlerRegistry().Add(new PrivacyPolicyValueHandler(reg));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var a = PolicyAction("HKLM", DataCollection, "AllowTelemetry", "1");
        var run = journal.BeginRun("p", "1");
        var r = exec.Execute(run, new DesiredStateResolver(handlers).Resolve([a], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.Equal("1", reg.Read(new RegistryTarget(RegHive.HKLM, DataCollection, "AllowTelemetry", RegView.Default)).Value);

        var back = exec.RevertRun(run).Single();
        Assert.Equal(RevertOutcome.Reverted, back.Outcome);
        Assert.Equal(-1, reg.ValueCount(RegHive.HKLM, DataCollection)); // chave criada pelo NEXUS e vazia: removida
    }

    [Fact]
    public void Policy_handler_restores_previous_value_and_keeps_existing_key()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(new RegistryTarget(RegHive.HKLM, DataCollection, "AllowTelemetry", RegView.Default), "DWord", "3");
        reg.Seed(new RegistryTarget(RegHive.HKLM, DataCollection, "Outro", RegView.Default), "String", "x");
        var handlers = new HandlerRegistry().Add(new PrivacyPolicyValueHandler(reg));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("p", "1");
        exec.Execute(run, new DesiredStateResolver(handlers).Resolve([PolicyAction("HKLM", DataCollection, "AllowTelemetry", "1")], T.Ctx), T.Ctx);
        Assert.Equal("1", reg.Read(new RegistryTarget(RegHive.HKLM, DataCollection, "AllowTelemetry", RegView.Default)).Value);
        exec.RevertRun(run);
        Assert.Equal("3", reg.Read(new RegistryTarget(RegHive.HKLM, DataCollection, "AllowTelemetry", RegView.Default)).Value);
        Assert.Equal(2, reg.ValueCount(RegHive.HKLM, DataCollection));
    }

    [Fact]
    public void Policy_handler_writes_user_policy_into_the_callers_profile()
    {
        var reg = new InMemoryRegistryBackend();
        var h = new PrivacyPolicyValueHandler(reg);
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        var a = PolicyAction("HKCU", @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "1", sid: sid);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(a, T.Ctx).Kind);
        h.Apply(a);
        Assert.True(reg.Read(new RegistryTarget(RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", RegView.Default, sid)).ValueExists);
        Assert.False(reg.Read(new RegistryTarget(RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", RegView.Default)).ValueExists);
    }

    [Fact]
    public void Whole_recommended_profile_applies_and_reverts_on_fake_registry()
    {
        var reg = new InMemoryRegistryBackend();
        var handlers = Handlers(reg);
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var c = Catalog();
        var scanner = new PrivacyScanner(reg);
        var before = scanner.Scan(c, 26100);
        var missing = PrivacyPlanner.MissingFor(before, PrivacyProfile.Recommended);
        var actions = missing.SelectMany(i => i.Pending).Select(p => Instance(p.Entry)).ToList();
        var run = journal.BeginRun("privacidade", c.Document.CatalogVersion);
        var results = exec.Execute(run, new DesiredStateResolver(handlers).Resolve(actions, T.Ctx), T.Ctx);
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));

        var after = scanner.Scan(c, 26100);
        Assert.All(after.Where(s => s.Item.InProfile(PrivacyProfile.Recommended) && s.Status != PrivacyItemStatus.NotApplicable),
            s => Assert.Equal(PrivacyItemStatus.Protected, s.Status));
        Assert.Empty(PrivacyPlanner.MissingFor(after, PrivacyProfile.Recommended));

        Assert.All(exec.RevertRun(run), r => Assert.Equal(RevertOutcome.Reverted, r.Outcome));
        Assert.Equal(PrivacyPlanner.Score(before), PrivacyPlanner.Score(scanner.Scan(c, 26100)));
    }

    // ------------------------------------------------------------------ placar e perfis

    [Fact]
    public void Score_counts_only_items_that_apply_to_this_build()
    {
        var c = Catalog();
        var reg = new InMemoryRegistryBackend();
        var win10 = new PrivacyScanner(reg).Scan(c, 19045);
        var win11 = new PrivacyScanner(reg).Scan(c, 26100);
        Assert.Equal(PrivacyItemStatus.NotApplicable, win11.Single(s => s.Item.Id == "privacy.item.news").Status);       // só Windows 10
        Assert.Equal(PrivacyItemStatus.NotApplicable, win10.Single(s => s.Item.Id == "privacy.item.widgets").Status);    // só Windows 11
        Assert.Equal(PrivacyItemStatus.NotApplicable, win10.Single(s => s.Item.Id == "privacy.item.recall").Status);
        Assert.Equal(PrivacyItemStatus.Exposed, win11.Single(s => s.Item.Id == "privacy.item.recall").Status);
        var s11 = PrivacyPlanner.Score(win11);
        Assert.Equal(0, s11.Protected);
        Assert.Equal(win11.Count(s => s.Status != PrivacyItemStatus.NotApplicable), s11.Applicable);
        Assert.True(s11.Applicable < c.Items.Count);
    }

    [Fact]
    public void Item_is_partial_when_only_some_actions_match_and_protected_values_count()
    {
        var c = Catalog();
        var reg = new InMemoryRegistryBackend();
        reg.Seed(new RegistryTarget(RegHive.HKCU, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", RegView.Default), "DWord", "0");
        reg.Seed(new RegistryTarget(RegHive.HKLM, DataCollection, "AllowTelemetry", RegView.Default), "DWord", "0"); // Enterprise: também protege
        var states = new PrivacyScanner(reg).Scan(c, 26100);
        var feedback = states.Single(s => s.Item.Id == "privacy.item.feedback");
        Assert.Equal(PrivacyItemStatus.Partial, feedback.Status);
        Assert.Equal(2, feedback.Pending.Count());
        Assert.Equal(PrivacyItemStatus.Protected, states.Single(s => s.Item.Id == "privacy.item.telemetry").Status);
        var score = PrivacyPlanner.Score(states);
        Assert.Equal(1, score.Protected);
        Assert.Equal(1, score.Partial);
        // Valor diferente do desejado e fora dos aceitos: exposto.
        reg.Seed(new RegistryTarget(RegHive.HKLM, DataCollection, "AllowTelemetry", RegView.Default), "DWord", "3");
        Assert.Equal(PrivacyItemStatus.Exposed, new PrivacyScanner(reg).Scan(c, 26100).Single(s => s.Item.Id == "privacy.item.telemetry").Status);
    }

    [Fact]
    public void Profiles_are_nested_and_optional_items_never_enter_a_profile()
    {
        var c = Catalog();
        var states = new PrivacyScanner(new InMemoryRegistryBackend()).Scan(c, 26100);
        var rec = PrivacyPlanner.MissingFor(states, PrivacyProfile.Recommended).Select(s => s.Item.Id).ToHashSet();
        var strict = PrivacyPlanner.MissingFor(states, PrivacyProfile.Strict).Select(s => s.Item.Id).ToHashSet();
        Assert.Subset(strict, rec);
        Assert.True(strict.Count > rec.Count);
        Assert.DoesNotContain("privacy.item.location", strict);
        Assert.DoesNotContain("privacy.item.findmydevice", strict);
        Assert.DoesNotContain("privacy.item.wer", rec);      // perda real: só no Rígido
        Assert.DoesNotContain("privacy.item.recall", rec);   // apaga capturas existentes: só no Rígido
        Assert.Contains("privacy.item.telemetry", rec);
        Assert.Empty(PrivacyPlanner.MissingFor(states, PrivacyProfile.Optional));
        // Recomendado: nenhuma ação depende só de fonte da comunidade sem aviso de classificação.
        Assert.All(c.Items.Where(i => i.Profile == PrivacyProfile.Recommended).SelectMany(i => i.Actions),
            a => Assert.True(a.Definition.Classification is Classification.SupportedDocumented or Classification.Conditional));
    }

    [Fact]
    public void Batches_respect_broker_limit_and_never_split_an_item()
    {
        var c = Catalog();
        var states = new PrivacyScanner(new InMemoryRegistryBackend()).Scan(c, 26100);
        var missing = PrivacyPlanner.MissingFor(states, PrivacyProfile.Strict);
        var batches = PrivacyPlanner.Batches(missing);
        Assert.True(batches.Count >= 2);
        Assert.All(batches, b => Assert.InRange(b.Sum(i => i.Pending.Count()), 1, PrivacyPlanner.MaxActionsPerRequest));
        Assert.Equal(missing.Count, batches.Sum(b => b.Count));
        var keys = PrivacyPlanner.ActionKeys(missing);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.EndsWith("@1", k, StringComparison.Ordinal));
        Assert.True(PrivacyPlanner.NeedsAdmin(missing));
        var text = PrivacyPlanner.ConfirmationText(batches[0], 1, batches.Count);
        Assert.Contains("Você perde", text, StringComparison.Ordinal);
        Assert.Contains("Parte 1 de", text, StringComparison.Ordinal);
        Assert.Contains("Desfazer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void User_only_selection_does_not_need_admin()
    {
        var states = new PrivacyScanner(new InMemoryRegistryBackend()).Scan(Catalog(), 26100);
        var userOnly = states.Where(s => !s.Item.NeedsAdmin && s.Status == PrivacyItemStatus.Exposed).ToList();
        Assert.NotEmpty(userOnly);
        Assert.False(PrivacyPlanner.NeedsAdmin(userOnly));
    }

    [Fact]
    public void Scanner_never_writes()
    {
        var ro = new ReadOnlyRegistryBackend(new InMemoryRegistryBackend());
        var t = new RegistryTarget(RegHive.HKCU, @"Software\X", "Y", RegView.Default);
        Assert.Throws<InvalidOperationException>(() => ro.Write(t, "DWord", "1"));
        Assert.Throws<InvalidOperationException>(() => ro.DeleteValue(t));
        Assert.Throws<InvalidOperationException>(() => ro.DeleteKeyIfEmpty(RegHive.HKCU, @"Software\X", RegView.Default));
        Assert.False(ro.Read(t).ValueExists);
    }

    [Fact]
    public void Consent_store_reading_and_settings_allowlist()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(new RegistryTarget(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "Value", RegView.Default), "String", "Deny");
        var s = new PrivacyScanner(reg);
        Assert.Equal("Deny", s.ReadConsent("webcam"));
        Assert.Null(s.ReadConsent("microphone"));
        Assert.Null(s.ReadConsent(@"..\webcam"));
        Assert.True(SettingsLauncher.IsAllowed("ms-settings:privacy-webcam"));
        Assert.False(SettingsLauncher.IsAllowed("cmd.exe"));
        Assert.False(SettingsLauncher.IsAllowed("ms-settings:windowsdefender"));
        Assert.All(Catalog().Info.Where(i => i.SettingsUri is not null), i => Assert.True(SettingsLauncher.IsAllowed(i.SettingsUri!)));
    }

    [Fact]
    public void Parser_refuses_orphan_actions_and_shared_actions()
    {
        const string head = "{ \"catalogVersion\": \"t\", \"schema\": \"nexus.catalog/1\", \"actions\": [ " +
            "{ \"id\": \"privacy.a\", \"version\": 1, \"title\": \"A\", \"classification\": \"SupportedDocumented\", \"handlerKind\": \"RegistryValue\", " +
            "\"target\": { \"hive\": \"HKCU\", \"path\": \"Software\\\\X\", \"name\": \"A\", \"type\": \"DWord\" }, \"desiredValue\": 0, " +
            "\"privileges\": \"User\", \"persistence\": \"Persistent\", \"reboot\": \"None\" } ]";
        Assert.Throws<InvalidDataException>(() => PrivacyCatalog.Parse(head + " }"));
        const string item = "{ \"id\": \"privacy.item.a\", \"theme\": \"t\", \"title\": \"t\", \"summary\": \"s\", \"loses\": \"l\", \"profile\": \"Recommended\", \"actions\": [\"privacy.a\"] }";
        Assert.Single(PrivacyCatalog.Parse(head + ", \"privacyItems\": [ " + item + " ] }").Items);
        Assert.Throws<InvalidDataException>(() => PrivacyCatalog.Parse(head + ", \"privacyItems\": [ " + item + ", " + item.Replace("privacy.item.a", "privacy.item.b", StringComparison.Ordinal) + " ] }"));
        Assert.Throws<InvalidDataException>(() => PrivacyCatalog.Parse(head.Replace("privacy.a", "outro.a", StringComparison.Ordinal) + " }"));
    }
}
