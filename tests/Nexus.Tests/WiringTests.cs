using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nexus.Core;
using Nexus.WindowsActions;
using Nexus.WindowsActions.Registry;

namespace Nexus.Tests;

/// <summary>
/// Fiação: todo botão que escreve precisa chegar a uma ação que o aplicador conhece, com os argumentos e o nível que ela exige,
/// e toda ação do catálogo precisa de handler. Já aconteceu de um botão mandar "memorylab.pagefile.automatic" sem "@1" (o
/// aplicador respondia "ação desconhecida") e de o catálogo ter ações sem handler — nenhum teste pegava. Estes testes leem o
/// código-fonte (pontos que montam <c>ActionRequest</c>), os binários compilados (strings e constantes) e os XAML.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class WiringTests
{
    // ================================================================== (a) chaves de ação usadas no código

    /// <summary>Toda string "x.y@N" dos binários do produto é uma ação viva do catálogo (ou chave antiga absorvida por "supersedes").</summary>
    [Fact]
    public void Every_action_key_literal_in_the_binaries_exists_in_the_catalog()
    {
        var live = Wiring.Catalog.Actions.Select(a => a.Definition.Key).ToHashSet(StringComparer.Ordinal);
        var legacy = Wiring.Catalog.Actions.SelectMany(a => a.Supersedes).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var dll in Wiring.ProductAssemblies())
            foreach (var s in Wiring.Literals(dll).Distinct(StringComparer.Ordinal))
                if (KeyRegex().IsMatch(s) && !live.Contains(s) && !legacy.Contains(s))
                    problems.Add($"{Path.GetFileName(dll)}: \"{s}\" não existe no catálogo");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Toda string com cara de id de ação (prefixo de um módulo do catálogo, sem "@N") é um id do catálogo — pega id digitado
    /// errado em constantes que depois recebem "+ \"@1\"".
    /// </summary>
    [Fact]
    public void Every_bare_action_id_literal_in_the_binaries_exists_in_the_catalog()
    {
        var ids = Wiring.Catalog.Actions.Select(a => a.Definition.Id).ToHashSet(StringComparer.Ordinal);
        var rejected = Wiring.Catalog.Rejected.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var dll in Wiring.ProductAssemblies())
            foreach (var s in Wiring.Literals(dll).Distinct(StringComparer.Ordinal))
            {
                if (!BareIdRegex().IsMatch(s) || ids.Contains(s) || rejected.Contains(s) || NotActionIds.Contains(s)) continue;
                if (!ModulePrefixes.Contains(s[..s.IndexOf('.', StringComparison.Ordinal)])) continue;
                problems.Add($"{Path.GetFileName(dll)}: \"{s}\"");
            }
        Assert.True(problems.Count == 0, "Parece id de ação, mas não existe no catálogo (se não for ação, inclua em NotActionIds):" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>Prefixos dos módulos que só servem para ações (os do catálogo principal, como "win." ou "gpu.", têm muitos outros usos).</summary>
    private static readonly HashSet<string> ModulePrefixes = new(StringComparer.Ordinal)
    {
        "automation", "boot", "cleaning", "cpulab", "disk", "driverupdates", "gpulab", "memorylab", "privacy", "programs", "scanner",
        "security", "system",
    };

    /// <summary>Strings com prefixo de módulo que NÃO são ids de ação (ex.: ids de achados, chaves de configuração).</summary>
    private static readonly HashSet<string> NotActionIds = new(StringComparer.Ordinal)
    {
        "disk.system-free", // achado do diagnóstico (HardwareDiscovery), não ação
        "disk.system-media", // achado do diagnóstico (SSD/HDD do disco do sistema), não ação
        "disk.sys", // driver de disco do Windows (categoria do Atraso de drivers em DriverModuleLookup), não ação
        "privacy.v1.json", // nome do arquivo do catálogo de privacidade
    };

    /// <summary>
    /// Cada <c>new ActionRequest(...)</c> do código: a chave resolve (literal, constante, "id + \"@1\"", variável local) para uma
    /// ação do catálogo; o valor (quando literal/constante) é aceito; os argumentos são exatamente os userParameters; ação
    /// irreversível leva ConfirmIrreversible; o nível literal passado a ApplyAsync é o que o catálogo exige; e a ação é
    /// instanciável com esses argumentos. Chave que não dá para resolver estaticamente precisa estar em <see cref="DynamicSites"/>,
    /// dizendo qual teste cobre a origem dela.
    /// </summary>
    [Fact]
    public void Assistant_advanced_keys_come_from_the_catalog() =>
        new Modules.AssistantPlannerTests().Performance_lists_the_rest_of_the_catalog_as_advanced_unchecked_and_never_session_or_with_bad_values();

    [Fact]
    public void Every_action_request_in_the_code_reaches_a_valid_catalog_action()
    {
        var sites = ActionSites.All();
        if (Environment.GetEnvironmentVariable("NEXUS_WIRING_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(dump, sites.Select(s => $"{s.File}:{s.Line} key={s.KeyExpr} => [{string.Join(",", s.Keys ?? [])}] values=[{(s.Values is null ? "?" : string.Join(",", s.Values.Select(v => v ?? "null")))}] args=[{(s.ArgKeys is null ? "?" : string.Join(",", s.ArgKeys))}]{(s.ArgsOptional ? "(opc)" : "")} level={s.Level ?? "-"} irr={s.ConfirmIrreversible}"));
        Assert.True(sites.Count >= 40, $"Só {sites.Count} pontos achados: o leitor de código quebrou?");
        var problems = new List<string>();
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            var where = $"{site.File}:{site.Line}";
            if (site.CatalogDerived) continue; // entry.Definition.Key: veio do próprio catálogo
            if (site.Keys is null)
            {
                var id = $"{site.File}|{site.KeyExpr}";
                unresolved.Add(id);
                if (!DynamicSites.TryGetValue(id, out var covered))
                    problems.Add($"{where}: chave não resolvida estaticamente: {site.KeyExpr} (resolva ou registre em DynamicSites com o teste que cobre a origem)");
                else if (typeof(WiringTests).GetMethod(covered) is null)
                    problems.Add($"DynamicSites['{id}'] aponta para o teste '{covered}', que não existe.");
                continue;
            }
            foreach (var key in site.Keys)
            {
                var entry = Wiring.Entry(key);
                if (entry is null)
                {
                    problems.Add($"{where}: '{key}' não existe no catálogo (o aplicador responde \"ação desconhecida\")" +
                                 (key.Contains('@', StringComparison.Ordinal) ? "" : ": falta \"@versão\""));
                    continue;
                }
                problems.AddRange(CheckRequest(where, entry, site.Values, site.ArgKeys, site.ArgsOptional, site.ConfirmIrreversible, site.Level));
            }
        }
        foreach (var stale in DynamicSites.Keys.Where(k => !unresolved.Contains(k)))
            problems.Add($"DynamicSites: '{stale}' não aparece mais no código (ou agora resolve): remova da lista.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Confere um pedido contra a regra do broker (BrokerService.Resolve) e o nível contra o catálogo.</summary>
    internal static IEnumerable<string> CheckRequest(string where, CatalogEntry entry, IReadOnlyList<string?>? values, IReadOnlyCollection<string>? argKeys,
        bool argsOptional, bool confirmIrreversible, string? level)
    {
        var key = entry.Definition.Key;
        if (values is not null)
            foreach (var value in values)
            {
                if (entry.DesiredValue is not null)
                {
                    if (value is not null && value != entry.DesiredValue) yield return $"{where}: '{key}' tem valor fixo ({entry.DesiredValue}); o código manda '{value}'";
                }
                else if (entry.Definition.AllowedValues is { Count: > 0 } allowed)
                {
                    if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
                        yield return $"{where}: '{key}' exige valor entre {string.Join(", ", allowed)}; o código manda '{value ?? "null"}'";
                }
                else if (value is not null) yield return $"{where}: '{key}' não aceita valor; o código manda '{value}'";
            }
        if (argKeys is not null)
        {
            foreach (var k in argKeys.Where(k => !entry.UserParameters.ContainsKey(k)))
                yield return $"{where}: '{key}' não aceita o argumento '{k}' (userParameters: {string.Join(", ", entry.UserParameters.Keys)})";
            if (!argsOptional)
                foreach (var k in entry.UserParameters.Keys.Where(k => !argKeys.Contains(k)))
                    yield return $"{where}: '{key}' exige o argumento '{k}', que o código não manda";
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["activeScheme"] = "381b4222-f694-41f0-9685-ff5bb260df2e" };
            foreach (var k in entry.UserParameters.Keys) parameters[k] = "x";
            var chosen = entry.DesiredValue is null && entry.Definition.AllowedValues is { Count: > 0 } a ? entry with { DesiredValue = a[0] } : entry;
            if (chosen.TryInstantiate(parameters, out var why) is null) yield return $"{where}: '{key}' não é instanciável: {why}";
        }
        if (entry.Definition.Irreversible && !confirmIrreversible)
            yield return $"{where}: '{key}' é irreversível e o pedido não leva ConfirmIrreversible: true (o aplicador recusa)";
        if (level is not null && !LevelMatches(entry, level))
            yield return $"{where}: '{key}' é \"{entry.PrivilegesRaw}\" no catálogo e o código manda BrokerLevel.{level}" +
                         (level == "User" ? " (o aplicador do usuário não tem permissão: sempre falha)" : " (pede UAC sem precisar)");
    }

    private static bool LevelMatches(CatalogEntry entry, string level) => entry.PrivilegesRaw switch
    {
        "User|Admin" or "Admin|User" => true,
        "User" => level == "User",
        _ => level == "Admin",
    };

    /// <summary>
    /// Pontos cuja chave vem de dados (linhas, achados, presets): "Arquivo|expressão" → nome do teste (desta classe) que confere a
    /// origem. O teste acima exige que o nome exista.
    /// </summary>
    private static readonly Dictionary<string, string> DynamicSites = new(StringComparer.Ordinal)
    {
        ["Planners.cs|item.ActionKey"] = nameof(Service_presets_send_valid_admin_actions),
        ["ServicesPage.cs|i.Item.ActionKey"] = nameof(Service_presets_send_valid_admin_actions),
        ["Planners.cs|a.Key"] = nameof(Privacy_planner_keys_are_valid_catalog_actions),
        ["Planners.cs|d.Key"] = nameof(Assistant_advanced_keys_come_from_the_catalog), // chaves lidas do próprio catálogo
        ["PrivacyPage.cs|k"] = nameof(Privacy_planner_keys_are_valid_catalog_actions),
        ["Planners.cs|f.ActionKey!"] = nameof(Key_and_level_written_together_match_the_catalog),
        ["MemoryDoctorPage.cs|f.ActionKey"] = nameof(Key_and_level_written_together_match_the_catalog),
        ["SafeCleaning.cs|i.ActionKey"] = nameof(Cleaning_rules_point_to_batch_actions),
        ["CleaningPage.cs|rule.ActionKey"] = nameof(Cleaning_rules_point_to_batch_actions),
        ["AssistantPage.cs|t.Key"] = nameof(Key_and_level_written_together_match_the_catalog),
        ["StartupPage.cs|req.Key"] = nameof(Key_and_level_written_together_match_the_catalog),
        ["StartupPage.cs|r.Key"] = nameof(Key_and_level_written_together_match_the_catalog), // desligar em massa: mesmo ToggleRequest
        ["MemoryDoctorPage.cs|req.Key"] = nameof(Key_and_level_written_together_match_the_catalog),
        ["ProgramsPage.cs|actionId"] = nameof(Key_and_level_written_together_match_the_catalog),
        ["SystemToolsPage.cs|actionId"] = nameof(Key_and_level_written_together_match_the_catalog),
        ["CpuLabPage.cs|f.ActionId"] = nameof(Cpu_findings_send_valid_actions_at_the_right_level),
        ["ScannerPage.cs|id + \"@1\""] = nameof(Scanner_quarantine_slots_exist_with_the_right_level),
        ["GameModeAutoPanel.cs|k"] = nameof(Game_mode_auto_actions_exist_with_the_right_level), // lista fechada GameModeActions

        ["SecurityPage.cs|actionId + \"@1\""] = nameof(Security_fix_actions_exist_with_the_right_level_and_arguments),
        ["SettingsPage.cs|ActionKey(editor.Kind.ActionId)"] = nameof(Schedule_kinds_point_to_valid_actions),
        ["AbLabText.cs|a.ActionId"] = nameof(Ab_lab_candidates_only_come_from_game_option_plans_and_catalog_tweaks),
    };

    // ================================================================== origens dinâmicas

    /// <summary>
    /// Chave e nível escritos juntos (tuplas e chamadas com BrokerLevel literal ou "admin: true/false"): a chave existe, o nível é
    /// o do catálogo, os argumentos (quando escritos ali) são os userParameters e o valor (quando escrito ali) é aceito.
    /// </summary>
    [Fact]
    public void Key_and_level_written_together_match_the_catalog()
    {
        var pairs = ActionSites.Pairs();
        Assert.True(pairs.Count >= 15, $"Só {pairs.Count} pares chave+nível achados: o leitor de código quebrou?");
        if (Environment.GetEnvironmentVariable("NEXUS_WIRING_DUMP") is { Length: > 0 } dump)
            File.WriteAllLines(dump + ".pairs", pairs.Select(p => $"{p.File}:{p.Line} [{string.Join(",", p.Keys)}] level={p.Level} values=[{(p.Values is null ? "?" : string.Join(",", p.Values.Select(v => v ?? "null")))}] args=[{(p.ArgKeys is null ? "?" : string.Join(",", p.ArgKeys))}]"));
        var problems = new List<string>();
        foreach (var p in pairs)
            foreach (var key in p.Keys)
            {
                var entry = Wiring.Entry(key);
                if (entry is null) { problems.Add($"{p.File}:{p.Line}: '{key}' não existe no catálogo"); continue; }
                problems.AddRange(CheckRequest($"{p.File}:{p.Line}", entry, p.Values, p.ArgKeys, false, confirmIrreversible: true, p.Level));
            }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Laboratório A/B: os pedidos ao aplicador são remontados de um AbCandidate (AbLabCandidates.Requests). Os candidatos nascem
    /// só de (a) GameOptionPlans da tela Jogos, cujas chaves+níveis literais este arquivo já confere, e (b) dos ajustes rápidos
    /// (QuickTweaksPanel.All), montados pela janela a partir do próprio catálogo (entry.Definition.Key). O ViewModel não escreve
    /// nenhuma chave à mão.
    /// </summary>
    [Fact]
    public void Ab_lab_candidates_only_come_from_game_option_plans_and_catalog_tweaks()
    {
        var page = File.ReadAllText(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "ViewModels", "Modules", "AbLabPage.cs"));
        var text = File.ReadAllText(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "ViewModels", "Modules", "AbLabText.cs"));
        Assert.DoesNotContain("new AbCandidate(", page);
        Assert.DoesNotContain("new AbActionRef(", page);
        Assert.Contains("AbLabCandidates.FromPlan(o.Text, o.Plan)", page);
        Assert.Contains("AbLabCandidates.FromCatalog(t.Key, t.Title, t.Request, t.Level)", page);
        Assert.Contains("Tweaks.All", page);
        // Nenhuma chave de ação literal nos dois arquivos.
        foreach (var file in new[] { page, text })
            Assert.DoesNotMatch(@"""[a-z]+(\.[a-z0-9-]+)+@\d+""", file);
        // Ida e volta: um plano real da tela Jogos vira pedido com a mesma chave, valor, argumento e nível.
        var plan = Nexus.Desktop.ViewModels.Simple.GameOptionPlans.NvUncap("cs2.exe");
        var back = Nexus.Desktop.ViewModels.AbLabCandidates.Requests(Nexus.Desktop.ViewModels.AbLabCandidates.FromPlan("x", plan));
        Assert.Equal(plan.Requests.Select(r => (r.Request.ActionId, r.Request.Value, r.Request.Arguments!["exe"], r.Level)),
            back.Select(r => (r.Request.ActionId, r.Request.Value, r.Request.Arguments!["exe"], r.Level)));
        foreach (var r in back)
        {
            var entry = Wiring.Entry(r.Request.ActionId);
            Assert.NotNull(entry);
            Assert.Empty(CheckRequest("AbLab", entry!, [r.Request.Value], r.Request.Arguments!.Keys.ToList(), false, true, r.Level.ToString()));
        }
    }

    [Fact]
    public void Service_presets_send_valid_admin_actions()
    {
        var problems = new List<string>();
        var count = 0;
        foreach (var preset in Nexus.Boot.ServicePresets.Load(Path.Combine(Wiring.Root, "catalog")))
            foreach (var item in preset.Items)
            {
                count++;
                var entry = Wiring.Entry(item.ActionKey);
                if (entry is null) { problems.Add($"preset {preset.Id}: '{item.ActionKey}' não existe"); continue; }
                // ServicesPage manda o preset inteiro como administrador, sem valor nem argumentos.
                problems.AddRange(CheckRequest($"preset {preset.Id}", entry, [null], [], false, false, "Admin"));
            }
        Assert.True(count > 0);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Privacy_planner_keys_are_valid_catalog_actions()
    {
        // A página monta os pedidos com PrivacyPlanner.ActionKeys (Entry.Definition.Key das ações de cada item) e o nível com
        // PrivacyPlanner.NeedsAdmin (privileges do catálogo); aqui: as ações de privacy.v1.json são as do catálogo carregado.
        var privacy = Nexus.Privacy.PrivacyCatalog.LoadFromDirectory(Path.Combine(Wiring.Root, "catalog"));
        var problems = new List<string>();
        var keys = privacy.Items.SelectMany(i => i.Actions).Select(a => a.Definition.Key).Distinct().ToList();
        Assert.True(keys.Count >= 30, $"Só {keys.Count} chaves de privacidade.");
        foreach (var key in keys)
        {
            var entry = Wiring.Entry(key);
            if (entry is null) { problems.Add($"'{key}' não existe"); continue; }
            problems.AddRange(CheckRequest("privacidade", entry, [null], [], false, false, null));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Cleaning_rules_point_to_batch_actions()
    {
        var problems = new List<string>();
        // Só as regras de quarentena viram pedido (CleaningRuleRow.CanClean; SafeCleaning e a CLI também filtram): as "só medir"
        // indicam a ferramenta do Windows. A página e o Automático agrupam o nível por rule.Admin.
        var rules = Nexus.Cleaning.CleaningRuleSet.BuiltIn.Rules.Where(r => r.Mode == Nexus.Cleaning.CleaningMode.Quarantine).ToList();
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            var entry = Wiring.Entry(rule.ActionKey);
            if (entry is null) { problems.Add($"regra {rule.Id}: '{rule.ActionKey}' não existe"); continue; }
            problems.AddRange(CheckRequest($"regra {rule.Id}", entry, [null], ["batch"], false, false, rule.Admin ? "Admin" : "User"));
        }
        var user = Wiring.Entry(Wiring.Constants["CleaningUserRuleHandler.ActionId"].Single() + "@1");
        Assert.NotNull(user);
        problems.AddRange(CheckRequest("regra do usuário", user!, [null], ["rule", "batch"], false, false, "User"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Cpu_findings_send_valid_actions_at_the_right_level()
    {
        // Entrada que dispara todos os achados com ação.
        var input = new Nexus.Lab.Cpu.CpuDiagnosisInput
        {
            Power = new Nexus.Lab.Cpu.CpuPowerState { MaxStateAc = 50, BoostModeAc = 0, CoreParkingMinAc = 10 },
            GameModeRead = true,
            GameModeEnabled = false,
        };
        var findings = Nexus.Lab.Cpu.CpuDiagnosis.Evaluate(input).Where(f => f.ActionId is not null).ToList();
        Assert.True(findings.Count >= 4, $"Só {findings.Count} achados com ação.");
        var problems = new List<string>();
        foreach (var f in findings)
        {
            var entry = Wiring.Entry(f.ActionId!);
            if (entry is null) { problems.Add($"{f.Id}: '{f.ActionId}' não existe"); continue; }
            problems.AddRange(CheckRequest(f.Id, entry, [f.ActionValue], [], false, false, f.ActionNeedsAdmin ? "Admin" : "User"));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Modo jogo automático: a lista fechada <see cref="Nexus.Measurement.GameModeActions"/> pede cada ação no nível que o catálogo declara, sem valor nem argumentos.</summary>
    [Fact]
    public void Game_mode_auto_actions_exist_with_the_right_level()
    {
        new Modules.GameModeAutoTests().Closed_list_matches_the_catalog_and_is_safe_for_a_match();
        var problems = new List<string>();
        foreach (var action in Nexus.Measurement.GameModeActions.All)
        {
            var entry = Wiring.Entry(action.Key);
            if (entry is null) { problems.Add($"'{action.Key}' não existe"); continue; }
            problems.AddRange(CheckRequest("Modo jogo automático", entry, [null], null, false, false, action.Admin ? "Admin" : "User"));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Scanner_quarantine_slots_exist_with_the_right_level()
    {
        var user = int.Parse(Wiring.Constants["ScannerPage.UserSlots"].Single(), System.Globalization.CultureInfo.InvariantCulture);
        var machine = int.Parse(Wiring.Constants["ScannerPage.MachineSlots"].Single(), System.Globalization.CultureInfo.InvariantCulture);
        var problems = new List<string>();
        foreach (var (prefix, slots, level) in new[] { ("scanner.quarantine.user.", user, "User"), ("scanner.quarantine.machine.", machine, "Admin") })
            for (var n = 1; n <= slots; n++)
            {
                var key = $"{prefix}{n:00}@1";
                var entry = Wiring.Entry(key);
                if (entry is null) { problems.Add($"'{key}' não existe (a página usa {slots} vagas)"); continue; }
                problems.AddRange(CheckRequest("Verificar arquivos", entry, [null], ["path", "sha256"], false, false, level));
            }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Security_fix_actions_exist_with_the_right_level_and_arguments()
    {
        // SecurityPage.LevelOf: os ids desta lista vão como usuário; o resto como administrador.
        var page = File.ReadAllText(Path.Combine(Wiring.Root, "src", "Nexus.Desktop", "ViewModels", "Modules", "SecurityPage.cs"));
        var levelOf = LevelOfRegex().Match(page);
        Assert.True(levelOf.Success, "SecurityPage.LevelOf mudou de forma: atualize este teste.");
        var userIds = QuotedRegex().Matches(levelOf.Groups[1].Value).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        var fixes = typeof(Nexus.Security.Heuristics).GetFields().Where(f => f.IsLiteral && f.Name.EndsWith("Action", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.True(fixes.Count >= 5);
        foreach (var id in fixes.Concat(userIds).Distinct())
        {
            var entry = Wiring.Entry(id + "@1");
            if (entry is null) { problems.Add($"'{id}@1' não existe"); continue; }
            // Achados por seleção mandam "selection"; os demais, nada.
            var args = entry.UserParameters.ContainsKey("selection") ? new[] { "selection" } : [];
            problems.AddRange(CheckRequest("Segurança", entry, [null], args, false, false, userIds.Contains(id) ? "User" : "Admin"));
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Schedule_kinds_point_to_valid_actions()
    {
        var problems = new List<string>();
        var kinds = Nexus.Automation.NexusTaskKind.All;
        Assert.NotEmpty(kinds);
        foreach (var kind in kinds)
        {
            var entry = Wiring.Catalog.Actions.Where(a => a.Definition.Id == kind.ActionId).MaxBy(a => a.Definition.Version);
            if (entry is null) { problems.Add($"'{kind.ActionId}' não existe"); continue; }
            problems.AddRange(CheckRequest(kind.ActionId, entry, ["ON", "OFF"], ["schedule", "conditions"], false, false, "User"));
            // Os valores que SettingsPage.Arguments manda (ToString) passam na regra do catálogo que o aplicador aplica.
            foreach (var schedule in new[] { Nexus.Automation.TaskSchedule.Daily(4, 5), Nexus.Automation.TaskSchedule.Weekly(DayOfWeek.Saturday, 23, 59),
                         Nexus.Automation.TaskSchedule.Monthly(28, 0, 0) })
                foreach (var conditions in Nexus.Automation.TaskConditions.All)
                    if (entry.ValidateUserArguments(new Dictionary<string, string> { ["schedule"] = schedule.ToString(), ["conditions"] = conditions.ToString() }, out var why) is null)
                        problems.Add($"{kind.ActionId}: '{schedule}' / '{conditions}' recusado pelo catálogo: {why}");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Regras de recomendação (Automático, Assistente, "Otimizar agora"): uma regra que cita ação inexistente some em silêncio
    /// (o motor pula), então toda ação/prefixo citado existe e todo valor fixo é aceito pela ação.
    /// </summary>
    [Fact]
    public void Recommendation_rules_point_to_existing_actions_and_values()
    {
        var problems = new List<string>();
        var rules = Nexus.Automation.RecommendationEngine.Default().Rules;
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            if (rule.Action is { } id)
            {
                var entry = Wiring.Catalog.Actions.Where(a => a.Definition.Id == id).MaxBy(a => a.Definition.Version);
                if (entry is null) { problems.Add($"regra {rule.Id}: ação '{id}' não existe no catálogo"); continue; }
                if (rule.Kind is Nexus.Automation.RecommendationKind.Recommend or Nexus.Automation.RecommendationKind.Consider && entry.UserParameters.Count == 0)
                    problems.AddRange(CheckRequest($"regra {rule.Id}", entry, [entry.DesiredValue is null ? rule.Value : null], null, false, false, null));
            }
            foreach (var prefix in new[] { rule.ActionPrefix, rule.Module }.OfType<string>())
                if (!Wiring.Catalog.Actions.Any(a => a.Definition.Id.StartsWith(prefix, StringComparison.Ordinal)))
                    problems.Add($"regra {rule.Id}: nenhuma ação começa com '{prefix}'");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [GeneratedRegex(@"LevelOf\(string actionId\)\s*=>\s*actionId is (.+?)\?\s*BrokerLevel\.User", RegexOptions.Singleline)]
    private static partial Regex LevelOfRegex();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedRegex();

    // ================================================================== (b) handlers

    /// <summary>
    /// Toda ação do catálogo tem handler no aplicador: os 7 embutidos de Broker.Host/Program.cs mais os módulos de
    /// ModuleHandlers.All. A lista de módulos é a mesma no aplicador, no autoteste e na pré-visualização da interface.
    /// </summary>
    [Fact]
    public void Every_catalog_action_has_a_registered_handler_in_the_broker()
    {
        var root = Wiring.Root;
        var broker = File.ReadAllText(Path.Combine(root, "src", "Nexus.Broker.Host", "ModuleHandlers.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "Nexus.Broker.Host", "Program.cs"));
        var selfTest = File.ReadAllText(Path.Combine(root, "src", "Nexus.SelfTest", "NexusChecks.cs"));
        var preview = File.ReadAllText(Path.Combine(root, "src", "Nexus.Desktop", "Services", "NexusServices.cs"));

        var modules = ModuleCreates(broker);
        Assert.True(modules.Count >= 14, $"ModuleHandlers.All com só {modules.Count} módulos?");
        Assert.Equal(modules, ModuleCreates(selfTest));
        Assert.Equal(modules, ModuleCreates(preview));
        var builtIn = BuiltInHandlers(program);
        Assert.True(builtIn.Count >= 7, $"Program.cs com só {builtIn.Count} handlers embutidos?");
        Assert.Equal(builtIn, BuiltInHandlers(selfTest));
        Assert.Equal(builtIn, BuiltInHandlers(preview));

        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handler in builtIn)
        {
            var type = FindType(handler) ?? throw new InvalidOperationException($"Tipo {handler} não encontrado.");
            var kind = type.GetField("Kind")?.GetRawConstantValue() as string
                       ?? throw new InvalidOperationException($"{handler} sem constante Kind.");
            kinds.Add(kind);
        }
        var env = new HandlerEnvironment(new InMemoryRegistryBackend(), Path.Combine(Path.GetTempPath(), "nexus-wiring"), OtherAccount: false, Elevated: true);
        foreach (var module in modules)
        {
            var type = FindType(module) ?? throw new InvalidOperationException($"Tipo {module} não encontrado.");
            var create = type.GetMethod("Create", [typeof(HandlerEnvironment)]) ?? throw new InvalidOperationException($"{module}.Create(HandlerEnvironment) não existe.");
            var created = ((IEnumerable<IActionHandler>)create.Invoke(null, [env])!).ToList();
            foreach (var h in created) kinds.Add(h.HandlerKind);
            // Alguns módulos só montam handlers no Windows (backends reais). Aqui: as constantes Kind dos handlers do assembly.
            if (created.Count == 0)
                foreach (var t in type.Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IActionHandler).IsAssignableFrom(t)))
                    if (t.GetField("Kind")?.GetRawConstantValue() is string k) kinds.Add(k);
        }
        var missing = Wiring.Catalog.Actions.Where(a => !kinds.Contains(a.Definition.HandlerKind))
            .Select(a => $"{a.Definition.Key} ({a.Definition.HandlerKind})").ToList();
        Assert.True(missing.Count == 0, "Ações sem handler no aplicador (o autoteste mostra \"sem handler\"; o botão responderia \"handler pendente\"):" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    private static List<string> ModuleCreates(string source) =>
        ModuleCreateRegex().Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static List<string> BuiltInHandlers(string source) =>
        BuiltInRegex().Matches(source).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static Type? FindType(string name)
    {
        var simple = name[(name.LastIndexOf('.') + 1)..];
        foreach (var asm in ProductTypesAnchor.Value)
        {
            var t = asm.GetTypes().FirstOrDefault(t => t.FullName == name || (!name.Contains('.', StringComparison.Ordinal) && t.Name == simple));
            if (t is not null) return t;
        }
        return null;
    }

    /// <summary>Assemblies do produto carregáveis em Linux (todos os net10.0 que os testes referenciam).</summary>
    private static readonly Lazy<List<System.Reflection.Assembly>> ProductTypesAnchor = new(() =>
    {
        // Os módulos referenciados pelo projeto de testes (typeof garante o carregamento) mais os já carregados.
        Type[] anchors =
        [
            typeof(Nexus.Cleaning.CleaningHandlers), typeof(Nexus.DiskTools.DiskToolsHandlers), typeof(Nexus.Programs.ProgramsHandlers),
            typeof(Nexus.Boot.BootHandlers), typeof(Nexus.Privacy.PrivacyHandlers), typeof(Nexus.Security.SecurityHandlers),
            typeof(Nexus.Security.Scanner.ScannerHandlers), typeof(Nexus.Monitoring.MonitoringHandlers), typeof(Nexus.SystemTools.SystemToolsHandlers),
            typeof(Nexus.DriverUpdates.DriverUpdatesHandlers), typeof(Nexus.Automation.AutomationHandlers), typeof(Nexus.Lab.Cpu.CpuLabHandlers),
            typeof(Nexus.Lab.Gpu.GpuLabHandlers), typeof(Nexus.Lab.Memory.MemoryLabHandlers), typeof(Nexus.GameProfiles.SourceCfg.SourceCfgConvarHandler),
            typeof(Nexus.WindowsActions.Registry.RegistryValueHandler),
            typeof(Nexus.NetRoute.NetRouteHandlers),
        ];
        return anchors.Select(t => t.Assembly)
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Nexus.", StringComparison.Ordinal) == true))
            .Distinct().ToList();
    });

    // ================================================================== regex

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)+@[0-9]{1,6}\z")]
    internal static partial Regex KeyRegex();

    [GeneratedRegex(@"\A[a-z][a-z0-9\-]*(?:\.[a-z0-9\-]+){1,}\z")]
    private static partial Regex BareIdRegex();

    [GeneratedRegex(@"\b(Nexus(?:\.\w+)+Handlers)\.Create\b")]
    private static partial Regex ModuleCreateRegex();

    [GeneratedRegex(@"new\s+(\w+Handler)\s*\(")]
    private static partial Regex BuiltInRegex();
}
