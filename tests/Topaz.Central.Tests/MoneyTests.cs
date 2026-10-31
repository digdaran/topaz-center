using System.IO.Compression;
using System.Net;
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

public sealed class MoneyTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "money-password-1";
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };

    private async Task<HttpClient> LoginAsync(string login, string role, params Guid[] stations)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, stations); } catch (Npgsql.PostgresException) { }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>
    /// One station: 10 l of АИ-92 at 50 ₽ on 05.10 and on 20.10, a coffee (barcode-less) sold twice, water (barcode) once.
    /// </summary>
    private async Task<Guid> StationAsync(string name)
    {
        var st = await fx.EnrollAsync(name);
        RecordChange Order(int id, string day) => Row("flOrders", $"{id}", new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = 1, ["OrderNum"] = id, ["DateTime"] = $"{day}T12:00:00.0000+03:00", ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1,
            ["PaymentModeID"] = 1, ["Remark"] = "", ["Price"] = 50m, ["Volume"] = 10m, ["Amount"] = 500m, ["AmountCash"] = 500m, ["AmountCashless"] = 0m, ["AmountDiscount"] = 0m,
        });
        var items = new List<RecordChange>
        {
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "АИ-92", ["Deleted"] = 0 }),
            Row("dcItems", "1", new JsonObject { ["ItemID"] = 1, ["Name"] = "Кофе  Американо", ["ItemCode"] = "" }),
            Row("dcItems", "2", new JsonObject { ["ItemID"] = 2, ["Name"] = "Вода", ["ItemCode"] = "4600000000009" }),
            Order(1, "2026-10-05"), Order(2, "2026-10-20"),
            Row("trFastTradeH", "1", new JsonObject { ["FastTradeHID"] = 1, ["SessionID"] = 1, ["DateDoc"] = "2026-10-05T12:00:00.0000+03:00", ["IsReturn"] = 0, ["PaymentModeID"] = 1, ["AmountCash"] = 350, ["AmountCashless"] = 0 }),
            Row("trFastTradeD", "1", new JsonObject { ["FastTradeDID"] = 1, ["FastTradeHID"] = 1, ["ItemID"] = 1, ["Quantity"] = 2, ["Price"] = 150, ["Total"] = 300 }),
            Row("trFastTradeD", "2", new JsonObject { ["FastTradeDID"] = 2, ["FastTradeHID"] = 1, ["ItemID"] = 2, ["Quantity"] = 1, ["Price"] = 50, ["Total"] = 50 }),
        };
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await fx.Client(st.Token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        return st.StationId;
    }

    [PostgresFact]
    public async Task Margins_use_the_cost_in_effect_at_each_sale_and_follow_later_edits()
    {
        var st = await StationAsync("АЗС-money");
        var owner = await LoginAsync("money-owner", Roles.Owner);
        var period = $"stations={st}&from=2026-10-01&to=2026-10-31";

        // No costs yet: margin unknown, coverage 0.
        var f0 = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}");
        Assert.Null(f0!["fuel"]!["cost"]?.GetValue<decimal?>());
        Assert.Equal(0m, f0["fuel"]!["coverage"]!.GetValue<decimal>());

        // 40 ₽/l from 01.10, 45 ₽/l from 15.10 → sale 1 costs 400, sale 2 costs 450.
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st, fuel_id = 1, valid_from = "2026-10-01T00:00:00+03:00", price_per_liter = 40 })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st, fuel_id = 1, valid_from = "2026-10-15T00:00:00+03:00", price_per_liter = 45 })).EnsureSuccessStatusCode();
        // Coffee by name (different spacing/case), water by barcode, one unknown line.
        var import = await owner.PostAsJsonAsync("/api/v1/ui/costs/items/import", new
        {
            station_id = (Guid?)null, valid_from = "2026-10-01T00:00:00+03:00",
            csv = "товар;цена\nкофе американо;60\n4600000000009;20,5\nНет такого;1\n",
        });
        var imp = await import.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2, imp!["matched"]!.GetValue<int>());
        Assert.Single(imp["unmatched"]!.AsArray());
        await fx.WaitProcessedAsync();

        var f = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}");
        Assert.Equal(850m, f!["fuel"]!["cost"]!.GetValue<decimal>());
        Assert.Equal(150m, f["fuel"]!["margin"]!.GetValue<decimal>());        // 1000 − 850
        Assert.Equal(7.5m, f["fuel"]!["margin_per_liter"]!.GetValue<decimal>());
        Assert.Equal(1m, f["fuel"]!["coverage"]!.GetValue<decimal>());
        Assert.Equal(140.5m, f["shop"]!["cost"]!.GetValue<decimal>());        // 2 × 60 + 20.5
        Assert.Equal(209.5m, f["shop"]!["margin"]!.GetValue<decimal>());      // 350 − 140.5

        // A later correction of an old price re-prices the past sales.
        var costs = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/costs/fuel?{period}");
        var second = costs!["costs"]!.AsArray().First(c => c!["price_per_liter"]!.GetValue<decimal>() == 45m)!["id"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/ui/costs/fuel/{second}")).StatusCode);
        await fx.WaitProcessedAsync();
        var f2 = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}");
        Assert.Equal(800m, f2!["fuel"]!["cost"]!.GetValue<decimal>());

        // Overview carries the margin for owners.
        var o = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?{period}&compare=none");
        Assert.Equal(200m + 209.5m, o!["current"]!["margin"]!.GetValue<decimal>());
    }

    [PostgresFact]
    public async Task Viewers_never_receive_costs_or_margins()
    {
        var st = await StationAsync("АЗС-money-viewer");
        var viewer = await LoginAsync("money-viewer", Roles.Viewer, st);
        var manager = await LoginAsync("money-manager", Roles.Manager, st);
        var period = $"stations={st}&from=2026-10-01&to=2026-10-31";

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/ui/analytics/finance?{period}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/ui/costs/fuel?{period}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st, fuel_id = 1, valid_from = "2026-10-01T00:00:00Z", price_per_liter = 1 })).StatusCode);
        var o = await viewer.GetStringAsync($"/api/v1/ui/analytics/overview?{period}");
        var current = JsonNode.Parse(o)!["current"]!;
        Assert.Null(current["margin"]?.GetValue<decimal?>());
        Assert.Null(current["fuel_cost"]?.GetValue<decimal?>());

        // Managers may enter costs for their stations, but not "all stations" prices.
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st, fuel_id = 1, valid_from = "2026-10-01T00:00:00Z", price_per_liter = 41 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/v1/ui/costs/items", new { station_id = (Guid?)null, item_key = "x", valid_from = "2026-10-01T00:00:00Z", cost = 1 })).StatusCode);
    }

    [PostgresFact]
    public async Task Plan_progress_and_gap()
    {
        var st = await StationAsync("АЗС-plan");
        var owner = await LoginAsync("plan-owner", Roles.Owner);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/ui/plans", new { station_id = st, month = "2026-10", metric = "fuel_liters", value = 40 })).StatusCode);
        var plans = await owner.GetFromJsonAsync<JsonObject>("/api/v1/ui/plans?month=2026-10");
        var row = plans!["rows"]!.AsArray().Single(r => r!["station_id"]!.GetValue<Guid>() == st && r["metric"]!.GetValue<string>() == "fuel_liters")!;
        Assert.Equal(20m, row["fact"]!.GetValue<decimal>());
        Assert.Equal(0.5m, row["done"]!.GetValue<decimal>());
        Assert.Equal(31, row["track"]!.AsArray().Count);
        // The month is over (tests run after it): the whole plan was expected → 20 l behind.
        if (DateTime.UtcNow > new DateTime(2026, 11, 1))
            Assert.Equal(-20m, row["gap"]!.GetValue<decimal>());
    }
}
