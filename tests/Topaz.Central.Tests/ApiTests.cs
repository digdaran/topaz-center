using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

public sealed class ApiTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static string Hash(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static RecordChange Order(long id, decimal volume, long session = 1) => new()
    {
        Dataset = "flOrders",
        Key = id.ToString(),
        SessionId = session,
        Hash = Hash($"{id}:{volume}"),
        Data = new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = session, ["Volume"] = volume, ["Price"] = 48.00m,
            ["Amount"] = volume * 48, ["DateTime"] = "2026-10-31T21:59:52.9720+03:00", ["FuelID"] = 190,
        },
    };

    private static BatchEnvelope Batch(long seq, params RecordChange[] items) => new()
    {
        BatchId = Guid.NewGuid(), Seq = seq, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "test", Items = items.ToList(),
    };

    private static HttpContent Gzip(BatchEnvelope b)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, b, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    [PostgresFact]
    public async Task Health_endpoints_respond()
    {
        Assert.Equal(HttpStatusCode.OK, (await fx.Client().GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Client().GetAsync("/health/ready")).StatusCode);
        Assert.Contains("topaz_batches_received_total", await fx.Client().GetStringAsync("/metrics"));
    }

    [PostgresFact]
    public async Task Batches_are_idempotent()
    {
        var st = await fx.EnrollAsync("АЗС-idem");
        var client = fx.Client(st.Token);
        var batch = Batch(1, Order(1, 10m));

        var r1 = await client.PostAsync("/api/v1/batches", Gzip(batch));
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        var r2 = await client.PostAsync("/api/v1/batches", Gzip(batch)); // retry after a lost ack
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("duplicate", (await r2.Content.ReadFromJsonAsync<BatchAck>(Protocol.Json))!.Status);

        var tampered = Batch(1, Order(1, 99m));
        tampered.BatchId = batch.BatchId;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/v1/batches", Gzip(tampered))).StatusCode);

        Assert.Equal(1, await fx.QueryAsync<long>("SELECT count(*) FROM raw_batches WHERE batch_id = @id", new { id = batch.BatchId }));
    }

    [PostgresFact]
    public async Task Invalid_requests_get_client_errors_not_retries()
    {
        var st = await fx.EnrollAsync("АЗС-invalid");
        var client = fx.Client(st.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client("tpz_nope").PostAsync("/api/v1/batches", Gzip(Batch(1, Order(1, 1m))))).StatusCode);

        var noItems = Batch(1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/v1/batches", Gzip(noItems))).StatusCode);

        var badHash = Batch(1, Order(1, 1m));
        badHash.Items[0].Hash = "xyz";
        var r = await client.PostAsync("/api/v1/batches", Gzip(badHash));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("items[0].hash", await r.Content.ReadAsStringAsync());

        var text = new StringContent("{}", Encoding.UTF8, "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/v1/batches", text)).StatusCode);

        var broken = new StringContent("{not json", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/batches", broken)).StatusCode);
    }

    [PostgresFact]
    public async Task Processing_maintains_records_history_and_normalized_tables()
    {
        var st = await fx.EnrollAsync("АЗС-proc");
        var client = fx.Client(st.Token);
        var args = new { s = st.StationId };

        (await client.PostAsync("/api/v1/batches", Gzip(Batch(1, Order(1, 0m), Order(2, 5m))))).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        Assert.Equal(2, await fx.QueryAsync<long>("SELECT count(*) FROM fuel_sales WHERE station_id = @s", args));

        // Order 1 finished fuelling (update), order 2 deleted, order 1 re-sent unchanged (no new version).
        (await client.PostAsync("/api/v1/batches", Gzip(Batch(2, Order(1, 23.03m), new RecordChange { Dataset = "flOrders", Op = "delete", Key = "2" })))).EnsureSuccessStatusCode();
        (await client.PostAsync("/api/v1/batches", Gzip(Batch(3, Order(1, 23.03m))))).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();

        Assert.Equal(23.03m, await fx.QueryAsync<decimal>("SELECT volume FROM fuel_sales WHERE station_id = @s AND order_id = 1", args));
        Assert.Equal(new DateTime(2026, 10, 31, 18, 59, 52, 972, DateTimeKind.Utc),
            await fx.QueryAsync<DateTime>("SELECT sold_at FROM fuel_sales WHERE station_id = @s AND order_id = 1", args));
        Assert.Equal(0, await fx.QueryAsync<long>("SELECT count(*) FROM fuel_sales WHERE station_id = @s AND order_id = 2", args));
        Assert.True(await fx.QueryAsync<bool>("SELECT deleted FROM records WHERE station_id = @s AND dataset = 'flOrders' AND key = '2'", args));
        Assert.Equal(2, await fx.QueryAsync<int>("SELECT version FROM records WHERE station_id = @s AND dataset = 'flOrders' AND key = '1'", args));
        Assert.Equal(4, await fx.QueryAsync<long>("SELECT count(*) FROM record_versions WHERE station_id = @s", args));
    }

    /// <summary>Rules found by reconciling with a real Topaz shift export (docs/topaz-schema.md).</summary>
    [PostgresFact]
    public async Task Shift_report_views_follow_topaz_rules()
    {
        var st = await fx.EnrollAsync("АЗС-views");
        RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };
        RecordChange Sale(int id, decimal volume, decimal amount, int mode, string remark, decimal cash, decimal cashless) => Row("flOrders", id.ToString(), new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = 80, ["Volume"] = volume, ["Amount"] = amount, ["Price"] = 48m, ["PaymentModeID"] = mode,
            ["Remark"] = remark, ["AmountCash"] = cash, ["AmountCashless"] = cashless, ["DateTime"] = "2026-10-27T10:00:00.0000+03:00",
        });
        var batch = Batch(1,
            Row("dcPaymentModes", "109", new JsonObject { ["PaymentModeID"] = 109, ["Name"] = "НАЛИЧКА" }),
            Row("dcPaymentModes", "115", new JsonObject { ["PaymentModeID"] = 115, ["Name"] = "НАЛИЧКА" }),
            Row("dcPaymentModes", "114", new JsonObject { ["PaymentModeID"] = 114, ["Name"] = "Терминал Альфабанк" }),
            Sale(1, 30m, 1410m, 115, "", 1440m, 0m),        // prepaid 1440, discount → cost 1410
            Sale(2, 0m, 0m, 115, "", 0m, 0m),                // aborted fuelling: still counted
            Sale(3, 10m, 480m, 109, "", 480m, 0m),           // other id, same name → same group
            Sale(4, 20m, 960m, 114, "Б", 0m, 960m),          // 'Б' = cashless
            Sale(5, 5m, 240m, 114, "", 240m, 0m));           // terminal mode without 'Б' = booked as cash
        (await fx.Client(st.Token).PostAsync("/api/v1/batches", Gzip(batch))).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        var s = new { s = st.StationId };

        Assert.Equal(3, await fx.QueryAsync<long>("SELECT count(*) FROM v_shift_fuel_outcomes WHERE station_id = @s", s));
        Assert.Equal(3, await fx.QueryAsync<long>("SELECT order_count FROM v_shift_fuel_outcomes WHERE station_id = @s AND payment_mode = 'НАЛИЧКА'", s));
        Assert.Equal(1890m, await fx.QueryAsync<decimal>("SELECT amount FROM v_shift_fuel_outcomes WHERE station_id = @s AND payment_mode = 'НАЛИЧКА'", s));
        Assert.Equal(1920m, await fx.QueryAsync<decimal>("SELECT amount_cash FROM v_shift_fuel_outcomes WHERE station_id = @s AND payment_mode = 'НАЛИЧКА'", s));
        Assert.Equal("cashless", await fx.QueryAsync<string>("SELECT payment_kind FROM fuel_sales WHERE station_id = @s AND order_id = 4", s));
        Assert.Equal("cash", await fx.QueryAsync<string>("SELECT payment_kind FROM fuel_sales WHERE station_id = @s AND order_id = 5", s));
        Assert.Equal("none", await fx.QueryAsync<string>("SELECT payment_kind FROM fuel_sales WHERE station_id = @s AND order_id = 2", s));
        Assert.Equal("Б", await fx.QueryAsync<string>("SELECT remark FROM fuel_sales WHERE station_id = @s AND order_id = 4", s));
    }

    [PostgresFact]
    public async Task Unknown_datasets_are_kept_generically()
    {
        var st = await fx.EnrollAsync("АЗС-new-table");
        var item = new RecordChange { Dataset = "flSomethingNew", Key = "1", Hash = Hash("x"), Data = new JsonObject { ["ID"] = 1 } };
        (await fx.Client(st.Token).PostAsync("/api/v1/batches", Gzip(Batch(1, item)))).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        Assert.Equal(1, await fx.QueryAsync<long>("SELECT count(*) FROM records WHERE dataset = 'flSomethingNew'"));
    }

    [PostgresFact]
    public async Task Export_streams_ndjson_and_csv_for_admins_only()
    {
        var st = await fx.EnrollAsync("АЗС-export");
        (await fx.Client(st.Token).PostAsync("/api/v1/batches", Gzip(Batch(1, Order(7, 12.5m))))).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Client(st.Token).GetAsync("/api/v1/export/flOrders")).StatusCode);

        var admin = fx.Client(CentralFixture.AdminToken);
        var ndjson = await admin.GetStringAsync($"/api/v1/export/flOrders?station_id={st.StationId}");
        var line = JsonNode.Parse(ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single())!;
        Assert.Equal("АЗС-export", line["station"]!.GetValue<string>());
        Assert.Equal(12.5m, line["data"]!["Volume"]!.GetValue<decimal>());

        var csv = await admin.GetStringAsync($"/api/v1/export/flOrders?station_id={st.StationId}&format=csv");
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("station;station_id;key;", rows[0]);
        Assert.Contains("OrderID", rows[0]);
        Assert.Equal(2, rows.Length);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/export/bad-name")).StatusCode);
    }
}
