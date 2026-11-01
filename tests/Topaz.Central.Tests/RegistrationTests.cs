using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>Stations connect by request: approval by a superadmin with the verification code, token claim, access control.</summary>
public sealed class RegistrationTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "registr-password-1";

    private async Task<HttpClient> UiAsync(string login, string role)
    {
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, Password, []); } catch (Npgsql.PostgresException) { }
        var c = fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    private HttpClient Agent(string ip)
    {
        var c = fx.Client();
        c.DefaultRequestHeaders.Add("X-Forwarded-For", ip); // as Caddy passes the station address
        return c;
    }

    private static async Task<(Guid Id, string Code, string Secret)> RequestAsync(HttpClient agent, string name, Guid? previous = null)
    {
        var secret = RegistrationCode.NewSecret();
        var r = await agent.PostAsJsonAsync("/api/v1/registrations", new RegistrationRequest
        {
            StationName = name, ClaimHash = RegistrationCode.ClaimHash(secret), MachineName = "TOPAZ-PC", AgentVersion = "1.3.0", Os = "Windows 10",
            PreviousStationId = previous,
            Topaz = new TopazInfo { LastSessionNum = 82, LastSessionOperator = "Иванов", Fuels = ["Аи92", "ДТ"] },
        }, Protocol.Json);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = (await r.Content.ReadFromJsonAsync<RegistrationResponse>(Protocol.Json))!;
        Assert.Equal(RegistrationCode.Verification(RegistrationCode.ClaimHash(secret)), body.VerificationCode); // the agent computes the same code
        return (body.RegistrationId, body.VerificationCode, secret);
    }

    private static async Task<(HttpStatusCode Status, RegistrationClaimResponse? Body)> ClaimAsync(HttpClient agent, Guid id, string secret)
    {
        var r = await agent.PostAsJsonAsync("/api/v1/registrations/claim", new RegistrationClaimRequest { RegistrationId = id, Secret = secret }, Protocol.Json);
        return (r.StatusCode, r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.Forbidden
            ? await r.Content.ReadFromJsonAsync<RegistrationClaimResponse>(Protocol.Json) : null);
    }

    private static Task<HttpResponseMessage> Beat(HttpClient c, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/heartbeat") { Content = JsonContent.Create(new HeartbeatRequest { AgentVersion = "1.3.0" }, options: Protocol.Json) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return c.SendAsync(req);
    }

    [PostgresFact]
    public async Task New_station_waits_for_approval_with_the_code_then_gets_its_token()
    {
        var super = await UiAsync("reg-super", Roles.Superadmin);
        var owner = await UiAsync("reg-owner", Roles.Owner);
        var agent = Agent("203.0.113.10");
        Assert.Equal(HttpStatusCode.Gone, (await agent.PostAsJsonAsync("/api/v1/enroll", new EnrollRequest { StationName = "x", EnrollmentKey = "k" }, Protocol.Json)).StatusCode);

        var (id, code, secret) = await RequestAsync(agent, "АЗС-Новая");
        var (status, body) = await ClaimAsync(agent, id, secret);
        Assert.Equal((HttpStatusCode.Accepted, RegistrationStatus.Pending), (status, body!.Status));
        Assert.Equal(HttpStatusCode.Gone, (await ClaimAsync(agent, id, RegistrationCode.NewSecret())).Status); // someone else's secret

        // Owners see the request (and a counter) but cannot decide; managers do not see it.
        var list = (await owner.GetFromJsonAsync<JsonObject>("/api/v1/ui/registrations"))!;
        var req = list["pending"]!.AsArray().Single(p => p!["id"]!.GetValue<Guid>() == id)!;
        Assert.Equal(("TOPAZ-PC", "203.0.113.10"), (req["machine_name"]!.GetValue<string>(), req["ip"]!.GetValue<string>()));
        Assert.Contains("Иванов", req["topaz_info"]!.GetValue<string>());
        Assert.False(list["can_decide"]!.GetValue<bool>());
        Assert.True((await owner.GetFromJsonAsync<JsonObject>("/api/v1/ui/meta"))!["pending_registrations"]!.GetValue<int>() >= 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v1/ui/registrations/{id}/approve", new { code })).StatusCode);
        var manager = await UiAsync("reg-manager", Roles.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/v1/ui/registrations")).StatusCode);

        // Event in «Контроль» (no station yet), visible to owners.
        await fx.Factory!.Services.GetRequiredService<AnomalyDetector>().RunAsync(CancellationToken.None);
        var alerts = await owner.GetStringAsync("/api/v1/ui/alerts?status=open");
        Assert.Contains("АЗС ожидает подключения: «АЗС-Новая»", alerts);

        // Wrong code is refused (and audited); the right one, typed sloppily, is accepted.
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PostAsJsonAsync($"/api/v1/ui/registrations/{id}/approve", new { code = "AAAA-AAAA" })).StatusCode);
        var approved = await super.PostAsJsonAsync($"/api/v1/ui/registrations/{id}/approve", new { code = code.ToLowerInvariant().Replace("-", " "), station_name = "АЗС №5 Новая" });
        approved.EnsureSuccessStatusCode();
        var stationId = (await approved.Content.ReadFromJsonAsync<JsonObject>())!["station_id"]!.GetValue<Guid>();
        Assert.Contains("station_registration_code_mismatch", await super.GetStringAsync("/api/v1/ui/audit"));

        // A lost response: the agent asks again and gets a new token; only the last one works.
        var (s1, t1) = await ClaimAsync(agent, id, secret);
        var (s2, t2) = await ClaimAsync(agent, id, secret);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (s1, s2));
        Assert.Equal((stationId, "АЗС №5 Новая"), (t2!.StationId!.Value, t2.StationName));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Beat(fx.Client(), t1!.Token!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Beat(fx.Client(), t2.Token!)).StatusCode);
        // Once the token is used the secret retires.
        Assert.Equal(HttpStatusCode.Gone, (await ClaimAsync(agent, id, secret)).Status);

        await fx.Factory.Services.GetRequiredService<AnomalyDetector>().RunAsync(CancellationToken.None);
        Assert.Equal("closed", await fx.QueryAsync<string>("SELECT status FROM alerts WHERE registration_id = @id", new { id }));
    }

    [PostgresFact]
    public async Task Reinstall_reject_block_and_limits()
    {
        var super = await UiAsync("reg-super2", Roles.Superadmin);
        var st = await fx.EnrollAsync("АЗС-Старая");

        // Access re-issued (or a new PC): the agent asks again with its previous id; approved as the same station.
        (await super.PostAsJsonAsync($"/api/v1/ui/stations/{st.StationId}/access", new { action = "reissue" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Beat(fx.Client(), st.Token)).StatusCode);
        var agent = Agent("198.51.100.7");
        var (id, code, secret) = await RequestAsync(agent, "АЗС-Старая", previous: st.StationId);
        var pending = (await super.GetFromJsonAsync<JsonObject>("/api/v1/ui/registrations"))!["pending"]!.AsArray().Single(p => p!["id"]!.GetValue<Guid>() == id)!;
        Assert.Equal("АЗС-Старая", pending["previous_station"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await super.PostAsJsonAsync($"/api/v1/ui/registrations/{id}/approve", new { code })).StatusCode); // name taken: choose existing
        (await super.PostAsJsonAsync($"/api/v1/ui/registrations/{id}/approve", new { code, station_id = st.StationId })).EnsureSuccessStatusCode();
        var (_, claimed) = await ClaimAsync(agent, id, secret);
        Assert.Equal(st.StationId, claimed!.StationId);
        Assert.Equal(HttpStatusCode.OK, (await Beat(fx.Client(), claimed.Token!)).StatusCode);

        // Block / unblock keeps the token.
        (await super.PostAsJsonAsync($"/api/v1/ui/stations/{st.StationId}/access", new { action = "block" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Beat(fx.Client(), claimed.Token!)).StatusCode);
        (await super.PostAsJsonAsync($"/api/v1/ui/stations/{st.StationId}/access", new { action = "unblock" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await Beat(fx.Client(), claimed.Token!)).StatusCode);

        // Reject + block the address: the agent is told to come back much later; new requests from there are refused.
        var spam = Agent("192.0.2.66");
        var (sid, _, ssecret) = await RequestAsync(spam, "АЗС-Фальшивая");
        (await super.PostAsJsonAsync($"/api/v1/ui/registrations/{sid}/reject", new { block_ip = true, note = "не наша" })).EnsureSuccessStatusCode();
        var (rs, rb) = await ClaimAsync(spam, sid, ssecret);
        Assert.Equal((HttpStatusCode.Forbidden, RegistrationStatus.Blocked), (rs, rb!.Status));
        Assert.True(rb.RetryAfterSeconds > 29 * 86400);
        var again = await spam.PostAsJsonAsync("/api/v1/registrations", new RegistrationRequest { StationName = "x", ClaimHash = RegistrationCode.ClaimHash("s") }, Protocol.Json);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        // At most 5 pending requests per address; the same request repeated is not a new one.
        var flood = Agent("192.0.2.99");
        for (var i = 0; i < 5; i++) await RequestAsync(flood, "Флуд " + i);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await flood.PostAsJsonAsync("/api/v1/registrations",
            new RegistrationRequest { StationName = "Флуд 6", ClaimHash = RegistrationCode.ClaimHash("x6") }, Protocol.Json)).StatusCode);

        // Stale requests expire.
        await fx.QueryAsync<int>("WITH u AS (UPDATE station_registrations SET last_poll_at = now() - interval '15 days' WHERE ip = '192.0.2.99' RETURNING 1) SELECT count(*)::int FROM u");
        await fx.Factory!.Services.GetRequiredService<AnomalyDetector>().RunAsync(CancellationToken.None);
        Assert.Equal(0, await fx.QueryAsync<int>("SELECT count(*)::int FROM station_registrations WHERE ip = '192.0.2.99' AND status = 'pending'"));
    }

    [PostgresFact]
    public async Task Only_a_superadmin_manages_users_and_the_last_one_stays()
    {
        var super = await UiAsync("reg-super3", Roles.Superadmin);
        var owner = await UiAsync("reg-owner3", Roles.Owner);
        Assert.Equal("superadmin", (await super.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["role"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await super.GetAsync("/api/v1/ui/analytics/overview")).StatusCode); // owner rights too
        foreach (var path in new[] { "/api/v1/ui/users", "/api/v1/ui/audit", "/api/v1/ui/agent-updates" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/v1/ui/users", new { login = "x1", name = "x", role = "superadmin", password = Password })).StatusCode);

        // Demoting the only other superadmins is fine, but the last active one cannot be demoted or disabled.
        var users = (await super.GetFromJsonAsync<JsonArray>("/api/v1/ui/users"))!;
        foreach (var u in users.Where(u => u!["role"]!.GetValue<string>() == "superadmin" && u["login"]!.GetValue<string>() != "reg-super3"))
            (await super.PatchAsJsonAsync($"/api/v1/ui/users/{u!["id"]}", new { role = "owner" })).EnsureSuccessStatusCode();
        var me = users.Single(u => u!["login"]!.GetValue<string>() == "reg-super3")!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PatchAsJsonAsync($"/api/v1/ui/users/{me}", new { role = "owner" })).StatusCode);
    }

    [PostgresFact]
    public async Task Telegram_hears_about_a_new_request_at_once()
    {
        var telegram = new ControlTests.FakeTelegram();
        await using var app = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting("Topaz:TelegramBotToken", "123:secret");
            b.UseSetting("Topaz:TelegramApiBase", "http://telegram.test");
            b.ConfigureTestServices(s => s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => telegram));
        });
        await using (var c = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("INSERT INTO notification_channels (target, title, min_severity, daily_digest) VALUES ('-100777', 'Дежурные', 'warning', false)", c);
            await cmd.ExecuteNonQueryAsync();
        }
        var agent = app.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.200");
        await RequestAsync(agent, "АЗС-Телеграм");
        for (var i = 0; i < 200 && !telegram.Sent.Any(s => s.Body.Contains("АЗС-Телеграм")); i++) await Task.Delay(50);
        Assert.Contains(telegram.Sent, s => s.Body.Contains("АЗС ожидает подключения") && s.Body.Contains("АЗС-Телеграм") && s.Body.Contains("Новая АЗС"));
    }
}
