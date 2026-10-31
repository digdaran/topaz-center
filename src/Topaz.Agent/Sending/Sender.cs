using Microsoft.Extensions.Logging;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.State;
using Topaz.Contracts;

namespace Topaz.Agent.Sending;

public static class Backoff
{
    /// <summary>Exponential backoff with "full jitter": random(0, min(max, base * 2^attempt)), at least 1s.</summary>
    public static TimeSpan Next(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, Random? random = null)
    {
        var exp = Math.Min(maxDelay.TotalSeconds, baseDelay.TotalSeconds * Math.Pow(2, Math.Min(attempt, 30)));
        var jittered = (random ?? Random.Shared).NextDouble() * exp;
        return TimeSpan.FromSeconds(Math.Max(1, jittered));
    }
}

public enum SendStep { Idle, Sent, Waiting, Failed }

/// <summary>
/// Sends queued batches strictly in order, one at a time. The head batch is retried with backoff
/// until the server acknowledges it; nothing behind it is sent first, so the server always applies
/// row states in the order they were read.
/// </summary>
public sealed class Sender(
    StateStore state,
    CentralClient client,
    TokenStore tokens,
    AgentOptions options,
    AgentStatus status,
    TimeProvider clock,
    ILogger<Sender> log)
{
    private DateTimeOffset _enrollNotBefore = DateTimeOffset.MinValue;
    private int _enrollAttempts;

    /// <summary>Performs at most one network operation. Returns how long the caller may sleep.</summary>
    public async Task<(SendStep Step, TimeSpan Delay)> StepAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var token = tokens.GetToken();
        if (token is null)
        {
            if (now < _enrollNotBefore) return (SendStep.Waiting, _enrollNotBefore - now);
            token = await EnrollAsync(ct);
            if (token is null) return (SendStep.Waiting, _enrollNotBefore - clock.GetUtcNow());
        }

        var batch = state.PeekPending();
        if (batch is null) return (SendStep.Idle, TimeSpan.FromSeconds(30));
        if (batch.NextAttemptAt > now) return (SendStep.Waiting, batch.NextAttemptAt - now);

        var result = await client.SendBatchAsync(token, batch, ct);
        switch (result.Outcome)
        {
            case SendOutcome.Acked:
                state.Ack(batch.Seq);
                status.LastSendAt = clock.GetUtcNow();
                status.LastSendError = null;
                log.LogDebug("Batch {Seq} ({Items} items) acknowledged", batch.Seq, batch.ItemCount);
                return (SendStep.Sent, TimeSpan.Zero);

            case SendOutcome.Unauthorized:
                // Token revoked or station reset by the administrator: obtain a new token, keep the queue.
                log.LogWarning("Server rejected the station token ({Error}); re-enrolling", result.Error);
                tokens.Clear();
                state.Defer(batch.Seq, now, result.Error ?? "unauthorized", countAttempt: false);
                status.LastSendError = result.Error;
                return (SendStep.Failed, TimeSpan.Zero);

            case SendOutcome.Rejected:
                log.LogError("Batch {Seq} permanently rejected: {Error}. Moved to dead letters; its rows will be re-read.", batch.Seq, result.Error);
                state.MarkDead(batch.Seq, result.Error ?? "rejected");
                status.LastSendError = result.Error;
                return (SendStep.Failed, TimeSpan.Zero);

            default:
                var delay = result.RetryAfter is { } ra && ra > TimeSpan.Zero
                    ? ra
                    : Backoff.Next(batch.Attempts, TimeSpan.FromSeconds(options.Server.RetryBaseSeconds), TimeSpan.FromSeconds(options.Server.RetryMaxSeconds));
                state.Defer(batch.Seq, now + delay, result.Error ?? "retry");
                status.LastSendError = result.Error;
                log.LogWarning("Batch {Seq} not delivered (attempt {Attempt}): {Error}. Next try in {Delay:F0}s",
                    batch.Seq, batch.Attempts + 1, result.Error, delay.TotalSeconds);
                return (SendStep.Failed, delay);
        }
    }

    private async Task<string?> EnrollAsync(CancellationToken ct)
    {
        var key = options.Server.EnrollmentKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            log.LogError("No station token and no enrollment key (TOPAZ_AGENT_Server__EnrollmentKey). Data is collected but not sent.");
            _enrollNotBefore = clock.GetUtcNow().AddMinutes(5);
            return null;
        }
        var (response, result) = await client.EnrollAsync(new EnrollRequest
        {
            StationName = options.Station.Name.Trim(),
            EnrollmentKey = key,
            MachineName = Environment.MachineName,
            AgentVersion = AgentInfo.Version,
        }, ct);

        if (response is not null)
        {
            tokens.Save(response.StationId, response.Token);
            _enrollAttempts = 0;
            log.LogInformation("Enrolled as station '{Name}' ({Id})", response.StationName, response.StationId);
            return response.Token;
        }

        _enrollAttempts++;
        var delay = result.Outcome == SendOutcome.Retry
            ? Backoff.Next(_enrollAttempts, TimeSpan.FromSeconds(options.Server.RetryBaseSeconds), TimeSpan.FromSeconds(options.Server.RetryMaxSeconds))
            : TimeSpan.FromMinutes(15); // wrong key / name taken: needs a human, don't hammer the server
        _enrollNotBefore = clock.GetUtcNow() + delay;
        status.LastSendError = "enroll: " + result.Error;
        log.LogError("Enrollment failed: {Error}. Next try in {Delay:F0}s", result.Error, delay.TotalSeconds);
        return null;
    }
}
