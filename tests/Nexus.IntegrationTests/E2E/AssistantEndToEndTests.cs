using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nexus.Assistant;
using Nexus.Automation;
using Nexus.Boot.Inventory;
using Nexus.Boot.Windows;
using Nexus.Broker;
using Nexus.Core;
using Nexus.Persistence;
using Nexus.WindowsActions.Power;

namespace Nexus.IntegrationTests.E2E;

/// <summary>
/// O ASSISTENTE DE PONTA A PONTA, num Windows de verdade (VM descartável do CI, elevada), com as MESMAS classes que a página usa:
/// leitores (AssistantReaders), planejadores (StepPlanners), conferência com o aplicador (StepPlanners.PlanAsync), agrupamento e
/// aplicação (AssistantRunner/ApplyGrouping), estado salvo (AssistantStateStore/AssistantFlow), relatório (AssistantReport) e
/// "Desfazer tudo" (AssistantRunner.UndoAllAsync). No lugar da janela, <see cref="BrokerTestHost"/> chama o APLICADOR REAL sob
/// demanda (usuário e administrador) e aprova as confirmações.
///
/// Roteiro: semeia estado "sujo" conhecido (arquivos velhos no %TEMP%, uma entrada de inicialização de terceiros) → etapas 1 a 6 →
/// para no meio (estado salvo), recria tudo a partir do arquivo e retoma → etapas 7 a 12 → relê os números (depois) → relatório
/// HTML → "Desfazer tudo" → confere, ação por ação, que cada mudança voltou ao estado lido pelo próprio aplicador antes de aplicar
/// (Detect dos handlers via Plan), e que os arquivos/registro/tarefas semeados voltaram.
///
/// Política de escolha (o que um usuário faria clicando "Aplicar as recomendadas" e marcando os opcionais de risco baixo): tudo
/// o que o assistente propõe com risco baixo e não experimental (marcadas por padrão + opcionais de risco baixo), mais desligar
/// da inicialização o item semeado. Drivers do Windows Update ficam para DriverUpdatesEndToEndTests (instalação real separada).
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ESerial.Name)]
[SupportedOSPlatform("windows")]
public sealed partial class AssistantEndToEndTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SeedRunValue = "NexusE2EStartup";

    /// <summary>
    /// Recusas ACEITÁVEIS numa VM do CI (Windows Server sem Wi-Fi, bateria, GPU física nem Restauração do Sistema). Qualquer outra
    /// recusa ou falha do aplicador é defeito e reprova o teste.
    /// </summary>
    [GeneratedRegex(@"wi-?fi|sem fio|wlan|bateria|battery|windows server|esta edição do windows|máquina virtual|restaura[cç][aã]o do sistema|ponto de restaura|ponto novo|prote[cç][aã]o do sistema|nenhuma placa|sem placa de v[ií]deo|gpu|monitor|hags",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcceptableInVm();

    [GeneratedRegex(@"(\d+) de (\d+)")]
    private static partial Regex Score();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record StepLog(string Step, string Recommendation, IReadOnlyList<string> Facts, int Proposed, int DroppedByPlan, int Chosen, string Result,
        IReadOnlyList<string> Refusals);

    private sealed class Run
    {
        public required E2ELog Log { get; init; }
        public required BrokerTestHost Host { get; init; }
        public required AssistantCatalog Catalog { get; init; }
        public required Nexus.HardwareDiscovery.SystemFacts Facts { get; init; }
        public required string CatalogDir { get; init; }
        public List<string> Problems { get; } = [];
        public List<string> Accepted { get; } = [];
        public List<StepLog> Steps { get; } = [];
        public Dictionary<string, string> Measures { get; } = [];
        public string? StartupSeedExe { get; set; }
    }

    [WindowsE2EFact]
    public async Task Assistant_runs_every_step_applies_through_the_real_broker_reports_resumes_and_undoes_everything()
    {
        Vm.Require(); // PRIMEIRO: nada é lido, criado ou escrito fora da VM liberada
        Assert.True(Vm.Elevated, "O E2E precisa do processo elevado (a VM do CI é): as ações de administrador vão pelo aplicador elevado sob demanda.");
        using var log = new E2ELog(output, "assistente-e2e.log");
        var catalogDir = Path.Combine(AppContext.BaseDirectory, "catalog");
        var run = new Run
        {
            Log = log,
            Host = new BrokerTestHost(log),
            Catalog = AssistantCatalog.Load(catalogDir),
            Facts = Vm.Facts,
            CatalogDir = catalogDir,
        };
        log.Line($"Windows build {run.Facts.WindowsBuild} · {run.Facts.WindowsEdition} · VM={run.Facts.IsVirtualMachine} · elevado={run.Facts.IsElevated}");
        var statePath = Path.Combine(log.Directory, "assistente-estado.json");
        var store = new AssistantStateStore(statePath);
        store.Delete();

        // ---------------------------------------------------------------- estado inicial conhecido (semente)
        var seedDir = Path.Combine(Path.GetTempPath(), "nexus-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        var seeded = SeedTempFiles(seedDir);
        SeedStartupEntry(run);
        var tasksBefore = (Scan: TaskOn(NexusTaskKind.Scan), Clean: TaskOn(NexusTaskKind.Clean));
        var startupEnabledBefore = EnabledThirdPartyStartup(run.Facts);
        run.Measures["inicialização: terceiros ligados (antes)"] = startupEnabledBefore.ToString(CultureInfo.InvariantCulture);
        log.Line($"Semente: {seeded.Count} arquivo(s) com 3 dias em {seedDir}; entrada Run '{SeedRunValue}'; tarefas NEXUS antes: verificação={tasksBefore.Scan} limpeza={tasksBefore.Clean}");

        AssistantState? state = null;
        try
        {
            // ------------------------------------------------------------ 1–6, depois "fecha" o NEXUS no meio
            var (s0, status0) = store.Load();
            Assert.Equal(LoadStatus.New, status0);
            state = s0;
            var flow = new AssistantFlow(state);
            while (flow.Current <= AssistantStepId.Privacy)
                await DoStepAsync(run, flow, store, seeded);
            Assert.Equal(AssistantStepId.Security, state.Current);

            // Cleaning conferida no meio: os arquivos semeados saíram do lugar (quarentena).
            foreach (var f in seeded.Keys.Where(File.Exists))
                run.Problems.Add($"Limpeza: o arquivo semeado {Path.GetFileName(f)} continua no lugar depois de aplicar.");

            // ------------------------------------------------------------ retomada: recria tudo a partir do arquivo
            var savedRuns = state.Runs.Select(r => r.RunId).ToList();
            var savedChanges = state.Changes.Count;
            state = null; // "fecha o NEXUS": nada em memória sobrevive, só o arquivo
            flow = null!;
            var (resumed, status) = store.Load();
            log.Line($"Retomada: {status}, etapa atual {resumed.Current}, {resumed.Runs.Count} execução(ões), {resumed.Changes.Count} mudança(s) registradas.");
            Assert.Equal(LoadStatus.Resumed, status);
            Assert.Equal(AssistantStepId.Security, resumed.Current);
            Assert.Equal(savedRuns, resumed.Runs.Select(r => r.RunId).ToList());
            Assert.Equal(savedChanges, resumed.Changes.Count);
            state = resumed;
            flow = new AssistantFlow(state);
            Assert.Equal(6, flow.Completed);

            // ------------------------------------------------------------ 7–12
            while (flow.Current < AssistantStepId.Report)
                await DoStepAsync(run, flow, store, seeded);

            // ------------------------------------------------------------ relatório (13): relê o "depois" como a página
            await ReadAfterAsync(run, state);
            run.Measures["inicialização: terceiros ligados (depois)"] = EnabledThirdPartyStartup(run.Facts).ToString(CultureInfo.InvariantCulture);
            var pc = AssistantReaders.SummaryFor(run.Facts);
            var report = AssistantReport.From(state, pc, DateTimeOffset.Now, ReportRedactor.ForCurrentUser());
            var reportPath = report.Save(Path.Combine(log.Directory, "relatorio-assistente"));
            state.ReportPath = reportPath;
            flow.Advance("Relatório gerado.");
            store.Save(state);
            Assert.True(state.Finished);
            var html = await File.ReadAllTextAsync(reportPath);
            Assert.Contains("Relatório da otimização", html, StringComparison.Ordinal);
            Assert.Equal(12, report.Steps.Count);
            Assert.Equal(state.Changes.Count(c => c.Outcome == ChangeOutcome.Applied), report.AppliedCount);
            Assert.DoesNotContain(Environment.UserName, html, StringComparison.OrdinalIgnoreCase);
            log.Line($"Relatório: {reportPath} ({report.AppliedCount} aplicadas, {report.Skipped.Count} puladas, {report.Metrics.Count} números).");
            foreach (var m in report.Metrics) log.Line($"    número: {m.Label}: {m.Display}");
            CheckMetrics(run, state);
        }
        finally
        {
            // ------------------------------------------------------------ "Desfazer tudo" (sempre, mesmo se algo acima falhou)
            // O estado em memória (se o teste parou no meio de uma etapa, pode ter execução ainda não gravada); senão o arquivo.
            var final = state ?? (store.Exists ? store.Load().State : new AssistantState());
            var lines = await AssistantRunner.UndoAllAsync(final, run.Host);
            store.Save(final);
            foreach (var l in lines) log.Line("[desfazer tudo] " + l);
            if (final.HasUndoableRuns)
                run.Problems.Add("Desfazer tudo: sobrou execução não desfeita: " + string.Join(", ", final.Runs.Where(r => !r.Reverted).Select(r => $"{r.Step}/{r.RunId}")));
            await VerifyEverythingRevertedAsync(run);
            foreach (var (path, hash) in seeded)
            {
                if (!File.Exists(path)) run.Problems.Add($"Desfazer: o arquivo semeado {Path.GetFileName(path)} não voltou da quarentena.");
                else if (Hash(path) != hash) run.Problems.Add($"Desfazer: o arquivo semeado {Path.GetFileName(path)} voltou com conteúdo diferente.");
            }
            if (StartupApprovedDisabled()) run.Problems.Add($"Desfazer: a entrada de inicialização '{SeedRunValue}' continua desligada.");
            var tasksAfter = (Scan: TaskOn(NexusTaskKind.Scan), Clean: TaskOn(NexusTaskKind.Clean));
            if (tasksAfter != tasksBefore) run.Problems.Add($"Desfazer: tarefas agendadas do NEXUS não voltaram (antes {tasksBefore}, depois {tasksAfter}).");
            run.Measures["inicialização: terceiros ligados (depois de desfazer)"] = EnabledThirdPartyStartup(run.Facts).ToString(CultureInfo.InvariantCulture);
            if (run.Measures["inicialização: terceiros ligados (depois de desfazer)"] != run.Measures["inicialização: terceiros ligados (antes)"])
                run.Problems.Add("Desfazer: a contagem de itens de inicialização ligados não voltou ao valor inicial.");

            CleanupSeeds(seedDir);
            WriteSummary(run);
        }
        Assert.True(run.Problems.Count == 0, "Defeitos encontrados pelo E2E do assistente:\n- " + string.Join("\n- ", run.Problems));
    }

    // ==================================================================== uma etapa, como a página faz

    private static async Task DoStepAsync(Run run, AssistantFlow flow, AssistantStateStore store, IReadOnlyDictionary<string, string> seeded)
    {
        var state = flow.State;
        var step = flow.Current;
        var info = AssistantSteps.Info(step);
        run.Log.Line($"===== Etapa {info.Number}: {info.Title}");
        run.Host.CurrentStep = step;
        var scan = await ReadAsync(run, step);
        foreach (var f in scan.Facts) run.Log.Line($"    fato: {f.Label} = {f.Value} [{f.Tone}]{(f.Detail is null ? "" : " · " + f.Detail)}");
        foreach (var n in scan.Notes) run.Log.Line($"    nota: {n}");
        if (scan.Recommendation == StepScan.Unavailable("", "").Recommendation)
            run.Problems.Add($"Etapa {info.Number} ({info.ShortName}): leitura indisponível: {string.Join("; ", scan.Facts.Select(f => f.Detail))}");

        // Igual a AssistantPage.ScanAsync: só o permitido, conferido com o aplicador do usuário (nada é escrito).
        var proposed = scan.Changes.Where(ChangeSelection.IsAllowed).ToList();
        var extras = ExtraChoices(run, step);
        var all = proposed.Concat(extras).ToList();
        IReadOnlyList<ProposedChange> keep = all;
        var droppedCount = 0;
        if (all.Count > 0)
        {
            var (k, dropped, problem) = await StepPlanners.PlanAsync(all, q => run.Host.PlanAsync(q, BrokerLevel.User));
            keep = k;
            droppedCount = dropped.Count;
            foreach (var (c, why) in dropped) run.Log.Line($"    conferido e fora: {c.Title}: {why}");
            if (problem is not null) run.Problems.Add($"Etapa {info.Number}: aplicador do usuário indisponível para conferir: {problem}");
        }
        foreach (var (key, m) in scan.Metrics)
            if (!state.Metrics.TryGetValue(key, out var old) || old.Before is null) state.SetMetric(key, m.Label, before: m.Value);
        foreach (var u in scan.UserActions) state.AddUserAction(u);

        var chosen = keep.Where(c => Choose(c) || extras.Any(e => e.Id == c.Id)).ToList();
        var unselected = keep.Except(chosen).ToList();
        foreach (var c in keep)
            run.Log.Line($"    {(chosen.Contains(c) ? "[x]" : "[ ]")} {c.Title} · {c.Request.ActionId} · {c.LevelText} · risco {c.RiskText}{(c.OptIn ? " · opcional" : "")} · {c.BeforeAfter}");

        string result;
        var refusals = new List<string>();
        if (chosen.Count > 0)
        {
            var before = state.Changes.Count;
            var r = await AssistantRunner.ApplyAsync(state, step, chosen, run.Host);
            AssistantRunner.RecordUnselected(state, unselected);
            result = r.Summary;
            foreach (var rec in state.Changes.Skip(before).Where(c => c.Outcome is ChangeOutcome.Refused or ChangeOutcome.Canceled))
            {
                var line = $"{rec.Title}: {rec.Outcome}: {rec.Detail}";
                refusals.Add(line);
                if (rec.Outcome == ChangeOutcome.Refused && rec.Detail is { } d && AcceptableInVm().IsMatch(d) && !d.StartsWith("Interrupted", StringComparison.Ordinal))
                {
                    run.Accepted.Add($"Etapa {info.Number}: {line}");
                    run.Log.Line($"    recusa ACEITA (motivo de VM): {line}");
                }
                else
                {
                    run.Problems.Add($"Etapa {info.Number} ({info.ShortName}): {line}");
                    run.Log.Line($"    recusa NÃO ESPERADA: {line}");
                }
            }
            flow.Advance(r.Summary, scan.Facts);
        }
        else
        {
            if (keep.Count > 0) AssistantRunner.RecordUnselected(state, keep);
            result = scan.Recommendation;
            flow.Advance(keep.Count > 0 ? "Verificada; nada aplicado." : scan.Recommendation, scan.Facts);
        }
        store.Save(state);
        run.Log.Line($"    resultado: {result}");
        run.Steps.Add(new StepLog($"{info.Number}. {info.Title}", scan.Recommendation, scan.Facts.Select(f => $"{f.Label}: {f.Value}").ToList(),
            proposed.Count, droppedCount, chosen.Count, result, refusals));

        if (step == AssistantStepId.Cleaning)
            run.Log.Line($"    semente no lugar depois da limpeza: {seeded.Keys.Count(File.Exists)} de {seeded.Count}");
    }

    /// <summary>Risco baixo e não experimental: as marcadas por padrão e os opcionais de risco baixo (drivers ficam no outro teste).</summary>
    private static bool Choose(ProposedChange c) =>
        ChangeSelection.IsAllowed(c) && !c.Experimental && c.Risk == ActionRisk.Low && c.Request.ActionId != StepPlanners.DriverInstallAction;

    /// <summary>O usuário marca, na etapa Inicialização, desligar a entrada semeada (impacto não medido numa VM recém-criada).</summary>
    private static List<ProposedChange> ExtraChoices(Run run, AssistantStepId step)
    {
        if (step != AssistantStepId.Startup) return [];
        var item = ReadStartup(run.Facts).FirstOrDefault(i => i.Source == StartupSource.RunUser && i.EntryName == SeedRunValue);
        if (item is null)
        {
            run.Problems.Add($"Inicialização: a entrada semeada '{SeedRunValue}' não apareceu no inventário.");
            return [];
        }
        if (item.IsMicrosoft) run.Problems.Add($"Inicialização: a entrada semeada '{SeedRunValue}' foi classificada como da Microsoft.");
        if (StartupToggle(item) is not { } req) { run.Problems.Add("Inicialização: a entrada semeada não tem pedido de desligar."); return []; }
        var change = run.Catalog.Change(AssistantStepId.Startup, req, $"Não abrir com o Windows: {item.Name}", "Marcado pelo usuário (semente do E2E).", "abre no boot", "não abre sozinho");
        if (change is null) { run.Problems.Add($"Inicialização: {req.ActionId} não está no catálogo."); return []; }
        return [change];
    }

    // ==================================================================== leituras (mesmas da AssistantPage.ReadAsync)

    private static async Task<StepScan> ReadAsync(Run run, AssistantStepId step)
    {
        var c = run.Catalog;
        var f = run.Facts;
        var build = f.WindowsBuild ?? 0;
        switch (step)
        {
            case AssistantStepId.Welcome:
                return await Task.Run(() => AssistantReaders.Welcome(c, f.SystemDrive, PendingRecovery()));
            case AssistantStepId.MyPc:
            {
                var measured = await Task.Run(() => AssistantReaders.Measure(CancellationToken.None));
                run.Log.Line($"    medido: 1 núcleo {measured.SingleThreadGhz:0.00} GHz · RAM leitura {measured.ReadGbps:0.0} GB/s");
                if (measured.SingleThreadGhz is null && measured.ReadGbps is null) run.Problems.Add("Seu PC: as medições curtas (CPU e RAM) não devolveram nada.");
                var r = await Task.Run(() => AssistantReaders.MyPc(c, f, measured));
                run.Log.Line($"    perfil: {r.Summary.Kind} · {r.Summary.Cpu} · {r.Summary.Gpu} · {r.Summary.Ram} · {r.Summary.Windows} · {r.Summary.Disk}");
                return r.Scan;
            }
            case AssistantStepId.Cleaning:
                return (await Task.Run(() => AssistantReaders.CleaningAsync(c, CancellationToken.None))).Scan;
            case AssistantStepId.Programs:
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                try { return await Task.Run(() => AssistantReaders.ProgramsAsync(checkUpdates: true, cts.Token)); }
                catch (OperationCanceledException)
                {
                    run.Log.Line("    busca de atualizações passou de 4 min (winget na primeira execução); lendo só o inventário, como a página sem o botão.");
                    return await Task.Run(() => AssistantReaders.ProgramsAsync(checkUpdates: false, CancellationToken.None));
                }
            }
            case AssistantStepId.Startup:
                return await Task.Run(() => AssistantReaders.Startup(c, StartupToggle, run.CatalogDir));
            case AssistantStepId.Privacy:
                return await Task.Run(() => AssistantReaders.Privacy(c, run.CatalogDir, build));
            case AssistantStepId.Security:
                return await Task.Run(AssistantReaders.Security);
            case AssistantStepId.Drivers:
            {
                // Como o botão "Procurar no Windows Update" da etapa (busca real, com o tempo limite da página).
                var scan = await Task.Run(() => AssistantReaders.Drivers(c, searchWindowsUpdate: true, CancellationToken.None));
                var reading = await Task.Run(() => Nexus.DriverUpdates.DriverUpdatesReader.Scan(TimeSpan.Zero, CancellationToken.None, searchUpdates: false));
                run.Measures["dispositivos lidos"] = reading.DeviceCount.ToString(CultureInfo.InvariantCulture);
                run.Measures["dispositivos sem driver/com falha"] = reading.NeedingDriver.Count.ToString(CultureInfo.InvariantCulture);
                if (!reading.ProblemCodesRead) run.Problems.Add("Drivers: o código de problema dos dispositivos não foi lido (CM_Get_DevNode_Status).");
                foreach (var p in reading.Problems) run.Log.Line($"    dispositivo com problema: {p.Name} · {p.CodeText} · {p.Kind} · {p.HardwareId}");
                return scan;
            }
            case AssistantStepId.Memory:
                return await Task.Run(() => AssistantReaders.Memory(c, f));
            case AssistantStepId.Performance:
            {
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                try { parameters["activeScheme"] = new WindowsPowerBackend().GetActiveScheme().ToString("D"); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { run.Log.Line("    esquema de energia não lido: " + ex.Message); }
                return await Task.Run(() => AssistantReaders.Performance(c, f, parameters));
            }
            case AssistantStepId.Measurement:
                // Só preferências do próprio NEXUS (MeasurePage, WPF); não escreve no Windows. Coberto pelos testes unitários.
                return StepPlanners.Measurement(presentMonFound: false, autoMeasure: false, overlayOnGameStart: false);
            case AssistantStepId.Schedules:
                return await Task.Run(() => AssistantReaders.Schedules(c));
            default:
                return StepScan.Empty("");
        }
    }

    /// <summary>Mesma regra da página Inicialização (StartupPage.ToggleRequest, Desktop): desligar = valor "disabled".</summary>
    internal static ActionRequest? StartupToggle(StartupItem i)
    {
        (string Key, Dictionary<string, string> Args)? t = i.Source switch
        {
            StartupSource.RunUser => ("boot.startup.user.run@1", new() { ["entryName"] = i.EntryName }),
            StartupSource.RunMachine => ("boot.startup.machine.run@1", new() { ["entryName"] = i.EntryName }),
            StartupSource.RunMachine32 => ("boot.startup.machine.run32@1", new() { ["entryName"] = i.EntryName }),
            StartupSource.StartupFolderUser => ("boot.startupfolder.user@1", new() { ["fileName"] = i.EntryName }),
            StartupSource.StartupFolderCommon => ("boot.startupfolder.common@1", new() { ["fileName"] = i.EntryName }),
            StartupSource.ScheduledTask when !i.IsMicrosoft => i.TaskNeedsAdmin
                ? ("boot.task.admin@1", new() { ["taskPath"] = i.EntryName })
                : ("boot.task.user@1", new() { ["taskPath"] = i.EntryName }),
            _ => null,
        };
        return t is { } x ? new ActionRequest(x.Key, "disabled", x.Args) : null;
    }

    private static IReadOnlyList<StartupItem> ReadStartup(Nexus.HardwareDiscovery.SystemFacts facts)
    {
        _ = facts;
        var boot = WindowsBootEventReader.Read();
        return new StartupInventory(new WindowsBootRegistryReader(), new WindowsFileInspector(), Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .Read(WindowsStartupFolders.Current(), new WindowsTaskScheduler(), new WindowsServiceReader(), boot).Items;
    }

    private static int EnabledThirdPartyStartup(Nexus.HardwareDiscovery.SystemFacts facts) =>
        ReadStartup(facts).Count(i => !i.IsMicrosoft && i.Enabled && i.Source != StartupSource.Service);

    /// <summary>Mesma leitura da AssistantPage.PendingRecovery (somente leitura dos dois diários).</summary>
    private static int PendingRecovery()
    {
        static int Count(string path)
        {
            if (!File.Exists(path)) return 0;
            using var j = SqliteJournal.OpenReadOnly(path);
            return RecoveryService.FindInterrupted(j).Count(f => f.Entry.State is JournalState.BackedUp or JournalState.Applying or JournalState.Applied or JournalState.Restoring);
        }
        return Count(LocalBroker.JournalPath(BrokerLevel.User)) + Count(LocalBroker.JournalPath(BrokerLevel.Admin));
    }

    // ==================================================================== depois, números e conferência do desfazer

    /// <summary>Mesmas leituras da AssistantPage.BuildReportAsync para o "depois".</summary>
    private static async Task ReadAfterAsync(Run run, AssistantState state)
    {
        var c = run.Catalog;
        var build = run.Facts.WindowsBuild ?? 0;
        var scans = new List<StepScan>
        {
            await Task.Run(() => AssistantReaders.Welcome(c, run.Facts.SystemDrive, 0)),
            (await Task.Run(() => AssistantReaders.CleaningAsync(c, CancellationToken.None))).Scan,
            await Task.Run(() => AssistantReaders.Startup(c, StartupToggle, run.CatalogDir)),
            await Task.Run(() => AssistantReaders.Privacy(c, run.CatalogDir, build)),
            await Task.Run(AssistantReaders.Security),
        };
        foreach (var (key, m) in scans.SelectMany(s => s.Metrics))
            if (state.Metrics.ContainsKey(key)) state.SetMetric(key, m.Label, after: m.Value);
    }

    private static void CheckMetrics(Run run, AssistantState state)
    {
        foreach (var (k, m) in state.Metrics) run.Measures[$"{k}: {m.Label}"] = m.Display;
        var privacyApplied = state.Changes.Any(c => c.Step == AssistantStepId.Privacy && c.Outcome == ChangeOutcome.Applied);
        if (state.Metrics.TryGetValue("privacy", out var p) && Parse(p.Before) is { } b && Parse(p.After) is { } a)
        {
            if (privacyApplied && a.Protected <= b.Protected)
                run.Problems.Add($"Privacidade: aplicou itens mas o placar não subiu ({p.Display}).");
        }
        else if (privacyApplied) run.Problems.Add("Privacidade: placar antes/depois não foi medido.");
        if (state.Changes.Any(c => c.Step == AssistantStepId.Cleaning && c.Outcome == ChangeOutcome.Applied) &&
            !(state.Metrics.TryGetValue("cleaning", out var cl) && cl.Before is not null && cl.After is not null))
            run.Problems.Add("Limpeza: espaço recuperável antes/depois não foi medido.");
    }

    private static (int Protected, int Total)? Parse(string? s) =>
        s is not null && Score().Match(s) is { Success: true } m ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;

    /// <summary>
    /// Para cada ação que o aplicador confirmou (Verified), o estado lido pelo próprio aplicador ANTES de aplicar (Plan → Observed)
    /// tem de ser o estado lido agora, depois de "Desfazer tudo". Ações efêmeras (ponto de restauração) não têm o que voltar.
    /// Quando a mesma ação aparece em mais de uma execução, vale o primeiro estado (o original do PC).
    /// </summary>
    private static async Task VerifyEverythingRevertedAsync(Run run)
    {
        var original = new Dictionary<string, (ActionRequest Request, BrokerLevel Level, string Observed, string Title)>(StringComparer.Ordinal);
        foreach (var call in run.Host.Calls)
        {
            var results = call.Result.Response?.Results ?? [];
            var planned = call.PlanBefore.Response?.Results ?? [];
            for (var i = 0; i < call.Requests.Count; i++)
            {
                var req = call.Requests[i];
                if (results.FirstOrDefault(x => x.Index == i) is not { Outcome: "Verified" }) continue;
                if (run.Catalog.Entry(req.ActionId)?.Definition.Persistence == PersistenceKind.Ephemeral) continue;
                var sig = ChangeSelection.Signature(req);
                if (original.ContainsKey(sig)) continue;
                if (planned.FirstOrDefault(x => x.Index == i) is { Outcome: "Pending", Observed: { } observed })
                    original[sig] = (req, call.Level, observed, call.Title);
                else
                    run.Problems.Add($"Desfazer: sem estado original simulado para {req.ActionId} ({call.Title}); a simulação disse {planned.FirstOrDefault(x => x.Index == i)?.Outcome ?? "nada"}.");
            }
        }
        run.Log.Line($"===== Conferência do desfazer: {original.Count} ação(ões) aplicadas e verificáveis");
        foreach (var group in original.Values.GroupBy(o => o.Level))
        {
            var items = group.ToList();
            foreach (var chunk in items.Chunk(ApplyGrouping.MaxActionsPerRequest))
            {
                var r = await run.Host.PlanAsync(chunk.Select(x => x.Request).ToList(), group.Key);
                var results = r.Response?.Results ?? [];
                for (var i = 0; i < chunk.Length; i++)
                {
                    var now = results.FirstOrDefault(x => x.Index == i);
                    var ok = now?.Observed == chunk[i].Observed;
                    run.Log.Line($"    {(ok ? "voltou" : "NÃO VOLTOU"),-11} {chunk[i].Request.ActionId} {ArgsText(chunk[i].Request)} · original {chunk[i].Observed} · agora {now?.Observed ?? now?.Outcome ?? r.Message}");
                    if (!ok)
                        run.Problems.Add($"Desfazer: {chunk[i].Request.ActionId} {ArgsText(chunk[i].Request)} não voltou ao original ({chunk[i].Observed}); agora: {now?.Observed ?? $"{now?.Outcome}: {now?.Detail}"}.");
                }
            }
        }
    }

    private static string ArgsText(ActionRequest r) =>
        r.Arguments is null ? (r.Value ?? "") : string.Join(" ", r.Arguments.Select(kv => $"{kv.Key}={kv.Value}")) + (r.Value is null ? "" : $" valor={r.Value}");

    // ==================================================================== semente e limpeza

    private static Dictionary<string, string> SeedTempFiles(string dir)
    {
        Directory.CreateDirectory(dir);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var old = DateTime.UtcNow.AddDays(-3);
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(dir, $"cache-{i}.tmp");
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(256 * 1024));
            File.SetCreationTimeUtc(path, old);
            File.SetLastWriteTimeUtc(path, old);
            File.SetLastAccessTimeUtc(path, old);
            files[path] = Hash(path);
        }
        Directory.SetLastWriteTimeUtc(dir, old);
        return files;
    }

    private static void SeedStartupEntry(Run run)
    {
        // Executável de terceiros (sem assinatura da Microsoft) que existe: o próprio aplicador do NEXUS da pasta do teste.
        var exe = Path.Combine(AppContext.BaseDirectory, "nexus-broker.exe");
        Assert.True(File.Exists(exe), "nexus-broker.exe ausente na pasta do teste (CopyBrokerHost).");
        run.StartupSeedExe = exe;
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        k.SetValue(SeedRunValue, $"\"{exe}\" --version", RegistryValueKind.String);
    }

    private static bool StartupApprovedDisabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        return k?.GetValue(SeedRunValue) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
    }

    private static void CleanupSeeds(string seedDir)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) k?.DeleteValue(SeedRunValue, throwOnMissingValue: false);
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true))
                k?.DeleteValue(SeedRunValue, throwOnMissingValue: false);
            if (Directory.Exists(seedDir)) Directory.Delete(seedDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string TaskOn(NexusTaskKind kind) =>
        AutomationHandlers.ReadTaskStatus(kind, AutomationScheduledScanHandler.DefaultExecutable).State;

    private static void WriteSummary(Run run)
    {
        var summary = new
        {
            windows = new { build = run.Facts.WindowsBuild, edition = run.Facts.WindowsEdition, vm = run.Facts.IsVirtualMachine },
            steps = run.Steps,
            applyCalls = run.Host.Calls.Select(c => new
            {
                step = c.Step?.ToString(),
                c.Title,
                level = c.Level.ToString(),
                actions = c.Requests.Count,
                message = c.Result.Message,
                results = (c.Result.Response?.Results ?? []).Select(x => $"{x.Outcome} {x.ActionKey} {x.Target} {x.Detail}"),
            }),
            reverts = run.Host.Reverts.Select(r => new { run = r.Run, level = r.Level.ToString(), message = r.Result.Message, fullyRestored = r.Result.Response?.FullyRestored }),
            measures = run.Measures,
            acceptedRefusals = run.Accepted,
            problems = run.Problems,
        };
        File.WriteAllText(Path.Combine(run.Log.Directory, "assistente-e2e.json"), JsonSerializer.Serialize(summary, Json));
        run.Log.Line($"===== Resumo: {run.Host.Calls.Count} pedido(s) de escrita, {run.Host.Reverts.Count} desfazer, {run.Accepted.Count} recusa(s) aceitas, {run.Problems.Count} defeito(s).");
        foreach (var p in run.Problems) run.Log.Line("DEFEITO: " + p);
    }
}
