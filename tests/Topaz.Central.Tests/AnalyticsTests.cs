using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

public sealed class ForecastTests
{
    [Fact]
    public void Abc_uses_cumulative_share_before_the_item()
    {
        var abc = Forecast.Abc([("a", 50), ("b", 30), ("c", 15), ("d", 5), ("zero", 0)]);
        Assert.Equal("A", abc["a"]);  // 0% before
        Assert.Equal("A", abc["b"]);  // 50% before
        Assert.Equal("B", abc["c"]);  // 80% before
        Assert.Equal("C", abc["d"]);  // 95% before
        Assert.Equal("C", abc["zero"]);
    }

    [Fact]
    public void Xyz_by_coefficient_of_variation()
    {
        Assert.Equal("X", Forecast.Xyz([10, 10, 10, 10]).Class);
        Assert.Equal("Y", Forecast.Xyz([10, 12, 8, 10]).Class);   // cv ≈ 0.14
        Assert.Equal("Z", Forecast.Xyz([0, 0, 20, 0]).Class);
        Assert.Equal("—", Forecast.Xyz([5, 5]).Class);            // too few weeks
    }

    [Fact]
    public void Forecast_repeats_the_weekly_pattern_without_trend()
    {
        var start = new DateTime(2026, 9, 7); // Monday
        var history = Enumerable.Range(0, 28).ToDictionary(i => start.AddDays(i), i => i % 7 == 4 ? 200m : 100m); // Fridays are busier
        var f = Forecast.Daily(history, start.AddDays(27), 7);
        Assert.Equal(7, f.Count);
        Assert.Equal(200m, f.Single(p => p.Date.DayOfWeek == DayOfWeek.Friday).Value);
        Assert.All(f.Where(p => p.Date.DayOfWeek != DayOfWeek.Friday), p => Assert.Equal(100m, p.Value));
    }

    [Fact]
    public void Stockout_date_is_empty_for_placeholder_stock()
    {
        var today = new DateTime(2026, 11, 10, 6, 9, 0);
        Assert.Equal(new DateTime(2026, 11, 20), Forecast.StockoutDate(today, 30, 10.4m));
        Assert.Null(Forecast.StockoutDate(today, 0, 5));
        Assert.Null(Forecast.StockoutDate(today, 5, null));
        // prod 10.11.2026: tea with stock 999 983 and 9 sold in 28 days -> 3.1 million days, used to answer 500
        Assert.Null(Forecast.StockoutDate(today, 999_983, 3_111_058.7m));
    }
}

public sealed class AnalyticsTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };

    private async Task SendAsync(string token, List<RecordChange> items)
    {
        var client = fx.Client(token);
        long seq = 0;
        foreach (var chunk in items.Chunk(1000))
        {
            var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = ++seq, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = chunk.ToList() };
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            (await client.PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        }
        await fx.WaitProcessedAsync();
    }

    private async Task<HttpClient> OwnerAsync()
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("analyst", "a", Roles.Owner, "analyst-password-1"); } catch (Npgsql.PostgresException) { }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "analyst", password = "analyst-password-1" })).EnsureSuccessStatusCode();
        return c;
    }

    [PostgresFact]
    public async Task Fuel_types_are_guessed_from_names()
    {
        foreach (var (name, code) in new[] { ("Аи92", "AI92"), ("АИ-95 Евро", "AI95"), ("АИ-100", "AI98"), ("ДТ-Е-К5", "DT"), ("Дизель", "DT"), ("СУГ", "LPG"), ("Пропан-бутан", "LPG"), ("Метан", "CNG"), ("Аи80", "OTHER") })
            Assert.Equal(code, await fx.QueryAsync<string>("SELECT guess_fuel_type(@name)", new { name }));
    }

    /// <summary>
    /// 20 days at 50 ₽ selling 100 l/day, then 20 days at 55 ₽ selling 90 l/day:
    /// price +10 %, volume −10 % → elasticity −1.0. Also checks heatmap averaging and rollup = raw.
    /// </summary>
    [PostgresFact]
    public async Task Price_change_elasticity_heatmap_and_rollups()
    {
        var st = await fx.EnrollAsync("АЗС-analytics");
        var items = new List<RecordChange>
        {
            Row("dcFuels", "7", new JsonObject { ["FuelID"] = 7, ["Name"] = "Аи-92 Евро", ["Deleted"] = 0 }),
            Row("dcPaymentModes", "1", new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
        };
        var start = new DateTime(2026, 9, 1); // Tuesday
        var id = 0;
        for (var d = 0; d < 40; d++)
        {
            var price = d < 20 ? 50m : 55m;
            var liters = d < 20 ? 100m : 90m;
            foreach (var part in new[] { 0.5m, 0.5m }) // two orders a day at 10:00 and 18:00 Moscow time
            {
                id++;
                var at = start.AddDays(d).AddHours(id % 2 == 1 ? 10 : 18);
                items.Add(Row("flOrders", $"{id}", new JsonObject
                {
                    ["OrderID"] = id, ["SessionID"] = d + 1, ["OrderNum"] = id, ["DateTime"] = at.ToString("yyyy-MM-dd'T'HH:mm:ss") + ".0000+03:00",
                    ["FuelID"] = 7, ["TankID"] = 1, ["HoseID"] = 1, ["PaymentModeID"] = 1, ["Remark"] = "", ["Price"] = price, ["Volume"] = liters * part,
                    ["Amount"] = liters * part * price, ["AmountCash"] = liters * part * price, ["AmountCashless"] = 0, ["AmountDiscount"] = 0, ["VolumeOrdered"] = 0,
                }));
            }
        }
        await SendAsync(st.Token, items);
        var owner = await OwnerAsync();

        var fuel = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/fuel?stations={st.StationId}&from=2026-09-01&to=2026-10-10");
        var byType = fuel!["by_type"]!.AsArray().Single()!;
        Assert.Equal("AI92", byType["fuel_type"]!.GetValue<string>());
        Assert.Equal(3800m, byType["liters"]!.GetValue<decimal>());
        var change = fuel["price_changes"]!.AsArray().Single()!;
        Assert.Equal("2026-09-21", change["date"]!.GetValue<string>());
        Assert.Equal(0.1m, change["price_change"]!.GetValue<decimal>());
        Assert.Equal(-0.1m, change["volume_change"]!.GetValue<decimal>());
        Assert.Equal(-1.0m, change["elasticity"]!.GetValue<decimal>());
        // VolumeOrdered = 0 → all "full tank"
        Assert.Equal("full_tank", Assert.Single(fuel["order_types"]!.AsArray())!["kind"]!.GetValue<string>());

        // Heatmap: per-day average for each weekday. Tuesdays 01.09–06.10 occur 6 times, each with one 10:00 order.
        var demand = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/demand?stations={st.StationId}&from=2026-09-01&to=2026-10-10");
        var tue10 = demand!["fuel_heatmap"]!.AsArray().Single(h => h!["dow"]!.GetValue<int>() == 2 && h["hour"]!.GetValue<int>() == 10)!;
        Assert.Equal(1m, tue10["orders"]!.GetValue<decimal>());
        Assert.Equal(14, demand["forecast"]!.AsArray().Count);

        // Rollups give exactly the raw figures.
        Assert.Equal(0, await fx.QueryAsync<long>("""
            WITH raw AS (SELECT station_id, local_date, sum(amount) r, sum(volume) FILTER (WHERE is_sale) l, count(*) FILTER (WHERE is_sale) o FROM v_fuel_sales_local GROUP BY 1, 2),
                 agg AS (SELECT station_id, local_date, sum(revenue) r, sum(liters) l, sum(orders) o FROM agg_fuel_daily GROUP BY 1, 2)
            SELECT count(*) FROM raw FULL JOIN agg USING (station_id, local_date)
            WHERE raw.r IS DISTINCT FROM agg.r OR COALESCE(raw.l, 0) <> COALESCE(agg.l, 0) OR raw.o IS DISTINCT FROM agg.o
            """));

        // Overview KPIs come from the rollups and match the raw total.
        var o = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={st.StationId}&from=2026-09-01&to=2026-10-10&compare=none");
        Assert.Equal(20 * 100m * 50m + 20 * 90m * 55m, o!["current"]!["fuel_revenue"]!.GetValue<decimal>());
    }

    [PostgresFact]
    public async Task Goods_stock_statuses_and_abc()
    {
        var st = await fx.EnrollAsync("АЗС-goods");
        var now = DateTimeOffset.UtcNow;
        string Ts(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz");
        var items = new List<RecordChange>
        {
            Row("dcItems", "1", new JsonObject { ["ItemID"] = 1, ["Name"] = "Кофе", ["ItemCode"] = "", ["ItemsGrpID"] = -1 }),
            Row("dcItems", "2", new JsonObject { ["ItemID"] = 2, ["Name"] = "Трос", ["ItemCode"] = "4610000000001", ["ItemsGrpID"] = -2 }),
            Row("dcItems", "3", new JsonObject { ["ItemID"] = 3, ["Name"] = "Вода", ["ItemCode"] = "4600000000002", ["ItemsGrpID"] = -1 }),
            Row("dcItemsGrps", "-1", new JsonObject { ["ItemsGrpID"] = -1, ["Name"] = "Кафе" }),
            Row("dcItemsGrps", "-2", new JsonObject { ["ItemsGrpID"] = -2, ["Name"] = "Автотовары" }),
            // stock: coffee 100, rope 5 (never sold → dead), water 30 − 28 sold = 2 (sells 1/day → low)
            Row("rgItemRests", "1", new JsonObject { ["rgItemRestID"] = 1, ["Date"] = Ts(now.AddDays(-60)), ["DocTypeID"] = 3, ["ItemID"] = 1, ["StorageID"] = 1, ["Quantity"] = 100 }),
            Row("rgItemRests", "2", new JsonObject { ["rgItemRestID"] = 2, ["Date"] = Ts(now.AddDays(-60)), ["DocTypeID"] = 3, ["ItemID"] = 2, ["StorageID"] = 1, ["Quantity"] = 5 }),
            Row("rgItemRests", "3", new JsonObject { ["rgItemRestID"] = 3, ["Date"] = Ts(now.AddDays(-60)), ["DocTypeID"] = 3, ["ItemID"] = 3, ["StorageID"] = 1, ["Quantity"] = 30 }),
        };
        var move = 10;
        for (var d = 1; d <= 28; d++)
        {
            var at = now.AddDays(-d);
            items.Add(Row("trFastTradeH", $"{d}", new JsonObject { ["FastTradeHID"] = d, ["SessionID"] = d, ["DateDoc"] = Ts(at), ["IsReturn"] = 0, ["PaymentModeID"] = 1 }));
            items.Add(Row("trFastTradeD", $"{d}", new JsonObject { ["FastTradeDID"] = d, ["FastTradeHID"] = d, ["ItemID"] = 3, ["Quantity"] = 1, ["Price"] = 50, ["Amount"] = 50, ["Total"] = 50 }));
            items.Add(Row("trFastTradeD", $"{100 + d}", new JsonObject { ["FastTradeDID"] = 100 + d, ["FastTradeHID"] = d, ["ItemID"] = 1, ["Quantity"] = 2, ["Price"] = 150, ["Amount"] = 300, ["Total"] = 300 }));
            items.Add(Row("rgItemRests", $"{move}", new JsonObject { ["rgItemRestID"] = move++, ["Date"] = Ts(at), ["DocTypeID"] = 5, ["ItemID"] = 3, ["StorageID"] = 1, ["Quantity"] = -1 }));
            items.Add(Row("rgItemRests", $"{move}", new JsonObject { ["rgItemRestID"] = move++, ["Date"] = Ts(at), ["DocTypeID"] = 5, ["ItemID"] = 1, ["StorageID"] = 1, ["Quantity"] = -2 }));
        }
        await SendAsync(st.Token, items);
        var owner = await OwnerAsync();
        var from = now.AddDays(-35).ToString("yyyy-MM-dd");
        var to = now.ToString("yyyy-MM-dd");
        var r = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/items?stations={st.StationId}&from={from}&to={to}");

        var abc = r!["items"]!.AsArray().ToDictionary(i => i!["name"]!.GetValue<string>(), i => i!["abc"]!.GetValue<string>());
        Assert.Equal("A", abc["Кофе"]);  // 8400 ₽ of 9800 ₽
        Assert.Equal("B", abc["Вода"]);  // 1400 ₽, starts at 85.7 %
        var stock = r["stock"]!.AsArray().ToDictionary(s => s!["name"]!.GetValue<string>(), s => s!);
        Assert.Equal("dead", stock["Трос"]["status"]!.GetValue<string>());
        Assert.Equal("low", stock["Вода"]["status"]!.GetValue<string>());       // 2 left, 1 per day
        Assert.Equal(2m, stock["Вода"]["stock"]!.GetValue<decimal>());
        Assert.Equal(1m, stock["Вода"]["per_day"]!.GetValue<decimal>());          // 28 sold in the last 28 days
        Assert.Equal(2m, stock["Вода"]["days_of_cover"]!.GetValue<decimal>());
        Assert.Equal(15m, stock["Вода"]["recommended_order"]!.GetValue<decimal>()); // ceil(1 × 14 × 1.2 − 2)
        Assert.Equal(44m, stock["Кофе"]["stock"]!.GetValue<decimal>());
    }
}
