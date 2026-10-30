using System.Net;
using System.Text.Json;
using Symphonist.Core.Config;
using Symphonist.Core.Integrations.Providers;
using Symphonist.Integrations.Jira;
using Symphonist.Tests.Http;

namespace Symphonist.Tests.Jira;

public class JiraDiscoveryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ProjectConfig Draft(string? projectKey = "ACME") => new("orders", "Orders",
        new JiraTrackerConfig("jira", "jira-orders", new Uri("https://example.atlassian.net"), "dev@example.com",
            "project = ACME AND labels = Claude", projectKey),
        null!, null!, null!, 3, [], new Dictionary<StageKind, string>(), "/cfg");

    private static StubHttpHandler Stub(HttpStatusCode count = HttpStatusCode.OK) => new StubHttpHandler()
        .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"))
        .On(HttpMethod.Get, "/rest/api/3/project/ACME/statuses", HttpStatusCode.OK, Fixture("jira-project-statuses.json"))
        .On(HttpMethod.Post, "/rest/api/3/search/approximate-count", count,
            count == HttpStatusCode.OK ? Fixture("jira-approximate-count.json") : "{\"errorMessages\":[\"bad jql\"]}");

    private static Task<TrackerDiscovery> Discover(StubHttpHandler stub, ProjectConfig? draft = null) =>
        new JiraProvider().DiscoverAsync(draft ?? Draft(), new HttpClient(stub), "secret-token", CancellationToken.None);

    private static void AssertReadOnly(StubHttpHandler stub) =>
        Assert.All(stub.Requests, r => Assert.True(r.Method == HttpMethod.Get
            || (r.Method == HttpMethod.Post && r.PathAndQuery == "/rest/api/3/search/approximate-count"),
            $"{r.Method} {r.PathAndQuery} is not a read"));

    [Fact]
    public async Task Discover_merges_statuses_across_issue_types_in_category_order()
    {
        var stub = Stub();

        var result = await Discover(stub);

        Assert.Equal("Dana Developer", result.AccountName);
        Assert.Equal("Jira project ACME", result.StatusSource);
        Assert.Equal(["Backlog", "Planned", "In Progress", "In Review", "Done"], result.Statuses.Select(s => s.Name));
        Assert.Equal([StatusCategory.ToDo, StatusCategory.ToDo, StatusCategory.InProgress, StatusCategory.InProgress,
            StatusCategory.Done], result.Statuses.Select(s => s.Category));
        Assert.Equal("Plan approved", result.Statuses[1].Description);
        Assert.Equal("Pull request open", result.Statuses[3].Description);
        Assert.Equal(12, result.MatchingTickets);
        Assert.Empty(result.Notes);

        var count = Assert.Single(stub.Requests, r => r.PathAndQuery == "/rest/api/3/search/approximate-count");
        using var body = JsonDocument.Parse(count.Body!);
        Assert.Equal("project = ACME AND labels = Claude", body.RootElement.GetProperty("jql").GetString());
        AssertReadOnly(stub);
    }

    [Fact]
    public async Task Discover_without_project_key_returns_no_statuses()
    {
        var stub = Stub();

        var result = await Discover(stub, Draft(projectKey: null));

        Assert.Empty(result.Statuses);
        Assert.Null(result.StatusSource);
        Assert.DoesNotContain(stub.Requests, r => r.PathAndQuery.Contains("/statuses", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("tracker.projectKey", StringComparison.Ordinal));
        Assert.Equal(12, result.MatchingTickets);
        AssertReadOnly(stub);
    }

    [Fact]
    public async Task Discover_count_failure_is_null()
    {
        var result = await Discover(Stub(HttpStatusCode.BadRequest));

        Assert.Null(result.MatchingTickets);
        Assert.Equal(5, result.Statuses.Count);
    }

    [Fact]
    public async Task Discover_unknown_project_throws_with_status()
    {
        var stub = new StubHttpHandler()
            .On(HttpMethod.Get, "/rest/api/3/myself", HttpStatusCode.OK, Fixture("jira-myself.json"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Discover(stub, Draft("NOPE")));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("project/NOPE/statuses", ex.Message);
        Assert.DoesNotContain("secret-token", ex.Message);
    }
}
