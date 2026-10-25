using System.Net;
using System.Text;

namespace Symphonist.Tests.Http;

/// <summary>Scripted HTTP handler for tests. First registered matching route wins; unmatched requests get 404.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(HttpMethod Method, string Prefix, string? BodyContains, HttpStatusCode Status, string Body,
        IReadOnlyDictionary<string, string>? Headers)> _routes = [];

    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

    /// <summary>Authorization header value of each request, parallel to <see cref="Requests"/>.</summary>
    public List<string?> Authorizations { get; } = [];

    /// <summary>Request headers of each request (name to joined values), parallel to <see cref="Requests"/>.</summary>
    public List<IReadOnlyDictionary<string, string>> RequestHeaders { get; } = [];

    /// <param name="bodyContains">Optional: only match requests whose body contains this text.</param>
    /// <param name="headers">Optional: response headers to send.</param>
    public StubHttpHandler On(HttpMethod method, string pathPrefix, HttpStatusCode status, string body,
        string? bodyContains = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        lock (_gate) _routes.Add((method, pathPrefix, bodyContains, status, body, headers));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        lock (_gate)
        {
            Requests.Add((request.Method, pathAndQuery, body));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            RequestHeaders.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value),
                StringComparer.OrdinalIgnoreCase));
            foreach (var r in _routes)
            {
                if (r.Method == request.Method
                    && pathAndQuery.StartsWith(r.Prefix, StringComparison.Ordinal)
                    && (r.BodyContains is null || (body?.Contains(r.BodyContains, StringComparison.Ordinal) ?? false)))
                {
                    var response = new HttpResponseMessage(r.Status)
                    {
                        Content = new StringContent(r.Body, Encoding.UTF8, "application/json"),
                    };
                    foreach (var (name, value) in r.Headers ?? new Dictionary<string, string>())
                        response.Headers.TryAddWithoutValidation(name, value);
                    return response;
                }
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"errorMessages\":[\"no stub registered\"]}", Encoding.UTF8, "application/json"),
        };
    }
}
