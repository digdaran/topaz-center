using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// 1.6: fuel cost as a moving average per tank, the "enter the receipt price" reminder, margins without VAT
/// (only receipted sales), card discounts and the pump price rule.
/// </summary>
public sealed class CostAndDiscountTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "cost-password-1";
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };
    private static string Ts(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz");
    private static string Day(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");

    private async Task SendAsync(string token, List<RecordChange> items)
    {
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await fx.Client(token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
    }

    private async Task<HttpClient> LoginAsync(string login, string role, Guid? org = null)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, [], org); } catch (Npgsql.PostgresException) { /* exists */ }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>An organization of its own (settings like VAT must not leak into the other tests).</summary>
    private async Task<(Guid Org, HttpClient Owner)> OrganizationAsync(string name)
    {
        var super = await LoginAsync("cost-super", Roles.Superadmin);
        var created = await super.PostAsJsonAsync("/api/v1/ui/organizations", new { name });
        created.EnsureSuccessStatusCode();
        var org = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        return (org, await LoginAsync("cost-owner-" + name, Roles.Owner, org));
    }

    private Task<Guid> DefaultOrgAsync() => fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at, name LIMIT 1");

    private static JsonObject Order(int id, int session, DateTimeOffset at, decimal volume, decimal price, decimal pump, int mode = 1, decimal discount = 0, int? fiscal = null, int tank = 1) => new()
    {
        ["OrderID"] = id, ["SessionID"] = session, ["OrderNum"] = id, ["DateTime"] = Ts(at), ["FuelID"] = 1, ["TankID"] = tank, ["HoseID"] = 1,
        ["PaymentModeID"] = mode, ["Remark"] = "", ["Price"] = price, ["PricePump"] = pump, ["Volume"] = volume, ["Amount"] = volume * pump,
        ["AmountCash"] = volume * pump, ["AmountCashless"] = 0, ["AmountDiscount"] = discount, ["FiscalNumber"] = fiscal,
    };

    [Fact]
    public void Average_follows_the_tank()
    {
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = FuelCostAverager.Compute(
            [(1, 1, 1, t0.AddDays(1), 10000m, 55m, 8000m), (2, 1, 1, t0.AddDays(2), 10000m, null, 17990m), (3, 2, 1, t0.AddDays(1), 5000m, 60m, 1000m)],
            [(1, t0, 50m)], [(1, 1), (2, 1)]);
        // Tank 1: 8 000 l at 50 + 10 000 l at 55; the unpriced receipt keeps the average.
        Assert.Equal([50m, 52.7778m, 52.7778m], rows.Where(r => r.TankId == 1).Select(r => r.AvgPrice));
        Assert.Equal(["stock_price", "receipt", "receipt_unpriced"], rows.Where(r => r.TankId == 1).Select(r => r.Source));
        // Tank 2 has its own average: 1 000 l at 50 + 5 000 l at 60.
        Assert.Equal(58.3333m, rows.Last(r => r.TankId == 2).AvgPrice);
        // Without a stock price the first priced receipt values the stock already in the tank.
        var first = FuelCostAverager.Compute([(1, 1, 1, t0, 10000m, 55m, 8000m)], [], []);
        Assert.Equal(55m, Assert.Single(first).AvgPrice);
    }

    [PostgresFact]
    public async Task Sales_cost_the_tank_average_and_a_later_receipt_price_reprices_them()
    {
        var st = await fx.EnrollAsync("АЗС-средняя", await DefaultOrgAsync());
        var owner = await LoginAsync("avg-owner", Roles.Owner);
        var d1 = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.FromHours(3));
        await SendAsync(st.Token, [
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "АИ-92", ["Deleted"] = 0 }),
            Row("dcTanks", "1", new JsonObject { ["TankID"] = 1, ["Num"] = 1, ["FuelID"] = 1, ["MaxVolume"] = 30000, ["Deleted"] = 0 }),
            Row("flSesTanks", "10|1", new JsonObject { ["SessionID"] = 10, ["TankID"] = 1, ["TankNum"] = 1, ["StartFuelVolume"] = 8000 }),
            Row("flSesTanks", "11|1", new JsonObject { ["SessionID"] = 11, ["TankID"] = 1, ["TankNum"] = 1, ["StartFuelVolume"] = 17990 }),
            Row("flRecepts", "1", new JsonObject { ["ReceptID"] = 1, ["SessionID"] = 10, ["ReceptDateTime"] = Ts(d1), ["TankID"] = 1, ["FuelID"] = 1, ["DocVolume"] = 10000 }),
            Row("flRecepts", "2", new JsonObject { ["ReceptID"] = 2, ["SessionID"] = 11, ["ReceptDateTime"] = Ts(d1.AddDays(1)), ["TankID"] = 1, ["FuelID"] = 1, ["DocVolume"] = 10000 }),
            Row("flOrders", "1", Order(1, 10, d1.AddHours(2), 10m, 60m, 60m)),
            Row("flOrders", "2", Order(2, 11, d1.AddDays(1).AddHours(2), 10m, 60m, 60m)),
        ]);
        var period = $"stations={st.StationId}&from=2026-09-01&to=2026-09-30";
        async Task<decimal> CostAsync()
        {
            await fx.WaitProcessedAsync();
            return (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}"))!["fuel"]!["cost"]!.GetValue<decimal>();
        }
        static decimal Avg(decimal stock, decimal avg, decimal liters, decimal price) => Math.Round((stock * avg + liters * price) / (stock + liters), 4, MidpointRounding.AwayFromZero);

        // Stock price 50 on 01.09, receipt 1 at 55: 8 000 l at 50 + 10 000 l at 55 → 52.7778; receipt 2 has no price yet.
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st.StationId, fuel_id = 1, valid_from = "2026-09-01T00:00:00+03:00", price_per_liter = 50 })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st.StationId, fuel_id = 1, valid_from = Ts(d1), price_per_liter = 55, receipt_id = 1 })).EnsureSuccessStatusCode();
        var a1 = Avg(8000, 50, 10000, 55);
        Assert.Equal(52.7778m, a1);
        Assert.Equal(20 * a1, await CostAsync());

        var costs = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/costs/fuel?{period}");
        var receipts = costs!["receipts"]!.AsArray();
        var r1 = receipts.Single(r => r!["receipt_id"]!.GetValue<long>() == 1)!;
        var r2 = receipts.Single(r => r!["receipt_id"]!.GetValue<long>() == 2)!;
        Assert.Equal(8000m, r1["stock_before"]!.GetValue<decimal>());
        Assert.Equal(a1, r1["average_after"]!.GetValue<decimal>());
        Assert.True(r2["unpriced"]!.GetValue<bool>());
        Assert.Equal(a1, r2["average_after"]!.GetValue<decimal>());

        // Pricing receipt 2 later re-prices the sale after it.
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st.StationId, fuel_id = 1, valid_from = Ts(d1.AddDays(1)), price_per_liter = 60, receipt_id = 2 })).EnsureSuccessStatusCode();
        Assert.Equal(10 * a1 + 10 * Avg(17990, a1, 10000, 60), await CostAsync());

        // Without the stock price the first priced receipt values the stock in the tank at its own price.
        var stockPrice = costs["costs"]!.AsArray().Single(c => c!["receipt_id"] is null)!["id"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/ui/costs/fuel/{stockPrice}")).StatusCode);
        Assert.Equal(10 * 55m + 10 * Avg(17990, 55, 10000, 60), await CostAsync());
    }

    [PostgresFact]
    public async Task A_receipt_without_a_price_reminds_until_the_price_is_entered()
    {
        var st = await fx.EnrollAsync("АЗС-напоминание", await DefaultOrgAsync());
        var owner = await LoginAsync("remind-owner", Roles.Owner);
        var at = DateTimeOffset.UtcNow.AddDays(-2);
        await SendAsync(st.Token, [
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "ДТ", ["Deleted"] = 0 }),
            Row("flRecepts", "7", new JsonObject { ["ReceptID"] = 7, ["SessionID"] = 1, ["ReceptDateTime"] = Ts(at), ["TankID"] = 1, ["FuelID"] = 1, ["DocNumber"] = "ТТН-7", ["DocVolume"] = 5000 }),
            Row("flRecepts", "8", new JsonObject { ["ReceptID"] = 8, ["SessionID"] = 1, ["ReceptDateTime"] = Ts(DateTimeOffset.UtcNow.AddHours(-1)), ["TankID"] = 1, ["FuelID"] = 1, ["DocVolume"] = 3000 }),
        ]);
        (await owner.PostAsync("/api/v1/ui/alerts/run", null)).EnsureSuccessStatusCode();
        // Only the receipt older than the 24 h threshold is an alert; both wait for a price.
        Assert.Equal(1, await fx.QueryAsync<long>("SELECT count(*) FROM alerts WHERE rule_code = 'receipt_without_cost' AND station_id = @s AND status = 'new'", new { s = st.StationId }));
        Assert.Equal("7", await fx.QueryAsync<string>("SELECT dedupe_key FROM alerts WHERE rule_code = 'receipt_without_cost' AND station_id = @s", new { s = st.StationId }));
        var meta = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/meta");
        Assert.True(meta!["unpriced_receipts"]!.GetValue<long>() >= 2);

        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st.StationId, fuel_id = 1, valid_from = Ts(at), price_per_liter = 61.5, receipt_id = 7 })).EnsureSuccessStatusCode();
        Assert.Equal("closed", await fx.QueryAsync<string>("SELECT status FROM alerts WHERE rule_code = 'receipt_without_cost' AND station_id = @s", new { s = st.StationId }));
    }

    [PostgresFact]
    public async Task Without_vat_the_margin_drops_the_vat_of_receipted_sales_only()
    {
        var (org, owner) = await OrganizationAsync("НДС-хозяин");
        var st = await fx.EnrollAsync("АЗС-ндс", org);
        var at = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(3));
        await SendAsync(st.Token, [
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "АИ-95", ["Deleted"] = 0 }),
            Row("flOrders", "1", Order(1, 1, at, 10m, 60m, 60m, fiscal: 123)),   // with a receipt
            Row("flOrders", "2", Order(2, 1, at.AddHours(1), 10m, 60m, 60m)),    // without
        ]);
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = st.StationId, fuel_id = 1, valid_from = "2026-09-01T00:00:00+03:00", price_per_liter = 50 })).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        var period = $"stations={st.StationId}&from=2026-09-01&to=2026-09-30";
        var gross = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}");
        Assert.Equal(200m, gross!["fuel"]!["margin"]!.GetValue<decimal>());   // 1 200 − 1 000
        Assert.Equal(600m, gross["fuel"]!["fiscal_revenue"]!.GetValue<decimal>());
        Assert.Equal("gross", gross["vat"]!["mode"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync("/api/v1/ui/organization/settings", new { margin_vat = "net", vat_rate = 20 })).StatusCode);
        await fx.WaitProcessedAsync();
        var net = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}");
        Assert.Equal(100m, net!["fuel"]!["margin"]!.GetValue<decimal>());     // VAT 600 × 20 / 120 = 100 out of the receipted sale only
        Assert.Equal("net", net["vat"]!["mode"]!.GetValue<string>());
        var overview = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?{period}&compare=none");
        Assert.Equal(100m, overview!["current"]!["margin"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/v1/ui/organization/settings", new { margin_vat = "half" })).StatusCode);
    }

    [PostgresFact]
    public async Task Card_discounts_count_as_discounts_and_only_real_price_mismatches_alert()
    {
        var (_, owner) = await OrganizationAsync("Скидки-хозяин");
        var org = await fx.QueryAsync<Guid>("SELECT id FROM organizations WHERE name = 'Скидки-хозяин'");
        var st = await fx.EnrollAsync("АЗС-скидки", org);
        var start = DateTimeOffset.UtcNow.AddHours(-4);
        await SendAsync(st.Token, [
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "СУГ", ["Deleted"] = 0 }),
            Row("dcPaymentModes", "1", new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "НАЛИЧКА" }),
            Row("dcPaymentModes", "2", new JsonObject { ["PaymentModeID"] = 2, ["Name"] = "Дисконтная(НАЛИЧКА)" }),
            Row("sysSessions", "1", new JsonObject { ["SessionID"] = 1, ["SessionNum"] = 1, ["UserName"] = "Иванов", ["StartDateTime"] = Ts(start), ["EndDateTime"] = Ts(start.AddHours(3)), ["Deleted"] = 0 }),
            Row("flOrders", "1", Order(1, 1, start.AddMinutes(10), 10m, 47m, 45m, mode: 2)),              // card discount: fine
            Row("flOrders", "2", Order(2, 1, start.AddMinutes(20), 10m, 47m, 47m)),                       // plain sale
            Row("flOrders", "3", Order(3, 1, start.AddMinutes(30), 10m, 47m, 45m, mode: 1, discount: 20)), // Topaz's own discount: fine
            Row("flOrders", "4", Order(4, 1, start.AddMinutes(40), 10m, 47m, 45m, mode: 1)),              // lowered price, no discount mode
            Row("flOrders", "5", Order(5, 1, start.AddMinutes(50), 10m, 47m, 49m, mode: 1)),              // customer overcharged
        ]);
        (await owner.PostAsync("/api/v1/ui/alerts/run", null)).EnsureSuccessStatusCode();
        var alert = await fx.QueryAsync<(decimal Value, string Details)>(
            "SELECT value, details FROM alerts WHERE rule_code = 'price_mismatch' AND station_id = @s", new { s = st.StationId });
        Assert.Equal(2m, alert.Value);
        Assert.Contains("переплата клиента — 1", alert.Details);
        Assert.Contains("мимо скидок Топаза — 1", alert.Details);

        // Discounts: Topaz's field, else litres × (order price − pump price): 20 + 20 + 20.
        var finance = await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?stations={st.StationId}&from={Day(start.AddDays(-1))}&to={Day(start.AddDays(1))}");
        Assert.Equal(60m, finance!["fuel"]!["discounts"]!.GetValue<decimal>());

        // Discount payment modes: guessed by name, the owner can override.
        var modes = (await owner.GetFromJsonAsync<JsonArray>("/api/v1/ui/payment-modes"))!.Where(m => m!["station_id"]!.GetValue<Guid>() == st.StationId).ToList();
        Assert.False(modes.Single(m => m!["payment_mode_id"]!.GetValue<long>() == 1)!["is_discount"]!.GetValue<bool>());
        Assert.True(modes.Single(m => m!["payment_mode_id"]!.GetValue<long>() == 2)!["is_discount"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync($"/api/v1/ui/payment-modes/{st.StationId}/1", new { is_discount = true })).StatusCode);
        var marked = (await owner.GetFromJsonAsync<JsonArray>("/api/v1/ui/payment-modes"))!.Single(m => m!["station_id"]!.GetValue<Guid>() == st.StationId && m["payment_mode_id"]!.GetValue<long>() == 1)!;
        Assert.True(marked["is_discount"]!.GetValue<bool>());
        Assert.True(marked["manual"]!.GetValue<bool>());
        // Another organization's owner cannot touch these modes.
        var (_, stranger) = await OrganizationAsync("Чужой-хозяин");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/v1/ui/payment-modes/{st.StationId}/1", new { is_discount = false })).StatusCode);
    }
}
