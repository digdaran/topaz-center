using System.Text.Json;
using System.Text.Json.Nodes;

namespace Topaz.Agent.Setup;

/// <summary>Everything the setup needs to know; resolved from a previous install, command line arguments and defaults.</summary>
public sealed class SetupPlan
{
    public const string DefaultServerUrl = "https://topaz.08u.ru";
    /// <summary>Password of a standard Topaz install (the same on all stations); kept only in the service environment.</summary>
    public const string DefaultFirebirdPassword = "electro";
    public const string ServiceName = "TopazAgent";
    public const string ExeName = "TopazAgent.exe";

    public string StationName { get; set; } = "";
    public string ServerUrl { get; set; } = DefaultServerUrl;
    public string? DatabasePath { get; set; }
    public string FirebirdHost { get; set; } = "127.0.0.1";
    public int FirebirdPort { get; set; } = 3050;
    public string FirebirdUser { get; set; } = "SYSDBA";
    /// <summary>Null: keep the one the service already has, else the standard one.</summary>
    public string? FirebirdPassword { get; set; }
    public string? TimeZone { get; set; }
    public string InstallDir { get; set; } = "";
    public string StateDir { get; set; } = "";
    /// <summary>The exe that runs the setup (copied into <see cref="InstallDir"/>).</summary>
    public string SourceExe { get; set; } = "";
    public bool IsUpdate { get; set; }

    public string InstalledExe => Path.Combine(InstallDir, ExeName);
    public bool LocalFirebird => FirebirdHost.Trim().ToLowerInvariant() is "" or "127.0.0.1" or "localhost" or "::1" or ".";

    /// <summary>
    /// Previous install first (an update keeps its settings), then arguments, then defaults.
    /// <paramref name="installedExe"/>: path of the exe the existing service runs, if any.
    /// </summary>
    public static SetupPlan Resolve(SetupArgs args, string? installedExe, string defaultInstallDir, string stateDir, string sourceExe, IEnumerable<string?> topazSearchRoots)
    {
        var plan = new SetupPlan { InstallDir = defaultInstallDir, StateDir = stateDir, SourceExe = sourceExe };
        if (installedExe is not null && Path.GetDirectoryName(installedExe) is { Length: > 0 } dir)
        {
            plan.InstallDir = dir;
            plan.IsUpdate = true;
            var previous = ReadSettings(Path.Combine(dir, "appsettings.json"));
            if (previous is not null)
            {
                plan.StationName = Str(previous, "Station", "Name") ?? plan.StationName;
                plan.TimeZone = Str(previous, "Station", "TimeZone");
                plan.ServerUrl = Str(previous, "Server", "Url") is { Length: > 0 } url && !url.Contains("topaz.example.com", StringComparison.OrdinalIgnoreCase) ? url : plan.ServerUrl;
                plan.DatabasePath = Str(previous, "Firebird", "Database");
                plan.FirebirdHost = Str(previous, "Firebird", "DataSource") ?? plan.FirebirdHost;
                plan.FirebirdPort = int.TryParse(Str(previous, "Firebird", "Port"), out var port) ? port : plan.FirebirdPort;
                plan.FirebirdUser = Str(previous, "Firebird", "User") ?? plan.FirebirdUser;
            }
        }

        if (args.Station is { Length: > 0 } s) plan.StationName = s.Trim();
        if (args.Server is { Length: > 0 } srv) plan.ServerUrl = NormalizeUrl(srv);
        if (args.Database is { Length: > 0 } db) plan.DatabasePath = db.Trim().Trim('"');
        if (args.FirebirdHost is { Length: > 0 } h) plan.FirebirdHost = h.Trim();
        if (args.FirebirdPort is { } p) plan.FirebirdPort = p;
        if (args.FirebirdUser is { Length: > 0 } u) plan.FirebirdUser = u.Trim();
        if (args.FirebirdPassword is { Length: > 0 } pw) plan.FirebirdPassword = pw;
        if (args.TimeZone is { Length: > 0 } tz) plan.TimeZone = tz.Trim();
        if (args.InstallDir is { Length: > 0 } inst && !plan.IsUpdate) plan.InstallDir = inst.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(plan.DatabasePath) && plan.LocalFirebird)
            plan.DatabasePath = DatabaseLocator.FindNewest(topazSearchRoots);
        return plan;
    }

    /// <summary>Problems that block the installation (shown next to the fields in the window).</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(StationName)) yield return "Укажите название АЗС.";
        else if (StationName.Trim().Length > 100) yield return "Название АЗС — не длиннее 100 символов.";
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            yield return "Адрес сервера должен начинаться с https://";
        if (string.IsNullOrWhiteSpace(DatabasePath)) yield return "Не найдена база Топаза: укажите путь к TopazAZS.fdb.";
        else if (LocalFirebird && Path.IsPathFullyQualified(DatabasePath) && !File.Exists(DatabasePath)) yield return $"Файл базы не найден: {DatabasePath}";
    }

    public static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        return url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
    }

    /// <summary>appsettings.json of the installed agent: the shipped defaults with this plan's values (no secrets).</summary>
    public string BuildAppSettings(string defaultsJson, string? existingJson)
    {
        var root = (existingJson is null ? null : JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject)
                   ?? JsonNode.Parse(defaultsJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        JsonObject Section(string name) => root[name] as JsonObject ?? (JsonObject)(root[name] = new JsonObject());
        Section("Server")["Url"] = ServerUrl;
        Section("Server").Remove("EnrollmentKey");
        Section("Station")["Name"] = StationName.Trim();
        if (!string.IsNullOrWhiteSpace(TimeZone)) Section("Station")["TimeZone"] = TimeZone;
        var fb = Section("Firebird");
        fb["DataSource"] = FirebirdHost;
        fb["Port"] = FirebirdPort;
        fb["Database"] = DatabasePath;
        fb["User"] = FirebirdUser;
        fb.Remove("Password");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static JsonObject? ReadSettings(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static string? Str(JsonObject o, string section, string key) =>
        o[section]?[key] is JsonNode n ? (n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()) is { Length: > 0 } v ? v : null : null;
}

/// <summary>Finds the Topaz database in its standard place: Program Files (x86)\TopazAZS-*\TopazAZS.fdb (newest wins).</summary>
public static class DatabaseLocator
{
    public static string? FindNewest(IEnumerable<string?> roots) =>
        roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(r => SafeDirs(r!, "TopazAZS-*"))
            .Select(d => Path.Combine(d, "TopazAZS.fdb")).Where(File.Exists)
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();

    public static IEnumerable<string?> StandardRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetEnvironmentVariable("ProgramW6432"),
    ];

    private static IEnumerable<string> SafeDirs(string root, string pattern)
    {
        try { return Directory.GetDirectories(root, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>Command line of the setup: /S /station="АЗС-5" /server=… /db=… /fbpassword=… /fbhost=… /fbport=… /fbuser=… /tz=… /installdir=…</summary>
public sealed class SetupArgs
{
    public bool Silent { get; private set; }
    public bool RemoveState { get; private set; }
    public string? Station { get; private set; }
    public string? Server { get; private set; }
    public string? Database { get; private set; }
    public string? FirebirdHost { get; private set; }
    public int? FirebirdPort { get; private set; }
    public string? FirebirdUser { get; private set; }
    public string? FirebirdPassword { get; private set; }
    public string? TimeZone { get; private set; }
    public string? InstallDir { get; private set; }
    public List<string> Errors { get; } = new();

    public static bool LooksLikeSetup(string[] args) => args.Length > 0 && args.All(a => a.StartsWith('/') || a.StartsWith("--", StringComparison.Ordinal));

    public static SetupArgs Parse(IEnumerable<string> args)
    {
        var r = new SetupArgs();
        foreach (var raw in args)
        {
            var a = raw.Trim();
            if (a.Length == 0) continue;
            var body = a.StartsWith("--", StringComparison.Ordinal) ? a[2..] : a.StartsWith('/') ? a[1..] : null;
            if (body is null) { r.Errors.Add($"Неизвестный параметр: {a}"); continue; }
            var eq = body.IndexOfAny(['=', ':']);
            var key = (eq < 0 ? body : body[..eq]).ToLowerInvariant();
            var value = eq < 0 ? null : body[(eq + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "s" or "silent" or "quiet" or "q": r.Silent = true; break;
                case "removestate": r.RemoveState = true; break;
                case "station": r.Station = value; break;
                case "server": r.Server = value; break;
                case "db" or "database": r.Database = value; break;
                case "fbhost": r.FirebirdHost = value; break;
                case "fbport":
                    if (int.TryParse(value, out var port) && port is > 0 and < 65536) r.FirebirdPort = port; else r.Errors.Add("/fbport: номер порта");
                    break;
                case "fbuser": r.FirebirdUser = value; break;
                case "fbpassword": r.FirebirdPassword = value; break;
                case "tz": r.TimeZone = value; break;
                case "installdir": r.InstallDir = value; break;
                default: r.Errors.Add($"Неизвестный параметр: {a}"); break;
            }
            if (value is "" && key is not ("s" or "silent" or "quiet" or "q" or "removestate")) r.Errors.Add($"Пустое значение: {a}");
        }
        return r;
    }
}
