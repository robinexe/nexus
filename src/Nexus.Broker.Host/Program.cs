using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Nexus.Broker;
using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;
using Nexus.WindowsActions.Startup;

// nexus-broker [--pipe NOME] [--journal CAMINHO] [--allow-sid SID]
//   Serve o protocolo do broker em \\.\pipe\NOME (padrão NexusBroker.v1).
//   Escrita (Execute/EndSession/RevertRun/Recover) só em VM de teste liberada (VmOnlyExecutionGate).
//   Leitura (Ping/Status/ReadState/Plan) sempre.

var argv = args.ToList();
string? Opt(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= argv.Count || argv[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{name} exige um valor.");
    return argv[i + 1];
}

string pipeName, journalPath, allowSid;
try
{
    pipeName = Opt("--pipe") ?? BrokerPipeServer.DefaultPipeName;
    journalPath = Opt("--journal") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", "journal.db");
    using var me = WindowsIdentity.GetCurrent();
    allowSid = Opt("--allow-sid") ?? me.User!.Value;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var snapshot = Discovery.Capture();
var facts = snapshot.Facts;
using var self = WindowsIdentity.GetCurrent();
IExecutionGate gate = self.IsSystem
    // Como LocalSystem, HKCU seria o perfil do SYSTEM: ações de usuário escreveriam no lugar errado e seriam "verificadas".
    // Até o broker resolver HKCU para o SID do cliente, o modo serviço só serve leitura.
    ? new BlockedGate("Executando como LocalSystem: resolução de HKCU para o usuário cliente ainda não implementada; escrita bloqueada.")
    : new VmOnlyExecutionGate(() => facts.IsVirtualMachine, VmOnlyExecutionGate.DefaultMarkerPath, Environment.MachineName, AdminOwnedFile.Check);
var decision = gate.Evaluate();

Console.WriteLine($"NEXUS broker {BrokerService.Version} — pipe \\\\.\\pipe\\{pipeName}");
Console.WriteLine($"Diário: {journalPath}");
Console.WriteLine($"Máquina: {facts.SystemManufacturer} {facts.SystemModel} (VM: {facts.IsVirtualMachine?.ToString() ?? "indeterminado"})");
Console.WriteLine($"Escrita real: {(decision.Allowed ? "LIBERADA" : "BLOQUEADA")} — {decision.Reason}");
Console.WriteLine($"Cliente autorizado: {allowSid}");

// Portão fechado (ex.: PC físico): nada é criado. O diário é aberto só para leitura se existir; senão, diário vazio em memória.
IJournal journal;
if (decision.Allowed)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
    journal = new SqliteJournal(journalPath);
}
else
{
    journal = File.Exists(journalPath) ? SqliteJournal.OpenReadOnly(journalPath) : new InMemoryJournal();
    Console.WriteLine("Modo somente leitura: nenhum arquivo será criado. Se liberar a VM depois, reinicie o broker.");
}
using var journalLifetime = journal as IDisposable;
var registry = new WindowsRegistryBackend();
var handlers = new HandlerRegistry()
    .Add(new RegistryValueHandler(registry))
    .Add(new DirectXUserGpuPreferenceHandler(registry))
    .Add(new StartupApprovedHandler(registry))
    .Add(new PowerActiveSchemeHandler(new WindowsPowerBackend()))
    .Add(new PowerSettingValueHandler(new WindowsPowerBackend()))
    .Add(new ServiceStartTypeHandler(new WindowsServiceBackend()))
    .Add(new SourceCfgConvarHandler(() => null));
var catalog = CatalogLoader.Load(Path.Combine(AppContext.BaseDirectory, "catalog", "actions.v1.json"));
var context = new SystemContext(facts.WindowsBuild ?? 0, facts.CpuModel ?? "desconhecido", facts.GpuVendorPrimary, facts.IsElevated ?? false);
var power = new WindowsPowerBackend();
IReadOnlyDictionary<string, string> Parameters()
{
    // Relido a cada pedido: o valor AC é sempre escrito no esquema ativo no momento do planejamento.
    var p = new Dictionary<string, string>(StringComparer.Ordinal);
    try { p["activeScheme"] = power.GetActiveScheme().ToString("D"); }
    catch (Exception ex) when (ex is not OutOfMemoryException) { } // sem esquema: ações de valor AC ficam "não instanciáveis"
    return p;
}

var pending = RecoveryService.FindInterrupted(journal);
if (pending.Count > 0)
    Console.WriteLine($"ATENÇÃO: {pending.Count} operação(ões) interrompida(s) no diário; Execute ficará recusado até Recover.");

using var service = new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(journalPath), gate,
    () => context, Parameters);

NamedPipeServerStream CreatePipe(bool first)
{
    var security = new PipeSecurity();
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(allowSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
    // Clientes remotos: o .NET já cria o pipe com PIPE_REJECT_REMOTE_CLIENTS; a negação explícita é uma segunda barreira.
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
    return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, BrokerPipeServer.MaxInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, security);
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
Console.WriteLine("Aguardando clientes (Ctrl+C encerra)...");
var server = new BrokerPipeServer(service, CreatePipe) { Log = m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}") };
await server.RunAsync(cancel.Token);
return 0;

internal sealed class BlockedGate(string reason) : IExecutionGate
{
    public GateDecision Evaluate() => new(false, reason);
}
