using System.Net;
using System.Text.Json.Nodes;
using Topaz.Agent.Configuration;
using Topaz.Agent.Source;

namespace Topaz.Agent.Tests;

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "topaz-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

/// <summary>In-memory Topaz: tables of rows, sessions with open/closed state.</summary>
public sealed class FakeTopaz : ITopazSource
{
    public Dictionary<string, List<SourceRow>> Tables { get; } = new();
    public SortedDictionary<long, bool> Sessions { get; } = new(); // id -> open
    public bool Fail { get; set; }
    public List<IReadOnlyList<long>> SessionReads { get; } = new();

    public static SourceRow Row(string key, long? scope, object fields)
    {
        var data = JsonSerializer(fields);
        return new SourceRow(key, scope, RowSerializer.Hash(data.ToJsonString(RowSerializer.CanonicalJson)), data);
    }

    private static JsonObject JsonSerializer(object fields) =>
        System.Text.Json.JsonSerializer.SerializeToNode(fields)!.AsObject();

    public Task<ITopazReadSession> OpenAsync(CancellationToken ct) =>
        Fail ? throw new InvalidOperationException("Topaz DB unavailable") : Task.FromResult<ITopazReadSession>(new Session(this));

    private sealed class Session(FakeTopaz t) : ITopazReadSession
    {
        public Task<string> GetServerVersionAsync(CancellationToken ct) => Task.FromResult("2.5.9-fake");

        public Task<IReadOnlyDictionary<string, HashSet<string>>> GetColumnsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, HashSet<string>>>(t.Tables.Keys.ToDictionary(k => k, _ => new HashSet<string>
                { "ID", "SessionID", "EventID", "OrderID", "FuelID" }));

        public Task<SessionWindow> GetSessionWindowAsync(long cursor, int closedWindow, int backfillCount, CancellationToken ct)
        {
            var open = t.Sessions.Where(s => s.Value).Select(s => s.Key).ToList();
            var closed = t.Sessions.Where(s => !s.Value).Select(s => s.Key).OrderByDescending(x => x).Take(closedWindow).ToList();
            var backfill = t.Sessions.Keys.Where(k => k > cursor).Take(backfillCount).ToList();
            var all = open.Concat(closed).Concat(backfill).Distinct().Order().ToList();
            long? latest = t.Sessions.Count > 0 ? t.Sessions.Keys.Max() : null;
            return Task.FromResult(new SessionWindow(all, backfill.Count > 0 ? backfill.Max() : null, latest, latest is { } l && t.Sessions[l]));
        }

        public Task<IReadOnlyList<SourceRow>> ReadSnapshotAsync(DatasetDefinition d, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SourceRow>>(t.Tables.GetValueOrDefault(d.Table, []).ToList());

        public Task<IReadOnlyList<SourceRow>> ReadSessionsAsync(DatasetDefinition d, IReadOnlyList<long> ids, CancellationToken ct)
        {
            t.SessionReads.Add(ids);
            return Task.FromResult<IReadOnlyList<SourceRow>>(t.Tables.GetValueOrDefault(d.Table, []).Where(r => r.Scope is { } s && ids.Contains(s)).ToList());
        }

        public Task<IReadOnlyList<SourceRow>> ReadIncrementalAsync(DatasetDefinition d, long from, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SourceRow>>(t.Tables.GetValueOrDefault(d.Table, [])
                .Where(r => r.Scope > from).OrderBy(r => r.Scope).Take(limit).ToList());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Scriptable HTTP endpoint: each request is answered by the current responder.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.Accepted);
    public List<(string Path, string? IdempotencyKey)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.AbsolutePath,
            request.Headers.TryGetValues("Idempotency-Key", out var v) ? v.Single() : null));
        return Task.FromResult(Responder(request));
    }

    public static HttpResponseMessage Throw() => throw new HttpRequestException("network is unreachable");
}
