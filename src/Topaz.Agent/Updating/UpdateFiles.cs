using System.Text.Json;
using Topaz.Contracts;

namespace Topaz.Agent.Updating;

/// <summary>What the installer process needs (written next to the new exe as apply.json).</summary>
public sealed class UpdatePlan
{
    public string Version { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public string StateDir { get; set; } = "";
    public string ServiceName { get; set; } = "TopazAgent";
}

/// <summary>Written by a running agent once it is healthy; the installer waits for it.</summary>
public sealed class StartedMarker
{
    public string Version { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>Layout of %ProgramData%\TopazAgent\updates (SYSTEM and Administrators only).</summary>
public sealed class UpdatePaths(string stateDir)
{
    public string Root { get; } = Path.Combine(stateDir, "updates");
    public string Staging(string version) => Path.Combine(Root, version);
    public string DownloadDir => Path.Combine(Root, "download");
    public string Backup => Path.Combine(Root, "backup");
    public string StartedMarkerFile => Path.Combine(Root, "started.json");
    public string ResultFile => Path.Combine(Root, "result.json");
    public string PlanFile(string version) => Path.Combine(Staging(version), "apply.json");
    public string LogFile(string version) => Path.Combine(stateDir, "logs", $"update-{version}.log");

    /// <summary>Only plain version strings become folder names (they come from the server).</summary>
    public static bool IsSafeVersion(string? v) =>
        !string.IsNullOrEmpty(v) && v.Length <= 64 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') && SemVer.TryParse(v, out _);

    public static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Protocol.Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json));
        File.Move(tmp, path, overwrite: true);
    }
}
