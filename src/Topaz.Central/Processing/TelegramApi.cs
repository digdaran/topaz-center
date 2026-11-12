using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Topaz.Central.Processing;

/// <summary>Result of sendMessage: Gone = the bot was blocked or removed from the chat; MigratedTo = the group became a supergroup.</summary>
public sealed record TelegramSend(bool Ok, bool Gone = false, string? MigratedTo = null);

/// <summary>The Telegram Bot API calls the service makes. The token is part of the URL: never log URLs.</summary>
public sealed class TelegramApi(IHttpClientFactory http, IOptions<CentralOptions> options, ILogger<TelegramApi> log)
{
    private string? _username;

    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.TelegramBotToken);

    /// <summary>Calls a method; longPoll uses the client with a timeout longer than getUpdates' wait.</summary>
    public async Task<(int Status, JsonNode? Json)> CallAsync(string method, Dictionary<string, object?> payload, CancellationToken ct, bool longPoll = false)
    {
        var client = http.CreateClient(longPoll ? "telegram-poll" : "telegram");
        var url = $"{options.Value.TelegramApiBase.TrimEnd('/')}/bot{options.Value.TelegramBotToken}/{method}";
        using var res = await client.PostAsJsonAsync(url, payload, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(body); } catch (JsonException) { }
        return ((int)res.StatusCode, json);
    }

    /// <summary>The bot's @username for t.me links (asked once).</summary>
    public async Task<string?> UsernameAsync(CancellationToken ct)
    {
        if (_username is not null || !Configured) return _username;
        try
        {
            var (status, json) = await CallAsync("getMe", new(), ct);
            if (status == 200) _username = json?["result"]?["username"]?.GetValue<string>();
            else log.LogWarning("Telegram getMe failed: {Status}", status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Telegram getMe failed: {Error}", ex.Message);
        }
        return _username;
    }

    public async Task<TelegramSend> SendAsync(string chat, string html, CancellationToken ct)
    {
        if (!Configured) return new(false);
        try
        {
            var (status, json) = await CallAsync("sendMessage", new()
            {
                ["chat_id"] = chat, ["text"] = html, ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true,
            }, ct);
            if (status == 200) return new(true);
            var description = json?["description"]?.GetValue<string>();
            log.LogWarning("Telegram send to {Chat} failed: {Status} {Description}", chat, status, description);
            var migrated = json?["parameters"]?["migrate_to_chat_id"]?.ToString();
            return new(false, Gone: status == 403, MigratedTo: migrated);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Telegram send to {Chat} failed: {Error}", chat, ex.Message);
            return new(false);
        }
    }
}
