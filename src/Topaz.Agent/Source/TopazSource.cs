using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using Topaz.Agent.Configuration;

namespace Topaz.Agent.Source;

/// <summary>One source row ready for diffing.</summary>
public sealed record SourceRow(string Key, long? Scope, string Hash, JsonObject Data);

/// <summary>Sessions (shifts) to read this cycle.</summary>
public sealed record SessionWindow(IReadOnlyList<long> SessionIds, long? BackfillMax, long? LatestSessionId, bool LatestOpen);

public interface ITopazSource
{
    /// <summary>Opens a read-only snapshot of the Topaz database for one collection cycle.</summary>
    Task<ITopazReadSession> OpenAsync(CancellationToken ct);
}

public interface ITopazReadSession : IAsyncDisposable
{
    Task<string> GetServerVersionAsync(CancellationToken ct);
    /// <summary>Table name -&gt; column names, to skip datasets missing in this Topaz version.</summary>
    Task<IReadOnlyDictionary<string, HashSet<string>>> GetColumnsAsync(CancellationToken ct);
    Task<SessionWindow> GetSessionWindowAsync(long backfillCursor, int closedWindow, int backfillCount, CancellationToken ct);
    // Rows are streamed one by one: a table of any size never sits in memory as a whole.
    IAsyncEnumerable<SourceRow> ReadSnapshotAsync(DatasetDefinition d, CancellationToken ct);
    IAsyncEnumerable<SourceRow> ReadSessionsAsync(DatasetDefinition d, IReadOnlyList<long> sessionIds, CancellationToken ct);
    IAsyncEnumerable<SourceRow> ReadIncrementalAsync(DatasetDefinition d, long fromExclusive, int limit, CancellationToken ct);
}

public static class AsyncRows
{
    /// <summary>For small reads only (a few rows): collects the stream into a list.</summary>
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct)) list.Add(item);
        return list;
    }
}

public sealed class FirebirdTopazSource(AgentOptions options, ILogger<FirebirdTopazSource>? log = null) : ITopazSource
{
    public async Task<ITopazReadSession> OpenAsync(CancellationToken ct)
    {
        var connection = new FbConnection(options.Firebird.BuildConnectionString());
        try
        {
            await connection.OpenAsync(ct);
            // Read-only snapshot transaction: the engine itself rejects any write, and every
            // dataset in the cycle sees one consistent state (headers match their lines).
            var tx = await connection.BeginTransactionAsync(new FbTransactionOptions
            {
                TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
            }, ct);
            return new FirebirdReadSession(connection, tx, options.ResolveTimeZone(), options.Collection.MaxBlobBytes, log);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

internal sealed class FirebirdReadSession(FbConnection connection, FbTransaction tx, TimeZoneInfo tz, int maxBlobBytes, ILogger? log) : ITopazReadSession
{
    private static readonly HashSet<string> WarnedBlobs = new(StringComparer.Ordinal);

    public async Task<string> GetServerVersionAsync(CancellationToken ct) =>
        Convert.ToString(await Scalar(SqlBuilder.ServerVersion, ct)) ?? "";

    public async Task<IReadOnlyDictionary<string, HashSet<string>>> GetColumnsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var cmd = Command(SqlBuilder.Columns);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var table = r.GetString(0);
            if (!result.TryGetValue(table, out var cols)) result[table] = cols = new HashSet<string>(StringComparer.Ordinal);
            cols.Add(r.GetString(1));
        }
        return result;
    }

    public async Task<SessionWindow> GetSessionWindowAsync(long backfillCursor, int closedWindow, int backfillCount, CancellationToken ct)
    {
        var open = await Ids(SqlBuilder.SessionsOpen, null, ct);
        var closed = closedWindow > 0 ? await Ids(SqlBuilder.SessionsLastClosed(closedWindow), null, ct) : [];
        var backfill = backfillCount > 0
            ? await Ids(SqlBuilder.SessionsAfter(backfillCount), c => c.Parameters.Add("@cursor", FbDbType.BigInt).Value = backfillCursor, ct)
            : [];
        var latest = await Scalar(SqlBuilder.SessionsMax, ct);
        long? latestId = latest is null or DBNull ? null : Convert.ToInt64(latest);

        var all = open.Concat(closed).Concat(backfill).Distinct().OrderBy(x => x).ToList();
        return new SessionWindow(all, backfill.Count > 0 ? backfill.Max() : null, latestId,
            latestId is { } l && open.Contains(l));
    }

    public IAsyncEnumerable<SourceRow> ReadSnapshotAsync(DatasetDefinition d, CancellationToken ct) =>
        Rows(d, SqlBuilder.Snapshot(d), _ => { }, hasScope: false, ct);

    public IAsyncEnumerable<SourceRow> ReadSessionsAsync(DatasetDefinition d, IReadOnlyList<long> sessionIds, CancellationToken ct)
    {
        if (sessionIds.Count == 0) return Empty();
        return Rows(d, SqlBuilder.Session(d, sessionIds.Count), cmd =>
        {
            for (var i = 0; i < sessionIds.Count; i++)
                cmd.Parameters.Add($"@s{i}", FbDbType.BigInt).Value = sessionIds[i];
        }, hasScope: true, ct);

        static async IAsyncEnumerable<SourceRow> Empty() { await Task.CompletedTask; yield break; }
    }

    public IAsyncEnumerable<SourceRow> ReadIncrementalAsync(DatasetDefinition d, long fromExclusive, int limit, CancellationToken ct) =>
        Rows(d, SqlBuilder.Incremental(d, limit),
            cmd => cmd.Parameters.Add("@from", FbDbType.BigInt).Value = fromExclusive, hasScope: true, ct);

    private async IAsyncEnumerable<SourceRow> Rows(DatasetDefinition d, string sql, Action<FbCommand> bind, bool hasScope, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = Command(sql);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var exclude = new HashSet<string>(d.Exclude, StringComparer.Ordinal);
        var dataOrdinals = new List<int>();
        var keyOrdinals = new int[d.Key.Length];
        var scopeOrdinal = -1;
        // Ordinal scan: the first occurrence of a name wins (t.* comes before the scope alias).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (name == SqlBuilder.ScopeAlias) { scopeOrdinal = i; continue; }
            if (!seen.Add(name) || exclude.Contains(name)) continue;
            dataOrdinals.Add(i);
        }
        for (var k = 0; k < d.Key.Length; k++)
        {
            keyOrdinals[k] = reader.GetOrdinal(d.Key[k]);
        }
        if (hasScope && scopeOrdinal < 0) throw new InvalidOperationException($"{d.DatasetName}: scope column missing");

        var keyValues = new object?[keyOrdinals.Length];
        while (await reader.ReadAsync(ct))
        {
            for (var k = 0; k < keyOrdinals.Length; k++) keyValues[k] = reader.GetValue(keyOrdinals[k]);
            var data = RowSerializer.ReadRow(reader, dataOrdinals, tz, maxBlobBytes, (column, bytes) => Oversized(d, column, bytes));
            var json = data.ToJsonString(RowSerializer.CanonicalJson);
            long? scope = hasScope && !reader.IsDBNull(scopeOrdinal) ? Convert.ToInt64(reader.GetValue(scopeOrdinal)) : null;
            yield return new SourceRow(RowSerializer.KeyOf(keyValues), scope, RowSerializer.Hash(json), data);
        }
    }

    /// <summary>An attachment larger than Collection:MaxBlobBytes is sent as null; said once per table and column.</summary>
    private void Oversized(DatasetDefinition d, string column, long bytes)
    {
        lock (WarnedBlobs)
            if (!WarnedBlobs.Add(d.DatasetName + "." + column)) return;
        log?.LogWarning("{Dataset}.{Column}: a value of {Bytes} bytes is over Collection:MaxBlobBytes and is sent as null", d.DatasetName, column, bytes);
    }

    private async Task<List<long>> Ids(string sql, Action<FbCommand>? bind, CancellationToken ct)
    {
        await using var cmd = Command(sql);
        bind?.Invoke(cmd);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<long>();
        while (await r.ReadAsync(ct)) list.Add(Convert.ToInt64(r.GetValue(0)));
        return list;
    }

    private async Task<object?> Scalar(string sql, CancellationToken ct)
    {
        await using var cmd = Command(sql);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private FbCommand Command(string sql)
    {
        // Defence in depth on top of the read-only transaction.
        if (!sql.TrimStart().StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT statements may be executed against Topaz.");
        return new FbCommand(sql, connection, tx) { CommandTimeout = 300 };
    }

    public async ValueTask DisposeAsync()
    {
        try { await tx.CommitAsync(); } catch { /* read-only: nothing to keep */ }
        await tx.DisposeAsync();
        await connection.DisposeAsync();
    }
}
