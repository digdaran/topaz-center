using Microsoft.Extensions.Options;

namespace Topaz.Central.Ui;

/// <summary>
/// The superadmin's own address (Topaz__ConsoleHost, e.g. admin.topaz.08u.ru). When it is set, a superadmin session
/// exists only there and every other session only on the clients' address: all superadmin powers, wherever they are
/// checked, work only at the console, and a stolen or phished client-side session can never be a superadmin's.
/// Empty = one address for everybody (as before; tests and local development).
/// </summary>
public static class ConsoleHosts
{
    public static bool Enabled(CentralOptions o) => !string.IsNullOrWhiteSpace(o.ConsoleHost);

    public static bool IsConsole(HttpContext ctx, CentralOptions o) =>
        Enabled(o) && string.Equals(ctx.Request.Host.Host, o.ConsoleHost!.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool IsConsole(HttpContext ctx) => IsConsole(ctx, Options(ctx));

    public static string? ConsoleUrl(CentralOptions o) => Enabled(o) ? "https://" + o.ConsoleHost!.Trim() : null;

    /// <summary>Why this role may not sign in or keep a session at this address; null = it may.</summary>
    public static string? Refusal(HttpContext ctx, string? role)
    {
        var o = Options(ctx);
        if (!Enabled(o)) return null;
        var console = IsConsole(ctx, o);
        if (role == Roles.Superadmin && !console)
            return $"Вход администратора сервиса — на {ConsoleUrl(o)}";
        if (role != Roles.Superadmin && console)
            return "Этот адрес только для администратора сервиса."
                + (string.IsNullOrWhiteSpace(o.PublicOrigin) ? "" : $" Вход в сервис — на {o.PublicOrigin}");
        return null;
    }

    private static CentralOptions Options(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IOptions<CentralOptions>>().Value;
}
