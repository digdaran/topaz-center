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
        RecordChange Cash(int id, int docType, decimal amount) => Row("rgCashes", $"{id}", 1, new JsonObject
        {
            ["RgCashID"] = id, ["SessionID"] = 1, ["Date"] = Ts(t0.AddHours(2).AddMinutes(id)), ["DocTypeID"] = docType, ["DocID"] = id, ["CashID"] = 3, ["Amount"] = amount,
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
            Cash(1, 14, 500), Cash(2, 6, 1000), Cash(3, 5, 250), Cash(4, 15, -1600),
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
    }
}
