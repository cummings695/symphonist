using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;

namespace Symphonist.Integrations.Jira;

public sealed partial class JiraTracker(HttpClient http, TrackerConfig config, string apiToken) : ITracker
{
    private const int PageSize = 50;
    private readonly SemaphoreSlim _myselfLock = new(1, 1);
    private string? _myAccountId;

    public async Task<IReadOnlyList<SourceTicket>> QueryAsync(CancellationToken ct)
    {
        var me = await GetMyAccountIdAsync(ct);
        var tickets = new List<SourceTicket>();
        string? pageToken = null;
        do
        {
            var body = new Dictionary<string, object>
            {
                ["jql"] = config.Jql,
                ["fields"] = new[] { "summary", "description", "status", "comment" },
                ["maxResults"] = PageSize,
            };
            if (pageToken is not null) body["nextPageToken"] = pageToken;

            using var doc = await SendAsync(HttpMethod.Post, "rest/api/3/search/jql", body, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("issues", out var issues))
                foreach (var issue in issues.EnumerateArray())
                    tickets.Add(Map(issue, me));

            pageToken = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        } while (!string.IsNullOrEmpty(pageToken));

        return tickets;
    }

    public async Task<bool> TransitionAsync(string key, string targetStatus, CancellationToken ct)
    {
        var path = $"rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions";
        string? id = null;
        using (var doc = await SendAsync(HttpMethod.Get, path, null, ct))
        {
            if (doc.RootElement.TryGetProperty("transitions", out var transitions))
                foreach (var tr in transitions.EnumerateArray())
                {
                    var to = tr.TryGetProperty("to", out var toEl) && toEl.TryGetProperty("name", out var n)
                        ? n.GetString() : null;
                    if (string.Equals(to, targetStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        id = tr.GetProperty("id").GetString();
                        break;
                    }
                }
        }

        if (id is null) return false;
        (await SendAsync(HttpMethod.Post, path, new { transition = new { id } }, ct)).Dispose();
        return true;
    }

    public async Task CommentAsync(string key, string markdown, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Post, $"rest/api/3/issue/{Uri.EscapeDataString(key)}/comment",
            new { body = Adf.FromMarkdown(markdown) }, ct)).Dispose();

    private SourceTicket Map(JsonElement issue, string myAccountId)
    {
        var key = issue.GetProperty("key").GetString()!;
        var fields = issue.GetProperty("fields");
        var comments = new List<SourceComment>();
        if (fields.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("comments", out var list))
        {
            foreach (var cm in list.EnumerateArray())
            {
                var author = cm.TryGetProperty("author", out var a) ? a : default;
                var accountId = author.ValueKind == JsonValueKind.Object && author.TryGetProperty("accountId", out var id)
                    ? id.GetString() : null;
                var name = author.ValueKind == JsonValueKind.Object && author.TryGetProperty("displayName", out var dn)
                    ? dn.GetString() : null;
                comments.Add(new SourceComment(
                    name ?? accountId ?? "unknown",
                    accountId is not null && accountId == myAccountId,
                    ParseJiraDate(cm.GetProperty("created").GetString()!),
                    cm.TryGetProperty("body", out var b) ? Adf.ToPlainText(b) : ""));
            }
        }

        return new SourceTicket(
            key,
            fields.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
            fields.TryGetProperty("description", out var d) ? Adf.ToPlainText(d) : "",
            fields.TryGetProperty("status", out var st) && st.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "",
            comments.OrderBy(x => x.Created).ToList(),
            new Uri($"{config.Site.ToString().TrimEnd('/')}/browse/{key}"));
    }

    // Jira emits offsets like +0000 (no colon); normalise to +00:00 before parsing.
    private static DateTimeOffset ParseJiraDate(string value) =>
        DateTimeOffset.Parse(OffsetNoColon().Replace(value, "$1:$2"), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex OffsetNoColon();

    private async Task<string> GetMyAccountIdAsync(CancellationToken ct)
    {
        if (_myAccountId is not null) return _myAccountId;
        await _myselfLock.WaitAsync(ct);
        try
        {
            if (_myAccountId is not null) return _myAccountId;
            using var doc = await SendAsync(HttpMethod.Get, "rest/api/3/myself", null, ct);
            _myAccountId = doc.RootElement.GetProperty("accountId").GetString()!;
            return _myAccountId;
        }
        finally
        {
            _myselfLock.Release();
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        http.BaseAddress ??= config.Site;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Email}:{apiToken}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var excerpt = text.Length > 300 ? text[..300] + "..." : text;
            throw new HttpRequestException(
                $"Jira {method} {path} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {excerpt}",
                null, response.StatusCode);
        }

        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
    }
}
