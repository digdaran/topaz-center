using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;
using Topaz.Agent.Collection;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Sending;
using Topaz.Agent.Source;
using Topaz.Agent.State;
using Topaz.Agent.Updating;

// Usage:
//   TopazAgent.exe                run (console or Windows service "TopazAgent")
//   TopazAgent.exe check          verify config, Topaz DB (read-only) and server connectivity
//   TopazAgent.exe status         show queue / watermarks
//   TopazAgent.exe requeue-dead   move rejected batches back to the queue
//   TopazAgent.exe apply-update <apply.json>   install a downloaded update (started by the agent itself)
//   TopazAgent.exe --version
var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "run";
if (args.Contains("--version")) { Console.WriteLine(AgentInfo.Version); return 0; }
// Runs from the staging folder with the package's default config: no config validation, no host.
if (command == "apply-update") return args.Length >= 2 ? UpdateInstaller.RunFromPlanFile(args[1]) : 2;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Services start in System32; config and datasets.json live next to the exe.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddEnvironmentVariables("TOPAZ_AGENT_");

var options = builder.Configuration.Get<AgentOptions>() ?? new AgentOptions();
var configErrors = options.Validate().ToList();
if (configErrors.Count > 0)
{
    Console.Error.WriteLine("Configuration errors:\n - " + string.Join("\n - ", configErrors));
    return 2;
}

var stateDir = options.ResolveStateDirectory();
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("station", options.Station.Name)
    .WriteTo.Console()
    .WriteTo.File(new CompactJsonFormatter(), Path.Combine(stateDir, "logs", "agent-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, fileSizeLimitBytes: 50_000_000, rollOnFileSizeLimit: true)
    .CreateLogger();
builder.Services.AddSerilog();

var datasetsPath = Path.IsPathRooted(options.DatasetsFile) ? options.DatasetsFile : Path.Combine(AppContext.BaseDirectory, options.DatasetsFile);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => DatasetCatalog.Load(datasetsPath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new StateStore(stateDir));
builder.Services.AddSingleton<AgentStatus>();
builder.Services.AddSingleton<HostProbe>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<Connection>();
builder.Services.AddSingleton<ITopazSource, FirebirdTopazSource>();
builder.Services.AddSingleton<Collector>();
builder.Services.AddSingleton<Sender>();
builder.Services.AddHttpClient<CentralClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(options.Server.TimeoutSeconds);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"TopazAgent/{AgentInfo.Version}");
});
builder.Services.AddHttpClient<UpdateClient>(c =>
{
    c.Timeout = TimeSpan.FromMinutes(Math.Max(1, options.Updates.DownloadTimeoutMinutes));
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"TopazAgent/{AgentInfo.Version}");
});
builder.Services.AddSingleton(_ => new UpdatePaths(stateDir));
builder.Services.AddSingleton<UpdateCoordinator>();
builder.Services.AddSingleton(_ => new UpdateVerifier(UpdateKeys.ReleasePublicKeyPem));
builder.Services.AddSingleton<IUpdateLauncher>(sp => OperatingSystem.IsWindows()
    ? new WindowsUpdateLauncher(m => Log.Warning("{Message}", m))
    : new LogOnlyUpdateLauncher());
builder.Services.AddWindowsService(o => o.ServiceName = "TopazAgent");

if (command == "run")
{
    builder.Services.AddHostedService<CollectorWorker>();
    builder.Services.AddHostedService<SenderWorker>();
    builder.Services.AddHostedService<HeartbeatWorker>();
    builder.Services.AddHostedService<UpdateWorker>();
}

using var host = builder.Build();
try
{
    switch (command)
    {
        case "run":
            Log.Information("TopazAgent {Version} starting; station '{Station}', server {Server}, state {State}",
                AgentInfo.Version, options.Station.Name, options.Server.Url, stateDir);
            await host.RunAsync();
            return 0;
        case "check":
            return await Commands.CheckAsync(host.Services);
        case "status":
            return Commands.Status(host.Services);
        case "requeue-dead":
            var n = host.Services.GetRequiredService<StateStore>().RequeueDead(DateTimeOffset.UtcNow);
            Console.WriteLine($"Requeued {n} batch(es).");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Use: run | check | status | requeue-dead | --version");
            return 2;
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

internal static class Commands
{
    public static async Task<int> CheckAsync(IServiceProvider sp)
    {
        var ok = true;
        var options = sp.GetRequiredService<AgentOptions>();
        var catalog = sp.GetRequiredService<DatasetCatalog>();
        Console.WriteLine($"Station: {options.Station.Name}; time zone: {options.ResolveTimeZone().Id}");
        Console.WriteLine($"Datasets in catalog: {catalog.Datasets.Count(d => d.Enabled)}");
        try
        {
            await using var db = await sp.GetRequiredService<ITopazSource>().OpenAsync(CancellationToken.None);
            Console.WriteLine($"[OK] Topaz DB: Firebird {await db.GetServerVersionAsync(CancellationToken.None)} (read-only transaction)");
            var columns = await db.GetColumnsAsync(CancellationToken.None);
            var missing = catalog.Datasets.Where(d => d.Enabled && !columns.ContainsKey(d.Table)).Select(d => d.Table).ToList();
            if (missing.Count > 0) Console.WriteLine($"[WARN] Tables not found (will be skipped): {string.Join(", ", missing)}");
            var window = await db.GetSessionWindowAsync(0, options.Collection.ClosedSessionsWindow, 0, CancellationToken.None);
            Console.WriteLine($"[OK] Latest shift: {window.LatestSessionId} ({(window.LatestOpen ? "open" : "closed")})");
        }
        catch (Exception ex)
        {
            ok = false;
            Console.WriteLine($"[FAIL] Topaz DB: {ex.GetBaseException().Message}");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var response = await http.GetAsync(new Uri(new Uri(options.Server.Url.TrimEnd('/') + "/"), "health/live"));
            Console.WriteLine(response.IsSuccessStatusCode ? $"[OK] Server {options.Server.Url} reachable" : $"[FAIL] Server returned {(int)response.StatusCode}");
            ok &= response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            ok = false;
            Console.WriteLine($"[FAIL] Server {options.Server.Url}: {ex.GetBaseException().Message}");
        }

        // Connection: sends the request if needed and shows the verification code for the administrator.
        var tokens = sp.GetRequiredService<TokenStore>();
        if (tokens.GetToken() is not null) Console.WriteLine($"[OK] Connected, station id {tokens.StationId}");
        else
        {
            var (_, view, _) = await sp.GetRequiredService<Connection>().StepAsync(CancellationToken.None, ignoreSchedule: true);
            switch (view.State)
            {
                case ConnectionState.Connected: Console.WriteLine($"[OK] Connection approved: {view.Detail}"); break;
                case ConnectionState.Pending:
                    Console.WriteLine($"[ACTION] Waiting for approval on the server. Verification code: {view.Code}");
                    Console.WriteLine("         Give this code to the server administrator (АЗС -> Подключение).");
                    break;
                case ConnectionState.Rejected: Console.WriteLine("[FAIL] The connection request was rejected by the server administrator"); ok = false; break;
                case ConnectionState.Blocked: Console.WriteLine("[FAIL] Connection requests from this address are blocked on the server"); ok = false; break;
                default: Console.WriteLine($"[WARN] Connection request not sent yet: {view.Detail}"); break;
            }
        }
        return ok ? 0 : 1;
    }

    public static int Status(IServiceProvider sp)
    {
        var state = sp.GetRequiredService<StateStore>();
        var q = state.GetQueueStats();
        Console.WriteLine($"State DB: {state.DatabasePath}");
        Console.WriteLine($"Queue: {q.Pending} pending ({q.PendingBytes / 1024} KiB), {q.Dead} dead, oldest {q.OldestPendingAt:O}");
        Console.WriteLine($"Shift cursor: {state.GetKv(Collector.SessionCursorKey) ?? "-"}; last full rescan: {state.GetKv(Collector.LastFullRescanKey) ?? "-"}");
        foreach (var d in sp.GetRequiredService<DatasetCatalog>().Datasets.Where(d => d.Kind == DatasetKind.Incremental))
            Console.WriteLine($"Watermark {d.DatasetName}: {state.GetKv(Collector.WatermarkKey(d.DatasetName)) ?? "-"}");
        foreach (var (seq, items, attempts, error) in state.ListDead())
            Console.WriteLine($"Dead batch #{seq}: {items} items, {attempts} attempts: {error}");
        return 0;
    }
}
