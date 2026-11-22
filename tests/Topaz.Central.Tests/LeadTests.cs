using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
}
