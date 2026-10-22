using System.Net;
using System.Text;

namespace Symphonist.Tests.Http;

/// <summary>Scripted HTTP handler for tests. First registered matching route wins; unmatched requests get 404.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(HttpMethod Method, string Prefix, string? BodyContains, HttpStatusCode Status, string Body)> _routes = [];

    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

    /// <summary>Authorization header value of each request, parallel to <see cref="Requests"/>.</summary>
    public List<string?> Authorizations { get; } = [];

    /// <param name="bodyContains">Optional: only match requests whose body contains this text.</param>
    public StubHttpHandler On(HttpMethod method, string pathPrefix, HttpStatusCode status, string body,
        string? bodyContains = null)
    {
        lock (_gate) _routes.Add((method, pathPrefix, bodyContains, status, body));
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
            foreach (var r in _routes)
            {
                if (r.Method == request.Method
                    && pathAndQuery.StartsWith(r.Prefix, StringComparison.Ordinal)
                    && (r.BodyContains is null || (body?.Contains(r.BodyContains, StringComparison.Ordinal) ?? false)))
                {
                    return new HttpResponseMessage(r.Status)
                    {
                        Content = new StringContent(r.Body, Encoding.UTF8, "application/json"),
                    };
                }
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"errorMessages\":[\"no stub registered\"]}", Encoding.UTF8, "application/json"),
        };
    }
}
