using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>Station PC health from heartbeats: clock skew, low disk with forecast, auto-closing alerts.</summary>
public sealed class HostHealthTests(CentralFixture fx) : IClassFixture<CentralFixture>
{
    private const long Gb = 1L << 30;

    private static HostInfo Host(TimeSpan clockOffset, long dbFree, long sysFree = 40 * Gb) => new()
    {
        ClockUtc = DateTimeOffset.UtcNow,
        // Station zone Europe/Moscow (UTC+3, no DST)
        LocalClock = (DateTime.UtcNow + TimeSpan.FromHours(3) + clockOffset).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        WindowsTimeZone = "Russian Standard Time",
        DbDiskFreeBytes = dbFree, DbDiskTotalBytes = 50 * Gb, DbFileBytes = 3 * Gb,
        SystemDiskFreeBytes = sysFree, SystemDiskTotalBytes = 60 * Gb, UptimeSeconds = 3600, Os = "Microsoft Windows 10.0.19045",
    };

    private async Task Beat(HttpClient agent, HostInfo? host) =>
        (await agent.PostAsJsonAsync("/api/v1/heartbeat", new HeartbeatRequest { AgentVersion = "1.2.1", Rid = "win-x64", TimeZone = "Europe/Moscow", Host = host }, Protocol.Json))
        .EnsureSuccessStatusCode();

    [PostgresFact]
    public async Task Clock_skew_and_low_disk_raise_alerts_that_close_when_fixed()
    {
        var st = await fx.EnrollAsync("АЗС-host");
        var agent = fx.Client(st.Token);
        var detector = fx.Factory!.Services.GetRequiredService<AnomalyDetector>();

        await Beat(agent, null);                                                  // an older agent: nothing breaks
        Assert.Null(await fx.QueryAsync<decimal?>("SELECT clock_skew_seconds FROM stations WHERE id = @id", new { id = st.StationId }));

        // Last week the disk had 8 GB free and the DB was 2.3 GB; now 1.5 GB free and the PC clock is 10 min fast.
        await fx.QueryAsync<int>("""
            WITH i AS (INSERT INTO station_host_daily (station_id, day, db_size, db_disk_free, db_disk_total, sys_disk_free, sys_disk_total)
                       VALUES (@id, (now() AT TIME ZONE 'Europe/Moscow')::date - 7, @db, @free, @total, @sys, @sys) RETURNING 1) SELECT count(*)::int FROM i
            """, new { id = st.StationId, db = (long)(2.3 * Gb), free = 8 * Gb, total = 50 * Gb, sys = 40 * Gb });
        await Beat(agent, Host(TimeSpan.FromMinutes(10), dbFree: (long)(1.5 * Gb)));
        var skew = await fx.QueryAsync<decimal>("SELECT clock_skew_seconds FROM stations WHERE id = @id", new { id = st.StationId });
        Assert.InRange(skew, 595, 605);

        await detector.RunAsync(CancellationToken.None);
        var clock = await fx.QueryAsync<string>("SELECT title || ' | ' || details FROM alerts WHERE rule_code = 'clock_skew' AND station_id = @id", new { id = st.StationId });
        Assert.Contains("спешат на 10 мин", clock);
        Assert.Contains("Russian Standard Time", clock);
        var disk = await fx.QueryAsync<string>("SELECT title || ' | ' || details FROM alerts WHERE rule_code = 'disk_low' AND station_id = @id", new { id = st.StationId });
        Assert.Contains("свободно 1,5 ГБ", disk);
        Assert.Contains("растёт на 717 МБ в неделю", disk);                   // (3 − 2.3) GB over 7 days
        Assert.Contains("закончится примерно через 2 дн.", disk);              // 1.5 GB at (8 − 1.5)/7 GB a day

        // The owner sees it in «АЗС».
        var users = fx.Factory.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("host-owner", "o", Roles.Owner, "host-password-1", []); } catch (Npgsql.PostgresException) { }
        var owner = fx.Factory.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "host-owner", password = "host-password-1" })).EnsureSuccessStatusCode();
        var row = (await owner.GetFromJsonAsync<JsonArray>("/api/v1/ui/stations"))!.Single(r => r!["id"]!.GetValue<Guid>() == st.StationId)!;
        Assert.Equal(2, row["days_until_full"]!.GetValue<decimal>());
        Assert.Equal((long)(1.5 * Gb), row["db_disk_free"]!.GetValue<long>());
        Assert.Equal("Russian Standard Time", row["windows_time_zone"]!.GetValue<string>());
        Assert.True(row["disk_low"]!.GetValue<bool>());
        Assert.True(row["clock_off"]!.GetValue<bool>());

        // Fixed on the PC: both alerts close by themselves.
        await Beat(agent, Host(TimeSpan.FromSeconds(20), dbFree: 12 * Gb));
        await detector.RunAsync(CancellationToken.None);
        Assert.Equal(0, await fx.QueryAsync<int>("SELECT count(*)::int FROM alerts WHERE station_id = @id AND rule_code IN ('clock_skew', 'disk_low') AND status <> 'closed'", new { id = st.StationId }));
    }
}
