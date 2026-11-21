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
/// 1.13: the money of a shift as the owner reads it — fuel and shop each by payment method, and the cash in the till
/// (cash put in + cash for fuel + cash for the shop − cash taken out), as Topaz's money register counts it.
/// </summary>
public sealed class ShiftCardTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, long? session, JsonObject data) =>
        new() { Dataset = dataset, Key = key, SessionId = session, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };
    private static string Ts(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffzzz");

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

    [PostgresFact]
    public async Task Shift_money_is_split_into_fuel_shop_and_cash_in_the_till()
    {
        var org = await fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at, name LIMIT 1");
        var st = await fx.EnrollAsync("АЗС-касса-смены", org);
        var t0 = DateTimeOffset.UtcNow.AddHours(-30);

        RecordChange Order(int id, int mode, decimal volume, decimal amount, bool card) => Row("flOrders", $"{id}", 1, new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = 1, ["OrderNum"] = id, ["DateTime"] = Ts(t0.AddMinutes(10 + id)),
            ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1, ["PaymentModeID"] = mode, ["Volume"] = volume, ["VolumeOrdered"] = volume,
            ["Price"] = 50, ["PricePump"] = 50, ["Amount"] = amount, ["AmountCash"] = card ? 0 : amount, ["AmountCashless"] = card ? amount : 0,
            ["AmountDiscount"] = 0, ["Remark"] = card ? "Б" : "", ["StatusOrder"] = 0,
        });
        RecordChange Receipt(int id, int mode, decimal total, bool isReturn) => Row("trFastTradeH", $"{id}", 1, new JsonObject
        {
            ["FastTradeHID"] = id, ["SessionID"] = 1, ["NDoc"] = id, ["DateDoc"] = Ts(t0.AddHours(1).AddMinutes(id)), ["IsReturn"] = isReturn ? 1 : 0,
            ["CashID"] = 3, ["PaymentModeID"] = mode, ["AmountCash"] = mode == 1 ? total : 0, ["AmountCashless"] = mode == 1 ? 0 : total,
            ["Status"] = 0, ["FiscalNumber"] = 1,
        });
        RecordChange Line(int id, decimal total) => Row("trFastTradeD", $"{id}", 1, new JsonObject
        {
            ["FastTradeDID"] = id, ["FastTradeHID"] = id, ["ItemID"] = 1, ["Quantity"] = 1, ["Price"] = total, ["Amount"] = total, ["Total"] = total,
        });
        // DocID: the document the money belongs to (order for 6, receipt for 5), as in Topaz.
        RecordChange Cash(int id, int docType, decimal amount, int doc = 0) => Row("rgCashes", $"{id}", 1, new JsonObject
        {
            ["RgCashID"] = id, ["SessionID"] = 1, ["Date"] = Ts(t0.AddHours(2).AddMinutes(id)), ["DocTypeID"] = docType, ["DocID"] = doc, ["CashID"] = 3, ["Amount"] = amount,
        });

        await SendAsync(st.Token,
        [
            Row("dcPaymentModes", "1", null, new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
            Row("dcPaymentModes", "2", null, new JsonObject { ["PaymentModeID"] = 2, ["Name"] = "Банк. терминал" }),
            Row("sysSessions", "1", 1, new JsonObject
            {
                ["SessionID"] = 1, ["SessionNum"] = 1, ["UserName"] = "Оператор", ["UserID"] = 1, ["Deleted"] = 0,
                ["StartDateTime"] = Ts(t0), ["EndDateTime"] = Ts(t0.AddHours(24)),
            }),
            // Fuel: 1 000 ₽ cash, 600 ₽ by card, one aborted order.
            Order(1, 1, 20, 1000, false), Order(2, 2, 12, 600, true), Order(3, 1, 0, 0, false),
            // Shop: 300 ₽ cash, 200 ₽ by card, a 50 ₽ cash return.
            Receipt(1, 1, 300, false), Line(1, 300),
            Receipt(2, 2, 200, false), Line(2, 200),
            Receipt(3, 1, 50, true), Line(3, 50),
            // The till: 500 ₽ change put in, cash for fuel and shop, 1 600 ₽ taken out → 150 ₽ left.
            Cash(1, 14, 500), Cash(2, 6, 1000, doc: 1), Cash(3, 5, 300, doc: 1), Cash(5, 5, -50, doc: 3), Cash(4, 15, -1600),
        ]);

        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("shiftcard-owner", "o", Roles.Owner, "shiftcard-password-1", [], org); } catch (PostgresException) { }
        var owner = fx.Factory.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "shiftcard-owner", password = "shiftcard-password-1" })).EnsureSuccessStatusCode();

        var h = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/shifts/{st.StationId}/1"))!["header"]!;
        decimal D(string k) => h[k]?.GetValue<decimal>() ?? 0;
        Assert.Equal(1600m, D("fuel_amount"));
        Assert.Equal(1000m, D("pay_cash"));
        Assert.Equal(600m, D("pay_card"));
        Assert.Equal(450m, D("shop_total"));   // returns subtracted
        Assert.Equal(250m, D("shop_cash"));
        Assert.Equal(200m, D("shop_card"));
        Assert.Equal(3, h["shop_receipts"]!.GetValue<long>());
        Assert.Equal(2, h["orders"]!.GetValue<long>());
        Assert.Equal(1, h["aborted"]!.GetValue<long>());
        Assert.Equal(500m, D("cash_in"));
        Assert.Equal(-1600m, D("cash_out"));
        Assert.Equal(1000m, D("cash_fuel"));
        Assert.Equal(250m, D("cash_shop"));
        Assert.Equal(150m, D("cash_end"));
        // The till's own sales lines agree with the sales by payment method.
        Assert.Equal(D("pay_cash"), D("cash_fuel"));
        Assert.Equal(D("shop_cash"), D("cash_shop"));

        var from = t0.AddDays(-1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        var to = DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        var row = Assert.Single((await owner.GetFromJsonAsync<JsonArray>($"/api/v1/ui/shifts?stations={st.StationId}&from={from}&to={to}"))!)!;
        Assert.Equal(450m, row["shop_total"]!.GetValue<decimal>());
        Assert.Equal(250m, row["shop_cash"]!.GetValue<decimal>());

        // 1.15 summary: the same split over a period (daily rollups), fuel and shop each by payment method.
        var k = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={st.StationId}&from={from}&to={to}&compare=none"))!["current"]!;
        decimal K(string key) => k[key]?.GetValue<decimal>() ?? 0;
        Assert.Equal(1000m, K("cash"));
        Assert.Equal(600m, K("card"));
        Assert.Equal(450m, K("shop_revenue"));
        Assert.Equal(250m, K("shop_cash"));
        Assert.Equal(200m, K("shop_card"));
        Assert.Equal(K("shop_revenue"), K("shop_cash") + K("shop_card") + K("shop_contract") + K("shop_fuel_card"));
        Assert.Equal(2050m, K("revenue"));
    }

    /// <summary>
    /// 1.22: how a sale was paid follows Topaz's till when the till knows it. A card sale whose card was declined is paid
    /// in cash (Topaz keeps the card mode, drops "Б", books the money into the till); a cash mode paid past a kept till
    /// was paid by card (a "Базовый" mode, method chosen at payment); a shift without till records keeps the mode.
    /// </summary>
    [PostgresFact]
    public async Task The_till_decides_how_a_sale_was_paid()
    {
        var org = await fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at, name LIMIT 1");
        var st = await fx.EnrollAsync("АЗС-касса-решает", org);
        var t0 = DateTimeOffset.UtcNow.AddHours(-30);

        RecordChange Shift(int id) => Row("sysSessions", $"{id}", id, new JsonObject
        {
            ["SessionID"] = id, ["SessionNum"] = id, ["UserName"] = "Оператор", ["UserID"] = 1, ["Deleted"] = 0,
            ["StartDateTime"] = Ts(t0.AddHours(10 * (id - 1))), ["EndDateTime"] = Ts(t0.AddHours(10 * (id - 1) + 9)),
        });
        RecordChange Order(int id, int session, int mode, decimal amount, bool b) => Row("flOrders", $"{id}", session, new JsonObject
        {
            ["OrderID"] = id, ["SessionID"] = session, ["OrderNum"] = id, ["DateTime"] = Ts(t0.AddHours(10 * (session - 1)).AddMinutes(10 + id)),
            ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1, ["PaymentModeID"] = mode, ["Volume"] = amount / 50, ["VolumeOrdered"] = 0,
            ["Price"] = 50, ["PricePump"] = 50, ["Amount"] = amount, ["AmountCash"] = b ? 0 : amount, ["AmountCashless"] = b ? amount : 0,
            ["AmountDiscount"] = 0, ["Remark"] = b ? "Б" : "", ["StatusOrder"] = 0,
        });
        // Topaz leaves the cash/cashless amounts of shop receipts empty.
        RecordChange Receipt(int id, int mode, decimal total, bool isReturn) => Row("trFastTradeH", $"{id}", 1, new JsonObject
        {
            ["FastTradeHID"] = id, ["SessionID"] = 1, ["NDoc"] = id, ["DateDoc"] = Ts(t0.AddHours(1).AddMinutes(id)), ["IsReturn"] = isReturn ? 1 : 0,
            ["CashID"] = 3, ["PaymentModeID"] = mode, ["AmountCash"] = 0, ["AmountCashless"] = 0, ["Status"] = 0, ["FiscalNumber"] = 1,
        });
        RecordChange Line(int id, decimal total) => Row("trFastTradeD", $"{id}", 1, new JsonObject
        {
            ["FastTradeDID"] = id, ["FastTradeHID"] = id, ["ItemID"] = 1, ["Quantity"] = 1, ["Price"] = total, ["Amount"] = total, ["Total"] = total,
        });
        RecordChange Cash(int id, int docType, int doc, decimal amount) => Row("rgCashes", $"{id}", 1, new JsonObject
        {
            ["RgCashID"] = id, ["SessionID"] = 1, ["Date"] = Ts(t0.AddHours(2).AddMinutes(id)), ["DocTypeID"] = docType, ["DocID"] = doc, ["CashID"] = 3, ["Amount"] = amount,
        });

        await SendAsync(st.Token,
        [
            Row("dcPaymentModes", "1", null, new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
            Row("dcPaymentModes", "2", null, new JsonObject { ["PaymentModeID"] = 2, ["Name"] = "Терминал Сбербанк" }),
            Shift(1), Shift(2),
            Order(1, 1, 1, 500, false),    // cash, in the till
            Order(2, 1, 2, 1000, true),    // card with "Б"
            Order(3, 1, 2, 700, false),    // card declined, paid in cash: in the till
            Order(4, 1, 2, 300, false),    // card without "Б" (stations that never mark it), not in the till
            Order(5, 1, 1, 200, false),    // cash mode paid by card: not in the till
            Order(6, 2, 1, 100, false),    // shift 2 has no till records: the mode decides
            Receipt(1, 2, 400, false), Line(1, 400),   // card receipt paid in cash
            Receipt(2, 2, 150, false), Line(2, 150),   // card receipt
            Receipt(3, 2, 100, true), Line(3, 100),    // return of a cash-paid card receipt
            Receipt(4, 1, 80, false), Line(4, 80),     // cash mode paid by card
            Cash(1, 14, 0, 500), Cash(2, 6, 1, 500), Cash(3, 6, 3, 700), Cash(4, 5, 1, 400), Cash(5, 5, 3, -100),
        ]);

        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("tillcard-owner", "o", Roles.Owner, "tillcard-password-1", [], org); } catch (PostgresException) { }
        var owner = fx.Factory.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "tillcard-owner", password = "tillcard-password-1" })).EnsureSuccessStatusCode();

        var card = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/shifts/{st.StationId}/1"))!;
        var h = card["header"]!;
        decimal D(string k) => h[k]?.GetValue<decimal>() ?? 0;
        Assert.Equal(1200m, D("pay_cash"));     // 500 + 700 declined card
        Assert.Equal(1500m, D("pay_card"));     // 1 000 "Б" + 300 without "Б" + 200 cash mode past the till
        Assert.Equal(300m, D("shop_cash"));     // 400 − 100 return
        Assert.Equal(230m, D("shop_card"));     // 150 + 80
        Assert.Equal(D("cash_fuel"), D("pay_cash"));
        Assert.Equal(D("cash_shop"), D("shop_cash"));

        JsonNode O(int id) => card["orders"]!.AsArray().Single(o => o!["order_id"]!.GetValue<long>() == id)!;
        Assert.Equal(("cash", "card", "in"), (O(3)["pay_category"]!.GetValue<string>(), O(3)["mode_category"]!.GetValue<string>(), O(3)["till"]!.GetValue<string>()));
        Assert.Equal(("card", "cash", "out"), (O(5)["pay_category"]!.GetValue<string>(), O(5)["mode_category"]!.GetValue<string>(), O(5)["till"]!.GetValue<string>()));

        var h2 = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/shifts/{st.StationId}/2"))!["header"]!;
        Assert.Equal(100m, h2["pay_cash"]!.GetValue<decimal>());

        // Daily rollups say the same.
        var from = t0.AddDays(-1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        var to = DateTimeOffset.UtcNow.AddDays(1).ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd");
        async Task<JsonNode> KpiAsync() => (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/overview?stations={st.StationId}&from={from}&to={to}&compare=none"))!["current"]!;
        var k = await KpiAsync();
        Assert.Equal(1300m, k["cash"]!.GetValue<decimal>());
        Assert.Equal(1500m, k["card"]!.GetValue<decimal>());
        Assert.Equal(300m, k["shop_cash"]!.GetValue<decimal>());
        Assert.Equal(230m, k["shop_card"]!.GetValue<decimal>());

        // A till record coming later (its own batch) rebuilds the rollup: order 4 turns out paid in cash.
        await SendAsync(st.Token, [Cash(6, 6, 4, 300)]);
        k = await KpiAsync();
        Assert.Equal(1600m, k["cash"]!.GetValue<decimal>());
        Assert.Equal(1200m, k["card"]!.GetValue<decimal>());
    }
}
