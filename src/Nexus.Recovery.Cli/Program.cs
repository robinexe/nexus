using Nexus.Broker;
using Nexus.Core;
using Nexus.HardwareDiscovery;
using Nexus.WindowsActions;
using Nexus.GameProfiles.SourceCfg;
using Nexus.Persistence;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Network;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;
using Nexus.WindowsActions.Startup;

// nexus-recover: funciona sem a interface. Usa somente o diário e os handlers tipados.
//   nexus-recover runs                      lista execuções
//   nexus-recover show <runId>              mostra entradas e estados
//   nexus-recover scan                      lista operações interrompidas (abre o diário somente leitura)
//   nexus-recover recover [--force]         restaura as interrompidas; --force sobrescreve conflitos
//                                           (recusa se houver execução ativa: exit 5; indeterminado: exit 6)
//   nexus-recover revert <runId> [--force]  reverte uma execução inteira
//   nexus-recover accept <entryId>          mantém o estado atual e encerra uma entrada pendente (superada/conflito),
//                                           com registro no diário; não lê nem escreve no sistema (sem portão).
//                                           Exige a trava (exit 5/6). Entrada inexistente ou não pendente: exit 4.
//   Opção global: --journal <caminho>  (padrão: %ProgramData%\NEXUS\journal.db)
//   recover/revert só desfazem o que o NEXUS registrou no diário; funcionam neste PC (decisão do responsável em 31/10/2026).
//   --host-emergency: aceito por compatibilidade (não é mais necessário).

var argv = args.ToList();
if (argv.IndexOf("--journal") is var ji and >= 0 && (ji + 1 >= argv.Count || argv[ji + 1].StartsWith("--", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("--journal exige um caminho. Nada foi feito (não cai no diário padrão).");
    return 2;
}
var journalPath = TakeOption(argv, "--journal")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", "journal.db");
var force = argv.Remove("--force");
var hostEmergency = argv.Remove("--host-emergency");

if (argv.Count == 0) { Console.Error.WriteLine("Uso: nexus-recover runs|show|scan|recover|revert|accept ..."); return 2; }
if (!File.Exists(journalPath)) { Console.Error.WriteLine($"Diário não encontrado: {journalPath}"); return 3; }

// runs/show/scan abrem o diário em modo somente leitura (sem migração; o SQLite pode criar -wal/-shm auxiliares para ler).
// recover/revert exigem a trava de execução compartilhada com a interface e o broker (FileRunCoordinator):
// se houver executor vivo, recusam (exit 5); se não der para saber (permissão/erro), também recusam (exit 6).
var command = argv[0];
var coordinator = FileRunCoordinator.ForJournal(journalPath);

switch (command)
{
    case "runs":
    {
        using var journal = SqliteJournal.OpenReadOnly(journalPath);
        var probe = coordinator.Probe();
        foreach (var r in journal.Runs())
            Console.WriteLine($"{r.RunId}  {r.StartedAt:u}  {RunStatus(r, probe)}  {r.Profile}  catálogo {r.CatalogVersion}");
        return 0;
    }

    case "show" when argv.Count > 1:
    {
        if (!Guid.TryParse(argv[1], out var runId)) { Console.Error.WriteLine("runId inválido."); return 2; }
        using var journal = SqliteJournal.OpenReadOnly(journalPath);
        foreach (var e in journal.Entries(runId))
            Console.WriteLine($"#{e.Id} [{e.State}] {e.Action.Definition.Key}  {e.Action.TargetDescription}\n" +
                              $"     original={e.Original?.Describe() ?? "-"}  desejado={e.Action.Desired.Describe()}  observado={e.LastObserved?.Describe() ?? "-"}  {e.Note}");
        return 0;
    }

    case "scan":
    {
        using var journal = SqliteJournal.OpenReadOnly(journalPath);
        var probe = coordinator.Probe();
        Console.WriteLine(DescribeProbe(probe));
        var runs = journal.Runs().ToDictionary(r => r.RunId);
        var found = RecoveryService.FindInterrupted(journal);
        foreach (var f in found)
            Console.WriteLine($"#{f.Entry.Id} {f.Entry.Action.Definition.Key} [{RunStatus(runs[f.Entry.RunId], probe)}]: {f.Reason}");
        Console.WriteLine(found.Count == 0 ? "Nenhuma operação interrompida." : $"{found.Count} operação(ões) a recuperar.");
        return found.Count == 0 ? 0 : 1;
    }

    case "recover":
    case "revert" when argv.Count > 1:
    {
        Guid revertRun = default;
        if (command == "revert" && !Guid.TryParse(argv[1], out revertRun)) { Console.Error.WriteLine("runId inválido."); return 2; }
        _ = hostEmergency; // compatibilidade: recuperar neste PC não exige mais o argumento
        var acquired = coordinator.TryAcquire(command, TimeSpan.FromSeconds(2));
        if (!acquired.Acquired)
            return Refuse(acquired);
        using var lease = acquired.Lease!;
        using var journal = new SqliteJournal(journalPath);
        // Com a trava em mãos, nenhum outro executor está vivo: toda execução não encerrada está abandonada.
        var executor = new ActionExecutor(BuildHandlers(), journal, lease);
        if (command == "recover")
        {
            var report = new RecoveryService(journal, executor).Recover(force);
            Print(report.Results);
            if (report.AnyInferred)
                Console.WriteLine("Itens marcados [inferido]: restaurados com base em inferência (interrompidos durante o Apply com o valor desejado presente).");
            Console.WriteLine(report.FullyRestored ? "Recuperação completa." : "RECUPERAÇÃO PARCIAL: veja os itens acima. Nada foi declarado restaurado sem releitura.");
            return report.FullyRestored ? 0 : 1;
        }
        var results = executor.RevertRun(revertRun, force);
        Print(results);
        return results.All(r => r.Outcome is RevertOutcome.Reverted or RevertOutcome.AlreadyOriginal or RevertOutcome.NothingToRevert) ? 0 : 1;
    }

    case "accept" when argv.Count > 1:
    {
        if (!long.TryParse(argv[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var entryId))
        {
            Console.Error.WriteLine("entryId inválido (use o número mostrado como #N em scan/show).");
            return 2;
        }
        var acquired = coordinator.TryAcquire(command, TimeSpan.FromSeconds(2));
        if (!acquired.Acquired)
            return Refuse(acquired);
        using var lease = acquired.Lease!;
        using var journal = new SqliteJournal(journalPath);
        var executor = new ActionExecutor(new HandlerRegistry(), journal, lease); // sem handlers: accept não toca o sistema
        var result = new RecoveryService(journal, executor).Accept(entryId);
        Console.WriteLine($"#{result.EntryId}: {result.Outcome} — {result.Detail}");
        return result.Outcome == AcceptOutcome.Accepted ? 0 : 4;
    }

    default:
        Console.Error.WriteLine("Comando desconhecido.");
        return 2;
}

static int Refuse(LeaseAcquireResult r)
{
    switch (r.Status)
    {
        case LeaseAcquireStatus.HeldByOther:
            Console.Error.WriteLine("RECUSADO: há uma execução do NEXUS em andamento" +
                (r.Holder is null ? "." : $": {r.Holder.Describe()}.") +
                " A recuperação não interfere numa execução ativa. Encerre-a (ou aguarde) e tente de novo.");
            return 5;
        default:
            Console.Error.WriteLine($"RECUSADO: não foi possível verificar se há execução ativa ({r.Status}: {r.Detail}). Nada foi alterado.");
            return 6;
    }
}

static string DescribeProbe(CoordinationProbe p) => p.State switch
{
    CoordinationState.Active => $"Executor ATIVO: {p.Holder?.Describe() ?? "dono não identificado"}. Recuperação seria recusada agora.",
    CoordinationState.Idle when p.AbandonedByCrash => $"Nenhum executor ativo. Trava abandonada por: {p.Holder!.Describe()}.",
    CoordinationState.Idle => "Nenhum executor ativo.",
    _ => $"Não foi possível verificar executores ativos: {p.Detail}. Recuperação seria recusada.",
};

static string RunStatus(RunInfo r, CoordinationProbe p)
{
    if (r.EndedAt is not null) return $"encerrada {r.EndedAt:u}";
    return p.State switch
    {
        CoordinationState.Idle => "ABANDONADA",
        CoordinationState.Active when p.Holder?.RunId == r.RunId => "EM ANDAMENTO",
        CoordinationState.Active when p.Holder is not null => "ABANDONADA (outro executor ativo)",
        _ => "INDETERMINADA",
    };
}

static void Print(IEnumerable<RevertResult> results)
{
    foreach (var r in results) Console.WriteLine($"#{r.EntryId} {r.ActionKey}: {r.Outcome} — {r.Detail}");
}

static string? TakeOption(List<string> a, string name)
{
    var i = a.IndexOf(name);
    if (i < 0 || i + 1 >= a.Count) return null;
    var v = a[i + 1];
    a.RemoveRange(i, 2);
    return v;
}

static HandlerRegistry BuildHandlers()
{
    var reg = new HandlerRegistry();
    // Arquivos de jogo: a restauração não exige catálogo de cvars (só escreve o valor original salvo no diário),
    // por isso o provider retorna null e Assess não é chamado no caminho de reversão.
    reg.Add(new SourceCfgConvarHandler(() => null));
    if (OperatingSystem.IsWindows())
    {
        var registry = new WindowsRegistryBackend();
        reg.Add(new RegistryValueHandler(registry))
           .Add(new QosGamePolicyHandler(registry))
           .Add(new DirectXUserGpuPreferenceHandler(registry))
           .Add(new StartupApprovedHandler(registry))
           .Add(new PowerActiveSchemeHandler(new WindowsPowerBackend()))
           .Add(new PowerSettingValueHandler(new WindowsPowerBackend()))
           .Add(new PowerOverlaySchemeHandler(new WindowsPowerBackend()))
           .Add(new PowerUltimateSchemeHandler(new WindowsPowerBackend()))
           .Add(new ServiceStartTypeHandler(new WindowsServiceBackend()));
    }
    return reg;
}
