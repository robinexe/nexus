using System.Diagnostics;
using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.Persistence;

namespace Nexus.Tests;

/// <summary>
/// Coordenação entre executores (caso A4). Usa processos separados de verdade: o auxiliar Nexus.TestHelper segura a
/// trava e para no meio de uma aplicação/restauração; o teste chama o próprio nexus-recover e depois mata o auxiliar.
/// Tudo acontece num diretório temporário (diário, trava e um .cfg de teste). Nada no sistema é tocado.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CoordinationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-coord-").FullName;
    private readonly List<Process> _children = [];
    private string Journal => Path.Combine(_dir, "journal.db");
    private bool _withoutEmergency;
    private string Cfg => Path.Combine(_dir, "autoexec.cfg");

    public void Dispose()
    {
        foreach (var p in _children)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); } }
            catch (InvalidOperationException) { }
            p.Dispose();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------- infraestrutura ----------

    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } p ? p : "dotnet";
    private static string Beside(string dll) => Path.Combine(AppContext.BaseDirectory, dll);

    private Process StartHelper(params string[] args)
    {
        var psi = new ProcessStartInfo(Dotnet) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(Beside("Nexus.TestHelper.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _children.Add(p);
        return p;
    }

    private static void WaitReady(Process p, string readyFile)
    {
        var sw = Stopwatch.StartNew();
        while (!File.Exists(readyFile))
        {
            if (p.HasExited) Assert.Fail($"auxiliar saiu ({p.ExitCode}) antes do ponto de parada: {p.StandardError.ReadToEnd()}");
            if (sw.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail("auxiliar não atingiu o ponto de parada em 60 s");
            Thread.Sleep(50);
        }
    }

    private static void Kill(Process p)
    {
        p.Kill(entireProcessTree: true); // equivalente a "Finalizar tarefa": nenhum código de limpeza roda
        Assert.True(p.WaitForExit(30_000));
    }

    private (int Exit, string Out) Cli(params string[] args)
    {
        var psi = new ProcessStartInfo(Dotnet) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(Beside("nexus-recover.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        // recover/revert passam pelo portão (que bloqueia fora de VM liberada). Estes testes só escrevem num .cfg temporário,
        // então usam a exceção explícita; o bloqueio em si é testado em Cli_recover_is_blocked_by_the_gate_outside_a_test_vm.
        if (args.Length > 0 && args[0] is "recover" or "revert" && !_withoutEmergency) psi.ArgumentList.Add("--host-emergency");
        psi.ArgumentList.Add("--journal");
        psi.ArgumentList.Add(Journal);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "nexus-recover não terminou");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static HandlerRegistry CfgHandlers() => new HandlerRegistry().Add(new SourceCfgConvarHandler(
        () => CvarCatalog.FromCvarListDump("fps_max : 400 : , \"cl\" : limite\n", "dump de teste")));

    private ActionInstance CfgAction(string value) =>
        new(T.Def("cs2.cfg.fps_max", SourceCfgConvarHandler.Kind),
            new Dictionary<string, string> { ["root"] = _dir, ["file"] = "autoexec.cfg", ["convar"] = "fps_max" },
            StateSnapshot.Of("Convar", value));

    private string FpsMax() => SourceCfgDocument.Parse(File.ReadAllText(Cfg)).GetEffective("fps_max")!;

    private static JournalState StateOf(string journal)
    {
        using var j = SqliteJournal.OpenReadOnly(journal);
        return j.Runs().SelectMany(r => j.Entries(r.RunId)).Single().State;
    }

    // ---------- trava em processo ----------

    [Fact]
    public void Simultaneous_executors_in_same_process_are_mutually_exclusive()
    {
        var a = new FileRunCoordinator(_dir);
        var b = new FileRunCoordinator(_dir); // outra instância (ex.: interface e CLI embutido no mesmo processo)
        using var first = a.TryAcquire("interface").Lease;
        Assert.NotNull(first);

        var second = b.TryAcquire("cli", TimeSpan.FromMilliseconds(300));
        Assert.Equal(LeaseAcquireStatus.HeldByOther, second.Status);
        Assert.Equal("interface", second.Holder?.Purpose);
        Assert.Equal(CoordinationState.Active, b.Probe().State);

        first!.Dispose();
        using var third = b.TryAcquire("cli").Lease;
        Assert.NotNull(third);
        Assert.False(b.Probe().AbandonedByCrash);
    }

    [Fact]
    public void Released_lease_cannot_drive_an_executor()
    {
        var lease = new FileRunCoordinator(_dir).TryAcquire("x").Lease!;
        using var journal = new SqliteJournal(Journal);
        var exec = new ActionExecutor(CfgHandlers(), journal, lease);
        var run = exec.BeginRun("p", "1");
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => exec.Execute(run, new Plan([]), T.Ctx));
        Assert.Throws<ObjectDisposedException>(() => new ActionExecutor(CfgHandlers(), journal, lease));
    }

    [Fact]
    public void Executor_refuses_a_run_not_bound_to_its_lease()
    {
        using var lease = new FileRunCoordinator(_dir).TryAcquire("x").Lease!;
        using var journal = new SqliteJournal(Journal);
        var exec = new ActionExecutor(CfgHandlers(), journal, lease);
        var foreign = journal.BeginRun("de outro executor", "1");
        Assert.Throws<InvalidOperationException>(() => exec.Execute(foreign, new Plan([]), T.Ctx));
    }

    [Fact]
    public void Recovery_never_touches_the_run_bound_to_its_own_lease()
    {
        File.WriteAllText(Cfg, "fps_max 300\n");
        using var lease = new FileRunCoordinator(_dir).TryAcquire("interface").Lease!;
        using var journal = new SqliteJournal(Journal);
        var handlers = CfgHandlers();
        var exec = new ActionExecutor(handlers, journal, lease);
        var run = exec.BeginRun("sessão em andamento", "1");
        var session = CfgAction("400") with { Definition = T.Def("cs2.cfg.fps_max", SourceCfgConvarHandler.Kind, persistence: PersistenceKind.Session) };
        exec.Execute(run, new DesiredStateResolver(handlers).Resolve([session], T.Ctx), T.Ctx);

        var report = new RecoveryService(journal, exec).Recover();

        Assert.Empty(report.Results);
        Assert.Single(report.SkippedActive);
        Assert.False(report.FullyRestored);
        Assert.Equal("400", FpsMax()); // a sessão ativa continua aplicada
    }

    [Fact]
    public void Cli_refuses_journal_option_without_a_path_instead_of_using_the_default_journal()
    {
        var psi = new ProcessStartInfo(Dotnet) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(Beside("nexus-recover.dll"));
        foreach (var a in new[] { "recover", "--force", "--journal" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000));
        Assert.Equal(2, p.ExitCode);
        Assert.Contains("--journal exige um caminho", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_recover_is_blocked_by_the_gate_outside_a_test_vm()
    {
        File.WriteAllText(Cfg, "fps_max 300\n");
        var ready = Path.Combine(_dir, "ready");
        var child = StartHelper("apply-and-hang", Journal, _dir, ready);
        WaitReady(child, ready);
        Kill(child); // execução abandonada com 400 aplicado

        _withoutEmergency = true;
        var refused = Cli("recover");
        Assert.Equal(7, refused.Exit);
        Assert.Contains("RECUSADO", refused.Out, StringComparison.Ordinal);
        Assert.Equal("400", FpsMax());                         // nada foi alterado
        Assert.Equal(JournalState.Applying, StateOf(Journal));
        Assert.Equal(1, Cli("scan").Exit);                     // consulta continua funcionando
    }

    [Fact]
    public void Cli_accept_closes_a_pending_entry_without_the_gate_and_refuses_while_another_process_holds_the_lock()
    {
        File.WriteAllText(Cfg, "fps_max 300\n");
        var ready = Path.Combine(_dir, "ready");
        var child = StartHelper("apply-and-hang", Journal, _dir, ready);
        WaitReady(child, ready);
        Kill(child); // execução abandonada com 400 aplicado: entrada pendente em Applying

        long id;
        using (var j = SqliteJournal.OpenReadOnly(Journal)) id = j.Runs().SelectMany(r => j.Entries(r.RunId)).Single().Id;

        var holderReady = Path.Combine(_dir, "ready-hold");
        var holder = StartHelper("hold", Journal, holderReady);
        WaitReady(holder, holderReady);
        Assert.Equal(5, Cli("accept", id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Exit); // trava ocupada
        Assert.Equal(JournalState.Applying, StateOf(Journal));
        Kill(holder);

        _withoutEmergency = true; // accept não escreve no sistema: não passa pelo portão (não há exit 7)
        Assert.Equal(2, Cli("accept", "abc").Exit);
        var ok = Cli("accept", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, ok.Exit);
        Assert.Contains("Accepted", ok.Out, StringComparison.Ordinal);
        Assert.Equal(JournalState.Accepted, StateOf(Journal));
        Assert.Equal("400", FpsMax());                 // estado atual mantido
        Assert.Equal(0, Cli("scan").Exit);             // nada mais a recuperar
        Assert.Equal(4, Cli("accept", id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Exit); // já encerrada
        Assert.Equal(4, Cli("accept", "9999").Exit);   // inexistente
    }

    // ---------- processos separados ----------

    [Fact]
    public void Lease_held_by_another_process_blocks_acquire_and_identifies_holder()
    {
        var ready = Path.Combine(_dir, "ready");
        var child = StartHelper("hold", Journal, ready);
        WaitReady(child, ready);

        var coordinator = FileRunCoordinator.ForJournal(Journal);
        var r = coordinator.TryAcquire("teste", TimeSpan.FromMilliseconds(300));
        Assert.Equal(LeaseAcquireStatus.HeldByOther, r.Status);
        Assert.Equal(child.Id, r.Holder?.ProcessId);
        var probe = coordinator.Probe();
        Assert.Equal(CoordinationState.Active, probe.State);
        Assert.Equal(child.Id, probe.Holder?.ProcessId);
    }

    [Fact]
    public void Killed_holder_leaves_an_abandoned_lock_that_is_detected_and_reacquired()
    {
        var ready = Path.Combine(_dir, "ready");
        var child = StartHelper("hold", Journal, ready);
        WaitReady(child, ready);
        var childPid = child.Id;
        Kill(child);

        var coordinator = FileRunCoordinator.ForJournal(Journal);
        var probe = coordinator.Probe();
        Assert.Equal(CoordinationState.Idle, probe.State);
        Assert.True(probe.AbandonedByCrash);
        Assert.Equal(childPid, probe.Holder!.ProcessId); // diagnóstico; a decisão veio da trava do SO, não do PID

        using var lease = coordinator.TryAcquire("teste").Lease;
        Assert.NotNull(lease);
        Assert.Equal(Environment.ProcessId, coordinator.ReadOwnerRecord()!.ProcessId);
    }

    [Fact]
    public void Recovery_is_refused_while_another_process_is_applying_and_runs_after_it_dies()
    {
        File.WriteAllText(Cfg, "fps_max 300\n");
        var ready = Path.Combine(_dir, "ready");
        var child = StartHelper("apply-and-hang", Journal, _dir, ready);
        WaitReady(child, ready); // auxiliar escreveu 400 e está parado antes de marcar Applied

        Assert.Equal("400", FpsMax());
        Assert.Equal(JournalState.Applying, StateOf(Journal));

        var scan = Cli("scan");
        Assert.Equal(1, scan.Exit);
        Assert.Contains("EM ANDAMENTO", scan.Out, StringComparison.Ordinal);

        var refused = Cli("recover");
        Assert.Equal(5, refused.Exit);
        Assert.Contains("RECUSADO", refused.Out, StringComparison.Ordinal);
        Assert.Equal("400", FpsMax());                   // nada foi tocado
        Assert.Equal(JournalState.Applying, StateOf(Journal));

        Kill(child);

        var scanAfter = Cli("scan");
        Assert.Contains("ABANDONADA", scanAfter.Out, StringComparison.Ordinal);
        var recovered = Cli("recover");
        Assert.Equal(0, recovered.Exit);
        Assert.Equal("300", FpsMax());
        Assert.Equal(JournalState.Reverted, StateOf(Journal));
        Assert.Equal(0, Cli("scan").Exit);
    }

    [Fact]
    public void Recovery_is_refused_while_another_process_is_restoring_and_resumes_safely_after_it_dies()
    {
        File.WriteAllText(Cfg, "fps_max 300\n");
        Guid run;
        var handlers = CfgHandlers();
        var coordinator = FileRunCoordinator.ForJournal(Journal);
        using (var lease = coordinator.TryAcquire("aplicação").Lease!)
        using (var journal = new SqliteJournal(Journal))
        {
            var exec = new ActionExecutor(handlers, journal, lease);
            run = exec.BeginRun("aplicado", "1");
            Assert.Equal(ExecutionOutcome.Verified,
                exec.Execute(run, new DesiredStateResolver(handlers).Resolve([CfgAction("400")], T.Ctx), T.Ctx).Single().Outcome);
            exec.EndRun(run);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var ready = Path.Combine(_dir, "ready");
        var child = StartHelper("restore-and-hang", Journal, _dir, ready, run.ToString());
        WaitReady(child, ready); // Restoring gravado no diário; restauração ainda não escrita

        Assert.Equal(JournalState.Restoring, StateOf(Journal));
        var refused = Cli("recover");
        Assert.Equal(5, refused.Exit);
        var refusedRevert = Cli("revert", run.ToString());
        Assert.Equal(5, refusedRevert.Exit);
        Assert.Equal("400", FpsMax());

        Kill(child);

        var scan = Cli("scan");
        Assert.Equal(1, scan.Exit);
        // A saída redirecionada do CLI usa a página de código do console (ex.: 437 numa VM em inglês), então texto
        // acentuado não pode ser comparado aqui. O motivo exato é conferido pelo diário; na saída, a entrada e o prefixo.
        using (var ro = SqliteJournal.OpenReadOnly(Journal))
        {
            var finding = Assert.Single(RecoveryService.FindInterrupted(ro));
            Assert.Equal("Restauração interrompida ou não confirmada.", finding.Reason);
            Assert.Contains($"#{finding.Entry.Id} ", scan.Out, StringComparison.Ordinal);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Contains(": Restaura", scan.Out, StringComparison.Ordinal);
        var recovered = Cli("recover");
        Assert.Equal(0, recovered.Exit);
        Assert.Equal("300", FpsMax());
        Assert.Equal(JournalState.Reverted, StateOf(Journal));
    }
}
