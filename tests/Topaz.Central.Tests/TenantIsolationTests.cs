using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// Station owners share one server and must never see each other: every UI route (found automatically),
/// every mutation by a foreign id, the database's row level security, fuel suggestions, Telegram routing,
/// rule thresholds and the superadmin's organization switch.
/// </summary>
public sealed class TenantIsolationTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "tenant-password-1";
    private const string Period = "from=2026-10-01&to=2026-10-31&compare=none";

    private sealed record Tenant(Guid Org, Guid Station, string StationName, string OwnerLogin, string ChatId);

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static RecordChange Row(string dataset, string key, JsonObject data) => new() { Dataset = dataset, Key = key, Hash = Hash(dataset + key + data.ToJsonString()), Data = data };

    private async Task<HttpClient> LoginAsync(string login, string role, Guid? org, params Guid[] stations)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, stations, org); } catch (PostgresException) { /* exists */ }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    private static readonly SemaphoreSlim Once = new(1, 1);
    private static (Tenant A, Tenant B)? _tenants;

    /// <summary>Two organizations, each with a station full of data, an owner, a Telegram chat and a cost "for all stations".</summary>
    private async Task<(Tenant A, Tenant B, HttpClient Super)> SetupAsync()
    {
        var super = await LoginAsync("iso-super", Roles.Superadmin, null);
        await Once.WaitAsync();
        try
        {
            if (_tenants is null)
            {
                var a = await TenantAsync(super, "Альфа", "ISO-Альфа-АЗС");
                var b = await TenantAsync(super, "Бета", "ISO-Бета-АЗС");
                _tenants = (a, b);
            }
        }
        finally { Once.Release(); }
        return (_tenants.Value.A, _tenants.Value.B, super);
    }

    private async Task<Tenant> TenantAsync(HttpClient super, string orgName, string stationName)
    {
        var created = await super.PostAsJsonAsync("/api/v1/ui/organizations", new { name = orgName });
        created.EnsureSuccessStatusCode();
        var org = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var st = await fx.EnrollAsync(stationName, org);
        var items = new List<RecordChange>
        {
            Row("dcFuels", "1", new JsonObject { ["FuelID"] = 1, ["Name"] = "Аи-95 Ultimate", ["Deleted"] = 0 }),
            Row("dcPaymentModes", "1", new JsonObject { ["PaymentModeID"] = 1, ["Name"] = "Наличные" }),
            Row("dcTanks", "1", new JsonObject { ["TankID"] = 1, ["Num"] = 1, ["FuelID"] = 1, ["MaxVolume"] = 10000, ["DeadRestLiter"] = 300, ["Deleted"] = 0 }),
            Row("sysSessions", "1", new JsonObject { ["SessionID"] = 1, ["SessionNum"] = 1, ["UserName"] = "Оператор " + orgName, ["StartDateTime"] = "2026-10-10T08:00:00.0000+03:00", ["EndDateTime"] = "2026-10-11T08:00:00.0000+03:00", ["Deleted"] = 0 }),
            Row("flOrders", "1", new JsonObject
            {
                ["OrderID"] = 1, ["SessionID"] = 1, ["OrderNum"] = 1, ["DateTime"] = "2026-10-10T12:00:00.0000+03:00", ["FuelID"] = 1, ["TankID"] = 1, ["HoseID"] = 1,
                ["Volume"] = 40m, ["Amount"] = 2400m, ["Price"] = 60m, ["PaymentModeID"] = 1, ["Remark"] = "", ["AmountCash"] = 2400m, ["AmountCashless"] = 0, ["AmountDiscount"] = 0,
            }),
            Row("sysEvents", "1", new JsonObject { ["EventID"] = 1, ["DateTime"] = "2026-10-10T12:00:00.0000+03:00", ["Text"] = "Событие " + orgName }),
        };
        var batch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t", Items = items };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gz, batch, Protocol.Json);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        (await fx.Client(st.Token).PostAsync("/api/v1/batches", content)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();

        var owner = await LoginAsync("iso-owner-" + orgName, Roles.Owner, org);
        var chat = "-100" + Math.Abs(orgName.GetHashCode() % 1000000);
        (await owner.PostAsJsonAsync("/api/v1/ui/notifications", new { target = chat, title = "Чат " + orgName, min_severity = "info", daily_digest = false })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/v1/ui/costs/items", new { item_key = "name:кофе " + orgName, cost = 50m })).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/v1/ui/catalog/fuels/{st.StationId}/1", new { fuel_type = "AI95" })).EnsureSuccessStatusCode();
        return new Tenant(org, st.StationId, stationName, "iso-owner-" + orgName, chat);
    }

    private static string[] Secrets(Tenant t) => [t.Station.ToString(), t.StationName, t.OwnerLogin, t.ChatId, "Оператор " + t.OwnerLogin.Split('-')[^1], "кофе " + t.OwnerLogin.Split('-')[^1]];

    /// <summary>Every GET route of the web UI, found in the routing table (a new route is checked automatically).</summary>
    private IEnumerable<string> UiGetRoutes() =>
        fx.Factory!.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/ui/", StringComparison.Ordinal) == true
                        && e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(e => e.RoutePattern.RawText!).Distinct();

    private IEnumerable<(string Method, string Route)> UiMutationsWithAStation() =>
        fx.Factory!.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/ui/", StringComparison.Ordinal) == true
                        && (e.RoutePattern.RawText.Contains("{station", StringComparison.Ordinal) || e.RoutePattern.RawText.StartsWith("/api/v1/ui/stations/{id", StringComparison.Ordinal)))
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Where(m => m != "GET").Select(m => (m, e.RoutePattern.RawText!)))
            .Distinct();

    private static string Fill(string route, Guid station) =>
        System.Text.RegularExpressions.Regex.Replace(route, @"\{(\w+)(:[^}]*)?\}", m => m.Groups[1].Value switch
        {
            "station" or "id" => station.ToString(),
            "session" or "tank" or "fuel" => "1",
            "code" => "AI95",
            _ => "1",
        });

    [PostgresFact]
    public async Task An_owner_sees_nothing_of_another_organization_on_any_route()
    {
        var (a, b, _) = await SetupAsync();
        var owner = await LoginAsync(a.OwnerLogin, Roles.Owner, a.Org);
        var routes = UiGetRoutes().ToList();
        Assert.True(routes.Count > 25, $"only {routes.Count} GET routes found");
        foreach (var route in routes)
        {
            // Routes with a station in the path are asked about the foreign one; the rest with the common filter.
            var url = route.Contains('{') ? Fill(route, b.Station) : route;
            var response = await owner.GetAsync(url + (url.Contains('?') ? "&" : "?") + Period);
            var body = await response.Content.ReadAsStringAsync();
            if (route.Contains('{')) Assert.True(!response.IsSuccessStatusCode || !body.Contains(b.Station.ToString()), $"{url}: {(int)response.StatusCode}");
            foreach (var secret in Secrets(b))
                Assert.False(body.Contains(secret, StringComparison.Ordinal), $"{url} leaks «{secret}» of the other organization");
        }
        // The owner's own data is there (the check above is not vacuous).
        Assert.Contains(a.StationName, await owner.GetStringAsync("/api/v1/ui/meta"));
        Assert.Contains("Оператор Альфа", await owner.GetStringAsync("/api/v1/ui/shifts?" + Period));
        // Filters naming a foreign station are refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v1/ui/analytics/overview?stations={b.Station}&{Period}")).StatusCode);
    }

    [PostgresFact]
    public async Task An_owner_cannot_change_anything_of_another_organization()
    {
        var (a, b, _) = await SetupAsync();
        var owner = await LoginAsync(a.OwnerLogin, Roles.Owner, a.Org);
        var before = await fx.QueryAsync<string>("SELECT md5(string_agg(t::text, '|' ORDER BY t::text)) FROM (SELECT s::text AS t FROM stations s WHERE id = @s UNION ALL SELECT m::text FROM station_fuel_map m WHERE station_id = @s UNION ALL SELECT x::text FROM tank_settings x WHERE station_id = @s) q", new { s = b.Station });
        var mutations = UiMutationsWithAStation().ToList();
        Assert.True(mutations.Count >= 6, $"only {mutations.Count} mutation routes found");
        foreach (var (method, route) in mutations)
        {
            var url = Fill(route, b.Station);
            using var req = new HttpRequestMessage(new HttpMethod(method), url)
            {
                Content = JsonContent.Create(new { fuel_type = "DT", display_name = "Взлом", min_liters = 1, lead_time_days = 1, safety_days = 1, update_group = "pilot", action = "block", organization_id = a.Org }),
            };
            var r = await owner.SendAsync(req);
            Assert.False(r.IsSuccessStatusCode, $"{method} {url} succeeded with {(int)r.StatusCode}");
        }
        // Mutations that carry the station in the body.
        Assert.False((await owner.PostAsJsonAsync("/api/v1/ui/costs/fuel", new { station_id = b.Station, fuel_id = 1, price_per_liter = 1m, valid_from = DateTimeOffset.UtcNow })).IsSuccessStatusCode);
        Assert.False((await owner.PutAsJsonAsync("/api/v1/ui/plans", new { station_id = b.Station, month = "2026-10-01", metric = "fuel_liters", fuel_type = "", value = 1 })).IsSuccessStatusCode);
        Assert.False((await owner.PostAsJsonAsync("/api/v1/ui/costs/items", new { station_id = b.Station, item_key = "x", cost = 1m })).IsSuccessStatusCode);
        var bCost = await fx.QueryAsync<long>("SELECT id FROM item_costs WHERE organization_id = @o LIMIT 1", new { o = b.Org });
        Assert.False((await owner.DeleteAsync($"/api/v1/ui/costs/items/{bCost}")).IsSuccessStatusCode);
        var bChat = await fx.QueryAsync<long>("SELECT id FROM notification_channels WHERE organization_id = @o LIMIT 1", new { o = b.Org });
        Assert.False((await owner.DeleteAsync($"/api/v1/ui/notifications/{bChat}")).IsSuccessStatusCode);
        Assert.Equal(1, await fx.QueryAsync<int>("SELECT count(*)::int FROM notification_channels WHERE id = @bChat", new { bChat }));
        Assert.Equal(0, await fx.QueryAsync<int>("SELECT count(*)::int FROM sales_plans WHERE station_id = @s", new { s = b.Station }));

        Assert.Equal(before, await fx.QueryAsync<string>("SELECT md5(string_agg(t::text, '|' ORDER BY t::text)) FROM (SELECT s::text AS t FROM stations s WHERE id = @s UNION ALL SELECT m::text FROM station_fuel_map m WHERE station_id = @s UNION ALL SELECT x::text FROM tank_settings x WHERE station_id = @s) q", new { s = b.Station }));

        // Users: an owner cannot see, edit or create users of another organization.
        var bOwnerId = await fx.QueryAsync<Guid>("SELECT id FROM users WHERE login = @l", new { l = b.OwnerLogin });
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PatchAsJsonAsync($"/api/v1/ui/users/{bOwnerId}", new { is_active = false })).StatusCode);
        var created = await owner.PostAsJsonAsync("/api/v1/ui/users", new { login = "iso-sneaky", name = "x", role = "manager", password = Password, organization_id = b.Org, stations = new[] { b.Station } });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode); // a foreign station
        var plain = await owner.PostAsJsonAsync("/api/v1/ui/users", new { login = "iso-own-manager", name = "x", role = "manager", password = Password, organization_id = b.Org });
        plain.EnsureSuccessStatusCode();
        Assert.Equal(a.Org, await fx.QueryAsync<Guid>("SELECT organization_id FROM users WHERE login = 'iso-own-manager'")); // stays in the owner's organization
    }

    [PostgresFact]
    public async Task Row_level_security_stops_a_query_that_forgot_its_filter()
    {
        var (a, b, _) = await SetupAsync();
        await using var ui = UiDb.BuildUiSource(fx.ConnectionString);
        await using var c = await ui.OpenConnectionAsync();
        var tables = new[] { "stations", "records", "raw_batches", "shifts", "fuel_sales", "agg_fuel_daily", "station_fuel_map", "item_costs", "users", "audit_log", "notification_channels" };

        // No scope set (a bug): nothing at all.
        foreach (var t in tables)
            Assert.Equal(0L, await c.ExecuteScalarAsync<long>($"SELECT count(*) FROM {t}"));

        // Scope of organization A: its rows only, even without any WHERE.
        await c.ExecuteAsync("SELECT set_config('app.stations', @s, false), set_config('app.org', @o, false), set_config('app.platform', 'off', false)",
            new { s = "{" + a.Station + "}", o = a.Org.ToString() });
        Assert.Equal(new[] { a.Station }, (await c.QueryAsync<Guid>("SELECT id FROM stations")).ToArray());
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT count(*) FROM fuel_sales WHERE station_id = @b", new { b = b.Station }));
        Assert.True(await c.ExecuteScalarAsync<long>("SELECT count(*) FROM fuel_sales") >= 1);
        Assert.DoesNotContain(b.OwnerLogin, (await c.QueryAsync<string>("SELECT login FROM users")).ToList());
        Assert.DoesNotContain(b.ChatId, (await c.QueryAsync<string>("SELECT target FROM notification_channels")).ToList());
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT count(*) FROM station_registrations"));
        // Writing into the other organization is refused by the database itself.
        var write = await Assert.ThrowsAsync<PostgresException>(() =>
            c.ExecuteAsync("INSERT INTO tank_settings (station_id, tank_id, min_liters) VALUES (@b, 1, 1)", new { b = b.Station }));
        Assert.Equal("42501", write.SqlState); // row level security violation
    }

    [PostgresFact]
    public async Task Every_view_runs_with_the_callers_rights()
    {
        await SetupAsync();
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        var unsafeViews = (await c.QueryAsync<string>("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'v' AND NOT COALESCE('security_invoker=true' = ANY(c.reloptions), false)
            """)).ToList();
        Assert.True(unsafeViews.Count == 0, "views without security_invoker (they would bypass row level security): " + string.Join(", ", unsafeViews));

        // A table added later with station data must get its own policy (018 only covered the tables of its time).
        var unprotected = (await c.QueryAsync<string>("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
              AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname IN ('station_id', 'organization_id') AND NOT a.attisdropped)
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity AND EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid))
            """)).ToList();
        Assert.True(unprotected.Count == 0, "tables with station/organization data but no row level security policy: " + string.Join(", ", unprotected));
    }

    [PostgresFact]
    public async Task Fuel_suggestions_rules_and_telegram_stay_inside_the_organization()
    {
        var (a, b, super) = await SetupAsync();

        // Both stations call their fuel "Аи-95 Ultimate"; B confirmed it, A confirmed it too — but learning never crosses organizations.
        var cStation = await fx.EnrollAsync("ISO-Альфа-АЗС-2", a.Org);
        var fuelBatch = new BatchEnvelope { BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t",
            Items = [Row("dcFuels", "7", new JsonObject { ["FuelID"] = 7, ["Name"] = "АИ 95 ultimate", ["Deleted"] = 0 })] };
        (await fx.Client(cStation.Token).PostAsJsonAsync("/api/v1/batches", fuelBatch, Protocol.Json)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
        {
            var learnedFrom = await c.ExecuteScalarAsync<Guid?>("SELECT suggested_from FROM v_station_fuels WHERE station_id = @s AND fuel_id = 7", new { s = cStation.StationId });
            Assert.Equal(a.Station, learnedFrom); // from its own organization, never from B
        }

        // Thresholds: A raises one, B keeps the default.
        var ownerA = await LoginAsync(a.OwnerLogin, Roles.Owner, a.Org);
        (await ownerA.PutAsJsonAsync("/api/v1/ui/alert-rules/service_volume", new { enabled = true, threshold = 500 })).EnsureSuccessStatusCode();
        Assert.Equal(500m, await fx.QueryAsync<decimal>("SELECT threshold FROM v_station_rules WHERE station_id = @s AND code = 'service_volume'", new { s = a.Station }));
        Assert.Equal(50m, await fx.QueryAsync<decimal>("SELECT threshold FROM v_station_rules WHERE station_id = @s AND code = 'service_volume'", new { s = b.Station }));
        var rulesB = await (await LoginAsync(b.OwnerLogin, Roles.Owner, b.Org)).GetStringAsync("/api/v1/ui/alert-rules");
        Assert.DoesNotContain("500", rulesB);

        // Telegram: an event of A goes to A's chat only; a connection request to the superadmin's chat only.
        var telegram = new ControlTests.FakeTelegram();
        await using var app = fx.Factory!.WithWebHostBuilder(w =>
        {
            w.UseSetting("Topaz:TelegramBotToken", "123:secret");
            w.UseSetting("Topaz:TelegramApiBase", "http://telegram.test");
            w.ConfigureTestServices(s => s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => telegram));
        });
        (await super.PostAsJsonAsync("/api/v1/ui/notifications", new { target = "-100999", title = "Платформа", min_severity = "info", daily_digest = false, platform = true })).EnsureSuccessStatusCode();
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
        {
            await c.ExecuteAsync("UPDATE alerts SET notified_at = now() WHERE notified_at IS NULL");
            await c.ExecuteAsync("""
                INSERT INTO alerts (station_id, rule_code, dedupe_key, severity, title, occurred_at) VALUES (@s, 'shift_too_long', 'iso', 'warning', 'Событие только для Альфы', now())
                """, new { s = a.Station });
        }
        var agent = app.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.77");
        (await agent.PostAsJsonAsync("/api/v1/registrations", new RegistrationRequest { StationName = "ISO-Заявка", ClaimHash = RegistrationCode.ClaimHash(RegistrationCode.NewSecret()) }, Protocol.Json)).EnsureSuccessStatusCode();
        await app.Services.GetRequiredService<AnomalyDetector>().RunAsync(CancellationToken.None);

        Assert.Contains(telegram.Sent, m => m.Body.StartsWith(a.ChatId + "\n") && m.Body.Contains("Событие только для Альфы"));
        Assert.DoesNotContain(telegram.Sent, m => m.Body.StartsWith(b.ChatId + "\n"));
        Assert.Contains(telegram.Sent, m => m.Body.StartsWith("-100999\n") && m.Body.Contains("ISO-Заявка"));
        Assert.DoesNotContain(telegram.Sent, m => m.Body.StartsWith(a.ChatId + "\n") && m.Body.Contains("ISO-Заявка"));
    }

    [PostgresFact]
    public async Task The_superadmin_sees_one_organization_at_a_time_and_the_switch_is_audited()
    {
        var (a, b, super) = await SetupAsync();
        await super.PostAsJsonAsync("/api/v1/ui/session/organization", new { id = a.Org });
        var me = (await super.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!;
        Assert.Equal(a.Org, me["organization_id"]!.GetValue<Guid>());
        Assert.True(me["organizations"]!.AsArray().Count >= 2);
        Assert.DoesNotContain(b.StationName, await super.GetStringAsync("/api/v1/ui/meta"));

        (await super.PostAsJsonAsync("/api/v1/ui/session/organization", new { id = b.Org })).EnsureSuccessStatusCode();
        var meta = await super.GetStringAsync("/api/v1/ui/meta");
        Assert.Contains(b.StationName, meta);
        Assert.DoesNotContain(a.StationName, meta);
        Assert.Contains("organization_switched", await super.GetStringAsync("/api/v1/ui/audit"));

        // A new station must be given an organization when there are several.
        var agent = fx.Client();
        agent.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.88");
        var secret = RegistrationCode.NewSecret();
        var reg = (await (await agent.PostAsJsonAsync("/api/v1/registrations", new RegistrationRequest { StationName = "ISO-Без-организации", ClaimHash = RegistrationCode.ClaimHash(secret) }, Protocol.Json))
            .Content.ReadFromJsonAsync<RegistrationResponse>(Protocol.Json))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PostAsJsonAsync($"/api/v1/ui/registrations/{reg.RegistrationId}/approve", new { code = reg.VerificationCode })).StatusCode);
        (await super.PostAsJsonAsync($"/api/v1/ui/registrations/{reg.RegistrationId}/approve", new { code = reg.VerificationCode, organization_id = b.Org })).EnsureSuccessStatusCode();
        Assert.Equal(b.Org, await fx.QueryAsync<Guid>("SELECT organization_id FROM stations WHERE name = 'ISO-Без-организации'"));

        // Disabling an organization signs its users out.
        var ownerB = await LoginAsync(b.OwnerLogin, Roles.Owner, b.Org);
        (await super.PatchAsJsonAsync($"/api/v1/ui/organizations/{b.Org}", new { is_active = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ownerB.GetAsync("/api/v1/ui/meta")).StatusCode);
        (await super.PatchAsJsonAsync($"/api/v1/ui/organizations/{b.Org}", new { is_active = true })).EnsureSuccessStatusCode();
    }
    [PostgresFact]
    public async Task A_transferred_station_takes_its_history_and_own_costs_to_the_new_organization()
    {
        var (_, b, super) = await SetupAsync();
        var g = await TenantAsync(super, "Гамма", "ISO-Гамма-АЗС");
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
            await c.ExecuteAsync("""
                INSERT INTO item_costs (station_id, item_key, valid_from, cost, created_by, organization_id)
                VALUES (@s, 'name:чай', '2026-01-01', 20, 'test', @o)
                """, new { s = g.Station, o = g.Org });

        (await super.PostAsJsonAsync($"/api/v1/ui/stations/{g.Station}/organization", new { organization_id = b.Org })).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync(); // margins rebuilt with the new organization's costs

        var ownerB = await LoginAsync(b.OwnerLogin, Roles.Owner, b.Org);
        var ownerG = await LoginAsync(g.OwnerLogin, Roles.Owner, g.Org);
        Assert.Contains(g.Station.ToString(), await ownerB.GetStringAsync("/api/v1/ui/meta"));
        Assert.DoesNotContain(g.Station.ToString(), await ownerG.GetStringAsync("/api/v1/ui/meta"));
        Assert.Equal(1, await fx.QueryAsync<long>("SELECT count(*) FROM fuel_sales WHERE station_id = @s", new { s = g.Station }));
        Assert.Equal(b.Org, await fx.QueryAsync<Guid>("SELECT organization_id FROM item_costs WHERE station_id = @s", new { s = g.Station }));
        Assert.Equal(g.Org, await fx.QueryAsync<Guid>("SELECT organization_id FROM item_costs WHERE station_id IS NULL AND item_key = 'name:кофе Гамма'"));

        // back to its organization: the other tests expect B to own only its own station
        (await super.PostAsJsonAsync($"/api/v1/ui/stations/{g.Station}/organization", new { organization_id = g.Org })).EnsureSuccessStatusCode();
    }
    [PostgresFact]
    public async Task A_user_moved_to_another_organization_takes_the_own_telegram_chat_along()
    {
        var (a, b, super) = await SetupAsync();
        await LoginAsync("iso-mover", Roles.Manager, a.Org, a.Station);
        var uid = await fx.QueryAsync<Guid>("SELECT id FROM users WHERE login = 'iso-mover'");
        await fx.QueryAsync<long>("""
            INSERT INTO notification_channels (user_id, organization_id, target, title, min_severity) VALUES (@uid, @org, '-777001', 'Личный: iso-mover', 'info')
            ON CONFLICT (user_id) DO UPDATE SET organization_id = EXCLUDED.organization_id RETURNING id
            """, new { uid, org = a.Org });

        await super.PostAsJsonAsync("/api/v1/ui/session/organization", new { id = b.Org });
        (await super.PatchAsJsonAsync($"/api/v1/ui/users/{uid}", new { organization_id = b.Org, stations = new[] { b.Station } })).EnsureSuccessStatusCode();

        // The old organization no longer sees (or can delete) the chat; the user manages it in the new one.
        var ownerA = await LoginAsync(a.OwnerLogin, Roles.Owner, a.Org);
        Assert.DoesNotContain("-777001", await ownerA.GetStringAsync("/api/v1/ui/notifications"));
        Assert.Contains("-777001", await (await LoginAsync(b.OwnerLogin, Roles.Owner, b.Org)).GetStringAsync("/api/v1/ui/notifications"));
        var mover = await LoginAsync("iso-mover", Roles.Manager, b.Org);
        var me = (await mover.GetFromJsonAsync<JsonObject>("/api/v1/ui/telegram/me"))!;
        Assert.NotNull(me["channel"]);
        Assert.Equal(HttpStatusCode.NoContent, (await mover.PutAsJsonAsync("/api/v1/ui/telegram/me", new { min_severity = "critical", daily_digest = false, enabled = true })).StatusCode);
        Assert.Equal(b.Org, await fx.QueryAsync<Guid>("SELECT organization_id FROM notification_channels WHERE user_id = @uid", new { uid }));
    }

    [PostgresFact]
    public async Task Run_now_counts_only_the_callers_new_events()
    {
        var (a, b, _) = await SetupAsync();
        var owner = await LoginAsync(a.OwnerLogin, Roles.Owner, a.Org);
        var before = await fx.QueryAsync<long>("SELECT COALESCE(max(id), 0) FROM alerts");
        // B's station falls silent: the check finds an event there that A must not even count.
        var lastSeen = await fx.QueryAsync<DateTime?>("SELECT last_seen_at FROM stations WHERE id = @s", new { s = b.Station });
        await fx.QueryAsync<int>("UPDATE stations SET last_seen_at = now() - interval '2 days' - random() * interval '1 hour' WHERE id = @s RETURNING 1", new { s = b.Station });
        try
        {
            var found = (await (await owner.PostAsync("/api/v1/ui/alerts/run", null)).Content.ReadFromJsonAsync<JsonObject>())!["found"]!.GetValue<long>();
            Assert.True(await fx.QueryAsync<long>("SELECT count(*) FROM alerts WHERE id > @before AND station_id = @s", new { before, s = b.Station }) >= 1);
            Assert.Equal(await fx.QueryAsync<long>("SELECT count(*) FROM alerts a JOIN stations s ON s.id = a.station_id WHERE a.id > @before AND s.organization_id = @org", new { before, org = a.Org }), found);
        }
        finally
        {
            await fx.QueryAsync<int>("UPDATE stations SET last_seen_at = @lastSeen WHERE id = @s RETURNING 1", new { lastSeen, s = b.Station });
            await fx.QueryAsync<int>("WITH d AS (UPDATE alerts SET status = 'closed' WHERE station_id = @s AND rule_code = 'station_offline' RETURNING 1) SELECT count(*)::int FROM d", new { s = b.Station });
        }
    }
}
