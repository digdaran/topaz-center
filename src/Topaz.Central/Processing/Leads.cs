using System.Net;
using System.Text;
using Dapper;
using Npgsql;

namespace Topaz.Central.Processing;

/// <summary>A request left on the public site (site/, azshub.ru): who to call back about a trial.</summary>
public sealed class LeadRequest
{
    public string? Name { get; set; }
    public string? Contact { get; set; }
    public int? Stations { get; set; }
    public string? City { get; set; }
    public string? Comment { get; set; }
    /// <summary>The visitor ticked "I agree to the processing of personal data" (152-ФЗ); required.</summary>
    public bool Consent { get; set; }
    /// <summary>Honeypot: a field hidden from people. Bots fill it; such requests are accepted and dropped.</summary>
    public string? Website { get; set; }
    /// <summary>Which page (and UTM tags) the request came from.</summary>
    public string? Page { get; set; }
}

/// <summary>
/// Stores site requests (table leads, platform data) and tells the superadmins in Telegram (LeadNotifier).
/// Unauthenticated: per-address rate limit (policy "lead") plus a daily cap per address here.
/// </summary>
public sealed class Leads(NpgsqlDataSource db, LeadNotifier notifier, ILogger<Leads> log)
{
    public const int MaxPerIpPerDay = 10;

    public sealed record Outcome(int Status, string? Error = null);

    public async Task<Outcome> CreateAsync(LeadRequest r, string? ip, string? userAgent)
    {
        if (!string.IsNullOrEmpty(r.Website))
        {
            log.LogInformation("Site request from {Ip} dropped: honeypot filled", ip);
            return new(202);
        }
        var name = Clean(r.Name, 100);
        var contact = Clean(r.Contact, 100);
        if (name is null) return new(400, "Укажите имя");
        if (contact is null || contact.Count(char.IsDigit) < 5 && !contact.Contains('@')) return new(400, "Укажите телефон, Telegram или почту");
        if (!r.Consent) return new(400, "Нужно согласие на обработку персональных данных");
        if (r.Stations is < 1 or > 10000) return new(400, "Число АЗС — от 1");

        await using var c = await db.OpenConnectionAsync();
        var today = await c.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM leads WHERE ip = @ip AND created_at > now() - interval '1 day'", new { ip });
        if (today >= MaxPerIpPerDay) return new(429, "Слишком много заявок, попробуйте завтра или напишите нам");

        var id = await c.ExecuteScalarAsync<long>("""
            INSERT INTO leads (name, contact, stations, city, comment, page, ip, user_agent, consent_at)
            VALUES (@name, @contact, @stations, @city, @comment, @page, @ip, @ua, now()) RETURNING id
            """, new
        {
            name, contact, stations = r.Stations, city = Clean(r.City, 100), comment = Clean(r.Comment, 1000),
            page = Clean(r.Page, 500), ip, ua = Clean(userAgent, 300),
        });
        log.LogInformation("Site request {Id} received", id); // no personal data in logs
        notifier.Wake();
        return new(201);
    }

    /// <summary>Trimmed, control characters removed, cut to max; null when empty.</summary>
    internal static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim()) sb.Append(char.IsControl(ch) && ch != '\n' ? ' ' : ch);
        var t = sb.ToString();
        return t.Length > max ? t[..max] : t;
    }
}

/// <summary>
/// Sends new site requests to the superadmins' Telegram: platform chats (no organization, no user) and the own chats
/// of active superadmins. A request is sent until at least one chat accepts it; retried every minute.
/// </summary>
public sealed class LeadNotifier(NpgsqlDataSource db, TelegramApi telegram, ILogger<LeadNotifier> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    public const int MaxAttempts = 1000; // about 16 hours of a Telegram outage
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(3 * 365); // privacy policy: kept up to three years

    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private sealed record Lead(long Id, DateTime CreatedAt, string Name, string Contact, int? Stations, string? City, string? Comment, string? Page);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RoundAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Site request notification round failed");
            }
            try { await _wake.WaitAsync(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RoundAsync(CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await c.ExecuteAsync("DELETE FROM leads WHERE created_at < now() - @keep", new { keep = KeepFor });
        if (!telegram.Configured) return;
        var leads = (await c.QueryAsync<Lead>(
            "SELECT id, created_at, name, contact, stations, city, comment, page FROM leads WHERE notified_at IS NULL AND notify_attempts < @max ORDER BY id LIMIT 20",
            new { max = MaxAttempts })).ToList();
        if (leads.Count == 0) return;
        var chats = (await c.QueryAsync<string>("""
            SELECT ch.target FROM notification_channels ch LEFT JOIN users u ON u.id = ch.user_id
            WHERE ch.enabled AND ((ch.user_id IS NULL AND ch.organization_id IS NULL) OR (u.role = 'superadmin' AND u.is_active))
            """)).Distinct().ToList();
        if (chats.Count == 0) return; // kept until a superadmin links a chat

        foreach (var lead in leads)
        {
            var text = Format(lead);
            var sent = false;
            foreach (var chat in chats)
                sent |= (await telegram.SendAsync(chat, text, ct)).Ok;
            await c.ExecuteAsync(sent
                ? "UPDATE leads SET notified_at = now() WHERE id = @id"
                : "UPDATE leads SET notify_attempts = notify_attempts + 1 WHERE id = @id", new { id = lead.Id });
        }
    }

    private static string Format(Lead l)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder("📝 <b>Заявка с сайта</b>\n");
        sb.Append("Имя: ").Append(H(l.Name)).Append('\n');
        sb.Append("Связь: <code>").Append(H(l.Contact)).Append("</code>\n");
        if (l.Stations is { } n) sb.Append("АЗС: ").Append(n).Append('\n');
        if (l.City is { } city) sb.Append("Город: ").Append(H(city)).Append('\n');
        if (l.Comment is { } comment) sb.Append("Комментарий: <i>").Append(H(comment)).Append("</i>\n");
        if (l.Page is { } page) sb.Append("Страница: ").Append(H(page)).Append('\n');
        return sb.ToString().TrimEnd();
    }
}
