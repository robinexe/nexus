using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.Persistence;

// Uso (somente pelos testes):
//   hold            <journal> <readyFile>
//   apply-and-hang  <journal> <cfgRoot> <readyFile>
//   restore-and-hang <journal> <cfgRoot> <readyFile> <runId>
//   fake-presentmon <csvDeOrigem|--deny> <argumentos do PresentMon...>   (PresentMon falso: copia o CSV para --output_file)
// "ready" é gravado quando o ponto de parada é atingido; depois o processo dorme até ser morto.
if (args.Length >= 2 && args[0] == "fake-presentmon")
{
    if (args[1] == "--deny") { Console.Error.WriteLine("Error: access denied. Requires elevated privilege or Performance Log Users group."); return 5; }
    var outIdx = Array.IndexOf(args, "--output_file");
    if (outIdx < 0 || outIdx + 1 >= args.Length || !args.Contains("--process_name") || !args.Contains("--terminate_after_timed")) return 3;
    Console.WriteLine("Started recording.");
    File.Copy(args[1], args[outIdx + 1], overwrite: true);
    return 0;
}
if (args.Length < 3) return 2;
var journalPath = args[1];
var coordinator = FileRunCoordinator.ForJournal(journalPath);
var acquired = coordinator.TryAcquire("test-" + args[0], TimeSpan.FromSeconds(10));
if (!acquired.Acquired) { Console.Error.WriteLine($"trava: {acquired.Status}"); return 3; }
var lease = acquired.Lease!;

switch (args[0])
{
    case "hold":
        Ready(args[2]);
        break;

    case "apply-and-hang":
    {
        var journal = new SqliteJournal(journalPath);
        var handlers = Handlers();
        var exec = new ActionExecutor(handlers, journal, lease)
        {
            FaultInjector = (where, _) => { if (where == "after-apply") Ready(args[3]); },
        };
        var run = exec.BeginRun("teste-aplicacao", "1");
        exec.Execute(run, new DesiredStateResolver(handlers).Resolve([CfgAction(args[2], "400")], Ctx), Ctx);
        Console.Error.WriteLine("não deveria chegar aqui");
        return 4;
    }

    case "restore-and-hang":
    {
        var journal = new SqliteJournal(journalPath);
        var exec = new ActionExecutor(Handlers(), journal, lease)
        {
            FaultInjector = (where, _) => { if (where == "before-restore") Ready(args[3]); },
        };
        exec.RevertRun(Guid.Parse(args[4]));
        Console.Error.WriteLine("não deveria chegar aqui");
        return 4;
    }

    default:
        return 2;
}
return 0;

static void Ready(string readyFile)
{
    File.WriteAllText(readyFile + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    File.Move(readyFile + ".tmp", readyFile, overwrite: true);
    Thread.Sleep(Timeout.Infinite); // simula processo travado/no meio da operação até o teste matá-lo
}

static HandlerRegistry Handlers() => new HandlerRegistry().Add(new SourceCfgConvarHandler(
    () => CvarCatalog.FromCvarListDump("fps_max : 400 : , \"cl\" : limite\n", "dump de teste")));

public static partial class Program
{
    public static readonly SystemContext Ctx = new(26100, "teste", null, IsElevated: false);

    public static ActionInstance CfgAction(string root, string value) =>
        new(new ActionDefinition("cs2.cfg.fps_max", 1, "fps_max", Classification.Conditional, SourceCfgConvarHandler.Kind, "teste",
                [new EvidenceRef(EvidenceLevel.Hypothesis, "teste")], "nenhum", PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None),
            new Dictionary<string, string> { ["root"] = root, ["file"] = "autoexec.cfg", ["convar"] = "fps_max" },
            StateSnapshot.Of("Convar", value));
}
