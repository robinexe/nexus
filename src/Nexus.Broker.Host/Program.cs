using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Nexus.Broker;
using Nexus.Broker.Host;
using Nexus.Core;
using Nexus.GameProfiles.SourceCfg;
using Nexus.HardwareDiscovery;
using Nexus.Persistence;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Power;
using Nexus.WindowsActions.Registry;
using Nexus.WindowsActions.Services;
using Nexus.WindowsActions.Startup;

// nexus-broker [--pipe NOME] [--journal CAMINHO] [--allow-sid SID] [--service]
//   Serve o protocolo do broker em \\.\pipe\NOME (padrão NexusBroker.v1).
//   Escrita (Execute/EndSession/RevertRun/Recover) só em VM de teste liberada (VmOnlyExecutionGate).
//   Leitura (Ping/Status/ReadState/Plan) sempre.
//   --service: roda sob o Gerenciador de Serviços (instalado por tools\service\Install-Broker.ps1, conta LocalSystem).
//              Como LocalSystem, ações de HKCU vão para o perfil de QUEM PEDIU (SID lido do token do cliente no pipe).

BrokerOptions options;
try
{
    options = BrokerOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (options.Service)
{
    ServiceBase.Run(new BrokerWindowsService(options));
    return 0;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
return await BrokerHost.RunAsync(options, Console.WriteLine, cancel.Token);

namespace Nexus.Broker.Host
{
    internal sealed record BrokerOptions(string PipeName, string JournalPath, string AllowSid, bool Service)
    {
        public static BrokerOptions Parse(string[] args)
        {
            var argv = args.ToList();
            string? Opt(string name)
            {
                var i = argv.IndexOf(name);
                if (i < 0) return null;
                if (i + 1 >= argv.Count || argv[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{name} exige um valor.");
                return argv[i + 1];
            }
            var service = argv.Contains("--service");
            using var me = WindowsIdentity.GetCurrent();
            var allow = Opt("--allow-sid");
            if (service && allow is null)
                throw new ArgumentException("--service exige --allow-sid (o usuário que poderá usar o broker).");
            if (allow is not null && !UserProfileScope.IsValidSid(allow))
                throw new ArgumentException("--allow-sid deve ser o SID de uma conta de usuário (S-1-5-21-…).");
            return new BrokerOptions(
                Opt("--pipe") ?? BrokerPipeServer.DefaultPipeName,
                Opt("--journal") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", "journal.db"),
                allow ?? me.User!.Value,
                service);
        }
    }

    internal static class BrokerHost
    {
        public static async Task<int> RunAsync(BrokerOptions o, Action<string> log, CancellationToken cancel)
        {
            var snapshot = Discovery.Capture();
            var facts = snapshot.Facts;
            using var self = WindowsIdentity.GetCurrent();
            var asSystem = self.IsSystem;
            // Como LocalSystem, HKCU do processo é o perfil do SYSTEM: o broker grava no alvo o SID de quem pediu (B10) e o
            // backend recusa HKCU sem SID. O portão é o mesmo nos dois modos.
            var gate = new VmOnlyExecutionGate(() => facts.IsVirtualMachine, VmOnlyExecutionGate.DefaultMarkerPath, Environment.MachineName, AdminOwnedFile.Check);
            var decision = gate.Evaluate();

            log($"NEXUS broker {BrokerService.Version} — pipe \\\\.\\pipe\\{o.PipeName}{(o.Service ? " (serviço)" : "")}");
            log($"Conta: {self.Name}{(asSystem ? " (LocalSystem: HKCU = perfil de quem pede)" : "")}");
            log($"Diário: {o.JournalPath}");
            log($"Máquina: {facts.SystemManufacturer} {facts.SystemModel} (VM: {facts.IsVirtualMachine?.ToString() ?? "indeterminado"})");
            log($"Escrita real: {(decision.Allowed ? "LIBERADA" : "BLOQUEADA")} — {decision.Reason}");
            log($"Cliente autorizado: {o.AllowSid}");

            // Portão fechado (ex.: PC físico): nada é criado. O diário é aberto só para leitura se existir; senão, vazio em memória.
            IJournal journal;
            if (decision.Allowed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.JournalPath))!);
                journal = new SqliteJournal(o.JournalPath);
            }
            else
            {
                journal = File.Exists(o.JournalPath) ? SqliteJournal.OpenReadOnly(o.JournalPath) : new InMemoryJournal();
                log("Modo somente leitura: nenhum arquivo será criado. Se liberar a VM depois, reinicie o broker.");
            }
            using var journalLifetime = journal as IDisposable;
            var registry = new WindowsRegistryBackend(requireUserProfileForHkcu: asSystem);
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
                log($"ATENÇÃO: {pending.Count} operação(ões) interrompida(s) no diário; Execute ficará recusado até Recover.");

            using var service = new BrokerService(catalog, handlers, journal, FileRunCoordinator.ForJournal(o.JournalPath), gate,
                () => context, Parameters, bindCallerProfile: asSystem);

            NamedPipeServerStream CreatePipe(bool first)
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(o.AllowSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                // Clientes remotos: o .NET já cria o pipe com PIPE_REJECT_REMOTE_CLIENTS; a negação explícita é uma segunda barreira.
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                return NamedPipeServerStreamAcl.Create(o.PipeName, PipeDirection.InOut, BrokerPipeServer.MaxInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, security);
            }

            log("Aguardando clientes...");
            var server = new BrokerPipeServer(service, CreatePipe)
            {
                Log = m => log($"{DateTime.Now:HH:mm:ss} {m}"),
                IdentifyClient = IdentifyClient,
            };
            await server.RunAsync(cancel);
            return 0;
        }

        /// <summary>SID do token do cliente (nível Identification basta). Nunca vem do conteúdo do pedido.</summary>
        private static string? IdentifyClient(NamedPipeServerStream pipe)
        {
            string? sid = null;
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(ifImpersonating: true);
                sid = id?.User?.Value;
            });
            return sid;
        }
    }

    /// <summary>Serviço do Windows: mesma lógica do modo console, com log em %ProgramData%\NEXUS\broker.log (limitado a 1 MB).</summary>
    internal sealed class BrokerWindowsService : ServiceBase
    {
        public const string Name = "NexusBroker";
        private readonly BrokerOptions _options;
        private readonly CancellationTokenSource _stop = new();
        private Task<int>? _run;
        private readonly Lock _logLock = new();

        public BrokerWindowsService(BrokerOptions options)
        {
            _options = options;
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        private string LogPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_options.JournalPath))!, "broker.log");

        private void Log(string message)
        {
            lock (_logLock)
            {
                try
                {
                    var path = LogPath;
                    if (!Directory.Exists(Path.GetDirectoryName(path))) return; // o instalador cria a pasta; o serviço não cria
                    if (File.Exists(path) && new FileInfo(path).Length > 1_048_576) File.WriteAllText(path, "");
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        protected override void OnStart(string[] args)
        {
            _run = Task.Run(async () =>
            {
                try { return await BrokerHost.RunAsync(_options, Log, _stop.Token); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log($"Broker parou com erro: {ex.GetType().Name}: {ex.Message}");
                    ExitCode = 1;
                    Stop();
                    return 1;
                }
            });
        }

        protected override void OnStop() => Shutdown();
        protected override void OnShutdown() => Shutdown();

        private void Shutdown()
        {
            _stop.Cancel();
            try { _run?.Wait(TimeSpan.FromSeconds(20)); } catch (AggregateException) { }
            Log("Serviço parado.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _stop.Dispose();
            base.Dispose(disposing);
        }
    }
}
