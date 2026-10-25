using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Symphonist.Integrations.GitHub;

/// <summary>A GraphQL response (HTTP 200) carrying an <c>errors</c> array.</summary>
/// <param name="errorTypes">Each error's <c>type</c> (e.g. <c>NOT_FOUND</c>), empty when GitHub gave none.</param>
public sealed class GitHubGraphQlException(string message, IReadOnlyList<string> errorTypes)
    : HttpRequestException(message)
{
    public IReadOnlyList<string> ErrorTypes { get; } = errorTypes;
}

/// <summary>Shared GitHub (github.com) request plumbing: per-request headers, base address, rate limits, error excerpts.</summary>
internal sealed class GitHubApi(HttpClient http, string token)
{
    private static readonly Uri Base = new("https://api.github.com/");

    public async Task<(HttpStatusCode Status, string Text)> SendRawAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        http.BaseAddress ??= Base;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Symphonist", null));
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        // A rate limit must surface as an error, never as a "the host refused" answer callers might act on.
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
                && remaining.Contains("0")))
            throw Failure(method, path, response.StatusCode, text);

        return (response.StatusCode, text);
    }

    public async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var (status, text) = await SendRawAsync(method, path, body, ct);
        if ((int)status is < 200 or > 299) throw Failure(method, path, status, text);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>
    /// POST to <c>graphql</c>. Non-2xx throws <see cref="HttpRequestException"/>; GraphQL reports most errors with
    /// HTTP 200, so a non-empty <c>errors</c> array throws <see cref="GitHubGraphQlException"/> joining their messages.
    /// </summary>
    public async Task<JsonDocument> GraphQlAsync(string query, object variables, CancellationToken ct)
    {
        var doc = await SendAsync(HttpMethod.Post, "graphql", new { query, variables }, ct);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var messages = new List<string>();
            var types = new List<string>();
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.Object) continue;
                if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    messages.Add(m.GetString()!);
                types.Add(error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()! : "");
            }

            var text = messages.Count > 0 ? string.Join("; ", messages) : errors.GetRawText();
            doc.Dispose();
            throw new GitHubGraphQlException($"GitHub POST graphql failed: {Excerpt(text)}", types);
        }

        return doc;
    }

    public static HttpRequestException Failure(HttpMethod method, string path, HttpStatusCode status, string text) =>
        new($"GitHub {method} {path} failed: {(int)status} {status}. {Excerpt(text)}", null, status);

    public static string Excerpt(string text) => text.Length > 300 ? text[..300] + "..." : text;

    /// <summary>Escapes each segment of a ref name, keeping the slashes GitHub expects in paths.</summary>
    public static string EscapeRef(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
}
