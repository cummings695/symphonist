using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Symphonist.Core;
using Symphonist.Host.Components;

namespace Symphonist.Host.Dashboard;

/// <summary>Where the dashboard is served: the API's loopback listener on <see cref="Port"/>.</summary>
public sealed record DashboardAddress(int Port)
{
    public Uri Url { get; } = new($"http://127.0.0.1:{Port}");

    /// <summary>Exactly <c>http://127.0.0.1:{port}</c> and <c>http://localhost:{port}</c>.</summary>
    public IReadOnlySet<string> AllowedOrigins { get; } =
        new HashSet<string>([$"http://127.0.0.1:{Port}", $"http://localhost:{Port}"], StringComparer.Ordinal);

    public Uri LoginUrl(string code) => new(Url, $"/login?code={code}");
}

/// <summary>
/// The Blazor Server dashboard: a cookie sign-in reached only through a single-use link from the token-protected API
/// (<c>POST /api/dashboard/login-code</c>), and same-origin enforcement for everything outside <c>/api</c>.
/// </summary>
public static class DashboardSetup
{
    public const string CookieName = "symphonist.dashboard";

    private const string ExpiredLink = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>Symphonist</title></head>
        <body><p>This sign-in link has expired or was already used. Run <code>symphonist dashboard</code> again.</p></body></html>
        """;

    private const string SignedOut = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>Symphonist</title></head>
        <body><p>Open the dashboard with <code>symphonist dashboard</code>.</p></body></html>
        """;

    public static IServiceCollection AddDashboard(this IServiceCollection services, int port)
    {
        services.AddSingleton(new DashboardAddress(port));
        services.AddSingleton<DashboardLogin>();
        services.AddSingleton<BoardQuery>();

        // Persisted keys keep the cookie valid across host restarts; on Windows they are encrypted to the user (DPAPI).
        var keys = SymphonistPaths.Resolve("keys");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(keys);
        else
        {
            const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(keys, userOnly);
            File.SetUnixFileMode(keys, userOnly); // also tighten a directory that already existed
        }
        var dataProtection = services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keys))
            .SetApplicationName("Symphonist");
        if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.Path = "/";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                o.LoginPath = "/signed-out";
                o.AccessDeniedPath = "/signed-out";
            });
        services.AddAuthorization();
        services.AddRazorComponents().AddInteractiveServerComponents();
        return services;
    }

    /// <summary>Call after <see cref="Api.ApiTokenMiddleware"/>.</summary>
    public static WebApplication MapDashboard(this WebApplication app)
    {
        app.UseMiddleware<DashboardOriginMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets();

        app.MapGet("/login", async (string? code, DashboardLogin login, HttpContext context) =>
        {
            if (!login.Redeem(code)) return Results.Content(ExpiredLink, "text/html", statusCode: StatusCodes.Status401Unauthorized);

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "developer")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect("/");
        });

        app.MapGet("/signed-out", () => Results.Content(SignedOut, "text/html"));

        // DashboardOriginMiddleware sends frame-ancestors 'none' on every response; drop Blazor's own (weaker) 'self'.
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = null)
            .RequireAuthorization();
        return app;
    }
}
