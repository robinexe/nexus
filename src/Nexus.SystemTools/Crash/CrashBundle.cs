using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Nexus.SystemTools.Crash;

/// <summary>Tudo o que foi lido sobre os travamentos (eventos dos últimos 30 dias, relatórios do WER) e o que não deu para ler.</summary>
public sealed record CrashData(
    IReadOnlyList<AppCrash> Crashes,
    IReadOnlyList<WerEvent> WerEvents,
    IReadOnlyList<DisplayReset> DisplayResets,
    IReadOnlyList<WerReportFile> Reports,
    IReadOnlyList<string> Notes)
{
    public static CrashData Empty { get; } = new([], [], [], [], []);

    public static CrashData FromXml(IEnumerable<string> applicationXml, IEnumerable<string> systemXml, IEnumerable<WerReportFile> reports, IEnumerable<string> notes)
    {
        var app = applicationXml.Select(CrashEvents.Parse).OfType<LoggedEvent>().ToList();
        var sys = systemXml.Select(CrashEvents.Parse).OfType<LoggedEvent>().ToList();
        return new CrashData(app.Select(CrashEvents.AsAppCrash).OfType<AppCrash>().OrderBy(c => c.TimeUtc).ToList(),
            app.Select(CrashEvents.AsWerEvent).OfType<WerEvent>().OrderBy(w => w.TimeUtc).ToList(),
            sys.Select(CrashEvents.AsDisplayReset).OfType<DisplayReset>().OrderBy(d => d.TimeUtc).ToList(),
            reports.ToList(), notes.ToList());
    }
}

/// <summary>
/// Monta o ZIP "informações do travamento": resumo em texto, resumo do PC, eventos em CSV e os Report.wer relevantes (sem o nome
/// da conta nos caminhos). Nunca inclui despejos de memória, documentos ou qualquer arquivo pessoal: só o que está nesta lista.
/// </summary>
public static class CrashBundle
{
    public const int MaxReports = 40;
    public const int MaxReportChars = 256 * 1024;

    /// <summary>Nomes dos arquivos dentro do ZIP (fixos: nada vem de texto livre).</summary>
    public static readonly IReadOnlyList<string> FixedEntries = ["LEIA-ME.txt", "resumo.txt", "sistema.txt", "eventos.csv"];

    public static void Write(Stream output, CrashData data, Func<string, string?, bool> isGame, string summaryText, string systemText, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(isGame);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "LEIA-ME.txt",
            "NEXUS — informações do travamento (" + now.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + ")\r\n\r\n" +
            "Conteúdo: resumo.txt (o que aconteceu, em palavras simples), sistema.txt (peças e versões do PC), eventos.csv (eventos 1000, " +
            "1001 e 4101 dos últimos 30 dias, só de jogos e do driver de vídeo) e wer\\*.txt (relatórios do Relatório de Erros do Windows).\r\n" +
            "Não há despejos de memória, documentos nem arquivos pessoais. O nome da sua conta foi trocado por %USERPROFILE% nos caminhos.\r\n" +
            (data.Notes.Count > 0 ? "\r\nO que não deu para ler:\r\n" + string.Join("\r\n", data.Notes.Select(n => "- " + n)) + "\r\n" : ""));
        Add(zip, "resumo.txt", summaryText ?? "");
        Add(zip, "sistema.txt", CrashEvents.Redact(systemText ?? ""));
        Add(zip, "eventos.csv", EventsCsv(data, isGame));
        var i = 0;
        foreach (var r in data.Reports.Where(r => CrashEvents.IsRelevantReport(r, isGame)).OrderByDescending(r => r.TimeUtc ?? DateTimeOffset.MinValue).Take(MaxReports))
        {
            i++;
            var text = r.RedactedText.Length > MaxReportChars ? r.RedactedText[..MaxReportChars] + "\n[cortado]\n" : r.RedactedText;
            Add(zip, string.Create(CultureInfo.InvariantCulture, $"wer/{i:00}-{SafeName(r.EventType)}.txt"), "Pasta: " + SafeName(r.Folder) + "\n\n" + text);
        }
    }

    /// <summary>CSV com ";" (abre no Excel em pt-BR): só jogos, reinícios do driver de vídeo e LiveKernelEvent.</summary>
    public static string EventsCsv(CrashData data, Func<string, string?, bool> isGame)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(isGame);
        var rows = new List<(DateTimeOffset T, string Line)>();
        foreach (var c in data.Crashes.Where(c => isGame(c.AppName, c.AppPath)))
            rows.Add((c.TimeUtc, Csv(c.TimeUtc, "1000", "Application Error", c.AppName, c.AppVersion, c.ModuleName, c.ExceptionCode)));
        foreach (var w in data.WerEvents.Where(w => CrashEvents.IsVideoLiveKernel(w) || (w.P1 is { } p && isGame(p, null))))
            rows.Add((w.TimeUtc, Csv(w.TimeUtc, "1001", w.EventName, w.P1, w.P2, null, null)));
        foreach (var d in data.DisplayResets)
            rows.Add((d.TimeUtc, Csv(d.TimeUtc, "4101", "Display (TDR)", d.Driver, null, null, null)));
        var sb = new StringBuilder("data_utc;evento;tipo;programa_ou_codigo;versao_ou_p2;modulo;excecao\r\n");
        foreach (var r in rows.OrderBy(r => r.T)) sb.Append(r.Line).Append("\r\n");
        return sb.ToString();
    }

    private static string Csv(DateTimeOffset t, string id, string kind, string? a, string? b, string? c, string? d) =>
        string.Join(';', t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), id, Cell(kind), Cell(a), Cell(b), Cell(c), Cell(d));

    private static string Cell(string? v)
    {
        var s = CrashEvents.Redact(v ?? "").Replace('\r', ' ').Replace('\n', ' ');
        // Planilha não executa fórmula vinda de dado lido do sistema.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.Contains(';', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal) ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }

    /// <summary>Nome seguro para entrada do ZIP (sem barras, pontos iniciais ou caracteres estranhos).</summary>
    public static string SafeName(string name)
    {
        var chars = (name ?? "").Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        var s = new string(chars).TrimStart('.');
        if (s.Length > 80) s = s[..80];
        return s.Length == 0 ? "relatorio" : s;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        w.Write(text);
    }
}
