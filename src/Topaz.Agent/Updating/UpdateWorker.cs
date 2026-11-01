using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Sending;
using Topaz.Contracts;

namespace Topaz.Agent.Updating;

/// <summary>
/// Acts on update offers from heartbeats: download, verify the CI signature and hash, unpack into
/// %ProgramData%\TopazAgent\updates\&lt;version&gt; and start "apply-update" from there (it replaces this process).
/// </summary>
public sealed class UpdateWorker(
    UpdateCoordinator coordinator, UpdateClient client, TokenStore tokens, UpdateVerifier verifier, IUpdateLauncher launcher,
    UpdatePaths paths, AgentOptions options, ILogger<UpdateWorker> log) : BackgroundService
{
    public string ExecutableName { get; init; } = OperatingSystem.IsWindows() ? "TopazAgent.exe" : "TopazAgent";
    public string CurrentVersion { get; init; } = AgentInfo.Version;
    public string InstallDir { get; init; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await coordinator.Signal.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (coordinator.TakeOffer() is { } offer) await TryUpdateAsync(offer, stoppingToken);
        }
    }

    /// <summary>True when the installer was launched.</summary>
    public async Task<bool> TryUpdateAsync(AgentUpdateOffer offer, CancellationToken ct)
    {
        var version = offer.Version;
        var rid = UpdateVerifier.CurrentRid;
        if (!options.Updates.Enabled) { log.LogInformation("Update to {Version} offered, but updates are disabled locally", version); return false; }
        if (!UpdatePaths.IsSafeVersion(version) || SemVer.Compare(version, CurrentVersion) <= 0) return false;
        if (!string.Equals(offer.Rid, rid, StringComparison.OrdinalIgnoreCase)) { log.LogWarning("Update offer for {OfferRid}, this agent is {Rid}", offer.Rid, rid); return false; }
        if (coordinator.WhyNot(version, DateTimeOffset.UtcNow) is { } why) { log.LogDebug("Update to {Version} skipped: {Reason}", version, why); return false; }
        if (tokens.GetToken() is not { } token) return false;

        log.LogInformation("Updating {Current} -> {Version}", CurrentVersion, version);
        coordinator.SetReport(version, AgentUpdateStatus.Downloading, null);
        try
        {
            var manifestBytes = await client.GetSmallAsync(token, version, rid, AgentUpdateManifest.FileName, ct);
            var signature = await client.GetSmallAsync(token, version, rid, AgentUpdateManifest.SignatureFileName, ct);
            var manifest = verifier.VerifyManifest(manifestBytes, signature, version, rid, CurrentVersion);

            Directory.CreateDirectory(paths.DownloadDir);
            var zip = Path.Combine(paths.DownloadDir, manifest.File);
            await client.DownloadAsync(token, version, rid, manifest.File, zip, manifest.Size, ct);
            UpdateVerifier.VerifyPackage(manifest, zip);

            var staging = paths.Staging(version);
            Unpack(zip, staging);
            File.Delete(zip);
            var exe = Path.Combine(staging, ExecutableName);
            if (!File.Exists(exe)) throw new UpdateRejectedException($"package has no {ExecutableName}");

            var plan = paths.PlanFile(version);
            UpdatePaths.Write(plan, new UpdatePlan
            {
                Version = version, PreviousVersion = CurrentVersion, InstallDir = InstallDir,
                StateDir = options.ResolveStateDirectory(), ServiceName = options.Updates.ServiceName,
            });
            coordinator.SetReport(version, AgentUpdateStatus.Installing, null);
            coordinator.NoteLaunched(version);
            log.LogInformation("Update {Version} verified; starting the installer, the service will restart", version);
            launcher.Launch(exe, plan);
            return true;
        }
        catch (UpdateRejectedException ex)
        {
            log.LogWarning("Update {Version} rejected: {Reason}", version, ex.Message);
            coordinator.SetReport(version, AgentUpdateStatus.Failed, ex.Message);
            coordinator.NoteFailure(version, DateTimeOffset.UtcNow, UpdateCoordinator.RetryAfterFailure);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            var reason = ex is TaskCanceledException ? "download timed out" : ex.Message;
            log.LogWarning("Update {Version} not downloaded: {Reason}", version, reason);
            coordinator.SetReport(version, AgentUpdateStatus.Failed, reason);
            coordinator.NoteFailure(version, DateTimeOffset.UtcNow, UpdateCoordinator.RetryAfterDownloadError, countsAsAttempt: false);
        }
        return false;
    }

    /// <summary>The release zip holds one folder with flat files; they end up directly in <paramref name="target"/>.</summary>
    private void Unpack(string zip, string target)
    {
        var tmp = target + ".unpack";
        foreach (var d in new[] { tmp, target }) if (Directory.Exists(d)) Directory.Delete(d, true);
        ZipFile.ExtractToDirectory(zip, tmp); // rejects entries outside the folder
        var root = Directory.GetFiles(tmp, ExecutableName, SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                   ?? throw new UpdateRejectedException($"package has no {ExecutableName}");
        Directory.Move(root!, target);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
}
