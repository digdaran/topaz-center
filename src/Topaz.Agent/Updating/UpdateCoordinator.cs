using System.Globalization;
using Topaz.Agent.Hosting;
using Topaz.Agent.State;
using Topaz.Contracts;

namespace Topaz.Agent.Updating;

/// <summary>
/// Shared between the heartbeat (which receives offers and reports the state) and the update worker.
/// Attempts per version are kept in the state DB: a failed version is retried at most 3 times, once a day.
/// </summary>
public sealed class UpdateCoordinator
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterDownloadError = TimeSpan.FromMinutes(30);

    private readonly StateStore _state;
    private readonly UpdatePaths _paths;
    private readonly object _gate = new();
    private AgentUpdateReport? _report;
    private AgentUpdateOffer? _offer;
    private bool _startedWritten;

    public UpdateCoordinator(StateStore state, UpdatePaths paths)
    {
        _state = state;
        _paths = paths;
        _report = UpdatePaths.Read<AgentUpdateReport>(paths.ResultFile);
        // The installer is still waiting for this (new) process: it will write "succeeded" itself.
        // A failed result for another version counts as a used attempt (the installer rolled back).
        if (_report is { Status: AgentUpdateStatus.Failed } r && r.Version != AgentInfo.Version)
        {
            var seen = $"{r.Version}|{r.At.ToUnixTimeMilliseconds()}";
            if (_state.GetKv("update.counted") != seen)
            {
                NoteFailure(r.Version, r.At, RetryAfterFailure);
                _state.SetKv("update.counted", seen);
            }
        }
    }

    public SemaphoreSlim Signal { get; } = new(0, 1);
    /// <summary>
    /// Latest update state. While "installing", result.json is re-read: the installer writes the final verdict
    /// a few seconds after this (new) process has started.
    /// </summary>
    public AgentUpdateReport? Report
    {
        get
        {
            lock (_gate)
            {
                if (_report is { Status: AgentUpdateStatus.Installing } current
                    && UpdatePaths.Read<AgentUpdateReport>(_paths.ResultFile) is { } saved
                    && saved.Version == current.Version && saved.Status != AgentUpdateStatus.Installing && saved.At >= current.At)
                    _report = saved;
                return _report;
            }
        }
    }

    public void Offer(AgentUpdateOffer? offer)
    {
        if (offer is null || !UpdatePaths.IsSafeVersion(offer.Version) || SemVer.Compare(offer.Version, AgentInfo.Version) <= 0) return;
        lock (_gate)
        {
            if (_offer?.Version == offer.Version) return;
            _offer = offer;
        }
        if (Signal.CurrentCount == 0) Signal.Release();
    }

    public AgentUpdateOffer? TakeOffer()
    {
        lock (_gate) { var o = _offer; _offer = null; return o; }
    }

    public void SetReport(string version, string status, string? error)
    {
        var report = new AgentUpdateReport { Version = version, Status = status, Error = error, At = DateTimeOffset.UtcNow };
        lock (_gate) _report = report; // in memory only: result.json belongs to the installer
    }

    /// <summary>Lets the installer know this version runs and talks to the server (written once per process).</summary>
    public void MarkStarted()
    {
        lock (_gate) { if (_startedWritten) return; _startedWritten = true; }
        UpdatePaths.Write(_paths.StartedMarkerFile, new StartedMarker { Version = AgentInfo.Version, At = DateTimeOffset.UtcNow });
    }

    /// <summary>Null if an attempt is allowed now, else why not.</summary>
    public string? WhyNot(string version, DateTimeOffset now)
    {
        var (count, retryAt) = Attempts(version);
        if (count >= MaxAttempts) return $"{count} failed attempts";
        return retryAt > now ? $"next attempt after {retryAt:O}" : null;
    }

    public void NoteFailure(string version, DateTimeOffset at, TimeSpan wait, bool countsAsAttempt = true)
    {
        var (count, _) = Attempts(version);
        _state.SetKv(Key(version), $"{(countsAsAttempt ? count + 1 : count)};{(at + wait).ToUnixTimeSeconds()}");
    }

    public void NoteLaunched(string version)
    {
        // Counted now: if the installer rolls back, the old agent finds the failure in result.json on start.
        var (count, _) = Attempts(version);
        _state.SetKv(Key(version), $"{count};{(DateTimeOffset.UtcNow + RetryAfterFailure).ToUnixTimeSeconds()}");
    }

    private (int Count, DateTimeOffset RetryAt) Attempts(string version)
    {
        var v = _state.GetKv(Key(version))?.Split(';');
        if (v is not { Length: 2 }) return (0, DateTimeOffset.MinValue);
        return (int.Parse(v[0], CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeSeconds(long.Parse(v[1], CultureInfo.InvariantCulture)));
    }

    private static string Key(string version) => "update.attempts." + version;
}
