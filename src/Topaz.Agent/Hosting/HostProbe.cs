using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Topaz.Agent.Configuration;
using Topaz.Contracts;

namespace Topaz.Agent.Hosting;

/// <summary>
/// Station PC health for heartbeats: free space (Topaz DB drive, system drive), DB file size, clock.
/// Reads only file system metadata; never opens the Topaz database file. Every part is best effort.
/// </summary>
public sealed class HostProbe(AgentOptions options)
{
    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<DateTime> LocalNow { get; init; } = () => DateTime.Now;

    public HostInfo Collect()
    {
        var info = new HostInfo
        {
            ClockUtc = UtcNow(),
            LocalClock = LocalNow().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            WindowsTimeZone = TimeZoneInfo.Local.Id,
            UptimeSeconds = Environment.TickCount64 / 1000,
            Os = RuntimeInformation.OSDescription,
        };
        try { using var p = Process.GetCurrentProcess(); info.AgentMemoryBytes = p.WorkingSet64; } catch (Exception) { /* not essential */ }

        if (LocalDbFile() is { } db)
        {
            try { info.DbFileBytes = new FileInfo(db).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            (info.DbDiskFreeBytes, info.DbDiskTotalBytes) = Drive(db);
        }
        (info.SystemDiskFreeBytes, info.SystemDiskTotalBytes) = Drive(options.ResolveStateDirectory());
        return info;
    }

    /// <summary>The Topaz DB path if it is a file on this PC (not an alias, not a remote Firebird server).</summary>
    internal string? LocalDbFile()
    {
        var host = options.Firebird.DataSource?.Trim().ToLowerInvariant();
        if (host is not (null or "" or "localhost" or "127.0.0.1" or "::1" or ".") && host != Environment.MachineName.ToLowerInvariant()) return null;
        var path = options.Firebird.Database;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        return File.Exists(path) ? path : null;
    }

    private static (long? Free, long? Total) Drive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return (null, null);
            var d = new DriveInfo(root);
            return d.IsReady ? (d.AvailableFreeSpace, d.TotalSize) : (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (null, null); }
    }
}
