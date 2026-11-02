// nexus: linha de comando do NEXUS. Lê o PC, recomenda, simula e aplica SÓ pelo aplicador (broker sob demanda), com
// confirmação no terminal, diário e desfazer. "nexus --help" lista os comandos; códigos de saída em CliExit.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nexus.Automation;
using Nexus.Automation.Notifications;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.Core;
using Nexus.HardwareDiscovery;
using Nexus.Measurement;
using Nexus.Persistence;

#if NEXUS_WINDOWLESS
// nexus-task.exe (src/Nexus.Cli.Task): este mesmo código compilado sem console, rodado pelas tarefas agendadas para não piscar uma
// janela preta. Sem console não há como perguntar nem mostrar nada: só as linhas de comando fixas das tarefas são aceitas
// (WindowlessTask); a saída vai para os relatórios e o diário, como no nexus.exe com --quiet.
if (!WindowlessTask.IsAllowed(args)) return CliExit.Usage;
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
#else
Console.OutputEncoding = Encoding.UTF8;
#endif
// Autoteste no PC real (somente leitura): opções próprias, interpretadas em Nexus.SelfTest.
if (args.Length > 0 && args[0] == "selftest") return await Nexus.Cli.SelfTestCommand.RunAsync(args[1..]);
// Atualização do programa (procurar / conferir manifesto assinado); docs/11-atualizacao.md.
if (args.Length > 0 && args[0] == "update") return await Nexus.Cli.UpdateCommand.RunAsync(args[1..]);
var parsed = CliParser.Parse(args);
// Tarefa agendada: o Agendador abre um console para o nexus.exe; solta-o já (a janela some) e a saída vai para os relatórios.
if (parsed.Command is { } scheduledCommand && scheduledCommand.Has("--scheduled")) ConsoleDetach.Detach();
if (parsed.Help)
{
    Console.WriteLine(CliParser.HelpFor(parsed.HelpTopic));
    if (parsed.HelpTopic is null) Console.WriteLine("Atualização:\n  update --check     Procura versão nova do NEXUS (não baixa nem instala). 'nexus update --help' mostra o resto.");
    if (parsed.HelpTopic is null) Console.WriteLine("Autoteste:\n  selftest           Roda as leituras de todos os módulos no PC real e diz OK/AVISO/FALHA (nada é alterado).\n                     'nexus selftest --help' mostra as opções (--json, --html, --with-benchmarks...).");
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
            "scan" => Task.FromResult(Scan(report: cmd.Has("--report"), cleaningSection: false)),
            "report" => Task.FromResult(Scan(report: true, cleaningSection: true)),
            "clean" => CleanAsync(),
            "quarantine" => Task.FromResult(Quarantine()),
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

        private static string UserDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.User))!;
        private static string AdminDataDirectory => Path.GetDirectoryName(LocalBroker.JournalPath(BrokerLevel.Admin))!;

        private int Scan(bool report, bool cleaningSection)
        {
            var scheduled = cmd.Has("--scheduled");
            var settings = NexusSettings.Load(NexusSettings.DefaultPath);
            if (scheduled && RunConditionChecker.WhyNot(new TaskConditions(cmd.Has("--skip-on-battery"), cmd.Has("--skip-when-gaming")),
                    WindowsPowerSource.Instance, WindowsGameActivity.Instance) is { } why)
            {
                ScheduleLog.Write($"verificação pulada: {why}");
                if (settings.Notifications && settings.NotifySkipped) Notify("Verificação agendada pulada", why, null);
                return CliExit.Ok;
            }
            var r = Read();
            var scan = ScanReport.From(r.Facts, r.Findings, r.Set, r.Catalog.CatalogVersion, DateTimeOffset.Now);
            if (cleaningSection || scheduled) scan = scan with { Cleaning = CleaningOverview.Read(UserDataDirectory, AdminDataDirectory) };
            IReadOnlyList<string> saved = [];
            if (report) saved = scan.Save(cmd.Option("--out") ?? ScanReport.DefaultDirectory, html: true);
            if (cmd.Json) Console.WriteLine(scan.ToJson());
            else if (!Quiet) Console.Write(scan.ToText());
            if (!cmd.Json) foreach (var file in saved) Console.WriteLine($"Relatório: {file}");
            if (scheduled)
            {
                var attention = scan.Findings.Count(f => f.Severity == nameof(DiagnosticSeverity.Attention));
                var recommended = scan.Recommendations.Count(x => x.Kind == "Recommend");
                ScheduleLog.Write($"verificação concluída: {attention} atenção, {recommended} recomendação(ões)");
                if (settings.Notifications)
                    Notify("Verificação do NEXUS concluída",
                        $"{attention} ponto(s) pedem atenção · {recommended} recomendação(ões). Clique para abrir o relatório.",
                        saved.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)));
            }
            return CliExit.Ok;
        }

        /// <summary>Notificação do Windows (balão da bandeja) com "abrir relatório" ao clicar. Só no Windows; falha em silêncio.</summary>
        private static void Notify(string title, string text, string? report, bool warning = false)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var desktop = Path.Combine(AppContext.BaseDirectory, "Nexus.Desktop.exe");
                var icon = File.Exists(desktop) ? desktop : Environment.ProcessPath ?? desktop;
                BalloonNotifier.Show(title, text, report, icon, TimeSpan.FromSeconds(25), warning);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ScheduleLog.Write($"notificação falhou: {ex.Message}"); }
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
            foreach (var g in plan.Gains) Console.WriteLine($"  Ganho esperado: {g}");

            // Limpeza segura (quarentena): conferida no aplicador do usuário, medida antes e depois.
            var cleaning = await PrepareSafeCleaningAsync(r.Catalog, user);
            if (cleaning.Requests.Count > 0)
                Console.WriteLine($"Limpeza segura: {cleaning.Requests.Count} regra(s), {SafeCleaningSelector.Bytes(cleaning.Before)} recuperáveis agora (vão para a quarentena).");
            if (plan.IsEmpty && cleaning.Requests.Count == 0) return CliExit.Ok;
            var exit = CliExit.Ok;
            var runs = new List<(Guid Run, BrokerLevel Level)>();
            foreach (var level in new[] { BrokerLevel.User, BrokerLevel.Admin })
            {
                var requests = plan.Requests(level);
                var confirmation = plan.Confirmation(level);
                if (level == BrokerLevel.User && cleaning.Requests.Count > 0)
                {
                    requests = [.. requests, .. cleaning.Requests];
                    confirmation = (plan.User.Count > 0 ? confirmation + Environment.NewLine + Environment.NewLine : "") + cleaning.Confirmation;
                }
                if (requests.Count == 0) continue;
                Console.WriteLine();
                if (!Confirm(confirmation)) { Console.WriteLine("Cancelado. Nada mais foi alterado."); return runs.Count == 0 ? CliExit.Cancelled : exit; }
                var res = (level == BrokerLevel.User ? user : Session(level, Progress)!).Execute(requests, "cli-otimizar");
                exit = Math.Max(exit, Report(res, "Aplicação"));
                if (res.Response is { Ok: true, RunId: { } run }) runs.Add((run, level));
                else break;
            }
            if (cleaning.Requests.Count > 0 && runs.Any(x => x.Level == BrokerLevel.User) && cleaning.Folders is { } folders)
            {
                var after = await SafeCleaningSelector.MeasureAsync(cleaning.Rules, folders, SystemProcessProbe.Instance);
                var moved = MovedBytes(cleaning.Batch);
                Console.WriteLine($"Espaço recuperável nas regras seguras: antes {SafeCleaningSelector.Bytes(cleaning.Before)} → depois {SafeCleaningSelector.Bytes(after)} " +
                                  $"({SafeCleaningSelector.Bytes(moved)} na quarentena; o espaço volta de fato quando ela for esvaziada).");
            }
            if (runs.Count > 0)
                Console.WriteLine($"Para desfazer tudo: {string.Join(" ; ", runs.Select(x => $"nexus revert {x.Run}{(x.Level == BrokerLevel.Admin ? " --admin" : "")}"))}");
            return exit;
        }

        // ------------------------------------------------------------------ limpeza segura

        private sealed record CleaningPrep(IReadOnlyList<ActionRequest> Requests, IReadOnlyList<CleaningRule> Rules, string Batch, long Before,
            CleaningFolders? Folders, string Confirmation, IReadOnlyList<CleaningRunItem> Skipped);

        /// <summary>Seleciona a limpeza segura, mede (somente leitura) e confere no aplicador do usuário o que de fato mudaria.</summary>
        private static async Task<CleaningPrep> PrepareSafeCleaningAsync(CatalogDocument catalog, BrokerSession user)
        {
            var batch = CleaningBatchId.New(DateTime.UtcNow);
            if (!OperatingSystem.IsWindows()) return new([], [], batch, 0, null, "", []);
            var (safe, _) = SafeCleaningSelector.Select(catalog, CleaningRuleSet.BuiltIn, RiskModel.ReadDeclared(CatalogDirectory));
            if (safe.Count == 0) return new([], [], batch, 0, null, "", []);
            var folders = SafeCleaningSelector.CurrentUserFolders(UserDataDirectory, AdminDataDirectory);
            var before = await SafeCleaningSelector.MeasureAsync(safe.Select(s => s.Rule), folders, SystemProcessProbe.Instance);
            var requests = SafeCleaningSelector.Requests(safe, batch);
            var (results, problem) = await OneClickPlanner.SimulateAsync(requests, q => Task.FromResult(user.Plan(q)));
            if (problem is not null) return new([], [], batch, before, folders, "", [new("cleaning", "Limpeza segura", "Unavailable", problem)]);
            var pending = new List<ActionRequest>();
            var rules = new List<CleaningRule>();
            var skipped = new List<CleaningRunItem>();
            foreach (var item in safe)
            {
                var res = results.FirstOrDefault(x => x.ActionKey == item.ActionKey);
                if (res is { Outcome: "Pending" })
                {
                    pending.Add(requests.First(q => q.ActionId == item.ActionKey));
                    rules.Add(item.Rule);
                }
                else skipped.Add(new(item.ActionKey, item.Title, res?.Outcome ?? "NoAnswer", res?.Detail));
            }
            var text = new StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"Limpeza segura ({pending.Count} regra(s), cerca de {SafeCleaningBytes(before)}):");
            foreach (var rule in rules) text.AppendLine(CultureInfo.InvariantCulture, $"  • {rule.Title}");
            text.Append("Tudo vai para a quarentena do NEXUS (nada é apagado) e pode ser desfeito com 'nexus quarantine restore " + batch + "'.");
            return new(pending, rules, batch, before, folders, text.ToString(), skipped);
        }

        private static string SafeCleaningBytes(long b) => SafeCleaningSelector.Bytes(b);

        /// <summary>Bytes movidos para a quarentena num lote (lidos dos manifestos; somente leitura).</summary>
        private static long MovedBytes(string batch) => MovedOf(batch).Bytes;

        private static (long Bytes, long Files) MovedOf(string batch)
        {
            long bytes = 0, files = 0;
            foreach (var dir in new[] { UserDataDirectory, AdminDataDirectory })
            {
                try
                {
                    foreach (var b in QuarantineStore.ReadBatches(dir).Where(b => b.BatchId == batch))
                    {
                        bytes += b.Rules.Sum(x => x.MovedBytes);
                        files += b.Rules.Sum(x => x.MovedFiles);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return (bytes, files);
        }

        private async Task<int> CleanAsync()
        {
            var scheduled = cmd.Has("--scheduled");
            var settings = NexusSettings.Load(NexusSettings.DefaultPath);
            var started = DateTimeOffset.Now;
            if (scheduled && RunConditionChecker.WhyNot(new TaskConditions(cmd.Has("--skip-on-battery"), cmd.Has("--skip-when-gaming")),
                    WindowsPowerSource.Instance, WindowsGameActivity.Instance) is { } why)
            {
                var skippedReport = new CleaningRunReport(started, true, why, null, null, null, null, 0, 0, [], [], why);
                ScheduleLog.Write($"limpeza pulada: {why}");
                SaveQuietly(skippedReport);
                if (settings.Notifications && settings.NotifySkipped) Notify("Limpeza agendada pulada", why, null);
                return CliExit.Ok;
            }
            if (!OperatingSystem.IsWindows()) return Unsupported();

            var catalog = CatalogLoader.LoadDirectory(CatalogDirectory);
            var (safe, excluded) = SafeCleaningSelector.Select(catalog, CleaningRuleSet.BuiltIn, RiskModel.ReadDeclared(CatalogDirectory));
            var chosen = new List<(CatalogEntry Entry, CleaningRule Rule)>();
            var outside = new List<SafeCleaningExclusion>();
            if (cmd.Option("--rules") is { } list)
            {
                foreach (var id in CliParser.ParseRuleList(list, out _)!)
                {
                    var rule = CleaningRuleSet.BuiltIn.Find(id);
                    var entry = catalog.Actions.Where(a => a.Definition.Id == CleaningRuleSet.ActionPrefix + id && a.Definition.HandlerKind == CleaningRuleSet.HandlerKind)
                        .MaxBy(a => a.Definition.Version);
                    if (rule is null || entry is null) { Console.Error.WriteLine($"Regra de limpeza '{id}' não existe. Veja a página Limpeza ou catalog/cleaning.v1.json."); return CliExit.NotFound; }
                    if (rule.Mode != CleaningMode.Quarantine) { Console.Error.WriteLine($"'{id}' só mede (usa a ferramenta do Windows); não dá para limpar pelo NEXUS."); return CliExit.Usage; }
                    if (cmd.Has("--safe") && safe.All(s => s.Rule.Id != id))
                    {
                        outside.Add(excluded.FirstOrDefault(x => x.ActionId == entry.Definition.Id) ?? new(entry.Definition.Id, rule.Title, "Fora da limpeza segura."));
                        continue;
                    }
                    chosen.Add((entry, rule));
                }
            }
            else chosen.AddRange(safe.Select(s => (s.Entry, s.Rule)));
            foreach (var x in outside) Console.WriteLine($"  – fora (--safe): {x.Title}: {x.Reason}");
            if (chosen.Count == 0) { Console.WriteLine("Nada para limpar com essa escolha."); return CliExit.Ok; }

            var batch = CleaningBatchId.New(DateTime.UtcNow);
            var folders = SafeCleaningSelector.CurrentUserFolders(UserDataDirectory, AdminDataDirectory);
            var before = await SafeCleaningSelector.MeasureAsync(chosen.Select(c => c.Rule), folders, SystemProcessProbe.Instance);
            var items = new List<CleaningRunItem>();
            var exit = CliExit.Ok;
            Guid? runId = null;
            var userGroup = chosen.Where(c => c.Entry.Definition.Privileges != PrivilegeLevel.Admin).ToList();
            var adminGroup = chosen.Where(c => c.Entry.Definition.Privileges == PrivilegeLevel.Admin).ToList();
            if (scheduled && adminGroup.Count > 0) throw new InvalidOperationException("A limpeza agendada nunca usa o aplicador de administrador.");

            if (userGroup.Count > 0)
            {
                var session = Session(BrokerLevel.User, scheduled ? null : Progress)!;
                var requests = userGroup.Select(c => new ActionRequest(c.Entry.Definition.Key, null, new Dictionary<string, string> { ["batch"] = batch })).ToList();
                var (results, problem) = await OneClickPlanner.SimulateAsync(requests, q => Task.FromResult(session.Plan(q)));
                if (problem is not null)
                {
                    Console.Error.WriteLine(problem);
                    ScheduleLog.Write($"limpeza: aplicador indisponível: {problem}");
                    if (scheduled && settings.Notifications) Notify("Limpeza agendada não rodou", problem, null, warning: true);
                    return CliExit.BrokerRefused;
                }
                var pending = new List<ActionRequest>();
                foreach (var (entry, rule) in userGroup)
                {
                    var res = results.FirstOrDefault(x => x.ActionKey == entry.Definition.Key);
                    if (res is { Outcome: "Pending" }) pending.Add(requests.First(q => q.ActionId == entry.Definition.Key));
                    else items.Add(new(entry.Definition.Key, rule.Title, res?.Outcome ?? "NoAnswer", res?.Detail));
                }
                if (cmd.Has("--dry-run"))
                {
                    Console.WriteLine($"Simulação: {pending.Count} regra(s) limpariam cerca de {SafeCleaningBytes(before)}. Nada foi movido.");
                    foreach (var q in pending) Console.WriteLine($"  • {chosen.First(c => c.Entry.Definition.Key == q.ActionId).Rule.Title}");
                    foreach (var i in items) Console.WriteLine($"  – {i.Title}: {i.Outcome}{(i.Detail is null ? "" : $" ({i.Detail})")}");
                }
                else if (pending.Count > 0)
                {
                    var text = new StringBuilder();
                    text.AppendLine(CultureInfo.InvariantCulture, $"Limpar {pending.Count} item(ns), cerca de {SafeCleaningBytes(before)}:");
                    foreach (var q in pending)
                    {
                        var rule = chosen.First(c => c.Entry.Definition.Key == q.ActionId).Rule;
                        text.AppendLine(CultureInfo.InvariantCulture, $"  • {rule.Title}{(rule.Warning is null ? "" : $"  ATENÇÃO: {rule.Warning}")}");
                    }
                    text.Append(CultureInfo.InvariantCulture, $"Tudo vai para a quarentena do NEXUS (nada é apagado). Desfazer: nexus quarantine restore {batch}");
                    if (!scheduled && !Confirm(text.ToString())) { Console.WriteLine("Cancelado. Nada foi alterado."); return CliExit.Cancelled; }
                    var res = session.Execute(pending, scheduled ? "agendado-limpeza" : "cli-limpeza");
                    exit = scheduled ? Outcome(res) : Report(res, "Limpeza");
                    runId = res.Response?.RunId;
                    foreach (var x in res.Response?.Results ?? [])
                        items.Add(new(x.ActionKey, chosen.FirstOrDefault(c => c.Entry.Definition.Key == x.ActionKey).Rule?.Title ?? x.ActionKey, x.Outcome, x.Detail));
                    if (!res.Ok && res.Message is { } m) items.Add(new("cleaning", "Aplicador", "Refused", m));
                }
            }
            if (adminGroup.Count > 0 && !cmd.Has("--dry-run"))
            {
                var text = new StringBuilder();
                text.AppendLine(CultureInfo.InvariantCulture, $"Limpar {adminGroup.Count} pasta(s) do sistema (administrador):");
                foreach (var (_, rule) in adminGroup) text.AppendLine(CultureInfo.InvariantCulture, $"  • {rule.Title}");
                text.Append("O Windows vai pedir permissão, e um ponto de restauração é criado antes. Tudo vai para a quarentena.");
                if (Confirm(text.ToString()))
                {
                    var requests = adminGroup.Select(c => new ActionRequest(c.Entry.Definition.Key, null, new Dictionary<string, string> { ["batch"] = batch })).ToList();
                    var res = Session(BrokerLevel.Admin, Progress)!.Execute(requests, "cli-limpeza");
                    exit = Math.Max(exit, Report(res, "Limpeza do sistema"));
                    runId ??= res.Response?.RunId;
                }
            }
            else if (adminGroup.Count > 0) Console.WriteLine($"Simulação: {adminGroup.Count} regra(s) de administrador seriam conferidas pelo aplicador elevado ao aplicar.");
            if (cmd.Has("--dry-run")) return CliExit.Ok;

            var after = await SafeCleaningSelector.MeasureAsync(chosen.Select(c => c.Rule), folders, SystemProcessProbe.Instance);
            var (movedBytes, movedFiles) = MovedOf(batch);
            var report = new CleaningRunReport(started, scheduled, null, batch, runId, before, after, movedBytes, movedFiles, items,
                cmd.Option("--rules") is null ? excluded : outside, "");
            report = report with { Message = report.Headline };
            var saved = SaveQuietly(report);
            if (cmd.Json) Console.WriteLine(report.ToJson());
            else if (!Quiet) Console.Write(report.ToText());
            if (!cmd.Json && saved.Count > 0) Console.WriteLine($"Relatório: {saved[^1]}");
            if (scheduled)
            {
                ScheduleLog.Write($"limpeza concluída: {report.Headline}");
                if (settings.Notifications) Notify("Limpeza segura do NEXUS", report.Headline + " Clique para abrir o relatório.", saved.Count > 0 ? saved[^1] : null);
            }
            return exit;
        }

        private static IReadOnlyList<string> SaveQuietly(CleaningRunReport report)
        {
            try { return report.Save(ScanReport.DefaultDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }

        /// <summary>Código de saída de uma chamada ao aplicador, sem escrever nada no console (tarefa agendada).</summary>
        private static int Outcome(BrokerCallResult r)
        {
            if (!r.Connected || r.Response is null || !r.Response.Ok) return CliExit.BrokerRefused;
            return (r.Response.Results ?? []).Any(x => x.Outcome is "Failed" or "FailedAndCompensated" or "Interrupted" or "Conflict" or "Refused") ? CliExit.Partial : CliExit.Ok;
        }

        // ------------------------------------------------------------------ quarentena

        /// <summary>Execuções do diário (somente leitura) que limparam cada lote, por nível.</summary>
        private List<(BrokerLevel Level, Guid Run, string Batch, DateTimeOffset Started, int Items, int Reverted)> CleaningRuns()
        {
            var list = new List<(BrokerLevel, Guid, string, DateTimeOffset, int, int)>();
            foreach (var (level, _, journal) in Journals())
            {
                if (journal is null) continue;
                foreach (var run in journal.Runs())
                {
                    var entries = journal.Entries(run.RunId).Where(e => e.Action.Definition.HandlerKind == CleaningRuleSet.HandlerKind && e.Action.Target.ContainsKey("batch")).ToList();
                    if (entries.Count == 0) continue;
                    foreach (var g in entries.GroupBy(e => e.Action.Target["batch"], StringComparer.Ordinal))
                        list.Add((level, run.RunId, g.Key, run.StartedAt, g.Count(), g.Count(e => e.State.ToString() == "Reverted")));
                }
            }
            return list;
        }

        private int Quarantine()
        {
            try
            {
                if (cmd.Arguments[0] == "list")
                {
                    var overview = CleaningOverview.Read(UserDataDirectory, AdminDataDirectory);
                    if (cmd.Json) { Console.WriteLine(JsonSerializer.Serialize(overview, Json)); return CliExit.Ok; }
                    if (overview.Batches == 0) { Console.WriteLine("Quarentena vazia: nenhuma limpeza registrada."); return CliExit.Ok; }
                    Console.WriteLine($"Na quarentena: {SafeCleaningBytes(overview.BytesInQuarantine)} em {overview.Batches} limpeza(s). (Mostrando as 10 mais recentes.)");
                    foreach (var b in overview.Recent)
                        Console.WriteLine($"  {b.CreatedUtc.ToLocalTime():dd/MM HH:mm}  {b.Batch}  {SafeCleaningBytes(b.MovedBytes),9}{(b.Restored ? "  (desfeita)" : "")}  {b.Rules}");
                    Console.WriteLine("Para devolver: nexus quarantine restore <lote>");
                    return CliExit.Ok;
                }
                if (!OperatingSystem.IsWindows()) return Unsupported();
                var batch = cmd.Arguments[1];
                var runs = CleaningRuns().Where(r => r.Batch == batch).ToList();
                DisposeJournals();
                if (runs.Count == 0) { Console.Error.WriteLine($"Lote {batch} não encontrado nos diários. Use 'nexus quarantine list'."); return CliExit.NotFound; }
                if (runs.All(r => r.Reverted == r.Items)) { Console.WriteLine("Essa limpeza já foi desfeita."); return CliExit.Ok; }
                if (!Confirm($"Devolver os arquivos da limpeza {batch} ao lugar original? Nada é sobrescrito: se já houver um arquivo novo no lugar, o antigo volta com o sufixo \" (restaurado)\".")) return CliExit.Cancelled;
                var exit = CliExit.Ok;
                foreach (var (level, run, _, _, items, reverted) in runs.Where(r => r.Reverted < r.Items))
                    exit = Math.Max(exit, Report(Session(level, Progress)!.RevertRun(run), level == BrokerLevel.Admin ? "Desfazer (sistema)" : "Desfazer"));
                return exit;
            }
            finally { DisposeJournals(); }
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
                if (cmd.Has("--cleaning")) return CleaningHistory();
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

        private int CleaningHistory()
        {
            var limit = int.Parse(cmd.Option("--limit") ?? "20", CultureInfo.InvariantCulture);
            var batches = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var dir in new[] { UserDataDirectory, AdminDataDirectory })
            {
                try { foreach (var b in QuarantineStore.ReadBatches(dir)) batches[b.BatchId] = batches.GetValueOrDefault(b.BatchId) + b.Rules.Sum(r => r.MovedBytes); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var runs = CleaningRuns().OrderByDescending(r => r.Started).Take(limit).ToList();
            if (cmd.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(runs.Select(r => new
                {
                    runId = r.Run, level = r.Level.ToString(), batch = r.Batch, startedAt = r.Started, rules = r.Items, reverted = r.Reverted,
                    movedBytes = batches.GetValueOrDefault(r.Batch),
                }), Json));
                return CliExit.Ok;
            }
            if (runs.Count == 0) { Console.WriteLine("Nenhuma limpeza registrada no diário."); return CliExit.Ok; }
            foreach (var r in runs)
                Console.WriteLine($"{r.Started.ToLocalTime():dd/MM HH:mm}  {r.Batch}  {(r.Level == BrokerLevel.Admin ? "sistema" : "usuário"),-8} {r.Items} regra(s)  {SafeCleaningBytes(batches.GetValueOrDefault(r.Batch)),9}" +
                                  $"{(r.Reverted == r.Items ? "  desfeita" : r.Reverted > 0 ? "  parcialmente desfeita" : "")}  (desfazer: nexus quarantine restore {r.Batch})");
            return CliExit.Ok;
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
            switch (verb)
            {
                case "list": return ScheduleList();
                case "add": return ScheduleSet(NexusTaskKind.Find(cmd.Arguments[1])!, on: true);
                case "remove": return ScheduleSet(NexusTaskKind.Find(cmd.Arguments[1])!, on: false);
            }
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

        private int ScheduleList()
        {
            var statuses = NexusTaskKind.All.Select(k => AutomationHandlers.ReadTaskStatus(k, AutomationScheduledScanHandler.DefaultExecutable)).ToList();
            var (legacy, legacyError) = AutomationHandlers.ReadScheduledScanState(AutomationScheduledScanHandler.DefaultExecutable);
            if (cmd.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    tasks = statuses.Select(s => new { kind = s.Kind.Id, task = s.Kind.TaskName, state = s.State, schedule = s.Schedule?.ToString(), conditions = s.Conditions?.ToString(), s.PointsHere, s.Error }),
                    legacyWeekly = new { state = legacy, error = legacyError },
                }, Json));
                return CliExit.Ok;
            }
            foreach (var s in statuses) Console.WriteLine($"{s.Kind.Title}: {s.Describe()}");
            if (legacy != ScanSchedule.Off) Console.WriteLine($"Verificação semanal (antiga): {legacy}. Desligue com 'nexus schedule off'.");
            if (statuses.Any(s => s.Error is not null)) return OperatingSystem.IsWindows() ? CliExit.Partial : CliExit.Unsupported;
            return CliExit.Ok;
        }

        private int ScheduleSet(NexusTaskKind kind, bool on)
        {
            if (!OperatingSystem.IsWindows()) return Unsupported();
            var catalog = CatalogLoader.LoadDirectory(CatalogDirectory);
            var entry = catalog.Actions.Where(a => a.Definition.Id == kind.ActionId).MaxBy(a => a.Definition.Version);
            if (entry is null) { Console.Error.WriteLine($"A ação {kind.ActionId} não está no catálogo desta instalação."); return CliExit.NotFound; }
            TaskSchedule schedule;
            TaskConditions conditions;
            string text;
            if (on)
            {
                schedule = CliParser.ScheduleFrom(cmd.Options, out var error) ?? throw new FormatException(error);
                conditions = CliParser.ConditionsFrom(cmd, kind);
                var sb = new StringBuilder();
                sb.AppendLine(CultureInfo.InvariantCulture, $"{kind.Title}: {schedule.Describe()} ({conditions.Describe()}).");
                sb.AppendLine(kind.Explanation);
                if (kind == NexusTaskKind.Clean)
                {
                    var (safe, _) = SafeCleaningSelector.Select(catalog, CleaningRuleSet.BuiltIn, RiskModel.ReadDeclared(CatalogDirectory));
                    sb.AppendLine("Vai limpar, sem perguntar, quando chegar a hora:");
                    foreach (var s in safe) sb.AppendLine(CultureInfo.InvariantCulture, $"  • {s.Title}");
                    sb.AppendLine("Nunca: pastas de administrador, histórico, cookies, caches de shader ou algo irreversível.");
                }
                sb.Append("A tarefa fica no Agendador do Windows, na sua conta, sem administrador. Ligar fica no diário e pode ser desfeito.");
                text = sb.ToString();
            }
            else
            {
                var current = AutomationHandlers.ReadTaskStatus(kind, AutomationScheduledScanHandler.DefaultExecutable);
                if (current.State == AutomationScheduledTaskHandler.Off && current.Error is null) { Console.WriteLine($"{kind.Title}: já está desligada."); return CliExit.Ok; }
                schedule = current.Schedule ?? TaskSchedule.Weekly(DayOfWeek.Sunday, 19, 0);
                conditions = current.Conditions ?? TaskConditions.None;
                text = $"Remover a tarefa {kind.TaskName} do Agendador do Windows?";
            }
            if (!Confirm(text)) { Console.WriteLine("Cancelado. Nada foi alterado."); return CliExit.Cancelled; }
            var request = new ActionRequest(entry.Definition.Key, on ? AutomationScheduledTaskHandler.On : AutomationScheduledTaskHandler.Off,
                new Dictionary<string, string> { ["schedule"] = schedule.ToString(), ["conditions"] = conditions.ToString() });
            return Report(Session(BrokerLevel.User, Progress)!.Execute([request], "cli-agendamento"), on ? "Agendamento" : "Remover agendamento");
        }

        private static int Version()
        {
            Console.WriteLine($"nexus {Nexus.Updates.AppVersionInfo.FromAssembly(typeof(CliApp).Assembly).Display} · aplicador {BrokerService.Version}");
            return CliExit.Ok;
        }
    }
}

namespace Nexus.Cli
{
    /// <summary>Registro curto das tarefas agendadas em %LocalAppData%\NEXUS\reports\agendamentos.log (o console não aparece).</summary>
    internal static class ScheduleLog
    {
        public static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(Nexus.Automation.ScanReport.DefaultDirectory);
                var file = Path.Combine(Nexus.Automation.ScanReport.DefaultDirectory, "agendamentos.log");
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
