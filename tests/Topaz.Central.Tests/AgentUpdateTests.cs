using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Topaz.Central.Processing;
using Topaz.Central.Ui;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Central.Tests;

/// <summary>Agent self-update on the server: package catalog, staged rollout policy, file download, alerts, UI rights.</summary>
public sealed class AgentUpdateTests(CentralFixture fx) : IClassFixture<CentralFixture>, IDisposable
{
    private const string Password = "update-password-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "topaz-updates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A package as CI lays it out; the server only checks that the signature file exists (agents verify it).</summary>
    private byte[] AddPackage(string version, string rid, bool signed = true)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, version, rid)).FullName;
        var zip = new byte[1234];
        Random.Shared.NextBytes(zip);
        var file = $"TopazAgent-{version}-{rid}.zip";
        File.WriteAllBytes(Path.Combine(dir, file), zip);
        File.WriteAllBytes(Path.Combine(dir, AgentUpdateManifest.FileName), JsonSerializer.SerializeToUtf8Bytes(
            new AgentUpdateManifest { Version = version, Rid = rid, File = file, Size = zip.Length, Sha256 = new string('a', 64) }, Protocol.Json));
        if (signed) File.WriteAllBytes(Path.Combine(dir, AgentUpdateManifest.SignatureFileName), [1, 2, 3]);
        return zip;
    }

    private WebApplicationFactory<Program> App() => fx.Factory!.WithWebHostBuilder(b => b.UseSetting("Topaz:AgentUpdatesDirectory", _dir));

    private static async Task<AgentUpdateOffer?> Heartbeat(HttpClient agent, string version, string? rid = "win-x64", AgentUpdateReport? report = null)
    {
        var r = await agent.PostAsJsonAsync("/api/v1/heartbeat", new HeartbeatRequest { AgentVersion = version, Rid = rid, Update = report }, Protocol.Json);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<HeartbeatResponse>(Protocol.Json))!.Update;
    }

    private static HttpClient Agent(WebApplicationFactory<Program> app, string token)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [PostgresFact]
    public async Task Rollout_goes_to_pilots_first_and_the_rest_after_their_success()
    {
        var zip = AddPackage("1.2.0", "win-x64");
        AddPackage("1.2.0", "win-x86");
        AddPackage("1.3.0", "win-x64", signed: false); // unsigned: never offered
        await using var app = App();
        var users = app.Services.GetRequiredService<UserService>();
        try { await users.CreateAsync("upd-owner", "o", Roles.Owner, Password, []); } catch (Npgsql.PostgresException) { }
        var owner = app.CreateClient();
        owner.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await owner.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "upd-owner", password = Password })).EnsureSuccessStatusCode();

        var pilotSt = await fx.EnrollAsync("АЗС-upd-pilot");
        var normalSt = await fx.EnrollAsync("АЗС-upd-normal");
        var excludedSt = await fx.EnrollAsync("АЗС-upd-excluded");
        var oldSt = await fx.EnrollAsync("АЗС-upd-old");
        var (pilot, normal, excluded, old) = (Agent(app, pilotSt.Token), Agent(app, normalSt.Token), Agent(app, excludedSt.Token), Agent(app, oldSt.Token));
        (await owner.PatchAsJsonAsync($"/api/v1/ui/stations/{pilotSt.StationId}/update", new { update_group = "pilot" })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/v1/ui/stations/{excludedSt.StationId}/update", new { update_group = "excluded" })).EnsureSuccessStatusCode();

        // Pilot at once; the others wait 24 h by default; agents without a runtime id (pre-1.2) get nothing.
        var offer = await Heartbeat(pilot, "1.1.0");
        Assert.Equal(("1.2.0", "win-x64"), (offer!.Version, offer.Rid));
        Assert.Equal("win-x86", (await Heartbeat(pilot, "1.1.0", "win-x86"))!.Rid);
        Assert.Null(await Heartbeat(normal, "1.1.0"));
        Assert.Null(await Heartbeat(excluded, "1.1.0"));
        Assert.Null(await Heartbeat(old, "1.0.2", rid: null));
        Assert.Null(await Heartbeat(pilot, "1.2.0"));                       // already there

        // Files: station token required, only the package's own files.
        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/agent/updates/1.2.0/win-x64/manifest.json")).StatusCode);
        Assert.Equal(zip, await pilot.GetByteArrayAsync("/api/v1/agent/updates/1.2.0/win-x64/TopazAgent-1.2.0-win-x64.zip"));
        Assert.Equal(HttpStatusCode.OK, (await pilot.GetAsync("/api/v1/agent/updates/1.2.0/win-x64/manifest.sig")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pilot.GetAsync("/api/v1/agent/updates/1.3.0/win-x64/manifest.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pilot.GetAsync("/api/v1/agent/updates/1.2.0/win-x64/other.txt")).StatusCode);

        // A failed pilot blocks the rest even after the delay, and raises an alert.
        (await owner.PutAsJsonAsync("/api/v1/ui/agent-updates", new { pilot_delay_hours = 0 })).EnsureSuccessStatusCode();
        await Heartbeat(pilot, "1.1.0", report: new AgentUpdateReport { Version = "1.2.0", Status = AgentUpdateStatus.Failed, Error = "the new version stopped right after start", At = DateTimeOffset.UtcNow });
        Assert.Null(await Heartbeat(normal, "1.1.0"));
        await app.Services.GetRequiredService<AnomalyDetector>().RunAsync(CancellationToken.None);
        Assert.Equal(1, await fx.QueryAsync<int>("SELECT count(*)::int FROM alerts WHERE rule_code = 'agent_update_failed' AND station_id = @id", new { id = pilotSt.StationId }));

        // The pilot succeeds on the next try: the others follow.
        await Heartbeat(pilot, "1.2.0", report: new AgentUpdateReport { Version = "1.2.0", Status = AgentUpdateStatus.Succeeded, At = DateTimeOffset.UtcNow.AddMinutes(1) });
        Assert.Equal("1.2.0", (await Heartbeat(normal, "1.1.0"))!.Version);
        Assert.Null(await Heartbeat(excluded, "1.1.0"));

        // Pause and "off" stop offers; "update now" on a station works regardless and clears itself once done.
        (await owner.PutAsJsonAsync("/api/v1/ui/agent-updates", new { paused = true })).EnsureSuccessStatusCode();
        Assert.Null(await Heartbeat(normal, "1.1.0"));
        (await owner.PatchAsJsonAsync($"/api/v1/ui/stations/{excludedSt.StationId}/update", new { update_now = true })).EnsureSuccessStatusCode();
        Assert.NotNull(await Heartbeat(excluded, "1.1.0"));
        Assert.Null(await Heartbeat(excluded, "1.2.0"));
        Assert.False(await fx.QueryAsync<bool>("SELECT update_now FROM stations WHERE id = @id", new { id = excludedSt.StationId }));
        (await owner.PutAsJsonAsync("/api/v1/ui/agent-updates", new { paused = false, mode = "off" })).EnsureSuccessStatusCode();
        Assert.Null(await Heartbeat(pilot, "1.1.0"));

        // "Update all now" skips the pilot wait.
        (await owner.PutAsJsonAsync("/api/v1/ui/agent-updates", new { mode = "auto", pilot_delay_hours = 48 })).EnsureSuccessStatusCode();
        await fx.QueryAsync<int>("WITH d AS (DELETE FROM agent_update_reports RETURNING 1) SELECT count(*)::int FROM d");
        await fx.QueryAsync<int>("WITH u AS (UPDATE stations SET agent_version = '1.1.0' RETURNING 1) SELECT count(*)::int FROM u");
        Assert.Null(await Heartbeat(normal, "1.1.0"));
        Assert.Equal("1.2.0", (await (await owner.PostAsync("/api/v1/ui/agent-updates/release-now", null)).Content.ReadFromJsonAsync<JsonObject>())!["version"]!.GetValue<string>());
        Assert.Equal("1.2.0", (await Heartbeat(normal, "1.1.0"))!.Version);

        var state = (await owner.GetFromJsonAsync<JsonObject>("/api/v1/ui/agent-updates"))!;
        Assert.Equal("1.2.0", state["latest"]!.GetValue<string>());
        Assert.Equal(2, state["packages"]!.AsArray().Count);                // the unsigned one is not listed
        Assert.Equal(4, state["progress"]!["total"]!.GetValue<int>());
        Assert.Contains("agent_updates_release_now", await owner.GetStringAsync("/api/v1/ui/audit"));

        // Owner only.
        try { await users.CreateAsync("upd-manager", "m", Roles.Manager, Password, [normalSt.StationId]); } catch (Npgsql.PostgresException) { }
        var manager = app.CreateClient();
        manager.DefaultRequestHeaders.Add(UiAuthSetup.CsrfHeader, "1");
        (await manager.PostAsJsonAsync("/api/v1/ui/auth/login", new { login = "upd-manager", password = Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/v1/ui/agent-updates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PatchAsJsonAsync($"/api/v1/ui/stations/{normalSt.StationId}/update", new { update_now = true })).StatusCode);
    }
}
