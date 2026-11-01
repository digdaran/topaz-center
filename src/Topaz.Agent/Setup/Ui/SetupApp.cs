using System.Diagnostics;
using System.Text;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Sending;
using Topaz.Agent.State;

namespace Topaz.Agent.Setup.Ui;

/// <summary>Entry points of the one-file setup on Windows: window, silent install, uninstall, status, preview.</summary>
public static class SetupApp
{
    public static string StateDir => new AgentOptions().ResolveStateDirectory();
    public static string DefaultInstallDir => Path.Combine(
        Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TopazAgent");
    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, SetupPlan.ExeName);
    public static string LogPath => Path.Combine(StateDir, "logs", "setup.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (IOException) { /* logging must never break the setup */ }
    }

    public static SetupPlan ResolvePlan(SetupArgs args, IMachine machine) =>
        SetupPlan.Resolve(args, machine.ServiceExePath(SetupPlan.ServiceName), DefaultInstallDir, StateDir, CurrentExe, DatabaseLocator.StandardRoots());

    /// <summary>Double click: the setup window, or the status window when started from the installed copy.</summary>
    public static int RunWindow()
    {
        ApplicationConfiguration.Initialize();
        var machine = new WindowsMachine(Log);
        var installed = machine.ServiceExePath(SetupPlan.ServiceName);
        var isInstalledCopy = installed is not null && string.Equals(Path.GetFullPath(installed), Path.GetFullPath(CurrentExe), StringComparison.OrdinalIgnoreCase);
        using var form = new SetupForm(machine, ResolvePlan(SetupArgs.Parse([]), machine), isInstalledCopy ? SetupForm.Page.Status : SetupForm.Page.Settings);
        Application.Run(form);
        return 0;
    }

    /// <summary>TopazAgent-Setup.exe /S /station="АЗС-5" …: same steps, output to the console and setup.log.</summary>
    public static int RunSilent(string[] args)
    {
        var parsed = SetupArgs.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var e in parsed.Errors) Console.Error.WriteLine(e);
            Console.Error.WriteLine("Usage: TopazAgent-Setup.exe /S /station=\"<name>\" [/server=URL] [/db=PATH] [/fbpassword=…] [/fbhost=…] [/fbport=…] [/tz=…]");
            return 2;
        }
        var machine = new WindowsMachine(Log);
        var plan = ResolvePlan(parsed, machine);
        void Out(string m) { Console.WriteLine(m); Log(m); }
        var result = new Installer(machine, new SetupProbe(), Log).RunAsync(plan,
            new SyncProgress<StepReport>(s => { if (s.State != StepState.Running) Out($"[{s.State}] {s.Key}: {s.Title}{(s.Detail is null ? "" : " - " + s.Detail)}"); }),
            CancellationToken.None).GetAwaiter().GetResult();
        if (result.Error is not null) Out("ERROR: " + result.Error);
        if (result.Connection?.Code is { } code) Out($"Verification code: {code} (give it to the server administrator: АЗС -> Подключение)");
        else if (result.Connection?.Connected == true) Out($"Connected as station '{result.Connection.StationName}'");
        Out($"Exit code {result.ExitCode}");
        return result.ExitCode;
    }

    /// <summary>
    /// "TopazAgent.exe uninstall [/S] [/removestate]". The installed exe cannot delete itself, so it continues from a
    /// temporary copy ("uninstall-continue") that waits for this process to exit and then removes everything.
    /// </summary>
    public static int Uninstall(string[] args)
    {
        var parsed = SetupArgs.Parse(args.Where(a => a.StartsWith('/') || a.StartsWith("--", StringComparison.Ordinal)));
        var machine = new WindowsMachine(Log);
        var installDir = machine.ServiceExePath(SetupPlan.ServiceName) is { } exe ? Path.GetDirectoryName(exe)! : DefaultInstallDir;
        var removeState = parsed.RemoveState;
        if (!parsed.Silent)
        {
            ApplicationConfiguration.Initialize();
            var keepData = new TaskDialogVerificationCheckBox("Удалить и данные, ещё не отправленные на сервер (очередь агента)");
            var page = new TaskDialogPage
            {
                Caption = "Topaz Center — агент АЗС",
                Heading = "Удалить агент Topaz Center?",
                Text = "Служба агента будет остановлена и удалена. АЗС перестанет передавать данные на сервер; история на сервере сохранится.",
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
                Verification = keepData,
            };
            if (TaskDialog.ShowDialog(page) != TaskDialogButton.Yes) return 1;
            removeState = keepData.Checked;
        }

        if (installDir.Length > 0 && CurrentExe.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
        {
            var temp = Path.Combine(Path.GetTempPath(), $"TopazAgentUninstall-{Guid.NewGuid():N}.exe");
            File.Copy(CurrentExe, temp);
            var psi = new ProcessStartInfo(temp) { UseShellExecute = false };
            foreach (var a in new[] { "uninstall-continue", installDir, Environment.ProcessId.ToString(), removeState ? "1" : "0", parsed.Silent ? "1" : "0" }) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return 0;
        }
        return UninstallNow(machine, installDir, removeState, parsed.Silent);
    }

    /// <summary>Second half of <see cref="Uninstall"/>, run from the temporary copy.</summary>
    public static int UninstallContinue(string[] args)
    {
        var installDir = args[1];
        if (int.TryParse(args[2], out var pid))
            try { Process.GetProcessById(pid).WaitForExit(30_000); } catch (ArgumentException) { /* already gone */ }
        var code = UninstallNow(new WindowsMachine(Log), installDir, args[3] == "1", args[4] == "1");
        // Remove this temporary copy once it has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{CurrentExe}\"") { CreateNoWindow = true, UseShellExecute = false });
        return code;
    }

    private static int UninstallNow(IMachine machine, string installDir, bool removeState, bool silent)
    {
        try
        {
            new Uninstaller(machine, Log).Run(installDir, StateDir, removeState);
            if (!silent)
            {
                ApplicationConfiguration.Initialize();
                MessageBox.Show("Агент Topaz Center удалён.", "Topaz Center — агент АЗС", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log("Uninstall failed: " + ex);
            if (!silent) MessageBox.Show("Не удалось удалить агент: " + ex.Message, "Topaz Center — агент АЗС", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    /// <summary>
    /// At service start: an agent installed by install.ps1 or updated by self-update gets its entry in
    /// «Программы и компоненты» and the Start menu shortcut (the service runs as SYSTEM and may write them).
    /// </summary>
    public static void EnsureRegistered()
    {
        try
        {
            var machine = new WindowsMachine(Log);
            var exe = CurrentExe;
            if (!WindowsMachine.IsProgramRegistered(AgentInfo.Version))
                machine.RegisterProgram(Path.GetDirectoryName(exe)!, exe, AgentInfo.Version, new FileInfo(exe).Length / 1024);
            if (!WindowsMachine.HasShortcut) machine.CreateStartMenuShortcut(exe);
        }
        catch (Exception ex) { Log("Registration in Windows skipped: " + ex.Message); }
    }

    /// <summary>What the status window shows (read from the state the service keeps).</summary>
    public sealed record AgentState(string? StationName, string? StationId, bool Connected, string? Code, string? ServerUrl, string? Database);

    public static AgentState ReadState(SetupPlan plan)
    {
        try
        {
            using var state = new StateStore(plan.StateDir);
            var tokens = new TokenStore(state);
            var connected = tokens.GetToken() is not null;
            var code = connected ? null : state.GetKv("registration_code");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return new AgentState(plan.StationName, tokens.StationId, connected, code, plan.ServerUrl, plan.DatabasePath);
        }
        catch (Exception ex)
        {
            Log("State not read: " + ex.Message);
            return new AgentState(plan.StationName, null, false, null, plan.ServerUrl, plan.DatabasePath);
        }
    }

    /// <summary>Hidden: draws each page of the window to PNG files (CI artifact to review the UI without Windows at hand).</summary>
    public static int Preview(string dir)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(dir);
        foreach (var (name, page, sample) in SetupForm.PreviewPages())
        {
            using var form = new SetupForm(new WindowsMachine(_ => { }), sample, page) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            form.Show();
            form.ApplyPreview(name);
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
        }
        return 0;
    }

    /// <summary>Progress&lt;T&gt; posts to a thread pool without a UI context; the console wants calls in order.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
