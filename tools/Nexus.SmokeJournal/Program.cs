using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;

// Uso: Nexus.SmokeJournal <caminho.db>
// Cria 2 execuções num registro EM MEMÓRIA (nada toca o sistema):
//   1) "encerrada": ação persistente Verified, EndRun registrado  -> scan não deve listar
//   2) "interrompida": ação de sessão Verified + ação que "morreu" em Applying, sem EndRun -> scan deve listar 2
if (args.Length != 1) { Console.Error.WriteLine("Uso: Nexus.SmokeJournal <caminho.db>"); return 2; }
var path = Path.GetFullPath(args[0]);
if (File.Exists(path)) { Console.Error.WriteLine($"Já existe: {path} (não sobrescrevo)."); return 3; }

var reg = new InMemoryRegistryBackend();
var handlers = new HandlerRegistry().Add(new RegistryValueHandler(reg));
// A trava fica no diretório do diário de teste (nunca em %ProgramData%).
var coordinator = FileRunCoordinator.ForJournal(path);
using (var journal = new SqliteJournal(path))
{
    var resolver = new DesiredStateResolver(handlers);
    var ctx = new SystemContext(26100, "smoke", null, IsElevated: false);

    using (var lease = coordinator.TryAcquire("smoke-encerrada").Lease ?? throw new InvalidOperationException("trava ocupada"))
    {
        var exec = new ActionExecutor(handlers, journal, lease);
        var r1 = exec.BeginRun("smoke-encerrada", "1");
        exec.Execute(r1, resolver.Resolve([Act("p1", "Persist", PersistenceKind.Persistent)], ctx), ctx);
        exec.EndRun(r1);
    }

    // Execução "interrompida": a trava é liberada sem EndRun, como se o processo tivesse morrido.
    using (var lease = coordinator.TryAcquire("smoke-interrompida").Lease ?? throw new InvalidOperationException("trava ocupada"))
    {
        var exec = new ActionExecutor(handlers, journal, lease);
        var r2 = exec.BeginRun("smoke-interrompida", "1");
        exec.Execute(r2, resolver.Resolve([Act("s1", "Session", PersistenceKind.Session)], ctx), ctx);
        exec.FaultInjector = (where, _) => { if (where == "after-apply") throw new InvalidOperationException("crash simulado"); };
        try { exec.Execute(r2, resolver.Resolve([Act("c1", "Crash", PersistenceKind.Persistent)], ctx), ctx); }
        catch (InvalidOperationException) { }
    }
}
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
Console.WriteLine($"Diário de teste criado: {path}");
Console.WriteLine($"SQLite nativo carregado: {SqliteJournal.NativeSqliteVersion()}");
return 0;

static ActionInstance Act(string id, string name, PersistenceKind persistence) =>
    new(new ActionDefinition(id, 1, id, Classification.SupportedDocumented, RegistryValueHandler.Kind, "smoke",
            [new EvidenceRef(EvidenceLevel.Hypothesis, "smoke")], "nenhum", PrivilegeLevel.User, persistence, RebootRequirement.None),
        new Dictionary<string, string> { ["hive"] = "HKCU", ["path"] = @"Software\NexusSmoke", ["name"] = name, ["type"] = "DWord" },
        StateSnapshot.Of("DWord", "1"));
