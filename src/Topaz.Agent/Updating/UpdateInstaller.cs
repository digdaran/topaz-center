using System.Runtime.Versioning;
using System.ServiceProcess;
using Topaz.Contracts;

namespace Topaz.Agent.Updating;

public interface IServiceControl
{
    void Stop(string name, TimeSpan timeout);
    void Start(string name, TimeSpan timeout);
    bool IsRunning(string name);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceControl : IServiceControl
{
    public void Stop(string name, TimeSpan timeout)
    {
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        if (sc.Status is not ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public void Start(string name, TimeSpan timeout)
    {
        using var sc = new ServiceController(name);
        if (sc.Status is ServiceControllerStatus.Running) return;
        if (sc.Status is not ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public bool IsRunning(string name)
    {
        using var sc = new ServiceController(name);
        sc.Refresh();
        return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
    }
}

/// <summary>
/// "TopazAgent.exe apply-update &lt;apply.json&gt;", run by the new exe from the staging folder (SYSTEM scheduled task):
/// stop the service, back up the installed files, copy the new ones (appsettings.json is never touched),
/// start the service and wait until the new version reports itself healthy; otherwise restore the backup.
/// </summary>
public sealed class UpdateInstaller(UpdatePlan plan, string sourceDir, IServiceControl services, Action<string> log)
{
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ServiceTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan CopyRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "appsettings.json", "apply.json", "install.ps1", "uninstall.ps1" };
    private readonly UpdatePaths _paths = new(plan.StateDir);

    public bool Run()
    {
        Report(AgentUpdateStatus.Installing, null);
        log($"Updating {plan.PreviousVersion} -> {plan.Version}: {sourceDir} -> {plan.InstallDir}");
        try { services.Stop(plan.ServiceName, ServiceTimeout); }
        catch (Exception ex)
        {
            log("Could not stop the service: " + ex.Message);
            TryStart();
            return Fail("could not stop the service: " + ex.Message);
        }

        try
        {
            BackUp();
            Install();
        }
        catch (Exception ex)
        {
            log("Copying failed: " + ex);
            return RollBack("copying the new files failed: " + ex.Message);
        }

        var since = DateTimeOffset.UtcNow;
        try { File.Delete(_paths.StartedMarkerFile); } catch (IOException) { }
        try { services.Start(plan.ServiceName, ServiceTimeout); }
        catch (Exception ex) { return RollBack("the new version did not start: " + ex.Message); }

        var problem = WaitHealthy(since);
        if (problem is not null) return RollBack(problem);

        log("Update succeeded");
        Report(AgentUpdateStatus.Succeeded, null);
        CleanUp();
        return true;
    }

    private string? WaitHealthy(DateTimeOffset since)
    {
        var deadline = DateTime.UtcNow + HealthTimeout;
        var stoppedChecks = 0;
        while (DateTime.UtcNow < deadline)
        {
            var marker = UpdatePaths.Read<StartedMarker>(_paths.StartedMarkerFile);
            if (marker is not null && marker.Version == plan.Version && marker.At >= since.AddSeconds(-5)) return null;
            bool running;
            try { running = services.IsRunning(plan.ServiceName); } catch (Exception) { running = false; }
            stoppedChecks = running ? 0 : stoppedChecks + 1;
            if (stoppedChecks >= 3) return "the new version stopped right after start";
            Thread.Sleep(Poll);
        }
        return $"the new version did not reach the server within {HealthTimeout.TotalMinutes:0} min";
    }

    private void BackUp()
    {
        if (Directory.Exists(_paths.Backup)) Directory.Delete(_paths.Backup, true);
        Directory.CreateDirectory(_paths.Backup);
        foreach (var f in Directory.GetFiles(plan.InstallDir))
            Copy(f, Path.Combine(_paths.Backup, Path.GetFileName(f)));
        log($"Backed up {Directory.GetFiles(_paths.Backup).Length} file(s) to {_paths.Backup}");
    }

    private void Install()
    {
        foreach (var f in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(f);
            if (Skip.Contains(name)) continue;
            var target = Path.Combine(plan.InstallDir, name);
            if (name.Equals("datasets.json", StringComparison.OrdinalIgnoreCase) && File.Exists(target)
                && !File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(f)))
            {
                Copy(target, target + ".bak");
                log("datasets.json replaced; the previous one is kept as datasets.json.bak");
            }
            Copy(f, target);
        }
        log("New files copied");
    }

    private bool RollBack(string reason)
    {
        log("Rolling back: " + reason);
        try
        {
            try { services.Stop(plan.ServiceName, ServiceTimeout); } catch (Exception ex) { log("Stop before rollback: " + ex.Message); }
            if (Directory.Exists(_paths.Backup) && Directory.GetFiles(_paths.Backup).Length > 0)
            {
                var kept = Directory.GetFiles(_paths.Backup).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(plan.InstallDir))
                    if (!kept.Contains(Path.GetFileName(f)) && !Skip.Contains(Path.GetFileName(f))) File.Delete(f);
                foreach (var f in Directory.GetFiles(_paths.Backup))
                    Copy(f, Path.Combine(plan.InstallDir, Path.GetFileName(f)));
                log("Previous files restored");
            }
        }
        catch (Exception ex) { log("Rollback copy failed: " + ex); }
        TryStart();
        return Fail(reason);
    }

    private void TryStart()
    {
        try { services.Start(plan.ServiceName, ServiceTimeout); } catch (Exception ex) { log("Could not start the service: " + ex.Message); }
    }

    private bool Fail(string reason)
    {
        Report(AgentUpdateStatus.Failed, reason);
        return false;
    }

    private void Report(string status, string? error) =>
        UpdatePaths.Write(_paths.ResultFile, new AgentUpdateReport { Version = plan.Version, Status = status, Error = error, At = DateTimeOffset.UtcNow });

    /// <summary>Older staging folders and downloads; the running installer's own folder stays until the next update.</summary>
    private void CleanUp()
    {
        foreach (var d in Directory.GetDirectories(_paths.Root))
        {
            var name = Path.GetFileName(d);
            if (name == plan.Version || d == _paths.Backup || Path.GetFullPath(d) == Path.GetFullPath(sourceDir)) continue;
            try { Directory.Delete(d, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log($"Could not remove {d}: {ex.Message}"); }
        }
    }

    private void Copy(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 15) { Thread.Sleep(CopyRetryDelay); }
        }
    }

    /// <summary>Entry point of the apply-update command. Exit code 0 = updated, 1 = rolled back or failed.</summary>
    public static int RunFromPlanFile(string planPath)
    {
        var plan = UpdatePaths.Read<UpdatePlan>(planPath);
        if (plan is null) { Console.Error.WriteLine($"Cannot read {planPath}"); return 2; }
        var paths = new UpdatePaths(plan.StateDir);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogFile(plan.Version))!);
        void Log(string m)
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {m}";
            Console.WriteLine(line);
            try { File.AppendAllText(paths.LogFile(plan.Version), line + Environment.NewLine); } catch (IOException) { }
        }
        if (!OperatingSystem.IsWindows()) { Log("apply-update works on Windows only"); return 2; }
        var ok = new UpdateInstaller(plan, Path.GetDirectoryName(Path.GetFullPath(planPath))!, new WindowsServiceControl(), Log).Run();
        WindowsUpdateLauncher.DeleteTask(Log);
        return ok ? 0 : 1;
    }
}
