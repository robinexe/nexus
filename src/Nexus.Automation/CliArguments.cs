using System.Globalization;

namespace Nexus.Automation;

/// <summary>Códigos de saída do <c>nexus</c>.</summary>
public static class CliExit
{
    public const int Ok = 0;
    /// <summary>Concluído com problemas: algum item falhou, restauração parcial, ou há recomendações de prioridade alta (scan).</summary>
    public const int Partial = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Cancelled = 4;
    /// <summary>O aplicador não respondeu ou recusou (ex.: portão de execução, execução ativa).</summary>
    public const int BrokerRefused = 5;
    public const int Unsupported = 6;
}

/// <summary>Comando já interpretado.</summary>
public sealed record CliCommand(
    string Name,
    IReadOnlyList<string> Arguments,
    IReadOnlySet<string> Flags,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Lists)
{
    public bool Has(string flag) => Flags.Contains(flag);
    public string? Option(string name) => Options.TryGetValue(name, out var v) ? v : null;
    public IReadOnlyList<string> List(string name) => Lists.TryGetValue(name, out var v) ? v : [];
    public bool Json => Has("--json");
    public bool Yes => Has("--yes");
}

public sealed record CliParseResult(CliCommand? Command, string? Error, bool Help, string? HelpTopic)
{
    public static CliParseResult Fail(string error) => new(null, error, false, null);
}

/// <summary>
/// Interpretação dos argumentos (pura e testável). Cada comando declara as opções que aceita; qualquer outra é erro de uso
/// (código 2). Não há opção que aceite caminho de registro, serviço ou comando livre: ids vêm do catálogo do aplicador.
/// </summary>
public static class CliParser
{
    private sealed record Spec(string[] Flags, string[] Values, string[] Lists, int MinArgs, int MaxArgs);

    private static readonly string[] Common = ["--json", "--quiet"];

    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.Ordinal)
    {
        ["scan"] = new(["--report", "--html", "--scheduled", "--skip-on-battery", "--skip-when-gaming"], ["--out"], [], 0, 0),
        ["report"] = new(["--html"], ["--out"], [], 0, 0),
        ["recommend"] = new([], [], [], 0, 0),
        ["optimize"] = new(["--yes"], [], [], 0, 0),
        ["plan"] = new(["--admin"], [], [], 1, 32),
        ["apply"] = new(["--yes"], [], [], 1, 32),
        ["revert"] = new(["--yes", "--admin"], [], [], 1, 1),
        ["history"] = new(["--cleaning"], ["--limit"], [], 0, 1),
        ["measure"] = new([], ["--process"], [], 1, 1),
        ["compare"] = new([], ["--process"], ["--before", "--after"], 0, 0),
        ["schedule"] = new(["--yes", "--skip-on-battery", "--skip-when-gaming", "--always"], ["--at", "--freq", "--day"], [], 1, 2),
        ["clean"] = new(["--safe", "--dry-run", "--yes", "--scheduled", "--skip-on-battery", "--skip-when-gaming"], ["--rules"], [], 0, 0),
        ["quarantine"] = new(["--yes"], [], [], 1, 2),
        ["help"] = new([], [], [], 0, 1),
        ["version"] = new([], [], [], 0, 0),
    };

    public static IReadOnlyCollection<string> Commands => Specs.Keys;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal) { ["-y"] = "--yes", ["-h"] = "--help", ["-j"] = "--json" };

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0) return new(null, null, true, null);
        var first = args[0];
        if (first is "--help" or "-h" or "/?") return new(null, null, true, null);
        if (first is "--version") first = "version";
        if (!Specs.TryGetValue(first, out var spec))
            return CliParseResult.Fail($"Comando desconhecido: '{first}'. Use 'nexus --help' para ver os comandos.");

        var positionals = new List<string>();
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var a = Aliases.GetValueOrDefault(args[i], args[i]);
            if (a == "--help") return new(null, null, true, first);
            if (a == "--") { positionals.AddRange(args.Skip(i + 1)); break; }
            if (!a.StartsWith('-') || a.Length == 1) { positionals.Add(a); continue; }
            if (spec.Flags.Contains(a) || Common.Contains(a)) { flags.Add(a); continue; }
            if (spec.Values.Contains(a))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return CliParseResult.Fail($"{a} exige um valor.");
                if (options.ContainsKey(a)) return CliParseResult.Fail($"{a} repetido.");
                options[a] = args[++i];
                continue;
            }
            if (spec.Lists.Contains(a))
            {
                var values = new List<string>();
                while (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) values.Add(args[++i]);
                if (values.Count == 0) return CliParseResult.Fail($"{a} exige pelo menos um arquivo.");
                if (lists.ContainsKey(a)) return CliParseResult.Fail($"{a} repetido.");
                lists[a] = values;
                continue;
            }
            return CliParseResult.Fail($"Opção '{a}' não existe para '{first}'. Use 'nexus {first} --help'.");
        }

        if (positionals.Count < spec.MinArgs) return CliParseResult.Fail($"'{first}' precisa de {Need(spec)}. Use 'nexus {first} --help'.");
        if (positionals.Count > spec.MaxArgs) return CliParseResult.Fail($"Argumentos demais para '{first}'.");
        if (options.TryGetValue("--limit", out var limit) && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 1000))
            return CliParseResult.Fail("--limit deve ser um número entre 1 e 1000.");
        if (first == "compare" && (!lists.ContainsKey("--before") || !lists.ContainsKey("--after")))
            return CliParseResult.Fail("'compare' precisa de --before e --after.");
        if (first == "schedule" && ValidateSchedule(positionals, flags, options) is { } scheduleError)
            return CliParseResult.Fail(scheduleError);
        if (first == "scan" && !flags.Contains("--scheduled") && (flags.Contains("--skip-on-battery") || flags.Contains("--skip-when-gaming")))
            return CliParseResult.Fail("--skip-on-battery e --skip-when-gaming são da verificação agendada (--scheduled).");
        if (first == "clean" && ValidateClean(flags, options) is { } cleanError)
            return CliParseResult.Fail(cleanError);
        if (first == "quarantine" && ValidateQuarantine(positionals) is { } quarantineError)
            return CliParseResult.Fail(quarantineError);
        if (first == "history" && flags.Contains("--cleaning") && positionals.Count > 0)
            return CliParseResult.Fail("'history --cleaning' não aceita id de execução.");
        if (first == "revert" && !Guid.TryParse(positionals[0], out _))
            return CliParseResult.Fail("'revert' precisa do id da execução (um GUID, veja 'nexus history').");
        if (first == "help") return new(null, null, true, positionals.FirstOrDefault());
        return new(new CliCommand(first, positionals, flags, options, lists), null, false, null);
    }

    private static readonly string[] ScheduleOnlyAdd = ["--freq", "--day", "--skip-on-battery", "--skip-when-gaming", "--always"];

    private static string? ValidateSchedule(List<string> args, HashSet<string> flags, Dictionary<string, string> options)
    {
        var verb = args[0];
        switch (verb)
        {
            case "on" or "off" or "status" or "list":
                if (args.Count > 1) return $"'schedule {verb}' não aceita mais argumentos.";
                if (ScheduleOnlyAdd.Any(o => flags.Contains(o) || options.ContainsKey(o))) return $"Opções de horário são de 'schedule add'.";
                if (verb != "on" && options.ContainsKey("--at")) return "--at só vale com 'schedule on' ou 'schedule add'.";
                if (verb == "on" && options.TryGetValue("--at", out var legacy) && ScanSchedule.TryParse(legacy) is null)
                    return "--at deve ser \"DIA HH:MM\" (ex.: \"SUN 19:00\").";
                return null;
            case "add" or "remove":
                if (args.Count != 2 || NexusTaskKind.Find(args[1]) is null) return $"'schedule {verb}' precisa do tipo: scan ou clean.";
                if (verb == "remove")
                    return ScheduleOnlyAdd.Any(o => flags.Contains(o) || options.ContainsKey(o)) || options.ContainsKey("--at") ? "'schedule remove' não aceita opções de horário." : null;
                if (flags.Contains("--always") && (flags.Contains("--skip-on-battery") || flags.Contains("--skip-when-gaming")))
                    return "--always não combina com --skip-on-battery/--skip-when-gaming.";
                return ScheduleFrom(options, out var error) is null ? error : null;
            default:
                return "'schedule' aceita: list, add, remove (e on, off, status da verificação semanal antiga).";
        }
    }

    /// <summary>Horário de 'schedule add': --freq daily|weekly|monthly (padrão weekly), --day SUN..SAT ou 1..28, --at HH:MM (padrão 19:00).</summary>
    public static TaskSchedule? ScheduleFrom(IReadOnlyDictionary<string, string> options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;
        var freq = options.GetValueOrDefault("--freq", "weekly");
        var at = options.GetValueOrDefault("--at", "19:00");
        var day = options.GetValueOrDefault("--day");
        if (at.Length != 5 || at[2] != ':' || !int.TryParse(at[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h > 23
            || !int.TryParse(at[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m > 59)
        {
            error = "--at deve ser HH:MM (ex.: 19:00).";
            return null;
        }
        string text;
        switch (freq)
        {
            case "daily":
                if (day is not null) { error = "--day não vale com --freq daily."; return null; }
                text = $"DAILY {at}";
                break;
            case "weekly":
                var code = (day ?? "SUN").ToUpperInvariant();
                if (!TaskSchedule.Codes.Contains(code)) { error = "--day deve ser SUN, MON, TUE, WED, THU, FRI ou SAT."; return null; }
                text = $"WEEKLY {code} {at}";
                break;
            case "monthly":
                text = $"MONTHLY {day ?? "1"} {at}";
                break;
            default:
                error = "--freq deve ser daily, weekly ou monthly.";
                return null;
        }
        var schedule = TaskSchedule.TryParse(text);
        if (schedule is null) error = freq == "monthly" ? "--day deve ser um dia de 1 a 28 (existe em todo mês)." : "Horário inválido.";
        return schedule;
    }

    /// <summary>Condições de 'schedule add': explícitas pelas flags; sem flags, a limpeza usa as duas (recomendado) e a verificação nenhuma.</summary>
    public static TaskConditions ConditionsFrom(CliCommand cmd, NexusTaskKind kind)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(kind);
        if (cmd.Has("--always")) return TaskConditions.None;
        var battery = cmd.Has("--skip-on-battery");
        var game = cmd.Has("--skip-when-gaming");
        if (!battery && !game && kind == NexusTaskKind.Clean) return TaskConditions.Both;
        return new TaskConditions(battery, game);
    }

    private static string? ValidateClean(HashSet<string> flags, Dictionary<string, string> options)
    {
        if (flags.Contains("--scheduled"))
        {
            if (!flags.Contains("--safe")) return "'clean --scheduled' só existe com --safe.";
            if (options.ContainsKey("--rules")) return "'clean --scheduled' não aceita --rules: a limpeza agendada é sempre a segura.";
        }
        else if (flags.Contains("--skip-on-battery") || flags.Contains("--skip-when-gaming"))
            return "--skip-on-battery e --skip-when-gaming são da limpeza agendada (--scheduled).";
        if (options.TryGetValue("--rules", out var rules) && ParseRuleList(rules, out var error) is null) return error;
        return null;
    }

    /// <summary>"temp-user,chrome-cache" ou "cleaning.temp-user,…" → ids de regra (sem repetição; até 64).</summary>
    public static IReadOnlyList<string>? ParseRuleList(string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        error = null;
        var ids = new List<string>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var id = raw.StartsWith("cleaning.", StringComparison.Ordinal) ? raw["cleaning.".Length..] : raw;
            if (id.Length is 0 or > 64 || id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) || id[0] == '-' || id[^1] == '-')
            {
                error = $"Regra inválida em --rules: '{raw}'.";
                return null;
            }
            if (!ids.Contains(id)) ids.Add(id);
        }
        if (ids.Count == 0) { error = "--rules precisa de pelo menos uma regra (ex.: temp-user,chrome-cache)."; return null; }
        if (ids.Count > 64) { error = "--rules aceita até 64 regras."; return null; }
        return ids;
    }

    private static string? ValidateQuarantine(List<string> args) => args[0] switch
    {
        "list" => args.Count == 1 ? null : "'quarantine list' não aceita mais argumentos.",
        "restore" => args.Count == 2 && Nexus.Cleaning.CleaningBatchId.IsValid(args[1]) ? null
            : "'quarantine restore' precisa do lote (ex.: 20261031T190000Z-1a2b3c4d, veja 'nexus quarantine list').",
        _ => "'quarantine' aceita: list ou restore <lote>.",
    };

    private static string Need(Spec s) => s.MinArgs == 1 ? "de pelo menos um argumento" : $"de {s.MinArgs} argumentos";

    /// <summary>"win.gamemode.enable", "win.gamemode.enable@1" ou "id=valor" (valor da lista permitida pelo catálogo).</summary>
    public static (string Id, int? Version, string? Value) ParseActionArgument(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? value = null;
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq >= 0) { value = text[(eq + 1)..]; text = text[..eq]; }
        int? version = null;
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            if (!int.TryParse(text[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) throw new FormatException($"Versão inválida em '{text}'.");
            version = v;
            text = text[..at];
        }
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new FormatException($"Id de ação inválido: '{text}'.");
        return (text, version, value is { Length: > 0 } ? value : null);
    }

    public const string Help = """
        nexus — linha de comando do NEXUS (otimização com segurança e explicação)

        Uso: nexus <comando> [opções]

        Ler (não muda nada):
          scan               Lê o PC, mostra o perfil, o diagnóstico e as recomendações.
                             --report  grava relatório JSON e HTML em %LocalAppData%\NEXUS\reports
                             --out DIR grava em outra pasta
          report [--html]    O mesmo que "scan --report", com a seção de limpeza (quarentena).
          recommend          Mostra só as recomendações e o plano do "Otimizar agora".
          plan <ids...>      Simula ações do catálogo no aplicador (o que mudaria). --admin: no aplicador de administrador.
          history [runId]    Lista as execuções registradas no diário (e os itens de uma execução).
                             --cleaning  só as limpezas (lote, tamanho, se foi desfeita)
          quarantine list    Lista as limpezas na quarentena (lote, data, tamanho).
          measure <csv>      Resume uma captura do PresentMon (FPS, 1% low, P99, engasgos). --process NOME
          compare --before <csv...> --after <csv...>   Compara sessões antes/depois, com o veredito honesto.
          schedule list      Mostra as tarefas agendadas do NEXUS.

        Mudar (sempre pelo aplicador, com confirmação, diário e desfazer):
          optimize           "Otimizar agora": mostra o plano e aplica só o que é de risco baixo para este PC,
                             incluindo a limpeza segura (quarentena), com o espaço antes e depois.
          clean              Limpeza segura: só caches e temporários de risco baixo da sua conta, para a quarentena.
                             --rules a,b   escolhe regras (as de administrador pedem permissão ao Windows)
                             --dry-run     só simula e mede      --yes  não pergunta
          quarantine restore <lote>   Devolve os arquivos de uma limpeza ao lugar original.
          apply <ids...>     Aplica ações do catálogo. Use id=valor quando o catálogo pede uma escolha.
          revert <runId>     Desfaz uma execução (o id aparece ao aplicar e em "history").
          schedule add scan|clean    Agenda a verificação (somente leitura) ou a limpeza segura (opt-in).
                             --freq daily|weekly|monthly  --day SUN..SAT ou 1..28  --at HH:MM
                             --skip-on-battery  --skip-when-gaming  --always (limpeza: as duas condições por padrão)
          schedule remove scan|clean Remove a tarefa agendada.

        Opções gerais:
          --json             Saída em JSON (para scripts).
          --yes, -y          Não perguntar no terminal (confirma automaticamente).
          --quiet            Menos texto.
          --help, -h         Esta ajuda. "nexus <comando> --help" mostra a ajuda do comando.

        Mudanças de sistema pedem permissão ao Windows e criam um ponto de restauração antes.
        O agendamento só faz a verificação somente leitura e, se você ligar, a limpeza segura (reversível, sem administrador).

        Códigos de saída: 0 ok · 1 concluído com problemas · 2 uso inválido · 3 não encontrado · 4 cancelado
                          · 5 aplicador recusou ou não respondeu · 6 não suportado neste sistema
        """;

    public static string HelpFor(string? topic) => topic switch
    {
        "scan" or "report" => "nexus scan [--report] [--html] [--out DIR] [--json]\n  Lê o PC (somente leitura): perfil, diagnóstico e recomendações. Com --report, grava JSON e HTML.\nnexus report [--html]\n  Igual a scan --report, com a seção de limpeza (o que está na quarentena).",
        "recommend" => "nexus recommend [--json]\n  Recomendações para este PC, o que não fazer, e o plano do \"Otimizar agora\".",
        "optimize" => "nexus optimize [--yes] [--json]\n  Simula, mostra o plano e aplica as recomendações de risco baixo (usuário primeiro; administrador depois, com aviso do Windows e ponto de restauração).\n  Inclui a limpeza segura (quarentena) e mostra o espaço recuperável antes e depois.",
        "plan" => "nexus plan <id[=valor]>... [--admin] [--json]\n  Simula no aplicador. Nada é escrito.",
        "apply" => "nexus apply <id[=valor]>... [--yes] [--json]\n  Aplica pelo aplicador, com confirmação. Mostra o id da execução para desfazer.",
        "revert" => "nexus revert <runId> [--admin] [--yes]\n  Desfaz uma execução. Sem --admin, procura o id nos diários do usuário e do sistema.",
        "history" => "nexus history [runId] [--limit N] [--json]\n  Lê os diários (somente leitura).\nnexus history --cleaning [--limit N] [--json]\n  Só as limpezas: lote, data, tamanho e se foram desfeitas.",
        "measure" => "nexus measure <arquivo.csv> [--process NOME] [--json]\n  Resume uma captura do PresentMon.",
        "compare" => "nexus compare --before <csv...> --after <csv...> [--process NOME] [--json]\n  Compara sessões; com menos de 3 de cada lado, a diferença não prova nada.",
        "clean" => "nexus clean [--safe] [--rules id,...] [--dry-run] [--yes] [--json]\n" +
                   "  Sem --rules (ou com --safe): limpeza segura = só regras de risco baixo da sua conta, sem histórico, cookies, shaders\n" +
                   "  nem pastas de administrador. Com --rules: as regras escolhidas (com --safe, só as que também são seguras).\n" +
                   "  Tudo vai para a quarentena do NEXUS, com diário e desfazer ('nexus quarantine restore <lote>').\n" +
                   "  --dry-run: simula no aplicador e mede, sem mover nada.\n" +
                   "  --scheduled: usado pela tarefa agendada (sem perguntas; respeita --skip-on-battery e --skip-when-gaming).",
        "quarantine" => "nexus quarantine list [--json]\n  Limpezas na quarentena: lote, data, regras e tamanho.\nnexus quarantine restore <lote> [--yes]\n  Devolve os arquivos ao lugar original (nunca sobrescreve: um arquivo novo no lugar faz o antigo voltar com sufixo).",
        "schedule" => "nexus schedule list [--json]\n" +
                      "nexus schedule add scan|clean [--freq daily|weekly|monthly] [--day SUN..SAT|1..28] [--at HH:MM]\n" +
                      "                  [--skip-on-battery] [--skip-when-gaming] [--always] [--yes]\n" +
                      "  scan = verificação somente leitura; clean = limpeza segura (opt-in; por padrão não roda na bateria nem com jogo aberto).\n" +
                      "nexus schedule remove scan|clean [--yes]\n" +
                      "nexus schedule on|off|status [--at \"SUN 19:00\"]   (verificação semanal antiga)\n" +
                      "  Tarefas no Agendador do Windows, na sua conta, sem administrador; ligar e desligar ficam no diário e podem ser desfeitos.",
        _ => Help,
    };
}
