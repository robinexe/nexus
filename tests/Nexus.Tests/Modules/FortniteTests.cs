using System.Text;
using Nexus.Core;
using Nexus.GameProfiles;
using Nexus.GameProfiles.Fortnite;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>
/// Fortnite: edição sem perdas do GameUserSettings.ini (ler, mudar, desfazer exatamente), lista fechada de chaves/valores,
/// recusa com o jogo aberto, perfil pelo SID, catálogo carregando e instanciando, e a fiação (módulo no aplicador).
/// </summary>
[Trait("Category", "Unit")]
public sealed class FortniteTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private readonly string _local = Directory.CreateTempSubdirectory("nexus-fn-").FullName;
    private bool _running;

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_local, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_local, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string IniPath => FortniteSettings.FilePath(_local);

    // Trecho real (2024) de um GameUserSettings.ini, com BOM UTF-8 e CRLF, encurtado.
    private const string RealIni =
        "[/Script/FortniteGame.FortGameUserSettings]\r\n" +
        "FullscreenMode=0\r\n" +
        "bMotionBlur=True\r\n" +
        "bShowGrass=True\r\n" +
        "LatencyTweak2=0\r\n" +
        "bDisableMouseAcceleration=False\r\n" +
        "bUseVSync=True\r\n" +
        "FrameRateLimit=0.000000\r\n" +
        "NamedCounts=((\"EcosystemTrailer\", 1),(\"SeasonTrailer\", 28))\r\n" +
        "\r\n" +
        "\r\n" +
        "[ScalabilityGroups]\r\n" +
        "sg.ResolutionQuality=100\r\n" +
        "sg.ShadowQuality=3\r\n" +
        "\r\n" +
        "\r\n" +
        "[D3DRHIPreference]\r\n" +
        "PreferredRHI=dx11\r\n" +
        "PreferredFeatureLevel=sm5\r\n" +
        "\r\n" +
        "\r\n" +
        "[PerformanceMode]\r\n" +
        "MeshQuality=0\r\n" +
        "\r\n";

    private void WriteIni(string text, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        encoding ??= new UTF8Encoding(true);
        File.WriteAllBytes(IniPath, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
    }

    private FortniteIniHandler Handler(bool otherAccount = false) =>
        new(sid => otherAccount && sid is null ? null : _local, () => _running, otherAccount);

    private static CatalogEntry Entry(string key) => Wiring.Entry(key) ?? throw new InvalidOperationException(key + " não está no catálogo");

    private static ActionInstance Instance(string key, string? value = null)
    {
        var e = Entry(key);
        if (value is not null) e = e with { DesiredValue = value };
        return e.TryInstantiate(new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);
    }

    private (InMemoryJournal Journal, ActionExecutor Exec, DesiredStateResolver Resolver) Engine(FortniteIniHandler? handler = null)
    {
        var handlers = new HandlerRegistry().Add(handler ?? Handler());
        var journal = new InMemoryJournal();
        return (journal, new ActionExecutor(handlers, journal), new DesiredStateResolver(handlers));
    }

    // ================================================================== IniDocument

    [Fact]
    public void Ini_round_trip_is_byte_identical_and_edit_changes_only_one_line()
    {
        var doc = IniDocument.Parse(RealIni);
        Assert.Equal(RealIni, doc.ToString());
        Assert.Equal((IniKeyState.Present, "0.000000"), doc.Get(FortniteSettings.MainSection, "FrameRateLimit"));
        Assert.Equal((IniKeyState.Present, "dx11"), doc.Get("d3drhipreference", "preferredrhi")); // nomes sem diferenciar maiúsculas

        doc.Set(FortniteSettings.MainSection, "FrameRateLimit", "141.000000");
        var after = doc.ToString();
        var a = RealIni.Split("\r\n");
        var b = after.Split("\r\n");
        Assert.Equal(a.Length, b.Length);
        Assert.Single(Enumerable.Range(0, a.Length), i => a[i] != b[i]);
        Assert.Contains("FrameRateLimit=141.000000\r\n", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Ini_add_goes_inside_its_section_before_blank_lines_and_remove_restores_exact_text()
    {
        var doc = IniDocument.Parse(RealIni);
        doc.Set(FortniteSettings.ScalabilitySection, "sg.FoliageQuality", "0");
        var text = doc.ToString();
        Assert.Contains("sg.ShadowQuality=3\r\nsg.FoliageQuality=0\r\n\r\n\r\n[D3DRHIPreference]", text, StringComparison.Ordinal);
        Assert.True(doc.Remove(FortniteSettings.ScalabilitySection, "sg.FoliageQuality"));
        Assert.Equal(RealIni, doc.ToString());
        Assert.False(doc.Remove(FortniteSettings.ScalabilitySection, "sg.FoliageQuality"));
    }

    [Fact]
    public void Ini_missing_section_is_appended_and_lf_files_stay_lf()
    {
        var doc = IniDocument.Parse("[A]\nx=1\n");
        doc.Set("D3DRHIPreference", "PreferredRHI", "dx12");
        Assert.Equal("[A]\nx=1\n\n[D3DRHIPreference]\nPreferredRHI=dx12\n", doc.ToString());
        var noFinalNewline = IniDocument.Parse("[A]\nx=1");
        noFinalNewline.Set("A", "x", "2");
        Assert.Equal("[A]\nx=2", noFinalNewline.ToString());
    }

    [Fact]
    public void Ini_ambiguous_keys_and_sections_are_refused_and_values_cannot_inject_lines()
    {
        Assert.Equal(IniKeyState.Ambiguous, IniDocument.Parse("[S]\nk=1\nk=2\n").Get("S", "k").State);
        Assert.Equal(IniKeyState.Ambiguous, IniDocument.Parse("[S]\n+k=1\n").Get("S", "k").State);
        Assert.Equal(IniKeyState.Ambiguous, IniDocument.Parse("[S]\nk=1\n[S]\nj=2\n").Get("S", "k").State);
        Assert.Equal(IniKeyState.Absent, IniDocument.Parse("[S]\n;k=1\n[T]\nk=1\n").Get("S", "k").State);
        var doc = IniDocument.Parse("[S]\nk=1\nk=2\n");
        Assert.Throws<InvalidOperationException>(() => doc.Set("S", "k", "3"));
        Assert.Throws<ArgumentException>(() => IniDocument.Parse("[S]\nk=1\n").Set("S", "k", "1\r\n[D3DRHIPreference]\nPreferredRHI=dx11"));
        Assert.Throws<ArgumentException>(() => IniDocument.Parse("").Set("S]\n[T", "k", "1"));
    }

    // ================================================================== lista fechada

    [Fact]
    public void Allowlist_accepts_only_known_keys_and_values()
    {
        Assert.True(FortniteSettings.Find("D3DRHIPreference", "PreferredFeatureLevel")!.Accepts("es31"));
        Assert.False(FortniteSettings.Find("D3DRHIPreference", "PreferredFeatureLevel")!.Accepts("sm5"));
        Assert.False(FortniteSettings.Find("D3DRHIPreference", "PreferredRHI")!.Accepts("dx11")); // fora do menu desde a 40.10
        Assert.True(FortniteSettings.Find(FortniteSettings.MainSection, "FrameRateLimit")!.Accepts("141.000000"));
        Assert.False(FortniteSettings.Find(FortniteSettings.MainSection, "FrameRateLimit")!.Accepts("141"));
        Assert.False(FortniteSettings.Find(FortniteSettings.MainSection, "LatencyTweak2")!.Accepts("3"));
        Assert.False(FortniteSettings.Find(FortniteSettings.MainSection, "bShowGrass")!.Accepts("false")); // o jogo grava False
        Assert.Null(FortniteSettings.Find(FortniteSettings.MainSection, "bDisableMouseAcceleration")); // mito: não faz nada no Windows
        Assert.Null(FortniteSettings.Find("SystemSettings", "r.Fog")); // Engine.ini/cvars: nunca
        Assert.Null(FortniteSettings.Find("d3drhipreference", "PreferredRHI")); // seção exata
    }

    [Fact]
    public void Handler_blocks_keys_outside_the_allowlist_and_unknown_target_fields()
    {
        WriteIni(RealIni);
        var good = Instance(FortniteSettings.VSyncOffAction);
        var bad = good with { Target = new Dictionary<string, string>(good.Target) { ["key"] = "bDisableMouseAcceleration" } };
        Assert.Equal(ApplicabilityKind.Blocked, Handler().Assess(bad, T.Ctx).Kind);
        var extra = good with { Target = new Dictionary<string, string>(good.Target) { ["file"] = @"..\..\Engine.ini" } };
        Assert.Equal(ApplicabilityKind.Blocked, Handler().Assess(extra, T.Ctx).Kind);
        var badValue = good with { Desired = StateSnapshot.Of(FortniteIniHandler.StateType, "Yes") };
        Assert.Equal(ApplicabilityKind.Blocked, Handler().Assess(badValue, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => Handler().Apply(badValue));
        Assert.Equal(RealIni, File.ReadAllText(IniPath)); // nada foi escrito
    }

    // ================================================================== Detect/Apply/Restore pelo executor

    [Fact]
    public void Performance_mode_applies_through_the_executor_and_reverts_exactly()
    {
        WriteIni(RealIni);
        var before = File.ReadAllBytes(IniPath);
        var (journal, exec, resolver) = Engine();
        var run = journal.BeginRun("fortnite", "1");
        var plan = resolver.Resolve([Instance(FortniteSettings.RhiDx12Action), Instance(FortniteSettings.PerformanceModeAction)], T.Ctx);
        var results = exec.Execute(run, plan, T.Ctx);
        Assert.All(results, r => Assert.Equal(ExecutionOutcome.Verified, r.Outcome));

        var after = File.ReadAllText(IniPath);
        Assert.Contains("PreferredRHI=dx12\r\nPreferredFeatureLevel=es31\r\n", after, StringComparison.Ordinal);
        Assert.Equal(before.Take(3), File.ReadAllBytes(IniPath).Take(3)); // BOM mantido
        Assert.Contains("NamedCounts=((\"EcosystemTrailer\", 1),(\"SeasonTrailer\", 28))", after, StringComparison.Ordinal);

        foreach (var e in journal.Entries(run).Reverse()) Assert.Equal(RevertOutcome.Reverted, exec.Revert(e.Id).Outcome);
        Assert.Equal(before, File.ReadAllBytes(IniPath)); // byte a byte
    }

    [Fact]
    public void Absent_key_is_added_and_undo_removes_only_that_line()
    {
        // arquivo sem a chave FrameRateLimit
        WriteIni(RealIni.Replace("FrameRateLimit=0.000000\r\n", "", StringComparison.Ordinal));
        var before = File.ReadAllBytes(IniPath);
        var (journal, exec, resolver) = Engine();
        var run = journal.BeginRun("fortnite", "1");
        var r = exec.Execute(run, resolver.Resolve([Instance(FortniteSettings.FpsLimitAction, "141.000000")], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.Contains("FrameRateLimit=141.000000", File.ReadAllText(IniPath), StringComparison.Ordinal);
        var entry = journal.Entries(run).Single();
        Assert.False(entry.Original!.Exists);
        Assert.Equal(RevertOutcome.Reverted, exec.Revert(entry.Id).Outcome);
        Assert.Equal(before, File.ReadAllBytes(IniPath));
    }

    [Fact]
    public void Utf16_file_with_accents_is_edited_in_utf16_and_kept_intact()
    {
        var text = RealIni.Replace("FullscreenMode=0", "CustomVoiceChatInputDevice=Microfone (Áudio de Alta Definição)", StringComparison.Ordinal);
        WriteIni(text, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var before = File.ReadAllBytes(IniPath);
        var (journal, exec, resolver) = Engine();
        var run = journal.BeginRun("fortnite", "1");
        Assert.Equal(ExecutionOutcome.Verified, exec.Execute(run, resolver.Resolve([Instance(FortniteSettings.MotionBlurOffAction)], T.Ctx), T.Ctx).Single().Outcome);
        var bytes = File.ReadAllBytes(IniPath);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        var now = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.Contains("Microfone (Áudio de Alta Definição)", now, StringComparison.Ordinal);
        Assert.Contains("bMotionBlur=False", now, StringComparison.Ordinal);
        Assert.Equal(RevertOutcome.Reverted, exec.Revert(journal.Entries(run).Single().Id).Outcome);
        Assert.Equal(before, File.ReadAllBytes(IniPath));
    }

    [Fact]
    public void Invalid_utf8_is_refused_instead_of_corrupting_the_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllBytes(IniPath, [.. Encoding.ASCII.GetBytes("[/Script/FortniteGame.FortGameUserSettings]\r\nName=Jo"), 0xE3, .. Encoding.ASCII.GetBytes("o\r\nbUseVSync=True\r\n")]);
        var a = Handler().Assess(Instance(FortniteSettings.VSyncOffAction), T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, a.Kind);
        Assert.Contains("Codificação", a.Reason, StringComparison.Ordinal);
    }

    // ================================================================== recusas

    [Fact]
    public void Refuses_while_the_game_is_running_both_in_assess_and_in_apply()
    {
        WriteIni(RealIni);
        _running = true;
        var a = Instance(FortniteSettings.VSyncOffAction);
        var assess = Handler().Assess(a, T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, assess.Kind);
        Assert.Equal(FortniteIniHandler.RunningMessage, assess.Reason);
        Assert.Throws<InvalidOperationException>(() => Handler().Apply(a));
        Assert.Throws<InvalidOperationException>(() => Handler().Restore(a, StateSnapshot.Of(FortniteIniHandler.StateType, "True")));
        Assert.Equal(RealIni, File.ReadAllText(IniPath));

        // pelo executor: bloqueada no plano, nada escrito
        var (journal, exec, resolver) = Engine();
        var r = exec.Execute(journal.BeginRun("fortnite", "1"), resolver.Resolve([a], T.Ctx), T.Ctx).Single();
        Assert.NotEqual(ExecutionOutcome.Verified, r.Outcome);
        Assert.Equal(RealIni, File.ReadAllText(IniPath));
    }

    [Fact]
    public void Undo_while_the_game_is_running_fails_and_works_after_closing()
    {
        WriteIni(RealIni);
        var (journal, exec, resolver) = Engine();
        var run = journal.BeginRun("fortnite", "1");
        exec.Execute(run, resolver.Resolve([Instance(FortniteSettings.VSyncOffAction)], T.Ctx), T.Ctx);
        var entry = journal.Entries(run).Single();
        _running = true;
        Assert.NotEqual(RevertOutcome.Reverted, exec.Revert(entry.Id).Outcome);
        Assert.Contains("bUseVSync=False", File.ReadAllText(IniPath), StringComparison.Ordinal);
        _running = false;
        Assert.Equal(RevertOutcome.Reverted, exec.Revert(entry.Id).Outcome);
        Assert.Equal(RealIni, File.ReadAllText(IniPath));
    }

    [Fact]
    public void Missing_file_is_not_applicable_and_never_created()
    {
        var a = Handler().Assess(Instance(FortniteSettings.VSyncOffAction), T.Ctx);
        Assert.Equal(ApplicabilityKind.NotApplicable, a.Kind);
        Assert.Throws<FileNotFoundException>(() => Handler().Apply(Instance(FortniteSettings.VSyncOffAction)));
        Assert.False(File.Exists(IniPath));
        Assert.False(Handler().Detect(Instance(FortniteSettings.VSyncOffAction)).Exists);
    }

    [Fact]
    public void Read_only_file_is_refused_with_an_explanation()
    {
        WriteIni(RealIni);
        File.SetAttributes(IniPath, FileAttributes.ReadOnly);
        var a = Handler().Assess(Instance(FortniteSettings.VSyncOffAction), T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, a.Kind);
        Assert.Contains("somente leitura", a.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void External_change_between_read_and_write_is_not_overwritten()
    {
        WriteIni(RealIni);
        var read = IniFiles.Read(IniPath);
        File.AppendAllText(IniPath, "x=1\r\n");
        Assert.Throws<IOException>(() => IniFiles.WriteAtomic(IniPath, "[A]\r\n", read.Encoding, read.Sha256));
        Assert.EndsWith("x=1\r\n", File.ReadAllText(IniPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Other_account_without_sid_reads_nothing_and_with_sid_uses_that_profile()
    {
        WriteIni(RealIni);
        string? asked = "-";
        var h = new FortniteIniHandler(sid => { asked = sid; return _local; }, () => false, otherAccount: true);
        var a = Instance(FortniteSettings.VSyncOffAction);
        var blocked = h.Assess(a, T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, blocked.Kind);
        Assert.Equal("-", asked); // o resolvedor de perfil nem foi chamado

        Assert.True(UserProfileScope.IsUserScoped(a)); // o broker-serviço anexa o SID
        var bound = UserProfileScope.Bind(a, Sid);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(bound, T.Ctx).Kind);
        Assert.Equal(Sid, asked);
        var forged = a with { Target = new Dictionary<string, string>(a.Target) { [UserProfileScope.TargetKey] = "S-1-5-18" } };
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(forged, T.Ctx).Kind);
    }

    [Fact]
    public void Profile_folder_comes_from_profile_list_by_sid()
    {
        var reg = new InMemoryRegistryBackend();
        reg.Seed(new RegistryTarget(RegHive.HKLM, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{Sid}", "ProfileImagePath", RegView.Registry64), "ExpandString", @"C:\Users\Ana");
        var local = WindowsProfiles.LocalAppDataForSid(reg, Sid);
        Assert.NotNull(local);
        Assert.StartsWith(@"C:\Users\Ana", local, StringComparison.Ordinal);
        Assert.EndsWith("Local", local, StringComparison.Ordinal);
        Assert.Null(WindowsProfiles.LocalAppDataForSid(reg, "S-1-5-18"));
        Assert.Null(WindowsProfiles.LocalAppDataForSid(reg, "S-1-5-21-1-2-3-999")); // sem entrada
        reg.Seed(new RegistryTarget(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\S-1-5-21-1-2-3-1002", "ProfileImagePath", RegView.Registry64), "ExpandString", @"\\srv\perfis\Ana");
        Assert.Null(WindowsProfiles.LocalAppDataForSid(reg, "S-1-5-21-1-2-3-1002")); // rede: recusado
    }

    [Fact]
    public void Check_restore_rejects_unsafe_journal_values()
    {
        WriteIni(RealIni);
        var a = Instance(FortniteSettings.VSyncOffAction);
        Assert.Null(Handler().CheckRestore(a, StateSnapshot.Of(FortniteIniHandler.StateType, "True")));
        Assert.Null(Handler().CheckRestore(a, StateSnapshot.Absent()));
        Assert.NotNull(Handler().CheckRestore(a, StateSnapshot.Of(FortniteIniHandler.StateType, "True\r\n[X]")));
        Assert.NotNull(Handler().CheckRestore(a, StateSnapshot.Of("DWord", "1")));
    }

    // ================================================================== estado para as telas

    [Fact]
    public void State_reader_shows_mode_cap_and_reflex()
    {
        WriteIni(RealIni.Replace("PreferredFeatureLevel=sm5", "PreferredFeatureLevel=es31", StringComparison.Ordinal));
        var s = FortniteState.Read(_local);
        Assert.True(s.FileFound);
        Assert.True(s.IsPerformanceMode);
        Assert.Equal("0.000000", s.FrameRateLimit);
        Assert.Equal("0", s.Reflex);
        Assert.False(FortniteState.Read(Path.Combine(_local, "nada")).FileFound);
        Assert.Equal("141.000000", FortniteSettings.SuggestedFpsLimit(144));
        Assert.Equal("237.000000", FortniteSettings.SuggestedFpsLimit(240));
        Assert.Equal("57.000000", FortniteSettings.SuggestedFpsLimit(60));
        Assert.Null(FortniteSettings.SuggestedFpsLimit(0));
        Assert.Equal("141", FortniteSettings.FpsLabel("141.000000"));
    }

    // ================================================================== catálogo e fiação

    [Fact]
    public void Catalog_loads_and_every_fortnite_action_instantiates_with_the_handler_rules()
    {
        var actions = Wiring.Catalog.Actions.Where(a => a.Definition.Id.StartsWith("fortnite.", StringComparison.Ordinal)).ToList();
        Assert.True(actions.Count >= 8);
        foreach (var e in actions)
        {
            Assert.Equal(FortniteIniHandler.Kind, e.Definition.HandlerKind);
            Assert.Equal(PrivilegeLevel.User, e.Definition.Privileges);
            Assert.Equal("User", e.TargetTemplate["scope"]);
            Assert.Equal(FortniteIniHandler.StateType, e.DesiredType);
            var key = FortniteSettings.Find(e.TargetTemplate["section"], e.TargetTemplate["key"]);
            Assert.True(key is not null, $"{e.Definition.Key}: chave fora da lista do handler");
            Assert.True(e.Translations.ContainsKey("en") && e.Translations.ContainsKey("es"), $"{e.Definition.Key}: falta i18n");
            Assert.NotEmpty(e.Definition.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(e.Revert));
            var values = e.DesiredValue is { } v ? [v] : e.Definition.AllowedValues ?? [];
            Assert.NotEmpty(values);
            foreach (var value in values)
            {
                Assert.True(key!.Accepts(value), $"{e.Definition.Key}: valor '{value}' recusado pelo handler");
                var inst = (e with { DesiredValue = value }).TryInstantiate(new Dictionary<string, string>(), out var why);
                Assert.True(inst is not null, why);
                Assert.Equal((FortniteIniHandler.StateType, value), (inst!.Desired.Type, inst.Desired.Value));
            }
        }
        Assert.Equal(FortniteSettings.FpsLimits, Entry(FortniteSettings.FpsLimitAction).Definition.AllowedValues);
        foreach (var key in new[] { FortniteSettings.RhiDx12Action, FortniteSettings.PerformanceModeAction, FortniteSettings.Dx12FullAction, FortniteSettings.FpsLimitAction,
                     FortniteSettings.ReflexAction, FortniteSettings.VSyncOffAction, FortniteSettings.MotionBlurOffAction, FortniteSettings.ShadowsOffAction, FortniteSettings.GrassOffAction })
            Assert.NotNull(Wiring.Entry(key));
        var myths = Wiring.Catalog.Rejected.Select(r => r.Id).ToList();
        foreach (var m in new[] { "myth.fortnite.mouse-acceleration-key", "myth.fortnite.useallavailablecores", "myth.fortnite.notexturestreaming", "myth.fortnite.engine-ini-tweaks" })
            Assert.Contains(m, myths);
    }

    [Fact]
    public void Performance_mode_and_full_dx12_conflict_and_both_need_the_dx12_renderer()
    {
        WriteIni(RealIni);
        var (_, _, resolver) = Engine();
        var both = resolver.Resolve([Instance(FortniteSettings.RhiDx12Action), Instance(FortniteSettings.PerformanceModeAction), Instance(FortniteSettings.Dx12FullAction)], T.Ctx);
        Assert.Contains(both.Items, i => i.Status == PlanStatus.Blocked);
        var alone = resolver.Resolve([Instance(FortniteSettings.PerformanceModeAction)], T.Ctx);
        Assert.Equal(PlanStatus.Blocked, alone.Items.Single().Status); // sem o renderizador DirectX 12 no pedido
    }

    [Fact]
    public void Module_creates_the_handler_and_is_registered_in_broker_selftest_and_preview()
    {
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), _local, OtherAccount: false, Elevated: false);
        Assert.Contains(GameProfilesHandlers.Create(env), h => h.HandlerKind == FortniteIniHandler.Kind);
        foreach (var file in new[] { "src/Nexus.Broker.Host/ModuleHandlers.cs", "src/Nexus.SelfTest/NexusChecks.cs", "src/Nexus.Desktop/Services/NexusServices.cs" })
            Assert.Contains("Nexus.GameProfiles.GameProfilesHandlers.Create", File.ReadAllText(Path.Combine(Wiring.Root, file)), StringComparison.Ordinal);
        Assert.Contains("FortniteClient-Win64-Shipping", FortniteSettings.BlockingProcesses);
        Assert.Equal("FortniteClient-Win64-Shipping.exe", FortniteSettings.GameExe);
    }

    [Fact]
    public void Every_fortnite_action_is_reachable_from_the_screens()
    {
        // Jogos (simples): os pedidos ficam nos planos de GameOptionStates.cs (mesma fonte para pedir e para ler o estado).
        var simple = File.ReadAllText(Path.Combine(Wiring.Root, "src/Nexus.Desktop/ViewModels/Simple/SimplePages.cs")) +
                     File.ReadAllText(Path.Combine(Wiring.Root, "src/Nexus.Desktop/ViewModels/Simple/GameOptionStates.cs"));
        var advanced = File.ReadAllText(Path.Combine(Wiring.Root, "src/Nexus.Desktop/ViewModels/Modules/GpuLabPage.Fortnite.cs"));
        var names = typeof(FortniteSettings).GetFields().Where(f => f.IsLiteral && f.Name.EndsWith("Action", StringComparison.Ordinal)).ToList();
        Assert.Equal(Wiring.Catalog.Actions.Count(a => a.Definition.Id.StartsWith("fortnite.", StringComparison.Ordinal)), names.Count);
        foreach (var f in names)
            Assert.True(advanced.Contains("FortniteSettings." + f.Name, StringComparison.Ordinal), $"{f.Name} sem botão em Placa de vídeo › Ajustes por jogo");
        // Jogos (simples): as três opções de um clique
        foreach (var name in new[] { "RhiDx12Action", "PerformanceModeAction", "FpsLimitAction", "ReflexAction", "MotionBlurOffAction" })
            Assert.Contains("FortniteSettings." + name, simple, StringComparison.Ordinal);
    }
}
