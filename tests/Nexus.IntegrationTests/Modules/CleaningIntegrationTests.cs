using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.IntegrationTests.Modules;

/// <summary>
/// Limpeza real (pasta %TEMP% da conta da VM) pelo broker completo: aplicar, conferir, arquivo em uso pulado, desfazer.
/// Só numa VM liberada: o portão é avaliado antes de qualquer arquivo ser criado.
/// </summary>
[Trait("Category", "VmIntegration")]
public sealed class CleaningIntegrationTests : IDisposable
{
    private readonly string _dir;

    public CleaningIntegrationTests()
    {
        Vm.Require();
        _dir = Directory.CreateTempSubdirectory("nexus-vm-limpeza-").FullName;
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void Temp_cleaning_moves_old_files_skips_open_ones_and_reverts()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var old = Path.Combine(temp, $"nexus-teste-{Guid.NewGuid():N}.tmp");
        var busy = Path.Combine(temp, $"nexus-aberto-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(old, "velho");
        File.WriteAllText(busy, "aberto");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(busy, DateTime.UtcNow.AddDays(-3));

        var registry = new WindowsRegistryBackend();
        var handlers = new HandlerRegistry();
        foreach (var h in CleaningHandlers.Create(new HandlerEnvironment(registry, _dir, false, Vm.Elevated))) handlers.Add(h);
        var journalPath = Path.Combine(_dir, "journal.db");
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        using var journal = new SqliteJournal(journalPath);
        using var broker = new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(journalPath), Vm.Gate, () => ctx,
            () => new Dictionary<string, string>());

        var batch = CleaningBatchId.New(DateTime.UtcNow);
        BrokerResponse exec;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read)) // aberto por "outro programa"
        {
            exec = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Execute,
                [new ActionRequest("cleaning.temp-user@1", null, new Dictionary<string, string> { ["batch"] = batch })]));
        }
        Assert.True(exec.Ok, exec.Error?.Detail);
        Assert.Equal("Verified", Assert.Single(exec.Results!).Outcome);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(busy));
        var manifest = QuarantineStore.ReadManifest(QuarantineStore.RuleDirectory(_dir, batch, "temp-user"))!;
        Assert.Contains(manifest.Entries, e => e.Path.Equals(old, StringComparison.OrdinalIgnoreCase) && e.State == QuarantineEntryState.Moved);
        Assert.Contains(manifest.Entries, e => e.Path.Equals(busy, StringComparison.OrdinalIgnoreCase) && e.State == QuarantineEntryState.SkippedInUse);

        var revert = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: exec.RunId));
        Assert.True(revert.Ok && revert.FullyRestored == true, string.Join("; ", revert.Results?.Select(r => r.Detail) ?? []));
        Assert.Equal("velho", File.ReadAllText(old));
        File.Delete(old);
        File.Delete(busy);
    }

    private BrokerService Broker(SqliteJournal journal, string journalPath, bool elevatedFlag)
    {
        var registry = new WindowsRegistryBackend();
        var handlers = new HandlerRegistry();
        foreach (var h in CleaningHandlers.Create(new HandlerEnvironment(registry, _dir, false, elevatedFlag))) handlers.Add(h);
        var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
        var facts = Vm.Facts;
        var ctx = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "?", facts.GpuVendorPrimary, Vm.Elevated);
        return new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(journalPath), Vm.Gate, () => ctx, () => new Dictionary<string, string>());
    }

    private static BrokerRequest Exec(string actionKey, Dictionary<string, string> args, bool? confirm = null) =>
        new(1, Guid.NewGuid(), BrokerOperation.Execute, [new ActionRequest(actionKey, null, args, confirm)]);

    [Fact]
    public void Purge_needs_confirmation_deletes_for_real_never_through_a_junction_and_blocks_undo()
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var old = Path.Combine(temp, $"nexus-esvaziar-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(old, "velho");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        var journalPath = Path.Combine(_dir, "journal-purge.db");
        using var journal = new SqliteJournal(journalPath);
        using var broker = Broker(journal, journalPath, Vm.Elevated);

        var batch = CleaningBatchId.New(DateTime.UtcNow);
        var clean = broker.Handle(Exec("cleaning.temp-user@1", new() { ["batch"] = batch }));
        Assert.True(clean.Ok, clean.Error?.Detail);
        Assert.False(File.Exists(old));

        // Junção real dentro da quarentena apontando para fora: o esvaziamento remove só a junção.
        var outside = Directory.CreateTempSubdirectory("nexus-vm-fora-").FullName;
        var precious = Path.Combine(outside, "precioso.txt");
        File.WriteAllText(precious, "não apague");
        var junction = Path.Combine(QuarantineStore.RuleDirectory(_dir, batch, "temp-user"), QuarantineStore.FilesDirectory, "juncao");
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, outside }, UseShellExecute = false, CreateNoWindow = true })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(junction));

        var purgeArgs = new Dictionary<string, string> { ["batch"] = batch };
        var refused = broker.Handle(Exec("cleaning.quarantine.purge@1", purgeArgs));
        Assert.False(refused.Ok);
        Assert.Equal("irreversible-unconfirmed", refused.Error!.Code);
        Assert.True(QuarantineStore.MeasureOnDisk(_dir, batch).Files > 0);

        var purge = broker.Handle(Exec("cleaning.quarantine.purge@1", purgeArgs, confirm: true));
        Assert.True(purge.Ok, purge.Error?.Detail);
        var purged = Assert.Single(purge.Results!);
        // O %TEMP% real do runner tem arquivos antigos de outros processos que a regra também leva; alguns recusam exclusão
        // (ACL do runner). Aí o resultado é falha parcial com motivo claro — o que importa aqui: o nosso arquivo saiu de vez,
        // a junção foi removida sem tocar o destino, e nada foi "desfeito" por engano.
        if (purged.Outcome != "Verified")
            Assert.Contains("não puderam ser apagados", purged.Detail ?? "", StringComparison.Ordinal);
        Assert.False(Directory.EnumerateFiles(QuarantineStore.BatchDirectory(_dir, batch), "*", SearchOption.AllDirectories)
            .Any(f => { try { return File.ReadAllText(f) == "velho"; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }),
            "o arquivo do teste continua na quarentena");
        Assert.True(File.Exists(precious));
        Assert.False(Directory.Exists(junction));

        var undoPurge = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: purge.RunId));
        // Esvaziado por inteiro → "Irreversible"; esvaziamento parcial (arquivos do runner) fica aceito → "NothingToRevert". Nunca restaura.
        Assert.Contains(Assert.Single(undoPurge.Results!).Outcome, new[] { "Irreversible", "NothingToRevert" });
        var undoClean = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: clean.RunId));
        Assert.NotEqual(true, undoClean.FullyRestored);
        Assert.False(File.Exists(old));
        Directory.Delete(outside, recursive: true);
    }

    [Fact]
    public void Recycle_bin_is_emptied_only_with_confirmation_and_verified_by_size()
    {
        var file = Path.Combine(Path.GetTempPath(), $"nexus-lixeira-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, new string('x', 4096));
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        var drive = char.ToUpperInvariant(Path.GetPathRoot(file)![0]).ToString();
        var before = RecycleBinInfo.TryQuery(drive + @":\");
        Assert.True(before is { Files: > 0 }, "o arquivo de teste não foi para a Lixeira");

        var journalPath = Path.Combine(_dir, "journal-lixeira.db");
        using var journal = new SqliteJournal(journalPath);
        using var broker = Broker(journal, journalPath, Vm.Elevated);
        var args = new Dictionary<string, string> { ["drive"] = drive };
        Assert.Equal("irreversible-unconfirmed", broker.Handle(Exec("cleaning.recycle-bin.empty@1", args)).Error!.Code);
        Assert.True(RecycleBinInfo.TryQuery(drive + @":\") is { Files: > 0 });

        var r = broker.Handle(Exec("cleaning.recycle-bin.empty@1", args, confirm: true));
        Assert.True(r.Ok, r.Error?.Detail);
        Assert.Equal("Verified", Assert.Single(r.Results!).Outcome);
        Assert.Equal<(long, long)?>((0L, 0L), RecycleBinInfo.TryQuery(drive + @":\"));
    }

    [Fact]
    public void User_rules_from_appdata_work_only_in_the_non_elevated_applier()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rulesDir = Path.Combine(appData, "NEXUS", "rules");
        Directory.CreateDirectory(rulesDir);
        var ruleFile = Path.Combine(rulesDir, $"vm-teste-{Guid.NewGuid():N}.json");
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusVmTeste", "Cache");
        Directory.CreateDirectory(cacheDir);
        var victim = Path.Combine(cacheDir, "x.bin");
        File.WriteAllText(victim, "cache");
        File.WriteAllText(ruleFile, """
            {"rules":[{"id":"vm-teste-cache","title":"Teste VM","category":"Apps","scope":"User","mode":"Quarantine",
              "roots":["%LocalAppData%\\NexusVmTeste\\Cache"],"patterns":["*"],"recursive":true,
              "explanation":"teste","expectedGain":"nenhum","evidence":"teste de integração"}]}
            """);
        try
        {
            var args = new Dictionary<string, string> { ["rule"] = "vm-teste-cache", ["batch"] = CleaningBatchId.New(DateTime.UtcNow) };
            var elevatedPath = Path.Combine(_dir, "journal-ur-elev.db");
            using (var j = new SqliteJournal(elevatedPath))
            using (var elevated = Broker(j, elevatedPath, elevatedFlag: true))
            {
                var r = elevated.Handle(Exec("cleaning.user-rule@1", args));
                Assert.True(!r.Ok || r.Results!.All(x => x.Outcome != "Verified"), "regra do usuário não pode valer no aplicador elevado");
                Assert.True(File.Exists(victim));
            }

            var userPath = Path.Combine(_dir, "journal-ur.db");
            using var journal = new SqliteJournal(userPath);
            using var broker = Broker(journal, userPath, elevatedFlag: false);
            var ok = broker.Handle(Exec("cleaning.user-rule@1", args));
            Assert.True(ok.Ok, ok.Error?.Detail);
            Assert.Equal("Verified", Assert.Single(ok.Results!).Outcome);
            Assert.False(File.Exists(victim));

            File.Delete(ruleFile); // o desfazer usa a cópia da regra guardada no manifesto
            var undo = broker.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: ok.RunId));
            Assert.True(undo.FullyRestored == true, string.Join("; ", undo.Results?.Select(x => x.Detail) ?? []));
            Assert.True(File.Exists(victim));
        }
        finally
        {
            if (File.Exists(ruleFile)) File.Delete(ruleFile);
            Directory.Delete(Path.GetDirectoryName(cacheDir)!, recursive: true);
        }
    }
}
