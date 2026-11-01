using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Nexus.Assistant;
using Nexus.Broker;

namespace Nexus.IntegrationTests.E2E;

/// <summary>
/// [Fact] dos testes de ponta a ponta: fora do Windows o teste é PULADO com o motivo (nada é lido nem escrito). No Windows, cada
/// teste chama <see cref="Vm.Require"/> antes de qualquer coisa: sem a liberação da VM descartável ele FALHA sem tocar no sistema.
/// </summary>
public sealed class WindowsE2EFactAttribute : FactAttribute
{
    public WindowsE2EFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Teste de ponta a ponta: só roda no Windows, na VM descartável do CI (.github/workflows/vm-integracao.yml). Nada foi executado.";
    }
}

/// <summary>
/// Os testes E2E rodam um de cada vez: todos usam o mesmo aplicador sob demanda (a trava dele recusaria um segundo pedido
/// simultâneo com "busy") e mexem no mesmo PC.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ESerial
{
    public const string Name = "Ponta a ponta (um de cada vez)";
}

/// <summary>Saída dos testes E2E: linhas no console do xUnit e num arquivo em NEXUS_E2E_OUT (o workflow publica como artifact).</summary>
public sealed class E2ELog : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private readonly StreamWriter _file;

    public E2ELog(Xunit.Abstractions.ITestOutputHelper output, string fileName)
    {
        _out = output;
        Directory = Environment.GetEnvironmentVariable("NEXUS_E2E_OUT") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "e2e-saida");
        System.IO.Directory.CreateDirectory(Directory);
        _file = new StreamWriter(Path.Combine(Directory, fileName), append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Directory { get; }

    public void Line(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {text}";
        lock (_file)
        {
            try { _out.WriteLine(line); }
            catch (InvalidOperationException) { } // saída do xUnit já fechada (linha tardia do aplicador)
            _file.WriteLine(line);
        }
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Uma chamada de escrita que o assistente fez: o que pediu, o que o aplicador simulou antes e o que respondeu.</summary>
public sealed record ApplyCall(AssistantStepId? Step, string Title, string Confirmation, BrokerLevel Level, IReadOnlyList<ActionRequest> Requests,
    BrokerCallResult PlanBefore, BrokerCallResult Result);

/// <summary>
/// O "host" do teste: no lugar da janela (IModuleHost.ApplyAsync/RevertAsync/PlanAsync) usa o APLICADOR REAL sob demanda,
/// exatamente como a interface (BrokerSession.ForThisPc, perfil "modulo"), e aprova toda confirmação automaticamente (o texto vai
/// para o log). Antes de cada escrita, pede ao mesmo aplicador uma simulação (Plan) para guardar o estado original de cada ação:
/// é contra ele que o "Desfazer tudo" é conferido.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerTestHost(E2ELog log) : IAssistantWriter
{
    public static string Sid => WindowsIdentity.GetCurrent().User!.Value;

    public AssistantStepId? CurrentStep { get; set; }
    public List<ApplyCall> Calls { get; } = [];
    public List<(Guid Run, BrokerLevel Level, BrokerCallResult Result)> Reverts { get; } = [];

    public BrokerSession Session(BrokerLevel level) =>
        BrokerSession.ForThisPc(level, AppContext.BaseDirectory, Sid, m => log.Line($"[aplicador {level}] {m}"));

    /// <summary>Mesmo papel de IModuleHost.PlanAsync (simulação, nada é escrito).</summary>
    public Task<BrokerCallResult> PlanAsync(IReadOnlyList<ActionRequest> requests, BrokerLevel level) => Task.Run(() => Session(level).Plan(requests));

    public async Task<WriteOutcome?> ApplyAsync(string title, string confirmation, IReadOnlyList<ActionRequest> requests, BrokerLevel level)
    {
        log.Line($"CONFIRMAÇÃO aprovada automaticamente: \"{title}\" ({requests.Count} ação(ões), {level})");
        foreach (var l in confirmation.Split('\n')) log.Line("    | " + l.TrimEnd('\r'));
        var plan = await PlanAsync(requests, level).ConfigureAwait(false);
        var r = await Task.Run(() => Session(level).Execute(requests, "modulo")).ConfigureAwait(false);
        log.Line($"[aplicar] {r.Message}");
        foreach (var x in r.Response?.Results ?? [])
            log.Line($"    {x.Outcome,-22} {x.ActionKey} {x.Target} {(x.Detail is null ? "" : "· " + x.Detail)}");
        Calls.Add(new ApplyCall(CurrentStep, title, confirmation, level, requests, plan, r));
        return WriteOutcome.From(r);
    }

    public async Task<WriteOutcome?> RevertAsync(Guid runId, BrokerLevel level)
    {
        var r = await Task.Run(() => Session(level).RevertRun(runId)).ConfigureAwait(false);
        log.Line($"[desfazer {runId} {level}] {r.Message}");
        foreach (var x in r.Response?.Results ?? [])
            log.Line($"    {x.Outcome,-22} {x.ActionKey} {x.Target} {(x.Detail is null ? "" : "· " + x.Detail)}");
        Reverts.Add((runId, level, r));
        return WriteOutcome.From(r);
    }
}
