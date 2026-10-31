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
        ["scan"] = new(["--report", "--html"], ["--out"], [], 0, 0),
        ["report"] = new(["--html"], ["--out"], [], 0, 0),
        ["recommend"] = new([], [], [], 0, 0),
        ["optimize"] = new(["--yes"], [], [], 0, 0),
        ["plan"] = new(["--admin"], [], [], 1, 32),
        ["apply"] = new(["--yes"], [], [], 1, 32),
        ["revert"] = new(["--yes", "--admin"], [], [], 1, 1),
        ["history"] = new([], ["--limit"], [], 0, 1),
        ["measure"] = new([], ["--process"], [], 1, 1),
        ["compare"] = new([], ["--process"], ["--before", "--after"], 0, 0),
        ["schedule"] = new(["--yes"], ["--at"], [], 1, 1),
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
        if (first == "schedule" && positionals[0] is not ("on" or "off" or "status"))
            return CliParseResult.Fail("'schedule' aceita: on, off ou status.");
        if (options.TryGetValue("--at", out var at) && ScanSchedule.TryParse(at) is null)
            return CliParseResult.Fail("--at deve ser \"DIA HH:MM\" (ex.: \"SUN 19:00\").");
        if (first == "revert" && !Guid.TryParse(positionals[0], out _))
            return CliParseResult.Fail("'revert' precisa do id da execução (um GUID, veja 'nexus history').");
        if (first == "help") return new(null, null, true, positionals.FirstOrDefault());
        return new(new CliCommand(first, positionals, flags, options, lists), null, false, null);
    }

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
          recommend          Mostra só as recomendações e o plano do "Otimizar agora".
          plan <ids...>      Simula ações do catálogo no aplicador (o que mudaria). --admin: no aplicador de administrador.
          history [runId]    Lista as execuções registradas no diário (e os itens de uma execução).
          measure <csv>      Resume uma captura do PresentMon (FPS, 1% low, P99, engasgos). --process NOME
          compare --before <csv...> --after <csv...>   Compara sessões antes/depois, com o veredito honesto.
          report [--html]    O mesmo que "scan --report".
          schedule status    Mostra se a verificação semanal está agendada.

        Mudar (sempre pelo aplicador, com confirmação, diário e desfazer):
          optimize           "Otimizar agora": mostra o plano e aplica só o que é de risco baixo para este PC.
          apply <ids...>     Aplica ações do catálogo. Use id=valor quando o catálogo pede uma escolha.
          revert <runId>     Desfaz uma execução (o id aparece ao aplicar e em "history").
          schedule on|off    Liga ou desliga a verificação semanal (somente leitura). --at "SUN 19:00"

        Opções gerais:
          --json             Saída em JSON (para scripts).
          --yes, -y          Não perguntar no terminal (confirma automaticamente).
          --quiet            Menos texto.
          --help, -h         Esta ajuda. "nexus <comando> --help" mostra a ajuda do comando.

        Mudanças de sistema pedem permissão ao Windows e criam um ponto de restauração antes.
        O NEXUS nunca agenda mudanças: o agendamento só faz verificações somente leitura.

        Códigos de saída: 0 ok · 1 concluído com problemas · 2 uso inválido · 3 não encontrado · 4 cancelado
                          · 5 aplicador recusou ou não respondeu · 6 não suportado neste sistema
        """;

    public static string HelpFor(string? topic) => topic switch
    {
        "scan" or "report" => "nexus scan [--report] [--html] [--out DIR] [--json]\n  Lê o PC (somente leitura): perfil, diagnóstico e recomendações. Com --report, grava JSON e HTML.",
        "recommend" => "nexus recommend [--json]\n  Recomendações para este PC, o que não fazer, e o plano do \"Otimizar agora\".",
        "optimize" => "nexus optimize [--yes] [--json]\n  Simula, mostra o plano e aplica as recomendações de risco baixo (usuário primeiro; administrador depois, com aviso do Windows e ponto de restauração).",
        "plan" => "nexus plan <id[=valor]>... [--admin] [--json]\n  Simula no aplicador. Nada é escrito.",
        "apply" => "nexus apply <id[=valor]>... [--yes] [--json]\n  Aplica pelo aplicador, com confirmação. Mostra o id da execução para desfazer.",
        "revert" => "nexus revert <runId> [--admin] [--yes]\n  Desfaz uma execução. Sem --admin, procura o id nos diários do usuário e do sistema.",
        "history" => "nexus history [runId] [--limit N] [--json]\n  Lê os diários (somente leitura).",
        "measure" => "nexus measure <arquivo.csv> [--process NOME] [--json]\n  Resume uma captura do PresentMon.",
        "compare" => "nexus compare --before <csv...> --after <csv...> [--process NOME] [--json]\n  Compara sessões; com menos de 3 de cada lado, a diferença não prova nada.",
        "schedule" => "nexus schedule on|off|status [--at \"SUN 19:00\"] [--yes]\n  Verificação semanal somente leitura no Agendador do Windows (sua conta, sem administrador).",
        _ => Help,
    };
}
