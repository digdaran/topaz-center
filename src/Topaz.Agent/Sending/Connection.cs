using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Source;
using Topaz.Agent.State;
using Topaz.Agent.Updating;
using Topaz.Contracts;

namespace Topaz.Agent.Sending;

public enum ConnectionState { Connected, Pending, Rejected, Blocked, Offline, Error }

public sealed record ConnectionView(ConnectionState State, string? Code, string? Detail);

/// <summary>
/// Getting a station token without any shared key: the agent sends a connection request (only the hash of its
/// secret), shows the verification code, and polls until a superadmin approves it on the server.
/// The secret is kept DPAPI-protected in the state DB, like the token. Data keeps being collected meanwhile.
/// </summary>
public sealed class Connection(
    StateStore state, CentralClient client, TokenStore tokens, AgentOptions options, AgentStatus status,
    ITopazSource topaz, DatasetCatalog catalog, TimeProvider clock, ILogger<Connection> log)
{
    private const string IdKey = "registration_id";
    private const string SecretKey = "registration_secret";
    private const string CodeKey = "registration_code";
    private const string NotBeforeKey = "registration_not_before";
    private int _failures;

    public string? PendingCode => state.GetKv(CodeKey);

    /// <summary>One step: returns the token once approved, otherwise null (and when to try again is remembered).</summary>
    public async Task<(string? Token, ConnectionView View, TimeSpan Wait)> StepAsync(CancellationToken ct, bool ignoreSchedule = false)
    {
        var now = clock.GetUtcNow();
        if (tokens.GetToken() is { } existing) return (existing, new ConnectionView(ConnectionState.Connected, null, null), TimeSpan.Zero);
        var notBefore = DateTimeOffset.FromUnixTimeSeconds(state.GetLong(NotBeforeKey));
        if (!ignoreSchedule && now < notBefore) return (null, new ConnectionView(ConnectionState.Pending, PendingCode, null), notBefore - now);

        var id = Guid.TryParse(state.GetKv(IdKey), out var g) ? g : (Guid?)null;
        var secret = TokenStore.Unprotect(state.GetKv(SecretKey));
        if (id is null || secret is null)
        {
            var created = await CreateAsync(ct);
            if (created.View.State != ConnectionState.Pending) return created;
            id = Guid.Parse(state.GetKv(IdKey)!);
            secret = TokenStore.Unprotect(state.GetKv(SecretKey))!;
        }

        var (code, body, error) = await client.ClaimAsync<RegistrationClaimResponse>(new RegistrationClaimRequest { RegistrationId = id.Value, Secret = secret }, ct);
        switch (code)
        {
            case 200 when body is { Token.Length: > 0, StationId: { } stationId }:
                tokens.Save(stationId, body.Token);
                Forget();
                _failures = 0;
                status.LastSendError = null;
                log.LogInformation("Connection approved: station '{Name}' ({Id})", body.StationName, stationId);
                return (body.Token, new ConnectionView(ConnectionState.Connected, null, body.StationName), TimeSpan.Zero);
            case 202:
                _failures = 0;
                return Wait(ConnectionState.Pending, TimeSpan.FromSeconds(Math.Clamp(body?.RetryAfterSeconds ?? 60, 15, 600)), "waiting for approval");
            case 403:
                Forget(); // a new request (new code) after the pause
                var state403 = body?.Status == RegistrationStatus.Blocked ? ConnectionState.Blocked : ConnectionState.Rejected;
                log.LogError("Connection request {Result} by the server administrator", state403 == ConnectionState.Blocked ? "blocked" : "rejected");
                return Wait(state403, TimeSpan.FromSeconds(Math.Max(3600, body?.RetryAfterSeconds ?? 86400)), "the administrator declined the request");
            case 410:
                Forget(); // expired or unknown: start over right away
                return (null, new ConnectionView(ConnectionState.Pending, null, "request expired, sending a new one"), TimeSpan.Zero);
            default:
                return Wait(code is null ? ConnectionState.Offline : ConnectionState.Error, Backoff(), error ?? $"HTTP {code}");
        }
    }

    private async Task<(string? Token, ConnectionView View, TimeSpan Wait)> CreateAsync(CancellationToken ct)
    {
        var secret = RegistrationCode.NewSecret();
        var request = new RegistrationRequest
        {
            StationName = options.Station.Name.Trim(),
            ClaimHash = RegistrationCode.ClaimHash(secret),
            MachineName = Environment.MachineName,
            AgentVersion = AgentInfo.Version,
            Os = RuntimeInformation.OSDescription,
            Rid = UpdateVerifier.CurrentRid,
            PreviousStationId = Guid.TryParse(tokens.StationId, out var prev) ? prev : null,
            Topaz = await TopazIdentityAsync(ct),
        };
        var (code, json, error) = await client.RegisterAsync<System.Text.Json.Nodes.JsonObject>(request, ct);
        var body = code is 200 or 201 && json is not null ? json.Deserialize<RegistrationResponse>(Protocol.Json) : null;
        if (body is not null)
        {
            state.SetKv(IdKey, body.RegistrationId.ToString());
            state.SetKv(SecretKey, TokenStore.Protect(secret));
            state.SetKv(CodeKey, body.VerificationCode);
            if (body.VerificationCode != RegistrationCode.Verification(request.ClaimHash))
                log.LogWarning("Server shows another verification code than this agent: {Server} vs {Agent}", body.VerificationCode, RegistrationCode.Verification(request.ClaimHash));
            status.LastSendError = $"waiting for approval on the server, verification code {body.VerificationCode}";
            log.LogWarning("Connection request sent for station '{Station}'. Verification code: {Code} - give it to the server administrator",
                request.StationName, body.VerificationCode);
            return (null, new ConnectionView(ConnectionState.Pending, body.VerificationCode, "request sent"), TimeSpan.Zero);
        }
        if (code == 403)
            return Wait(ConnectionState.Blocked, TimeSpan.FromSeconds(Math.Max(3600, json?["retry_after_seconds"]?.GetValue<int>() ?? 86400)),
                "requests from this address are blocked");
        return Wait(code is null ? ConnectionState.Offline : ConnectionState.Error, Backoff(), error ?? $"HTTP {code}");
    }

    /// <summary>Recognisable facts for the administrator, read with the agent's usual read-only queries.</summary>
    internal async Task<TopazInfo?> TopazIdentityAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await using var db = await topaz.OpenAsync(cts.Token);
            var info = new TopazInfo { ServerVersion = await db.GetServerVersionAsync(cts.Token) };
            var window = await db.GetSessionWindowAsync(0, 0, 0, cts.Token);
            info.LastSessionId = window.LatestSessionId;
            if (window.LatestSessionId is { } last && catalog.Datasets.FirstOrDefault(d => d.Table == "sysSessions") is { } sessions)
            {
                var row = (await db.ReadSessionsAsync(sessions, [last], cts.Token).ToListAsync(cts.Token)).FirstOrDefault()?.Data;
                info.LastSessionNum = long.TryParse(row?["SessionNum"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) ? num : null;
                info.LastSessionOperator = row?["UserName"]?.ToString();
                info.LastSessionStart = DateTimeOffset.TryParse(row?["StartDateTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
            }
            if (catalog.Datasets.FirstOrDefault(d => d.Table == "dcFuels") is { } fuels)
                info.Fuels = (await db.ReadSnapshotAsync(fuels, cts.Token).ToListAsync(cts.Token))
                    .Where(r => r.Data["Deleted"]?.ToString() is null or "0" && r.Data["Name"]?.ToString() is { Length: > 0 } n && !n.StartsWith('<'))
                    .Select(r => r.Data["Name"]!.ToString()).Order(StringComparer.Ordinal).Take(20).ToList();
            return info;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Topaz facts for the connection request not read: {Error}", ex.GetBaseException().Message);
            return null;
        }
    }

    private (string?, ConnectionView, TimeSpan) Wait(ConnectionState s, TimeSpan wait, string detail)
    {
        state.SetKv(NotBeforeKey, (clock.GetUtcNow() + wait).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        if (s is ConnectionState.Offline or ConnectionState.Error) status.LastSendError = "connection request: " + detail;
        return (null, new ConnectionView(s, PendingCode, detail), wait);
    }

    private TimeSpan Backoff() => Sending.Backoff.Next(++_failures, TimeSpan.FromSeconds(options.Server.RetryBaseSeconds), TimeSpan.FromSeconds(Math.Min(900, options.Server.RetryMaxSeconds)));

    private void Forget()
    {
        foreach (var k in new[] { IdKey, SecretKey, CodeKey, NotBeforeKey }) state.SetKv(k, null);
    }
}
