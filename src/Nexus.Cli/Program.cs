// nexus: linha de comando do NEXUS. Lê o PC, recomenda, simula e aplica SÓ pelo aplicador (broker sob demanda), com
// confirmação no terminal, diário e desfazer. "nexus --help" lista os comandos; códigos de saída em CliExit.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nexus.Automation;
using Nexus.Broker;
using Nexus.Core;
using Nexus.HardwareDiscovery;
using Nexus.Measurement;
using Nexus.Persistence;

Console.OutputEncoding = Encoding.UTF8;
var parsed = CliParser.Parse(args);
if (parsed.Help)
{
    Console.WriteLine(CliParser.HelpFor(parsed.HelpTopic));
    return CliExit.Ok;
}
if (parsed.Command is null)
{
    Console.Error.WriteLine(parsed.Error);
    return CliExit.Usage;
}

try
{
    return await new Nexus.Cli.CliApp(parsed.Command).RunAsync();
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException or InvalidOperationException)
{
    Console.Error.WriteLine($"nexus: {ex.Message}");
    return CliExit.Partial;
}

namespace Nexus.Cli
{
    internal sealed class CliApp(CliCommand cmd)
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private static string CatalogDirectory => Path.Combine(AppContext.BaseDirectory, "catalog");
        private bool Quiet => cmd.Has("--quiet");

        public Task<int> RunAsync() => cmd.Name switch
        {
            "scan" => Task.FromResult(Scan(report: cmd.Has("--report"))),
            "report" => Task.FromResult(Scan(report: true)),
            "recommend" => Task.FromResult(Recommend()),
            "optimize" => OptimizeAsync(),
            "plan" => Task.FromResult(Plan()),
            "apply" => Task.FromResult(Apply(cmd.Arguments)),
            "revert" => Task.FromResult(Revert()),
            "history" => Task.FromResult(History()),
            "measure" => Task.FromResult(Measure()),
            "compare" => Task.FromResult(Compare()),
            "schedule" => Task.FromResult(Schedule()),
            "version" => Task.FromResult(Version()),
            _ => Task.FromResult(CliExit.Usage),
        };

        // ------------------------------------------------------------------ leitura

        private sealed record Reading(SystemFacts Facts, IReadOnlyList<DiagnosticFinding> Findings, RecommendationSet Set, CatalogDocument Catalog);

        private static Reading Read()
        {
            var snapshot = Discovery.Capture();
            var facts = snapshot.Facts;
            var findings = DiagnosticRules.Evaluate(facts);
            var profile = PcProfiler.Classify(facts, PcTraitsProbe.Read(facts.SystemDrive), findings);
            var catalog = CatalogLoader.LoadDirectory(CatalogDirectory);
            var set = RecommendationEngine.Default().Evaluate(profile, catalog, RiskModel.ReadDeclared(CatalogDirectory), Parameters(facts));
            return new Reading(facts, findings, set, catalog);
        }

        private static Dictionary<string, string> Parameters(SystemFacts f)
        {
            var p = new Dictionary<string, string>(StringComparer.Ordinal);
            if (f.ActivePowerScheme is { } s) p["activeScheme"] = s.ToString("D");
            return p;
        }

        private int Scan(bool report)
        {
            var r = Read();
            var scan = ScanReport.From(r.Facts, r.Findings, r.Set, r.Catalog.CatalogVersion, DateTimeOffset.Now);
            IReadOnlyList<string> saved = [];
            if (report) saved = scan.Save(cmd.Option("--out") ?? ScanReport.DefaultDirectory, html: true);
            if (cmd.Json) Console.WriteLine(scan.ToJson());
            else if (!Quiet) Console.Write(scan.ToText());
            if (!cmd.Json) foreach (var file in saved) Console.WriteLine($"Relatório: {file}");
            return CliExit.Ok;
        }

        private int Recommend()
        {
            var r = Read();
            var candidates = OneClickPlanner.Candidates(r.Set, r.Catalog);
            if (cmd.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    profile = r.Set.Profile.Headline,
                    recommendations = ScanReport.From(r.Facts, r.Findings, r.Set, r.Catalog.CatalogVersion, DateTimeOffset.Now).Recommendations,
                    oneClick = candidates.Select(c => c.Request).ToList(),
                }, Json));
                return CliExit.Ok;
            }
            Console.WriteLine($"Seu PC: {r.Set.Profile.Headline}");
            foreach (var g in r.Set.Items.GroupBy(i => i.Kind))
            {
                Console.WriteLine();
                Console.WriteLine($"{ScanReport.KindText(g.Key.ToString())}:");
                foreach (var i in g)
                {
                    Console.WriteLine($"  • {i.Title}{(i.ActionKey is null ? "" : $"  [{i.ActionKey} · risco {i.RiskText}]")}");
                    Console.WriteLine($"    {i.Reason}");
                }
            }
            Console.WriteLine();
            Console.WriteLine(candidates.Count == 0
                ? "Otimizar agora: nada de risco baixo a aplicar para este PC."
                : $"Otimizar agora: {candidates.Count} ação(ões) candidata(s). Rode 'nexus optimize' para ver o plano confirmado pelo aplicador.");
            return CliExit.Ok;
        }

        // ------------------------------------------------------------------ aplicador

        private static string? UserSid() =>
            OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value : null;

        private static BrokerSession? Session(BrokerLevel level, Action<string>? progress = null) =>
            UserSid() is { } sid ? BrokerSession.ForThisPc(level, AppContext.BaseDirectory, sid, progress) : null;

        private static int Unsupported()
        {
            Console.Error.WriteLine("Aplicar e simular exigem o Windows (o aplicador do NEXUS usa pipes e APIs do Windows).");
            return CliExit.Unsupported;
        }

        private static void Progress(string m) => Console.Error.WriteLine(m);

        private bool Confirm(string text)
        {
            Console.WriteLine(text);
            if (cmd.Yes) { Console.WriteLine("(--yes: confirmado)"); return true; }
            if (Console.IsInputRedirected) { Console.Error.WriteLine("Sem terminal para confirmar: use --yes. Nada foi alterado."); return false; }
            Console.Write("Confirmar? [s/N] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            return answer is "s" or "sim" or "y" or "yes";
        }

        /// <summary>Converte os argumentos em pedidos e os separa por nível (privilégio declarado no catálogo).</summary>
        private static (List<(CatalogEntry Entry, ActionRequest Request)> Items, string? Error) Resolve(IEnumerable<string> ids, CatalogDocument catalog)
        {
            var list = new List<(CatalogEntry, ActionRequest)>();
            foreach (var text in ids)
            {
                (string Id, int? Version, string? Value) a;
                try { a = CliParser.ParseActionArgument(text); }
                catch (FormatException ex) { return ([], ex.Message); }
                var entry = catalog.Actions.Where(e => e.Definition.Id == a.Id && (a.Version is null || e.Definition.Version == a.Version))
                    .MaxBy(e => e.Definition.Version);
                if (entry is null) return ([], $"Ação '{text}' não existe no catálogo.");
                if (list.Any(x => x.Item1.Definition.Id == entry.Definition.Id)) return ([], $"Ação '{entry.Definition.Id}' repetida.");
                list.Add((entry, new ActionRequest(entry.Definition.Key, a.Value)));
            }
            return (list, null);
        }

        private int Plan()
        {
            if (!OperatingSystem.IsWindows()) return Unsupported();
            var catalog = CatalogLoader.LoadDirectory(CatalogDirectory);
            var (items, error) = Resolve(cmd.Arguments, catalog);
            if (error is not null) { Console.Error.WriteLine(error); return CliExit.Usage; }
            var level = cmd.Has("--admin") ? BrokerLevel.Admin : BrokerLevel.User;
            var result = Session(level, Progress)!.Plan(items.Select(i => i.Request).ToList());
            return Report(result, "Simulação");
        }

        private int Apply(IReadOnlyList<string> ids)
        {
            if (!OperatingSystem.IsWindows()) return Unsupported();
            var catalog = CatalogLoader.LoadDirectory(CatalogDirectory);
            var (items, error) = Resolve(ids, catalog);
            if (error is not null) { Console.Error.WriteLine(error); return CliExit.Usage; }
            var exit = CliExit.Ok;
            foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
            {
                var group = items.Where(i => (i.Entry.Definition.Privileges == PrivilegeLevel.Admin) == (level == BrokerLevel.Admin)).ToList();
                if (group.Count == 0) continue;
                var text = new StringBuilder();
                text.AppendLine(CultureInfo.InvariantCulture, $"Aplicar {group.Count} ação(ões){(level == BrokerLevel.Admin ? " de sistema (administrador)" : "")}:");
                foreach (var (entry, req) in group)
                    text.AppendLine(CultureInfo.InvariantCulture, $"  • {entry.Definition.Title}{(req.Value is null ? "" : $" = {req.Value}")}  — risco: {entry.Definition.Risks}");
                if (level == BrokerLevel.Admin)
                    text.AppendLine("O Windows vai pedir permissão, e um ponto de restauração é criado antes de alterar.");
                text.Append("Tudo fica no diário e pode ser desfeito com 'nexus revert <id>'.");
                if (!Confirm(text.ToString())) { Console.WriteLine("Cancelado. Nada foi alterado."); return CliExit.Cancelled; }
                var r = Session(level, Progress)!.Execute(group.Select(g => g.Request).ToList(), "cli");
                exit = Math.Max(exit, Report(r, "Aplicação"));
                if (!r.Connected || r.Response is { Ok: false }) break;
            }
            return exit;
        }

        private async Task<int> OptimizeAsync()
        {
            if (!OperatingSystem.IsWindows()) return Unsupported();
            var r = Read();
            var candidates = OneClickPlanner.Candidates(r.Set, r.Catalog);
            Console.WriteLine($"Seu PC: {r.Set.Profile.Headline}");
            if (candidates.Count == 0) { Console.WriteLine("Nada de risco baixo a aplicar para este PC."); return CliExit.Ok; }
            var user = Session(BrokerLevel.User, Progress)!;
            var (results, problem) = await OneClickPlanner.SimulateAsync(candidates.Select(c => c.Request).ToList(), q => Task.FromResult(user.Plan(q)));
            if (problem is not null) { Console.Error.WriteLine(problem); return CliExit.BrokerRefused; }
            var plan = OneClickPlanner.Build(candidates, results);
            Console.WriteLine(plan.Summary);
            foreach (var s in plan.Skipped) Console.WriteLine($"  – fora do plano: {s.Recommendation.Title} ({s.Reason})");
            if (plan.IsEmpty) return CliExit.Ok;
            foreach (var g in plan.Gains) Console.WriteLine($"  Ganho esperado: {g}");
            var exit = CliExit.Ok;
            var runs = new List<(Guid Run, BrokerLevel Level)>();
            foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
            {
                var requests = plan.Requests(level);
                if (requests.Count == 0) continue;
                Console.WriteLine();
                if (!Confirm(plan.Confirmation(level))) { Console.WriteLine("Cancelado. Nada mais foi alterado."); return runs.Count == 0 ? CliExit.Cancelled : exit; }
                var res = (level == BrokerLevel.User ? user : Session(level, Progress)!).Execute(requests, "cli-otimizar");
                exit = Math.Max(exit, Report(res, "Aplicação"));
                if (res.Response is { Ok: true, RunId: { } run }) runs.Add((run, level));
                else break;
            }
            if (runs.Count > 0)
                Console.WriteLine($"Para desfazer tudo: {string.Join(" ; ", runs.Select(x => $"nexus revert {x.Run}{(x.Level == BrokerLevel.Admin ? " --admin" : "")}"))}");
            return exit;
        }

        private int Revert()
        {
            if (!OperatingSystem.IsWindows()) return Unsupported();
            var run = Guid.Parse(cmd.Arguments[0]);
            BrokerLevel? level = cmd.Has("--admin") ? BrokerLevel.Admin : null;
            if (level is null)
            {
                foreach (var l in new[] { BrokerLevel.User, BrokerLevel.Admin })
                    if (Journals().FirstOrDefault(j => j.Level == l) is { Journal: { } jj } && jj.Runs().Any(x => x.RunId == run)) { level = l; break; }
                DisposeJournals();
            }
            if (level is null) { Console.Error.WriteLine($"Execução {run} não encontrada nos diários. Use 'nexus history'."); return CliExit.NotFound; }
            if (!Confirm($"Desfazer a execução {run}? O NEXUS volta ao estado registrado antes da mudança (e não sobrescreve o que mudou depois).")) return CliExit.Cancelled;
            return Report(Session(level.Value, Progress)!.RevertRun(run), "Desfazer");
        }

        /// <summary>Mostra o resultado de uma chamada ao aplicador e devolve o código de saída.</summary>
        private int Report(BrokerCallResult r, string what)
        {
            if (cmd.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { connected = r.Connected, ok = r.Ok, message = r.Message, runId = r.Response?.RunId, results = r.Response?.Results }, Json));
            }
            else
            {
                Console.WriteLine($"{what}: {r.Message}");
                foreach (var x in r.Response?.Results ?? [])
                    Console.WriteLine($"  {x.Outcome,-20} {x.ActionKey}{(x.Detail is null ? "" : $" — {x.Detail}")}{(x.Observed is null ? "" : $" ({x.Observed} → {x.Desired})")}");
                if (r.Response?.RunId is { } run) Console.WriteLine($"Execução: {run}  (desfazer: nexus revert {run})");
            }
            if (!r.Connected || r.Response is null || !r.Response.Ok) return CliExit.BrokerRefused;
            var bad = (r.Response.Results ?? []).Any(x => x.Outcome is "Failed" or "FailedAndCompensated" or "Interrupted" or "Conflict" or "Refused")
                      || r.Response.FullyRestored == false;
            return bad ? CliExit.Partial : CliExit.Ok;
        }

        // ------------------------------------------------------------------ diário (somente leitura)

        private List<(BrokerLevel Level, string Path, SqliteJournal? Journal)>? _journals;

        private List<(BrokerLevel Level, string Path, SqliteJournal? Journal)> Journals()
        {
            if (_journals is not null) return _journals;
            _journals = [];
            foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
            {
                var path = LocalBroker.JournalPath(level);
                SqliteJournal? j = null;
                try { if (File.Exists(path)) j = SqliteJournal.OpenReadOnly(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { }
                _journals.Add((level, path, j));
            }
            return _journals;
        }

        private void DisposeJournals()
        {
            foreach (var j in _journals ?? []) j.Journal?.Dispose();
            _journals = null;
        }

        private int History()
        {
            try
            {
                var journals = Journals().Where(j => j.Journal is not null).ToList();
                if (journals.Count == 0) { Console.WriteLine("Nenhum diário encontrado: nada foi aplicado por este usuário ainda."); return CliExit.NotFound; }
                if (cmd.Arguments.Count == 1)
                {
                    if (!Guid.TryParse(cmd.Arguments[0], out var run)) { Console.Error.WriteLine("Id de execução inválido."); return CliExit.Usage; }
                    var owner = journals.FirstOrDefault(j => j.Journal!.Runs().Any(x => x.RunId == run));
                    if (owner.Journal is null) { Console.Error.WriteLine($"Execução {run} não encontrada."); return CliExit.NotFound; }
                    var entries = owner.Journal.Entries(run);
                    if (cmd.Json) Console.WriteLine(JsonSerializer.Serialize(entries.Select(e => new { e.Sequence, action = e.Action.Definition.Key, state = e.State.ToString(), original = e.Original?.Describe(), e.Note, e.UpdatedAt }), Json));
                    else foreach (var e in entries)
                            Console.WriteLine($"  {e.Sequence,3} {e.State,-15} {e.Action.Definition.Title} ({e.Action.Definition.Key}){(e.Original is null ? "" : $" · antes: {e.Original.Describe()}")}");
                    return CliExit.Ok;
                }
                var limit = int.Parse(cmd.Option("--limit") ?? "20", CultureInfo.InvariantCulture);
                var runs = journals.SelectMany(j => j.Journal!.Runs().Select(r => (j.Level, Run: r, Entries: j.Journal!.Entries(r.RunId))))
                    .OrderByDescending(x => x.Run.StartedAt).Take(limit).ToList();
                if (cmd.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(runs.Select(x => new
                    {
                        runId = x.Run.RunId, level = x.Level.ToString(), x.Run.Profile, x.Run.StartedAt, x.Run.EndedAt,
                        states = x.Entries.GroupBy(e => e.State.ToString()).ToDictionary(g => g.Key, g => g.Count()),
                    }), Json));
                    return CliExit.Ok;
                }
                foreach (var x in runs)
                {
                    var states = string.Join(", ", x.Entries.GroupBy(e => e.State).Select(g => $"{g.Count()} {g.Key}"));
                    Console.WriteLine($"{x.Run.StartedAt.ToLocalTime():dd/MM HH:mm}  {x.Run.RunId}  {(x.Level == BrokerLevel.Admin ? "sistema" : "usuário"),-8} {x.Run.Profile,-14} {states}");
                }
                if (runs.Count == 0) Console.WriteLine("Nenhuma execução registrada.");
                return CliExit.Ok;
            }
            finally { DisposeJournals(); }
        }

        // ------------------------------------------------------------------ medição

        private int Measure()
        {
            var file = cmd.Arguments[0];
            if (!File.Exists(file)) { Console.Error.WriteLine($"Arquivo não encontrado: {file}"); return CliExit.NotFound; }
            var s = SessionAnalyzer.Analyze(Path.GetFileNameWithoutExtension(file), Path.GetFileName(file), file, "cli", File.GetLastWriteTime(file), cmd.Option("--process"));
            if (cmd.Json) { Console.WriteLine(JsonSerializer.Serialize(new { s.Application, s.Summary }, Json)); return CliExit.Ok; }
            var m = s.Summary;
            Console.WriteLine($"{s.Application}: {m.Frames} quadros em {m.DurationMs / 1000:0.0} s");
            Console.WriteLine($"  FPS médio {m.AverageFps:0.0} · 1% low {m.OnePercentLowAverageFps:0.0} · P99 {m.P99FrametimeMs:0.00} ms · {m.StuttersPerMinute:0.0} engasgos/min");
            return CliExit.Ok;
        }

        private int Compare()
        {
            List<MeasuredSession> Load(IReadOnlyList<string> files) => files.Select(f => File.Exists(f)
                ? SessionAnalyzer.Analyze(Path.GetFileNameWithoutExtension(f), Path.GetFileName(f), f, "cli", File.GetLastWriteTime(f), cmd.Option("--process"))
                : throw new FileNotFoundException($"Arquivo não encontrado: {f}", f)).ToList();
            List<MeasuredSession> before, after;
            try { before = Load(cmd.List("--before")); after = Load(cmd.List("--after")); }
            catch (FileNotFoundException ex) { Console.Error.WriteLine(ex.Message); return CliExit.NotFound; }
            var result = SessionComparison.Compare(before, after);
            if (cmd.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    result.Headline, result.Method, result.Paired,
                    metrics = result.Metrics.Select(x => new { x.Metric.Key, x.Before, x.After, x.DeltaPercent, verdict = x.Verdict.ToString(), x.Sentence }),
                }, Json));
                return CliExit.Ok;
            }
            Console.WriteLine(result.Headline);
            foreach (var x in result.Metrics) Console.WriteLine($"  {x.Sentence}");
            if (!string.IsNullOrEmpty(result.Method)) Console.WriteLine($"Método: {result.Method}");
            return CliExit.Ok;
        }

        // ------------------------------------------------------------------ agendamento (somente leitura)

        private int Schedule()
        {
            var verb = cmd.Arguments[0];
            if (verb == "status")
            {
                var (state, error) = AutomationHandlers.ReadScheduledScanState(AutomationScheduledScanHandler.DefaultExecutable);
                if (error is not null) { Console.Error.WriteLine(error); return OperatingSystem.IsWindows() ? CliExit.Partial : CliExit.Unsupported; }
                Console.WriteLine(state switch
                {
                    ScanSchedule.Off => "Verificação semanal: desligada.",
                    AutomationScheduledScanHandler.Stale => "Verificação semanal: agendada, mas aponta para outro nexus.exe (o NEXUS mudou de pasta). Rode 'nexus schedule on' para atualizar.",
                    AutomationScheduledScanHandler.Foreign => $"Existe uma tarefa {ScheduledScanTask.TaskName} que não foi criada pelo NEXUS.",
                    _ => $"Verificação semanal: ligada ({ScanSchedule.TryParse(state)?.Describe() ?? state}). Relatórios em {ScanReport.DefaultDirectory}.",
                });
                return CliExit.Ok;
            }
            var value = verb == "on" ? cmd.Option("--at") ?? "SUN 19:00" : ScanSchedule.Off;
            return Apply([$"{AutomationHandlers.ScheduledScanActionId}={value}"]);
        }

        private static int Version()
        {
            Console.WriteLine($"nexus {typeof(CliApp).Assembly.GetName().Version} · aplicador {BrokerService.Version}");
            return CliExit.Ok;
        }
    }
}
