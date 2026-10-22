using System.Net;
using System.Text;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Integrations.Jira;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.Jira;

public class JiraTrackerTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static readonly TrackerConfig Config = new("jira", new Uri("https://example.atlassian.net"),
        "dev@example.com", "jira-token", "assignee = currentUser()");

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
}
