using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Symphonist.Integrations.Bitbucket;

/// <summary>Shared Bitbucket Cloud 2.0 request plumbing: per-request basic auth, base address, error excerpts.</summary>
internal sealed class BitbucketApi(HttpClient http, BitbucketRepoConfig config, string apiToken)
{
    private static readonly Uri Base = new("https://api.bitbucket.org/2.0/");

    public string RepoPath =>
        $"repositories/{Uri.EscapeDataString(config.Workspace)}/{Uri.EscapeDataString(config.Slug)}";

    public async Task<(HttpStatusCode Status, string Text)> SendRawAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        http.BaseAddress ??= Base;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Email}:{apiToken}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var (status, text) = await SendRawAsync(method, path, body, ct);
        if ((int)status is < 200 or > 299) throw Failure(method, path, status, text);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    public static HttpRequestException Failure(HttpMethod method, string path, HttpStatusCode status, string text) =>
        new($"Bitbucket {method} {path} failed: {(int)status} {status}. {Excerpt(text)}", null, status);

    public static string Excerpt(string text) => text.Length > 300 ? text[..300] + "..." : text;
}
