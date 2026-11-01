using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Nexus.Core;
using Nexus.Measurement;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

/// <summary>
/// PresentMon embutido (localização com o embutido primeiro, conferência por hash/assinatura antes de executar) e a ação
/// measure.perflog-group.join (grupo "Usuários do log de desempenho"), com backends falsos.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MeasurePresentMonTests
{
    private const string App = @"C:\Program Files\NEXUS";
    private static string Bundled => PresentMonTrust.BundledPath(App);

    private sealed class FakeFiles(params string[] files) : IPresentMonFiles
    {
        // Caminhos do Windows também no Linux: barra e contrabarra valem igual.
        private static string N(string p) => p.Replace('/', '\\');
        private readonly HashSet<string> _files = new(files.Select(N), StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => _files.Contains(N(path));
        public IEnumerable<string> Executables(string directory) =>
            _files.Where(f => string.Equals(f[..f.LastIndexOf('\\')], N(directory), StringComparison.OrdinalIgnoreCase) &&
                              f[(f.LastIndexOf('\\') + 1)..].StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeTrust : IPresentMonTrustProbe
    {
        public Dictionary<string, string?> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PresentMonSignature> Signatures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Checked { get; } = [];
        public string? Sha256(string path) { Checked.Add(path); return Hashes.TryGetValue(path, out var h) ? h : "00"; }
        public PresentMonSignature Signature(string path) => Signatures.TryGetValue(path, out var s) ? s : PresentMonSignature.Invalid("sem assinatura");
    }

    private static string Folder(Environment.SpecialFolder f) => f switch
    {
        Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
        Environment.SpecialFolder.ProgramFilesX86 => @"C:\Program Files (x86)",
        Environment.SpecialFolder.LocalApplicationData => @"C:\Users\ana\AppData\Local",
        Environment.SpecialFolder.UserProfile => @"C:\Users\ana",
        _ => "",
    };

    // ------------------------------------------------------------------ conferência

    [Fact]
    public void Known_sha256_is_trusted_even_without_signature()
    {
        var trust = new FakeTrust { Hashes = { [Bundled] = PresentMonTrust.BundledSha256.ToUpperInvariant() } };
        var v = PresentMonTrust.Verify(Bundled, trust);
        Assert.True(v.Trusted);
        Assert.Contains("SHA-256", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_intel_signature_is_trusted_for_unknown_hash()
    {
        const string p = @"C:\Users\ana\Downloads\PresentMon-2.7.0-x64.exe";
        var trust = new FakeTrust { Signatures = { [p] = new(true, "Intel Corporation", "ok") } };
        Assert.True(PresentMonTrust.Verify(p, trust).Trusted);
    }

    [Theory]
    [InlineData(true, "Intel Corp Fake LLC")]
    [InlineData(true, "Mallory")]
    [InlineData(false, "Intel Corporation")] // nome certo, assinatura quebrada (arquivo alterado)
    [InlineData(false, null)]
    public void Wrong_signer_or_invalid_signature_is_refused_with_reason(bool valid, string? signer)
    {
        const string p = @"C:\Users\ana\Downloads\PresentMon-2.6.0-x64.exe";
        var trust = new FakeTrust { Signatures = { [p] = new(valid, signer, valid ? "ok" : "a assinatura não confere") } };
        var v = PresentMonTrust.Verify(p, trust);
        Assert.False(v.Trusted);
        Assert.Contains("não vai executar", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_file_is_refused()
    {
        var trust = new FakeTrust { Hashes = { [Bundled] = null } };
        Assert.False(PresentMonTrust.Verify(Bundled, trust).Trusted);
    }

    [Fact]
    public void Real_bundled_file_in_repository_matches_the_known_hash()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var exe = Path.Combine(dir!.FullName, "tools", "presentmon", "PresentMon-2.6.0-x64.exe");
        Assert.Equal(PresentMonTrust.BundledSha256, PresentMonTrust.FileSha256(exe));
        Assert.True(PresentMonTrust.Verify(exe, new HashOnlyTrustProbe()).Trusted);
        Assert.True(File.Exists(Path.Combine(dir.FullName, "tools", "presentmon", "LICENSE.txt")));
        Assert.Contains(PresentMonTrust.BundledSha256, File.ReadAllText(Path.Combine(dir.FullName, "THIRD-PARTY-NOTICES.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_output_has_the_bundled_copy_next_to_the_cli()
    {
        // O build da CLI (e da interface) copia o PresentMon para <saída>\tools\PresentMon\PresentMon.exe.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var cliOut = Path.Combine(dir!.FullName, "src", "Nexus.Cli", "bin", config, "net10.0");
        Assert.True(Directory.Exists(cliOut), "compile a solução inteira antes dos testes");
        Assert.Equal(PresentMonTrust.BundledSha256, PresentMonTrust.FileSha256(PresentMonTrust.BundledPath(cliOut)));
        Assert.True(File.Exists(Path.Combine(cliOut, "tools", "PresentMon", "LICENSE.txt")));
        Assert.True(File.Exists(Path.Combine(cliOut, "THIRD-PARTY-NOTICES.txt")));
    }

    // ------------------------------------------------------------------ localização

    [Fact]
    public void Bundled_copy_comes_first_even_when_a_path_is_configured()
    {
        const string configured = @"C:\Users\ana\Downloads\PresentMon-2.3.0-x64.exe";
        var files = new FakeFiles(Bundled, configured);
        var list = PresentMonLocator.Candidates(configured, App, files, Folder);
        Assert.Equal((Bundled, PresentMonSource.Bundled), list[0]);

        var trust = new FakeTrust { Hashes = { [Bundled] = PresentMonTrust.BundledSha256 } };
        var found = PresentMonLocator.Locate(configured, App, trust, files, Folder);
        Assert.NotNull(found);
        Assert.Equal(Bundled, found!.Path);
        Assert.Equal(PresentMonSource.Bundled, found.Source);
        Assert.True(found.Usable);
        Assert.Equal([Bundled], trust.Checked); // parou no primeiro aprovado
    }

    [Fact]
    public void Tampered_bundled_copy_is_skipped_for_a_trusted_alternative()
    {
        const string intel = @"C:\Program Files\Intel\PresentMon\PresentMonCli\PresentMon.exe";
        var files = new FakeFiles(Bundled, intel);
        var trust = new FakeTrust
        {
            Hashes = { [Bundled] = "deadbeef" },
            Signatures = { [intel] = new(true, "Intel Corporation", "ok") },
        };
        var found = PresentMonLocator.Locate(null, App, trust, files, Folder);
        Assert.Equal(intel, found!.Path);
        Assert.Equal(PresentMonSource.Found, found.Source);
        Assert.True(found.Usable);
    }

    [Fact]
    public void Only_untrusted_candidates_returns_the_first_with_the_refusal_reason()
    {
        var files = new FakeFiles(Bundled);
        var found = PresentMonLocator.Locate(null, App, new FakeTrust { Hashes = { [Bundled] = "deadbeef" } }, files, Folder);
        Assert.NotNull(found);
        Assert.False(found!.Usable);
        Assert.Contains("não vai executar", found.Verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_found_returns_null_and_falls_back_to_known_folders()
    {
        Assert.Null(PresentMonLocator.Locate(null, App, new FakeTrust(), new FakeFiles(), Folder));
        const string dl = @"C:\Users\ana\Downloads\PresentMon-2.6.0-x64.exe";
        var list = PresentMonLocator.Candidates(null, App, new FakeFiles(dl), Folder);
        Assert.Equal([(dl, PresentMonSource.Found)], list);
    }

    // ------------------------------------------------------------------ captura: recusa antes de executar

    [Fact]
    public void Live_starter_refuses_untrusted_exe_without_starting_a_process()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"nexus-falso-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "não é o PresentMon");
        try
        {
            var refused = new PresentMonVerdict(false, "00", null, "recusado no teste");
            var starter = new PresentMonLiveStarter(() => exe, () => false, verify: _ => refused);
            Assert.False(starter.Available);
            using var capture = starter.Start("cs2.exe", _ => { });
            Assert.True(capture.Ended);
            Assert.Contains("recusado no teste", capture.Log, StringComparison.Ordinal);
            Assert.Equal("recusado no teste", starter.LastRefusal);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Runner_refuses_untrusted_exe()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"nexus-falso-{Guid.NewGuid():N}.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var r = await PresentMonRunner.RunAsync(new(exe, "cs2.exe", 30, Path.Combine(Path.GetTempPath(), "nexus-x.csv"), false), CancellationToken.None,
                verify: p => PresentMonTrust.Verify(p, new HashOnlyTrustProbe()));
            Assert.False(r.Ok);
            Assert.Contains("não vai executar", r.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void Smoke_test_arguments_and_frame_counting()
    {
        var args = PresentMonSmokeTest.Arguments("dwm.exe", @"C:\t\x.csv", 5);
        Assert.Equal(["--process_name", "dwm.exe", "--output_file", @"C:\t\x.csv", "--timed", "5", "--terminate_after_timed", "--no_console_stats",
                      "--stop_existing_session", "--session_name", PresentMonSmokeTest.SessionName], args);
        Assert.DoesNotContain("--restart_as_admin", args);
        Assert.Throws<ArgumentException>(() => PresentMonSmokeTest.Arguments("dwm.exe\" --x", "o.csv", 5));
        Assert.Equal((true, 2), PresentMonSmokeTest.CountFrames(["Application,ProcessID,SwapChainAddress", "dwm.exe,1,0x1", "", "dwm.exe,1,0x1"]));
        Assert.Equal((false, 0), PresentMonSmokeTest.CountFrames([]));
    }

    // ------------------------------------------------------------------ grupo "Usuários do log de desempenho"

    private const string Me = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string Other = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    private sealed class FakeGroups : ILocalGroupBackend
    {
        public HashSet<(string Group, string User)> Members { get; } = [];
        public Dictionary<string, string> Markers { get; } = new(StringComparer.Ordinal);
        public int Adds, Removes;
        public bool IsMember(string groupSid, string userSid) => Members.Contains((groupSid, userSid));
        public void AddMember(string groupSid, string userSid) { Adds++; Members.Add((groupSid, userSid)); }
        public void RemoveMember(string groupSid, string userSid) { Removes++; Members.Remove((groupSid, userSid)); }
        public void SetMarker(string userSid, string by) => Markers[userSid] = by;
        public void ClearMarker(string userSid) => Markers.Remove(userSid);
        public bool HasMarker(string userSid) => Markers.ContainsKey(userSid);
    }

    private static CatalogEntry Entry() =>
        CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog")).Actions.Single(a => a.Definition.Id == "measure.perflog-group.join");

    private static ActionInstance Instance(string? userSid = null)
    {
        var a = Entry().TryInstantiate(new Dictionary<string, string>(), out var why) ?? throw new InvalidOperationException(why);
        return userSid is null ? a : UserProfileScope.Bind(a, userSid);
    }

    [Fact]
    public void Catalog_entry_is_admin_reversible_user_scoped_and_registered()
    {
        var e = Entry();
        Assert.Equal(PrivilegeLevel.Admin, e.Definition.Privileges);
        Assert.False(e.Definition.Irreversible);
        Assert.Equal(RebootRequirement.NextLogon, e.Definition.Reboot);
        Assert.Equal(PerfLogGroupHandler.Kind, e.Definition.HandlerKind);
        var a = Instance();
        Assert.Equal(PerfLogGroupHandler.PerformanceLogUsersSid, a.Target["group"]);
        Assert.True(UserProfileScope.IsUserScoped(a)); // o aplicador como serviço acrescenta o SID de quem pede (B10)
        Assert.Equal(StateSnapshot.Of(PerfLogGroupHandler.MembershipType, PerfLogGroupHandler.MemberValue), a.Desired);

        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        Assert.Equal(catalog.Actions.Count, catalog.Actions.Select(x => x.Definition.Id).Distinct(StringComparer.Ordinal).Count());
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), OtherAccount: false, Elevated: true);
        var kinds = MeasureHandlers.Create(env).Select(h => h.HandlerKind).ToHashSet(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
            foreach (var m in catalog.Actions.Where(x => x.Definition.Id.StartsWith("measure.", StringComparison.Ordinal)))
                Assert.Contains(m.Definition.HandlerKind, kinds);
        else Assert.Empty(kinds);
    }

    [Fact]
    public void Join_then_restore_removes_only_because_nexus_added()
    {
        var groups = new FakeGroups();
        var handler = new PerfLogGroupHandler(groups, () => Me, otherAccount: false);
        var a = Instance(Me);
        Assert.Equal(ApplicabilityKind.Applicable, handler.Assess(a, T.Ctx).Kind);

        var before = handler.Detect(a);
        Assert.False(before.Exists);
        handler.Apply(a);
        Assert.True(handler.Detect(a).SameValueAs(a.Desired));
        Assert.Equal("app", groups.Markers[Me]);

        handler.Restore(a, before);
        Assert.False(groups.IsMember(PerfLogGroupHandler.PerformanceLogUsersSid, Me));
        Assert.Empty(groups.Markers);
    }

    [Fact]
    public void Already_member_is_never_removed_and_gets_no_marker()
    {
        var groups = new FakeGroups();
        groups.Members.Add((PerfLogGroupHandler.PerformanceLogUsersSid, Me));
        var handler = new PerfLogGroupHandler(groups, () => Me, otherAccount: false);
        var a = Instance(Me);
        var before = handler.Detect(a);
        Assert.True(before.Exists);
        handler.Apply(a);
        Assert.Equal(0, groups.Adds);
        Assert.Empty(groups.Markers);
        handler.Restore(a, before);
        Assert.Equal(0, groups.Removes);
        Assert.True(groups.IsMember(PerfLogGroupHandler.PerformanceLogUsersSid, Me));
    }

    [Fact]
    public void Executor_end_to_end_apply_verify_and_revert()
    {
        var groups = new FakeGroups();
        var handlers = new HandlerRegistry().Add(new PerfLogGroupHandler(groups, () => Me, otherAccount: false));
        var journal = new InMemoryJournal();
        var exec = new ActionExecutor(handlers, journal);
        var run = journal.BeginRun("p", "1");
        var r = exec.Execute(run, new DesiredStateResolver(handlers).Resolve([Instance()], T.Ctx), T.Ctx).Single();
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.True(groups.IsMember(PerfLogGroupHandler.PerformanceLogUsersSid, Me));

        var reverted = exec.RevertRun(run).Single();
        Assert.True(reverted.Settled, reverted.Detail);
        Assert.False(groups.IsMember(PerfLogGroupHandler.PerformanceLogUsersSid, Me));
    }

    [Fact]
    public void Uses_requester_sid_and_refuses_unknown_account_or_other_group()
    {
        var groups = new FakeGroups();
        // Aplicador com outra conta (serviço/SYSTEM): sem userSid no pedido, recusa; com userSid, usa o de quem pediu.
        var service = new PerfLogGroupHandler(groups, () => "S-1-5-18", otherAccount: true);
        Assert.Equal(ApplicabilityKind.Blocked, service.Assess(Instance(), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => service.Apply(Instance()));
        service.Apply(Instance(Other));
        Assert.True(groups.IsMember(PerfLogGroupHandler.PerformanceLogUsersSid, Other));
        Assert.False(groups.IsMember(PerfLogGroupHandler.PerformanceLogUsersSid, "S-1-5-18"));

        // Mesma conta, processo sem SID de usuário válido (ex.: SYSTEM): recusa.
        Assert.Null(new PerfLogGroupHandler(groups, () => "S-1-5-18", otherAccount: false).ResolveUser(Instance()));

        // Grupo diferente (ex.: Administradores) nunca é aceito.
        var admins = Instance(Me) with { Target = new Dictionary<string, string>(Instance(Me).Target) { ["group"] = "S-1-5-32-544" } };
        var handler = new PerfLogGroupHandler(groups, () => Me, otherAccount: false);
        Assert.Equal(ApplicabilityKind.Blocked, handler.Assess(admins, T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => handler.Apply(admins));
    }

    [Fact]
    public void Restore_for_a_different_account_is_refused()
    {
        var groups = new FakeGroups();
        var handler = new PerfLogGroupHandler(groups, () => Me, otherAccount: false);
        var recorded = StateSnapshot.Absent(new Dictionary<string, string> { [PerfLogGroupHandler.UserDetailKey] = Other });
        Assert.Throws<InvalidOperationException>(() => handler.Restore(Instance(), recorded));
    }

    // ------------------------------------------------------------------ interface só lê

    [Fact]
    public void Desktop_never_calls_group_writes_directly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";
        var dll = Path.Combine(dir!.FullName, "src", "Nexus.Desktop", "bin", config, "net10.0-windows", "Nexus.Desktop.dll");
        Assert.True(File.Exists(dll), "compile a solução inteira antes dos testes");
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var refs = md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(mr => mr.Parent.Kind == HandleKind.TypeReference)
            .Select(mr => (md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name), md.GetString(mr.Name)))
            .ToHashSet();
        foreach (var forbidden in new[] { "AddMember", "RemoveMember", "SetMarker", "ClearMarker", "Apply", "Restore" })
        {
            Assert.DoesNotContain(("WindowsLocalGroupBackend", forbidden), refs);
            Assert.DoesNotContain(("ILocalGroupBackend", forbidden), refs);
            Assert.DoesNotContain(("PerfLogGroupHandler", forbidden), refs);
        }
        Assert.Contains(("WindowsLocalGroupBackend", "IsMemberOfPerfLogUsers"), refs); // controle: a leitura aparece
    }
}
