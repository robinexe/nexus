using System.Text;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public sealed class BrokerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-broker-").FullName;
    private readonly InMemoryRegistryBackend _reg = new();
    private readonly InMemoryJournal _journal = new();
    private const string P = @"Software\NexusBrokerTest";

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FixedGate(bool allowed) : IExecutionGate
    {
        public int Calls { get; private set; }
        public GateDecision Evaluate() { Calls++; return new(allowed, allowed ? "teste: liberado" : "teste: bloqueado"); }
    }

    /// <summary>Catálogo de teste: só alvos HKCU de teste; uma ação persistente, uma de sessão e uma com lista de valores.</summary>
    private static CatalogDocument Catalog() => CatalogLoader.Parse($$"""
        {
          "catalogVersion": "teste-1", "schema": "nexus.catalog/1",
          "actions": [
            { "id": "t.persist", "version": 1, "title": "Persistente", "classification": "SupportedDocumented", "handlerKind": "RegistryValue",
              "target": { "hive": "HKCU", "path": "{{P.Replace("\\", "\\\\")}}", "name": "Persist", "type": "DWord" }, "desiredValue": 1,
              "privileges": "User", "persistence": "Persistent", "reboot": "None" },
            { "id": "t.session", "version": 1, "title": "Sessão", "classification": "SupportedDocumented", "handlerKind": "RegistryValue",
              "target": { "hive": "HKCU", "path": "{{P.Replace("\\", "\\\\")}}", "name": "Session", "type": "DWord" }, "desiredValue": 1,
              "privileges": "User", "persistence": "Session", "reboot": "None" },
            { "id": "t.choice", "version": 1, "title": "Escolha", "classification": "Experimental", "handlerKind": "RegistryValue",
              "target": { "hive": "HKCU", "path": "{{P.Replace("\\", "\\\\")}}", "name": "Choice", "type": "DWord" }, "allowedValues": [2, 38],
              "privileges": "User", "persistence": "Persistent", "reboot": "None" }
          ]
        }
        """);

    private (BrokerService Service, FileRunCoordinator Coordinator, FixedGate Gate) Broker(bool allowed)
    {
        var coordinator = new FileRunCoordinator(_dir);
        var gate = new FixedGate(allowed);
        var service = new BrokerService(Catalog(), new HandlerRegistry().Add(new RegistryValueHandler(_reg)), _journal, coordinator, gate,
            () => T.Ctx, () => new Dictionary<string, string>());
        return (service, coordinator, gate);
    }

    private BrokerService ServiceMode()
    {
        var coordinator = new FileRunCoordinator(_dir);
        return new BrokerService(Catalog(), new HandlerRegistry().Add(new RegistryValueHandler(_reg)), _journal, coordinator, new FixedGate(true),
            () => T.Ctx, () => new Dictionary<string, string>(), bindCallerProfile: true);
    }

    private const string SidA = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SidB = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private string? UserValue(string sid, string name) => _reg.Read(T.RT(P, name) with { UserSid = sid }).Value;

    // ---------------------------------------------------------------- B10: broker como serviço (perfil de quem pediu)

    [Fact]
    public void Service_mode_writes_hkcu_actions_to_the_callers_profile_and_records_the_sid()
    {
        using var service = ServiceMode();
        var r = service.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")), SidA);

        Assert.True(r.Ok, r.Error?.Detail);
        Assert.Equal("1", UserValue(SidA, "Persist"));
        Assert.Null(Value("Persist"));               // nada no HKCU do processo (que, como serviço, seria o do SYSTEM)
        Assert.Null(UserValue(SidB, "Persist"));
        var entry = Assert.Single(_journal.Entries(r.RunId!.Value));
        Assert.Equal(SidA, entry.Action.Target[UserProfileScope.TargetKey]); // o perfil fica no diário
    }

    [Theory]
    [InlineData(null)]
    [InlineData("S-1-5-18")]                                        // SYSTEM
    [InlineData("S-1-5-21-1-2-3")]                                  // incompleto
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1001x")]
    public void Service_mode_refuses_hkcu_actions_when_the_caller_is_not_a_user_account(string? sid)
    {
        using var service = ServiceMode();
        var r = service.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")), sid);

        Assert.False(r.Ok);
        Assert.Equal("caller-unknown", r.Error!.Code);
        Assert.Empty(_journal.Runs());
        Assert.Null(Value("Persist"));
    }

    [Theory]
    [InlineData("HKLM", SidA, "só se aplica a HKCU")]
    [InlineData("HKCU", "S-1-5-18", "SID de usuário inválido")]
    [InlineData("HKCU", ".DEFAULT", "SID de usuário inválido")]
    public void Registry_handler_blocks_profile_targets_outside_hkcu_or_with_non_user_sids(string hive, string sid, string expected)
    {
        var h = new RegistryValueHandler(_reg);
        var a = T.RegAction("x", P, "V", 1) with
        {
            Target = new Dictionary<string, string> { ["hive"] = hive, ["path"] = P, ["name"] = "V", ["type"] = "DWord", [UserProfileScope.TargetKey] = sid },
        };

        var assess = h.Assess(a, T.Ctx);
        Assert.Equal(ApplicabilityKind.Blocked, assess.Kind);
        Assert.Contains(expected, assess.Reason, StringComparison.Ordinal);
        Assert.Contains(expected, h.CheckRestore(a, StateSnapshot.Of("DWord", "0")), StringComparison.Ordinal);
    }

    [Fact]
    public void Revert_by_another_user_still_restores_the_original_profile()
    {
        using var service = ServiceMode();
        _reg.Seed(T.RT(P, "Persist") with { UserSid = SidA }, "DWord", "0");
        var exec = service.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")), SidA);
        Assert.Equal("1", UserValue(SidA, "Persist"));

        var revert = service.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, null, RunId: exec.RunId), SidB);

        Assert.True(revert.FullyRestored, string.Join("; ", revert.Results?.Select(x => x.Detail) ?? []));
        Assert.Equal("0", UserValue(SidA, "Persist")); // voltou ao perfil de quem aplicou
        Assert.Null(UserValue(SidB, "Persist"));       // o perfil de quem pediu a reversão não foi tocado
    }

    [Fact]
    public void Service_mode_refuses_to_restore_a_hkcu_entry_without_recorded_profile()
    {
        // Entrada criada pelo broker em modo console (HKCU do processo). Como serviço, restaurá-la iria para o SYSTEM.
        using (var console = Broker(allowed: true).Service)
            Assert.True(console.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1"))).Ok);
        var run = _journal.Runs().Single().RunId;

        using var service = ServiceMode();
        var r = service.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, null, RunId: run), SidA);

        var item = Assert.Single(r.Results!);
        Assert.Equal("Failed", item.Outcome);
        Assert.Contains("perfil do SYSTEM", item.Detail, StringComparison.Ordinal);
        Assert.Equal("1", Value("Persist"));          // nada foi escrito
        Assert.Null(UserValue(SidA, "Persist"));
    }

    [Fact]
    public void Tampered_user_sid_in_the_journal_is_refused()
    {
        using var service = ServiceMode();
        var exec = service.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")), SidA);
        var entry = _journal.Entries(exec.RunId!.Value).Single();
        var forged = new InMemoryJournal();
        var run = forged.BeginRun("p", "teste-1");
        var target = new Dictionary<string, string>(entry.Action.Target) { [UserProfileScope.TargetKey] = "S-1-5-18" };
        var id = forged.AddPlanned(run, 0, entry.Action with { Target = target });
        forged.Transition(id, JournalState.BackedUp, original: StateSnapshot.Absent());
        forged.Transition(id, JournalState.Applying);
        _reg.Seed(T.RT(P, "Persist") with { UserSid = "S-1-5-18" }, "DWord", "1"); // restaurar exigiria escrever

        var handlers = CatalogBoundHandler.Wrap(new HandlerRegistry().Add(new RegistryValueHandler(_reg)), [RegistryValueHandler.Kind], Catalog(),
            () => new Dictionary<string, string>(), requireUserProfile: true);
        var result = new ActionExecutor(handlers, forged).Revert(id, force: true);

        Assert.Equal(RevertOutcome.Failed, result.Outcome);
        Assert.Contains("Perfil de usuário inválido", result.Detail, StringComparison.Ordinal);
        Assert.Equal("1", _reg.Read(T.RT(P, "Persist") with { UserSid = "S-1-5-18" }).Value); // nada escrito
    }

    private static BrokerRequest Req(BrokerOperation op, params ActionRequest[] actions) =>
        new(1, Guid.NewGuid(), op, actions.Length == 0 ? null : actions);

    private string? Value(string name) => _reg.Read(T.RT(P, name)).Value;

    // ---------------------------------------------------------------- portão

    [Fact]
    public void Vm_gate_requires_a_virtual_machine_and_a_marker_naming_this_machine()
    {
        var marker = Path.Combine(_dir, VmOnlyExecutionGate.MarkerFileName);
        Assert.False(new VmOnlyExecutionGate(() => false, marker, "VM1").Evaluate().Allowed);   // PC físico
        File.WriteAllText(marker, "VM1");
        Assert.False(new VmOnlyExecutionGate(() => false, marker, "VM1").Evaluate().Allowed);   // físico, mesmo com o arquivo
        Assert.False(new VmOnlyExecutionGate(() => null, marker, "VM1").Evaluate().Allowed);    // VM não confirmada
        Assert.False(new VmOnlyExecutionGate(() => throw new InvalidOperationException(), marker, "VM1").Evaluate().Allowed);
        Assert.False(new VmOnlyExecutionGate(() => true, marker, "OUTRA").Evaluate().Allowed);  // arquivo copiado de outra máquina
        File.Delete(marker);
        Assert.False(new VmOnlyExecutionGate(() => true, marker, "VM1").Evaluate().Allowed);    // VM sem liberação
        File.WriteAllText(marker, "vm1\nqualquer coisa");
        Assert.True(new VmOnlyExecutionGate(() => true, marker, "VM1").Evaluate().Allowed);
    }

    [Fact]
    public void Blocked_gate_means_execute_touches_nothing_no_lock_no_journal_no_system()
    {
        var (svc, coordinator, gate) = Broker(allowed: false);

        var r = svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")));

        Assert.False(r.Ok);
        Assert.Equal("execution-blocked", r.Error!.Code);
        Assert.Equal(1, gate.Calls);
        Assert.Empty(_journal.Runs());
        Assert.Null(Value("Persist"));
        Assert.False(File.Exists(Path.Combine(_dir, FileRunCoordinator.LockFileName))); // nem tentou pegar a trava
        foreach (var op in new[] { BrokerOperation.Recover })
            Assert.Equal("execution-blocked", svc.Handle(new BrokerRequest(1, Guid.NewGuid(), op)).Error!.Code);
        Assert.Equal("execution-blocked", svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: Guid.NewGuid())).Error!.Code);
    }

    [Fact]
    public void Reads_and_simulation_work_with_the_gate_blocked()
    {
        _reg.Seed(T.RT(P, "Persist"), "DWord", "0");
        var (svc, _, _) = Broker(allowed: false);

        var read = svc.Handle(Req(BrokerOperation.ReadState, new ActionRequest("t.persist@1")));
        Assert.True(read.Ok);
        Assert.Equal("DWord:0", Assert.Single(read.Results!).Observed);

        var plan = svc.Handle(Req(BrokerOperation.Plan, new ActionRequest("t.persist@1")));
        Assert.True(plan.Ok);
        Assert.Equal(nameof(PlanStatus.Pending), Assert.Single(plan.Results!).Outcome);
        Assert.Equal("0", Value("Persist"));

        var status = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Status)).Status!;
        Assert.False(status.ExecutionAllowed);
        Assert.Equal("teste-1", status.CatalogVersion);
    }

    // ---------------------------------------------------------------- validação

    [Theory]
    [InlineData("nao.existe@1", null, "unknown-action")]
    [InlineData("t.persist@2", null, "unknown-action")]          // versão errada
    [InlineData("t.persist@1", "7", "invalid-value")]            // valor fixo no catálogo
    [InlineData("t.choice@1", null, "invalid-value")]            // exige escolha
    [InlineData("t.choice@1", "40", "invalid-value")]            // fora da lista
    public void Requests_are_validated_against_the_broker_catalog(string id, string? value, string code)
    {
        var (svc, _, _) = Broker(allowed: true);
        var r = svc.Handle(Req(BrokerOperation.Execute, new ActionRequest(id, value)));
        Assert.False(r.Ok);
        Assert.Equal(code, r.Error!.Code);
        Assert.Empty(_journal.Runs());
    }

    [Fact]
    public void Repeated_or_too_many_actions_and_wrong_version_are_refused()
    {
        var (svc, _, _) = Broker(allowed: true);
        Assert.Equal("invalid-request", svc.Handle(Req(BrokerOperation.Plan, new ActionRequest("t.persist@1"), new ActionRequest("t.persist@1"))).Error!.Code);
        Assert.Equal("invalid-request", svc.Handle(Req(BrokerOperation.Plan,
            Enumerable.Range(0, BrokerService.MaxActionsPerRequest + 1).Select(i => new ActionRequest($"x{i}@1")).ToArray())).Error!.Code);
        Assert.Equal("unsupported-version", svc.Handle(new BrokerRequest(2, Guid.NewGuid(), BrokerOperation.Ping)).Error!.Code);
    }

    [Theory]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"Execute","actions":[{"actionId":"t.persist@1","target":"HKLM\\SYSTEM"}]}""")]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"Execute","path":"HKLM\\SAM"}""")]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":4}""")]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"RunCommand"}""")]
    public void Wire_decoder_refuses_unknown_fields_numeric_enums_and_unknown_operations(string json) =>
        Assert.Throws<BrokerProtocolException>(() => BrokerWire.Decode<BrokerRequest>(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Wire_refuses_oversized_frames_without_reading_them()
    {
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, BrokerWire.MaxFrameBytes + 1);
        Assert.Throws<BrokerProtocolException>(() => BrokerWire.ReadFrame(new MemoryStream(header)));
    }

    // ---------------------------------------------------------------- execução liberada

    [Fact]
    public void Allowed_execute_applies_verifies_ends_the_run_and_releases_the_lock()
    {
        var (svc, coordinator, _) = Broker(allowed: true);

        var r = svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1"), new ActionRequest("t.choice@1", "38")));

        Assert.True(r.Ok, r.Error?.Detail);
        Assert.All(r.Results!, x => Assert.Equal(nameof(ExecutionOutcome.Verified), x.Outcome));
        Assert.False(r.SessionActive);
        Assert.Equal(("1", "38"), (Value("Persist"), Value("Choice")));
        Assert.NotNull(Assert.Single(_journal.Runs()).EndedAt);
        Assert.Equal(CoordinationState.Idle, coordinator.Probe().State);
    }

    [Fact]
    public void Session_keeps_the_lock_blocks_other_executors_and_end_session_reverts_it()
    {
        var (svc, coordinator, _) = Broker(allowed: true);

        var r = svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.session@1")));
        Assert.True(r.Ok, r.Error?.Detail);
        Assert.True(r.SessionActive);
        Assert.Equal("1", Value("Session"));

        // Durante a sessão: outro executor (ex.: nexus-recover) não consegue a trava; o broker recusa nova execução.
        Assert.Equal(LeaseAcquireStatus.HeldByOther, new FileRunCoordinator(_dir).TryAcquire("cli", TimeSpan.FromMilliseconds(200)).Status);
        Assert.Equal("session-active", svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1"))).Error!.Code);
        Assert.Equal("session-active", svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Recover)).Error!.Code);
        Assert.Equal(r.RunId, svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Status)).Status!.ActiveSessionRunId);
        Assert.Equal("no-such-session", svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.EndSession, RunId: Guid.NewGuid())).Error!.Code);

        var end = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.EndSession, RunId: r.RunId));
        Assert.True(end.Ok, end.Error?.Detail);
        Assert.True(end.FullyRestored);
        Assert.Null(Value("Session"));
        Assert.Equal(CoordinationState.Idle, coordinator.Probe().State);
        Assert.NotNull(Assert.Single(_journal.Runs()).EndedAt);
    }

    [Fact]
    public void Broker_that_stops_with_an_open_session_leaves_it_for_recovery()
    {
        var (svc, coordinator, _) = Broker(allowed: true);
        Assert.True(svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.session@1"))).SessionActive);
        svc.Dispose(); // equivalente a parar o serviço (ou o processo morrer)

        var (svc2, _, _) = Broker(allowed: true);
        Assert.Equal("recovery-required", svc2.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1"))).Error!.Code);
        var rec = svc2.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Recover));
        Assert.True(rec.FullyRestored, string.Join("; ", rec.Results!.Select(x => x.Detail)));
        Assert.Null(Value("Session"));
        Assert.True(svc2.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1"))).Ok);
    }

    [Fact]
    public void Execute_is_refused_while_another_executor_holds_the_lock()
    {
        var (svc, _, _) = Broker(allowed: true);
        using var other = new FileRunCoordinator(_dir).TryAcquire("interface").Lease!;
        var r = svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")));
        Assert.Equal("busy", r.Error!.Code);
        Assert.Null(Value("Persist"));
    }

    // ---------------------------------------------------------------- transporte real (named pipe)

    [Fact]
    public async Task Pipe_server_passes_the_transport_identity_not_request_content_to_the_service()
    {
        using var service = ServiceMode();
        var name = "nexus-test-" + Guid.NewGuid().ToString("N")[..12];
        var identified = 0;
        var server = new BrokerPipeServer(service, first => BrokerPipeServer.CurrentUserPipe(name, first))
        {
            IdentifyClient = _ => { Interlocked.Increment(ref identified); return SidA; },
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = server.RunAsync(cancel.Token);

        using (var client = BrokerClient.Connect(name, 10_000))
        {
            Assert.True(client.Send(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Ping)).Ok);
            var exec = client.Send(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")));
            Assert.True(exec.Ok, exec.Error?.Detail);
        }

        cancel.Cancel();
        await running;
        Assert.Equal(1, identified);                  // uma vez por conexão, depois do primeiro quadro
        Assert.Equal("1", UserValue(SidA, "Persist"));
        Assert.Null(Value("Persist"));
    }

    [Fact]
    public void Service_owner_check_accepts_only_system_or_administrators()
    {
        if (!OperatingSystem.IsWindows()) return; // ACL de pipe só existe no Windows
        Assert.True(BrokerClient.IsServiceOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null)));
        Assert.True(BrokerClient.IsServiceOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(BrokerClient.IsServiceOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(BrokerClient.IsServiceOwner(new System.Security.Principal.SecurityIdentifier(SidA)));
        Assert.False(BrokerClient.IsServiceOwner(null));
    }

    [Fact]
    public async Task Client_in_service_mode_refuses_a_pipe_owned_by_an_ordinary_user()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        // Processo elevado: o dono padrão dos objetos criados é Administradores, que o cliente aceita por definição. O cenário
        // do impostor (usuário comum) só pode ser reproduzido sem elevação.
        if (new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return;

        var (svc, _, _) = Broker(allowed: true);
        var name = "nexus-test-" + Guid.NewGuid().ToString("N")[..12];
        var server = new BrokerPipeServer(svc, first => new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut,
            BrokerPipeServer.MaxInstances, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = server.RunAsync(cancel.Token);

        var ex = Assert.Throws<UnauthorizedAccessException>(() => BrokerClient.Connect(name, 10_000, BrokerServerKind.Service));
        Assert.Contains("impostor", ex.Message, StringComparison.Ordinal);

        cancel.Cancel();
        await running;
        Assert.Empty(_journal.Runs());
    }

    [Fact]
    public async Task Named_pipe_round_trip_and_malformed_frames()
    {
        var (svc, _, _) = Broker(allowed: false);
        var name = "nexus-test-" + Guid.NewGuid().ToString("N")[..12];
        var server = new BrokerPipeServer(svc, first => BrokerPipeServer.CurrentUserPipe(name, first));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = server.RunAsync(cancel.Token);

        using (var client = BrokerClient.Connect(name, 10_000))
        {
            Assert.Equal("pong", client.Send(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Ping)).Message);
            var exec = client.Send(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")));
            Assert.Equal("execution-blocked", exec.Error!.Code);
            Assert.True(client.Send(Req(BrokerOperation.Plan, new ActionRequest("t.persist@1"))).Ok);
        }

        using (var client = BrokerClient.Connect(name, 10_000))
        {
            var body = Encoding.UTF8.GetBytes("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"Execute","target":"HKLM\\SAM"}""");
            var frame = new byte[4 + body.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
            body.CopyTo(frame, 4);
            Assert.Equal("protocol-error", client.SendRaw(frame)!.Error!.Code);
        }

        using (var client = BrokerClient.Connect(name, 10_000))
        {
            var huge = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(huge, uint.MaxValue);
            Assert.Equal("protocol-error", client.SendRaw(huge)!.Error!.Code);
        }

        // O servidor continua atendendo depois de pedidos malformados.
        using (var client = BrokerClient.Connect(name, 10_000))
            Assert.True(client.Send(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Status)).Ok);

        cancel.Cancel();
        await running;
        Assert.Empty(_journal.Runs());
        Assert.Null(Value("Persist"));
    }

    // ---------------------------------------------------------------- achados da revisão independente do broker

    private long ForgedEntry(string key, string handler, Dictionary<string, string> target, string desired, string original)
    {
        var run = _journal.BeginRun("forjado", "1");
        var def = new ActionDefinition(key.Split('@')[0], int.Parse(key.Split('@')[1], System.Globalization.CultureInfo.InvariantCulture), "x",
            Classification.SupportedDocumented, handler, "x", [new EvidenceRef(EvidenceLevel.Hypothesis, "x")], "x",
            PrivilegeLevel.User, PersistenceKind.Persistent, RebootRequirement.None);
        var id = _journal.AddPlanned(run, 0, new ActionInstance(def, target, StateSnapshot.Of("DWord", desired)));
        _journal.Transition(id, JournalState.BackedUp, original: StateSnapshot.Of("DWord", original));
        _journal.Transition(id, JournalState.Applying);
        return id;
    }

    [Fact]
    public void Tampered_journal_entry_outside_the_catalog_is_never_restored_by_the_broker()
    {
        // Entrada forjada com chave inexistente no catálogo e alvo arbitrário (ControlSet001\...\Lsa, que escapava da lista).
        var lsa = new Dictionary<string, string> { ["hive"] = "HKLM", ["path"] = @"SYSTEM\ControlSet001\Control\Lsa", ["name"] = "RunAsPPL", ["type"] = "DWord" };
        var t = new RegistryTarget(RegHive.HKLM, @"SYSTEM\ControlSet001\Control\Lsa", "RunAsPPL", RegView.Default);
        _reg.Seed(t, "DWord", "1");
        var a = ForgedEntry("forjada@1", RegistryValueHandler.Kind, lsa, "1", "0");
        var (svc, _, _) = Broker(allowed: true);

        var rec = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Recover, Force: true));

        var r = Assert.Single(rec.Results!);
        Assert.Equal(nameof(RevertOutcome.Failed), r.Outcome);
        Assert.Contains("não existe no catálogo", r.Detail, StringComparison.Ordinal);
        Assert.False(rec.FullyRestored);
        Assert.Equal("1", _reg.Read(t).Value);                       // nada escrito
        Assert.Equal(JournalState.Applying, _journal.Get(a).State);  // continua visível
    }

    [Fact]
    public void Tampered_journal_entry_with_a_valid_key_but_another_target_is_never_restored()
    {
        var lsa = new Dictionary<string, string> { ["hive"] = "HKLM", ["path"] = @"SYSTEM\ControlSet001\Control\Lsa", ["name"] = "RunAsPPL", ["type"] = "DWord" };
        var t = new RegistryTarget(RegHive.HKLM, @"SYSTEM\ControlSet001\Control\Lsa", "RunAsPPL", RegView.Default);
        _reg.Seed(t, "DWord", "1");
        var b = ForgedEntry("t.persist@1", RegistryValueHandler.Kind, lsa, "1", "0");
        var (svc, _, _) = Broker(allowed: true);

        var r = Assert.Single(svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Recover, Force: true)).Results!);

        Assert.Equal(nameof(RevertOutcome.Failed), r.Outcome);
        Assert.Contains("difere do alvo definido pelo catálogo", r.Detail, StringComparison.Ordinal);
        Assert.Equal("1", _reg.Read(t).Value);
        Assert.Equal(JournalState.Applying, _journal.Get(b).State);
    }

    [Theory]
    [InlineData(@"SYSTEM\ControlSet001\Control\Lsa")]
    [InlineData(@"SYSTEM\ControlSet002\Services\WinDefend")]
    [InlineData(@"SOFTWARE\WOW6432Node\Policies\Microsoft")]
    [InlineData(@"SYSTEM\controlset001\Control\DeviceGuard")]
    public void Safety_policy_normalizes_control_sets_and_wow64_views(string path) =>
        Assert.NotNull(Nexus.WindowsActions.SafetyPolicy.CheckRegistry("HKLM", path, "X"));

    [Fact]
    public void Conflicts_do_not_block_new_executions_but_interrupted_operations_do()
    {
        var (svc, _, _) = Broker(allowed: true);
        Assert.True(svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1"))).Ok);
        _reg.Write(T.RT(P, "Persist"), "DWord", "9"); // alteração externa
        var run = _journal.Runs().Single().RunId;
        var conflict = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.RevertRun, RunId: run));
        Assert.Equal(nameof(RevertOutcome.Conflict), Assert.Single(conflict.Results!).Outcome);

        // Conflito pendente é decisão do usuário: não impede outra execução.
        Assert.True(svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.choice@1", "2"))).Ok);
        Assert.Equal("9", Value("Persist"));
    }

    [Fact]
    public void Huge_owner_record_cannot_blow_up_responses()
    {
        var (svc, coordinator, _) = Broker(allowed: true);
        using var lease = new FileRunCoordinator(_dir).TryAcquire("outro").Lease!;
        var huge = lease.Holder with { Purpose = new string('x', 70_000) };
        File.WriteAllText(Path.Combine(_dir, FileRunCoordinator.OwnerFileName), System.Text.Json.JsonSerializer.Serialize(huge));

        var status = svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Status));
        var busy = svc.Handle(Req(BrokerOperation.Execute, new ActionRequest("t.persist@1")));

        Assert.True(BrokerWire.Encode(status).Length < BrokerWire.MaxFrameBytes);
        Assert.True(BrokerWire.Encode(busy).Length < BrokerWire.MaxFrameBytes);
        Assert.Equal("busy", busy.Error!.Code);
        Assert.Null(coordinator.ReadOwnerRecord()); // registro anômalo ignorado
    }

    [Fact]
    public void Null_or_oversized_action_fields_are_refused()
    {
        var (svc, _, _) = Broker(allowed: true);
        Assert.Equal("invalid-request", svc.Handle(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Plan, [null!])).Error!.Code);
        Assert.Equal("invalid-request", svc.Handle(Req(BrokerOperation.Plan, new ActionRequest(new string('a', 500)))).Error!.Code);
    }

    [Theory]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"execute"}""")]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"Ping, Execute"}""")]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"Ping","op":"Execute"}""")]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"v":1,"op":"Ping"}""")]
    [InlineData("""{"v":1,"id":"00000000-0000-0000-0000-000000000001","op":"Plan","actions":[{"value":"1"}]}""")]
    public void Wire_decoder_is_exact_about_names_duplicates_and_required_fields(string json) =>
        Assert.Throws<BrokerProtocolException>(() => BrokerWire.Decode<BrokerRequest>(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task Idle_client_does_not_block_other_clients()
    {
        var (svc, _, _) = Broker(allowed: false);
        var name = "nexus-test-" + Guid.NewGuid().ToString("N")[..12];
        var server = new BrokerPipeServer(svc, first => BrokerPipeServer.CurrentUserPipe(name, first));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = server.RunAsync(cancel.Token);

        using var idle = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut);
        idle.Connect(10_000);
        idle.Write([1, 0]); // meio cabeçalho e silêncio

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var other = BrokerClient.Connect(name, 10_000))
            Assert.Equal("pong", other.Send(new BrokerRequest(1, Guid.NewGuid(), BrokerOperation.Ping)).Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"segundo cliente esperou {sw.Elapsed}");

        cancel.Cancel();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(running, finished); // o servidor para mesmo com o cliente inativo conectado
    }

    [Fact]
    public async Task Server_refuses_to_start_when_the_pipe_name_is_already_taken()
    {
        var (svc, _, _) = Broker(allowed: false);
        var name = "nexus-test-" + Guid.NewGuid().ToString("N")[..12];
        await using var squatter = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var server = new BrokerPipeServer(svc, first => BrokerPipeServer.CurrentUserPipe(name, first));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // sem a recusa, o servidor ficaria tentando
        var ex = await Record.ExceptionAsync(() => server.RunAsync(limit.Token));
        Assert.True(ex is IOException or UnauthorizedAccessException, $"esperado recusar a partida; obtido: {ex?.GetType().Name ?? "nenhuma exceção (serviu ao lado do impostor)"}");
    }
}
