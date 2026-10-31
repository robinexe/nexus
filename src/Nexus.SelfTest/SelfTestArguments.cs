using System.Globalization;

namespace Nexus.SelfTest;

/// <summary>Argumentos de <c>nexus selftest</c> (puro e testável). Opção desconhecida = erro de uso.</summary>
public sealed record SelfTestArguments
{
    public string? JsonPath { get; init; }
    public string? HtmlPath { get; init; }
    public string? UiReportPath { get; init; }
    public bool Quiet { get; init; }
    public bool Help { get; init; }
    public SelfTestOptions Options { get; init; } = new();

    public const string Usage = """
        nexus selftest [opções]
          Autoteste no PC real, SOMENTE LEITURA: roda as leituras de todos os módulos (descoberta, catálogo com Detect,
          limpeza, disco, programas, inicialização, serviços, privacidade, segurança, verificar arquivos, monitor,
          ferramentas do sistema, drivers, laboratórios, memória, medir, automático, ajustes) e diz OK / AVISO / FALHA.
          Nada é aplicado, apagado ou configurado. Código de saída: 0 sem FALHA, 1 com alguma FALHA, 2 uso inválido.

          --json ARQUIVO          grava o relatório em JSON (nexus.selftest/1)
          --html ARQUIVO          grava o relatório em HTML (para abrir no navegador)
          --ui ARQUIVO            inclui no HTML o resultado de "Nexus.Desktop.exe --selftest-ui ARQUIVO"
          --with-windows-update   também procura drivers no Windows Update (lento; só leitura)
          --with-benchmarks       também mede CPU e memória por alguns segundos (o PC trabalha nesse tempo)
          --timeout SEGUNDOS      tempo limite padrão de cada verificação (padrão 60; 5 a 900)
          --max-files N           limite da verificação rápida de arquivos (padrão 300; 10 a 100000)
          --only MÓDULO[,MÓDULO]  só estes módulos (ex.: --only Programas,Segurança)
          --quiet                 só o resumo final
        """;

    public static SelfTestArguments Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var r = new SelfTestArguments();
        var o = new SelfTestOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Value()
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return null;
                return args[++i];
            }
            switch (a)
            {
                case "--help" or "-h" or "/?":
                    r = r with { Help = true };
                    break;
                case "--json" or "-j":
                    if (Value() is not { } j) { error = "--json exige o caminho do arquivo."; return r; }
                    r = r with { JsonPath = j };
                    break;
                case "--html":
                    if (Value() is not { } h) { error = "--html exige o caminho do arquivo."; return r; }
                    r = r with { HtmlPath = h };
                    break;
                case "--ui":
                    if (Value() is not { } u) { error = "--ui exige o caminho do ui.json."; return r; }
                    r = r with { UiReportPath = u };
                    break;
                case "--quiet":
                    r = r with { Quiet = true };
                    break;
                case "--with-windows-update":
                    o = o with { WithWindowsUpdate = true };
                    break;
                case "--with-benchmarks":
                    o = o with { WithBenchmarks = true };
                    break;
                case "--timeout":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) || s is < 5 or > 900)
                    { error = "--timeout deve ser um número de segundos entre 5 e 900."; return r; }
                    o = o with { Timeout = TimeSpan.FromSeconds(s) };
                    break;
                case "--max-files":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 10 or > 100_000)
                    { error = "--max-files deve ser um número entre 10 e 100000."; return r; }
                    o = o with { ScannerMaxFiles = n };
                    break;
                case "--only":
                    if (Value() is not { } list) { error = "--only exige o nome de pelo menos um módulo."; return r; }
                    var names = list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (names.Length == 0) { error = "--only exige o nome de pelo menos um módulo."; return r; }
                    o = o with { Only = names };
                    break;
                default:
                    error = $"Opção '{a}' não existe para 'selftest'. Use 'nexus selftest --help'.";
                    return r;
            }
        }
        return r with { Options = o };
    }
}
