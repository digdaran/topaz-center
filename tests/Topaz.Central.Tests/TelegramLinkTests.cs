using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>
/// Telegram by link: a t.me link with a one-time code, "/start code" read by the bot, the channel it creates,
/// and where a user's own chat gets its messages from (only the user's stations, only while the user is active).
/// </summary>
public sealed class TelegramLinkTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const string Password = "telegram-password-1";

    /// <summary>A scripted Bot API: getMe, getUpdates from a queue, sendMessage recorded (403 for chats that blocked the bot).</summary>
    private sealed class FakeBotApi : HttpMessageHandler
    {
        public ConcurrentQueue<JsonObject> Updates { get; } = new();
        public ConcurrentBag<string> Blocked { get; } = new();
        public List<(string Method, JsonNode Body)> Calls { get; } = new();
        public IEnumerable<(string Chat, string Text)> Sent =>
            Calls.Where(c => c.Method == "sendMessage").Select(c => (c.Body["chat_id"]!.ToString(), c.Body["text"]!.GetValue<string>()));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.Segments[^1];
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct)) ?? new JsonObject();
            lock (Calls) Calls.Add((method, body));
            JsonNode result = method switch
            {
                "getMe" => new JsonObject { ["id"] = 1, ["username"] = "topaz_test_bot" },
                "getUpdates" => new JsonArray(Drain().ToArray<JsonNode?>()),
                _ => true,
            };
            if (method == "sendMessage" && Blocked.Contains(body["chat_id"]!.ToString()))
                return Json(HttpStatusCode.Forbidden, new JsonObject { ["ok"] = false, ["error_code"] = 403, ["description"] = "Forbidden: bot was blocked by the user" });
            return Json(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["result"] = result });
        }

        private IEnumerable<JsonObject> Drain() { while (Updates.TryDequeue(out var u)) yield return u; }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonObject o) =>
            new(status) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static long _updateId = 1000;

    private static JsonObject Message(long chat, string type, string text, string? title = null) => new()
    {
        ["update_id"] = Interlocked.Increment(ref _updateId),
        ["message"] = new JsonObject
        {
            ["message_id"] = 1, ["text"] = text, ["from"] = new JsonObject { ["id"] = 42, ["first_name"] = "Иван" },
            ["chat"] = new JsonObject { ["id"] = chat, ["type"] = type, ["title"] = title },
        },
    };

    private (WebApplicationFactory<Program> App, FakeBotApi Api) App()
    {
        var api = new FakeBotApi();
        var app = fx.Factory!.WithWebHostBuilder(b =>
        {
            b.UseSetting("Topaz:TelegramBotToken", "123:secret");
            b.UseSetting("Topaz:TelegramApiBase", "http://telegram.test");
            b.UseSetting("Topaz:TelegramPolling", "false"); // the test reads updates itself
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient("telegram").ConfigurePrimaryHttpMessageHandler(() => api);
                s.AddHttpClient("telegram-poll").ConfigurePrimaryHttpMessageHandler(() => api);
            });
        });
        return (app, api);
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> app, string login, string role, params Guid[] stations)
    {
        try { await app.Services.GetRequiredService<UserService>().CreateAsync(login, "Имя " + login, role, Password, stations); }
        catch (PostgresException) { /* exists */ }
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await c.PostAsJsonAsync("/api/v1/ui/auth/login", new { login, password = Password })).EnsureSuccessStatusCode();
        return c;
    }

    private static string Code(string url) => url[(url.IndexOf('=') + 1)..];

    private async Task AlertAsync(Guid station, string title)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.ExecuteAsync("UPDATE alerts SET notified_at = now() WHERE notified_at IS NULL");
        await c.ExecuteAsync("""
            INSERT INTO alerts (station_id, rule_code, dedupe_key, severity, title, occurred_at) VALUES (@station, 'shift_too_long', @title, 'warning', @title, now())
            """, new { station, title });
    }

    [PostgresFact]
    public async Task A_user_links_own_telegram_by_link_and_hears_only_about_own_stations()
    {
        var (app, api) = App();
        await using var _ = app;
        var bot = app.Services.GetRequiredService<TelegramBot>();
        var detector = app.Services.GetRequiredService<AnomalyDetector>();
        var mine = (await fx.EnrollAsync("TG-моя-АЗС")).StationId;
        var other = (await fx.EnrollAsync("TG-чужая-АЗС")).StationId;
        var manager = await LoginAsync(app, "tg-manager", Roles.Manager, mine);

        var me = (await manager.GetFromJsonAsync<JsonObject>("/api/v1/ui/telegram/me"))!;
        Assert.True(me["bot_configured"]!.GetValue<bool>());
        Assert.Null(me["channel"]);
        var link = (await (await manager.PostAsJsonAsync("/api/v1/ui/telegram/me/link", new { min_severity = "info", daily_digest = false }))
            .Content.ReadFromJsonAsync<JsonObject>())!;
        var url = link["url"]!.GetValue<string>();
        Assert.StartsWith("https://t.me/topaz_test_bot?start=", url);
        var code = Code(url);

        // A personal code does not work in a group (and is not used up by it).
        api.Updates.Enqueue(Message(-100123, "group", "/start@topaz_test_bot " + code, "Чужая группа"));
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.Contains(api.Sent, m => m.Chat == "-100123" && m.Text.Contains("Ссылка не подошла"));
        // A command for another bot in a group is ignored.
        api.Updates.Enqueue(Message(-100123, "group", "/start@other_bot " + code, "Чужая группа"));
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.Single(api.Sent, m => m.Chat == "-100123");

        api.Updates.Enqueue(Message(5001, "private", "/start " + code));
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.Contains(api.Sent, m => m.Chat == "5001" && m.Text.Contains("Готово"));
        // One-time: the same link from another account changes nothing.
        api.Updates.Enqueue(Message(5002, "private", "/start " + code));
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.Contains(api.Sent, m => m.Chat == "5002" && m.Text.Contains("Ссылка не подошла"));
        me = (await manager.GetFromJsonAsync<JsonObject>("/api/v1/ui/telegram/me"))!;
        Assert.Equal("info", me["channel"]!["min_severity"]!.GetValue<string>());
        Assert.Equal("5001", await fx.QueryAsync<string>("SELECT target FROM notification_channels WHERE user_id = (SELECT id FROM users WHERE login = 'tg-manager')"));
        // The offset moves past what was read: a restart does not replay /start.
        var lastPoll = api.Calls.Last(c => c.Method == "getUpdates").Body;
        Assert.Equal(_updateId, await fx.QueryAsync<long>("SELECT update_offset FROM telegram_state") - 1);
        Assert.True(lastPoll["offset"]!.GetValue<long>() > 1000);

        // Only the manager's station reaches the manager's chat.
        await AlertAsync(other, "TG событие чужой АЗС");
        await detector.RunAsync(CancellationToken.None);
        await AlertAsync(mine, "TG событие моей АЗС");
        await detector.RunAsync(CancellationToken.None);
        Assert.Contains(api.Sent, m => m.Chat == "5001" && m.Text.Contains("TG событие моей АЗС"));
        Assert.DoesNotContain(api.Sent, m => m.Chat == "5001" && m.Text.Contains("TG событие чужой АЗС"));

        // A blocked user hears nothing.
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
            await c.ExecuteAsync("UPDATE users SET is_active = false WHERE login = 'tg-manager'");
        await AlertAsync(mine, "TG после блокировки");
        await detector.RunAsync(CancellationToken.None);
        Assert.DoesNotContain(api.Sent, m => m.Text.Contains("TG после блокировки"));
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
            await c.ExecuteAsync("UPDATE users SET is_active = true WHERE login = 'tg-manager'");

        // The user blocked the bot in Telegram: the chat is switched off.
        api.Blocked.Add("5001");
        await AlertAsync(mine, "TG бот заблокирован");
        await detector.RunAsync(CancellationToken.None);
        Assert.False(await fx.QueryAsync<bool>("SELECT enabled FROM notification_channels WHERE target = '5001'"));

        // Settings and unlinking from the profile; /stop in the chat does the same.
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PutAsJsonAsync("/api/v1/ui/telegram/me", new { min_severity = "critical", daily_digest = true, enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync("/api/v1/ui/telegram/me")).StatusCode);
        Assert.Null((await manager.GetFromJsonAsync<JsonObject>("/api/v1/ui/telegram/me"))!["channel"]);
        Assert.Equal(1L, await fx.QueryAsync<long>("SELECT count(*) FROM audit_log WHERE action = 'telegram_linked' AND user_login = 'tg-manager'"));
    }

    [PostgresFact]
    public async Task An_expired_link_and_stop_command()
    {
        var (app, api) = App();
        await using var _ = app;
        var bot = app.Services.GetRequiredService<TelegramBot>();
        var viewer = await LoginAsync(app, "tg-viewer", Roles.Viewer);

        var code = Code((await (await viewer.PostAsJsonAsync("/api/v1/ui/telegram/me/link", new { min_severity = "warning", daily_digest = true }))
            .Content.ReadFromJsonAsync<JsonObject>())!["url"]!.GetValue<string>());
        await using (var c = new NpgsqlConnection(fx.ConnectionString))
            await c.ExecuteAsync("UPDATE telegram_link_codes SET expires_at = now() - interval '1 minute' WHERE code_hash = @h", new { h = TelegramBot.HashCode(code) });
        api.Updates.Enqueue(Message(6001, "private", "/start " + code));
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.Null((await viewer.GetFromJsonAsync<JsonObject>("/api/v1/ui/telegram/me"))!["channel"]);

        code = Code((await (await viewer.PostAsJsonAsync("/api/v1/ui/telegram/me/link", new { min_severity = "warning", daily_digest = true }))
            .Content.ReadFromJsonAsync<JsonObject>())!["url"]!.GetValue<string>());
        api.Updates.Enqueue(Message(6001, "private", "/start " + code));
        api.Updates.Enqueue(Message(6001, "private", "/stop"));
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.Contains(api.Sent, m => m.Chat == "6001" && m.Text.Contains("отключены"));
        Assert.Null((await viewer.GetFromJsonAsync<JsonObject>("/api/v1/ui/telegram/me"))!["channel"]);
    }

    [PostgresFact]
    public async Task An_owner_adds_a_group_by_link_and_the_bot_follows_the_group()
    {
        var (app, api) = App();
        await using var _ = app;
        var bot = app.Services.GetRequiredService<TelegramBot>();
        var owner = await LoginAsync(app, "tg-owner", Roles.Owner);
        var manager = await LoginAsync(app, "tg-manager-2", Roles.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/v1/ui/notifications/link-group", new { min_severity = "warning", daily_digest = true })).StatusCode);

        var url = (await (await owner.PostAsJsonAsync("/api/v1/ui/notifications/link-group", new { min_severity = "critical", daily_digest = true }))
            .Content.ReadFromJsonAsync<JsonObject>())!["url"]!.GetValue<string>();
        Assert.StartsWith("https://t.me/topaz_test_bot?startgroup=", url);
        // A group code does not work in a private chat.
        api.Updates.Enqueue(Message(7001, "private", "/start " + Code(url)));
        api.Updates.Enqueue(Message(-100700, "supergroup", "/start@topaz_test_bot " + Code(url), "Операторы"));
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.Contains(api.Sent, m => m.Chat == "7001" && m.Text.Contains("Ссылка не подошла"));
        Assert.Contains(api.Sent, m => m.Chat == "-100700" && m.Text.Contains("Готово"));
        var list = await owner.GetStringAsync("/api/v1/ui/notifications");
        Assert.Contains("Операторы", list);
        Assert.Equal("critical", await fx.QueryAsync<string>("SELECT min_severity FROM notification_channels WHERE target = '-100700'"));

        // The group becomes a supergroup with a new id; later the bot is removed from it.
        api.Updates.Enqueue(new JsonObject
        {
            ["update_id"] = Interlocked.Increment(ref _updateId),
            ["message"] = new JsonObject { ["message_id"] = 2, ["chat"] = new JsonObject { ["id"] = -100700, ["type"] = "group" }, ["migrate_to_chat_id"] = -100701 },
        });
        api.Updates.Enqueue(new JsonObject
        {
            ["update_id"] = Interlocked.Increment(ref _updateId),
            ["my_chat_member"] = new JsonObject
            {
                ["chat"] = new JsonObject { ["id"] = -100701, ["type"] = "supergroup" },
                ["new_chat_member"] = new JsonObject { ["status"] = "kicked" },
            },
        });
        await bot.PollOnceAsync(CancellationToken.None);
        Assert.False(await fx.QueryAsync<bool>("SELECT enabled FROM notification_channels WHERE target = '-100701'"));

        // Adding the same group again by number does not duplicate it; it switches it back on.
        (await owner.PostAsJsonAsync("/api/v1/ui/notifications", new { target = "-100701", title = "Операторы", min_severity = "warning", daily_digest = false })).EnsureSuccessStatusCode();
        Assert.Equal(1L, await fx.QueryAsync<long>("SELECT count(*) FROM notification_channels WHERE target = '-100701'"));
        Assert.True(await fx.QueryAsync<bool>("SELECT enabled FROM notification_channels WHERE target = '-100701'"));
    }
}
