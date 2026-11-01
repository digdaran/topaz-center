using System.Reflection;

namespace Topaz.Agent.Setup;

/// <summary>What the setup needs from Windows (a fake one in tests).</summary>
public interface IMachine
{
    /// <summary>Exe path the installed service runs, or null if the service does not exist.</summary>
    string? ServiceExePath(string service);
    bool IsServiceRunning(string service);
    void StopService(string service, TimeSpan timeout);
    void StartService(string service, TimeSpan timeout);
    /// <summary>Creates or reconfigures: automatic (delayed) start, restart on failure, command "exe run".</summary>
    void ConfigureService(string service, string exePath, string displayName, string description);
    void DeleteService(string service);
    Dictionary<string, string> GetServiceEnvironment(string service);
    void SetServiceEnvironment(string service, IReadOnlyDictionary<string, string> vars);
    /// <summary>Only SYSTEM and Administrators may read the folder (queue, token).</summary>
    void RestrictToAdministrators(string directory);
    void RegisterProgram(string installDir, string exePath, string version, long sizeKb);
    void UnregisterProgram();
    void CreateStartMenuShortcut(string exePath);
    void RemoveStartMenuShortcut();
    void DeleteScheduledTask(string name);
}

public enum DbCheck { Ok, LoginFailed, Failed, Skipped }

/// <summary>Live checks against the Topaz DB and the server (a fake one in tests).</summary>
public interface ISetupProbe
{
    Task<(DbCheck Result, string? Detail)> CheckDatabaseAsync(SetupPlan plan, string password, CancellationToken ct);
    /// <summary>Server reachable? Then send (or reuse) the connection request; returns the state and the code.</summary>
    Task<SetupConnection> ConnectAsync(SetupPlan plan, string password, CancellationToken ct);
}

public sealed record SetupConnection(bool ServerReachable, bool Connected, string? StationName, string? Code, string? Problem);

public enum StepState { Running, Done, Warning, Failed }

public sealed record StepReport(string Key, string Title, StepState State, string? Detail = null);

public enum SetupOutcome { Installed, InstalledWithWarnings, NeedsPassword, Failed }

public sealed record SetupResult(SetupOutcome Outcome, SetupConnection? Connection, IReadOnlyList<string> Warnings, string? Error = null)
{
    /// <summary>0 ok, 1 installed with warnings (offline, Topaz DB not readable yet), 2 not installed / bad input.</summary>
    public int ExitCode => Outcome switch { SetupOutcome.Installed => 0, SetupOutcome.InstalledWithWarnings => 1, _ => 2 };
}

/// <summary>
/// Installs or updates the agent as a Windows service (what install.ps1 did before 1.4):
/// files, settings without secrets, the Firebird password in the service-only environment, a protected state folder,
/// the service itself, checks of the Topaz DB and the server (connection request → verification code), start,
/// and an entry in «Программы и компоненты» with a Start menu shortcut.
/// </summary>
public sealed class Installer(IMachine machine, ISetupProbe probe, Action<string> log)
{
    public TimeSpan ServiceTimeout { get; init; } = TimeSpan.FromSeconds(60);
    private const string PasswordVar = "TOPAZ_AGENT_Firebird__Password";

    public async Task<SetupResult> RunAsync(SetupPlan plan, IProgress<StepReport>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        void Step(string key, string title, StepState state, string? detail = null)
        {
            progress?.Report(new StepReport(key, title, state, detail));
            log($"[{state}] {title}{(detail is null ? "" : ": " + detail)}");
            if (state == StepState.Warning && detail is not null) warnings.Add(detail);
        }

        if (plan.Validate().ToList() is { Count: > 0 } invalid) return new SetupResult(SetupOutcome.Failed, null, warnings, string.Join(" ", invalid));
        log($"Setup {Assembly.GetExecutingAssembly().GetName().Version}: {(plan.IsUpdate ? "update" : "install")} '{plan.StationName}' into {plan.InstallDir}");

        // Firebird password: explicit > what the service already has > the standard one.
        var env = machine.GetServiceEnvironment(SetupPlan.ServiceName);
        var password = plan.FirebirdPassword ?? (env.TryGetValue(PasswordVar, out var p) && p.Length > 0 ? p : SetupPlan.DefaultFirebirdPassword);

        // The Topaz DB is checked before anything changes, so a wrong password is fixed in the window first.
        Step("db", "Проверка базы Топаза", StepState.Running);
        var (db, dbDetail) = await probe.CheckDatabaseAsync(plan, password, ct);
        switch (db)
        {
            case DbCheck.LoginFailed:
                Step("db", "Проверка базы Топаза", StepState.Failed, "Firebird не принял пароль");
                return new SetupResult(SetupOutcome.NeedsPassword, null, warnings, "Firebird не принял пароль. Укажите пароль Firebird (в «Дополнительно»).");
            case DbCheck.Failed:
                Step("db", "Проверка базы Топаза", StepState.Warning, "база Топаза пока недоступна: " + dbDetail + ". Агент будет пробовать сам.");
                break;
            default:
                Step("db", "Проверка базы Топаза", StepState.Done, dbDetail);
                break;
        }

        try
        {
            if (plan.IsUpdate && machine.IsServiceRunning(SetupPlan.ServiceName))
            {
                Step("stop", "Остановка прежней версии", StepState.Running);
                machine.StopService(SetupPlan.ServiceName, ServiceTimeout);
                Step("stop", "Остановка прежней версии", StepState.Done);
            }

            Step("files", "Копирование файлов", StepState.Running);
            CopyFiles(plan);
            Step("files", "Копирование файлов", StepState.Done, plan.InstallDir);

            Step("service", "Настройка службы", StepState.Running);
            env[PasswordVar] = password;
            env.Remove("TOPAZ_AGENT_Server__EnrollmentKey"); // shared keys are gone since 1.3
            Directory.CreateDirectory(plan.StateDir);
            machine.RestrictToAdministrators(plan.StateDir);
            machine.ConfigureService(SetupPlan.ServiceName, plan.InstalledExe, "Topaz Center: агент АЗС",
                "Читает базу АСУ «Топаз АЗС» (только чтение) и передаёт данные на сервер Topaz Center по HTTPS.");
            machine.SetServiceEnvironment(SetupPlan.ServiceName, env);
            Step("service", "Настройка службы", StepState.Done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Step("files", "Установка", StepState.Failed, ex.Message);
            TryStart(plan); // an update must not leave the old agent stopped
            return new SetupResult(SetupOutcome.Failed, null, warnings, ex.Message);
        }

        Step("server", "Подключение к серверу", StepState.Running);
        var connection = await probe.ConnectAsync(plan, password, ct);
        if (connection.Connected) Step("server", "Подключение к серверу", StepState.Done, $"АЗС подключена: {connection.StationName}");
        else if (connection.Code is not null) Step("server", "Подключение к серверу", StepState.Done, $"заявка отправлена, код {connection.Code}");
        else Step("server", "Подключение к серверу", StepState.Warning, connection.Problem ?? "сервер недоступен; агент отправит заявку, когда появится связь");

        Step("start", "Запуск службы", StepState.Running);
        try
        {
            machine.StartService(SetupPlan.ServiceName, ServiceTimeout);
            Step("start", "Запуск службы", StepState.Done);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            Step("start", "Запуск службы", StepState.Failed, ex.Message);
            return new SetupResult(SetupOutcome.Failed, connection, warnings, "Служба не запустилась: " + ex.Message);
        }

        Step("register", "Регистрация в Windows", StepState.Running);
        try
        {
            machine.RegisterProgram(plan.InstallDir, plan.InstalledExe, Hosting.AgentInfo.Version, SizeKb(plan.InstallDir));
            machine.CreateStartMenuShortcut(plan.InstalledExe);
            Step("register", "Регистрация в Windows", StepState.Done);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Step("register", "Регистрация в Windows", StepState.Warning, "ярлык или запись в «Программах и компонентах» не созданы: " + ex.Message);
        }

        return new SetupResult(warnings.Count == 0 ? SetupOutcome.Installed : SetupOutcome.InstalledWithWarnings, connection, warnings);
    }

    /// <summary>The exe itself, the dataset catalog and the settings (merged: values the station had stay).</summary>
    internal static void CopyFiles(SetupPlan plan)
    {
        Directory.CreateDirectory(plan.InstallDir);
        if (!string.Equals(Path.GetFullPath(plan.SourceExe), Path.GetFullPath(plan.InstalledExe), StringComparison.OrdinalIgnoreCase))
            CopyWithRetry(plan.SourceExe, plan.InstalledExe);

        // Replaced like self-update does: the catalog belongs to the agent version; a different old one is kept as .bak.
        var datasets = Path.Combine(plan.InstallDir, "datasets.json");
        var shipped = Defaults.Read("datasets.json");
        if (File.Exists(datasets) && File.ReadAllText(datasets) != shipped) File.Copy(datasets, datasets + ".bak", overwrite: true);
        File.WriteAllText(datasets, shipped);

        var settings = Path.Combine(plan.InstallDir, "appsettings.json");
        File.WriteAllText(settings, plan.BuildAppSettings(Defaults.Read("appsettings.json"), File.Exists(settings) ? File.ReadAllText(settings) : null));
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(500); } // the old service process may still hold the file
        }
    }

    private void TryStart(SetupPlan plan)
    {
        if (!plan.IsUpdate) return;
        try { machine.StartService(SetupPlan.ServiceName, ServiceTimeout); } catch (Exception ex) { log("Could not restart the previous version: " + ex.Message); }
    }

    private static long SizeKb(string dir)
    {
        try { return new DirectoryInfo(dir).GetFiles().Sum(f => f.Length) / 1024; }
        catch (IOException) { return 0; }
    }
}

/// <summary>Removes the agent: service, update task, Start menu shortcut, the entry in «Программы и компоненты», the program folder.</summary>
public sealed class Uninstaller(IMachine machine, Action<string> log)
{
    public void Run(string installDir, string stateDir, bool removeState)
    {
        if (machine.ServiceExePath(SetupPlan.ServiceName) is not null)
        {
            try { machine.StopService(SetupPlan.ServiceName, TimeSpan.FromSeconds(60)); } catch (Exception ex) { log("Stop: " + ex.Message); }
            machine.DeleteService(SetupPlan.ServiceName);
            log("Service removed");
        }
        machine.DeleteScheduledTask(Updating.WindowsUpdateLauncher.TaskName);
        machine.RemoveStartMenuShortcut();
        machine.UnregisterProgram();
        DeleteDir(installDir);
        DeleteDir(Path.Combine(stateDir, "updates"));
        if (removeState) DeleteDir(stateDir);
        log(removeState ? "Agent and its local state removed" : "Agent removed (local state kept)");
    }

    private void DeleteDir(string dir)
    {
        for (var attempt = 1; attempt <= 20 && Directory.Exists(dir); attempt++)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (attempt == 20) log($"Could not remove {dir}: {ex.Message}"); else Thread.Sleep(500); }
        }
    }
}

/// <summary>Default appsettings.json / datasets.json built into the exe (the one-file setup writes them out).</summary>
public static class Defaults
{
    public static string Read(string name)
    {
        using var s = typeof(Defaults).Assembly.GetManifestResourceStream("defaults." + name)
                      ?? throw new InvalidOperationException($"resource defaults.{name} is missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
