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

// nexus-broker [--pipe NOME] [--journal CAMINHO] [--allow-sid SID] [--service] [--allow-host] [--idle-exit SEG] [--parent-pid PID]
//   Serve o protocolo do broker em \\.\pipe\NOME (padrão NexusBroker.v1).
//   Escrita (Execute/EndSession/RevertRun/Recover): em VM de teste liberada, ou neste PC com --allow-host (autorizado pelo usuário:
//   a interface passa esse argumento depois da confirmação; o instalador, com -AllowHost).
//   --idle-exit/--parent-pid: broker sob demanda encerra quando ocioso ou quando a interface fecha (nunca com sessão aberta).
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
    internal sealed record BrokerOptions(string PipeName, string JournalPath, string AllowSid, bool Service, bool AllowHost = false, int IdleExitSeconds = 0, int ParentPid = 0)
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
            int Int(string name) => Opt(name) is { } v ? int.Parse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture) : 0;
            return new BrokerOptions(
                Opt("--pipe") ?? BrokerPipeServer.DefaultPipeName,
                Opt("--journal") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", "journal.db"),
                allow ?? me.User!.Value,
                service,
                argv.Contains("--allow-host"),
                Int("--idle-exit"),
                Int("--parent-pid"));
        }
    }

    internal static class BrokerHost
    {
        public static async Task<int> RunAsync(BrokerOptions o, Action<string> log, CancellationToken cancel)
        {
            var snapshot = Discovery.Capture();
            var facts = snapshot.Facts;
            using var self = WindowsIdentity.GetCurrent();
            // Perfil de quem pede (B10) sempre que este processo não é o próprio usuário autorizado: LocalSystem (serviço) ou
            // elevado com outra conta (usuário comum que digitou a senha de um administrador no aviso do Windows).
            var asSystem = self.IsSystem || !string.Equals(self.User?.Value, o.AllowSid, StringComparison.Ordinal);
            var vmGate = new VmOnlyExecutionGate(() => facts.IsVirtualMachine, VmOnlyExecutionGate.DefaultMarkerPath, Environment.MachineName, AdminOwnedFile.Check);
            IExecutionGate gate = o.AllowHost ? new AnyGate(new HostConsentGate(), vmGate) : vmGate;
            var decision = gate.Evaluate();

            log($"NEXUS broker {BrokerService.Version} — pipe \\\\.\\pipe\\{o.PipeName}{(o.Service ? " (serviço)" : "")}");
            log($"Conta: {self.Name}{(asSystem ? " (HKCU = perfil de quem pede)" : "")}");
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
            var catalog = CatalogLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "catalog"));
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
                () => context, Parameters, bindCallerProfile: asSystem)
            {
                BeforeSystemChange = facts.IsElevated == true ? RestorePoint.CreateOncePerProcess : null,
            };

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
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            if (o.IdleExitSeconds > 0 || o.ParentPid > 0)
                _ = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try { await Task.Delay(5000, stop.Token); } catch (OperationCanceledException) { return; }
                        if (service.HasActiveSession) continue; // nunca encerra com ajustes de sessão aplicados
                        var idle = o.IdleExitSeconds > 0 && DateTime.UtcNow - server.LastActivityUtc > TimeSpan.FromSeconds(o.IdleExitSeconds);
                        var orphan = o.ParentPid > 0 && !ProcessAlive(o.ParentPid);
                        if (idle || orphan) { log(idle ? "Encerrando: ocioso." : "Encerrando: a interface fechou."); stop.Cancel(); }
                    }
                }, stop.Token);
            await server.RunAsync(stop.Token);
            return 0;
        }

        private static bool ProcessAlive(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
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
