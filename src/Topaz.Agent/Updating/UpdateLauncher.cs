using System.Diagnostics;

namespace Topaz.Agent.Updating;

/// <summary>Starts the installer outside the service, so it survives the service being stopped.</summary>
public interface IUpdateLauncher
{
    void Launch(string exePath, string planPath);
}

/// <summary>Self-update installs Windows services only; elsewhere (dev runs) the offer is just logged.</summary>
public sealed class LogOnlyUpdateLauncher : IUpdateLauncher
{
    public void Launch(string exePath, string planPath) => Serilog.Log.Warning("Update ready in {Path}, but self-install works on Windows only", exePath);
}

/// <summary>One-shot scheduled task running as SYSTEM; plain detached process as a fallback.</summary>
public sealed class WindowsUpdateLauncher(Action<string> log) : IUpdateLauncher
{
    public const string TaskName = "TopazAgentUpdate";

    public void Launch(string exePath, string planPath)
    {
        var command = $"\"{exePath}\" apply-update \"{planPath}\"";
        var created = Run("schtasks.exe", ["/Create", "/TN", TaskName, "/SC", "ONCE", "/ST", "00:00", "/RU", "SYSTEM", "/RL", "HIGHEST", "/F", "/TR", command]);
        if (created == 0 && Run("schtasks.exe", ["/Run", "/TN", TaskName]) == 0) return;
        log("Scheduled task could not be started; launching the installer directly");
        using var p = Process.Start(new ProcessStartInfo(exePath) { ArgumentList = { "apply-update", planPath }, UseShellExecute = false, CreateNoWindow = true });
    }

    public static void DeleteTask(Action<string> log)
    {
        try { Run("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"]); }
        catch (Exception ex) { log("Could not delete the scheduled task: " + ex.Message); }
    }

    private static int Run(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return p.HasExited ? p.ExitCode : -1;
    }
}
