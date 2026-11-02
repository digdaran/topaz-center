using System.Reflection;

namespace Topaz.Central;

/// <summary>
/// The release version of this server (the tag the image was built from; "1.0.0" in a local build).
/// The web UI compares it with the version it was built with and asks for a reload after an update.
/// </summary>
public static class AppVersion
{
    public const string Header = "X-Topaz-Version";

    public static readonly string Current =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Static files: index.html is revalidated every time, hashed assets are cached for good.</summary>
    public static void SetCacheHeaders(HttpContext ctx, string fileName)
    {
        if (fileName.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            ctx.Response.Headers.CacheControl = "no-cache";
        else if (ctx.Request.Path.StartsWithSegments("/assets"))
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    }

    /// <summary>A request for a file (/assets/…, anything with an extension) is never answered with the SPA page.</summary>
    public static bool LooksLikeFile(PathString path) =>
        path.StartsWithSegments("/assets") || Path.HasExtension(path.Value ?? "");
}
