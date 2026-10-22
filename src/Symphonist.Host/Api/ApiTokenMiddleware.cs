using System.Security.Cryptography;
using System.Text;
using Symphonist.Core;

namespace Symphonist.Host.Api;

/// <summary>
/// Requires the <see cref="ApiToken.Header"/> header to equal the host's API token on every request under
/// <c>/api</c> (compared in constant time); anything else gets <c>401</c>. Other paths (health checks) pass through.
/// </summary>
public sealed class ApiTokenMiddleware(RequestDelegate next, string token)
{
    private readonly byte[] expected = Encoding.UTF8.GetBytes(token);

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api") || IsValid(context.Request.Headers[ApiToken.Header]))
            return next(context);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new ApiError($"missing or invalid {ApiToken.Header} header"));
    }

    private bool IsValid(Microsoft.Extensions.Primitives.StringValues values) =>
        values.Count == 1 && values[0] is { } given
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), expected);
}
