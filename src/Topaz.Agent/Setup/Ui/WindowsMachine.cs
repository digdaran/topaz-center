using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Topaz.Agent.Setup.Ui;

/// <summary>Windows side of the setup: services (sc.exe), registry, ACLs (icacls), Programs and Features, Start menu.</summary>
public sealed class WindowsMachine(Action<string> log) : IMachine
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TopazCenterAgent";
    private const string ShortcutName = "Topaz Center — агент АЗС.lnk";

    private static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

    public string? ServiceExePath(string service)
    {
        using var key = Hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        if (key?.GetValue("ImagePath") is not string image) return null;
        image = Environment.ExpandEnvironmentVariables(image.Trim());
        // "C:\Program Files\TopazAgent\TopazAgent.exe" run   or   C:\…\TopazAgent.exe
        if (image.StartsWith('"')) return image[1..image.IndexOf('"', 1)];
        var exe = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? image[..(exe + 4)] : image;
    }

    public bool IsServiceRunning(string service)
    {
        try
        {
            using var sc = new ServiceController(service);
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch (InvalidOperationException) { return false; }
    }

    public void StopService(string service, TimeSpan timeout)
    {
        using var sc = new ServiceController(service);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public void StartService(string service, TimeSpan timeout)
    {
        using var sc = new ServiceController(service);
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public void ConfigureService(string service, string exePath, string displayName, string description)
    {
        var bin = $"\"{exePath}\" run";
        if (ServiceExePath(service) is null)
            Sc("create", service, "binPath=", bin, "start=", "delayed-auto", "DisplayName=", displayName);
        else
            Sc("config", service, "binPath=", bin, "start=", "delayed-auto", "DisplayName=", displayName);
        Sc("description", service, description);
        Sc("failure", service, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");
        Sc("failureflag", service, "1");
    }

    public void DeleteService(string service) => Sc("delete", service);

    public Dictionary<string, string> GetServiceEnvironment(string service)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        if (key?.GetValue("Environment") is string[] lines)
            foreach (var line in lines)
                if (line.IndexOf('=') is var eq and > 0) vars[line[..eq]] = line[(eq + 1)..];
        return vars;
    }

    public void SetServiceEnvironment(string service, IReadOnlyDictionary<string, string> vars)
    {
        using var key = Hklm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}", writable: true)
                        ?? throw new InvalidOperationException($"service {service} is not registered");
        key.SetValue("Environment", vars.Select(kv => $"{kv.Key}={kv.Value}").ToArray(), RegistryValueKind.MultiString);
    }

    public void RestrictToAdministrators(string directory) =>
        Run("icacls.exe", [directory, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"]);

    public void RegisterProgram(string installDir, string exePath, string version, long sizeKb)
    {
        using var key = Hklm.CreateSubKey(UninstallKey, writable: true);
        key.SetValue("DisplayName", "Topaz Center — агент АЗС");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Topaz Center");
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{exePath}\" uninstall");
        key.SetValue("QuietUninstallString", $"\"{exePath}\" uninstall /S");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    public void UnregisterProgram() => Hklm.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

    public static bool IsProgramRegistered(string version)
    {
        using var key = Hklm.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string == version;
    }

    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutName);

    public static bool HasShortcut => File.Exists(ShortcutPath);

    public void CreateStartMenuShortcut(string exePath)
    {
        // WScript.Shell is available on every Windows; late binding keeps the exe free of interop assemblies.
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = Path.GetDirectoryName(exePath);
            link.Description = "Состояние агента Topaz Center, код подключения, удаление";
            link.IconLocation = exePath + ",0";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    public void RemoveStartMenuShortcut()
    {
        try { File.Delete(ShortcutPath); } catch (IOException ex) { log("Shortcut: " + ex.Message); }
    }

    public void DeleteScheduledTask(string name)
    {
        try { Run("schtasks.exe", ["/Delete", "/TN", name, "/F"], allowFailure: true); } catch (Exception ex) { log("Task: " + ex.Message); }
    }

    private void Sc(params string[] args) => Run("sc.exe", args);

    private void Run(string file, string[] args, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        if (p.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"{file} {string.Join(' ', args.Take(2))}: {output.Trim()}");
    }
}

/// <summary>A WinExe has no console: commands typed in cmd/PowerShell attach to the caller's console to print their output.</summary>
public static class ConsoleAttach
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static void ToParent()
    {
        if (AttachConsole(-1))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true });
            Console.WriteLine();
        }
    }
}

/// <summary>WinForms, dialogs and the clipboard need a single-threaded apartment; the async Main is MTA.</summary>
public static class Sta
{
    public static int Run(Func<int> action)
    {
        var code = 2;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { code = action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null)
        {
            SetupApp.Log("Unhandled: " + error);
            try { MessageBox.Show(error.Message, "Topaz Center — агент АЗС", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch (Exception) { /* no desktop */ }
        }
        return code;
    }
}
