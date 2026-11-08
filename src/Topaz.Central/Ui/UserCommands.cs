using Dapper;
using Npgsql;

namespace Topaz.Central.Ui;

/// <summary>
/// First owner: either the CLI
///   docker compose exec central dotnet Topaz.Central.dll create-user --login admin --role owner [--name "..."]
/// (password asked interactively or from env TOPAZ_NEW_PASSWORD), or env Topaz__BootstrapOwnerLogin/Password
/// which creates a superadmin only while there are no users at all.
/// </summary>
public static class UserCommands
{
    public static async Task<int> CreateUserAsync(IServiceProvider sp, string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var login = Arg("--login");
        var role = Arg("--role") ?? Roles.Superadmin;
        if (string.IsNullOrWhiteSpace(login))
        {
            Console.Error.WriteLine("Usage: create-user --login <login> [--role superadmin|owner|manager|viewer] [--name <name>]");
            return 2;
        }
        var password = Environment.GetEnvironmentVariable("TOPAZ_NEW_PASSWORD") ?? ReadPassword($"Password for {login}: ");
        try
        {
            var id = await sp.GetRequiredService<UserService>().CreateAsync(login, Arg("--name") ?? login, role, password);
            await Audit.WriteAsync(sp.GetRequiredService<NpgsqlDataSource>(), null, "cli", "user_created", "user:" + id, new { login, role });
            Console.WriteLine($"User {login} ({role}) created: {id}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or PostgresException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Lost phone and recovery codes (or lost data protection keys):
    ///   docker compose exec central dotnet Topaz.Central.dll reset-totp --login admin
    /// </summary>
    public static async Task<int> ResetTotpAsync(IServiceProvider sp, string[] args)
    {
        var i = Array.IndexOf(args, "--login");
        var login = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (string.IsNullOrWhiteSpace(login))
        {
            Console.Error.WriteLine("Usage: reset-totp --login <login>");
            return 2;
        }
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        await using var c = await db.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<Guid?>("SELECT id FROM users WHERE login_normalized = @l", new { l = UserService.Normalize(login) });
        if (id is null)
        {
            Console.Error.WriteLine($"User {login} not found");
            return 1;
        }
        await c.ExecuteAsync(UserService.ResetTotpSql, new { id, stamp = Guid.NewGuid() });
        await Audit.WriteAsync(db, null, "cli", "totp_reset", "user:" + id, new { login });
        Console.WriteLine($"Two-factor login of {login} is off; it is set up again at the next login.");
        return 0;
    }

    public static async Task BootstrapOwnerAsync(IServiceProvider sp, CentralOptions options, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapOwnerLogin) || string.IsNullOrEmpty(options.BootstrapOwnerPassword)) return;
        await using var c = await sp.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users)")) return;
        await sp.GetRequiredService<UserService>().CreateAsync(options.BootstrapOwnerLogin, options.BootstrapOwnerLogin, Roles.Superadmin, options.BootstrapOwnerPassword);
        log.LogWarning("Bootstrap owner {Login} created. Remove Topaz__BootstrapOwnerPassword from the environment.", options.BootstrapOwnerLogin);
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
