using System.Reflection;

namespace Topaz.Agent.Hosting;

public static class AgentInfo
{
    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AgentInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>Live agent state shared between workers and reported in heartbeats.</summary>
public sealed class AgentStatus
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastCollectAt, _lastSendAt;
    private string? _lastCollectError, _lastSendError, _topazDbVersion;
    private bool _topazDbOk;
    private long? _lastSessionId;
    private bool? _lastSessionOpen;

    public DateTimeOffset? LastCollectAt { get { lock (_gate) return _lastCollectAt; } set { lock (_gate) _lastCollectAt = value; } }
    public string? LastCollectError { get { lock (_gate) return _lastCollectError; } set { lock (_gate) _lastCollectError = value; } }
    public DateTimeOffset? LastSendAt { get { lock (_gate) return _lastSendAt; } set { lock (_gate) _lastSendAt = value; } }
    public string? LastSendError { get { lock (_gate) return _lastSendError; } set { lock (_gate) _lastSendError = value; } }
    public bool TopazDbOk { get { lock (_gate) return _topazDbOk; } set { lock (_gate) _topazDbOk = value; } }
    public string? TopazDbVersion { get { lock (_gate) return _topazDbVersion; } set { lock (_gate) _topazDbVersion = value; } }
    public long? LastSessionId { get { lock (_gate) return _lastSessionId; } set { lock (_gate) _lastSessionId = value; } }
    public bool? LastSessionOpen { get { lock (_gate) return _lastSessionOpen; } set { lock (_gate) _lastSessionOpen = value; } }

    /// <summary>Signalled when new batches are queued, so the sender wakes up immediately.</summary>
    public SemaphoreSlim QueueSignal { get; } = new(0, int.MaxValue);
}
