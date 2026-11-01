using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Topaz.Agent.Collection;
using Topaz.Agent.Configuration;
using Topaz.Agent.Sending;
using Topaz.Agent.State;
using Topaz.Agent.Updating;
using Topaz.Contracts;

namespace Topaz.Agent.Hosting;

/// <summary>Reads Topaz on a schedule and queues changes. Pauses (without losing anything) when the queue is full.</summary>
public sealed class CollectorWorker(Collector collector, StateStore state, AgentOptions options, AgentStatus status, ILogger<CollectorWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Collection.IntervalSeconds);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = interval;
            try
            {
                var queue = state.GetQueueStats();
                if (queue.PendingBytes > options.Collection.MaxQueueBytes)
                {
                    log.LogWarning("Send queue is {Bytes} bytes (limit {Limit}); collection paused, data stays in Topaz",
                        queue.PendingBytes, options.Collection.MaxQueueBytes);
                }
                else
                {
                    var started = DateTimeOffset.UtcNow;
                    var result = await collector.RunCycleAsync(stoppingToken);
                    status.LastCollectAt = DateTimeOffset.UtcNow;
                    status.LastCollectError = null;
                    failures = 0;
                    if (result.Batches > 0) status.QueueSignal.Release();
                    log.LogDebug("Cycle done in {Ms} ms: {Datasets} datasets, {Changes} changes, {Batches} batches",
                        (DateTimeOffset.UtcNow - started).TotalMilliseconds, result.DatasetsRead, result.Changes, result.Batches);
                    // Initial load / rescan: continue right away while the backfill is moving.
                    if (result.Changes > 0) delay = TimeSpan.FromSeconds(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                status.TopazDbOk = false;
                status.LastCollectError = ex.GetBaseException().Message;
                delay = Backoff.Next(failures, interval, TimeSpan.FromMinutes(10));
                log.LogError(ex, "Collection cycle failed (#{Failures}); retry in {Delay:F0}s", failures, delay.TotalSeconds);
            }
            await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}

public sealed class SenderWorker(Sender sender, AgentStatus status, ILogger<SenderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                (_, delay) = await sender.StepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Sender step failed");
                delay = TimeSpan.FromSeconds(30);
            }
            if (delay <= TimeSpan.Zero) continue;
            if (delay > TimeSpan.FromMinutes(15)) delay = TimeSpan.FromMinutes(15);
            // Wake up early when the collector queues something new.
            try { await status.QueueSignal.WaitAsync(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public sealed partial class HeartbeatWorker(
    CentralClient client, TokenStore tokens, StateStore state, AgentOptions options, AgentStatus status, UpdateCoordinator updates, ILogger<HeartbeatWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, options.Server.HeartbeatSeconds)));
        do
        {
            try
            {
                var token = tokens.GetToken();
                if (token is null) continue;
                var q = state.GetQueueStats();
                var (result, response) = await client.HeartbeatAsync(token, new HeartbeatRequest
                {
                    AgentVersion = AgentInfo.Version,
                    MachineName = Environment.MachineName,
                    StartedAt = AgentInfo.StartedAt,
                    QueuePending = q.Pending,
                    QueueDead = q.Dead,
                    QueueBytes = q.PendingBytes,
                    OldestPendingAt = q.OldestPendingAt,
                    LastCollectAt = status.LastCollectAt,
                    LastCollectError = status.LastCollectError,
                    LastSendAt = status.LastSendAt,
                    LastSendError = status.LastSendError,
                    TopazDbOk = status.TopazDbOk,
                    TopazDbVersion = status.TopazDbVersion,
                    LastSessionId = status.LastSessionId,
                    LastSessionOpen = status.LastSessionOpen,
                    TimeZone = IanaTimeZone(options.ResolveTimeZone()),
                    Rid = UpdateVerifier.CurrentRid,
                    Update = updates.Report,
                }, stoppingToken);
                if (result.Outcome != SendOutcome.Acked)
                    log.LogDebug("Heartbeat not delivered: {Error}", result.Error);
                else
                {
                    updates.MarkStarted(); // the server answers: an installer waiting for this version may finish
                    updates.Offer(response?.Update);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Heartbeat failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result, TaskScheduler.Default));
    }

    /// <summary>Windows zone ids ("Russian Standard Time") are converted to IANA ("Europe/Moscow").</summary>
    internal static string? IanaTimeZone(TimeZoneInfo tz) =>
        tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : null;
}
