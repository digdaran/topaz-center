using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// 1.7: tanks with an electronic level gauge get the measured stock at every shift close: the source of the stock,
/// book against measurement per shift, losses over the period and the shortage alert.
/// </summary>
public sealed class TankMeasurementTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };
    private static string Ts(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz");

    [PostgresFact]
    public async Task Gauge_measurements_show_the_stock_source_losses_and_shortages()
    {
        var org = await fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at, name LIMIT 1");
        var st = await fx.EnrollAsync("АЗС-уровнемер", org);
        var t0 = DateTimeOffset.UtcNow.AddDays(-2);
        RecordChange Shift(int id) => Row("sysSessions", $"{id}", new JsonObject { ["SessionID"] = id, ["SessionNum"] = id, ["UserName"] = "Оператор",
            ["StartDateTime"] = Ts(t0.AddHours(12 * (id - 1))), ["EndDateTime"] = Ts(t0.AddHours(12 * id - 1)), ["Deleted"] = 0 });
        RecordChange State(int session, int tank, decimal start, decimal pump, decimal? fact) => Row("flSesTanks", $"{session}|{tank}", new JsonObject
        {
            ["SessionID"] = session, ["TankID"] = tank, ["TankNum"] = tank, ["FuelName"] = "ДТ", ["StartFuelVolume"] = start, ["ReceptVolume"] = 0,
            ["PumpVolume"] = pump, ["FactFuelVolume"] = fact, ["Height"] = fact is null ? null : 1100, ["Density"] = fact is null ? null : 832,
        });
        var items = new List<RecordChange>
        {
            Row("dcTanks", "1", new JsonObject { ["TankID"] = 1, ["Num"] = 1, ["FuelID"] = 1, ["MaxVolume"] = 10000, ["LevelGauge"] = "{9ACDF4EE-164E-446D-98E7-A3ED38FB43C2}", ["IsNonDimensional"] = 0, ["Deleted"] = 0 }),
            Row("dcTanks", "2", new JsonObject { ["TankID"] = 2, ["Num"] = 2, ["FuelID"] = 1, ["MaxVolume"] = 10000, ["LevelGauge"] = "", ["IsNonDimensional"] = 0, ["Deleted"] = 0 }),
            Shift(1), Shift(2), Shift(3),
            State(1, 1, 1000, 100, 870),   // book 900, measured 870: 30 l short
            State(2, 1, 870, 50, 818),     // book 820, measured 818: 2 l short
            State(3, 1, 818, 0, 1818),     // a truck not entered in Topaz: 1 000 l surplus
            State(1, 2, 5000, 100, null),  // a virtual tank: book only
            State(2, 2, 4900, 100, null),
        };
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            (await fx.Client(st.Token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        }
        await fx.WaitProcessedAsync();

        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("gauge-owner", "o", Roles.Owner, "gauge-password-1", [], org); } catch (PostgresException) { }
        var owner = fx.Factory.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "gauge-owner", password = "gauge-password-1" })).EnsureSuccessStatusCode();

        // Shortage alert: only the 30 l shift is above the 20 l default.
        (await owner.PostAsync("/api/v1/ui/alerts/run", null)).EnsureSuccessStatusCode();
        Assert.Equal(30m, await fx.QueryAsync<decimal>("SELECT value FROM alerts WHERE station_id = @s AND rule_code = 'tank_shortage'", new { s = st.StationId }));
        Assert.Equal(1, await fx.QueryAsync<long>("SELECT count(*) FROM alerts WHERE station_id = @s AND rule_code = 'tank_shortage'", new { s = st.StationId }));
        Assert.Equal(1000m, await fx.QueryAsync<decimal>("SELECT value FROM alerts WHERE station_id = @s AND rule_code = 'tank_surplus'", new { s = st.StationId }));

        var from = t0.AddDays(-1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        var to = DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        var data = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/tanks?stations={st.StationId}&from={from}&to={to}");
        var tanks = data!["tanks"]!.AsArray();
        var gauge = tanks.Single(t => t!["tank_id"]!.GetValue<long>() == 1)!;
        Assert.True(gauge["has_gauge"]!.GetValue<bool>());
        Assert.Equal(1818m, gauge["last_fact_volume"]!.GetValue<decimal>());
        Assert.Equal(1000m, gauge["last_fact_diff"]!.GetValue<decimal>());
        var virt = tanks.Single(t => t!["tank_id"]!.GetValue<long>() == 2)!;
        Assert.False(virt["has_gauge"]!.GetValue<bool>());
        Assert.Null(virt["last_fact_volume"]?.GetValue<decimal?>());

        Assert.Equal(3, data["measures"]!.AsArray().Count);
        var loss = data["losses"]!.AsArray().Single()!;
        Assert.Equal(150m, loss["pumped"]!.GetValue<decimal>());
        Assert.Equal(32m, loss["shortage"]!.GetValue<decimal>());
        Assert.Equal(-32m, loss["net"]!.GetValue<decimal>());
        Assert.Equal(-21.333m, loss["net_pct"]!.GetValue<decimal>());   // the 1 000 l truck stays out of the losses
        Assert.Equal(1, loss["big_surplus_count"]!.GetValue<long>());
        Assert.Equal(1000m, loss["big_surplus"]!.GetValue<decimal>());
    }
}
