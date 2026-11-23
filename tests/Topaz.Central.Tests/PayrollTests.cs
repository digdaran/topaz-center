using System.IO.Compression;
using System.Net;
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
/// 1.33: operators' pay. A station with two workplaces; shifts with a handover, a short one, an administrator's login
/// without pay and a shift taken out; a station scheme and an operator's own one; manual lines; closing the month.
/// </summary>
public sealed class PayrollTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "payroll-password-1";
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

    private async Task<HttpClient> LoginAsync(string login, string role, Guid org, Guid[]? stations = null)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, stations ?? [], org); } catch (PostgresException) { /* exists */ }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    [PostgresFact]
    public async Task Pay_is_accrued_per_shift_by_the_scheme_and_frozen_when_the_month_is_closed()
    {
        var org = await fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at, name LIMIT 1");
        var st = await fx.EnrollAsync("АЗС-зарплата", org);
        // Everything happens in the previous month (Moscow time), so it can be closed.
        var nowMsk = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
        var monthStart = new DateTimeOffset(nowMsk.Year, nowMsk.Month, 1, 0, 0, 0, TimeSpan.FromHours(3)).AddMonths(-1);
        var month = monthStart.ToString("yyyy-MM");
        var t0 = monthStart.AddDays(1).AddHours(8);

        RecordChange Shift(int id, double from, double to, string user) => Row("sysSessions", $"{id}", id, new JsonObject
        {
            ["SessionID"] = id, ["SessionNum"] = id, ["UserName"] = user, ["UserID"] = 1, ["Deleted"] = 0,
            ["StartDateTime"] = Ts(t0.AddHours(from)), ["EndDateTime"] = Ts(t0.AddHours(to)),
        });
        RecordChange Local(int id, int session, int workbench, int user, double from, double to) => Row("sysLocalSessions", $"{id}", session, new JsonObject
        {
            ["LocalSessionID"] = id, ["SessionID"] = session, ["WorkbenchID"] = workbench, ["UserID"] = user, ["RegFault"] = 0,
            ["StartDateTime"] = Ts(t0.AddHours(from)), ["EndDateTime"] = Ts(t0.AddHours(to)),
        });
        var orderId = 0;
        RecordChange Order(int session, int local, decimal volume, decimal amount, double at)
        {
            orderId++;
            return Row("flOrders", $"{orderId}", session, new JsonObject
            {
                ["OrderID"] = orderId, ["SessionID"] = session, ["OrderNum"] = orderId, ["DateTime"] = Ts(t0.AddHours(at)), ["LocalSessionID"] = local,
                ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1, ["PaymentModeID"] = 1, ["Volume"] = volume, ["VolumeOrdered"] = volume,
                ["Price"] = 50, ["PricePump"] = 50, ["Amount"] = amount, ["AmountCash"] = amount, ["AmountCashless"] = 0, ["AmountDiscount"] = 0,
                ["Remark"] = "", ["StatusOrder"] = 0,
            });
        }

        var items = new List<RecordChange>
        {
            Row("dcPaymentModes", "1", null, new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
            Row("sysWorkbenchs", "2", null, new JsonObject { ["WorkbenchID"] = 2, ["Name"] = "Основное рабочее место", ["WorkbenchTypeID"] = 0, ["Deleted"] = 0 }),
            Row("sysWorkbenchs", "4", null, new JsonObject { ["WorkbenchID"] = 4, ["Name"] = "Дополнительное рабочее место", ["WorkbenchTypeID"] = 3, ["Deleted"] = 0 }),
            Row("sysUsers", "110", null, new JsonObject { ["UserID"] = 110, ["Login"] = "Инна", ["Deleted"] = 0 }),
            Row("sysUsers", "112", null, new JsonObject { ["UserID"] = 112, ["Login"] = "Оля", ["Deleted"] = 0 }),
            Row("sysUsers", "199", null, new JsonObject { ["UserID"] = 199, ["Login"] = "Админ", ["Deleted"] = 0 }),
            Row("dcItemsGrps", "1", null, new JsonObject { ["ItemsGrpID"] = 1, ["Name"] = "Табак" }),
            Row("dcItemsGrps", "2", null, new JsonObject { ["ItemsGrpID"] = 2, ["Name"] = "Кофе" }),
            Row("dcItems", "1", null, new JsonObject { ["ItemID"] = 1, ["Name"] = "Сигареты", ["ItemsGrpID"] = 1 }),
            Row("dcItems", "2", null, new JsonObject { ["ItemID"] = 2, ["Name"] = "Капучино", ["ItemsGrpID"] = 2 }),
            // 1: both workplaces all day — Inna at the main one, Olya at the additional one.
            Shift(1, 0, 24, "Инна"), Local(11, 1, 2, 110, 0, 24), Local(12, 1, 4, 112, 0, 24),
            Order(1, 11, 20, 1000, 1), Order(1, 12, 40, 2000, 2),
            // 2: one workplace, handover after 6 h: Inna 6 h, Olya 18 h.
            Shift(2, 24, 48, "Инна"), Local(21, 2, 2, 110, 24, 30), Local(22, 2, 2, 112, 30, 48), Order(2, 22, 10, 500, 40),
            // 3: opened by mistake, closed after half an hour.
            Shift(3, 48, 48.5, "Инна"), Local(31, 3, 2, 110, 48, 48.5), Order(3, 31, 1, 50, 48.2),
            // 4: the administrator opened it for an hour, then Inna for 23 h; the administrator gets no pay.
            Shift(4, 49, 73, "Админ"), Local(41, 4, 2, 199, 49, 50), Local(42, 4, 2, 110, 50, 73), Order(4, 41, 10, 500, 49.5),
            // 5: taken out of the payroll by the owner.
            Shift(5, 74, 98, "Оля"), Local(51, 5, 2, 112, 74, 98), Order(5, 51, 100, 5000, 80),
        };
        // Shop receipt of Inna in shift 1: cigarettes 300 (group without %), coffee 200.
        items.Add(Row("trFastTradeH", "1", 1, new JsonObject
        {
            ["FastTradeHID"] = 1, ["SessionID"] = 1, ["NDoc"] = 1, ["DateDoc"] = Ts(t0.AddHours(3)), ["IsReturn"] = 0, ["CashID"] = 3, ["PaymentModeID"] = 1,
            ["AmountCash"] = 500, ["AmountCashless"] = 0, ["Status"] = 0, ["FiscalNumber"] = 1, ["LocalSessionID"] = 11,
        }));
        items.Add(Row("trFastTradeD", "1", 1, new JsonObject { ["FastTradeDID"] = 1, ["FastTradeHID"] = 1, ["ItemID"] = 1, ["Quantity"] = 1, ["Price"] = 300, ["Amount"] = 300, ["Total"] = 300 }));
        items.Add(Row("trFastTradeD", "2", 1, new JsonObject { ["FastTradeDID"] = 2, ["FastTradeHID"] = 1, ["ItemID"] = 2, ["Quantity"] = 1, ["Price"] = 200, ["Amount"] = 200, ["Total"] = 200 }));
        await SendAsync(st.Token, items);

        var owner = await LoginAsync("payroll-owner", Roles.Owner, org);
        var s = st.StationId;
        var q = $"stations={s}&month={month}";

        // Without a scheme nothing is accrued, but the station is listed and says so.
        var empty = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/payroll?{q}"))!["stations"]!.AsArray().Single()!;
        Assert.False(empty["has_scheme"]!.GetValue<bool>());
        Assert.Equal(0m, empty["totals"]!["accrued"]!.GetValue<decimal>());

        var from = monthStart.ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/ui/payroll/schemes", new { station_id = s, valid_from = from })).StatusCode);
        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/schemes", new
        {
            station_id = s, valid_from = from, shift_fixed = 3000m, fuel_pct = 2m, shop_pct = 10m, per_liter = 0m, min_shift_hours = 1m, excluded_groups = new[] { 1L },
        })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/schemes", new { station_id = s, login = "Оля", valid_from = from, shift_fixed = 3500m, fuel_pct = 0m, shop_pct = 0m, per_liter = 0.5m })).EnsureSuccessStatusCode();
        // A later change of Inna's scheme does not touch this month.
        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/schemes", new { station_id = s, login = "Инна", valid_from = monthStart.AddMonths(1).ToString("yyyy-MM-dd"), shift_fixed = 9999m, fuel_pct = 0m, shop_pct = 0m, per_liter = 0m })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v1/ui/payroll/employees", new { station_id = s, login = "Админ", no_pay = true })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v1/ui/payroll/exclusions", new { station_id = s, session_id = 5, excluded = true })).EnsureSuccessStatusCode();

        var schemes = (await owner.GetFromJsonAsync<JsonArray>($"/api/v1/ui/payroll/schemes?stations={s}"))!.Single()!;
        Assert.Single(schemes["schemes"]!.AsArray());
        Assert.Equal(["Кофе", "Табак"], schemes["groups"]!.AsArray().Select(g => g!["name"]!.GetValue<string>()).Order());
        var admin = schemes["employees"]!.AsArray().Single(e => e!["login"]!.GetValue<string>() == "Админ")!;
        Assert.True(admin["no_pay"]!.GetValue<bool>());
        Assert.Single(schemes["employees"]!.AsArray().Single(e => e!["login"]!.GetValue<string>() == "Инна")!["schemes"]!.AsArray());

        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/adjustments", new { station_id = s, login = "Инна", month, kind = "bonus", amount = 500m, comment = "за чистоту" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/adjustments", new { station_id = s, login = "Инна", month, kind = "fine", amount = 200m })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/adjustments", new { station_id = s, login = "Инна", month, kind = "advance", amount = 1000m })).EnsureSuccessStatusCode();

        var sheet = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/payroll?{q}"))!;
        var station = sheet["stations"]!.AsArray().Single()!;
        Assert.True(station["has_scheme"]!.GetValue<bool>());
        var employees = station["employees"]!.AsArray();
        Assert.Equal(["Инна", "Оля"], employees.Select(e => e!["login"]!.GetValue<string>()).Order());
        var inna = employees.Single(e => e!["login"]!.GetValue<string>() == "Инна")!;
        // 1: 3000 + 2 % of 1000 + 10 % of the coffee 200; 2: 6/24 of 3000; 3: short, only 2 % of 50; 4: 23/24 of 3000.
        Assert.Equal(3000m + 750m + 2875m, inna["fixed"]!.GetValue<decimal>());
        Assert.Equal(21m, inna["fuel_pay"]!.GetValue<decimal>());
        Assert.Equal(20m, inna["shop_pay"]!.GetValue<decimal>());
        Assert.Equal(6666m, inna["accrued"]!.GetValue<decimal>());
        Assert.Equal(6966m, inna["total"]!.GetValue<decimal>());
        Assert.Equal(5966m, inna["to_pay"]!.GetValue<decimal>());
        Assert.Equal(4, inna["shifts"]!.GetValue<int>());
        var olya = employees.Single(e => e!["login"]!.GetValue<string>() == "Оля")!;
        // Her own scheme: 1: 3500 + 40 l × 0.5; 2: 18/24 of 3500 + 10 l × 0.5; 5 is taken out.
        Assert.Equal(3500m + 2625m, olya["fixed"]!.GetValue<decimal>());
        Assert.Equal(25m, olya["liter_pay"]!.GetValue<decimal>());
        Assert.Equal(6150m, olya["accrued"]!.GetValue<decimal>());
        Assert.Equal(2, olya["shifts"]!.GetValue<int>());

        var slip = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/payroll/slip?station={s}&login=Инна&month={month}"))!;
        var slipShifts = slip["shifts"]!.AsArray();
        Assert.Equal(4, slipShifts.Count);
        Assert.True(slipShifts.Single(x => x!["session_id"]!.GetValue<long>() == 3)!["short"]!.GetValue<bool>());
        Assert.Equal(0.25m, slipShifts.Single(x => x!["session_id"]!.GetValue<long>() == 2)!["fixed_share"]!.GetValue<decimal>());
        Assert.Equal(3, slip["adjustments"]!.AsArray().Count);

        // Finance: the period's pay is an expense for the owner; a manager does not see it, nor the payroll itself.
        var period = $"stations={s}&from={from}&to={monthStart.AddMonths(1).AddDays(-1):yyyy-MM-dd}";
        var finance = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}"))!;
        Assert.Equal(6666m + 6150m + 300m, finance["payroll"]!["total"]!.GetValue<decimal>());
        var manager = await LoginAsync("payroll-manager", Roles.Manager, org, [s]);
        Assert.Null((await manager.GetFromJsonAsync<JsonObject>($"/api/v1/ui/analytics/finance?{period}"))!["payroll"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/ui/payroll?{q}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/v1/ui/payroll/schemes?stations={s}")).StatusCode);

        // «Выплачено» needs a closed month; the current month cannot be closed yet.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/ui/payroll/payouts", new { station_id = s, month, login = "Инна", paid = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/ui/payroll/close", new { station_id = s, month = nowMsk.ToString("yyyy-MM") })).StatusCode);
        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/close", new { station_id = s, month })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/ui/payroll/close", new { station_id = s, month })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/v1/ui/payroll/adjustments", new { station_id = s, login = "Оля", month, kind = "bonus", amount = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync("/api/v1/ui/payroll/exclusions", new { station_id = s, session_id = 5, excluded = false })).StatusCode);
        (await owner.PutAsJsonAsync("/api/v1/ui/payroll/payouts", new { station_id = s, month, login = "Инна", paid = true })).EnsureSuccessStatusCode();

        // Late data from Topaz: one more order of Olya in shift 2. The closed sums stay; the change is reported.
        await SendAsync(st.Token, [Order(2, 22, 100, 5000, 41)]);
        station = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/payroll?{q}"))!["stations"]!.AsArray().Single()!;
        Assert.NotNull(station["closed_at"]);
        Assert.Equal(6150m, station["employees"]!.AsArray().Single(e => e!["login"]!.GetValue<string>() == "Оля")!["accrued"]!.GetValue<decimal>());
        Assert.Equal(5966m, station["employees"]!.AsArray().Single(e => e!["login"]!.GetValue<string>() == "Инна")!["paid"]!["amount"]!.GetValue<decimal>());
        Assert.Equal(6966m + 6150m, station["changed"]!["was"]!.GetValue<decimal>());
        Assert.Equal(6966m + 6150m + 50m, station["changed"]!["now"]!.GetValue<decimal>());

        // CSV for Excel.
        var csv = await owner.GetStringAsync($"/api/v1/ui/payroll?{q}&format=csv");
        Assert.Contains("\"Инна\"", csv);
        Assert.Contains("5966,00", csv);

        // Reopening drops the frozen sums and the «Выплачено» marks; the live figures are back.
        (await owner.PostAsJsonAsync("/api/v1/ui/payroll/reopen", new { station_id = s, month })).EnsureSuccessStatusCode();
        station = (await owner.GetFromJsonAsync<JsonObject>($"/api/v1/ui/payroll?{q}"))!["stations"]!.AsArray().Single()!;
        Assert.Null(station["closed_at"]);
        Assert.Equal(6200m, station["employees"]!.AsArray().Single(e => e!["login"]!.GetValue<string>() == "Оля")!["accrued"]!.GetValue<decimal>());
        Assert.Null(station["employees"]!.AsArray().Single(e => e!["login"]!.GetValue<string>() == "Инна")!["paid"]);
    }

    /// <summary>
    /// The image runs with invariant globalization (no ICU): GetCultureInfo("ru-RU") throws there, while tests run with
    /// ICU and would never notice. CSV numbers use <see cref="RuNumbers"/>.
    /// </summary>
    [Fact]
    public void Central_code_does_not_ask_for_cultures_the_image_does_not_have()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Topaz.Central"));
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadLines(f).Any(l => !l.TrimStart().StartsWith("///") && (l.Contains("GetCultureInfo(") || l.Contains("new CultureInfo("))))
            .Select(Path.GetFileName).ToList();
        Assert.True(Directory.Exists(root), root);
        Assert.Empty(offenders);
    }

    [Fact]
    public void A_login_scheme_wins_from_its_date_and_the_station_scheme_covers_the_rest()
    {
        var st = Guid.NewGuid();
        var schemes = new List<Payroll.Scheme>
        {
            new() { Id = 1, StationId = st, ValidFrom = new DateTime(2026, 1, 1), ShiftFixed = 1000 },
            new() { Id = 2, StationId = st, ValidFrom = new DateTime(2026, 3, 1), ShiftFixed = 2000 },
            new() { Id = 3, StationId = st, Login = "Оля", ValidFrom = new DateTime(2026, 2, 15), ShiftFixed = 3000 },
        };
        Assert.Null(Payroll.SchemeFor(schemes, st, "Инна", new DateTime(2025, 12, 31)));
        Assert.Equal(1, Payroll.SchemeFor(schemes, st, "Инна", new DateTime(2026, 2, 20))!.Id);
        Assert.Equal(2, Payroll.SchemeFor(schemes, st, "Инна", new DateTime(2026, 3, 1))!.Id);
        Assert.Equal(1, Payroll.SchemeFor(schemes, st, "Оля", new DateTime(2026, 2, 14))!.Id);
        Assert.Equal(3, Payroll.SchemeFor(schemes, st, "Оля", new DateTime(2026, 4, 1))!.Id);
    }

    [Fact]
    public void An_open_shift_gets_the_fixed_part_at_once_and_two_logins_at_one_workplace_share_it()
    {
        var st = Guid.NewGuid();
        var now = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        var input = new Payroll.Input(
            [new Payroll.Shift { StationId = st, SessionId = 1, StartedAt = now.AddMinutes(-20), LocalDate = now.Date }],
            [new Payroll.Presence { StationId = st, SessionId = 1, WorkbenchId = 2, Login = "Инна", Secs = 600 },
             new Payroll.Presence { StationId = st, SessionId = 1, WorkbenchId = 2, Login = "Оля", Secs = 600 }],
            [new Payroll.Sales { StationId = st, SessionId = 1, Login = "Оля", Fuel = 1000, Liters = 20 }],
            [new Payroll.Scheme { Id = 1, StationId = st, ValidFrom = now.Date, ShiftFixed = 3000, FuelPct = 1.5m, MinShiftHours = 1 }],
            []);
        var pay = Payroll.Compute(input, now);
        Assert.All(pay, p => Assert.True(p.Open));
        Assert.Equal(1500m, pay.Single(p => p.Login == "Инна").Total);
        Assert.Equal(1515m, pay.Single(p => p.Login == "Оля").Total);
        var sum = Payroll.Summarize(pay, [new Payroll.Adjustment { StationId = st, Login = "Оля", Kind = "advance", Amount = 0.4m }]);
        Assert.Equal(1515m, sum.Single(e => e.Login == "Оля").ToPay); // 1514.6 → rounded to the rouble
    }
}
