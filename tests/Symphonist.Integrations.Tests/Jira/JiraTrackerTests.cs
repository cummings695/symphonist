using System.Net;
using System.Text;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations;
using Symphonist.Integrations.Jira;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.Jira;

public class JiraTrackerTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static readonly JiraTrackerConfig Config = new("jira", "jira-token", new Uri("https://example.atlassian.net"),
        "dev@example.com", "assignee = currentUser()");

    private static (JiraTracker Tracker, StubHttpHandler Stub) Create(StubHttpHandler? stub = null)
    {
        stub ??= new StubHttpHandler();
        return (new JiraTracker(new HttpClient(stub), Config, "secret-token"), stub);
    }

    private static StubHttpHandler PagedStub() => new StubHttpHandler()
        .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"))
        .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK, Fixture("jira-search-page2.json"), "\"p2\"")
        .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK, Fixture("jira-search.json"));

    [Fact]
    public async Task Query_follows_pages_and_maps_fields()
    {
        var (tracker, stub) = Create(PagedStub());

        var tickets = await tracker.QueryAsync(CancellationToken.None);

        Assert.Equal(["PROJ-1", "PROJ-2", "PROJ-3"], tickets.Select(t => t.Key));
        var searches = stub.Requests.Where(r => r.PathAndQuery.StartsWith("/rest/api/3/search/jql")).ToList();
        Assert.Equal(2, searches.Count);
        Assert.DoesNotContain("nextPageToken", searches[0].Body);
        Assert.Contains("\"p2\"", searches[1].Body);
        Assert.Contains("assignee = currentUser()", searches[0].Body);
        Assert.Equal(1, stub.Requests.Count(r => r.PathAndQuery.StartsWith("/rest/api/3/myself")));

        var first = tickets[0];
        Assert.Equal("Add login throttling", first.Title);
        Assert.Equal("Planned", first.Status);
        Assert.Equal("Throttle failed logins.\nAfter five attempts\nlock for 15 minutes.", first.Description);
        Assert.Equal(new Uri("https://example.atlassian.net/browse/PROJ-1"), first.WebUrl);
        Assert.Equal("", tickets[1].Description);

        Assert.Equal(2, first.Comments.Count);
        Assert.Equal("Please keep it simple.", first.Comments[0].Body);
        Assert.False(first.Comments[0].IsMine);
        Assert.Equal("Pat Product", first.Comments[0].Author);
        Assert.Equal("Plan posted.", first.Comments[1].Body);
        Assert.True(first.Comments[1].IsMine);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 10, 15, 30, 123, TimeSpan.Zero), first.Comments[1].Created);
    }

    private static string CommentJson(string id, string text, string created) =>
        "{\"id\":\"" + id + "\",\"author\":{\"accountId\":\"u1\",\"displayName\":\"Pat\"},\"created\":\"" + created
        + "\",\"body\":{\"type\":\"doc\",\"version\":1,\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

    private static string SearchJson(int total, params string[] comments) =>
        "{\"issues\":[{\"key\":\"PROJ-9\",\"fields\":{\"summary\":\"s\",\"status\":{\"name\":\"Planned\"},\"comment\":{\"total\":"
        + total + ",\"comments\":[" + string.Join(",", comments) + "]}}}]}";

    private const string C1 = "2026-03-01T10:00:00.000+0000";
    private const string C2 = "2026-03-02T10:00:00.000+0000";
    private const string C3 = "2026-03-03T10:00:00.000+0000";

    [Fact]
    public async Task Fetches_remaining_comments_when_truncated()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"))
            .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK, SearchJson(3, CommentJson("2", "second", C2)))
            .On(HttpMethod.Get, "/rest/api/3/issue/PROJ-9/comment?startAt=0", HttpStatusCode.OK,
                "{\"startAt\":0,\"maxResults\":100,\"total\":3,\"comments\":["
                + CommentJson("3", "newest", C3) + "," + CommentJson("1", "first", C1) + "," + CommentJson("2", "second", C2) + "]}");
        var (tracker, _) = Create(stub);

        var ticket = Assert.Single(await tracker.QueryAsync(CancellationToken.None));

        Assert.Equal(["first", "second", "newest"], ticket.Comments.Select(x => x.Body));
    }

    [Fact]
    public async Task Does_not_fetch_comments_when_complete()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"))
            .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK,
                SearchJson(2, CommentJson("1", "first", C1), CommentJson("2", "second", C2)));
        var (tracker, _) = Create(stub);

        var ticket = Assert.Single(await tracker.QueryAsync(CancellationToken.None));

        Assert.Equal(2, ticket.Comments.Count);
        Assert.DoesNotContain(stub.Requests, r => r.PathAndQuery.Contains("/comment"));
    }

    [Fact]
    public async Task Comment_page_failure_throws()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"))
            .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK, SearchJson(3, CommentJson("2", "second", C2)))
            .On(HttpMethod.Get, "/rest/api/3/issue/PROJ-9/comment", HttpStatusCode.InternalServerError, "{}");
        var (tracker, _) = Create(stub);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => tracker.QueryAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Query_throws_on_http_error()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"))
            .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.InternalServerError, "{\"errorMessages\":[\"boom\"]}", "\"p2\"")
            .On(HttpMethod.Post, "/rest/api/3/search/jql", HttpStatusCode.OK, Fixture("jira-search.json"));
        var (tracker, _) = Create(stub);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => tracker.QueryAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("500", ex.Message);
        Assert.Contains("boom", ex.Message);
        Assert.DoesNotContain("secret-token", ex.Message);
    }

    [Fact]
    public async Task Transition_posts_matching_id_case_insensitive()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/issue/PROJ-1/transitions", HttpStatusCode.OK, Fixture("jira-transitions.json"))
            .On(HttpMethod.Post, "/rest/api/3/issue/PROJ-1/transitions", HttpStatusCode.NoContent, "");
        var (tracker, _) = Create(stub);

        var moved = await tracker.TransitionAsync("PROJ-1", "in review", CancellationToken.None);

        Assert.True(moved);
        var post = Assert.Single(stub.Requests, r => r.Method == HttpMethod.Post);
        using var doc = JsonDocument.Parse(post.Body!);
        Assert.Equal("31", doc.RootElement.GetProperty("transition").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Transition_returns_false_when_not_available()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/issue/PROJ-1/transitions", HttpStatusCode.OK, Fixture("jira-transitions.json"));
        var (tracker, _) = Create(stub);

        var moved = await tracker.TransitionAsync("PROJ-1", "Released", CancellationToken.None);

        Assert.False(moved);
        Assert.DoesNotContain(stub.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Comment_posts_adf_paragraphs()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Post, "/rest/api/3/issue/PROJ-1/comment", HttpStatusCode.Created, "{\"id\":\"1\"}");
        var (tracker, _) = Create(stub);

        await tracker.CommentAsync("PROJ-1", "a\n\nb", CancellationToken.None);

        using var doc = JsonDocument.Parse(Assert.Single(stub.Requests).Body!);
        var body = doc.RootElement.GetProperty("body");
        Assert.Equal("doc", body.GetProperty("type").GetString());
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        var content = body.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, content.Count);
        Assert.All(content, p => Assert.Equal("paragraph", p.GetProperty("type").GetString()));
        Assert.Equal("a", content[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("b", content[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void FromMarkdown_uses_hard_breaks_and_skips_empty_blocks()
    {
        var json = JsonSerializer.SerializeToElement(Adf.FromMarkdown("one\ntwo\n\n\n\n  \n\nthree\n"));
        var content = json.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, content.Count);
        var types = content[0].GetProperty("content").EnumerateArray().Select(n => n.GetProperty("type").GetString());
        Assert.Equal(["text", "hardBreak", "text"], types);
    }

    [Fact]
    public async Task Sends_basic_auth_header_per_request_without_touching_defaults()
    {
        var stub = PagedStub();
        var client = new HttpClient(stub);
        var tracker = new JiraTracker(client, Config, "secret-token");

        await tracker.QueryAsync(CancellationToken.None);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("dev@example.com:secret-token"));
        Assert.All(stub.Authorizations, a => Assert.Equal(expected, a));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal(Config.Site, client.BaseAddress);
    }

    [Fact]
    public async Task Query_requests_and_maps_attachments()
    {
        var (tracker, stub) = Create(PagedStub());

        var tickets = await tracker.QueryAsync(CancellationToken.None);

        Assert.Contains("\"attachment\"", stub.Requests.First(r => r.PathAndQuery.StartsWith("/rest/api/3/search/jql")).Body);
        var list = tickets[0].AttachmentList;
        Assert.Equal(2, list.Count);
        Assert.Equal(new SourceAttachment("10001", "screen shot.png", "image/png", 2048,
            new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)), list[0]);
        Assert.Equal(new SourceAttachment("10002", "orders.csv", "text/csv", 512,
            new DateTimeOffset(2026, 10, 20, 9, 5, 0, TimeSpan.Zero)), list[1]);
        Assert.Empty(tickets[1].AttachmentList);
    }

    private static readonly SourceAttachment Csv = new("10002", "orders.csv", "text/csv", 8, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Download_streams_content_with_basic_auth()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/attachment/content/10002", HttpStatusCode.OK, "a,b\n1,2\n");
        var (tracker, _) = Create(stub);
        using var dest = new MemoryStream();

        await tracker.DownloadAttachmentAsync("PROJ-1", Csv, dest, CancellationToken.None);

        Assert.Equal("a,b\n1,2\n", Encoding.UTF8.GetString(dest.ToArray()));
        Assert.StartsWith("Basic ", stub.Authorizations[^1]);
    }

    private static readonly Dictionary<string, string> Redirect = new() { ["Location"] = "https://api.media.atlassian.com/file/abc?token=x" };

    [Fact]
    public async Task Download_follows_redirect_without_authorization()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/attachment/content/10002", HttpStatusCode.SeeOther, "", headers: Redirect)
            .On(HttpMethod.Get, "/file/abc", HttpStatusCode.OK, "data");
        var (tracker, _) = Create(stub);
        using var dest = new MemoryStream();

        await tracker.DownloadAttachmentAsync("PROJ-1", Csv, dest, CancellationToken.None);

        Assert.Equal("data", Encoding.UTF8.GetString(dest.ToArray()));
        Assert.Equal(2, stub.Requests.Count);
        Assert.Null(stub.Authorizations[^1]);
    }

    [Fact]
    public async Task Download_failure_throws_without_leaking_url_query()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/attachment/content/10002", HttpStatusCode.SeeOther, "", headers: Redirect)
            .On(HttpMethod.Get, "/file/abc", HttpStatusCode.Forbidden, "denied");
        var (tracker, _) = Create(stub);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            tracker.DownloadAttachmentAsync("PROJ-1", Csv, new MemoryStream(), CancellationToken.None));

        Assert.DoesNotContain("token=x", ex.Message);
        Assert.Contains("10002", ex.Message);
        Assert.Contains("403", ex.Message);
    }
}
