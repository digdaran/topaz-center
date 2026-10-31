using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Topaz.Contracts;

namespace Topaz.Agent.State;

public sealed record RowStateEntry(string Hash, long? Scope);

public sealed record OutboxBatch(long Seq, Guid BatchId, int ItemCount, byte[] Payload, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset NextAttemptAt);

public sealed record QueueStats(int Pending, int Dead, long PendingBytes, DateTimeOffset? OldestPendingAt);

/// <summary>Row-level result of one dataset diff, committed atomically with the queued batches.</summary>
public sealed class DatasetCommit
{
    public required string Dataset { get; init; }
    public List<RecordChange> Changes { get; } = new();
    /// <summary>row_state upserts (key -&gt; new hash/scope).</summary>
    public Dictionary<string, RowStateEntry> StateUpserts { get; } = new(StringComparer.Ordinal);
    public List<string> StateDeletes { get; } = new();
    /// <summary>Incremental datasets: forget row hashes with scope below this (outside overlap).</summary>
    public long? PruneScopeBelow { get; set; }
    public Dictionary<string, string> KvUpdates { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Local durable state in SQLite (WAL, synchronous=FULL):
///   outbox     - unsent batches (gzip JSON), FIFO by seq; survives restarts and long offline periods;
///   row_state  - last sent hash per (dataset, key): the change detector;
///   kv         - watermarks, session cursor, encrypted station token.
/// Queuing a batch and advancing row_state/watermarks happen in ONE transaction, so a crash
/// either loses nothing or re-sends (the server is idempotent).
/// </summary>
public sealed class StateStore : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "agent-state.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public string DatabasePath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;");
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, null, """
            CREATE TABLE IF NOT EXISTS kv (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS row_state (
                dataset TEXT NOT NULL,
                key     TEXT NOT NULL,
                scope   INTEGER,
                hash    TEXT NOT NULL,
                PRIMARY KEY (dataset, key)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_row_state_scope ON row_state (dataset, scope);
            CREATE TABLE IF NOT EXISTS outbox (
                seq             INTEGER PRIMARY KEY AUTOINCREMENT,
                batch_id        TEXT NOT NULL UNIQUE,
                created_at      TEXT NOT NULL,
                item_count      INTEGER NOT NULL,
                payload         BLOB NOT NULL,
                status          TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','dead')),
                attempts        INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NOT NULL,
                last_error      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_status ON outbox (status, seq);
            """);
        SetKv(c, null, "schema_version", "1");
    }

    // ---------- kv ----------

    public string? GetKv(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetKv(string key, string? value)
    {
        using var c = Open();
        SetKv(c, null, key, value);
    }

    public long GetLong(string key, long fallback = 0) =>
        long.TryParse(GetKv(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // ---------- row state ----------

    public Dictionary<string, RowStateEntry> LoadRowState(string dataset) =>
        LoadRowState(dataset, "", _ => { });

    public Dictionary<string, RowStateEntry> LoadRowStateForScopes(string dataset, IReadOnlyCollection<long> scopes)
    {
        if (scopes.Count == 0) return new(StringComparer.Ordinal);
        var names = scopes.Select((_, i) => $"$s{i}").ToArray();
        return LoadRowState(dataset, $" AND scope IN ({string.Join(',', names)})", cmd =>
        {
            var i = 0;
            foreach (var s in scopes) cmd.Parameters.AddWithValue(names[i++], s);
        });
    }

    public Dictionary<string, RowStateEntry> LoadRowStateFromScope(string dataset, long minScopeInclusive) =>
        LoadRowState(dataset, " AND scope >= $min", cmd => cmd.Parameters.AddWithValue("$min", minScopeInclusive));

    private Dictionary<string, RowStateEntry> LoadRowState(string dataset, string filter, Action<SqliteCommand> bind)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, hash, scope FROM row_state WHERE dataset = $d" + filter;
        cmd.Parameters.AddWithValue("$d", dataset);
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, RowStateEntry>(StringComparer.Ordinal);
        while (r.Read())
            result[r.GetString(0)] = new RowStateEntry(r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2));
        return result;
    }

    // ---------- commit (queue + state, atomically) ----------

    /// <summary>Queues the changes as batches and applies state updates in one SQLite transaction.</summary>
    public int Commit(DatasetCommit commit, int maxItemsPerBatch, string agentVersion, DateTimeOffset now)
    {
        _lock.Wait();
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var batches = 0;
            foreach (var chunk in commit.Changes.Chunk(maxItemsPerBatch))
            {
                Enqueue(c, tx, chunk, agentVersion, now);
                batches++;
            }
            foreach (var (key, entry) in commit.StateUpserts)
            {
                using var cmd = Cmd(c, tx, """
                    INSERT INTO row_state (dataset, key, scope, hash) VALUES ($d, $k, $s, $h)
                    ON CONFLICT (dataset, key) DO UPDATE SET scope = excluded.scope, hash = excluded.hash
                    """);
                cmd.Parameters.AddWithValue("$d", commit.Dataset);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$s", (object?)entry.Scope ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$h", entry.Hash);
                cmd.ExecuteNonQuery();
            }
            foreach (var key in commit.StateDeletes)
            {
                using var cmd = Cmd(c, tx, "DELETE FROM row_state WHERE dataset = $d AND key = $k");
                cmd.Parameters.AddWithValue("$d", commit.Dataset);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }
            if (commit.PruneScopeBelow is { } below)
            {
                using var cmd = Cmd(c, tx, "DELETE FROM row_state WHERE dataset = $d AND scope < $b");
                cmd.Parameters.AddWithValue("$d", commit.Dataset);
                cmd.Parameters.AddWithValue("$b", below);
                cmd.ExecuteNonQuery();
            }
            foreach (var (k, v) in commit.KvUpdates) SetKv(c, tx, k, v);
            tx.Commit();
            return batches;
        }
        finally { _lock.Release(); }
    }

    private static void Enqueue(SqliteConnection c, SqliteTransaction tx, RecordChange[] items, string agentVersion, DateTimeOffset now)
    {
        var batchId = Guid.NewGuid();
        long seq;
        using (var cmd = Cmd(c, tx, """
            INSERT INTO outbox (batch_id, created_at, item_count, payload, next_attempt_at)
            VALUES ($b, $t, $n, zeroblob(0), $t);
            SELECT last_insert_rowid();
            """))
        {
            cmd.Parameters.AddWithValue("$b", batchId.ToString());
            cmd.Parameters.AddWithValue("$t", Iso(now));
            cmd.Parameters.AddWithValue("$n", items.Length);
            seq = (long)cmd.ExecuteScalar()!;
        }
        var envelope = new BatchEnvelope
        {
            BatchId = batchId,
            Seq = seq,
            CreatedAt = now,
            AgentVersion = agentVersion,
            Items = items.ToList(),
        };
        using (var cmd = Cmd(c, tx, "UPDATE outbox SET payload = $p WHERE seq = $s"))
        {
            cmd.Parameters.AddWithValue("$p", Gzip(envelope));
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.ExecuteNonQuery();
        }
    }

    public static byte[] Gzip(BatchEnvelope envelope)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, envelope, Protocol.Json);
        return ms.ToArray();
    }

    public static BatchEnvelope Gunzip(byte[] payload)
    {
        using var gz = new GZipStream(new MemoryStream(payload), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<BatchEnvelope>(gz, Protocol.Json)!;
    }

    // ---------- outbox ----------

    /// <summary>Oldest pending batch. Strict FIFO: a later batch is never sent before an earlier one.</summary>
    public OutboxBatch? PeekPending()
    {
        using var c = Open();
        using var cmd = Cmd(c, null, """
            SELECT seq, batch_id, item_count, payload, attempts, created_at, next_attempt_at
            FROM outbox WHERE status = 'pending' ORDER BY seq LIMIT 1
            """);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new OutboxBatch(r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetInt32(2), (byte[])r.GetValue(3),
            r.GetInt32(4), DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture));
    }

    /// <summary>Server confirmed the batch: remove it.</summary>
    public void Ack(long seq)
    {
        using var c = Open();
        using var cmd = Cmd(c, null, "DELETE FROM outbox WHERE seq = $s");
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.ExecuteNonQuery();
    }

    public void Defer(long seq, DateTimeOffset nextAttemptAt, string error, bool countAttempt = true)
    {
        using var c = Open();
        using var cmd = Cmd(c, null, $"""
            UPDATE outbox SET {(countAttempt ? "attempts = attempts + 1," : "")}
                next_attempt_at = $n, last_error = $e WHERE seq = $s
            """);
        cmd.Parameters.AddWithValue("$s", seq);
        cmd.Parameters.AddWithValue("$n", Iso(nextAttemptAt));
        cmd.Parameters.AddWithValue("$e", Truncate(error, 2000));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The server permanently rejected the batch. It is kept for diagnostics, and the row hashes of its
    /// items are forgotten so the next cycle re-reads and re-sends those rows (self-healing, no silent loss).
    /// </summary>
    public void MarkDead(long seq, string error)
    {
        _lock.Wait();
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            byte[] payload;
            using (var cmd = Cmd(c, tx, "SELECT payload FROM outbox WHERE seq = $s"))
            {
                cmd.Parameters.AddWithValue("$s", seq);
                payload = cmd.ExecuteScalar() as byte[] ?? [];
            }
            if (payload.Length > 0)
            {
                foreach (var item in Gunzip(payload).Items)
                {
                    using var del = Cmd(c, tx, "DELETE FROM row_state WHERE dataset = $d AND key = $k");
                    del.Parameters.AddWithValue("$d", item.Dataset);
                    del.Parameters.AddWithValue("$k", item.Key);
                    del.ExecuteNonQuery();
                }
            }
            using (var cmd = Cmd(c, tx, "UPDATE outbox SET status = 'dead', attempts = attempts + 1, last_error = $e WHERE seq = $s"))
            {
                cmd.Parameters.AddWithValue("$s", seq);
                cmd.Parameters.AddWithValue("$e", Truncate(error, 2000));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { _lock.Release(); }
    }

    /// <summary>Moves dead batches back to the queue (after the server-side cause was fixed).</summary>
    public int RequeueDead(DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = Cmd(c, null, "UPDATE outbox SET status = 'pending', next_attempt_at = $n WHERE status = 'dead'");
        cmd.Parameters.AddWithValue("$n", Iso(now));
        return cmd.ExecuteNonQuery();
    }

    public QueueStats GetQueueStats()
    {
        using var c = Open();
        using var cmd = Cmd(c, null, """
            SELECT
              COALESCE(SUM(status = 'pending'), 0),
              COALESCE(SUM(status = 'dead'), 0),
              COALESCE(SUM(CASE WHEN status = 'pending' THEN length(payload) END), 0),
              MIN(CASE WHEN status = 'pending' THEN created_at END)
            FROM outbox
            """);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new QueueStats(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2),
            r.IsDBNull(3) ? null : DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<(long Seq, int Items, int Attempts, string? Error)> ListDead()
    {
        using var c = Open();
        using var cmd = Cmd(c, null, "SELECT seq, item_count, attempts, last_error FROM outbox WHERE status = 'dead' ORDER BY seq");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, int, int, string?)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    // ---------- helpers ----------

    private static void SetKv(SqliteConnection c, SqliteTransaction? tx, string key, string? value)
    {
        using var cmd = Cmd(c, tx, value is null
            ? "DELETE FROM kv WHERE key = $k"
            : "INSERT INTO kv (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value");
        cmd.Parameters.AddWithValue("$k", key);
        if (value is not null) cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = Cmd(c, tx, sql);
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        _lock.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
