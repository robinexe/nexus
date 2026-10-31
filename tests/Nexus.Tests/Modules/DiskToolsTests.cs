using System.Text;
using Nexus.Broker;
using Nexus.Core;
using Nexus.DiskTools;
using Nexus.DiskTools.Analysis;
using Nexus.DiskTools.SecureDelete;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests.Modules;

// ============================================================================ treemap

[Trait("Category", "Unit")]
public sealed class TreemapTests
{
    private const double Eps = 1e-6;

    private static void AssertValidLayout(double[] sizes, TreemapRect bounds, IReadOnlyList<TreemapRect> rects)
    {
        Assert.Equal(sizes.Length, rects.Count);
        var positive = Enumerable.Range(0, sizes.Length).Where(i => sizes[i] > 0).ToList();
        var total = positive.Sum(i => sizes[i]);
        // Cobre a área inteira.
        Assert.Equal(bounds.Area, positive.Sum(i => rects[i].Area), 6);
        foreach (var i in positive)
        {
            var r = rects[i];
            // Dentro da área.
            Assert.True(r.X >= bounds.X - Eps && r.Y >= bounds.Y - Eps && r.Right <= bounds.Right + Eps && r.Bottom <= bounds.Bottom + Eps, $"fora: {r}");
            Assert.True(r.Width >= 0 && r.Height >= 0);
            // Proporcional ao tamanho.
            Assert.Equal(sizes[i] / total, r.Area / bounds.Area, 6);
        }
        // Sem sobreposição.
        for (var a = 0; a < positive.Count; a++)
            for (var b = a + 1; b < positive.Count; b++)
            {
                var r1 = rects[positive[a]];
                var r2 = rects[positive[b]];
                var w = Math.Min(r1.Right, r2.Right) - Math.Max(r1.X, r2.X);
                var h = Math.Min(r1.Bottom, r2.Bottom) - Math.Max(r1.Y, r2.Y);
                Assert.False(w > 1e-6 && h > 1e-6, $"sobreposição entre {r1} e {r2}");
            }
    }

    [Fact]
    public void Classic_example_covers_area_proportionally_without_overlap()
    {
        double[] sizes = [6, 6, 4, 3, 2, 2, 1]; // exemplo do artigo de Bruls et al.
        var bounds = new TreemapRect(0, 0, 6, 4);
        var rects = Treemap.Squarify(sizes, bounds);
        AssertValidLayout(sizes, bounds, rects);
    }

    [Theory]
    [InlineData(1, 800, 600)]
    [InlineData(7, 300, 900)]
    [InlineData(42, 1000, 100)]
    [InlineData(99, 512, 512)]
    public void Random_inputs_always_produce_a_valid_layout(int seed, double width, double height)
    {
        var rng = new Random(seed);
        var sizes = Enumerable.Range(0, 1 + rng.Next(60)).Select(_ => Math.Pow(rng.NextDouble(), 3) * 1e9).ToArray();
        var bounds = new TreemapRect(10, 20, width, height);
        AssertValidLayout(sizes, bounds, Treemap.Squarify(sizes, bounds));
    }

    [Fact]
    public void Result_follows_input_order_and_ignores_zero_and_invalid_sizes()
    {
        double[] sizes = [1, 0, 3, -5, double.NaN, 2];
        var rects = Treemap.Squarify(sizes, new TreemapRect(0, 0, 100, 100));
        Assert.Equal(0, rects[1].Area);
        Assert.Equal(0, rects[3].Area);
        Assert.Equal(0, rects[4].Area);
        Assert.True(rects[2].Area > rects[5].Area && rects[5].Area > rects[0].Area);
        AssertValidLayout([1, 0, 3, 0, 0, 2], new TreemapRect(0, 0, 100, 100), rects);
    }

    [Fact]
    public void Equal_sizes_in_a_square_become_squares()
    {
        var rects = Treemap.Squarify([1, 1, 1, 1], new TreemapRect(0, 0, 100, 100));
        Assert.All(rects, r => { Assert.Equal(50, r.Width, 6); Assert.Equal(50, r.Height, 6); });
    }

    [Fact]
    public void Empty_or_degenerate_bounds_are_safe()
    {
        Assert.Empty(Treemap.Squarify([], new TreemapRect(0, 0, 10, 10)));
        Assert.All(Treemap.Squarify([1, 2], new TreemapRect(0, 0, 0, 10)), r => Assert.Equal(0, r.Area));
    }
}

// ============================================================================ varredura

[Trait("Category", "Unit")]
public sealed class DiskScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nexus-disk-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("nexus-disk-out-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _root, _outside })
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Make(string path, int bytes, DateTime? lastWriteUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (lastWriteUtc is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    private void Tree()
    {
        Make(Path.Combine(_root, "videos", "filme.mp4"), 50_000);
        Make(Path.Combine(_root, "videos", "clipe.mkv"), 20_000);
        Make(Path.Combine(_root, "docs", "a.pdf"), 3_000);
        Make(Path.Combine(_root, "docs", "sub", "b.txt"), 1_000);
        Make(Path.Combine(_root, "solto.zip"), 7_000);
        // Algo grande FORA da raiz, alcançável só por links: não pode ser contado.
        Make(Path.Combine(_outside, "enorme.bin"), 500_000);
    }

    [Fact]
    public void Scan_builds_sorted_tree_top_files_and_categories()
    {
        Tree();
        var r = DiskScanner.Scan(_root);
        Assert.Equal(81_000, r.Root.Size);
        Assert.Equal(5, r.Root.FileCount);
        Assert.Equal(["videos", "docs"], r.Root.Children.Select(c => c.Name));
        Assert.Equal(70_000, r.Root.Children[0].Size);
        Assert.Equal(4_000, r.Root.Children[1].Size);
        Assert.Equal(7_000, r.Root.OwnFilesSize);
        Assert.Equal("filme.mp4", r.TopFiles[0].Name);
        Assert.True(r.TopFiles.Zip(r.TopFiles.Skip(1)).All(p => p.First.Size >= p.Second.Size));
        Assert.Equal(FileCategory.Video, r.Categories[0].Category);
        Assert.Equal(70_000, r.Categories[0].Bytes);
        Assert.Contains(r.Categories, c => c.Category == FileCategory.Archive && c.Bytes == 7_000);
        Assert.Equal(0, r.AccessDenied);
        Assert.Same(r.Root, r.Root.Children[1].Children[0].Lineage()[0]);
    }

    [Fact]
    public void Symlinks_and_junction_like_links_are_never_followed()
    {
        Tree();
        Directory.CreateSymbolicLink(Path.Combine(_root, "link-pasta"), _outside);
        File.CreateSymbolicLink(Path.Combine(_root, "docs", "link-arquivo.bin"), Path.Combine(_outside, "enorme.bin"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "docs", "laco"), _root); // laço: seguiria para sempre

        var r = DiskScanner.Scan(_root);
        Assert.Equal(81_000, r.Root.Size); // nada de fora entrou
        Assert.Equal(3, r.ReparsePointsSkipped);
        Assert.DoesNotContain(r.Root.SelfAndDescendants(), n => n.Name is "link-pasta" or "laco");
        Assert.DoesNotContain(r.TopFiles, f => f.Name == "link-arquivo.bin" || f.Name == "enorme.bin");
    }

    [Fact]
    public void Access_denied_is_counted_and_the_scan_continues()
    {
        Tree();
        var denied = Path.Combine(_root, "docs");
        var options = new DiskScanOptions
        {
            EnumerateOverride = p => string.Equals(p, denied, StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("negado")
                : new DirectoryInfo(p).EnumerateFileSystemInfos(),
        };
        var r = DiskScanner.Scan(_root, options);
        Assert.Equal(1, r.AccessDenied);
        Assert.Contains(denied, r.DeniedSamples);
        var docs = r.Root.Children.Single(c => c.Name == "docs");
        Assert.True(docs.Incomplete);
        Assert.True(r.Root.Incomplete);
        Assert.Equal(77_000, r.Root.Size); // o resto foi contado
    }

    [Fact]
    public void Old_bytes_and_tiles_add_up()
    {
        var old = DateTime.UtcNow.AddDays(-400);
        for (var i = 0; i < 10; i++) Make(Path.Combine(_root, $"f{i}.dat"), 1000 * (i + 1), i < 3 ? old : null);
        Make(Path.Combine(_root, "pasta", "x.dat"), 5);
        var r = DiskScanner.Scan(_root, new DiskScanOptions { FilesPerDirectory = 4 });
        Assert.Equal(1000 + 2000 + 3000, r.Root.OldBytes);
        var tiles = r.Root.Tiles();
        Assert.Equal(r.Root.Size, tiles.Sum(t => t.Size));
        Assert.Equal(4, tiles.Count(t => t.Kind == DiskTileKind.File));
        Assert.Single(tiles, t => t.Kind == DiskTileKind.OtherFiles);
        Assert.Single(tiles, t => t.Kind == DiskTileKind.Folder && t.Folder!.Name == "pasta");
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        Tree();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskScanner.ScanAsync(_root, cancel: cts.Token));
    }

    [Fact]
    public void Progress_is_reported()
    {
        Tree();
        var reports = new List<ScanProgress>();
        DiskScanner.Scan(_root, progress: new SyncProgress<ScanProgress>(reports.Add));
        Assert.NotEmpty(reports);
        Assert.Equal(5, reports[^1].Files);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public void Known_folders_are_explained_with_game_names()
    {
        Make(Path.Combine(_root, "Windows", "WinSxS", "x.dll"), 4096);
        Make(Path.Combine(_root, "Steam", "steamapps", "common", "cs2", "game.vpk"), 9000);
        File.WriteAllText(Path.Combine(_root, "Steam", "steamapps", "appmanifest_730.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"installdir\"\t\t\"cs2\"\n}\n");
        Make(Path.Combine(_root, "Users", "ana", "Downloads", "setup.exe"), 3000, DateTime.UtcNow.AddDays(-200));
        Make(Path.Combine(_root, "Users", "ana", "Downloads", "novo.zip"), 1000);

        var r = DiskScanner.Scan(_root);
        var insights = KnownFolders.Analyze(r, new KnownFolderContext { MinSize = 1 });
        var winsxs = Assert.Single(insights, i => i.Title.Contains("WinSxS", StringComparison.Ordinal));
        Assert.Equal(InsightKind.DoNotTouch, winsxs.Kind);
        Assert.Contains("Limpeza de Disco", winsxs.Advice, StringComparison.Ordinal);
        var game = Assert.Single(insights, i => i.Kind == InsightKind.Game);
        Assert.Equal("Counter-Strike 2 (Steam)", game.Title);
        var downloads = Assert.Single(insights, i => i.Title == "Downloads");
        Assert.Contains("90 dias", downloads.What, StringComparison.Ordinal);
        Assert.Equal(FileCategory.Game, r.TopFiles.Single(f => f.Name == "game.vpk").Category);
        Assert.Equal(FileCategory.Installer, r.TopFiles.Single(f => f.Name == "setup.exe").Category);
    }

    [Fact]
    public void Store_manifests_are_parsed()
    {
        var steam = GameLibraries.ParseSteamManifest("\"AppState\" { \"appid\" \"570\" \"name\" \"Dota 2\" \"installdir\" \"dota 2 beta\" }");
        Assert.Equal(("dota 2 beta", "Dota 2", "570"), steam);
        Assert.Null(GameLibraries.ParseSteamManifest("lixo"));
        var epic = GameLibraries.ParseEpicManifest("{\"DisplayName\":\"Fortnite\",\"InstallLocation\":\"C:\\\\Program Files\\\\Epic Games\\\\Fortnite\"}");
        Assert.Equal((@"C:\Program Files\Epic Games\Fortnite", "Fortnite"), epic);
        Assert.Null(GameLibraries.ParseEpicManifest("{nao é json"));
    }

    [Fact]
    public void Sizes_are_formatted_like_the_explorer()
    {
        Assert.Equal("512 bytes", Format.Bytes(512));
        Assert.Equal("1,50 KB", Format.Bytes(1536));
        Assert.Equal("10,0 GB", Format.Bytes(10L * 1024 * 1024 * 1024));
    }
}

// ============================================================================ exclusão segura: alvos

[Trait("Category", "Unit")]
public sealed class SecureDeleteTargetTests : IDisposable
{
    private static readonly SecureDeletePolicy Policy = new(@"C:\Windows", [@"C:\Users\ana\AppData\Local\NEXUS"]);
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-sdel-").FullName;

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(@"D:\Videos\clipe.mp4")]
    [InlineData(@"C:\Users\ana\Documents\contrato antigo.pdf")]
    [InlineData(@"C:\Users\ana\Downloads\setup (1).exe")]
    [InlineData(@"E:\backup\senhas.kdbx")]
    [InlineData(@"D:\arquivo.zip")]
    public void Allowed_user_files(string path) => Assert.Null(Policy.Check(path));

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"D:\Windows\System32\x.dll")]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\ProgramData\Microsoft\x.dat")]
    [InlineData(@"C:\PROGRA~1\App\app.exe")]
    [InlineData(@"C:\Users\ana\NTUSER.DAT")]
    [InlineData(@"C:\Users\ana\qualquer.txt")]
    [InlineData(@"C:\Users\ana\Docs\ntuser.dat.LOG1")]
    [InlineData(@"C:\Users\Default\NTUSER.DAT")]
    [InlineData(@"C:\Users\x.txt")]
    [InlineData(@"C:\Users\ana\AppData\Roaming\Microsoft\Protect\S-1-5-21\chave")]
    [InlineData(@"C:\Users\ana\AppData\Local\Microsoft\Windows\UsrClass.dat")]
    [InlineData(@"C:\Users\ana\AppData\Local\NEXUS\journal.db")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"D:\hiberfil.sys")]
    [InlineData(@"C:\qualquer.txt")]
    [InlineData(@"C:\$Recycle.Bin\S-1\x")]
    [InlineData(@"C:\System Volume Information\x")]
    [InlineData(@"C:\Users\ana\Documents\..\..\..\Windows\win.ini")]
    [InlineData(@"C:\Users\ana\Documents\.\x.txt")]
    [InlineData(@"C:\Users\ana\Documents\x.txt.")]
    [InlineData(@"C:\Users\ana\Documents\x.txt ")]
    [InlineData(@"C:\Users\ana\Documents\x.txt:fluxo")]
    [InlineData(@"C:\Users\ana\Documents\CON.txt")]
    [InlineData(@"C:\Users\ana\Documents\*.txt")]
    [InlineData(@"\\servidor\pasta\x.txt")]
    [InlineData(@"\\?\C:\Users\ana\x.txt")]
    [InlineData(@"\\.\C:")]
    [InlineData(@"Documents\x.txt")]
    [InlineData(@"C:")]
    [InlineData(@"C:\")]
    [InlineData(@"C:/Users/ana/Documents/x.txt")]
    [InlineData("")]
    public void Forbidden_targets_are_refused(string path) => Assert.NotNull(Policy.Check(path));

    [Fact]
    public void Catalog_rule_is_exactly_the_policy_pattern_and_the_handler_is_registered()
    {
        var catalog = CatalogLoader.LoadDirectory(CatalogDirectory());
        var entry = Assert.Single(catalog.Actions, a => a.Definition.Id == "disk.secure-delete");
        Assert.True(entry.Definition.Irreversible);
        Assert.Equal(SecureDeletePolicy.PathPattern, entry.UserParameters["filePath"]);
        Assert.Equal(PersistenceKind.Persistent, entry.Definition.Persistence);
        Assert.Equal(PrivilegeLevel.User, entry.Definition.Privileges);

        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.GetTempPath(), OtherAccount: false, Elevated: false);
        var kinds = DiskToolsHandlers.Create(env).Select(h => h.HandlerKind).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct(StringComparer.Ordinal).Count());
        var disk = catalog.Actions.Where(a => a.Definition.Id.StartsWith("disk.", StringComparison.Ordinal)).ToList();
        Assert.All(disk, a => Assert.Contains(a.Definition.HandlerKind, kinds));
        Assert.Equal(catalog.Actions.Count, catalog.Actions.Select(a => a.Definition.Key).Distinct(StringComparer.Ordinal).Count());

        // A regra do catálogo (validada pelo broker) já barra o formato; a política faz o resto.
        Assert.NotNull(entry.ValidateUserArguments(new Dictionary<string, string> { ["filePath"] = @"C:\Users\ana\Docs\x.txt" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["filePath"] = @"C:\Users\ana\..\x.txt" }, out _));
        Assert.Null(entry.ValidateUserArguments(new Dictionary<string, string> { ["filePath"] = @"\\srv\x\y.txt" }, out _));
    }

    internal static string CatalogDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "catalog");
    }

    // ------------------------------------------------------------------ handler no disco (arquivos reais, pasta temporária)

    private static ActionDefinition Definition(bool irreversible = true) =>
        new("disk.secure-delete", 1, "Exclusão segura", Classification.Conditional, DiskSecureDeleteHandler.Kind, "m", [], "r",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None) { Irreversible = irreversible };

    private static ActionInstance Action(string path, bool irreversible = true) =>
        new(Definition(irreversible), new Dictionary<string, string> { ["path"] = path }, StateSnapshot.Of(DiskSecureDeleteHandler.StateType, DiskSecureDeleteHandler.AbsentValue));

    private DiskSecureDeleteHandler Handler(bool otherAccount = false, bool elevated = false) =>
        new(new HandlerEnvironment(new InMemoryRegistryBackend(), _dir, otherAccount, elevated), new SecureDeleteFileSystem(),
            p => p.StartsWith(_dir + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : "fora da pasta de teste");

    private string NewFile(string name, string content = "segredo: 1234")
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Erases_a_plain_file_and_leaves_nothing_behind()
    {
        var path = NewFile("a.txt");
        var h = Handler();
        var action = Action(path);
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(action, T.Ctx).Kind);
        Assert.StartsWith("present;size=13;", h.Detect(action).Value, StringComparison.Ordinal);
        h.Apply(action);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir)); // nem o nome aleatório ficou
        Assert.True(h.Detect(action).SameValueAs(action.Desired));
    }

    [Fact]
    public void Overwrite_replaces_every_byte_before_deleting()
    {
        var original = Enumerable.Repeat((byte)'A', 3 * 1024 * 1024 + 17).ToArray(); // mais de um bloco de 1 MiB
        var path = Path.Combine(_dir, "grande.bin");
        File.WriteAllBytes(path, original);
        Assert.Equal(original.Length, SecureDeleteFileSystem.OverwriteInPlace(path));
        var after = File.ReadAllBytes(path);
        Assert.Equal(original.Length, after.Length);
        // Aleatório: a chance de sobrar uma sequência longa de 'A' é desprezível.
        Assert.True(after.Count(b => b == (byte)'A') < original.Length / 100);
        var temp = SecureDeleteFileSystem.RenameTruncateDelete(path);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(temp));
        Assert.Matches("^[0-9A-F]{16}$", Path.GetFileName(temp));
    }

    [Fact]
    public void Refuses_outside_the_policy_without_touching_the_file()
    {
        var path = NewFile("a.txt");
        var h = new DiskSecureDeleteHandler(new HandlerEnvironment(new InMemoryRegistryBackend(), _dir, false, false), new SecureDeleteFileSystem(), _ => "proibido");
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action(path), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(Action(path)));
        Assert.Equal("segredo: 1234", File.ReadAllText(path));
    }

    [Fact]
    public void Refuses_when_running_as_another_account_elevated_or_not_irreversible()
    {
        var path = NewFile("a.txt");
        Assert.Equal(ApplicabilityKind.Blocked, Handler(otherAccount: true).Assess(Action(path), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, Handler(elevated: true).Assess(Action(path), T.Ctx).Kind);
        Assert.Equal(ApplicabilityKind.Blocked, Handler().Assess(Action(path, irreversible: false), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => Handler(elevated: true).Apply(Action(path)));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Refuses_directories_symlinks_and_paths_through_links()
    {
        var target = NewFile(Path.Combine("real", "alvo.txt"));
        var h = Handler();
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action(Path.Combine(_dir, "real")), T.Ctx).Kind); // pasta

        var fileLink = Path.Combine(_dir, "link.txt");
        File.CreateSymbolicLink(fileLink, target);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action(fileLink), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Apply(Action(fileLink)));

        var dirLink = Path.Combine(_dir, "atalho");
        Directory.CreateSymbolicLink(dirLink, Path.Combine(_dir, "real"));
        var through = Path.Combine(dirLink, "alvo.txt");
        var refusal = h.Assess(Action(through), T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, refusal.Kind);
        Assert.Contains("link ou junção", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal("segredo: 1234", File.ReadAllText(target)); // o alvo real ficou intacto
    }

    [Fact]
    public void Refuses_read_only_and_files_in_use()
    {
        var h = Handler();
        var ro = NewFile("ro.txt");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        Assert.Equal(ApplicabilityKind.Blocked, h.Assess(Action(ro), T.Ctx).Kind);

        var busy = NewFile("aberto.txt");
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = h.Assess(Action(busy), T.Ctx);
            Assert.Equal(ApplicabilityKind.Blocked, r.Kind);
            Assert.Contains("aberto", r.Reason, StringComparison.Ordinal);
        }
        Assert.Equal(ApplicabilityKind.Applicable, h.Assess(Action(busy), T.Ctx).Kind);
    }

    [Fact]
    public void Missing_file_is_not_applicable_and_restore_is_always_refused()
    {
        var h = Handler();
        var missing = Path.Combine(_dir, "nao-existe.txt");
        Assert.Equal(ApplicabilityKind.NotApplicable, h.Assess(Action(missing), T.Ctx).Kind);
        Assert.Throws<InvalidOperationException>(() => h.Restore(Action(missing), StateSnapshot.Of("FileState", "present;size=1;mtime=1")));
        Assert.NotNull(h.CheckRestore(Action(missing), StateSnapshot.Of("FileState", "present;size=1;mtime=1")));
    }
}

// ============================================================================ contrato de ações irreversíveis (núcleo + broker)

/// <summary>Handler falso de "apagar": estado em memória, conta chamadas a Apply e Restore.</summary>
internal sealed class FakeEraseHandler : IActionHandler
{
    public const string KindName = "FakeErase";
    public bool Exists { get; set; } = true;
    public int ApplyCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    /// <summary>Simula falha no meio: o conteúdo muda (sobrescrito), mas o arquivo continua lá, e Apply lança.</summary>
    public bool FailMidway { get; set; }
    private string _content = "original";

    public string HandlerKind => KindName;
    public Applicability Assess(ActionInstance action, SystemContext context) => Applicability.Ok;
    public StateSnapshot Detect(ActionInstance action) =>
        Exists ? StateSnapshot.Of("FileState", "present;" + _content) : StateSnapshot.Of("FileState", "absent");

    public void Apply(ActionInstance action)
    {
        ApplyCalls++;
        _content = "sobrescrito";
        if (FailMidway) throw new IOException("disco removido");
        Exists = false;
    }

    public void Restore(ActionInstance action, StateSnapshot original) => RestoreCalls++;
}

[Trait("Category", "Unit")]
public sealed class IrreversibleContractTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-irrev-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string CatalogJson(bool irreversible, string persistence = "Persistent", string irreversibleRaw = "") =>
        "{ \"catalogVersion\": \"irr-1\", \"schema\": \"nexus.catalog/1\", \"actions\": [ " +
        "{ \"id\": \"t.erase\", \"version\": 1, \"title\": \"Apagar\", \"classification\": \"Conditional\", \"handlerKind\": \"FakeErase\", " +
        "\"target\": { \"name\": \"alvo\" }, \"desiredValue\": \"absent\", \"desiredType\": \"FileState\", " +
        (irreversibleRaw.Length > 0 ? $"\"irreversible\": {irreversibleRaw}, " : irreversible ? "\"irreversible\": true, " : "") +
        $"\"privileges\": \"User\", \"persistence\": \"{persistence}\", \"reboot\": \"None\" }} ] }}";

    private static ActionInstance Instance(bool irreversible = true) =>
        CatalogLoader.Parse(CatalogJson(irreversible)).Actions[0].TryInstantiate(new Dictionary<string, string>(), out _)!;

    // ------------------------------------------------------------------ catálogo

    [Fact]
    public void Catalog_flag_maps_to_the_definition_and_is_validated()
    {
        Assert.True(CatalogLoader.Parse(CatalogJson(true)).Actions[0].Definition.Irreversible);
        Assert.False(CatalogLoader.Parse(CatalogJson(false)).Actions[0].Definition.Irreversible);
        Assert.False(CatalogLoader.Parse(CatalogJson(false, irreversibleRaw: "false")).Actions[0].Definition.Irreversible);
        Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(CatalogJson(true, "Session")));
        Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(CatalogJson(false, irreversibleRaw: "\"sim\"")));
        // Nenhuma ação do catálogo principal mudou de comportamento.
        Assert.DoesNotContain(CatalogLoader.Load(Path.Combine(SecureDeleteTargetTests.CatalogDirectory(), CatalogLoader.MainFile)).Actions,
            a => a.Definition.Irreversible);
    }

    // ------------------------------------------------------------------ executor e recuperação

    private static (FakeEraseHandler Handler, InMemoryJournal Journal, ActionExecutor Exec, Plan Plan) Engine(bool irreversible = true)
    {
        var handler = new FakeEraseHandler();
        var handlers = new HandlerRegistry().Add(handler);
        var journal = new InMemoryJournal();
        var plan = new DesiredStateResolver(handlers).Resolve([Instance(irreversible)], T.Ctx);
        return (handler, journal, new ActionExecutor(handlers, journal), plan);
    }

    [Fact]
    public void Executor_refuses_without_explicit_confirmation_and_writes_nothing()
    {
        var (h, journal, exec, plan) = Engine();
        var run = exec.BeginRun("t", "c");
        var r = Assert.Single(exec.Execute(run, plan, T.Ctx));
        Assert.Equal(ExecutionOutcome.Blocked, r.Outcome);
        Assert.Equal(ActionExecutor.IrreversibleNotConfirmed, r.Detail);
        Assert.Equal(0, h.ApplyCalls);
        Assert.True(h.Exists);
        Assert.Empty(journal.Entries(run)); // nem chegou ao diário

        // Confirmar OUTRA chave não vale.
        var r2 = Assert.Single(exec.Execute(run, plan, T.Ctx, ["outra.acao@1"]));
        Assert.Equal(ExecutionOutcome.Blocked, r2.Outcome);
        Assert.Equal(0, h.ApplyCalls);
    }

    [Fact]
    public void Executor_applies_with_confirmation_and_restore_is_refused()
    {
        var (h, journal, exec, plan) = Engine();
        var run = exec.BeginRun("t", "c");
        var r = Assert.Single(exec.Execute(run, plan, T.Ctx, ["t.erase@1"]));
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        Assert.False(h.Exists);
        var entry = journal.Get(r.EntryId!.Value);
        Assert.Equal(JournalState.Verified, entry.State);
        Assert.True(entry.Action.Definition.Irreversible); // o diário guarda o contrato

        var revert = exec.Revert(entry.Id);
        Assert.Equal(RevertOutcome.Irreversible, revert.Outcome);
        Assert.Contains("não há como desfazer", revert.Detail, StringComparison.Ordinal);
        Assert.Equal(0, h.RestoreCalls);
        Assert.Equal(JournalState.Verified, journal.Get(entry.Id).State);
        Assert.All(exec.RevertRun(run), x => Assert.Equal(RevertOutcome.Irreversible, x.Outcome));
        Assert.Empty(RecoveryService.FindInterrupted(journal)); // nada pendente
    }

    [Fact]
    public void Reversible_actions_are_unchanged_by_the_contract()
    {
        var (h, _, exec, plan) = Engine(irreversible: false);
        var run = exec.BeginRun("t", "c");
        var r = Assert.Single(exec.Execute(run, plan, T.Ctx)); // sem confirmação: segue como sempre
        Assert.Equal(ExecutionOutcome.Verified, r.Outcome);
        exec.Revert(r.EntryId!.Value);
        Assert.Equal(1, h.RestoreCalls);
    }

    [Fact]
    public void Failure_midway_closes_the_entry_honestly_without_restore()
    {
        var (h, journal, exec, plan) = Engine();
        h.FailMidway = true;
        var run = exec.BeginRun("t", "c");
        var r = Assert.Single(exec.Execute(run, plan, T.Ctx, ["t.erase@1"]));
        Assert.Equal(ExecutionOutcome.Failed, r.Outcome);
        Assert.Contains("irreversível", r.Detail, StringComparison.Ordinal);
        Assert.Equal(0, h.RestoreCalls);
        Assert.Equal(JournalState.Accepted, journal.Get(r.EntryId!.Value).State);
        Assert.Empty(RecoveryService.FindInterrupted(journal));
    }

    [Fact]
    public void Recovery_never_tries_to_restore_and_marks_as_accepted()
    {
        var (h, journal, exec, plan) = Engine();
        exec.FaultInjector = (point, _) => { if (point == "after-apply") throw new InvalidOperationException("queda de energia"); };
        var run = exec.BeginRun("t", "c");
        Assert.Throws<InvalidOperationException>(() => exec.Execute(run, plan, T.Ctx, ["t.erase@1"]));
        var entry = Assert.Single(journal.Entries(run));
        Assert.Equal(JournalState.Applying, entry.State);
        Assert.Single(RecoveryService.FindInterrupted(journal));

        var recovering = new ActionExecutor(new HandlerRegistry().Add(h), journal);
        var report = new RecoveryService(journal, recovering).Recover(forceConflicts: true);
        var result = Assert.Single(report.Results);
        Assert.Equal(RevertOutcome.Irreversible, result.Outcome);
        Assert.False(report.FullyRestored); // honesto: nada foi restaurado
        Assert.Equal(0, h.RestoreCalls);
        var closed = journal.Get(entry.Id);
        Assert.Equal(JournalState.Accepted, closed.State);
        Assert.Contains("não há como desfazer", closed.Note, StringComparison.Ordinal);
        Assert.Empty(RecoveryService.FindInterrupted(journal)); // não volta a aparecer
    }

    [Fact]
    public void Interrupted_before_apply_is_closed_as_nothing_done()
    {
        var (h, journal, exec, plan) = Engine();
        exec.FaultInjector = (point, _) => { if (point == "after-backup") throw new InvalidOperationException("queda"); };
        var run = exec.BeginRun("t", "c");
        Assert.Throws<InvalidOperationException>(() => exec.Execute(run, plan, T.Ctx, ["t.erase@1"]));
        var report = new RecoveryService(journal, new ActionExecutor(new HandlerRegistry().Add(h), journal)).Recover();
        Assert.Equal(RevertOutcome.NothingToRevert, Assert.Single(report.Results).Outcome);
        Assert.Equal(JournalState.Reverted, Assert.Single(journal.Entries(run)).State);
        Assert.Equal(0, h.ApplyCalls);
        Assert.Equal(0, h.RestoreCalls);
    }

    [Fact]
    public void Sqlite_journal_keeps_the_flag_and_old_entries_stay_reversible()
    {
        var path = Path.Combine(_dir, "j.db");
        long id, plain;
        Guid run;
        using (var j = new SqliteJournal(path))
        {
            run = j.BeginRun("t", "c");
            id = j.AddPlanned(run, 0, Instance(irreversible: true));
            plain = j.AddPlanned(run, 1, Instance(irreversible: false));
        }
        using var ro = SqliteJournal.OpenReadOnly(path);
        Assert.True(ro.Get(id).Action.Definition.Irreversible);
        Assert.False(ro.Get(plain).Action.Definition.Irreversible);
        // JSON gravado por versões anteriores (sem o campo) desserializa como reversível.
        var json = System.Text.Json.JsonSerializer.Serialize(Instance(irreversible: false), SqliteJournal.Json).Replace(",\"Irreversible\":false", "", StringComparison.Ordinal);
        Assert.DoesNotContain("Irreversible", json, StringComparison.Ordinal);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<ActionInstance>(json, SqliteJournal.Json)!.Definition.Irreversible);
    }

    // ------------------------------------------------------------------ broker

    private sealed class OpenGate : IExecutionGate
    {
        public GateDecision Evaluate() => new(true, "teste: liberado");
    }

    private (BrokerService Service, FakeEraseHandler Handler, InMemoryJournal Journal) Broker()
    {
        var h = new FakeEraseHandler();
        var journal = new InMemoryJournal();
        var svc = new BrokerService(CatalogLoader.Parse(CatalogJson(true)), new HandlerRegistry().Add(h), journal, new FileRunCoordinator(_dir),
            new OpenGate(), () => T.Ctx, () => new Dictionary<string, string>());
        return (svc, h, journal);
    }

    private static BrokerRequest Req(BrokerOperation op, bool? confirm = null, Guid? run = null) =>
        new(1, Guid.NewGuid(), op, op is BrokerOperation.RevertRun or BrokerOperation.Recover ? null : [new ActionRequest("t.erase@1", ConfirmIrreversible: confirm)], run);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Broker_refuses_execute_without_confirmation(bool? confirm)
    {
        var (svc, h, journal) = Broker();
        var r = svc.Handle(Req(BrokerOperation.Execute, confirm));
        Assert.False(r.Ok);
        Assert.Equal("irreversible-unconfirmed", r.Error!.Code);
        Assert.Equal(0, h.ApplyCalls);
        Assert.Empty(journal.Runs()); // nada travado, nada gravado
    }

    [Fact]
    public void Broker_executes_with_confirmation_and_refuses_to_undo()
    {
        var (svc, h, journal) = Broker();
        var plan = svc.Handle(Req(BrokerOperation.Plan)); // simular não exige confirmação, mas avisa
        Assert.True(plan.Ok);
        Assert.Contains("IRREVERSÍVEL", plan.Message, StringComparison.Ordinal);

        var exec = svc.Handle(Req(BrokerOperation.Execute, confirm: true));
        Assert.True(exec.Ok, exec.Error?.Detail);
        Assert.Equal("Verified", Assert.Single(exec.Results!).Outcome);
        Assert.False(h.Exists);

        var undo = svc.Handle(Req(BrokerOperation.RevertRun, run: exec.RunId));
        Assert.True(undo.Ok);
        Assert.False(undo.FullyRestored);
        var item = Assert.Single(undo.Results!);
        Assert.Equal("Irreversible", item.Outcome);
        Assert.Contains("não há como desfazer", item.Detail, StringComparison.Ordinal);
        Assert.Equal(0, h.RestoreCalls);

        var recover = svc.Handle(Req(BrokerOperation.Recover));
        Assert.True(recover.Ok);
        Assert.Empty(recover.Results!); // nada pendente, nada tentado
        Assert.Equal(0, h.RestoreCalls);
        Assert.Equal(JournalState.Verified, journal.Entries(exec.RunId!.Value).Single().State);
    }

    [Fact]
    public void Confirmation_field_is_omitted_from_the_wire_when_not_set()
    {
        var plain = Encoding.UTF8.GetString(BrokerWire.Encode(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Plan, [new ActionRequest("x@1")])));
        Assert.DoesNotContain("confirmIrreversible", plain, StringComparison.Ordinal);
        var body = BrokerWire.Encode(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest("x@1", ConfirmIrreversible: true)]));
        Assert.Contains("\"confirmIrreversible\":true", Encoding.UTF8.GetString(body), StringComparison.Ordinal);
        var back = BrokerWire.Decode<BrokerRequest>(body[4..]);
        Assert.True(back.Actions![0].ConfirmIrreversible);
    }
}
