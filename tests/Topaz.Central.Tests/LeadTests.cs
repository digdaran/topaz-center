using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>Requests from the public site (site/): stored as platform data, sent to the superadmins' Telegram only.</summary>
public sealed class LeadTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private static object Lead(string name, string contact = "+7 900 123-45-67", bool consent = true, string? website = null) =>
        new { name, contact, stations = 3, city = "Элиста", comment = "Нужна <маржа> & контроль", consent, website, page = "/?utm_source=test" };

    [PostgresFact]
    public async Task A_request_reaches_the_superadmins_in_telegram_and_nobody_else()
    {
        var telegram = new ControlTests.FakeTelegram();
        await using var app = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting("Topaz:TelegramBotToken", "123:secret");
            b.UseSetting("Topaz:TelegramApiBase", "http://telegram.test");
            b.UseSetting("Topaz:TelegramPolling", "false");
            b.ConfigureTestServices(s => s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => telegram));
        });
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
        {
            var org = Guid.NewGuid();
            await c.ExecuteAsync("INSERT INTO organizations (id, name, name_normalized) VALUES (@org, 'Клиент', 'клиент-lead')", new { org });
            await c.ExecuteAsync("""
                INSERT INTO notification_channels (target, title, min_severity, daily_digest) VALUES ('-100555', 'Платформа', 'warning', false);
                INSERT INTO notification_channels (target, title, min_severity, daily_digest, organization_id) VALUES ('-100666', 'Клиент', 'warning', false, @org);
                """, new { org });
        }

        var site = app.CreateClient();
        site.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.7");
        Assert.Equal(HttpStatusCode.Created, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Иван Петров"))).StatusCode);

        for (var i = 0; i < 200 && !telegram.Sent.Any(s => s.Body.Contains("Иван Петров")); i++) await Task.Delay(50);
        var sent = Assert.Single(telegram.Sent, s => s.Body.Contains("Иван Петров"));
        Assert.StartsWith("-100555", sent.Body); // the platform chat, not the client organization's chat
        Assert.Contains("Заявка с сайта", sent.Body);
        Assert.Contains("+7 900 123-45-67", sent.Body);
        Assert.Contains("&lt;маржа&gt; &amp; контроль", sent.Body); // HTML is escaped
        Assert.DoesNotContain(telegram.Sent, s => s.Body.StartsWith("-100666"));
        Assert.NotNull(await fx.QueryAsync<DateTime?>("SELECT notified_at FROM leads WHERE name = 'Иван Петров'"));
    }

    [PostgresFact]
    public async Task Bots_and_incomplete_requests_are_not_stored()
    {
        var site = fx.Client();
        site.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.8");
        Assert.Equal(HttpStatusCode.Accepted, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Бот", website: "http://spam"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Без согласия", consent: false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Без связи", contact: "звоните"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync("/api/v1/leads", Lead(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Телеграм", contact: "@azs_owner"))).StatusCode);
        Assert.Equal(1L, await fx.QueryAsync<long>("SELECT count(*) FROM leads WHERE ip = '198.51.100.8'"));
    }

    [PostgresFact]
    public async Task One_address_cannot_flood_the_form()
    {
        var site = fx.Client();
        site.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.9");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) codes.Add((await site.PostAsJsonAsync("/api/v1/leads", Lead("Флуд " + i))).StatusCode);
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.Created)); // Topaz:LeadsPerHourPerIp
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    [PostgresFact]
    public async Task The_web_interface_role_cannot_read_prospects()
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SET LOCAL ROLE topaz_ui", transaction: tx);
        var e = await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteScalarAsync<long>("SELECT count(*) FROM leads", transaction: tx));
        Assert.Equal("42501", e.SqlState); // insufficient_privilege
    }

    [PostgresFact]
    public async Task The_superadmin_moves_a_request_through_stages_notes_it_and_deletes_it()
    {
        var site = fx.Client();
        site.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.10");
        Assert.Equal(HttpStatusCode.Created, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Пётр Веб"))).StatusCode);
        var id = await fx.QueryAsync<long>("SELECT id FROM leads WHERE name = 'Пётр Веб'");

        var super = await LoginAsync("lead-super", Roles.Superadmin);
        var row = (await super.GetFromJsonAsync<JsonArray>("/api/v1/ui/leads"))!.Single(l => l!["id"]!.GetValue<long>() == id)!;
        Assert.Equal("+7 900 123-45-67", row["contact"]!.GetValue<string>());
        Assert.Equal("new", row["stage"]!.GetValue<string>());
        var before = (await super.GetFromJsonAsync<JsonObject>("/api/v1/ui/meta"))!["new_leads"]!.GetValue<int>();
        Assert.True(before >= 1);

        (await super.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { stage = "work", note = "перезвонить в пятницу" })).EnsureSuccessStatusCode();
        Assert.Equal(before - 1, (await super.GetFromJsonAsync<JsonObject>("/api/v1/ui/meta"))!["new_leads"]!.GetValue<int>());
        Assert.Equal(("work", "lead-super", "перезвонить в пятницу"),
            await fx.QueryAsync<(string, string, string)>("SELECT stage, stage_by, note FROM leads WHERE id = @id", new { id }));
        // Changing only the note keeps the stage; the audit log does not get the note's text.
        (await super.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { note = "2 АЗС" })).EnsureSuccessStatusCode();
        Assert.Equal("work", await fx.QueryAsync<string>("SELECT stage FROM leads WHERE id = @id", new { id }));
        Assert.DoesNotContain("2 АЗС", await super.GetStringAsync("/api/v1/ui/audit"));

        // A refusal needs a reason; "other" needs words.
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { stage = "refused" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { stage = "refused", refuse_reason = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { stage = "lost" })).StatusCode);
        (await super.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { stage = "refused", refuse_reason = "price" })).EnsureSuccessStatusCode();
        var month = await fx.QueryAsync<string>("SELECT to_char(created_at AT TIME ZONE 'Europe/Moscow', 'YYYY-MM') FROM leads WHERE id = @id", new { id });
        var summary = (await super.GetFromJsonAsync<JsonObject>($"/api/v1/ui/leads/summary?month={month}"))!;
        Assert.True(summary["stages"]!["refused"]!.GetValue<int>() >= 1);
        Assert.True(summary["refuse_reasons"]!["price"]!.GetValue<int>() >= 1);
        // Back to work: the reason is gone.
        (await super.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { stage = "work" })).EnsureSuccessStatusCode();
        Assert.Null(await fx.QueryAsync<string?>("SELECT refuse_reason FROM leads WHERE id = @id", new { id }));

        // Prospects are platform data: an owner neither reads them, changes them nor sees their count.
        var owner = await LoginAsync("lead-owner", Roles.Owner, await fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at LIMIT 1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/ui/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/v1/ui/leads/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PatchAsJsonAsync($"/api/v1/ui/leads/{id}", new { stage = "work" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/v1/ui/leads/{id}")).StatusCode);
        Assert.Equal(0, (await owner.GetFromJsonAsync<JsonObject>("/api/v1/ui/meta"))!["new_leads"]!.GetValue<int>());

        // Deleting erases the person's data; the log keeps only the number.
        (await super.DeleteAsync($"/api/v1/ui/leads/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(0L, await fx.QueryAsync<long>("SELECT count(*) FROM leads WHERE id = @id", new { id }));
        Assert.Equal(1L, await fx.QueryAsync<long>("SELECT count(*) FROM audit_log WHERE action = 'lead_deleted' AND entity = @e AND details IS NULL", new { e = "lead:" + id }));
        Assert.Equal(HttpStatusCode.NotFound, (await super.DeleteAsync($"/api/v1/ui/leads/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.PatchAsJsonAsync("/api/v1/ui/leads/999999999", new { stage = "work" })).StatusCode);
    }

    [PostgresFact]
    public async Task A_request_waiting_a_day_is_reminded_once_and_old_refusals_are_erased()
    {
        var telegram = new ControlTests.FakeTelegram();
        await using var app = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting("Topaz:TelegramBotToken", "123:secret");
            b.UseSetting("Topaz:TelegramApiBase", "http://telegram.test");
            b.UseSetting("Topaz:TelegramPolling", "false");
            b.ConfigureTestServices(s => s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => telegram));
        });
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
        {
            await c.ExecuteAsync("""
                INSERT INTO notification_channels (target, title, min_severity, daily_digest)
                SELECT '-100777', 'Платформа 2', 'warning', false WHERE NOT EXISTS (SELECT 1 FROM notification_channels WHERE target = '-100777');
                INSERT INTO leads (created_at, name, contact, consent_at, notified_at) VALUES (now() - interval '25 hours', 'Забытый', '+7 900 000-00-01', now(), now());
                INSERT INTO leads (created_at, name, contact, consent_at, notified_at) VALUES (now() - interval '2 hours', 'Свежий', '+7 900 000-00-02', now(), now());
                INSERT INTO leads (created_at, name, contact, consent_at, notified_at, stage) VALUES (now() - interval '25 hours', 'Позвонили', '+7 900 000-00-03', now(), now(), 'work');
                INSERT INTO leads (created_at, name, contact, consent_at, notified_at, stage, stage_at, refuse_reason)
                    VALUES (now() - interval '400 days', 'Старый отказ', '+7 900 000-00-04', now(), now(), 'refused', now() - interval '370 days', 'price');
                """);
        }
        var notifier = app.Services.GetRequiredService<LeadNotifier>();
        notifier.Wake();
        for (var i = 0; i < 200 && await fx.QueryAsync<DateTime?>("SELECT reminded_at FROM leads WHERE name = 'Забытый'") is null; i++) await Task.Delay(50);
        await notifier.RoundAsync(CancellationToken.None); // a second round sends no second reminder

        Assert.Single(telegram.Sent, s => s.Body.StartsWith("-100777\n") && s.Body.Contains("Забытый") && s.Body.Contains("ждёт звонка"));
        Assert.DoesNotContain(telegram.Sent, s => s.Body.Contains("Свежий") || s.Body.Contains("Позвонили"));
        Assert.Equal(0L, await fx.QueryAsync<long>("SELECT count(*) FROM leads WHERE name = 'Старый отказ'"));
    }


    private async Task SendBatchAsync(string token)
    {
        var data = new JsonObject { ["Id"] = 1 };
        var batch = new Topaz.Contracts.BatchEnvelope
        {
            BatchId = Guid.NewGuid(), Seq = 1, CreatedAt = DateTimeOffset.UtcNow, AgentVersion = "t",
            Items = [new Topaz.Contracts.RecordChange { Dataset = "lead_test", Key = "1", Hash = new string('a', 64), Data = data }],
        };
        (await fx.Client(token).PostAsJsonAsync("/api/v1/batches", batch, Topaz.Contracts.Protocol.Json)).EnsureSuccessStatusCode();
        await fx.WaitProcessedAsync();
    }

    [PostgresFact]
    public async Task A_request_becomes_a_client_who_changes_the_password_connects_a_station_and_gets_a_trial()
    {
        var telegram = new ControlTests.FakeTelegram();
        await using var app = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting("Topaz:TelegramBotToken", "123:secret");
            b.UseSetting("Topaz:TelegramApiBase", "http://telegram.test");
            b.UseSetting("Topaz:TelegramPolling", "false");
            b.UseSetting("Topaz:PublicOrigin", "https://hub.example");
            b.ConfigureTestServices(s => s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => telegram));
        });
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
            await c.ExecuteAsync("""
                INSERT INTO notification_channels (target, title, min_severity, daily_digest)
                SELECT '-100888', 'Платформа 3', 'warning', false WHERE NOT EXISTS (SELECT 1 FROM notification_channels WHERE target = '-100888')
                """);
        var site = fx.Client();
        site.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.11");
        Assert.Equal(HttpStatusCode.Created, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Сергей Клиент"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await site.PostAsJsonAsync("/api/v1/leads", Lead("Сергей Ещё АЗС"))).StatusCode);
        var id = await fx.QueryAsync<long>("SELECT id FROM leads WHERE name = 'Сергей Клиент'");
        var second = await fx.QueryAsync<long>("SELECT id FROM leads WHERE name = 'Сергей Ещё АЗС'");

        var super = await LoginAsync("lead-super", Roles.Superadmin, client: app.CreateClient());
        var owner = await LoginAsync("lead-owner2", Roles.Owner, await fx.QueryAsync<Guid>("SELECT id FROM organizations ORDER BY created_at LIMIT 1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v1/ui/leads/{id}/client", new { mode = "new" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PostAsJsonAsync($"/api/v1/ui/leads/{id}/client", new { mode = "new", organization_name = "Сеть Сергея", owner_login = "a b" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await super.PostAsJsonAsync($"/api/v1/ui/leads/{id}/client", new { mode = "new", organization_name = "Сеть Сергея", owner_login = "lead-super" })).StatusCode);
        Assert.Equal(0L, await fx.QueryAsync<long>("SELECT count(*) FROM organizations WHERE name = 'Сеть Сергея'")); // nothing half-made

        var created = await super.PostAsJsonAsync($"/api/v1/ui/leads/{id}/client", new { mode = "new", organization_name = "Сеть Сергея", owner_name = "Сергей", owner_login = "sergey.client" });
        created.EnsureSuccessStatusCode();
        var r = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        var org = r["organization_id"]!.GetValue<Guid>();
        var password = r["password"]!.GetValue<string>();
        Assert.Equal(12, password.Length);
        Assert.Equal("https://hub.example", r["service_url"]!.GetValue<string>());
        Assert.Equal(("work", org), await fx.QueryAsync<(string, Guid)>("SELECT stage, organization_id FROM leads WHERE id = @id", new { id }));
        Assert.Equal(HttpStatusCode.Conflict, (await super.PostAsJsonAsync($"/api/v1/ui/leads/{id}/client", new { mode = "new", organization_name = "Ещё", owner_login = "sergey2" })).StatusCode);
        Assert.DoesNotContain(password, await super.GetStringAsync("/api/v1/ui/audit")); // the password is shown once, never stored in clear
        var orgs = (await super.GetFromJsonAsync<JsonArray>("/api/v1/ui/organizations"))!;
        Assert.Equal(id, orgs.Single(o => o!["id"]!.GetValue<Guid>() == org)!["lead_id"]!.GetValue<long>());
        var meOrgs = (await super.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["organizations"]!.AsArray();
        Assert.Equal(id, meOrgs.Single(o => o!["id"]!.GetValue<Guid>() == org)!["lead_id"]!.GetValue<long>());

        // The owner signs in with the temporary password: only the password change works until it is replaced.
        var client = fx.Factory!.CreateClient();
        client.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await client.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "sergey.client", password })).EnsureSuccessStatusCode();
        Assert.True((await client.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["password_change_required"]!.GetValue<bool>());
        var refused = await client.GetAsync("/api/v1/ui/meta");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("password_change_required", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/ui/auth/password", new { current_password = password, new_password = password })).StatusCode);
        (await client.PostAsJsonAsync("/api/v1/ui/auth/password", new { current_password = password, new_password = "sergey-own-password" })).EnsureSuccessStatusCode();
        Assert.False((await client.GetFromJsonAsync<JsonObject>("/api/v1/ui/auth/me"))!["password_change_required"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/ui/meta")).StatusCode);

        // An existing client attached to a request: no owner, no trial, not "connected" by its old stations.
        var old = await fx.EnrollAsync("АЗС-старый-клиент", org);
        await SendBatchAsync(old.Token);
        var attached = await super.PostAsJsonAsync($"/api/v1/ui/leads/{second}/client", new { mode = "existing", organization_id = org });
        attached.EnsureSuccessStatusCode();
        Assert.Null((await attached.Content.ReadFromJsonAsync<JsonObject>())!["password"]);

        // The first station sends data: the request is connected, the trial runs 30 days from that day, Telegram hears it once.
        var notifier = app.Services.GetRequiredService<LeadNotifier>();
        await notifier.RoundAsync(CancellationToken.None);
        Assert.Equal("connected", await fx.QueryAsync<string>("SELECT stage FROM leads WHERE id = @id", new { id })); // old station counts: created after the client
        Assert.Equal("work", await fx.QueryAsync<string>("SELECT stage FROM leads WHERE id = @second", new { second }));
        var today = DateTime.UtcNow.AddHours(3).Date;
        Assert.Equal(today.AddDays(30), await fx.QueryAsync<DateTime>("SELECT trial_until FROM organizations WHERE id = @org", new { org }));
        await notifier.RoundAsync(CancellationToken.None);
        Assert.Single(telegram.Sent, m => m.Body.StartsWith("-100888\n") && m.Body.Contains($"Клиент из заявки №{id} подключил первую АЗС"));

        var next = await fx.EnrollAsync("АЗС-вторая-клиента", org);
        await SendBatchAsync(next.Token);
        await notifier.RoundAsync(CancellationToken.None);
        Assert.Equal("connected", await fx.QueryAsync<string>("SELECT stage FROM leads WHERE id = @second", new { second }));
        Assert.Equal(today.AddDays(30), await fx.QueryAsync<DateTime>("SELECT trial_until FROM organizations WHERE id = @org", new { org })); // unchanged

        // The trial ends soon: one reminder; a new date asks again; "" removes the trial.
        (await super.PatchAsJsonAsync($"/api/v1/ui/organizations/{org}", new { trial_until = today.AddDays(2).ToString("yyyy-MM-dd") })).EnsureSuccessStatusCode();
        await notifier.RoundAsync(CancellationToken.None);
        await notifier.RoundAsync(CancellationToken.None);
        Assert.Single(telegram.Sent, m => m.Body.StartsWith("-100888\n") && m.Body.Contains("Пробный период «Сеть Сергея»"));
        (await super.PatchAsJsonAsync($"/api/v1/ui/organizations/{org}", new { trial_until = today.AddDays(1).ToString("yyyy-MM-dd") })).EnsureSuccessStatusCode();
        await notifier.RoundAsync(CancellationToken.None);
        Assert.Equal(2, telegram.Sent.Count(m => m.Body.StartsWith("-100888\n") && m.Body.Contains("Пробный период «Сеть Сергея»")));
        (await super.PatchAsJsonAsync($"/api/v1/ui/organizations/{org}", new { trial_until = "" })).EnsureSuccessStatusCode();
        Assert.Null(await fx.QueryAsync<DateTime?>("SELECT trial_until FROM organizations WHERE id = @org", new { org }));
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PatchAsJsonAsync($"/api/v1/ui/organizations/{org}", new { trial_until = "31.12.2026" })).StatusCode);

        // Deleting the request keeps the client.
        (await super.DeleteAsync($"/api/v1/ui/leads/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(1L, await fx.QueryAsync<long>("SELECT count(*) FROM organizations WHERE id = @org", new { org }));
    }

    private async Task<HttpClient> LoginAsync(string login, string role, Guid? org = null, HttpClient? client = null)
    {
        const string password = "lead-password-1";
        var users = fx.Factory!.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync(login, login, role, password, [], org); } catch (PostgresException) { /* exists */ }
        var c = client ?? fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password })).EnsureSuccessStatusCode();
        return c;
    }
}
