using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Symphonist.Core.Integrations;
using Symphonist.Core.Integrations.Providers;

namespace Symphonist.Integrations.Jira;

public sealed partial class JiraTracker(HttpClient http, JiraTrackerConfig config, string apiToken) : ITracker
{
    private const int PageSize = 50;
    private readonly SemaphoreSlim _myselfLock = new(1, 1);
    private string? _myAccountId;
    private string? _myDisplayName;

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
                    tickets.Add(await MapAsync(issue, me, ct));

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

    private async Task<SourceTicket> MapAsync(JsonElement issue, string myAccountId, CancellationToken ct)
    {
        var key = issue.GetProperty("key").GetString()!;
        var fields = issue.GetProperty("fields");
        var comments = new List<SourceComment>();
        if (fields.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("comments", out var list))
        {
            var seen = new HashSet<string>();
            foreach (var cm in list.EnumerateArray())
                AddComment(cm, myAccountId, seen, comments);

            // Search truncates the thread; read it in full from the comment endpoint.
            var total = c.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
            if (total > comments.Count)
            {
                var startAt = 0;
                while (startAt < total)
                {
                    using var page = await SendAsync(HttpMethod.Get,
                        $"rest/api/3/issue/{Uri.EscapeDataString(key)}/comment?startAt={startAt}&maxResults=100", null, ct);
                    if (!page.RootElement.TryGetProperty("comments", out var items) || items.GetArrayLength() == 0) break;
                    foreach (var cm in items.EnumerateArray())
                        AddComment(cm, myAccountId, seen, comments);
                    startAt += items.GetArrayLength();
                }
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

    private static void AddComment(JsonElement cm, string myAccountId, HashSet<string> seen, List<SourceComment> comments)
    {
        if (cm.TryGetProperty("id", out var cid) && cid.GetString() is { } idText && !seen.Add(idText)) return;
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

    // Jira emits offsets like +0000 (no colon); normalise to +00:00 before parsing.
    private static DateTimeOffset ParseJiraDate(string value) =>
        DateTimeOffset.Parse(OffsetNoColon().Replace(value, "$1:$2"), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex OffsetNoColon();

    private async Task<string> GetMyAccountIdAsync(CancellationToken ct) => (await GetMyselfAsync(ct)).AccountId;

    private async Task<(string AccountId, string DisplayName)> GetMyselfAsync(CancellationToken ct)
    {
        if (_myAccountId is not null) return (_myAccountId, _myDisplayName!);
        await _myselfLock.WaitAsync(ct);
        try
        {
            if (_myAccountId is not null) return (_myAccountId, _myDisplayName!);
            using var doc = await SendAsync(HttpMethod.Get, "rest/api/3/myself", null, ct);
            var accountId = doc.RootElement.GetProperty("accountId").GetString()!;
            _myDisplayName = doc.RootElement.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                ? dn.GetString()! : accountId;
            _myAccountId = accountId;
            return (_myAccountId, _myDisplayName);
        }
        finally
        {
            _myselfLock.Release();
        }
    }

    /// <summary>
    /// Read-only: the signed-in user, the statuses of <paramref name="projectKey"/> across its issue types (merged by
    /// status id, first-seen spelling, ordered To Do, In Progress, Done, other) and the approximate JQL match count.
    /// </summary>
    internal async Task<TrackerDiscovery> DiscoverAsync(string? projectKey, CancellationToken ct)
    {
        var (_, displayName) = await GetMyselfAsync(ct);
        var notes = new List<string>();
        IReadOnlyList<SourceStatus> statuses = [];
        string? source = null;
        if (projectKey is null)
            notes.Add("set tracker.projectKey to read the project's statuses");
        else
        {
            statuses = await ProjectStatusesAsync(projectKey, ct);
            source = $"Jira project {projectKey}";
        }

        return new TrackerDiscovery(displayName, source, statuses, await ApproximateCountAsync(ct), notes);
    }

    private async Task<IReadOnlyList<SourceStatus>> ProjectStatusesAsync(string projectKey, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"rest/api/3/project/{Uri.EscapeDataString(projectKey)}/statuses", null, ct);
        var seen = new Dictionary<string, SourceStatus>(StringComparer.Ordinal);
        var order = new List<string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var issueType in doc.RootElement.EnumerateArray())
            {
                if (!issueType.TryGetProperty("statuses", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var status in list.EnumerateArray())
                {
                    var name = Text(status, "name");
                    if (name.Length == 0) continue;
                    var id = Text(status, "id") is { Length: > 0 } i ? i : name;
                    if (seen.ContainsKey(id)) continue;
                    var category = status.TryGetProperty("statusCategory", out var c) && c.ValueKind == JsonValueKind.Object
                        ? Text(c, "key") switch
                        {
                            "new" => StatusCategory.ToDo,
                            "indeterminate" => StatusCategory.InProgress,
                            "done" => StatusCategory.Done,
                            _ => StatusCategory.Unknown,
                        }
                        : StatusCategory.Unknown;
                    seen[id] = new SourceStatus(name, category, Text(status, "description"));
                    order.Add(id);
                }
            }

        // OrderBy is stable: within a category, first appearance wins.
        return order.Select(id => seen[id]).OrderBy(s => s.Category).ToList();
    }

    /// <summary>The approximate number of issues the JQL matches, or null when Jira refuses or fails.</summary>
    private async Task<int?> ApproximateCountAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, "rest/api/3/search/approximate-count",
                new { jql = config.Jql }, ct);
            return doc.RootElement.TryGetProperty("count", out var count) && count.TryGetInt32(out var n) ? n : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

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
