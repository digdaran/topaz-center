using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Topaz.Central.Ui;

namespace Topaz.Central.Processing;

/// <summary>
/// Telegram by link: the web UI makes a one-time code and opens t.me/&lt;bot&gt;?start=&lt;code&gt; (the user's own chat)
/// or ?startgroup=&lt;code&gt; (Telegram asks for a group and adds the bot). Telegram then delivers "/start &lt;code&gt;"
/// to the bot, which this service reads by long polling (getUpdates: no inbound webhook to expose) and turns into
/// a notification channel. It also follows the bot being removed from a chat and groups becoming supergroups.
/// Never logs message texts or the token.
/// </summary>
public sealed class TelegramBot(NpgsqlDataSource db, TelegramApi telegram, IOptions<CentralOptions> options, ILogger<TelegramBot> log) : BackgroundService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    public static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    /// <summary>A new one-time code (43 characters, allowed in a t.me start parameter: A-Z a-z 0-9 _ -).</summary>
    public static string NewCode() => WebAuthn.ToBase64Url(RandomNumberGenerator.GetBytes(32));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!telegram.Configured || !options.Value.TelegramPolling) return;
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }
        var backoff = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Telegram polling failed: {Error}; retry in {Delay}", ex.Message, backoff);
                try { await Task.Delay(backoff, stoppingToken); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
            }
        }
    }

    /// <summary>One getUpdates round: handles the updates and stores the new offset. Returns how many came.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        var offset = await c.QuerySingleAsync<long>("SELECT update_offset FROM telegram_state WHERE id = 1");
        var (status, json) = await telegram.CallAsync("getUpdates", new()
        {
            ["offset"] = offset, ["timeout"] = options.Value.TelegramPollSeconds, ["allowed_updates"] = new[] { "message", "my_chat_member" },
        }, ct, longPoll: true);
        if (status != 200)
        {
            var description = json?["description"]?.GetValue<string>() ?? "";
            // 409: the bot has a webhook (getUpdates does not work then) or another copy of the service polls the same bot.
            if (status == 409 && description.Contains("webhook", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Telegram bot had a webhook; removing it so the service can read /start commands");
                await telegram.CallAsync("deleteWebhook", new(), ct);
            }
            throw new InvalidOperationException($"getUpdates answered {status} {description}");
        }
        var updates = json?["result"]?.AsArray() ?? [];
        foreach (var u in updates)
        {
            try { await HandleAsync(c, u!, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Telegram update {Id} failed", u?["update_id"]); }
        }
        if (updates.Count > 0)
            await c.ExecuteAsync("UPDATE telegram_state SET update_offset = @o WHERE id = 1",
                new { o = updates.Max(u => u!["update_id"]!.GetValue<long>()) + 1 });
        return updates.Count;
    }

    private async Task HandleAsync(NpgsqlConnection c, JsonNode u, CancellationToken ct)
    {
        if (u["my_chat_member"] is { } member)
        {
            // Blocked in a private chat or removed from a group: stop sending there (a new link switches it back on).
            var state = member["new_chat_member"]?["status"]?.GetValue<string>();
            if (state is "kicked" or "left")
            {
                var n = await c.ExecuteAsync("UPDATE notification_channels SET enabled = false WHERE target = @t AND enabled", new { t = ChatId(member) });
                if (n > 0) log.LogInformation("Telegram: the bot left a chat, {Count} channel(s) switched off", n);
            }
            return;
        }
        if (u["message"] is not { } m) return;
        var chat = ChatId(m);
        var chatType = m["chat"]?["type"]?.GetValue<string>();
        if (m["migrate_to_chat_id"] is { } to)
        {
            await c.ExecuteAsync("UPDATE notification_channels SET target = @to WHERE target = @chat", new { to = to.ToString(), chat });
            return;
        }
        var text = m["text"]?.GetValue<string>()?.Trim();
        if (text is null || !text.StartsWith('/')) return;
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var head = parts[0].Split('@', 2);
        var command = head[0].ToLowerInvariant();
        // "/start@other_bot" in a group with several bots is not ours.
        if (head.Length > 1 && !string.Equals(head[1], await telegram.UsernameAsync(ct), StringComparison.OrdinalIgnoreCase)) return;
        var arg = parts.Length > 1 ? parts[1] : null;
        var isPrivate = chatType == "private";

        if (command == "/start" && arg is not null) await LinkAsync(c, chat, isPrivate ? "personal" : "group", m["chat"]?["title"]?.GetValue<string>(), arg, ct);
        else if (command == "/start" && isPrivate)
            await telegram.SendAsync(chat, "Это бот уведомлений Topaz Center. Чтобы получать уведомления, откройте в Топаз Центре " +
                                           "<b>Настройки → Уведомления в Telegram</b> и нажмите «Подключить Telegram».", ct);
        else if (command == "/stop" && isPrivate)
        {
            var n = await c.ExecuteAsync("DELETE FROM notification_channels WHERE target = @chat AND user_id IS NOT NULL", new { chat });
            await telegram.SendAsync(chat, n > 0 ? "Уведомления Topaz Center отключены. Подключить снова можно в Топаз Центре." : "Этот чат не подключён к Topaz Center.", ct);
        }
    }

    private static string ChatId(JsonNode n) => n["chat"]!["id"]!.ToString();

    private sealed record LinkCode(string Kind, Guid UserId, Guid? OrganizationId, string MinSeverity, bool DailyDigest, string UserName, string Login);

    /// <summary>Uses a link code (once, unexpired, of the kind that matches the chat) and creates or updates the channel.</summary>
    private async Task LinkAsync(NpgsqlConnection c, string chat, string kind, string? groupTitle, string code, CancellationToken ct)
    {
        var link = await c.QuerySingleOrDefaultAsync<LinkCode>("""
            UPDATE telegram_link_codes k SET used_at = now() FROM users u
            WHERE k.code_hash = @hash AND k.kind = @kind AND k.used_at IS NULL AND k.expires_at > now() AND u.id = k.user_id AND u.is_active
            RETURNING k.kind, k.user_id AS UserId, k.organization_id AS OrganizationId, k.min_severity AS MinSeverity, k.daily_digest AS DailyDigest,
                      u.name AS UserName, u.login
            """, new { hash = HashCode(code), kind });
        if (link is null)
        {
            await telegram.SendAsync(chat, "Ссылка не подошла: она одноразовая и действует 15 минут" +
                                           (kind == "group" ? ", а эта, возможно, для личного чата" : ", а эта, возможно, для группы") +
                                           ". Создайте новую в Топаз Центре.", ct);
            return;
        }
        string reply;
        if (kind == "personal")
        {
            await c.ExecuteAsync("""
                INSERT INTO notification_channels (user_id, organization_id, target, title, min_severity, daily_digest)
                VALUES (@UserId, @OrganizationId, @chat, @title, @MinSeverity, @DailyDigest)
                ON CONFLICT (user_id) DO UPDATE SET target = EXCLUDED.target, title = EXCLUDED.title, enabled = true,
                    organization_id = EXCLUDED.organization_id, min_severity = EXCLUDED.min_severity, daily_digest = EXCLUDED.daily_digest
                """, new { link.UserId, link.OrganizationId, chat, title = "Личный: " + link.UserName, link.MinSeverity, link.DailyDigest });
            reply = $"✅ Готово, {System.Net.WebUtility.HtmlEncode(link.UserName)}: уведомления Topaz Center по вашим АЗС будут приходить сюда.\nОтключить: команда /stop или кнопка в настройках Топаз Центра.";
        }
        else
        {
            await c.ExecuteAsync("""
                INSERT INTO notification_channels (organization_id, target, title, min_severity, daily_digest)
                VALUES (@OrganizationId, @chat, @title, @MinSeverity, @DailyDigest)
                ON CONFLICT (organization_id, target) WHERE user_id IS NULL
                DO UPDATE SET title = EXCLUDED.title, enabled = true, min_severity = EXCLUDED.min_severity, daily_digest = EXCLUDED.daily_digest
                """, new { link.OrganizationId, chat, title = groupTitle ?? "Группа", link.MinSeverity, link.DailyDigest });
            reply = "✅ Готово: уведомления Topaz Center будут приходить в эту группу.";
        }
        await Audit.WriteAsync(db, link.UserId, link.Login, "telegram_linked", "telegram:" + kind, new { kind }, link.OrganizationId);
        await telegram.SendAsync(chat, reply, ct);
    }
}
