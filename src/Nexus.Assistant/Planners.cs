using System.Globalization;
using Nexus.Automation;
using Nexus.Boot;
using Nexus.Boot.Impact;
using Nexus.Broker;
using Nexus.Cleaning;
using Nexus.DriverUpdates;
using Nexus.HardwareDiscovery;
using Nexus.Lab.Gpu;
using Nexus.Lab.Memory;
using Nexus.Lab.Memory.Problems;
using Nexus.Privacy;
using Nexus.Programs.Inventory;
using Nexus.Security;
using BootStartupItem = Nexus.Boot.Inventory.StartupItem;

namespace Nexus.Assistant;

/// <summary>Item de revisão (programa grande, maior consumidor de memória): o usuário decide; o assistente não mexe.</summary>
public sealed record ReviewItem(string Id, string Title, string Detail, string? Value = null);

/// <summary>
/// Regras puras de cada etapa: recebem o que as bibliotecas dos módulos leram e devolvem fatos, mudanças (sempre ações do
/// catálogo, montadas por <see cref="AssistantCatalog.Change"/>) e ações do usuário. Testáveis em qualquer SO.
/// </summary>
public static class StepPlanners
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    public const string RestorePointAction = "system.restore.create-point@1";
    public const string ScheduleScanAction = "automation.schedule.scan@1";
    public const string ScheduleCleanAction = "automation.schedule.clean@1";
    public const string DriverInstallAction = "driverupdates.install@1";

    public static string Bytes(long bytes) => SafeCleaningSelector.Bytes(bytes);

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : string.Create(Pt, $"{n} {many}");

    private static void Add(List<ProposedChange> list, ProposedChange? change)
    {
        if (change is not null) list.Add(change);
    }

    // ------------------------------------------------------------------ 1. boas-vindas

    public static StepScan Welcome(AssistantCatalog catalog, PcTraits traits, long? freeBytes, long? totalBytes, int pendingRecovery)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(traits);
        var facts = new List<StepFact>();
        var notes = new List<string>();
        if (traits.HasSystemBattery == true)
        {
            var onAc = traits.OnAcPower == true;
            facts.Add(new StepFact("Energia", onAc ? "Na tomada" : traits.BatteryPercent is { } b ? $"Bateria {b}%" : "Na bateria", onAc ? "good" : "warn"));
            if (!onAc) notes.Add("Ligue o carregador: na bateria o Windows limita o processador e a placa de vídeo, e uma atualização de driver não deve ser interrompida.");
        }
        else facts.Add(new StepFact("Energia", traits.HasSystemBattery == false ? "Na tomada (sem bateria)" : "não lida", traits.HasSystemBattery == false ? "good" : "info"));

        if (freeBytes is { } free && totalBytes is > 0)
        {
            var low = free < 15L * 1024 * 1024 * 1024 || free < totalBytes.Value / 10;
            facts.Add(new StepFact("Espaço livre", Bytes(free), low ? "warn" : "good", $"de {Bytes(totalBytes.Value)} no disco do Windows"));
            if (low) notes.Add("Pouco espaço livre: o ponto de restauração e as atualizações precisam de alguns GB. A etapa de limpeza ajuda.");
        }
        else facts.Add(new StepFact("Espaço livre", "não lido", "info"));

        facts.Add(new StepFact("Alterações pela metade", pendingRecovery.ToString(Pt), pendingRecovery > 0 ? "bad" : "good",
            pendingRecovery > 0 ? "Uma alteração anterior do NEXUS foi interrompida (ex.: PC desligou no meio)." : "Nada pendente no diário."));

        var changes = new List<ProposedChange>();
        string recommendation;
        if (pendingRecovery > 0)
        {
            recommendation = "Antes de tudo, resolva as alterações pela metade (página Aplicar › Recuperar interrompidas). Depois volte aqui.";
        }
        else
        {
            Add(changes, catalog.Change(AssistantStepId.Welcome, new ActionRequest(RestorePointAction), "Criar um ponto de restauração",
                "Guarda o estado do Windows (sistema, registro e drivers) antes de qualquer mudança. Exige a Proteção do Sistema ligada.",
                "sem ponto recente", "ponto criado"));
            recommendation = "Criar um ponto de restauração agora. Leva menos de um minuto e é a rede de segurança para todo o resto.";
        }
        return new StepScan(facts, changes, [], notes, recommendation)
        {
            Metrics = freeBytes is { } f2 ? M(("space", "Espaço livre no disco do Windows", Bytes(f2))) : M(),
        };
    }

    // ------------------------------------------------------------------ 2. seu PC

    public sealed record PcMeasurements(double? SingleThreadGhz = null, double? ReadGbps = null, double? CopyGbps = null);

    public static StepScan MyPc(AssistantCatalog catalog, SystemFacts facts, PcProfile profile, MemoryAnalysis? memory,
        IReadOnlyList<MemoryFinding> memoryFindings, IReadOnlyList<GpuFinding> gpuFindings, PcMeasurements? measured = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(memoryFindings);
        ArgumentNullException.ThrowIfNull(gpuFindings);
        var list = new List<StepFact>
        {
            new("Processador", facts.CpuModel?.Trim() ?? "não lido", "info",
                facts.PhysicalCores is { } c && facts.LogicalProcessors is { } t ? $"{c} núcleos, {t} threads" : null),
            new("Placa de vídeo", profile.GpuShortName ?? "não lida", "info"),
        };
        var ramGb = facts.TotalMemoryBytes is { } ram ? Math.Round(ram / (1024.0 * 1024 * 1024)) : (double?)null;
        var channel = memory?.Layout.Mode switch
        {
            ChannelMode.SingleChannel => "single channel",
            ChannelMode.DualChannel => "dual channel",
            ChannelMode.QuadChannel => "quad channel",
            ChannelMode.MultiChannel => "multicanal",
            _ => null,
        };
        list.Add(new StepFact("Memória", ramGb is { } g ? string.Create(Pt, $"{g:0} GB") : "não lida",
            memory?.Layout.Mode == ChannelMode.SingleChannel ? "warn" : "info", channel is null ? null : $"{memory!.Inventory.Populated.Count} módulo(s), {channel}"));
        if (measured?.SingleThreadGhz is { } ghz) list.Add(new StepFact("CPU medida (1 núcleo)", string.Create(Pt, $"{ghz:0.00} GHz"), "info", "Frequência efetiva medida agora, por alguns segundos."));
        if (measured?.ReadGbps is { } rd) list.Add(new StepFact("RAM medida (leitura)", string.Create(Pt, $"{rd:0.0} GB/s"), "info", "Banda de leitura medida agora."));

        var users = new List<UserAction>();
        var notes = new List<string>();
        foreach (var f in memoryFindings)
        {
            switch (f.Id)
            {
                case "single-channel":
                    users.Add(new UserAction("Comprar um 2º módulo de RAM igual ao atual",
                        "Com um módulo só, a memória trabalha em um canal (metade da banda). Em notebooks com vídeo integrado e em jogos que dependem do processador, o segundo módulo costuma dar dezenas de % a mais. " + f.Recommendation,
                        FirstUrl(f)));
                    list.Add(new StepFact("Canais da RAM", "single channel", "warn", f.Evidence));
                    break;
                case "xmp-off":
                    users.Add(new UserAction("Ligar o perfil XMP/EXPO no BIOS", f.Recommendation, FirstUrl(f)));
                    list.Add(new StepFact("Velocidade da RAM", "abaixo do módulo", "warn", f.Evidence));
                    break;
                case "low-capacity":
                    users.Add(new UserAction("Considerar mais memória RAM", f.Recommendation, FirstUrl(f)));
                    break;
            }
        }

        var changes = new List<ProposedChange>();
        foreach (var f in gpuFindings)
        {
            if (f.Id.StartsWith("driver.age.", StringComparison.Ordinal))
            {
                list.Add(new StepFact("Driver de vídeo", f.Title.Replace("Driver da ", "", StringComparison.Ordinal), f.Level == "bad" ? "bad" : "warn", f.Evidence));
                var url = f.FixArguments?.GetValueOrDefault("url");
                users.Add(new UserAction("Atualizar o driver de vídeo pelo site oficial do fabricante", f.Advice, url));
            }
            else if (f.Id.StartsWith("pcie.width.", StringComparison.Ordinal))
            {
                list.Add(new StepFact("Link PCIe da placa", f.Title, f.Level == "bad" ? "bad" : "warn", f.Evidence));
                notes.Add(f.Advice);
            }
            else if (f.Fix == GpuFixKind.DisplayMode && f.FixArguments is { } args)
            {
                Add(changes, catalog.Change(AssistantStepId.MyPc, new ActionRequest(GpuDiagnostics.DisplayModeAction, null, Copy(args)),
                    f.FixLabel ?? f.Title, f.Advice, f.Title.Split(',')[0], f.FixLabel));
                list.Add(new StepFact("Monitor", f.Title, "warn", f.Evidence));
            }
            else if (f.Fix == GpuFixKind.GameMode)
                Add(changes, catalog.Change(AssistantStepId.MyPc, new ActionRequest(GpuDiagnostics.GameModeAction), "Ligar o Modo de Jogo do Windows", f.Advice, "desligado", "ligado"));
            else if (f.Fix == GpuFixKind.Hags)
                Add(changes, catalog.Change(AssistantStepId.MyPc, new ActionRequest(GpuDiagnostics.HagsAction, "2"), "Ligar o HAGS (agendamento de GPU)", f.Advice, "desligado", "ligado (depois de reiniciar)", optIn: true));
            else if (f.Fix == GpuFixKind.AppGpuPreference && f.FixArguments is { } app && changes.Count(c => c.Request.ActionId == GpuDiagnostics.AppPreferenceAction) < 10)
                Add(changes, catalog.Change(AssistantStepId.MyPc, new ActionRequest(GpuDiagnostics.AppPreferenceAction, null, Copy(app)), f.Title + ": usar a GPU dedicada", f.Advice, "integrada/automático", "alto desempenho"));
            else if (f.Id == "power.battery")
                notes.Add(f.Advice);
        }
        var rec = users.Count > 0
            ? $"Encontrei {Plural(users.Count, "ponto que só você pode resolver", "pontos que só você pode resolver")} (vão para o relatório). " +
              (changes.Count > 0 ? "E há ajustes do Windows que o NEXUS faz por você." : "")
            : changes.Count > 0 ? "O hardware está bem aproveitado; há ajustes do Windows que o NEXUS faz por você." : "Nada de errado no hardware que eu consiga ver.";
        var metrics = new List<(string, string, string)>();
        if (measured?.SingleThreadGhz is { } g1) metrics.Add(("cpu", "Processador (1 núcleo, medido)", string.Create(Pt, $"{g1:0.00} GHz")));
        if (measured?.ReadGbps is { } r1) metrics.Add(("ram", "Memória (leitura, medida)", string.Create(Pt, $"{r1:0.0} GB/s")));
        return new StepScan(list, changes, users, notes, rec) { Metrics = M([.. metrics]) };
    }

    private static string? FirstUrl(MemoryFinding f) => f.Sources.Count > 0 ? f.Sources[0].Url : null;

    private static Dictionary<string, MetricValue> M(params (string Key, string Label, string Value)[] items) =>
        items.ToDictionary(i => i.Key, i => new MetricValue(i.Label, i.Value), StringComparer.Ordinal);

    private static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> a) => a.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    // ------------------------------------------------------------------ 3. limpeza

    /// <param name="scanned">Resultado da medição das regras seguras (bytes por regra).</param>
    public static StepScan Cleaning(AssistantCatalog catalog, IReadOnlyList<SafeCleaningItem> safe, IReadOnlyList<RuleScanResult> scanned, string batch, long bytesInQuarantine)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(safe);
        ArgumentNullException.ThrowIfNull(scanned);
        var byKey = scanned.GroupBy(s => s.Rule.QuarantineKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var requests = SafeCleaningSelector.Requests(safe, batch);
        var changes = new List<ProposedChange>();
        long total = 0;
        var open = new List<string>();
        for (var i = 0; i < safe.Count; i++)
        {
            var item = safe[i];
            byKey.TryGetValue(item.Rule.QuarantineKey, out var s);
            if (s is not null)
            {
                open.AddRange(s.RunningApps);
                if (s.Measured && s.Bytes == 0) continue; // nada para limpar nesta regra
                if (!s.Measured && s.RootsFound == 0) continue; // programa não instalado
            }
            var bytes = s?.Measured == true ? s.Bytes : (long?)null;
            total += bytes ?? 0;
            Add(changes, catalog.Change(AssistantStepId.Cleaning, requests[i], item.Title,
                bytes is { } b ? $"{Bytes(b)} vão para a quarentena." : "Vai para a quarentena (tamanho medido na hora).",
                bytes is { } bb ? Bytes(bb) : null, "na quarentena"));
        }
        var facts = new List<StepFact>
        {
            new("Recuperável agora", Bytes(total), total > 0 ? "good" : "info", "Só caches e temporários da sua conta."),
            new("Itens", changes.Count.ToString(Pt), "info"),
        };
        if (bytesInQuarantine > 0) facts.Add(new StepFact("Já na quarentena", Bytes(bytesInQuarantine), "info", "Esvaziar a quarentena é na página Limpeza (o assistente nunca apaga de verdade)."));
        var notes = new List<string>();
        if (open.Count > 0)
            notes.Add("Abertos agora: " + string.Join(", ", open.Distinct(StringComparer.OrdinalIgnoreCase)) + ". O cache deles é pulado; feche-os e leia de novo para incluir.");
        notes.Add("A quarentena fica no mesmo disco: o espaço volta de fato quando você esvaziá-la na página Limpeza.");
        return new StepScan(facts, changes, [], notes,
            changes.Count == 0 ? "Nada para limpar agora." : $"Mover {Bytes(total)} de caches para a quarentena. Documentos, senhas e histórico não entram.")
        {
            Metrics = M(("cleaning", "Caches recuperáveis (limpeza segura)", Bytes(total))),
        };
    }

    // ------------------------------------------------------------------ 4. programas

    /// <summary>Programas grandes (≥ 1 GB) ou antigos e médios (instalados há mais de 1 ano, ≥ 300 MB), maiores primeiro.</summary>
    public static IReadOnlyList<ReviewItem> ProgramCandidates(IEnumerable<InstalledProgram> programs, DateOnly today, int max = 12)
    {
        ArgumentNullException.ThrowIfNull(programs);
        const long Gb = 1024L * 1024 * 1024, Mb300 = 300L * 1024 * 1024;
        return programs
            .Where(p => !p.NoRemove && p.SizeBytes is { } s && (s >= Gb || (s >= Mb300 && p.InstallDate is { } d && d.AddYears(1) < today)))
            .OrderByDescending(p => p.SizeBytes)
            .Take(max)
            .Select(p => new ReviewItem(p.Id, p.Name,
                ProgramInventory.FormatSize(p.SizeBytes) + (p.InstallDate is { } d ? $" · instalado em {d.ToString("dd/MM/yyyy", Pt)}" : ""), p.Publisher))
            .ToList();
    }

    public static StepScan Programs(IReadOnlyList<InstalledProgram> programs, IReadOnlyList<ReviewItem> candidates, int? updates, IReadOnlyList<string> managers)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(managers);
        var facts = new List<StepFact>
        {
            new("Programas instalados", programs.Count.ToString(Pt), "info", Bytes(programs.Sum(p => p.SizeBytes ?? 0)) + " declarados pelos instaladores"),
            new("Atualizações disponíveis", updates is { } u ? u.ToString(Pt) : "não verificado", updates > 0 ? "warn" : updates == 0 ? "good" : "info",
                managers.Count == 0 ? "Nenhum gerenciador (winget, Chocolatey, Scoop, npm) encontrado." : "Pelo " + string.Join(", ", managers) + "."),
            new("Para revisar", candidates.Count.ToString(Pt), "info", "Grandes ou instalados há mais de um ano."),
        };
        var notes = new List<string>
        {
            "O NEXUS nunca desinstala sozinho. Se quiser remover algum, ele já vai marcado na página Programas para você conferir e confirmar.",
            "O Windows não guarda com segurança quando cada programa foi usado pela última vez; por isso a lista usa tamanho e data de instalação.",
        };
        var rec = updates > 0 ? $"Atualizar os {updates} programas pela página Programas (janela do próprio gerenciador, visível)." :
            candidates.Count > 0 ? "Revise a lista e marque o que não usa mais." : "Nada para revisar.";
        return new StepScan(facts, [], [], notes, rec) { Review = candidates };
    }

    // ------------------------------------------------------------------ 5. inicialização e serviços

    /// <param name="toggle">Pedido de "desligar da inicialização" para cada item (mesma regra da página Inicialização); null = origem não permite.</param>
    public static StepScan Startup(AssistantCatalog catalog, IReadOnlyList<BootStartupItem> items, Func<BootStartupItem, ActionRequest?> toggle,
        IReadOnlyList<ServicePreset> presets, IReadOnlyDictionary<string, string> serviceStartTypes, long? lastBootMs)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(toggle);
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(serviceStartTypes);
        var third = items.Where(i => !i.IsMicrosoft).ToList();
        var high = third.Where(i => i.Enabled && !i.IsOrphan && i.Impact.Level == ImpactLevel.High).ToList();
        var changes = new List<ProposedChange>();
        foreach (var i in high)
        {
            if (toggle(i) is not { } req) continue;
            Add(changes, catalog.Change(AssistantStepId.Startup, req, $"Não abrir com o Windows: {i.Name}",
                $"Impacto alto: {i.Impact.Reason} O programa continua instalado e abre normalmente quando você quiser.", "abre no boot", "não abre sozinho"));
        }
        foreach (var p in presets)
        {
            foreach (var item in p.Items)
            {
                if (!serviceStartTypes.TryGetValue(item.Service, out var current) || string.Equals(current, item.StartType, StringComparison.OrdinalIgnoreCase)) continue;
                Add(changes, catalog.Change(AssistantStepId.Startup, new ActionRequest(item.ActionKey), $"{p.Title}: {item.Service}",
                    $"{item.Why} Para quem: {p.ForWho} {p.Honesty}", ServicePresets.StartTypeLabel(current), ServicePresets.StartTypeLabel(item.StartType), optIn: true));
            }
        }
        var facts = new List<StepFact>
        {
            new("Último boot", lastBootMs is { } ms ? BootReport.Seconds(ms) : "indisponível", lastBootMs is > 60_000 ? "warn" : "info", "Medido pelo próprio Windows (Diagnostics-Performance)."),
            new("Abrem com o Windows", third.Count(i => i.Enabled).ToString(Pt), "info", "Programas de terceiros."),
            new("Impacto alto", high.Count.ToString(Pt), high.Count > 0 ? "warn" : "good"),
        };
        var orphans = items.Count(i => i.IsOrphan);
        var notes = new List<string>();
        if (orphans > 0) notes.Add($"{Plural(orphans, "entrada aponta", "entradas apontam")} para programa que não existe mais. Não atrasa nada; dá para limpar na página Inicialização.");
        notes.Add("Os presets de serviços dependem do seu uso (impressora, Xbox, busca do Iniciar): ficam desmarcados para você escolher.");
        var rec = high.Count > 0
            ? $"Tirar do boot {Plural(high.Count, "programa de impacto alto", "programas de impacto alto")}. Ficam desmarcados: só você sabe se usa logo ao ligar."
            : "Nada de impacto alto abrindo com o Windows.";
        return new StepScan(facts, changes, [], notes, rec)
        {
            Metrics = lastBootMs is { } bm
                ? M(("startup-high", "Programas de impacto alto no boot", high.Count.ToString(Pt)), ("boot", "Último boot (medido pelo Windows)", BootReport.Seconds(bm)))
                : M(("startup-high", "Programas de impacto alto no boot", high.Count.ToString(Pt))),
        };
    }

    // ------------------------------------------------------------------ 6. privacidade

    public static StepScan Privacy(AssistantCatalog catalog, IReadOnlyList<PrivacyItemState> states)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(states);
        var score = PrivacyPlanner.Score(states);
        var missing = PrivacyPlanner.MissingFor(states, PrivacyProfile.Recommended);
        var changes = new List<ProposedChange>();
        foreach (var item in missing)
            foreach (var a in item.Pending)
                Add(changes, catalog.Change(AssistantStepId.Privacy, new ActionRequest(a.Key), item.Item.Title,
                    item.Item.Summary + (string.IsNullOrWhiteSpace(item.Item.Loses) ? "" : " Você perde: " + item.Item.Loses), a.Current, a.Desired));
        var facts = new List<StepFact>
        {
            new("Proteções ativas", $"{score.Protected} de {score.Applicable}", score.Applicable > 0 && score.Protected * 2 >= score.Applicable ? "good" : "warn"),
            new("Faltam no Recomendado", missing.Count.ToString(Pt), missing.Count > 0 ? "warn" : "good"),
        };
        return new StepScan(facts, changes, [], ["Não acelera o PC. Para o perfil Rígido ou item a item, use a página Privacidade."],
            missing.Count == 0 ? "O perfil Recomendado já está aplicado." : $"Aplicar o perfil Recomendado ({Plural(missing.Count, "item", "itens")}).")
        {
            Metrics = M(("privacy", "Privacidade (proteções ativas)", $"{score.Protected} de {score.Applicable}")),
        };
    }

    // ------------------------------------------------------------------ 7. segurança

    public sealed record SecurityScore(int Good, int Total, int Warn, int Bad, int ActiveThreats, int Suspects)
    {
        public string Text => $"{Good} de {Total}";
    }

    public static SecurityScore ScoreOf(SecurityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new SecurityScore(report.Protection.Count(p => p.Tone == Tone.Good), report.Protection.Count, report.Protection.Count(p => p.Tone == Tone.Warn),
            report.Protection.Count(p => p.Tone == Tone.Bad), report.Threats.Count(t => t.Active), report.Findings.Count);
    }

    public static StepScan Security(SecurityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = ScoreOf(report);
        var facts = new List<StepFact>
        {
            new("Itens em verde", s.Text, s.Bad > 0 ? "bad" : s.Warn > 0 ? "warn" : "good"),
            new("Ameaças ativas", s.ActiveThreats.ToString(Pt), s.ActiveThreats > 0 ? "bad" : "good"),
            new("Pontos para revisar", s.Suspects.ToString(Pt), s.Suspects > 0 ? "warn" : "good", "Entradas de inicialização e registro fora do comum."),
        };
        var review = report.Protection.Where(p => p.Tone is Tone.Bad or Tone.Warn)
            .Select(p => new ReviewItem("prot:" + p.Name, p.Name, p.Advice ?? p.Detail, p.State))
            .Concat(report.Findings.Take(10).Select(f => new ReviewItem("find:" + f.Title, f.Title, f.Why, f.Category)))
            .ToList();
        var rec = s.Bad > 0 || s.ActiveThreats > 0 ? "Há proteção desligada ou ameaça pendente: abra Segurança e resolva primeiro."
            : s.Suspects > 0 ? "Proteção em dia. Revise os pontos suspeitos na página Segurança (o assistente não remove nada sozinho)."
            : "Proteção em dia.";
        return new StepScan(facts, [], [], ["A verificação rápida do Defender é opcional e roda pela ferramenta oficial da Microsoft."], rec)
        {
            Review = review,
            Metrics = M(("security", "Segurança (itens em verde)", s.Text)),
        };
    }

    // ------------------------------------------------------------------ 8. drivers

    public static StepScan Drivers(AssistantCatalog catalog, DriverUpdatesReading reading, bool searched)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reading);
        var facts = new List<StepFact>();
        var users = new List<UserAction>();
        foreach (var g in reading.Gpus)
        {
            facts.Add(new StepFact(g.Integrated ? "Vídeo integrado" : "Placa de vídeo", g.AgeDays is { } d ? $"driver com {d} dias" : "idade do driver não lida",
                g.Level == "good" ? "good" : g.Level == "bad" ? "bad" : g.Level == "warn" ? "warn" : "info", $"{g.Name} · {g.VendorVersion ?? g.WindowsVersion}"));
            if (g.Level is "warn" or "bad" && !g.Integrated)
                users.Add(new UserAction($"Atualizar o driver da {g.Name} pelo site oficial", g.Verdict, g.OfficialPage));
        }
        if (reading.RebootPending == true) facts.Add(new StepFact("Reinício pendente", "sim", "warn", "O Windows Update espera um reinício para terminar."));
        var needing = reading.NeedingDriver;
        if (reading.ProblemCodesRead || needing.Count > 0)
            facts.Add(new StepFact("Dispositivos sem driver", needing.Count.ToString(Pt), needing.Count > 0 ? "bad" : "good",
                needing.Count > 0 ? string.Join(" · ", needing.Take(3).Select(p => $"{p.Name} ({p.CodeText})")) + (needing.Count > 3 ? $" e mais {needing.Count - 3}" : "")
                    : $"{reading.DeviceCount} dispositivos conferidos."));
        if (needing.Count > 0 && (!searched || reading.Offers.All(o => o.InstalledVersion is not null)))
            users.Add(new UserAction(
                needing.Count == 1 ? $"Instalar o driver de \"{needing[0].Name}\"" : $"Instalar o driver de {needing.Count} dispositivos sem driver",
                (searched ? "O Windows Update não ofereceu driver para eles. " : "Procure primeiro no Windows Update (botão desta etapa). ") + DeviceProblems.Advice,
                DeviceProblems.Source));
        var changes = new List<ProposedChange>();
        if (searched)
        {
            var offers = reading.Offers.Where(o => o.Blocked is null && o.Group == OfferGroup.Recommended).ToList();
            facts.Add(new StepFact("Do Windows Update", reading.Error == WuErrorKind.None ? reading.Offers.Count.ToString(Pt) : "falhou",
                reading.Error == WuErrorKind.None ? (reading.Offers.Count > 0 ? "warn" : "good") : "warn", reading.ErrorMessage));
            foreach (var o in offers.Take(10))
                Add(changes, catalog.Change(AssistantStepId.Drivers, new ActionRequest(DriverInstallAction, null, new Dictionary<string, string>
                {
                    ["updateId"] = o.Update.UpdateId,
                    ["updateRevision"] = o.Update.Revision.ToString(CultureInfo.InvariantCulture),
                }), $"{o.Device}: {o.Update.Title}", o.VersionLine + ". " + o.Rollback.Explanation, o.InstalledVersion, o.Update.OfferedVersion));
        }
        var rec = !searched ? "Procurar no Windows Update (pode levar alguns minutos) ou pular." :
            changes.Count > 0 ? "Há drivers recomendados pelo Windows Update. Ficam desmarcados: driver é troca de sistema; marque se quiser." :
            "Nenhum driver recomendado pelo Windows Update agora (normal).";
        return new StepScan(facts, changes, users, ["Driver de vídeo: prefira o site oficial (NVIDIA, AMD, Intel). O NEXUS nunca baixa driver de terceiros."], rec);
    }

    // ------------------------------------------------------------------ 9. memória

    public static StepScan Memory(AssistantCatalog catalog, MemorySnapshot snapshot, IReadOnlyList<AppGroup> groups, IReadOnlyList<Suspect> suspects, DoctorContext context)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(suspects);
        ArgumentNullException.ThrowIfNull(context);
        var s = snapshot.System;
        var facts = new List<StepFact>();
        if (s.Total > 0)
        {
            var used = s.Total - s.Available - (s.Modified ?? 0);
            facts.Add(new StepFact("Em uso", $"{MemoryDoctorEngine.Bytes(used)} de {MemoryDoctorEngine.Bytes(s.Total)}", (double)used / s.Total >= 0.9 ? "bad" : (double)used / s.Total >= 0.8 ? "warn" : "good"));
            if (s.CommitLimit > 0)
                facts.Add(new StepFact("Comprometida", string.Create(Pt, $"{100.0 * s.CommitTotal / s.CommitLimit:0}% do limite"), s.CommitTotal >= 0.9 * s.CommitLimit ? "bad" : "info", "Limite = RAM + arquivo de paginação."));
        }
        facts.Add(new StepFact("Compressão de memória", context.CompressionEnabled switch { true => "ligada", false => "desligada", _ => "não lida" }, context.CompressionEnabled == false ? "warn" : "info"));
        facts.Add(new StepFact("Arquivo de paginação", context.PageFileMode switch
        {
            PageFileMode.None => "desligado",
            PageFileMode.AutomaticAllDrives or PageFileMode.SystemManaged => "automático",
            PageFileMode.Custom => "fixo",
            _ => "não lido",
        }, context.PageFileMode == PageFileMode.None ? "bad" : "info"));
        var review = groups.Where(g => g.Kind != AppKind.System).OrderByDescending(g => g.PrivateBytes).Take(5)
            .Select(g => new ReviewItem("mem:" + g.Key, g.DisplayName, $"{MemoryDoctorEngine.Bytes(g.PrivateBytes)} · {Plural(g.Processes.Count, "processo", "processos")}", MemoryDoctorEngine.Bytes(g.PrivateBytes)))
            .ToList();
        var changes = new List<ProposedChange>();
        foreach (var sus in suspects)
            foreach (var f in sus.Permanent.Where(f => f.Kind == FixKind.Apply && f.ActionKey is not null && f.Reversible && f.Guard == ProcessGuard.Normal))
                Add(changes, catalog.Change(AssistantStepId.Memory, new ActionRequest(f.ActionKey!, f.Value, f.Arguments), f.Title, $"{sus.Title}: {f.WhatItDoes} Custo: {f.Cost}"));
        var rec = changes.Count > 0 ? "Aplicar as soluções permanentes recomendadas (as temporárias, como fechar programas, ficam na página Memória)." :
            "Memória sem problema que mereça mudança. Os maiores consumidores estão abaixo, só para você saber.";
        return new StepScan(facts, changes, [], ["\"Limpar a RAM\" não acelera nada: memória em cache é memória disponível. O NEXUS não faz isso por padrão."], rec) { Review = review };
    }

    // ------------------------------------------------------------------ 10. desempenho

    /// <summary>Catálogos de ajustes de jogo/sistema que só aparecem na página Aplicar; o assistente os mostra como "avançado".</summary>
    private static readonly string[] AdvancedPrefixes = ["win.", "power.", "sched.", "gpu.", "svc.", "boot.service."];

    public static StepScan Performance(AssistantCatalog catalog, RecommendationSet set) => Performance(catalog, set, null);

    /// <param name="parameters">Parâmetros deste PC (ex.: activeScheme). Com eles, o resto dos ajustes do catálogo entra como
    /// "avançado", desmarcado: o mesmo que a página Aplicar lista, sem precisar sair do assistente.</param>
    public static StepScan Performance(AssistantCatalog catalog, RecommendationSet set, IReadOnlyDictionary<string, string>? parameters)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(set);
        var changes = new List<ProposedChange>();
        foreach (var (r, req) in OneClickPlanner.Candidates(set, catalog.Document))
            Add(changes, catalog.Change(AssistantStepId.Performance, req, r.Title, r.Reason + (r.Gain is null ? "" : " Ganho esperado: " + r.Gain)));
        foreach (var r in set.Actionable.Where(r => !r.OneClick && r.Kind is RecommendationKind.Recommend or RecommendationKind.Consider && r.ActionKey is not null))
        {
            if (catalog.Entry(r.ActionKey!) is not { } e || e.UserParameters.Count > 0) continue;
            // Mesma regra do aplicador: valor só quando o catálogo oferece escolha (allowedValues).
            var value = e.DesiredValue is null && e.Definition.AllowedValues is { Count: > 0 } allowed && r.Value is not null && allowed.Contains(r.Value, StringComparer.Ordinal) ? r.Value : null;
            Add(changes, catalog.Change(AssistantStepId.Performance, new ActionRequest(e.Definition.Key, value), r.Title,
                r.Reason + (r.Kind == RecommendationKind.Consider ? " Vale medir antes e depois." : "")));
        }
        var recommended = changes.Count;
        if (parameters is not null)
        {
            var proposed = changes.Select(c => c.Request.ActionId).ToHashSet(StringComparer.Ordinal);
            var avoid = set.Avoid.Where(a => a.ActionKey is not null).Select(a => a.ActionKey!).ToHashSet(StringComparer.Ordinal);
            foreach (var e in catalog.Document.Actions)
            {
                var d = e.Definition;
                if (!AdvancedPrefixes.Any(p => d.Id.StartsWith(p, StringComparison.Ordinal)) || e.UserParameters.Count > 0 ||
                    proposed.Contains(d.Key) || avoid.Contains(d.Key) || e.TryInstantiate(parameters, out _) is null) continue;
                Add(changes, catalog.Change(AssistantStepId.Performance, new ActionRequest(d.Key), "Avançado: " + d.Title,
                    d.Mechanism + (d.Classification == Nexus.Core.Classification.Experimental ? " Experimental: meça antes e depois." : ""), optIn: true));
            }
        }
        var facts = new List<StepFact>
        {
            new("Perfil", set.Profile.Headline, "info"),
            new("Recomendações", recommended.ToString(Pt), "info"),
            new("Avançados (desmarcados)", (changes.Count - recommended).ToString(Pt), "info"),
            new("Não fazer neste PC", set.Avoid.Count().ToString(Pt), "info"),
        };
        var notes = set.Avoid.Take(5).Select(a => $"Não fazer: {a.Title}. {a.Reason}").ToList();
        return new StepScan(facts, changes, [], notes,
            changes.Count == 0 ? "Nada a mudar para o perfil do seu PC." : "Aplicar as recomendações de risco baixo (já marcadas). As de risco médio ficam para você decidir.");
    }

    // ------------------------------------------------------------------ 11. medição

    public static StepScan Measurement(bool presentMonFound, bool autoMeasure, bool overlayOnGameStart)
    {
        var facts = new List<StepFact>
        {
            new("PresentMon", presentMonFound ? "encontrado" : "não encontrado", presentMonFound ? "good" : "warn", "Ferramenta oficial da Intel/GameTechDev que mede os quadros."),
            new("Medição automática", autoMeasure ? "ligada" : "desligada", autoMeasure ? "good" : "info"),
            new("Mostrador ao abrir jogo", overlayOnGameStart ? "ligado" : "desligado", "info"),
        };
        return new StepScan(facts, [], [],
            [presentMonFound ? "Jogue uma partida normal: o NEXUS mede sozinho e compara com a próxima." : "Sem o PresentMon a medição de FPS não funciona. Baixe no GitHub oficial (GameTechDev/PresentMon) e indique o caminho em Medir."],
            autoMeasure ? "A medição automática já está ligada. Jogue uma partida para ter o \"depois\"." : "Ligar a medição automática e jogar uma partida.");
    }

    // ------------------------------------------------------------------ 12. agendamentos

    public static readonly TaskSchedule WeeklyScan = TaskSchedule.Weekly(DayOfWeek.Sunday, 19, 0);
    public static readonly TaskSchedule WeeklyClean = TaskSchedule.Weekly(DayOfWeek.Saturday, 19, 30);

    public static StepScan Schedules(AssistantCatalog catalog, NexusTaskStatus scan, NexusTaskStatus clean)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(clean);
        var changes = new List<ProposedChange>();
        var conditions = TaskConditions.Both.ToString();
        if (!scan.IsOn && scan.State != AutomationScheduledTaskHandler.Foreign)
            Add(changes, catalog.Change(AssistantStepId.Schedules, new ActionRequest(ScheduleScanAction, AutomationScheduledTaskHandler.On,
                new Dictionary<string, string> { ["schedule"] = WeeklyScan.ToString(), ["conditions"] = conditions }),
                "Verificação semanal (só leitura)", $"{WeeklyScan.Describe()}, fora da bateria e sem jogo aberto. Só lê o PC e grava um relatório.", "desligada", "ligada"));
        if (!clean.IsOn && clean.State != AutomationScheduledTaskHandler.Foreign)
            Add(changes, catalog.Change(AssistantStepId.Schedules, new ActionRequest(ScheduleCleanAction, AutomationScheduledTaskHandler.On,
                new Dictionary<string, string> { ["schedule"] = WeeklyClean.ToString(), ["conditions"] = conditions }),
                "Limpeza segura semanal (opcional)", $"{WeeklyClean.Describe()}. Move caches da sua conta para a quarentena, com diário e desfazer. Roda sem você olhar, por isso fica desmarcada.",
                "desligada", "ligada", optIn: true));
        var facts = new List<StepFact>
        {
            new("Verificação agendada", scan.IsOn ? "ligada" : "desligada", scan.IsOn ? "good" : "info", scan.Describe()),
            new("Limpeza agendada", clean.IsOn ? "ligada" : "desligada", "info", clean.Describe()),
        };
        return new StepScan(facts, changes, [], ["Dá para mudar horário ou desligar em Ajustes a qualquer momento."],
            changes.Count == 0 ? "Os agendamentos já estão como recomendado." : "Ligar a verificação semanal. A limpeza agendada é opcional.");
    }

    // ------------------------------------------------------------------ planejar com o aplicador

    /// <summary>
    /// Confere as mudanças no aplicador do usuário (Host.PlanAsync, sem escrever): fica o que mudaria ("Pending") e a ação de
    /// administrador que o aplicador do usuário não pode simular; o resto vira "já está assim"/"não se aplica" com o motivo.
    /// Sem aplicador: devolve tudo (o aplicador confere de novo antes de escrever) e o problema.
    /// </summary>
    public static async Task<(IReadOnlyList<ProposedChange> Keep, IReadOnlyList<(ProposedChange Change, string Why)> Dropped, string? Problem)> PlanAsync(
        IReadOnlyList<ProposedChange> changes, Func<IReadOnlyList<ActionRequest>, Task<BrokerCallResult>> plan)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(plan);
        var keep = new List<ProposedChange>();
        var dropped = new List<(ProposedChange, string)>();
        foreach (var chunk in changes.Chunk(ApplyGrouping.MaxActionsPerRequest))
        {
            var (results, problem) = await OneClickPlanner.SimulateAsync(chunk.Select(c => c.Request).ToList(), plan).ConfigureAwait(true);
            if (problem is not null) return (changes, [], problem);
            var matched = AssistantRunner.Match(chunk, results);
            for (var i = 0; i < chunk.Length; i++)
            {
                var c = chunk[i];
                switch (matched[i])
                {
                    case null: keep.Add(c); break;
                    case { Outcome: "Pending" } r: keep.Add(c with { Before = c.Before ?? r.Observed, After = c.After ?? r.Desired }); break;
                    case { Outcome: "Blocked" } r when c.Level == BrokerLevel.Admin && r.Detail == OneClickPlanner.NeedsElevation: keep.Add(c); break;
                    case { Outcome: "AlreadyConfigured" }: dropped.Add((c, "Já está assim neste PC.")); break;
                    case { Outcome: "NotApplicable" } r: dropped.Add((c, $"Não se aplica: {r.Detail}")); break;
                    case var r: dropped.Add((c, $"{r.Outcome}: {r.Detail}")); break;
                }
            }
        }
        return (keep, dropped, null);
    }
}
