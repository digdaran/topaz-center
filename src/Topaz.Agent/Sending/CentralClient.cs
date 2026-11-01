using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Topaz.Agent.Configuration;
using Topaz.Agent.State;
using Topaz.Contracts;

namespace Topaz.Agent.Sending;

public enum SendOutcome
{
    /// <summary>Stored by the server (new or duplicate).</summary>
    Acked,
    /// <summary>Temporary problem (network, 5xx, 429, timeout): retry later, keep order.</summary>
    Retry,
    /// <summary>Token unknown or revoked: re-enroll.</summary>
    Unauthorized,
    /// <summary>Server will never accept this batch as is (400/409/413/415/422).</summary>
    Rejected,
}

public sealed record SendResult(SendOutcome Outcome, string? Error = null, TimeSpan? RetryAfter = null);

/// <summary>HTTPS client for the central API. Outbound only; the agent never listens on a port.</summary>
public sealed class CentralClient(HttpClient http, AgentOptions options)
{
    public async Task<SendResult> SendBatchAsync(string token, OutboxBatch batch, CancellationToken ct)
    {
        using var content = new ByteArrayContent(batch.Payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("batches")) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(Protocol.IdempotencyKeyHeader, batch.BatchId.ToString());
        request.Headers.Add(Protocol.SeqHeader, batch.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await SendAsync(request, ct);
    }

    /// <summary>Connection request (no credentials). Null status = network problem.</summary>
    public Task<(int? Status, T? Body, string? Error)> RegisterAsync<T>(RegistrationRequest body, CancellationToken ct) where T : class =>
        PostJsonAsync<T>("registrations", body, ct);

    public Task<(int? Status, T? Body, string? Error)> ClaimAsync<T>(RegistrationClaimRequest body, CancellationToken ct) where T : class =>
        PostJsonAsync<T>("registrations/claim", body, ct);

    private async Task<(int? Status, T? Body, string? Error)> PostJsonAsync<T>(string path, object body, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = JsonContent.Create(body, options: Protocol.Json) };
        try
        {
            using var response = await http.SendAsync(request, ct);
            T? parsed = null;
            try { parsed = await response.Content.ReadFromJsonAsync<T>(Protocol.Json, ct); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { /* proxy page or empty body */ }
            return ((int)response.StatusCode, parsed, response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, null, Describe(ex));
        }
    }

    public async Task<(SendResult Result, HeartbeatResponse? Response)> HeartbeatAsync(string token, HeartbeatRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("heartbeat")) { Content = JsonContent.Create(body, options: Protocol.Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (await Classify(response, ct), null);
            HeartbeatResponse? parsed = null;
            try { parsed = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(Protocol.Json, ct); }
            catch (System.Text.Json.JsonException) { /* older server or proxy page: no offer */ }
            return (new SendResult(SendOutcome.Acked), parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (new SendResult(SendOutcome.Retry, Describe(ex)), null);
        }
    }

    private async Task<SendResult> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? new SendResult(SendOutcome.Acked) : await Classify(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new SendResult(SendOutcome.Retry, Describe(ex));
        }
    }

    private static async Task<SendResult> Classify(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await SafeBody(response, ct);
        var error = $"HTTP {(int)response.StatusCode}: {body}";
        var retryAfter = response.Headers.RetryAfter?.Delta
                         ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new SendResult(SendOutcome.Unauthorized, error),
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge
                or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity => new SendResult(SendOutcome.Rejected, error),
            // Everything else (404 behind a misconfigured proxy, 429, 5xx...) is retried: never drop data.
            _ => new SendResult(SendOutcome.Retry, error, retryAfter),
        };
    }

    private static async Task<string> SafeBody(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var s = await response.Content.ReadAsStringAsync(ct);
            return s.Length > 500 ? s[..500] : s;
        }
        catch (Exception) { return ""; }
    }

    private static string Describe(Exception ex) => ex is TaskCanceledException ? "timeout" : ex.GetBaseException().Message;

    private Uri Url(string path) => new(new Uri(options.Server.Url.TrimEnd('/') + "/"), $"api/v1/{path}");
}
