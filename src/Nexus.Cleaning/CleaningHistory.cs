using System.Globalization;
using System.Text;
using Nexus.Core;

namespace Nexus.Cleaning;

/// <summary>Onde a limpeza aconteceu: conta do usuário (aplicador sem administrador) ou administrador (pastas do sistema).</summary>
public enum CleaningLevel { User, Admin }

/// <summary>Uma limpeza (um lote num nível), com o que foi movido e o que aconteceu depois.</summary>
public sealed record CleaningSession(
    string BatchId,
    CleaningLevel Level,
    DateTime StartedUtc,
    IReadOnlyList<string> Rules,
    long Files,
    long Bytes,
    long BytesInQuarantine,
    long BytesRestored,
    long BytesPurged,
    DateTime? RestoredUtc,
    DateTime? PurgedUtc,
    Guid? RunId,
    bool UserRules)
{
    /// <summary>"na quarentena", "desfeita", "esvaziada", "parcial…".</summary>
    public string Status
    {
        get
        {
            var parts = new List<string>();
            if (BytesInQuarantine > 0) parts.Add("na quarentena");
            if (BytesRestored > 0 || (RestoredUtc is not null && BytesInQuarantine == 0 && BytesPurged == 0)) parts.Add("desfeita");
            if (PurgedUtc is not null) parts.Add("esvaziada");
            if (parts.Count == 0) return Files == 0 ? "nada movido" : "na quarentena";
            return parts.Count == 1 ? parts[0] : "parte " + string.Join(", parte ", parts);
        }
    }
}

/// <summary>Esvaziamento da Lixeira registrado no diário (irreversível; conta como espaço recuperado de verdade).</summary>
public sealed record RecycleBinEvent(DateTime Utc, long Bytes, long Items, bool Verified);

/// <summary>Totais de um mês (UTC → mês local é responsabilidade de quem mostra; aqui, mês do calendário em UTC).</summary>
public sealed record CleaningMonth(int Year, int Month, long Cleaned, long Recovered)
{
    public string Label => new DateTime(Year, Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("MMM/yy", CultureInfo.GetCultureInfo("pt-BR"));
}

/// <summary>Histórico agregado: sessões, totais e meses.</summary>
/// <param name="RecoveredTotal">Espaço recuperado DE VERDADE (quarentena esvaziada + Lixeira esvaziada).</param>
/// <param name="Recoverable">Ainda na quarentena: ocupa o disco até ser esvaziado (pode voltar).</param>
public sealed record CleaningHistoryReport(
    IReadOnlyList<CleaningSession> Sessions,
    IReadOnlyList<RecycleBinEvent> RecycleBin,
    IReadOnlyList<CleaningMonth> Months,
    long RecoveredTotal,
    long RecoveredThisMonth,
    long Recoverable,
    long CleanedTotal,
    long CleanedThisMonth,
    long RestoredTotal);

/// <summary>
/// Monta o histórico de limpezas a partir dos MANIFESTOS da quarentena (o que foi movido, devolvido e esvaziado, com bytes) e do
/// DIÁRIO (somente leitura: qual execução limpou cada lote, para o desfazer, e os esvaziamentos da Lixeira). Puro e testável.
/// </summary>
public static class CleaningHistory
{
    public static CleaningHistoryReport Build(
        IEnumerable<(QuarantineBatch Batch, CleaningLevel Level)> batches,
        IEnumerable<(IJournal Journal, CleaningLevel Level)> journals,
        DateTime nowUtc,
        int months = 12)
    {
        ArgumentNullException.ThrowIfNull(batches);
        ArgumentNullException.ThrowIfNull(journals);
        var runs = new Dictionary<(string, CleaningLevel), Guid>();
        var recycle = new List<RecycleBinEvent>();
        foreach (var (journal, level) in journals)
        {
            foreach (var run in journal.Runs())
                foreach (var e in journal.Entries(run.RunId))
                {
                    var kind = e.Action.Definition.HandlerKind;
                    if ((kind == CleaningRuleSet.HandlerKind || kind == CleaningUserRuleHandler.Kind) && e.Action.Target.TryGetValue("batch", out var b))
                        runs[(b, level)] = run.RunId;
                    else if (kind == CleaningRecycleBinHandler.Kind && e.State is JournalState.Verified or JournalState.Accepted &&
                             e.Original is { Detail: { } d } && Long(d, "bytes") is { } bytes && bytes > 0)
                        recycle.Add(new RecycleBinEvent(e.UpdatedAt.UtcDateTime, bytes, Long(d, "items") ?? 0, e.State == JournalState.Verified));
                }
        }

        var sessions = new List<CleaningSession>();
        foreach (var (batch, level) in batches)
        {
            var moved = batch.Rules.Sum(r => r.MovedBytes);
            var purged = batch.Rules.Sum(r => r.PurgedBytes);
            var inQuarantine = batch.BytesInQuarantine;
            var restored = batch.Rules.Sum(r => r.Entries.Count == 0
                ? (r.Status == QuarantineManifest.Restored ? r.MovedBytes : 0)
                : r.Entries.Where(e => e.State is QuarantineEntryState.Restored or QuarantineEntryState.RestoredRenamed).Sum(e => e.Size));
            sessions.Add(new CleaningSession(
                batch.BatchId, level, batch.CreatedUtc,
                batch.Rules.Where(r => r.MovedFiles > 0 || r.Status == QuarantineManifest.Restored || r.PurgedFiles > 0).Select(r => r.RuleTitle).Distinct(StringComparer.Ordinal).ToList(),
                batch.Rules.Sum(r => r.MovedFiles), moved, inQuarantine, restored, purged,
                batch.Rules.Where(r => r.RestoredUtc is not null).Select(r => r.RestoredUtc).Max(),
                batch.PurgedUtc,
                runs.TryGetValue((batch.BatchId, level), out var run) ? run : null,
                batch.Rules.Any(r => r.UserRule)));
        }
        sessions.Sort((a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
        recycle.Sort((a, b) => b.Utc.CompareTo(a.Utc));

        static bool SameMonth(DateTime a, DateTime b) => a.Year == b.Year && a.Month == b.Month;
        var now = nowUtc.ToUniversalTime();
        long recoveredTotal = sessions.Sum(s => s.BytesPurged) + recycle.Sum(r => r.Bytes);
        long recoveredMonth = sessions.Where(s => s.PurgedUtc is { } p && SameMonth(p, now)).Sum(s => s.BytesPurged) +
                              recycle.Where(r => SameMonth(r.Utc, now)).Sum(r => r.Bytes);

        var monthList = new List<CleaningMonth>();
        var first = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-(Math.Max(1, months) - 1));
        for (var m = first; m <= now; m = m.AddMonths(1))
        {
            var cleaned = sessions.Where(s => SameMonth(s.StartedUtc, m)).Sum(s => s.Bytes);
            var recovered = sessions.Where(s => s.PurgedUtc is { } p && SameMonth(p, m)).Sum(s => s.BytesPurged) + recycle.Where(r => SameMonth(r.Utc, m)).Sum(r => r.Bytes);
            monthList.Add(new CleaningMonth(m.Year, m.Month, cleaned, recovered));
        }

        return new CleaningHistoryReport(
            sessions, recycle, monthList,
            recoveredTotal, recoveredMonth,
            sessions.Sum(s => s.BytesInQuarantine),
            sessions.Sum(s => s.Bytes),
            sessions.Where(s => SameMonth(s.StartedUtc, now)).Sum(s => s.Bytes),
            sessions.Sum(s => s.BytesRestored));
    }

    private static long? Long(IReadOnlyDictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// CSV (separador ";", como o Excel em pt-BR abre direto; UTF-8): uma linha por limpeza e uma por esvaziamento da Lixeira.
    /// Datas em ISO 8601 UTC, bytes como números inteiros. Campos de texto escapados (aspas duplicadas; fórmulas neutralizadas).
    /// </summary>
    public static string ToCsv(CleaningHistoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append("tipo;data_utc;lote;nivel;regras;arquivos;bytes_movidos;bytes_na_quarentena;bytes_devolvidos;bytes_esvaziados;esvaziado_em_utc;situacao\r\n");
        static string D(DateTime? t) => t is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "";
        static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        foreach (var s in report.Sessions)
        {
            sb.Append(string.Join(';',
                "limpeza", D(s.StartedUtc), Field(s.BatchId), s.Level == CleaningLevel.Admin ? "administrador" : "usuario",
                Field(string.Join(" | ", s.Rules)), N(s.Files), N(s.Bytes), N(s.BytesInQuarantine), N(s.BytesRestored), N(s.BytesPurged),
                D(s.PurgedUtc), Field(s.Status)));
            sb.Append("\r\n");
        }
        foreach (var r in report.RecycleBin)
        {
            sb.Append(string.Join(';', "lixeira", D(r.Utc), "", "usuario", Field("Lixeira"), N(r.Items), "0", "0", "0", N(r.Bytes), D(r.Utc),
                Field(r.Verified ? "esvaziada" : "esvaziada (verificação incompleta)")));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Escapa um campo de CSV e neutraliza fórmulas (=, +, -, @ no início viram texto).</summary>
    internal static string Field(string value)
    {
        var v = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t') v = "'" + v;
        return v.IndexOfAny([';', '"', ',']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }
}
