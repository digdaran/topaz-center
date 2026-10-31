using Microsoft.Extensions.Logging.Abstractions;
using Topaz.Agent.Collection;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Sending;
using Topaz.Agent.Source;
using Topaz.Agent.State;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// Contract + end-to-end: the real agent pipeline (collector, SQLite queue, sender, enrollment)
/// talks to the real central API in-process; data lands in the normalized tables.
/// </summary>
public sealed class EndToEndTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private sealed class OneTableTopaz(List<SourceRow> rows) : ITopazSource, ITopazReadSession
    {
        public List<SourceRow> Rows { get; set; } = rows;
        public Task<ITopazReadSession> OpenAsync(CancellationToken ct) => Task.FromResult<ITopazReadSession>(this);
        public Task<string> GetServerVersionAsync(CancellationToken ct) => Task.FromResult("2.5.9");
        public Task<IReadOnlyDictionary<string, HashSet<string>>> GetColumnsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, HashSet<string>>>(new Dictionary<string, HashSet<string>> { ["flOrders"] = ["OrderID", "SessionID"] });
        public Task<SessionWindow> GetSessionWindowAsync(long c, int w, int b, CancellationToken ct) => Task.FromResult(new SessionWindow([1], c < 1 ? 1 : null, 1, true));
        public Task<IReadOnlyList<SourceRow>> ReadSnapshotAsync(DatasetDefinition d, CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceRow>>([]);
        public Task<IReadOnlyList<SourceRow>> ReadSessionsAsync(DatasetDefinition d, IReadOnlyList<long> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceRow>>(Rows);
        public Task<IReadOnlyList<SourceRow>> ReadIncrementalAsync(DatasetDefinition d, long f, int l, CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceRow>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SourceRow Order(int id, decimal volume)
    {
        var data = new System.Text.Json.Nodes.JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = 1, ["Volume"] = volume, ["DateTime"] = RowSerializer.FormatTimestamp(new DateTime(2026, 10, 31, 12, 0, 0), TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow")),
        };
        return new SourceRow(id.ToString(), 1, RowSerializer.Hash(data.ToJsonString(RowSerializer.CanonicalJson)), data);
    }

    [PostgresFact]
    public async Task Agent_enrolls_collects_and_delivers_through_outage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "topaz-e2e-" + Guid.NewGuid().ToString("N"));
        var options = new AgentOptions
        {
            Server = new ServerOptions { Url = "http://localhost", AllowInsecureHttp = true, EnrollmentKey = CentralFixture.EnrollKey, RetryBaseSeconds = 1, RetryMaxSeconds = 2 },
            Station = new StationOptions { Name = "АЗС-e2e" },
        };
        var catalog = new DatasetCatalog { Datasets = { new DatasetDefinition { Table = "flOrders", Kind = DatasetKind.Session, Key = ["OrderID"] } } };
        var topaz = new OneTableTopaz([Order(1, 0m), Order(2, 10m)]);
        using var state = new StateStore(dir);
        var status = new AgentStatus();
        var collector = new Collector(topaz, state, catalog, options, status, TimeProvider.System, NullLogger<Collector>.Instance);

        // Outage: the HTTP handler fails until we "restore" the network.
        var online = false;
        var handler = new SwitchableHandler(fx.Factory!.Server.CreateHandler(), () => online);
        var client = new CentralClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, options);
        var tokens = new TokenStore(state);
        var sender = new Sender(state, client, tokens, options, status, TimeProvider.System, NullLogger<Sender>.Instance);

        await collector.RunCycleAsync(default);
        var (step, _) = await sender.StepAsync(default);
        Assert.Equal(SendStep.Waiting, step); // cannot even enroll while offline
        Assert.Equal(1, state.GetQueueStats().Pending);

        // Topaz keeps changing while offline.
        topaz.Rows = [Order(1, 23.03m), Order(2, 10m), Order(3, 5m)];
        await collector.RunCycleAsync(default);
        Assert.Equal(2, state.GetQueueStats().Pending);

        online = true;
        for (var i = 0; i < 20 && state.GetQueueStats().Pending > 0; i++)
        {
            var (_, delay) = await sender.StepAsync(default);
            if (delay > TimeSpan.Zero) await Task.Delay(delay > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : delay);
        }
        Assert.Equal(0, state.GetQueueStats().Pending);
        await fx.WaitProcessedAsync();

        var sid = Guid.Parse(tokens.StationId!);
        Assert.Equal(3, await fx.QueryAsync<long>("SELECT count(*) FROM fuel_sales WHERE station_id = @sid", new { sid }));
        Assert.Equal(23.03m, await fx.QueryAsync<decimal>("SELECT volume FROM fuel_sales WHERE station_id = @sid AND order_id = 1", new { sid }));
        Directory.Delete(dir, true);
    }

    private sealed class SwitchableHandler(HttpMessageHandler inner, Func<bool> online) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            online() ? base.SendAsync(request, ct) : throw new HttpRequestException("network is unreachable");
    }
}
