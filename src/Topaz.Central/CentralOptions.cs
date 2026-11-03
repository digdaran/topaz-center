namespace Topaz.Central;

/// <summary>Bound from section "Topaz"; in Docker via env, e.g. Topaz__AdminToken.</summary>
public sealed class CentralOptions
{
    /// <summary>Connection requests accepted per IP address and hour (unauthenticated endpoint).</summary>
    public int RegistrationsPerHourPerIp { get; set; } = 10;
    /// <summary>Obsolete (shared install key, removed in 1.3): only read to warn that it can be deleted.</summary>
    public string? EnrollmentKey { get; set; }
    /// <summary>Bearer token for /api/v1/admin and /api/v1/export.</summary>
    public string? AdminToken { get; set; }
    /// <summary>Raw batch payloads older than this are cleared (rows stay for audit and dedupe).</summary>
    public int RawRetentionDays { get; set; } = 90;
    /// <summary>A station silent for longer is reported as stale (metrics, logs, admin API).</summary>
    public int StationSilentMinutes { get; set; } = 15;
    /// <summary>Failed processing attempts before a batch is parked as 'failed'.</summary>
    public int MaxProcessingAttempts { get; set; } = 5;
    /// <summary>
    /// Deletes of rows of a shift closed more than this many days ago are held, not applied (Topaz's database
    /// compaction removes old documents; the history stays here). 0 = apply every delete.
    /// </summary>
    public int HoldDeletesAfterDays { get; set; } = 3;
    public long MaxRequestBodyBytes { get; set; } = 64L * 1024 * 1024;
    public int BatchesPerMinutePerStation { get; set; } = 600;
    /// <summary>Where cookie encryption keys are kept (a Docker volume); empty = in-memory (dev/tests).</summary>
    public string? KeysDirectory { get; set; }
    /// <summary>First start only: creates this owner if there are no users yet.</summary>
    public string? BootstrapOwnerLogin { get; set; }
    public string? BootstrapOwnerPassword { get; set; }
    /// <summary>Telegram bot token (from @BotFather) for alert notifications; empty = notifications off.</summary>
    public string? TelegramBotToken { get; set; }
    public string TelegramApiBase { get; set; } = "https://api.telegram.org";
    /// <summary>Signed agent packages for self-update; default: agent-updates next to the server dll (filled by CI).</summary>
    public string? AgentUpdatesDirectory { get; set; }
}
