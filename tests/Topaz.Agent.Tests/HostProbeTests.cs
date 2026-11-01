using System.Text.Json;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Agent.Tests;

public sealed class HostProbeTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private AgentOptions Options(string database, string dataSource = "127.0.0.1") =>
        new() { StateDirectory = Path.Combine(_dir.Path, "state"), Firebird = { Database = database, DataSource = dataSource } };

    [Fact]
    public void Reports_disks_db_size_and_clock_without_opening_the_database()
    {
        var db = Path.Combine(_dir.Path, "TopazAZS.fdb");
        File.WriteAllBytes(db, new byte[4096]);
        using var locked = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.None); // Firebird holds the file
        var probe = new HostProbe(Options(db)) { UtcNow = () => new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero), LocalNow = () => new DateTime(2026, 11, 1, 10, 7, 30) };

        var h = probe.Collect();
        Assert.Equal(4096, h.DbFileBytes);
        Assert.True(h.DbDiskTotalBytes > 0 && h.DbDiskFreeBytes > 0 && h.DbDiskFreeBytes <= h.DbDiskTotalBytes);
        Assert.True(h.SystemDiskTotalBytes > 0);
        Assert.Equal("2026-11-01T10:07:30", h.LocalClock);
        Assert.True(h.UptimeSeconds > 0);
        Assert.Contains("\"db_file_bytes\":4096", JsonSerializer.Serialize(new HeartbeatRequest { Host = h }, Protocol.Json));
    }

    [Theory]
    [InlineData("TOPAZ", "127.0.0.1")]                       // Firebird alias, not a path
    [InlineData("C:\\TopazAZS\\DB\\missing.fdb", "127.0.0.1")]
    [InlineData("/var/db/topaz.fdb", "10.0.0.5")]            // Firebird on another PC
    public void Unknown_db_location_gives_no_db_figures(string database, string dataSource)
    {
        var h = new HostProbe(Options(database, dataSource)).Collect();
        Assert.Null(h.DbFileBytes);
        Assert.Null(h.DbDiskFreeBytes);
        Assert.True(h.SystemDiskFreeBytes > 0); // the rest is still reported
    }
}
