using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Topaz.Agent.Configuration;
using Topaz.Agent.Sending;
using Topaz.Agent.State;
using Topaz.Agent.Updating;
using Topaz.Contracts;
using Xunit;

namespace Topaz.Agent.Tests;

/// <summary>Self-update: signature checks, download + unpack, installer with rollback, retry limits.</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PublicPem => _key.ExportSubjectPublicKeyInfoPem();
    private static string Rid => UpdateVerifier.CurrentRid;

    public void Dispose()
    {
        _key.Dispose();
        _dir.Dispose();
    }

    /// <summary>A release zip (one folder, flat files) plus its manifest and CI-style signature.</summary>
    private (byte[] Zip, byte[] Manifest, byte[] Signature) Package(string version, string exeContent = "new exe", string? rid = null, ECDsa? signer = null)
    {
        var name = $"TopazAgent-{version}-{rid ?? Rid}";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string file, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry($"{name}/{file}").Open());
                w.Write(content);
            }
            Add("TopazAgent.exe", exeContent);
            Add("appsettings.json", "{\"Station\":{\"Name\":\"\"}}");
            Add("datasets.json", "{\"datasets\":[\"new\"]}");
            Add("install.ps1", "# installer");
        }
        var zipBytes = ms.ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new AgentUpdateManifest
        {
            Version = version, Rid = rid ?? Rid, File = name + ".zip", Size = zipBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant(),
        }, Protocol.Json);
        var signature = (signer ?? _key).SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (zipBytes, manifest, signature);
    }

    [Fact]
    public void Verifier_accepts_only_signed_newer_packages_for_this_runtime()
    {
        var v = new UpdateVerifier(PublicPem);
        var (zip, manifest, sig) = Package("1.2.0");
        Assert.Equal("1.2.0", v.VerifyManifest(manifest, sig, "1.2.0", Rid, "1.1.0").Version);

        var tampered = (byte[])manifest.Clone();
        tampered[^3] ^= 1;
        Assert.Throws<UpdateRejectedException>(() => v.VerifyManifest(tampered, sig, "1.2.0", Rid, "1.1.0"));
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var (_, m2, s2) = Package("1.2.0", signer: other);
            Assert.Throws<UpdateRejectedException>(() => v.VerifyManifest(m2, s2, "1.2.0", Rid, "1.1.0"));
        }
        Assert.Throws<UpdateRejectedException>(() => v.VerifyManifest(manifest, sig, "1.3.0", Rid, "1.1.0"));    // not what was offered
        Assert.Throws<UpdateRejectedException>(() => v.VerifyManifest(manifest, sig, "1.2.0", "win-arm64", "1.1.0")); // other runtime
        Assert.Throws<UpdateRejectedException>(() => v.VerifyManifest(manifest, sig, "1.2.0", Rid, "1.2.0"));     // not newer
        Assert.Throws<UpdateRejectedException>(() => v.VerifyManifest(manifest, sig, "1.2.0", Rid, "1.10.0"));

        var m = v.VerifyManifest(manifest, sig, "1.2.0", Rid, "1.1.0");
        var path = Path.Combine(_dir.Path, "p.zip");
        File.WriteAllBytes(path, zip);
        UpdateVerifier.VerifyPackage(m, path);
        zip[100] ^= 1;
        File.WriteAllBytes(path, zip);
        Assert.Throws<UpdateRejectedException>(() => UpdateVerifier.VerifyPackage(m, path));
    }

    /// <summary>Signed by CI's method (openssl dgst -sha256 -sign) with the real release key: the built-in key accepts it.</summary>
    [Fact]
    public void Openssl_signature_from_ci_verifies_with_the_built_in_key()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var manifest = File.ReadAllBytes(Path.Combine(dir, "release-manifest.json"));
        var sig = File.ReadAllBytes(Path.Combine(dir, "release-manifest.sig"));
        var v = new UpdateVerifier(UpdateKeys.ReleasePublicKeyPem);
        Assert.Equal("1.2.0", v.VerifyManifest(manifest, sig, "1.2.0", "win-x64", "1.1.0").Version);
        manifest[10] ^= 1;
        Assert.Throws<UpdateRejectedException>(() => v.VerifyManifest(manifest, sig, "1.2.0", "win-x64", "1.1.0"));
    }

    [Fact]
    public void The_built_in_release_key_is_a_valid_public_key() =>
        Assert.Throws<UpdateRejectedException>(() => new UpdateVerifier(UpdateKeys.ReleasePublicKeyPem).VerifyManifest("{}"u8.ToArray(), new byte[70], "1.2.0", Rid, "1.0.0"));

    [Theory]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.2.0", "1.2.0-rc.1", 1)]
    [InlineData("1.2.0-rc.2", "1.2.0-rc.10", -1)]
    [InlineData("1.0.0-ci.5", "1.0.0", -1)]
    [InlineData("v1.2.0", "1.2.0+abc", 0)]
    [InlineData("garbage", "0.0.1", -1)]
    public void SemVer_orders_versions(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SemVer.Compare(a, b)));

    private sealed class FakeLauncher : IUpdateLauncher
    {
        public List<(string Exe, string Plan)> Launched { get; } = new();
        public void Launch(string exePath, string planPath) => Launched.Add((exePath, planPath));
    }

    private (UpdateWorker Worker, FakeHttp Http, FakeLauncher Launcher, UpdateCoordinator Coordinator, StateStore State) Worker(string installDir)
    {
        var options = new AgentOptions { StateDirectory = Path.Combine(_dir.Path, "state"), Server = { Url = "http://central.test" } };
        var state = new StateStore(options.ResolveStateDirectory());
        var tokens = new TokenStore(state);
        tokens.Save(Guid.NewGuid(), "station-token");
        var http = new FakeHttp();
        var paths = new UpdatePaths(options.ResolveStateDirectory());
        var coordinator = new UpdateCoordinator(state, paths);
        var launcher = new FakeLauncher();
        var worker = new UpdateWorker(coordinator, new UpdateClient(new HttpClient(http), options), tokens, new UpdateVerifier(PublicPem), launcher,
            paths, options, NullLogger<UpdateWorker>.Instance) { ExecutableName = "TopazAgent.exe", CurrentVersion = "1.1.0", InstallDir = installDir };
        return (worker, http, launcher, coordinator, state);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Serve((byte[] Zip, byte[] Manifest, byte[] Signature) p) => r =>
    {
        Assert.Equal("Bearer station-token", r.Headers.Authorization?.ToString());
        var file = r.RequestUri!.Segments[^1];
        var body = file switch { "manifest.json" => p.Manifest, "manifest.sig" => p.Signature, _ when file.EndsWith(".zip") => p.Zip, _ => null };
        return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    };

    [Fact]
    public async Task Worker_downloads_verifies_unpacks_and_starts_the_installer()
    {
        var install = Directory.CreateDirectory(Path.Combine(_dir.Path, "install")).FullName;
        var (worker, http, launcher, coordinator, _) = Worker(install);
        http.Responder = Serve(Package("1.2.0"));

        Assert.True(await worker.TryUpdateAsync(new AgentUpdateOffer { Version = "1.2.0", Rid = Rid }, CancellationToken.None));
        var (exe, planPath) = Assert.Single(launcher.Launched);
        Assert.Equal("new exe", File.ReadAllText(exe));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "datasets.json")));
        Assert.Contains("/api/v1/agent/updates/1.2.0/" + Rid + "/manifest.json", http.Requests.Select(x => x.Path));
        var plan = UpdatePaths.Read<UpdatePlan>(planPath)!;
        Assert.Equal(("1.2.0", "1.1.0", install), (plan.Version, plan.PreviousVersion, plan.InstallDir));
        Assert.Equal(AgentUpdateStatus.Installing, coordinator.Report!.Status);
        // Launched once; another offer of the same version waits for the installer's verdict.
        Assert.False(await worker.TryUpdateAsync(new AgentUpdateOffer { Version = "1.2.0", Rid = Rid }, CancellationToken.None));
    }

    [Fact]
    public async Task Worker_refuses_a_tampered_package_and_limits_attempts()
    {
        var (worker, http, launcher, coordinator, _) = Worker(_dir.Path);
        var p = Package("1.2.0");
        p.Zip[200] ^= 1; // corrupted on the server (or in transit)
        http.Responder = Serve(p);

        Assert.False(await worker.TryUpdateAsync(new AgentUpdateOffer { Version = "1.2.0", Rid = Rid }, CancellationToken.None));
        Assert.Empty(launcher.Launched);
        Assert.Equal(AgentUpdateStatus.Failed, coordinator.Report!.Status);
        Assert.Contains("hash", coordinator.Report.Error);
        Assert.NotNull(coordinator.WhyNot("1.2.0", DateTimeOffset.UtcNow));                 // next try in a day
        Assert.Null(coordinator.WhyNot("1.2.0", DateTimeOffset.UtcNow.AddHours(25)));
        coordinator.NoteFailure("1.2.0", DateTimeOffset.UtcNow, TimeSpan.Zero);
        coordinator.NoteFailure("1.2.0", DateTimeOffset.UtcNow, TimeSpan.Zero);
        Assert.Contains("3 failed attempts", coordinator.WhyNot("1.2.0", DateTimeOffset.UtcNow.AddDays(30)));

        // A network error does not burn an attempt, only delays the next one.
        http.Responder = _ => FakeHttp.Throw();
        Assert.False(await worker.TryUpdateAsync(new AgentUpdateOffer { Version = "1.3.0", Rid = Rid }, CancellationToken.None));
        Assert.Null(coordinator.WhyNot("1.3.0", DateTimeOffset.UtcNow.AddMinutes(31)));
        // Offers for another runtime or an older version are ignored.
        Assert.False(await worker.TryUpdateAsync(new AgentUpdateOffer { Version = "1.4.0", Rid = "win-arm64" }, CancellationToken.None));
        Assert.False(await worker.TryUpdateAsync(new AgentUpdateOffer { Version = "1.0.0", Rid = Rid }, CancellationToken.None));
    }

    [Fact]
    public void New_version_picks_up_the_installers_verdict_after_start()
    {
        using var state = new StateStore(Path.Combine(_dir.Path, "state"));
        var paths = new UpdatePaths(Path.Combine(_dir.Path, "state"));
        var started = DateTimeOffset.UtcNow;
        // As found by the new process on start: the installer still waits for it.
        UpdatePaths.Write(paths.ResultFile, new AgentUpdateReport { Version = "1.2.1", Status = AgentUpdateStatus.Installing, At = started });
        var coordinator = new UpdateCoordinator(state, paths);
        Assert.Equal(AgentUpdateStatus.Installing, coordinator.Report!.Status);
        UpdatePaths.Write(paths.ResultFile, new AgentUpdateReport { Version = "1.2.1", Status = AgentUpdateStatus.Succeeded, At = started.AddSeconds(5) });
        Assert.Equal(AgentUpdateStatus.Succeeded, coordinator.Report!.Status);
    }

    /// <summary>Stands in for the Windows service: "starting" it runs the (fake) new version, which may report itself healthy.</summary>
    private sealed class FakeService(Action onStart) : IServiceControl
    {
        public bool Running { get; set; } = true;
        public List<string> Calls { get; } = new();
        public void Stop(string name, TimeSpan timeout) { Calls.Add("stop"); Running = false; }
        public void Start(string name, TimeSpan timeout) { Calls.Add("start"); Running = true; onStart(); }
        public bool IsRunning(string name) => Running;
    }

    private (UpdatePlan Plan, string Source, UpdatePaths Paths) Staged()
    {
        var install = Directory.CreateDirectory(Path.Combine(_dir.Path, "install")).FullName;
        File.WriteAllText(Path.Combine(install, "TopazAgent.exe"), "old exe");
        File.WriteAllText(Path.Combine(install, "appsettings.json"), "station settings");
        File.WriteAllText(Path.Combine(install, "datasets.json"), "{\"datasets\":[\"customized\"]}");
        var plan = new UpdatePlan { Version = "1.2.0", PreviousVersion = "1.1.0", InstallDir = install, StateDir = Path.Combine(_dir.Path, "state"), ServiceName = "TopazAgent" };
        var paths = new UpdatePaths(plan.StateDir);
        var source = Directory.CreateDirectory(paths.Staging("1.2.0")).FullName;
        File.WriteAllText(Path.Combine(source, "TopazAgent.exe"), "new exe");
        File.WriteAllText(Path.Combine(source, "appsettings.json"), "package defaults");
        File.WriteAllText(Path.Combine(source, "datasets.json"), "{\"datasets\":[\"new\"]}");
        File.WriteAllText(Path.Combine(source, "install.ps1"), "#");
        UpdatePaths.Write(paths.PlanFile("1.2.0"), plan);
        return (plan, source, paths);
    }

    private static UpdateInstaller Installer(UpdatePlan plan, string source, IServiceControl svc, List<string> log) =>
        new(plan, source, svc, log.Add) { HealthTimeout = TimeSpan.FromMilliseconds(600), Poll = TimeSpan.FromMilliseconds(50), CopyRetryDelay = TimeSpan.Zero };

    [Fact]
    public void Installer_replaces_files_keeps_settings_and_reports_success()
    {
        var (plan, source, paths) = Staged();
        var svc = new FakeService(() => UpdatePaths.Write(paths.StartedMarkerFile, new StartedMarker { Version = "1.2.0", At = DateTimeOffset.UtcNow }));
        var log = new List<string>();

        Assert.True(Installer(plan, source, svc, log).Run());
        Assert.Equal(["stop", "start"], svc.Calls);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(plan.InstallDir, "TopazAgent.exe")));
        Assert.Equal("station settings", File.ReadAllText(Path.Combine(plan.InstallDir, "appsettings.json")));
        Assert.Contains("new", File.ReadAllText(Path.Combine(plan.InstallDir, "datasets.json")));
        Assert.Contains("customized", File.ReadAllText(Path.Combine(plan.InstallDir, "datasets.json.bak")));
        Assert.False(File.Exists(Path.Combine(plan.InstallDir, "install.ps1")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(paths.Backup, "TopazAgent.exe")));
        Assert.Equal(AgentUpdateStatus.Succeeded, UpdatePaths.Read<AgentUpdateReport>(paths.ResultFile)!.Status);
    }

    [Fact]
    public void Installer_rolls_back_when_the_new_version_does_not_report_in()
    {
        var (plan, source, paths) = Staged();
        var svc = new FakeService(() => { });            // starts, but never reaches the server
        var log = new List<string>();

        Assert.False(Installer(plan, source, svc, log).Run());
        Assert.Equal(["stop", "start", "stop", "start"], svc.Calls);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(plan.InstallDir, "TopazAgent.exe")));
        Assert.Contains("customized", File.ReadAllText(Path.Combine(plan.InstallDir, "datasets.json")));
        Assert.False(File.Exists(Path.Combine(plan.InstallDir, "datasets.json.bak")));     // file added by the update is removed
        Assert.Equal("station settings", File.ReadAllText(Path.Combine(plan.InstallDir, "appsettings.json")));
        var result = UpdatePaths.Read<AgentUpdateReport>(paths.ResultFile)!;
        Assert.Equal((AgentUpdateStatus.Failed, "1.2.0"), (result.Status, result.Version));
        Assert.Contains("did not reach the server", result.Error);
        Assert.Contains(log, l => l.Contains("Previous files restored"));
    }

    [Fact]
    public void Installer_detects_a_crashing_new_version_quickly_and_the_old_agent_counts_the_failure()
    {
        var (plan, source, paths) = Staged();
        FakeService? svc = null;
        svc = new FakeService(() => svc!.Running = false) ;  // dies right after start
        Assert.False(Installer(plan, source, svc, []).Run());
        Assert.Contains("stopped right after start", UpdatePaths.Read<AgentUpdateReport>(paths.ResultFile)!.Error);

        // The restored old agent reads the result on start and counts the attempt once, however often it restarts.
        using var state = new StateStore(Path.Combine(_dir.Path, "state"));
        var c1 = new UpdateCoordinator(state, paths);
        _ = new UpdateCoordinator(state, paths);
        Assert.Equal(AgentUpdateStatus.Failed, c1.Report!.Status);
        Assert.NotNull(c1.WhyNot("1.2.0", DateTimeOffset.UtcNow));
        c1.NoteFailure("1.2.0", DateTimeOffset.UtcNow, TimeSpan.Zero);
        Assert.Null(c1.WhyNot("1.2.0", DateTimeOffset.UtcNow.AddSeconds(1)));          // 2 of 3 used
    }
}
