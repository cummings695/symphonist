namespace Symphonist.Host.Dashboard;

/// <summary>
/// Keeps other websites open in the same browser from driving the dashboard: outside <c>/api</c>, a request whose
/// <c>Origin</c> header is not the dashboard's own origin gets <c>403</c>, and the Blazor hub (<c>/_blazor</c>, negotiate
/// and WebSocket alike) additionally requires an allowed <c>Origin</c> to be present. <c>/api</c> is left to
/// <see cref="Api.ApiTokenMiddleware"/>. Every response outside <c>/api</c> also forbids framing: <c>SameSite=Strict</c>
/// does not stop a page on another 127.0.0.1 port (same site) from framing the dashboard and overlaying its buttons.
/// </summary>
/// <remarks>
/// One exception to "present": blazor.web.js loads <c>GET /_blazor/initializers</c> with a same-origin <c>fetch</c>, and
/// browsers send no <c>Origin</c> on same-origin GETs. That endpoint only lists JS initializer modules, still requires
/// the sign-in cookie, and a cross-origin <c>fetch</c> of it carries an <c>Origin</c> (rejected above) and cannot read
/// the response anyway. The SignalR long-polling and server-sent-events transports (GETs without <c>Origin</c>) stay
/// blocked, which leaves WebSockets as the only hub transport.
/// </remarks>
public sealed class DashboardOriginMiddleware(RequestDelegate next, DashboardAddress address)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api")) return next(context);

        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";

        var origin = request.Headers.Origin;
        var allowed = origin.Count == 0
            ? !request.Path.StartsWithSegments("/_blazor")
              || (HttpMethods.IsGet(request.Method) && request.Path.Equals("/_blazor/initializers"))
            : origin.Count == 1 && address.AllowedOrigins.Contains(origin[0]!);
        if (allowed) return next(context);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
