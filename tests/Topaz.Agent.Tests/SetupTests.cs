using System.Text.Json.Nodes;
using Topaz.Agent.Setup;
using Xunit;

namespace Topaz.Agent.Tests;

/// <summary>The one-file setup's logic (the Windows parts are behind IMachine; CI also runs a real install on Windows).</summary>
public sealed class SetupTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private sealed class FakeMachine : IMachine
    {
        public string? ExePath;
        public bool Running;
        public List<string> Calls { get; } = new();
        public Dictionary<string, string> Env { get; } = new();
        public bool Registered, Shortcut;
        public string? ServiceExePath(string service) => ExePath;
        public bool IsServiceRunning(string service) => Running;
        public void StopService(string service, TimeSpan timeout) { Calls.Add("stop"); Running = false; }
        public void StartService(string service, TimeSpan timeout) { Calls.Add("start"); Running = true; }
        public void ConfigureService(string service, string exePath, string displayName, string description) { Calls.Add("configure"); ExePath = exePath; }
        public void DeleteService(string service) { Calls.Add("delete"); ExePath = null; }
        public Dictionary<string, string> GetServiceEnvironment(string service) => new(Env);
        public void SetServiceEnvironment(string service, IReadOnlyDictionary<string, string> vars) { Env.Clear(); foreach (var kv in vars) Env[kv.Key] = kv.Value; }
        public void RestrictToAdministrators(string directory) => Calls.Add("acl");
        public void RegisterProgram(string installDir, string exePath, string version, long sizeKb) => Registered = true;
        public void UnregisterProgram() => Registered = false;
        public void CreateStartMenuShortcut(string exePath) => Shortcut = true;
        public void RemoveStartMenuShortcut() => Shortcut = false;
        public void DeleteScheduledTask(string name) => Calls.Add("task:" + name);
    }

    private sealed class FakeProbe(DbCheck db, SetupConnection connection) : ISetupProbe
    {
        public List<string> Passwords { get; } = new();
        public Task<(DbCheck Result, string? Detail)> CheckDatabaseAsync(SetupPlan plan, string password, CancellationToken ct)
        {
            Passwords.Add(password);
            return Task.FromResult<(DbCheck, string?)>((db, db == DbCheck.Ok ? "Firebird 2.5.9" : "Your user name and password are not defined"));
        }
        public Task<SetupConnection> ConnectAsync(SetupPlan plan, string password, CancellationToken ct) => Task.FromResult(connection);
    }

    private (SetupPlan Plan, string Db) NewPlan(string station = "АЗС-5 Северная")
    {
        var topaz = Directory.CreateDirectory(Path.Combine(_dir.Path, "pf86", "TopazAZS-2")).FullName;
        var db = Path.Combine(topaz, "TopazAZS.fdb");
        File.WriteAllText(db, "fdb");
        var source = Path.Combine(_dir.Path, "Downloads", "TopazAgent-Setup-1.4.0-win-x64.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "agent exe");
        var plan = SetupPlan.Resolve(SetupArgs.Parse([$"/station={station}"]), null, Path.Combine(_dir.Path, "Program Files", "TopazAgent"),
            Path.Combine(_dir.Path, "ProgramData", "TopazAgent"), source, [Path.Combine(_dir.Path, "pf86")]);
        return (plan, db);
    }

    [Fact]
    public void Arguments_parse_like_a_windows_setup()
    {
        var a = SetupArgs.Parse(["/S", "/station=АЗС 5 Северная", "/server:topaz.example.ru", "--db=\"D:\\Topaz\\TopazAZS.fdb\"", "/fbport=3051", "/fbpassword=secret"]);
        Assert.Empty(a.Errors);
        Assert.True(a.Silent);
        Assert.Equal(("АЗС 5 Северная", "topaz.example.ru", "D:\\Topaz\\TopazAZS.fdb", 3051, "secret"), (a.Station, a.Server, a.Database, a.FirebirdPort, a.FirebirdPassword));
        Assert.True(SetupArgs.LooksLikeSetup(["/S", "/station=x"]));
        Assert.False(SetupArgs.LooksLikeSetup(["check"]));
        Assert.False(SetupArgs.LooksLikeSetup([]));
        var bad = SetupArgs.Parse(["/stantion=x", "/fbport=abc", "/server="]);
        Assert.Equal(3, bad.Errors.Count);
    }

    [Fact]
    public void Plan_finds_the_newest_topaz_db_and_an_update_keeps_previous_settings()
    {
        var older = Directory.CreateDirectory(Path.Combine(_dir.Path, "pf86", "TopazAZS-1")).FullName;
        File.WriteAllText(Path.Combine(older, "TopazAZS.fdb"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(older, "TopazAZS.fdb"), DateTime.UtcNow.AddDays(-30));
        var (plan, db) = NewPlan();
        Assert.Equal(db, plan.DatabasePath);
        Assert.Equal(SetupPlan.DefaultServerUrl, plan.ServerUrl);
        Assert.False(plan.IsUpdate);
        Assert.Empty(plan.Validate());
        Assert.Contains("Укажите название АЗС.", NewPlan(station: " ").Plan.Validate());

        // An installed agent (install.ps1 era): its settings win over the defaults, arguments win over both.
        var installed = Directory.CreateDirectory(Path.Combine(_dir.Path, "Old", "TopazAgent")).FullName;
        File.WriteAllText(Path.Combine(installed, "appsettings.json"), """
            { "Server": { "Url": "https://other.example.ru", "EnrollmentKey": "old" }, "Station": { "Name": "АЗС Старт", "TimeZone": "Europe/Samara" },
              "Firebird": { "DataSource": "127.0.0.1", "Port": 3050, "Database": "E:\\Topaz\\TopazAZS.fdb", "User": "SYSDBA" } }
            """);
        var update = SetupPlan.Resolve(SetupArgs.Parse([]), Path.Combine(installed, "TopazAgent.exe"), "C:\\ignored", plan.StateDir, plan.SourceExe, []);
        Assert.True(update.IsUpdate);
        Assert.Equal((installed, "АЗС Старт", "https://other.example.ru", "E:\\Topaz\\TopazAZS.fdb", "Europe/Samara"),
            (update.InstallDir, update.StationName, update.ServerUrl, update.DatabasePath, update.TimeZone));
        var renamed = SetupPlan.Resolve(SetupArgs.Parse(["/station=АЗС Старт-2"]), Path.Combine(installed, "TopazAgent.exe"), "C:\\ignored", plan.StateDir, plan.SourceExe, []);
        Assert.Equal("АЗС Старт-2", renamed.StationName);

        // Settings file: our values, no secrets, the old enrollment key dropped, unknown sections kept.
        var json = JsonNode.Parse(update.BuildAppSettings(Defaults.Read("appsettings.json"), File.ReadAllText(Path.Combine(installed, "appsettings.json"))))!;
        Assert.Null(json["Server"]!["EnrollmentKey"]);
        Assert.Null(json["Firebird"]!["Password"]);
        Assert.Equal("АЗС Старт", json["Station"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Installs_files_service_password_and_registration_in_order()
    {
        var (plan, _) = NewPlan();
        var machine = new FakeMachine();
        var probe = new FakeProbe(DbCheck.Ok, new SetupConnection(true, false, null, "K7Q4-9MXD", null));
        var steps = new List<StepReport>();
        var result = await new Installer(machine, probe, _ => { }).RunAsync(plan, new Progress(steps), default);

        Assert.Equal(SetupOutcome.Installed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("K7Q4-9MXD", result.Connection!.Code);
        Assert.Equal("agent exe", File.ReadAllText(plan.InstalledExe));
        Assert.Equal(Defaults.Read("datasets.json"), File.ReadAllText(Path.Combine(plan.InstallDir, "datasets.json")));
        Assert.Contains("АЗС-5 Северная", File.ReadAllText(Path.Combine(plan.InstallDir, "appsettings.json")));
        Assert.DoesNotContain(SetupPlan.DefaultFirebirdPassword, File.ReadAllText(Path.Combine(plan.InstallDir, "appsettings.json")));
        Assert.Equal(SetupPlan.DefaultFirebirdPassword, machine.Env["TOPAZ_AGENT_Firebird__Password"]);   // only in the service environment
        Assert.Equal(["acl", "configure", "start"], machine.Calls);
        Assert.True(machine.Registered && machine.Shortcut);
        Assert.Equal(["db", "files", "service", "server", "start", "register"], steps.Where(s => s.State == StepState.Done).Select(s => s.Key).ToArray());
    }

    [Fact]
    public async Task Wrong_firebird_password_changes_nothing_and_asks_again()
    {
        var (plan, _) = NewPlan();
        var machine = new FakeMachine { ExePath = "x", Running = true };
        machine.Env["TOPAZ_AGENT_Firebird__Password"] = "station-specific";
        var probe = new FakeProbe(DbCheck.LoginFailed, new SetupConnection(true, false, null, null, null));
        var result = await new Installer(machine, probe, _ => { }).RunAsync(plan, null, default);

        Assert.Equal(SetupOutcome.NeedsPassword, result.Outcome);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(["station-specific"], probe.Passwords);    // the service's current password is tried first
        Assert.Empty(machine.Calls);                           // the running agent was not touched
        Assert.False(File.Exists(plan.InstalledExe));
    }

    [Fact]
    public async Task Update_stops_the_old_agent_keeps_a_custom_catalog_as_bak_and_drops_the_old_key()
    {
        var (plan, _) = NewPlan();
        Directory.CreateDirectory(plan.InstallDir);
        File.WriteAllText(Path.Combine(plan.InstallDir, "datasets.json"), "{\"datasets\":[\"customized\"]}");
        var machine = new FakeMachine { ExePath = plan.InstalledExe, Running = true };
        machine.Env["TOPAZ_AGENT_Server__EnrollmentKey"] = "old-key";
        plan.IsUpdate = true;
        var offline = new SetupConnection(false, false, null, null, "сервер недоступен");
        var result = await new Installer(machine, new FakeProbe(DbCheck.Ok, offline), _ => { }).RunAsync(plan, null, default);

        Assert.Equal(SetupOutcome.InstalledWithWarnings, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["stop", "acl", "configure", "start"], machine.Calls);
        Assert.Contains("customized", File.ReadAllText(Path.Combine(plan.InstallDir, "datasets.json.bak")));
        Assert.False(machine.Env.ContainsKey("TOPAZ_AGENT_Server__EnrollmentKey"));
    }

    [Fact]
    public void Uninstall_removes_service_task_registration_and_program_folder_but_keeps_state_by_default()
    {
        var (plan, _) = NewPlan();
        Installer.CopyFiles(plan);
        Directory.CreateDirectory(Path.Combine(plan.StateDir, "updates"));
        File.WriteAllText(Path.Combine(plan.StateDir, "agent.db"), "queue");
        var machine = new FakeMachine { ExePath = plan.InstalledExe, Running = true, Registered = true, Shortcut = true };

        new Uninstaller(machine, _ => { }).Run(plan.InstallDir, plan.StateDir, removeState: false);
        Assert.Equal(["stop", "delete", "task:TopazAgentUpdate"], machine.Calls);
        Assert.False(machine.Registered || machine.Shortcut);
        Assert.False(Directory.Exists(plan.InstallDir));
        Assert.False(Directory.Exists(Path.Combine(plan.StateDir, "updates")));
        Assert.True(File.Exists(Path.Combine(plan.StateDir, "agent.db")));

        new Uninstaller(machine, _ => { }).Run(plan.InstallDir, plan.StateDir, removeState: true);
        Assert.False(Directory.Exists(plan.StateDir));
    }

    [Fact]
    public void Login_failures_are_recognised() =>
        Assert.True(SetupProbe.IsLoginFailure("Your user name and password are not defined. Ask your database administrator to set up a Firebird login."));

    private sealed class Progress(List<StepReport> steps) : IProgress<StepReport>
    {
        public void Report(StepReport value) => steps.Add(value);
    }
}
