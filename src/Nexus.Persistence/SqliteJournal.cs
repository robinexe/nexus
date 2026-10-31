using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Nexus.Core;

namespace Nexus.Persistence;

/// <summary>
/// Diário durável em SQLite (WAL, synchronous=FULL). Cada transição é gravada e confirmada
/// antes de retornar, de modo que "Applying" está em disco antes de qualquer escrita no sistema.
/// Esquema versionado por PRAGMA user_version; migrações são aditivas e idempotentes.
/// Seguro para uso entre threads: uma única conexão protegida por trava (a conexão do SQLite não é thread-safe).
/// Para coordenar PROCESSOS (interface, CLI, broker) use <see cref="FileRunCoordinator"/>.
/// </summary>
public sealed class SqliteJournal : IJournal, IDisposable
{
    /// <summary>
    /// 1 = esquema inicial. 2 = estado <see cref="JournalState.Restoring"/> (sem mudança de tabela; o número impede que
    /// uma versão antiga, que não conhece o estado, abra o diário e falhe no meio de uma recuperação).
    /// 3 = estado <see cref="JournalState.Accepted"/> (mesmo motivo; sem mudança de tabela).
    /// </summary>
    public const int SchemaVersion = 3;
    private const int OldestReadableSchema = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly SqliteConnection _db;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    public SqliteJournal(string path, TimeProvider? clock = null)
        : this(path, clock, readOnly: false)
    {
    }

    private SqliteJournal(string path, TimeProvider? clock, bool readOnly)
    {
        _clock = clock ?? TimeProvider.System;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _db.Open();
        try
        {
            if (readOnly)
            {
                // Nenhum PRAGMA de escrita e nenhuma migração: só confere se é um diário que esta versão entende.
                var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
                if (version < OldestReadableSchema || version > SchemaVersion)
                    throw new InvalidOperationException($"Arquivo não é um diário NEXUS compatível (schema {version}, esperado {OldestReadableSchema}–{SchemaVersion}).");
                return;
            }
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=FULL;");
            Exec("PRAGMA foreign_keys=ON;");
            Migrate();
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Abre um diário existente sem nenhuma escrita (para runs/show/scan). Não cria o arquivo, não migra
    /// o esquema e não troca o journal_mode. Qualquer tentativa de escrita falha no SQLite.
    /// </summary>
    public static SqliteJournal OpenReadOnly(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Diário não encontrado.", path);
        return new SqliteJournal(path, null, readOnly: true);
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Diário criado por versão mais nova (schema {version}).");
        if (version < 1)
        {
            using var tx = _db.BeginTransaction();
            Exec("""
                CREATE TABLE IF NOT EXISTS runs(
                  run_id TEXT PRIMARY KEY, profile TEXT NOT NULL, catalog_version TEXT NOT NULL,
                  started_at TEXT NOT NULL, ended_at TEXT NULL);
                CREATE TABLE IF NOT EXISTS entries(
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  run_id TEXT NOT NULL REFERENCES runs(run_id),
                  seq INTEGER NOT NULL,
                  action_json TEXT NOT NULL,
                  state TEXT NOT NULL,
                  original_json TEXT NULL,
                  observed_json TEXT NULL,
                  note TEXT NULL,
                  updated_at TEXT NOT NULL,
                  UNIQUE(run_id, seq));
                CREATE TABLE IF NOT EXISTS transitions(
                  entry_id INTEGER NOT NULL REFERENCES entries(id),
                  from_state TEXT NOT NULL, to_state TEXT NOT NULL, at TEXT NOT NULL, note TEXT NULL);
                PRAGMA user_version = 1;
                """, tx);
            tx.Commit();
            version = 1;
        }
        if (version < SchemaVersion)
            Exec($"PRAGMA user_version = {SchemaVersion};");
    }

    /// <summary>Versão da biblioteca SQLite nativa efetivamente carregada (ex.: "3.49.1").</summary>
    public static string NativeSqliteVersion()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "?";
    }

    public Guid BeginRun(string profile, string catalogVersion)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid();
            Exec("INSERT INTO runs(run_id, profile, catalog_version, started_at) VALUES ($id,$p,$c,$t);", null,
                ("$id", id.ToString("D")), ("$p", profile), ("$c", catalogVersion), ("$t", Now()));
            return id;
        }
    }

    public void EndRun(Guid runId)
    {
        lock (_gate)
            Exec("UPDATE runs SET ended_at=$t WHERE run_id=$id;", null, ("$t", Now()), ("$id", runId.ToString("D")));
    }

    public long AddPlanned(Guid runId, int sequence, ActionInstance action)
    {
        lock (_gate)
        {
            Exec("INSERT INTO entries(run_id, seq, action_json, state, updated_at) VALUES ($r,$s,$a,$st,$t);", null,
                ("$r", runId.ToString("D")), ("$s", sequence), ("$a", JsonSerializer.Serialize(action, Json)),
                ("$st", nameof(JournalState.Planned)), ("$t", Now()));
            return Convert.ToInt64(Scalar("SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
        }
    }

    public void Transition(long entryId, JournalState targetState, StateSnapshot? original = null, StateSnapshot? observed = null, string? note = null)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            var current = Get(entryId, tx);
            JournalTransitions.Ensure(current.State, targetState);
            if (current.Original is not null && original is not null)
                throw new InvalidOperationException("Backup original é imutável depois de gravado.");

            Exec("""
                UPDATE entries SET state=$st,
                  original_json=COALESCE($o, original_json),
                  observed_json=COALESCE($ob, observed_json),
                  note=COALESCE($n, note), updated_at=$t
                WHERE id=$id;
                """, tx,
                ("$st", targetState.ToString()),
                ("$o", original is null ? null : JsonSerializer.Serialize(original, Json)),
                ("$ob", observed is null ? null : JsonSerializer.Serialize(observed, Json)),
                ("$n", note), ("$t", Now()), ("$id", entryId));
            Exec("INSERT INTO transitions(entry_id, from_state, to_state, at, note) VALUES ($id,$f,$to,$t,$n);", tx,
                ("$id", entryId), ("$f", current.State.ToString()), ("$to", targetState.ToString()), ("$t", Now()), ("$n", note));
            tx.Commit();
        }
    }

    public JournalEntry Get(long entryId)
    {
        lock (_gate)
            return Get(entryId, null);
    }

    private JournalEntry Get(long entryId, SqliteTransaction? tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, run_id, seq, action_json, state, original_json, observed_json, note, updated_at FROM entries WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException($"Entrada {entryId} não existe.");
        return Map(r);
    }

    public IReadOnlyList<JournalEntry> Entries(Guid runId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, run_id, seq, action_json, state, original_json, observed_json, note, updated_at FROM entries WHERE run_id=$r ORDER BY seq;";
            cmd.Parameters.AddWithValue("$r", runId.ToString("D"));
            using var r = cmd.ExecuteReader();
            var list = new List<JournalEntry>();
            while (r.Read()) list.Add(Map(r));
            return list;
        }
    }

    public IReadOnlyList<RunInfo> Runs()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT run_id, profile, catalog_version, started_at, ended_at FROM runs ORDER BY started_at;";
            using var r = cmd.ExecuteReader();
            var list = new List<RunInfo>();
            while (r.Read())
                list.Add(new RunInfo(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), Parse(r.GetString(3)),
                    r.IsDBNull(4) ? null : Parse(r.GetString(4))));
            return list;
        }
    }

    private static JournalEntry Map(SqliteDataReader r) => new(
        r.GetInt64(0),
        Guid.Parse(r.GetString(1)),
        r.GetInt32(2),
        JsonSerializer.Deserialize<ActionInstance>(r.GetString(3), Json)!,
        Enum.Parse<JournalState>(r.GetString(4)),
        r.IsDBNull(5) ? null : JsonSerializer.Deserialize<StateSnapshot>(r.GetString(5), Json),
        r.IsDBNull(6) ? null : JsonSerializer.Deserialize<StateSnapshot>(r.GetString(6), Json),
        r.IsDBNull(7) ? null : r.GetString(7),
        Parse(r.GetString(8)));

    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void Exec(string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}

/// <summary>Trava simples por arquivo (sem registro de dono). Prefira <see cref="FileRunCoordinator"/>, usado por todos os executores.
/// O arquivo não é apagado ao fechar: DeleteOnClose cria uma corrida entre exclusão e nova abertura.</summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream _stream;

    private RunLock(FileStream s) => _stream = s;

    public static RunLock? TryAcquire(string lockPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
            return new RunLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
