using Microsoft.Extensions.Logging.Abstractions;
using Topaz.Agent.Configuration;
using Topaz.Agent.Hosting;
using Topaz.Agent.Sending;
using Topaz.Agent.Source;
using Topaz.Agent.State;

namespace Topaz.Agent.Setup;

/// <summary>The setup's live checks, using the agent's own read-only Topaz access and connection request.</summary>
public sealed class SetupProbe : ISetupProbe
{
    public async Task<(DbCheck Result, string? Detail)> CheckDatabaseAsync(SetupPlan plan, string password, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            await using var db = await new FirebirdTopazSource(Options(plan, password)).OpenAsync(cts.Token);
            var version = await db.GetServerVersionAsync(cts.Token);
            var window = await db.GetSessionWindowAsync(0, 0, 0, cts.Token);
            return (DbCheck.Ok, $"Firebird {version}, последняя смена №{window.LatestSessionId}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var message = ex.GetBaseException().Message;
            return (IsLoginFailure(message) ? DbCheck.LoginFailed : DbCheck.Failed, message);
        }
    }

    public static bool IsLoginFailure(string message) =>
        message.Contains("user name and password are not defined", StringComparison.OrdinalIgnoreCase)
        || message.Contains("login", StringComparison.OrdinalIgnoreCase) && message.Contains("password", StringComparison.OrdinalIgnoreCase);

    public async Task<SetupConnection> ConnectAsync(SetupPlan plan, string password, CancellationToken ct)
    {
        var options = Options(plan, password);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var health = await http.GetAsync(new Uri(new Uri(plan.ServerUrl.TrimEnd('/') + "/"), "health/live"), ct);
            if (!health.IsSuccessStatusCode) return new SetupConnection(false, false, null, null, $"сервер {plan.ServerUrl} ответил {(int)health.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new SetupConnection(false, false, null, null, $"сервер {plan.ServerUrl} недоступен ({ex.GetBaseException().Message}); агент подключится, когда появится связь");
        }

        try
        {
            using var state = new StateStore(plan.StateDir);
            var tokens = new TokenStore(state);
            var catalog = DatasetCatalog.Load(Path.Combine(plan.InstallDir, "datasets.json"));
            var connection = new Connection(state, new CentralClient(http, options), tokens, options, new AgentStatus(),
                new FirebirdTopazSource(options), catalog, TimeProvider.System, NullLogger<Connection>.Instance);
            var (_, view, _) = await connection.StepAsync(ct, ignoreSchedule: true);
            return view.State switch
            {
                ConnectionState.Connected => new SetupConnection(true, true, view.Detail ?? plan.StationName, null, null),
                ConnectionState.Pending when view.Code is not null => new SetupConnection(true, false, null, view.Code, null),
                ConnectionState.Rejected => new SetupConnection(true, false, null, null, "сервер отклонил заявку этой АЗС; новая уйдёт через сутки"),
                ConnectionState.Blocked => new SetupConnection(true, false, null, null, "заявки с этого адреса заблокированы на сервере"),
                _ => new SetupConnection(true, false, null, null, view.Detail ?? "заявка не отправлена; агент повторит сам"),
            };
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // the service opens the same state DB next
        }
    }

    private static AgentOptions Options(SetupPlan plan, string password) => new()
    {
        StateDirectory = plan.StateDir,
        Server = { Url = plan.ServerUrl, AllowInsecureHttp = plan.ServerUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) },
        Station = { Name = plan.StationName.Trim(), TimeZone = plan.TimeZone },
        Firebird = { DataSource = plan.FirebirdHost, Port = plan.FirebirdPort, Database = plan.DatabasePath ?? "", User = plan.FirebirdUser, Password = password },
    };
}
